using System.Text.Json.Nodes;

namespace Orf;

/// <summary>
/// Deterministic opening: no LLM, no latency — Power Plant -> Refinery -> Barracks,
/// each queued the second the Building queue frees up and placed the second it is
/// ready, at human shift-click speed. Runs as the swarm's bootstrap thread; the
/// coordinator hands off to the LLM specialists once the Barracks stands.
/// </summary>
public static class BootstrapScript
{
	static readonly string[] BarracksTypes = ["Barracks", "Hand of Nod"];

	public static (JsonArray Orders, string Summary) Decide(JsonObject state)
	{
		var orders = new JsonArray();
		var actions = new List<string>();

		var buildings = state["buildings"] as JsonArray ?? [];
		var production = state["production"] as JsonArray ?? [];
		var spawn = ReadCell(state["map"]?["yourSpawnCell"]) ?? (64, 64);

		// 1. Place anything that is ready, immediately, with a sane site per type.
		foreach (var p in (state["pendingPlacement"] as JsonArray ?? []).OfType<JsonObject>())
		{
			var item = p["item"]?.GetValue<string>() ?? "";
			var cells = (p["validCellsSample"] as JsonArray ?? [])
				.Select(ReadCell)
				.OfType<(int X, int Y)>()
				.ToList();
			if (cells.Count == 0)
				continue;

			(int X, int Y) anchor;
			if (item == "Tiberium Refinery")
			{
				// Closest valid cell to the nearest scouted tiberium block.
				anchor = NearestResource(state, spawn) ?? spawn;
			}
			else if (BarracksTypes.Contains(item))
			{
				// Toward the map center: open exit facing the front.
				var w = state["map"]?["width"]?.GetValue<int>() ?? 128;
				var h = state["map"]?["height"]?.GetValue<int>() ?? 128;
				anchor = ((spawn.Item1 + w / 2) / 2, (spawn.Item2 + h / 2) / 2);
			}
			else
			{
				// Power plants tuck in near the spawn.
				anchor = spawn;
			}

			var best = cells.MinBy(c => (c.X - anchor.Item1) * (c.X - anchor.Item1) + (c.Y - anchor.Item2) * (c.Y - anchor.Item2));
			orders.Add(new JsonObject
			{
				["type"] = "place_building",
				["item"] = item,
				["cell"] = new JsonArray(best.X, best.Y),
			});
			actions.Add($"place {item} at [{best.X},{best.Y}]");
		}

		// 2. Shift-queue the whole opening sequence at once (the human move: Power,
		// Refinery, Barracks back-to-back in the Building queue's waiting list) —
		// anything already standing, queued, current, or awaiting placement is skipped.
		var buildingQueue = production.OfType<JsonObject>().FirstOrDefault(q =>
			q["queue"]?.GetValue<string>()?.StartsWith("Building", StringComparison.Ordinal) == true);
		if (buildingQueue != null)
		{
			var have = buildings.OfType<JsonObject>()
				.Select(b => b["name"]?.GetValue<string>())
				.OfType<string>()
				.ToList();

			if (buildingQueue["current"]?["name"]?.GetValue<string>() is { } current)
				have.Add(current);
			foreach (var q in buildingQueue["queued"] as JsonArray ?? [])
				if (q is JsonObject qo && qo["name"]?.GetValue<string>() is { } queued)
					have.Add(queued);
				else if (q?.GetValue<string>() is { } s)
					have.Add(s);

			// Pending placements count as had — don't re-order what's waiting to land.
			foreach (var p in (state["pendingPlacement"] as JsonArray ?? []).OfType<JsonObject>())
				if (p["item"]?.GetValue<string>() is { } pending)
					have.Add(pending);

			var buildable = (buildingQueue["buildable"] as JsonArray ?? [])
				.OfType<JsonObject>()
				.Select(b => b["name"]?.GetValue<string>())
				.OfType<string>()
				.ToHashSet();

			foreach (var next in MissingSequence(have, buildable))
			{
				have.Add(next);
				orders.Add(new JsonObject { ["type"] = "start_production", ["item"] = next, ["count"] = 1 });
				actions.Add($"queue {next}");
			}
		}

		var done = Complete(state);
		return (orders, actions.Count > 0
			? string.Join("; ", actions)
			: done ? "opening complete — ready for handoff" : "waiting on build/cash");
	}

	/// <summary>The handoff condition the coordinator also uses: the Barracks stands.</summary>
	public static bool Complete(JsonObject state) =>
		(state["buildings"] as JsonArray ?? []).OfType<JsonObject>()
			.Any(b => BarracksTypes.Contains(b["name"]?.GetValue<string>()));

	/// <summary>Every sequence item not yet owned/queued that is buildable right now
	/// (prerequisites gate the buildable list, so later items join as earlier ones land).</summary>
	static IEnumerable<string> MissingSequence(List<string> have, HashSet<string> buildable)
	{
		if (!have.Contains("Power Plant") && buildable.Contains("Power Plant"))
			yield return "Power Plant";

		if (!have.Contains("Tiberium Refinery") && buildable.Contains("Tiberium Refinery"))
			yield return "Tiberium Refinery";

		if (!have.Any(BarracksTypes.Contains) && BarracksTypes.FirstOrDefault(buildable.Contains) is { } barracks)
			yield return barracks;
	}

	static (int, int)? NearestResource(JsonObject state, (int X, int Y) from)
	{
		(int, int)? best = null;
		var bestD = long.MaxValue;
		foreach (var r in (state["exploredResources"] as JsonArray ?? []).OfType<JsonObject>())
		{
			if (ReadCell(r["cell"]) is not { } c)
				continue;

			long d = (long)(c.Item1 - from.X) * (c.Item1 - from.X) + (long)(c.Item2 - from.Y) * (c.Item2 - from.Y);
			if (d < bestD)
			{
				bestD = d;
				best = c;
			}
		}

		return best;
	}

	static (int, int)? ReadCell(JsonNode? cell) =>
		cell is JsonArray { Count: >= 2 } a ? (a[0]!.GetValue<int>(), a[1]!.GetValue<int>()) : null;
}
