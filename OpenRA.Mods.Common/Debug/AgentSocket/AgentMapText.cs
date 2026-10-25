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
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// The text map reply (map_state with format "text"). Origin is the top-left map cell and Size is the grid in cells. Rows has
	/// one string per map row, top first, one character per cell. Legend maps each glyph to its meaning; a terrain glyph is upper
	/// case for a visible cell and lower case for an explored cell out of sight.
	/// </summary>
	public sealed record MapTextV1(bool Ok, string Cmd, string Format, int[] Origin, int[] Size,
		Dictionary<string, string> Legend, string[] Rows, int Tick);

	/// <summary>
	/// The text map (P2.11): the same discovered-map rule as the image, as a grid of glyphs returned inline. Unexplored is a space.
	/// Each explored cell is its terrain, or $ when it has a resource. Then the actors the observer knows, painted over their
	/// footprint: own, ally, enemy and neutral, with structures and units told apart for the owner and enemies. Reads only, so it
	/// never touches the image's cache.
	/// </summary>
	public static class AgentMapText
	{
		/// <summary>The glyphs the fixed legend uses. No terrain type takes one of them, so a letter in the grid always means one thing.</summary>
		const string Reserved = " $BUAEXN";

		/// <summary>The glyphs a terrain type takes when its first letter is reserved or taken by another type.</summary>
		const string Spare = "0123456789";

		/// <summary>Builds the text map for the observer. A missing observer sees everything explored, as the image does.</summary>
		public static MapTextV1 Build(World world, Player observer)
		{
			var bounds = world.Map.Bounds;
			var width = bounds.Width;
			var height = bounds.Height;

			var terrain = new string[width * height];
			var resource = new string[width * height];
			var types = new SortedSet<string>(StringComparer.Ordinal);
			for (var y = bounds.Top; y < bounds.Bottom; y++)
				for (var x = bounds.Left; x < bounds.Right; x++)
				{
					var (type, contents, _) = AgentLook.CellClass(world, new CPos(x, y));
					var index = (y - bounds.Top) * width + (x - bounds.Left);
					terrain[index] = type;
					resource[index] = contents;
					types.Add(type);
				}

			var legend = new Dictionary<string, string>
			{
				[" "] = "unexplored",
				["$"] = "resource",
				["B"] = "your structure",
				["U"] = "your unit",
				["A"] = "ally",
				["E"] = "enemy structure",
				["X"] = "enemy unit",
				["N"] = "neutral",
			};

			var glyphs = TerrainGlyphs(types, legend);
			var grid = new char[height][];
			for (var row = 0; row < height; row++)
			{
				grid[row] = new char[width];
				Array.Fill(grid[row], ' ');
			}

			for (var y = bounds.Top; y < bounds.Bottom; y++)
				for (var x = bounds.Left; x < bounds.Right; x++)
				{
					var uv = new CPos(x, y).ToMPos(world.Map);
					var index = (y - bounds.Top) * width + (x - bounds.Left);
					if (observer != null && !observer.Shroud.IsExplored(uv))
						continue;

					var visible = observer == null || observer.Shroud.IsVisible(uv);
					var glyph = glyphs[terrain[index]];
					grid[y - bounds.Top][x - bounds.Left] = resource[index] != null ? '$' : visible ? glyph : char.ToLowerInvariant(glyph);
				}

			if (observer != null)
				foreach (var actor in world.Actors)
				{
					if (actor.OccupiesSpace == null || actor.IsDead || AgentStateDump.IsScenery(actor))
						continue;

					if (actor.Owner != observer && !AgentFog.Known(actor, observer))
						continue;

					var glyph = ActorGlyph(actor, observer);
					foreach (var occupied in actor.OccupiesSpace.OccupiedCells())
					{
						var cell = occupied.Cell;
						if (bounds.Contains(cell.X, cell.Y))
							grid[cell.Y - bounds.Top][cell.X - bounds.Left] = glyph;
					}
				}

			var rows = grid.Select(row => new string(row)).ToArray();
			return new MapTextV1(true, "map_state", "text", [bounds.Left, bounds.Top], [width, height], legend, rows, world.WorldTick);
		}

		/// <summary>
		/// Gives each terrain type a glyph: its first letter in upper case, so Clear is C. A letter the legend or another type
		/// already holds takes the next digit instead. A type with no glyph left takes ?, which the legend does not name.
		/// </summary>
		static Dictionary<string, char> TerrainGlyphs(IEnumerable<string> types, Dictionary<string, string> legend)
		{
			var taken = new HashSet<char>(Reserved);
			var spare = 0;
			var glyphs = new Dictionary<string, char>();
			foreach (var type in types)
			{
				var letter = string.IsNullOrEmpty(type) ? '?' : char.ToUpperInvariant(type[0]);
				char glyph;
				if (char.IsLetter(letter) && taken.Add(letter))
					glyph = letter;
				else if (spare < Spare.Length)
					glyph = Spare[spare++];
				else
					glyph = '?';

				glyphs[type] = glyph;
				if (glyph != '?')
					legend[glyph.ToString()] = type;
			}

			return glyphs;
		}

		/// <summary>An actor the observer knows, as its glyph: your structure or unit, an ally, an enemy's structure or unit, or neutral.</summary>
		static char ActorGlyph(Actor actor, Player observer)
		{
			var structure = actor.Info.HasTraitInfo<BuildingInfo>();
			if (actor.Owner == observer)
				return structure ? 'B' : 'U';

			switch (observer.RelationshipWith(actor.Owner))
			{
				case PlayerRelationship.Ally:
					return 'A';
				case PlayerRelationship.Enemy:
					return structure ? 'E' : 'X';
				default:
					return 'N';
			}
		}
	}
}
