using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Exports game state snapshots to text files for analysis (e.g., by LLMs).")]
	public class GameStateExporterInfo : TraitInfo
	{
		[Desc("Directory path where state snapshots will be saved.")]
		public readonly string OutputDirectory = @"C:\OpenRATest";

		[Desc("Interval between snapshots in game ticks (25 ticks = 1 second).")]
		public readonly int SnapshotInterval = 500; // 20 seconds

		[Desc("Enable or disable the exporter.")]
		public readonly bool Enabled = true;

		[Desc("If true, export snapshots at regular intervals. If false, use request-based mode triggered by external orders.")]
		public readonly bool UseIntervalMode = false;

		public override object Create(ActorInitializer init) { return new GameStateExporter(this); }
	}

	public class GameStateExporter : ITick
	{
		readonly GameStateExporterInfo info;
		World world;
		int exportRequestedAtTick = -1;
		int lastIntervalExportTick = 0;
		bool hasArchivedForThisGame = false;
		const int ExportDelayTicks = 5; // Wait 5 ticks after request to allow orders to propagate

		public GameStateExporter(GameStateExporterInfo info)
		{
			this.info = info;
		}

		public void RequestSnapshot()
		{
			if (world != null)
				exportRequestedAtTick = world.WorldTick;
		}

		void ITick.Tick(Actor self)
		{
			if (!info.Enabled)
				return;

			world = self.World;

			// Skip if in menu (Blank Shellmap)
			if (world.Map.Title == "Blank Shellmap")
				return;

			// Archive previous game files and take initial snapshot on tick 2
			if (world.WorldTick == 2)
			{
				// Check if this is a new map/game
				if (!hasArchivedForThisGame)
				{
					ArchivePreviousGameFiles();
					hasArchivedForThisGame = true;
				}

				ExportGameState();
				lastIntervalExportTick = world.WorldTick;
				return;
			}

			if (info.UseIntervalMode)
			{
				// Interval-based mode: export at regular intervals
				if (world.WorldTick - lastIntervalExportTick >= info.SnapshotInterval)
				{
					lastIntervalExportTick = world.WorldTick;
					ExportGameState();
				}
			}
			else
			{
				// Request-based mode: export after delay when requested
				if (exportRequestedAtTick >= 0 && world.WorldTick >= exportRequestedAtTick + ExportDelayTicks)
				{
					exportRequestedAtTick = -1;
					ExportGameState();
				}
			}
		}

		void ArchivePreviousGameFiles()
		{
			try
			{
				if (!Directory.Exists(info.OutputDirectory))
					return;

				var currentGameStateFile = Path.Combine(info.OutputDirectory, "current_gamestate.txt");
				if (!File.Exists(currentGameStateFile))
					return;

				var archiveDirectory = Path.Combine(info.OutputDirectory, "archive");
				if (!Directory.Exists(archiveDirectory))
					Directory.CreateDirectory(archiveDirectory);

				var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
				var gameArchiveDirectory = Path.Combine(archiveDirectory, $"game_{timestamp}");
				Directory.CreateDirectory(gameArchiveDirectory);

				var destinationPath = Path.Combine(gameArchiveDirectory, "current_gamestate.txt");
				File.Move(currentGameStateFile, destinationPath);

				Log.Write("debug", $"Archived current gamestate file to {gameArchiveDirectory}");
			}
			catch (Exception e)
			{
				Log.Write("debug", $"Failed to archive previous game files: {e.Message}");
			}
		}

		void ExportPlayerState(StringBuilder sb, Player player, World world, bool isHumanPlayer)
		{
			var factionName = player.Faction.Name.Replace("faction-", "").Replace(".name", "");
			// Proper case for faction names
			if (factionName.Equals("nod", StringComparison.OrdinalIgnoreCase))
				factionName = "Nod";
			else if (factionName.Equals("gdi", StringComparison.OrdinalIgnoreCase))
				factionName = "GDI";
			
			var cleanPlayerName = player.PlayerName.Replace("bot-", "").Replace(".name", "");
			
			if (!isHumanPlayer)
			{
				sb.AppendLine();
				sb.AppendLine(CultureInfo.InvariantCulture, $"## Player: {cleanPlayerName} ({factionName})");
			}

			// Show allies and status
			var players = world.Players.Where(p => !p.NonCombatant && p.Playable).ToList();
			var allies = players.Where(p => p != player && player.IsAlliedWith(p)).Select(p => p.PlayerName).ToList();
			if (!isHumanPlayer)
			{
				sb.AppendLine("Allies: " + (allies.Count > 0 ? string.Join(", ", allies) : "None"));
				var status = player.WinState == WinState.Won ? "Won" : player.WinState == WinState.Lost ? "Lost" : "Playing";
				sb.AppendLine(CultureInfo.InvariantCulture, $"Status: {status}");
			}

			// Economic status for human player
			if (isHumanPlayer)
			{
				var playerResources = player.PlayerActor.TraitOrDefault<PlayerResources>();
				if (playerResources != null)
				{
					sb.AppendLine();
					sb.AppendLine("### Economic Status");
					sb.AppendLine(CultureInfo.InvariantCulture, $"Cash: ${playerResources.Cash}, Resources: {playerResources.Resources}/{playerResources.ResourceCapacity}");
				}
				
				// Military statistics for human player
				var playerStats = player.PlayerActor.TraitOrDefault<PlayerStatistics>();
				if (playerStats != null)
				{
					sb.AppendLine();
					sb.AppendLine("### Military Statistics");
					sb.AppendLine(CultureInfo.InvariantCulture, $"Army: ${playerStats.ArmyValue}, Killed: {playerStats.UnitsKilled}, Lost: {playerStats.UnitsDead}");
				}
			}

			// Power Status
			var power = player.PlayerActor.TraitOrDefault<PowerManager>();
			if (power != null)
			{
				sb.AppendLine();
				sb.AppendLine("### Power Status");
				var balance = power.PowerProvided - power.PowerDrained;
				var status = balance >= 0 ? $"+{balance}" : $"{balance}";
				sb.AppendLine(CultureInfo.InvariantCulture, $"{power.PowerProvided}/{power.PowerDrained} ({status})");
			}

			// Get actors for this player
			var playerActors = world.Actors.Where(a => a.Owner == player && !a.IsDead).ToList();

			// Tiberium Blossom Trees within Build Radius
			var constructionYards = playerActors
				.Where(a => a.Info.HasTraitInfo<BuildingInfo>() && a.TraitsImplementing<BaseProvider>().Any())
				.ToList();

			if (constructionYards.Count > 0)
			{
				// Find all Tiberium Blossom Trees in the world
				var blossomTrees = world.Actors
					.Where(a => !a.IsDead && a.TraitsImplementing<SeedsResource>().Any())
					.ToList();

				if (blossomTrees.Count > 0)
				{
					sb.AppendLine();
					sb.AppendLine("### Tiberium Blossom Trees in Build Radius");

					foreach (var cy in constructionYards)
					{
						var baseProvider = cy.TraitsImplementing<BaseProvider>().FirstOrDefault();
						if (baseProvider == null)
							continue;

						var cyName = FriendlyNames.GetFriendlyBuildingName(cy.Info.Name);
						var cyCell = world.Map.CellContaining(cy.CenterPosition);

						// Count trees within this Construction Yard's build radius
						var treesInRange = blossomTrees.Count(tree =>
						{
							var target = Target.FromPos(tree.CenterPosition);
							return target.IsInRange(cy.CenterPosition, baseProvider.Info.Range);
						});

						sb.AppendLine(CultureInfo.InvariantCulture, $"{cyName} ({cyCell.X},{cyCell.Y}): {treesInRange} trees");
					}
				}
			}

			var buildings = playerActors.Where(a => a.Info.HasTraitInfo<BuildingInfo>()).ToList();
			// Filter out C17 cargo planes and player actors as they're not player-controllable units
			var units = playerActors.Where(a => !a.Info.HasTraitInfo<BuildingInfo>() && 
				!string.Equals(a.Info.Name, "C17", StringComparison.OrdinalIgnoreCase) &&
				!string.Equals(a.Info.Name, "player", StringComparison.OrdinalIgnoreCase)).ToList();

			// Building Positions
			if (buildings.Count > 0)
			{
				sb.AppendLine();
				sb.AppendLine("#### Building Positions:");

				// Create a map of buildings to their production queues
				var buildingProduction = new Dictionary<Actor, List<string>>();
				var allProductionItems = new List<string>();

				foreach (var building in buildings)
				{
					buildingProduction[building] = new List<string>();
					var queues = building.TraitsImplementing<ProductionQueue>();
					foreach (var queue in queues)
					{
						var items = queue.AllQueued().ToList();
						foreach (var item in items)
						{
							var progress = item.RemainingCost == 0 ? 100 :
								(100 * (item.TotalCost - item.RemainingCost) / item.TotalCost);
							var friendlyName = world.Map.Rules.Actors[item.Item].TraitInfoOrDefault<BuildingInfo>() != null
								? FriendlyNames.GetFriendlyBuildingName(item.Item)
								: FriendlyNames.GetFriendlyUnitName(item.Item);

							var statusDescription = item.Paused ? "PAUSED" : item.Done ? "READY" : $"{progress}% complete";

							if (item.Paused && !item.Done)
								statusDescription = $"{progress}% complete (PAUSED)";

							var description = $"{friendlyName} {statusDescription}";

							buildingProduction[building].Add(description);
							allProductionItems.Add(description);
						}
					}
				}

				// Output buildings with positions (without production info)
				foreach (var building in buildings.OrderBy(b => FriendlyNames.GetFriendlyBuildingName(b.Info.Name)))
				{
					var pos = building.CenterPosition;
					var cell = world.Map.CellContaining(pos);
					var friendlyName = FriendlyNames.GetFriendlyBuildingName(building.Info.Name);
					sb.AppendLine(CultureInfo.InvariantCulture, $"{friendlyName} ({cell.X},{cell.Y})");
				}

				// Output building queue as separate section
				if (allProductionItems.Count > 0)
				{
					sb.AppendLine();
					sb.AppendLine("#### Building Queue:");
					int numberIn = 0;
					foreach (var item in allProductionItems)
					{
						numberIn++;
						sb.AppendLine(numberIn.ToString()+". "+item);
					}
				}
			}

			// Unit Positions
			if (units.Count > 0)
			{
				sb.AppendLine();
				sb.AppendLine("#### Unit Positions:");
				foreach (var unit in units.OrderBy(u => FriendlyNames.GetFriendlyUnitName(u.Info.Name)))
				{
					var pos = unit.CenterPosition;
					var cell = world.Map.CellContaining(pos);
					var friendlyName = FriendlyNames.GetFriendlyUnitName(unit.Info.Name);
					sb.AppendLine(CultureInfo.InvariantCulture, $"{friendlyName} ({cell.X},{cell.Y})");
				}
			}
		}

		void AutoPlaceReadyBuildings()
		{
			try
			{
				// Find Player 1 (the human player)
				var player1 = world.Players.FirstOrDefault(p => !p.IsBot && !p.NonCombatant && p.Playable);
				if (player1 == null)
					return;

				// Find all buildings with production queues
				var buildings = world.Actors
					.Where(a => a.Owner == player1 && !a.IsDead && a.Info.HasTraitInfo<BuildingInfo>())
					.ToList();

				// Check each building's production queues for ready items
				foreach (var building in buildings)
				{
					var queues = building.TraitsImplementing<ProductionQueue>();
					foreach (var queue in queues)
					{
						var readyItems = queue.AllQueued()
							.Where(item => item.Done && !item.Paused)
							.ToList();

						foreach (var item in readyItems)
						{
							// Only auto-place buildings, not units
							var actorInfo = world.Map.Rules.Actors[item.Item];
							var buildingInfo = actorInfo.TraitInfoOrDefault<BuildingInfo>();
							if (buildingInfo == null)
								continue; // Skip units

							// Try to find a valid placement location
							var placementLocation = FindValidPlacementLocation(actorInfo, buildingInfo, player1);
							if (placementLocation.HasValue)
							{
								// Create the PlaceBuilding order
								var order = new Order("PlaceBuilding", player1.PlayerActor, Target.FromCell(world, placementLocation.Value), false)
								{
									TargetString = item.Item,
									ExtraData = queue.Actor.ActorID,
									ExtraLocation = new CPos(0, 0), // No variant
									SuppressVisualFeedback = true
								};

								// Issue the order
				world.IssueOrder(order);
								Log.Write("debug", $"[GameStateExporter] Auto-placed {item.Item} at {placementLocation.Value}");
							}
							else
							{
								Log.Write("debug", $"[GameStateExporter] Could not find valid placement for {item.Item}");
							}
						}
					}
				}
			}
			catch (Exception e)
			{
				Log.Write("debug", $"[GameStateExporter] Failed to auto-place buildings: {e.Message}");
			}
		}

		CPos? FindValidPlacementLocation(ActorInfo actorInfo, BuildingInfo buildingInfo, Player player)
		{
			// Start near the player's base
			var baseCenter = FindBaseCenter(player);
			if (baseCenter == null)
				return null;

			// Search in expanding circles around base center
			var searchRadius = 1;
			while (searchRadius < 50) // Max search radius
			{
				for (var dx = -searchRadius; dx <= searchRadius; dx++)
				{
					for (var dy = -searchRadius; dy <= searchRadius; dy++)
					{
						// Only check cells on the perimeter of the current radius
						if (Math.Abs(dx) != searchRadius && Math.Abs(dy) != searchRadius)
							continue;

						var candidate = new CPos(baseCenter.Value.X + dx, baseCenter.Value.Y + dy);

						// Check if this location is valid
						if (world.CanPlaceBuilding(candidate, actorInfo, buildingInfo, null) &&
							buildingInfo.IsCloseEnoughToBase(world, player, actorInfo, candidate))
						{
							return candidate;
						}
					}
				}
				searchRadius++;
			}

			return null;
		}

		CPos? FindBaseCenter(Player player)
		{
			// Find the Construction Yard or any building as base center
			var constructionYard = world.Actors
				.FirstOrDefault(a => a.Owner == player &&
									!a.IsDead &&
									a.Info.HasTraitInfo<BuildingInfo>() &&
									a.Info.Name.Contains("mcv", StringComparison.OrdinalIgnoreCase));

			if (constructionYard != null)
				return world.Map.CellContaining(constructionYard.CenterPosition);

			// Fall back to any building
			var anyBuilding = world.Actors
				.FirstOrDefault(a => a.Owner == player && !a.IsDead && a.Info.HasTraitInfo<BuildingInfo>());

			if (anyBuilding != null)
				return world.Map.CellContaining(anyBuilding.CenterPosition);

			return null;
		}

		void ExportGameState()
		{
			try
			{
				// Check if a session is active by looking for the session marker file
				var sessionMarkerFile = Path.Combine(info.OutputDirectory, ".session_active");
				if (!File.Exists(sessionMarkerFile))
				{
					Log.Write("debug", "[GameStateExporter] Skipping export - no active session (marker file not found)");
					return;
				}

				// Auto-place any ready buildings before exporting state
				AutoPlaceReadyBuildings();

				if (!Directory.Exists(info.OutputDirectory))
					Directory.CreateDirectory(info.OutputDirectory);

				// Use single filename instead of timestamped files
				var filename = Path.Combine(info.OutputDirectory, "current_gamestate.txt");

				var totalSeconds = world.WorldTick / 25;
			var minutes = totalSeconds / 60;
			var seconds = totalSeconds % 60;

				var sb = new StringBuilder();
				sb.AppendLine("# Game State Snapshot");
				sb.AppendLine(CultureInfo.InvariantCulture, $"Time: {minutes:D2}:{seconds:D2}, Map: {world.Map.Title}");
				
				// Export game settings
				if (world.LobbyInfo?.GlobalSettings != null)
				{
					sb.AppendLine();
					sb.AppendLine("## Game Settings");
					var settings = world.LobbyInfo.GlobalSettings;
					
					// Render common lobby options in a meaningful way
					if (settings.LobbyOptions.Count > 0)
					{
						foreach (var option in settings.LobbyOptions.OrderBy(kv => kv.Key))
						{
							var description = GetFriendlyOptionDescription(option.Key, option.Value);
							if (description != null)
								sb.AppendLine(description);
						}
					}
					
				}
				
				sb.AppendLine();

				// Export player states
				var players = world.Players.Where(p => !p.NonCombatant && p.Playable).ToList();
				// Find Player 1 for visibility checks
				var player1 = players.FirstOrDefault(p => p.PlayerName == "Player1");
				
				// Start with Player 1's state prominently
				if (player1 != null)
				{
					sb.AppendLine();
					sb.AppendLine("# Player 1 (Human)");
					
					// Export Player 1's complete state first
					ExportPlayerState(sb, player1, world, true);
				}
				
				// Export other players' states
				var otherPlayers = players.Where(p => p != player1).ToList();
				if (otherPlayers.Count > 0)
				{
					sb.AppendLine();
					sb.AppendLine("# Other Players");
					
					foreach (var player in otherPlayers)
					{
						ExportPlayerState(sb, player, world, false);
					}
				}


				// Include orders since last snapshot as separate section
				var recentOrders = Network.HumanReadableOrderLogger.GetAndClearOrderBuffer();
				if (recentOrders.Count > 0)
				{
					var consolidatedOrders = ConsolidateOrders(recentOrders);
					if (consolidatedOrders.Count > 0)
					{
						sb.AppendLine();
						sb.AppendLine("# Recent Orders");
						sb.AppendLine("## Production Orders Since Last Snapshot");
						sb.AppendLine();
						
						var productionCount = recentOrders.Count(IsProductionOrder);
						sb.AppendLine(CultureInfo.InvariantCulture, $"*{productionCount} production orders shown (filtered from {recentOrders.Count} total orders)*");
						sb.AppendLine();
						
						foreach (var order in consolidatedOrders)
						{
							sb.AppendLine(order);
						}
					}
				}

				// Write to file
				File.WriteAllText(filename, sb.ToString());
			}
			catch (Exception e)
			{
				Log.Write("debug", $"Failed to export game state: {e.Message}");
			}
		}


		static string GetFriendlyOptionDescription(string optionId, OpenRA.Network.Session.LobbyOptionState optionState)
		{
			var value = optionState.Value ?? (optionState.IsEnabled ? "Enabled" : "Disabled");
			
			return optionId switch
			{
				"techlevel" => value switch
				{
					"unrestricted" => "Tech Level: Unrestricted (all units and superweapons available)",
					"medium" => "Tech Level: Medium (no superweapons or advanced units)",
					"low" => "Tech Level: Low (basic units only)",
					_ => $"Tech Level: {value}"
				},
				"superweapons" => optionState.IsEnabled 
					? "Superweapons: Enabled (Ion Cannon, Nuclear Missile available)"
					: "Superweapons: Disabled (no Ion Cannon or Nuclear Missile)",
				"allybuildradius" => optionState.IsEnabled
					? "Ally Build Radius: Can build near allied structures"
					: "Ally Build Radius: Cannot build near allied structures)",
				"allybuild" => optionState.IsEnabled
					? "Build Off Allies: Enabled (can build near allied structures)"
					: "Build Off Allies: Disabled",
				"buildradius" => optionState.IsEnabled
					? "Build Radius: Limited (must build near existing structures)"
					: "Build Radius: Unlimited (can build anywhere)",
				"shortgame" => optionState.IsEnabled
					? "Short Game: Enabled (destroy all enemy structures to win)"
					: "Short Game: Disabled (must destroy all enemy units and structures)",
				"fogofwar" => optionState.IsEnabled
					? "Fog of War: Enabled (unexplored areas hidden)"
					: "Fog of War: Disabled (entire map visible)",
				"fog" => optionState.IsEnabled
					? "Fog of War: Enabled (unexplored areas hidden)"
					: "Fog of War: Disabled (entire map visible)",
				"explore_map" => optionState.IsEnabled
					? "Map Explored: Yes (terrain visible, units still hidden)"
					: "Map Explored: No (must scout to see terrain)",
				"explored" => optionState.IsEnabled
					? "Explored Map: Yes (terrain visible, units still hidden)"
					: "Explored Map: No (must scout to see terrain)",
				"difficulty" => value switch
				{
					"easy" => "AI Difficulty: Easy",
					"normal" => "AI Difficulty: Normal",
					"hard" => "AI Difficulty: Hard",
					_ => $"AI Difficulty: {value}"
				},
				"kill_bounty" => optionState.IsEnabled
					? "Kill Bounties: Enabled (earn money for destroying enemies)"
					: "Kill Bounties: Disabled",
				"crates" => optionState.IsEnabled
					? "Crates: Enabled (bonus crates appear on map)"
					: "Crates: Disabled",
				"factundeploy" => optionState.IsEnabled
					? "Redeployable MCVs: Enabled (can pack/unpack Construction Yard)"
					: "Redeployable MCVs: Disabled",
				"timelimit" => value == "0" ? "Time Limit: None" : $"Time Limit: {value} minutes",
				"cheats" => null, // Hide cheats setting from output
				"gamespeed" => null, // Hide game speed setting from output
				"separateteamspawns" => null, // Hide separate team spawns setting from output
				"startingcash" => null, // Hide starting cash setting from output
				"startingunits" => null, // Hide starting units setting from output
				_ => $"{optionId.Replace('_', ' ').Replace('-', ' ')}: {value}"
			};
		}

		static string GetGameSpeedDescription(int netFrameInterval)
		{
			return netFrameInterval switch
			{
				1 => "Fastest",
				2 => "Faster",
				3 => "Normal",
				4 => "Slower",
				5 => "Slowest",
				_ => $"Custom ({netFrameInterval})"
			};
		}

		class OrderInfo
		{
			public string Timestamp { get; set; }
			public string Player { get; set; }
			public string OrderType { get; set; }
			public string UnitType { get; set; }
			public string UnitPosition { get; set; }
			public string Target { get; set; }
			public string FullLine { get; set; }
			public int ExtraActors { get; set; }
		}

		List<string> ConsolidateOrders(List<string> orders)
		{
			// First, filter to only production-related orders
			var productionOrders = orders.Where(IsProductionOrder).ToList();
			
			var result = new List<string>();
			var i = 0;
			
			while (i < productionOrders.Count)
			{
				var current = productionOrders[i];
				
				// Check if this is a CreateGroup order
				if (current.Contains("CreateGroup") && current.Contains("ExtraActors:"))
				{
					var createGroupInfo = ParseOrderLine(current);
					if (createGroupInfo != null && createGroupInfo.ExtraActors > 1)
					{
						// Look ahead for matching orders
						var groupedOrders = new List<OrderInfo>();
						var j = i + 1;
						
						// Collect all orders with same timestamp and player
						while (j < productionOrders.Count && groupedOrders.Count < createGroupInfo.ExtraActors)
						{
							var next = productionOrders[j];
							if (next.Contains("CreateGroup"))
								break; // Hit another CreateGroup, stop here
							
							var nextInfo = ParseOrderLine(next);
							if (nextInfo != null && 
								nextInfo.Timestamp == createGroupInfo.Timestamp && 
								nextInfo.Player == createGroupInfo.Player)
							{
								groupedOrders.Add(nextInfo);
								j++;
							}
							else
							{
								break;
							}
						}
						
						// If we found a complete group of orders, consolidate them
						if (groupedOrders.Count == createGroupInfo.ExtraActors && groupedOrders.Count > 1)
						{
							var consolidated = ConsolidateGroupOrders(createGroupInfo, groupedOrders);
							if (consolidated != null)
							{
								result.Add(consolidated);
								i = j; // Skip past all the orders we just consolidated
								continue;
							}
						}
					}
					
					// If we couldn't consolidate, skip the CreateGroup order
					i++;
					continue;
				}
				
				// If not a CreateGroup order or not consolidated, add as-is
				result.Add(current);
				i++;
			}
			
			return result;
		}

		OrderInfo ParseOrderLine(string orderLine)
		{
			try
			{
				// Parse "[13:22] Player1: Attack (From:Mammoth Tank@61009,48559,0)"
				// or "[13:22] Player1: CreateGroup (ExtraActors:14)"
				var info = new OrderInfo { FullLine = orderLine };
				
				// Extract timestamp [HH:MM]
				var timestampEnd = orderLine.IndexOf(']');
				if (timestampEnd > 0)
				{
					info.Timestamp = orderLine.Substring(1, timestampEnd - 1);
				}
				
				// Extract player name
				var playerStart = timestampEnd + 2;
				var colonIndex = orderLine.IndexOf(':', playerStart);
				if (colonIndex > playerStart)
				{
					info.Player = orderLine.Substring(playerStart, colonIndex - playerStart);
				}
				
				// Extract order type
				var orderStart = colonIndex + 2;
				var parenIndex = orderLine.IndexOf('(', orderStart);
				if (parenIndex > orderStart)
				{
					info.OrderType = orderLine.Substring(orderStart, parenIndex - orderStart - 1);
				}
				else
				{
					info.OrderType = orderLine.Substring(orderStart).Trim();
				}
				
				// Extract details within parentheses
				if (parenIndex >= 0)
				{
					var closeParenIndex = orderLine.LastIndexOf(')');
					if (closeParenIndex > parenIndex)
					{
						var details = orderLine.Substring(parenIndex + 1, closeParenIndex - parenIndex - 1);
						
						// Parse ExtraActors for CreateGroup
						if (details.Contains("ExtraActors:"))
						{
							var extraActorsStart = details.IndexOf("ExtraActors:") + 12;
							var nextSpace = details.IndexOf(' ', extraActorsStart);
							var extraActorsStr = nextSpace > 0 
								? details.Substring(extraActorsStart, nextSpace - extraActorsStart)
								: details.Substring(extraActorsStart);
							if (int.TryParse(extraActorsStr, out var extraActors))
								info.ExtraActors = extraActors;
						}
						
						// Parse From: unit type and position
						if (details.Contains("From:"))
						{
							var fromStart = details.IndexOf("From:") + 5;
							var atIndex = details.IndexOf('@', fromStart);
							if (atIndex > fromStart)
							{
								info.UnitType = details.Substring(fromStart, atIndex - fromStart);
								var nextSpace = details.IndexOf(' ', atIndex);
								if (nextSpace > 0)
									info.UnitPosition = details.Substring(atIndex + 1, nextSpace - atIndex - 1);
								else
									info.UnitPosition = details.Substring(atIndex + 1);
							}
						}
						
						// Parse Target:
						if (details.Contains("Target:"))
						{
							var targetStart = details.IndexOf("Target:") + 7;
							var nextSpace = details.IndexOf(" From:", targetStart);
							if (nextSpace > 0)
								info.Target = details.Substring(targetStart, nextSpace - targetStart);
							else
								info.Target = details.Substring(targetStart);
						}
					}
				}
				
				return info;
			}
			catch
			{
				return null;
			}
		}

		string ConsolidateGroupOrders(OrderInfo createGroup, List<OrderInfo> orders)
		{
			if (orders.Count == 0)
				return null;
			
			// Check if all orders are the same type
			var orderType = orders[0].OrderType;
			if (!orders.All(o => o.OrderType == orderType))
				return null; // Mixed order types, don't consolidate
			
			// Group units by type
			var unitGroups = orders
				.Where(o => !string.IsNullOrEmpty(o.UnitType))
				.GroupBy(o => o.UnitType)
				.Select(g => g.Count() > 1 ? $"{g.Count()} {g.Key}s" : g.Key)
				.ToList();
			
			if (unitGroups.Count == 0)
				return null;
			
			// Check if all have the same target
			var targets = orders.Where(o => !string.IsNullOrEmpty(o.Target)).Select(o => o.Target).Distinct().ToList();
			string targetStr;
			
			if (targets.Count == 0)
			{
				targetStr = "";
			}
			else if (targets.Count == 1)
			{
				targetStr = $" → {targets[0]}";
			}
			else
			{
				// Multiple targets, show count
				targetStr = $" → Multiple targets ({targets.Count} different)";
			}
			
			// Format consolidated output
			return $"[{createGroup.Timestamp}] {createGroup.Player}: Control Group {orderType} ({string.Join(", ", unitGroups)}){targetStr}";
		}
		
		bool IsProductionOrder(string orderLine)
		{
			if (string.IsNullOrEmpty(orderLine))
				return false;
			
			// Check if the order contains production-related commands
			// Format is typically: "[HH:MM] PlayerName: OrderType (params)"
			// We want to keep: StartProduction, CancelProduction, PauseProduction, PlaceBuilding
			
			// Extract the order type from the line
			var colonIndex = orderLine.IndexOf(']');
			if (colonIndex < 0)
				return false;
			
			var afterTimestamp = orderLine.Substring(colonIndex + 1);
			
			// Check for production-related order types
			return afterTimestamp.Contains(": StartProduction ") ||
			       afterTimestamp.Contains(": CancelProduction ") ||
			       afterTimestamp.Contains(": PauseProduction ") ||
			       afterTimestamp.Contains(": PlaceBuilding ");
		}
	}
}
