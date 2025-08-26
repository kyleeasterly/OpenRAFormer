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

namespace OpenRA
{
	public static class FriendlyNames
	{
		public static string GetFriendlyBuildingName(string internalName)
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
				"V19" => "Oil Pump",
				_ => internalName
			};
		}

		public static string GetFriendlyUnitName(string internalName)
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
				"A10" => "A10 Warthog",
				_ => internalName
			};
		}
	}
}