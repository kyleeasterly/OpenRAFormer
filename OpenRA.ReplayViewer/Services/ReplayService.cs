using System.IO.Compression;
using OpenRA;
using OpenRA.FileFormats;
using OpenRA.FileSystem;
using OpenRA.Graphics;
using OpenRA.Network;
using OpenRA.Primitives;

namespace OpenRA.ReplayViewer.Services;

public class ReplayService
{
	public class ReplayInfo
	{
		public string FilePath { get; set; } = "";
		public GameInformation? GameInfo { get; set; }
		public Map? Map { get; set; }
		public string? Error { get; set; }
		public byte[]? MapData { get; set; }
		public TerrainTile[,]? Tiles { get; set; }
		public ResourceTile[,]? Resources { get; set; }
		public byte[,]? Heights { get; set; }
	}

	private readonly ILogger<ReplayService> logger;
	private readonly IWebHostEnvironment environment;

	public ReplayService(ILogger<ReplayService> logger, IWebHostEnvironment environment)
	{
		this.logger = logger;
		this.environment = environment;
	}

	public async Task<ReplayInfo> LoadReplayAsync(string filePath)
	{
		var result = new ReplayInfo { FilePath = filePath };

		try
		{
			if (!File.Exists(filePath))
			{
				result.Error = $"File not found: {filePath}";
				return result;
			}

			await Task.Run(() =>
			{
				// Read replay metadata
				var metadata = ReplayMetadata.Read(filePath);
				if (metadata == null)
				{
					result.Error = "Failed to read replay metadata";
					return;
				}

				result.GameInfo = metadata.GameInfo;

				// Try to load the map data
				if (metadata.GameInfo.MapData != null && metadata.GameInfo.MapData.Length > 0)
				{
					// Map is embedded in the replay
					result.MapData = Convert.FromBase64String(metadata.GameInfo.MapData);
					LoadMapFromData(result);
				}
				else if (!string.IsNullOrEmpty(metadata.GameInfo.MapUid))
				{
					// Try to find the map locally
					var mapPath = FindLocalMap(metadata.GameInfo.MapUid);
					if (mapPath != null)
					{
						LoadMapFromFile(result, mapPath);
					}
					else
					{
						result.Error = $"Map not found locally: {metadata.GameInfo.MapUid}";
					}
				}
			});
		}
		catch (Exception ex)
		{
			result.Error = $"Error loading replay: {ex.Message}";
			logger.LogError(ex, "Failed to load replay from {FilePath}", filePath);
		}

		return result;
	}

	private void LoadMapFromData(ReplayInfo result)
	{
		try
		{
			if (result.MapData == null)
				return;

			// The map data is a ZIP archive
			using (var stream = new MemoryStream(result.MapData))
			using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
			{
				// Find the map.yaml entry
				var mapYamlEntry = zip.GetEntry("map.yaml");
				if (mapYamlEntry == null)
				{
					result.Error = "No map.yaml found in embedded map data";
					return;
				}

				// Find the map.bin entry for binary map data
				var mapBinEntry = zip.GetEntry("map.bin");
				if (mapBinEntry != null)
				{
					using (var binStream = mapBinEntry.Open())
					{
						ParseMapBinary(result, binStream);
					}
				}
			}
		}
		catch (Exception ex)
		{
			result.Error = $"Failed to load embedded map data: {ex.Message}";
			logger.LogError(ex, "Failed to load embedded map data");
		}
	}

	private void LoadMapFromFile(ReplayInfo result, string mapPath)
	{
		try
		{
			// This would load from a local .oramap file
			// For now, we'll just set an error
			result.Error = "Loading from local map files not yet implemented";
		}
		catch (Exception ex)
		{
			result.Error = $"Failed to load map from file: {ex.Message}";
			logger.LogError(ex, "Failed to load map from {MapPath}", mapPath);
		}
	}

	private string? FindLocalMap(string mapUid)
	{
		// Look for maps in standard locations
		// This is a simplified version - the real implementation would check multiple paths
		var possiblePaths = new[]
		{
			Path.Combine(environment.ContentRootPath, "..", "mods", "ra", "maps"),
			Path.Combine(environment.ContentRootPath, "..", "mods", "cnc", "maps"),
			Path.Combine(environment.ContentRootPath, "..", "mods", "d2k", "maps"),
		};

		foreach (var basePath in possiblePaths)
		{
			if (Directory.Exists(basePath))
			{
				var mapDirs = Directory.GetDirectories(basePath);
				foreach (var dir in mapDirs)
				{
					// Check if this directory contains a map with matching UID
					var mapYaml = Path.Combine(dir, "map.yaml");
					if (File.Exists(mapYaml))
					{
						// For now, just return the first map found
						// A real implementation would parse the YAML and check the UID
						return dir;
					}
				}
			}
		}

		return null;
	}

	private void ParseMapBinary(ReplayInfo result, Stream stream)
	{
		try
		{
			using (var reader = new BinaryReader(stream))
			{
				// Read header
				var format = reader.ReadByte();
				if (format != 2) // Format version 2 is current
				{
					result.Error = $"Unsupported map format version: {format}";
					return;
				}

				// Read map bounds
				var boundsLeft = reader.ReadInt32();
				var boundsTop = reader.ReadInt32();
				var boundsRight = reader.ReadInt32();
				var boundsBottom = reader.ReadInt32();

				var width = boundsRight - boundsLeft;
				var height = boundsBottom - boundsTop;

				// Read data offsets
				var tileDataOffset = reader.ReadUInt32();
				var heightDataOffset = reader.ReadUInt32();
				var resourceDataOffset = reader.ReadUInt32();

				// Read tile data
				stream.Seek(tileDataOffset, SeekOrigin.Begin);
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

				// Read height data if present
				if (heightDataOffset > 0)
				{
					stream.Seek(heightDataOffset, SeekOrigin.Begin);
					result.Heights = new byte[width, height];
					for (var y = 0; y < height; y++)
					{
						for (var x = 0; x < width; x++)
						{
							result.Heights[x, y] = reader.ReadByte();
						}
					}
				}

				// Read resource data if present
				if (resourceDataOffset > 0)
				{
					stream.Seek(resourceDataOffset, SeekOrigin.Begin);
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
			}
		}
		catch (Exception ex)
		{
			result.Error = $"Failed to parse map binary data: {ex.Message}";
			logger.LogError(ex, "Failed to parse map binary");
		}
	}
}
