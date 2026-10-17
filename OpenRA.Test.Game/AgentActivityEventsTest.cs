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
	/// The transform lifecycle in the activity events (P1.7). A deploy of an mcv reports started then completed. A deploy the
	/// Transforms trait refuses at its tick reports canceled with reason cannot_deploy. The mcv and its blocker are real actors in
	/// a headless world, and the mod's types are reached by reflection, as in AgentMoveEventsTest.
	/// </summary>
	[TestFixture]
	sealed class AgentActivityEventsTest
	{
		const string DeployableUnit = "mcv";
		const string Blocker = "e1";

		/// <summary>Ticks to let the map's starting units settle before placing the mcv.</summary>
		const int SettleTicks = 5;

		/// <summary>Upper bound on ticks for a deploy to finish. The mcv turns, then plays its make-animation in reverse.</summary>
		const int DeployTimeoutTicks = 300;

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

		static string KindOf(object payload)
		{
			return payload.GetType().Name;
		}

		int Pending()
		{
			return (int)subscriber.GetType().GetProperty("Pending").GetValue(subscriber);
		}

		/// <summary>Takes every queued event. Fails rather than blocking when nothing was delivered.</summary>
		List<object> Drain()
		{
			Assert.That(Pending(), Is.GreaterThan(0), "No events reached the subscriber.");
			return ((IList)Call(subscriber, "Take")).Cast<object>().ToList();
		}

		/// <summary>Places an mcv in the middle of a clear patch, big enough for its fact to be built there.</summary>
		static Actor CreateMcv(HeadlessWorld w)
		{
			var area = w.FindClearArea(w.LocalPlayer.HomeLocation, Clearing, Clearing);
			var mcv = w.CreateActor(DeployableUnit, area + new CVec(2, 2));
			Assert.That(mcv.Owner, Is.EqualTo(w.LocalPlayer));
			return mcv;
		}

		[TestCase(TestName = "Deploying an mcv reports a started transform, then a completed one for the mcv")]
		public void DeployReportsStartedThenCompleted()
		{
			using var hw = HeadlessWorld.Create(0xDEB1D);
			hw.Tick(SettleTicks);
			var mcv = CreateMcv(hw);

			Call(subscriber, "Subscribe", new[] { "activity" }, null);
			hw.World.IssueOrder(new Order("DeployTransform", mcv, queued: false));

			Assert.That(hw.TickUntil(() => Pending() >= 2, DeployTimeoutTicks), Is.Not.Null, "The deploy did not finish.");
			Assert.That(mcv.IsDead, Is.True, "The mcv should have been replaced.");

			var events = Drain();
			Assert.That(events.ConvertAll(KindOf), Is.EqualTo(new[] { "ActivityV1", "ActivityV1" }));

			foreach (var e in events)
			{
				Assert.That(Field<string>(e, "Kind"), Is.EqualTo("transform"));
				Assert.That(Field<uint>(e, "Actor"), Is.EqualTo(mcv.ActorID));
				Assert.That(Field<string>(e, "Type"), Is.EqualTo(DeployableUnit));
			}

			Assert.That(Field<string>(events[0], "State"), Is.EqualTo("started"));
			Assert.That(Field<string>(events[0], "Reason"), Is.Null);
			Assert.That(Field<string>(events[1], "State"), Is.EqualTo("completed"));
			Assert.That(Field<string>(events[1], "Reason"), Is.Null);
		}

		[TestCase(TestName = "A queued deploy onto a blocked footprint reports started, then canceled with reason cannot_deploy")]
		public void RefusedDeployReportsCannotDeploy()
		{
			using var hw = HeadlessWorld.Create(0xDEB1D);
			hw.Tick(SettleTicks);
			var mcv = CreateMcv(hw);

			// A unit on the fact footprint keeps Transforms.CanDeploy false when the activity reaches its tick. A queued order skips
			// the non-queued check in DeployTransform, so the activity starts and Tick is where the refusal happens.
			hw.CreateActor(Blocker, mcv.Location + new CVec(-1, -1));
			hw.Tick(1);

			Call(subscriber, "Subscribe", new[] { "activity" }, null);
			hw.World.IssueOrder(new Order("DeployTransform", mcv, queued: true));

			Assert.That(hw.TickUntil(() => Pending() >= 2, DeployTimeoutTicks), Is.Not.Null, "The refused deploy reported no end.");
			Assert.That(mcv.IsDead, Is.False, "The mcv should not have been replaced.");

			var events = Drain();
			Assert.That(events.ConvertAll(KindOf), Is.EqualTo(new[] { "ActivityV1", "ActivityV1" }));
			Assert.That(Field<string>(events[0], "State"), Is.EqualTo("started"));
			Assert.That(Field<string>(events[1], "Kind"), Is.EqualTo("transform"));
			Assert.That(Field<string>(events[1], "State"), Is.EqualTo("canceled"));
			Assert.That(Field<string>(events[1], "Reason"), Is.EqualTo("cannot_deploy"));
		}
	}
}
