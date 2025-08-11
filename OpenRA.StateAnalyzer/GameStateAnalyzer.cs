using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OpenRA.StateAnalyzer
{
	public class GameStateAnalyzer
	{
		readonly GameStateParser parser = new();

		public async Task<List<string>> AnalyzeStateChanges(string state1Path, string state2Path)
		{
			var state1 = await parser.ParseAsync(state1Path);
			var state2 = await parser.ParseAsync(state2Path);

			var actions = new List<string>();

			// Validate that these are from the same game
			if (state1.MapName != state2.MapName)
			{
				actions.Add($"WARNING: Different maps detected ({state1.MapName} vs {state2.MapName})");
			}

			var timeDiff = (state2.GameTick - state1.GameTick) / 25.0; // Convert ticks to seconds
			actions.Add($"Time period analyzed: {timeDiff:F1} seconds (ticks {state1.GameTick} to {state2.GameTick})");
			actions.Add("");

			// Analyze each player's changes
			foreach (var playerName in state1.Players.Keys.Union(state2.Players.Keys))
			{
				if (!state1.Players.ContainsKey(playerName) || !state2.Players.ContainsKey(playerName))
				{
					actions.Add($"{playerName}: Player state missing in one of the snapshots");
					continue;
				}

				var player1 = state1.Players[playerName];
				var player2 = state2.Players[playerName];

				var playerActions = AnalyzePlayerChanges(player1, player2);
				if (playerActions.Count > 0)
				{
					actions.Add($"=== {playerName} ({player1.Faction}) ===");
					actions.AddRange(playerActions);
					actions.Add("");
				}
			}

			// Analyze visible enemy structure changes
			AnalyzeEnemyStructureChanges(state1, state2, actions);

			return actions;
		}

		List<string> AnalyzePlayerChanges(Player before, Player after)
		{
			var actions = new List<string>();

			// Economic changes
			AnalyzeEconomicChanges(before, after, actions);

			// Military statistics changes
			AnalyzeMilitaryChanges(before, after, actions);

			// Power changes
			AnalyzePowerChanges(before, after, actions);

			// Unit changes
			AnalyzeUnitChanges(before, after, actions);

			// Building changes
			AnalyzeBuildingChanges(before, after, actions);

			// Production queue changes
			AnalyzeProductionChanges(before, after, actions);

			// Special unit changes
			AnalyzeSpecialUnitChanges(before, after, actions);

			return actions;
		}

		void AnalyzeEconomicChanges(Player before, Player after, List<string> actions)
		{
			var cashChange = after.Cash - before.Cash;
			var resourceChange = after.StoredResources - before.StoredResources;
			var earnedChange = after.TotalEarned - before.TotalEarned;
			var spentChange = after.TotalSpent - before.TotalSpent;

			if (Math.Abs(cashChange) > 0 || Math.Abs(resourceChange) > 0)
			{
				actions.Add("ECONOMIC ACTIVITY:");
				
				if (cashChange != 0)
					actions.Add($"  Cash changed by ${cashChange:+#;-#;0} (${before.Cash} → ${after.Cash})");
				
				if (resourceChange != 0)
					actions.Add($"  Stored resources changed by {resourceChange:+#;-#;0} ({before.StoredResources} → {after.StoredResources})");

				if (earnedChange > 0)
					actions.Add($"  Earned ${earnedChange} additional income");

				if (spentChange > 0)
					actions.Add($"  Spent ${spentChange} on purchases/construction");

				// Infer specific economic actions
				if (earnedChange > spentChange && cashChange < 0)
					actions.Add("  → Likely harvesting resources and spending on construction/units");
				else if (earnedChange > 0 && spentChange == 0)
					actions.Add("  → Harvesting resources with no major purchases");
				else if (spentChange > 0 && cashChange < 0)
					actions.Add("  → Making purchases (units/buildings) faster than income");

				actions.Add("");
			}
		}

		void AnalyzeMilitaryChanges(Player before, Player after, List<string> actions)
		{
			var armyValueChange = after.ArmyValue - before.ArmyValue;
			var assetsValueChange = after.TotalAssetsValue - before.TotalAssetsValue;
			var newKills = after.UnitsKilled - before.UnitsKilled;
			var newLosses = after.UnitsLost - before.UnitsLost;
			var buildingKills = after.BuildingsDestroyed - before.BuildingsDestroyed;
			var buildingLosses = after.BuildingsLost - before.BuildingsLost;
			var expGain = after.Experience - before.Experience;

			var hasCombat = newKills > 0 || newLosses > 0 || buildingKills > 0 || buildingLosses > 0;
			var hasSignificantChange = Math.Abs(armyValueChange) > 1000 || Math.Abs(assetsValueChange) > 1000;

			if (hasCombat || hasSignificantChange)
			{
				actions.Add("MILITARY ACTIVITY:");

				if (newKills > 0)
					actions.Add($"  Destroyed {newKills} enemy units (${after.KillsCost - before.KillsCost} value)");

				if (newLosses > 0)
					actions.Add($"  Lost {newLosses} units (${after.DeathsCost - before.DeathsCost} value)");

				if (buildingKills > 0)
					actions.Add($"  Destroyed {buildingKills} enemy buildings");

				if (buildingLosses > 0)
					actions.Add($"  Lost {buildingLosses} buildings");

				if (expGain > 0)
					actions.Add($"  Gained {expGain} experience points");

				if (armyValueChange != 0)
					actions.Add($"  Army value changed by ${armyValueChange:+#;-#;0} (${before.ArmyValue} → ${after.ArmyValue})");

				// Combat analysis
				if (hasCombat)
				{
					if (newKills > newLosses)
						actions.Add("  → Successful engagement, favorable trade");
					else if (newKills < newLosses)
						actions.Add("  → Unfavorable engagement, took heavy losses");
					else if (newKills == newLosses && newKills > 0)
						actions.Add("  → Even exchange in combat");
				}

				actions.Add("");
			}
		}

		void AnalyzePowerChanges(Player before, Player after, List<string> actions)
		{
			var powerProvidedChange = after.PowerProvided - before.PowerProvided;
			var powerConsumedChange = after.PowerConsumed - before.PowerConsumed;
			var stateChanged = before.PowerState != after.PowerState;

			if (powerProvidedChange != 0 || powerConsumedChange != 0 || stateChanged)
			{
				actions.Add("POWER GRID CHANGES:");

				if (powerProvidedChange > 0)
					actions.Add($"  Built power generation (+{powerProvidedChange} power)");
				else if (powerProvidedChange < 0)
					actions.Add($"  Lost power generation ({powerProvidedChange} power)");

				if (powerConsumedChange > 0)
					actions.Add($"  Increased power consumption (+{powerConsumedChange} power)");
				else if (powerConsumedChange < 0)
					actions.Add($"  Reduced power consumption ({powerConsumedChange} power)");

				if (stateChanged)
					actions.Add($"  Power state changed: {before.PowerState} → {after.PowerState}");

				actions.Add("");
			}
		}

		void AnalyzeUnitChanges(Player before, Player after, List<string> actions)
		{
			var unitActions = new List<string>();
			var allUnitTypes = before.Units.Keys.Union(after.Units.Keys);

			foreach (var unitType in allUnitTypes)
			{
				var beforeCount = before.Units.ContainsKey(unitType) ? before.Units[unitType].Count : 0;
				var afterCount = after.Units.ContainsKey(unitType) ? after.Units[unitType].Count : 0;
				var change = afterCount - beforeCount;

				if (change > 0)
					unitActions.Add($"  Produced {change} {unitType}{(change > 1 ? "s" : "")}");
				else if (change < 0)
					unitActions.Add($"  Lost/consumed {-change} {unitType}{(-change > 1 ? "s" : "")}");
			}

			if (unitActions.Count > 0)
			{
				actions.Add("UNIT PRODUCTION/LOSSES:");
				actions.AddRange(unitActions);
				actions.Add("");
			}
		}

		void AnalyzeBuildingChanges(Player before, Player after, List<string> actions)
		{
			var buildingActions = new List<string>();
			var allBuildingTypes = before.Buildings.Keys.Union(after.Buildings.Keys);

			foreach (var buildingType in allBuildingTypes)
			{
				var beforeCount = before.Buildings.ContainsKey(buildingType) ? before.Buildings[buildingType].Count : 0;
				var afterCount = after.Buildings.ContainsKey(buildingType) ? after.Buildings[buildingType].Count : 0;
				var change = afterCount - beforeCount;

				if (change > 0)
					buildingActions.Add($"  Constructed {change} {buildingType}{(change > 1 ? "s" : "")}");
				else if (change < 0)
					buildingActions.Add($"  Lost {-change} {buildingType}{(-change > 1 ? "s" : "")}");
			}

			// Check for new building positions (expansions)
			var newPositions = after.BuildingPositions.Where(pos => 
				!before.BuildingPositions.Any(oldPos => 
					oldPos.Type == pos.Type && oldPos.X == pos.X && oldPos.Y == pos.Y)).ToList();

			foreach (var pos in newPositions)
			{
				buildingActions.Add($"  Built {pos.Type} at position ({pos.X}, {pos.Y})");
			}

			if (buildingActions.Count > 0)
			{
				actions.Add("CONSTRUCTION ACTIVITY:");
				actions.AddRange(buildingActions);
				
				// Analyze expansion patterns
				AnalyzeExpansionPatterns(before, after, actions);
				actions.Add("");
			}
		}

		void AnalyzeExpansionPatterns(Player before, Player after, List<string> actions)
		{
			// Check for base expansion
			var beforePositions = before.BuildingPositions.Select(p => (p.X, p.Y)).ToHashSet();
			var afterPositions = after.BuildingPositions.Select(p => (p.X, p.Y)).ToHashSet();
			var newPositions = afterPositions.Except(beforePositions).ToList();

			if (newPositions.Count > 0)
			{
				// Calculate if this looks like a new base area
				var avgBeforeX = before.BuildingPositions.Average(p => p.X);
				var avgBeforeY = before.BuildingPositions.Average(p => p.Y);
				
				var distantExpansions = newPositions.Where(pos => 
					Math.Sqrt(Math.Pow(pos.X - avgBeforeX, 2) + Math.Pow(pos.Y - avgBeforeY, 2)) > 15).ToList();

				if (distantExpansions.Count > 0)
				{
					actions.Add("  → Base expansion detected to new area");
				}
			}
		}

		void AnalyzeProductionChanges(Player before, Player after, List<string> actions)
		{
			var productionActions = new List<string>();
			var allQueues = before.ProductionQueues.Keys.Union(after.ProductionQueues.Keys);

			foreach (var queueType in allQueues)
			{
				var beforeQueue = before.ProductionQueues.ContainsKey(queueType) ? before.ProductionQueues[queueType] : new List<ProductionItem>();
				var afterQueue = after.ProductionQueues.ContainsKey(queueType) ? after.ProductionQueues[queueType] : new List<ProductionItem>();

				// Check for completed items (items that were in progress before but not in queue after)
				var beforeInProgress = beforeQueue.Where(i => i.Status == "IN_PROGRESS").ToList();
				var afterItems = afterQueue.Select(i => i.Type).ToHashSet();

				foreach (var item in beforeInProgress)
				{
					if (!afterItems.Contains(item.Type))
						productionActions.Add($"  Completed production of {item.Type}");
				}

				// Check for new items in queue
				var beforeItems = beforeQueue.Select(i => i.Type).ToHashSet();
				var newItems = afterQueue.Where(i => !beforeItems.Contains(i.Type)).ToList();

				foreach (var item in newItems)
				{
					var countStr = item.Count > 1 ? $" x{item.Count}" : "";
					productionActions.Add($"  Queued {item.Type}{countStr} for production");
				}

				// Check for items that became ready
				var readyItems = afterQueue.Where(i => i.Status == "READY").ToList();
				var wasNotReady = beforeQueue.Where(i => i.Type == readyItems.FirstOrDefault()?.Type && i.Status != "READY").Any();

				foreach (var item in readyItems.Where(i => wasNotReady))
				{
					productionActions.Add($"  {item.Type} production completed and ready for deployment");
				}
			}

			if (productionActions.Count > 0)
			{
				actions.Add("PRODUCTION ACTIVITY:");
				actions.AddRange(productionActions);
				actions.Add("");
			}
		}

		void AnalyzeSpecialUnitChanges(Player before, Player after, List<string> actions)
		{
			var harvesterChange = after.HarvesterCount - before.HarvesterCount;
			var mcvChange = after.McvCount - before.McvCount;

			if (harvesterChange != 0 || mcvChange != 0)
			{
				actions.Add("SPECIAL UNIT CHANGES:");

				if (harvesterChange > 0)
					actions.Add($"  Gained {harvesterChange} harvester{(harvesterChange > 1 ? "s" : "")}");
				else if (harvesterChange < 0)
					actions.Add($"  Lost {-harvesterChange} harvester{(-harvesterChange > 1 ? "s" : "")}");

				if (mcvChange > 0)
					actions.Add($"  Gained {mcvChange} MCV{(mcvChange > 1 ? "s" : "")}");
				else if (mcvChange < 0)
					actions.Add($"  Lost/deployed {-mcvChange} MCV{(-mcvChange > 1 ? "s" : "")}");
					
				if (mcvChange < 0 && (after.Buildings.Count > before.Buildings.Count))
					actions.Add("  → Likely deployed MCV to establish new base");

				actions.Add("");
			}
		}

		void AnalyzeEnemyStructureChanges(GameState before, GameState after, List<string> actions)
		{
			var allEnemyPlayers = before.VisibleEnemyStructures.Keys.Union(after.VisibleEnemyStructures.Keys);
			var enemyChanges = new List<string>();

			foreach (var enemyPlayer in allEnemyPlayers)
			{
				var beforeStructures = before.VisibleEnemyStructures.ContainsKey(enemyPlayer) ? 
					before.VisibleEnemyStructures[enemyPlayer] : new List<EnemyBuilding>();
				var afterStructures = after.VisibleEnemyStructures.ContainsKey(enemyPlayer) ? 
					after.VisibleEnemyStructures[enemyPlayer] : new List<EnemyBuilding>();

				// Find destroyed structures
				var destroyed = beforeStructures.Where(b => !afterStructures.Any(a => 
					a.Type == b.Type && a.X == b.X && a.Y == b.Y)).ToList();

				// Find new structures
				var newStructures = afterStructures.Where(a => !beforeStructures.Any(b => 
					b.Type == a.Type && b.X == a.X && b.Y == a.Y)).ToList();

				foreach (var structure in destroyed)
					enemyChanges.Add($"  {enemyPlayer}'s {structure.Type} at ({structure.X}, {structure.Y}) was destroyed");

				foreach (var structure in newStructures)
					enemyChanges.Add($"  Spotted new {enemyPlayer} {structure.Type} at ({structure.X}, {structure.Y})");
			}

			if (enemyChanges.Count > 0)
			{
				actions.Add("=== ENEMY INTELLIGENCE ===");
				actions.AddRange(enemyChanges);
				actions.Add("");
			}
		}
	}
}