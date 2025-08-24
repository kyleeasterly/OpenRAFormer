using System.Text;
using OpenRA;
using OpenRA.FileFormats;
using OpenRA.Graphics;
using OpenRA.Primitives;

namespace OpenRA.ReplayViewer.Services;

public class PaletteService
{
	private readonly ILogger<PaletteService> logger;
	private readonly FileSystemService fileSystemService;
	private readonly Dictionary<string, ImmutablePalette> paletteCache = new();

	public PaletteService(ILogger<PaletteService> logger, FileSystemService fileSystemService)
	{
		this.logger = logger;
		this.fileSystemService = fileSystemService;
	}

	public ImmutablePalette? LoadPalette(string modId, string paletteName)
	{
		var cacheKey = $"{modId}:{paletteName}";
		if (paletteCache.TryGetValue(cacheKey, out var cached))
			return cached;

		try
		{
			// Try to load the palette file
			var palette = LoadPaletteFromFile(modId, paletteName);
			
			if (palette == null)
			{
				// Try to find it in the mod's palette definitions
				palette = LoadPaletteFromDefinition(modId, paletteName);
			}

			if (palette != null)
			{
				paletteCache[cacheKey] = palette;
				logger.LogInformation("Loaded palette {PaletteName} for mod {ModId}", paletteName, modId);
			}
			else
			{
				logger.LogWarning("Failed to load palette {PaletteName} for mod {ModId}, using default", paletteName, modId);
				palette = CreateDefaultPalette();
				paletteCache[cacheKey] = palette;
			}

			return palette;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Error loading palette {PaletteName} for mod {ModId}", paletteName, modId);
			return CreateDefaultPalette();
		}
	}

	private ImmutablePalette? LoadPaletteFromFile(string modId, string paletteName)
	{
		// Common palette file names
		var possibleNames = new[]
		{
			$"{paletteName}.pal",
			$"{paletteName}.act",
			$"palettes/{paletteName}.pal",
			$"bits/{paletteName}.pal",
			$"{paletteName}"
		};

		foreach (var filename in possibleNames)
		{
			using var stream = fileSystemService.OpenFile(modId, filename);
			if (stream != null)
			{
				logger.LogDebug("Found palette file: {Filename}", filename);
				return LoadPaletteFromStream(stream, filename);
			}
		}

		return null;
	}

	private ImmutablePalette? LoadPaletteFromStream(Stream stream, string filename)
	{
		try
		{
			var extension = Path.GetExtension(filename).ToLowerInvariant();
			
			// Check file size to determine format
			var length = stream.Length;
			
			if (length == 768) // 256 * 3 (RGB)
			{
				return LoadRawPalette(stream, false);
			}
			else if (length == 1024) // 256 * 4 (RGBA)
			{
				return LoadRawPalette(stream, true);
			}
			else if (extension == ".act") // Adobe Color Table
			{
				return LoadActPalette(stream);
			}
			else if (extension == ".pal")
			{
				// Could be various formats, try to detect
				return LoadPalFile(stream);
			}

			logger.LogWarning("Unknown palette format for {Filename} (size: {Size})", filename, length);
			return null;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to load palette from {Filename}", filename);
			return null;
		}
	}

	private ImmutablePalette LoadRawPalette(Stream stream, bool hasAlpha)
	{
		var colors = new uint[256];
		var bytesPerColor = hasAlpha ? 4 : 3;
		var buffer = new byte[256 * bytesPerColor];
		
		stream.Read(buffer, 0, buffer.Length);
		
		for (int i = 0; i < 256; i++)
		{
			var offset = i * bytesPerColor;
			var r = buffer[offset];
			var g = buffer[offset + 1];
			var b = buffer[offset + 2];
			var a = hasAlpha ? buffer[offset + 3] : (byte)255;
			
			// Some palettes use 6-bit values (0-63), scale to 8-bit
			if (r <= 63 && g <= 63 && b <= 63)
			{
				r = (byte)(r * 4);
				g = (byte)(g * 4);
				b = (byte)(b * 4);
			}
			
			colors[i] = (uint)((a << 24) | (r << 16) | (g << 8) | b);
		}
		
		return new ImmutablePalette(colors);
	}

	private ImmutablePalette LoadActPalette(Stream stream)
	{
		// Adobe Color Table format
		return LoadRawPalette(stream, false);
	}

	private ImmutablePalette LoadPalFile(Stream stream)
	{
		// Try to detect format based on content
		var buffer = new byte[Math.Min(1024, stream.Length)];
		stream.Read(buffer, 0, buffer.Length);
		stream.Position = 0;
		
		// Check for RIFF header (Microsoft PAL)
		if (buffer.Length >= 4 && Encoding.ASCII.GetString(buffer, 0, 4) == "RIFF")
		{
			return LoadRiffPalette(stream);
		}
		
		// Check for JASC header
		if (buffer.Length >= 8 && Encoding.ASCII.GetString(buffer, 0, 8) == "JASC-PAL")
		{
			return LoadJascPalette(stream);
		}
		
		// Assume raw format
		return LoadRawPalette(stream, stream.Length == 1024);
	}

	private ImmutablePalette LoadRiffPalette(Stream stream)
	{
		// Microsoft RIFF PAL format
		var reader = new BinaryReader(stream);
		
		// Skip RIFF header
		reader.ReadBytes(12); // "RIFF" + size + "PAL "
		reader.ReadBytes(8);  // "data" + size
		
		var version = reader.ReadUInt16();
		var numEntries = reader.ReadUInt16();
		
		var colors = new uint[256];
		for (int i = 0; i < Math.Min((int)numEntries, 256); i++)
		{
			var r = reader.ReadByte();
			var g = reader.ReadByte();
			var b = reader.ReadByte();
			var flags = reader.ReadByte(); // Usually 0
			
			colors[i] = (uint)(0xFF000000 | (r << 16) | (g << 8) | b);
		}
		
		// Fill remaining with black
		for (int i = numEntries; i < 256; i++)
		{
			colors[i] = 0xFF000000;
		}
		
		return new ImmutablePalette(colors);
	}

	private ImmutablePalette LoadJascPalette(Stream stream)
	{
		// JASC Paint Shop Pro palette format
		var reader = new StreamReader(stream);
		
		// Skip header lines
		reader.ReadLine(); // "JASC-PAL"
		reader.ReadLine(); // Version
		var numColors = int.Parse(reader.ReadLine() ?? "256");
		
		var colors = new uint[256];
		for (int i = 0; i < Math.Min(numColors, 256); i++)
		{
			var line = reader.ReadLine();
			if (string.IsNullOrEmpty(line))
				break;
				
			var parts = line.Split(' ');
			if (parts.Length >= 3)
			{
				var r = byte.Parse(parts[0]);
				var g = byte.Parse(parts[1]);
				var b = byte.Parse(parts[2]);
				colors[i] = (uint)(0xFF000000 | (r << 16) | (g << 8) | b);
			}
		}
		
		return new ImmutablePalette(colors);
	}

	private ImmutablePalette? LoadPaletteFromDefinition(string modId, string paletteName)
	{
		// Load palette definitions from mod's palettes.yaml
		var gameRoot = Path.Combine(Environment.CurrentDirectory, "..", "mods", modId.ToLowerInvariant());
		var palettesYaml = Path.Combine(gameRoot, "palettes.yaml");
		
		if (!File.Exists(palettesYaml))
		{
			logger.LogDebug("No palettes.yaml found for mod {ModId}", modId);
			return null;
		}

		try
		{
			var yaml = MiniYaml.FromFile(palettesYaml);
			var paletteNode = yaml.FirstOrDefault(n => n.Key == "Palettes");
			
			if (paletteNode != null)
			{
				var targetNode = paletteNode.Value.Nodes.FirstOrDefault(n => 
					n.Key.Equals(paletteName, StringComparison.OrdinalIgnoreCase));
				
				if (targetNode != null)
				{
					// Look for BasePalette or Filename fields
					var basePalette = targetNode.Value.Nodes.FirstOrDefault(n => n.Key == "BasePalette")?.Value?.Value;
					var filename = targetNode.Value.Nodes.FirstOrDefault(n => n.Key == "Filename")?.Value?.Value;
					
					if (!string.IsNullOrEmpty(filename))
					{
						return LoadPaletteFromFile(modId, filename);
					}
					else if (!string.IsNullOrEmpty(basePalette))
					{
						return LoadPalette(modId, basePalette);
					}
				}
			}
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to load palette definition for {PaletteName}", paletteName);
		}

		return null;
	}

	private ImmutablePalette CreateDefaultPalette()
	{
		// Create a default grayscale palette
		var colors = new uint[256];
		for (int i = 0; i < 256; i++)
		{
			var value = (byte)i;
			colors[i] = (uint)(0xFF000000 | (value << 16) | (value << 8) | value);
		}
		
		// Set index 0 to transparent (common convention)
		colors[0] = 0x00000000;
		
		return new ImmutablePalette(colors);
	}

	public Color[] GetPaletteColors(ImmutablePalette palette)
	{
		var colors = new Color[256];
		for (int i = 0; i < 256; i++)
		{
			var argb = palette[i];
			colors[i] = Color.FromArgb(
				(byte)((argb >> 24) & 0xFF),
				(byte)((argb >> 16) & 0xFF),
				(byte)((argb >> 8) & 0xFF),
				(byte)(argb & 0xFF)
			);
		}
		return colors;
	}

	public ImmutablePalette? GetTerrainPalette(string modId)
	{
		// Common terrain palette names by mod
		var paletteName = modId.ToLowerInvariant() switch
		{
			"ra" => "terrain",
			"cnc" => "terrain",
			"d2k" => "d2k",
			"ts" => "unitsno",
			_ => "terrain"
		};
		
		return LoadPalette(modId, paletteName);
	}
}