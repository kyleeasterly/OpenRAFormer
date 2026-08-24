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
	/// Explains WHY something is not buildable, in the display names agents speak.
	/// The production palette only knows "buildable" or not; an agent that cannot see
	/// the missing prerequisite burns whole matches guessing at it (a Medium Tank
	/// needing a Communications Center cost one agent 21 orders and 3 minutes).
	/// </summary>
	public static class PrerequisiteInfo
	{
		/// <summary>How many alternative providers are named for one prerequisite token.</summary>
		public const int MaxAlternatives = 3;

		const string TechLevelPrefix = "techlevel.";

		// The engine's convention for "nothing ever grants this", used to keep campaign
		// and decoration actors out of the palette. Nothing useful to tell the agent.
		const string NeverToken = "disabled";

		// Prerequisite token -> internal names of the actors that grant it, cheapest first.
		static readonly ConditionalWeakTable<World, Dictionary<string, List<string>>> ProviderCache = [];

		/// <summary>Prerequisite tokens the player currently owns, mirroring TechTree's own gather.</summary>
		public static HashSet<string> Owned(Player player)
		{
			var owned = new HashSet<string>(StringComparer.Ordinal);
			foreach (var p in player.World.ActorsWithTrait<ITechTreePrerequisite>())
			{
				if (p.Actor.Owner != player || !p.Actor.IsInWorld || p.Actor.IsDead)
					continue;

				foreach (var token in p.Trait.ProvidesPrerequisites)
					if (token != null)
						owned.Add(token);
			}

			// Build-limited actors count as their own prerequisite; TechTree does the same.
			foreach (var b in player.World.ActorsWithTrait<Buildable>())
				if (b.Actor.Owner == player && b.Actor.IsInWorld && !b.Actor.IsDead
					&& b.Actor.Info.TraitInfo<BuildableInfo>().BuildLimit > 0)
					owned.Add(b.Actor.Info.Name);

			return owned;
		}

		/// <summary>
		/// Splits an actor's unmet build requirements into things that must be built,
		/// things that block it, and the map's tech level gate. Satisfied prerequisites
		/// are omitted — the agent only needs to see what is actually in its way.
		/// </summary>
		public static (List<string> Missing, List<string> Blocking, bool TechLevelBlocked) Describe(
			World world, ActorInfo ai, HashSet<string> owned, IReadOnlySet<string> producible)
		{
			var missing = new List<string>();
			var blocking = new List<string>();
			var techLevelBlocked = false;

			var bi = ai.TraitInfoOrDefault<BuildableInfo>();
			if (bi == null)
				return (missing, blocking, techLevelBlocked);

			foreach (var raw in bi.Prerequisites)
			{
				// Token grammar, in the order the engine strips it: an optional '~'
				// (hide from the palette while unmet) then an optional '!' (must NOT
				// be owned). What is left is the prerequisite itself.
				var token = raw.Replace("~", "");
				var negated = token.StartsWith('!');
				token = token.Replace("!", "");

				// Satisfied when ownership matches what the token asks for.
				if (owned.Contains(token) != negated)
					continue;

				// Tech level is a lobby/map gate, not something the player can build.
				if (token.StartsWith(TechLevelPrefix, StringComparison.Ordinal))
				{
					techLevelBlocked = true;
					continue;
				}

				if (token.StartsWith(NeverToken, StringComparison.Ordinal))
					continue;

				var name = DescribeToken(world, token, producible);
				if (negated)
					blocking.Add(name);
				else
					missing.Add(name);
			}

			return (missing, blocking, techLevelBlocked);
		}

		/// <summary>One-sentence explanation of why <paramref name="ai"/> cannot be built right now.</summary>
		public static string Explain(World world, Player player, ActorInfo ai, string requested, IReadOnlySet<string> producible)
		{
			var (missing, blocking, techLevelBlocked) = Describe(world, ai, Owned(player), producible);

			var parts = new List<string>();
			if (missing.Count > 0)
				parts.Add($"it requires: {string.Join(", ", missing)}. Build that first");

			if (blocking.Count > 0)
				parts.Add($"it cannot be built while you have {string.Join(", ", blocking)}");

			if (techLevelBlocked)
				parts.Add("it is not available at this map's tech level");

			var display = LlmNames.Display(world, ai);
			if (parts.Count == 0)
				return $"'{requested}' is not buildable right now — none of your production queues can make a {display} " +
					"(wrong faction, not available in this game mode, or the structure that builds it is destroyed, sold, or shut down)";

			return $"'{display}' is not buildable yet — {string.Join("; ", parts)}.";
		}

		/// <summary>Names the thing that grants a prerequisite token, as an agent would say it.</summary>
		static string DescribeToken(World world, string token, IReadOnlySet<string> producible)
		{
			if (Providers(world).TryGetValue(token, out var names) && names.Count > 0)
			{
				// Prefer providers this player's own queues could offer, so a GDI player
				// is never told to go and build the Temple of Nod.
				var relevant = producible == null ? names : names.Where(producible.Contains).ToList();
				if (relevant.Count == 0)
					relevant = names;

				return string.Join(" or ", relevant.Take(MaxAlternatives)
					.Select(n => LlmNames.Display(world, world.Map.Rules.Actors[n])));
			}

			return world.Map.Rules.Actors.TryGetValue(token, out var ai) ? LlmNames.Display(world, ai) : token;
		}

		static Dictionary<string, List<string>> Providers(World world)
		{
			return ProviderCache.GetValue(world, w =>
			{
				var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
				foreach (var (name, actorInfo) in w.Map.Rules.Actors)
				{
					if (name.StartsWith('^'))
						continue;

					foreach (var provider in actorInfo.TraitInfos<ITechTreePrerequisiteInfo>())
					{
						foreach (var token in provider.Prerequisites(actorInfo))
						{
							if (string.IsNullOrEmpty(token))
								continue;

							if (!index.TryGetValue(token, out var names))
								index[token] = names = [];

							if (!names.Contains(name))
								names.Add(name);
						}
					}
				}

				// Cheapest first: point the agent at the most basic building that
				// unlocks the item, not at the endgame one.
				foreach (var names in index.Values)
					names.Sort((a, b) => Cost(w, a).CompareTo(Cost(w, b)));

				return index;
			});
		}

		static int Cost(World world, string actorName)
		{
			return world.Map.Rules.Actors.TryGetValue(actorName, out var ai)
				? ai.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? int.MaxValue
				: int.MaxValue;
		}
	}
}
