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
using System.IO;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.LLM.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Exports per-player fog-scoped game state and observer stats for the orf orchestrator.")]
	public sealed class LlmMatchControllerInfo : TraitInfo
	{
		[Desc("Fallback export interval in ticks when match.json does not specify one.")]
		public readonly int StateIntervalTicks = 25;

		public override object Create(ActorInitializer init) { return new LlmMatchController(this); }
	}

	public sealed class LlmMatchController : ITick, ITickRender, IWorldLoaded, IGameOver
	{
		readonly LlmMatchControllerInfo info;

		Dictionary<Player, LlmPlayerConfig> configs;
		IResourceLayer resourceLayer;
		int interval;
		bool resultWritten;

		public LlmMatchController(LlmMatchControllerInfo info)
		{
			this.info = info;
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			if (!LlmRun.Active)
				return;

			LlmDamageLog.Clear();
			configs = LlmRun.PlayerConfigs(w);
			resourceLayer = w.WorldActor.TraitOrDefault<IResourceLayer>();
			interval = LlmRun.Match.StateIntervalTicks > 0 ? LlmRun.Match.StateIntervalTicks : info.StateIntervalTicks;

			// God view for the capture: no shroud, viewport pinned to the map center.
			// Zoom is enforced continuously in TickRender because the engine resets it
			// when the window or graphics settings settle after load.
			w.RenderPlayer = null;
			var map = w.Map;
			var center = map.CenterOfCell(new MPos(map.MapSize.Width / 2, map.MapSize.Height / 2).ToCPos(map));
			wr.Viewport.ViewportCenterProvider = () => new float2(center.X, center.Y);
		}

		void ITickRender.TickRender(WorldRenderer wr, Actor self)
		{
			if (!LlmRun.Active)
				return;

			// The browser view is the product: hide the in-game UI chrome so the capture
			// is pure map, and keep the observer view shroud-free and fit to the map.
			foreach (var child in Ui.Root.Children)
				if (child.IsVisible())
					child.IsVisible = () => false;

			if (self.World.RenderPlayer != null)
				self.World.RenderPlayer = null;

			var map = self.World.Map;
			var tileSize = map.Rules.TerrainInfo.TileSize;
			var mapPxWidth = map.MapSize.Width * tileSize.Width;
			var mapPxHeight = map.MapSize.Height * tileSize.Height;
			var resolution = Game.Renderer.NativeResolution;
			var desired = Math.Min((float)resolution.Width / mapPxWidth, (float)resolution.Height / mapPxHeight);
			desired = Math.Min(desired, wr.Viewport.MaxZoom);

			if (Math.Abs(wr.Viewport.Zoom - desired) > 0.01f)
			{
				wr.Viewport.UnlockMinimumZoom(desired / Math.Max(wr.Viewport.MinZoom, 0.001f));
				wr.Viewport.AdjustZoom(-100f);
			}
		}

		void ITick.Tick(Actor self)
		{
			if (!LlmRun.Active || configs == null || configs.Count == 0)
				return;

			var world = self.World;
			if (world.WorldTick == 0 || world.WorldTick % interval != 0)
				return;

			try
			{
				foreach (var (player, cfg) in configs)
					LlmRun.WriteJsonAtomic(Path.Combine(LlmRun.StateDir, cfg.Slug + ".json"), PlayerState(world, player, cfg));

				LlmRun.WriteJsonAtomic(Path.Combine(LlmRun.StateDir, "game.json"), GameState(world));

				if (!resultWritten)
				{
					var undefeated = configs.Keys.Count(p => p.WinState != WinState.Lost);
					if (world.IsGameOver || (configs.Count > 1 && undefeated <= 1))
						WriteResult(world);
				}
			}
			catch (Exception e)
			{
				Log.Write("debug", $"LlmMatchController: export failed: {e}");
			}
		}

		void IGameOver.GameOver(World world)
		{
			if (LlmRun.Active && configs != null)
				WriteResult(world);
		}

		void WriteResult(World world)
		{
			if (resultWritten)
				return;

			resultWritten = true;
			LlmRun.WriteJsonAtomic(Path.Combine(LlmRun.RunDir, "result.json"), new
			{
				finishedAtTick = world.WorldTick,
				second = world.WorldTick * world.Timestep / 1000,
				players = configs.Select(kv => new { slug = kv.Value.Slug, winState = kv.Key.WinState.ToString() }).ToList()
			});
		}

		readonly Dictionary<Player, Queue<(int Tick, long Earned, long Spent)>> econHistory = [];

		(int IncomePerMinute, int SpendPerMinute) EconRates(World world, Player player, PlayerResources resources)
		{
			if (resources == null)
				return (0, 0);

			if (!econHistory.TryGetValue(player, out var hist))
				econHistory[player] = hist = new Queue<(int, long, long)>();

			hist.Enqueue((world.WorldTick, resources.Earned, resources.Spent));
			while (hist.Count > 1 && world.WorldTick - hist.Peek().Tick > 750)
				hist.Dequeue();

			var (oldTick, oldEarned, oldSpent) = hist.Peek();
			var seconds = (world.WorldTick - oldTick) * world.Timestep / 1000f;
			if (seconds < 1f)
				return (0, 0);

			return (
				(int)((resources.Earned - oldEarned) / seconds * 60),
				(int)((resources.Spent - oldSpent) / seconds * 60));
		}

		object PlayerState(World world, Player player, LlmPlayerConfig cfg)
		{
			var map = world.Map;
			var resources = player.PlayerActor.TraitOrDefault<PlayerResources>();
			var power = player.PlayerActor.TraitOrDefault<PowerManager>();
			var (incomePerMinute, spendPerMinute) = EconRates(world, player, resources);

			var buildings = new List<object>();
			var units = new List<object>();
			var visibleEnemies = new List<object>();

			foreach (var a in world.Actors)
			{
				if (a.IsDead || !a.IsInWorld)
					continue;

				var isBuilding = a.Info.HasTraitInfo<BuildingInfo>();
				var isUnit = !isBuilding && (a.Info.HasTraitInfo<MobileInfo>() || a.Info.HasTraitInfo<AircraftInfo>());
				if (!isBuilding && !isUnit)
					continue;

				if (a.Owner == player)
				{
					var cell = map.CellContaining(a.CenterPosition);
					if (isBuilding)
					{
						var rally = a.TraitOrDefault<RallyPoint>()?.Path.FirstOrDefault();
						buildings.Add(new
						{
							id = a.ActorID,
							name = LlmNames.Display(world, a.Info),
							cell = CellArray(cell),
							hpPercent = HpPercent(a),
							rally = rally.HasValue ? CellArray(rally.Value) : null
						});
					}
					else
					{
						units.Add(new
						{
							id = a.ActorID,
							name = LlmNames.Display(world, a.Info),
							cell = CellArray(cell),
							hpPercent = HpPercent(a),
							idle = a.IsIdle
						});
					}
				}
				else if (configs.TryGetValue(a.Owner, out var ownerCfg) && ownerCfg != cfg
					&& player.RelationshipWith(a.Owner) == PlayerRelationship.Enemy
					&& a.CanBeViewedByPlayer(player))
				{
					visibleEnemies.Add(new
					{
						id = a.ActorID,
						name = LlmNames.Display(world, a.Info),
						owner = ownerCfg.Slug,
						cell = CellArray(map.CellContaining(a.CenterPosition)),
						hpPercent = HpPercent(a),
						isBuilding
					});
				}
			}

			var production = new List<object>();
			var busyQueues = 0;
			foreach (var queue in world.ActorsWithTrait<ProductionQueue>()
				.Where(x => x.Actor.Owner == player && x.Trait.Enabled)
				.Select(x => x.Trait))
			{
				var current = queue.CurrentItem();
				if (current != null)
					busyQueues++;

				production.Add(new
				{
					queue = queue.Info.Type,
					busy = current != null,
					current = current == null ? null : new
					{
						name = LlmNames.Display(world, world.Map.Rules.Actors[current.Item]),
						progressPercent = current.TotalTime > 0
							? (current.TotalTime - current.RemainingTime) * 100 / current.TotalTime
							: 0,
						paused = current.Paused,
						ready = current.Done
					},
					queued = queue.AllQueued().Select(i => LlmNames.Display(world, world.Map.Rules.Actors[i.Item])).ToList(),
					buildable = queue.BuildableItems().Select(b => new
					{
						name = LlmNames.Display(world, b),
						cost = queue.GetProductionCost(b)
					}).ToList()
				});
			}

			var frozen = new List<object>();
			var frozenLayer = player.PlayerActor.TraitOrDefault<FrozenActorLayer>();
			if (frozenLayer != null)
			{
				foreach (var fa in frozenLayer.FrozenActorsInRegion(map.AllCells, false))
				{
					if (!fa.IsValid || fa.Owner == null || fa.Owner == player)
						continue;

					if (!configs.TryGetValue(fa.Owner, out var ownerCfg))
						continue;

					frozen.Add(new
					{
						name = LlmNames.Display(world, fa.Info),
						owner = ownerCfg.Slug,
						cell = CellArray(map.CellContaining(fa.CenterPosition))
					});
				}
			}

			return new
			{
				tick = world.WorldTick,
				second = world.WorldTick * world.Timestep / 1000,
				you = new
				{
					slug = cfg.Slug,
					faction = player.Faction.InternalName,
					cash = resources == null ? 0 : resources.Cash + resources.Resources,
					incomePerMinute,
					spendPerMinute,
					powerProvided = power?.PowerProvided ?? 0,
					powerDrained = power?.PowerDrained ?? 0,
					defeated = player.WinState == WinState.Lost
				},
				buildCapacity = new
				{
					queues = production.Count,
					busy = busyQueues,
					idle = production.Count - busyQueues
				},
				underAttack = LlmDamageLog.Recent(player, world.WorldTick - 250)
					.GroupBy(e => (e.VictimId, e.AttackerId))
					.Take(12)
					.Select(g =>
					{
						var e = g.Last();
						configs.TryGetValue(e.AttackerOwner, out var attackerCfg);
						return (object)new
						{
							yourUnitId = e.VictimId,
							yourUnit = NameOf(world, e.VictimType),
							attackerId = e.AttackerId,
							attacker = NameOf(world, e.AttackerType),
							attackerOwner = attackerCfg?.Slug,
							attackerCell = e.AttackerCell == CPos.Zero ? null : CellArray(e.AttackerCell)
						};
					})
					.ToList(),
				map = new
				{
					width = map.MapSize.Width,
					height = map.MapSize.Height,
					yourSpawnCell = CellArray(player.HomeLocation)
				},

				// Fair map knowledge: the lobby shows every start position, so players
				// always know roughly where the enemies begin. Order matches nothing —
				// the agent is not told which opponent holds which spawn.
				enemySpawns = configs.Keys
					.Where(other => other != player && player.RelationshipWith(other) == PlayerRelationship.Enemy)
					.Select(other => new
					{
						cell = CellArray(other.HomeLocation),
						explored = player.Shroud.IsExplored(other.HomeLocation)
					})
					.OrderBy(s => s.cell[0]).ThenBy(s => s.cell[1])
					.ToList(),
				production,
				buildings,
				units,
				visibleEnemies,
				lastKnownEnemyBuildings = frozen,
				exploredResources = ExploredResources(world, player)
			};
		}

		List<object> ExploredResources(World world, Player player)
		{
			var blocks = new Dictionary<(int X, int Y), (long SumX, long SumY, int Count)>();
			if (resourceLayer == null)
				return [];

			foreach (var cell in world.Map.AllCells)
			{
				if (resourceLayer.GetResource(cell).Density == 0 || !player.Shroud.IsExplored(cell))
					continue;

				var key = (cell.X / 8, cell.Y / 8);
				var (sumX, sumY, count) = blocks.TryGetValue(key, out var b) ? b : (0, 0, 0);
				blocks[key] = (sumX + cell.X, sumY + cell.Y, count + 1);
			}

			return blocks.Values
				.OrderByDescending(b => b.Count)
				.Take(20)
				.Select(b => (object)new { cell = new[] { (int)(b.SumX / b.Count), (int)(b.SumY / b.Count) }, cells = b.Count })
				.ToList();
		}

		object GameState(World world)
		{
			var players = new List<object>();
			foreach (var (player, cfg) in configs)
			{
				var resources = player.PlayerActor.TraitOrDefault<PlayerResources>();
				var power = player.PlayerActor.TraitOrDefault<PowerManager>();
				var stats = player.PlayerActor.TraitOrDefault<PlayerStatistics>();

				var unitCount = 0;
				var buildingCount = 0;
				foreach (var a in world.Actors)
				{
					if (a.IsDead || !a.IsInWorld || a.Owner != player)
						continue;

					if (a.Info.HasTraitInfo<BuildingInfo>())
						buildingCount++;
					else if (a.Info.HasTraitInfo<MobileInfo>() || a.Info.HasTraitInfo<AircraftInfo>())
						unitCount++;
				}

				players.Add(new
				{
					slug = cfg.Slug,
					name = player.PlayerName,
					faction = player.Faction.InternalName,
					colorHex = $"{player.Color.R:X2}{player.Color.G:X2}{player.Color.B:X2}",
					spawnCell = CellArray(player.HomeLocation),
					cash = resources == null ? 0 : resources.Cash + resources.Resources,
					earned = resources?.Earned ?? 0,
					powerProvided = power?.PowerProvided ?? 0,
					powerDrained = power?.PowerDrained ?? 0,
					unitCount,
					buildingCount,
					armyValue = stats?.ArmyValue ?? 0,
					assetsValue = stats?.AssetsValue ?? 0,
					unitsKilled = stats?.UnitsKilled ?? 0,
					unitsLost = stats?.UnitsDead ?? 0,
					buildingsKilled = stats?.BuildingsKilled ?? 0,
					buildingsLost = stats?.BuildingsDead ?? 0,
					winState = player.WinState.ToString()
				});
			}

			return new
			{
				tick = world.WorldTick,
				second = world.WorldTick * world.Timestep / 1000,
				paused = world.Paused,
				gameOver = world.IsGameOver,
				map = new { title = world.Map.Title, width = world.Map.MapSize.Width, height = world.Map.MapSize.Height },
				players
			};
		}

		static string NameOf(World world, string internalType)
		{
			return world.Map.Rules.Actors.TryGetValue(internalType, out var ai)
				? LlmNames.Display(world, ai)
				: internalType;
		}

		static int[] CellArray(CPos cell)
		{
			return [cell.X, cell.Y];
		}

		static int HpPercent(Actor a)
		{
			var health = a.TraitOrDefault<IHealth>();
			return health == null || health.MaxHP == 0 ? 100 : (int)((long)health.HP * 100 / health.MaxHP);
		}
	}
}
