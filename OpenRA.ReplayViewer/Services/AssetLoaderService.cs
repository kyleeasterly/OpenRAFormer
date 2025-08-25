using System.Drawing;
using System.Drawing.Imaging;
using OpenRA;
using OpenRA.FileFormats;
using OpenRA.Graphics;
using OpenRA.Primitives;
using Bitmap = System.Drawing.Bitmap;
using Image = System.Drawing.Image;

namespace OpenRA.ReplayViewer.Services;

public class AssetLoaderService
{
	private readonly ILogger<AssetLoaderService> logger;
	private readonly IWebHostEnvironment environment;
	private readonly TilesetLoaderService tilesetLoaderService;
	private readonly Dictionary<string, TilesetAssets> tilesetCache = new();

	public class TilesetAssets
	{
		public Dictionary<ushort, TerrainTemplate> Templates { get; set; } = new();
		public Dictionary<string, TerrainTypeInfo> TerrainTypes { get; set; } = new();
		public string? Error { get; set; }
	}

	public class TerrainTemplate
	{
		public ushort Id { get; set; }
		public string Name { get; set; } = "";
		public OpenRA.Primitives.Size Size { get; set; }
		public Dictionary<int, byte[]> TileImages { get; set; } = new();
		public Dictionary<int, string> TileTerrainTypes { get; set; } = new();
	}

	public class TerrainTypeInfo
	{
		public string Type { get; set; } = "";
		public System.Drawing.Color Color { get; set; }
		public bool IsWater { get; set; }
	}

	public AssetLoaderService(
		ILogger<AssetLoaderService> logger,
		IWebHostEnvironment environment,
		TilesetLoaderService tilesetLoaderService)
	{
		this.logger = logger;
		this.environment = environment;
		this.tilesetLoaderService = tilesetLoaderService;
	}

	public async Task<TilesetAssets> LoadTilesetAsync(string modId, string tilesetName)
	{
		var cacheKey = $"{modId}:{tilesetName}";
		if (tilesetCache.TryGetValue(cacheKey, out var cached))
			return cached;

		var assets = new TilesetAssets();

		try
		{
			await Task.Run(() =>
			{
				// Use the TilesetLoaderService to load the tileset
				var tilesetData = tilesetLoaderService.LoadTileset(modId, tilesetName);
				
				if (!string.IsNullOrEmpty(tilesetData.Error))
				{
					assets.Error = tilesetData.Error;
					return;
				}

				// Convert TilesetData to TilesetAssets format
				foreach (var kvp in tilesetData.TerrainTypes)
				{
					assets.TerrainTypes[kvp.Key] = new TerrainTypeInfo
					{
						Type = kvp.Value.Type,
						Color = kvp.Value.Color,
						IsWater = kvp.Value.IsWater
					};
				}

				foreach (var kvp in tilesetData.Templates)
				{
					var template = kvp.Value;
					var assetTemplate = new TerrainTemplate
					{
						Id = template.Id,
						Name = template.Name,
						Size = new OpenRA.Primitives.Size(template.Size.X, template.Size.Y),
						TileImages = new Dictionary<int, byte[]>(),
						TileTerrainTypes = new Dictionary<int, string>()
					};

					// Convert sprites to tile images
					for (int i = 0; i < template.Sprites.Count; i++)
					{
						var sprite = template.Sprites[i];
						assetTemplate.TileImages[i] = sprite.ImageData;
						
						if (template.Tiles.TryGetValue(i, out var tileInfo))
						{
							assetTemplate.TileTerrainTypes[i] = tileInfo.TerrainType;
						}
					}

					assets.Templates[assetTemplate.Id] = assetTemplate;
				}
			});

			tilesetCache[cacheKey] = assets;
		}
		catch (Exception ex)
		{
			assets.Error = $"Failed to load tileset: {ex.Message}";
			logger.LogError(ex, "Failed to load tileset {Tileset} for mod {Mod}", tilesetName, modId);
		}

		return assets;
	}

	public async Task<byte[]?> RenderMapWithAssets(Map map, TilesetAssets assets)
	{
		try
		{
			return await Task.Run(() =>
			{
				var bounds = map.Bounds;
				var tileSize = 24;
				var width = bounds.Width * tileSize;
				var height = bounds.Height * tileSize;

				using (var bitmap = new Bitmap(width, height))
				using (var g = System.Drawing.Graphics.FromImage(bitmap))
				{
					// Clear to black
					g.Clear(System.Drawing.Color.Black);

					// Draw each tile
					foreach (var cell in map.AllCells)
					{
						var tile = map.Tiles[cell];
						var pos = new int2(cell.X - bounds.Left, cell.Y - bounds.Top);
						
						if (assets.Templates.TryGetValue(tile.Type, out var template))
						{
							var tileIndex = Math.Min(tile.Index, template.TileImages.Count - 1);
							if (template.TileImages.TryGetValue(tileIndex, out var imageData))
							{
								using (var stream = new MemoryStream(imageData))
								using (var tileImage = System.Drawing.Image.FromStream(stream))
								{
									var destX = pos.X * tileSize;
									var destY = pos.Y * tileSize;
									g.DrawImage(tileImage, destX, destY, tileSize, tileSize);
								}
							}
						}
					}

					// Convert to PNG
					using (var stream = new MemoryStream())
					{
						bitmap.Save(stream, ImageFormat.Png);
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
}