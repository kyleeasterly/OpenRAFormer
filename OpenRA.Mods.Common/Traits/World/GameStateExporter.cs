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
				sb.AppendLine("=== OpenRA Game State Snapshot ===");
				sb.AppendLine(CultureInfo.InvariantCulture, $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
				sb.AppendLine(CultureInfo.InvariantCulture, $"Game Tick: {world.WorldTick} (Time: {world.WorldTick / 25}s)");
				sb.AppendLine(CultureInfo.InvariantCulture, $"Map: {world.Map.Title} ({world.Map.Uid})");
				sb.AppendLine("Game Type: " + (world.LobbyInfo?.GlobalSettings?.ServerName ?? "Unknown"));
				sb.AppendLine();

				// Export player states
				var players = world.Players.Where(p => !p.NonCombatant && p.Playable).ToList();
				foreach (var player in players)
				{
					sb.AppendLine(CultureInfo.InvariantCulture, $"=== Player: {player.PlayerName} ({player.Faction.Name}) ===");

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
						sb.AppendLine("Economic Status:");
						sb.AppendLine(CultureInfo.InvariantCulture, $"  Cash: ${resources.Cash}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"  Stored Resources: {resources.Resources}/{resources.ResourceCapacity}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"  Total Value: ${resources.GetCashAndResources()}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"  Total Earned: ${resources.Earned}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"  Total Spent: ${resources.Spent}");
					}

					// Statistics
					var stats = player.PlayerActor.TraitOrDefault<PlayerStatistics>();
					if (stats != null)
					{
						sb.AppendLine();
						sb.AppendLine("Military Statistics:");
						sb.AppendLine(CultureInfo.InvariantCulture, $"  Army Value: ${stats.ArmyValue}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"  Total Assets Value: ${stats.AssetsValue}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"  Units Killed: {stats.UnitsKilled} (${stats.KillsCost} value)");
						sb.AppendLine(CultureInfo.InvariantCulture, $"  Units Lost: {stats.UnitsDead} (${stats.DeathsCost} value)");
						sb.AppendLine(CultureInfo.InvariantCulture, $"  Buildings Destroyed: {stats.BuildingsKilled}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"  Buildings Lost: {stats.BuildingsDead}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"  Experience Points: {stats.Experience}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"  Income Rate: ${stats.DisplayIncome}/min");
					}

					// Power
					var power = player.PlayerActor.TraitOrDefault<PowerManager>();
					if (power != null)
					{
						sb.AppendLine();
						sb.AppendLine("Power Status:");
						sb.AppendLine(CultureInfo.InvariantCulture, $"  Power Provided: {power.PowerProvided}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"  Power Consumed: {power.PowerDrained}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"  Power Balance: {power.PowerProvided - power.PowerDrained}");
						sb.AppendLine(CultureInfo.InvariantCulture, $"  Power State: {power.PowerState}");
					}

					// Units and Buildings
					var playerActors = world.Actors.Where(a => a.Owner == player && !a.IsDead).ToList();
					var buildings = playerActors.Where(a => a.Info.HasTraitInfo<BuildingInfo>()).ToList();
					var units = playerActors.Where(a => !a.Info.HasTraitInfo<BuildingInfo>()).ToList();

					sb.AppendLine();
					sb.AppendLine(CultureInfo.InvariantCulture, $"Unit Summary: {units.Count} units, {buildings.Count} buildings");

					// Group units by type
					var unitGroups = units.GroupBy(a => a.Info.Name).OrderByDescending(g => g.Count());
					sb.AppendLine("Units by Type:");
					foreach (var group in unitGroups)
					{
						var firstUnit = group.First();
						var valued = firstUnit.Info.TraitInfoOrDefault<ValuedInfo>();
						var cost = valued?.Cost ?? 0;
						var friendlyName = GetFriendlyUnitName(group.Key);
						sb.AppendLine(CultureInfo.InvariantCulture, $"  {friendlyName}: {group.Count()} units (${cost} each, ${cost * group.Count()} total)");
					}

					// Group buildings by type
					var buildingGroups = buildings.GroupBy(a => a.Info.Name).OrderByDescending(g => g.Count());
					sb.AppendLine();
					sb.AppendLine("Buildings by Type:");
					foreach (var group in buildingGroups)
					{
						var firstBuilding = group.First();
						var valued = firstBuilding.Info.TraitInfoOrDefault<ValuedInfo>();
						var cost = valued?.Cost ?? 0;
						var friendlyName = GetFriendlyBuildingName(group.Key);
						sb.AppendLine(CultureInfo.InvariantCulture, $"  {friendlyName}: {group.Count()} buildings (${cost} each, ${cost * group.Count()} total)");
					}

					// Production queues
					var queues = player.PlayerActor.TraitsImplementing<ProductionQueue>();
					var activeProduction = new List<string>();
					foreach (var queue in queues)
					{
						if (queue.CurrentItem() != null)
						{
							var item = queue.CurrentItem();
							var progress = item.RemainingCost == 0 ? 100 :
								(100 * (item.TotalCost - item.RemainingCost) / item.TotalCost);
							var friendlyName = world.Map.Rules.Actors[item.Item].TraitInfoOrDefault<BuildingInfo>() != null
								? GetFriendlyBuildingName(item.Item)
								: GetFriendlyUnitName(item.Item);
							activeProduction.Add($"{friendlyName} ({progress}% complete)");
						}
					}

					if (activeProduction.Count > 0)
					{
						sb.AppendLine();
						sb.AppendLine("Active Production:");
						foreach (var prod in activeProduction)
							sb.AppendLine(CultureInfo.InvariantCulture, $"  {prod}");
					}

					// Special units (harvesters, MCVs)
					var harvesters = world.ActorsWithTrait<Harvester>()
						.Where(tp => tp.Actor.Owner == player && !tp.Actor.IsDead)
						.Select(tp => tp.Actor).ToList();
					var mcvs = world.Actors
						.Where(a => a.Owner == player && !a.IsDead && a.Info.HasTraitInfo<BaseBuildingInfo>())
						.ToList();

					sb.AppendLine();
					sb.AppendLine("Special Units:");
					sb.AppendLine(CultureInfo.InvariantCulture, $"  Harvesters: {harvesters.Count}");
					sb.AppendLine(CultureInfo.InvariantCulture, $"  MCVs: {mcvs.Count}");

					sb.AppendLine();
				}

				// Map control analysis
				sb.AppendLine("=== Map Control Analysis ===");

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
	}
}
