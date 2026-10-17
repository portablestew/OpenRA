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
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using OpenRA.FileFormats;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// The map-state reply (P2.11). Path is map-state.png in the workspace, or null when the image could not be rendered. Render is
	/// "ok" or "unavailable". Reason names the failure and is left out when the render succeeded. MapPxPerCell is the image scale,
	/// Origin is the top-left map cell, and Size is the image in pixels.
	/// </summary>
	public sealed record MapStateV1(bool Ok, string Cmd, string Path, string Render,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string Reason,
		int MapPxPerCell, int[] Origin, int[] Size, int Tick);

	/// <summary>
	/// The map-state terrain and fog layer (P2.11). Each map cell is drawn at MapPxPerCell pixels square, with its terrain colour from
	/// PlayerRadarTerrain.GetColor, split along the diagonal as the radar splits it. An unexplored cell is the shroud colour. An explored
	/// cell the observer cannot see is the terrain under the fog colour. A visible cell is its terrain colour. The fog colours are
	/// constants that copy RadarWidget's, so the socket does not depend on a widget. Rendering is CPU-only, with no renderer or GPU.
	/// </summary>
	public static class AgentMapState
	{
		/// <summary>The image scale: pixels per map cell along each side.</summary>
		public const int MapPxPerCell = 4;

		/// <summary>The shroud colour, black and opaque: RadarWidget.ColorShroud.</summary>
		public const uint ShroudArgb = 0xFF000000;

		/// <summary>The fog's alpha over the terrain, black: RadarWidget.ColorFog is black at 128.</summary>
		public const byte FogAlpha = 128;

		/// <summary>The file name in the workspace. The render replaces it through a temporary file.</summary>
		public const string FileName = "map-state.png";

		/// <summary>Renders the map-state image for the observer and writes it to the workspace. A failure is reported in the reply, not thrown.</summary>
		public static MapStateV1 Render(World world, Player observer)
		{
			var tick = world.WorldTick;
			var bounds = world.Map.Bounds;
			var origin = new[] { bounds.Left, bounds.Top };
			var size = new[] { bounds.Width * MapPxPerCell, bounds.Height * MapPxPerCell };

			try
			{
				var path = Write(Encode(world, observer, bounds));
				return new MapStateV1(true, "map_state", path, "ok", null, MapPxPerCell, origin, size, tick);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
			{
				return new MapStateV1(true, "map_state", null, "unavailable", e.Message, MapPxPerCell, origin, size, tick);
			}
		}

		/// <summary>The PNG bytes of the image: RGBA, one block of MapPxPerCell pixels per map cell in the bounds.</summary>
		static byte[] Encode(World world, Player observer, Rectangle bounds)
		{
			var map = world.Map;
			var layers = world.WorldActor.TraitsImplementing<IRadarTerrainLayer>().ToArray();
			var width = bounds.Width * MapPxPerCell;
			var height = bounds.Height * MapPxPerCell;
			var data = new byte[width * height * 4];

			for (var y = bounds.Top; y < bounds.Bottom; y++)
				for (var x = bounds.Left; x < bounds.Right; x++)
				{
					var uv = new CPos(x, y).ToMPos(map);
					var (left, right) = PlayerRadarTerrain.GetColor(map, layers, uv);
					var explored = observer == null || observer.Shroud.IsExplored(uv);
					var visible = observer == null || observer.Shroud.IsVisible(uv);

					var leftShade = Shade(left, explored, visible);
					var rightShade = Shade(right, explored, visible);
					for (var py = 0; py < MapPxPerCell; py++)
						for (var px = 0; px < MapPxPerCell; px++)
						{
							var shade = px + py < MapPxPerCell ? leftShade : rightShade;
							var index = (((y - bounds.Top) * MapPxPerCell + py) * width + (x - bounds.Left) * MapPxPerCell + px) * 4;
							data[index] = (byte)(shade >> 16);
							data[index + 1] = (byte)(shade >> 8);
							data[index + 2] = (byte)shade;
							data[index + 3] = (byte)(shade >> 24);
						}
				}

			return new Png(data, SpriteFrameType.Rgba32, width, height).Save();
		}

		/// <summary>The colour a cell's half is drawn in: shroud when unexplored, the terrain under fog when explored but not visible, else the terrain.</summary>
		public static uint Shade(uint argb, bool explored, bool visible)
		{
			if (!explored)
				return ShroudArgb;

			if (!visible)
				return Darken(argb);

			return argb;
		}

		/// <summary>The colour under the fog: black at FogAlpha over argb, with the alpha kept opaque.</summary>
		public static uint Darken(uint argb)
		{
			const uint Keep = 255u - FogAlpha;
			var r = (argb >> 16 & 0xFF) * Keep / 255;
			var g = (argb >> 8 & 0xFF) * Keep / 255;
			var b = (argb & 0xFF) * Keep / 255;
			return 0xFF000000 | r << 16 | g << 8 | b;
		}

		/// <summary>
		/// Writes the image to a temporary file in the workspace, then renames it over map-state.png, so a reader never sees a partial file.
		/// The temporary file is removed if the write fails. Returns the path of the image.
		/// </summary>
		static string Write(byte[] png)
		{
			var root = AgentWorkspace.Root;
			Directory.CreateDirectory(root);

			var path = Path.Combine(root, FileName);
			var temp = Path.Combine(root, FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
			try
			{
				File.WriteAllBytes(temp, png);
				File.Move(temp, path, overwrite: true);
			}
			finally
			{
				if (File.Exists(temp))
					File.Delete(temp);
			}

			return path;
		}
	}
}
