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
using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Automatically sets rally points for production buildings towards the center of the map.")]
	public class AutoRallyPointInfo : TraitInfo
	{
		[Desc("Enable or disable automatic rally point assignment.")]
		public readonly bool Enabled = true;

		[Desc("Distance from the base towards map center (as a percentage, 0-100). Lower values keep rally point closer to base.")]
		public readonly int DistancePercentage = 30;

		[Desc("Exclude these actor types from using rally points (e.g. harvesters).")]
		public readonly string[] ExcludedActorTypes = { "harv" };

		public override object Create(ActorInitializer init) { return new AutoRallyPoint(this); }
	}

	public class AutoRallyPoint : INotifyCreated
	{
		readonly AutoRallyPointInfo info;

		public AutoRallyPoint(AutoRallyPointInfo info)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			if (!info.Enabled)
				return;

			// Listen for building completion events
			self.World.ActorAdded += a =>
			{
				// Only process buildings owned by human players
				if (a.Owner == null || a.Owner.IsBot || a.Owner.NonCombatant)
					return;

				var rallyPoint = a.TraitOrDefault<RallyPoint>();
				if (rallyPoint == null)
					return;

				var production = a.TraitOrDefault<Production>();
				if (production == null)
					return;

				// Set rally point when building is created
				SetRallyPoint(a, rallyPoint);
			};
		}

		void SetRallyPoint(Actor building, RallyPoint rallyPoint)
		{
			var world = building.World;
			var map = world.Map;

			// Find base center (average position of all buildings)
			var baseCenter = FindBaseCenter(building.Owner, world);
			if (!baseCenter.HasValue)
			{
				// Fallback to building's own location
				baseCenter = building.Location;
			}

			// Calculate map center
			var mapCenter = new CPos(map.MapSize.Width / 2, map.MapSize.Height / 2);

			// Calculate rally point position: partway between base center and map center
			var dx = mapCenter.X - baseCenter.Value.X;
			var dy = mapCenter.Y - baseCenter.Value.Y;

			var rallyX = baseCenter.Value.X + (dx * info.DistancePercentage / 100);
			var rallyY = baseCenter.Value.Y + (dy * info.DistancePercentage / 100);
			var rallyCell = new CPos(rallyX, rallyY);

			// Find nearest valid cell
			var validRallyCell = FindNearestValidCell(world, rallyCell);
			if (validRallyCell.HasValue)
			{
				rallyPoint.Path.Clear();
				rallyPoint.Path.Add(validRallyCell.Value);
			}
		}

		CPos? FindBaseCenter(Player player, World world)
		{
			var buildings = world.Actors
				.Where(a => a.Owner == player &&
							!a.IsDead &&
							a.Info.HasTraitInfo<BuildingInfo>())
				.ToList();

			if (buildings.Count == 0)
				return null;

			var sumX = 0;
			var sumY = 0;
			foreach (var building in buildings)
			{
				var pos = world.Map.CellContaining(building.CenterPosition);
				sumX += pos.X;
				sumY += pos.Y;
			}

			return new CPos(sumX / buildings.Count, sumY / buildings.Count);
		}

		CPos? FindNearestValidCell(World world, CPos target)
		{
			// Check if target cell itself is valid
			if (world.Map.Contains(target) && IsCellValid(world, target))
				return target;

			// Search in expanding circles
			for (var radius = 1; radius < 20; radius++)
			{
				for (var dx = -radius; dx <= radius; dx++)
				{
					for (var dy = -radius; dy <= radius; dy++)
					{
						// Only check perimeter cells for this radius
						if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
							continue;

						var candidate = new CPos(target.X + dx, target.Y + dy);
						if (world.Map.Contains(candidate) && IsCellValid(world, candidate))
							return candidate;
					}
				}
			}

			return null;
		}

		bool IsCellValid(World world, CPos cell)
		{
			// Check if cell is on the map and is passable terrain
			if (!world.Map.Contains(cell))
				return false;

			// Check if it's passable by checking terrain type
			var terrainInfo = world.Map.GetTerrainInfo(cell);
			return terrainInfo.Type != "Water" && terrainInfo.Type != "Rock";
		}
	}
}
