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
using NUnit.Framework;
using OpenRA.Traits;

namespace OpenRA.Test
{
	/// <summary>
	/// The pending-order registry (P1.2). Orders go through the real engine notify: Ordered runs for every resolved order,
	/// with no subscriber, and resolves the await registered on the order's world. The mod's types are reached by
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
			var await = Call("Register", order);
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
			var firstAwait = Call("Register", firstOrder);

			using var second = HeadlessWorld.Create(2);
			var secondUnit = second.CreateActor("e1", second.FindClearArea(new CPos(50, 50), 1, 1), second.LocalPlayer);
			second.Tick(1);
			var secondDestination = second.FindClearArea(secondUnit.Location, 1, 1, 6);
			var secondOrder = new Order("Move", secondUnit, Target.FromCell(second.World, secondDestination), false);
			var secondAwait = Call("Register", secondOrder);

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
	}
}
