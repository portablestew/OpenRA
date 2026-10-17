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

using NUnit.Framework;
using OpenRA.Mods.Common.AgentSocket;

namespace OpenRA.Test
{
	/// <summary>
	/// The bearing helper (P2.1). Each of the eight points, the sector boundaries on both sides of each axis point and each
	/// diagonal, and the zero vector. +X is East and +Y is South, so North is -Y.
	/// </summary>
	[TestFixture]
	sealed class AgentBearingTest
	{
		[TestCase(0, 0, 0, -1000, "N", TestName = "A target straight north of the reference is N")]
		[TestCase(0, 0, 1000, -1000, "NE", TestName = "A target north-east is NE")]
		[TestCase(0, 0, 1000, 0, "E", TestName = "A target straight east is E")]
		[TestCase(0, 0, 1000, 1000, "SE", TestName = "A target south-east is SE")]
		[TestCase(0, 0, 0, 1000, "S", TestName = "A target straight south is S")]
		[TestCase(0, 0, -1000, 1000, "SW", TestName = "A target south-west is SW")]
		[TestCase(0, 0, -1000, 0, "W", TestName = "A target straight west is W")]
		[TestCase(0, 0, -1000, -1000, "NW", TestName = "A target north-west is NW")]
		public void EachOfTheEightPoints(int fromX, int fromY, int toX, int toY, string expected)
		{
			Assert.That(AgentBearing.Of(new WPos(fromX, fromY, 0), new WPos(toX, toY, 0)), Is.EqualTo(expected));
		}

		[TestCase(10000, -4141, "E", TestName = "Just inside the east sector, north of the axis, is E")]
		[TestCase(10000, -4142, "NE", TestName = "At the 4142/10000 ratio on the north side, the vector is diagonal: NE")]
		[TestCase(10000, 4141, "E", TestName = "Just inside the east sector, south of the axis, is E")]
		[TestCase(10000, 4142, "SE", TestName = "At the 4142/10000 ratio on the south side, the vector is diagonal: SE")]
		[TestCase(4141, -10000, "N", TestName = "Just inside the north sector, east of the axis, is N")]
		[TestCase(4142, -10000, "NE", TestName = "At the 4142/10000 ratio on the east side, the vector is diagonal: NE")]
		[TestCase(-4141, 10000, "S", TestName = "Just inside the south sector, west of the axis, is S")]
		[TestCase(-4142, 10000, "SW", TestName = "At the 4142/10000 ratio on the west side, the vector is diagonal: SW")]
		public void SectorBoundariesAreExact(int dx, int dy, string expected)
		{
			Assert.That(AgentBearing.Vector(dx, dy), Is.EqualTo(expected));
		}

		[TestCase(TestName = "A pure diagonal is at the centre of its sector, and every sign combination gives its own point")]
		public void DiagonalsTakeTheirSignCombination()
		{
			Assert.That(AgentBearing.Vector(1, -1), Is.EqualTo("NE"));
			Assert.That(AgentBearing.Vector(1, 1), Is.EqualTo("SE"));
			Assert.That(AgentBearing.Vector(-1, 1), Is.EqualTo("SW"));
			Assert.That(AgentBearing.Vector(-1, -1), Is.EqualTo("NW"));
		}

		[TestCase(TestName = "The bearing is relative to the reference: one target gives different bearings from two references")]
		public void BearingIsRelativeToTheReference()
		{
			// From (5000, 5000), a target at (5000, 4000) is 1000 north. From the origin the same target is north-west.
			Assert.That(AgentBearing.Of(new WPos(5000, 5000, 0), new WPos(5000, 4000, 0)), Is.EqualTo("N"));
			Assert.That(AgentBearing.Of(new WPos(0, 0, 0), new WPos(5000, 4000, 0)), Is.EqualTo("SE"));
		}

		[TestCase(-1000000000, 1000000000, 1000000000, -1000000000, "NE",
			TestName = "Displacements of two billion units on each axis do not overflow")]
		public void LargeDisplacementsDoNotOverflow(int fromX, int fromY, int toX, int toY, string expected)
		{
			Assert.That(AgentBearing.Of(new WPos(fromX, fromY, 0), new WPos(toX, toY, 0)), Is.EqualTo(expected));
		}

		[TestCase(0, 0, 0, 0, TestName = "The zero vector from the origin has no bearing")]
		[TestCase(5, 5, 5, 5, TestName = "The same position as the reference has no bearing")]
		public void ZeroVectorHasNoBearing(int fromX, int fromY, int toX, int toY)
		{
			Assert.That(AgentBearing.Of(new WPos(fromX, fromY, 0), new WPos(toX, toY, 0)), Is.Null);
			Assert.That(AgentBearing.Vector(0, 0), Is.Null);
		}
	}
}
