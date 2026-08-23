using System.Text.Json.Nodes;

namespace Orf;

/// <summary>
/// One agent playing one player slot. Runs a turn every turnIntervalSeconds against
/// R/state/&lt;slug&gt;.json and writes orders to R/orders/&lt;slug&gt;/inbox/ plus artifacts
/// under R/agents/&lt;slug&gt;/. Exceptions never propagate out of the loop.
/// </summary>
public sealed class AgentLoop
{
	readonly string runDir;
	readonly Spec spec;
	readonly PlayerSpec player;
	readonly ProviderSpec provider;
	readonly int index;
	readonly string systemPrompt;
	readonly LlmClient? llm;

	// Swarm mode: this loop drives one specialist role of the player rather than
	// the whole player. Role loops share the player's inbox (with a role prefix on
	// filenames), publish their status as a dashboard module lane, and coordinate
	// through the team board + the shared strategist's advice.
	readonly RoleSpec? role;
	readonly bool ownsAdvisor;
	readonly HashSet<string>? allowedOrders;
	string Prefix => role == null ? "" : role.Name + "-";
	string Lane => role == null ? player.Slug : $"{player.Slug}/{role.Name}";

	readonly HashSet<string> seenResults = [];
	readonly List<List<JsonObject>> history = [];

	long lastTick = -1;
	int turnCount;
	int seqCounter;
	long totalPromptTokens;
	double totalCostUsd;
	readonly List<string> goals = [];
	readonly Dictionary<string, bool> checklist = [];
	readonly HashSet<string> exploredSpawns = [];
	long totalCompletionTokens;
	readonly Queue<string> actionLog = new();
	string? lastError;
	string summaryLine = "";
	string seqStr = "000000";

	// Demand system (swarm): sequence for requests this role has posted, and the
	// set of inbox submissions still awaiting engine results (anti-double-order).
	int requestCounter;
	readonly Dictionary<string, string> pendingSubmissions = [];

	// Optional add-on: over-the-shoulder advisor (runs its own async loop).
	readonly AdvisorLoop? advisorLoop;

	// Cadence telemetry for the dashboard: when a provider request is in flight,
	// when the last response landed, and a rolling window of full-turn latencies.
	DateTime? requestStartedAtUtc;
	DateTime? lastResponseAtUtc;
	double? lastTurnSeconds;
	readonly Queue<double> turnLatencies = new();
	const int LatencyWindow = 10;

	public AgentLoop(string runDir, Spec spec, PlayerSpec player, int index, string specDir,
		RoleSpec? role = null, AdvisorLoop? sharedAdvisor = null)
	{
		this.runDir = runDir;
		this.spec = spec;
		this.player = player;
		this.index = index;
		this.role = role;

		var providerKey = role?.Provider ?? player.Provider;
		if (!spec.Providers.TryGetValue(providerKey, out provider!))
			throw new InvalidOperationException($"'{Lane}' references unknown provider '{providerKey}'");

		if (role != null && role.Orders.Count > 0)
			allowedOrders = [.. role.Orders];

		var promptFile = role?.PromptFile ?? player.PromptFile;
		var promptPath = promptFile != null
			? Path.GetFullPath(Path.Combine(specDir, promptFile))
			: Util.AssetPath(Path.Combine("prompts", "system-default.md"));
		systemPrompt = File.ReadAllText(promptPath);

		// Shared game knowledge (unit counters, armor classes) appended to every
		// prompt variant so instruction A/Bs stay orthogonal to game facts.
		var guidePath = Util.AssetPath(Path.Combine("prompts", "strategy-guide.md"));
		if (File.Exists(guidePath))
			systemPrompt += "\n\n" + File.ReadAllText(guidePath);

		if (!provider.IsTest && role is not BootstrapSpec { Deterministic: true })
		{
			var apiKey = Environment.GetEnvironmentVariable(provider.ApiKeyEnv);
			if (string.IsNullOrEmpty(apiKey))
				throw new InvalidOperationException($"Provider '{providerKey}' requires env var {provider.ApiKeyEnv} to be set");
			llm = new LlmClient(provider.BaseUrl, apiKey, role?.TimeoutSeconds ?? player.TimeoutSeconds,
				msg => AppendErrorLog($"retry: {msg}"));
		}

		// Advisor/strategist: swarm role loops receive one shared instance (started
		// by the runner); a classic driver creates and owns its own.
		if (sharedAdvisor != null)
		{
			advisorLoop = sharedAdvisor;
			ownsAdvisor = false;
		}
		else if (role == null && player.Advisor != null && !provider.IsTest)
		{
			advisorLoop = new AdvisorLoop(runDir, spec, player, specDir);
			ownsAdvisor = true;
		}

		seqCounter = ScanExistingSeq();
	}

	string AgentDir => Path.Combine(runDir, "agents", player.Slug);
	string InboxDir => Path.Combine(runDir, "orders", player.Slug, "inbox");
	string ResultsDir => Path.Combine(runDir, "orders", player.Slug, "results");

	int EffectiveInterval => role != null ? role.TurnIntervalSeconds : spec.TurnIntervalSeconds;

	public async Task RunAsync(CancellationToken ct)
	{
		// The advisor rides alongside on the same cancellation token; the driver
		// loop never waits on it, so a slow (or dead) advisor cannot stall play.
		// Shared (swarm) advisors are started by the runner, not by role loops.
		if (ownsAdvisor)
			_ = advisorLoop?.RunAsync(ct);

		try
		{
			await Task.Delay(index * (EffectiveInterval > 0 ? 1500 : 300), ct);

			while (!ct.IsCancellationRequested)
			{
				try
				{
					if (!await StepAsync(ct))
					{
						Util.Log(Lane, "agent loop finished");
						return;
					}
				}
				catch (OperationCanceledException) when (ct.IsCancellationRequested)
				{
					return;
				}
				catch (Exception ex)
				{
					RecordError(ex);
				}

				if (spec.MaxTurnsPerPlayer > 0 && turnCount >= spec.MaxTurnsPerPlayer)
				{
					Util.Log(Lane, $"reached maxTurnsPerPlayer ({spec.MaxTurnsPerPlayer})");
					return;
				}

				// turnIntervalSeconds <= 0 means "as fast as the provider allows": poll for
				// fresh engine state (written 1/s) and take the next turn immediately.
				// StepAsync's tick gate prevents duplicate turns on stale state.
				await Task.Delay(EffectiveInterval > 0
					? TimeSpan.FromSeconds(EffectiveInterval)
					: TimeSpan.FromMilliseconds(250), ct);
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	/// <summary>One scheduler step. Returns false when the loop should stop (defeat / win decided).</summary>
	async Task<bool> StepAsync(CancellationToken ct)
	{
		var statePath = Path.Combine(runDir, "state", $"{player.Slug}.json");
		if (!File.Exists(statePath))
		{
			Util.Log(Lane, "no state file yet, skipping turn");
			return true;
		}

		var state = await ReadStateWithRetryAsync(statePath, ct);
		if (state == null)
		{
			Util.Log(Lane, "state file unreadable, skipping turn");
			return true;
		}

		var tick = state["tick"]?.GetValue<long>() ?? -1;
		if (tick == lastTick)
			return !IsDecided(state); // stale state; also stop if game already decided for us

		lastTick = tick;

		var results = CollectNewResults();
		await TakeTurnAsync(state, results, ct);

		return !IsDecided(state);
	}

	bool IsDecided(JsonObject state)
	{
		if (state["you"]?["defeated"]?.GetValue<bool>() == true)
			return true;

		var game = Util.TryReadJson(Path.Combine(runDir, "state", "game.json"));
		var winState = (game?["players"] as JsonArray)?
			.OfType<JsonObject>()
			.FirstOrDefault(p => p["slug"]?.GetValue<string>() == player.Slug)?["winState"]?.GetValue<string>();

		return winState is "Won" or "Lost";
	}

	static async Task<JsonObject?> ReadStateWithRetryAsync(string path, CancellationToken ct)
	{
		for (var attempt = 0; attempt < 2; attempt++)
		{
			try
			{
				return JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as JsonObject;
			}
			catch (Exception) when (attempt == 0)
			{
				await Task.Delay(250, ct);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception)
			{
				return null;
			}
		}

		return null;
	}

	List<JsonObject> CollectNewResults()
	{
		var found = new List<JsonObject>();
		if (!Directory.Exists(ResultsDir))
			return found;

		foreach (var file in Directory.GetFiles(ResultsDir, "*.json").Order())
		{
			var name = Path.GetFileName(file);

			// Swarm: results carry the same role-prefixed name as the inbox file
			// that produced them; each role only consumes its own.
			if (role != null && !name.StartsWith(Prefix, StringComparison.Ordinal))
				continue;

			if (!seenResults.Add(name))
				continue;

			pendingSubmissions.Remove(name);
			if (Util.TryReadJson(file) is JsonObject result)
				found.Add(result);
		}

		return found;
	}

	/// <summary>Maps every currently known production item name to its queue's base name
	/// (Building, Infantry, Vehicle, …) from the state's queue listings.</summary>
	static Dictionary<string, string> ItemQueueIndex(JsonObject state)
	{
		var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var q in state["production"] as JsonArray ?? [])
		{
			if (q is not JsonObject queue || queue["queue"]?.GetValue<string>() is not { } queueName)
				continue;

			var baseName = queueName.Split('.')[0];
			foreach (var b in queue["buildable"] as JsonArray ?? [])
				if (NameOf(b) is { } item)
					index.TryAdd(item, baseName);

			if (NameOf(queue["current"]) is { } current)
				index.TryAdd(current, baseName);

			// NOTE: "queued" entries are plain strings, not objects — indexing them
			// as objects threw and silently ate a whole turn's orders (2026-08-23).
			foreach (var w in queue["queued"] as JsonArray ?? [])
				if (NameOf(w) is { } waiting)
					index.TryAdd(waiting, baseName);
		}

		return index;
	}

	/// <summary>Item name from either shape: {"name": "Barracks", ...} or plain "Barracks".</summary>
	static string? NameOf(JsonNode? node) => node switch
	{
		JsonObject o => o["name"]?.GetValue<string>(),
		JsonValue v => v.TryGetValue<string>(out var s) ? s : null,
		_ => null,
	};

	/// <summary>Executes exactly one turn against the given state. Also used by the offline agent-turn command.</summary>
	public async Task TakeTurnAsync(JsonObject state, List<JsonObject> results, CancellationToken ct)
	{
		turnCount++;
		seqStr = Prefix + (++seqCounter).ToString("D6");
		var turnDir = Path.Combine(AgentDir, "turns", seqStr);
		Directory.CreateDirectory(turnDir);
		Util.WriteAtomic(Path.Combine(turnDir, "state.json"), state.ToJsonString(Util.Indented));

		var turnStartedUtc = DateTime.UtcNow;
		JsonArray orders;
		try
		{
			if (role is BootstrapSpec { Deterministic: true })
			{
				(orders, summaryLine) = BootstrapScript.Decide(state);
				summaryLine = Truncate(summaryLine, 160);
			}
			else
			{
				orders = provider.IsTest
					? TestTurn(state, turnDir)
					: await LlmTurnAsync(state, results, turnDir, ct);
			}

			lastError = null;
		}
		catch
		{
			requestStartedAtUtc = null;
			WriteStatus();
			throw;
		}

		// Swarm lane discipline: a role may only issue its whitelisted order types,
		// and production orders only for its whitelisted queues (this is what stops
		// the unit chief from building a Barracks). Dropped counts surface in the
		// log line so leaks are visible.
		var dropped = 0;
		if (role != null && orders.Count > 0)
		{
			var itemQueues = role.Queues.Count > 0 ? ItemQueueIndex(state) : [];
			var kept = new JsonArray();
			foreach (var o in orders.OfType<JsonObject>())
			{
				var type = o["type"]?.GetValue<string>() ?? "";
				if (allowedOrders != null && !allowedOrders.Contains(type))
				{
					dropped++;
					continue;
				}

				if (role.Queues.Count > 0 && type is "start_production" or "cancel_production"
					&& o["item"]?.GetValue<string>() is { } item
					&& itemQueues.TryGetValue(item, out var queue)
					&& !role.Queues.Contains(queue))
				{
					dropped++;
					continue;
				}

				// Mass-before-attack gate: no lone-unit death marches across the map.
				// Attacks near home (defense) always pass; far attack_moves need mass.
				if (role.MinAttackGroup > 0 && type == "attack_move"
					&& o["cell"] is JsonArray { Count: >= 2 } target
					&& state["map"]?["yourSpawnCell"] is JsonArray { Count: >= 2 } home)
				{
					var dx = (target[0]?.GetValue<int>() ?? 0) - (home[0]?.GetValue<int>() ?? 0);
					var dy = (target[1]?.GetValue<int>() ?? 0) - (home[1]?.GetValue<int>() ?? 0);
					var unitCount = (o["actorIds"] as JsonArray)?.Count ?? 0;
					if (dx * dx + dy * dy > 30 * 30 && unitCount < role.MinAttackGroup)
					{
						dropped++;
						continue;
					}
				}

				kept.Add(o.DeepClone());
			}

			orders = kept;
		}

		lastTurnSeconds = (DateTime.UtcNow - turnStartedUtc).TotalSeconds;
		turnLatencies.Enqueue(lastTurnSeconds.Value);
		while (turnLatencies.Count > LatencyWindow)
			turnLatencies.Dequeue();

		if (orders.Count > 0)
		{
			var inboxFile = Path.Combine(InboxDir, $"{seqStr}.json");
			Util.WriteAtomic(inboxFile, new JsonObject { ["orders"] = orders.DeepClone() }.ToJsonString(Util.Indented));
			RecordActions(state, orders);

			// Track until the engine's result comes back so the next prompt can say
			// "already submitted, don't re-issue" (fast turns outrun the engine).
			pendingSubmissions[seqStr + ".json"] = string.Join("; ", orders.OfType<JsonObject>()
				.Select(o => $"{o["type"]?.GetValue<string>()}{(o["item"] != null ? " " + o["item"]!.GetValue<string>() : "")}"));
		}

		Util.WriteAtomic(Path.Combine(turnDir, "orders.json"), new JsonObject { ["orders"] = orders.DeepClone() }.ToJsonString(Util.Indented));
		WriteStatus();
		Util.Log(Lane, $"turn {turnCount} seq {seqStr}: {summaryLine}"
			+ (dropped > 0 ? $" [{dropped} out-of-lane orders dropped]" : ""));
	}

	JsonArray TestTurn(JsonObject state, string turnDir)
	{
		var (orders, summary) = TestAgent.Decide(state);
		summaryLine = Truncate(summary, 160);
		return orders;
	}

	async Task<JsonArray> LlmTurnAsync(JsonObject state, List<JsonObject> results, string turnDir, CancellationToken ct)
	{
		var userMsg = new JsonObject { ["role"] = "user", ["content"] = BuildUserContent(state, results) };

		var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = systemPrompt } };
		foreach (var exchange in history)
			foreach (var m in exchange)
				messages.Add(m.DeepClone());
		messages.Add(userMsg.DeepClone());

		var payload = new JsonObject
		{
			["model"] = role?.Model ?? player.Model,
			["messages"] = messages,
			["temperature"] = role?.Temperature ?? player.Temperature,
			["max_tokens"] = role?.MaxTokens ?? player.MaxTokens,
			["tools"] = ToolSchema.Tools(swarm: role != null),
			["tool_choice"] = "auto",
		};

		if ((role?.ReasoningEffort ?? player.ReasoningEffort) is string effort)
			payload["reasoning_effort"] = effort;

		Util.WriteAtomic(Path.Combine(turnDir, "request.json"), payload.ToJsonString(Util.Indented));

		// Publish the in-flight window so the dashboard can show a live
		// "thinking for N seconds" indicator while we wait on the provider.
		requestStartedAtUtc = DateTime.UtcNow;
		WriteStatus();

		string raw;
		try
		{
			raw = await llm!.ChatAsync(payload, ct);
		}
		finally
		{
			lastResponseAtUtc = DateTime.UtcNow;
			requestStartedAtUtc = null;
		}

		Util.WriteAtomic(Path.Combine(turnDir, "response.json"), raw);

		var response = JsonNode.Parse(raw) as JsonObject
			?? throw new InvalidOperationException("Provider returned non-object JSON");

		if (response["usage"] is JsonObject usage)
		{
			totalPromptTokens += usage["prompt_tokens"]?.GetValue<long>() ?? 0;
			totalCompletionTokens += usage["completion_tokens"]?.GetValue<long>() ?? 0;

			// DeepInfra reports exact spend as estimated_cost; Nous Portal as cost.
			if (usage["estimated_cost"] is JsonValue cost)
				totalCostUsd += cost.GetValue<double>();
			else if (usage["cost"] is JsonValue nousCost)
				totalCostUsd += nousCost.GetValue<double>();
		}

		var message = response["choices"]?[0]?["message"] as JsonObject
			?? throw new InvalidOperationException("Provider response has no choices[0].message");

		var content = message["content"]?.GetValue<string>();

		// Merge orders from all issue_orders tool calls; none = pass.
		var orders = new JsonArray();
		var toolMessages = new List<JsonObject>();
		var textFallbackUsed = false;

		// Some models (e.g. Llama 3.1 on certain endpoints) emit their native
		// function-call template as plain text instead of structured tool_calls:
		//   <function=issue_orders>{"orders": [...]}<function>
		// Rescue those: extract the JSON payload so the model can still play.
		if (message["tool_calls"] is not JsonArray { Count: > 0 } && !string.IsNullOrEmpty(content))
		{
			var m = System.Text.RegularExpressions.Regex.Match(
				content, @"<function=issue_orders>\s*(\{.*?\})\s*(?:</?function>?|$)",
				System.Text.RegularExpressions.RegexOptions.Singleline);
			if (m.Success)
			{
				try
				{
					var args = JsonNode.Parse(m.Groups[1].Value);
					foreach (var order in args?["orders"] as JsonArray ?? [])
						if (order != null)
							orders.Add(order.DeepClone());

					textFallbackUsed = orders.Count > 0;
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					AppendErrorLog($"text-form tool call unparseable (treated as pass): {ex.Message}");
				}
			}
		}
		foreach (var tc in message["tool_calls"] as JsonArray ?? [])
		{
			if (tc is not JsonObject call)
				continue;

			var toolName = call["function"]?["name"]?.GetValue<string>();
			var toolResult = $"{{\"submitted\": true, \"seq\": \"{seqStr}\"}}";
			try
			{
				var args = JsonNode.Parse(call["function"]?["arguments"]?.GetValue<string>() ?? "{}");
				switch (toolName)
				{
					case "issue_orders":
						foreach (var order in args?["orders"] as JsonArray ?? [])
							if (order != null)
								orders.Add(order.DeepClone());
						break;

					case "set_goals":
						goals.Clear();
						foreach (var g in (args?["goals"] as JsonArray ?? []).Take(8))
							if (g is JsonValue v && v.TryGetValue<string>(out var goal) && goal.Length > 0)
								goals.Add(goal.Length > 200 ? goal[..200] : goal);
						PersistPlanning();
						toolResult = "{\"ok\": true}";
						break;

					case "post_request" when role != null:
					{
						var id = $"{role.Name}-{++requestCounter}";
						var requestsDir = Path.Combine(AgentDir, "requests");
						Directory.CreateDirectory(requestsDir);
						Util.WriteAtomic(Path.Combine(requestsDir, $"{id}.json"), new JsonObject
						{
							["id"] = id,
							["from"] = role.Name,
							["to"] = args?["to"]?.GetValue<string>() ?? "any",
							["need"] = Truncate(args?["need"]?.GetValue<string>() ?? "", 200),
							["why"] = Truncate(args?["why"]?.GetValue<string>() ?? "", 200),
							["priority"] = args?["priority"]?.GetValue<string>() ?? "normal",
							["status"] = "open",
							["postedAtUtc"] = DateTime.UtcNow.ToString("o"),
						}.ToJsonString(Util.Indented));
						toolResult = $"{{\"ok\": true, \"id\": \"{id}\"}}";
						break;
					}

					case "resolve_request" when role != null:
					{
						toolResult = "{\"ok\": false, \"reason\": \"unknown request id\"}";
						var reqId = args?["id"]?.GetValue<string>() ?? "";
						var reqPath = Path.Combine(AgentDir, "requests", $"{Path.GetFileName(reqId)}.json");
						if (File.Exists(reqPath) && Util.TryReadJson(reqPath) is JsonObject req)
						{
							req["status"] = "done";
							req["note"] = Truncate(args?["note"]?.GetValue<string>() ?? "", 200);
							req["resolvedBy"] = role.Name;
							req["resolvedAtUtc"] = DateTime.UtcNow.ToString("o");
							Util.WriteAtomic(reqPath, req.ToJsonString(Util.Indented));
							toolResult = "{\"ok\": true}";
						}

						break;
					}

					case "complete_checklist_item":
						toolResult = "{\"ok\": false, \"reason\": \"unknown checklist cell\"}";
						if (args?["cell"] is JsonArray { Count: 2 } cell)
						{
							var key = $"{cell[0]},{cell[1]}";
							if (checklist.ContainsKey(key))
							{
								// Honesty gate: the engine tells us whether this player has
								// actually explored the spawn. No scouting, no credit.
								if (!exploredSpawns.Contains(key))
								{
									toolResult = "{\"ok\": false, \"reason\": \"rejected: you have not explored that spawn area yet — scout it first\"}";
								}
								else
								{
									checklist[key] = true;
									PersistPlanning();
									toolResult = "{\"ok\": true, \"cleared\": \"" + key + "\"}";
								}
							}
						}

						break;
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// Broad by design: models emit duplicate-key JSON ("Key: id" — v3's
				// army lane lost whole turns to it), which throws ArgumentException,
				// not JsonException. One bad tool call must never eat the turn.
				AppendErrorLog($"tool call arguments unparseable (treated as pass): {ex.Message}");
				toolResult = "{\"ok\": false, \"reason\": \"arguments were not valid JSON\"}";
			}

			// Protocol correctness: every tool_call gets a tool-role result message.
			toolMessages.Add(new JsonObject
			{
				["role"] = "tool",
				["tool_call_id"] = call["id"]?.GetValue<string>() ?? "",
				["content"] = toolResult,
			});
		}

		// Rolling history: keep the last 6 exchange pairs, each a protocol-correct group.
		var assistantMsg = new JsonObject { ["role"] = "assistant", ["content"] = content };
		if (message["tool_calls"] is JsonArray tcs && tcs.Count > 0)
			assistantMsg["tool_calls"] = tcs.DeepClone();

		var group = new List<JsonObject> { userMsg, assistantMsg };
		group.AddRange(toolMessages);
		history.Add(group);

		// Each exchange embeds a full state snapshot — history length is the single
		// biggest prompt-size lever. Role lanes default to 2 (v3's 6 ballooned every
		// lane to ~15k tokens and 25-40s turns).
		var maxHistory = role == null ? 6 : role.HistoryTurns > 0 ? role.HistoryTurns : 2;
		while (history.Count > maxHistory)
			history.RemoveAt(0);

		summaryLine = textFallbackUsed
			? $"(text-form tool call rescued: {orders.Count} orders)"
			: !string.IsNullOrWhiteSpace(content)
			? Truncate(content.ReplaceLineEndings(" ").Trim(), 160)
			: orders.Count > 0 ? $"(tool call: {orders.Count} orders)" : "(pass)";

		return orders;
	}

	// Rolling factual log of the agent's own issued orders. The 6-exchange message
	// history only spans seconds at fast cadence; this digest spans minutes, so the
	// agent can see it already ordered/cancelled something before doing it again.
	void RecordActions(JsonObject state, JsonArray orders)
	{
		var sec = state["second"]?.GetValue<long>() ?? 0;
		var t = $"{sec / 60}:{sec % 60:D2}";
		foreach (var o in orders.OfType<JsonObject>())
		{
			var type = o["type"]?.GetValue<string>() ?? "?";
			var item = o["item"]?.GetValue<string>();
			var cell = o["cell"] is JsonArray c ? $" -> [{c[0]},{c[1]}]" : "";
			var count = o["count"]?.GetValue<int>() is int n and > 1 ? $" x{n}" : "";
			var actors = o["actorIds"] is JsonArray a ? $" ({a.Count} units)" : "";
			actionLog.Enqueue($"t={t} {type}{(item != null ? " " + item : "")}{count}{cell}{actors}");
		}

		while (actionLog.Count > 15)
			actionLog.Dequeue();
	}

	string BuildUserContent(JsonObject state, List<JsonObject> results)
	{
		var markdown = spec.StateFormat == "markdown";
		var text = markdown
			? $"Current game state:\n{StateMarkdown.Render(state)}"
			: $"Current game state:\n{state.ToJsonString()}";

		if (results.Count > 0)
		{
			if (markdown)
			{
				text += $"\n\nRecent order results:\n{StateMarkdown.RenderResults(results)}";
			}
			else
			{
				var arr = new JsonArray();
				foreach (var r in results)
					arr.Add(r.DeepClone());
				text += $"\n\nRecent order results:\n{arr.ToJsonString()}";
			}
		}

		if (player.RecentActions && actionLog.Count > 0)
			text += "\n\nYour recent actions (already done — do not repeat them):\n"
				+ string.Join("\n", actionLog.Select(a => "  " + a));

		if (pendingSubmissions.Count > 0)
			text += "\n\nORDERS ALREADY SUBMITTED, awaiting engine confirmation — do NOT re-issue these:\n"
				+ string.Join("\n", pendingSubmissions.Values.TakeLast(5).Select(p => "  " + p));

		// The elimination checklist only concerns roles that can actually attack;
		// build/econ specialists shouldn't burn tokens reasoning about it.
		var attackCapable = allowedOrders == null || allowedOrders.Contains("attack_move");
		if (attackCapable)
		{
			SyncChecklistFromState(state);

			if (checklist.Count > 0)
			{
				var items = checklist.Select(kv => $"  spawn [{kv.Key}] — {(kv.Value ? "CLEARED" : "PENDING")}");
				text += "\n\nElimination checklist (enemy start positions; mark cleared with complete_checklist_item once you confirm the base there is destroyed):\n"
					+ string.Join("\n", items);
			}
		}

		text += goals.Count > 0
			? "\n\nYour long-term goals (update with set_goals):\n" + string.Join("\n", goals.Select(g => "  - " + g))
			: "\n\nYou have no long-term goals set. Use set_goals to record your strategic plan.";

		var board = TeamBoard();
		if (board != null)
			text += "\n\nTEAM BOARD — your fellow specialists' current goals (coordinate, don't duplicate):\n" + board;

		var requests = RequestsDigest();
		if (requests != null)
			text += requests;

		// Advisor module: inject the latest review (sticky until superseded) so
		// slow, deep guidance persists across the driver's fast turns. Role lanes
		// get it capped — a 16k-token memo repeated into six prompts was a major
		// part of v3's token bloat.
		var advice = advisorLoop?.CurrentAdvice;
		if (advice != null)
		{
			if (role != null && advice.Length > 1500)
				advice = advice[..1500] + " …[truncated]";

			text += "\n\nADVISOR GUIDANCE — a deeper model reviewed your recent turns; weigh this seriously:\n" + advice;
		}

		return text;
	}

	void SyncChecklistFromState(JsonObject state)
	{
		foreach (var spawn in state["enemySpawns"] as JsonArray ?? [])
		{
			if (spawn is not JsonObject s || s["cell"] is not JsonArray { Count: 2 } cell)
				continue;

			var key = $"{cell[0]},{cell[1]}";
			checklist.TryAdd(key, false);
			if (s["explored"]?.GetValue<bool>() == true)
				exploredSpawns.Add(key);
		}
	}

	void PersistPlanning()
	{
		var goalsArr = new JsonArray();
		foreach (var g in goals)
			goalsArr.Add(g);

		// Swarm roles publish goals to the shared team board (one file per role, no
		// locking needed) so every specialist sees what its teammates are working on.
		if (role != null)
		{
			var boardDir = Path.Combine(AgentDir, "board");
			Directory.CreateDirectory(boardDir);
			Util.WriteAtomic(Path.Combine(boardDir, $"{role.Name}.json"), new JsonObject
			{
				["role"] = role.Name,
				["goals"] = goalsArr,
				["updatedAtUtc"] = DateTime.UtcNow.ToString("o"),
			}.ToJsonString(Util.Indented));
			return;
		}

		var checklistArr = new JsonArray();
		foreach (var (key, done) in checklist)
			checklistArr.Add(new JsonObject { ["cell"] = key, ["done"] = done });

		Util.WriteAtomic(Path.Combine(AgentDir, "planning.json"),
			new JsonObject { ["goals"] = goalsArr, ["checklist"] = checklistArr }.ToJsonString(Util.Indented));
	}

	/// <summary>Demand-system digest: open requests addressed to this role (act on them,
	/// resolve_request when done) plus this role's own still-open asks.</summary>
	string? RequestsDigest()
	{
		if (role == null)
			return null;

		var requestsDir = Path.Combine(AgentDir, "requests");
		if (!Directory.Exists(requestsDir))
			return null;

		var forMe = new List<string>();
		var mine = new List<string>();
		foreach (var file in Directory.GetFiles(requestsDir, "*.json").Order())
		{
			if (Util.TryReadJson(file) is not JsonObject r || r["status"]?.GetValue<string>() != "open")
				continue;

			var from = r["from"]?.GetValue<string>() ?? "?";
			var to = r["to"]?.GetValue<string>() ?? "any";
			var line = $"  [{r["id"]?.GetValue<string>()}] ({r["priority"]?.GetValue<string>()}) {r["need"]?.GetValue<string>()}"
				+ (r["why"]?.GetValue<string>() is { Length: > 0 } why ? $" — {why}" : "");

			if (from == role.Name)
				mine.Add($"{line} -> waiting on {to}");
			else if (to == role.Name || to == "any")
				forMe.Add($"{line} (from {from})");
		}

		var text = "";
		if (forMe.Count > 0)
			text += "\n\nOPEN REQUESTS FOR YOU — teammates need these from you; act and resolve_request when done:\n"
				+ string.Join("\n", forMe.TakeLast(8));
		if (mine.Count > 0)
			text += "\n\nYour own open requests (don't repost; they're visible to the team):\n"
				+ string.Join("\n", mine.TakeLast(5));

		return text.Length > 0 ? text : null;
	}

	/// <summary>Team board digest for swarm prompts: every other role's current goals.</summary>
	string? TeamBoard()
	{
		if (role == null)
			return null;

		var boardDir = Path.Combine(AgentDir, "board");
		if (!Directory.Exists(boardDir))
			return null;

		var lines = new List<string>();
		foreach (var file in Directory.GetFiles(boardDir, "*.json").Order())
		{
			var name = Path.GetFileNameWithoutExtension(file);
			if (name == role.Name)
				continue;

			if (Util.TryReadJson(file) is not JsonObject entry || entry["goals"] is not JsonArray g || g.Count == 0)
				continue;

			lines.Add($"  [{name}]");
			foreach (var goal in g)
				lines.Add($"    - {goal?.GetValue<string>()}");
		}

		return lines.Count > 0 ? string.Join("\n", lines) : null;
	}

	void WriteStatus()
	{
		// Swarm roles publish as dashboard module lanes (same shape the advisor
		// uses, so the existing UI renders them with zero changes) and refresh a
		// light player-level aggregate for the card header. Token/cost counters
		// live ONLY in the module files — the dashboard sums drivers + modules,
		// so an aggregate carrying them too would double-count.
		if (role != null)
		{
			var modulesDir = Path.Combine(AgentDir, "modules");
			Directory.CreateDirectory(modulesDir);
			Util.WriteAtomic(Path.Combine(modulesDir, $"{role.Name}.json"), new JsonObject
			{
				["name"] = role.Name,
				["model"] = role.Model ?? player.Model,
				["reasoningEffort"] = role.ReasoningEffort ?? player.ReasoningEffort,
				["requestStartedAtUtc"] = requestStartedAtUtc?.ToString("o"),
				["lastResponseAtUtc"] = lastResponseAtUtc?.ToString("o"),
				["lastTurnSeconds"] = lastTurnSeconds,
				["avgTurnSeconds"] = turnLatencies.Count > 0 ? Math.Round(turnLatencies.Average(), 1) : null,
				["cycles"] = turnCount,
				["totalPromptTokens"] = totalPromptTokens,
				["totalCompletionTokens"] = totalCompletionTokens,
				["totalCostUsd"] = totalCostUsd,
				["summaryLine"] = summaryLine,
				["lastError"] = lastError,
			}.ToJsonString(Util.Indented));

			var latest = new List<string>();
			foreach (var file in Directory.GetFiles(modulesDir, "*.json").Order())
				if (Util.TryReadJson(file) is JsonObject m && m["summaryLine"]?.GetValue<string>() is { Length: > 0 } s)
					latest.Add($"{m["name"]?.GetValue<string>()}: {Truncate(s, 60)}");

			Util.WriteAtomic(Path.Combine(AgentDir, "status.json"), new JsonObject
			{
				["seq"] = seqStr,
				["lastTurnAtUtc"] = DateTime.UtcNow.ToString("o"),
				["lastResponseAtUtc"] = lastResponseAtUtc?.ToString("o"),
				["model"] = player.Model,
				["provider"] = player.Provider,
				["summaryLine"] = string.Join(" | ", latest),
				["swarm"] = true,
			}.ToJsonString(Util.Indented));
			return;
		}

		var status = new JsonObject
		{
			["turn"] = turnCount,
			["seq"] = seqStr,
			["lastTurnAtUtc"] = DateTime.UtcNow.ToString("o"),
			["requestStartedAtUtc"] = requestStartedAtUtc?.ToString("o"),
			["lastResponseAtUtc"] = lastResponseAtUtc?.ToString("o"),
			["lastTurnSeconds"] = lastTurnSeconds,
			["avgTurnSeconds"] = turnLatencies.Count > 0 ? Math.Round(turnLatencies.Average(), 1) : null,
			["latencyWindow"] = turnLatencies.Count,
			["rateLimited429s"] = llm?.RateLimited429s ?? 0,
			["model"] = player.Model,
			["provider"] = player.Provider,
			["summaryLine"] = summaryLine,
			["totalPromptTokens"] = totalPromptTokens,
			["totalCompletionTokens"] = totalCompletionTokens,
			["totalCostUsd"] = totalCostUsd,
			["goals"] = new JsonArray([.. goals.Select(g => (JsonNode)g)]),
			["checklist"] = new JsonArray([.. checklist.Select(kv =>
				(JsonNode)new JsonObject { ["cell"] = kv.Key, ["done"] = kv.Value })]),
			["lastError"] = lastError,
		};

		Util.WriteAtomic(Path.Combine(AgentDir, "status.json"), status.ToJsonString(Util.Indented));
	}

	void RecordError(Exception ex)
	{
		lastError = ex.Message;
		AppendErrorLog($"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
		try
		{
			WriteStatus();
		}
		catch
		{
			// never let error reporting take the loop down
		}

		Util.Log(Lane, $"turn error: {ex.Message}");
	}

	void AppendErrorLog(string message)
	{
		try
		{
			Directory.CreateDirectory(AgentDir);
			File.AppendAllText(Path.Combine(AgentDir, "errors.log"), $"[{DateTime.UtcNow:o}] {message}\n");
		}
		catch
		{
		}
	}

	int ScanExistingSeq()
	{
		var max = 0;

		void Scan(IEnumerable<string> names)
		{
			foreach (var name in names)
			{
				var stem = Path.GetFileNameWithoutExtension(name);
				if (role != null)
				{
					if (!stem.StartsWith(Prefix, StringComparison.Ordinal))
						continue;
					stem = stem[Prefix.Length..];
				}

				if (int.TryParse(stem, out var n) && n > max)
					max = n;
			}
		}

		if (Directory.Exists(InboxDir))
			Scan(Directory.GetFiles(InboxDir, "*.json"));
		if (Directory.Exists(ResultsDir))
			Scan(Directory.GetFiles(ResultsDir, "*.json"));

		var turnsDir = Path.Combine(AgentDir, "turns");
		if (Directory.Exists(turnsDir))
			Scan(Directory.GetDirectories(turnsDir));

		return max;
	}

	static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
