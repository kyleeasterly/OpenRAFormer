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

using System.Collections.Generic;

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
	}
}
