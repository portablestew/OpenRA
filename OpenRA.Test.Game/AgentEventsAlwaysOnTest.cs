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
using NUnit.Framework;
using OpenRA.Traits;

namespace OpenRA.Test
{
	/// <summary>
	/// Events are recorded with no connection open (P3.1). The gates that guarded DTO construction now test only whether the socket
	/// is enabled, so a Move's path and activity events, and a refused deploy's notification, reach the test sink while no
	/// connection is open. With the socket disabled nothing is raised.
	/// </summary>
	[TestFixture]
	sealed class AgentEventsAlwaysOnTest
	{
		const int Seed = 0xA1A0;
		const string Infantry = "e1";
		const string Mcv = "mcv";
		const string Blocker = "e1";

		/// <summary>Ticks to let the map settle.</summary>
		const int SettleTicks = 5;

		/// <summary>The mcv's clearing, as AgentNotificationTest uses it.</summary>
		const int Clearing = 6;

		bool savedAgentSocket;
		bool savedEnabled;
		Action<object> savedSink;

		// Every event the sink sees, as its group and payload.
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

		/// <summary>Every event group the sink has seen, in the order raised.</summary>
		List<string> Groups()
		{
			return seen.ConvertAll(EventName);
		}

		/// <summary>The event key of a DTO, which names its type.</summary>
		static string EventName(object dto)
		{
			return (string)dto.GetType().GetProperty("Event").GetValue(dto);
		}

		static Actor CreateMcv(HeadlessWorld hw, Player owner, CPos near)
		{
			var area = hw.FindClearArea(near, Clearing, Clearing);
			return hw.CreateActor(Mcv, area + new CVec(2, 2), owner);
		}

		[TestCase(TestName = "With no connection open, a Move's path and activity events reach the sink")]
		public void MoveReachesSinkWithNoSubscriber()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);
			Assert.That((int)ModType("AgentSocket.AgentEvents").GetProperty("ConnectionCount").GetValue(null), Is.Zero,
				"Precondition: no connection should be open.");

			var unit = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			hw.Tick(1);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);

			// The socket order issues on the game thread with no connection (a null connection), so the sink is the only listener.
			using var response = AgentStateDumpTest.RequestWithTicks(hw,
				$"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}]}}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.True, response.RootElement.ToString());

			Assert.That(Groups(), Does.Contain("path"), "A Move with no connection open should raise its path to the sink.");
			Assert.That(Groups(), Does.Contain("activity"), "A Move with no connection open should raise its activity to the sink.");
		}

		[TestCase(TestName = "With no connection open, a refused deploy's notification reaches the sink")]
		public void NotificationReachesSinkWithNoSubscriber()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var mcv = CreateMcv(hw, local, local.HomeLocation);
			hw.CreateActor(Blocker, mcv.Location + new CVec(-1, -1), local);
			hw.Tick(1);

			using var response = AgentStateDumpTest.RequestWithTicks(hw,
				$"{{\"cmd\":\"order\",\"actor\":{mcv.ActorID},\"order\":\"DeployTransform\",\"queued\":false}}");

			Assert.That(Groups(), Does.Contain("notification"), "A refused deploy should raise its notification to the sink.");
		}

		[TestCase(TestName = "With the socket disabled, the sink sees no events from a Move")]
		public void NothingReachesSinkWhenSocketDisabled()
		{
			Game.Settings.Debug.AgentSocket = false;
			ModType("AgentSocket.AgentEvents").GetProperty("Enabled").SetValue(null, false);

			using var hw = HeadlessWorld.Create(Seed);
			var unit = hw.CreateActor(Infantry, hw.FindClearArea(hw.LocalPlayer.HomeLocation, 3, 3), hw.LocalPlayer);
			hw.Tick(1);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);
			hw.World.IssueOrder(new Order("Move", unit, Target.FromCell(hw.World, destination), false));
			hw.TickUntil(() => unit.Location == destination, 500);

			Assert.That(seen, Is.Empty, "With the socket disabled, no event should be raised.");
		}
	}
}
