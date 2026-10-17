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

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The order command over the real socket line path (see AgentStateDumpTest for the request helper). Orders go through
	/// World.IssueOrder, so the unit moves only after the order has been looped back and resolved on a later tick.
	/// </summary>
	[TestFixture]
	sealed class AgentOrderTest
	{
		const string StartLine = "{\"cmd\":\"order\",\"actor\":1,\"order\":\"StartProduction\",";
		const string PlaceLine = "{\"cmd\":\"order\",\"actor\":1,\"order\":\"PlaceBuilding\",";

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
		}

		[TearDown]
		public void RestoreAgentSocket()
		{
			Game.Settings.Debug.AgentSocket = savedAgentSocket;
		}

		[TestCase(TestName = "A Move order by cell returns the D11 ack and the unit reaches the destination")]
		public void MoveByCellAcksAndMoves()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);
			var tick = hw.World.WorldTick;

			var line = $"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}]}}";
			using var response = AgentStateDumpTest.RequestWithTicks(hw, line);
			var root = response.RootElement;

			Assert.That(Keys(root), Is.EquivalentTo(["ok", "cmd", "tick", "min_effect_tick", "valid", "effect_tick", "settled", "settled_ticks", "path"]));
			Assert.That(root.GetProperty("settled").GetBoolean(), Is.True, root.ToString());
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("order"));

			// The pump ticks before a request is handled, so the issue tick is at or after the tick before the request.
			var issueTick = root.GetProperty("tick").GetInt32();
			Assert.That(issueTick, Is.GreaterThan(tick));
			Assert.That(root.GetProperty("min_effect_tick").GetInt32(), Is.EqualTo(issueTick + 1));

			Assert.That(hw.TickUntil(() => unit.Location == destination, 500), Is.Not.Null, "The unit never reached the destination.");
		}

		[TestCase(TestName = "A Move order by pos is accepted and returns the ack")]
		public void MoveByPosIsAccepted()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);
			var pos = new WPos(destination.X * 1024 + 512, destination.Y * 1024 + 512, 0);

			var line = $"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"pos\":[{pos.X},{pos.Y},{pos.Z}]}}";
			using var response = AgentStateDumpTest.RequestWithTicks(hw, line);
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.True);

			// The pump ticks before a request is handled, so the min_effect_tick follows the reply's tick, not the one before.
			var issueTick = response.RootElement.GetProperty("tick").GetInt32();
			Assert.That(response.RootElement.GetProperty("min_effect_tick").GetInt32(), Is.EqualTo(issueTick + 1));

			Assert.That(hw.TickUntil(() => unit.Location == destination, 500), Is.Not.Null, "The unit never reached the destination.");
		}

		[TestCase(TestName = "A Move order with target_actor is accepted and returns the ack")]
		public void MoveToTargetActorIsAccepted()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			var friend = hw.CreateActor("e1", hw.FindClearArea(new CPos(60, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			var line = $"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"target_actor\":{friend.ActorID}}}";
			using var response = AgentStateDumpTest.RequestWithTicks(hw, line);
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(response.RootElement.GetProperty("cmd").GetString(), Is.EqualTo("order"));
		}

		[TestCase(TestName = "An order with no target is accepted and returns the ack")]
		public void UntargetedOrderIsAccepted()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.Request($"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Stop\"}}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(response.RootElement.GetProperty("min_effect_tick").GetInt32(), Is.EqualTo(hw.World.WorldTick + 1));
		}

		[TestCase(TestName = "An order for an unknown actor returns the unknown-actor error")]
		public void UnknownActorIsAnError()
		{
			using var hw = HeadlessWorld.Create(1);

			using var response = AgentStateDumpTest.Request("{\"cmd\":\"order\",\"actor\":999999,\"order\":\"Move\",\"cell\":[1,1]}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("cmd").GetString(), Is.EqualTo("order"));
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo("Unknown actor 999999"));
		}

		[TestCase(TestName = "An order for a dead actor returns the dead-actor error, distinct from unknown")]
		public void DeadActorIsAnError()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			unit.Kill(unit);
			Assert.That(unit.IsDead, Is.True);

			using var response = AgentStateDumpTest.Request($"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"cell\":[1,1]}}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo($"Actor {unit.ActorID} is dead"));
		}

		[TestCase(TestName = "An order whose target_actor is dead returns the dead-actor error")]
		public void DeadTargetActorIsAnError()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			var target = hw.CreateActor("e1", hw.FindClearArea(new CPos(60, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			target.Kill(target);

			using var response = AgentStateDumpTest.Request($"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"target_actor\":{target.ActorID}}}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo($"Actor {target.ActorID} is dead"));
		}

		[TestCase(TestName = "An order for an actor the local player does not own is refused with an explicit error")]
		public void NotOwnedActorIsAnError()
		{
			using var hw = HeadlessWorld.Create(1);
			var enemy = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.EnemyPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.Request($"{{\"cmd\":\"order\",\"actor\":{enemy.ActorID},\"order\":\"Move\",\"cell\":[1,1]}}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo($"Actor {enemy.ActorID} is not owned by the local player"));
		}

		[TestCase(TestName = "An order with more than one target is rejected before it reaches the game thread")]
		public void TwoTargetsAreRejected()
		{
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"order\",\"actor\":1,\"order\":\"Move\",\"cell\":[1,1],\"target_actor\":2}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo("give at most one of cell, pos and target_actor"));
		}

		[TestCase(TestName = "An order without an order name is rejected")]
		public void MissingOrderNameIsRejected()
		{
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"order\",\"actor\":1}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo("order must be a non-empty string"));
		}

		[TestCase(TestName = "A queued value that is not a boolean is rejected, not read as false")]
		public void NonBooleanQueuedIsRejected()
		{
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"order\",\"actor\":1,\"order\":\"Move\",\"cell\":[1,1],\"queued\":\"true\"}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo("queued must be a boolean"));
		}

		[TestCase(TestName = "A StartProduction order with item and extra_data queues that many of the item")]
		public void StartProductionWithItemQueuesTheCount()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);

			var line = $"{{\"cmd\":\"order\",\"actor\":{hw.LocalPlayer.PlayerActor.ActorID},\"order\":\"StartProduction\",\"item\":\"powr\",\"extra_data\":2}}";
			using (var response = AgentStateDumpTest.Request(line))
				Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.True);

			hw.Tick(5);

			using var production = AgentStateDumpTest.Request("{\"cmd\":\"production\"}");
			var queue = AgentProductionTest.QueueOf(production.RootElement, "Building");
			Assert.That(queue.GetProperty("items").EnumerateArray().Count(i => i.GetProperty("name").GetString() == "powr"), Is.EqualTo(2));
		}

		[TestCase(StartLine + "\"item\":5}", "item must be a string", TestName = "A non-string item is rejected")]
		[TestCase(StartLine + "\"extra_data\":-1}", "extra_data must be an unsigned integer", TestName = "A negative extra_data is rejected")]
		[TestCase(StartLine + "\"extra_data\":1.5}", "extra_data must be an unsigned integer", TestName = "A fractional extra_data is rejected")]
		[TestCase(StartLine + "\"extra_data\":\"2\"}", "extra_data must be an unsigned integer", TestName = "A string extra_data is rejected")]
		[TestCase(PlaceLine + "\"extra_location\":[1]}", "extra_location must be [x, y]", TestName = "An extra_location with one value is rejected")]
		[TestCase(PlaceLine + "\"extra_location\":[1,\"x\"]}", "extra_location must be [x, y]", TestName = "An extra_location with a non-integer is rejected")]
		[TestCase(PlaceLine + "\"extra_location\":\"1,2\"}", "extra_location must be [x, y]", TestName = "An extra_location that is not an array is rejected")]
		public void MalformedExtraFieldsAreRejected(string line, string error)
		{
			using var response = AgentStateDumpTest.Request(line);
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("cmd").GetString(), Is.EqualTo("order"));
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo(error));
		}

		static IEnumerable<string> Keys(JsonElement element)
		{
			return element.EnumerateObject().Select(p => p.Name);
		}
	}
}
