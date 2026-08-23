using System.Text.Json.Nodes;

namespace Orf;

/// <summary>
/// Runs one swarm player through its lifecycle: a solo bootstrap commander nails the
/// opening, hands off to the core specialist threads once the base is established,
/// and dynamic threads spawn as the game heats up (the swarm scales with the fight).
/// All threads share the player's inbox, team board, demand system, and strategist.
/// </summary>
public sealed class SwarmCoordinator
{
	readonly string runDir;
	readonly Spec spec;
	readonly PlayerSpec player;
	readonly int index;
	readonly string specDir;
	readonly SwarmSpec swarm;

	public SwarmCoordinator(string runDir, Spec spec, PlayerSpec player, int index, string specDir)
	{
		this.runDir = runDir;
		this.spec = spec;
		this.player = player;
		this.index = index;
		this.specDir = specDir;
		swarm = player.Swarm!;
	}

	string Lane => player.Slug + "/coord";

	public async Task RunAsync(CancellationToken ct)
	{
		var tasks = new List<Task>();
		try
		{
			AdvisorLoop? strategist = null;
			if (player.Advisor != null && !spec.ProviderFor(player).IsTest)
			{
				strategist = new AdvisorLoop(runDir, spec, player, specDir);
				tasks.Add(strategist.RunAsync(ct));
			}

			// Phase 1 — bootstrap: one commander with full authority opens the game.
			if (swarm.Bootstrap is { } boot)
			{
				using var bootCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
				var bootTask = new AgentLoop(runDir, spec, player, index, specDir, boot, strategist).RunAsync(bootCts.Token);
				Util.Log(Lane, $"bootstrap '{boot.Name}' commanding solo (handoff: {boot.HandoffBuilding ?? "none"} or t={boot.HandoffAtSeconds}s)");

				while (!ct.IsCancellationRequested && !bootTask.IsCompleted)
				{
					if (ReadState() is { } state && HandoffReady(state, boot))
						break;

					await Task.Delay(2000, ct);
				}

				bootCts.Cancel();
				await Task.WhenAny(bootTask, Task.Delay(5000, CancellationToken.None));
				MarkHandedOff(boot);
				SeedBoardFromState(boot);
				Util.Log(Lane, $"handoff — spawning {swarm.Roles.Count} specialist threads");
			}

			// Phase 2 — the core swarm.
			foreach (var role in swarm.Roles)
				tasks.Add(new AgentLoop(runDir, spec, player, index, specDir, role, strategist).RunAsync(ct));

			// Phase 3 — dynamic scaling: watch the game and grow the swarm.
			var spawned = new Dictionary<string, int>();
			var sustained = new Dictionary<string, int>();
			while (!ct.IsCancellationRequested
				&& swarm.Dynamic.Any(r => spawned.GetValueOrDefault(r.Name) < r.Cap)
				&& !tasks.All(t => t.IsCompleted))
			{
				await Task.Delay(2000, ct);
				if (ReadState() is not { } state)
					continue;

				var second = state["second"]?.GetValue<long>() ?? 0;
				foreach (var rule in swarm.Dynamic)
				{
					var count = spawned.GetValueOrDefault(rule.Name);
					if (count >= rule.Cap)
						continue;

					bool fire;
					if (rule.Trigger == "under_attack")
					{
						var attacked = (state["underAttack"] as JsonArray)?.Count > 0;
						sustained[rule.Name] = attacked ? sustained.GetValueOrDefault(rule.Name) + 1 : 0;
						fire = sustained[rule.Name] >= 2; // two consecutive polls = real fight, not a stray shot
					}
					else
					{
						fire = int.TryParse(rule.Trigger["at_seconds:".Length..], out var at) && second >= at;
					}

					if (!fire)
						continue;

					spawned[rule.Name] = ++count;
					sustained[rule.Name] = 0;
					var instance = count == 1 ? rule : CloneWithName(rule, $"{rule.Name}{count}");
					Util.Log(Lane, $"trigger '{rule.Trigger}' fired at t={second}s — spawning '{instance.Name}' ({count}/{rule.Cap})");
					tasks.Add(new AgentLoop(runDir, spec, player, index, specDir, instance, strategist).RunAsync(ct));
				}
			}

			await Task.WhenAll(tasks);
		}
		catch (OperationCanceledException)
		{
		}
	}

	JsonObject? ReadState() => Util.TryReadJson(Path.Combine(runDir, "state", $"{player.Slug}.json")) as JsonObject;

	static bool HandoffReady(JsonObject state, BootstrapSpec boot)
	{
		var second = state["second"]?.GetValue<long>() ?? 0;
		if (second >= boot.HandoffAtSeconds)
			return true;

		if (boot.HandoffBuilding is not { Length: > 0 } target)
			return false;

		foreach (var b in state["buildings"] as JsonArray ?? [])
			if (string.Equals(b?["name"]?.GetValue<string>(), target, StringComparison.OrdinalIgnoreCase))
				return true;

		return false;
	}

	/// <summary>Leave a tombstone on the bootstrap's dashboard lane so the stream can see the baton pass.</summary>
	void MarkHandedOff(BootstrapSpec boot)
	{
		try
		{
			var path = Path.Combine(runDir, "agents", player.Slug, "modules", $"{boot.Name}.json");
			if (Util.TryReadJson(path) is not JsonObject status)
				return;

			status["summaryLine"] = "✔ handed off to the swarm";
			status["requestStartedAtUtc"] = null;
			Util.WriteAtomic(path, status.ToJsonString(Util.Indented));
		}
		catch
		{
			// cosmetic only
		}
	}

	/// <summary>Brief the incoming specialists: the bootstrap posts what it built to the
	/// team board, so every role's first prompt says exactly where the opening left off.</summary>
	void SeedBoardFromState(BootstrapSpec boot)
	{
		try
		{
			var state = ReadState();
			var built = (state?["buildings"] as JsonArray ?? []).OfType<JsonObject>()
				.Select(b => b["name"]?.GetValue<string>())
				.OfType<string>()
				.ToList();

			var boardDir = Path.Combine(runDir, "agents", player.Slug, "board");
			Directory.CreateDirectory(boardDir);
			Util.WriteAtomic(Path.Combine(boardDir, $"{boot.Name}.json"), new JsonObject
			{
				["role"] = boot.Name,
				["goals"] = new JsonArray(
					$"Opening handoff at t={state?["second"]?.GetValue<long>() ?? 0}s. Standing: {string.Join(", ", built)}.",
					"Build: continue the doctrine from here (2nd Power, Weapons Factory next). Produce: start infantry + scouts NOW. Army: get scouts moving toward the enemy spawn. Econ: verify the harvester is on tiberium."),
				["updatedAtUtc"] = DateTime.UtcNow.ToString("o"),
			}.ToJsonString(Util.Indented));
		}
		catch
		{
			// briefing is best-effort
		}
	}

	static DynamicRoleSpec CloneWithName(DynamicRoleSpec r, string name) => new()
	{
		Name = name,
		PromptFile = r.PromptFile,
		Provider = r.Provider,
		Model = r.Model,
		ReasoningEffort = r.ReasoningEffort,
		Temperature = r.Temperature,
		MaxTokens = r.MaxTokens,
		TimeoutSeconds = r.TimeoutSeconds,
		Orders = [.. r.Orders],
		Queues = [.. r.Queues],
		TurnIntervalSeconds = r.TurnIntervalSeconds,
		Trigger = r.Trigger,
		Cap = r.Cap,
	};
}
