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
using NUnit.Framework;
using OpenRA.Mods.Common.AgentSocket;

namespace OpenRA.Test
{
	/// <summary>
	/// Hand-checked path metrics for the path event (plan §2). Each case gives the cells source to target; the helper
	/// passes them to Compute the way EvalPath returns them (target first, source excluded).
	/// </summary>
	[TestFixture]
	sealed class AgentPathMetricsTest
	{
		static PathCell[] Line(PathCell from, PathCell to)
		{
			var n = Math.Max(Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y));
			var sx = Math.Sign(to.X - from.X);
			var sy = Math.Sign(to.Y - from.Y);
			return Enumerable.Range(0, n + 1).Select(i => new PathCell(from.X + sx * i, from.Y + sy * i)).ToArray();
		}

		static PathMetrics Measure(PathCell[] sourceToTarget, Func<PathCell, int> cost = null)
		{
			var path = sourceToTarget.Skip(1).Reverse().ToList();
			return AgentPathMetrics.Compute(sourceToTarget[0], sourceToTarget[^1], path, cost ?? (_ => 0));
		}

		// An L-shaped route: up 5, across 10, down 5. Source (0,0), destination (10,0), crow distance 10.
		static PathCell[] LShape()
		{
			return Line(new PathCell(0, 0), new PathCell(0, 5))
				.Concat(Line(new PathCell(0, 5), new PathCell(10, 5)).Skip(1))
				.Concat(Line(new PathCell(10, 5), new PathCell(10, 0)).Skip(1))
				.ToArray();
		}

		[Test]
		public void AgentPathMetricsStraightOrthogonalRatioIsOne()
		{
			var m = Measure(Line(new PathCell(0, 0), new PathCell(6, 0)));

			Assert.That(m.PathLenCells, Is.EqualTo(6));
			Assert.That(m.PathLen, Is.EqualTo(6.0));
			Assert.That(m.CrowCells, Is.EqualTo(6.0));
			Assert.That(m.DetourRatio, Is.EqualTo(1.0));
			Assert.That(m.MaxMinCells, Is.EqualTo(3.0));
			Assert.That(m.StraightHalfCells, Is.EqualTo(3.0));
		}

		[Test]
		public void AgentPathMetricsStraightDiagonalRatioWithinBound()
		{
			var m = Measure(Line(new PathCell(0, 0), new PathCell(3, 3)));

			// Three diagonal steps of 1449/1024 cells each: 4.25 cells against a crow line of 4.24.
			Assert.That(m.PathLenCells, Is.EqualTo(3));
			Assert.That(m.PathLen, Is.EqualTo(4.25));
			Assert.That(m.CrowCells, Is.EqualTo(4.24));
			Assert.That(m.DetourRatio, Is.GreaterThanOrEqualTo(1.0));
			Assert.That(m.DetourRatio, Is.LessThanOrEqualTo(1.001));
		}

		[Test]
		public void AgentPathMetricsDetourAroundWallIsAboveOne()
		{
			var m = Measure(LShape());

			// 20 orthogonal steps against a crow distance of 10. The peak is at (5,5): min(√50, √50) ≈ 7.07.
			Assert.That(m.PathLenCells, Is.EqualTo(20));
			Assert.That(m.PathLen, Is.EqualTo(20.0));
			Assert.That(m.CrowCells, Is.EqualTo(10.0));
			Assert.That(m.DetourRatio, Is.EqualTo(2.0));
			Assert.That(m.MaxMinCells, Is.EqualTo(7.07));
			Assert.That(m.StraightHalfCells, Is.EqualTo(5.0));
			Assert.That(m.MaxMinCells, Is.GreaterThan(m.StraightHalfCells));
		}

		[Test]
		public void AgentPathMetricsBlockedEmptyPathIsNoPath()
		{
			var m = AgentPathMetrics.Compute(new PathCell(0, 0), new PathCell(5, 0), [], _ => 0);

			Assert.That(m, Is.Null);
		}

		[Test]
		public void AgentPathMetricsZeroLengthPath()
		{
			var m = AgentPathMetrics.Compute(new PathCell(2, 2), new PathCell(2, 2), [], _ => 7);

			Assert.That(m.PathLenCells, Is.EqualTo(0));
			Assert.That(m.PathLen, Is.EqualTo(0.0));
			Assert.That(m.CrowCells, Is.EqualTo(0.0));
			Assert.That(m.DetourRatio, Is.EqualTo(1.0));
			Assert.That(m.MaxMinCells, Is.EqualTo(0.0));
			Assert.That(m.StraightHalfCells, Is.EqualTo(0.0));
			Assert.That(m.TerrainCost, Is.EqualTo(0));
		}

		[Test]
		public void AgentPathMetricsEtaUsesPathLengthAndDeclaredSpeed()
		{
			var m = Measure(Line(new PathCell(0, 0), new PathCell(6, 0)));

			// 6 cells x 1024 world units, at the given world units per tick.
			Assert.That(AgentPathMetrics.EtaTicks(m, 1024), Is.EqualTo(6));
			Assert.That(AgentPathMetrics.EtaTicks(m, 512), Is.EqualTo(12));
			Assert.That(AgentPathMetrics.EtaTicks(m, 100), Is.EqualTo(62));
		}

		[Test]
		public void AgentPathMetricsEtaIsNullWithoutAPathOrSpeed()
		{
			var m = Measure(Line(new PathCell(0, 0), new PathCell(6, 0)));

			Assert.That(AgentPathMetrics.EtaTicks(null, 1024), Is.Null);
			Assert.That(AgentPathMetrics.EtaTicks(m, 0), Is.Null);
			Assert.That(AgentPathMetrics.EtaTicks(m, -1), Is.Null);
		}

		[Test]
		public void AgentPathMetricsEtaOfZeroLengthPathIsZero()
		{
			var m = AgentPathMetrics.Compute(new PathCell(2, 2), new PathCell(2, 2), [], _ => 0);

			Assert.That(AgentPathMetrics.EtaTicks(m, 1024), Is.EqualTo(0));
		}

		[Test]
		public void AgentPathMetricsTerrainCostSkipsSourceCell()
		{
			// Cells 1 to 6 are charged, the source is not. Cell (3,0) costs 5 and the rest cost 1: 5 × 1 + 5 = 10.
			var m = Measure(Line(new PathCell(0, 0), new PathCell(6, 0)), c => c == new PathCell(3, 0) ? 5 : 1);

			Assert.That(m.TerrainCost, Is.EqualTo(10));
		}

		[Test]
		public void AgentPathMetricsRejectsNonAdjacentStep()
		{
			List<PathCell> path = [new(2, 0)];

			Assert.Throws<ArgumentException>(() =>
				AgentPathMetrics.Compute(new PathCell(0, 0), new PathCell(2, 0), path, _ => 0));
		}

		[Test]
		public void AgentPathMetricsRejectsPathNotEndingAtDestination()
		{
			List<PathCell> path = [new(6, 0), new(5, 0)];

			Assert.Throws<ArgumentException>(() =>
				AgentPathMetrics.Compute(new PathCell(4, 0), new PathCell(9, 9), path, _ => 0));
		}
	}
}
