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
using System.Text.Json;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.LLM.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("A bot whose orders are supplied externally by the orf orchestrator via JSON files.")]
	public sealed class LlmBotInfo : TraitInfo, IBotInfo
	{
		[Desc("Human-readable name this bot uses.")]
		public readonly string Name = "LLM";

		[Desc("Internal id for this bot.")]
		public readonly string Type = "llm";

		[Desc("Ticks between order inbox scans.")]
		public readonly int OrderScanInterval = 10;

		[Desc("Ticks between automatic behaviour checks (building placement, MCV deploy).")]
		public readonly int AutoBehaviourInterval = 25;

		[Desc("Maximum orders issued to the game per tick.")]
		public readonly int MaxOrdersPerTick = 30;

		[Desc("Maximum search radius (in cells) for automatic building placement.")]
		public readonly int PlacementRadius = 25;

		string IBotInfo.Type => Type;
		string IBotInfo.Name => Name;

		public override object Create(ActorInitializer init) { return new LlmBot(this); }
	}

	public sealed class LlmBot : IBot, ITick
	{
		static CVec[] placementOffsets;

		readonly LlmBotInfo info;
		readonly Queue<Order> pending = new();

		Player player;
		bool enabled;
		bool initialized;
		string inboxDir;
		string resultsDir;

		public LlmBot(LlmBotInfo info)
		{
			this.info = info;
		}

		IBotInfo IBot.Info => info;
		Player IBot.Player => player;

		void IBot.Activate(Player p)
		{
			player = p;
			enabled = LlmRun.Active && !p.World.IsReplay;
		}

		void IBot.QueueOrder(Order order)
		{
			pending.Enqueue(order);
		}

		void ITick.Tick(Actor self)
		{
			if (!enabled || player.WinState == WinState.Lost)
				return;

			var world = self.World;
			if (!initialized)
			{
				initialized = true;
				try
				{
					var configs = LlmRun.PlayerConfigs(world);
					if (configs.TryGetValue(player, out var cfg))
					{
						inboxDir = LlmRun.InboxDir(cfg.Slug);
						resultsDir = LlmRun.ResultsDir(cfg.Slug);
						Directory.CreateDirectory(inboxDir);
						Directory.CreateDirectory(resultsDir);
					}
					else
						enabled = false;
				}
				catch (Exception e)
				{
					Log.Write("debug", $"LlmBot: init failed for {player.PlayerName}: {e}");
					enabled = false;
				}

				if (!enabled)
					return;
			}

			if (world.WorldTick % info.OrderScanInterval == 0)
				ScanInbox(world);

			if (world.WorldTick % info.AutoBehaviourInterval == 0)
			{
				AutoDeployMcv(world);
				AutoPlaceReadyBuildings(world);
			}

			var issued = 0;
			while (pending.Count > 0 && issued++ < info.MaxOrdersPerTick)
				world.IssueOrder(pending.Dequeue());
		}

		void ScanInbox(World world)
		{
			string[] files;
			try
			{
				files = Directory.GetFiles(inboxDir, "*.json");
			}
			catch (Exception e)
			{
				Log.Write("debug", $"LlmBot: inbox scan failed: {e.Message}");
				return;
			}

			foreach (var file in files.OrderBy(Path.GetFileName, StringComparer.Ordinal))
			{
				var seq = Path.GetFileNameWithoutExtension(file);
				var results = new List<object>();

				try
				{
					using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
					{
						if (doc.RootElement.TryGetProperty("orders", out var orders) && orders.ValueKind == JsonValueKind.Array)
						{
							var index = 0;
							foreach (var order in orders.EnumerateArray())
								results.Add(ProcessOrder(world, order, index++));
						}
						else
							results.Add(new { index = 0, status = "rejected", reason = "missing 'orders' array" });
					}
				}
				catch (Exception e)
				{
					results.Add(new { index = 0, status = "rejected", reason = $"unreadable order file: {e.Message}" });
				}

				try
				{
					LlmRun.WriteJsonAtomic(Path.Combine(resultsDir, seq + ".json"),
						new { seq, tick = world.WorldTick, results });
					File.Delete(file);
				}
				catch (Exception e)
				{
					Log.Write("debug", $"LlmBot: failed to finalize {file}: {e.Message}");
				}
			}
		}

		object ProcessOrder(World world, JsonElement order, int index)
		{
			try
			{
				var type = order.TryGetProperty("type", out var t) ? t.GetString() : null;
				var reason = type switch
				{
					"start_production" => StartProduction(world, order),
					"cancel_production" => CancelProduction(world, order),
					"deploy" => ForEachActor(world, order, a => new Order("DeployTransform", a, false)),
					"move" => TargetCellOrder(world, order, "Move"),
					"attack_move" => TargetCellOrder(world, order, "AttackMove"),
					"attack" => TargetActorOrder(world, order, "Attack"),
					"capture" => TargetActorOrder(world, order, "CaptureActor"),
					"guard" => TargetActorOrder(world, order, "Guard"),
					"stop" => ForEachActor(world, order, a => new Order("Stop", a, false)),
					"sell" => SingleActorOrder(world, order, a => new Order("Sell", a, false)),
					"repair" => SingleActorOrder(world, order,
						a => new Order("RepairBuilding", player.PlayerActor, Target.FromActor(a), false)),
					"set_rally" => SetRally(world, order),
					null => "missing 'type'",
					_ => $"unknown order type '{type}'"
				};

				return reason == null
					? new { index, status = "ok" }
					: new { index, status = "rejected", reason };
			}
			catch (Exception e)
			{
				return new { index, status = "rejected", reason = $"error: {e.Message}" };
			}
		}

		IEnumerable<ProductionQueue> Queues(World world)
		{
			return world.ActorsWithTrait<ProductionQueue>()
				.Where(x => x.Actor.Owner == player && x.Trait.Enabled)
				.Select(x => x.Trait);
		}

		string StartProduction(World world, JsonElement order)
		{
			var requested = GetString(order, "item");
			var item = LlmNames.ResolveInternal(world, requested);
			if (item == null)
				return $"unknown unit or structure '{requested}'";

			var queue = Queues(world).FirstOrDefault(q => q.BuildableItems().Any(b => b.Name == item));
			if (queue == null)
				return $"'{requested}' is not buildable right now (missing prerequisites or wrong faction)";

			var count = GetInt(order, "count", 1).Clamp(1, 10);
			pending.Enqueue(Order.StartProduction(queue.Actor, item, count));
			return null;
		}

		string CancelProduction(World world, JsonElement order)
		{
			var item = LlmNames.ResolveInternal(world, GetString(order, "item"));
			if (item == null)
				return "missing or unknown 'item'";

			var queue = Queues(world).FirstOrDefault(q => q.AllQueued().Any(i => i.Item == item));
			if (queue == null)
				return $"'{item}' is not in any production queue";

			var count = GetInt(order, "count", 1).Clamp(1, 10);
			pending.Enqueue(Order.CancelProduction(queue.Actor, item, count));
			return null;
		}

		string TargetCellOrder(World world, JsonElement order, string orderString)
		{
			if (!TryGetCell(world, order, "cell", out var cell))
				return "missing or invalid 'cell'";

			var queued = GetBool(order, "queued");
			return ForEachActor(world, order, a => new Order(orderString, a, Target.FromCell(world, cell), queued));
		}

		string TargetActorOrder(World world, JsonElement order, string orderString)
		{
			var targetId = GetInt(order, "targetActorId", -1);
			if (targetId < 0)
				return "missing 'targetActorId'";

			var target = world.GetActorById((uint)targetId);
			if (target == null || target.IsDead || !target.IsInWorld)
				return $"target actor {targetId} does not exist";

			if (!target.CanBeViewedByPlayer(player))
				return $"target actor {targetId} is not visible to you";

			return ForEachActor(world, order, a => new Order(orderString, a, Target.FromActor(target), false));
		}

		string SetRally(World world, JsonElement order)
		{
			if (!TryGetCell(world, order, "cell", out var cell))
				return "missing or invalid 'cell'";

			return SingleActorOrder(world, order, a => a.Info.HasTraitInfo<RallyPointInfo>()
				? new Order("SetRallyPoint", a, Target.FromCell(world, cell), false)
				: null);
		}

		string SingleActorOrder(World world, JsonElement order, Func<Actor, Order> makeOrder)
		{
			var id = GetInt(order, "actorId", -1);
			if (id < 0)
				return "missing 'actorId'";

			var actor = OwnActor(world, (uint)id);
			if (actor == null)
				return $"actor {id} is not one of your live actors";

			var o = makeOrder(actor);
			if (o == null)
				return $"actor {id} cannot receive this order";

			pending.Enqueue(o);
			return null;
		}

		string ForEachActor(World world, JsonElement order, Func<Actor, Order> makeOrder)
		{
			if (!order.TryGetProperty("actorIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
				return "missing 'actorIds'";

			var any = false;
			var missing = new List<int>();
			foreach (var idEl in ids.EnumerateArray())
			{
				var id = idEl.GetInt32();
				var actor = OwnActor(world, (uint)id);
				if (actor == null)
				{
					missing.Add(id);
					continue;
				}

				var o = makeOrder(actor);
				if (o != null)
				{
					pending.Enqueue(o);
					any = true;
				}
			}

			if (!any)
				return missing.Count > 0
					? $"no valid actors (unknown or not yours: {string.Join(", ", missing)})"
					: "no valid actors";

			return null;
		}

		Actor OwnActor(World world, uint id)
		{
			var actor = world.GetActorById(id);
			return actor != null && !actor.IsDead && actor.IsInWorld && actor.Owner == player ? actor : null;
		}

		void AutoDeployMcv(World world)
		{
			var hasBase = world.Actors.Any(a => a.Owner == player && !a.IsDead && a.Info.HasTraitInfo<BuildingInfo>());
			if (hasBase)
				return;

			var mcv = world.Actors.FirstOrDefault(a => a.Owner == player && !a.IsDead && a.IsInWorld
				&& a.Info.HasTraitInfo<TransformsInfo>() && a.Info.HasTraitInfo<MobileInfo>());

			if (mcv != null && mcv.IsIdle)
				pending.Enqueue(new Order("DeployTransform", mcv, false));
		}

		void AutoPlaceReadyBuildings(World world)
		{
			foreach (var queue in Queues(world))
			{
				var done = queue.AllQueued().FirstOrDefault(i => i.Done);
				if (done == null || !world.Map.Rules.Actors.TryGetValue(done.Item, out var ai))
					continue;

				var bi = ai.TraitInfoOrDefault<BuildingInfo>();
				if (bi == null)
					continue;

				var cell = ChoosePlacementCell(world, ai, bi);
				if (cell == null)
					continue;

				var orderString = ai.HasTraitInfo<LineBuildInfo>() ? "LineBuild" : "PlaceBuilding";
				pending.Enqueue(new Order(orderString, player.PlayerActor, Target.FromCell(world, cell.Value), false)
				{
					TargetString = done.Item,
					ExtraLocation = CPos.Zero,
					ExtraData = queue.Actor.ActorID,
					SuppressVisualFeedback = true
				});
			}
		}

		CPos? ChoosePlacementCell(World world, ActorInfo ai, BuildingInfo bi)
		{
			var buildings = world.Actors
				.Where(a => a.Owner == player && !a.IsDead && a.Info.HasTraitInfo<BuildingInfo>())
				.ToList();

			var center = buildings.Count > 0
				? world.Map.CellContaining(new WPos(
					(int)(buildings.Sum(b => (long)b.CenterPosition.X) / buildings.Count),
					(int)(buildings.Sum(b => (long)b.CenterPosition.Y) / buildings.Count), 0))
				: player.HomeLocation;

			placementOffsets ??= GenerateOffsets(info.PlacementRadius);

			foreach (var offset in placementOffsets)
			{
				var cell = center + offset;
				if (!world.Map.Contains(cell))
					continue;

				if (world.CanPlaceBuilding(cell, ai, bi, null) && bi.IsCloseEnoughToBase(world, player, ai, cell))
					return cell;
			}

			return null;
		}

		static CVec[] GenerateOffsets(int radius)
		{
			var offsets = new List<CVec>();
			for (var x = -radius; x <= radius; x++)
				for (var y = -radius; y <= radius; y++)
					offsets.Add(new CVec(x, y));

			return [.. offsets.OrderBy(o => o.LengthSquared)];
		}

		static string GetString(JsonElement el, string name)
		{
			return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
		}

		static int GetInt(JsonElement el, string name, int fallback)
		{
			return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
		}

		static bool GetBool(JsonElement el, string name)
		{
			return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
		}

		static bool TryGetCell(World world, JsonElement el, string name, out CPos cell)
		{
			cell = CPos.Zero;
			if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != 2)
				return false;

			cell = new CPos(v[0].GetInt32(), v[1].GetInt32());
			return world.Map.Contains(cell);
		}
	}
}
