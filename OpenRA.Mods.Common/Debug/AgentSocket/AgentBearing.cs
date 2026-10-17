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

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// The 8-way bearing from a friendly reference toward a target (P2.1), on map axes: +X is East and +Y is South, so North is -Y.
	/// Pure: it takes no world, trait, or actor, so it is tested headlessly.
	/// </summary>
	/// <remarks>
	/// Sector boundaries. Each of the eight points owns a 45 degree sector centred on it. A vector is on an axis point (N, E, S or W)
	/// when its minor component is under tan(22.5°) of its major one, which is 0.4142. That ratio is compared in integers as
	/// minor * 10000 &lt; major * 4142, so a vector at exactly 4142/10000 is diagonal, and the boundary has no rounding. Every other
	/// vector is diagonal (NE, SE, SW or NW), by the signs of its two components. A zero vector has no bearing and gives null.
	/// </remarks>
	public static class AgentBearing
	{
		// tan(22.5°) to four places, as a numerator over Denominator.
		const long TanEighthTurnNumerator = 4142;
		const long Denominator = 10000;

		/// <summary>The bearing from one world position toward another, or null when they are the same position.</summary>
		public static string Of(WPos from, WPos to)
		{
			return Vector(to.X - (long)from.X, to.Y - (long)from.Y);
		}

		/// <summary>The bearing of a displacement (dx, dy), with +X East and +Y South, or null for the zero vector.</summary>
		public static string Vector(long dx, long dy)
		{
			if (dx == 0 && dy == 0)
				return null;

			var absX = Math.Abs(dx);
			var absY = Math.Abs(dy);
			var east = dx > 0;
			var south = dy > 0;

			if (absY * Denominator < absX * TanEighthTurnNumerator)
				return east ? "E" : "W";

			if (absX * Denominator < absY * TanEighthTurnNumerator)
				return south ? "S" : "N";

			return (south ? "S" : "N") + (east ? "E" : "W");
		}
	}
}
