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
	// Schema v1. Wire keys are snake_case (AgentSocketCommands.Json).
	// MinEffectTick is a lower bound on the tick the order applies on. Network frame delay can make the real tick later.
	public sealed record OrderV1(bool Ok, string Cmd, int Tick, int MinEffectTick);

	/// <summary>A parsed order. At most one of Cell, Pos and TargetActor is set. None of them means an untargeted order, such as Stop.</summary>
	public sealed record OrderRequest(string Order, uint Actor, CPos? Cell, WPos? Pos, uint? TargetActor, bool Queued);

	/// <summary>
	/// Issues an order for a local-player actor through World.IssueOrder, so it takes the same network and validation path
	/// as a click (D11). The ack reports min_effect_tick = tick + 1, a lower bound: live, the order applied 4 ticks later
	/// than the ack (ack 506, applied 510) because of the network frame delay.
	/// </summary>
	public static class AgentOrder
	{
		/// <summary>Parses the request on the socket thread. Returns an error message, or null when the request is valid.</summary>
		public static string Parse(JsonElement root, out OrderRequest request)
		{
			request = null;
			if (!root.TryGetProperty("order", out var orderElement) || orderElement.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(orderElement.GetString()))
				return "order must be a non-empty string";

			if (!root.TryGetProperty("actor", out var actorElement) || !actorElement.TryGetUInt32(out var actor))
				return "actor must be an unsigned integer";

			CPos? cell = null;
			WPos? pos = null;
			uint? targetActor = null;
			var targets = 0;

			if (IsPresent(root, "cell"))
			{
				if (!TryGetInts(root.GetProperty("cell"), 2, out var c))
					return "cell must be [x, y]";

				cell = new CPos(c[0], c[1]);
				targets++;
			}

			if (IsPresent(root, "pos"))
			{
				if (!TryGetInts(root.GetProperty("pos"), 3, out var p))
					return "pos must be [x, y, z] in world units";

				pos = new WPos(p[0], p[1], p[2]);
				targets++;
			}

			if (IsPresent(root, "target_actor"))
			{
				if (!root.GetProperty("target_actor").TryGetUInt32(out var target))
					return "target_actor must be an unsigned integer";

				targetActor = target;
				targets++;
			}

			if (targets > 1)
				return "give at most one of cell, pos and target_actor";

			var queued = false;
			if (IsPresent(root, "queued"))
			{
				var queuedElement = root.GetProperty("queued");
				if (queuedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
					return "queued must be a boolean";

				queued = queuedElement.ValueKind == JsonValueKind.True;
			}

			request = new OrderRequest(orderElement.GetString(), actor, cell, pos, targetActor, queued);
			return null;
		}

		/// <summary>Validates the request and issues it. Must run on the game thread: it reads live world state.</summary>
		public static OrderV1 Issue(World world, OrderRequest request)
		{
			var actor = Live(world, request.Actor);
			if (actor.Owner != world.LocalPlayer)
				throw new ArgumentException($"Actor {request.Actor} is not owned by the local player");

			// Mirrors the check ValidateOrder makes, so a refused order is reported here instead of being dropped silently.
			if (!actor.AcceptsOrder(request.Order))
				throw new ArgumentException($"Actor {request.Actor} does not accept {request.Order}");

			Target target;
			if (request.Cell is { } cell)
			{
				if (!world.Map.Contains(cell))
					throw new ArgumentException($"cell {cell.X},{cell.Y} is outside the map");

				target = Target.FromCell(world, cell);
			}
			else if (request.Pos is { } pos)
				target = Target.FromPos(pos);
			else if (request.TargetActor is { } targetId)
				target = Target.FromActor(Live(world, targetId));
			else
				target = Target.Invalid;

			world.IssueOrder(new Order(request.Order, actor, target, request.Queued));

			return new OrderV1(true, "order", world.WorldTick, world.WorldTick + 1);
		}

		/// <summary>Finds an actor by ID. An unknown ID and a dead actor are reported as different errors.</summary>
		static Actor Live(World world, uint id)
		{
			var actor = world.Actors.FirstOrDefault(a => a.ActorID == id);
			if (actor == null)
				throw new ArgumentException($"Unknown actor {id}");

			if (actor.IsDead)
				throw new ArgumentException($"Actor {id} is dead");

			return actor;
		}

		static bool IsPresent(JsonElement root, string name)
		{
			return root.TryGetProperty(name, out var element) && element.ValueKind != JsonValueKind.Null;
		}

		static bool TryGetInts(JsonElement element, int count, out int[] values)
		{
			values = null;
			if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != count)
				return false;

			values = new int[count];
			for (var i = 0; i < count; i++)
				if (!element[i].TryGetInt32(out values[i]))
					return false;

			return true;
		}
	}
}
