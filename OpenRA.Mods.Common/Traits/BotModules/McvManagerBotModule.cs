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
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Manages AI MCVs.")]
	public class McvManagerBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Actor types that are considered MCVs (deploy into base builders).")]
		public readonly HashSet<string> McvTypes = [];

		[Desc("Actor types that are considered construction yards (base builders).")]
		public readonly HashSet<string> ConstructionYardTypes = [];

		[Desc("Actor types that are able to produce MCVs.")]
		public readonly HashSet<string> McvFactoryTypes = [];

		[Desc("Actor types that are considered refineries.")]
		public readonly HashSet<string> RefineryTypes = [];

		[Desc("Try to maintain at least this many ConstructionYardTypes, build an MCV if number is below this.")]
		public readonly int MinimumConstructionYardCount = 1;

		[Desc("Delay (in ticks) between looking for and giving out orders to new MCVs.")]
		public readonly int ScanForNewMcvInterval = 150;

		[Desc("Minimum distance in cells from center of the base when checking for MCV deployment location.")]
		public readonly int MinBaseRadius = 2;

		[Desc("Maximum distance in cells from center of the base when checking for MCV deployment location.",
			"Only applies if RestrictMCVDeploymentFallbackToBase is enabled and there's at least one construction yard.")]
		public readonly int MaxBaseRadius = 20;

		[Desc("Should deployment of additional MCVs be restricted to MaxBaseRadius if explicit deploy locations are missing or occupied?")]
		public readonly bool RestrictMCVDeploymentFallbackToBase = true;

		[Desc("Prefer deploying MCVs near resource patches outside the base.")]
		public readonly bool PreferResourceExpansion = false;


		public override object Create(ActorInitializer init) { return new McvManagerBotModule(init.Self, this); }
	}

	public class McvManagerBotModule : ConditionalTrait<McvManagerBotModuleInfo>,
		IBotTick, IBotPositionsUpdated, IGameSaveTraitData, INotifyActorDisposing
	{
		public CPos GetRandomBaseCenter()
		{
			var randomConstructionYard = constructionYards.Actors
				.RandomOrDefault(world.LocalRandom);

			return randomConstructionYard?.Location ?? initialBaseCenter;
		}

		readonly World world;
		readonly Player player;
		readonly ActorIndex.OwnerAndNamesAndTrait<TransformsInfo> mcvs;
		readonly ActorIndex.OwnerAndNamesAndTrait<BuildingInfo> constructionYards;
		readonly ActorIndex.OwnerAndNamesAndTrait<BuildingInfo> mcvFactories;

		IBotPositionsUpdated[] notifyPositionsUpdated;
		IBotRequestUnitProduction[] requestUnitProduction;

		CPos initialBaseCenter;
		int scanInterval;
		bool firstTick = true;

		public McvManagerBotModule(Actor self, McvManagerBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
			mcvs = new ActorIndex.OwnerAndNamesAndTrait<TransformsInfo>(world, info.McvTypes, player);
			constructionYards = new ActorIndex.OwnerAndNamesAndTrait<BuildingInfo>(world, info.ConstructionYardTypes, player);
			mcvFactories = new ActorIndex.OwnerAndNamesAndTrait<BuildingInfo>(world, info.McvFactoryTypes, player);
		}

		protected override void Created(Actor self)
		{
			notifyPositionsUpdated = self.Owner.PlayerActor.TraitsImplementing<IBotPositionsUpdated>().ToArray();
			requestUnitProduction = self.Owner.PlayerActor.TraitsImplementing<IBotRequestUnitProduction>().ToArray();
		}

		protected override void TraitEnabled(Actor self)
		{
			// Avoid all AIs reevaluating assignments on the same tick, randomize their initial evaluation delay.
			scanInterval = world.LocalRandom.Next(Info.ScanForNewMcvInterval, Info.ScanForNewMcvInterval * 2);
		}

		void IBotPositionsUpdated.UpdatedBaseCenter(CPos newLocation)
		{
			initialBaseCenter = newLocation;
		}

		void IBotPositionsUpdated.UpdatedDefenseCenter(CPos newLocation) { }

		void IBotTick.BotTick(IBot bot)
		{
			if (firstTick)
			{
				DeployMcvs(bot, false);
				firstTick = false;
			}

			if (--scanInterval <= 0)
			{
				scanInterval = Info.ScanForNewMcvInterval;
				DeployMcvs(bot, true);

				// No construction yards - Build a new MCV
				var unitBuilder = requestUnitProduction.FirstEnabledTraitOrDefault();
				if (unitBuilder != null && Info.McvTypes.Count > 0 && ShouldBuildMCV())
				{
					var mcvType = Info.McvTypes.Random(world.LocalRandom);
					if (unitBuilder.RequestedProductionCount(bot, mcvType) == 0)
						unitBuilder.RequestUnitProduction(bot, mcvType);
				}
			}
		}

		bool ShouldBuildMCV()
		{
			var currentBases = AIUtils.CountActorByCommonName(constructionYards);
			var currentMcvs = AIUtils.CountActorByCommonName(mcvs);

			// Total base potential = existing bases + MCVs (which will become bases)
			var totalBasePotential = currentBases + currentMcvs;

			// Don't build more MCVs if we already have enough bases + MCVs
			if (totalBasePotential >= Info.MinimumConstructionYardCount)
				return false;

			// Check if we have a factory to build it
			if (AIUtils.CountActorByCommonName(mcvFactories) == 0)
				return false;

			// Don't build MCVs too early - need basic infrastructure first
			var refineries = world.ActorsHavingTrait<Building>()
				.Count(a => a.Owner == player && Info.RefineryTypes.Contains(a.Info.Name));
			var barracks = world.ActorsHavingTrait<Building>()
				.Count(a => a.Owner == player && (a.Info.Name == "pyle" || a.Info.Name == "hand"));
			
			// For first expansion: need at least 2 refineries and 1 barracks
			if (currentBases == 1 && totalBasePotential == 1)
				return refineries >= 2 && barracks >= 1;
			
			// For subsequent expansions: need more infrastructure
			return refineries >= 3 && barracks >= 2;
		}

		void DeployMcvs(IBot bot, bool chooseLocation)
		{
			var newMCVs = mcvs.Actors
				.Where(a => a.IsIdle);

			foreach (var mcv in newMCVs)
				DeployMcv(bot, mcv, chooseLocation);
		}

		// Find any MCV and deploy them at a sensible location.
		void DeployMcv(IBot bot, Actor mcv, bool move)
		{
			if (move)
			{
				// If we lack a base, we need to make sure we don't restrict deployment of the MCV to the base!
				var restrictToBase =
					Info.RestrictMCVDeploymentFallbackToBase &&
					AIUtils.CountActorByCommonName(constructionYards) > 0;

				var transformsInfo = mcv.Info.TraitInfo<TransformsInfo>();
				var desiredLocation = ChooseMcvDeployLocation(transformsInfo.IntoActor, transformsInfo.Offset, restrictToBase);
				if (desiredLocation == null)
					return;

				bot.QueueOrder(new Order("Move", mcv, Target.FromCell(world, desiredLocation.Value), true));
			}

			// If the MCV has to move first, we can't be sure it reaches the destination alive, so we only
			// update base and defense center if the MCV is deployed immediately (i.e. at game start).
			// TODO: This could be addressed via INotifyTransform.
			foreach (var n in notifyPositionsUpdated)
			{
				n.UpdatedBaseCenter(mcv.Location);
				n.UpdatedDefenseCenter(mcv.Location);
			}

			bot.QueueOrder(new Order("DeployTransform", mcv, true));
		}

		sealed class ResourcePatch
		{
			public CPos Center { get; set; }
			public int ResourceCount { get; set; }
			public int DistanceFromBase { get; set; }
			public float Score { get; set; }
		}

		List<ResourcePatch> FindResourcePatches(IResourceLayer resourceLayer, CPos baseCenter)
		{
			Console.WriteLine($"[{player.PlayerName}] Finding resource patches from base at {baseCenter}");
			var patches = new List<ResourcePatch>();
			var visited = new HashSet<CPos>();
			
			// Find all resource cells and group them into patches
			foreach (var cell in world.Map.AllCells)
			{
				if (visited.Contains(cell))
					continue;

				var resource = resourceLayer.GetResource(cell);
				if (resource.Type == null || !resourceLayer.IsVisible(cell))
					continue;

				// Found a resource cell, flood-fill to find the whole patch
				var patchCells = new List<CPos>();
				var queue = new Queue<CPos>();
				queue.Enqueue(cell);
				visited.Add(cell);

				while (queue.Count > 0)
				{
					var current = queue.Dequeue();
					patchCells.Add(current);

					// Check all adjacent cells
					foreach (var direction in CVec.Directions)
					{
						var adjacent = current + direction;
						if (!world.Map.Contains(adjacent) || visited.Contains(adjacent))
							continue;

						var adjacentResource = resourceLayer.GetResource(adjacent);
						if (adjacentResource.Type != null && resourceLayer.IsVisible(adjacent))
						{
							visited.Add(adjacent);
							queue.Enqueue(adjacent);
						}
					}
				}

				// Create a patch from the cells we found
				if (patchCells.Count > 0)
				{
					var centerX = patchCells.Sum(c => c.X) / patchCells.Count;
					var centerY = patchCells.Sum(c => c.Y) / patchCells.Count;
					var patchCenter = new CPos(centerX, centerY);
					var distance = (patchCenter - baseCenter).Length;

					patches.Add(new ResourcePatch
					{
						Center = patchCenter,
						ResourceCount = patchCells.Count,
						DistanceFromBase = distance,
						Score = 0 // Will be calculated below
					});
				}
			}

			// Calculate min and max distances for relative scoring
			var distances = patches.Select(p => p.DistanceFromBase).ToList();
			var minDistance = distances.Count > 0 ? distances.Min() : 0;
			var maxDistance = distances.Count > 0 ? distances.Max() : 1;
			var distanceRange = maxDistance - minDistance;
			if (distanceRange == 0)
				distanceRange = 1; // Avoid division by zero

			// Get all friendly construction yards and refineries for checking harvesting status
			var friendlyHarvestingBuildings = world.ActorsHavingTrait<Building>()
				.Where(a => a.Owner == player && 
					(Info.ConstructionYardTypes.Contains(a.Info.Name) || 
					Info.RefineryTypes.Contains(a.Info.Name)))
				.ToList();

			Console.WriteLine($"[{player.PlayerName}] Found {patches.Count} resource patches");
			Console.WriteLine($"[{player.PlayerName}] Distance range: {minDistance} to {maxDistance}");
			Console.WriteLine($"[{player.PlayerName}] Friendly harvesting buildings: {friendlyHarvestingBuildings.Count}");

			foreach (var patch in patches)
			{
				// Base score heavily on distance - closest patches get highest score
				var distanceScore = (maxDistance - patch.DistanceFromBase) / (float)distanceRange;
				
				// Use resource count as a secondary factor
				var score = distanceScore * 100f + patch.ResourceCount;

				// Check if this patch is already being harvested (has a construction yard or refinery nearby)
				var alreadyHarvesting = friendlyHarvestingBuildings
					.Any(building => (building.Location - patch.Center).Length < 15);

				if (alreadyHarvesting)
				{
					Console.WriteLine($"[{player.PlayerName}] Patch at {patch.Center} (dist={patch.DistanceFromBase}, res={patch.ResourceCount}): Already harvesting, penalizing score from {score:F1} to {score * 0.1f:F1}");
					score *= 0.1f; // Heavily penalize patches we're already harvesting
				}
				else
				{
					Console.WriteLine($"[{player.PlayerName}] Patch at {patch.Center} (dist={patch.DistanceFromBase}, res={patch.ResourceCount}): Score = {score:F1}");
				}

				patch.Score = score;
			}

			// Return patches sorted by score
			var sortedPatches = patches.OrderByDescending(p => p.Score).ToList();
			
			Console.WriteLine($"[{player.PlayerName}] Top 3 patches by score:");
			foreach (var patch in sortedPatches.Take(3))
			{
				Console.WriteLine($"[{player.PlayerName}]   - {patch.Center}: score={patch.Score:F1}, dist={patch.DistanceFromBase}, res={patch.ResourceCount}");
			}
			
			return sortedPatches;
		}

		CPos? ChooseMcvDeployLocation(string actorType, CVec offset, bool distanceToBaseIsImportant)
		{
			var actorInfo = world.Map.Rules.Actors[actorType];
			var bi = actorInfo.TraitInfoOrDefault<BuildingInfo>();
			if (bi == null)
				return null;

			// Find the buildable cell that is closest to pos and centered around center
			CPos? FindPos(CPos center, CPos target, int minRange, int maxRange)
			{
				var cells = world.Map.FindTilesInAnnulus(center, minRange, maxRange);

				// Sort by distance to target if we have one
				if (center != target)
					cells = cells.OrderBy(c => (c - target).LengthSquared);
				else
					cells = cells.Shuffle(world.LocalRandom);

				foreach (var cell in cells)
					if (world.CanPlaceBuilding(cell + offset, actorInfo, bi, null))
						return cell;

				return null;
			}

			var baseCenter = GetRandomBaseCenter();

			// Try to find resource patches for expansion
			if (Info.PreferResourceExpansion && !distanceToBaseIsImportant)
			{
				Console.WriteLine($"[{player.PlayerName}] Choosing MCV deploy location (PreferResourceExpansion=true, distanceToBaseIsImportant={distanceToBaseIsImportant})");
				var resourceLayer = world.WorldActor.TraitOrDefault<IResourceLayer>();
				if (resourceLayer != null)
				{
					// Find and evaluate resource patches
					var resourcePatches = FindResourcePatches(resourceLayer, baseCenter);

					Console.WriteLine($"[{player.PlayerName}] Checking {resourcePatches.Count} resource patches for buildable locations...");
					foreach (var patch in resourcePatches)
					{
						// Check if we can build near this resource patch
						var location = FindPos(patch.Center, patch.Center, 3, 10);
						if (location != null)
						{
							Console.WriteLine($"[{player.PlayerName}] Selected MCV location near patch at {patch.Center} -> deploying at {location}");
							return location;
						}
						else
						{
							Console.WriteLine($"[{player.PlayerName}] No buildable location near patch at {patch.Center}");
						}
					}
					Console.WriteLine($"[{player.PlayerName}] No suitable resource patches found, falling back to base expansion");
				}
			}
			else
			{
				Console.WriteLine($"[{player.PlayerName}] Choosing MCV deploy location (PreferResourceExpansion={Info.PreferResourceExpansion}, distanceToBaseIsImportant={distanceToBaseIsImportant})");
			}

			// Fallback to current logic
			var fallbackLocation = FindPos(baseCenter, baseCenter, Info.MinBaseRadius,
				distanceToBaseIsImportant ? Info.MaxBaseRadius : world.Map.Grid.MaximumTileSearchRange);
			Console.WriteLine($"[{player.PlayerName}] Using fallback location: {fallbackLocation}");
			return fallbackLocation;
		}

		List<MiniYamlNode> IGameSaveTraitData.IssueTraitData(Actor self)
		{
			if (IsTraitDisabled)
				return null;

			return
			[
				new("InitialBaseCenter", FieldSaver.FormatValue(initialBaseCenter))
			];
		}

		void IGameSaveTraitData.ResolveTraitData(Actor self, MiniYaml data)
		{
			if (self.World.IsReplay)
				return;

			var initialBaseCenterNode = data.NodeWithKeyOrDefault("InitialBaseCenter");
			if (initialBaseCenterNode != null)
				initialBaseCenter = FieldLoader.GetValue<CPos>("InitialBaseCenter", initialBaseCenterNode.Value.Value);
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			mcvs.Dispose();
			constructionYards.Dispose();
			mcvFactories.Dispose();
		}
	}
}
