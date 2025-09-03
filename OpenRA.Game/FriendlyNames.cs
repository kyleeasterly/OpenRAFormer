#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenRA
{
	public static class FriendlyNames
	{
		// HashSet for O(1) lookups when checking if something is a building
		// Using lowercase since actor names are stored in lowercase
		public static readonly HashSet<string> MeanBuildingNames = new()
		{
			"fact", "nuke", "nuk2", "proc", "silo", "pyle", "hand",
			"weap", "afld", "hpad", "eye", "tmpl", "gtwr", "atwr",
			"obli", "gun", "sam", "hq", "fix", "hbox", "v19",
			"sbag", "cycl", "brik", "bio", "hosp", "miss", "arco"
		};

		// Dictionary for O(1) friendly name lookups
		// Keys are lowercase since actor names are stored in lowercase
		static readonly Dictionary<string, string> BuildingNameMap = new()
		{
			["fact"] = "Construction Yard",
			["nuke"] = "Power Plant",
			["nuk2"] = "Advanced Power Plant",
			["proc"] = "Refinery",
			["silo"] = "Tiberium Silo",
			["pyle"] = "Barracks (GDI)",
			["hand"] = "Hand of Nod (Barracks)",
			["weap"] = "War Factory",
			["afld"] = "Airfield",
			["hpad"] = "Helipad",
			["eye"] = "Advanced Communications Center",
			["tmpl"] = "Temple of Nod",
			["gtwr"] = "Guard Tower",
			["atwr"] = "Advanced Guard Tower",
			["obli"] = "Obelisk of Light",
			["gun"] = "Turret",
			["sam"] = "SAM Site",
			["hq"] = "Communications Center",
			["fix"] = "Repair Bay",
			["hbox"] = "Pillbox",
			["v19"] = "Oil Pump",
			["sbag"] = "Sandbag Wall",
			["cycl"] = "Chain Link Fence",
			["brik"] = "Concrete Wall",
			["bio"] = "Biological Research Facility",
			["hosp"] = "Hospital",
			["miss"] = "Technology Center",
			["arco"] = "Oil Pump"
		};

		static readonly Dictionary<string, string> UnitNameMap = new()
		{
			["mcv"] = "Mobile Construction Vehicle",
			["harv"] = "Harvester",
			["apc"] = "Armored Personnel Carrier",
			["arty"] = "Artillery",
			["ftnk"] = "Flame Tank",
			["bggy"] = "Nod Buggy",
			["bike"] = "Recon Bike",
			["jeep"] = "Humvee",
			["ltnk"] = "Light Tank",
			["mtnk"] = "Medium Tank",
			["htnk"] = "Mammoth Tank",
			["msam"] = "Rocket Launcher",
			["mlrs"] = "Mobile Rocket Launch System",
			["stnk"] = "Stealth Tank",
			["tran"] = "Chinook Transport",
			["heli"] = "Apache Attack Helicopter",
			["orca"] = "Orca VTOL",
			["e1"] = "Minigunner",
			["e2"] = "Grenadier",
			["e3"] = "Rocket Soldier",
			["e4"] = "Flamethrower Infantry",
			["e5"] = "Chemical Warrior",
			["e6"] = "Engineer",
			["rmbo"] = "Commando",
			["a10"] = "A10 Warthog",
			["c17"] = "Supply Aircraft",
			["truck"] = "Supply Truck",
			["mhq"] = "Mobile HQ",
			["boat"] = "Gunboat",
			["lst"] = "Landing Craft"
		};

		// Reverse mappings for converting friendly names to internal names
		// Built lazily on first access for performance
		static readonly Lazy<Dictionary<string, string>> ReverseBuildingMap = new Lazy<Dictionary<string, string>>(() =>
			BuildingNameMap.ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase));

		static readonly Lazy<Dictionary<string, string>> ReverseUnitMap = new Lazy<Dictionary<string, string>>(() =>
			UnitNameMap.ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase));

		public static string GetFriendlyBuildingName(string internalName)
		{
			// No need for ToUpperInvariant since names are already lowercase
			// Try dictionary lookup first for O(1) performance
			if (BuildingNameMap.TryGetValue(internalName, out var friendlyName))
				return friendlyName;
			
			return internalName;
		}

		public static string GetFriendlyUnitName(string internalName)
		{
			// No need for ToUpperInvariant since names are already lowercase
			// Try dictionary lookup first for O(1) performance
			if (UnitNameMap.TryGetValue(internalName, out var friendlyName))
				return friendlyName;
			
			return internalName;
		}

		// Convert friendly name or internal name to the proper internal name for use in orders
		public static string GetInternalActorName(string input)
		{
			// Handle null/empty
			if (string.IsNullOrWhiteSpace(input))
				return input;

			// First check if it's already a known internal name (case-insensitive)
			var lowerInput = input.ToLowerInvariant();
			
			// Check buildings
			if (BuildingNameMap.ContainsKey(lowerInput))
				return lowerInput.ToUpperInvariant(); // C&C uses uppercase internal names
			
			// Check units
			if (UnitNameMap.ContainsKey(lowerInput))
				return lowerInput.ToUpperInvariant(); // C&C uses uppercase internal names

			// Now check if it's a friendly name that needs conversion
			// Check building friendly names
			if (ReverseBuildingMap.Value.TryGetValue(input, out var internalBuilding))
				return internalBuilding.ToUpperInvariant(); // C&C uses uppercase
			
			// Check unit friendly names
			if (ReverseUnitMap.Value.TryGetValue(input, out var internalUnit))
				return internalUnit.ToUpperInvariant(); // C&C uses uppercase

			// Additional common variations and shortcuts
			var shortcuts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				// Common abbreviations and variations
				["PowerPlant"] = "NUKE",
				["Power Plant"] = "NUKE",
				["PP"] = "NUKE",
				["AdvPowerPlant"] = "NUK2",
				["Advanced Power Plant"] = "NUK2",
				["APP"] = "NUK2",
				["Refinery"] = "PROC",
				["Ref"] = "PROC",
				["Tiberium Refinery"] = "PROC",
				["Barracks"] = "PYLE",
				["Bar"] = "PYLE",
				["GDI Barracks"] = "PYLE",
				["Hand of Nod"] = "HAND",
				["Hand"] = "HAND",
				["Nod Barracks"] = "HAND",
				["War Factory"] = "WEAP",
				["Weapons Factory"] = "WEAP",
				["WF"] = "WEAP",
				["Factory"] = "WEAP",
				["Construction Yard"] = "FACT",
				["CY"] = "FACT",
				["ConYard"] = "FACT",
				["Silo"] = "SILO",
				["Storage"] = "SILO",
				
				// Units
				["MCV"] = "MCV",
				["Mobile Construction Vehicle"] = "MCV",
				["Harvester"] = "HARV",
				["Harv"] = "HARV",
				["Minigunner"] = "E1",
				["Rifle"] = "E1",
				["Grenadier"] = "E2",
				["Rocket Soldier"] = "E3",
				["Rocket"] = "E3",
				["Bazooka"] = "E3",
				["Engineer"] = "E6",
				["Engi"] = "E6",
				["Eng"] = "E6",
				["Commando"] = "RMBO",
				["Humvee"] = "JEEP",
				["Hummer"] = "JEEP",
				["APC"] = "APC",
				["Medium Tank"] = "MTNK",
				["Med Tank"] = "MTNK",
				["Light Tank"] = "LTNK",
				["Mammoth Tank"] = "HTNK",
				["Mammoth"] = "HTNK",
				["Heavy Tank"] = "HTNK",
				["Artillery"] = "ARTY",
				["Arty"] = "ARTY",
				["Rocket Launcher"] = "MSAM",
				["MLRS"] = "MSAM",
				["Buggy"] = "BGGY",
				["Recon Bike"] = "BIKE",
				["Bike"] = "BIKE",
				["Flame Tank"] = "FTNK",
				["Flamer"] = "FTNK",
				["Stealth Tank"] = "STNK",
				["Stealth"] = "STNK",
				["SSM Launcher"] = "SSM",
				["SSM"] = "SSM"
			};

			if (shortcuts.TryGetValue(input, out var shortcutResult))
				return shortcutResult;

			// If nothing matched, return the input uppercased (C&C convention)
			return input.ToUpperInvariant();
		}
	}
}
