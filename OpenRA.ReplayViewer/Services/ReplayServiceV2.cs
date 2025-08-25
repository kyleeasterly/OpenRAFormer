using System.IO.Compression;
using OpenRA;
using OpenRA.FileFormats;
using OpenRA.Graphics;
using OpenRA.Primitives;

namespace OpenRA.ReplayViewer.Services;

public class ReplayServiceV2
{
	public class ReplayInfo
	{
		public string FilePath { get; set; } = "";
		public GameInformation? GameInfo { get; set; }
		public SimpleMapLoaderService.SimpleMap? Map { get; set; }
		public string? Error { get; set; }
		public string? MapPath { get; set; }
	}

	private readonly ILogger<ReplayServiceV2> logger;
	private readonly IWebHostEnvironment environment;
	private readonly SimpleMapLoaderService mapLoader;
	private readonly AssetLoaderService assetLoader;

	public ReplayServiceV2(
		ILogger<ReplayServiceV2> logger,
		IWebHostEnvironment environment,
		SimpleMapLoaderService mapLoader,
		AssetLoaderService assetLoader)
	{
		this.logger = logger;
		this.environment = environment;
		this.mapLoader = mapLoader;
		this.assetLoader = assetLoader;
	}

	public async Task<ReplayInfo> LoadReplayAsync(string filePath)
	{
		var result = new ReplayInfo { FilePath = filePath };
		logger.LogInformation("=== Loading replay: {FilePath} ===", filePath);

		try
		{
			if (!File.Exists(filePath))
			{
				result.Error = $"File not found: {filePath}";
				logger.LogError("Replay file not found: {FilePath}", filePath);
				return result;
			}

			await Task.Run(() =>
			{
				logger.LogInformation("Reading replay metadata...");
				
				// Read replay metadata
				var metadata = ReplayMetadata.Read(filePath);
				if (metadata == null)
				{
					result.Error = "Failed to read replay metadata";
					logger.LogError("Failed to read replay metadata from {FilePath}", filePath);
					return;
				}

				result.GameInfo = metadata.GameInfo;
				var modId = metadata.GameInfo.Mod ?? "ra";
				
				logger.LogInformation("Replay metadata loaded:");
				logger.LogInformation("  Map Title: {MapTitle}", metadata.GameInfo.MapTitle);
				logger.LogInformation("  Map UID: {MapUid}", metadata.GameInfo.MapUid);
				logger.LogInformation("  Mod: {Mod}", modId);
				logger.LogInformation("  Has embedded map data: {HasData}", !string.IsNullOrEmpty(metadata.GameInfo.MapData));

				// Try to load the map
				if (!string.IsNullOrEmpty(metadata.GameInfo.MapData))
				{
					logger.LogInformation("Loading embedded map data ({Length} bytes)...", metadata.GameInfo.MapData.Length);
					
					// Map is embedded in the replay
					var mapData = Convert.FromBase64String(metadata.GameInfo.MapData);
					logger.LogInformation("Decoded map data: {Length} bytes", mapData.Length);
					
					result.Map = mapLoader.LoadMapFromData(mapData);

					if (!string.IsNullOrEmpty(result.Map.Error))
					{
						result.Error = result.Map.Error;
						logger.LogError("Failed to load embedded map: {Error}", result.Map.Error);
						return;
					}
					
					logger.LogInformation("Successfully loaded embedded map");
					logger.LogInformation("  Map size: {Width}x{Height}", result.Map.Bounds.Width, result.Map.Bounds.Height);
					logger.LogInformation("  Tileset: {Tileset}", result.Map.Tileset ?? "unknown");
				}
				else if (!string.IsNullOrEmpty(metadata.GameInfo.MapTitle))
				{
					logger.LogInformation("No embedded map data, searching for local map with title: {MapTitle}", metadata.GameInfo.MapTitle);
					
					// Try to find the map locally by title
					var mapInfo = mapLoader.FindMapByTitle(metadata.GameInfo.MapTitle, modId);
					if (mapInfo != null)
					{
						result.MapPath = mapInfo["Path"];
						logger.LogInformation("Found local map at: {Path}", result.MapPath);
						
						result.Map = mapLoader.LoadMapFromPath(result.MapPath);

						if (!string.IsNullOrEmpty(result.Map.Error))
						{
							result.Error = result.Map.Error;
							logger.LogError("Failed to load local map: {Error}", result.Map.Error);
							return;
						}
						
						logger.LogInformation("Successfully loaded local map");
						logger.LogInformation("  Map size: {Width}x{Height}", result.Map.Bounds.Width, result.Map.Bounds.Height);
						logger.LogInformation("  Tileset: {Tileset}", result.Map.Tileset ?? "unknown");
					}
					else
					{
						result.Error = $"Map not found locally: {metadata.GameInfo.MapTitle}";
						logger.LogError("Map not found locally: {MapTitle}", metadata.GameInfo.MapTitle);
						return;
					}
				}
				else
				{
					result.Error = "No map data available in replay";
					logger.LogError("No map data or UID available in replay");
					return;
				}
			});
			
			if (string.IsNullOrEmpty(result.Error))
			{
				logger.LogInformation("=== Replay loaded successfully ===");
			}
			else
			{
				logger.LogError("=== Replay loading failed: {Error} ===", result.Error);
			}
		}
		catch (Exception ex)
		{
			result.Error = $"Error loading replay: {ex.Message}";
			logger.LogError(ex, "Failed to load replay from {FilePath}", filePath);
		}

		return result;
	}

	public async Task<byte[]?> RenderMapAsync(ReplayInfo replayInfo)
	{
		if (replayInfo.Map?.Tiles == null || replayInfo.GameInfo == null)
			return null;

		try
		{
			var modId = replayInfo.GameInfo.Mod ?? "ra";
			var tilesetName = replayInfo.Map.Tileset ?? "temperat";

			// Load tileset assets
			var assets = await assetLoader.LoadTilesetAsync(modId, tilesetName);
			if (!string.IsNullOrEmpty(assets.Error))
			{
				logger.LogWarning("Failed to load tileset {Tileset}: {Error}", tilesetName, assets.Error);
				// Fall back to simple rendering
				return await RenderMapSimple(replayInfo.Map);
			}

			// Render the map with assets
			return await RenderMapWithAssets(replayInfo.Map, assets);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to render map");
			return null;
		}
	}

	private async Task<byte[]?> RenderMapWithAssets(SimpleMapLoaderService.SimpleMap map, AssetLoaderService.TilesetAssets assets)
	{
		if (map.Tiles == null)
			return null;

		try
		{
			return await Task.Run(() =>
			{
				var width = map.Tiles.GetLength(0);
				var height = map.Tiles.GetLength(1);
				var tileSize = 24;
				
				logger.LogInformation("Rendering map: {Width}x{Height} tiles, {TemplateCount} templates available", 
					width, height, assets.Templates.Count);
				
				// Collect statistics
				var templateUsage = new Dictionary<ushort, int>();
				var missingTiles = 0;
				var renderedTiles = 0;

				using (var bitmap = new System.Drawing.Bitmap(width * tileSize, height * tileSize))
				using (var g = System.Drawing.Graphics.FromImage(bitmap))
				{
					g.Clear(System.Drawing.Color.Black);

					for (var y = 0; y < height; y++)
					{
						for (var x = 0; x < width; x++)
						{
							var tile = map.Tiles[x, y];
							
							// Track template usage
							if (!templateUsage.ContainsKey(tile.Type))
								templateUsage[tile.Type] = 0;
							templateUsage[tile.Type]++;
							
							// tile.Type is the template ID, tile.Index is the tile index within that template
							if (assets.Templates.TryGetValue(tile.Type, out var template))
							{
								// Use the tile index directly - it specifies which tile in the template
								var tileIndex = tile.Index;
								
								if (template.TileImages.TryGetValue(tileIndex, out var imageData))
								{
									using (var stream = new MemoryStream(imageData))
									using (var tileImage = System.Drawing.Image.FromStream(stream))
									{
										var destX = x * tileSize;
										var destY = y * tileSize;
										g.DrawImage(tileImage, destX, destY, tileSize, tileSize);
										renderedTiles++;
									}
								}
								else
								{
									// Tile index not found in template
									missingTiles++;
									logger.LogDebug("Tile index {Index} not found in template {TemplateId}, using fallback", 
										tileIndex, tile.Type);
									
									// Try to use first tile as fallback
									if (template.TileImages.Count > 0 && template.TileImages.TryGetValue(0, out var fallbackData))
									{
										using (var stream = new MemoryStream(fallbackData))
										using (var tileImage = System.Drawing.Image.FromStream(stream))
										{
											var destX = x * tileSize;
											var destY = y * tileSize;
											g.DrawImage(tileImage, destX, destY, tileSize, tileSize);
										}
									}
									else
									{
										// No tiles available - use color fallback
										var color = GetSimpleTileColor(tile.Type);
										using (var brush = new System.Drawing.SolidBrush(color))
										{
											g.FillRectangle(brush, x * tileSize, y * tileSize, tileSize, tileSize);
										}
									}
								}
							}
							else
							{
								// Unknown template - use fallback color
								missingTiles++;
								var color = GetSimpleTileColor(tile.Type);
								using (var brush = new System.Drawing.SolidBrush(color))
								{
									g.FillRectangle(brush, x * tileSize, y * tileSize, tileSize, tileSize);
								}
							}
						}
					}
					
					// Log rendering statistics
					var totalTiles = width * height;
					logger.LogInformation("Map rendering complete: {Rendered}/{Total} tiles rendered, {Missing} missing tiles", 
						renderedTiles, totalTiles, missingTiles);
					
					if (templateUsage.Count > 0)
					{
						var topTemplates = templateUsage.OrderByDescending(kvp => kvp.Value).Take(5);
						logger.LogInformation("Top 5 template usage: {Templates}", 
							string.Join(", ", topTemplates.Select(kvp => $"ID {kvp.Key}: {kvp.Value} tiles")));
					}
					
					if (missingTiles > totalTiles * 0.1) // More than 10% missing
					{
						logger.LogWarning("High number of missing tiles detected ({Percentage:F1}%). Check tileset loading.", 
							(missingTiles * 100.0) / totalTiles);
					}

					using (var stream = new MemoryStream())
					{
						bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
						return stream.ToArray();
					}
				}
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to render map with assets");
			return null;
		}
	}

	private async Task<byte[]?> RenderMapSimple(SimpleMapLoaderService.SimpleMap map)
	{
		if (map.Tiles == null)
			return null;

		try
		{
			return await Task.Run(() =>
			{
				var width = map.Tiles.GetLength(0);
				var height = map.Tiles.GetLength(1);
				var tileSize = 24;

				using (var bitmap = new System.Drawing.Bitmap(width * tileSize, height * tileSize))
				using (var g = System.Drawing.Graphics.FromImage(bitmap))
				{
					g.Clear(System.Drawing.Color.Black);

					for (var y = 0; y < height; y++)
					{
						for (var x = 0; x < width; x++)
						{
							var tile = map.Tiles[x, y];

							// Simple color based on tile type
							var color = GetSimpleTileColor(tile.Type);
							using (var brush = new System.Drawing.SolidBrush(color))
							{
								g.FillRectangle(brush, x * tileSize, y * tileSize, tileSize, tileSize);
							}

							// Add grid
							using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(32, 0, 0, 0), 1))
							{
								g.DrawRectangle(pen, x * tileSize, y * tileSize, tileSize - 1, tileSize - 1);
							}
						}
					}

					using (var stream = new MemoryStream())
					{
						bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
						return stream.ToArray();
					}
				}
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to render map (simple)");
			return null;
		}
	}

	private System.Drawing.Color GetSimpleTileColor(ushort tileType)
	{
		// Basic color mapping based on common tile type ranges
		if (tileType == 0 || tileType == 255 || tileType == 65535)
			return System.Drawing.Color.FromArgb(76, 140, 43); // Clear/grass
		else if (tileType < 16)
			return System.Drawing.Color.FromArgb(64, 64, 192); // Water
		else if (tileType < 32)
			return System.Drawing.Color.FromArgb(194, 178, 128); // Beach
		else if (tileType < 48)
			return System.Drawing.Color.FromArgb(128, 128, 128); // Road
		else if (tileType < 64)
			return System.Drawing.Color.FromArgb(96, 96, 96); // Rock
		else if (tileType < 80)
			return System.Drawing.Color.FromArgb(34, 85, 34); // Trees
		else
			return System.Drawing.Color.FromArgb(100, 100, 100); // Unknown
	}
}