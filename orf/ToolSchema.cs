using System.Text.Json.Nodes;

namespace Orf;

/// <summary>The single issue_orders tool. Parameters mirror PROTOCOL.md's inbox order array exactly.</summary>
public static class ToolSchema
{
	const string ToolsJson =
		"""
		[
		  {
		    "type": "function",
		    "function": {
		      "name": "issue_orders",
		      "description": "Issue a batch of game orders for this turn. Include every order you want executed; orders are validated individually and invalid ones are rejected without affecting the rest.",
		      "parameters": {
		        "type": "object",
		        "properties": {
		          "orders": {
		            "type": "array",
		            "items": {
		              "type": "object",
		              "properties": {
		                "type": {
		                  "type": "string",
		                  "enum": ["start_production", "cancel_production", "place_building", "resign", "deploy", "move", "attack_move", "attack", "capture", "guard", "stop", "sell", "repair", "set_rally"],
		                  "description": "The order type."
		                },
		                "item": { "type": "string", "description": "Exact unit or structure name from your buildable list, e.g. 'Power Plant', 'Minigunner', 'Tiberium Refinery' (start_production, cancel_production, place_building)." },
		                "count": { "type": "integer", "description": "How many to train/cancel, 1-10 (start_production, cancel_production). 1 is a single click; 5 is the shift-click batch a human uses when massing an army. Ordering units one at a time wastes turns — batch unless cash-limited." },
		                "actorIds": { "type": "array", "items": { "type": "integer" }, "description": "Ids of your actors (deploy, move, attack_move, attack, capture, guard, stop). capture requires engineers; the target enemy building becomes yours, and a captured Construction Yard lets you build that faction's structures." },
		                "actorId": { "type": "integer", "description": "Single actor id (sell, repair, set_rally)." },
		                "targetActorId": { "type": "integer", "description": "Target actor id, must be visible to you (attack, guard)." },
		                "cell": { "type": "array", "items": { "type": "integer" }, "minItems": 2, "maxItems": 2, "description": "[x, y] map cell (move, attack_move, set_rally, place_building). For place_building it must be a '+' cell from the pendingPlacement grid." },
		                "queued": { "type": "boolean", "description": "Queue after the actor's current activity instead of replacing it (move, attack_move). Default false." }
		              },
		              "required": ["type"]
		            }
		          }
		        },
		        "required": ["orders"]
		      }
		    }
		  },
		  {
		    "type": "function",
		    "function": {
		      "name": "set_goals",
		      "description": "Replace your list of long-term strategic goals. These persist and are shown back to you every turn — use them to stay on plan instead of reacting turn by turn. Keep 1-5 concise goals.",
		      "parameters": {
		        "type": "object",
		        "properties": {
		          "goals": { "type": "array", "items": { "type": "string" }, "description": "Your new complete goal list (replaces the old one)." }
		        },
		        "required": ["goals"]
		      }
		    }
		  },
		  {
		    "type": "function",
		    "function": {
		      "name": "complete_checklist_item",
		      "description": "Mark an enemy spawn on your elimination checklist as cleared. Do this only after you have scouted that spawn area and confirmed the base there is destroyed (or was never built).",
		      "parameters": {
		        "type": "object",
		        "properties": {
		          "cell": { "type": "array", "items": { "type": "integer" }, "minItems": 2, "maxItems": 2, "description": "The checklist spawn cell [x, y] to mark cleared." }
		        },
		        "required": ["cell"]
		      }
		    }
		  }
		]
		""";

	const string SwarmToolsJson =
		"""
		[
		  {
		    "type": "function",
		    "function": {
		      "name": "post_request",
		      "description": "Post a structured request to a teammate specialist (the demand system). Use this when you NEED something outside your own authority: the army needs rocket soldiers -> ask produce; econ needs a refinery placed near new tiberium -> ask build. Requests appear in the target's prompt until resolved. Don't spam duplicates — check OPEN REQUESTS first.",
		      "parameters": {
		        "type": "object",
		        "properties": {
		          "to": { "type": "string", "description": "Target role name (e.g. build, produce, army, econ) or 'any'." },
		          "need": { "type": "string", "description": "One line: what you need, concretely (e.g. '6 Rocket Soldiers to staging [80,50]')." },
		          "why": { "type": "string", "description": "One line of context so the target can prioritize (e.g. 'enemy tank push forming west')." },
		          "priority": { "type": "string", "enum": ["low", "normal", "urgent"], "description": "How urgent this is. Default normal." }
		        },
		        "required": ["to", "need"]
		      }
		    }
		  },
		  {
		    "type": "function",
		    "function": {
		      "name": "resolve_request",
		      "description": "Mark a request addressed to you as done (or decline it with a reason). Do this the turn you fulfil it so the requester stops waiting.",
		      "parameters": {
		        "type": "object",
		        "properties": {
		          "id": { "type": "string", "description": "The request id shown in OPEN REQUESTS FOR YOU." },
		          "note": { "type": "string", "description": "Short outcome note (e.g. '5 rockets queued, rally set' or 'declined: no cash')." }
		        },
		        "required": ["id"]
		      }
		    }
		  }
		]
		""";

	public static JsonArray Tools(bool swarm = false)
	{
		var tools = (JsonArray)JsonNode.Parse(ToolsJson)!;
		if (swarm)
			foreach (var extra in (JsonArray)JsonNode.Parse(SwarmToolsJson)!)
				tools.Add(extra!.DeepClone());

		return tools;
	}
}
