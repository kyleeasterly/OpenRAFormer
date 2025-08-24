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
	private readonly FileSystemService fileSystemService;
	private readonly FrameCacheService frameCacheService;
	private readonly PaletteService paletteService;
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
		FileSystemService fileSystemService,
		FrameCacheService frameCacheService,
		PaletteService paletteService)
	{
		this.logger = logger;
		this.environment = environment;
		this.fileSystemService = fileSystemService;
		this.frameCacheService = frameCacheService;
		this.paletteService = paletteService;
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
				var gameRoot = Path.Combine(environment.ContentRootPath, "..");
				var modPath = Path.Combine(gameRoot, "mods", modId.ToLowerInvariant());
				
				// Find the tileset YAML file
				var tilesetPath = FindTilesetFile(modPath, tilesetName);
				if (tilesetPath == null)
				{
					assets.Error = $"Tileset {tilesetName} not found in mod {modId}";
					return;
				}

				// Parse the tileset YAML - materialize the list to avoid multiple enumeration
				var yaml = MiniYaml.FromFile(tilesetPath).ToList();
				
				// Load terrain types
				var terrainNode = yaml.FirstOrDefault(n => n.Key == "Terrain");
				if (terrainNode != null)
				{
					foreach (var terrain in terrainNode.Value.Nodes)
					{
						var info = ParseTerrainType(terrain);
						assets.TerrainTypes[info.Type] = info;
					}
				}

				// Get the terrain palette for this tileset
				var palette = paletteService.GetTerrainPalette(modId);
				var paletteColors = palette != null ? paletteService.GetPaletteColors(palette) : null;

				// Load templates
				var templatesNode = yaml.FirstOrDefault(n => n.Key == "Templates");
				if (templatesNode != null)
				{
					foreach (var template in templatesNode.Value.Nodes)
					{
						var tmpl = ParseTemplate(template, modId, paletteColors);
						if (tmpl != null)
							assets.Templates[tmpl.Id] = tmpl;
					}
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

	private string? FindTilesetFile(string modPath, string tilesetName)
	{
		// Common locations for tileset files
		var possiblePaths = new[]
		{
			Path.Combine(modPath, "tilesets", $"{tilesetName}.yaml"),
			Path.Combine(modPath, "tileset", $"{tilesetName}.yaml"),
			Path.Combine(modPath, $"{tilesetName}.yaml"),
			Path.Combine(modPath, "sequences", $"{tilesetName}.yaml"),
		};

		foreach (var path in possiblePaths)
		{
			if (File.Exists(path))
				return path;
		}

		// Search recursively
		var yamlFiles = Directory.GetFiles(modPath, $"{tilesetName}.yaml", SearchOption.AllDirectories);
		if (yamlFiles.Length > 0)
			return yamlFiles[0];

		return null;
	}

	private TerrainTypeInfo ParseTerrainType(MiniYamlNode node)
	{
		var info = new TerrainTypeInfo
		{
			Type = node.Key
		};

		foreach (var field in node.Value.Nodes)
		{
			switch (field.Key)
			{
				case "Color":
					var colorStr = field.Value.Value;
					if (!string.IsNullOrEmpty(colorStr))
					{
						var parts = colorStr.Split(',');
						if (parts.Length >= 3)
						{
							if (int.TryParse(parts[0], out var r) &&
								int.TryParse(parts[1], out var g) &&
								int.TryParse(parts[2], out var b))
							{
								info.Color = System.Drawing.Color.FromArgb(r, g, b);
							}
						}
					}
					break;
				case "IsWater":
					info.IsWater = field.Value.Value?.ToLowerInvariant() == "true";
					break;
			}
		}

		// Default color if not specified
		if (info.Color == System.Drawing.Color.Empty)
		{
			info.Color = info.IsWater ? System.Drawing.Color.Blue : System.Drawing.Color.Green;
		}

		return info;
	}

	private TerrainTemplate? ParseTemplate(MiniYamlNode node, string modId, OpenRA.Primitives.Color[]? paletteColors)
	{
		try
		{
			var template = new TerrainTemplate
			{
				Name = node.Key
			};

			string? imageFile = null;
			var tileMapping = new Dictionary<int, string>();

			foreach (var field in node.Value.Nodes)
			{
				switch (field.Key)
				{
					case "Id":
						if (ushort.TryParse(field.Value.Value, out var id))
							template.Id = id;
						break;
					case "Images":
						imageFile = field.Value.Value;
						break;
					case "Size":
						var sizeStr = field.Value.Value;
						if (!string.IsNullOrEmpty(sizeStr))
						{
							var parts = sizeStr.Split(',');
							if (parts.Length >= 2 &&
								int.TryParse(parts[0], out var w) &&
								int.TryParse(parts[1], out var h))
							{
								template.Size = new OpenRA.Primitives.Size(w, h);
							}
						}
						break;
					case "Tiles":
						foreach (var tile in field.Value.Nodes)
						{
							if (int.TryParse(tile.Key, out var tileIndex))
							{
								tileMapping[tileIndex] = tile.Value.Value ?? "Clear";
							}
						}
						break;
				}
			}

			// Try to load the image file
			if (!string.IsNullOrEmpty(imageFile))
			{
				LoadTemplateImages(template, imageFile, modId, tileMapping, paletteColors);
			}
			else
			{
				// Create placeholder images based on terrain types
				CreatePlaceholderImages(template, tileMapping);
			}

			template.TileTerrainTypes = tileMapping;
			return template;
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Failed to parse template {Name}", node.Key);
			return null;
		}
	}

	private void LoadTemplateImages(TerrainTemplate template, string imageFile, string modId, Dictionary<int, string> tileMapping, OpenRA.Primitives.Color[]? paletteColors)
	{
		try
		{
			// Load the frames from the image file
			var frameCount = frameCacheService.GetFrameCount(modId, imageFile);
			if (frameCount == 0)
			{
				logger.LogWarning("No frames found in image file: {ImageFile}", imageFile);
				CreatePlaceholderImages(template, tileMapping);
				return;
			}

			logger.LogInformation("Loading {Count} frames from {ImageFile} for template {Id}", frameCount, imageFile, template.Id);

			// Load each frame as a tile image
			for (int i = 0; i < frameCount; i++)
			{
				var sprite = frameCacheService.GetRgbaSprite(modId, imageFile, i, paletteColors);
				if (sprite != null)
				{
					// Convert RGBA data to PNG for web display
					using (var bitmap = new Bitmap(sprite.Width, sprite.Height))
					{
						var rect = new System.Drawing.Rectangle(0, 0, sprite.Width, sprite.Height);
						var bitmapData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
						
						try
						{
							unsafe
							{
								var ptr = (byte*)bitmapData.Scan0;
								for (int y = 0; y < sprite.Height; y++)
								{
									for (int x = 0; x < sprite.Width; x++)
									{
										var srcIdx = (y * sprite.Width + x) * 4;
										var dstIdx = y * bitmapData.Stride + x * 4;
										
										// RGBA to BGRA conversion for System.Drawing
										ptr[dstIdx] = sprite.RgbaData[srcIdx + 2];     // B
										ptr[dstIdx + 1] = sprite.RgbaData[srcIdx + 1]; // G
										ptr[dstIdx + 2] = sprite.RgbaData[srcIdx];     // R
										ptr[dstIdx + 3] = sprite.RgbaData[srcIdx + 3]; // A
									}
								}
							}
						}
						finally
						{
							bitmap.UnlockBits(bitmapData);
						}

						// Convert to PNG
						using (var stream = new MemoryStream())
						{
							bitmap.Save(stream, ImageFormat.Png);
							template.TileImages[i] = stream.ToArray();
						}
					}
				}
				else
				{
					logger.LogWarning("Failed to load frame {Index} from {ImageFile}", i, imageFile);
				}
			}

			// If we didn't get enough tiles, fill in with placeholders
			if (template.TileImages.Count < tileMapping.Count)
			{
				logger.LogWarning("Not enough frames in {ImageFile}, creating placeholders", imageFile);
				for (int i = template.TileImages.Count; i < tileMapping.Count; i++)
				{
					CreatePlaceholderTile(template, i, tileMapping.ContainsKey(i) ? tileMapping[i] : "Clear");
				}
			}
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Failed to load template images from {ImageFile}", imageFile);
			CreatePlaceholderImages(template, tileMapping);
		}
	}

	private void CreatePlaceholderTile(TerrainTemplate template, int index, string terrainType)
	{
		var tileSize = 24;
		var color = GetTerrainColor(terrainType);
		
		using (var bitmap = new Bitmap(tileSize, tileSize))
		using (var g = System.Drawing.Graphics.FromImage(bitmap))
		{
			// Fill with base color
			using (var brush = new System.Drawing.SolidBrush(color))
			{
				g.FillRectangle(brush, 0, 0, tileSize, tileSize);
			}

			// Add some texture
			var random = new Random(template.Id * 1000 + index);
			using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(32, 0, 0, 0), 1))
			{
				for (int j = 0; j < 5; j++)
				{
					var x1 = random.Next(tileSize);
					var y1 = random.Next(tileSize);
					var x2 = x1 + random.Next(-3, 4);
					var y2 = y1 + random.Next(-3, 4);
					g.DrawLine(pen, x1, y1, x2, y2);
				}
			}

			// Add border
			using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(64, 0, 0, 0), 1))
			{
				g.DrawRectangle(pen, 0, 0, tileSize - 1, tileSize - 1);
			}

			// Convert to PNG
			using (var stream = new MemoryStream())
			{
				bitmap.Save(stream, ImageFormat.Png);
				template.TileImages[index] = stream.ToArray();
			}
		}
	}

	private void CreatePlaceholderImages(TerrainTemplate template, Dictionary<int, string> tileMapping)
	{
		// Create colored placeholder tiles based on terrain type
		var tileSize = 24;
		var tileCount = Math.Max(template.Size.Width * template.Size.Height, tileMapping.Count);
		
		for (int i = 0; i < tileCount; i++)
		{
			var terrainType = tileMapping.ContainsKey(i) ? tileMapping[i] : "Clear";
			var color = GetTerrainColor(terrainType);
			
			using (var bitmap = new Bitmap(tileSize, tileSize))
			using (var g = System.Drawing.Graphics.FromImage(bitmap))
			{
				// Fill with base color
				using (var brush = new System.Drawing.SolidBrush(color))
				{
					g.FillRectangle(brush, 0, 0, tileSize, tileSize);
				}

				// Add some texture
				var random = new Random(template.Id * 1000 + i);
				using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(32, 0, 0, 0), 1))
				{
					for (int j = 0; j < 5; j++)
					{
						var x1 = random.Next(tileSize);
						var y1 = random.Next(tileSize);
						var x2 = x1 + random.Next(-3, 4);
						var y2 = y1 + random.Next(-3, 4);
						g.DrawLine(pen, x1, y1, x2, y2);
					}
				}

				// Add border
				using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(64, 0, 0, 0), 1))
				{
					g.DrawRectangle(pen, 0, 0, tileSize - 1, tileSize - 1);
				}

				// Convert to PNG
				using (var stream = new MemoryStream())
				{
					bitmap.Save(stream, ImageFormat.Png);
					template.TileImages[i] = stream.ToArray();
				}
			}
		}
	}

	private System.Drawing.Color GetTerrainColor(string terrainType)
	{
		// Default colors for common terrain types
		return terrainType.ToLowerInvariant() switch
		{
			"water" => System.Drawing.Color.FromArgb(64, 64, 192),
			"river" => System.Drawing.Color.FromArgb(80, 80, 200),
			"road" => System.Drawing.Color.FromArgb(128, 128, 128),
			"rock" => System.Drawing.Color.FromArgb(96, 96, 96),
			"mountain" => System.Drawing.Color.FromArgb(80, 80, 80),
			"tree" => System.Drawing.Color.FromArgb(34, 85, 34),
			"forest" => System.Drawing.Color.FromArgb(28, 70, 28),
			"beach" => System.Drawing.Color.FromArgb(194, 178, 128),
			"sand" => System.Drawing.Color.FromArgb(210, 190, 140),
			"rough" => System.Drawing.Color.FromArgb(100, 90, 70),
			"cliff" => System.Drawing.Color.FromArgb(70, 70, 70),
			"ore" => System.Drawing.Color.FromArgb(180, 140, 80),
			"gems" => System.Drawing.Color.FromArgb(140, 80, 180),
			_ => System.Drawing.Color.FromArgb(76, 140, 43) // Default green for "clear"
		};
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