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
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The state digest leaves out scenery (P2.4, Q4). The world's own actors (neutral scenery, owned by the player that owns the
	/// world) and mpspawn markers are absent from state units. Player and enemy units are unchanged. The test compares the exact
	/// set of units, by owner and type, against the world's occupying actors, and checks the scenery and spawn cases by name.
	/// </summary>
	[TestFixture]
	sealed class AgentStateSceneryTest
	{
		const int Seed = 0x5C3E;
		const string SpawnMarker = "mpspawn";
		const string Infantry = "e1";

		/// <summary>Ticks to let the map's starting units settle before adding the test actors.</summary>
		const int SettleTicks = 5;

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

		[TestCase(TestName = "The state digest's units are exactly the world's non-scenery actors: neutral scenery and mpspawn are absent, player units are present")]
		public void StateUnitsAreExactlyTheNonSceneryActors()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);

			var neutral = hw.World.Players.First(p => p.PlayerReference.OwnsWorld);
			Assert.That(hw.World.Actors.Any(a => a.Owner == neutral && a.OccupiesSpace != null && !a.IsDead), Is.True,
				"The world should contain neutral scenery to exclude.");

			// A spawn marker and a player unit, plus an enemy unit, on clear cells away from the existing actors.
			var spawn = hw.CreateActor(SpawnMarker, hw.FindClearArea(hw.LocalPlayer.HomeLocation, 3, 3, 30), neutral);
			var own = hw.CreateActor(Infantry, hw.FindClearArea(hw.LocalPlayer.HomeLocation, 3, 3), hw.LocalPlayer);
			var enemy = hw.CreateActor(Infantry, hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3), hw.EnemyPlayer);
			hw.Tick(1);

			Assert.That(spawn.IsDead, Is.False, "The spawn marker should exist in the world.");

			// The expected set: every occupying actor that is not owned by the neutral world and not a spawn marker.
			var expected = hw.World.Actors
				.Where(a => a.OccupiesSpace != null && !a.IsDead && a.Owner != neutral && a.Info.Name != SpawnMarker)
				.Select(a => (Owner: a.Owner.PlayerName, Type: a.Info.Name, Id: a.ActorID))
				.ToArray();

			using var state = AgentStateDumpTest.Request("{\"cmd\":\"state\"}");
			var units = state.RootElement.GetProperty("units").EnumerateArray()
				.Select(u => (Owner: u.GetProperty("owner").GetString(), Type: u.GetProperty("type").GetString(), Id: u.GetProperty("id").GetUInt32()))
				.ToArray();

			// The exact set, by owner, type and id.
			Assert.That(units, Is.EquivalentTo(expected), "The state units should be exactly the non-scenery actors of the world.");

			// The two cases by name, for the failure message.
			Assert.That(units.Any(u => u.Owner == neutral.PlayerName), Is.False, "No neutral scenery should appear in state units.");
			Assert.That(units.Any(u => u.Type == SpawnMarker), Is.False, "No mpspawn should appear in state units.");
			Assert.That(units.Any(u => u.Id == own.ActorID && u.Owner == hw.LocalPlayer.PlayerName), Is.True,
				"A player-owned unit should still appear in state units.");
			Assert.That(units.Any(u => u.Id == enemy.ActorID), Is.True, "An enemy unit should still appear in state units.");
		}

		[TestCase(TestName = "The actors command still lists neutral scenery when it is asked for the neutral owner")]
		public void ActorsCommandKeepsNeutralScenery()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);

			var neutral = hw.World.Players.First(p => p.PlayerReference.OwnsWorld);
			var expected = hw.World.Actors.Count(a => a.Owner == neutral && a.OccupiesSpace != null && !a.IsDead);
			Assert.That(expected, Is.GreaterThan(0), "The world should contain neutral scenery to find.");

			using var actors = AgentStateDumpTest.Request($"{{\"cmd\":\"actors\",\"owner\":\"{neutral.PlayerName}\"}}");
			Assert.That(actors.RootElement.GetProperty("units").GetArrayLength(), Is.EqualTo(expected),
				"The actors command should still find the neutral owner's actors.");
		}
	}
}
