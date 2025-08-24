using System.Collections.Concurrent;
using OpenRA.Graphics;
using OpenRA.Primitives;

namespace OpenRA.ReplayViewer.Services;

public class FrameCacheService
{
	private readonly ILogger<FrameCacheService> logger;
	private readonly FileSystemService fileSystemService;
	private readonly SpriteLoaderService spriteLoaderService;
	private readonly ConcurrentDictionary<string, ISpriteFrame[]?> frameCache = new();

	public FrameCacheService(
		ILogger<FrameCacheService> logger,
		FileSystemService fileSystemService,
		SpriteLoaderService spriteLoaderService)
	{
		this.logger = logger;
		this.fileSystemService = fileSystemService;
		this.spriteLoaderService = spriteLoaderService;
	}

	public ISpriteFrame[]? GetFrames(string modId, string filename)
	{
		var cacheKey = $"{modId}:{filename}";
		
		return frameCache.GetOrAdd(cacheKey, key =>
		{
			logger.LogDebug("Loading frames for {Filename} in mod {ModId}", filename, modId);
			
			using var stream = fileSystemService.OpenFile(modId, filename);
			if (stream == null)
			{
				logger.LogWarning("File not found: {Filename} in mod {ModId}", filename, modId);
				return null;
			}

			var frames = spriteLoaderService.LoadSprite(stream, filename);
			if (frames != null)
			{
				logger.LogInformation("Cached {Count} frames for {Filename}", frames.Length, filename);
			}
			else
			{
				logger.LogWarning("Failed to load frames for {Filename}", filename);
			}

			return frames;
		});
	}

	public ISpriteFrame? GetFrame(string modId, string filename, int frameIndex)
	{
		var frames = GetFrames(modId, filename);
		if (frames == null || frameIndex < 0 || frameIndex >= frames.Length)
			return null;
			
		return frames[frameIndex];
	}

	public int GetFrameCount(string modId, string filename)
	{
		var frames = GetFrames(modId, filename);
		return frames?.Length ?? 0;
	}

	public void ClearCache()
	{
		frameCache.Clear();
		logger.LogInformation("Frame cache cleared");
	}

	public class CachedSprite
	{
		public string Filename { get; set; } = "";
		public int FrameIndex { get; set; }
		public byte[] RgbaData { get; set; } = Array.Empty<byte>();
		public int Width { get; set; }
		public int Height { get; set; }
		public float2 Offset { get; set; }
	}

	private readonly ConcurrentDictionary<string, CachedSprite> rgbaCache = new();

	public CachedSprite? GetRgbaSprite(string modId, string filename, int frameIndex, Color[]? palette = null)
	{
		var cacheKey = $"{modId}:{filename}:{frameIndex}:{palette?.GetHashCode() ?? 0}";
		
		return rgbaCache.GetOrAdd(cacheKey, key =>
		{
			var frame = GetFrame(modId, filename, frameIndex);
			if (frame == null)
				return null;

			var rgbaData = spriteLoaderService.ConvertFrameToRgba(frame, palette ?? CreateDefaultPalette());
			
			return new CachedSprite
			{
				Filename = filename,
				FrameIndex = frameIndex,
				RgbaData = rgbaData,
				Width = frame.Size.Width,
				Height = frame.Size.Height,
				Offset = frame.Offset
			};
		});
	}

	private Color[] CreateDefaultPalette()
	{
		var palette = new Color[256];
		for (int i = 0; i < 256; i++)
		{
			var value = (byte)i;
			palette[i] = Color.FromArgb(255, value, value, value);
		}
		palette[0] = Color.FromArgb(0, 0, 0, 0); // Transparent
		return palette;
	}

	public void PreloadTemplates(string modId, IEnumerable<string> templateFiles)
	{
		var tasks = new List<Task>();
		
		foreach (var file in templateFiles)
		{
			tasks.Add(Task.Run(() =>
			{
				GetFrames(modId, file);
			}));
		}

		Task.WaitAll(tasks.ToArray());
		logger.LogInformation("Preloaded {Count} template files", templateFiles.Count());
	}

	public CacheStatistics GetStatistics()
	{
		return new CacheStatistics
		{
			CachedFrameSets = frameCache.Count,
			CachedRgbaSprites = rgbaCache.Count,
			TotalFrames = frameCache.Values.Where(v => v != null).Sum(v => v!.Length),
			EstimatedMemoryMB = EstimateMemoryUsage() / (1024 * 1024)
		};
	}

	private long EstimateMemoryUsage()
	{
		long totalBytes = 0;

		// Estimate frame cache memory
		foreach (var frames in frameCache.Values)
		{
			if (frames != null)
			{
				foreach (var frame in frames)
				{
					totalBytes += frame.Data.Length;
				}
			}
		}

		// Estimate RGBA cache memory
		foreach (var sprite in rgbaCache.Values)
		{
			if (sprite != null)
			{
				totalBytes += sprite.RgbaData.Length;
			}
		}

		return totalBytes;
	}

	public class CacheStatistics
	{
		public int CachedFrameSets { get; set; }
		public int CachedRgbaSprites { get; set; }
		public int TotalFrames { get; set; }
		public long EstimatedMemoryMB { get; set; }
	}
}