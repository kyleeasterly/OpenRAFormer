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
					
					sb.AppendLine($"Spectators: {(settings.AllowSpectators ? "Allowed" : "Not allowed")}");
					sb.AppendLine($"Game Speed: {GetGameSpeedDescription(settings.NetFrameInterval)}");
				}
				
				sb.AppendLine();

				// Export player states
				var players = world.Players.Where(p => !p.NonCombatant && p.Playable).ToList();
				// Find Player 1 for visibility checks
				var player1 = players.FirstOrDefault(p => p.PlayerName == "Player1");
				foreach (var player in players)
				{
					var factionName = player.Faction.Name.Replace("faction-", "").Replace(".name", "").ToUpperInvariant();
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
					var units = playerActors.Where(a => !a.Info.HasTraitInfo<BuildingInfo>()).ToList();

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
						var friendlyName = GetFriendlyUnitName(group.Key);
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
						var friendlyName = GetFriendlyBuildingName(group.Key);
						sb.AppendLine(CultureInfo.InvariantCulture, $"{friendlyName}: {group.Count()} buildings (${cost} each, ${cost * group.Count()} total)");
					}

					// Building positions
					if (buildings.Count > 0)
					{
						sb.AppendLine();
						sb.AppendLine("#### Building Positions:");
						foreach (var building in buildings.OrderBy(b => GetFriendlyBuildingName(b.Info.Name)))
						{
							var pos = building.CenterPosition;
							var cell = world.Map.CellContaining(pos);
							var friendlyName = GetFriendlyBuildingName(building.Info.Name);
							sb.AppendLine(CultureInfo.InvariantCulture, $"{friendlyName} at ({cell.X}, {cell.Y})");
						}
					}

					// Production queues
					var queues = player.PlayerActor.TraitsImplementing<ProductionQueue>();
					var hasProduction = false;
					
					foreach (var queue in queues)
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
							
							sb.AppendLine($"#### {queue.Info.Type} Queue:");
							foreach (var item in items)
							{
								var progress = item.RemainingCost == 0 ? 100 :
									(100 * (item.TotalCost - item.RemainingCost) / item.TotalCost);
								var friendlyName = world.Map.Rules.Actors[item.Item].TraitInfoOrDefault<BuildingInfo>() != null
									? GetFriendlyBuildingName(item.Item)
									: GetFriendlyUnitName(item.Item);
								var itemStatus = item.Paused ? " (PAUSED)" : item.Done ? " (READY)" : $" ({progress}% complete)";
								sb.AppendLine(CultureInfo.InvariantCulture, $"{friendlyName}{itemStatus}");
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
						sb.AppendLine("## Visible Enemy Structures");
						
						var enemyBuildingsByPlayer = enemyBuildings.GroupBy(b => b.Owner);
						foreach (var playerGroup in enemyBuildingsByPlayer)
						{
							var enemyFactionName = playerGroup.Key.Faction.Name.Replace("faction-", "").Replace(".name", "").ToUpperInvariant();
							var enemyPlayerName = playerGroup.Key.PlayerName.Replace("bot-", "").Replace(".name", "");
							sb.AppendLine(CultureInfo.InvariantCulture, $"### {enemyPlayerName} ({enemyFactionName})");
							
							foreach (var building in playerGroup.OrderBy(b => GetFriendlyBuildingName(b.Info.Name)))
							{
								var pos = building.CenterPosition;
								var cell = world.Map.CellContaining(pos);
								var friendlyName = GetFriendlyBuildingName(building.Info.Name);
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

				// Write to file
				File.WriteAllText(filename, sb.ToString());
			}
			catch (Exception e)
			{
				Log.Write("debug", $"Failed to export game state: {e.Message}");
			}
		}

		static string GetFriendlyBuildingName(string internalName)
		{
			return internalName.ToUpperInvariant() switch
			{
				"FACT" => "Construction Yard",
				"NUKE" => "Power Plant",
				"NUK2" => "Advanced Power Plant",
				"PROC" => "Refinery",
				"SILO" => "Tiberium Silo",
				"PYLE" => "Barracks (GDI)",
				"HAND" => "Hand of Nod (Barracks)",
				"WEAP" => "War Factory",
				"AFLD" => "Airfield",
				"HPAD" => "Helipad",
				"EYE" => "Advanced Communications Center",
				"TMPL" => "Temple of Nod",
				"GTWR" => "Guard Tower",
				"ATWR" => "Advanced Guard Tower",
				"OBLI" => "Obelisk of Light",
				"GUN" => "Turret",
				"SAM" => "SAM Site",
				"HQ" => "Communications Center",
				"FIX" => "Repair Bay",
				"HBOX" => "Pillbox",
				_ => internalName
			};
		}

		static string GetFriendlyUnitName(string internalName)
		{
			return internalName.ToUpperInvariant() switch
			{
				"MCV" => "Mobile Construction Vehicle",
				"HARV" => "Harvester",
				"APC" => "Armored Personnel Carrier",
				"ARTY" => "Artillery",
				"FTNK" => "Flame Tank",
				"BGGY" => "Nod Buggy",
				"BIKE" => "Recon Bike",
				"JEEP" => "Humvee",
				"LTNK" => "Light Tank",
				"MTNK" => "Medium Tank",
				"HTNK" => "Mammoth Tank",
				"MSAM" => "Rocket Launcher",
				"MLRS" => "Mobile Rocket Launch System",
				"STNK" => "Stealth Tank",
				"TRAN" => "Chinook Transport",
				"HELI" => "Apache Attack Helicopter",
				"ORCA" => "Orca VTOL",
				"E1" => "Minigunner",
				"E2" => "Grenadier",
				"E3" => "Rocket Soldier",
				"E4" => "Flamethrower Infantry",
				"E5" => "Chemical Warrior",
				"E6" => "Engineer",
				"RMBO" => "Commando",
				"C17" => "C17 Cargo Plane",
				"A10" => "A10 Warthog",
				_ => internalName
			};
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
					: "Ally Build Radius: Cannot build near allied structures",
				"buildradius" => optionState.IsEnabled
					? "Build Radius: Limited (must build near existing structures)"
					: "Build Radius: Unlimited (can build anywhere)",
				"shortgame" => optionState.IsEnabled
					? "Short Game: Enabled (destroy all enemy structures to win)"
					: "Short Game: Disabled (must destroy all enemy units and structures)",
				"fogofwar" => optionState.IsEnabled
					? "Fog of War: Enabled (unexplored areas hidden)"
					: "Fog of War: Disabled (entire map visible)",
				"explore_map" => optionState.IsEnabled
					? "Map Explored: Yes (terrain visible, units still hidden)"
					: "Map Explored: No (must scout to see terrain)",
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
