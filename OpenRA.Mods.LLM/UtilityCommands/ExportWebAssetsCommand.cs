#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using OpenRA.FileFormats;
using OpenRA.FileSystem;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Terrain;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.LLM.UtilityCommands
{
	/// <summary>
	/// Bakes everything a browser-side observer renderer needs into static web
	/// assets: the engine-packed sprite sheets exported twice (base colors with
	/// the player-remap indices transparent, plus a mask carrying the remap ramp
	/// so the client can tint per player), a JSON manifest mapping every
	/// image/sequence/frame/facing to its sheet rectangle, and — when a map is
	/// given — a composited terrain PNG plus map metadata (bounds, spawns,
	/// initial resources, decorative map actors).
	/// </summary>
	public sealed class ExportWebAssetsCommand : IUtilityCommand
	{
		static readonly int[] ChannelMasks = [2, 1, 0, 3];

		string IUtilityCommand.Name => "--llm-export-web-assets";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length >= 2;
		}

		[Desc("OUTPUT-DIR", "[MAP-PATH]", "Bake sprite atlases, manifest and (optionally) a map's terrain for the web observer client.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			// HACK: The engine code assumes that Game.modData is set (same as --dump-sheets).
			var modData = Game.ModData = utility.ModData;

			var outDir = Path.GetFullPath(args[1]);
			Directory.CreateDirectory(outDir);

			Map map = null;
			var tileset = modData.DefaultTerrainInfo.Keys.First();
			SequenceSet sequences;
			if (args.Length > 2)
			{
				var mapPackage = new Folder(Platform.EngineDir).OpenPackage(args[2], modData.ModFiles)
					?? throw new InvalidOperationException($"{args[2]} is not a valid map path");
				map = new Map(modData, mapPackage);
				tileset = map.Tileset;
				sequences = map.Sequences;
			}
			else
				sequences = new SequenceSet(modData.ModFiles, modData, tileset, null);

			sequences.LoadSprites();

			var (basePalette, maskPalette, terrainPalette) = BuildPalettes(modData, tileset, map);

			// --- Sheets: every engine-packed atlas, exported per used channel with
			// both palettes. sheetKeys lets manifest entries reference them.
			var sheetKeys = new Dictionary<Sheet, string>();
			var sheetFiles = new Dictionary<string, object>();

			var indexed = sequences.SpriteCache.SheetBuilders[SheetType.Indexed];
			var sheetIndex = 0;
			foreach (var sheet in indexed.AllSheets)
			{
				var key = $"i{sheetIndex}";
				sheetKeys[sheet] = key;
				var channels = sheet == indexed.Current ? (int)indexed.CurrentChannel + 1 : 4;
				for (var logical = 0; logical < channels; logical++)
				{
					// Files are named by the LOGICAL channel (what sprite.Channel
					// reports); AsPng wants the byte offset in the sheet's BGRA
					// data, hence the ChannelMasks translation (same as --dump-sheets).
					var byteChannel = (TextureChannel)ChannelMasks[logical];
					var baseName = $"{key}.c{logical}.png";
					var maskName = $"{key}.c{logical}.mask.png";
					sheet.AsPng(byteChannel, basePalette).Save(Path.Combine(outDir, baseName), CompressionLevel.Optimal);
					sheet.AsPng(byteChannel, maskPalette).Save(Path.Combine(outDir, maskName), CompressionLevel.Optimal);
					sheetFiles[$"{key}.c{logical}"] = new { file = baseName, mask = maskName };
				}

				sheetIndex++;
			}

			sheetIndex = 0;
			foreach (var sheet in sequences.SpriteCache.SheetBuilders[SheetType.BGRA].AllSheets)
			{
				var key = $"b{sheetIndex}";
				sheetKeys[sheet] = key;
				var fileName = $"{key}.png";
				sheet.AsPng().Save(Path.Combine(outDir, fileName), CompressionLevel.Optimal);
				sheetFiles[key] = new { file = fileName };
				sheetIndex++;
			}

			// --- Manifest: dedup sprite rectangles, then map every
			// image/sequence/frame/facing to a sprite id.
			var spriteIds = new Dictionary<Sprite, int>();
			var spriteTable = new List<object>();

			int SpriteId(Sprite s)
			{
				if (s == null || !sheetKeys.TryGetValue(s.Sheet, out var sheetKey))
					return -1;

				if (spriteIds.TryGetValue(s, out var id))
					return id;

				id = spriteTable.Count;
				spriteIds[s] = id;
				var indexedSheet = sheetKey.StartsWith('i');
				spriteTable.Add(new
				{
					s = indexedSheet ? $"{sheetKey}.c{(int)s.Channel}" : sheetKey,
					x = s.Bounds.X,
					y = s.Bounds.Y,
					w = s.Bounds.Width,
					h = s.Bounds.Height,
					ox = s.Offset.X,
					oy = s.Offset.Y,
				});
				return id;
			}

			var images = new Dictionary<string, Dictionary<string, object>>();
			foreach (var image in sequences.Images)
			{
				var imageEntry = new Dictionary<string, object>();
				foreach (var sequenceName in sequences.Sequences(image))
				{
					try
					{
						var seq = sequences.GetSequence(image, sequenceName);
						var ids = new List<int>();
						var shadowIds = new List<int>();
						var anyShadow = false;
						for (var frame = 0; frame < seq.Length; frame++)
						{
							for (var facing = 0; facing < seq.Facings; facing++)
							{
								var angle = new WAngle(facing * 1024 / seq.Facings);
								ids.Add(SpriteId(seq.GetSprite(frame, angle)));
								var shadow = seq.GetShadow(frame, angle);
								shadowIds.Add(SpriteId(shadow));
								anyShadow |= shadow != null;
							}
						}

						imageEntry[sequenceName] = new
						{
							length = seq.Length,
							facings = seq.Facings,
							tick = seq.Tick,
							scale = seq.Scale,
							ids,
							shadowIds = anyShadow ? shadowIds : null,
						};
					}
					catch (Exception e)
					{
						Console.WriteLine($"skipping {image}.{sequenceName}: {e.Message}");
					}
				}

				if (imageEntry.Count > 0)
					images[image] = imageEntry;
			}

			var manifest = new
			{
				mod = modData.Manifest.Id,
				tileset,
				sheets = sheetFiles,
				sprites = spriteTable,
				images,
			};

			var jsonOptions = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
			File.WriteAllText(Path.Combine(outDir, "manifest.json"), JsonSerializer.Serialize(manifest, jsonOptions));
			Console.WriteLine($"manifest: {spriteTable.Count} sprites, {images.Count} images, {sheetFiles.Count} sheet files");

			if (map != null)
				ExportMap(modData, map, terrainPalette, outDir, jsonOptions);

			sequences.Dispose();
		}

		/// <summary>Base palette (remap transparent), tint mask palette (remap ramp only), and the untouched terrain palette.</summary>
		static (ImmutablePalette Base, ImmutablePalette Mask, ImmutablePalette Terrain) BuildPalettes(ModData modData, string tileset, Map map)
		{
			var rules = map?.Rules ?? modData.DefaultRules;
			var world = rules.Actors[SystemActors.World];

			// PaletteFromFileInfo is internal and its IProvidesCursorPaletteInfo
			// surface is cursor-gated, so read its public fields reflectively.
			static object Field(object o, string name) => o.GetType().GetField(name)?.GetValue(o);

			var paletteInfo = world.TraitInfos<TraitInfo>()
				.FirstOrDefault(t => t.GetType().Name == "PaletteFromFileInfo"
					&& (string)Field(t, "Name") == "terrain"
					&& (Field(t, "Tileset") is not string ts
						|| string.Equals(ts, tileset, StringComparison.OrdinalIgnoreCase)))
				?? throw new InvalidOperationException($"No terrain PaletteFromFile for tileset {tileset}");

			var remapInfo = world.TraitInfos<PlayerColorPaletteInfo>().FirstOrDefault(p => p.RemapIndex.Length > 0)
				?? throw new InvalidOperationException("No PlayerColorPalette found for remap indices");
			var remap = remapInfo.RemapIndex;

			var fs = (IReadOnlyFileSystem)map ?? modData.DefaultFileSystem;
			ImmutablePalette terrain;
			using (var stream = fs.Open((string)Field(paletteInfo, "Filename")))
				terrain = new ImmutablePalette(stream,
					(System.Collections.Immutable.ImmutableArray<int>)Field(paletteInfo, "TransparentIndex"),
					(System.Collections.Immutable.ImmutableArray<int>)Field(paletteInfo, "ShadowIndex"));

			var baseColors = new uint[Palette.Size];
			var maskColors = new uint[Palette.Size];
			for (var i = 0; i < Palette.Size; i++)
			{
				baseColors[i] = terrain[i];
				maskColors[i] = 0;
			}

			// The remap list orders the player-color ramp light -> dark; the mask
			// stores each pixel's ramp position as a gray value so the client can
			// recolor with any player color.
			for (var r = 0; r < remap.Length; r++)
			{
				var gray = (uint)(255 * r / Math.Max(1, remap.Length - 1));
				baseColors[remap[r]] = 0;
				maskColors[remap[r]] = 0xFF000000 | (gray << 16) | (gray << 8) | gray;
			}

			return (new ImmutablePalette(baseColors), new ImmutablePalette(maskColors), terrain);
		}

		/// <summary>Composites the map's terrain into one PNG and writes map.json (bounds, spawns, resources, decorative actors).</summary>
		static void ExportMap(ModData modData, Map map, ImmutablePalette terrainPalette, string outDir, JsonSerializerOptions jsonOptions)
		{
			if (map.Rules.TerrainInfo is not ITemplatedTerrainInfo templated)
				throw new InvalidOperationException("Map tileset is not template-based");

			const int TileSize = 24;
			var width = map.MapSize.Width * TileSize;
			var height = map.MapSize.Height * TileSize;
			var canvas = new byte[width * height * 4];

			var frameCache = new Dictionary<string, ISpriteFrame[]>();
			ISpriteFrame[] Frames(string filename)
			{
				if (!frameCache.TryGetValue(filename, out var frames))
					frameCache[filename] = frames = FrameLoader.GetFrames(map, filename, modData.SpriteLoaders, out _);
				return frames;
			}

			for (var v = 0; v < map.MapSize.Height; v++)
			{
				for (var u = 0; u < map.MapSize.Width; u++)
				{
					var tile = map.Tiles[new MPos(u, v)];
					if (!templated.Templates.TryGetValue(tile.Type, out var template)
						|| template is not DefaultTerrainTemplateInfo defaultTemplate || defaultTemplate.Images.Length == 0)
						continue;

					ISpriteFrame frame;
					try
					{
						var frames = Frames(defaultTemplate.Images[0]);
						frame = frames[Math.Min(tile.Index, frames.Length - 1)];
					}
					catch (Exception)
					{
						continue;
					}

					BlitIndexed(canvas, width, height, u * TileSize, v * TileSize, frame, terrainPalette);
				}
			}

			new Png(canvas, SpriteFrameType.Rgba32, width, height).Save(Path.Combine(outDir, "terrain.png"), CompressionLevel.Optimal);

			// Map metadata: playable bounds, spawns, initial resources, and the
			// decorative actors (trees etc.) the client should draw over terrain.
			var spawns = new List<int[]>();
			var mapActors = new List<object>();
			foreach (var kv in map.ActorDefinitions)
			{
				try
				{
					var actor = new ActorReference(kv.Value.Value, kv.Value);
					var cell = actor.Get<LocationInit>().Value;
					if (actor.Type == "mpspawn")
						spawns.Add([cell.X, cell.Y]);
					else
						mapActors.Add(new { type = actor.Type, x = cell.X, y = cell.Y });
				}
				catch (Exception)
				{
					// actor without a location (e.g. players) — not drawable
				}
			}

			var resources = new List<int[]>();
			for (var v = 0; v < map.MapSize.Height; v++)
			{
				for (var u = 0; u < map.MapSize.Width; u++)
				{
					var res = map.Resources[new MPos(u, v)];
					if (res.Type != 0)
						resources.Add([u, v, res.Type, res.Index]);
				}
			}

			var mapJson = new
			{
				title = map.Title,
				tileSize = TileSize,
				width = map.MapSize.Width,
				height = map.MapSize.Height,
				bounds = new { x = map.Bounds.X, y = map.Bounds.Y, w = map.Bounds.Width, h = map.Bounds.Height },
				spawns,
				actors = mapActors,
				resources,
			};

			File.WriteAllText(Path.Combine(outDir, "map.json"), JsonSerializer.Serialize(mapJson, jsonOptions));
			Console.WriteLine($"map: {map.Title} {map.MapSize.Width}x{map.MapSize.Height}, {mapActors.Count} actors, {resources.Count} resource cells");
		}

		static void BlitIndexed(byte[] canvas, int canvasWidth, int canvasHeight, int px, int py, ISpriteFrame frame, IPalette palette)
		{
			var data = frame.Data;
			var w = frame.Size.Width;
			var h = frame.Size.Height;
			for (var y = 0; y < h; y++)
			{
				var cy = py + y;
				if (cy < 0 || cy >= canvasHeight)
					continue;

				for (var x = 0; x < w; x++)
				{
					var cx = px + x;
					if (cx < 0 || cx >= canvasWidth)
						continue;

					var index = data[y * w + x];
					if (index == 0)
						continue;

					var color = palette[index];
					var offset = (cy * canvasWidth + cx) * 4;
					canvas[offset] = (byte)((color >> 16) & 0xFF);
					canvas[offset + 1] = (byte)((color >> 8) & 0xFF);
					canvas[offset + 2] = (byte)(color & 0xFF);
					canvas[offset + 3] = (byte)((color >> 24) & 0xFF);
				}
			}
		}
	}
}
