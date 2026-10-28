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
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The guards the Monte Carlo driver checks before a decision, and its cadence. Each refusal is tested on its own, with
	/// a positive control, so a guard that stops firing shows up as a failing test. Built on a headless ra world.
	/// </summary>
	[TestFixture]
	sealed class MonteCarloDriverTest
	{
		const int Seed = 0x3C2;
		const string Infantry = "e1";

		HeadlessWorld hw;
		Func<Actor, bool> savedPendingCheck;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[SetUp]
		public void SetUp()
		{
			// Other fixtures load worlds that wire the real socket check, so save whatever is current and restore it after.
			savedPendingCheck = MonteCarloDriver.HasPendingAgentOrder;
			MonteCarloMode.Clear();
			MonteCarloMode.Toggle();
			MonteCarloDriver.HasPendingAgentOrder = _ => false;
			MonteCarloDriver.ForgetDecisions();
			hw = HeadlessWorld.Create(Seed);
		}

		[TearDown]
		public void TearDown()
		{
			hw.Dispose();
			MonteCarloMode.Clear();
			MonteCarloDriver.HasPendingAgentOrder = savedPendingCheck;
			MonteCarloDriver.ForgetDecisions();
		}

		Actor CreateUnit(CPos near)
		{
			return hw.CreateActor(Infantry, hw.FindClearArea(near, 1, 1), hw.LocalPlayer);
		}

		void Select(params Actor[] actors)
		{
			foreach (var actor in actors)
				hw.World.Selection.Add(actor);
		}

		[TestCase(TestName = "An eligible selected unit is decided for when the mode is on")]
		public void EligibleUnitIsDecidedFor()
		{
			var unit = CreateUnit(new CPos(56, 50));
			Select(unit);

			var decided = MonteCarloDriver.ShouldDecide(hw.World, out var target, out var reason);

			Assert.That(decided, Is.True, "A live, eligible unit was refused: {0}", reason);
			Assert.That(target, Is.SameAs(unit));
		}

		[TestCase(TestName = "The driver refuses to decide while the Monte Carlo mode is off")]
		public void ModeOffRefuses()
		{
			MonteCarloMode.Clear();
			Select(CreateUnit(new CPos(56, 50)));

			var decided = MonteCarloDriver.ShouldDecide(hw.World, out var target, out var reason);

			Assert.That(decided, Is.False);
			Assert.That(target, Is.Null);
			Assert.That(reason, Is.EqualTo("the mode is off"));
		}

		[TestCase(TestName = "A rollout world never decides, even though its OrderManager is the live one")]
		public void RolloutWorldRefuses()
		{
			Select(CreateUnit(new CPos(56, 50)));
			var fork = WorldForks.ForkForRollout(hw.OrderManager);
			Assert.That(fork, Is.Not.Null, "The rollout fork was refused.");

			var decided = MonteCarloDriver.ShouldDecide(fork, out var target, out var reason);

			Assert.That(decided, Is.False);
			Assert.That(target, Is.Null);
			Assert.That(reason, Is.EqualTo("a rollout world never decides"));
		}

		[TestCase(TestName = "A paused game refuses to decide, through the eligibility rule")]
		public void PausedGameRefuses()
		{
			Select(CreateUnit(new CPos(56, 50)));
			hw.World.Paused = true;

			var decided = MonteCarloDriver.ShouldDecide(hw.World, out var target, out var reason);

			Assert.That(decided, Is.False);
			Assert.That(target, Is.Null);
			Assert.That(reason, Is.EqualTo("the game is paused"));
		}

		[TestCase(TestName = "An empty selection refuses to decide")]
		public void NoSelectionRefuses()
		{
			var decided = MonteCarloDriver.ShouldDecide(hw.World, out var target, out var reason);

			Assert.That(decided, Is.False);
			Assert.That(target, Is.Null);
			Assert.That(reason, Is.EqualTo("the selection is not one eligible unit"));
		}

		[TestCase(TestName = "Two selected units refuse to decide")]
		public void TwoUnitsRefuse()
		{
			Select(CreateUnit(new CPos(56, 50)), CreateUnit(new CPos(60, 50)));

			var decided = MonteCarloDriver.ShouldDecide(hw.World, out var target, out var reason);

			Assert.That(decided, Is.False);
			Assert.That(target, Is.Null);
			Assert.That(reason, Is.EqualTo("the selection is not one eligible unit"));
		}

		[TestCase(TestName = "A unit with an agent-socket order in flight is skipped")]
		public void PendingAgentOrderRefuses()
		{
			var unit = CreateUnit(new CPos(56, 50));
			Select(unit);
			MonteCarloDriver.HasPendingAgentOrder = a => ReferenceEquals(a, unit);

			var decided = MonteCarloDriver.ShouldDecide(hw.World, out var target, out var reason);

			Assert.That(decided, Is.False);
			Assert.That(target, Is.Null);
			Assert.That(reason, Is.EqualTo("the unit has an agent-socket order in flight"));
		}

		[TestCase(TestName = "Until the agent socket is wired, the default pending check blocks every unit")]
		public void DefaultPendingCheckFailsClosed()
		{
			var unit = CreateUnit(new CPos(56, 50));

			Assert.That(MonteCarloDriver.FailClosed(unit), Is.True, "The fail-closed default must refuse every unit.");
		}

		[TestCase(TestName = "The first logic tick is due at once, and the next is due a cadence later")]
		public void CadenceIsDueAtOnceThenEveryCadence()
		{
			Assert.That(MonteCarloDriver.TakeDue(0), Is.True, "The first tick was not due.");
			Assert.That(MonteCarloDriver.TakeDue(MonteCarloDriver.CadenceTicks - 1), Is.False, "A tick inside the cadence was due.");
			Assert.That(MonteCarloDriver.TakeDue(MonteCarloDriver.CadenceTicks), Is.True, "The tick at the cadence was not due.");
		}

		[TestCase(TestName = "A rewind to an earlier tick restarts the cadence rather than waiting out a stale gap")]
		public void RewindRestartsTheCadence()
		{
			Assert.That(MonteCarloDriver.TakeDue(500), Is.True);

			Assert.That(MonteCarloDriver.TakeDue(200), Is.True, "A rewound world was not decided for at once.");
			Assert.That(MonteCarloDriver.TakeDue(250), Is.False, "The restarted cadence was not respected.");
		}

		static MonteCarloCandidate Hold(double score)
		{
			return new MonteCarloCandidate(MonteCarloCandidateKind.Hold, "Hold", null, null, 0, 0, false, score);
		}

		static MonteCarloCandidate Move(CPos cell, double score)
		{
			return new MonteCarloCandidate(MonteCarloCandidateKind.Move, "Move", cell, null, 0, 0, false, score);
		}

		static MonteCarloCandidate Attack(uint targetActorId, double score)
		{
			return new MonteCarloCandidate(MonteCarloCandidateKind.Attack, $"Attack #{targetActorId}", null, targetActorId, 0, 0, false, score);
		}

		[TestCase(TestName = "The winning Move is issued through the live order path and the unit reaches its cell")]
		public void WinningMoveIsIssued()
		{
			var unit = CreateUnit(new CPos(56, 50));
			Select(unit);
			var destination = hw.FindClearArea(new CPos(60, 50), 1, 1);
			var decision = new MonteCarloDecision([Hold(0), Move(destination, 5)]);

			var issued = MonteCarloDriver.Issue(hw.World, unit, unit.Location, decision);

			Assert.That(issued, Is.True, "The winning Move was not issued.");
			Assert.That(hw.TickUntil(() => unit.Location == destination, 200), Is.Not.Null, "The unit never reached the Move's cell.");
		}

		[TestCase(TestName = "Hold issues no order")]
		public void HoldIssuesNoOrder()
		{
			var unit = CreateUnit(new CPos(56, 50));
			Select(unit);
			var start = unit.Location;
			var decision = new MonteCarloDecision([Hold(5), Move(hw.FindClearArea(new CPos(60, 50), 1, 1), 0)]);

			var issued = MonteCarloDriver.Issue(hw.World, unit, unit.Location, decision);

			Assert.That(issued, Is.False, "Hold issued an order.");
			hw.Tick(60);
			Assert.That(unit.Location, Is.EqualTo(start), "Hold moved the unit.");
		}

		[TestCase(TestName = "A unit that died after the evaluation issues nothing")]
		public void DeadUnitIssuesNothing()
		{
			var unit = CreateUnit(new CPos(56, 50));
			Select(unit);
			var from = unit.Location;
			var decision = new MonteCarloDecision([Hold(0), Move(hw.FindClearArea(new CPos(60, 50), 1, 1), 5)]);

			unit.Kill(unit);
			var issued = MonteCarloDriver.Issue(hw.World, unit, from, decision);

			Assert.That(issued, Is.False, "A dead unit was issued an order.");
		}

		[TestCase(TestName = "A unit that is no longer the selection issues nothing")]
		public void UnitNoLongerSelectedIssuesNothing()
		{
			var unit = CreateUnit(new CPos(56, 50));
			var other = CreateUnit(new CPos(60, 50));
			Select(unit);
			var from = unit.Location;
			var decision = new MonteCarloDecision([Hold(0), Move(hw.FindClearArea(new CPos(64, 50), 1, 1), 5)]);

			hw.World.Selection.Remove(unit);
			Select(other);
			var issued = MonteCarloDriver.Issue(hw.World, unit, from, decision);

			Assert.That(issued, Is.False, "A unit that is no longer selected was issued an order.");
		}

		[TestCase(TestName = "A unit that moved since the evaluation issues nothing")]
		public void MovedUnitIssuesNothing()
		{
			var unit = CreateUnit(new CPos(56, 50));
			Select(unit);
			var start = unit.Location;
			var decision = new MonteCarloDecision([Hold(0), Move(hw.FindClearArea(new CPos(60, 50), 1, 1), 5)]);

			var issued = MonteCarloDriver.Issue(hw.World, unit, start + new CVec(1, 0), decision);

			Assert.That(issued, Is.False, "A unit that moved since the evaluation was issued an order.");
			hw.Tick(60);
			Assert.That(unit.Location, Is.EqualTo(start), "The stale decision moved the unit.");
		}

		[TestCase(TestName = "An Attack on a target that is gone issues nothing")]
		public void GoneAttackTargetIssuesNothing()
		{
			var unit = CreateUnit(new CPos(56, 50));
			Select(unit);
			var decision = new MonteCarloDecision([Hold(0), Attack(uint.MaxValue, 5)]);

			var issued = MonteCarloDriver.Issue(hw.World, unit, unit.Location, decision);

			Assert.That(issued, Is.False, "An Attack on a missing actor was issued.");
		}

		[TestCase(TestName = "A decision never runs on a rollout world")]
		public void RolloutWorldNeverIssues()
		{
			var unit = CreateUnit(new CPos(56, 50));
			Select(unit);
			var fork = WorldForks.ForkForRollout(hw.OrderManager);
			Assert.That(fork, Is.Not.Null, "The rollout fork was refused.");
			var forkUnit = fork.GetActorById(unit.ActorID);
			var decision = new MonteCarloDecision([Hold(0), Move(hw.FindClearArea(new CPos(60, 50), 1, 1), 5)]);

			var issued = MonteCarloDriver.Issue(fork, forkUnit, forkUnit.Location, decision);

			Assert.That(issued, Is.False, "A rollout world issued an order.");
			Assert.That(fork.RolloutOrdersDropped, Is.Zero, "The driver tried to issue on the rollout world.");
		}
	}
}
