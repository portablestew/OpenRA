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
using System.Text.Json;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// Group orders (P1.14) over the real socket line path. A group is one engine Order with GroupedActors: the engine splits it per
	/// actor on resolve, so each actor reports its own outcome. Each request here is a Move, so the reply waits for every actor.
	/// </summary>
	[TestFixture]
	sealed class AgentOrderGroupTest
	{
		const string HelpHint = "see {\"cmd\":\"help\"}";
		const string ExactlyOne = "give exactly one of actor, actors and type";

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

		[TestCase(TestName = "Two e1 selected by type are both ordered, both move, and both are selected")]
		public void TwoOfOneTypeAreBothOrdered()
		{
			using var hw = HeadlessWorld.Create(1);
			var a = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			var b = hw.CreateActor("e1", hw.FindClearArea(new CPos(60, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var startA = a.Location;
			var startB = b.Location;
			var destination = hw.FindClearArea(new CPos(50, 56), 1, 1, 4);

			var line = $"{{\"cmd\":\"order\",\"type\":\"e1\",\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}]}}";
			using var response = AgentStateDumpTest.RequestWithTicks(hw, line);
			var root = response.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
			Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("order"));
			Assert.That(OrderedIds(root), Is.EquivalentTo([a.ActorID, b.ActorID]));
			Assert.That(root.GetProperty("matched").GetInt32(), Is.EqualTo(2));

			var results = root.GetProperty("results").EnumerateArray().ToArray();
			Assert.That(results.Length, Is.EqualTo(2));
			foreach (var result in results)
			{
				Assert.That(result.GetProperty("settled").GetBoolean(), Is.True, result.ToString());
				Assert.That(result.GetProperty("valid").GetBoolean(), Is.True, result.ToString());
			}

			Assert.That(hw.TickUntil(() => a.Location != startA && b.Location != startB, 500), Is.Not.Null, "Both e1 should leave their start cells.");
			Assert.That(hw.World.Selection.Actors, Is.EquivalentTo([a, b]));
		}

		[TestCase(TestName = "A type pattern of e. orders every infantry actor and no vehicle")]
		public void InfantryPatternOrdersAllInfantry()
		{
			using var hw = HeadlessWorld.Create(1);
			var e1 = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			var e3 = hw.CreateActor("e3", hw.FindClearArea(new CPos(60, 50), 1, 1), hw.LocalPlayer);
			var tank = hw.CreateActor("2tnk", hw.FindClearArea(new CPos(70, 50), 2, 2), hw.LocalPlayer);
			hw.Tick(1);
			var destination = hw.FindClearArea(new CPos(50, 56), 1, 1, 4);

			var line = $"{{\"cmd\":\"order\",\"type\":\"e.\",\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}]}}";
			using var response = AgentStateDumpTest.RequestWithTicks(hw, line);
			var root = response.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
			Assert.That(OrderedIds(root), Is.EquivalentTo([e1.ActorID, e3.ActorID]));
			Assert.That(OrderedIds(root), Does.Not.Contain(tank.ActorID));
			Assert.That(root.GetProperty("matched").GetInt32(), Is.EqualTo(2));
		}

		[TestCase(TestName = "An actors list orders exactly the listed actors")]
		public void ActorsListOrdersTheListedActors()
		{
			using var hw = HeadlessWorld.Create(1);
			var a = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			var b = hw.CreateActor("e1", hw.FindClearArea(new CPos(60, 50), 1, 1), hw.LocalPlayer);
			hw.CreateActor("e1", hw.FindClearArea(new CPos(70, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var destination = hw.FindClearArea(new CPos(50, 56), 1, 1, 4);

			var line = $"{{\"cmd\":\"order\",\"actors\":[{a.ActorID},{b.ActorID}],\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}]}}";
			using var response = AgentStateDumpTest.RequestWithTicks(hw, line);
			var root = response.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
			Assert.That(OrderedIds(root), Is.EquivalentTo([a.ActorID, b.ActorID]));
		}

		[TestCase(TestName = "A group with an actor the local player does not own is refused, and nothing moves")]
		public void UnownedActorInListRefusesTheGroup()
		{
			using var hw = HeadlessWorld.Create(1);
			var mine = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			var enemy = hw.CreateActor("e1", hw.FindClearArea(new CPos(60, 50), 1, 1), hw.EnemyPlayer);
			hw.Tick(1);
			var start = mine.Location;
			var destination = hw.FindClearArea(new CPos(50, 56), 1, 1, 4);

			var line = $"{{\"cmd\":\"order\",\"actors\":[{mine.ActorID},{enemy.ActorID}],\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}]}}";
			using var response = AgentStateDumpTest.Request(line);
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo($"Actor {enemy.ActorID} is not owned by the local player"));

			hw.Tick(10);
			Assert.That(mine.Location, Is.EqualTo(start));
		}

		[TestCase(TestName = "A group with both actor and type is rejected, with the help hint")]
		public void ActorPlusTypeIsRejected()
		{
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"order\",\"actor\":1,\"type\":\"e1\",\"order\":\"Move\",\"cell\":[1,1]}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo(ExactlyOne));
			Assert.That(response.RootElement.GetProperty("hint").GetString(), Is.EqualTo(HelpHint));
		}

		[TestCase(TestName = "An order with no selector is rejected, with the help hint")]
		public void NoSelectorIsRejected()
		{
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"order\",\"order\":\"Move\",\"cell\":[1,1]}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo(ExactlyOne));
			Assert.That(response.RootElement.GetProperty("hint").GetString(), Is.EqualTo(HelpHint));
		}

		[TestCase(TestName = "A type pattern that is not a regular expression is rejected, with the help hint")]
		public void InvalidRegexIsRejected()
		{
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"order\",\"type\":\"e(\",\"order\":\"Move\",\"cell\":[1,1]}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Does.StartWith("type is not a valid regular expression: "));
			Assert.That(response.RootElement.GetProperty("hint").GetString(), Is.EqualTo(HelpHint));
		}

		[TestCase(TestName = "A type pattern that matches no owned actor is an error, not an empty order")]
		public void EmptyMatchIsAnError()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var start = unit.Location;

			using var response = AgentStateDumpTest.Request("{\"cmd\":\"order\",\"type\":\"zzz\",\"order\":\"Move\",\"cell\":[1,1]}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo("no owned actor's type matches \"zzz\""));
			Assert.That(response.RootElement.GetProperty("hint").GetString(), Is.EqualTo(HelpHint));

			hw.Tick(5);
			Assert.That(unit.Location, Is.EqualTo(start));
		}

		[TestCase(TestName = "await:false is refused for a group, since a job holds one actor's await")]
		public void GroupAwaitFalseIsRejected()
		{
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"order\",\"type\":\"e1\",\"order\":\"Move\",\"cell\":[1,1],\"await\":false}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo("await:false applies only to a single actor"));
		}

		static uint[] OrderedIds(JsonElement root)
		{
			return root.GetProperty("ordered").EnumerateArray().Select(e => e.GetUInt32()).ToArray();
		}
	}
}
