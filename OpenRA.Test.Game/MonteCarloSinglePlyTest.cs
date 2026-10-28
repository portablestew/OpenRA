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
using System.Reflection;
using NUnit.Framework;
using OpenRA.Network;

namespace OpenRA.Test
{
	/// <summary>
	/// The single-ply evaluator (<see cref="MonteCarloSinglePly"/>): which candidate it prefers, that a throwing tick
	/// leaves the rollout scope, and that an evaluation changes nothing in the live game.
	/// </summary>
	[TestFixture]
	sealed class MonteCarloSinglePlyTest
	{
		const int Seed = 0x4F0B;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[SetUp]
		public void ClearWindow()
		{
			WorldForks.Clear();
		}

		[TearDown]
		public void ClearWindowAfter()
		{
			WorldForks.Clear();
		}

		/// <summary>The parent's buffered, not yet sent, orders. Read by reflection: the buffer is private and has no count.</summary>
		static int PendingLocalOrders(OrderManager om)
		{
			var field = typeof(OrderManager).GetField("localOrders", BindingFlags.Instance | BindingFlags.NonPublic);
			return ((List<Order>)field.GetValue(om)).Count;
		}

		/// <summary>
		/// Places a unit and an enemy <paramref name="distance"/> cells east of it, both in clear ground near the local base.
		/// The live world ticks twice first, so the local player's vision has revealed the enemy by the time candidates are generated.
		/// </summary>
		static (Actor Unit, Actor Enemy) PlaceUnitAndEnemy(HeadlessWorld hw, string enemyType, int distance)
		{
			var at = hw.FindClearArea(hw.LocalPlayer.HomeLocation, 4, 4);
			var unit = hw.CreateActor("e1", at, hw.LocalPlayer);
			var enemy = hw.CreateActor(enemyType, at + new CVec(distance, 0), hw.EnemyPlayer);
			hw.Tick(2);
			return (unit, enemy);
		}

		[TestCase(TestName = "An attack on a visible enemy outscores hold when the attack gains HP")]
		public void AttackOutscoresHoldWhenItGainsHp()
		{
			// An unarmed building just outside the unit's weapon range (5 cells), so hold cannot fire on it, and the attack
			// gains HP with no return fire. The unit's own vision does not reach that far, so an unarmed own mcv beside the
			// building spots it. The mcv has no weapon, so it does not fire either.
			using var hw = HeadlessWorld.Create(Seed);
			var at = hw.FindClearArea(hw.LocalPlayer.HomeLocation, 4, 4);
			var unit = hw.CreateActor("e1", at, hw.LocalPlayer);
			hw.CreateActor("mcv", at + new CVec(4, 0), hw.LocalPlayer);
			hw.CreateActor("powr", at + new CVec(5, 0), hw.EnemyPlayer);
			hw.Tick(2);

			var decision = MonteCarloSinglePly.Evaluate(hw.OrderManager, unit, hw.LocalPlayer);

			var hold = decision.Candidates.Single(c => c.Kind == MonteCarloCandidateKind.Hold);
			var attack = decision.Candidates.Single(c => c.Kind == MonteCarloCandidateKind.Attack);
			Assert.That(attack.EnemyHpLost, Is.GreaterThan(0), "The attack did not hurt the enemy.");
			Assert.That(attack.Score, Is.GreaterThan(hold.Score), "The attack did not outscore hold.");
			Assert.That(decision.Winner.Kind, Is.EqualTo(MonteCarloCandidateKind.Attack), "Hold won over a clearly better attack.");
		}

		[TestCase(TestName = "Hold wins when no candidate gains anything")]
		public void HoldWinsWhenNothingIsGained()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var unit = hw.CreateActor("e1", hw.FindClearArea(hw.LocalPlayer.HomeLocation, 4, 4), hw.LocalPlayer);

			var decision = MonteCarloSinglePly.Evaluate(hw.OrderManager, unit, hw.LocalPlayer);

			// No enemy is visible, so there is no Move (it heads away from an enemy) and no Attack: Hold is the only candidate.
			Assert.That(decision.Candidates.Select(c => c.Kind), Is.EqualTo(new[] { MonteCarloCandidateKind.Hold }), "A candidate was generated with no visible enemy.");
			Assert.That(decision.Candidates.All(c => c.Score == 0), Is.True, "A candidate scored without gaining anything.");
			Assert.That(decision.Winner.Kind, Is.EqualTo(MonteCarloCandidateKind.Hold), "Hold did not win a tie.");
		}

		[TestCase(TestName = "The only Move heads directly away from the nearest visible enemy, and three candidates are scored")]
		public void MoveHeadsAwayFromNearestEnemy()
		{
			// The enemy is two cells east, so away is west.
			using var hw = HeadlessWorld.Create(Seed);
			var (unit, _) = PlaceUnitAndEnemy(hw, "e1", 2);
			var start = unit.Location;

			var decision = MonteCarloSinglePly.Evaluate(hw.OrderManager, unit, hw.LocalPlayer);

			var move = decision.Candidates.Single(c => c.Kind == MonteCarloCandidateKind.Move);
			Assert.That(move.Label, Is.EqualTo("Move W"), "The Move did not head west, away from the enemy.");
			Assert.That(move.Destination, Is.EqualTo(start + new CVec(-MonteCarloSinglePly.MoveDistance, 0)), "The Move destination is not four cells west.");
			Assert.That(decision.Candidates.Count, Is.EqualTo(3), "The decision did not score exactly three candidates.");
		}

		[TestCase(TestName = "The rollout scope is exited after a throwing tick")]
		public void RolloutScopeIsExitedAfterThrowingTick()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var (unit, _) = PlaceUnitAndEnemy(hw, "e1", 2);

			Assert.Throws<InvalidOperationException>(() =>
				MonteCarloSinglePly.Evaluate(hw.OrderManager, unit, hw.LocalPlayer, MonteCarloSinglePly.Horizon,
					w => throw new InvalidOperationException("The injected tick failure.")));

			Assert.That(WorldForks.InRolloutScope, Is.False, "The rollout scope was left open after a throwing tick.");
		}

		[TestCase(TestName = "An evaluation leaves the live SyncHash, the parent's pending orders and the live actors unchanged")]
		public void EvaluationLeavesLiveWorldUnchanged()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var (unit, enemy) = PlaceUnitAndEnemy(hw, "e1", 2);
			var hashBefore = hw.World.SyncHash();
			var tickBefore = hw.World.WorldTick;
			var pendingBefore = PendingLocalOrders(hw.OrderManager);
			var unitCellBefore = unit.Location;
			var enemyHpBefore = enemy.TraitOrDefault<OpenRA.Traits.IHealth>().HP;

			var decision = MonteCarloSinglePly.Evaluate(hw.OrderManager, unit, hw.LocalPlayer);

			Assert.That(decision, Is.Not.Null, "The evaluation was refused.");
			Assert.That(hw.World.SyncHash(), Is.EqualTo(hashBefore), "The live SyncHash changed.");
			Assert.That(hw.World.WorldTick, Is.EqualTo(tickBefore), "The live world ticked.");
			Assert.That(PendingLocalOrders(hw.OrderManager), Is.EqualTo(pendingBefore), "An evaluation order reached the parent OrderManager.");
			Assert.That(unit.Location, Is.EqualTo(unitCellBefore), "The live unit moved.");
			Assert.That(enemy.TraitOrDefault<OpenRA.Traits.IHealth>().HP, Is.EqualTo(enemyHpBefore), "The live enemy lost HP.");
		}

		[TestCase(TestName = "Each candidate's score is its own HP change, plus the enemy weight times the enemy HP lost, less the death penalty")]
		public void ScoreIsTheDocumentedFormula()
		{
			// The same setup as AttackOutscoresHoldWhenItGainsHp, so the Attack candidate has enemy HP lost to score.
			using var hw = HeadlessWorld.Create(Seed);
			var at = hw.FindClearArea(hw.LocalPlayer.HomeLocation, 4, 4);
			var unit = hw.CreateActor("e1", at, hw.LocalPlayer);
			hw.CreateActor("mcv", at + new CVec(4, 0), hw.LocalPlayer);
			hw.CreateActor("powr", at + new CVec(5, 0), hw.EnemyPlayer);
			hw.Tick(2);

			var decision = MonteCarloSinglePly.Evaluate(hw.OrderManager, unit, hw.LocalPlayer);

			Assert.That(decision.Candidates.Any(c => c.EnemyHpLost > 0), Is.True, "No candidate hurt the enemy, so the enemy term is not exercised.");
			foreach (var candidate in decision.Candidates)
			{
				var expected = candidate.OwnHpDelta + MonteCarloSinglePly.EnemyHpWeight * candidate.EnemyHpLost
					- (candidate.UnitDied ? MonteCarloSinglePly.DeathPenalty : 0);
				Assert.That(candidate.Score, Is.EqualTo(expected).Within(1e-9), $"{candidate.Label} scored {candidate.Score}, not {expected}.");
			}
		}

		[TestCase(TestName = "Among candidates a tie goes to the earlier one, and a strictly higher later candidate beats Hold")]
		public void TieGoesToTheEarlierCandidate()
		{
			var hold = new MonteCarloCandidate(MonteCarloCandidateKind.Hold, "Hold", null, null, 0, 0, false, 0);
			var move = new MonteCarloCandidate(MonteCarloCandidateKind.Move, "Move W", null, null, 0, 0, false, 50);
			var attack = new MonteCarloCandidate(MonteCarloCandidateKind.Attack, "Attack #7", null, 7u, 0, 0, false, 50);
			var idleMove = new MonteCarloCandidate(MonteCarloCandidateKind.Move, "Move E", null, null, 0, 0, false, 0);

			Assert.That(new MonteCarloDecision([hold, move, attack]).Winner, Is.SameAs(move), "A Move tied with an Attack did not win by coming first.");
			Assert.That(new MonteCarloDecision([hold, attack, move]).Winner, Is.SameAs(attack), "An Attack tied with a Move did not win by coming first.");
			Assert.That(new MonteCarloDecision([hold, move]).Winner, Is.SameAs(move), "A higher-scoring Move did not beat Hold.");
			Assert.That(new MonteCarloDecision([hold, idleMove]).Winner, Is.SameAs(hold), "Hold did not win a tie with a Move.");
		}
	}
}
