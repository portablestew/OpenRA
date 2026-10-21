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
	/// The notification event (P2.10). A refused non-queued DeployTransform plays the mcv's NoTransformNotification through
	/// Transforms.DeployTransform, and Sound.PlayNotification reports it. Notifications are reported only for the local player
	/// and its allies, and the cash counter's tick notifications are dropped. The connection is reached the way the other
	/// agent tests reach it.
	/// </summary>
	[TestFixture]
	sealed class AgentNotificationTest
	{
		const int Seed = 0x9071;
		const string Mcv = "mcv";
		const string Blocker = "e1";

		/// <summary>The mcv's NoTransformNotification in ra (vehicles.yaml, MCV), named by the event.</summary>
		const string RefusalNotification = "BuildingCannotPlaceAudio";

		/// <summary>The notification PlaceBuilding plays when a player's buildable options change, named by the event.</summary>
		const string NewOptionsNotification = "NewOptions";

		/// <summary>Ticks to let the map settle, and the refused order run.</summary>
		const int SettleTicks = 5;

		/// <summary>The clearing the mcv is placed in. Its fact footprint is 3x4, so a 6x6 clearing leaves a margin.</summary>
		const int Clearing = 6;

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

		/// <summary>Takes the notification events queued so far. Empty when none reached the connection.</summary>
		List<object> TakeNotifications()
		{
			var pending = (int)connection.GetType().GetProperty("Pending").GetValue(connection);
			if (pending == 0)
				return [];

			return Only(((IList)Call(connection, "Flush")).Cast<object>(), "notification")
				.Where(e => e.GetType().Name == "NotificationV1")
				.ToList();
		}

		static Actor CreateMcv(HeadlessWorld hw, Player owner, CPos near)
		{
			var area = hw.FindClearArea(near, Clearing, Clearing);
			return hw.CreateActor(Mcv, area + new CVec(2, 2), owner);
		}

		[TestCase(TestName = "A refused non-queued deploy on a blocked footprint emits one notification for the mcv's owner, at the refusal's tick")]
		public void RefusedDeployNotifiesItsOwner()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var mcv = CreateMcv(hw, local, local.HomeLocation);
			hw.CreateActor(Blocker, mcv.Location + new CVec(-1, -1), local);
			hw.Tick(1);

			var start = hw.World.WorldTick;
			using var response = AgentStateDumpTest.RequestWithTicks(hw,
				$"{{\"cmd\":\"order\",\"actor\":{mcv.ActorID},\"order\":\"DeployTransform\",\"queued\":false}}");
			var notes = TakeNotifications();

			var refusals = notes.Where(n => Field<string>(n, "Name") == RefusalNotification).ToList();
			Assert.That(refusals, Has.Count.EqualTo(1), "A refused deploy should emit exactly one notification with the mcv's refusal name.");
			Assert.That(Field<string>(refusals[0], "Owner"), Is.EqualTo(local.PlayerName), "The notification should name the mcv's owner.");
			Assert.That(Field<string>(refusals[0], "Event"), Is.EqualTo("notification"));
			Assert.That(Field<int>(refusals[0], "Tick"), Is.InRange(start, hw.World.WorldTick),
				"The notification should carry the tick the refusal happened on.");
		}

		[TestCase(TestName = "A refused deploy by another player emits no notification, so an opponent's state does not leak")]
		public void RefusedDeployOfAnotherOwnerIsNotReported()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var enemy = hw.EnemyPlayer;
			hw.Tick(SettleTicks);

			// The engine path: the enemy's own order to its mcv, with no socket involved. Transforms.cs names self.Owner.
			var mcv = CreateMcv(hw, enemy, enemy.HomeLocation);
			hw.CreateActor(Blocker, mcv.Location + new CVec(-1, -1), enemy);
			hw.Tick(1);

			hw.World.IssueOrder(new Order("DeployTransform", mcv, false));
			hw.Tick(3);
			var notes = TakeNotifications();

			Assert.That(notes.Where(n => Field<string>(n, "Name") == RefusalNotification), Is.Empty,
				"An opponent's notification must not be sent.");
		}

		[TestCase(TestName = "A NewOptions notification for the local player lists the options it newly made buildable, and not the ones already buildable")]
		public void NewOptionsListsTheNewlyBuildableOptions()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			// A fact makes powr buildable, and a powr makes proc buildable (proc needs anypower). The first NewOptions sets the baseline.
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), local);
			hw.Tick(1);
			PlayNewOptions(hw, local);
			TakeNotifications();

			hw.CreateActor("powr", hw.FindClearArea(new CPos(60, 60), 2, 2), local);
			hw.Tick(1);
			PlayNewOptions(hw, local);
			var note = TakeNotifications().Single(n => Field<string>(n, "Name") == NewOptionsNotification);

			var options = ((IEnumerable)Field<object>(note, "Options")).Cast<object>().ToList();
			var names = options.ConvertAll(o => Field<string>(o, "Name"));
			Assert.That(names, Does.Contain("proc"), "A powr should make proc newly buildable, so the notification should list it.");
			Assert.That(names, Does.Not.Contain("powr"), "powr was buildable before the powr was built, so it is not newly available.");

			var proc = options.Single(o => Field<string>(o, "Name") == "proc");
			Assert.That(Field<bool>(proc, "Buildable"), Is.True);
			Assert.That(Field<string[]>(proc, "Prerequisites"), Does.Contain("anypower"));
		}

		[TestCase(TestName = "A NewOptions notification for an enemy is not sent, so the enemy's tech progress does not leak")]
		public void NewOptionsOfAnEnemyIsNotSent()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var enemy = hw.EnemyPlayer;
			hw.Tick(SettleTicks);

			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), enemy);
			hw.Tick(1);
			PlayNewOptions(hw, enemy);
			TakeNotifications();

			// The same unlock as the local test, so the enemy's notification would list proc if it were reported.
			hw.CreateActor("powr", hw.FindClearArea(new CPos(60, 60), 2, 2), enemy);
			hw.Tick(1);
			PlayNewOptions(hw, enemy);
			var notes = TakeNotifications();

			Assert.That(notes.Where(n => Field<string>(n, "Name") == NewOptionsNotification), Is.Empty,
				"An enemy's NewOptions must not be sent.");
		}

		[TestCase(TestName = "The cash counter's tick notifications are not sent: cash is in the state and production replies")]
		public void CashTickNotificationsAreNotSent()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			Game.Sound.PlayNotification(hw.World.Map.Rules, local, "Sounds", "CashTickUp", local.Faction.InternalName);
			Game.Sound.PlayNotification(hw.World.Map.Rules, local, "Sounds", "CashTickDown", local.Faction.InternalName);
			hw.Tick(1);

			Assert.That(TakeNotifications(), Is.Empty, "The counter's tick notifications should not be sent.");
		}

		static void PlayNewOptions(HeadlessWorld hw, Player owner)
		{
			Game.Sound.PlayNotification(hw.World.Map.Rules, owner, "Speech", NewOptionsNotification, owner.Faction.InternalName);
		}

		[TestCase(TestName = "A nameless notification is not sent, and a named one from the same player still is")]
		public void NamelessNotificationIsNotSent()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			Game.Sound.PlayNotification(hw.World.Map.Rules, local, "Sounds", null, local.Faction.InternalName);
			Game.Sound.PlayNotification(hw.World.Map.Rules, local, "Sounds", RefusalNotification, local.Faction.InternalName);
			hw.Tick(1);
			var notes = TakeNotifications();

			Assert.That(notes.Where(n => Field<string>(n, "Name") == null), Is.Empty, "A nameless notification should not be sent.");
			Assert.That(notes.Where(n => Field<string>(n, "Name") == RefusalNotification).ToList(), Has.Count.EqualTo(1),
				"The named notification from the same player should still be sent.");
		}

		/// <summary>The events of the named types, in the order they were raised. The connection carries every event; a test looks at its own.</summary>
		static IEnumerable<object> Only(IEnumerable<object> events, params string[] types)
		{
			return events.Where(e => types.Contains(e.GetType().GetProperty("Event")?.GetValue(e) as string));
		}
	}
}
