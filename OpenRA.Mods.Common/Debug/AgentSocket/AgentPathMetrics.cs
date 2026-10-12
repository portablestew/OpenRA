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

namespace OpenRA.Mods.Common.AgentSocket
{
	public readonly record struct PathCell(int X, int Y);

	// Schema v1 path metrics (plan D5, design "Path event metrics"). Keys are snake_case when serialized (AgentSocketCommands.Json).
	public sealed record PathMetrics(int PathLenCells, double PathLen, double CrowCells, double DetourRatio,
		double MaxMinCells, double StraightHalfCells, int TerrainCost);

	/// <summary>
	/// Pure path metrics over plain cell coordinates. Takes no world, trait, or CPos input, so it can be tested headlessly.
	/// The caller supplies the terrain cost per cell. Fixed point throughout: 1024 = one cell, and a diagonal step is 1449/1024.
	/// </summary>
	public static class AgentPathMetrics
	{
		const int Scale = 1024;
		const int OrthogonalStep = 1024;

		// 1449/1024 > √2, so a straight diagonal never measures shorter than its crow line.
		const int DiagonalStep = 1449;

		/// <summary>
		/// <paramref name="pathTargetToSource"/> is the list as EvalPath returns it: target first, source excluded.
		/// An empty list means the source already is the destination when <paramref name="src"/> equals <paramref name="dst"/>
		/// (zero-length, metrics all zero); otherwise it means no path and the result is null.
		/// </summary>
		public static PathMetrics Compute(PathCell src, PathCell dst, IReadOnlyList<PathCell> pathTargetToSource, Func<PathCell, int> cellCost)
		{
			if (pathTargetToSource.Count == 0)
				return src == dst ? new PathMetrics(0, 0, 0, 1.0, 0, 0, 0) : null;

			if (pathTargetToSource[0] != dst)
				throw new ArgumentException("The first path cell must be the destination.", nameof(pathTargetToSource));

			// cells runs source to target, so cells[0] is src and the last cell is dst.
			var cells = new PathCell[pathTargetToSource.Count + 1];
			cells[0] = src;
			for (var i = 0; i < pathTargetToSource.Count; i++)
				cells[i + 1] = pathTargetToSource[pathTargetToSource.Count - 1 - i];

			var pathLenFixed = 0L;
			var terrainCost = 0;
			for (var i = 1; i < cells.Length; i++)
			{
				var dx = Math.Abs(cells[i].X - cells[i - 1].X);
				var dy = Math.Abs(cells[i].Y - cells[i - 1].Y);
				if (dx > 1 || dy > 1 || (dx == 0 && dy == 0))
					throw new ArgumentException("Path steps must move to an adjacent cell.", nameof(pathTargetToSource));

				pathLenFixed += dx == 1 && dy == 1 ? DiagonalStep : OrthogonalStep;
				terrainCost += cellCost(cells[i]);
			}

			var crowFixed = Distance(src, dst);

			// Path length is at least the crow distance, so the ratio is >= 1 by construction. max(crow, 1 cell) guards the divide.
			var detour = Math.Max(1.0, pathLenFixed / (double)Math.Max(crowFixed, Scale));

			var maxMinFixed = 0L;
			foreach (var c in cells)
				maxMinFixed = Math.Max(maxMinFixed, Math.Min(Distance(c, src), Distance(dst, c)));

			return new PathMetrics(
				pathTargetToSource.Count,
				Round(pathLenFixed / (double)Scale, 2),
				Round(crowFixed / (double)Scale, 2),
				Round(detour, 3),
				Round(maxMinFixed / (double)Scale, 2),
				Round(crowFixed / (2.0 * Scale), 2),
				terrainCost);
		}

		// Euclidean distance in fixed point: floor(1024 × √(dx² + dy²)).
		static long Distance(PathCell a, PathCell b)
		{
			var dx = (long)(a.X - b.X);
			var dy = (long)(a.Y - b.Y);
			return Exts.ISqrt((long)Scale * Scale * (dx * dx + dy * dy));
		}

		static double Round(double value, int digits)
		{
			return Math.Round(value, digits, MidpointRounding.AwayFromZero);
		}
	}
}
