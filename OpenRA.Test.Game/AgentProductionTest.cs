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
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
		static readonly string[] ProductionKeys = ["ok", "cmd", "player", "funds", "cash_funds", "resource_funds", "power", "queues"];
		static readonly string[] QueueKeys = ["actor", "producers", "queue_type", "enabled", "options", "changed", "items", "blocker"];
		static readonly string[] OptionKeys = ["name", "cost", "power_amount", "prerequisites", "buildable", "can_afford"];
		static readonly string[] ItemKeys = ["name", "cost", "remaining_cost", "progress", "state"];
		static readonly string[] PowerKeys = ["provided", "drained", "excess", "state"];

		readonly List<object> connections = [];
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

		[TestCase(TestName = "production_tree returns the full option records for every queue, and the banner lists the command")]
		public void ProductionTreeReturnsFullOptionsAndIsInTheBanner()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);

			using var tree = AgentStateDumpTest.Request("{\"cmd\":\"production_tree\"}");
			var root = tree.RootElement;
			Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("production_tree"));
			Assert.That(Keys(root), Is.EquivalentTo(ProductionKeys));

			var queue = QueueOf(root, "Building");
			var options = queue.GetProperty("options").EnumerateArray().ToArray();
			Assert.That(options.Length, Is.GreaterThan(1), "The Building queue should list more than one option.");
			foreach (var option in options)
				Assert.That(Keys(option), Is.EquivalentTo(OptionKeys));

			var powr = Option(queue, "powr");
			Assert.That(powr.GetProperty("buildable").GetBoolean(), Is.True);
			Assert.That(powr.GetProperty("cost").GetInt32(), Is.GreaterThan(0));

			// With no connection, production lists every option, so its queues match production_tree's byte for byte.
			using var production = AgentStateDumpTest.Request("{\"cmd\":\"production\"}");
			Assert.That(root.GetProperty("queues").GetRawText(), Is.EqualTo(production.RootElement.GetProperty("queues").GetRawText()));

			var commands = (string[])HeadlessMod.ModData.ObjectCreator.FindType("AgentSocket.AgentBanner")
				.GetProperty("CommandNames").GetValue(null);
			Assert.That(commands, Does.Contain("production_tree"), "The banner should list production_tree.");
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

		[TestCase(TestName = "An item the player cannot afford is buildable but flagged can_afford false, and funds and the pools are reported")]
		public void UnaffordableItemIsFlagged()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);
			SetLocalCash(hw, 0);

			using var response = AgentStateDumpTest.Request("{\"cmd\":\"production\"}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("funds").GetInt32(), Is.EqualTo(0));
			Assert.That(root.GetProperty("cash_funds").GetInt32(), Is.EqualTo(0));
			Assert.That(root.GetProperty("resource_funds").GetInt32(), Is.EqualTo(0));

			var powr = Option(QueueOf(root, "Building"), "powr");
			Assert.That(powr.GetProperty("buildable").GetBoolean(), Is.True);
			Assert.That(powr.GetProperty("can_afford").GetBoolean(), Is.False);
		}

		[TestCase(TestName = "funds is cash plus resources in state and production, and can_afford counts both pools")]
		public void FundsAddsCashAndResources()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);

			// 200 cash alone cannot pay POWR's 300; 150 stored resources makes 350.
			SetLocalCashAndResources(hw, 200, 150);

			using var production = AgentStateDumpTest.Request("{\"cmd\":\"production\"}");
			Assert.That(production.RootElement.GetProperty("funds").GetInt32(), Is.EqualTo(350));
			Assert.That(production.RootElement.GetProperty("cash_funds").GetInt32(), Is.EqualTo(200));
			Assert.That(production.RootElement.GetProperty("resource_funds").GetInt32(), Is.EqualTo(150));
			Assert.That(Option(QueueOf(production.RootElement, "Building"), "powr").GetProperty("can_afford").GetBoolean(), Is.True,
				"can_afford counts cash_funds + resource_funds.");

			using var state = AgentStateDumpTest.State();
			var local = LocalPlayer(state.RootElement);
			Assert.That(local.GetProperty("funds").GetInt32(), Is.EqualTo(350));
			Assert.That(local.GetProperty("cash_funds").GetInt32(), Is.EqualTo(200));
			Assert.That(local.GetProperty("resource_funds").GetInt32(), Is.EqualTo(150));
		}

		[TestCase(TestName = "A queued item reports its state, progress and remaining cost, and funds, cash_funds and power match the state line")]
		public void QueuedItemReportsProgress()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);
			hw.IssueOrder(Order.StartProduction(hw.LocalPlayer.PlayerActor, "powr", 1));
			hw.Tick(10);

			using var production = AgentStateDumpTest.Request("{\"cmd\":\"production\"}");
			using var state = AgentStateDumpTest.State();
			var item = QueueOf(production.RootElement, "Building").GetProperty("items")[0];

			Assert.That(item.GetProperty("name").GetString(), Is.EqualTo("powr"));
			Assert.That(item.GetProperty("state").GetString(), Is.EqualTo("building"));
			Assert.That(item.GetProperty("progress").GetInt32(), Is.InRange(1, 99));
			Assert.That(item.GetProperty("remaining_cost").GetInt32(), Is.LessThan(300));

			// Both requests read the same tick, so the production line and the state line must agree on funds, cash_funds and power.
			var local = LocalPlayer(state.RootElement);
			Assert.That(production.RootElement.GetProperty("funds").GetInt32(), Is.EqualTo(local.GetProperty("funds").GetInt32()));
			Assert.That(production.RootElement.GetProperty("cash_funds").GetInt32(), Is.EqualTo(local.GetProperty("cash_funds").GetInt32()));
			Assert.That(production.RootElement.GetProperty("power").GetProperty("provided").GetInt32(),
				Is.EqualTo(local.GetProperty("power").GetProperty("provided").GetInt32()));
		}

		[TestCase(TestName = "A connection's first production read lists every queue's options, and an immediate second read lists none")]
		public void SecondReadOmitsUnchangedOptions()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);
			var connection = NewConnection();

			using var first = ConnectionRequest(connection, "{\"cmd\":\"production\"}");
			var building = QueueOf(first.RootElement, "Building");
			Assert.That(building.GetProperty("changed").GetBoolean(), Is.True);
			Assert.That(building.GetProperty("options").GetArrayLength(), Is.GreaterThan(1));

			using var second = ConnectionRequest(connection, "{\"cmd\":\"production\"}");
			var again = QueueOf(second.RootElement, "Building");
			Assert.That(again.GetProperty("changed").GetBoolean(), Is.False);
			Assert.That(again.TryGetProperty("options", out _), Is.False, "An unchanged queue leaves its options out.");
			Assert.That(Keys(again), Is.EquivalentTo(QueueKeys.Where(k => k != "options")));
		}

		[TestCase(TestName = "Once a prerequisite is met the option set changes, so the options come back with changed true")]
		public void OptionsReturnWhenAPrerequisiteIsMet()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);
			var connection = NewConnection();

			using var before = ConnectionRequest(connection, "{\"cmd\":\"production\"}");
			Assert.That(Option(QueueOf(before.RootElement, "Building"), "proc").GetProperty("buildable").GetBoolean(), Is.False);

			hw.CreateActor("powr", hw.FindClearArea(new CPos(60, 60), 2, 2), hw.LocalPlayer);
			hw.Tick(1);

			using var after = ConnectionRequest(connection, "{\"cmd\":\"production\"}");
			var queue = QueueOf(after.RootElement, "Building");
			Assert.That(queue.GetProperty("changed").GetBoolean(), Is.True);
			Assert.That(Option(queue, "proc").GetProperty("buildable").GetBoolean(), Is.True);
		}

		[TestCase(TestName = "A world change clears a connection's seen options, so the next production read lists every queue again")]
		public void WorldChangeClearsTheSeenOptions()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);
			var connection = NewConnection();

			using (var first = ConnectionRequest(connection, "{\"cmd\":\"production\"}"))
				Assert.That(QueueOf(first.RootElement, "Building").GetProperty("changed").GetBoolean(), Is.True);

			using (var second = ConnectionRequest(connection, "{\"cmd\":\"production\"}"))
				Assert.That(QueueOf(second.RootElement, "Building").GetProperty("changed").GetBoolean(), Is.False);

			// An undo runs the real WorldAdopted hook, so this proves the hook clears the seen options, not only DiscardWaiting.
			var bridgeType = ModType("AgentWorldBridge");
			bridgeType.GetMethod("WorldAdopted").Invoke(Activator.CreateInstance(bridgeType), [hw.World, null]);

			using var after = ConnectionRequest(connection, "{\"cmd\":\"production\"}");
			var queue = QueueOf(after.RootElement, "Building");
			Assert.That(queue.GetProperty("changed").GetBoolean(), Is.True);
			Assert.That(queue.TryGetProperty("options", out _), Is.True);
		}

		[TestCase(TestName = "A change in can_afford alone does not list the options again")]
		public void CanAffordAloneIsNotAChange()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);
			SetLocalCash(hw, 0);
			var connection = NewConnection();

			using var first = ConnectionRequest(connection, "{\"cmd\":\"production\"}");
			Assert.That(Option(QueueOf(first.RootElement, "Building"), "powr").GetProperty("can_afford").GetBoolean(), Is.False);

			SetLocalCash(hw, 10000);
			using var second = ConnectionRequest(connection, "{\"cmd\":\"production\"}");
			Assert.That(QueueOf(second.RootElement, "Building").GetProperty("changed").GetBoolean(), Is.False,
				"can_afford is not part of the option set, so funds moving does not list the options again.");

			// The cash did move: the full catalog now reports powr as affordable.
			using var tree = AgentStateDumpTest.Request("{\"cmd\":\"production_tree\"}");
			Assert.That(Option(QueueOf(tree.RootElement, "Building"), "powr").GetProperty("can_afford").GetBoolean(), Is.True);
		}

		[TestCase(TestName = "production_tree lists every option and leaves a connection's seen options as they were")]
		public void TreeDoesNotChangeTheSeenOptions()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);
			var connection = NewConnection();

			using (var first = ConnectionRequest(connection, "{\"cmd\":\"production\"}"))
				Assert.That(QueueOf(first.RootElement, "Building").GetProperty("changed").GetBoolean(), Is.True);

			using (var tree = ConnectionRequest(connection, "{\"cmd\":\"production_tree\"}"))
				Assert.That(QueueOf(tree.RootElement, "Building").GetProperty("changed").GetBoolean(), Is.True);

			using var second = ConnectionRequest(connection, "{\"cmd\":\"production\"}");
			Assert.That(QueueOf(second.RootElement, "Building").GetProperty("changed").GetBoolean(), Is.False);
		}

		/// <summary>A connection as the socket makes one. Every connection a test makes is closed in TearDown.</summary>
		object NewConnection()
		{
			var connection = ModType("AgentSocket.AgentConnection").GetConstructor(Type.EmptyTypes).Invoke(null);
			connections.Add(connection);
			return connection;
		}

		/// <summary>
		/// Runs one request line on a connection, as the socket does, and returns the envelope's results. The game thread is
		/// pumped while the request waits, as RawRequest does. The caller disposes the result.
		/// </summary>
		static JsonDocument ConnectionRequest(object connection, string line)
		{
			var handle = ModType("AgentSocket.AgentSocketCommands").GetMethod("Handle", [typeof(string), connection.GetType()]);
			var request = Task.Run(() => (string)handle.Invoke(null, [line, connection]));

			var deadline = DateTime.UtcNow.AddSeconds(10);
			while (!request.IsCompleted && DateTime.UtcNow < deadline)
			{
				Game.PerformDelayedActions();
				Thread.Sleep(1);
			}

			using var envelope = JsonDocument.Parse(request.GetAwaiter().GetResult());
			return AgentStateDumpTest.Results(envelope);
		}

		/// <summary>The world-change path: AgentEvents.DiscardWaiting, which every open connection's Discard runs.</summary>
		static Type ModType(string name) => HeadlessMod.ModData.ObjectCreator.FindType(name);

		[TearDown]
		public void CloseConnections()
		{
			foreach (var connection in connections)
				connection.GetType().GetMethod("Close").Invoke(connection, null);

			connections.Clear();
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

		/// <summary>Sets the local player's cash, with no stored resources. PlayerResources is reached by reflection, as the test project cannot reference the mod.</summary>
		internal static void SetLocalCash(HeadlessWorld hw, int cash)
		{
			SetLocalCashAndResources(hw, cash, 0);
		}

		/// <summary>Sets the local player's two pools. PlayerResources is reached by reflection, as the test project cannot reference the mod.</summary>
		internal static void SetLocalCashAndResources(HeadlessWorld hw, int cash, int storedResources)
		{
			var type = HeadlessMod.ModData.ObjectCreator.FindType("Traits.PlayerResources");
			var resourcesMethod = typeof(Actor).GetMethods().Single(m => m.Name == "TraitOrDefault" && m.GetParameters().Length == 0);
			var resources = resourcesMethod.MakeGenericMethod(type).Invoke(hw.LocalPlayer.PlayerActor, null);

			type.GetField("Cash").SetValue(resources, cash);
			type.GetField("Resources").SetValue(resources, storedResources);
		}
	}
}
