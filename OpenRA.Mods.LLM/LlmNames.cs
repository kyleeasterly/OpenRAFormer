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
using System.Runtime.CompilerServices;
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Mods.LLM
{
	/// <summary>
	/// Agent-facing actor naming: agents only ever see and speak human-readable names
	/// ("Tiberium Refinery"), never internal codes ("proc"). Internal codes are still
	/// accepted on input for robustness.
	/// </summary>
	public static class LlmNames
	{
		sealed class Maps
		{
			public readonly Dictionary<string, string> Display = [];
			public readonly Dictionary<string, string> ToInternal = new(StringComparer.OrdinalIgnoreCase);
		}

		static readonly ConditionalWeakTable<World, Maps> Cache = [];

		static Maps For(World world)
		{
			return Cache.GetValue(world, w =>
			{
				var maps = new Maps();
				foreach (var (name, actorInfo) in w.Map.Rules.Actors)
				{
					if (name.StartsWith('^'))
						continue;

					var display = name;
					var tooltip = actorInfo.TraitInfos<TooltipInfo>().FirstOrDefault();
					if (tooltip != null && !string.IsNullOrEmpty(tooltip.Name))
					{
						try
						{
							display = FluentProvider.GetMessage(tooltip.Name);
						}
						catch
						{
							display = name;
						}
					}

					maps.Display[name] = display;

					// First definition wins on display-name collisions (e.g. husks or
					// campaign variants sharing a name); buildable actors are defined
					// first in practice, and order input also accepts internal names.
					maps.ToInternal.TryAdd(display, name);
				}

				return maps;
			});
		}

		public static string Display(World world, ActorInfo info)
		{
			return For(world).Display.TryGetValue(info.Name, out var display) ? display : info.Name;
		}

		/// <summary>Resolves agent input (display or internal name) to an internal actor type, or null.</summary>
		public static string ResolveInternal(World world, string input)
		{
			if (string.IsNullOrWhiteSpace(input))
				return null;

			input = input.Trim();
			if (world.Map.Rules.Actors.ContainsKey(input.ToLowerInvariant()))
				return input.ToLowerInvariant();

			return For(world).ToInternal.TryGetValue(input, out var internalName) ? internalName : null;
		}
	}
}
