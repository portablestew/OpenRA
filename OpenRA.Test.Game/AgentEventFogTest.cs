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
using OpenRA.Traits;

namespace OpenRA.Test
{
	/// <summary>
	/// The event side of the fog policy (8.4). An event that names an actor is sent only when the local player reports that actor
	/// (AgentFog.Reported): its own actors always, and any other actor only when known (visible, a FrozenUnderFog snapshot, or a GPS
	/// dot). The same events are then sent once the actor becomes known, through each of those seams.
	/// </summary>
	[TestFixture]
	sealed class AgentEventFogTest
	{
		const int Seed = 0xE8F0;
		const string Infantry = "e1";
		const string Scout = "mcv";
		const string Structure = "powr";
		const string Satellite = "atek";

		/// <summary>Ticks to let the map settle, and let visibility, frozen snapshots and the satellite update.</summary>
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

		/// <summary>Launches the player's satellite through its real entry points, as AgentFogTest does.</summary>
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

		/// <summary>The event keys the sink saw that name the actor, in the order raised.</summary>
		List<string> EventsFor(uint actorId)
		{
			return seen.Where(d => ActorOf(d) == actorId).Select(EventName).ToList();
		}

		/// <summary>
		/// Orders the unit a short Move and runs a few ticks. The order event is raised when the order resolves, so it does not
		/// depend on the unit arriving. The unit is moved three cells off, so the Move is never a zero-length one.
		/// </summary>
		static void MoveAndSettle(HeadlessWorld hw, Actor unit)
		{
			var destination = hw.FindClearArea(unit.Location + new CVec(0, 3), 1, 1, 3);
			hw.World.IssueOrder(new Order("Move", unit, Target.FromCell(hw.World, destination), false));
			hw.Tick(SettleTicks);
		}

		/// <summary>Damages the actor from an attacker, through the health trait, and runs one tick, so the damaged event is raised for it.</summary>
		static void Hit(HeadlessWorld hw, Actor target, Actor attacker)
		{
			target.TraitOrDefault<IHealth>().InflictDamage(target, attacker, new Damage(100), true);
			hw.Tick(1);
		}

		[TestCase("Enemy", TestName = "An enemy unit's events are sent only while the local player knows it: visible, or a GPS dot")]
		[TestCase("Neutral", TestName = "A neutral unit's events are sent only while the local player knows it: visible, or a GPS dot")]
		public void UnitEventsFollowKnown(string owner)
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			// The neutral owner is the player that owns the world (the one the map's neutral units belong to, and that the digest calls scenery).
			var other = owner == "Neutral" ? hw.World.Players.FirstOrDefault(p => p.PlayerReference?.OwnsWorld == true) : hw.EnemyPlayer;
			Assert.That(other, Is.Not.Null, "Premise: the world has a " + owner + " player.");

			var unit = hw.CreateActor(Infantry, hw.FindClearArea(other.HomeLocation, 3, 3), other);
			var attacker = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			hw.Tick(SettleTicks);
			Assert.That(local.Shroud.IsVisible(unit.CenterPosition), Is.False, "The unit should start out of sight.");
			Assert.That(Known(unit, local), Is.False, "Premise: a hidden, never-seen unit is unknown.");

			// Unknown: its order, path, activity and damage are not sent.
			seen.Clear();
			MoveAndSettle(hw, unit);
			Hit(hw, unit, attacker);
			Assert.That(EventsFor(unit.ActorID), Is.Empty, "An unknown unit must have no events sent.");

			// Visible: a scout beside it makes it known, and its order and damage are sent.
			var scout = hw.CreateActor(Scout, hw.FindClearArea(unit.Location + new CVec(3, 0), 3, 3));
			hw.Tick(SettleTicks);
			Assert.That(local.Shroud.IsVisible(unit.CenterPosition), Is.True, "The scout should make the unit visible.");
			seen.Clear();
			MoveAndSettle(hw, unit);
			Hit(hw, unit, attacker);
			Assert.That(EventsFor(unit.ActorID), Does.Contain("order"), "A visible unit's order must be sent.");
			Assert.That(EventsFor(unit.ActorID), Does.Contain("damaged"), "A visible unit's damage must be sent.");

			// Fogged again: an infantry unit has no last-known snapshot in ra, so it is unknown again.
			scout.Dispose();
			hw.Tick(SettleTicks);
			Assert.That(Known(unit, local), Is.False, "A fogged infantry unit has no last-known snapshot, so it is unknown again.");
			seen.Clear();
			MoveAndSettle(hw, unit);
			Hit(hw, unit, attacker);
			Assert.That(EventsFor(unit.ActorID), Is.Empty, "A fogged unit with no snapshot must have no events sent.");

			// GPS: the satellite draws the unit as a dot, which makes it known without making it visible.
			LaunchSatellite(hw, local);
			hw.Tick(SettleTicks);
			Assert.That(local.Shroud.IsVisible(unit.CenterPosition), Is.False, "The satellite must not make the unit visible.");
			Assert.That(Known(unit, local), Is.True, "After the GPS launch, the unit is known through its dot.");
			seen.Clear();
			MoveAndSettle(hw, unit);
			Hit(hw, unit, attacker);
			Assert.That(EventsFor(unit.ActorID), Does.Contain("order"), "A GPS-known unit's order must be sent.");
			Assert.That(EventsFor(unit.ActorID), Does.Contain("damaged"), "A GPS-known unit's damage must be sent.");
		}

		[TestCase(TestName = "A structure's damaged event is sent while it is visible, and still after it fogs, from its frozen snapshot")]
		public void StructureEventFollowsFrozenSnapshot()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var attacker = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			var structure = hw.CreateActor(Structure, hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3), hw.EnemyPlayer);
			hw.Tick(SettleTicks);
			Assert.That(Known(structure, local), Is.False, "Premise: a hidden, never-seen structure is unknown.");

			// Unknown: a hit on it is not sent.
			Hit(hw, structure, attacker);
			Assert.That(EventsFor(structure.ActorID), Is.Empty, "An unknown structure must have no events sent.");

			// Visible: a scout beside it makes it known.
			var scout = hw.CreateActor(Infantry, structure.Location + new CVec(3, 0));
			hw.Tick(SettleTicks);
			Assert.That(local.Shroud.IsVisible(structure.CenterPosition), Is.True, "The scout should make the structure visible.");
			seen.Clear();
			Hit(hw, structure, attacker);
			Assert.That(EventsFor(structure.ActorID), Does.Contain("damaged"), "A visible structure's damage must be sent.");

			// Fogged: the structure keeps a frozen last-known snapshot, so it is still known and its damage is still sent.
			scout.Dispose();
			hw.Tick(SettleTicks);
			Assert.That(local.Shroud.IsVisible(structure.CenterPosition), Is.False, "The structure should be fogged again.");
			Assert.That(Known(structure, local), Is.True, "A fogged structure with a frozen snapshot is still known.");
			seen.Clear();
			Hit(hw, structure, attacker);
			Assert.That(EventsFor(structure.ActorID), Does.Contain("damaged"), "A frozen structure's damage must still be sent.");
		}

		[TestCase(TestName = "A killed event is sent for our own units and for kills we saw, and never names an attacker we do not know")]
		public void KilledEventsFollowKnown()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var ours1 = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			var ours2 = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3, 8), local);
			var enemy = hw.CreateActor(Infantry, hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3), hw.EnemyPlayer);
			var unknownKiller = hw.CreateActor(Infantry, hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3, 8), hw.EnemyPlayer);
			var visibleEnemy = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 1, 1, 8), hw.EnemyPlayer);
			hw.Tick(SettleTicks);
			Assert.That(Known(enemy, local), Is.False, "Premise: the far enemy is unknown.");
			Assert.That(Known(unknownKiller, local), Is.False, "Premise: the far killer is unknown.");
			Assert.That(Known(visibleEnemy, local), Is.True, "Premise: the enemy at our base is visible.");

			// An unknown enemy's death is not sent.
			seen.Clear();
			enemy.Kill(ours1);
			hw.Tick(1);
			Assert.That(EventsFor(enemy.ActorID), Does.Not.Contain("killed"), "The death of an unknown enemy must not be sent.");

			// One of our units dies to an enemy we do not know: sent, and the attacker is not named.
			seen.Clear();
			ours2.Kill(unknownKiller);
			hw.Tick(1);
			var ownDeath = seen.Where(d => EventName(d) == "killed" && ActorOf(d) == ours2.ActorID).ToList();
			Assert.That(ownDeath, Has.Count.EqualTo(1), "The death of our own unit must always be sent.");
			Assert.That(ownDeath[0].GetType().GetProperty("Attacker").GetValue(ownDeath[0]), Is.Null,
				"The attacker is unknown, so the event must not name it.");

			// A kill we saw: the victim was visible, so its death is sent and names our attacker.
			seen.Clear();
			visibleEnemy.Kill(ours1);
			hw.Tick(1);
			var sawKill = seen.Where(d => EventName(d) == "killed" && ActorOf(d) == visibleEnemy.ActorID).ToList();
			Assert.That(sawKill, Has.Count.EqualTo(1), "The death of a visible enemy must be sent.");
			Assert.That(sawKill[0].GetType().GetProperty("Attacker").GetValue(sawKill[0]), Is.EqualTo(ours1.ActorID),
				"The attacker is ours, so the event names it.");
		}
	}
}
