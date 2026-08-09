using System.Text.Json.Nodes;

namespace Orf;

/// <summary>
/// Deterministic scripted policy for provider "test". No network. Reads the fog-scoped
/// state JSON and emits the same order JSON shape as real agents.
/// </summary>
public static class TestAgent
{
	static readonly string[] BarracksTypes = ["Hand of Nod", "Barracks"];

	public static (JsonArray Orders, string Summary) Decide(JsonObject state)
	{
		var orders = new JsonArray();
		var actions = new List<string>();

		var buildings = state["buildings"] as JsonArray ?? [];
		var units = state["units"] as JsonArray ?? [];
		var production = state["production"] as JsonArray ?? [];
		var cash = state["you"]?["cash"]?.GetValue<long>() ?? 0;

		// 1. Work toward base goals when the Building queue is idle. Queue types are
		// faction-suffixed (e.g. "Building.GDI"), and buildable items differ per faction,
		// so goals are chosen from what is actually buildable.
		var buildingQueue = production.OfType<JsonObject>().FirstOrDefault(q =>
			q["queue"]?.GetValue<string>()?.StartsWith("Building", StringComparison.Ordinal) == true);
		if (buildingQueue != null && buildingQueue["current"] is null or not JsonObject)
		{
			var have = buildings.OfType<JsonObject>()
				.Select(b => b["name"]?.GetValue<string>())
				.OfType<string>()
				.ToList();

			foreach (var q in buildingQueue["queued"] as JsonArray ?? [])
				if (q?.GetValue<string>() is string queued)
					have.Add(queued);

			var buildable = (buildingQueue["buildable"] as JsonArray ?? [])
				.OfType<JsonObject>()
				.Select(b => b["name"]?.GetValue<string>())
				.OfType<string>()
				.ToHashSet();

			var next = NextGoal(have, buildable);
			if (next != null)
			{
				orders.Add(new JsonObject { ["type"] = "start_production", ["item"] = next, ["count"] = 1 });
				actions.Add($"build {next}");
			}
		}

		// 2. Train infantry while the army is small.
		var hasBarracks = buildings.OfType<JsonObject>().Any(b => BarracksTypes.Contains(b["name"]?.GetValue<string>()));
		if (hasBarracks && cash > 300 && units.Count < 12)
		{
			orders.Add(new JsonObject { ["type"] = "start_production", ["item"] = "Minigunner", ["count"] = 5 });
			actions.Add("train Minigunner x5");
		}

		// 3. Attack-move with the idle army once it is big enough.
		var idle = units.OfType<JsonObject>().Where(u => u["idle"]?.GetValue<bool>() == true).ToList();
		if (idle.Count >= 8)
		{
			var target = PickTarget(state, idle);
			var ids = new JsonArray();
			foreach (var u in idle)
				ids.Add(u["id"]?.GetValue<long>() ?? 0);

			orders.Add(new JsonObject { ["type"] = "attack_move", ["actorIds"] = ids, ["cell"] = new JsonArray(target.X, target.Y), ["queued"] = false });
			actions.Add($"attack_move {idle.Count} units -> [{target.X},{target.Y}]");
		}

		return (orders, actions.Count > 0 ? string.Join("; ", actions) : "pass (nothing to do)");
	}

	/// <summary>Next base goal not yet satisfied by existing or queued buildings, chosen from buildable items.</summary>
	static string? NextGoal(List<string> have, HashSet<string> buildable)
	{
		if (!have.Contains("Power Plant") && buildable.Contains("Power Plant"))
			return "Power Plant";

		if (!have.Any(BarracksTypes.Contains))
		{
			var barracks = BarracksTypes.FirstOrDefault(buildable.Contains);
			if (barracks != null)
				return barracks;
		}

		if (!have.Contains("Tiberium Refinery") && buildable.Contains("Tiberium Refinery"))
			return "Tiberium Refinery";

		if (have.Count(t => t == "Power Plant") < 2 && buildable.Contains("Power Plant"))
			return "Power Plant";

		return null;
	}

	static (int X, int Y) PickTarget(JsonObject state, List<JsonObject> idleUnits)
	{
		// Reference point: centroid of the idle army (fallback: own spawn).
		double cx = 0, cy = 0;
		var n = 0;
		foreach (var u in idleUnits)
			if (ReadCell(u["cell"]) is var (x, y, ok) && ok)
			{
				cx += x;
				cy += y;
				n++;
			}

		if (n > 0)
		{
			cx /= n;
			cy /= n;
		}
		else if (ReadCell(state["map"]?["yourSpawnCell"]) is var (sx, sy, sok) && sok)
		{
			cx = sx;
			cy = sy;
		}

		var candidates = CellsOf(state["lastKnownEnemyBuildings"]);
		if (candidates.Count == 0)
			candidates = CellsOf(state["visibleEnemies"]);

		if (candidates.Count == 0)
		{
			var w = state["map"]?["width"]?.GetValue<int>() ?? 128;
			var h = state["map"]?["height"]?.GetValue<int>() ?? 128;
			return (w / 2, h / 2);
		}

		return candidates.MinBy(c => (c.X - cx) * (c.X - cx) + (c.Y - cy) * (c.Y - cy));
	}

	static List<(int X, int Y)> CellsOf(JsonNode? entries)
	{
		var cells = new List<(int, int)>();
		foreach (var e in entries as JsonArray ?? [])
			if (ReadCell(e?["cell"]) is var (x, y, ok) && ok)
				cells.Add((x, y));
		return cells;
	}

	static (int X, int Y, bool Ok) ReadCell(JsonNode? cell)
	{
		if (cell is JsonArray { Count: >= 2 } a)
			return (a[0]!.GetValue<int>(), a[1]!.GetValue<int>(), true);
		return (0, 0, false);
	}
}
