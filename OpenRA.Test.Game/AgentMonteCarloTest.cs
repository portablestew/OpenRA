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
	/// The montecarlo and select commands (the socket's way to reach the Monte Carlo label's three states). montecarlo sets
	/// the mode, select replaces the selection, and both reply with the label's status. Neither issues an order.
	/// </summary>
	[TestFixture]
	sealed class AgentMonteCarloTest
	{
		const int Seed = 0x3C2;
		const string Infantry = "e1";
		const string Building = "powr";

		bool savedAgentSocket;
		HeadlessWorld hw;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[SetUp]
		public void SetUp()
		{
			savedAgentSocket = Game.Settings.Debug.AgentSocket;
			Game.Settings.Debug.AgentSocket = true;
			MonteCarloMode.Clear();
			hw = HeadlessWorld.Create(Seed);
		}

		[TearDown]
		public void TearDown()
		{
			hw.Dispose();
			MonteCarloMode.Clear();
			Game.Settings.Debug.AgentSocket = savedAgentSocket;
		}

		Actor CreateActor(string type)
		{
			return hw.CreateActor(type, hw.FindClearArea(new CPos(56, 50), 2, 2), hw.LocalPlayer);
		}

		JsonDocument Request(string line)
		{
			return AgentStateDumpTest.RequestWithTicks(hw, line);
		}

		static string Status(JsonDocument response)
		{
			return response.RootElement.GetProperty("status").GetString();
		}

		[TestCase(TestName = "Select replies with the selected id and leaves the mode off, and montecarlo on makes that unit active")]
		public void SelectThenOnMakesTheUnitActive()
		{
			var unit = CreateActor(Infantry);

			using var selected = Request($"{{\"cmd\":\"select\",\"actor\":{unit.ActorID}}}");
			Assert.That(selected.RootElement.GetProperty("ok").GetBoolean(), Is.True, selected.RootElement.ToString());
			Assert.That(selected.RootElement.GetProperty("enabled").GetBoolean(), Is.False);
			Assert.That(Status(selected), Is.EqualTo("off"));
			Assert.That(selected.RootElement.GetProperty("selection").EnumerateArray().Select(e => e.GetUInt32()),
				Is.EqualTo(new[] { unit.ActorID }));

			using var on = Request("{\"cmd\":\"montecarlo\",\"on\":true}");
			Assert.That(on.RootElement.GetProperty("ok").GetBoolean(), Is.True, on.RootElement.ToString());
			Assert.That(on.RootElement.GetProperty("enabled").GetBoolean(), Is.True);
			Assert.That(Status(on), Is.EqualTo("active"));
			Assert.That(on.RootElement.GetProperty("unit").GetString(), Is.EqualTo(Infantry));
		}

		[TestCase(TestName = "On with a building selected is idle, and its unit is null")]
		public void OnWithABuildingSelectedIsIdle()
		{
			var building = CreateActor(Building);
			using (Request($"{{\"cmd\":\"select\",\"actor\":{building.ActorID}}}")) { }

			using var on = Request("{\"cmd\":\"montecarlo\",\"on\":true}");

			Assert.That(Status(on), Is.EqualTo("idle"));
			Assert.That(on.RootElement.GetProperty("unit").ValueKind, Is.EqualTo(JsonValueKind.Null));
		}

		[TestCase(TestName = "On with nothing selected is idle, with an empty selection")]
		public void OnWithNothingSelectedIsIdle()
		{
			using var on = Request("{\"cmd\":\"montecarlo\",\"on\":true}");

			Assert.That(Status(on), Is.EqualTo("idle"));
			Assert.That(on.RootElement.GetProperty("selection").GetArrayLength(), Is.EqualTo(0));
		}

		[TestCase(TestName = "Off after on returns the status off and clears the mode")]
		public void OffReturnsToOff()
		{
			var unit = CreateActor(Infantry);
			using (Request($"{{\"cmd\":\"select\",\"actor\":{unit.ActorID}}}")) { }
			using (Request("{\"cmd\":\"montecarlo\",\"on\":true}")) { }

			using var off = Request("{\"cmd\":\"montecarlo\",\"on\":false}");

			Assert.That(off.RootElement.GetProperty("enabled").GetBoolean(), Is.False);
			Assert.That(Status(off), Is.EqualTo("off"));
			Assert.That(MonteCarloMode.Enabled, Is.False);
		}

		[TestCase(TestName = "Select issues no order, so the unit stays idle through ticks")]
		public void SelectIssuesNoOrder()
		{
			var unit = CreateActor(Infantry);

			using (Request($"{{\"cmd\":\"select\",\"actor\":{unit.ActorID}}}")) { }
			hw.Tick(60);

			Assert.That(unit.IsIdle, Is.True, "Selecting the unit gave it an activity.");
		}

		[TestCase(TestName = "Select of an unknown actor id is an error and leaves the selection as it was")]
		public void SelectOfAnUnknownActorIsAnError()
		{
			var unit = CreateActor(Infantry);
			using (Request($"{{\"cmd\":\"select\",\"actor\":{unit.ActorID}}}")) { }

			using var response = Request("{\"cmd\":\"select\",\"actor\":4294967000}");

			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.TryGetProperty("error", out _), Is.True);
			Assert.That(hw.World.Selection.Actors.Select(a => a.ActorID), Is.EqualTo(new[] { unit.ActorID }));
		}

		[TestCase(TestName = "A montecarlo request whose on is not a boolean is an error and leaves the mode alone")]
		public void NonBooleanOnIsAnError()
		{
			using var response = Request("{\"cmd\":\"montecarlo\",\"on\":\"yes\"}");

			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(MonteCarloMode.Enabled, Is.False);
		}
	}
}
