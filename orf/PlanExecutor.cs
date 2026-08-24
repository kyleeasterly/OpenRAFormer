using System.Text.Json.Nodes;

namespace Orf;

/// <summary>
/// Executes the standing plans the AGENT wrote for itself, between its turns.
///
/// The agent decides once every 8-40 seconds; the game does not wait. A human
/// opens with ~12 deliberate actions in the first 90 seconds, which no
/// LLM-rate loop can match by issuing immediate orders alone. So the agent
/// instead declares intent once — an ordered build plan and a set of standing
/// production rules — and this executor carries it out at state-poll rate
/// (~1 Hz) until the agent revises it.
///
/// Nothing here is doctrine: the executor is inert until the agent calls
/// set_build_plan / set_production_rules, and it only ever does what those
/// calls said. It replaces the old hardcoded BootstrapScript, which played the
/// opening *for* the agent and made the opening a constant rather than a
/// variable under study.
/// </summary>
public sealed class PlanExecutor
{
	public sealed class BuildStep
	{
		public string Item = "";
		public string Placement = "near_base";
		public int[]? Cell;
		public bool Done;
		public bool Started;
	}

	sealed class Rule
	{
		public string Item = "";
		public int Maintain;
	}

	readonly List<BuildStep> plan = [];
	readonly List<Rule> rules = [];
	readonly Queue<string> activity = new();

	// Anti-double-order cooldown. The engine confirms an order (~0.4 s) before the
	// next state export shows it in the queue (~1 s), so "is it in production yet?"
	// is briefly false after we already ordered it — which duplicated a Power Plant
	// in the first live test. Remember what we issued and when, in game ticks.
	readonly Dictionary<string, long> lastIssuedTick = new(StringComparer.OrdinalIgnoreCase);
	const long IssueCooldownTicks = 150;

	public int PlanCount => plan.Count;
	public int RuleCount => rules.Count;

	static readonly string[] Placements =
		["near_tiberium", "toward_enemy", "back_of_base", "near_base"];

	/// <summary>Replaces the build plan. Returns a short outcome for the tool result.</summary>
	public string SetPlan(JsonArray? steps)
	{
		plan.Clear();
		foreach (var s in steps ?? [])
		{
			if (s is not JsonObject step)
				continue;

			var item = step["item"]?.GetValue<string>();
			if (string.IsNullOrWhiteSpace(item))
				continue;

			var placement = step["placement"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "near_base";
			int[]? cell = null;
			if (step["cell"] is JsonArray { Count: >= 2 } c)
				cell = [c[0]?.GetValue<int>() ?? 0, c[1]?.GetValue<int>() ?? 0];
			else if (!Placements.Contains(placement))
				placement = "near_base";

			plan.Add(new BuildStep { Item = item, Placement = placement, Cell = cell });
			if (plan.Count >= 24)
				break;
		}

		return $"{{\"ok\": true, \"steps\": {plan.Count}}}";
	}

	/// <summary>Replaces the standing production rules.</summary>
	public string SetRules(JsonArray? incoming)
	{
		rules.Clear();
		foreach (var r in incoming ?? [])
		{
			if (r is not JsonObject rule)
				continue;

			var item = rule["item"]?.GetValue<string>();
			if (string.IsNullOrWhiteSpace(item))
				continue;

			var maintain = rule["maintain"]?.GetValue<int>() ?? 1;
			rules.Add(new Rule { Item = item, Maintain = Math.Clamp(maintain, 1, 10) });
			if (rules.Count >= 8)
				break;
		}

		return $"{{\"ok\": true, \"rules\": {rules.Count}}}";
	}

	/// <summary>
	/// One executor pass over fresh state. Returns the orders to submit (possibly
	/// empty). <paramref name="inFlight"/> is the set of item names this agent has
	/// already submitted but the engine has not confirmed — never double-order those.
	/// </summary>
	public JsonArray Tick(JsonObject state, ISet<string> inFlight)
	{
		var orders = new JsonArray();
		if (plan.Count == 0 && rules.Count == 0)
			return orders;

		var cash = state["you"]?["cash"]?.GetValue<long>() ?? 0;
		var tick = state["tick"]?.GetValue<long>() ?? 0;
		var buildable = BuildableIndex(state);
		var owned = OwnedBuildingNames(state);
		var inProduction = InProductionCounts(state);

		bool OnCooldown(string key) =>
			lastIssuedTick.TryGetValue(key, out var at) && tick - at < IssueCooldownTicks;

		// 1. Reconcile the plan against reality, from scratch every tick.
		//    Counting rather than latching matters: "Power Plant" twice in a plan
		//    means two power plants, and a step whose building was destroyed
		//    reverts to pending so the plan rebuilds it.
		var remaining = new Dictionary<string, int>(owned, StringComparer.OrdinalIgnoreCase);
		foreach (var step in plan)
		{
			if (remaining.GetValueOrDefault(step.Item) > 0)
			{
				remaining[step.Item]--;
				step.Done = true;
			}
			else
			{
				step.Done = false;
			}
		}

		foreach (var p in state["pendingPlacement"] as JsonArray ?? [])
		{
			if (p is not JsonObject pending)
				continue;

			var item = pending["item"]?.GetValue<string>();
			if (item == null || inFlight.Contains("place:" + item) || OnCooldown("place:" + item))
				continue;

			var step = plan.FirstOrDefault(s => !s.Done
				&& string.Equals(s.Item, item, StringComparison.OrdinalIgnoreCase));
			if (step == null)
				continue;

			var cell = ChooseCell(state, pending, step);
			if (cell == null)
				continue;

			orders.Add(new JsonObject
			{
				["type"] = "place_building",
				["item"] = item,
				["cell"] = new JsonArray(cell[0], cell[1]),
			});
			lastIssuedTick["place:" + item] = tick;
			Note($"placed {item} at [{cell[0]},{cell[1]}] ({step.Placement})");
		}

		// 2. Advance the build plan: start the next step that is not already
		//    accounted for. A step counts as handled if it is built, in the queue,
		//    or finished and waiting for placement — missing that last case made an
		//    earlier draft re-order a building it was about to place.
		var committed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (var source in (Dictionary<string, int>[])[owned, inProduction, PendingPlacementCounts(state)])
			foreach (var (name, n) in source)
				committed[name] = committed.GetValueOrDefault(name) + n;

		BuildStep? next = null;
		foreach (var step in plan)
		{
			if (committed.GetValueOrDefault(step.Item) > 0)
			{
				committed[step.Item]--;
				step.Started = true;
				continue;
			}

			next = step;
			break;
		}

		if (next != null && !inFlight.Contains("start:" + next.Item) && !OnCooldown("start:" + next.Item)
			&& buildable.TryGetValue(next.Item, out var cost) && cash >= cost)
		{
			orders.Add(new JsonObject
			{
				["type"] = "start_production",
				["item"] = next.Item,
				["count"] = 1,
			});
			next.Started = true;
			lastIssuedTick["start:" + next.Item] = tick;
			Note($"started {next.Item} (${cost})");
		}

		// 3. Standing production rules: top each one back up to its maintain level.
		foreach (var rule in rules)
		{
			if (inFlight.Contains("start:" + rule.Item) || OnCooldown("start:" + rule.Item))
				continue;

			if (!buildable.TryGetValue(rule.Item, out var unitCost))
				continue;

			inProduction.TryGetValue(rule.Item, out var have);
			var deficit = rule.Maintain - have;
			if (deficit <= 0)
				continue;

			// Never spend below a reserve the build plan still needs.
			var affordable = (int)Math.Min(deficit, unitCost > 0 ? cash / unitCost : deficit);
			if (affordable <= 0)
				continue;

			orders.Add(new JsonObject
			{
				["type"] = "start_production",
				["item"] = rule.Item,
				["count"] = Math.Clamp(affordable, 1, 10),
			});
			cash -= (long)affordable * unitCost;
			lastIssuedTick["start:" + rule.Item] = tick;
			Note($"queued {affordable}x {rule.Item} (rule: keep {rule.Maintain})");
		}

		return orders;
	}

	/// <summary>
	/// Records an order batch that was submitted by ANY path — including the
	/// commander's own hand-issued orders — so the executor does not then build the
	/// same thing again. In the first live test the model hand-ordered a Power Plant
	/// that its own build plan already covered, and got two.
	/// </summary>
	public void NoteSubmitted(JsonArray orders, long tick)
	{
		foreach (var o in orders.OfType<JsonObject>())
		{
			var item = o["item"]?.GetValue<string>();
			if (item == null)
				continue;

			var type = o["type"]?.GetValue<string>();
			if (type == "start_production")
				lastIssuedTick["start:" + item] = tick;
			else if (type == "place_building")
				lastIssuedTick["place:" + item] = tick;
		}
	}

	/// <summary>Human-readable plan state for the agent's prompt.</summary>
	public string? Render()
	{
		if (plan.Count == 0 && rules.Count == 0)
			return null;

		var text = "";
		if (plan.Count > 0)
		{
			var parts = plan.Select(s => s.Done
				? $"[built] {s.Item}"
				: s.Started ? $"[building] {s.Item}" : $"[queued] {s.Item} ({s.Placement})");
			text += "\n\nBUILD PLAN (your standing plan; the harness builds and places these for you as cash and prerequisites allow — revise with set_build_plan):\n  "
				+ string.Join("\n  ", parts);
		}

		if (rules.Count > 0)
			text += "\n\nPRODUCTION RULES (standing orders topped up automatically every second — revise with set_production_rules):\n  "
				+ string.Join("\n  ", rules.Select(r => $"keep {r.Maintain}x {r.Item} queued"));

		return text;
	}

	/// <summary>What the executor actually did since the agent last looked.</summary>
	public string? DrainActivity()
	{
		if (activity.Count == 0)
			return null;

		var lines = activity.ToList();
		activity.Clear();
		return "\n\nYOUR PLAN EXECUTED (done automatically on your behalf since your last turn — do not re-order these):\n  "
			+ string.Join("\n  ", lines);
	}

	void Note(string line)
	{
		activity.Enqueue(line);
		while (activity.Count > 12)
			activity.Dequeue();
	}

	static Dictionary<string, long> BuildableIndex(JsonObject state)
	{
		var index = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		foreach (var q in state["production"] as JsonArray ?? [])
			foreach (var b in (q as JsonObject)?["buildable"] as JsonArray ?? [])
				if (b is JsonObject item && item["name"]?.GetValue<string>() is { } name)
					index.TryAdd(name, item["cost"]?.GetValue<long>() ?? 0);

		return index;
	}

	static Dictionary<string, int> OwnedBuildingNames(JsonObject state)
	{
		var owned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (var b in state["buildings"] as JsonArray ?? [])
			if ((b as JsonObject)?["name"]?.GetValue<string>() is { } name)
				owned[name] = owned.GetValueOrDefault(name) + 1;

		return owned;
	}

	static Dictionary<string, int> PendingPlacementCounts(JsonObject state)
	{
		var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (var p in state["pendingPlacement"] as JsonArray ?? [])
			if ((p as JsonObject)?["item"]?.GetValue<string>() is { } name)
				counts[name] = counts.GetValueOrDefault(name) + 1;

		return counts;
	}

	/// <summary>How many of each item are currently building or waiting, across all queues.</summary>
	static Dictionary<string, int> InProductionCounts(JsonObject state)
	{
		var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (var q in state["production"] as JsonArray ?? [])
		{
			if (q is not JsonObject queue)
				continue;

			if (queue["current"] is JsonObject current && current["name"]?.GetValue<string>() is { } name)
				counts[name] = counts.GetValueOrDefault(name) + 1;

			// "queued" entries are plain strings, not objects (2026-08-23 regression).
			foreach (var w in queue["queued"] as JsonArray ?? [])
				if (w is JsonValue v && v.TryGetValue<string>(out var waiting))
					counts[waiting] = counts.GetValueOrDefault(waiting) + 1;
		}

		return counts;
	}

	/// <summary>Applies the step's placement policy to the engine's valid-cell sample.</summary>
	static int[]? ChooseCell(JsonObject state, JsonObject pending, BuildStep step)
	{
		var valid = new List<int[]>();
		foreach (var c in pending["validCellsSample"] as JsonArray ?? [])
			if (c is JsonArray { Count: >= 2 } cell)
				valid.Add([cell[0]?.GetValue<int>() ?? 0, cell[1]?.GetValue<int>() ?? 0]);

		if (valid.Count == 0)
			return null;

		if (step.Cell != null)
			return Nearest(valid, step.Cell);

		var home = CellOf(state["map"]?["yourSpawnCell"]) ?? valid[0];

		switch (step.Placement)
		{
			case "near_tiberium":
			{
				var fields = (state["exploredResources"] as JsonArray ?? [])
					.OfType<JsonObject>()
					.Select(f => CellOf(f["cell"]))
					.Where(c => c != null)
					.ToList();

				// Prefer the field closest to home, then hug it.
				var target = fields.Count > 0
					? fields.OrderBy(f => Dist2(f!, home)).First()!
					: home;
				return Nearest(valid, target);
			}

			case "toward_enemy":
			{
				var enemy = NearestEnemySpawn(state, home);
				return enemy == null ? Nearest(valid, home) : Nearest(valid, enemy);
			}

			case "back_of_base":
			{
				var enemy = NearestEnemySpawn(state, home);
				if (enemy == null)
					return Nearest(valid, home);

				// Farthest from the enemy while still close to home.
				return valid.OrderByDescending(c => Dist2(c, enemy)).ThenBy(c => Dist2(c, home)).First();
			}

			default:
				return Nearest(valid, home);
		}
	}

	static int[]? NearestEnemySpawn(JsonObject state, int[] home)
	{
		var spawns = (state["enemySpawns"] as JsonArray ?? [])
			.OfType<JsonObject>()
			.Select(s => CellOf(s["cell"]))
			.Where(c => c != null)
			.ToList();

		return spawns.Count == 0 ? null : spawns.OrderBy(s => Dist2(s!, home)).First();
	}

	static int[] Nearest(List<int[]> cells, int[] target) =>
		cells.OrderBy(c => Dist2(c, target)).First();

	static long Dist2(int[] a, int[] b)
	{
		long dx = a[0] - b[0];
		long dy = a[1] - b[1];
		return dx * dx + dy * dy;
	}

	static int[]? CellOf(JsonNode? node) =>
		node is JsonArray { Count: >= 2 } a ? [a[0]?.GetValue<int>() ?? 0, a[1]?.GetValue<int>() ?? 0] : null;
}
