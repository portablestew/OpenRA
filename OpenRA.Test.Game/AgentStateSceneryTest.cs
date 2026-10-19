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
		const string Satellite = "atek";
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

			// The expected set (adjusted for the fog policy): every occupying actor that the digest does not call scenery (AgentStateDump.IsScenery),
			// and that the local player owns or knows (AgentFog). The enemy unit is fogged and never seen, so it is out.
			var expected = hw.World.Actors
				.Where(a => a.OccupiesSpace != null && !a.IsDead && !Scenery(a)
					&& (a.Owner == hw.LocalPlayer || Known(a, hw.LocalPlayer)))
				.Select(a => (Owner: a.Owner.PlayerName, Type: a.Info.Name, Id: a.ActorID))
				.ToArray();

			using var state = AgentStateDumpTest.State();
			var units = state.RootElement.GetProperty("units").EnumerateArray()
				.Select(u => (Owner: u.GetProperty("owner").GetString(), Type: u.GetProperty("type").GetString(), Id: u.GetProperty("id").GetUInt32()))
				.ToArray();

			// The exact set, by owner, type and id.
			Assert.That(units, Is.EquivalentTo(expected), "The state units should be exactly the non-scenery actors of the world.");

			// The two cases by name, for the failure message.
			var neutralScenery = hw.World.Actors
				.Where(a => a.Owner == neutral && a.OccupiesSpace != null && !a.IsDead && Scenery(a))
				.Select(a => a.ActorID)
				.ToArray();
			Assert.That(neutralScenery, Is.Not.Empty, "The world should contain neutral scenery to exclude.");
			Assert.That(units.Any(u => neutralScenery.Contains(u.Id)), Is.False, "No neutral scenery should appear in state units.");
			Assert.That(units.Any(u => u.Type == SpawnMarker), Is.False, "No mpspawn should appear in state units.");
			Assert.That(units.Any(u => u.Id == own.ActorID && u.Owner == hw.LocalPlayer.PlayerName), Is.True,
				"A player-owned unit should still appear in state units.");
			Assert.That(units.Any(u => u.Id == enemy.ActorID), Is.False,
				"A fogged, never-seen enemy unit should not appear in state units.");
		}

		[TestCase(TestName = "A fogged, never-seen enemy is absent from state units until a real GPS launch makes it known; own units are always present")]
		public void StateUnitsFollowTheFogPolicy()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			// The AgentFogTest setup: an enemy unit at the enemy's home, out of every local unit's sight.
			var enemy = hw.CreateActor(Infantry, hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3), hw.EnemyPlayer);
			var own = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			hw.Tick(SettleTicks);

			Assert.That(local.Shroud.IsVisible(enemy.CenterPosition), Is.False, "Precondition: the enemy unit should be out of sight.");
			Assert.That(Known(enemy, local), Is.False, "Precondition: the enemy unit should be unknown before the launch.");

			Assert.That(StateHas(enemy), Is.False, "A hidden, never-seen enemy must be absent from state units.");
			Assert.That(StateHas(own), Is.True, "The local player's own unit must always be in state units.");

			// The real GPS entry points: the satellite building is added to the watcher, then the satellite reaches orbit.
			LaunchSatellite(hw, local);
			hw.Tick(SettleTicks);

			Assert.That(Known(enemy, local), Is.True, "After the real GPS launch, the enemy unit should be known.");
			Assert.That(StateHas(enemy), Is.True, "After the launch, the known enemy unit should be in state units.");
			Assert.That(StateHas(own), Is.True, "The own unit should still be in state units after the launch.");
		}

		static Type ModType(string name)
		{
			return HeadlessMod.ModData.ObjectCreator.FindType(name);
		}

		/// <summary>The fog helper under test, read through the mod's type.</summary>
		static bool Known(Actor actor, Player observer)
		{
			return (bool)ModType("AgentSocket.AgentFog").GetMethod("Known").Invoke(null, [actor, observer]);
		}

		/// <summary>The digest's scenery definition (AgentStateDump.IsScenery, internal), called through the mod's type.</summary>
		static bool Scenery(Actor actor)
		{
			var method = ModType("AgentSocket.AgentStateDump").GetMethod("IsScenery",
				System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
			return (bool)method.Invoke(null, [actor]);
		}

		/// <summary>Whether the state's units include the actor, read from a fresh state request.</summary>
		static bool StateHas(Actor actor)
		{
			using var state = AgentStateDumpTest.State();
			return state.RootElement.GetProperty("units").EnumerateArray().Any(u => u.GetProperty("id").GetUInt32() == actor.ActorID);
		}

		/// <summary>The player's GpsWatcher trait, read through the mod's type.</summary>
		static object Watcher(Player player)
		{
			var trait = ModType("Traits.GpsWatcher");
			return typeof(Actor).GetMethod("Trait").MakeGenericMethod(trait).Invoke(player.PlayerActor, null);
		}

		/// <summary>Launches the player's satellite through its real entry points: the satellite building is added, then it reaches orbit.</summary>
		static void LaunchSatellite(HeadlessWorld hw, Player player)
		{
			var watcher = Watcher(player);
			var satellite = hw.CreateActor(Satellite, hw.FindClearArea(player.HomeLocation, 4, 4), player);
			var type = watcher.GetType();
			type.GetMethod("GpsAdd").Invoke(watcher, [satellite]);
			type.GetMethod("ReachedOrbit").Invoke(watcher, [player]);
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
