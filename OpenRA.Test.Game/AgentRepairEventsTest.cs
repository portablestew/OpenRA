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
	/// Repair spam: a repair heals through IHealth.InflictDamage with negative damage, so the damaged notify fires once per step.
	/// A structure's self-repair and a depot's repair are suppressed. Any other negative damage, such as a medic, is reported once
	/// per healer per window as healed. The sink sees every event before it reaches a connection, so a suppressed event never
	/// reaches the turn buffer and never wakes wait.
	/// </summary>
	[TestFixture]
	sealed class AgentRepairEventsTest
	{
		/// <summary>The observer's window: a second heal from the same healer within it is not reported.</summary>
		const int HealWindowTicks = 25;

		/// <summary>The hit that damages a unit or structure before its repair, in hit points.</summary>
		const int SetupDamage = 300;

		/// <summary>Ticks to let a repair run over many repair steps.</summary>
		const int RepairTicks = 300;

		/// <summary>Ticks per step of the whole-repair loop, and the cap on the loop so a stuck repair fails rather than hangs.</summary>
		const int RepairLoopStepTicks = 100;
		const int RepairLoopCapTicks = 5000;

		bool savedAgentSocket;
		bool savedHub;
		object savedSink;
		Type hub;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
			hub = HeadlessMod.ModData.ObjectCreator.FindType("AgentSocket.AgentEvents");
		}

		[SetUp]
		public void EnableAgentSocket()
		{
			savedAgentSocket = Game.Settings.Debug.AgentSocket;
			Game.Settings.Debug.AgentSocket = true;
		}

		[TearDown]
		public void RestoreHub()
		{
			Game.Settings.Debug.AgentSocket = savedAgentSocket;
			if (savedSink != null)
			{
				hub.GetField("Sink").SetValue(null, savedSink);
				hub.GetProperty("Enabled").SetValue(null, savedHub);
				savedSink = null;
			}
		}

		/// <summary>Turns the hub on and collects every event it raises from now on. Must run after HeadlessWorld.Create.</summary>
		List<object> WatchEvents()
		{
			var seen = new List<object>();
			savedHub = (bool)hub.GetProperty("Enabled").GetValue(null);
			savedSink = hub.GetField("Sink").GetValue(null);
			hub.GetProperty("Enabled").SetValue(null, true);
			hub.GetField("Sink").SetValue(null, new Action<object>(dto => seen.Add(dto)));
			return seen;
		}

		static Type ModType(string name)
		{
			return HeadlessMod.ModData.ObjectCreator.FindType(name);
		}

		static object Field(object target, string name)
		{
			return target.GetType().GetProperty(name).GetValue(target);
		}

		static bool IsEvent(object dto, string type, uint actor)
		{
			return dto.GetType().Name == type && (uint)Field(dto, "Actor") == actor;
		}

		/// <summary>A trait of a mod type on the actor, read by reflection since the test project cannot reference the mod.</summary>
		static object ModTrait(Actor actor, string name)
		{
			var type = ModType(name);
			var method = typeof(Actor).GetMethods().Single(m => m.Name == "TraitOrDefault" && m.GetParameters().Length == 0);
			return method.MakeGenericMethod(type).Invoke(actor, null);
		}

		/// <summary>The most recent hit that attacker landed on one of ours, or null (AgentRecentDamage.RecentVictim).</summary>
		static uint? RecentVictim(HeadlessWorld hw, uint attacker)
		{
			var method = ModType("AgentSocket.AgentRecentDamage").GetMethod("RecentVictim");
			return (uint?)method.Invoke(null, [hw.World, attacker]);
		}

		static int Hp(Actor actor)
		{
			return actor.TraitOrDefault<IHealth>().HP;
		}

		/// <summary>The damaged events for the actor that lowered its hit points, which are the ones a repair would produce.</summary>
		static List<object> NegativeDamaged(List<object> events, uint actor)
		{
			return events.Where(e => IsEvent(e, "DamagedV1", actor) && (int)Field(e, "Damage") < 0).ToList();
		}

		static List<object> Healed(List<object> events, uint actor)
		{
			return events.Where(e => IsEvent(e, "HealedV1", actor)).ToList();
		}

		[TestCase(TestName = "A structure's self-repair over many ticks raises no damaged or healed events, and does not feed recent damage")]
		public void StructureRepairRaisesNoEvents()
		{
			using var hw = HeadlessWorld.Create(1);
			var local = hw.LocalPlayer;
			var dome = hw.CreateActor("dome", hw.FindClearArea(new CPos(50, 50), 3, 3), local);
			hw.CreateActor("powr", hw.FindClearArea(new CPos(60, 50), 2, 2), local);
			hw.Tick(1);

			var enemy = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 60), 1, 1), hw.EnemyPlayer);
			dome.TraitOrDefault<IHealth>().InflictDamage(dome, enemy, new Damage(SetupDamage), true);
			var events = WatchEvents();
			AgentProductionTest.SetLocalCash(hw, 10000);

			var hpBefore = Hp(dome);
			var repairable = ModTrait(dome, "Traits.RepairableBuilding");
			repairable.GetType().GetMethod("RepairBuilding").Invoke(repairable, [dome, local]);
			hw.Tick(RepairTicks);

			Assert.That(Hp(dome), Is.GreaterThan(hpBefore), "Precondition: the structure should have been repaired.");
			Assert.That(NegativeDamaged(events, dome.ActorID), Is.Empty, "A structure repair must raise no damaged event.");
			Assert.That(Healed(events, dome.ActorID), Is.Empty, "A structure repair must raise no healed event.");
			Assert.That(RecentVictim(hw, dome.ActorID), Is.Null, "A structure repair must not feed recent damage.");
		}

		[TestCase(TestName = "A structure repaired from damage to full health: repairing is true then false, hit points rise to max, and no damaged or healed events")]
		public void StructureRepairFlagAndHpOverWholeRepair()
		{
			using var hw = HeadlessWorld.Create(1);
			var local = hw.LocalPlayer;
			var dome = hw.CreateActor("dome", hw.FindClearArea(new CPos(50, 50), 3, 3), local);
			hw.CreateActor("powr", hw.FindClearArea(new CPos(60, 50), 2, 2), local);
			hw.Tick(1);

			var enemy = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 60), 1, 1), hw.EnemyPlayer);
			dome.TraitOrDefault<IHealth>().InflictDamage(dome, enemy, new Damage(SetupDamage), true);
			var events = WatchEvents();
			AgentProductionTest.SetLocalCash(hw, 10000);

			// The state's repairing flag: the local player is among the structure's repairers.
			var repairable = ModTrait(dome, "Traits.RepairableBuilding");
			var repairers = (System.Collections.IEnumerable)repairable.GetType().GetField("Repairers").GetValue(repairable);
			bool Repairing() => repairers.Cast<object>().Contains(local);

			var maxHp = dome.TraitOrDefault<IHealth>().MaxHP;
			var hpBefore = Hp(dome);
			Assert.That(Repairing(), Is.False, "Precondition: the local player is not repairing before the order.");

			repairable.GetType().GetMethod("RepairBuilding").Invoke(repairable, [dome, local]);
			Assert.That(Repairing(), Is.True, "The repairing flag is true once the local player is among the repairers.");

			for (var elapsed = 0; Hp(dome) < maxHp && elapsed < RepairLoopCapTicks; elapsed += RepairLoopStepTicks)
				hw.Tick(RepairLoopStepTicks);

			Assert.That(Hp(dome), Is.GreaterThan(hpBefore), "Hit points should rise over the repair.");
			Assert.That(Hp(dome), Is.EqualTo(maxHp), "The structure should reach full health within the cap.");
			Assert.That(Repairing(), Is.False, "The repairing flag is false once the structure is at full health.");
			Assert.That(NegativeDamaged(events, dome.ActorID), Is.Empty, "No damaged event for the structure across the whole repair.");
			Assert.That(Healed(events, dome.ActorID), Is.Empty, "No healed event for the structure across the whole repair.");
		}

		[TestCase(TestName = "A structure repair raises one start notification, and no damaged or healed events over the whole repair")]
		public void StructureRepairRaisesOneStartNotification()
		{
			using var hw = HeadlessWorld.Create(1);
			var local = hw.LocalPlayer;
			var dome = hw.CreateActor("dome", hw.FindClearArea(new CPos(50, 50), 3, 3), local);
			hw.CreateActor("powr", hw.FindClearArea(new CPos(60, 50), 2, 2), local);
			hw.Tick(1);

			var enemy = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 60), 1, 1), hw.EnemyPlayer);
			dome.TraitOrDefault<IHealth>().InflictDamage(dome, enemy, new Damage(SetupDamage), true);
			var events = WatchEvents();
			AgentProductionTest.SetLocalCash(hw, 10000);

			// RA's RepairableBuilding.RepairingNotification, played once when the local player starts repairing.
			const string repairingNotification = "Repairing";
			var maxHp = dome.TraitOrDefault<IHealth>().MaxHP;
			var repairable = ModTrait(dome, "Traits.RepairableBuilding");
			repairable.GetType().GetMethod("RepairBuilding").Invoke(repairable, [dome, local]);
			for (var elapsed = 0; Hp(dome) < maxHp && elapsed < RepairLoopCapTicks; elapsed += RepairLoopStepTicks)
				hw.Tick(RepairLoopStepTicks);

			var starts = events.Where(e => e.GetType().Name == "NotificationV1" && (string)Field(e, "Name") == repairingNotification).ToList();
			Assert.That(starts, Has.Count.EqualTo(1), "A structure repair should raise its start notification exactly once.");
			Assert.That(NegativeDamaged(events, dome.ActorID), Is.Empty, "A structure repair must raise no damaged event.");
			Assert.That(Healed(events, dome.ActorID), Is.Empty, "A structure repair must raise no healed event.");
		}

		[TestCase(TestName = "A vehicle repaired at a fix depot over many ticks raises no damaged or healed events, and does not feed recent damage")]
		public void DepotRepairRaisesNoEvents()
		{
			using var hw = HeadlessWorld.Create(1);
			var local = hw.LocalPlayer;
			var depot = hw.CreateActor("fix", hw.FindClearArea(new CPos(50, 50), 2, 2), local);
			var jeep = hw.CreateActor("jeep", hw.FindClearArea(new CPos(55, 50), 1, 1), local);
			hw.Tick(1);

			var enemy = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 60), 1, 1), hw.EnemyPlayer);
			jeep.TraitOrDefault<IHealth>().InflictDamage(jeep, enemy, new Damage(SetupDamage), true);
			var events = WatchEvents();
			AgentProductionTest.SetLocalCash(hw, 10000);

			var hpBefore = Hp(jeep);
			hw.World.IssueOrder(new Order("Repair", jeep, Target.FromActor(depot), false));
			hw.Tick(RepairTicks);

			Assert.That(Hp(jeep), Is.GreaterThan(hpBefore), "Precondition: the vehicle should have been repaired.");
			Assert.That(NegativeDamaged(events, jeep.ActorID), Is.Empty, "A depot repair must raise no damaged event for the vehicle.");
			Assert.That(Healed(events, jeep.ActorID), Is.Empty, "A depot repair must raise no healed event for the vehicle.");
			Assert.That(RecentVictim(hw, depot.ActorID), Is.Null, "A depot repair must not feed recent damage.");
		}

		[TestCase(TestName = "A non-repair heal from a medic raises exactly one healed event, naming the healer")]
		public void MedicHealRaisesOneHealedEvent()
		{
			using var hw = HeadlessWorld.Create(1);
			var local = hw.LocalPlayer;
			var victim = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), local);
			var medic = hw.CreateActor("e1", hw.FindClearArea(new CPos(52, 50), 1, 1), local);
			hw.Tick(1);

			var enemy = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 60), 1, 1), hw.EnemyPlayer);
			victim.TraitOrDefault<IHealth>().InflictDamage(victim, enemy, new Damage(50), true);
			var events = WatchEvents();

			victim.TraitOrDefault<IHealth>().InflictDamage(victim, medic, new Damage(-20), true);

			var healed = Healed(events, victim.ActorID);
			Assert.That(healed, Has.Count.EqualTo(1), "A medic heal should raise exactly one healed event.");
			Assert.That(Field(healed[0], "Event"), Is.EqualTo("healed"));
			Assert.That(Field(healed[0], "Healer"), Is.EqualTo((uint?)medic.ActorID), "The healer is named: the medic is known to the local player.");
			Assert.That(Field(healed[0], "Hp"), Is.EqualTo(Hp(victim)), "The event carries the hit points after the heal.");
			Assert.That(Field(healed[0], "DamageState"), Is.Not.Null);
			Assert.That(NegativeDamaged(events, victim.ActorID), Is.Empty, "A medic heal is not reported as damaged.");
		}

		[TestCase(TestName = "Repeated medic heals from one healer inside one window raise one healed event, and a heal after the window raises another")]
		public void MedicHealsAreRateLimitedPerWindow()
		{
			using var hw = HeadlessWorld.Create(1);
			var local = hw.LocalPlayer;
			var victim = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), local);
			var medic = hw.CreateActor("e1", hw.FindClearArea(new CPos(52, 50), 1, 1), local);
			hw.Tick(1);

			var enemy = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 60), 1, 1), hw.EnemyPlayer);
			victim.TraitOrDefault<IHealth>().InflictDamage(victim, enemy, new Damage(100), true);
			var events = WatchEvents();

			victim.TraitOrDefault<IHealth>().InflictDamage(victim, medic, new Damage(-10), true);
			victim.TraitOrDefault<IHealth>().InflictDamage(victim, medic, new Damage(-10), true);
			Assert.That(Healed(events, victim.ActorID), Has.Count.EqualTo(1), "Two heals inside one window should raise one healed event.");

			hw.Tick(HealWindowTicks);
			victim.TraitOrDefault<IHealth>().InflictDamage(victim, medic, new Damage(-10), true);
			Assert.That(Healed(events, victim.ActorID), Has.Count.EqualTo(2), "A heal after the window should raise another healed event.");
		}

		[TestCase(TestName = "Ordinary positive damage is still a damaged event, with no healed event, and feeds recent damage")]
		public void PositiveDamageIsUnchanged()
		{
			using var hw = HeadlessWorld.Create(1);
			var victim = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			var enemy = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 60), 1, 1), hw.EnemyPlayer);
			hw.Tick(1);
			var events = WatchEvents();

			victim.TraitOrDefault<IHealth>().InflictDamage(victim, enemy, new Damage(40), true);

			var damaged = events.Where(e => IsEvent(e, "DamagedV1", victim.ActorID)).ToList();
			Assert.That(damaged, Has.Count.EqualTo(1), "A hit should raise exactly one damaged event.");
			Assert.That(Field(damaged[0], "Damage"), Is.EqualTo(40));
			Assert.That(Healed(events, victim.ActorID), Is.Empty, "A hit is not a heal.");
			Assert.That(RecentVictim(hw, enemy.ActorID), Is.EqualTo((uint?)victim.ActorID), "A hit feeds recent damage.");
		}
	}
}
