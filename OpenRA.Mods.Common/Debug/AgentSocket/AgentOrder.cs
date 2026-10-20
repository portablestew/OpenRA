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
using System.Text.RegularExpressions;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.AgentSocket
{
	// Schema v1. Wire keys are snake_case (AgentSocketCommands.Json).
	// MinEffectTick is a lower bound on the tick the order applies on. Network frame delay can make the real tick later.
	public sealed record OrderV1(bool Ok, string Cmd, int Tick, int MinEffectTick);

	/// <summary>
	/// The reply to an order name with no richer shape of its own (9.2): the resolution signal, so the socket never blocks for
	/// the order to finish. Valid and EffectTick are the validator result and the tick the order resolved on, and SettledTicks is
	/// the settle window the reply then held, so trailing events land in the same digest. Reason and Orders (P1.5) are set only on
	/// a refusal, and are omitted from every other reply. Valid and EffectTick are null, and Settled false, when the game was
	/// paused and the reply did not wait.
	/// </summary>
	public sealed record OrderOutcomeV1(bool Ok, string Cmd, int Tick, int MinEffectTick, bool? Valid, int? EffectTick, bool Settled,
		int SettledTicks,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string Reason = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[] Orders = null);

	/// <summary>
	/// A parsed order. At most one of Cell, Pos and TargetActor is set. None of them means an untargeted order, such as Stop.
	/// Item, ExtraData and ExtraLocation become the order's TargetString, ExtraData and ExtraLocation. Null means absent.
	/// A group (P1.14) sets Actors (the actor list) or Type and TypePattern (the type regex). Actor is 0 then, and unused.
	/// On is the on key, the desired state of a toggle order; null when absent.
	/// </summary>
	public sealed record OrderRequest(string Order, uint Actor, CPos? Cell, WPos? Pos, uint? TargetActor, bool Queued,
		string Item, uint? ExtraData, CPos? ExtraLocation, int? SettleTicks, bool Await, uint[] Actors = null, string Type = null,
		Regex TypePattern = null, bool? On = null)
	{
		/// <summary>True for a group order (P1.14): the request names its actors by list or by type.</summary>
		public bool Grouped => Actors != null || TypePattern != null;
	}

	/// <summary>
	/// One actor's outcome in a grouped order (P1.14). Valid, EffectTick, SettledTicks, Path and Orders are the single-actor Move
	/// reply's values for that actor. Outcome and Reason are the Attack or DeployTransform outcome. Error is set when the actor's
	/// await failed: its subject died, or it did not resolve in time. A field that does not apply is omitted.
	/// </summary>
	public sealed record GroupResultV1(uint Actor, bool Settled,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Valid = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EffectTick = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SettledTicks = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[][] Path = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string Outcome = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string Reason = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[] Orders = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string Error = null);

	/// <summary>
	/// The reply to a grouped order (P1.14). Ordered is the ids of the actors the one engine order went to, and Matched is how many
	/// actors the selector matched. A request is refused as a whole when any matched actor refuses the order, so the two are equal.
	/// Results has one entry per ordered actor, in the same order as Ordered.
	/// </summary>
	public sealed record GroupedOrderV1(bool Ok, string Cmd, int Tick, int MinEffectTick, uint[] Ordered, int Matched, GroupResultV1[] Results);

	/// <summary>
	/// The sell reply (3.4). Outcome is sold once the owner sold the actor. The actor is disposed by then, so the reply names no
	/// state of it. Valid and EffectTick are the validator result and the tick the sell resolved on.
	/// </summary>
	public sealed record SellV1(bool Ok, string Cmd, int Tick, int MinEffectTick, bool Valid, int EffectTick, bool Settled, string Outcome);

	/// <summary>
	/// What IssueGroup returns on the game thread. Awaits has one await per ordered actor, in the same order as Ordered, one for
	/// every order name (9.2). Paused is true when the game is paused, so nothing will tick the awaits.
	/// </summary>
	public sealed record GroupIssued(OrderV1 Ack, uint[] Ordered, AgentOrderAwait[] Awaits, bool Paused);

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
	/// The attack reply (P1.6). Outcome is started, or no_valid_target with Reason saying why the attack did not start. Both are
	/// null when the game was paused and the reply did not wait, so Settled is false.
	/// </summary>
	public sealed record AttackV1(bool Ok, string Cmd, int Tick, int MinEffectTick, bool Settled, string Outcome,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string Reason = null);

	/// <summary>
	/// The deploy reply (P1.8). Outcome is completed when the transform replaced the actor, or canceled with Reason naming the
	/// cell reason (occupied, terrain, ramp) or the trait abort. Ok is false only for a canceled deploy. Outcome and Reason are
	/// null when the game was paused and the reply did not wait.
	/// </summary>
	public sealed record DeployV1(bool Ok, string Cmd, int Tick, int MinEffectTick, bool Settled, string Outcome,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string Reason = null);

	/// <summary>
	/// The AttackMove reply (P3.0a). Valid, EffectTick and Settled follow the Move reply, but there is no path: the unit's travel is not
	/// awaited. Outcome is started, or no_path when the unit planned no way to the cell (valid is then false). Reason is set for a
	/// no_path and for a refusal, and Orders only for a refusal. A paused game gives nulls, as a Move does.
	/// </summary>
	public sealed record AttackMoveV1(bool Ok, string Cmd, int Tick, int MinEffectTick, bool? Valid, int? EffectTick, bool Settled,
		int SettledTicks,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string Outcome = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string Reason = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[] Orders = null);

	/// <summary>
	/// What Issue returns on the game thread. Await is the wait for the order's resolution: every order name gets one (9.2), and
	/// the richer verbs add their own outcome on top of it. Paused is true when the game is paused, which never ticks the await,
	/// so Settle does not wait for it (Q1).
	/// </summary>
	public sealed record Issued(OrderV1 Ack, bool Move, bool Paused, AgentOrderAwait Await, ToggleIssue Toggle = null);

	/// <summary>What a toggle order issued on the game thread: its subject and name, the state asked for, and whether that state already held.</summary>
	public sealed record ToggleIssue(Actor Subject, string Order, bool On, bool Unchanged);

	/// <summary>
	/// The reply to a toggle order (3.3). State is "on" or "off": the state the engine left once the order settled, or the state
	/// already held when Unchanged is true and nothing was issued. Valid, EffectTick, Settled and SettledTicks follow the Move
	/// reply. Reason and Orders are set only on a refusal.
	/// </summary>
	public sealed record ToggleOrderV1(bool Ok, string Cmd, int Tick, int MinEffectTick, bool? Valid, int? EffectTick, bool Settled,
		int SettledTicks, string State, bool Unchanged,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string Reason = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[] Orders = null);

	/// <summary>The orders command: the order names an actor accepts. Schema v1, additive.</summary>
	public sealed record OrdersV1(bool Ok, string Cmd, uint Actor, string Type, string[] Orders);

	/// <summary>The refusal for an order name the actor does not accept. Available lists the names it does accept.</summary>
	public sealed record OrderRefusedV1(bool Ok, string Cmd, string Error, string[] Available, string Hint);

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
		const string NoPathReason = "no path to the cell";

		/// <summary>Parses the request on the socket thread. Returns an error message, or null when the request is valid.</summary>
		public static string Parse(JsonElement root, out OrderRequest request)
		{
			request = null;
			if (!root.TryGetProperty("order", out var orderElement) || orderElement.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(orderElement.GetString()))
				return "order must be a non-empty string";

			// Exactly one selector names the actors: one actor, a list of them, or a type regex (P1.14).
			var selectors = (IsPresent(root, "actor") ? 1 : 0) + (IsPresent(root, "actors") ? 1 : 0) + (IsPresent(root, "type") ? 1 : 0);
			if (selectors != 1)
				return "give exactly one of actor, actors and type";

			uint actor = 0;
			uint[] actors = null;
			string type = null;
			Regex typePattern = null;
			if (IsPresent(root, "actor"))
			{
				if (!root.GetProperty("actor").TryGetUInt32(out actor))
					return "actor must be an unsigned integer";
			}
			else if (IsPresent(root, "actors"))
			{
				if (!TryGetUInts(root.GetProperty("actors"), out actors) || actors.Length == 0)
					return "actors must be a non-empty array of unsigned integers";

				if (actors.Distinct().Count() != actors.Length)
					return "actors must not repeat an actor";
			}
			else
			{
				var typeElement = root.GetProperty("type");
				if (typeElement.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(typeElement.GetString()))
					return "type must be a non-empty string";

				type = typeElement.GetString();
				try
				{
					// Full match: the pattern covers the whole type name, so "e1" matches only e1, and "e." matches e1 through e9.
					typePattern = new Regex(@"\A(?:" + type + @")\z");
				}
				catch (ArgumentException e)
				{
					return $"type is not a valid regular expression: {e.Message}";
				}
			}

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

				if (orderElement.GetString() is not ("Move" or "AttackMove"))
					return "settle_ticks applies only to Move and AttackMove";

				settleTicks = (int)settle;
			}

			// await:false returns a job id at once instead of waiting for the Move to settle (P1.4).
			var awaits = true;
			if (IsPresent(root, "await"))
			{
				var awaitElement = root.GetProperty("await");
				if (awaitElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
					return "await must be a boolean";

				if (orderElement.GetString() is not ("Move" or "AttackMove") && awaitElement.ValueKind == JsonValueKind.False)
					return "await:false applies only to Move and AttackMove";

				// A group has one await per actor, and a job holds one, so a group always waits (P1.14).
				if ((actors != null || typePattern != null) && awaitElement.ValueKind == JsonValueKind.False)
					return "await:false applies only to a single actor";

				awaits = awaitElement.ValueKind == JsonValueKind.True;
			}

			// on is the desired state of a toggle order. Whether the order is a toggle depends on the actor, so Accept checks it at issue.
			bool? on = null;
			if (IsPresent(root, "on"))
			{
				var onElement = root.GetProperty("on");
				if (onElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
					return "on must be a boolean";

				on = onElement.ValueKind == JsonValueKind.True;
			}

			// A toggle's on sets one actor's state. A group's actors may disagree, so each would need its own check, and it is refused.
			if (on is not null && (actors != null || typePattern != null))
				return "on applies to a single actor, not a group";

			request = new OrderRequest(orderElement.GetString(), actor, cell, pos, targetActor, queued, item, extraData, extraLocation,
				settleTicks, awaits, actors, type, typePattern, on);
			return null;
		}

		/// <summary>
		/// Validates the request and issues it, then selects the ordered actor for the request (P1.15). Must run on the game thread:
		/// it reads live world state.
		/// </summary>
		public static Issued Issue(World world, OrderRequest request, AgentSelection selection)
		{
			var order = ToOrder(world, request);
			if (request.On is { } on)
				return IssueToggle(world, order, on, selection);

			var move = order.OrderString is "Move" or "AttackMove";

			// Every order name registers an await (9.2), so the reply reports the resolution outcome and returns at that plus the
			// settle window, never waiting for the order to finish. Only Move and AttackMove may set their own window.
			var settleTicks = move && request.SettleTicks is { } given ? given : AgentOrderAwait.DefaultSettleTicks;
			var await = AgentOrderAwait.Register(order, settleTicks);
			world.IssueOrder(order);
			selection.Ordered(world, order.Subject);

			// A paused game never ticks, so Settle does not wait there (Q1), and drops the await it will never settle.
			var ack = new OrderV1(true, "order", world.WorldTick, world.WorldTick + 1);
			return new Issued(ack, move, world.Paused, await);
		}

		/// <summary>
		/// Issues a toggle order as its desired state (3.3). It reads the toggle on the game thread, and issues one engine toggle
		/// only when the state differs from on. When the state already matches, nothing is issued and the reply says unchanged.
		/// Must run on the game thread: it reads live world state.
		/// </summary>
		static Issued IssueToggle(World world, Order order, bool on, AgentSelection selection)
		{
			var actor = order.Subject;

			// A toggle still in flight has not flipped yet, so a read now would miss it and a second flip would undo the first.
			if (AgentOrderAwait.Unresolved(actor, order.OrderString))
				throw new ArgumentException($"'{order.OrderString}' on actor {actor.ActorID} is still in flight, so wait for it to settle before setting it again");

			var unchanged = ToggleOn(actor, order.OrderString) == on;
			var toggle = new ToggleIssue(actor, order.OrderString, on, unchanged);
			selection.Ordered(world, actor);

			var ack = new OrderV1(true, "order", world.WorldTick, world.WorldTick + 1);
			if (unchanged)
				return new Issued(ack, false, world.Paused, null, toggle);

			var await = AgentOrderAwait.Register(order, AgentOrderAwait.DefaultSettleTicks);
			world.IssueOrder(order);
			return new Issued(ack, false, world.Paused, await, toggle);
		}

		/// <summary>The state of the ToggleConditionOnOrder that takes the order name: true when on. Accept has checked the name. Must run on the game thread.</summary>
		static bool ToggleOn(Actor actor, string order)
		{
			return actor.TraitsImplementing<ToggleConditionOnOrder>().First(t => t.Info.OrderName == order).Enabled;
		}

		static string StateName(bool on)
		{
			return on ? "on" : "off";
		}

		/// <summary>
		/// The reply to a toggle order (3.3). State is read on the game thread after the order settles, so it is the state the engine
		/// left, not the one requested. An unchanged request waited for nothing, so it is settled at once.
		/// </summary>
		static ToggleOrderV1 SettleToggle(OrderV1 ack, AgentOrderAwait await, bool paused, ToggleIssue toggle)
		{
			if (toggle.Unchanged)
				return new ToggleOrderV1(true, "order", ack.Tick, ack.MinEffectTick, true, ack.Tick, true, 0, StateName(toggle.On), true);

			if (paused)
			{
				// Nothing will tick the await in a paused game, so drop it here, or it stays in the registry until the world is disposed.
				await.Abandon();
				return ToggleUnsettled(ack, toggle);
			}

			try
			{
				await.Wait();
			}
			catch (AgentPausedException)
			{
				// The game paused while the wait was parked, so no tick will complete it. Drop it and report it unsettled.
				await.Abandon();
				return ToggleUnsettled(ack, toggle);
			}

			var state = AgentMarshal.ExecuteOnGameThread(() => ToggleOn(toggle.Subject, toggle.Order));
			var refused = !await.Valid;
			return new ToggleOrderV1(true, "order", ack.Tick, ack.MinEffectTick, await.Valid, await.ResolvedTick, true, await.SettleTicks,
				StateName(state), false, refused ? ValidatorRejected : null, refused ? await.Orders : null);
		}

		/// <summary>The reply for a toggle a paused game left unsettled. The state is still read, since the game thread still runs.</summary>
		static ToggleOrderV1 ToggleUnsettled(OrderV1 ack, ToggleIssue toggle)
		{
			var state = AgentMarshal.ExecuteOnGameThread(() => ToggleOn(toggle.Subject, toggle.Order));
			return new ToggleOrderV1(true, "order", ack.Tick, ack.MinEffectTick, null, null, false, 0, StateName(state), false);
		}

		/// <summary>The Move reply for an await that has finished. Cmd names the request that asked for it.</summary>
		public static MoveV1 Reply(string cmd, OrderV1 ack, AgentOrderAwait await)
		{
			var refused = !await.Valid;
			return new MoveV1(true, cmd, ack.Tick, ack.MinEffectTick, await.Valid, await.ResolvedTick, true, await.SettleTicks,
				await.Path ?? [], refused ? ValidatorRejected : null, refused ? await.Orders : null);
		}

		/// <summary>
		/// Builds the reply once the order's await has settled or its outcome is known (9.2): the richer verbs report their own
		/// outcome, and every other order name reports its resolution signal. Parks the calling socket thread in Wait, so the game
		/// thread keeps ticking, and returns at the resolution plus the settle window, never at the order's completion. Throws when
		/// the await fails: restored, a resolve timeout, a dead subject, or the wall-clock backstop. Must not run on the game thread.
		/// </summary>
		public static object Settle(Issued issued)
		{
			var ack = issued.Ack;
			var await = issued.Await;
			if (issued.Toggle is { } toggle)
				return SettleToggle(ack, await, issued.Paused, toggle);

			if (issued.Paused)
			{
				// Nothing will tick the await in a paused game, so drop it here, or it stays in the registry until the world is disposed.
				await?.Abandon();
				if (await is { IsAttack: true })
					return new AttackV1(true, "order", ack.Tick, ack.MinEffectTick, false, null);

				if (await is { IsDeploy: true })
					return new DeployV1(true, "order", ack.Tick, ack.MinEffectTick, false, null);

				if (await is { IsAttackMove: true })
					return new AttackMoveV1(true, "order", ack.Tick, ack.MinEffectTick, null, null, false, 0);

				if (await is { IsMoveOrder: true })
					return new MoveV1(true, "order", ack.Tick, ack.MinEffectTick, null, null, false, 0, []);

				return Outcome("order", ack, null);
			}

			await.Wait();
			if (await.IsAttack)
				return new AttackV1(true, "order", ack.Tick, ack.MinEffectTick, true, await.Outcome, await.Reason);

			if (await.IsAttackMove)
			{
				var (valid, outcome, reason, orders) = AttackMoveOutcome(await);
				return new AttackMoveV1(true, "order", ack.Tick, ack.MinEffectTick, valid, await.ResolvedTick, true, await.SettleTicks, outcome, reason, orders);
			}

			if (await.IsDeploy)
				return new DeployV1(await.Outcome == AgentOrderAwait.Completed, "order", ack.Tick, ack.MinEffectTick, true, await.Outcome, await.Reason);

			if (await.IsSell && await.Outcome == AgentOrderAwait.SoldOutcome)
				return new SellV1(true, "order", ack.Tick, ack.MinEffectTick, true, await.ResolvedTick, true, await.Outcome);

			if (await.IsMoveOrder)
				return Reply("order", ack, await);

			return Outcome("order", ack, await);
		}

		/// <summary>
		/// The reply to an order name with no richer shape of its own (9.2): valid and effect_tick, with reason and orders on a
		/// refusal. A null await is a paused game, which never waits. Must not run on the game thread.
		/// </summary>
		static OrderOutcomeV1 Outcome(string cmd, OrderV1 ack, AgentOrderAwait await)
		{
			if (await == null)
				return new OrderOutcomeV1(true, cmd, ack.Tick, ack.MinEffectTick, null, null, false, 0);

			var refused = !await.Valid;
			return new OrderOutcomeV1(true, cmd, ack.Tick, ack.MinEffectTick, await.Valid, await.ResolvedTick, true, await.SettleTicks,
				refused ? ValidatorRejected : null, refused ? await.Orders : null);
		}

		/// <summary>
		/// Validates a grouped request (P1.14) and issues it as one engine Order with GroupedActors, so the engine resolves each actor
		/// on its own. Every matched actor is checked before anything is issued, and one that does not accept the order name refuses
		/// the whole request. Each ordered actor is selected (P1.15). Each actor gets its own await, on the per-subject copy the engine
		/// resolves, so each actor reports its own outcome. Must run on the game thread: it reads live world state.
		/// </summary>
		public static GroupIssued IssueGroup(World world, OrderRequest request, AgentSelection selection)
		{
			var actors = GroupActors(world, request);
			var order = ToGroupOrder(world, request, actors);
			var move = order.OrderString is "Move" or "AttackMove";

			// Each await is registered on the copy of the order that the engine resolves for its actor, so Resolve matches it by
			// subject. Every order name gets one (9.2), as the single-actor reply does.
			var settleTicks = move && request.SettleTicks is { } given ? given : AgentOrderAwait.DefaultSettleTicks;
			var awaits = actors.Select(a => AgentOrderAwait.Register(Order.FromGroupedOrder(order, a), settleTicks)).ToArray();
			world.IssueOrder(order);
			foreach (var actor in actors)
				selection.Ordered(world, actor);

			var ack = new OrderV1(true, "order", world.WorldTick, world.WorldTick + 1);
			return new GroupIssued(ack, actors.Select(a => a.ActorID).ToArray(), awaits, world.Paused);
		}

		/// <summary>
		/// Builds the reply to a grouped order once every ordered actor's await has settled or failed (P1.14). A failed await becomes
		/// that actor's error, and the other actors still report. Throws when the shared wall-clock limit passes first, and then
		/// abandons every await. Must not run on the game thread.
		/// </summary>
		public static GroupedOrderV1 SettleGroup(GroupIssued issued)
		{
			var ack = issued.Ack;
			var ids = issued.Ordered;
			var awaits = issued.Awaits;
			if (issued.Paused)
			{
				// Nothing ticks the awaits in a paused game, so drop them here, or they stay in the registry until the world is disposed.
				foreach (var await in awaits)
					await.Abandon();

				return new GroupedOrderV1(true, "order", ack.Tick, ack.MinEffectTick, ids, ids.Length, ids.Select(id => new GroupResultV1(id, false)).ToArray());
			}

			var deadline = DateTime.UtcNow.AddMilliseconds(AgentOrderAwait.WallClockMs);
			foreach (var await in awaits)
			{
				var remaining = Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
				if (await.TryWait(remaining))
					continue;

				foreach (var other in awaits)
					other.Abandon();

				throw new TimeoutException($"the order did not settle within {AgentOrderAwait.WallClockMs} ms of wall-clock time");
			}

			var results = new GroupResultV1[ids.Length];
			for (var i = 0; i < ids.Length; i++)
				results[i] = GroupResult(ids[i], awaits[i]);

			return new GroupedOrderV1(true, "order", ack.Tick, ack.MinEffectTick, ids, ids.Length, results);
		}

		/// <summary>
		/// The AttackMove outcome of a settled await (P3.0a): whether the order is valid, the outcome and reason to report, and the
		/// orders the subject accepts on a refusal. A refusal is valid false with its reason. A path of no cells, from a unit not
		/// already on the cell, is valid false with no_path.
		/// </summary>
		static (bool Valid, string Outcome, string Reason, string[] Orders) AttackMoveOutcome(AgentOrderAwait await)
		{
			if (!await.Valid)
				return (false, null, ValidatorRejected, await.Orders);

			if (await.NoPath)
				return (false, AgentOrderAwait.NoPathOutcome, NoPathReason, null);

			return (true, AgentOrderAwait.Started, null, null);
		}

		/// <summary>One actor's entry in a grouped reply. Called once the actor's await has settled or failed. Must not run on the game thread.</summary>
		static GroupResultV1 GroupResult(uint id, AgentOrderAwait await)
		{
			try
			{
				await.ThrowIfFailed();
			}
			catch (Exception e)
			{
				return new GroupResultV1(id, false, Error: e.Message);
			}

			if (await.IsAttack || await.IsDeploy || (await.IsSell && await.Outcome != null))
				return new GroupResultV1(id, true, Outcome: await.Outcome, Reason: await.Reason);

			if (await.IsAttackMove)
			{
				var (valid, outcome, reason, orders) = AttackMoveOutcome(await);
				return new GroupResultV1(id, true, Valid: valid, EffectTick: await.ResolvedTick, SettledTicks: await.SettleTicks,
					Outcome: outcome, Reason: reason, Orders: orders);
			}

			var refused = !await.Valid;
			return new GroupResultV1(id, true, Valid: await.Valid, EffectTick: await.ResolvedTick, SettledTicks: await.SettleTicks,
				Path: await.IsMoveOrder ? await.Path ?? [] : null, Reason: refused ? ValidatorRejected : null, Orders: refused ? await.Orders : null);
		}

		/// <summary>
		/// The actors a grouped request selects, each checked to be owned, live and to accept the order name. A list is checked as
		/// given. A type matches the owned, live actors whose type name the pattern matches. No match is an error (fail loud).
		/// Must run on the game thread.
		/// </summary>
		static Actor[] GroupActors(World world, OrderRequest request)
		{
			if (request.Actors != null)
				return request.Actors.Select(id => Accept(world, id, request.Order, request.On)).ToArray();

			var matched = world.Actors
				.Where(a => a.Owner == world.LocalPlayer && !a.IsDead && request.TypePattern.IsMatch(a.Info.Name))
				.ToArray();
			if (matched.Length == 0)
				throw new ArgumentException($"no owned actor's type matches \"{request.Type}\"");

			return matched.Select(a => Accept(world, a.ActorID, request.Order, request.On)).ToArray();
		}

		/// <summary>The Order a grouped request stands for. Its subject is null, as the engine's own grouped orders are: each actor's copy has its subject. Must run on the game thread.</summary>
		static Order ToGroupOrder(World world, OrderRequest request, Actor[] actors)
		{
			return new Order(request.Order, null, TargetOf(world, request), request.Queued, groupedActors: actors)
			{
				TargetString = request.Item,
				ExtraData = request.ExtraData ?? 0,
				ExtraLocation = request.ExtraLocation ?? CPos.Zero,
			};
		}

		/// <summary>
		/// The Order the request stands for, after the checks Issue makes. It is the object Issue passes to World.IssueOrder.
		/// Must run on the game thread: it reads live world state.
		/// </summary>
		public static Order ToOrder(World world, OrderRequest request)
		{
			var actor = Accept(world, request.Actor, request.Order, request.On);

			return new Order(request.Order, actor, TargetOf(world, request), request.Queued)
			{
				TargetString = request.Item,
				ExtraData = request.ExtraData ?? 0,
				ExtraLocation = request.ExtraLocation ?? CPos.Zero,
			};
		}

		/// <summary>The target the request names, or Invalid when it names none. Must run on the game thread.</summary>
		static Target TargetOf(World world, OrderRequest request)
		{
			if (request.Cell is { } cell)
			{
				if (!world.Map.Contains(cell))
					throw new ArgumentException($"cell {cell.X},{cell.Y} is outside the map");

				return Target.FromCell(world, cell);
			}

			if (request.Pos is { } pos)
				return Target.FromPos(pos);

			if (request.TargetActor is { } targetId)
				return Target.FromActor(Live(world, targetId));

			return Target.Invalid;
		}

		/// <summary>Lists the order names a local-player actor accepts. Must run on the game thread.</summary>
		public static OrdersV1 List(World world, uint id)
		{
			var actor = Owned(world, id);
			return new OrdersV1(true, "orders", actor.ActorID, actor.Info.Name, AgentOrderNames.For(actor));
		}

		/// <summary>
		/// Finds the local-player actor and checks that it accepts the order name. A name outside the accepted set is refused
		/// here, with no bypass, so nothing is issued for it. The one exception is Attack (P1.6): an actor with no attack trait is
		/// still issued the order, ValidateOrder refuses it, and the attack reply says why. An on key is refused, since it only
		/// applies to a toggle order, and a toggle order needs one: there is no bare toggle (desired state only). Must run on the
		/// game thread.
		/// </summary>
		public static Actor Accept(World world, uint id, string order, bool? on = null)
		{
			var actor = Owned(world, id);
			var available = AgentOrderNames.For(actor);
			if (!available.Contains(order) && order != "Attack")
				throw new OrderRefusedException(id, order, available);

			var toggle = AgentOrderNames.IsToggle(actor, order);
			if (toggle && on is null)
				throw new ArgumentException($"'{order}' is a toggle for actor {id}, so it needs on: true or on: false");

			if (!toggle && on is not null)
				throw new ArgumentException($"on applies only to a toggle order, and '{order}' is not one for actor {id}");

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

		/// <summary>True for a JSON array whose every item is an unsigned integer. An empty array is true, with no values.</summary>
		static bool TryGetUInts(JsonElement element, out uint[] values)
		{
			values = null;
			if (element.ValueKind != JsonValueKind.Array)
				return false;

			values = new uint[element.GetArrayLength()];
			for (var i = 0; i < values.Length; i++)
				if (!TryGetUInt(element[i], out values[i]))
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
