using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenRA.Mods.Common.Orders;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Reads external orders from text files and injects them into the game.")]
	public class ExternalOrderReaderInfo : TraitInfo
	{
		[Desc("Directory path where order files will be read from.")]
		public readonly string InputDirectory = @"C:\OpenRATest_Orders\input";

		[Desc("Check for new order files every N game ticks (25 ticks = 1 second).")]
		public readonly int CheckInterval = 25; // Check every second

		[Desc("Enable or disable the external order reader.")]
		public readonly bool Enabled = true;

		[Desc("Maximum number of orders to process per tick to avoid freezing.")]
		public readonly int MaxOrdersPerTick = 10;

		[Desc("Delete order files after processing.")]
		public readonly bool DeleteAfterProcessing = true;

		public override object Create(ActorInitializer init) { return new ExternalOrderReader(this); }
	}

	public class ExternalOrderReader : ITick
	{
		readonly ExternalOrderReaderInfo info;
		readonly HashSet<string> processedFiles = new HashSet<string>();
		readonly Queue<string> pendingOrders = new Queue<string>();
		World world;
		int lastCheckTick = 0;
		ExternalOrderParser parser;

		public ExternalOrderReader(ExternalOrderReaderInfo info)
		{
			this.info = info;
		}

		void ITick.Tick(Actor self)
		{
			if (!info.Enabled)
				return;

			world = self.World;

			// Initialize parser on first tick when world is available
			if (parser == null)
				parser = new ExternalOrderParser(world);

			// Check for new files at intervals
			if (world.WorldTick - lastCheckTick >= info.CheckInterval)
			{
				lastCheckTick = world.WorldTick;
				CheckForNewOrderFiles();
			}

			// Process pending orders
			ProcessPendingOrders();
		}

		void CheckForNewOrderFiles()
		{
			try
			{
				if (!Directory.Exists(info.InputDirectory))
				{
					Directory.CreateDirectory(info.InputDirectory);
					return;
				}

				var files = Directory.GetFiles(info.InputDirectory, "*.txt")
					.Where(f => !processedFiles.Contains(f))
					.OrderBy(f => File.GetCreationTime(f));

				foreach (var file in files)
				{
					try
					{
						ReadOrderFile(file);
						processedFiles.Add(file);

						if (info.DeleteAfterProcessing)
						{
							try
							{
								File.Delete(file);
							}
							catch (Exception e)
							{
								Log.Write("debug", $"Failed to delete processed order file {file}: {e.Message}");
							}
						}
					}
					catch (Exception e)
					{
						Log.Write("debug", $"Failed to read order file {file}: {e.Message}");
						processedFiles.Add(file); // Don't retry failed files
					}
				}
			}
			catch (Exception e)
			{
				Log.Write("debug", $"Failed to check for order files: {e.Message}");
			}
		}

		void ReadOrderFile(string filePath)
		{
			var lines = File.ReadAllLines(filePath);
			foreach (var line in lines)
			{
				var trimmed = line.Trim();
				if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#") && !trimmed.StartsWith("//"))
				{
					pendingOrders.Enqueue(trimmed);
				}
			}

			Log.Write("debug", $"Read {lines.Length} lines from order file: {Path.GetFileName(filePath)}");
		}

		void ProcessPendingOrders()
		{
			var ordersProcessed = 0;

			while (pendingOrders.Count > 0 && ordersProcessed < info.MaxOrdersPerTick)
			{
				var orderText = pendingOrders.Dequeue();
				
				try
				{
					var orders = parser.ParseOrder(orderText);
					if (orders != null && orders.Length > 0)
					{
						foreach (var order in orders)
						{
							if (order != null)
							{
								Log.Write("debug", $"Issuing external order: {order.OrderString}");
								world.IssueOrder(order);
							}
						}
						ordersProcessed++;
					}
				}
				catch (Exception e)
				{
					Log.Write("debug", $"Failed to parse/issue order '{orderText}': {e.Message}");
				}
			}
		}
	}
}