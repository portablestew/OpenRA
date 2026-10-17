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
	/// The production command over the real socket line path (see AgentStateDumpTest for the request helper). The rules come
	/// from mods/ra: POWR costs 300, provides 100 power and needs only a Building queue, which a Construction Yard (FACT)
	/// gives. PROC needs anypower, which a POWR provides. This project cannot reference OpenRA.Mods.Common, so the
	/// PlayerResources trait is reached by reflection.
	/// </summary>
	[TestFixture]
	sealed class AgentProductionTest
	{
		static readonly string[] ProductionKeys = ["ok", "cmd", "player", "cash", "resources", "power", "queues"];
		static readonly string[] QueueKeys = ["actor", "producers", "queue_type", "enabled", "options", "items", "blocker"];
		static readonly string[] OptionKeys = ["name", "cost", "power_amount", "prerequisites", "buildable", "can_afford"];
		static readonly string[] ItemKeys = ["name", "cost", "remaining_cost", "progress", "state"];
		static readonly string[] PowerKeys = ["provided", "drained", "excess", "state"];

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

		[TestCase(TestName = "The production line carries exactly the schema v1 keys, at each level")]
		public void ProductionLineMatchesSchema()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);
			hw.IssueOrder(Order.StartProduction(hw.LocalPlayer.PlayerActor, "powr", 1));
			hw.Tick(3);

			using var response = AgentStateDumpTest.Request("{\"cmd\":\"production\"}");
			var root = response.RootElement;
			Assert.That(Keys(root), Is.EquivalentTo(ProductionKeys));
			Assert.That(Keys(root.GetProperty("power")), Is.EquivalentTo(PowerKeys));

			var queue = QueueOf(root, "Building");
			Assert.That(Keys(queue), Is.EquivalentTo(QueueKeys));
			Assert.That(Keys(queue.GetProperty("options")[0]), Is.EquivalentTo(OptionKeys));
			Assert.That(Keys(queue.GetProperty("items")[0]), Is.EquivalentTo(ItemKeys));
		}

		[TestCase(TestName = "POWR is buildable from a Construction Yard, with its cost, power amount and prerequisites")]
		public void PowerPlantIsBuildableFromAConstructionYard()
		{
			using var hw = HeadlessWorld.Create(1);
			var fact = hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.Request("{\"cmd\":\"production\"}");
			var queue = QueueOf(response.RootElement, "Building");
			Assert.That(queue.GetProperty("queue_type").GetString(), Is.EqualTo("Building"));
			Assert.That(queue.GetProperty("producers").EnumerateArray().Select(p => p.GetUInt32()), Does.Contain(fact.ActorID));
			Assert.That(queue.GetProperty("enabled").GetBoolean(), Is.True);

			var powr = Option(queue, "powr");
			Assert.That(powr.GetProperty("buildable").GetBoolean(), Is.True);
			Assert.That(powr.GetProperty("cost").GetInt32(), Is.EqualTo(300), "POWR Valued Cost, mods/ra/rules/structures.yaml");
			Assert.That(powr.GetProperty("power_amount").GetInt32(), Is.EqualTo(100), "POWR Power Amount, mods/ra/rules/structures.yaml");
			Assert.That(powr.GetProperty("can_afford").GetBoolean(), Is.True);
			Assert.That(powr.GetProperty("prerequisites").EnumerateArray().Select(p => p.GetString()), Does.Contain("~techlevel.infonly"));
		}

		[TestCase(TestName = "PROC is listed but not buildable until a power plant provides anypower, then it is buildable")]
		public void RefineryIsFlaggedUntilAPowerPlantExists()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);

			using (var before = AgentStateDumpTest.Request("{\"cmd\":\"production\"}"))
			{
				var proc = Option(QueueOf(before.RootElement, "Building"), "proc");
				Assert.That(proc.GetProperty("buildable").GetBoolean(), Is.False);
			}

			hw.CreateActor("powr", hw.FindClearArea(new CPos(60, 60), 2, 2), hw.LocalPlayer);
			hw.Tick(1);

			using var after = AgentStateDumpTest.Request("{\"cmd\":\"production\"}");
			Assert.That(Option(QueueOf(after.RootElement, "Building"), "proc").GetProperty("buildable").GetBoolean(), Is.True);
		}

		[TestCase(TestName = "An item the player cannot afford is buildable but flagged can_afford false, and cash is reported")]
		public void UnaffordableItemIsFlagged()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);
			SetLocalCash(hw, 0);

			using var response = AgentStateDumpTest.Request("{\"cmd\":\"production\"}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("cash").GetInt32(), Is.EqualTo(0));
			Assert.That(root.GetProperty("resources").GetInt32(), Is.EqualTo(0));

			var powr = Option(QueueOf(root, "Building"), "powr");
			Assert.That(powr.GetProperty("buildable").GetBoolean(), Is.True);
			Assert.That(powr.GetProperty("can_afford").GetBoolean(), Is.False);
		}

		[TestCase(TestName = "A queued item reports its state, progress and remaining cost, and cash and power match the state line")]
		public void QueuedItemReportsProgress()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);
			hw.IssueOrder(Order.StartProduction(hw.LocalPlayer.PlayerActor, "powr", 1));
			hw.Tick(10);

			using var production = AgentStateDumpTest.Request("{\"cmd\":\"production\"}");
			using var state = AgentStateDumpTest.Request("{\"cmd\":\"state\"}");
			var item = QueueOf(production.RootElement, "Building").GetProperty("items")[0];

			Assert.That(item.GetProperty("name").GetString(), Is.EqualTo("powr"));
			Assert.That(item.GetProperty("state").GetString(), Is.EqualTo("building"));
			Assert.That(item.GetProperty("progress").GetInt32(), Is.InRange(1, 99));
			Assert.That(item.GetProperty("remaining_cost").GetInt32(), Is.LessThan(300));

			// Both requests read the same tick, so the production line and the state line must agree on cash and power.
			var local = LocalPlayer(state.RootElement);
			Assert.That(production.RootElement.GetProperty("cash").GetInt32(), Is.EqualTo(local.GetProperty("cash").GetInt32()));
			Assert.That(production.RootElement.GetProperty("power").GetProperty("provided").GetInt32(),
				Is.EqualTo(local.GetProperty("power").GetProperty("provided").GetInt32()));
		}

		internal static IEnumerable<string> Keys(JsonElement element)
		{
			return element.EnumerateObject().Select(p => p.Name);
		}

		/// <summary>The queue of one type. The RA queues sit on the player actor, so the type is the key, not the actor.</summary>
		internal static JsonElement QueueOf(JsonElement root, string queueType)
		{
			return root.GetProperty("queues").EnumerateArray().Single(q => q.GetProperty("queue_type").GetString() == queueType);
		}

		internal static JsonElement Option(JsonElement queue, string name)
		{
			var option = queue.GetProperty("options").EnumerateArray().FirstOrDefault(o => o.GetProperty("name").GetString() == name);
			Assert.That(option.ValueKind, Is.Not.EqualTo(JsonValueKind.Undefined), $"The queue does not list {name}.");
			return option;
		}

		static JsonElement LocalPlayer(JsonElement root)
		{
			var local = root.GetProperty("local_player").GetString();
			return root.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("name").GetString() == local);
		}

		/// <summary>Sets the local player's cash and resources. PlayerResources is reached by reflection, as the test project cannot reference the mod.</summary>
		internal static void SetLocalCash(HeadlessWorld hw, int cash)
		{
			var type = HeadlessMod.ModData.ObjectCreator.FindType("Traits.PlayerResources");
			var resourcesMethod = typeof(Actor).GetMethods().Single(m => m.Name == "TraitOrDefault" && m.GetParameters().Length == 0);
			var resources = resourcesMethod.MakeGenericMethod(type).Invoke(hw.LocalPlayer.PlayerActor, null);

			type.GetField("Cash").SetValue(resources, cash);
			type.GetField("Resources").SetValue(resources, 0);
		}
	}
}
