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

namespace OpenRA.Test
{
	/// <summary>
	/// The player-actor observer's payloads (M7, <c>3h-player-observer</c>). The combat fight is the same as
	/// <c>WorldCombatTest</c>: two hostile e1s in range, one dies. The events come from the real engine hooks, through
	/// the hub, into a connection's queue.
	/// </summary>
	/// <remarks>
	/// The mod loads OpenRA.Mods.Common into its own load context, so the hub and payload types it uses are not the
	/// ones this assembly would compile against. Everything goes through the mod's copy, as AgentStateDumpTest does.
	/// </remarks>
	[TestFixture]
	sealed class AgentPlayerObserverTest
	{
		const int Separation = 2;
		const int CombatTimeoutTicks = 500;
		const int SettleTicks = 5;

		bool savedEnabled;
		object subscriber;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[SetUp]
		public void CreateSubscriber()
		{
			savedEnabled = (bool)ModType("AgentSocket.AgentEvents").GetProperty("Enabled").GetValue(null);
			subscriber = Activator.CreateInstance(ModType("AgentSocket.AgentEventSubscriber"));
		}

		[TearDown]
		public void RestoreHub()
		{
			Call(subscriber, "Close");
			ModType("AgentSocket.AgentEvents").GetProperty("Enabled").SetValue(null, savedEnabled);
		}

		/// <summary>
		/// Turns the hub on. Must run after HeadlessWorld.Create: the world's AgentWorldBridge sets Enabled from the settings
		/// (false here) when the world loads, so an earlier setting is overwritten.
		/// </summary>
		static void EnableHub()
		{
			ModType("AgentSocket.AgentEvents").GetProperty("Enabled").SetValue(null, true);
		}

		static Type ModType(string name)
		{
			return HeadlessMod.ModData.ObjectCreator.FindType(name);
		}

		static object Call(object target, string method, params object[] args)
		{
			return target.GetType().GetMethod(method).Invoke(target, args);
		}

		/// <summary>Takes every queued event. Fails rather than blocking when nothing was delivered.</summary>
		List<object> Drain()
		{
			var pending = (int)subscriber.GetType().GetProperty("Pending").GetValue(subscriber);
			Assert.That(pending, Is.GreaterThan(0), "No events reached the subscriber.");
			return ((IList)Call(subscriber, "Take")).Cast<object>().ToList();
		}

		static T Field<T>(object target, string name)
		{
			return (T)target.GetType().GetProperty(name).GetValue(target);
		}

		static string KindOf(object payload)
		{
			return payload.GetType().Name;
		}

		[TestCase(TestName = "Two hostile e1s fight: one killed event and at least one damaged event, with payloads")]
		public void CombatRaisesDamagedAndKilled()
		{
			using var w = HeadlessWorld.Create(0xC0FFEE);

			Assert.That(w.EnemyPlayer, Is.Not.Null, "The selected map has only one playable slot.");

			w.Tick(SettleTicks);

			EnableHub();

			var (mineCell, _) = w.FindClearRun(w.LocalPlayer.HomeLocation, Separation);
			var theirsCell = mineCell + new CVec(Separation, 0);

			var mine = w.CreateActor("e1", mineCell);
			var theirs = w.CreateActor("e1", theirsCell, w.EnemyPlayer);

			Call(subscriber, "Subscribe", new[] { "damage", "killed" }, null);

			var resolvedAt = w.TickUntil(() => mine.IsDead || theirs.IsDead, CombatTimeoutTicks);
			Assert.That(resolvedAt, Is.Not.Null, "The fight did not resolve within the timeout.");

			var batch = Drain();

			var dead = mine.IsDead ? mine : theirs;
			var survivor = mine.IsDead ? theirs : mine;

			var killed = batch.Where(e => KindOf(e) == "KilledV1").ToList();
			Assert.That(killed, Has.Count.EqualTo(1), "Expected exactly one killed event.");

			var kill = killed[0];
			Assert.That(Field<string>(kill, "Event"), Is.EqualTo("killed"));
			Assert.That(Field<uint>(kill, "Actor"), Is.EqualTo(dead.ActorID));
			Assert.That(Field<string>(kill, "Type"), Is.EqualTo("e1"));
			Assert.That(Field<string>(kill, "Owner"), Is.EqualTo(dead.Owner.PlayerName));
			Assert.That(Field<uint?>(kill, "Attacker"), Is.EqualTo(survivor.ActorID));
			Assert.That(Field<string>(kill, "AttackerType"), Is.EqualTo("e1"));
			Assert.That(Field<string>(kill, "AttackerOwner"), Is.EqualTo(survivor.Owner.PlayerName));
			Assert.That(Field<int>(kill, "Cost"), Is.GreaterThan(0));
			Assert.That(Field<int>(kill, "Tick"), Is.LessThanOrEqualTo(w.World.WorldTick));

			var damaged = batch.Where(e => KindOf(e) == "DamagedV1").ToList();
			Assert.That(damaged, Is.Not.Empty, "Expected at least one damaged event.");

			// The e1 may also hit other units in range, so a hit can name an actor that is not one of the two combatants.
			var ownerOf = new Dictionary<uint, string>
			{
				[mine.ActorID] = mine.Owner.PlayerName,
				[theirs.ActorID] = theirs.Owner.PlayerName,
			};

			foreach (var hit in damaged)
			{
				Assert.That(Field<string>(hit, "Event"), Is.EqualTo("damaged"));
				Assert.That(Field<string>(hit, "Type"), Is.Not.Null.And.Not.Empty);
				Assert.That(Field<int>(hit, "Damage"), Is.GreaterThan(0));
				Assert.That(Field<int>(hit, "MaxHp"), Is.GreaterThan(0));
				Assert.That(Field<int>(hit, "Hp"), Is.InRange(0, Field<int>(hit, "MaxHp")));
				Assert.That(Field<string>(hit, "DamageState"), Is.Not.Null.And.Not.Empty);

				if (ownerOf.TryGetValue(Field<uint>(hit, "Actor"), out var owner))
					Assert.That(Field<string>(hit, "Owner"), Is.EqualTo(owner));
			}

			// The killing blow is the last hit on the dead actor, and it is recorded before the kill.
			var lastHitOnDead = damaged.LastOrDefault(h => Field<uint>(h, "Actor") == dead.ActorID);
			Assert.That(lastHitOnDead, Is.Not.Null);
			Assert.That(Field<int>(lastHitOnDead, "Hp"), Is.EqualTo(0));
			Assert.That(batch.IndexOf(lastHitOnDead), Is.LessThan(batch.IndexOf(kill)));
		}

		[TestCase(TestName = "Captured payload names the old and new owner and the captor")]
		public void CapturedPayloadNamesOwners()
		{
			using var w = HeadlessWorld.Create(0xC0FFEE);

			Assert.That(w.EnemyPlayer, Is.Not.Null, "The selected map has only one playable slot.");

			var (cell, _) = w.FindClearRun(w.LocalPlayer.HomeLocation, Separation);
			var captured = w.CreateActor("e1", cell, w.EnemyPlayer);
			var captor = w.CreateActor("e1", cell + new CVec(Separation, 0), w.LocalPlayer);

			EnableHub();
			Call(subscriber, "Subscribe", new[] { "captured" }, null);
			ModType("AgentPlayerEventObserver").GetMethod("Captured")
				.Invoke(null, [captured, captor, w.EnemyPlayer, w.LocalPlayer]);

			var batch = Drain();
			Assert.That(batch, Has.Count.EqualTo(1));

			var capture = batch[0];
			Assert.That(KindOf(capture), Is.EqualTo("CapturedV1"));
			Assert.That(Field<string>(capture, "Event"), Is.EqualTo("captured"));
			Assert.That(Field<uint>(capture, "Actor"), Is.EqualTo(captured.ActorID));
			Assert.That(Field<string>(capture, "Type"), Is.EqualTo("e1"));
			Assert.That(Field<string>(capture, "From"), Is.EqualTo(w.EnemyPlayer.PlayerName));
			Assert.That(Field<string>(capture, "To"), Is.EqualTo(w.LocalPlayer.PlayerName));
			Assert.That(Field<uint>(capture, "Captor"), Is.EqualTo(captor.ActorID));
		}
	}
}
