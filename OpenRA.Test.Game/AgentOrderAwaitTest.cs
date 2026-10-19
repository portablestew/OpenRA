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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenRA.Network;
using OpenRA.Traits;

namespace OpenRA.Test
{
	/// <summary>
	/// The pending-order registry (P1.2). Orders go through the real engine notify: Ordered runs for every resolved order,
	/// with no connection, and resolves the await registered on the order's world. The mod's types are reached by
	/// reflection, as in AgentOrderStanceEventsTest.
	/// </summary>
	[TestFixture]
	sealed class AgentOrderAwaitTest
	{
		bool savedAgentSocket;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[SetUp]
		public void EnableAgentSocket()
		{
			savedAgentSocket = Game.Settings.Debug.AgentSocket;
			Game.Settings.Debug.AgentSocket = true;
			Call("Clear");
		}

		[TearDown]
		public void RestoreAgentSocket()
		{
			Call("Clear");
			Game.Settings.Debug.AgentSocket = savedAgentSocket;
		}

		static Type AwaitType()
		{
			return HeadlessMod.ModData.ObjectCreator.FindType("AgentSocket.AgentOrderAwait");
		}

		static object Call(string method, params object[] args)
		{
			return AwaitType().GetMethod(method).Invoke(null, args);
		}

		static T Field<T>(object target, string name)
		{
			return (T)target.GetType().GetProperty(name).GetValue(target);
		}

		static int PendingCount(World world)
		{
			return (int)Call("PendingCount", world);
		}

		[TestCase(TestName = "One Move resolves its await once, valid, on the world it was issued on")]
		public void MoveResolvesItsAwaitOnce()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);

			var order = new Order("Move", unit, Target.FromCell(hw.World, destination), false);
			var await = Call("Register", order, 3);
			Assert.That(PendingCount(hw.World), Is.EqualTo(1), "The issued order was not registered.");

			var issuedAt = hw.World.WorldTick;
			hw.World.IssueOrder(order);
			Assert.That(hw.TickUntil(() => Field<bool>(await, "Resolved"), 5), Is.Not.Null, "The order never resolved.");

			Assert.That(Field<bool>(await, "Valid"), Is.True, "The validators rejected a legal Move.");
			Assert.That(Field<World>(await, "World"), Is.SameAs(hw.World));
			Assert.That(Field<int>(await, "ResolvedTick"), Is.EqualTo(issuedAt + HeadlessWorld.OrderLatencyTicks));
			Assert.That(PendingCount(hw.World), Is.EqualTo(0), "The resolved await is still pending.");
		}

		[TestCase(TestName = "A second world's order does not resolve the first world's pending await")]
		public void OtherWorldsOrderDoesNotResolveAwait()
		{
			using var first = HeadlessWorld.Create(1);
			var firstUnit = first.CreateActor("e1", first.FindClearArea(new CPos(50, 50), 1, 1), first.LocalPlayer);
			first.Tick(1);
			var firstDestination = first.FindClearArea(firstUnit.Location, 1, 1, 6);
			var firstOrder = new Order("Move", firstUnit, Target.FromCell(first.World, firstDestination), false);
			var firstAwait = Call("Register", firstOrder, 3);

			using var second = HeadlessWorld.Create(2);
			var secondUnit = second.CreateActor("e1", second.FindClearArea(new CPos(50, 50), 1, 1), second.LocalPlayer);
			second.Tick(1);

			// The world filter is only exercised when both worlds hand out the same actor ID.
			Assert.That(secondUnit.ActorID, Is.EqualTo(firstUnit.ActorID), "The two worlds do not share an actor ID, so the world filter is untested.");
			var secondDestination = second.FindClearArea(secondUnit.Location, 1, 1, 6);
			var secondOrder = new Order("Move", secondUnit, Target.FromCell(second.World, secondDestination), false);
			var secondAwait = Call("Register", secondOrder, 3);

			second.World.IssueOrder(secondOrder);
			Assert.That(second.TickUntil(() => Field<bool>(secondAwait, "Resolved"), 5), Is.Not.Null, "The second world's order never resolved.");

			Assert.That(Field<bool>(firstAwait, "Resolved"), Is.False, "The second world's order resolved the first world's await.");
			Assert.That(PendingCount(first.World), Is.EqualTo(1), "The first world's await was consumed.");
		}

		[TestCase(TestName = "An order for a dead actor is rejected at issue and leaves nothing pending")]
		public void DeadSubjectIsRejectedAtIssue()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			unit.Kill(unit);

			// The request returns at once with the error, so nothing waits on an order that will never resolve.
			using var response = AgentStateDumpTest.Request($"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"cell\":[1,1]}}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo($"Actor {unit.ActorID} is dead"));
			Assert.That(PendingCount(hw.World), Is.EqualTo(0), "A rejected order left an await pending.");
		}

		[TestCase(TestName = "An order selects the actor it was issued to")]
		public void OrderSelectsItsActor()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.Request(MoveRequest(hw, unit));
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(hw.World.Selection.Actors, Is.EquivalentTo([unit]));
		}

		[TestCase(TestName = "An array's orders select exactly the actors they ordered, and a later ping leaves the selection alone")]
		public void ArrayOrdersSelectTheirUnion()
		{
			using var hw = HeadlessWorld.Create(1);
			var a = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			var b = hw.CreateActor("e1", hw.FindClearArea(new CPos(60, 50), 1, 1), hw.LocalPlayer);
			var c = hw.CreateActor("e1", hw.FindClearArea(new CPos(70, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			// c is selected before the request, so the array has to replace the selection rather than add to it.
			hw.World.Selection.Combine(hw.World, [c], false, false);

			using var response = AgentStateDumpTest.Request($"[{MoveRequest(hw, a)},{MoveRequest(hw, b)}]");
			Assert.That(response.RootElement.GetArrayLength(), Is.EqualTo(2));
			Assert.That(hw.World.Selection.Actors, Is.EquivalentTo([a, b]));

			using var ping = AgentStateDumpTest.RequestWithTicks(hw, "{\"cmd\":\"ping\"}");
			Assert.That(ping.RootElement.GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(hw.World.Selection.Actors, Is.EquivalentTo([a, b]), "A request with no order changed the selection.");
		}

		[TestCase(TestName = "A refused order selects nothing, and the first accepted order of a request replaces the selection")]
		public void RefusedOrderSelectsNothing()
		{
			using var hw = HeadlessWorld.Create(1);
			var a = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			var dead = hw.CreateActor("e1", hw.FindClearArea(new CPos(60, 50), 1, 1), hw.LocalPlayer);
			var b = hw.CreateActor("e1", hw.FindClearArea(new CPos(70, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			dead.Kill(dead);

			hw.World.Selection.Combine(hw.World, [a], false, false);

			using var refused = AgentStateDumpTest.RawRequest($"[{MoveRequest(hw, dead)}]");
			Assert.That(refused.RootElement.GetProperty("results")[0].GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(hw.World.Selection.Actors, Is.EquivalentTo([a]), "A refused order changed the selection.");

			using var mixed = AgentStateDumpTest.Request($"[{MoveRequest(hw, dead)},{MoveRequest(hw, b)}]");
			Assert.That(mixed.RootElement[1].GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(hw.World.Selection.Actors, Is.EquivalentTo([b]), "The selection was not replaced by the accepted order alone.");
		}

		/// <summary>An await:false Move for the unit to a clear cell, so the request returns at once with a job.</summary>
		static string MoveRequest(HeadlessWorld hw, Actor unit)
		{
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);
			return $"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}],\"await\":false}}";
		}

		static Type ModType(string name)
		{
			return HeadlessMod.ModData.ObjectCreator.FindType(name);
		}

		static object Invoke(object target, string method, params object[] args)
		{
			return target.GetType().GetMethod(method).Invoke(target, args);
		}

		/// <summary>Takes every queued event from the connection. Fails rather than blocking when nothing was delivered.</summary>
		static List<object> Take(object connection)
		{
			var pending = (int)connection.GetType().GetProperty("Pending").GetValue(connection);
			Assert.That(pending, Is.GreaterThan(0), "No events reached the connection.");
			return Only(((IList)Invoke(connection, "Flush")).Cast<object>(), "order").ToList();
		}

		[TestCase(TestName = "A Move reply waits for the order to settle, then reports its effect tick, path and settle count")]
		public void MoveReplyWaitsForSettleAndReportsIt()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);
			var start = unit.Location;

			var connection = Activator.CreateInstance(ModType("AgentSocket.AgentConnection"));
			try
			{
				var line = $"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}]}}";
				using var response = AgentStateDumpTest.RequestWithTicks(hw, line);
				var root = response.RootElement;

				Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());

				// The pump ticks before a request is handled, so the issue tick is the reply's tick, not the one before the request.
				var issueTick = root.GetProperty("tick").GetInt32();
				Assert.That(root.GetProperty("min_effect_tick").GetInt32(), Is.EqualTo(issueTick + 1));
				Assert.That(root.GetProperty("valid").GetBoolean(), Is.True);
				Assert.That(root.GetProperty("settled").GetBoolean(), Is.True);
				Assert.That(root.GetProperty("settled_ticks").GetInt32(), Is.EqualTo(3));

				var effectTick = root.GetProperty("effect_tick").GetInt32();
				Assert.That(effectTick, Is.GreaterThanOrEqualTo(issueTick + 1), "The order took effect before the tick after issue.");

				var orders = Take(connection).Where(e => e.GetType().Name == "OrderEventV1").ToList();
				Assert.That(orders, Has.Count.EqualTo(1), "Expected exactly one order event.");
				Assert.That(Field<int>(orders[0], "EffectTick"), Is.EqualTo(effectTick), "The reply's effect_tick is not the order event's.");

				var path = root.GetProperty("path");
				Assert.That(path.GetArrayLength(), Is.GreaterThan(0), "The path is empty.");
				Assert.That(new[] { path[0][0].GetInt32(), path[0][1].GetInt32() }, Is.EqualTo(new[] { start.X, start.Y }), "The path does not start at the actor's cell.");
			}
			finally
			{
				Invoke(connection, "Close");
			}
		}

		[TestCase(TestName = "A Move while the game is paused returns at once, unsettled, and the tick does not move")]
		public void PausedMoveReturnsAtOnce()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);
			hw.World.SetLocalPauseState(true);
			var tick = hw.World.WorldTick;

			// Request, not RequestWithTicks: a paused game never ticks, so a reply that waited would hang.
			var line = $"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}]}}";
			using var response = AgentStateDumpTest.Request(line);
			var root = response.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
			Assert.That(root.GetProperty("settled").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("valid").ValueKind, Is.EqualTo(JsonValueKind.Null));
			Assert.That(root.GetProperty("effect_tick").ValueKind, Is.EqualTo(JsonValueKind.Null));
			Assert.That(root.GetProperty("tick").GetInt32(), Is.EqualTo(tick));
			Assert.That(hw.World.WorldTick, Is.EqualTo(tick), "The paused game advanced.");
		}

		[TestCase(TestName = "A Move while the game is paused leaves no await in the registry")]
		public void PausedMoveLeavesNoPendingAwait()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);
			hw.World.SetLocalPauseState(true);

			// Request, not RequestWithTicks: the reply must return without the world ticking, so any await left behind is stale.
			var line = $"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}]}}";
			using var response = AgentStateDumpTest.Request(line);
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.True, response.RootElement.ToString());
			Assert.That(PendingCount(hw.World), Is.EqualTo(0), "A Move in a paused game left its await in the registry.");
		}

		[TestCase(TestName = "A Move the validators refuse replies valid:false with the reason and the actor's order names")]
		public void RefusedMoveReplyCarriesReasonAndOrders()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);
			var start = unit.Location;

			// The socket's Accept refuses any order the local player's actor would not take, so the shipped ValidateOrder cannot
			// refuse a socket Move. The world's validators are swapped for one that refuses everything, then restored.
			var validators = hw.World.OrderValidators;
			var saved = validators.ToArray();
			Assert.That(saved, Is.Not.Empty, "The world has no validators to swap.");
			for (var i = 0; i < validators.Length; i++)
				validators[i] = new RefuseEveryOrder();

			try
			{
				var line = $"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}]}}";
				using var response = AgentStateDumpTest.RequestWithTicks(hw, line);
				var root = response.RootElement;

				Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
				Assert.That(root.GetProperty("valid").GetBoolean(), Is.False, root.ToString());
				Assert.That(root.GetProperty("reason").GetString(), Is.EqualTo("rejected by order validator"));

				var orders = root.GetProperty("orders").EnumerateArray().Select(e => e.GetString()).ToArray();
				Assert.That(orders, Is.Not.Empty, "The refusal listed no order names.");
				Assert.That(orders, Does.Contain("Move"), "The actor accepts Move, so the refusal should list it.");
			}
			finally
			{
				for (var i = 0; i < saved.Length; i++)
					validators[i] = saved[i];
			}

			Assert.That(unit.Location, Is.EqualTo(start), "A refused Move moved the unit.");
		}

		[TestCase(TestName = "An order name with no richer shape the validators refuse replies valid:false with the reason and the actor's order names")]
		public void RefusedUntrackedOrderReplyCarriesReasonAndOrders()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			// As above: the socket's Accept refuses any order the actor would not take, so the shipped ValidateOrder cannot refuse
			// a socket order. The world's validators are swapped for one that refuses everything, then restored.
			var validators = hw.World.OrderValidators;
			var saved = validators.ToArray();
			Assert.That(saved, Is.Not.Empty, "The world has no validators to swap.");
			for (var i = 0; i < validators.Length; i++)
				validators[i] = new RefuseEveryOrder();

			try
			{
				// Stop has no richer shape of its own, so 9.2's generic reply is what reports the refusal.
				using var response = AgentStateDumpTest.RequestWithTicks(hw, $"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Stop\"}}");
				var root = response.RootElement;

				Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
				Assert.That(root.GetProperty("valid").GetBoolean(), Is.False, root.ToString());
				Assert.That(root.GetProperty("settled").GetBoolean(), Is.True, root.ToString());
				Assert.That(root.GetProperty("reason").GetString(), Is.EqualTo("rejected by order validator"));

				var orders = root.GetProperty("orders").EnumerateArray().Select(e => e.GetString()).ToArray();
				Assert.That(orders, Is.Not.Empty, "The refusal listed no order names.");
				Assert.That(orders, Does.Contain("Stop"), "The actor accepts Stop, so the refusal should list it.");
			}
			finally
			{
				for (var i = 0; i < saved.Length; i++)
					validators[i] = saved[i];
			}
		}

		/// <summary>A validator that refuses every order. It stands in for the shipped one, which the socket cannot reach.</summary>
		sealed class RefuseEveryOrder : IValidateOrder
		{
			public bool OrderValidation(OrderManager orderManager, World world, int clientId, Order order)
			{
				return false;
			}
		}

		/// <summary>
		/// Sends a Move on a worker thread, as the socket does, and pumps the game loop until it has registered. The world is
		/// not ticked, so nothing settles the Move. Then runs swap and pumps until the reply returns. The caller disposes the document.
		/// </summary>
		static JsonDocument MoveInFlightAcross(HeadlessWorld hw, Actor unit, CPos destination, Action swap)
		{
			var line = $"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}]}}";
			var handle = ModType("AgentSocket.AgentSocketCommands").GetMethod("Handle", BindingFlags.Public | BindingFlags.Static);
			var reply = Task.Run(() => (string)handle.Invoke(null, [line, null]));

			var deadline = DateTime.UtcNow.AddSeconds(10);
			while (PendingCount(hw.World) == 0 && DateTime.UtcNow < deadline)
			{
				Game.PerformDelayedActions();
				Thread.Sleep(1);
			}

			Assert.That(PendingCount(hw.World), Is.EqualTo(1), "The Move never registered.");

			swap();

			while (!reply.IsCompleted && DateTime.UtcNow < deadline)
			{
				Game.PerformDelayedActions();
				Thread.Sleep(1);
			}

			Assert.That(reply.IsCompleted, Is.True, "The request never returned.");
			using var envelope = JsonDocument.Parse(reply.Result);
			return AgentStateDumpTest.Results(envelope);
		}

		[TestCase(TestName = "A world swap fails a Move in flight with the restored error")]
		public void WorldSwapFailsMoveInFlightWithRestored()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);

			var bridgeType = ModType("AgentWorldBridge");
			using var response = MoveInFlightAcross(hw, unit, destination,
				() => bridgeType.GetMethod("WorldAdopted").Invoke(Activator.CreateInstance(bridgeType), [hw.World, null]));
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("order"));
			Assert.That(root.GetProperty("error").GetString(), Is.EqualTo("restored"));
			Assert.That(PendingCount(hw.World), Is.EqualTo(0), "The failed Move left an await pending.");
		}

		[TestCase(TestName = "A load_skirmish that replaces the world fails a Move in flight with restored, not the wall-clock timeout")]
		public void WorldLoadFailsMoveInFlightWithRestored()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);
			var replaced = hw.World;

			// The replaced world is disposed, then the new one loads through LoadComplete, as a load_skirmish does.
			using var response = MoveInFlightAcross(hw, unit, destination, () =>
			{
				replaced.Dispose();
				using var next = HeadlessWorld.Create(2);
			});
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("error").GetString(), Is.EqualTo("restored"));
			Assert.That(PendingCount(replaced), Is.EqualTo(0), "The failed Move left an await pending.");
		}

		[TestCase(TestName = "An order that never resolves is failed by the resolve budget and leaves nothing pending")]
		public void UnresolvedOrderIsFailedByBudget()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);

			// Registered but never issued to the world, so it can never resolve.
			Call("Register", new Order("Move", unit, Target.FromCell(hw.World, destination), false), 3);
			Assert.That(PendingCount(hw.World), Is.EqualTo(1));

			var budget = (int)AwaitType().GetField("ResolveBudgetTicks").GetValue(null);
			hw.Tick(budget + 1);

			Assert.That(PendingCount(hw.World), Is.EqualTo(0), "The unresolved await was not failed by the resolve budget.");
		}

		/// <summary>The events of the named types, in the order they were raised. The connection carries every event; a test looks at its own.</summary>
		static IEnumerable<object> Only(IEnumerable<object> events, params string[] types)
		{
			return events.Where(e => types.Contains(e.GetType().GetProperty("Event")?.GetValue(e) as string));
		}
	}
}
