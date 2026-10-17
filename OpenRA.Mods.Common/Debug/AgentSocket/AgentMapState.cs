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
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

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
	/// The map-state image (P2.11, P2.12). Each map cell is drawn at MapPxPerCell pixels square. The terrain comes first, from
	/// PlayerRadarTerrain.GetColor, split along the diagonal as the radar splits it. An unexplored cell is the shroud colour, and an
	/// explored cell the observer cannot see is its terrain under the fog colour. Then the overlays, in this order: a green crosshair
	/// across the map through each selected unit, the yellow path of each moving selected unit ending in a red X, a red line from each
	/// selected unit to its attack target, and last the blips, one owner-coloured block per actor the observer knows. The fog colours
	/// copy RadarWidget's, as constants. Rendering is CPU-only, with no renderer or GPU.
	/// </summary>
	public static class AgentMapState
	{
		/// <summary>The image scale: pixels per map cell along each side.</summary>
		public const int MapPxPerCell = 4;

		/// <summary>The shroud colour, black and opaque: RadarWidget.ColorShroud.</summary>
		public const uint ShroudArgb = 0xFF000000;

		/// <summary>The fog's alpha over the terrain, black: RadarWidget.ColorFog is black at 128.</summary>
		public const byte FogAlpha = 128;

		/// <summary>The selection crosshair: green, as TODO asks.</summary>
		public const uint GreenArgb = 0xFF00FF00;

		/// <summary>The path segments: yellow, as TODO asks.</summary>
		public const uint YellowArgb = 0xFFFFFF00;

		/// <summary>The path's end X and the target line: red, as TODO asks.</summary>
		public const uint RedArgb = 0xFFFF0000;

		/// <summary>The file name in the workspace. The render replaces it through a temporary file.</summary>
		public const string FileName = "map-state.png";

		/// <summary>The last render, and the world and tick it was taken at. The envelope reuses it until the world's tick moves (P2.13).</summary>
		static World latestWorld;
		static int latestTick;
		static MapStateV1 latest;

		/// <summary>
		/// The map-state image as of the world's current tick: the last render when the world has not moved since, else a new render.
		/// The envelope calls this, so the file it names is always the one written at map_state_tick.
		/// </summary>
		public static MapStateV1 Refresh(World world, Player observer)
		{
			if (latest != null && ReferenceEquals(latestWorld, world) && latestTick == world.WorldTick)
				return latest;

			return Render(world, observer);
		}

		/// <summary>Renders the map-state image for the observer and writes it to the workspace. A failure is reported in the reply, not thrown.</summary>
		public static MapStateV1 Render(World world, Player observer)
		{
			var tick = world.WorldTick;
			var bounds = world.Map.Bounds;
			var origin = new[] { bounds.Left, bounds.Top };
			var size = new[] { bounds.Width * MapPxPerCell, bounds.Height * MapPxPerCell };

			MapStateV1 result;
			try
			{
				var path = Write(Encode(world, observer, bounds));
				result = new MapStateV1(true, "map_state", path, "ok", null, MapPxPerCell, origin, size, tick);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
			{
				result = new MapStateV1(true, "map_state", null, "unavailable", e.Message, MapPxPerCell, origin, size, tick);
			}

			latestWorld = world;
			latestTick = tick;
			latest = result;
			return result;
		}

		/// <summary>The PNG bytes of the image: terrain, then the overlays, then the blips.</summary>
		static byte[] Encode(World world, Player observer, Rectangle bounds)
		{
			var canvas = new Canvas(bounds);
			DrawTerrain(canvas, world, observer, bounds);

			var selected = world.Selection.Actors.Where(a => !a.IsDead && a.OccupiesSpace != null).ToArray();
			foreach (var unit in selected)
				canvas.Crosshair(unit.Location, GreenArgb);

			foreach (var unit in selected)
			{
				var activities = unit.CurrentActivity?.ActivitiesImplementing<IActivityInterface>().ToArray() ?? [];

				foreach (var move in activities.OfType<Move>())
				{
					var previous = unit.Location;
					CPos? last = null;
					foreach (var cell in move.RemainingPath)
					{
						canvas.Line(previous, cell, YellowArgb);
						previous = cell;
						last = cell;
					}

					if (last is { } end)
						canvas.Cross(end, RedArgb);
				}

				foreach (var attack in activities.OfType<Attack>())
				{
					var target = attack.CurrentTarget;
					if (target.Type != TargetType.Invalid)
						canvas.Line(unit.Location, world.Map.CellContaining(target.CenterPosition), RedArgb);
				}
			}

			foreach (var actor in world.Actors)
			{
				if (actor.OccupiesSpace == null || actor.IsDead || AgentStateDump.IsScenery(actor))
					continue;

				if (actor.Owner == observer || AgentFog.Known(actor, observer))
					canvas.FillCell(actor.Location, actor.Owner.Color.ToArgb());
			}

			return new Png(canvas.Data, SpriteFrameType.Rgba32, canvas.Width, canvas.Height).Save();
		}

		/// <summary>The terrain layer (P2.11): each cell's colour, split along the diagonal, shaded by the observer's shroud.</summary>
		static void DrawTerrain(Canvas canvas, World world, Player observer, Rectangle bounds)
		{
			var map = world.Map;
			var layers = world.WorldActor.TraitsImplementing<IRadarTerrainLayer>().ToArray();

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
							canvas.Set(new CPos(x, y), px, py, px + py < MapPxPerCell ? leftShade : rightShade);
				}
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

		/// <summary>
		/// The RGBA pixel buffer the image is drawn into, with the map's top-left cell at pixel 0,0. Cells outside the bounds are skipped.
		/// Each pixel is set from its colour, in RGBA order for the PNG encoder.
		/// </summary>
		sealed class Canvas
		{
			readonly Rectangle bounds;

			public Canvas(Rectangle bounds)
			{
				this.bounds = bounds;
				Width = bounds.Width * MapPxPerCell;
				Height = bounds.Height * MapPxPerCell;
				Data = new byte[Width * Height * 4];
			}

			public int Width { get; }

			public int Height { get; }

			public byte[] Data { get; }

			/// <summary>Sets pixel (px, py) of a cell's block. The pixel is skipped when the cell is off the bounds.</summary>
			public void Set(CPos cell, int px, int py, uint argb)
			{
				if (!bounds.Contains(cell.X, cell.Y))
					return;

				SetPixel((cell.X - bounds.Left) * MapPxPerCell + px, (cell.Y - bounds.Top) * MapPxPerCell + py, argb);
			}

			void SetPixel(int x, int y, uint argb)
			{
				if (x < 0 || y < 0 || x >= Width || y >= Height)
					return;

				var index = (y * Width + x) * 4;
				Data[index] = (byte)(argb >> 16);
				Data[index + 1] = (byte)(argb >> 8);
				Data[index + 2] = (byte)argb;
				Data[index + 3] = (byte)(argb >> 24);
			}

			/// <summary>Fills a cell's whole block: a blip.</summary>
			public void FillCell(CPos cell, uint argb)
			{
				for (var py = 0; py < MapPxPerCell; py++)
					for (var px = 0; px < MapPxPerCell; px++)
						Set(cell, px, py, argb);
			}

			/// <summary>The centre pixel column of a cell, across the whole image: the vertical line of a crosshair.</summary>
			int CenterX(CPos cell)
			{
				return (cell.X - bounds.Left) * MapPxPerCell + MapPxPerCell / 2;
			}

			/// <summary>The centre pixel row of a cell, across the whole image: the horizontal line of a crosshair.</summary>
			int CenterY(CPos cell)
			{
				return (cell.Y - bounds.Top) * MapPxPerCell + MapPxPerCell / 2;
			}

			/// <summary>A green crosshair through a cell: its centre row and column, spanning the whole map.</summary>
			public void Crosshair(CPos cell, uint argb)
			{
				var row = CenterY(cell);
				var column = CenterX(cell);
				for (var x = 0; x < Width; x++)
					SetPixel(x, row, argb);

				for (var y = 0; y < Height; y++)
					SetPixel(column, y, argb);
			}

			/// <summary>A line from one cell's centre to another's, drawn with Bresenham's algorithm.</summary>
			public void Line(CPos from, CPos to, uint argb)
			{
				int x0 = CenterX(from), y0 = CenterY(from), x1 = CenterX(to), y1 = CenterY(to);
				var dx = Math.Abs(x1 - x0);
				var dy = -Math.Abs(y1 - y0);
				var sx = x0 < x1 ? 1 : -1;
				var sy = y0 < y1 ? 1 : -1;
				var error = dx + dy;
				while (true)
				{
					SetPixel(x0, y0, argb);
					if (x0 == x1 && y0 == y1)
						break;

					var doubled = 2 * error;
					if (doubled >= dy)
					{
						error += dy;
						x0 += sx;
					}

					if (doubled <= dx)
					{
						error += dx;
						y0 += sy;
					}
				}
			}

			/// <summary>A small X over a cell: its two diagonals.</summary>
			public void Cross(CPos cell, uint argb)
			{
				for (var i = 0; i < MapPxPerCell; i++)
				{
					Set(cell, i, i, argb);
					Set(cell, MapPxPerCell - 1 - i, i, argb);
				}
			}
		}
	}
}
