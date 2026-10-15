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
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The place command over the real socket line path (see AgentStateDumpTest for the request helper). The rules come from
	/// mods/ra: POWR is a 2x2 building buildable from a Construction Yard (FACT), and the test world starts with one.
	/// The building is checked on the map after the PlaceBuilding order has resolved, not only in the response.
	/// </summary>
	[TestFixture]
	sealed class AgentPlaceTest
	{
		// The search radius of AgentPlace. The test project compiles against OpenRA.Game only, so it cannot name that constant.
		const int MaxRadius = 10;

		static readonly string[] SuccessKeys = ["ok", "cmd", "item", "requested_cell", "search_from", "cell", "tick", "min_effect_tick"];

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

		[TestCase(TestName = "A place at a valid requested cell puts the building on that cell")]
		public void PlaceAtValidCellPlacesTheBuilding()
		{
			using var hw = HeadlessWorld.Create(1);
			var fact = hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);
			QueueAndFinishPowr(hw);

			var cell = hw.FindClearArea(fact.Location, 2, 2, 6);
			using (var response = AgentStateDumpTest.Request($"{{\"cmd\":\"place\",\"item\":\"powr\",\"cell\":[{cell.X},{cell.Y}]}}"))
			{
				var root = response.RootElement;
				Assert.That(AgentProductionTest.Keys(root), Is.EquivalentTo(SuccessKeys));
				Assert.That(root.GetProperty("ok").GetBoolean(), Is.True);
				Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("place"));
				Assert.That(root.GetProperty("requested_cell").EnumerateArray().Select(c => c.GetInt32()), Is.EqualTo(new[] { cell.X, cell.Y }));
				Assert.That(root.GetProperty("cell").EnumerateArray().Select(c => c.GetInt32()), Is.EqualTo(new[] { cell.X, cell.Y }),
					"A valid requested cell is used as it is");
			}

			Assert.That(hw.TickUntil(() => PowrAt(hw, cell) != null, 20), Is.Not.Null,
				"The PlaceBuilding order never placed a POWR on the chosen cell.");
			Assert.That(PowrDone(), Is.False, "The finished item was consumed by the placement");
		}

		[TestCase(TestName = "An invalid requested cell is moved to the nearest valid cell, and the building appears there")]
		public void InvalidCellIsMovedToAValidOne()
		{
			using var hw = HeadlessWorld.Create(1);
			var fact = hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);
			QueueAndFinishPowr(hw);

			// The fact's own top-left cell is occupied, so no building can go there.
			var requested = fact.Location;
			using var response = AgentStateDumpTest.Request($"{{\"cmd\":\"place\",\"item\":\"powr\",\"cell\":[{requested.X},{requested.Y}]}}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True);

			var chosen = new CPos(root.GetProperty("cell")[0].GetInt32(), root.GetProperty("cell")[1].GetInt32());
			Assert.That(chosen, Is.Not.EqualTo(requested), "The occupied requested cell was chosen");
			Assert.That(Math.Max(Math.Abs(chosen.X - requested.X), Math.Abs(chosen.Y - requested.Y)), Is.LessThanOrEqualTo(MaxRadius));

			Assert.That(hw.TickUntil(() => PowrAt(hw, chosen) != null, 20), Is.Not.Null, "No POWR was placed on the chosen cell.");
			Assert.That(PowrAt(hw, requested), Is.Null, "A POWR was placed on the occupied requested cell");
		}

		[TestCase(TestName = "A place without a cell searches from the producer's location, and says so in search_from")]
		public void OmittedCellSeedsAtTheProducer()
		{
			using var hw = HeadlessWorld.Create(1);
			var fact = hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);
			QueueAndFinishPowr(hw);

			using var response = AgentStateDumpTest.Request("{\"cmd\":\"place\",\"item\":\"powr\"}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(root.GetProperty("requested_cell").ValueKind, Is.EqualTo(JsonValueKind.Null));
			Assert.That(root.GetProperty("search_from").EnumerateArray().Select(c => c.GetInt32()), Is.EqualTo(new[] { fact.Location.X, fact.Location.Y }));

			var chosen = new CPos(root.GetProperty("cell")[0].GetInt32(), root.GetProperty("cell")[1].GetInt32());
			Assert.That(hw.TickUntil(() => PowrAt(hw, chosen) != null, 20), Is.Not.Null, "No POWR was placed on the chosen cell.");
		}

		[TestCase(TestName = "A place with no valid cell within range is refused, and the finished item stays in the queue")]
		public void NoValidCellIsRefused()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);
			QueueAndFinishPowr(hw);

			// Every cell within MaxRadius of this one is off the map, so none can be valid.
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"place\",\"item\":\"powr\",\"cell\":[-100,-100]}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("place"));
			Assert.That(root.GetProperty("error").GetString(), Is.EqualTo("no valid cell for 'powr' within 10 cells of -100,-100"));
			Assert.That(PowrDone(), Is.True, "A refused placement must leave the finished item in the queue");
		}

		[TestCase(TestName = "A place for an item still in production is refused, and nothing is placed")]
		public void UnfinishedItemIsRefused()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);

			using (var build = AgentStateDumpTest.Request("{\"cmd\":\"build\",\"item\":\"powr\"}"))
				Assert.That(build.RootElement.GetProperty("ok").GetBoolean(), Is.True);

			// The build order loops back through the network before it reaches the queue, so give it a few ticks to get there.
			hw.Tick(5);

			using var response = AgentStateDumpTest.Request("{\"cmd\":\"place\",\"item\":\"powr\"}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo("'powr' is not done yet: it is still in production"));
		}

		[TestCase(TestName = "A place for an item that was never queued is refused")]
		public void UnqueuedItemIsRefused()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.Request("{\"cmd\":\"place\",\"item\":\"powr\"}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo("no finished 'powr' in a production queue: build it first"));
		}

		[TestCase(TestName = "A place for an unknown item is refused")]
		public void UnknownItemIsRefused()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.Request("{\"cmd\":\"place\",\"item\":\"nope\"}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo("unknown item 'nope'"));
		}

		[TestCase("{\"cmd\":\"place\",\"item\":\"powr\",\"cell\":[1]}", TestName = "A place with a malformed cell is refused by the parser")]
		[TestCase("{\"cmd\":\"place\"}", TestName = "A place without an item is refused by the parser")]
		public void MalformedRequestIsRefused(string line)
		{
			using var response = AgentStateDumpTest.Request(line);
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("cmd").GetString(), Is.EqualTo("place"));
			Assert.That(response.RootElement.GetProperty("error").GetString(), Does.Contain("must be"));
		}

		/// <summary>Queues one POWR through the build command and ticks until its item is finished.</summary>
		static void QueueAndFinishPowr(HeadlessWorld hw)
		{
			using (var build = AgentStateDumpTest.Request("{\"cmd\":\"build\",\"item\":\"powr\"}"))
				Assert.That(build.RootElement.GetProperty("ok").GetBoolean(), Is.True, "The POWR build was refused");

			Assert.That(hw.TickUntil(PowrDone, 3000), Is.Not.Null, "The POWR never finished building.");
		}

		/// <summary>True when a POWR in the building queue is finished and waits for placement, read through the production command.</summary>
		static bool PowrDone()
		{
			using var production = AgentStateDumpTest.Request("{\"cmd\":\"production\"}");
			return AgentProductionTest.QueueOf(production.RootElement, "Building").GetProperty("items").EnumerateArray()
				.Any(i => i.GetProperty("name").GetString() == "powr" && i.GetProperty("state").GetString() == "done");
		}

		static Actor PowrAt(HeadlessWorld hw, CPos cell)
		{
			return hw.World.Actors.FirstOrDefault(a => a.Info.Name == "powr" && a.Owner == hw.LocalPlayer && !a.IsDead && a.Location == cell);
		}
	}
}
