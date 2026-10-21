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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpenRA.Traits;

namespace OpenRA.Test
{
	/// <summary>
	/// Damaged events under the fog policy (P2.3). A hit always carries attacker_bearing, from the damaged actor toward the attacker.
	/// The attacker's identity is named only when the local player knows it (AgentFog). The hits are real: IHealth.InflictDamage
	/// raises the same Damaged notification a weapon does. The connection and the satellite are reached the way the other agent
	/// tests reach them, since the mod's types load through ObjectCreator.
	/// </summary>
	[TestFixture]
	sealed class AgentDamagedFogTest
	{
		const int Seed = 0xDA3A;
		const string Infantry = "e1";
		const string Satellite = "atek";

		/// <summary>How far the never-seen attacker stands from the victim, in cells: outside the victim's sight.</summary>
		const int Separation = 15;

		/// <summary>The hit's damage, in hit points.</summary>
		const int HitPoints = 100;

		/// <summary>Ticks to let the map settle, and visibility and the satellite update.</summary>
		const int SettleTicks = 5;

		bool savedAgentSocket;
		bool savedEnabled;
		object connection;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[SetUp]
		public void CreateConnection()
		{
			savedAgentSocket = Game.Settings.Debug.AgentSocket;
			Game.Settings.Debug.AgentSocket = true;

			savedEnabled = (bool)ModType("AgentSocket.AgentEvents").GetProperty("Enabled").GetValue(null);
			connection = Activator.CreateInstance(ModType("AgentSocket.AgentConnection"));
		}

		[TearDown]
		public void RestoreHub()
		{
			Call(connection, "Close");
			ModType("AgentSocket.AgentEvents").GetProperty("Enabled").SetValue(null, savedEnabled);
			Game.Settings.Debug.AgentSocket = savedAgentSocket;
		}

		static Type ModType(string name)
		{
			return HeadlessMod.ModData.ObjectCreator.FindType(name);
		}

		static object Call(object target, string method, params object[] args)
		{
			return target.GetType().GetMethod(method).Invoke(target, args);
		}

		static T Field<T>(object target, string name)
		{
			return (T)target.GetType().GetProperty(name).GetValue(target);
		}

		/// <summary>The fog helper under test (AgentSocket.AgentFog.Known).</summary>
		static bool Known(Actor actor, Player observer)
		{
			return (bool)ModType("AgentSocket.AgentFog").GetMethod("Known").Invoke(null, [actor, observer]);
		}

		/// <summary>Takes the damaged events queued so far that name the victim. Empty when none reached the connection.</summary>
		List<object> TakeDamaged(uint victim)
		{
			var pending = (int)connection.GetType().GetProperty("Pending").GetValue(connection);
			if (pending == 0)
				return [];

			return Only(((IList)Call(connection, "Flush")).Cast<object>(), "damaged")
				.Where(e => e.GetType().Name == "DamagedV1" && Field<uint>(e, "Actor") == victim)
				.ToList();
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

		/// <summary>One real hit on the victim, from the attacker. Health notifies the damaged actor's owner.</summary>
		static void Hit(Actor victim, Actor attacker)
		{
			victim.TraitOrDefault<IHealth>().InflictDamage(victim, attacker, new Damage(HitPoints), true);
		}

		/// <summary>A friendly victim and an enemy attacker: the victim is the local player's infantry, the attacker is an enemy infantry.</summary>
		static (Actor Victim, Actor Attacker) Pair(HeadlessWorld hw, int attackerOffset)
		{
			var local = hw.LocalPlayer;
			var victim = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			var attacker = hw.CreateActor(Infantry, victim.Location + new CVec(attackerOffset, 0), hw.EnemyPlayer);
			return (victim, attacker);
		}

		[TestCase(TestName = "A damaged event with a visible attacker names it, with identity and bearing")]
		public void VisibleAttackerIsNamed()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var (victim, attacker) = Pair(hw, 2);
			hw.Tick(SettleTicks);
			Assert.That(local.Shroud.IsVisible(attacker.CenterPosition), Is.True, "Precondition: the attacker should be visible.");

			Hit(victim, attacker);
			var hits = TakeDamaged(victim.ActorID);

			Assert.That(hits, Has.Count.EqualTo(1), "Expected one damaged event for the victim.");
			var hit = hits[0];
			Assert.That(Field<uint?>(hit, "Attacker"), Is.EqualTo(attacker.ActorID), "A visible attacker is named by id.");
			Assert.That(Field<string>(hit, "AttackerType"), Is.EqualTo(Infantry));
			Assert.That(Field<string>(hit, "AttackerOwner"), Is.EqualTo(hw.EnemyPlayer.PlayerName));
			Assert.That(Field<bool>(hit, "AttackerShrouded"), Is.False, "A known attacker is not shrouded.");
			Assert.That(Field<string>(hit, "AttackerBearing"), Is.EqualTo("E"), "The attacker is two cells east of the victim.");
		}

		[TestCase(TestName = "A damaged event with a fogged, never-seen attacker hides its identity, shrouds it, and keeps the bearing")]
		public void FoggedAttackerIsShrouded()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var (victim, attacker) = Pair(hw, Separation);
			hw.Tick(SettleTicks);
			Assert.That(local.Shroud.IsVisible(attacker.CenterPosition), Is.False, "Precondition: the attacker should be out of sight.");
			Assert.That(Known(attacker, local), Is.False, "Precondition: the attacker should be unknown.");

			Hit(victim, attacker);
			var hits = TakeDamaged(victim.ActorID);

			Assert.That(hits, Has.Count.EqualTo(1), "Expected one damaged event for the victim.");
			var hit = hits[0];
			Assert.That(Field<uint?>(hit, "Attacker"), Is.Null, "A fogged, never-seen attacker must not be named.");
			Assert.That(Field<string>(hit, "AttackerType"), Is.Null, "A fogged attacker's type must not be given.");
			Assert.That(Field<string>(hit, "AttackerOwner"), Is.Null, "A fogged attacker's owner must not be given.");
			Assert.That(Field<bool>(hit, "AttackerShrouded"), Is.True, "A fogged attacker is shrouded.");
			Assert.That(Field<string>(hit, "AttackerBearing"), Is.EqualTo("E"), "The bearing is still given: the attacker is 15 cells east.");
		}

		[TestCase(TestName = "A real GPS launch makes the same fogged attacker identified again in later damaged events")]
		public void GpsLaunchNamesTheAttackerAgain()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var (victim, attacker) = Pair(hw, Separation);
			hw.Tick(SettleTicks);
			Hit(victim, attacker);
			var before = TakeDamaged(victim.ActorID);
			Assert.That(Field<bool>(before[0], "AttackerShrouded"), Is.True, "Precondition: before the launch, the attacker is shrouded.");

			LaunchSatellite(hw, local);
			hw.Tick(SettleTicks);
			Assert.That(local.Shroud.IsVisible(attacker.CenterPosition), Is.False, "The launch must not make the attacker visible.");
			Assert.That(Known(attacker, local), Is.True, "After the launch, the attacker is known through the GPS dot rule.");

			Hit(victim, attacker);
			var after = TakeDamaged(victim.ActorID);

			Assert.That(after, Has.Count.EqualTo(1), "Expected one damaged event for the victim after the launch.");
			var hit = after[0];
			Assert.That(Field<uint?>(hit, "Attacker"), Is.EqualTo(attacker.ActorID), "After the launch, the attacker is named again.");
			Assert.That(Field<string>(hit, "AttackerType"), Is.EqualTo(Infantry));
			Assert.That(Field<string>(hit, "AttackerOwner"), Is.EqualTo(hw.EnemyPlayer.PlayerName));
			Assert.That(Field<bool>(hit, "AttackerShrouded"), Is.False, "A known attacker is not shrouded.");
			Assert.That(Field<string>(hit, "AttackerBearing"), Is.EqualTo("E"));
		}

		[TestCase(TestName = "A damaged event naming an already-disposed attacker is reported without identity or bearing, and without throwing")]
		public void DisposedAttackerIsNotTouched()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			// The attacker is out of sight, so the fog rule falls through to the radar-dot check whose trait lookup throws on a
			// disposed actor; a visible attacker would short-circuit before reaching it.
			var (victim, attacker) = Pair(hw, Separation);
			hw.Tick(SettleTicks);
			Assert.That(local.Shroud.IsVisible(attacker.CenterPosition), Is.False, "Precondition: the attacker is out of sight.");

			// A missile in flight names its firer as the attacker after that firer is gone: the world knows the attacker, but the
			// local player cannot, and its traits are disposed. Reaching them throws (TraitDictionary.CheckDestroyed) - the crash
			// this guards, from AgentFog.GpsDotShown's trait lookup during a damaged event.
			attacker.Dispose();
			hw.Tick(SettleTicks);
			Assert.That(attacker.Disposed, Is.True, "Precondition: the disposed attacker is out of the world with its traits gone.");
			Assert.That(Known(attacker, local), Is.False, "A disposed attacker is not known.");

			Assert.DoesNotThrow(() => Hit(victim, attacker), "The hit must be reported, not thrown.");
			var hits = TakeDamaged(victim.ActorID);

			Assert.That(hits, Has.Count.EqualTo(1), "Expected one damaged event for the victim.");
			var hit = hits[0];
			Assert.That(Field<uint?>(hit, "Attacker"), Is.Null, "A disposed attacker is not named.");
			Assert.That(Field<string>(hit, "AttackerType"), Is.Null, "A disposed attacker's type is not given.");
			Assert.That(Field<string>(hit, "AttackerOwner"), Is.Null, "A disposed attacker's owner is not given.");
			Assert.That(Field<bool>(hit, "AttackerShrouded"), Is.True, "A disposed attacker is shrouded.");
			Assert.That(Field<string>(hit, "AttackerBearing"), Is.Null, "A disposed attacker has no live position, so it gives no bearing.");
		}

		/// <summary>The events of the named types, in the order they were raised. The connection carries every event; a test looks at its own.</summary>
		static IEnumerable<object> Only(IEnumerable<object> events, params string[] types)
		{
			return events.Where(e => types.Contains(e.GetType().GetProperty("Event")?.GetValue(e) as string));
		}
	}
}
