using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Orders
{
	public class ExternalOrderParser
	{
		readonly World world;

		public ExternalOrderParser(World world)
		{
			this.world = world;
		}

		public Order[] ParseOrder(string orderText)
		{
			try
			{
				// Trim leading/trailing whitespace from the entire order line
				orderText = orderText?.Trim();
				if (string.IsNullOrWhiteSpace(orderText))
					return null;

				// Format: "Player1: OrderType (Params)"
				// Examples:
				// "Player1: Move (Units:Tank,Infantry Target:50,75)"
				// "Player1: Attack (Units:Tank#3 Target:EnemyBuilding@100,200)"
				// "Player1: StartProduction (Building:Barracks Item:Infantry Count:5)"

				var colonIndex = orderText.IndexOf(':');
				if (colonIndex < 0)
					return null;

				var playerName = orderText.Substring(0, colonIndex).Trim();
				var player = FindPlayer(playerName);
				if (player == null)
				{
					Log.Write("debug", $"Player not found: {playerName}");
					return null;
				}

				var remainder = orderText.Substring(colonIndex + 1).Trim();
				var parenIndex = remainder.IndexOf('(');
				if (parenIndex < 0)
				{
					// No parameters, simple order
					return ParseSimpleOrder(remainder.Trim(), player);
				}

				var orderType = remainder.Substring(0, parenIndex).Trim();
				var parenEnd = remainder.LastIndexOf(')');
				if (parenEnd < parenIndex)
					return null;

				var paramsText = remainder.Substring(parenIndex + 1, parenEnd - parenIndex - 1);
				var parameters = ParseParameters(paramsText);

				return ParseComplexOrder(orderType, parameters, player);
			}
			catch (Exception e)
			{
				Log.Write("debug", $"Failed to parse order '{orderText}': {e.Message}");
				return null;
			}
		}

		Player FindPlayer(string playerName)
		{
			// Handle various player name formats
			playerName = playerName.Trim();
			
			// Direct name match
			var player = world.Players.FirstOrDefault(p => 
				string.Equals(p.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));
			
			if (player != null)
				return player;

			// Try "Player1", "Player2" etc.
			if (playerName.StartsWith("Player", StringComparison.OrdinalIgnoreCase))
			{
				var numberPart = playerName.Substring(6);
				if (int.TryParse(numberPart, out var playerIndex))
				{
					// Find by index (1-based for user convenience)
					var players = world.Players.Where(p => p.Playable && !p.NonCombatant).ToArray();
					if (playerIndex > 0 && playerIndex <= players.Length)
						return players[playerIndex - 1];
				}
			}

			return null;
		}

		Dictionary<string, string> ParseParameters(string paramsText)
		{
			var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			
			// Handle multiple formats:
			// 1. Quoted values: Item:"Power Plant" or Item:'Power Plant'
			// 2. Non-quoted values that end at next param: Item:PowerPlant Count:1
			// 3. Values with spaces: Item:Power Plant Count:1
			
			// Updated regex to handle all cases
			// Matches: key:"quoted value" or key:'quoted value' or key:value_until_next_key_or_end
			var pattern = @"(\w+):(?:""([^""]*)""|'([^']*)'|([^\s]+(?:\s+(?!\w+:)[^\s]+)*))";
			var matches = Regex.Matches(paramsText, pattern);
			
			foreach (Match match in matches)
			{
				if (match.Groups.Count >= 2)
				{
					var key = match.Groups[1].Value.Trim();
					// Check which group matched (quoted with ", quoted with ', or unquoted)
					var value = match.Groups[2].Success ? match.Groups[2].Value :
							   match.Groups[3].Success ? match.Groups[3].Value :
							   match.Groups[4].Value;
					result[key] = value.Trim();
				}
			}

			return result;
		}

		Order[] ParseSimpleOrder(string orderType, Player player)
		{
			switch (orderType.ToLowerInvariant())
			{
				case "stop":
					var selectedUnits = GetSelectedUnits(player);
					return selectedUnits.Select(u => new Order("Stop", u, false)).ToArray();

				default:
					Log.Write("debug", $"Unknown simple order type: {orderType}");
					return null;
			}
		}

		Order[] ParseComplexOrder(string orderType, Dictionary<string, string> parameters, Player player)
		{
			switch (orderType.ToLowerInvariant())
			{
				case "move":
					return ParseMoveOrder(parameters, player);
					
				case "attack":
					return ParseAttackOrder(parameters, player);
					
				case "startproduction":
					return ParseProductionOrder(parameters, player);
					
				case "cancelproduction":
					return ParseCancelProductionOrder(parameters, player);
					
				case "stop":
					return ParseStopOrder(parameters, player);
					
				case "guard":
					return ParseGuardOrder(parameters, player);

				case "creategroup":
					return ParseCreateGroupOrder(parameters, player);

				case "selectgroup":
					return ParseSelectGroupOrder(parameters, player);

				default:
					Log.Write("debug", $"Unknown order type: {orderType}");
					return null;
			}
		}

		Order[] ParseMoveOrder(Dictionary<string, string> parameters, Player player)
		{
			// Get target position
			if (!parameters.TryGetValue("Target", out var targetStr))
			{
				Log.Write("debug", "Move order missing Target parameter");
				return null;
			}

			var target = ParseTarget(targetStr, player);
			if (target == Target.Invalid)
			{
				Log.Write("debug", $"Invalid move target: {targetStr}");
				return null;
			}

			// Get units to move
			var units = GetUnitsFromParameters(parameters, player);
			if (units.Length == 0)
			{
				Log.Write("debug", "No units found for move order");
				return null;
			}

			// Check if this should be queued
			var queued = parameters.ContainsKey("Queued") && 
				parameters["Queued"].Equals("true", StringComparison.OrdinalIgnoreCase);

			return units.Select(u => new Order("Move", u, target, queued)).ToArray();
		}

		Order[] ParseAttackOrder(Dictionary<string, string> parameters, Player player)
		{
			// Get target
			if (!parameters.TryGetValue("Target", out var targetStr))
			{
				Log.Write("debug", "Attack order missing Target parameter");
				return null;
			}

			var target = ParseTarget(targetStr, player);
			if (target == Target.Invalid)
			{
				Log.Write("debug", $"Invalid attack target: {targetStr}");
				return null;
			}

			// Get attacking units
			var units = GetUnitsFromParameters(parameters, player);
			if (units.Length == 0)
			{
				Log.Write("debug", "No units found for attack order");
				return null;
			}

			// Check if this should be queued
			var queued = parameters.ContainsKey("Queued") && 
				parameters["Queued"].Equals("true", StringComparison.OrdinalIgnoreCase);

			return units.Select(u => new Order("Attack", u, target, queued)).ToArray();
		}

		Order[] ParseStopOrder(Dictionary<string, string> parameters, Player player)
		{
			var units = GetUnitsFromParameters(parameters, player);
			if (units.Length == 0)
			{
				Log.Write("debug", "No units found for stop order");
				return null;
			}

			return units.Select(u => new Order("Stop", u, false)).ToArray();
		}

		Order[] ParseGuardOrder(Dictionary<string, string> parameters, Player player)
		{
			// Get target to guard
			if (!parameters.TryGetValue("Target", out var targetStr))
			{
				Log.Write("debug", "Guard order missing Target parameter");
				return null;
			}

			var target = ParseTarget(targetStr, player);
			if (target == Target.Invalid)
			{
				Log.Write("debug", $"Invalid guard target: {targetStr}");
				return null;
			}

			// Get units to guard
			var units = GetUnitsFromParameters(parameters, player);
			if (units.Length == 0)
			{
				Log.Write("debug", "No units found for guard order");
				return null;
			}

			return units.Select(u => new Order("Guard", u, target, false)).ToArray();
		}

		Order[] ParseProductionOrder(Dictionary<string, string> parameters, Player player)
		{
			// Get the building/queue
			if (!parameters.TryGetValue("Building", out var buildingStr) &&
				!parameters.TryGetValue("Queue", out buildingStr))
			{
				// Try to find any production building
				buildingStr = "any";
			}

			// Get item to produce
			if (!parameters.TryGetValue("Item", out var itemStr))
			{
				Log.Write("debug", "Production order missing Item parameter");
				return null;
			}

			// Get count (default 1)
			var count = 1;
			if (parameters.TryGetValue("Count", out var countStr))
			{
				if (!int.TryParse(countStr, out count) || count < 1)
					count = 1;
			}

			// Find the production building
			var building = FindProductionBuilding(buildingStr, itemStr, player);
			if (building == null)
			{
				Log.Write("debug", $"No production building found for {itemStr}");
				return null;
			}

			// Map friendly item name to internal name
			var internalItemName = FriendlyNames.GetInternalActorName(itemStr);
			Log.Write("debug", $"[ExternalOrderParser] Mapped '{itemStr}' to internal name '{internalItemName}'");

			// Check if this should be queued (default true for production)
			var queued = !parameters.ContainsKey("Queued") || 
				!parameters["Queued"].Equals("false", StringComparison.OrdinalIgnoreCase);

			return new[] { Order.StartProduction(building, internalItemName, count, queued) };
		}

		Order[] ParseCancelProductionOrder(Dictionary<string, string> parameters, Player player)
		{
			// Similar to StartProduction but cancels
			if (!parameters.TryGetValue("Item", out var itemStr))
			{
				Log.Write("debug", "Cancel production order missing Item parameter");
				return null;
			}

			var count = 1;
			if (parameters.TryGetValue("Count", out var countStr))
			{
				if (!int.TryParse(countStr, out count) || count < 1)
					count = 1;
			}

			// Find the production building with this item queued
			var building = FindProductionBuildingWithItem(itemStr, player);
			if (building == null)
			{
				Log.Write("debug", $"No production building found with queued {itemStr}");
				return null;
			}

			var internalItemName = FriendlyNames.GetInternalActorName(itemStr);
			return new[] { Order.CancelProduction(building, internalItemName, count) };
		}

		Order[] ParseCreateGroupOrder(Dictionary<string, string> parameters, Player player)
		{
			if (!parameters.TryGetValue("Number", out var numberStr) || 
				!int.TryParse(numberStr, out var groupNumber) || 
				groupNumber < 1 || groupNumber > 10)
			{
				Log.Write("debug", "Invalid group number for CreateGroup");
				return null;
			}

			var units = GetUnitsFromParameters(parameters, player);
			if (units.Length == 0)
			{
				Log.Write("debug", "No units found for CreateGroup");
				return null;
			}

			// CreateGroup is handled by control groups system
			// We need to simulate the key press for Ctrl+[number]
			// This is complex and may need different implementation
			Log.Write("debug", $"CreateGroup order not yet fully implemented");
			return null;
		}

		Order[] ParseSelectGroupOrder(Dictionary<string, string> parameters, Player player)
		{
			if (!parameters.TryGetValue("Number", out var numberStr) || 
				!int.TryParse(numberStr, out var groupNumber) || 
				groupNumber < 1 || groupNumber > 10)
			{
				Log.Write("debug", "Invalid group number for SelectGroup");
				return null;
			}

			// SelectGroup is handled by control groups system
			// We need to simulate the key press for [number]
			// This is complex and may need different implementation
			Log.Write("debug", $"SelectGroup order not yet fully implemented");
			return null;
		}

		Target ParseTarget(string targetStr, Player player)
		{
			// Format can be:
			// "50,75" - map position
			// "EnemyBuilding@100,200" - specific actor type at position
			// "Tank#3" - specific unit by type and index

			// Check for map coordinates (simplest case)
			var parts = targetStr.Split(',');
			if (parts.Length == 2)
			{
				if (int.TryParse(parts[0].Trim(), out var x) && 
					int.TryParse(parts[1].Trim(), out var y))
				{
					var cell = new CPos(x, y);
					if (world.Map.Contains(cell))
					{
						var pos = world.Map.CenterOfCell(cell);
						return Target.FromPos(pos);
					}
				}
			}

			// Check for actor@position format
			if (targetStr.Contains('@'))
			{
				var atParts = targetStr.Split('@');
				var actorType = atParts[0].Trim();
				var posStr = atParts[1].Trim();
				
				// Parse position
				var posParts = posStr.Split(',');
				if (posParts.Length == 2 && 
					int.TryParse(posParts[0].Trim(), out var px) && 
					int.TryParse(posParts[1].Trim(), out var py))
				{
					var targetCell = new CPos(px, py);
					if (world.Map.Contains(targetCell))
					{
						// Find nearest actor of that type
						var targetActor = FindNearestActor(actorType, targetCell, player);
						if (targetActor != null)
							return Target.FromActor(targetActor);
					}
				}
			}

			// Check for unit by type and index (e.g., "Tank#3")
			if (targetStr.Contains('#'))
			{
				var hashParts = targetStr.Split('#');
				var unitType = hashParts[0].Trim();
				if (hashParts.Length == 2 && int.TryParse(hashParts[1].Trim(), out var index))
				{
					var unit = GetUnitByTypeAndIndex(unitType, index, player);
					if (unit != null)
						return Target.FromActor(unit);
				}
			}

			// Try to find any actor with that name
			var actor = FindActorByName(targetStr, player);
			if (actor != null)
				return Target.FromActor(actor);

			return Target.Invalid;
		}

		Actor[] GetUnitsFromParameters(Dictionary<string, string> parameters, Player player)
		{
			if (!parameters.TryGetValue("Units", out var unitsStr))
			{
				// If no units specified, use currently selected units
				return GetSelectedUnits(player);
			}

			var unitList = new List<Actor>();
			var unitSpecs = unitsStr.Split(',');

			foreach (var spec in unitSpecs)
			{
				var trimmed = spec.Trim();
				
				// Handle "Type#Index" format
				if (trimmed.Contains('#'))
				{
					var parts = trimmed.Split('#');
					if (parts.Length == 2 && int.TryParse(parts[1], out var index))
					{
						var unit = GetUnitByTypeAndIndex(parts[0], index, player);
						if (unit != null)
							unitList.Add(unit);
					}
				}
				else
				{
					// Just unit type, get all of that type
					var units = GetUnitsByType(trimmed, player);
					unitList.AddRange(units);
				}
			}

			return unitList.Distinct().ToArray();
		}

		Actor[] GetSelectedUnits(Player player)
		{
			// This is tricky as selection is usually client-side
			// We might need to track this separately or use a different approach
			// For now, return empty array
			Log.Write("debug", "GetSelectedUnits not yet implemented - need selection tracking");
			return new Actor[0];
		}

		Actor[] GetUnitsByType(string unitType, Player player)
		{
			var internalName = FriendlyNames.GetInternalActorName(unitType);
			
			return world.Actors
				.Where(a => a.Owner == player && 
						   !a.IsDead && 
						   string.Equals(a.Info.Name, internalName, StringComparison.OrdinalIgnoreCase) &&
						   !a.Info.HasTraitInfo<BuildingInfo>())
				.ToArray();
		}

		Actor GetUnitByTypeAndIndex(string unitType, int index, Player player)
		{
			if (index < 1)
				return null;

			var units = GetUnitsByType(unitType, player);
			if (index <= units.Length)
				return units[index - 1];

			return null;
		}

		Actor[] GetBuildingsByType(string buildingType, Player player)
		{
			var internalName = FriendlyNames.GetInternalActorName(buildingType);
			
			return world.Actors
				.Where(a => a.Owner == player && 
						   !a.IsDead && 
						   string.Equals(a.Info.Name, internalName, StringComparison.OrdinalIgnoreCase) &&
						   a.Info.HasTraitInfo<BuildingInfo>())
				.OrderBy(a => a.ActorID) // Consistent ordering by ActorID
				.ToArray();
		}

		Actor GetBuildingByTypeAndIndex(string buildingType, int index, Player player)
		{
			if (index < 1)
				return null;

			var buildings = GetBuildingsByType(buildingType, player);
			if (index <= buildings.Length)
				return buildings[index - 1];

			return null;
		}

		Actor FindProductionBuilding(string buildingSpec, string itemToProduce, Player player)
		{
			// Check for Building#Index format first
			if (buildingSpec.Contains('#'))
			{
				var parts = buildingSpec.Split('#');
				if (parts.Length == 2 && int.TryParse(parts[1], out var index))
				{
					var building = GetBuildingByTypeAndIndex(parts[0].Trim(), index, player);
					if (building != null)
					{
						// Verify it can produce the item
						var queues = building.TraitsImplementing<ProductionQueue>();
						foreach (var queue in queues)
						{
							var internalName = FriendlyNames.GetInternalActorName(itemToProduce);
							var actorInfo = world.Map.Rules.Actors.TryGetValue(internalName, out var info) ? info : null;
							if (actorInfo != null && queue.CanBuild(actorInfo))
							{
								Log.Write("debug", $"[ExternalOrderParser] Found building #{index} {building.Info.Name} that can produce {internalName}");
								return building;
							}
						}
						
						// Building exists but can't produce this item
						Log.Write("debug", $"[ExternalOrderParser] Building {parts[0]}#{index} exists but cannot produce {itemToProduce}");
						return null;
					}
					else
					{
						Log.Write("debug", $"[ExternalOrderParser] Building {parts[0]}#{index} not found");
						return null;
					}
				}
			}
			
			// Original logic for non-indexed building specs
			// Find a building that can produce the specified item
			var buildings = world.Actors
				.Where(a => a.Owner == player && !a.IsDead && a.Info.HasTraitInfo<BuildingInfo>());

			if (!buildingSpec.Equals("any", StringComparison.OrdinalIgnoreCase))
			{
				// Specific building type requested
				var internalBuildingName = FriendlyNames.GetInternalActorName(buildingSpec);
				buildings = buildings.Where(b => 
					string.Equals(b.Info.Name, internalBuildingName, StringComparison.OrdinalIgnoreCase));
			}

			// Find one that can produce the item
			foreach (var building in buildings)
			{
				var queues = building.TraitsImplementing<ProductionQueue>();
				foreach (var queue in queues)
				{
					var internalName = FriendlyNames.GetInternalActorName(itemToProduce);
					var actorInfo = world.Map.Rules.Actors.TryGetValue(internalName, out var info) ? info : null;
					if (actorInfo != null && queue.CanBuild(actorInfo))
					{
						Log.Write("debug", $"[ExternalOrderParser] Found building {building.Info.Name} that can produce {internalName}");
						return building;
					}
				}
			}

			// Also check player actor for queues
			var playerQueues = player.PlayerActor.TraitsImplementing<ProductionQueue>();
			foreach (var queue in playerQueues)
			{
				var internalName = FriendlyNames.GetInternalActorName(itemToProduce);
				var actorInfo = world.Map.Rules.Actors.TryGetValue(internalName, out var info) ? info : null;
				if (actorInfo != null && queue.CanBuild(actorInfo))
				{
					Log.Write("debug", $"[ExternalOrderParser] Found player queue that can produce {internalName}");
					return player.PlayerActor;
				}
			}

			return null;
		}

		Actor FindProductionBuildingWithItem(string item, Player player)
		{
			var internalName = FriendlyNames.GetInternalActorName(item);
			
			// Check all buildings
			var buildings = world.Actors
				.Where(a => a.Owner == player && !a.IsDead && a.Info.HasTraitInfo<BuildingInfo>());

			foreach (var building in buildings)
			{
				var queues = building.TraitsImplementing<ProductionQueue>();
				foreach (var queue in queues)
				{
					if (queue.AllQueued().Any(q => string.Equals(q.Item, internalName, StringComparison.OrdinalIgnoreCase)))
						return building;
				}
			}

			// Check player actor queues
			var playerQueues = player.PlayerActor.TraitsImplementing<ProductionQueue>();
			foreach (var queue in playerQueues)
			{
				if (queue.AllQueued().Any(q => string.Equals(q.Item, internalName, StringComparison.OrdinalIgnoreCase)))
					return player.PlayerActor;
			}

			return null;
		}

		Actor FindNearestActor(string actorType, CPos position, Player player)
		{
			var internalName = FriendlyNames.GetInternalActorName(actorType);
			var worldPos = world.Map.CenterOfCell(position);

			var candidates = world.Actors
				.Where(a => !a.IsDead && 
						   (a.Owner == player || a.Owner.IsAlliedWith(player) || true) && // For now, allow targeting any actor
						   string.Equals(a.Info.Name, internalName, StringComparison.OrdinalIgnoreCase));

			Actor nearest = null;
			var nearestDist = int.MaxValue;

			foreach (var actor in candidates)
			{
				var dist = (actor.CenterPosition - worldPos).LengthSquared;
				if (dist < nearestDist)
				{
					nearest = actor;
					nearestDist = (int)dist;
				}
			}

			return nearest;
		}

		Actor FindActorByName(string name, Player player)
		{
			var internalName = FriendlyNames.GetInternalActorName(name);
			
			return world.Actors
				.FirstOrDefault(a => a.Owner == player && 
								   !a.IsDead && 
								   string.Equals(a.Info.Name, internalName, StringComparison.OrdinalIgnoreCase));
		}

	}
}