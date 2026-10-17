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
using System.Text.Json.Serialization;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.AgentSocket
{
	// Schema v1. Wire keys are snake_case (AgentSocketCommands.Json).
	// MinEffectTick is a lower bound on the tick the order applies on. Network frame delay can make the real tick later.
	public sealed record OrderV1(bool Ok, string Cmd, int Tick, int MinEffectTick);

	/// <summary>
	/// A parsed order. At most one of Cell, Pos and TargetActor is set. None of them means an untargeted order, such as Stop.
	/// Item, ExtraData and ExtraLocation become the order's TargetString, ExtraData and ExtraLocation. Null means absent.
	/// </summary>
	public sealed record OrderRequest(string Order, uint Actor, CPos? Cell, WPos? Pos, uint? TargetActor, bool Queued,
		string Item, uint? ExtraData, CPos? ExtraLocation, int? SettleTicks, bool Await);

	/// <summary>
	/// The Move reply (P1.3). Valid and EffectTick are null, and Settled is false, when the game was paused and the reply did not
	/// wait. Path is the cells the Move planned, source cell first. Reason and Orders (P1.5) are set only when the validators
	/// refused the order, and are omitted from every other reply.
	/// </summary>
	public sealed record MoveV1(bool Ok, string Cmd, int Tick, int MinEffectTick, bool? Valid, int? EffectTick, bool Settled,
		int SettledTicks, int[][] Path,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string Reason = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[] Orders = null);

	/// <summary>
	/// What Issue returns on the game thread. Move is true for a Move order. Await is the wait for a Move, and null for any other
	/// order. Paused is true for a Move in a paused game, which never ticks, so Settle does not wait for it (Q1).
	/// </summary>
	public sealed record Issued(OrderV1 Ack, bool Move, bool Paused, AgentOrderAwait Await);

	/// <summary>The orders command: the order names an actor accepts. Schema v1, additive.</summary>
	public sealed record OrdersV1(bool Ok, string Cmd, uint Actor, string Type, string[] Orders);

	/// <summary>The refusal for an order name the actor does not accept. Available lists the names it does accept.</summary>
	public sealed record OrderRefusedV1(bool Ok, string Cmd, string Error, string[] Available);

	/// <summary>Thrown on the game thread when an actor does not accept the order name. Carries the names it does accept.</summary>
	public sealed class OrderRefusedException : Exception
	{
		public OrderRefusedException(uint actor, string order, string[] available)
			: base($"actor {actor} does not accept order '{order}'")
		{
			Available = available;
		}

		public string[] Available { get; }
	}

	/// <summary>
	/// Issues an order for a local-player actor through World.IssueOrder, so it takes the same network and validation path
	/// as a click (D11). The ack reports min_effect_tick = tick + 1, a lower bound: live, the order applied 4 ticks later
	/// than the ack (ack 506, applied 510) because of the network frame delay.
	/// </summary>
	public static class AgentOrder
	{
		/// <summary>The reason a Move reply gives when the validators refused the order (P1.5).</summary>
		public const string ValidatorRejected = "rejected by order validator";

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

			string item = null;
			if (IsPresent(root, "item"))
			{
				if (root.GetProperty("item").ValueKind != JsonValueKind.String)
					return "item must be a string";

				item = root.GetProperty("item").GetString();
			}

			uint? extraData = null;
			if (IsPresent(root, "extra_data"))
			{
				if (!TryGetUInt(root.GetProperty("extra_data"), out var data))
					return "extra_data must be an unsigned integer";

				extraData = data;
			}

			CPos? extraLocation = null;
			if (IsPresent(root, "extra_location"))
			{
				if (!TryGetInts(root.GetProperty("extra_location"), 2, out var l))
					return "extra_location must be [x, y]";

				extraLocation = new CPos(l[0], l[1]);
			}

			int? settleTicks = null;
			if (IsPresent(root, "settle_ticks"))
			{
				if (!TryGetUInt(root.GetProperty("settle_ticks"), out var settle) || settle > int.MaxValue)
					return "settle_ticks must be an unsigned integer";

				if (orderElement.GetString() != "Move")
					return "settle_ticks applies only to Move";

				settleTicks = (int)settle;
			}

			// await:false returns a job id at once instead of waiting for the Move to settle (P1.4).
			var awaits = true;
			if (IsPresent(root, "await"))
			{
				var awaitElement = root.GetProperty("await");
				if (awaitElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
					return "await must be a boolean";

				if (orderElement.GetString() != "Move" && awaitElement.ValueKind == JsonValueKind.False)
					return "await:false applies only to Move";

				awaits = awaitElement.ValueKind == JsonValueKind.True;
			}

			request = new OrderRequest(orderElement.GetString(), actor, cell, pos, targetActor, queued, item, extraData, extraLocation, settleTicks, awaits);
			return null;
		}

		/// <summary>Validates the request and issues it. Must run on the game thread: it reads live world state.</summary>
		public static Issued Issue(World world, OrderRequest request)
		{
			var order = ToOrder(world, request);
			var move = order.OrderString == "Move";
			var await = AgentOrderAwait.Register(order, move ? request.SettleTicks ?? AgentOrderAwait.DefaultSettleTicks : 0);
			world.IssueOrder(order);

			// A paused game never ticks, so Settle does not wait for a Move there (Q1), and drops the await it will never settle.
			var ack = new OrderV1(true, "order", world.WorldTick, world.WorldTick + 1);
			return new Issued(ack, move, world.Paused, move ? await : null);
		}

		/// <summary>The Move reply for an await that has finished. Cmd names the request that asked for it.</summary>
		public static MoveV1 Reply(string cmd, OrderV1 ack, AgentOrderAwait await)
		{
			var refused = !await.Valid;
			return new MoveV1(true, cmd, ack.Tick, ack.MinEffectTick, await.Valid, await.ResolvedTick, true, await.SettleTicks,
				await.Path ?? [], refused ? ValidatorRejected : null, refused ? await.Orders : null);
		}

		/// <summary>
		/// Builds the Move reply once the order has settled. Parks the calling socket thread in Wait, so the game thread keeps
		/// ticking. Throws when the await fails: restored, a resolve timeout, a dead subject, or the wall-clock backstop.
		/// Must not run on the game thread.
		/// </summary>
		public static MoveV1 Settle(Issued issued)
		{
			var ack = issued.Ack;
			if (issued.Paused)
			{
				// Nothing will tick the await in a paused game, so drop it here, or it stays in the registry until the world is disposed.
				issued.Await?.Abandon();
				return new MoveV1(true, "order", ack.Tick, ack.MinEffectTick, null, null, false, 0, []);
			}

			if (issued.Await == null)
				return new MoveV1(true, "order", ack.Tick, ack.MinEffectTick, null, null, false, 0, []);

			issued.Await.Wait();
			return Reply("order", ack, issued.Await);
		}

		/// <summary>
		/// The Order the request stands for, after the checks Issue makes. It is the object Issue passes to World.IssueOrder.
		/// Must run on the game thread: it reads live world state.
		/// </summary>
		public static Order ToOrder(World world, OrderRequest request)
		{
			var actor = Accept(world, request.Actor, request.Order);

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

			return new Order(request.Order, actor, target, request.Queued)
			{
				TargetString = request.Item,
				ExtraData = request.ExtraData ?? 0,
				ExtraLocation = request.ExtraLocation ?? CPos.Zero,
			};
		}

		/// <summary>Lists the order names a local-player actor accepts. Must run on the game thread.</summary>
		public static OrdersV1 List(World world, uint id)
		{
			var actor = Owned(world, id);
			return new OrdersV1(true, "orders", actor.ActorID, actor.Info.Name, AgentOrderNames.For(actor));
		}

		/// <summary>
		/// Finds the local-player actor and checks that it accepts the order name. A name outside the accepted set is refused
		/// here, with no bypass, so nothing is issued for it. Must run on the game thread.
		/// </summary>
		public static Actor Accept(World world, uint id, string order)
		{
			var actor = Owned(world, id);
			var available = AgentOrderNames.For(actor);
			if (!available.Contains(order))
				throw new OrderRefusedException(id, order, available);

			return actor;
		}

		/// <summary>Finds a live actor the local player owns. Must run on the game thread.</summary>
		static Actor Owned(World world, uint id)
		{
			var actor = Live(world, id);
			if (actor.Owner != world.LocalPlayer)
				throw new ArgumentException($"Actor {id} is not owned by the local player");

			return actor;
		}

		/// <summary>Finds an actor by ID. An unknown ID and a dead actor are reported as different errors.</summary>
		internal static Actor Live(World world, uint id)
		{
			var actor = world.Actors.FirstOrDefault(a => a.ActorID == id);
			if (actor == null)
				throw new ArgumentException($"Unknown actor {id}");

			if (actor.IsDead)
				throw new ArgumentException($"Actor {id} is dead");

			return actor;
		}

		/// <summary>True when the key is set to something other than null. An absent key or null is not present.</summary>
		internal static bool IsPresent(JsonElement root, string name)
		{
			return root.TryGetProperty(name, out var element) && element.ValueKind != JsonValueKind.Null;
		}

		internal static bool TryGetInts(JsonElement element, int count, out int[] values)
		{
			values = null;
			if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != count)
				return false;

			values = new int[count];
			for (var i = 0; i < count; i++)
				if (element[i].ValueKind != JsonValueKind.Number || !element[i].TryGetInt32(out values[i]))
					return false;

			return true;
		}

		/// <summary>True for a JSON number that is a whole, non-negative value. A string, a fraction or a negative number is false.</summary>
		static bool TryGetUInt(JsonElement element, out uint value)
		{
			value = 0;
			return element.ValueKind == JsonValueKind.Number && element.TryGetUInt32(out value);
		}
	}
}
