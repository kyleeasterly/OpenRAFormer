using System.Text;
using System.Text.Json.Nodes;

namespace Orf;

/// <summary>
/// Renders the engine state JSON as Markdown tables for the stateFormat: markdown
/// experiment arm. Same information, same section names and field vocabulary as the
/// JSON representation — only the syntax differs, so prompt docs apply to both.
/// </summary>
public static class StateMarkdown
{
	public static string Render(JsonObject state)
	{
		var sb = new StringBuilder();
		var handled = new HashSet<string>
		{
			"tick", "second", "you", "buildCapacity", "underAttack", "map", "enemySpawns",
			"production", "pendingPlacement", "buildings", "units", "visibleEnemies",
			"lastKnownEnemyBuildings", "exploredResources",
		};

		sb.AppendLine($"tick: {state["tick"]} | second: {state["second"]}");

		if (state["you"] is JsonObject you)
		{
			sb.AppendLine();
			sb.AppendLine("## you");
			foreach (var (key, value) in you)
				sb.AppendLine($"- {key}: {Scalar(value)}");
		}

		if (state["buildCapacity"] is JsonObject cap)
		{
			sb.AppendLine();
			sb.AppendLine($"## buildCapacity");
			sb.AppendLine($"queues: {cap["queues"]} | busy: {cap["busy"]} | idle: {cap["idle"]}");
		}

		if (state["map"] is JsonObject map)
		{
			sb.AppendLine();
			sb.AppendLine("## map");
			sb.AppendLine($"width: {map["width"]} | height: {map["height"]} | yourSpawnCell: {Cell(map["yourSpawnCell"])}");
			sb.AppendLine("All positions are x,y cells on this grid.");
		}

		Table(sb, state, "underAttack", ["yourUnit", "yourUnitId", "attacker", "attackerId", "attackerOwner", "attackerCell"],
			["Your unit", "ID", "Attacker", "Attacker ID", "Owner", "From"]);

		Table(sb, state, "enemySpawns", ["cell", "explored"], ["Cell", "Explored"]);

		if (state["production"] is JsonArray production && production.Count > 0)
		{
			sb.AppendLine();
			sb.AppendLine("## production");
			sb.AppendLine("|Queue|Busy|Producing|Queued|Buildable (cost)|");
			sb.AppendLine("|---|---|---|---|---|");
			foreach (var q in production.OfType<JsonObject>())
			{
				var current = q["current"] is JsonObject c
					? $"{c["name"]} {c["progressPercent"]}%{(True(c["paused"]) ? " PAUSED" : "")}{(True(c["ready"]) ? " READY" : "")}"
					: "";
				var queued = Join(q["queued"], n => Scalar(n));
				var buildable = Join(q["buildable"], n => n is JsonObject b ? $"{b["name"]} ${b["cost"]}" : Scalar(n));
				sb.AppendLine($"|{q["queue"]}|{(True(q["busy"]) ? "yes" : "no")}|{current}|{queued}|{buildable}|");
			}

			// Locked items with their missing prerequisites. Agents have lost whole
			// matches ordering a unit 21 times without ever learning what unlocks it.
			var locked = production.OfType<JsonObject>()
				.SelectMany(q => (q["locked"] as JsonArray ?? []).OfType<JsonObject>())
				.ToList();

			if (locked.Count > 0)
			{
				sb.AppendLine();
				sb.AppendLine("## locked — NOT buildable yet, and exactly what each one needs first");
				sb.AppendLine("|Item|Cost|Requires|Blocked by|");
				sb.AppendLine("|---|---|---|---|");
				foreach (var l in locked)
					sb.AppendLine($"|{l["name"]}|${l["cost"]}|{Join(l["requires"], Scalar)}|{Join(l["blockedBy"], Scalar)}|");
			}
		}

		if (state["pendingPlacement"] is JsonArray pending && pending.Count > 0)
		{
			sb.AppendLine();
			sb.AppendLine("## pendingPlacement — finished buildings waiting for YOUR placement decision");
			foreach (var p in pending.OfType<JsonObject>())
			{
				var origin = p["gridOrigin"] as JsonArray;
				sb.AppendLine($"**{p["item"]}** is ready. Issue `place_building` with a '+' cell, or it will be auto-placed for you after a grace period.");
				sb.AppendLine($"Legend: {p["legend"]}");
				sb.AppendLine($"Grid top-left corner is cell {Cell(origin)}; each row is one y step down, each character one x step right.");
				sb.AppendLine("```");
				var y = origin?[1]?.GetValue<int>() ?? 0;
				foreach (var row in p["grid"] as JsonArray ?? [])
					sb.AppendLine($"y={y++,3} {row}");
				sb.AppendLine("```");
				sb.AppendLine($"Some valid cells: {Join(p["validCellsSample"], Scalar)}");
			}
		}

		Table(sb, state, "buildings", ["id", "name", "cell", "hpPercent", "rally"],
			["ID", "Name", "Position", "HP %", "Rally"]);

		if (state["units"] is JsonArray units && units.Count > 0)
		{
			sb.AppendLine();
			sb.AppendLine("## units");
			sb.AppendLine("|ID|Name|Position|HP %|Activity|");
			sb.AppendLine("|---|---|---|---|---|");
			foreach (var u in units.OfType<JsonObject>())
			{
				var activity = True(u["idle"])
					? "idle"
					: (u["activity"]?.GetValue<string>() ?? "busy")
						+ (u["destination"] is JsonArray d ? $" destination: {Cell(d)}" : "");
				sb.AppendLine($"|{u["id"]}|{u["name"]}|{Cell(u["cell"])}|{u["hpPercent"]}|{activity}|");
			}
		}

		Table(sb, state, "visibleEnemies", ["id", "name", "owner", "cell", "hpPercent", "isBuilding"],
			["ID", "Name", "Owner", "Position", "HP %", "Building?"]);

		Table(sb, state, "lastKnownEnemyBuildings", ["name", "owner", "cell"],
			["Name", "Owner", "Position"]);

		Table(sb, state, "exploredResources", ["cell", "cells"], ["Position", "Size (cells)"]);

		// Anything the engine adds later that this renderer doesn't know yet must not
		// be silently dropped — emit it as compact JSON under its own heading.
		foreach (var (key, value) in state)
		{
			if (handled.Contains(key) || value == null)
				continue;
			sb.AppendLine();
			sb.AppendLine($"## {key}");
			sb.AppendLine(value.ToJsonString());
		}

		return sb.ToString();
	}

	public static string RenderResults(List<JsonObject> results)
	{
		var sb = new StringBuilder();
		sb.AppendLine("|Turn seq|Tick|Order #|Status|");
		sb.AppendLine("|---|---|---|---|");
		foreach (var r in results)
			foreach (var item in r["results"] as JsonArray ?? [])
				if (item is JsonObject o)
				{
					var status = Scalar(o["status"]);
					if (o["reason"] != null)
						status += $" — {Scalar(o["reason"])}";
					sb.AppendLine($"|{r["seq"]}|{r["tick"]}|{o["index"]}|{status}|");
				}

		return sb.ToString();
	}

	static void Table(StringBuilder sb, JsonObject state, string key, string[] fields, string[] headers)
	{
		if (state[key] is not JsonArray rows || rows.Count == 0)
			return;

		sb.AppendLine();
		sb.AppendLine($"## {key}");
		sb.AppendLine($"|{string.Join("|", headers)}|");
		sb.AppendLine($"|{string.Join("|", headers.Select(_ => "---"))}|");
		foreach (var row in rows.OfType<JsonObject>())
			sb.AppendLine($"|{string.Join("|", fields.Select(f => Scalar(row[f])))}|");
	}

	static string Scalar(JsonNode? node)
	{
		return node switch
		{
			null => "",
			JsonArray { Count: 2 } cell when cell[0] is JsonValue && cell[1] is JsonValue => Cell(cell),
			JsonValue v when v.TryGetValue<bool>(out var b) => b ? "yes" : "no",
			JsonValue v => v.ToString(),
			_ => node.ToJsonString(),
		};
	}

	static string Cell(JsonNode? node)
	{
		return node is JsonArray { Count: 2 } a ? $"{a[0]},{a[1]}" : "";
	}

	static string Join(JsonNode? array, Func<JsonNode?, string> render)
	{
		return array is JsonArray items && items.Count > 0
			? string.Join("; ", items.Select(render))
			: "";
	}

	static bool True(JsonNode? node)
	{
		return node is JsonValue v && v.TryGetValue<bool>(out var b) && b;
	}
}
