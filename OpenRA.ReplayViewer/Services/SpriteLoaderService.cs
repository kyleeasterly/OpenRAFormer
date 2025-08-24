using OpenRA;
using OpenRA.Graphics;
using OpenRA.Mods.Cnc.SpriteLoaders;
using OpenRA.Mods.Common.SpriteLoaders;
using OpenRA.Primitives;

namespace OpenRA.ReplayViewer.Services;

public class SpriteLoaderService
{
	private readonly ILogger<SpriteLoaderService> logger;
	private readonly List<ISpriteLoader> loaders;

	public SpriteLoaderService(ILogger<SpriteLoaderService> logger)
	{
		this.logger = logger;
		
		// Register all known sprite loaders
		loaders = new List<ISpriteLoader>
		{
			new TmpRALoader(),      // For .tem files (RA terrain templates)
			new TmpTDLoader(),      // For TD .tem files
			new ShpTDLoader(),      // For TD .shp files
			new ShpTSLoader(),      // For TS .shp files
			new PngSheetLoader(),   // For .png files
			new TgaLoader(),        // For .tga files
			new DdsLoader(),        // For .dds files
		};
		
		logger.LogInformation("Initialized {Count} sprite loaders", loaders.Count);
	}

	public ISpriteFrame[]? LoadSprite(Stream stream, string filename)
	{
		if (stream == null)
		{
			logger.LogWarning("Null stream provided for {Filename}", filename);
			return null;
		}

		// Try each loader
		foreach (var loader in loaders)
		{
			try
			{
				stream.Position = 0;
				if (loader.TryParseSprite(stream, filename, out var frames, out var metadata))
				{
					logger.LogDebug("Successfully loaded {Filename} with {LoaderType} ({FrameCount} frames)",
						filename, loader.GetType().Name, frames?.Length ?? 0);
					return frames;
				}
			}
			catch (Exception ex)
			{
				logger.LogDebug(ex, "Loader {LoaderType} failed for {Filename}", 
					loader.GetType().Name, filename);
			}
		}

		logger.LogWarning("No loader could parse {Filename}", filename);
		return null;
	}

	public ISpriteFrame[]? LoadSpriteFromBytes(byte[] data, string filename)
	{
		using var stream = new MemoryStream(data);
		return LoadSprite(stream, filename);
	}

	public byte[] ConvertFrameToRgba(ISpriteFrame frame, Color[] palette)
	{
		if (frame == null)
			throw new ArgumentNullException(nameof(frame));

		var width = frame.Size.Width;
		var height = frame.Size.Height;
		var rgbaData = new byte[width * height * 4];

		switch (frame.Type)
		{
			case SpriteFrameType.Indexed8:
				ConvertIndexed8ToRgba(frame.Data, rgbaData, palette);
				break;
				
			case SpriteFrameType.Bgra32:
				// Already in BGRA format, convert to RGBA
				for (int i = 0; i < frame.Data.Length; i += 4)
				{
					rgbaData[i] = frame.Data[i + 2];     // R
					rgbaData[i + 1] = frame.Data[i + 1]; // G
					rgbaData[i + 2] = frame.Data[i];     // B
					rgbaData[i + 3] = frame.Data[i + 3]; // A
				}
				break;
				
			case SpriteFrameType.Rgba32:
				// Already RGBA
				Array.Copy(frame.Data, rgbaData, frame.Data.Length);
				break;
				
			case SpriteFrameType.Rgb24:
				// RGB to RGBA
				for (int i = 0, j = 0; i < frame.Data.Length; i += 3, j += 4)
				{
					rgbaData[j] = frame.Data[i];         // R
					rgbaData[j + 1] = frame.Data[i + 1]; // G
					rgbaData[j + 2] = frame.Data[i + 2]; // B
					rgbaData[j + 3] = 255;               // A
				}
				break;
				
			default:
				logger.LogWarning("Unsupported sprite frame type: {Type}", frame.Type);
				break;
		}

		return rgbaData;
	}

	private void ConvertIndexed8ToRgba(byte[] indexedData, byte[] rgbaData, Color[] palette)
	{
		if (palette == null || palette.Length == 0)
		{
			// Use a default grayscale palette if none provided
			palette = CreateDefaultPalette();
		}

		for (int i = 0; i < indexedData.Length; i++)
		{
			var paletteIndex = indexedData[i];
			var color = palette[Math.Min(paletteIndex, palette.Length - 1)];
			
			var offset = i * 4;
			rgbaData[offset] = color.R;
			rgbaData[offset + 1] = color.G;
			rgbaData[offset + 2] = color.B;
			rgbaData[offset + 3] = color.A;
		}
	}

	private Color[] CreateDefaultPalette()
	{
		var palette = new Color[256];
		for (int i = 0; i < 256; i++)
		{
			var value = (byte)i;
			palette[i] = Color.FromArgb(255, value, value, value);
		}
		return palette;
	}

	public class SpriteFrameInfo
	{
		public int Width { get; set; }
		public int Height { get; set; }
		public SpriteFrameType Type { get; set; }
		public byte[] Data { get; set; } = Array.Empty<byte>();
		public float2 Offset { get; set; }
	}

	public SpriteFrameInfo? GetFrameInfo(ISpriteFrame frame)
	{
		if (frame == null)
			return null;

		return new SpriteFrameInfo
		{
			Width = frame.Size.Width,
			Height = frame.Size.Height,
			Type = frame.Type,
			Data = frame.Data,
			Offset = frame.Offset
		};
	}
}