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
using OpenRA.Mods.Common.LoadScreens;
using OpenRA.Network;
using OpenRA.Widgets;

namespace OpenRA.Mods.LLM
{
	/// <summary>
	/// When ORF_RUN_DIR points at a run directory containing match.json, boots straight
	/// into a local match: the local client becomes a spectator, every configured player
	/// slot is filled with an externally-driven bot, and the game is started.
	/// Otherwise behaves exactly like the default load screen.
	/// </summary>
	public sealed class LlmLoadScreen : BlankLoadScreen
	{
		public override void StartGame(Arguments args)
		{
			if (!LlmRun.Active)
			{
				base.StartGame(args);
				return;
			}

			Launch = new LaunchArguments(args);
			Ui.ResetAll();
			Game.Settings.Save();

			var match = LlmRun.Match;
			var map = Game.ModData.MapCache.FirstOrDefault(m =>
				m.Uid == match.Map || Path.GetFileName(m.Path) == match.Map);

			if (map == null)
				throw new InvalidDataException($"LlmLoadScreen: map '{match.Map}' was not found in the map cache.");

			OrderManager om = null;
			var phase = 0;

			void OnLobbyInfoChanged()
			{
				try
				{
					var lobby = om.LobbyInfo;
					if (phase == 0)
					{
						if (lobby.Slots.Count == 0 || lobby.ClientWithIndex(om.Connection.LocalClientId) == null)
							return;

						phase = 1;
						var slots = SortedSlots(lobby);
						var orders = new List<Order>
						{
							Order.Command("allow_spectators true"),
							Order.Command("spectate")
						};

						for (var i = 0; i < match.Players.Count && i < slots.Count; i++)
							orders.Add(Order.Command($"slot_bot {slots[i]} {om.Connection.LocalClientId} {match.Players[i].Bot}"));

						foreach (var option in match.Options)
							orders.Add(Order.Command($"option {option.Key} {option.Value}"));

						// Do NOT go Ready here: a Ready client may only issue state/startgame
						// commands, and a local server auto-starts once all humans are Ready —
						// either would make phase 1's faction/spawn/team configuration dead
						// letters (they were, silently, until spawns became non-default).
						foreach (var order in orders)
							om.IssueOrder(order);
					}
					else if (phase == 1)
					{
						var bots = lobby.Clients.Where(c => c.Bot != null).ToList();
						if (bots.Count < Math.Min(match.Players.Count, lobby.Slots.Count))
							return;

						phase = 2;
						Game.LobbyInfoChanged -= OnLobbyInfoChanged;

						var slots = SortedSlots(lobby);
						var orders = new List<Order>();
						foreach (var bot in bots)
						{
							var index = slots.IndexOf(bot.Slot);
							if (index < 0 || index >= match.Players.Count)
								continue;

							var pc = match.Players[index];
							if (!string.IsNullOrEmpty(pc.Faction) && pc.Faction != "Random")
								orders.Add(Order.Command($"faction {bot.Index} {pc.Faction}"));

							if (pc.Spawn > 0)
								orders.Add(Order.Command($"spawn {bot.Index} {pc.Spawn}"));

							if (pc.Team > 0)
								orders.Add(Order.Command($"team {bot.Index} {pc.Team}"));
						}

						orders.Add(Order.Command($"state {Session.ClientState.Ready}"));
						orders.Add(Order.Command("startgame"));
						foreach (var order in orders)
							om.IssueOrder(order);
					}
				}
				catch (Exception e)
				{
					Log.Write("debug", $"LlmLoadScreen: lobby setup failed: {e}");
					Game.LobbyInfoChanged -= OnLobbyInfoChanged;
				}
			}

			Game.LobbyInfoChanged += OnLobbyInfoChanged;
			om = Game.JoinServer(Game.CreateLocalServer(map.Uid), "");
		}

		static List<string> SortedSlots(Session lobby)
		{
			return [.. lobby.Slots.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)];
		}
	}
}
