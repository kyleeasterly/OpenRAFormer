using System.Text;
using System.Text.Json.Nodes;

namespace Orf;

/// <summary>
/// Add-on module: a slower/deeper model periodically reviews the driver agent's
/// recent turns (read back from the persisted turn dirs) and publishes advice.
/// Fully asynchronous — the driver never waits on it; whatever advice is current
/// gets injected into the driver's next prompt. Status is published to
/// agents/&lt;slug&gt;/modules/&lt;name&gt;.json so the dashboard renders it as an extra lane.
/// </summary>
public sealed class AdvisorLoop
{
	readonly string runDir;
	readonly Spec spec;
	readonly PlayerSpec player;
	readonly AdvisorSpec advisor;
	readonly LlmClient llm;
	readonly string systemPrompt;

	volatile string? currentAdvice;
	int consumedTurns;
	int cycles;
	DateTime? requestStartedAtUtc;
	DateTime? lastResponseAtUtc;
	double? lastCycleSeconds;
	readonly Queue<double> cycleLatencies = new();
	long totalPromptTokens;
	long totalCompletionTokens;
	double totalCostUsd;
	string? lastError;
	string lastAdviceShort = "";
	string basedOnTurns = "";

	public AdvisorLoop(string runDir, Spec spec, PlayerSpec player, string specDir)
	{
		this.runDir = runDir;
		this.spec = spec;
		this.player = player;
		advisor = player.Advisor ?? throw new InvalidOperationException($"Player '{player.Slug}' has no advisor spec");

		var providerName = advisor.Provider ?? player.Provider;
		var provider = spec.Providers[providerName];
		var apiKey = Environment.GetEnvironmentVariable(provider.ApiKeyEnv);
		if (string.IsNullOrEmpty(apiKey))
			throw new InvalidOperationException($"Advisor for '{player.Slug}' requires env var {provider.ApiKeyEnv} to be set");
		llm = new LlmClient(provider.BaseUrl, apiKey, advisor.TimeoutSeconds,
			msg => AppendErrorLog($"retry: {msg}"));

		var promptPath = advisor.PromptFile != null
			? Path.GetFullPath(Path.Combine(specDir, advisor.PromptFile))
			: Util.AssetPath(Path.Combine("prompts", "advisor-default.md"));
		systemPrompt = File.ReadAllText(promptPath);

		var guidePath = Util.AssetPath(Path.Combine("prompts", "strategy-guide.md"));
		if (File.Exists(guidePath))
			systemPrompt += "\n\n" + File.ReadAllText(guidePath);
	}

	string AgentDir => Path.Combine(runDir, "agents", player.Slug);
	string TurnsDir => Path.Combine(AgentDir, "turns");
	string ModulesDir => Path.Combine(AgentDir, "modules");
	string AdviceDir => Path.Combine(AgentDir, "advice");

	/// <summary>Latest advice formatted for prompt injection, or null before the first review lands.</summary>
	public string? CurrentAdvice => currentAdvice;

	public async Task RunAsync(CancellationToken ct)
	{
		Directory.CreateDirectory(ModulesDir);
		Directory.CreateDirectory(AdviceDir);
		WriteStatus();

		try
		{
			while (!ct.IsCancellationRequested)
			{
				var turns = CompletedTurns();
				if (turns.Count >= advisor.MinNewTurns && turns.Count - consumedTurns >= advisor.MinNewTurns)
				{
					try
					{
						await AdviseAsync(turns, ct);
						lastError = null;
					}
					catch (OperationCanceledException) when (ct.IsCancellationRequested)
					{
						return;
					}
					catch (Exception ex)
					{
						lastError = ex.Message;
						WriteStatus();
						Util.Log(player.Slug, $"advisor error: {ex.Message}");
						await Task.Delay(5000, ct);
					}
				}

				await Task.Delay(1000, ct);
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	/// <summary>Runs a single review cycle against whatever turns exist (offline testing via `orf advisor-once`).</summary>
	public async Task<bool> RunOnceAsync(CancellationToken ct)
	{
		Directory.CreateDirectory(ModulesDir);
		Directory.CreateDirectory(AdviceDir);

		var turns = CompletedTurns();
		if (turns.Count == 0)
			return false;

		await AdviseAsync(turns, ct);
		return true;
	}

	/// <summary>Turn dirs whose orders.json exists (i.e. the driver finished that turn), sorted by seq.</summary>
	List<string> CompletedTurns()
	{
		if (!Directory.Exists(TurnsDir))
			return [];

		return [.. Directory.GetDirectories(TurnsDir)
			.Where(d => File.Exists(Path.Combine(d, "orders.json")))
			.OrderBy(d => d, StringComparer.Ordinal)];
	}

	async Task AdviseAsync(List<string> turns, CancellationToken ct)
	{
		var window = turns.Skip(Math.Max(0, turns.Count - advisor.WindowTurns)).ToList();
		var digest = BuildDigest(window);
		if (digest == null)
			return; // latest state not readable yet; try again next poll

		var payload = new JsonObject
		{
			["model"] = advisor.Model,
			["messages"] = new JsonArray
			{
				new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
				new JsonObject { ["role"] = "user", ["content"] = digest },
			},
			["temperature"] = advisor.Temperature,
			["max_tokens"] = advisor.MaxTokens,
		};

		if (advisor.ReasoningEffort != null)
			payload["reasoning_effort"] = advisor.ReasoningEffort;

		var startedUtc = DateTime.UtcNow;
		requestStartedAtUtc = startedUtc;
		WriteStatus();

		string raw;
		try
		{
			raw = await llm.ChatAsync(payload, ct);
		}
		finally
		{
			lastResponseAtUtc = DateTime.UtcNow;
			requestStartedAtUtc = null;
		}

		var cycleSeconds = (DateTime.UtcNow - startedUtc).TotalSeconds;
		lastCycleSeconds = cycleSeconds;
		cycleLatencies.Enqueue(cycleSeconds);
		while (cycleLatencies.Count > 10)
			cycleLatencies.Dequeue();

		var response = JsonNode.Parse(raw) as JsonObject
			?? throw new InvalidOperationException("Advisor provider returned non-object JSON");

		if (response["usage"] is JsonObject usage)
		{
			totalPromptTokens += usage["prompt_tokens"]?.GetValue<long>() ?? 0;
			totalCompletionTokens += usage["completion_tokens"]?.GetValue<long>() ?? 0;
			if (usage["estimated_cost"] is JsonValue cost)
				totalCostUsd += cost.GetValue<double>();
			else if (usage["cost"] is JsonValue nousCost)
				totalCostUsd += nousCost.GetValue<double>();
		}

		var content = (response["choices"]?[0]?["message"] as JsonObject)?["content"]?.GetValue<string>();
		if (string.IsNullOrWhiteSpace(content))
			throw new InvalidOperationException("Advisor returned empty content");

		cycles++;
		var firstSeq = Path.GetFileName(window[0]);
		var lastSeq = Path.GetFileName(window[^1]);
		basedOnTurns = $"{firstSeq}–{lastSeq}";

		var gameClock = LatestGameClock(window);
		currentAdvice = $"[advisor review issued at game {gameClock}, covering your turns {basedOnTurns}]\n{content.Trim()}";
		lastAdviceShort = Truncate(content.Trim(), 300);
		consumedTurns = turns.Count;

		Util.WriteAtomic(Path.Combine(AdviceDir, $"{cycles:D4}.json"), new JsonObject
		{
			["atUtc"] = DateTime.UtcNow.ToString("o"),
			["gameClock"] = gameClock,
			["basedOnTurns"] = basedOnTurns,
			["advice"] = content.Trim(),
		}.ToJsonString(Util.Indented));

		WriteStatus();
		Util.Log(player.Slug, $"advisor review #{cycles} ({basedOnTurns}): {Truncate(content.Trim(), 110)}");
	}

	/// <summary>Current full state (as the driver sees it) plus a compact digest of the window's turns.</summary>
	string? BuildDigest(List<string> window)
	{
		if (Util.TryReadJson(Path.Combine(window[^1], "state.json")) is not JsonObject latestState)
			return null;

		var sb = new StringBuilder();
		sb.AppendLine("You are reviewing the play of your driver agent. Its current game state:");
		sb.AppendLine();
		sb.AppendLine(spec.StateFormat == "markdown" ? StateMarkdown.Render(latestState) : latestState.ToJsonString());
		sb.AppendLine();
		sb.AppendLine($"## The driver's last {window.Count} turns (oldest first)");

		foreach (var turnDir in window)
		{
			var seq = Path.GetFileName(turnDir);
			var second = (Util.TryReadJson(Path.Combine(turnDir, "state.json")) as JsonObject)?["second"]?.GetValue<long>();
			sb.AppendLine();
			sb.AppendLine($"### turn {seq}{(second != null ? $" — game {second / 60}:{second % 60:D2}" : "")}");

			if (Util.TryReadJson(Path.Combine(turnDir, "response.json")) is JsonObject resp
				&& resp["choices"]?[0]?["message"] is JsonObject msg)
			{
				var commentary = msg["content"]?.GetValue<string>();
				if (!string.IsNullOrWhiteSpace(commentary))
					sb.AppendLine($"driver commentary: {Truncate(commentary.Trim(), 500)}");
			}

			if (Util.TryReadJson(Path.Combine(turnDir, "orders.json")) is JsonObject ordersDoc
				&& ordersDoc["orders"] is JsonArray orders)
				sb.AppendLine(orders.Count > 0 ? $"orders: {Truncate(orders.ToJsonString(), 600)}" : "orders: (none — passed)");

			var resultsPath = Path.Combine(runDir, "orders", player.Slug, "results", $"{seq}.json");
			if (Util.TryReadJson(resultsPath) is JsonObject resultsDoc && resultsDoc["results"] is JsonArray results)
			{
				var parts = results.OfType<JsonObject>().Select(r =>
				{
					var status = r["status"]?.GetValue<string>() ?? "?";
					var reason = r["reason"]?.GetValue<string>();
					return reason != null ? $"{status}({Truncate(reason, 80)})" : status;
				});
				sb.AppendLine($"engine results: {string.Join(", ", parts)}");
			}
		}

		sb.AppendLine();
		sb.AppendLine("Write your advice for the driver's next several turns now.");
		return sb.ToString();
	}

	static string LatestGameClock(List<string> window)
	{
		var second = (Util.TryReadJson(Path.Combine(window[^1], "state.json")) as JsonObject)?["second"]?.GetValue<long>() ?? 0;
		return $"{second / 60}:{second % 60:D2}";
	}

	void WriteStatus()
	{
		// Cadence fields deliberately mirror the driver's status.json so the
		// dashboard renders driver and module lanes with the same logic.
		var status = new JsonObject
		{
			["name"] = advisor.Name,
			["model"] = advisor.Model,
			["reasoningEffort"] = advisor.ReasoningEffort,
			["requestStartedAtUtc"] = requestStartedAtUtc?.ToString("o"),
			["lastResponseAtUtc"] = lastResponseAtUtc?.ToString("o"),
			["lastTurnSeconds"] = lastCycleSeconds,
			["avgTurnSeconds"] = cycleLatencies.Count > 0 ? Math.Round(cycleLatencies.Average(), 1) : null,
			["cycles"] = cycles,
			["basedOnTurns"] = basedOnTurns,
			["lastAdvice"] = lastAdviceShort,
			["totalPromptTokens"] = totalPromptTokens,
			["totalCompletionTokens"] = totalCompletionTokens,
			["totalCostUsd"] = totalCostUsd,
			["rateLimited429s"] = llm.RateLimited429s,
			["lastError"] = lastError,
		};

		Util.WriteAtomic(Path.Combine(ModulesDir, $"{advisor.Name}.json"), status.ToJsonString(Util.Indented));
	}

	void AppendErrorLog(string message)
	{
		try
		{
			Directory.CreateDirectory(AgentDir);
			File.AppendAllText(Path.Combine(AgentDir, $"{advisor.Name}-errors.log"), $"[{DateTime.UtcNow:HH:mm:ss}] {message}\n");
		}
		catch
		{
			// never let logging take the advisor down
		}
	}

	static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
