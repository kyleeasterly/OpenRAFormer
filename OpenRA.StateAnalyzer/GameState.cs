using System;
using System.Collections.Generic;

namespace OpenRA.StateAnalyzer
{
	public class GameState
	{
		public DateTime Timestamp { get; set; }
		public int GameTick { get; set; }
		public string MapName { get; set; }
		public Dictionary<string, Player> Players { get; set; } = new();
		public Dictionary<string, List<EnemyBuilding>> VisibleEnemyStructures { get; set; } = new();
		public int TotalResourceCells { get; set; }
	}

	public class Player
	{
		public string Name { get; set; }
		public string Faction { get; set; }
		public bool IsLocalPlayer { get; set; }
		public bool IsBot { get; set; }
		public string Status { get; set; }
		public List<string> Allies { get; set; } = new();

		// Economic
		public int Cash { get; set; }
		public int StoredResources { get; set; }
		public int ResourceCapacity { get; set; }
		public int TotalValue { get; set; }
		public int TotalEarned { get; set; }
		public int TotalSpent { get; set; }

		// Military
		public int ArmyValue { get; set; }
		public int TotalAssetsValue { get; set; }
		public int UnitsKilled { get; set; }
		public int KillsCost { get; set; }
		public int UnitsLost { get; set; }
		public int DeathsCost { get; set; }
		public int BuildingsDestroyed { get; set; }
		public int BuildingsLost { get; set; }
		public int Experience { get; set; }
		public int IncomeRate { get; set; }

		// Power
		public int PowerProvided { get; set; }
		public int PowerConsumed { get; set; }
		public int PowerBalance { get; set; }
		public string PowerState { get; set; }

		// Units and buildings
		public int UnitCount { get; set; }
		public int BuildingCount { get; set; }
		public Dictionary<string, UnitGroup> Units { get; set; } = new();
		public Dictionary<string, BuildingGroup> Buildings { get; set; } = new();
		public List<BuildingPosition> BuildingPositions { get; set; } = new();

		// Production
		public Dictionary<string, List<ProductionItem>> ProductionQueues { get; set; } = new();

		// Special units
		public int HarvesterCount { get; set; }
		public int McvCount { get; set; }
	}

	public class UnitGroup
	{
		public string Type { get; set; }
		public int Count { get; set; }
		public int CostPer { get; set; }
		public int TotalCost { get; set; }
	}

	public class BuildingGroup
	{
		public string Type { get; set; }
		public int Count { get; set; }
		public int CostPer { get; set; }
		public int TotalCost { get; set; }
	}

	public class BuildingPosition
	{
		public string Type { get; set; }
		public int X { get; set; }
		public int Y { get; set; }
	}

	public class ProductionItem
	{
		public string Type { get; set; }
		public string Status { get; set; }
		public int Count { get; set; }
		public int Progress { get; set; }
	}

	public class EnemyBuilding
	{
		public string Player { get; set; }
		public string Faction { get; set; }
		public string Type { get; set; }
		public int X { get; set; }
		public int Y { get; set; }
	}
}