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
	/// The build command over the real socket line path (see AgentStateDumpTest for the request helper), with the queue read
	/// back through the production command. The rules come from mods/ra, as in AgentProductionTest: POWR costs 300 and is
	/// buildable from a Construction Yard (FACT), PROC needs a power plant, and 1TNK needs a War Factory, which the test
	/// world does not have.
	/// </summary>
	[TestFixture]
	sealed class AgentBuildTest
	{
		static readonly string[] SuccessKeys = ["ok", "cmd", "actor", "queue_type", "item", "count", "tick", "min_effect_tick"];
		static readonly string[] RefusedKeys = ["ok", "cmd", "error", "available"];

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

		[TestCase(TestName = "A build queues the item as a StartProduction order, and the production list shows it in the queue")]
		public void BuildQueuesTheItem()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);

			using (var response = AgentStateDumpTest.Request("{\"cmd\":\"build\",\"item\":\"powr\",\"count\":2}"))
			{
				var root = response.RootElement;
				Assert.That(AgentProductionTest.Keys(root), Is.EquivalentTo(SuccessKeys));
				Assert.That(root.GetProperty("ok").GetBoolean(), Is.True);
				Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("build"));
				Assert.That(root.GetProperty("actor").GetUInt32(), Is.EqualTo(hw.LocalPlayer.PlayerActor.ActorID), "The Building queue sits on the player actor");
				Assert.That(root.GetProperty("queue_type").GetString(), Is.EqualTo("Building"));
				Assert.That(root.GetProperty("item").GetString(), Is.EqualTo("powr"));
				Assert.That(root.GetProperty("count").GetInt32(), Is.EqualTo(2));
			}

			hw.Tick(5);

			using var production = AgentStateDumpTest.Request("{\"cmd\":\"production\"}");
			Assert.That(PowrItems(production.RootElement), Has.Length.EqualTo(2));
		}

		[TestCase(TestName = "A build without a count queues one")]
		public void BuildWithoutCountQueuesOne()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);

			using (var response = AgentStateDumpTest.Request("{\"cmd\":\"build\",\"item\":\"powr\"}"))
				Assert.That(response.RootElement.GetProperty("count").GetInt32(), Is.EqualTo(1));

			hw.Tick(5);

			using var production = AgentStateDumpTest.Request("{\"cmd\":\"production\"}");
			Assert.That(PowrItems(production.RootElement), Has.Length.EqualTo(1));
		}

		[TestCase(TestName = "An unknown item is refused with its error, and the items build accepts now")]
		public void UnknownItemIsRefused()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.Request("{\"cmd\":\"build\",\"item\":\"nope\"}");
			var root = response.RootElement;
			Assert.That(AgentProductionTest.Keys(root), Is.EquivalentTo(RefusedKeys));
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("error").GetString(), Is.EqualTo("unknown item 'nope'"));

			var available = Available(root);
			Assert.That(available, Does.Contain("powr"));
			Assert.That(available, Does.Not.Contain("nope"));
		}

		[TestCase(TestName = "An item whose prerequisites are not met is refused, and is left out of the available items")]
		public void UnmetPrerequisitesAreRefused()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.Request("{\"cmd\":\"build\",\"item\":\"proc\"}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("error").GetString(), Is.EqualTo("'proc' is not buildable now: its prerequisites are not met"));

			var available = Available(root);
			Assert.That(available, Does.Contain("powr"));
			Assert.That(available, Does.Not.Contain("proc"));
		}

		[TestCase(TestName = "An item with no queue that builds it is refused, and nothing is queued")]
		public void ItemWithNoQueueIsRefused()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.Request("{\"cmd\":\"build\",\"item\":\"1tnk\"}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("error").GetString(), Does.StartWith("no queue builds '1tnk'"));
			Assert.That(root.GetProperty("available").GetArrayLength(), Is.GreaterThan(0));
		}

		[TestCase(TestName = "An item the player cannot afford is refused with the cost and cash, and nothing is queued")]
		public void UnaffordableItemIsRefused()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);
			AgentProductionTest.SetLocalCash(hw, 0);

			using (var response = AgentStateDumpTest.Request("{\"cmd\":\"build\",\"item\":\"powr\"}"))
			{
				var root = response.RootElement;
				Assert.That(root.GetProperty("ok").GetBoolean(), Is.False);
				Assert.That(root.GetProperty("error").GetString(), Is.EqualTo("not enough cash for 'powr': it costs 300, and you have 0"));
				Assert.That(Available(root), Is.Empty, "Nothing is affordable with no cash");
			}

			hw.Tick(5);

			using var production = AgentStateDumpTest.Request("{\"cmd\":\"production\"}");
			Assert.That(PowrItems(production.RootElement), Is.Empty);
		}

		static string[] Available(JsonElement root)
		{
			return root.GetProperty("available").EnumerateArray().Select(a => a.GetString()).ToArray();
		}

		static JsonElement[] PowrItems(JsonElement root)
		{
			return AgentProductionTest.QueueOf(root, "Building").GetProperty("items").EnumerateArray()
				.Where(i => i.GetProperty("name").GetString() == "powr")
				.ToArray();
		}
	}
}
