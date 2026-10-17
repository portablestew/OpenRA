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
	/// Transforms.DeployTransform, and Sound.PlayNotification reports it. The owner is the actor's owner, so the event also covers a
	/// player that is not the local one. The subscriber is reached the way the other agent tests reach it.
	/// </summary>
	[TestFixture]
	sealed class AgentNotificationTest
	{
		const int Seed = 0x9071;
		const string Mcv = "mcv";
		const string Blocker = "e1";

		/// <summary>The mcv's NoTransformNotification in ra (vehicles.yaml, MCV), named by the event.</summary>
		const string RefusalNotification = "BuildingCannotPlaceAudio";

		/// <summary>Ticks to let the map settle, and the refused order run.</summary>
		const int SettleTicks = 5;

		/// <summary>The clearing the mcv is placed in. Its fact footprint is 3x4, so a 6x6 clearing leaves a margin.</summary>
		const int Clearing = 6;

		bool savedAgentSocket;
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
			savedAgentSocket = Game.Settings.Debug.AgentSocket;
			Game.Settings.Debug.AgentSocket = true;

			savedEnabled = (bool)ModType("AgentSocket.AgentEvents").GetProperty("Enabled").GetValue(null);
			subscriber = Activator.CreateInstance(ModType("AgentSocket.AgentEventSubscriber"));
		}

		[TearDown]
		public void RestoreHub()
		{
			Call(subscriber, "Close");
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

		void SubscribeToNotifications()
		{
			Call(subscriber, "Subscribe", new[] { "notification" }, null);
		}

		/// <summary>Takes the notification events queued so far. Empty when none reached the subscriber.</summary>
		List<object> TakeNotifications()
		{
			var pending = (int)subscriber.GetType().GetProperty("Pending").GetValue(subscriber);
			if (pending == 0)
				return [];

			return ((IList)Call(subscriber, "Take")).Cast<object>()
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

			SubscribeToNotifications();
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

		[TestCase(TestName = "A refused deploy by another player's mcv emits its notification with that player as owner")]
		public void RefusedDeployOfAnotherOwnerNotifiesThatOwner()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var enemy = hw.EnemyPlayer;
			hw.Tick(SettleTicks);

			// The engine path: the enemy's own order to its mcv, with no socket involved. Transforms.cs names self.Owner.
			var mcv = CreateMcv(hw, enemy, enemy.HomeLocation);
			hw.CreateActor(Blocker, mcv.Location + new CVec(-1, -1), enemy);
			hw.Tick(1);

			SubscribeToNotifications();
			hw.World.IssueOrder(new Order("DeployTransform", mcv, false));
			hw.Tick(3);
			var notes = TakeNotifications();

			var refusals = notes.Where(n => Field<string>(n, "Name") == RefusalNotification).ToList();
			Assert.That(refusals, Has.Count.EqualTo(1), "The enemy's refused deploy should emit its notification.");
			Assert.That(Field<string>(refusals[0], "Owner"), Is.EqualTo(enemy.PlayerName),
				"The notification should name the enemy as owner, not the local player.");
		}
	}
}
