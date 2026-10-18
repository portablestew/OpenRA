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
		static readonly string[] SuccessKeys = ["ok", "cmd", "actor", "queue_type", "item", "count", "tick", "min_effect_tick", "ready", "eta_ticks", "blocked_by"];
		static readonly string[] RefusedKeys = ["ok", "cmd", "error", "available", "missing_prerequisites", "ready", "eta_ticks", "blocked_by", "hint"];

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
			Assert.That(root.GetProperty("error").GetString(), Is.EqualTo("'proc' is not buildable now: missing prerequisites: anypower"));
			Assert.That(Missing(root), Is.EqualTo(new[] { "anypower" }));

			var available = Available(root);
			Assert.That(available, Does.Contain("powr"));
			Assert.That(available, Does.Not.Contain("proc"));
		}

		[TestCase(TestName = "A tech item with two missing prerequisites is refused, and the refusal names both")]
		public void TwoMissingPrerequisitesAreBothNamed()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);

			// ATEK needs a War Factory (weap) and a Radar Dome (dome). Its faction and tech-level terms are also listed, as the engine reports them.
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"build\",\"item\":\"atek\"}");
			var root = response.RootElement;
			Assert.That(AgentProductionTest.Keys(root), Is.EquivalentTo(RefusedKeys));
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("error").GetString(), Does.Contain("weap").And.Contains("dome"));
			Assert.That(Missing(root), Has.Member("weap").And.Member("dome"));
			Assert.That(root.GetProperty("ready").ValueKind, Is.EqualTo(JsonValueKind.Null), "No ATEK is queued, so there is no ready state");
		}

		[TestCase(TestName = "A build reports ready:false and an ETA for the copy it queues, counting the copies ahead of it; behind a finished head there is no ETA")]
		public void ReadyAndEtaDescribeTheCopyThisBuildQueues()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);

			// Nothing is queued, so the copy this build queues is the only one ahead of its own build time: not ready, with an ETA.
			int firstEta;
			using (var first = AgentStateDumpTest.Request("{\"cmd\":\"build\",\"item\":\"powr\"}"))
			{
				var root = first.RootElement;
				Assert.That(root.GetProperty("ok").GetBoolean(), Is.True);
				Assert.That(root.GetProperty("ready").ValueKind, Is.EqualTo(JsonValueKind.False), "A copy queued now is not done yet");
				firstEta = root.GetProperty("eta_ticks").GetInt32();
				Assert.That(firstEta, Is.GreaterThan(0), "The new copy needs its build time");
				Assert.That(root.GetProperty("blocked_by").ValueKind, Is.EqualTo(JsonValueKind.Null), "Nothing is finished at the head, so nothing blocks");
			}

			Assert.That(hw.TickUntil(PowrBuilding, 100), Is.Not.Null, "The POWR never started building.");

			// The first copy is in progress and ahead of this one, so this copy's ETA counts the first copy's remaining time too.
			using (var second = AgentStateDumpTest.Request("{\"cmd\":\"build\",\"item\":\"powr\"}"))
			{
				var root = second.RootElement;
				Assert.That(root.GetProperty("ready").ValueKind, Is.EqualTo(JsonValueKind.False));
				Assert.That(root.GetProperty("eta_ticks").GetInt32(), Is.GreaterThan(firstEta), "The copy ahead of it is still building, so the ETA is longer");
			}

			Assert.That(hw.TickUntil(PowrFinished, 3000), Is.Not.Null, "The POWR never finished building.");

			// The first copy is done and holds the queue until it is placed, so a copy queued behind it has no ETA; blocked_by names the head.
			using var blocked = AgentStateDumpTest.Request("{\"cmd\":\"build\",\"item\":\"powr\"}");
			Assert.That(blocked.RootElement.GetProperty("ready").ValueKind, Is.EqualTo(JsonValueKind.False));
			Assert.That(blocked.RootElement.GetProperty("eta_ticks").ValueKind, Is.EqualTo(JsonValueKind.Null), "A finished head holds the queue, so the ETA depends on placement");
			Assert.That(blocked.RootElement.GetProperty("blocked_by").GetString(), Is.EqualTo("powr"));
		}

		[TestCase(TestName = "A finished head blocks the queue: a build into it names the blocker, and the queue view names it too")]
		public void FinishedHeadNamesTheBlocker()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);

			using (var first = AgentStateDumpTest.Request("{\"cmd\":\"build\",\"item\":\"powr\"}"))
				Assert.That(first.RootElement.GetProperty("ok").GetBoolean(), Is.True);

			// The POWR finishes and waits for placement at the head. Nothing places it, so it holds the queue.
			Assert.That(hw.TickUntil(PowrFinished, 3000), Is.Not.Null, "The POWR never finished building.");

			// A second POWR joins behind the finished head, so the response names the head as its blocker.
			using (var second = AgentStateDumpTest.Request("{\"cmd\":\"build\",\"item\":\"powr\"}"))
			{
				var root = second.RootElement;
				Assert.That(root.GetProperty("ok").GetBoolean(), Is.True);
				Assert.That(root.GetProperty("blocked_by").GetString(), Is.EqualTo("powr"));
			}

			hw.Tick(5);

			using var production = AgentStateDumpTest.Request("{\"cmd\":\"production\"}");
			Assert.That(AgentProductionTest.QueueOf(production.RootElement, "Building").GetProperty("blocker").GetString(), Is.EqualTo("powr"));
		}

		[TestCase(TestName = "An item with no queue that builds it is refused, and the available items are listed")]
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

		static string[] Missing(JsonElement root)
		{
			return root.GetProperty("missing_prerequisites").EnumerateArray().Select(a => a.GetString()).ToArray();
		}

		/// <summary>True when a POWR in the building queue is the head of the queue and in progress, read through the production command.</summary>
		internal static bool PowrBuilding()
		{
			using var production = AgentStateDumpTest.Request("{\"cmd\":\"production\"}");
			return PowrItems(production.RootElement).Any(i => i.GetProperty("state").GetString() == "building");
		}

		/// <summary>True when a POWR in the building queue is finished and waits for placement, read through the production command.</summary>
		internal static bool PowrFinished()
		{
			using var production = AgentStateDumpTest.Request("{\"cmd\":\"production\"}");
			return PowrItems(production.RootElement).Any(i => i.GetProperty("state").GetString() == "done");
		}

		internal static JsonElement[] PowrItems(JsonElement root)
		{
			return AgentProductionTest.QueueOf(root, "Building").GetProperty("items").EnumerateArray()
				.Where(i => i.GetProperty("name").GetString() == "powr")
				.ToArray();
		}
	}
}
