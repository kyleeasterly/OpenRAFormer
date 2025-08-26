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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
namespace OpenRA.Network
{
	public sealed class HumanReadableOrderLogger : IDisposable
	{
		const long MaxFileSizeBytes = 3 * 1024 * 1024; // 3MB
		const string LogDirectory = @"C:\OpenRATest_Orders";
		const string GameStateDirectory = @"C:\OpenRATest";

		StreamWriter writer;
		bool isDisposed;
		bool fileSizeLimitReached;

		// For tracking orders between gamestate snapshots
		readonly List<string> ordersBuffer = [];
		string currentGameStateFile;
		int lastGameStateTick = 0;

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
			if (isDisposed)
				return;

			// Skip pregame activities - only log orders with frame > 0
			if (frame <= 0)
				return;

			// Skip handshake and other pregame protocol messages
			if (IsPreGameOrder(order))
				return;

			try
				{
					// Check if a new gamestate file was created and append orders to the previous one
					CheckAndAppendOrdersToGameState(frame);

					var gameTime = FormatGameTime(frame);
					var playerName = GetPlayerName(order, world, clientId);
					var orderDetails = FormatOrderDetails(order);
					var orderLine = $"[{gameTime}] {playerName}: {order.OrderString} {orderDetails}";

					// Add to buffer for gamestate file appending
					ordersBuffer.Add(orderLine);

					// Also write to regular orders file if available
					if (writer != null && !fileSizeLimitReached)
					{
						// Check file size limit
						if (writer.BaseStream.Length > MaxFileSizeBytes)
						{
							writer.WriteLine($"[{gameTime}] --- File size limit reached (3MB), stopping order logging ---");
							writer.Flush();
							fileSizeLimitReached = true;
							return;
						}

						writer.WriteLine(orderLine);
						writer.Flush();
					}
				}
				catch (Exception ex)
				{
					Log.Write("debug", $"Failed to log order: {ex}");
				}
		}

		bool IsPreGameOrder(Order order)
		{
			// Filter out handshake, lobby, and other pregame protocol messages
			var orderString = order.OrderString;
			return orderString == "HandshakeRequest" ||
				   orderString == "HandshakeResponse" ||
				   orderString == "FluentMessage" ||
				   orderString == "Message" ||
				   orderString == "PauseGame" ||
				   orderString == "StartGame" ||
				   orderString.StartsWith("Sync");
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

			// Add unit starting position with friendly name
			if (order.Subject != null && !order.Subject.Disposed && order.Subject.OccupiesSpace != null)
			{
				try
				{
					var startPos = order.Subject.CenterPosition;
					var subjectName = GetFriendlyActorName(order.Subject);
					details.Append($"From:{subjectName}@{startPos}");
				}
				catch
				{
					// Actor might not have valid position (system actors, disposed, etc.)
					var subjectName = GetFriendlyActorName(order.Subject);
					details.Append($"From:{subjectName}@Unknown");
				}
			}

			// Add target information with position
			if (order.Target.Type != Traits.TargetType.Invalid)
			{
				if (details.Length > 0) details.Append(" ");

				if (order.Target.Type == Traits.TargetType.Actor && order.Target.Actor != null && !order.Target.Actor.Disposed)
				{
					try
					{
						var targetPos = order.Target.Actor.CenterPosition;
						var targetName = GetFriendlyActorName(order.Target.Actor);
						if(String.IsNullOrWhiteSpace(targetName))
							details.Append($"Target:{targetName}@{targetPos}");
						else details.Append($"Target:{order.Target.Actor.Info.Name}@{targetPos}");
					}
					catch
					{
						var targetName = GetFriendlyActorName(order.Target.Actor);
						if (String.IsNullOrWhiteSpace(targetName))
							details.Append($"Target:{targetName}@Unknown");
						else details.Append($"Target:{order.Target.Actor.Info.Name}@Unknown");
					}
				}
				else if (order.Target.Type == Traits.TargetType.Terrain)
				{
					details.Append($"Target:{order.Target.CenterPosition}");
				}
				else if (order.Target.Type == Traits.TargetType.FrozenActor)
				{
					var targetPos = order.Target.CenterPosition;
					details.Append($"Target:FrozenActor@{targetPos}");
				}
			}

			// Add extra location (sometimes used for waypoints or secondary targets)
			if (order.ExtraLocation != CPos.Zero)
			{
				if (details.Length > 0) details.Append(" ");
				// Convert cell position to world position for consistency
				var worldPos = order.Subject?.World?.Map?.CenterOfCell(order.ExtraLocation);
				details.Append($"ExtraLocation:{worldPos ?? new WPos(order.ExtraLocation.X, order.ExtraLocation.Y, 0)}");
			}

			// Include other relevant order details
			if (!string.IsNullOrEmpty(order.TargetString))
			{
				if (details.Length > 0) details.Append(" ");
				var targetName = FindFriendlyName(order.TargetString);
				if (String.IsNullOrWhiteSpace(targetName))
					details.Append($"TargetString:{order.TargetString}");
				else details.Append($"TargetString:{targetName}");
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

		void CheckAndAppendOrdersToGameState(int currentFrame)
		{
			try
			{
				if (!Directory.Exists(GameStateDirectory))
					return;

				// Find the most recent gamestate file
				var gameStateFiles = Directory.GetFiles(GameStateDirectory, "gamestate_*.txt")
					.OrderByDescending(File.GetCreationTime)
					.ToList();

				if (gameStateFiles.Count == 0)
					return;

				var newestGameStateFile = gameStateFiles.First();

				// Check if this is a new gamestate file since last check
				if (newestGameStateFile != currentGameStateFile)
				{
					// If we have a previous gamestate file and accumulated orders, append them
					if (currentGameStateFile != null && ordersBuffer.Count > 0)
					{
						AppendOrdersToGameStateFile(currentGameStateFile, ordersBuffer);
						ordersBuffer.Clear();
					}

					// Update to the new gamestate file
					currentGameStateFile = newestGameStateFile;
					
					// Extract tick from filename to track progress
					var filename = Path.GetFileName(newestGameStateFile);
					var tickMatch = Regex.Match(filename, @"tick(\d+)");
					if (tickMatch.Success && int.TryParse(tickMatch.Groups[1].Value, out var tick))
					{
						lastGameStateTick = tick;
					}
				}
			}
			catch (Exception ex)
			{
				Log.Write("debug", $"Failed to check gamestate files: {ex}");
			}
		}

		void AppendOrdersToGameStateFile(string gameStateFilePath, List<string> orders)
		{
			try
			{
				if (orders.Count == 0)
					return;

				// Read existing content
				var existingContent = File.ReadAllText(gameStateFilePath);
				
				// Append orders section
				var sb = new StringBuilder();
				sb.AppendLine();
				sb.AppendLine("## Orders Since Last Game State");
				sb.AppendLine($"*{orders.Count} orders recorded between game state snapshots*");
				sb.AppendLine();
				foreach (var order in orders)
				{
					sb.AppendLine(order);
				}
				sb.AppendLine();
				sb.AppendLine("---");
				sb.AppendLine("*End of orders section*");

				// Write back to file
				File.WriteAllText(gameStateFilePath, existingContent + sb.ToString());
			}
			catch (Exception ex)
			{
				Log.Write("debug", $"Failed to append orders to gamestate file: {ex}");
			}
		}

		string GetFriendlyActorName(Actor actor)
		{
			if (actor == null || actor.Disposed)
				return "Unknown";

			// Check if actor is a building
			if (FriendlyNames.MeanBuildingNames.Contains(actor.Info.Name))
				return FriendlyNames.GetFriendlyBuildingName(actor.Info.Name);
			else
				return FriendlyNames.GetFriendlyUnitName(actor.Info.Name);
		}
		string FindFriendlyName(string internalName)
		{
			// Check if actor is a building
			if (FriendlyNames.MeanBuildingNames.Contains(internalName))
				return FriendlyNames.GetFriendlyBuildingName(internalName);
			else
				return FriendlyNames.GetFriendlyUnitName(internalName);

		}
		public void Dispose()
		{
			if (isDisposed)
				return;

			isDisposed = true;

			try
			{
				// Append any remaining orders to the current gamestate file
				if (currentGameStateFile != null && ordersBuffer.Count > 0)
				{
					AppendOrdersToGameStateFile(currentGameStateFile, ordersBuffer);
					ordersBuffer.Clear();
				}

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
