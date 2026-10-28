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
using OpenRA.Network;
using OpenRA.Traits;

namespace OpenRA
{
	/// <summary>The kinds of candidate order the single-ply evaluator generates.</summary>
	public enum MonteCarloCandidateKind { Hold, Move, Attack }

	/// <summary>One candidate order for the unit, with the rollout's score and the parts that make it up.</summary>
	public sealed class MonteCarloCandidate
	{
		public readonly MonteCarloCandidateKind Kind;

		/// <summary>A short name for the candidate, such as "Hold", "Move N" or "Attack #12".</summary>
		public readonly string Label;

		/// <summary>The cell a Move candidate heads for. Null for the other kinds.</summary>
		public readonly CPos? Destination;

		/// <summary>The enemy an Attack candidate targets. Null for the other kinds.</summary>
		public readonly uint? TargetActorId;

		/// <summary>The change in the unit's own HP over the horizon. Zero or negative.</summary>
		public readonly int OwnHpDelta;

		/// <summary>The HP the enemies visible at decision time lost over the horizon, net of any heal.</summary>
		public readonly int EnemyHpLost;

		/// <summary>Whether the unit died within the horizon.</summary>
		public readonly bool UnitDied;

		/// <summary>Own HP delta, plus the enemy weight times the enemy HP lost, less the death penalty when the unit died.</summary>
		public readonly double Score;

		public MonteCarloCandidate(MonteCarloCandidateKind kind, string label, CPos? destination, uint? targetActorId,
			int ownHpDelta, int enemyHpLost, bool unitDied, double score)
		{
			Kind = kind;
			Label = label;
			Destination = destination;
			TargetActorId = targetActorId;
			OwnHpDelta = ownHpDelta;
			EnemyHpLost = enemyHpLost;
			UnitDied = unitDied;
			Score = score;
		}
	}

	/// <summary>The evaluator's result: every candidate it scored, in generation order, and the one it prefers.</summary>
	public sealed class MonteCarloDecision
	{
		public readonly IReadOnlyList<MonteCarloCandidate> Candidates;

		/// <summary>The highest-scoring candidate. A tie goes to the earlier one, so Hold wins a tie.</summary>
		public readonly MonteCarloCandidate Winner;

		public MonteCarloDecision(IReadOnlyList<MonteCarloCandidate> candidates)
		{
			Candidates = candidates;
			Winner = candidates[0];
			foreach (var candidate in candidates)
				if (candidate.Score > Winner.Score)
					Winner = candidate;
		}
	}

	/// <summary>
	/// Scores the candidate orders for one unit by rolling each one out on its own fork of the live world (D9, D10).
	/// Candidates are Hold, an Attack on the nearest enemy visible to the viewer, and one Move of 4 cells directly away
	/// from that enemy along its dominant axis. The Move is omitted when no enemy is visible or the cell is off the map,
	/// so a decision forks at most three times. Each rollout applies its order directly to the fork's actor with
	/// <c>ResolveOrder</c> and then ticks the fork for the horizon. Hold issues no order.
	/// The evaluator issues nothing: the live world and its OrderManager are never touched.
	/// Limitation (v0): only the candidates and the enemy scoring are fog-fair. The rollout simulates hidden actors as
	/// it does visible ones, so the own-HP term can react to threats the viewer cannot see. Hidden actors are not frozen.
	/// </summary>
	public static class MonteCarloSinglePly
	{
		/// <summary>Ticks each rollout runs for (D10).</summary>
		public const int Horizon = 40;

		/// <summary>How far a Move candidate heads, in cells (D10).</summary>
		public const int MoveDistance = 4;

		/// <summary>The weight of enemy HP lost against own HP lost in the score (D10).</summary>
		public const double EnemyHpWeight = 0.25;

		/// <summary>The score cost of the unit dying within the horizon.</summary>
		public const double DeathPenalty = 10000;

		/// <summary>
		/// The cardinal Move that leads away from <paramref name="enemy"/>, along the axis on which the unit is farthest from it.
		/// Ties go to the x axis. Returns the candidate's name and its step.
		/// </summary>
		static (string Name, CVec Step) AwayFrom(CPos unit, CPos enemy)
		{
			var dx = unit.X - enemy.X;
			var dy = unit.Y - enemy.Y;
			if (Math.Abs(dx) >= Math.Abs(dy))
				return dx >= 0 ? ("E", new CVec(MoveDistance, 0)) : ("W", new CVec(-MoveDistance, 0));

			return dy >= 0 ? ("S", new CVec(0, MoveDistance)) : ("N", new CVec(0, -MoveDistance));
		}

		/// <summary>
		/// Scores the candidates for <paramref name="unit"/>, as seen by <paramref name="viewer"/>. Returns null when the
		/// world cannot be forked; <see cref="WorldForks.ForkForRollout"/> has already logged why.
		/// </summary>
		public static MonteCarloDecision Evaluate(OrderManager orderManager, Actor unit, Player viewer, int horizon = Horizon)
		{
			return Evaluate(orderManager, unit, viewer, horizon, w => w.Tick());
		}

		/// <summary>
		/// The evaluator with the tick step injected, so a test can make a tick throw. The rollout scope is entered
		/// around each candidate's order and ticks, and exited in a finally block even when a tick throws.
		/// </summary>
		internal static MonteCarloDecision Evaluate(OrderManager orderManager, Actor unit, Player viewer, int horizon, Action<World> tick)
		{
			if (unit.IsDead)
				throw new ArgumentException("The unit to evaluate is dead.", nameof(unit));

			ArgumentNullException.ThrowIfNull(viewer);

			var world = orderManager.World;

			// Fog fairness (D10): only enemies the viewer can see at decision time are candidates or scored.
			var visibleEnemies = world.Actors
				.Where(a => IsVisibleEnemy(unit, a, viewer))
				.Select(a => (a.ActorID, StartHp: HpOf(a)))
				.ToList();

			var plans = new List<CandidatePlan> { new(MonteCarloCandidateKind.Hold, "Hold", null, null) };
			var nearest = NearestEnemy(unit, visibleEnemies, world);
			if (nearest != null)
			{
				var (name, step) = AwayFrom(unit.Location, world.GetActorById(nearest.Value).Location);
				var cell = unit.Location + step;
				if (world.Map.Contains(cell))
					plans.Add(new(MonteCarloCandidateKind.Move, "Move " + name, cell, null));

				plans.Add(new(MonteCarloCandidateKind.Attack, $"Attack #{nearest.Value}", null, nearest.Value));
			}

			var unitStartHp = HpOf(unit);
			var candidates = new List<MonteCarloCandidate>(plans.Count);
			foreach (var plan in plans)
			{
				var fork = WorldForks.ForkForRollout(orderManager);
				if (fork == null)
					return null;

				var forkUnit = fork.GetActorById(unit.ActorID)
					?? throw new InvalidOperationException("The unit is missing from the rollout fork.");
				var watched = visibleEnemies.ConvertAll(e => (Actor: fork.GetActorById(e.ActorID), e.StartHp));

				// Hold issues no order, so the rollout keeps the unit's current activity and auto-targeting, as a do-nothing would.
				var order = plan.Kind switch
				{
					MonteCarloCandidateKind.Move => new Order("Move", forkUnit, Target.FromCell(fork, plan.Destination.Value), false),
					MonteCarloCandidateKind.Attack => new Order("Attack", forkUnit, Target.FromActor(fork.GetActorById(plan.TargetActorId.Value)), false),
					_ => null,
				};

				WorldForks.EnterRolloutScope();
				try
				{
					if (order != null)
						forkUnit.ResolveOrder(order);
					for (var i = 0; i < horizon; i++)
						tick(fork);
				}
				finally
				{
					WorldForks.ExitRolloutScope();
				}

				var unitDied = forkUnit.IsDead;
				var ownHpDelta = (unitDied ? 0 : HpOf(forkUnit)) - unitStartHp;
				var enemyHpLost = watched.Sum(e => e.StartHp - (e.Actor == null || e.Actor.IsDead ? 0 : HpOf(e.Actor)));
				var score = ownHpDelta + EnemyHpWeight * enemyHpLost - (unitDied ? DeathPenalty : 0);

				candidates.Add(new MonteCarloCandidate(plan.Kind, plan.Label, plan.Destination, plan.TargetActorId,
					ownHpDelta, enemyHpLost, unitDied, score));
			}

			return new MonteCarloDecision(candidates);
		}

		static bool IsVisibleEnemy(Actor unit, Actor other, Player viewer)
		{
			return other.Owner != null
				&& !other.IsDead
				&& other.TraitOrDefault<IHealth>() != null
				&& unit.Owner.RelationshipWith(other.Owner) == PlayerRelationship.Enemy
				&& other.CanBeViewedByPlayer(viewer);
		}

		static uint? NearestEnemy(Actor unit, List<(uint ActorID, int StartHp)> enemies, World world)
		{
			uint? nearest = null;
			var nearestDistance = int.MaxValue;
			foreach (var (actorId, _) in enemies)
			{
				var actor = world.GetActorById(actorId);
				var distance = Math.Abs(actor.Location.X - unit.Location.X) + Math.Abs(actor.Location.Y - unit.Location.Y);
				if (distance < nearestDistance)
				{
					nearest = actorId;
					nearestDistance = distance;
				}
			}

			return nearest;
		}

		static int HpOf(Actor actor)
		{
			return actor.TraitOrDefault<IHealth>()?.HP ?? 0;
		}

		/// <summary>One candidate to roll out, before its score is known.</summary>
		readonly record struct CandidatePlan(MonteCarloCandidateKind Kind, string Label, CPos? Destination, uint? TargetActorId);
	}
}
