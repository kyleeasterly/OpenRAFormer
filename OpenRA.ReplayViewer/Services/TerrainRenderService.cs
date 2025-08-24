using System.Drawing;
using System.Drawing.Imaging;
using OpenRA;
using OpenRA.FileSystem;
using OpenRA.Graphics;
using OpenRA.Primitives;
using Color = System.Drawing.Color;
using Bitmap = System.Drawing.Bitmap;
using Image = System.Drawing.Image;
using SolidBrush = System.Drawing.SolidBrush;
using Pen = System.Drawing.Pen;

namespace OpenRA.ReplayViewer.Services;

public class TerrainRenderService
{
	public class TerrainAssets
	{
		public Dictionary<ushort, TerrainTemplate> Templates { get; set; } = new();
		public string? Error { get; set; }
	}

	public class TerrainTemplate
	{
		public ushort Id { get; set; }
		public string Name { get; set; } = "";
		public int TileCount { get; set; }
		public List<byte[]> TileImages { get; set; } = new();
		public int TileWidth { get; set; } = 24;
		public int TileHeight { get; set; } = 24;
	}

	private readonly ILogger<TerrainRenderService> logger;
	private readonly IWebHostEnvironment environment;
	private readonly Dictionary<string, TerrainAssets> cachedAssets = new();

	public TerrainRenderService(ILogger<TerrainRenderService> logger, IWebHostEnvironment environment)
	{
		this.logger = logger;
		this.environment = environment;
	}

	public async Task<TerrainAssets> LoadTerrainAssetsAsync(string mod)
	{
		// Check cache first
		if (cachedAssets.TryGetValue(mod, out var cached))
			return cached;

		var assets = new TerrainAssets();

		try
		{
			await Task.Run(() =>
			{
				// Find the mod directory
				var modPath = Path.Combine(environment.ContentRootPath, "..", "mods", mod.ToLower());
				if (!Directory.Exists(modPath))
				{
					assets.Error = $"Mod directory not found: {modPath}";
					return;
				}

				// Load terrain definitions from YAML
				var terrainYamlPath = Path.Combine(modPath, "rules", "terrain.yaml");
				if (!File.Exists(terrainYamlPath))
				{
					// Try sequences instead
					terrainYamlPath = Path.Combine(modPath, "sequences", "terrain.yaml");
				}

				if (!File.Exists(terrainYamlPath))
				{
					assets.Error = "Terrain definitions not found";
					return;
				}

				// For now, we'll create some placeholder templates
				// In a real implementation, we'd parse the YAML and load actual sprites
				CreatePlaceholderTemplates(assets, mod);
			});

			cachedAssets[mod] = assets;
		}
		catch (Exception ex)
		{
			assets.Error = $"Failed to load terrain assets: {ex.Message}";
			logger.LogError(ex, "Failed to load terrain assets for mod {Mod}", mod);
		}

		return assets;
	}

	private void CreatePlaceholderTemplates(TerrainAssets assets, string mod)
	{
		// Create some basic terrain templates with color-coded tiles
		// These would normally be loaded from actual game assets

		// Clear terrain (grass/sand/etc)
		assets.Templates[0] = CreateColorTemplate(0, "clear", Color.FromArgb(76, 140, 43), 1);
		
		// Water
		assets.Templates[1] = CreateColorTemplate(1, "water", Color.FromArgb(64, 64, 192), 1);
		
		// Road
		assets.Templates[2] = CreateColorTemplate(2, "road", Color.FromArgb(128, 128, 128), 1);
		
		// Rock/Mountain
		assets.Templates[3] = CreateColorTemplate(3, "rock", Color.FromArgb(96, 96, 96), 1);
		
		// Beach/Shore
		assets.Templates[4] = CreateColorTemplate(4, "beach", Color.FromArgb(194, 178, 128), 1);

		// Trees/Forest
		assets.Templates[5] = CreateColorTemplate(5, "tree", Color.FromArgb(34, 85, 34), 1);
	}

	private TerrainTemplate CreateColorTemplate(ushort id, string name, System.Drawing.Color color, int tileCount)
	{
		var template = new TerrainTemplate
		{
			Id = id,
			Name = name,
			TileCount = tileCount,
			TileWidth = 24,
			TileHeight = 24
		};

		// Create a simple colored tile image
		for (int i = 0; i < tileCount; i++)
		{
			using (var bitmap = new Bitmap(24, 24))
			using (var g = System.Drawing.Graphics.FromImage(bitmap))
			{
				// Fill with base color
				using (var brush = new SolidBrush(color))
				{
					g.FillRectangle(brush, 0, 0, 24, 24);
				}

				// Add some texture variation
				var random = new Random(id * 100 + i);
				for (int y = 0; y < 24; y += 3)
				{
					for (int x = 0; x < 24; x += 3)
					{
						var variation = random.Next(-20, 20);
						var variedColor = Color.FromArgb(
							Math.Max(0, Math.Min(255, color.R + variation)),
							Math.Max(0, Math.Min(255, color.G + variation)),
							Math.Max(0, Math.Min(255, color.B + variation))
						);
						bitmap.SetPixel(x, y, variedColor);
					}
				}

				// Add a subtle border
				using (var pen = new Pen(Color.FromArgb(64, 0, 0, 0), 1))
				{
					g.DrawRectangle(pen, 0, 0, 23, 23);
				}

				// Convert to PNG byte array
				using (var stream = new MemoryStream())
				{
					bitmap.Save(stream, ImageFormat.Png);
					template.TileImages.Add(stream.ToArray());
				}
			}
		}

		return template;
	}

	public async Task<byte[]?> RenderMapToImageAsync(
		TerrainTile[,] tiles, 
		TerrainAssets assets,
		int tilePixelWidth = 24,
		int tilePixelHeight = 24)
	{
		try
		{
			return await Task.Run(() =>
			{
				var width = tiles.GetLength(0);
				var height = tiles.GetLength(1);

				var imageWidth = width * tilePixelWidth;
				var imageHeight = height * tilePixelHeight;

				using (var bitmap = new Bitmap(imageWidth, imageHeight))
				using (var g = System.Drawing.Graphics.FromImage(bitmap))
				{
					// Clear to black
					g.Clear(Color.Black);

					// Draw each tile
					for (int y = 0; y < height; y++)
					{
						for (int x = 0; x < width; x++)
						{
							var tile = tiles[x, y];
							
							// Get the template for this tile type
							if (assets.Templates.TryGetValue(tile.Type, out var template))
							{
								// Get the specific tile image within the template
								var tileIndex = Math.Min(tile.Index, template.TileImages.Count - 1);
								if (tileIndex >= 0 && tileIndex < template.TileImages.Count)
								{
									var imageData = template.TileImages[tileIndex];
									using (var stream = new MemoryStream(imageData))
									using (var tileImage = Image.FromStream(stream))
									{
										var destX = x * tilePixelWidth;
										var destY = y * tilePixelHeight;
										g.DrawImage(tileImage, destX, destY, tilePixelWidth, tilePixelHeight);
									}
								}
							}
							else
							{
								// Unknown tile type - draw a placeholder
								using (var brush = new SolidBrush(Color.FromArgb(32, 32, 32)))
								{
									g.FillRectangle(brush, x * tilePixelWidth, y * tilePixelHeight, tilePixelWidth, tilePixelHeight);
								}
							}
						}
					}

					// Convert to PNG byte array
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
			logger.LogError(ex, "Failed to render map to image");
			return null;
		}
	}
}
