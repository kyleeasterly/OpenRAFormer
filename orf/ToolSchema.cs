using System.Text.Json.Nodes;

namespace Orf;

/// <summary>The agent's tool surface. Order parameters mirror PROTOCOL.md's inbox order array exactly.</summary>
public static class ToolSchema
{
	const string ToolsJson =
		"""
		[
		  {
		    "type": "function",
		    "function": {
		      "name": "issue_orders",
		      "description": "Issue a batch of game orders for this turn. Include every order you want executed; orders are validated individually and invalid ones are rejected without affecting the rest. Orders you issue here take effect once, immediately — for anything that should keep happening between your turns, use set_build_plan and set_production_rules instead.",
		      "parameters": {
		        "type": "object",
		        "properties": {
		          "intent": { "type": "string", "description": "REQUIRED. One sentence: what you are trying to achieve with this batch. You will be shown your recent intents on later turns as your own memory of the plan, so write it for your future self." },
		          "orders": {
		            "type": "array",
		            "items": {
		              "type": "object",
		              "properties": {
		                "type": {
		                  "type": "string",
		                  "enum": ["start_production", "cancel_production", "place_building", "resign", "deploy", "move", "attack_move", "attack", "capture", "guard", "stop", "sell", "repair", "set_rally", "enter", "unload", "harvest", "set_stance", "scatter"],
		                  "description": "The order type."
		                },
		                "item": { "type": "string", "description": "Exact unit or structure name from your buildable list, e.g. 'Power Plant', 'Minigunner', 'Tiberium Refinery' (start_production, cancel_production, place_building)." },
		                "count": { "type": "integer", "description": "How many to train/cancel, 1-10 (start_production, cancel_production). 5 is the shift-click batch a human uses when massing an army. Ordering units one at a time wastes turns — batch unless cash-limited." },
		                "actorIds": { "type": "array", "items": { "type": "integer" }, "description": "Ids of your actors (deploy, move, attack_move, attack, capture, guard, stop, enter, harvest, set_stance, scatter). Prefer 'group' for anything you command repeatedly." },
		                "group": { "type": "string", "description": "Name of a group you created with set_group, used INSTEAD of actorIds. The harness expands it to the members that are still alive, so a group never goes stale between turns." },
		                "actorId": { "type": "integer", "description": "Single actor id (sell, repair, set_rally, unload)." },
		                "targetActorId": { "type": "integer", "description": "Target actor id, must be visible to you (attack, guard, capture, enter — for enter this is the transport to board)." },
		                "cell": { "type": "array", "items": { "type": "integer" }, "minItems": 2, "maxItems": 2, "description": "[x, y] map cell (move, attack_move, set_rally, place_building, harvest, unload). For place_building it must be a '+' cell from the pendingPlacement grid." },
		                "stance": { "type": "string", "enum": ["hold_fire", "return_fire", "defend", "attack_anything"], "description": "Unit stance (set_stance)." },
		                "queued": { "type": "boolean", "description": "Queue after the actor's current activity instead of replacing it (move, attack_move). Chain several queued moves to steer a column along a route instead of letting it path straight through defences." }
		              },
		              "required": ["type"]
		            }
		          }
		        },
		        "required": ["intent", "orders"]
		      }
		    }
		  },
		  {
		    "type": "function",
		    "function": {
		      "name": "set_build_plan",
		      "description": "Declare the ordered list of STRUCTURES you want built. The harness starts and places each one for you automatically, as soon as cash and prerequisites allow, without waiting for your next turn — this is the only way to build at human speed, because the game runs 8-40 seconds between your decisions. Set this on your very first turn and revise it whenever your strategy changes. Replaces the previous plan entirely.",
		      "parameters": {
		        "type": "object",
		        "properties": {
		          "steps": {
		            "type": "array",
		            "items": {
		              "type": "object",
		              "properties": {
		                "item": { "type": "string", "description": "Structure name, e.g. 'Power Plant', 'Tiberium Refinery', 'Barracks', 'Weapons Factory', 'Communications Center'." },
		                "placement": { "type": "string", "enum": ["near_tiberium", "toward_enemy", "back_of_base", "near_base"], "description": "Where to put it. Refineries near_tiberium (short harvester trips), defences toward_enemy, power back_of_base, everything else near_base." },
		                "cell": { "type": "array", "items": { "type": "integer" }, "minItems": 2, "maxItems": 2, "description": "Optional explicit [x, y] instead of a placement policy; the nearest legal cell to it is used." }
		              },
		              "required": ["item"]
		            }
		          }
		        },
		        "required": ["steps"]
		      }
		    }
		  },
		  {
		    "type": "function",
		    "function": {
		      "name": "set_production_rules",
		      "description": "Standing orders for UNITS. The harness keeps the given number of each item queued at all times, topping them up every second. This is how you say 'Rocket Soldiers forever' once instead of re-ordering every turn, and it is why your queues should never sit idle. Replaces the previous rules entirely; pass an empty list to stop all standing production.",
		      "parameters": {
		        "type": "object",
		        "properties": {
		          "rules": {
		            "type": "array",
		            "items": {
		              "type": "object",
		              "properties": {
		                "item": { "type": "string", "description": "Unit name, e.g. 'Rocket Soldier', 'Medium Tank', 'Minigunner'." },
		                "maintain": { "type": "integer", "description": "How many of this item to keep queued at all times (1-10)." }
		              },
		              "required": ["item", "maintain"]
		            }
		          }
		        },
		        "required": ["rules"]
		      }
		    }
		  },
		  {
		    "type": "function",
		    "function": {
		      "name": "set_group",
		      "description": "Name a set of units so you can command them as one for the rest of the match. The harness prunes members as they die, so 'group' never goes stale the way a hand-copied id list does. Use it for your main army ('ball'), your scouts, and your home guard — then order them with {\"type\":\"attack_move\",\"group\":\"ball\",\"cell\":[42,7]}.",
		      "parameters": {
		        "type": "object",
		        "properties": {
		          "name": { "type": "string", "description": "Short group name, e.g. 'ball', 'scouts', 'guard'." },
		          "actorIds": { "type": "array", "items": { "type": "integer" }, "description": "The unit ids to put in the group (replaces any existing membership). Pass an empty list to delete the group." }
		        },
		        "required": ["name", "actorIds"]
		      }
		    }
		  },
		  {
		    "type": "function",
		    "function": {
		      "name": "remember",
		      "description": "Append a VERIFIED fact to your permanent notepad, shown to you every turn for the rest of the match. Use it the moment you learn something the hard way — an order that was rejected and why, a prerequisite you discovered, where the enemy's base is, what their army is made of. This is how you stop re-deriving the same wrong conclusion every turn. Facts are permanent and cannot be edited, so only write things you have actually confirmed.",
		      "parameters": {
		        "type": "object",
		        "properties": {
		          "fact": { "type": "string", "description": "One concise sentence, e.g. 'Medium Tank requires a Communications Center - confirmed by rejection at 4:33'." }
		        },
		        "required": ["fact"]
		      }
		    }
		  },
		  {
		    "type": "function",
		    "function": {
		      "name": "set_goals",
		      "description": "Replace your list of short strategic goals. These persist and are shown back to you every turn — use them to stay on plan instead of re-planning from scratch. Keep 1-5 concise goals.",
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
