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

using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.LLM.Traits
{
	[Desc("Plays the building's BuildSounds positionally for everyone when it is placed. " +
		"The engine's own placement sound is PlayToPlayer (owner only), which leaves the " +
		"LLM match spectator — and therefore the dashboard audio stream — silent. No-ops " +
		"outside LLM runs.")]
	public sealed class SpectatorBuildSoundInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new SpectatorBuildSound(); }
	}

	public sealed class SpectatorBuildSound : INotifyAddedToWorld
	{
		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			if (!LlmRun.Active)
				return;

			// Skip actors pre-placed by the map at load, and neutral/civilian
			// spawns (e.g. husks appearing on destruction) — only real player
			// construction should thud.
			if (self.World.WorldTick == 0 || self.Owner.NonCombatant)
				return;

			var buildingInfo = self.Info.TraitInfoOrDefault<BuildingInfo>();
			if (buildingInfo == null)
				return;

			foreach (var s in buildingInfo.BuildSounds)
				Game.Sound.Play(SoundType.World, s, self.CenterPosition);
		}
	}
}
