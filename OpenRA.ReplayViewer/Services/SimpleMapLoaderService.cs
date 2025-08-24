using System.IO.Compression;
using OpenRA;
using OpenRA.FileFormats;
using OpenRA.Primitives;

namespace OpenRA.ReplayViewer.Services;

public class SimpleMapLoaderService
{
	private readonly ILogger<SimpleMapLoaderService> logger;
	private readonly IWebHostEnvironment environment;

	public class SimpleMap
	{
		public string? Title { get; set; }
		public string? Tileset { get; set; }
		public Rectangle Bounds { get; set; }
		public TerrainTile[,]? Tiles { get; set; }
		public ResourceTile[,]? Resources { get; set; }
		public byte[,]? Heights { get; set; }
		public string? Error { get; set; }
	}

	public SimpleMapLoaderService(ILogger<SimpleMapLoaderService> logger, IWebHostEnvironment environment)
	{
		this.logger = logger;
		this.environment = environment;
	}

	public SimpleMap LoadMapFromPath(string mapPath)
	{
		var result = new SimpleMap();

		try
		{
			if (Directory.Exists(mapPath))
			{
				// Load from directory
				LoadFromDirectory(result, mapPath);
			}
			else if (File.Exists(mapPath))
			{
				// Load from .oramap file
				LoadFromOramap(result, mapPath);
			}
			else
			{
				result.Error = $"Map path not found: {mapPath}";
			}
		}
		catch (Exception ex)
		{
			result.Error = $"Failed to load map: {ex.Message}";
			logger.LogError(ex, "Failed to load map from {Path}", mapPath);
		}

		return result;
	}

	public SimpleMap LoadMapFromData(byte[] mapData)
	{
		var result = new SimpleMap();

		try
		{
			using (var stream = new MemoryStream(mapData))
			using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
			{
				LoadFromZipArchive(result, zip);
			}
		}
		catch (Exception ex)
		{
			result.Error = $"Failed to load map from data: {ex.Message}";
			logger.LogError(ex, "Failed to load map from embedded data");
		}

		return result;
	}

	private void LoadFromDirectory(SimpleMap result, string dirPath)
	{
		var yamlPath = Path.Combine(dirPath, "map.yaml");
		var binPath = Path.Combine(dirPath, "map.bin");

		if (!File.Exists(yamlPath))
		{
			result.Error = "map.yaml not found";
			return;
		}

		if (!File.Exists(binPath))
		{
			result.Error = "map.bin not found";
			return;
		}

		// Parse YAML for metadata
		ParseMapYaml(result, File.ReadAllText(yamlPath));

		// Load binary data
		using (var stream = File.OpenRead(binPath))
		{
			LoadMapBinary(result, stream);
		}
	}

	private void LoadFromOramap(SimpleMap result, string filePath)
	{
		using (var zip = ZipFile.OpenRead(filePath))
		{
			LoadFromZipArchive(result, zip);
		}
	}

	private void LoadFromZipArchive(SimpleMap result, ZipArchive zip)
	{
		// Find map.yaml
		var yamlEntry = zip.GetEntry("map.yaml");
		if (yamlEntry == null)
		{
			result.Error = "map.yaml not found in archive";
			return;
		}

		// Parse YAML
		using (var stream = yamlEntry.Open())
		using (var reader = new StreamReader(stream))
		{
			ParseMapYaml(result, reader.ReadToEnd());
		}

		// Find map.bin
		var binEntry = zip.GetEntry("map.bin");
		if (binEntry == null)
		{
			result.Error = "map.bin not found in archive";
			return;
		}

		// Load binary data
		using (var stream = binEntry.Open())
		{
			LoadMapBinary(result, stream);
		}
	}

	private void ParseMapYaml(SimpleMap result, string yaml)
	{
		try
		{
			var lines = yaml.Split('\n');
			foreach (var line in lines)
			{
				var trimmed = line.Trim();
				
				if (trimmed.StartsWith("Title:"))
				{
					result.Title = trimmed.Substring(6).Trim();
				}
				else if (trimmed.StartsWith("Tileset:"))
				{
					result.Tileset = trimmed.Substring(8).Trim();
				}
				else if (trimmed.StartsWith("MapSize:"))
				{
					var sizeStr = trimmed.Substring(8).Trim();
					var parts = sizeStr.Split(',');
					if (parts.Length >= 2)
					{
						if (int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
						{
							result.Bounds = new Rectangle(0, 0, w, h);
						}
					}
				}
				else if (trimmed.StartsWith("Bounds:"))
				{
					var boundsStr = trimmed.Substring(7).Trim();
					var parts = boundsStr.Split(',');
					if (parts.Length >= 4)
					{
						if (int.TryParse(parts[0], out var x) && 
							int.TryParse(parts[1], out var y) &&
							int.TryParse(parts[2], out var w) && 
							int.TryParse(parts[3], out var h))
						{
							result.Bounds = new Rectangle(x, y, w, h);
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Failed to parse map YAML");
		}
	}

	private void LoadMapBinary(SimpleMap result, Stream stream)
	{
		try
		{
			using (var reader = new BinaryReader(stream))
			{
				// Read format version
				var format = reader.ReadByte();
				if (format != 2 && format != 1)
				{
					result.Error = $"Unsupported map format version: {format}";
					return;
				}

				if (format == 2)
				{
					LoadFormatV2(result, reader);
				}
				else
				{
					LoadFormatV1(result, reader);
				}
			}
		}
		catch (Exception ex)
		{
			result.Error = $"Failed to load map binary: {ex.Message}";
			logger.LogError(ex, "Failed to load map binary data");
		}
	}

	private void LoadFormatV1(SimpleMap result, BinaryReader reader)
	{
		// Format 1: Simple linear format
		// Tiles are stored directly after the format byte
		var width = result.Bounds.Width;
		var height = result.Bounds.Height;

		if (width <= 0 || height <= 0)
		{
			result.Error = "Invalid map dimensions";
			return;
		}

		result.Tiles = new TerrainTile[width, height];

		for (var y = 0; y < height; y++)
		{
			for (var x = 0; x < width; x++)
			{
				var type = reader.ReadUInt16();
				var index = reader.ReadByte();
				result.Tiles[x, y] = new TerrainTile(type, index);
			}
		}
	}

	private void LoadFormatV2(SimpleMap result, BinaryReader reader)
	{
		// Format 2: Multi-section format with offsets
		
		// Read bounds
		var left = reader.ReadInt32();
		var top = reader.ReadInt32();
		var right = reader.ReadInt32();
		var bottom = reader.ReadInt32();

		var width = right - left;
		var height = bottom - top;

		if (width <= 0 || height <= 0)
		{
			result.Error = "Invalid map dimensions in binary";
			return;
		}

		result.Bounds = new Rectangle(left, top, width, height);

		// Read data offsets
		var tileDataOffset = reader.ReadUInt32();
		var heightDataOffset = reader.ReadUInt32();
		var resourceDataOffset = reader.ReadUInt32();

		// Load tiles
		if (tileDataOffset > 0)
		{
			reader.BaseStream.Seek(tileDataOffset, SeekOrigin.Begin);
			result.Tiles = new TerrainTile[width, height];

			for (var y = 0; y < height; y++)
			{
				for (var x = 0; x < width; x++)
				{
					var type = reader.ReadUInt16();
					var index = reader.ReadByte();
					result.Tiles[x, y] = new TerrainTile(type, index);
				}
			}
		}

		// Load heights
		if (heightDataOffset > 0 && reader.BaseStream.Position < reader.BaseStream.Length)
		{
			try
			{
				reader.BaseStream.Seek(heightDataOffset, SeekOrigin.Begin);
				result.Heights = new byte[width, height];

				for (var y = 0; y < height; y++)
				{
					for (var x = 0; x < width; x++)
					{
						result.Heights[x, y] = reader.ReadByte();
					}
				}
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "Failed to load height data");
			}
		}

		// Load resources
		if (resourceDataOffset > 0 && reader.BaseStream.Position < reader.BaseStream.Length)
		{
			try
			{
				reader.BaseStream.Seek(resourceDataOffset, SeekOrigin.Begin);
				result.Resources = new ResourceTile[width, height];

				for (var y = 0; y < height; y++)
				{
					for (var x = 0; x < width; x++)
					{
						var type = reader.ReadByte();
						var density = reader.ReadByte();
						result.Resources[x, y] = new ResourceTile(type, density);
					}
				}
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "Failed to load resource data");
			}
		}
	}

	public Dictionary<string, string>? FindMapByTitle(string mapTitle, string modId)
	{
		try
		{
			logger.LogInformation("=== Starting map search by title ===");
			logger.LogInformation("Looking for map title: {MapTitle} in mod: {ModId}", mapTitle, modId);
			
			var gameRoot = Path.Combine(environment.ContentRootPath, "..");
			logger.LogInformation("Game root: {GameRoot}", gameRoot);
			
			var modPath = Path.Combine(gameRoot, "mods", modId.ToLowerInvariant());
			logger.LogInformation("Mod path: {ModPath}", modPath);
			
			var mapsPath = Path.Combine(modPath, "maps");
			logger.LogInformation("Maps path: {MapsPath}", mapsPath);

			if (!Directory.Exists(mapsPath))
			{
				logger.LogWarning("Maps directory not found: {MapsPath}", mapsPath);
				
				// Try alternative paths
				var altPaths = new[]
				{
					Path.Combine(gameRoot, "maps"),
					Path.Combine(gameRoot, "mods", "ra", "maps"),
					Path.Combine(gameRoot, "mods", "cnc", "maps"),
					Path.Combine(gameRoot, "mods", "d2k", "maps")
				};
				
				logger.LogInformation("Trying alternative paths...");
				foreach (var altPath in altPaths)
				{
					logger.LogInformation("Checking: {Path}", altPath);
					if (Directory.Exists(altPath))
					{
						logger.LogInformation("Found alternative maps directory: {Path}", altPath);
						mapsPath = altPath;
						break;
					}
				}
				
				if (!Directory.Exists(mapsPath))
					return null;
			}

			// Normalize the title for comparison
			var normalizedSearchTitle = mapTitle.Trim().ToLowerInvariant();
			
			// Search directories
			var directories = Directory.GetDirectories(mapsPath);
			logger.LogInformation("Found {Count} directories to search", directories.Length);
			
			int dirCount = 0;
			foreach (var mapDir in directories)
			{
				dirCount++;
				logger.LogInformation("Checking directory {Count}/{Total}: {Dir}", dirCount, directories.Length, Path.GetFileName(mapDir));
				
				var mapYamlPath = Path.Combine(mapDir, "map.yaml");
				if (File.Exists(mapYamlPath))
				{
					var yaml = File.ReadAllText(mapYamlPath);
					var mapFileTitle = ExtractMapTitle(yaml);
					
					if (!string.IsNullOrEmpty(mapFileTitle))
					{
						var normalizedFileTitle = mapFileTitle.Trim().ToLowerInvariant();
						logger.LogDebug("Comparing '{SearchTitle}' with '{FileTitle}'", normalizedSearchTitle, normalizedFileTitle);
						
						if (normalizedFileTitle == normalizedSearchTitle)
						{
							logger.LogInformation("FOUND MAP by title! Directory: {Path}", mapDir);
							return new Dictionary<string, string>
							{
								["Path"] = mapDir,
								["Name"] = Path.GetFileName(mapDir),
								["Title"] = mapFileTitle
							};
						}
					}
				}
			}

			// Search .oramap files
			var oramapFiles = Directory.GetFiles(mapsPath, "*.oramap");
			logger.LogInformation("Found {Count} .oramap files to search", oramapFiles.Length);
			
			int fileCount = 0;
			foreach (var mapFile in oramapFiles)
			{
				fileCount++;
				logger.LogInformation("Checking .oramap {Count}/{Total}: {File}", fileCount, oramapFiles.Length, Path.GetFileName(mapFile));
				
				try
				{
					using (var zip = ZipFile.OpenRead(mapFile))
					{
						var yamlEntry = zip.GetEntry("map.yaml");
						if (yamlEntry != null)
						{
							using (var stream = yamlEntry.Open())
							using (var reader = new StreamReader(stream))
							{
								var yaml = reader.ReadToEnd();
								var mapFileTitle = ExtractMapTitle(yaml);
								
								if (!string.IsNullOrEmpty(mapFileTitle))
								{
									var normalizedFileTitle = mapFileTitle.Trim().ToLowerInvariant();
									logger.LogDebug("Comparing '{SearchTitle}' with '{FileTitle}'", normalizedSearchTitle, normalizedFileTitle);
									
									if (normalizedFileTitle == normalizedSearchTitle)
									{
										logger.LogInformation("FOUND MAP by title! File: {Path}", mapFile);
										return new Dictionary<string, string>
										{
											["Path"] = mapFile,
											["Name"] = Path.GetFileNameWithoutExtension(mapFile),
											["Title"] = mapFileTitle
										};
									}
								}
							}
						}
					}
				}
				catch (Exception ex)
				{
					logger.LogWarning(ex, "Failed to check map file {MapFile}", mapFile);
				}
			}

			logger.LogWarning("Map not found after searching all directories and .oramap files");
			return null;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to find map {MapTitle} in mod {ModId}", mapTitle, modId);
			return null;
		}
	}
	
	private string ExtractMapTitle(string yaml)
	{
		var lines = yaml.Split('\n');
		foreach (var line in lines)
		{
			var trimmed = line.Trim();
			
			if (trimmed.StartsWith("Title:"))
			{
				return trimmed.Substring(6).Trim();
			}
		}
		return string.Empty;
	}

	// Keep the old method for backward compatibility - just log a warning
	public Dictionary<string, string>? FindMapInMod(string mapUid, string modId)
	{
		logger.LogWarning("FindMapInMod called with UID {MapUid} - this method is deprecated, use FindMapByTitle instead", mapUid);
		// Can't search by UID since maps don't store UIDs, return null
		return null;
	}
}