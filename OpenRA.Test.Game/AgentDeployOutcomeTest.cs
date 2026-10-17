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

using System.Text.Json;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The deploy outcome (P1.8). A DeployTransform order replies once its transform ends: completed on a clear footprint, or
	/// canceled with the cell reason the footprint fails. The reason comes from the same mirror the P1.9 vocabulary uses.
	/// The mcv and its blocker are placed as AgentActivityEventsTest places them.
	/// </summary>
	[TestFixture]
	sealed class AgentDeployOutcomeTest
	{
		const string DeployableUnit = "mcv";
		const string Blocker = "e1";

		/// <summary>Ticks to let the map's starting units settle before placing the mcv.</summary>
		const int SettleTicks = 5;

		/// <summary>The clearing the mcv is placed in. Its fact footprint is 3x4, so a 6x6 clearing leaves a margin.</summary>
		const int Clearing = 6;

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

		static string DeployLine(Actor mcv, bool queued)
		{
			return $"{{\"cmd\":\"order\",\"actor\":{mcv.ActorID},\"order\":\"DeployTransform\",\"queued\":{(queued ? "true" : "false")}}}";
		}

		/// <summary>Places an mcv in the middle of a clear patch, big enough for its fact to be built there.</summary>
		static Actor CreateMcv(HeadlessWorld hw)
		{
			var area = hw.FindClearArea(hw.LocalPlayer.HomeLocation, Clearing, Clearing);
			return hw.CreateActor(DeployableUnit, area + new CVec(2, 2));
		}

		[TestCase(TestName = "A deploy on a clear footprint replies completed, with no reason, and the mcv is replaced")]
		public void ClearFootprintReplyCompleted()
		{
			using var hw = HeadlessWorld.Create(0xDEB1D);
			hw.Tick(SettleTicks);
			var mcv = CreateMcv(hw);

			using var response = AgentStateDumpTest.RequestWithTicks(hw, DeployLine(mcv, false));
			var root = response.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
			Assert.That(root.GetProperty("settled").GetBoolean(), Is.True);
			Assert.That(root.GetProperty("outcome").GetString(), Is.EqualTo("completed"));
			Assert.That(root.TryGetProperty("reason", out _), Is.False, "A completed deploy carries no reason.");
			Assert.That(mcv.IsDead, Is.True, "The mcv should have been replaced.");
		}

		[TestCase(TestName = "A non-queued deploy on a blocked footprint replies canceled with the occupied cell reason, and the mcv stays")]
		public void BlockedFootprintReplyCanceledOccupied()
		{
			using var hw = HeadlessWorld.Create(0xDEB1D);
			hw.Tick(SettleTicks);
			var mcv = CreateMcv(hw);
			hw.CreateActor(Blocker, mcv.Location + new CVec(-1, -1));
			hw.Tick(1);

			using var response = AgentStateDumpTest.RequestWithTicks(hw, DeployLine(mcv, false));
			var root = response.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False, root.ToString());
			Assert.That(root.GetProperty("settled").GetBoolean(), Is.True);
			Assert.That(root.GetProperty("outcome").GetString(), Is.EqualTo("canceled"));
			Assert.That(root.GetProperty("reason").GetString(), Is.EqualTo("occupied"));
			Assert.That(mcv.IsDead, Is.False, "The mcv should not have been replaced.");
		}

		[TestCase(TestName = "A queued deploy on a blocked footprint replies canceled with the cell reason, not cannot_deploy")]
		public void BlockedQueuedReplyCanceledWithCellReason()
		{
			using var hw = HeadlessWorld.Create(0xDEB1D);
			hw.Tick(SettleTicks);
			var mcv = CreateMcv(hw);
			hw.CreateActor(Blocker, mcv.Location + new CVec(-1, -1));
			hw.Tick(1);

			// A queued deploy starts its activity, and the activity refuses at its tick. The socket names the cell, not the event's cannot_deploy.
			using var response = AgentStateDumpTest.RequestWithTicks(hw, DeployLine(mcv, true));
			var root = response.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False, root.ToString());
			Assert.That(root.GetProperty("outcome").GetString(), Is.EqualTo("canceled"));
			Assert.That(root.GetProperty("reason").GetString(), Is.EqualTo("occupied"));
			Assert.That(mcv.IsDead, Is.False, "The mcv should not have been replaced.");
		}

		[TestCase(TestName = "A deploy in a paused game returns at once, unsettled, with no outcome and no pending await")]
		public void PausedDeployReturnsAtOnce()
		{
			using var hw = HeadlessWorld.Create(0xDEB1D);
			hw.Tick(SettleTicks);
			var mcv = CreateMcv(hw);
			hw.World.SetLocalPauseState(true);
			var tick = hw.World.WorldTick;

			// Request, not RequestWithTicks: a paused game never ticks, so a reply that waited would hang.
			using var response = AgentStateDumpTest.Request(DeployLine(mcv, false));
			var root = response.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
			Assert.That(root.GetProperty("settled").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("outcome").ValueKind, Is.EqualTo(JsonValueKind.Null));
			Assert.That(hw.World.WorldTick, Is.EqualTo(tick), "The paused game advanced.");

			var awaitType = HeadlessMod.ModData.ObjectCreator.FindType("AgentSocket.AgentOrderAwait");
			var pending = (int)awaitType.GetMethod("PendingCount").Invoke(null, [hw.World]);
			Assert.That(pending, Is.EqualTo(0), "A paused deploy left its await in the registry.");
		}
	}
}
