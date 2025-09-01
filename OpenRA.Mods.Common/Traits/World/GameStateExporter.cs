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
		public readonly int SnapshotInterval = 250; // 10 seconds

		[Desc("Enable or disable the exporter.")]
		public readonly bool Enabled = true;

		public override object Create(ActorInitializer init) { return new GameStateExporter(this); }
	}

	public class GameStateExporter : ITick
	{
		readonly GameStateExporterInfo info;
		World world;
		int lastSnapshotTick = 0;

		public GameStateExporter(GameStateExporterInfo info)
		{
			this.info = info;
		}

		void ITick.Tick(Actor self)
		{
			if (!info.Enabled)
				return;

			world = self.World;

			if (world.WorldTick - lastSnapshotTick >= info.SnapshotInterval)
			{
				lastSnapshotTick = world.WorldTick;
				ExportGameState();
			}
		}

		void ExportGameState()
		{
			try
			{
				if (!Directory.Exists(info.OutputDirectory))
					Directory.CreateDirectory(info.OutputDirectory);

				var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
				var filename = Path.Combine(info.OutputDirectory, $"gamestate_{timestamp}_tick{world.WorldTick}.txt");

				var sb = new StringBuilder();
				sb.AppendLine("# OpenRA Game State Snapshot");
				sb.AppendLine("## IMPORTANT: This report is for advising Player 1 (the human player)");
				sb.AppendLine();
				sb.AppendLine(CultureInfo.InvariantCulture, $"**Timestamp:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
				sb.AppendLine(CultureInfo.InvariantCulture, $"**Game Tick:** {world.WorldTick} (Time: {world.WorldTick / 25}s)");
				sb.AppendLine(CultureInfo.InvariantCulture, $"**Map:** {world.Map.Title}");
				sb.AppendLine("**Game Type:** " + (world.LobbyInfo?.GlobalSettings?.ServerName ?? "Unknown"));
				
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
							sb.AppendLine(description);
						}
					}
					
					sb.AppendLine(CultureInfo.InvariantCulture, $"Spectators: {(settings.AllowSpectators ? "Allowed" : "Not allowed")}");
					sb.AppendLine(CultureInfo.InvariantCulture, $"Game Speed: {GetGameSpeedDescription(settings.NetFrameInterval)}");
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
					sb.AppendLine("# YOUR PLAYER STATE (Player 1 - The Human Player You Are Advising)");
					sb.AppendLine("*This is the player you should provide strategy advice for*");
				}
				
				var isFirstOtherPlayer = true;
				foreach (var player in players)
				{
					// Add section header for other players
					if (player != player1 && isFirstOtherPlayer)
					{
						sb.AppendLine();
						sb.AppendLine("# OTHER PLAYERS (Opponents and Allies)");
						isFirstOtherPlayer = false;
					}
					var factionName = player.Faction.Name.Replace("faction-", "").Replace(".name", "");
					// Proper case for faction names
					if (factionName.Equals("nod", StringComparison.OrdinalIgnoreCase))
						factionName = "Nod";
					else if (factionName.Equals("gdi", StringComparison.OrdinalIgnoreCase))
						factionName = "GDI";
					
					var cleanPlayerName = player.PlayerName.Replace("bot-", "").Replace(".name", "");
					sb.AppendLine(CultureInfo.InvariantCulture, $"## Player: {cleanPlayerName} ({factionName})");

					// Show allies instead of teams
					var allies = players.Where(p => p != player && player.IsAlliedWith(p)).Select(p => p.PlayerName).ToList();
					sb.AppendLine("Allies: " + (allies.Count > 0 ? string.Join(", ", allies) : "None"));
					sb.AppendLine(CultureInfo.InvariantCulture, $"Is Local Player: {player == world.LocalPlayer}");
					sb.AppendLine(CultureInfo.InvariantCulture, $"Is Bot: {player.IsBot}");
					var status = player.WinState == WinState.Won ? "Won" : player.WinState == WinState.Lost ? "Lost" : "Playing";
					sb.AppendLine(CultureInfo.InvariantCulture, $"Status: {status}");

					// Resources
					var resources = player.PlayerActor.TraitOrDefault<PlayerResources>();
					if (resources != null)
					{
						sb.AppendLine();
						sb.AppendLine("### Economic Status");
						sb.AppendLine(CultureInfo.InvariantCulture, $"Cash: ${resources.Cash}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"Stored Resources: {resources.Resources}/{resources.ResourceCapacity}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"Total Value: ${resources.GetCashAndResources()}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"Total Earned: ${resources.Earned}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"Total Spent: ${resources.Spent}");
					}

					// Statistics
					var stats = player.PlayerActor.TraitOrDefault<PlayerStatistics>();
					if (stats != null)
					{
						sb.AppendLine();
						sb.AppendLine("### Military Statistics");
						sb.AppendLine(CultureInfo.InvariantCulture, $"Army Value: ${stats.ArmyValue}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"Total Assets Value: ${stats.AssetsValue}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"Units Killed: {stats.UnitsKilled} (${stats.KillsCost} value)");
						sb.AppendLine(CultureInfo.InvariantCulture, $"Units Lost: {stats.UnitsDead} (${stats.DeathsCost} value)");
						sb.AppendLine(CultureInfo.InvariantCulture, $"Buildings Destroyed: {stats.BuildingsKilled}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"Buildings Lost: {stats.BuildingsDead}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"Experience Points: {stats.Experience}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"Income Rate: ${stats.DisplayIncome}/min");
					}

					// Power
					var power = player.PlayerActor.TraitOrDefault<PowerManager>();
					if (power != null)
					{
						sb.AppendLine();
						sb.AppendLine("### Power Status");
						sb.AppendLine(CultureInfo.InvariantCulture, $"Power Provided: {power.PowerProvided}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"Power Consumed: {power.PowerDrained}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"Power Balance: {power.PowerProvided - power.PowerDrained}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"Power State: {power.PowerState}");
					}

					// Units and Buildings
					var playerActors = world.Actors.Where(a => a.Owner == player && !a.IsDead).ToList();
					var buildings = playerActors.Where(a => a.Info.HasTraitInfo<BuildingInfo>()).ToList();
					// Filter out C17 cargo planes and player actors as they're not player-controllable units
					var units = playerActors.Where(a => !a.Info.HasTraitInfo<BuildingInfo>() && 
						!string.Equals(a.Info.Name, "C17", StringComparison.OrdinalIgnoreCase) &&
						!string.Equals(a.Info.Name, "player", StringComparison.OrdinalIgnoreCase)).ToList();

					sb.AppendLine();
					sb.AppendLine(CultureInfo.InvariantCulture, $"### Unit Summary: {units.Count} units, {buildings.Count} buildings");

					// Group units by type
					var unitGroups = units.GroupBy(a => a.Info.Name).OrderByDescending(g => g.Count());
					sb.AppendLine("#### Units by Type:");
					foreach (var group in unitGroups)
					{
						var firstUnit = group.First();
						var valued = firstUnit.Info.TraitInfoOrDefault<ValuedInfo>();
						var cost = valued?.Cost ?? 0;
						var friendlyName = FriendlyNames.GetFriendlyUnitName(group.Key);
						sb.AppendLine(CultureInfo.InvariantCulture, $"{friendlyName}: {group.Count()} units (${cost} each, ${cost * group.Count()} total)");
					}

					// Group buildings by type
					var buildingGroups = buildings.GroupBy(a => a.Info.Name).OrderByDescending(g => g.Count());
					sb.AppendLine();
					sb.AppendLine("#### Buildings by Type:");
					foreach (var group in buildingGroups)
					{
						var firstBuilding = group.First();
						var valued = firstBuilding.Info.TraitInfoOrDefault<ValuedInfo>();
						var cost = valued?.Cost ?? 0;
						var friendlyName = FriendlyNames.GetFriendlyBuildingName(group.Key);
						sb.AppendLine(CultureInfo.InvariantCulture, $"{friendlyName}: {group.Count()} buildings (${cost} each, ${cost * group.Count()} total)");
					}

					// Building positions
					if (buildings.Count > 0)
					{
						sb.AppendLine();
						sb.AppendLine("#### Building Positions:");
						
						// Track unique positions to avoid duplicates (e.g., oil pumps)
						var buildingPositions = new Dictionary<string, HashSet<(int X, int Y)>>();
						
						foreach (var building in buildings)
						{
							var pos = building.CenterPosition;
							var cell = world.Map.CellContaining(pos);
							var friendlyName = FriendlyNames.GetFriendlyBuildingName(building.Info.Name);
							
							if (!buildingPositions.ContainsKey(friendlyName))
								buildingPositions[friendlyName] = new HashSet<(int, int)>();
							
							buildingPositions[friendlyName].Add((cell.X, cell.Y));
						}
						
						// Output unique positions only
						foreach (var kvp in buildingPositions.OrderBy(kv => kv.Key))
						{
							foreach (var position in kvp.Value.OrderBy(p => p.X).ThenBy(p => p.Y))
							{
								sb.AppendLine(CultureInfo.InvariantCulture, $"{kvp.Key} at ({position.X}, {position.Y})");
							}
						}
					}

					// Production queues - check all buildings for production queues
					var allQueues = new List<ProductionQueue>(player.PlayerActor.TraitsImplementing<ProductionQueue>());
					
					// Then check all buildings for queues
					foreach (var building in buildings)
					{
						allQueues.AddRange(building.TraitsImplementing<ProductionQueue>());
					}
					
					var hasProduction = false;
					foreach (var queue in allQueues)
					{
						var items = queue.AllQueued().ToList();
						if (items.Count > 0)
						{
							if (!hasProduction)
							{
								sb.AppendLine();
								sb.AppendLine("### Production Queues");
								hasProduction = true;
							}
							
							sb.AppendLine(CultureInfo.InvariantCulture, $"#### {queue.Info.Type} Queue:");
							
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
								
								sb.AppendLine(CultureInfo.InvariantCulture, $"{friendlyName}{countStr}{itemStatus}");
							}
						}
					}

					// Special units (harvesters, MCVs)
					var harvesters = world.ActorsWithTrait<Harvester>()
						.Where(tp => tp.Actor.Owner == player && !tp.Actor.IsDead)
						.Select(tp => tp.Actor).ToList();
					var mcvs = world.Actors
						.Where(a => a.Owner == player && !a.IsDead && a.Info.HasTraitInfo<BaseBuildingInfo>())
						.ToList();

					sb.AppendLine();
					sb.AppendLine("### Special Units");
					sb.AppendLine(CultureInfo.InvariantCulture, $"Harvesters: {harvesters.Count}");
					sb.AppendLine(CultureInfo.InvariantCulture, $"MCVs: {mcvs.Count}");

					sb.AppendLine();
				}

				// Visible enemy structures
				if (player1 != null)
				{
					var enemyBuildings = world.Actors
						.Where(a => a.Owner != player1 && 
								   !a.Owner.NonCombatant && 
								   a.Owner.Playable &&
								   !a.IsDead && 
								   a.Info.HasTraitInfo<BuildingInfo>() &&
								   a.CanBeViewedByPlayer(player1))
						.ToList();

					if (enemyBuildings.Count > 0)
					{
						sb.AppendLine();
						sb.AppendLine("## Enemy Structures Visible to Player 1");
						sb.AppendLine("*These are enemy buildings that Player 1 can currently see on the map*");
						
						var enemyBuildingsByPlayer = enemyBuildings.GroupBy(b => b.Owner);
						foreach (var playerGroup in enemyBuildingsByPlayer)
						{
							var enemyFactionName = playerGroup.Key.Faction.Name.Replace("faction-", "").Replace(".name", "");
							// Proper case for faction names
							if (enemyFactionName.Equals("nod", StringComparison.OrdinalIgnoreCase))
								enemyFactionName = "Nod";
							else if (enemyFactionName.Equals("gdi", StringComparison.OrdinalIgnoreCase))
								enemyFactionName = "GDI";
							
							sb.AppendLine(CultureInfo.InvariantCulture, $"### {playerGroup.Key.ResolvedPlayerName} ({enemyFactionName})");
							
							foreach (var building in playerGroup.OrderBy(b => FriendlyNames.GetFriendlyBuildingName(b.Info.Name)))
							{
								var pos = building.CenterPosition;
								var cell = world.Map.CellContaining(pos);
								var friendlyName = FriendlyNames.GetFriendlyBuildingName(building.Info.Name);
								sb.AppendLine(CultureInfo.InvariantCulture, $"{friendlyName} at ({cell.X}, {cell.Y})");
							}
						}
					}
				}

				// Map control analysis
				sb.AppendLine();
				sb.AppendLine("## Map Control Analysis");

				// Count resource cells on the map
				var resourceLayer = world.WorldActor.TraitOrDefault<IResourceLayer>();
				if (resourceLayer != null)
				{
					var totalResourceCells = 0;
					var bounds = world.Map.Bounds;
					for (var x = bounds.Left; x < bounds.Right; x++)
					{
						for (var y = bounds.Top; y < bounds.Bottom; y++)
						{
							var cell = new CPos(x, y);
							if (resourceLayer.GetResource(cell).Type != null)
								totalResourceCells++;
						}
					}

					sb.AppendLine(CultureInfo.InvariantCulture, $"Total Resource Cells: {totalResourceCells}");
				}

				// Include orders since last snapshot
				var recentOrders = Network.HumanReadableOrderLogger.GetAndClearOrderBuffer();
				if (recentOrders.Count > 0)
				{
					sb.AppendLine();
					sb.AppendLine("## Orders Since Last Snapshot");
					sb.AppendLine(CultureInfo.InvariantCulture, $"*{recentOrders.Count} orders recorded*");
					sb.AppendLine();
					foreach (var order in recentOrders)
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
				"startingunits" => value switch
				{
					"mcv" => "Starting Units: MCV only",
					"light" => "Starting Units: MCV + light forces",
					"heavy" => "Starting Units: MCV + heavy forces",
					_ => $"Starting Units: {value}"
				},
				"startingcash" => $"Starting Cash: ${value}",
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
				"C17-Stealth" => optionState.IsEnabled
					? "Stealth Deliveries: Enabled (airfield deliveries are cloaked)"
					: "Stealth Deliveries: Disabled",
				"gamespeed" => $"Game Speed: {value}",
				"separateteamspawns" => optionState.IsEnabled
					? "Separate Team Spawns: Enabled (teammates spawn together)"
					: "Separate Team Spawns: Disabled",
				"timelimit" => value == "0" ? "Time Limit: None" : $"Time Limit: {value} minutes",
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
	}
}
