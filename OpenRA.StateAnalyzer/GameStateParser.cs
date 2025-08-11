using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OpenRA.StateAnalyzer
{
	public class GameStateParser
	{
		public async Task<GameState> ParseAsync(string filePath)
		{
			var content = await File.ReadAllTextAsync(filePath);
			var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
			
			var state = new GameState();
			
			for (var i = 0; i < lines.Length; i++)
			{
				var line = lines[i].Trim();
				
				if (line.StartsWith("**Timestamp:**"))
				{
					var timestampStr = line.Substring("**Timestamp:**".Length).Trim();
					if (DateTime.TryParseExact(timestampStr, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
						state.Timestamp = timestamp;
				}
				else if (line.StartsWith("**Game Tick:**"))
				{
					var tickMatch = Regex.Match(line, @"(\d+)");
					if (tickMatch.Success && int.TryParse(tickMatch.Value, out var tick))
						state.GameTick = tick;
				}
				else if (line.StartsWith("**Map:**"))
				{
					state.MapName = line.Substring("**Map:**".Length).Trim();
				}
				else if (line.StartsWith("## Player:"))
				{
					i = ParsePlayer(lines, i, state);
				}
				else if (line.StartsWith("## Visible Enemy Structures"))
				{
					i = ParseEnemyStructures(lines, i, state);
				}
				else if (line.StartsWith("Total Resource Cells:"))
				{
					var match = Regex.Match(line, @"(\d+)");
					if (match.Success && int.TryParse(match.Value, out var cells))
						state.TotalResourceCells = cells;
				}
			}
			
			return state;
		}

		int ParsePlayer(string[] lines, int startIndex, GameState state)
		{
			var i = startIndex;
			var headerLine = lines[i].Trim();
			
			// Parse player name and faction from "## Player: PlayerName (Faction)"
			var playerMatch = Regex.Match(headerLine, @"## Player: (.+?) \((.+?)\)");
			if (!playerMatch.Success)
				return i;

			var player = new Player
			{
				Name = playerMatch.Groups[1].Value,
				Faction = playerMatch.Groups[2].Value
			};

			i++;
			while (i < lines.Length && !lines[i].StartsWith("## "))
			{
				var line = lines[i].Trim();
				
				if (line.StartsWith("Allies:"))
				{
					var allies = line.Substring("Allies:".Length).Trim();
					if (allies != "None")
						player.Allies = allies.Split(',').Select(a => a.Trim()).ToList();
				}
				else if (line.StartsWith("Is Local Player:"))
				{
					player.IsLocalPlayer = line.Contains("True");
				}
				else if (line.StartsWith("Is Bot:"))
				{
					player.IsBot = line.Contains("True");
				}
				else if (line.StartsWith("Status:"))
				{
					player.Status = line.Substring("Status:".Length).Trim();
				}
				else if (line.StartsWith("Cash:"))
				{
					player.Cash = ExtractIntValue(line, "Cash:");
				}
				else if (line.StartsWith("Stored Resources:"))
				{
					var match = Regex.Match(line, @"(\d+)/(\d+)");
					if (match.Success)
					{
						int.TryParse(match.Groups[1].Value, out var storedResources);
						int.TryParse(match.Groups[2].Value, out var resourceCapacity);
						player.StoredResources = storedResources;
						player.ResourceCapacity = resourceCapacity;
					}
				}
				else if (line.StartsWith("Total Value:"))
				{
					player.TotalValue = ExtractIntValue(line, "Total Value:");
				}
				else if (line.StartsWith("Total Earned:"))
				{
					player.TotalEarned = ExtractIntValue(line, "Total Earned:");
				}
				else if (line.StartsWith("Total Spent:"))
				{
					player.TotalSpent = ExtractIntValue(line, "Total Spent:");
				}
				else if (line.StartsWith("Army Value:"))
				{
					player.ArmyValue = ExtractIntValue(line, "Army Value:");
				}
				else if (line.StartsWith("Total Assets Value:"))
				{
					player.TotalAssetsValue = ExtractIntValue(line, "Total Assets Value:");
				}
				else if (line.StartsWith("Units Killed:"))
				{
					var match = Regex.Match(line, @"(\d+) \(\$(\d+) value\)");
					if (match.Success)
					{
						int.TryParse(match.Groups[1].Value, out var unitsKilled);
						int.TryParse(match.Groups[2].Value, out var killsCost);
						player.UnitsKilled = unitsKilled;
						player.KillsCost = killsCost;
					}
				}
				else if (line.StartsWith("Units Lost:"))
				{
					var match = Regex.Match(line, @"(\d+) \(\$(\d+) value\)");
					if (match.Success)
					{
						int.TryParse(match.Groups[1].Value, out var unitsLost);
						int.TryParse(match.Groups[2].Value, out var deathsCost);
						player.UnitsLost = unitsLost;
						player.DeathsCost = deathsCost;
					}
				}
				else if (line.StartsWith("Buildings Destroyed:"))
				{
					player.BuildingsDestroyed = ExtractIntValue(line, "Buildings Destroyed:");
				}
				else if (line.StartsWith("Buildings Lost:"))
				{
					player.BuildingsLost = ExtractIntValue(line, "Buildings Lost:");
				}
				else if (line.StartsWith("Experience Points:"))
				{
					player.Experience = ExtractIntValue(line, "Experience Points:");
				}
				else if (line.StartsWith("Income Rate:"))
				{
					player.IncomeRate = ExtractIntValue(line, "Income Rate:");
				}
				else if (line.StartsWith("Power Provided:"))
				{
					player.PowerProvided = ExtractIntValue(line, "Power Provided:");
				}
				else if (line.StartsWith("Power Consumed:"))
				{
					player.PowerConsumed = ExtractIntValue(line, "Power Consumed:");
				}
				else if (line.StartsWith("Power Balance:"))
				{
					player.PowerBalance = ExtractIntValue(line, "Power Balance:");
				}
				else if (line.StartsWith("Power State:"))
				{
					player.PowerState = line.Substring("Power State:".Length).Trim();
				}
				else if (line.StartsWith("### Unit Summary:"))
				{
					var match = Regex.Match(line, @"(\d+) units, (\d+) buildings");
					if (match.Success)
					{
						int.TryParse(match.Groups[1].Value, out var unitCount);
						int.TryParse(match.Groups[2].Value, out var buildingCount);
						player.UnitCount = unitCount;
						player.BuildingCount = buildingCount;
					}
				}
				else if (line.StartsWith("#### Units by Type:"))
				{
					i = ParseUnitsByType(lines, i + 1, player);
					continue;
				}
				else if (line.StartsWith("#### Buildings by Type:"))
				{
					i = ParseBuildingsByType(lines, i + 1, player);
					continue;
				}
				else if (line.StartsWith("#### Building Positions:"))
				{
					i = ParseBuildingPositions(lines, i + 1, player);
					continue;
				}
				else if (line.StartsWith("### Production Queues"))
				{
					i = ParseProductionQueues(lines, i + 1, player);
					continue;
				}
				else if (line.StartsWith("Harvesters:"))
				{
					player.HarvesterCount = ExtractIntValue(line, "Harvesters:");
				}
				else if (line.StartsWith("MCVs:"))
				{
					player.McvCount = ExtractIntValue(line, "MCVs:");
				}
				
				i++;
			}
			
			state.Players[player.Name] = player;
			return i - 1;
		}

		int ParseUnitsByType(string[] lines, int startIndex, Player player)
		{
			var i = startIndex;
			while (i < lines.Length && !lines[i].StartsWith("#") && !string.IsNullOrWhiteSpace(lines[i]))
			{
				var line = lines[i].Trim();
				// Format: "Unit Name: X units ($Y each, $Z total)"
				var match = Regex.Match(line, @"(.+?): (\d+) units \(\$(\d+) each, \$(\d+) total\)");
				if (match.Success)
				{
					var unitGroup = new UnitGroup
					{
						Type = match.Groups[1].Value,
						Count = int.Parse(match.Groups[2].Value),
						CostPer = int.Parse(match.Groups[3].Value),
						TotalCost = int.Parse(match.Groups[4].Value)
					};
					player.Units[unitGroup.Type] = unitGroup;
				}
				i++;
			}
			return i - 1;
		}

		int ParseBuildingsByType(string[] lines, int startIndex, Player player)
		{
			var i = startIndex;
			while (i < lines.Length && !lines[i].StartsWith("#") && !string.IsNullOrWhiteSpace(lines[i]))
			{
				var line = lines[i].Trim();
				// Format: "Building Name: X buildings ($Y each, $Z total)"
				var match = Regex.Match(line, @"(.+?): (\d+) buildings \(\$(\d+) each, \$(\d+) total\)");
				if (match.Success)
				{
					var buildingGroup = new BuildingGroup
					{
						Type = match.Groups[1].Value,
						Count = int.Parse(match.Groups[2].Value),
						CostPer = int.Parse(match.Groups[3].Value),
						TotalCost = int.Parse(match.Groups[4].Value)
					};
					player.Buildings[buildingGroup.Type] = buildingGroup;
				}
				i++;
			}
			return i - 1;
		}

		int ParseBuildingPositions(string[] lines, int startIndex, Player player)
		{
			var i = startIndex;
			while (i < lines.Length && !lines[i].StartsWith("#") && !string.IsNullOrWhiteSpace(lines[i]))
			{
				var line = lines[i].Trim();
				// Format: "Building Name at (X, Y)"
				var match = Regex.Match(line, @"(.+?) at \((\d+), (\d+)\)");
				if (match.Success)
				{
					var position = new BuildingPosition
					{
						Type = match.Groups[1].Value,
						X = int.Parse(match.Groups[2].Value),
						Y = int.Parse(match.Groups[3].Value)
					};
					player.BuildingPositions.Add(position);
				}
				i++;
			}
			return i - 1;
		}

		int ParseProductionQueues(string[] lines, int startIndex, Player player)
		{
			var i = startIndex;
			string currentQueueType = null;
			
			while (i < lines.Length && !lines[i].StartsWith("### ") && !lines[i].StartsWith("## "))
			{
				var line = lines[i].Trim();
				
				if (line.StartsWith("#### ") && line.EndsWith(" Queue:"))
				{
					currentQueueType = line.Substring(4, line.Length - 11).Trim();
					player.ProductionQueues[currentQueueType] = new List<ProductionItem>();
				}
				else if (currentQueueType != null && !string.IsNullOrWhiteSpace(line))
				{
					// Parse production items
					var item = ParseProductionItem(line);
					if (item != null)
						player.ProductionQueues[currentQueueType].Add(item);
				}
				
				i++;
			}
			
			return i - 1;
		}

		ProductionItem ParseProductionItem(string line)
		{
			// Format examples:
			// "Unit Name (50% complete)"
			// "Unit Name x3 (READY)"
			// "Unit Name (PAUSED)"
			
			var item = new ProductionItem { Count = 1 };
			
			// Check for count multiplier
			var countMatch = Regex.Match(line, @"(.+?) x(\d+) (.+)");
			if (countMatch.Success)
			{
				item.Type = countMatch.Groups[1].Value;
				item.Count = int.Parse(countMatch.Groups[2].Value);
				var statusPart = countMatch.Groups[3].Value;
				(item.Status, item.Progress) = ExtractStatus(statusPart);
			}
			else
			{
				var statusMatch = Regex.Match(line, @"(.+?) (.+)");
				if (statusMatch.Success)
				{
					item.Type = statusMatch.Groups[1].Value;
					var statusPart = statusMatch.Groups[2].Value;
					(item.Status, item.Progress) = ExtractStatus(statusPart);
				}
			}
			
			return item.Type != null ? item : null;
		}

		(string Status, int Progress) ExtractStatus(string statusPart)
		{
			var progress = 0;
			
			if (statusPart.Contains("READY"))
				return ("READY", 100);
			if (statusPart.Contains("PAUSED"))
				return ("PAUSED", 0);
			
			var progressMatch = Regex.Match(statusPart, @"\((\d+)% complete\)");
			if (progressMatch.Success)
			{
				int.TryParse(progressMatch.Groups[1].Value, out progress);
				return ("IN_PROGRESS", progress);
			}
			
			return ("UNKNOWN", 0);
		}

		int ParseEnemyStructures(string[] lines, int startIndex, GameState state)
		{
			var i = startIndex + 1;
			string currentPlayer = null;
			string currentFaction = null;
			
			while (i < lines.Length && !lines[i].StartsWith("## "))
			{
				var line = lines[i].Trim();
				
				if (line.StartsWith("### "))
				{
					// Parse "### PlayerName (Faction)"
					var playerMatch = Regex.Match(line, @"### (.+?) \((.+?)\)");
					if (playerMatch.Success)
					{
						currentPlayer = playerMatch.Groups[1].Value;
						currentFaction = playerMatch.Groups[2].Value;
						state.VisibleEnemyStructures[currentPlayer] = new List<EnemyBuilding>();
					}
				}
				else if (currentPlayer != null && !string.IsNullOrWhiteSpace(line))
				{
					// Parse "Building Name at (X, Y)"
					var buildingMatch = Regex.Match(line, @"(.+?) at \((\d+), (\d+)\)");
					if (buildingMatch.Success)
					{
						var building = new EnemyBuilding
						{
							Player = currentPlayer,
							Faction = currentFaction,
							Type = buildingMatch.Groups[1].Value,
							X = int.Parse(buildingMatch.Groups[2].Value),
							Y = int.Parse(buildingMatch.Groups[3].Value)
						};
						state.VisibleEnemyStructures[currentPlayer].Add(building);
					}
				}
				
				i++;
			}
			
			return i - 1;
		}

		static int ExtractIntValue(string line, string prefix)
		{
			var valueStr = line.Substring(prefix.Length).Trim().Replace("$", "").Replace("/min", "");
			var match = Regex.Match(valueStr, @"(\d+)");
			if (match.Success && int.TryParse(match.Value, out var value))
				return value;
			return 0;
		}
	}
}