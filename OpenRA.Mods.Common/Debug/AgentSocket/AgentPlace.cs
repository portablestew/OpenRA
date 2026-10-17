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
using System.Linq;
using System.Text.Json;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.AgentSocket
{
	// Schema v1, additive. Wire keys are snake_case (AgentSocketCommands.Json).

	/// <summary>
	/// The place command's success. RequestedCell is the cell the request gave, or null when it was omitted. SearchFrom is
	/// the cell the search started from: the requested cell, or the producer's location when none was given. Cell is the
	/// top-left cell the PlaceBuilding order was issued for.
	/// </summary>
	public sealed record PlaceV1(bool Ok, string Cmd, string Item, int[] RequestedCell, int[] SearchFrom, int[] Cell, int Tick, int MinEffectTick);

	/// <summary>A parsed place request. Cell is null when the request omitted it.</summary>
	public sealed record PlaceRequest(string Item, CPos? Cell);

	/// <summary>
	/// Places a finished item from the local player's production queue, as a real PlaceBuilding order: the same order the
	/// placement cursor issues, so it takes the same path as a click. PlaceBuilding drops an invalid cell silently, so the
	/// cell is checked here first, with the pair PlaceBuildingOrderGenerator uses. If the search start is not valid, the nearest
	/// valid cell within MaxRadius is chosen, nearest ring first and the closest cell within a ring.
	/// </summary>
	public static class AgentPlace
	{
		/// <summary>How far, in cells, the search looks from its start for a valid top-left cell.</summary>
		public const int MaxRadius = 10;

		/// <summary>Parses the request on the socket thread. Returns an error message, or null when the request is valid.</summary>
		public static string Parse(JsonElement root, out PlaceRequest request)
		{
			request = null;
			if (!root.TryGetProperty("item", out var itemElement) || itemElement.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(itemElement.GetString()))
				return "item must be a non-empty string";

			CPos? cell = null;
			if (AgentOrder.IsPresent(root, "cell"))
			{
				if (!AgentOrder.TryGetInts(root.GetProperty("cell"), 2, out var c))
					return "cell must be [x, y]";

				cell = new CPos(c[0], c[1]);
			}

			request = new PlaceRequest(itemElement.GetString(), cell);
			return null;
		}

		/// <summary>Finds the finished item, picks the cell, and issues PlaceBuilding. Must run on the game thread.</summary>
		public static PlaceV1 Start(World world, PlaceRequest request, AgentSelection selection)
		{
			var item = request.Item;
			if (!world.Map.Rules.Actors.TryGetValue(item, out var info))
				throw new InvalidOperationException($"unknown item '{item}'");

			var building = info.TraitInfoOrDefault<BuildingInfo>();
			if (building == null)
				throw new InvalidOperationException($"'{item}' is not a building");

			var queues = AgentProduction.List(world).Queues
				.Select(q => (Info: q, Trait: AgentBuild.QueueTrait(world, q)))
				.Where(x => x.Trait.CanBuild(info))
				.ToArray();

			var ready = queues.Cast<(ProductionQueueV1 Info, ProductionQueue Trait)?>()
				.FirstOrDefault(x => x.Value.Trait.AllQueued().Any(i => i.Done && i.Item == item));

			if (ready == null)
			{
				if (queues.Any(x => x.Trait.AllQueued().Any(i => i.Item == item)))
					throw new InvalidOperationException($"'{item}' is not done yet: it is still in production");

				throw new InvalidOperationException($"no finished '{item}' in a production queue: build it first");
			}

			var queue = ready.Value.Info;
			var producer = world.Actors.First(a => a.ActorID == queue.Producers[0]);
			var searchFrom = request.Cell ?? producer.Location;

			var cell = FindValidCell(world, info, building, world.LocalPlayer, searchFrom)
				?? throw new InvalidOperationException($"no valid cell for '{item}' within {MaxRadius} cells of {searchFrom.X},{searchFrom.Y}");

			// ExtraData is the actor that holds the queue, as PlaceBuilding resolves it, not the producer that makes the item.
			var actor = AgentOrder.Accept(world, queue.Actor, "PlaceBuilding");
			world.IssueOrder(new Order("PlaceBuilding", actor, Target.FromCell(world, cell), false)
			{
				TargetString = item,
				ExtraData = queue.Actor,
			});
			selection.Ordered(world, actor);

			var requested = request.Cell is { } given ? new[] { given.X, given.Y } : null;
			return new PlaceV1(true, "place", item, requested, [searchFrom.X, searchFrom.Y], [cell.X, cell.Y], world.WorldTick, world.WorldTick + 1);
		}

		/// <summary>Searches outward from the start, ring by ring, and returns the closest valid cell in the first ring that has one.</summary>
		static CPos? FindValidCell(World world, ActorInfo info, BuildingInfo building, Player owner, CPos start)
		{
			for (var radius = 0; radius <= MaxRadius; radius++)
			{
				CPos? best = null;
				var bestDistance = int.MaxValue;
				for (var dy = -radius; dy <= radius; dy++)
					for (var dx = -radius; dx <= radius; dx++)
					{
						if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius)
							continue;

						var distance = dx * dx + dy * dy;
						var cell = new CPos(start.X + dx, start.Y + dy);
						if (distance < bestDistance && IsValid(world, info, building, owner, cell))
						{
							best = cell;
							bestDistance = distance;
						}
					}

				if (best != null)
					return best;
			}

			return null;
		}

		/// <summary>The same check PlaceBuildingOrderGenerator makes before it places a building.</summary>
		static bool IsValid(World world, ActorInfo info, BuildingInfo building, Player owner, CPos cell)
		{
			return world.CanPlaceBuilding(cell, info, building, null) && building.IsCloseEnoughToBase(world, owner, info, cell);
		}
	}
}
