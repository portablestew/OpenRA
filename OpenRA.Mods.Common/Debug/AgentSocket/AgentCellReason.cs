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

using System.Linq;
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// The cell-reason mirror (P1.8, reused by P1.9). It says why a building footprint cannot go on a cell. It never decides
	/// validity: CanPlaceBuilding does, and this is consulted only after it refused. The reason is the first failing tile check,
	/// in the order IsCellBuildable makes them: off the map or unusable terrain, an occupied tile, then a ramp.
	/// </summary>
	public static class AgentCellReason
	{
		public const string Occupied = "occupied";
		public const string Terrain = "terrain";
		public const string Ramp = "ramp";

		/// <summary>The first reason the footprint of bi at cell fails, or null when CanPlaceBuilding allows it.</summary>
		public static string Of(World world, ActorInfo ai, BuildingInfo bi, CPos cell, Actor toIgnore)
		{
			if (world.CanPlaceBuilding(cell, ai, bi, toIgnore))
				return null;

			foreach (var tile in bi.Tiles(cell))
			{
				if (!world.Map.Contains(tile))
					return Terrain;

				if (world.IsCellBuildable(tile, ai, bi, toIgnore))
					continue;

				if (IsOccupied(world, tile, toIgnore))
					return Occupied;

				return world.Map.Ramp[tile] != 0 ? Ramp : Terrain;
			}

			// Unreachable: CanPlaceBuilding is the conjunction of the tile checks above, so a refused footprint has a failing tile.
			return Terrain;
		}

		static bool IsOccupied(World world, CPos tile, Actor toIgnore)
		{
			return world.ActorMap.GetActorsAt(tile).Any(a => a != toIgnore) || world.WorldActor.Trait<BuildingInfluence>().AnyBuildingAt(tile);
		}
	}
}
