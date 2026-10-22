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
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The spotted event (3d): the first time the local player comes to know an enemy actor (AgentFog.Known), it is raised once for
	/// that actor. A re-reveal from fog is not a new sighting. Husks and scenery are never spotted.
	/// </summary>
	[TestFixture]
	sealed class AgentSpottedTest
	{
		const int Seed = 0x5C07;
		const string Infantry = "e1";
		const string Scout = "mcv";
		const string Satellite = "atek";

		/// <summary>Ticks to let the map settle, and let visibility and the satellite update.</summary>
		const int SettleTicks = 5;

		bool savedAgentSocket;
		bool savedEnabled;
		Action<object> savedSink;

		// Every event the sink has seen since the last clear, as its DTO.
		readonly List<object> seen = [];

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[SetUp]
		public void InstallSink()
		{
			savedAgentSocket = Game.Settings.Debug.AgentSocket;
			Game.Settings.Debug.AgentSocket = true;

			var events = ModType("AgentSocket.AgentEvents");
			savedEnabled = (bool)events.GetProperty("Enabled").GetValue(null);
			savedSink = (Action<object>)events.GetField("Sink").GetValue(null);
			seen.Clear();
			events.GetField("Sink").SetValue(null, new Action<object>(dto => seen.Add(dto)));
		}

		[TearDown]
		public void RestoreSink()
		{
			var events = ModType("AgentSocket.AgentEvents");
			events.GetField("Sink").SetValue(null, savedSink);
			events.GetProperty("Enabled").SetValue(null, savedEnabled);
			Game.Settings.Debug.AgentSocket = savedAgentSocket;
		}

		static Type ModType(string name)
		{
			return HeadlessMod.ModData.ObjectCreator.FindType(name);
		}

		/// <summary>The fog helper (AgentFog.Known), called through the mod's type.</summary>
		static bool Known(Actor actor, Player observer)
		{
			return (bool)ModType("AgentSocket.AgentFog").GetMethod("Known").Invoke(null, [actor, observer]);
		}

		/// <summary>The player's GpsWatcher trait, read through the mod's type.</summary>
		static object Watcher(Player player)
		{
			var trait = ModType("Traits.GpsWatcher");
			return typeof(Actor).GetMethod("Trait").MakeGenericMethod(trait).Invoke(player.PlayerActor, null);
		}

		/// <summary>Launches the player's satellite through its real entry points, as AgentEventFogTest does.</summary>
		static void LaunchSatellite(HeadlessWorld hw, Player player)
		{
			var watcher = Watcher(player);
			var satellite = hw.CreateActor(Satellite, hw.FindClearArea(player.HomeLocation, 4, 4), player);
			var type = watcher.GetType();
			type.GetMethod("GpsAdd").Invoke(watcher, [satellite]);
			type.GetMethod("ReachedOrbit").Invoke(watcher, [player]);
		}

		/// <summary>The event key of a DTO, which names its type.</summary>
		static string EventName(object dto)
		{
			return (string)dto.GetType().GetProperty("Event").GetValue(dto);
		}

		/// <summary>The actor a DTO names, or null when it names none.</summary>
		static uint? ActorOf(object dto)
		{
			return dto.GetType().GetProperty("Actor")?.GetValue(dto) as uint?;
		}

		/// <summary>The spotted DTOs the sink has seen that name the actor, in the order raised.</summary>
		List<object> SpottedFor(uint actorId)
		{
			return seen.Where(d => EventName(d) == "spotted" && ActorOf(d) == actorId).ToList();
		}

		[TestCase(TestName = "An enemy unit is spotted once, the first time it is visible, and a re-reveal from fog is not spotted again")]
		public void FirstSightingRaisesSpottedOnce()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var unit = hw.CreateActor(Infantry, hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3), hw.EnemyPlayer);
			hw.Tick(SettleTicks);
			Assert.That(Known(unit, local), Is.False, "Premise: a hidden, never-seen unit is unknown.");
			Assert.That(SpottedFor(unit.ActorID), Is.Empty, "An unknown enemy must not be spotted.");

			// First sighting: a scout beside it makes it visible, and it is spotted with its type, owner and cell.
			var scout = hw.CreateActor(Scout, hw.FindClearArea(unit.Location + new CVec(3, 0), 3, 3));
			hw.Tick(SettleTicks);
			Assert.That(local.Shroud.IsVisible(unit.CenterPosition), Is.True, "The scout should make the unit visible.");

			var sighting = SpottedFor(unit.ActorID);
			Assert.That(sighting, Has.Count.EqualTo(1), "The first sighting must raise one spotted event.");
			var dto = sighting[0];
			Assert.That(dto.GetType().GetProperty("Type").GetValue(dto), Is.EqualTo(unit.Info.Name), "Spotted names the actor's type.");
			Assert.That(dto.GetType().GetProperty("Owner").GetValue(dto), Is.EqualTo(hw.EnemyPlayer.PlayerName), "Spotted names the actor's owner.");
			Assert.That(dto.GetType().GetProperty("Cell").GetValue(dto), Is.EqualTo(new[] { unit.Location.X, unit.Location.Y }),
				"Spotted names the actor's cell.");

			// Fogged, then seen again: the actor is already known to the player, so it is not spotted a second time.
			scout.Dispose();
			hw.Tick(SettleTicks);
			Assert.That(local.Shroud.IsVisible(unit.CenterPosition), Is.False, "The unit should fog again.");
			seen.Clear();
			hw.CreateActor(Scout, hw.FindClearArea(unit.Location + new CVec(3, 0), 3, 3));
			hw.Tick(SettleTicks);
			Assert.That(local.Shroud.IsVisible(unit.CenterPosition), Is.True, "The unit should be visible again.");
			Assert.That(SpottedFor(unit.ActorID), Is.Empty, "A re-reveal from fog must not raise spotted again.");
		}

		[TestCase(TestName = "An enemy unit is spotted when a GPS satellite draws it as a dot, though it is not visible")]
		public void GpsDotRaisesSpotted()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var unit = hw.CreateActor(Infantry, hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3), hw.EnemyPlayer);
			hw.Tick(SettleTicks);
			Assert.That(SpottedFor(unit.ActorID), Is.Empty, "Premise: the unit is not spotted before the satellite.");

			LaunchSatellite(hw, local);
			hw.Tick(SettleTicks);
			Assert.That(local.Shroud.IsVisible(unit.CenterPosition), Is.False, "The satellite must not make the unit visible.");
			Assert.That(Known(unit, local), Is.True, "After the GPS launch, the unit is known through its dot.");
			Assert.That(SpottedFor(unit.ActorID), Has.Count.EqualTo(1), "A unit known through its GPS dot must be spotted once.");
		}

		[TestCase(TestName = "A husk is never spotted, even when the local player knows it")]
		public void HuskIsNeverSpotted()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var husk = hw.CreateActor("3tnk.husk", hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3), hw.EnemyPlayer);
			var scout = hw.CreateActor(Scout, husk.Location + new CVec(3, 0));
			hw.Tick(SettleTicks);
			Assert.That(husk.Info.Name, Is.EqualTo("3tnk.husk").IgnoreCase, "Premise: the actor should be a husk.");
			Assert.That(Known(husk, local), Is.True, "Premise: the husk should be known, so only the husk rule can keep it out.");
			Assert.That(SpottedFor(husk.ActorID), Is.Empty, "A husk must not be spotted.");
		}

		[TestCase(TestName = "Each enemy unit in a flood of sightings is spotted once, with its own id")]
		public void FloodSpotsEachActorOnce()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);

			var units = Enumerable.Range(0, 4)
				.Select(_ => hw.CreateActor(Infantry, hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3, 8), hw.EnemyPlayer))
				.ToList();
			var local = hw.LocalPlayer;
			hw.CreateActor(Scout, units[0].Location + new CVec(3, 0));
			hw.Tick(SettleTicks);

			// The scout's sight reaches several of them at once, and each one it reaches is spotted once.
			var known = units.Where(u => Known(u, local)).ToList();
			Assert.That(known, Has.Count.GreaterThan(1), "Premise: several enemy units are known at once.");
			foreach (var unit in known)
				Assert.That(SpottedFor(unit.ActorID), Has.Count.EqualTo(1), "Each sighted unit must be spotted exactly once.");
			Assert.That(seen.Count(d => EventName(d) == "spotted"), Is.EqualTo(known.Count),
				"Only the sighted units are spotted, and each is raised once.");
		}
	}
}
