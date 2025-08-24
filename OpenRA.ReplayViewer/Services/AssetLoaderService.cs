using System.Drawing;
using System.Drawing.Imaging;
using OpenRA;
using OpenRA.FileFormats;
using OpenRA.FileSystem;
using OpenRA.Graphics;
using OpenRA.Mods.Common.FileFormats;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.SpriteLoaders;
using OpenRA.Mods.Common.Terrain;
using OpenRA.Primitives;
using Bitmap = System.Drawing.Bitmap;
using Color = System.Drawing.Color;
using Image = System.Drawing.Image;

namespace OpenRA.ReplayViewer.Services;

public class AssetLoaderService
{
	private readonly ILogger<AssetLoaderService> logger;
	private readonly IWebHostEnvironment environment;
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
		public Color Color { get; set; }
		public bool IsWater { get; set; }
	}

	public AssetLoaderService(ILogger<AssetLoaderService> logger, IWebHostEnvironment environment)
	{
		this.logger = logger;
		this.environment = environment;
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

				// Load templates
				var templatesNode = yaml.FirstOrDefault(n => n.Key == "Templates");
				if (templatesNode != null)
				{
					foreach (var template in templatesNode.Value.Nodes)
					{
						var tmpl = ParseTemplate(template, modPath);
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
								info.Color = Color.FromArgb(r, g, b);
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
		if (info.Color == Color.Empty)
		{
			info.Color = info.IsWater ? Color.Blue : Color.Green;
		}

		return info;
	}

	private TerrainTemplate? ParseTemplate(MiniYamlNode node, string modPath)
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
				LoadTemplateImages(template, imageFile, modPath, tileMapping);
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

	private void LoadTemplateImages(TerrainTemplate template, string imageFile, string modPath, Dictionary<int, string> tileMapping)
	{
		try
		{
			// Look for the image file
			var imagePath = FindImageFile(modPath, imageFile);
			if (imagePath == null)
			{
				logger.LogWarning("Image file not found: {ImageFile}", imageFile);
				CreatePlaceholderImages(template, tileMapping);
				return;
			}

			// For now, create placeholder images
			// In a full implementation, we'd use OpenRA's sprite loaders
			CreatePlaceholderImages(template, tileMapping);
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Failed to load template images from {ImageFile}", imageFile);
			CreatePlaceholderImages(template, tileMapping);
		}
	}

	private string? FindImageFile(string modPath, string imageFile)
	{
		// Remove extension if present
		var baseName = Path.GetFileNameWithoutExtension(imageFile);
		var possibleExtensions = new[] { ".tem", ".shp", ".png", ".sno", ".int", ".des", ".jun", ".win" };

		// Common locations for sprites
		var searchDirs = new[]
		{
			Path.Combine(modPath, "bits"),
			Path.Combine(modPath, "bits", "terrain"),
			Path.Combine(modPath, "sprites"),
			Path.Combine(modPath, "uibits"),
			modPath
		};

		foreach (var dir in searchDirs)
		{
			if (!Directory.Exists(dir))
				continue;

			foreach (var ext in possibleExtensions)
			{
				var fullPath = Path.Combine(dir, baseName + ext);
				if (File.Exists(fullPath))
					return fullPath;
			}
		}

		return null;
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
				using (var pen = new System.Drawing.Pen(Color.FromArgb(32, 0, 0, 0), 1))
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
				using (var pen = new System.Drawing.Pen(Color.FromArgb(64, 0, 0, 0), 1))
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

	private Color GetTerrainColor(string terrainType)
	{
		// Default colors for common terrain types
		return terrainType.ToLowerInvariant() switch
		{
			"water" => Color.FromArgb(64, 64, 192),
			"river" => Color.FromArgb(80, 80, 200),
			"road" => Color.FromArgb(128, 128, 128),
			"rock" => Color.FromArgb(96, 96, 96),
			"mountain" => Color.FromArgb(80, 80, 80),
			"tree" => Color.FromArgb(34, 85, 34),
			"forest" => Color.FromArgb(28, 70, 28),
			"beach" => Color.FromArgb(194, 178, 128),
			"sand" => Color.FromArgb(210, 190, 140),
			"rough" => Color.FromArgb(100, 90, 70),
			"cliff" => Color.FromArgb(70, 70, 70),
			"ore" => Color.FromArgb(180, 140, 80),
			"gems" => Color.FromArgb(140, 80, 180),
			_ => Color.FromArgb(76, 140, 43) // Default green for "clear"
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
					g.Clear(Color.Black);

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
								using (var tileImage = Image.FromStream(stream))
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