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
using System.Linq;
using OpenRA.FileFormats;

namespace OpenRA.Network
{
	public sealed class ReplayRecorder
	{
		// Arbitrary value.
		const int CreateReplayFileMaxRetryCount = 128;

		public ReplayMetadata Metadata;
		BinaryWriter writer;
		readonly Func<string> chooseFilename;
		MemoryStream preStartBuffer = new();
		HumanReadableOrderLogger humanReadableLogger;

		static bool IsGameStart(byte[] data)
		{
			if (!OrderIO.TryParseOrderPacket(data, out var orders))
				return false;

			return orders.Frame == 0 && orders.Orders.GetOrders(null).Any(o => o.OrderString == "StartGame");
		}

		public ReplayRecorder(Func<string> chooseFilename)
		{
			this.chooseFilename = chooseFilename;

			writer = new BinaryWriter(preStartBuffer);
			humanReadableLogger = new HumanReadableOrderLogger();
		}

		void StartSavingReplay(byte[] initialContent)
		{
			var filename = chooseFilename();
			var mod = Game.ModData.Manifest;
			var dir = Path.Combine(Platform.SupportDir, "Replays", mod.Id, mod.Metadata.Version);

			if (!Directory.Exists(dir))
				Directory.CreateDirectory(dir);

			FileStream file = null;
			var id = -1;
			while (file == null)
			{
				var fullFilename = Path.Combine(dir, id < 0 ? $"{filename}.orarep" : $"{filename}-{id}.orarep");
				id++;
				try
				{
					file = File.Create(fullFilename);
				}
				catch (IOException ex)
				{
					if (id > CreateReplayFileMaxRetryCount)
						throw new ArgumentException($"Error creating replay file \"{filename}.orarep\"", ex);
				}
			}

			file.Write(initialContent);
			writer = new BinaryWriter(file);
		}

		public void Receive(int clientID, byte[] data)
		{
			if (disposed) // TODO: This can be removed once NetworkConnection is fixed to dispose properly.
				return;

			if (preStartBuffer != null && IsGameStart(data))
			{
				writer.Flush();
				var preStartData = preStartBuffer.ToArray();
				preStartBuffer = null;
				StartSavingReplay(preStartData);
			}

			// Log human-readable orders if we can parse them
			if (OrderIO.TryParseOrderPacket(data, out var orders))
			{
				LogHumanReadableOrders(clientID, orders.Frame, orders.Orders);
			}

			writer.Write(clientID);
			writer.Write(data.Length);
			writer.Write(data);
		}

		public void ReceiveFrame(int clientID, int frame, byte[] data)
		{
			var ms = new MemoryStream(4 + data.Length);
			ms.Write(frame);
			ms.Write(data);
			Receive(clientID, ms.GetBuffer());
		}

		void LogHumanReadableOrders(int clientId, int frame, OrderPacket orders)
		{
			if (humanReadableLogger == null)
				return;

			try
			{
				// Get the current world for order parsing
				var world = Game.OrderManager?.World;
				foreach (var order in orders.GetOrders(world))
				{
					humanReadableLogger.LogOrder(frame, clientId, order, world);
				}
			}
			catch (Exception ex)
			{
				Log.Write("debug", $"Failed to log human readable order: {ex}");
			}
		}

		bool disposed;

		public void Dispose()
		{
			if (disposed)
				return;
			disposed = true;

			if (Metadata != null)
			{
				if (Metadata.GameInfo != null)
					Metadata.GameInfo.EndTimeUtc = DateTime.UtcNow;
				Metadata.Write(writer);
			}

			preStartBuffer?.Dispose();
			writer.Close();
			humanReadableLogger?.Dispose();
		}
	}
}
