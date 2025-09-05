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

		public override object Create(ActorInitializer init) 
		{
			return new ExternalOrderReader(this); 
		}
	}

	public class ExternalOrderReader : ITick, INotifyCreated
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

		void INotifyCreated.Created(Actor self)
		{
			if (!info.Enabled)
			{
				Log.Write("debug", "[ExternalOrderReader] is disabled in configuration");
				return;
			}

			// Announce initialization to console only
			Console.WriteLine("[ExternalOrderReader] Initializing...");
			Log.Write("debug", "[ExternalOrderReader] Initializing...");
			
			// Ensure directory exists
			try
			{
				if (!Directory.Exists(info.InputDirectory))
				{
					Directory.CreateDirectory(info.InputDirectory);
					Console.WriteLine($"[ExternalOrderReader] Created input directory: {info.InputDirectory}");
					Log.Write("debug", $"[ExternalOrderReader] Created input directory: {info.InputDirectory}");
				}
				else
				{
					Console.WriteLine($"[ExternalOrderReader] Monitoring: {info.InputDirectory}");
					Log.Write("debug", $"[ExternalOrderReader] Monitoring: {info.InputDirectory}");
				}
				
				Console.WriteLine($"[ExternalOrderReader] Active - checking every {info.CheckInterval / 25f:0.#} seconds");
				Log.Write("debug", $"[ExternalOrderReader] Active - checking every {info.CheckInterval / 25f:0.#} seconds");
			}
			catch (Exception e)
			{
				// Keep error visible in-game
				TextNotificationsManager.AddSystemLine("External Order Reader", $"ERROR: Failed to create directory - {e.Message}");
				Console.WriteLine($"[ExternalOrderReader] ERROR: Failed to create directory - {e.Message}");
				Log.Write("debug", $"[ExternalOrderReader] initialization error: {e}");
			}
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
						var fileName = Path.GetFileName(file);
						Console.WriteLine($"[ExternalOrderReader] Processing file: {fileName}");
						Log.Write("debug", $"[ExternalOrderReader] Processing file: {fileName}");
						
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

						// Trigger next game state snapshot
						var gameStateExporter = world.WorldActor.TraitOrDefault<GameStateExporter>();
						gameStateExporter?.RequestSnapshot();
					}
					catch (Exception e)
					{
						// Keep error visible in-game
						TextNotificationsManager.AddSystemLine("External Orders", $"ERROR reading file: {Path.GetFileName(file)}");
						Console.WriteLine($"[ExternalOrderReader] ERROR reading file: {Path.GetFileName(file)} - {e.Message}");
						Log.Write("debug", $"[ExternalOrderReader] Failed to read order file {file}: {e.Message}");
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
			var validLines = 0;
			
			foreach (var line in lines)
			{
				var trimmed = line.Trim();
				if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#") && !trimmed.StartsWith("//"))
				{
					pendingOrders.Enqueue(trimmed);
					validLines++;
				}
			}

			var fileName = Path.GetFileName(filePath);
			if (validLines > 0)
			{
				Console.WriteLine($"[ExternalOrderReader] Queued {validLines} orders from {fileName}");
				Log.Write("debug", $"[ExternalOrderReader] Queued {validLines} orders from {fileName}");
			}
			else
			{
				Console.WriteLine($"[ExternalOrderReader] No valid orders in {fileName}");
				Log.Write("debug", $"[ExternalOrderReader] No valid orders in {fileName}");
			}
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
								Console.WriteLine($"[ExternalOrderReader] Executing: {order.OrderString} for {order.Player?.PlayerName ?? "Unknown"}");
								Log.Write("debug", $"[ExternalOrderReader] Issuing external order: {order.OrderString} for {order.Player?.PlayerName ?? "Unknown"}");
								world.IssueOrder(order);
							}
						}
						ordersProcessed++;
					}
				}
				catch (Exception e)
				{
					var preview = orderText.Length > 50 ? orderText.Substring(0, 50) + "..." : orderText;
					// Keep error visible in-game
					TextNotificationsManager.AddSystemLine("External Orders", $"ERROR: {preview}");
					Console.WriteLine($"[ExternalOrderReader] ERROR parsing order: {preview} - {e.Message}");
					Log.Write("debug", $"[ExternalOrderReader] Failed to parse/issue order '{orderText}': {e.Message}");
				}
			}
		}
	}
}