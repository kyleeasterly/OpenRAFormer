using System.Drawing;
using System.Drawing.Imaging;
using OpenRA;
using OpenRA.FileFormats;
using OpenRA.Graphics;
using OpenRA.Primitives;
using Color = OpenRA.Primitives.Color;

namespace OpenRA.ReplayViewer.Services;

public class TilesetLoaderService
{
	private readonly ILogger<TilesetLoaderService> logger;
	private readonly FileSystemService fileSystemService;
	private readonly FrameCacheService frameCacheService;
	private readonly PaletteService paletteService;
	private readonly Dictionary<string, TilesetData> tilesetCache = new();

	public class TilesetData
	{
		public string Name { get; set; } = "";
		public Dictionary<ushort, TerrainTemplateInfo> Templates { get; set; } = new();
		public Dictionary<string, TerrainTypeInfo> TerrainTypes { get; set; } = new();
		public string? PaletteName { get; set; }
		public ImmutablePalette? Palette { get; set; }
		public string? Error { get; set; }
	}

	public class TerrainTemplateInfo
	{
		public ushort Id { get; set; }
		public string Name { get; set; } = "";
		public int2 Size { get; set; }
		public string[] Images { get; set; } = Array.Empty<string>();
		public int[]? Frames { get; set; }
		public bool PickAny { get; set; }
		public string Category { get; set; } = "";
		public Dictionary<int, TerrainTileInfo> Tiles { get; set; } = new();
		public List<Sprite> Sprites { get; set; } = new();
	}

	public class TerrainTileInfo
	{
		public int Index { get; set; }
		public string TerrainType { get; set; } = "";
		public float ZOffset { get; set; }
		public float ZRamp { get; set; } = 1f;
	}

	public class TerrainTypeInfo
	{
		public string Type { get; set; } = "";
		public System.Drawing.Color Color { get; set; }
		public bool IsWater { get; set; }
		public string[] TargetTypes { get; set; } = Array.Empty<string>();
	}

	public class Sprite
	{
		public byte[] ImageData { get; set; } = Array.Empty<byte>();
		public int Width { get; set; }
		public int Height { get; set; }
		public float2 Offset { get; set; }
	}

	public TilesetLoaderService(
		ILogger<TilesetLoaderService> logger,
		FileSystemService fileSystemService,
		FrameCacheService frameCacheService,
		PaletteService paletteService)
	{
		this.logger = logger;
		this.fileSystemService = fileSystemService;
		this.frameCacheService = frameCacheService;
		this.paletteService = paletteService;
	}

	public TilesetData LoadTileset(string modId, string tilesetName)
	{
		var cacheKey = $"{modId}:{tilesetName}";
		if (tilesetCache.TryGetValue(cacheKey, out var cached))
			return cached;

		var tileset = new TilesetData { Name = tilesetName };

		try
		{
			// Find and parse the tileset YAML file
			var tilesetPath = FindTilesetFile(modId, tilesetName);
			if (tilesetPath == null)
			{
				tileset.Error = $"Tileset file not found: {tilesetName}";
				logger.LogWarning("Tileset file not found: {TilesetName} for mod {ModId}", tilesetName, modId);
				return tileset;
			}

			logger.LogInformation("Loading tileset from: {Path}", tilesetPath);

			// Parse the YAML
			var yaml = MiniYaml.FromFile(tilesetPath);
			
			// Parse General section for palette info
			var generalNode = yaml.FirstOrDefault(n => n.Key == "General");
			if (generalNode != null)
			{
				var paletteNode = generalNode.Value.Nodes.FirstOrDefault(n => n.Key == "Palette");
				if (paletteNode != null)
				{
					tileset.PaletteName = paletteNode.Value.Value;
				}
			}

			// Load the palette
			var paletteName = tileset.PaletteName ?? "terrain";
			tileset.Palette = paletteService.LoadPalette(modId, paletteName);
			var paletteColors = tileset.Palette != null ? paletteService.GetPaletteColors(tileset.Palette) : null;

			// Parse Terrain types
			var terrainNode = yaml.FirstOrDefault(n => n.Key == "Terrain");
			if (terrainNode != null)
			{
				foreach (var typeNode in terrainNode.Value.Nodes)
				{
					var terrainType = ParseTerrainType(typeNode);
					tileset.TerrainTypes[terrainType.Type] = terrainType;
				}
			}

			// Parse Templates
			var templatesNode = yaml.FirstOrDefault(n => n.Key == "Templates");
			if (templatesNode != null)
			{
				foreach (var templateNode in templatesNode.Value.Nodes)
				{
					var template = ParseTemplate(templateNode, modId, tileset, paletteColors);
					if (template != null)
					{
						tileset.Templates[template.Id] = template;
						logger.LogDebug("Loaded template {Id}: {Name} ({TileCount} tiles)", 
							template.Id, template.Name, template.Tiles.Count);
					}
				}
			}

			logger.LogInformation("Loaded tileset {Name}: {TemplateCount} templates, {TerrainTypeCount} terrain types",
				tilesetName, tileset.Templates.Count, tileset.TerrainTypes.Count);

			tilesetCache[cacheKey] = tileset;
		}
		catch (Exception ex)
		{
			tileset.Error = $"Failed to load tileset: {ex.Message}";
			logger.LogError(ex, "Failed to load tileset {TilesetName} for mod {ModId}", tilesetName, modId);
		}

		return tileset;
	}

	private string? FindTilesetFile(string modId, string tilesetName)
	{
		var gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(Environment.CurrentDirectory)) ?? "";
		var modPath = Path.Combine(gameRoot, "mods", modId.ToLowerInvariant());

		// Common locations for tileset files
		var possiblePaths = new[]
		{
			Path.Combine(modPath, "tilesets", $"{tilesetName}.yaml"),
			Path.Combine(modPath, "tileset", $"{tilesetName}.yaml"),
			Path.Combine(modPath, $"{tilesetName}.yaml"),
		};

		foreach (var path in possiblePaths)
		{
			if (File.Exists(path))
				return path;
		}

		return null;
	}

	private TerrainTypeInfo ParseTerrainType(MiniYamlNode node)
	{
		var info = new TerrainTypeInfo();
		
		// Extract type name from node key (e.g., "TerrainType@Clear" -> "Clear")
		var typeName = node.Key;
		if (typeName.Contains('@'))
		{
			typeName = typeName.Substring(typeName.IndexOf('@') + 1);
		}
		info.Type = typeName;

		foreach (var field in node.Value.Nodes)
		{
			switch (field.Key)
			{
				case "Type":
					info.Type = field.Value.Value ?? typeName;
					break;
					
				case "Color":
					var colorStr = field.Value.Value;
					if (!string.IsNullOrEmpty(colorStr))
					{
						var parts = colorStr.Split(',');
						if (parts.Length >= 3)
						{
							if (int.TryParse(parts[0].Trim(), out var r) &&
								int.TryParse(parts[1].Trim(), out var g) &&
								int.TryParse(parts[2].Trim(), out var b))
							{
								var a = parts.Length > 3 && int.TryParse(parts[3].Trim(), out var alpha) ? alpha : 255;
								info.Color = System.Drawing.Color.FromArgb(a, r, g, b);
							}
						}
					}
					break;
					
				case "TargetTypes":
					info.TargetTypes = field.Value.Value?.Split(',').Select(s => s.Trim()).ToArray() ?? Array.Empty<string>();
					break;
			}
		}

		// Set IsWater based on target types or type name
		info.IsWater = info.TargetTypes.Contains("Water") || 
			info.Type.Equals("Water", StringComparison.OrdinalIgnoreCase) ||
			info.Type.Equals("River", StringComparison.OrdinalIgnoreCase);

		return info;
	}

	private TerrainTemplateInfo? ParseTemplate(MiniYamlNode node, string modId, TilesetData tileset, Color[]? paletteColors)
	{
		try
		{
			var template = new TerrainTemplateInfo();
			
			// Extract template ID from node key (e.g., "Template@255" -> 255)
			var key = node.Key;
			if (key.Contains('@'))
			{
				var idStr = key.Substring(key.IndexOf('@') + 1);
				if (ushort.TryParse(idStr, out var id))
				{
					template.Id = id;
				}
			}

			template.Name = $"Template{template.Id}";

			// Parse template fields
			foreach (var field in node.Value.Nodes)
			{
				switch (field.Key)
				{
					case "Id":
						if (ushort.TryParse(field.Value.Value, out var templateId))
							template.Id = templateId;
						break;
						
					case "Images":
						var images = field.Value.Value?.Split(',').Select(s => s.Trim()).ToArray() ?? Array.Empty<string>();
						template.Images = images;
						break;
						
					case "Size":
						var sizeStr = field.Value.Value;
						if (!string.IsNullOrEmpty(sizeStr))
						{
							var parts = sizeStr.Split(',');
							if (parts.Length >= 2 &&
								int.TryParse(parts[0].Trim(), out var w) &&
								int.TryParse(parts[1].Trim(), out var h))
							{
								template.Size = new int2(w, h);
							}
						}
						break;
						
					case "Frames":
						var frameStr = field.Value.Value;
						if (!string.IsNullOrEmpty(frameStr))
						{
							var frameParts = frameStr.Split(',');
							template.Frames = frameParts.Select(s => int.Parse(s.Trim())).ToArray();
						}
						break;
						
					case "PickAny":
						template.PickAny = field.Value.Value?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
						break;
						
					case "Categories":
					case "Category":
						template.Category = field.Value.Value ?? "";
						break;
						
					case "Tiles":
						foreach (var tileNode in field.Value.Nodes)
						{
							if (int.TryParse(tileNode.Key, out var tileIndex))
							{
								var tileInfo = new TerrainTileInfo
								{
									Index = tileIndex,
									TerrainType = tileNode.Value.Value ?? "Clear"
								};
								
								// Check if there are sub-nodes for advanced tile properties
								if (tileNode.Value.Nodes != null && tileNode.Value.Nodes.Length > 0)
								{
									foreach (var prop in tileNode.Value.Nodes)
									{
										switch (prop.Key)
										{
											case "ZOffset":
												if (float.TryParse(prop.Value.Value, out var zOffset))
													tileInfo.ZOffset = zOffset;
												break;
											case "ZRamp":
												if (float.TryParse(prop.Value.Value, out var zRamp))
													tileInfo.ZRamp = zRamp;
												break;
										}
									}
								}
								
								template.Tiles[tileIndex] = tileInfo;
							}
						}
						break;
				}
			}

			// Load the actual sprites for this template
			LoadTemplateSprites(template, modId, tileset, paletteColors);

			return template;
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Failed to parse template {Name}", node.Key);
			return null;
		}
	}

	private void LoadTemplateSprites(TerrainTemplateInfo template, string modId, TilesetData tileset, Color[]? paletteColors)
	{
		if (template.Images.Length == 0)
		{
			logger.LogDebug("No images specified for template {Id}", template.Id);
			CreatePlaceholderSprites(template, tileset);
			return;
		}

		foreach (var imageFile in template.Images)
		{
			var frameCount = frameCacheService.GetFrameCount(modId, imageFile);
			if (frameCount == 0)
			{
				logger.LogDebug("No frames found in {ImageFile} for template {Id}", imageFile, template.Id);
				continue;
			}

			logger.LogDebug("Loading {FrameCount} frames from {ImageFile} for template {Id}", 
				frameCount, imageFile, template.Id);

			// Determine which frames to use
			var framesToLoad = template.Frames ?? Enumerable.Range(0, frameCount).ToArray();
			
			foreach (var frameIndex in framesToLoad)
			{
				if (frameIndex >= frameCount)
				{
					logger.LogWarning("Frame index {Index} out of range for {ImageFile} (has {Count} frames)", 
						frameIndex, imageFile, frameCount);
					continue;
				}

				var cachedSprite = frameCacheService.GetRgbaSprite(modId, imageFile, frameIndex, paletteColors);
				if (cachedSprite != null)
				{
					var sprite = new Sprite
					{
						ImageData = ConvertRgbaToPng(cachedSprite.RgbaData, cachedSprite.Width, cachedSprite.Height),
						Width = cachedSprite.Width,
						Height = cachedSprite.Height,
						Offset = cachedSprite.Offset
					};
					template.Sprites.Add(sprite);
				}
			}
		}

		// If we didn't get enough sprites, fill with placeholders
		var expectedTileCount = template.Size.X * template.Size.Y;
		if (template.Sprites.Count < expectedTileCount)
		{
			logger.LogDebug("Template {Id} has {Actual} sprites but needs {Expected}, adding placeholders",
				template.Id, template.Sprites.Count, expectedTileCount);
			
			while (template.Sprites.Count < expectedTileCount)
			{
				var tileIndex = template.Sprites.Count;
				var tileInfo = template.Tiles.ContainsKey(tileIndex) ? template.Tiles[tileIndex] : null;
				var terrainType = tileInfo?.TerrainType ?? "Clear";
				
				template.Sprites.Add(CreatePlaceholderSprite(terrainType, tileset, template.Id, tileIndex));
			}
		}
	}

	private byte[] ConvertRgbaToPng(byte[] rgbaData, int width, int height)
	{
		using (var bitmap = new Bitmap(width, height))
		{
			var rect = new System.Drawing.Rectangle(0, 0, width, height);
			var bitmapData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
			
			try
			{
				unsafe
				{
					var ptr = (byte*)bitmapData.Scan0;
					for (int y = 0; y < height; y++)
					{
						for (int x = 0; x < width; x++)
						{
							var srcIdx = (y * width + x) * 4;
							var dstIdx = y * bitmapData.Stride + x * 4;
							
							// RGBA to BGRA conversion
							ptr[dstIdx] = rgbaData[srcIdx + 2];     // B
							ptr[dstIdx + 1] = rgbaData[srcIdx + 1]; // G
							ptr[dstIdx + 2] = rgbaData[srcIdx];     // R
							ptr[dstIdx + 3] = rgbaData[srcIdx + 3]; // A
						}
					}
				}
			}
			finally
			{
				bitmap.UnlockBits(bitmapData);
			}

			using (var stream = new MemoryStream())
			{
				bitmap.Save(stream, ImageFormat.Png);
				return stream.ToArray();
			}
		}
	}

	private void CreatePlaceholderSprites(TerrainTemplateInfo template, TilesetData tileset)
	{
		var tileCount = Math.Max(template.Size.X * template.Size.Y, template.Tiles.Count);
		
		for (int i = 0; i < tileCount; i++)
		{
			var tileInfo = template.Tiles.ContainsKey(i) ? template.Tiles[i] : null;
			var terrainType = tileInfo?.TerrainType ?? "Clear";
			
			template.Sprites.Add(CreatePlaceholderSprite(terrainType, tileset, template.Id, i));
		}
	}

	private Sprite CreatePlaceholderSprite(string terrainType, TilesetData tileset, ushort templateId, int tileIndex)
	{
		var tileSize = 24;
		var color = System.Drawing.Color.Gray;
		
		// Get color from terrain type definition
		if (tileset.TerrainTypes.TryGetValue(terrainType, out var typeInfo))
		{
			color = typeInfo.Color;
		}
		else
		{
			// Default colors
			color = terrainType.ToLowerInvariant() switch
			{
				"water" => System.Drawing.Color.FromArgb(64, 64, 192),
				"river" => System.Drawing.Color.FromArgb(80, 80, 200),
				"clear" => System.Drawing.Color.FromArgb(76, 140, 43),
				"road" => System.Drawing.Color.FromArgb(128, 128, 128),
				"rock" => System.Drawing.Color.FromArgb(96, 96, 96),
				"beach" => System.Drawing.Color.FromArgb(194, 178, 128),
				"tree" => System.Drawing.Color.FromArgb(34, 85, 34),
				_ => System.Drawing.Color.FromArgb(100, 100, 100)
			};
		}

		using (var bitmap = new Bitmap(tileSize, tileSize))
		using (var g = System.Drawing.Graphics.FromImage(bitmap))
		{
			// Fill with base color
			using (var brush = new System.Drawing.SolidBrush(color))
			{
				g.FillRectangle(brush, 0, 0, tileSize, tileSize);
			}

			// Add some texture variation
			var random = new Random(templateId * 1000 + tileIndex);
			for (int i = 0; i < 10; i++)
			{
				var x = random.Next(tileSize);
				var y = random.Next(tileSize);
				var variation = random.Next(-20, 20);
				var variedColor = System.Drawing.Color.FromArgb(
					Math.Max(0, Math.Min(255, color.R + variation)),
					Math.Max(0, Math.Min(255, color.G + variation)),
					Math.Max(0, Math.Min(255, color.B + variation))
				);
				bitmap.SetPixel(x, y, variedColor);
			}

			// Add subtle grid
			using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(32, 0, 0, 0), 1))
			{
				g.DrawRectangle(pen, 0, 0, tileSize - 1, tileSize - 1);
			}

			// Convert to PNG
			using (var stream = new MemoryStream())
			{
				bitmap.Save(stream, ImageFormat.Png);
				return new Sprite
				{
					ImageData = stream.ToArray(),
					Width = tileSize,
					Height = tileSize,
					Offset = float2.Zero
				};
			}
		}
	}

	public Sprite? GetTileSprite(TilesetData tileset, ushort templateId, byte tileIndex, int? variant = null)
	{
		if (!tileset.Templates.TryGetValue(templateId, out var template))
			return null;

		if (template.Sprites.Count == 0)
			return null;

		// Handle variant selection for templates with multiple image sets
		var spriteIndex = (int)tileIndex;
		if (template.PickAny && variant.HasValue && template.Images.Length > 1)
		{
			var variantOffset = variant.Value % template.Images.Length;
			var tilesPerImage = template.Sprites.Count / template.Images.Length;
			spriteIndex = variantOffset * tilesPerImage + tileIndex;
		}

		if (spriteIndex >= template.Sprites.Count)
			spriteIndex = template.Sprites.Count - 1;

		return template.Sprites[spriteIndex];
	}
}