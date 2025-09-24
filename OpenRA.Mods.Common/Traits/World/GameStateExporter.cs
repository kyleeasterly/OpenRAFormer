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

				var gameStateFiles = Directory.GetFiles(info.OutputDirectory, "gamestate_*.txt");
				if (gameStateFiles.Length == 0)
					return;

				var archiveDirectory = Path.Combine(info.OutputDirectory, "archive");
				if (!Directory.Exists(archiveDirectory))
					Directory.CreateDirectory(archiveDirectory);

				var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
				var gameArchiveDirectory = Path.Combine(archiveDirectory, $"game_{timestamp}");
				Directory.CreateDirectory(gameArchiveDirectory);

				foreach (var file in gameStateFiles)
				{
					var fileName = Path.GetFileName(file);
					var destinationPath = Path.Combine(gameArchiveDirectory, fileName);
					File.Move(file, destinationPath);
				}

				Log.Write("debug", $"Archived {gameStateFiles.Length} gamestate files to {gameArchiveDirectory}");
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
				foreach (var building in buildings)
				{
					buildingProduction[building] = new List<string>();
					var queues = building.TraitsImplementing<ProductionQueue>();
					foreach (var queue in queues)
					{
						var items = queue.AllQueued().ToList();
						if (items.Count > 0)
						{
							// Group items by type and status
							var itemGroups = items.GroupBy(item => new 
							{ 
								Name = item.Item,
								Status = item.Paused ? "PAUSED" : item.Done ? "READY" : "IN_PROGRESS"
							});
							
							foreach (var group in itemGroups)
							{
								var firstItem = group.First();
								var progress = firstItem.RemainingCost == 0 ? 100 :
									(100 * (firstItem.TotalCost - firstItem.RemainingCost) / firstItem.TotalCost);
								var friendlyName = world.Map.Rules.Actors[group.Key.Name].TraitInfoOrDefault<BuildingInfo>() != null
									? FriendlyNames.GetFriendlyBuildingName(group.Key.Name)
									: FriendlyNames.GetFriendlyUnitName(group.Key.Name);
								
								var count = group.Count();
								var countStr = count > 1 ? $" x{count}" : "";
								
								var itemStatus = group.Key.Status switch
								{
									"PAUSED" => " (PAUSED)",
									"READY" => " (READY)",
									_ => $" ({progress}% complete)"
								};
								
								buildingProduction[building].Add($"{friendlyName}{countStr}{itemStatus}");
							}
						}
					}
				}
				
				// Output buildings with positions and production
				foreach (var building in buildings.OrderBy(b => FriendlyNames.GetFriendlyBuildingName(b.Info.Name)))
				{
					var pos = building.CenterPosition;
					var cell = world.Map.CellContaining(pos);
					var friendlyName = FriendlyNames.GetFriendlyBuildingName(building.Info.Name);
					
					var productionInfo = buildingProduction[building].Count > 0 
						? $" [{string.Join(", ", buildingProduction[building])}]"
						: "";
						
					sb.AppendLine(CultureInfo.InvariantCulture, $"{friendlyName} ({cell.X},{cell.Y}){productionInfo}");
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

		void ExportGameState()
		{
			try
			{
				if (!Directory.Exists(info.OutputDirectory))
					Directory.CreateDirectory(info.OutputDirectory);

				var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
				var totalSeconds = world.WorldTick / 25;
			var minutes = totalSeconds / 60;
			var seconds = totalSeconds % 60;
			var filename = Path.Combine(info.OutputDirectory, $"gamestate_{timestamp}_{minutes:D2}m{seconds:D2}s.txt");

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
					sb.AppendLine();
					sb.AppendLine("# Recent Orders");
					
					var consolidatedOrders = ConsolidateOrders(recentOrders);
					foreach (var order in consolidatedOrders)
					{
						sb.AppendLine(order);
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
			var result = new List<string>();
			var i = 0;
			
			while (i < orders.Count)
			{
				var current = orders[i];
				
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
						while (j < orders.Count && groupedOrders.Count < createGroupInfo.ExtraActors)
						{
							var next = orders[j];
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
	}
}
