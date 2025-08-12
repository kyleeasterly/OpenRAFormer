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
using System.IO;
using System.Text;

namespace OpenRA.Network
{
	public sealed class HumanReadableOrderLogger : IDisposable
	{
		const long MaxFileSizeBytes = 3 * 1024 * 1024; // 3MB
		const string LogDirectory = @"C:\OpenRATest_Orders";

		StreamWriter writer;
		bool isDisposed;
		bool fileSizeLimitReached;

		public HumanReadableOrderLogger()
		{
			try
			{
				if (!Directory.Exists(LogDirectory))
					Directory.CreateDirectory(LogDirectory);

				var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
				var filename = Path.Combine(LogDirectory, $"orders_{timestamp}.txt");

				writer = new StreamWriter(filename, false, Encoding.UTF8);
				writer.WriteLine($"OpenRA Human Readable Order Log - Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
				writer.WriteLine("Format: [GameTime] Player: OrderString (Details)");
				writer.WriteLine("----------------------------------------");
				writer.Flush();
			}
			catch (Exception ex)
			{
				Log.Write("debug", $"Failed to create human readable order logger: {ex}");
				writer = null;
			}
		}

		public void LogOrder(int frame, int clientId, Order order, World world)
		{
			if (writer == null || isDisposed || fileSizeLimitReached)
				return;

			try
			{
				// Check file size limit
				if (writer.BaseStream.Length > MaxFileSizeBytes)
				{
					writer.WriteLine($"[{FormatGameTime(frame)}] --- File size limit reached (3MB), stopping order logging ---");
					writer.Flush();
					fileSizeLimitReached = true;
					return;
				}

				var gameTime = FormatGameTime(frame);
				var playerName = GetPlayerName(order, world, clientId);
				var orderDetails = FormatOrderDetails(order);

				writer.WriteLine($"[{gameTime}] {playerName}: {order.OrderString} {orderDetails}");
				writer.Flush();
			}
			catch (Exception ex)
			{
				Log.Write("debug", $"Failed to log order: {ex}");
			}
		}

		string FormatGameTime(int frame)
		{
			// Assuming 15 FPS (typical OpenRA tick rate)
			var totalSeconds = frame / 15;
			var minutes = totalSeconds / 60;
			var seconds = totalSeconds % 60;
			return $"{minutes:D2}:{seconds:D2}";
		}

		string GetPlayerName(Order order, World world, int clientId)
		{
			if (order.Player != null)
				return order.Player.PlayerName;

			// Try to get player from client ID
			if (world?.LobbyInfo != null)
			{
				foreach (var client in world.LobbyInfo.Clients)
				{
					if (client.Index == clientId)
						return client.Name;
				}
			}

			return $"Client{clientId}";
		}

		string FormatOrderDetails(Order order)
		{
			var details = new StringBuilder();

			if (!order.Target.IsEmpty())
			{
				if (order.Target.Type == Traits.TargetType.Actor && order.Target.Actor != null)
					details.Append($"Target:{order.Target.Actor.Info.Name}");
				else if (order.Target.Type == Traits.TargetType.Terrain)
					details.Append($"Target:{order.Target.CenterPosition}");
				else if (order.Target.Type == Traits.TargetType.FrozenActor)
					details.Append($"Target:FrozenActor");
			}

			if (!string.IsNullOrEmpty(order.TargetString))
			{
				if (details.Length > 0) details.Append(" ");
				details.Append($"TargetString:\"{order.TargetString}\"");
			}

			if (order.ExtraLocation != CPos.Zero)
			{
				if (details.Length > 0) details.Append(" ");
				details.Append($"ExtraLocation:{order.ExtraLocation}");
			}

			if (order.ExtraData != 0)
			{
				if (details.Length > 0) details.Append(" ");
				details.Append($"ExtraData:{order.ExtraData}");
			}

			if (order.Queued)
			{
				if (details.Length > 0) details.Append(" ");
				details.Append("Queued:true");
			}

			if (order.ExtraActors != null && order.ExtraActors.Length > 0)
			{
				if (details.Length > 0) details.Append(" ");
				details.Append($"ExtraActors:{order.ExtraActors.Length}");
			}

			if (order.GroupedActors != null && order.GroupedActors.Length > 0)
			{
				if (details.Length > 0) details.Append(" ");
				details.Append($"GroupedActors:{order.GroupedActors.Length}");
			}

			return details.Length > 0 ? $"({details})" : "";
		}

		public void Dispose()
		{
			if (isDisposed)
				return;

			isDisposed = true;

			try
			{
				if (writer != null)
				{
					writer.WriteLine($"----------------------------------------");
					writer.WriteLine($"OpenRA Human Readable Order Log - Ended at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
					writer.Flush();
					writer.Dispose();
				}
			}
			catch (Exception ex)
			{
				Log.Write("debug", $"Failed to dispose human readable order logger: {ex}");
			}
		}
	}
}
