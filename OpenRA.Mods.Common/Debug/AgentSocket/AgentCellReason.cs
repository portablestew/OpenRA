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
	/// The cell-reason mirror (P1.8, completed by P1.9). It says why a building footprint cannot go on a cell, as the first failing
	/// check, or null when the cell is valid. It never decides the engine's answer: CanPlaceBuilding does, and the engine's failing
	/// tile checks are consulted only after it refused. Shroud and distance are not engine placement checks, so they are reported
	/// only for a footprint the engine accepts: the first unexplored tile, then the distance to a buildable area. A cell the engine
	/// refuses therefore never reports shroud or distance, and a cell that is explored and close enough is null exactly when
	/// CanPlaceBuilding is true.
	/// </summary>
	public static class AgentCellReason
	{
		public const string Occupied = "occupied";
		public const string Terrain = "terrain";
		public const string Shroud = "shroud";
		public const string Resource = "resource";
		public const string Ramp = "ramp";
		public const string Distance = "distance";

		/// <summary>The first reason the footprint of bi at cell fails for owner, or null when the cell is valid.</summary>
		public static string Of(World world, ActorInfo ai, BuildingInfo bi, CPos cell, Actor toIgnore, Player owner)
		{
			if (world.CanPlaceBuilding(cell, ai, bi, toIgnore))
			{
				foreach (var tile in bi.Tiles(cell))
					if (!owner.Shroud.IsExplored(tile))
						return Shroud;

				return bi.IsCloseEnoughToBase(world, owner, ai, cell) ? null : Distance;
			}

			// The engine's tile checks, in the order IsCellBuildable makes them: off the map or unusable terrain, an occupied tile,
			// then a ramp. A tile on a resource terrain the building does not accept is a resource reason, not a terrain one.
			foreach (var tile in bi.Tiles(cell))
			{
				if (!world.Map.Contains(tile))
					return Terrain;

				if (world.IsCellBuildable(tile, ai, bi, toIgnore))
					continue;

				if (IsOccupied(world, tile, toIgnore))
					return Occupied;

				if (world.Map.Ramp[tile] != 0)
					return Ramp;

				return IsResourceTerrain(world, tile) ? Resource : Terrain;
			}

			// Unreachable: CanPlaceBuilding is the conjunction of the tile checks above, so a refused footprint has a failing tile.
			return Terrain;
		}

		static bool IsOccupied(World world, CPos tile, Actor toIgnore)
		{
			return world.ActorMap.GetActorsAt(tile).Any(a => a != toIgnore) || world.WorldActor.Trait<BuildingInfluence>().AnyBuildingAt(tile);
		}

		static bool IsResourceTerrain(World world, CPos tile)
		{
			var terrain = world.Map.GetTerrainInfo(tile).Type;
			var resourceLayer = world.WorldActor.Info.TraitInfoOrDefault<ResourceLayerInfo>();

			return resourceLayer != null && resourceLayer.ResourceTypes.Values.Any(r => r.TerrainType == terrain);
		}
	}
}
