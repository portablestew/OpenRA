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
	/// The attack outcome (P1.6). An attack order replies once its outcome is known: started when the attack activity appears on
	/// the subject, or no_valid_target with the reason AttackBase's preconditions give. The attacker, the target and the range are
	/// set up per case; the ruleset's own actors are used throughout.
	/// </summary>
	[TestFixture]
	sealed class AgentAttackOutcomeTest
	{
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

		static string AttackLine(Actor attacker, Actor target)
		{
			return $"{{\"cmd\":\"order\",\"actor\":{attacker.ActorID},\"order\":\"Attack\",\"target_actor\":{target.ActorID}}}";
		}

		[TestCase(TestName = "An attack on an enemy in range replies started, with no reason")]
		public void InRangeEnemyReplyStarted()
		{
			using var hw = HeadlessWorld.Create(1);
			var attacker = hw.CreateActor("e1", new CPos(50, 50), hw.LocalPlayer);
			hw.Tick(1);
			var enemy = hw.CreateActor("e1", hw.FindClearArea(new CPos(52, 50), 1, 1, 2), hw.EnemyPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.RequestWithTicks(hw, AttackLine(attacker, enemy));
			var root = response.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
			Assert.That(root.GetProperty("settled").GetBoolean(), Is.True);
			Assert.That(root.GetProperty("outcome").GetString(), Is.EqualTo("started"));
			Assert.That(root.TryGetProperty("reason", out _), Is.False, "A started attack carries no reason.");
		}

		[TestCase(TestName = "An attack on the local player's own unit replies no_valid_target, not valid for self")]
		public void OwnUnitTargetReplyNoValidTarget()
		{
			using var hw = HeadlessWorld.Create(1);
			var attacker = hw.CreateActor("e1", new CPos(50, 50), hw.LocalPlayer);
			hw.Tick(1);
			var friend = hw.CreateActor("e1", hw.FindClearArea(new CPos(52, 50), 1, 1, 2), hw.LocalPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.RequestWithTicks(hw, AttackLine(attacker, friend));
			var root = response.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
			Assert.That(root.GetProperty("outcome").GetString(), Is.EqualTo("no_valid_target"));
			Assert.That(root.GetProperty("reason").GetString(), Is.EqualTo("target not valid for self"));
		}

		[TestCase(TestName = "An attack on an enemy beyond an immobile attacker's range replies no_valid_target with max range and distance")]
		public void OutOfRangeEnemyReplyNoValidTarget()
		{
			using var hw = HeadlessWorld.Create(1);

			// A GUN turret cannot move, so an out-of-range attack has nothing to close the distance with.
			var turret = hw.CreateActor("GUN", new CPos(50, 50), hw.LocalPlayer);
			hw.Tick(1);
			var enemy = hw.CreateActor("e1", hw.FindClearArea(new CPos(72, 50), 1, 1, 2), hw.EnemyPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.RequestWithTicks(hw, AttackLine(turret, enemy));
			var root = response.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
			Assert.That(root.GetProperty("outcome").GetString(), Is.EqualTo("no_valid_target"));
			var reason = root.GetProperty("reason").GetString();
			Assert.That(reason, Does.StartWith("target out of range"), reason);
			Assert.That(reason, Does.Contain("max range"), reason);
			Assert.That(reason, Does.Contain("distance"), reason);
		}

		[TestCase(TestName = "An attack by an unarmed actor replies no_valid_target, no armament")]
		public void UnarmedActorReplyNoValidTarget()
		{
			using var hw = HeadlessWorld.Create(1);

			// An MCV has no armament, so the order names Attack is not among its own and ValidateOrder refuses it.
			var mcv = hw.CreateActor("MCV", new CPos(50, 50), hw.LocalPlayer);
			hw.Tick(1);
			var enemy = hw.CreateActor("e1", hw.FindClearArea(new CPos(53, 50), 1, 1, 2), hw.EnemyPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.RequestWithTicks(hw, AttackLine(mcv, enemy));
			var root = response.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
			Assert.That(root.GetProperty("outcome").GetString(), Is.EqualTo("no_valid_target"));
			Assert.That(root.GetProperty("reason").GetString(), Is.EqualTo("actor has no armament"));
		}

		[TestCase(TestName = "An attack in a paused game returns at once, unsettled, with no outcome and no pending await")]
		public void PausedAttackReturnsAtOnceWithNoOutcome()
		{
			using var hw = HeadlessWorld.Create(1);
			var attacker = hw.CreateActor("e1", new CPos(50, 50), hw.LocalPlayer);
			hw.Tick(1);
			var enemy = hw.CreateActor("e1", hw.FindClearArea(new CPos(52, 50), 1, 1, 2), hw.EnemyPlayer);
			hw.Tick(1);
			hw.World.SetLocalPauseState(true);
			var tick = hw.World.WorldTick;

			// Request, not RequestWithTicks: a paused game never ticks, so a reply that waited would hang.
			using var response = AgentStateDumpTest.Request(AttackLine(attacker, enemy));
			var root = response.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
			Assert.That(root.GetProperty("settled").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("outcome").ValueKind, Is.EqualTo(JsonValueKind.Null));
			Assert.That(hw.World.WorldTick, Is.EqualTo(tick), "The paused game advanced.");

			var awaitType = HeadlessMod.ModData.ObjectCreator.FindType("AgentSocket.AgentOrderAwait");
			var pending = (int)awaitType.GetMethod("PendingCount").Invoke(null, [hw.World]);
			Assert.That(pending, Is.EqualTo(0), "A paused attack left its await in the registry.");
		}
	}
}
