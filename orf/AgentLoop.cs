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

	public AgentLoop(string runDir, Spec spec, PlayerSpec player, int index, string specDir)
	{
		this.runDir = runDir;
		this.spec = spec;
		this.player = player;
		this.index = index;

		provider = spec.ProviderFor(player);

		var promptPath = player.PromptFile != null
			? Path.GetFullPath(Path.Combine(specDir, player.PromptFile))
			: Util.AssetPath(Path.Combine("prompts", "system-default.md"));
		systemPrompt = File.ReadAllText(promptPath);

		// Shared game knowledge (unit counters, armor classes) appended to every
		// prompt variant so instruction A/Bs stay orthogonal to game facts.
		var guidePath = Util.AssetPath(Path.Combine("prompts", "strategy-guide.md"));
		if (File.Exists(guidePath))
			systemPrompt += "\n\n" + File.ReadAllText(guidePath);

		if (!provider.IsTest)
		{
			var apiKey = Environment.GetEnvironmentVariable(provider.ApiKeyEnv);
			if (string.IsNullOrEmpty(apiKey))
				throw new InvalidOperationException($"Provider '{player.Provider}' requires env var {provider.ApiKeyEnv} to be set");
			llm = new LlmClient(provider.BaseUrl, apiKey);
		}

		seqCounter = ScanExistingSeq();
	}

	string AgentDir => Path.Combine(runDir, "agents", player.Slug);
	string InboxDir => Path.Combine(runDir, "orders", player.Slug, "inbox");
	string ResultsDir => Path.Combine(runDir, "orders", player.Slug, "results");

	public async Task RunAsync(CancellationToken ct)
	{
		try
		{
			await Task.Delay(index * (spec.TurnIntervalSeconds > 0 ? 1500 : 300), ct);

			while (!ct.IsCancellationRequested)
			{
				try
				{
					if (!await StepAsync(ct))
					{
						Util.Log(player.Slug, "agent loop finished");
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
					Util.Log(player.Slug, $"reached maxTurnsPerPlayer ({spec.MaxTurnsPerPlayer})");
					return;
				}

				// turnIntervalSeconds <= 0 means "as fast as the provider allows": poll for
				// fresh engine state (written 1/s) and take the next turn immediately.
				// StepAsync's tick gate prevents duplicate turns on stale state.
				await Task.Delay(spec.TurnIntervalSeconds > 0
					? TimeSpan.FromSeconds(spec.TurnIntervalSeconds)
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
			Util.Log(player.Slug, "no state file yet, skipping turn");
			return true;
		}

		var state = await ReadStateWithRetryAsync(statePath, ct);
		if (state == null)
		{
			Util.Log(player.Slug, "state file unreadable, skipping turn");
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
			if (!seenResults.Add(name))
				continue;
			if (Util.TryReadJson(file) is JsonObject result)
				found.Add(result);
		}

		return found;
	}

	/// <summary>Executes exactly one turn against the given state. Also used by the offline agent-turn command.</summary>
	public async Task TakeTurnAsync(JsonObject state, List<JsonObject> results, CancellationToken ct)
	{
		turnCount++;
		seqStr = (++seqCounter).ToString("D6");
		var turnDir = Path.Combine(AgentDir, "turns", seqStr);
		Directory.CreateDirectory(turnDir);
		Util.WriteAtomic(Path.Combine(turnDir, "state.json"), state.ToJsonString(Util.Indented));

		JsonArray orders;
		try
		{
			orders = provider.IsTest
				? TestTurn(state, turnDir)
				: await LlmTurnAsync(state, results, turnDir, ct);
			lastError = null;
		}
		catch
		{
			WriteStatus();
			throw;
		}

		if (orders.Count > 0)
		{
			var inboxFile = Path.Combine(InboxDir, $"{seqStr}.json");
			Util.WriteAtomic(inboxFile, new JsonObject { ["orders"] = orders.DeepClone() }.ToJsonString(Util.Indented));
			RecordActions(state, orders);
		}

		Util.WriteAtomic(Path.Combine(turnDir, "orders.json"), new JsonObject { ["orders"] = orders.DeepClone() }.ToJsonString(Util.Indented));
		WriteStatus();
		Util.Log(player.Slug, $"turn {turnCount} seq {seqStr}: {summaryLine}");
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
			["model"] = player.Model,
			["messages"] = messages,
			["temperature"] = player.Temperature,
			["max_tokens"] = 2000,
			["tools"] = ToolSchema.Tools(),
			["tool_choice"] = "auto",
		};

		Util.WriteAtomic(Path.Combine(turnDir, "request.json"), payload.ToJsonString(Util.Indented));

		var raw = await llm!.ChatAsync(payload, ct);
		Util.WriteAtomic(Path.Combine(turnDir, "response.json"), raw);

		var response = JsonNode.Parse(raw) as JsonObject
			?? throw new InvalidOperationException("Provider returned non-object JSON");

		if (response["usage"] is JsonObject usage)
		{
			totalPromptTokens += usage["prompt_tokens"]?.GetValue<long>() ?? 0;
			totalCompletionTokens += usage["completion_tokens"]?.GetValue<long>() ?? 0;

			// DeepInfra reports exact spend per request; other providers may not.
			if (usage["estimated_cost"] is JsonValue cost)
				totalCostUsd += cost.GetValue<double>();
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
				catch (System.Text.Json.JsonException ex)
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
			catch (System.Text.Json.JsonException ex)
			{
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
		while (history.Count > 6)
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

		SyncChecklistFromState(state);

		if (checklist.Count > 0)
		{
			var items = checklist.Select(kv => $"  spawn [{kv.Key}] — {(kv.Value ? "CLEARED" : "PENDING")}");
			text += "\n\nElimination checklist (enemy start positions; mark cleared with complete_checklist_item once you confirm the base there is destroyed):\n"
				+ string.Join("\n", items);
		}

		text += goals.Count > 0
			? "\n\nYour long-term goals (update with set_goals):\n" + string.Join("\n", goals.Select(g => "  - " + g))
			: "\n\nYou have no long-term goals set. Use set_goals to record your strategic plan.";

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

		var checklistArr = new JsonArray();
		foreach (var (key, done) in checklist)
			checklistArr.Add(new JsonObject { ["cell"] = key, ["done"] = done });

		Util.WriteAtomic(Path.Combine(AgentDir, "planning.json"),
			new JsonObject { ["goals"] = goalsArr, ["checklist"] = checklistArr }.ToJsonString(Util.Indented));
	}

	void WriteStatus()
	{
		var status = new JsonObject
		{
			["turn"] = turnCount,
			["seq"] = seqStr,
			["lastTurnAtUtc"] = DateTime.UtcNow.ToString("o"),
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

		Util.Log(player.Slug, $"turn error: {ex.Message}");
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
				if (int.TryParse(Path.GetFileNameWithoutExtension(name), out var n) && n > max)
					max = n;
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
