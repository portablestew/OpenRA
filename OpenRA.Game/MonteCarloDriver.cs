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
using OpenRA.Network;
using OpenRA.Traits;

namespace OpenRA
{
	/// <summary>
	/// The live driver for the Monte Carlo mode. Each logic tick, while the mode is on, it checks every 100 ticks whether
	/// the selection is one eligible unit and whether a decision may run. It holds no actor or world references: it keeps
	/// only the tick of its last decision, so a rewind or a new game can never leave it with a stale object.
	/// A due decision evaluates the unit, logs every candidate, and issues the winner on the live world. Hold issues nothing.
	/// </summary>
	public static class MonteCarloDriver
	{
		/// <summary>The logic ticks between decisions.</summary>
		public const int CadenceTicks = 100;

		const int NoDecisionYet = int.MinValue;

		static int lastDecisionTick = NoDecisionYet;

		/// <summary>
		/// Whether the unit has an agent-socket order still in flight. The socket lives in OpenRA.Mods.Common, which
		/// OpenRA.Game cannot reference, so the mod wires this up when its world loads. Until it does, the driver fails
		/// closed and decides nothing for any unit.
		/// </summary>
		public static Func<Actor, bool> HasPendingAgentOrder = FailClosed;

		/// <summary>The default pending check. It reports every unit as pending, so nothing is decided until the mod wires the real check.</summary>
		internal static bool FailClosed(Actor unit)
		{
			return true;
		}

		/// <summary>Called once per logic tick. Does nothing unless the mode is on and a decision is due.</summary>
		public static void Tick(OrderManager orderManager)
		{
			if (!MonteCarloMode.Enabled || orderManager?.World == null)
				return;

			var world = orderManager.World;
			if (!TakeDue(world.WorldTick))
				return;

			if (!ShouldDecide(world, out var unit, out var reason))
			{
				Log.Write("debug", $"[MonteCarlo] no decision at tick {world.WorldTick}: {reason}");
				return;
			}

			var decidedFrom = unit.Location;
			MonteCarloDecision decision;
			try
			{
				decision = MonteCarloSinglePly.Evaluate(orderManager, unit, world.LocalPlayer);
			}
			catch (Exception e)
			{
				// The rollout scope is exited in a finally block, and the fork is discarded, so the live world is unaffected.
				Log.Write("debug", $"[MonteCarlo] decision abandoned at tick {world.WorldTick}: the evaluator threw {e.GetType().Name}: {e.Message}");
				return;
			}

			// A null decision means the fork failed. ForkForRollout has already logged why.
			if (decision == null)
				return;

			LogDecision(world.WorldTick, decision);
			Issue(world, unit, decidedFrom, decision);
		}

		static void LogDecision(int worldTick, MonteCarloDecision decision)
		{
			foreach (var candidate in decision.Candidates)
				Log.Write("debug", $"[MonteCarlo] tick {worldTick} candidate {candidate.Label}: score {candidate.Score:F2} " +
					$"(own HP {candidate.OwnHpDelta}, enemy HP lost {candidate.EnemyHpLost}, died {candidate.UnitDied})");

			Log.Write("debug", $"[MonteCarlo] tick {worldTick} chose {decision.Winner.Label}");
		}

		/// <summary>
		/// Issues the decision's winner for the unit on the live world, through World.IssueOrder. Hold issues nothing. The
		/// order is rebuilt from the winner's cell or actor ID, never taken from a fork. The guards run again first, because
		/// the unit or the selection may have changed since the evaluation. Returns true when an order was issued.
		/// </summary>
		internal static bool Issue(World world, Actor unit, CPos decidedFrom, MonteCarloDecision decision)
		{
			if (!ShouldDecide(world, out var current, out var reason) || current != unit)
			{
				Log.Write("debug", $"[MonteCarlo] decision skipped at tick {world.WorldTick}: {reason ?? "the selection changed"}");
				return false;
			}

			if (unit.Location != decidedFrom)
			{
				Log.Write("debug", $"[MonteCarlo] decision skipped at tick {world.WorldTick}: the unit moved since the evaluation");
				return false;
			}

			var winner = decision.Winner;
			var order = Rebuild(world, unit, winner, out reason);
			if (order == null)
			{
				Log.Write("debug", $"[MonteCarlo] no order at tick {world.WorldTick} for {winner.Label}: {reason}");
				return false;
			}

			world.IssueOrder(order);
			Log.Write("debug", $"[MonteCarlo] issued {winner.Label} for unit #{unit.ActorID} at tick {world.WorldTick}");
			return true;
		}

		/// <summary>Builds the live order for a candidate. Returns null, with the reason, when the candidate issues nothing.</summary>
		static Order Rebuild(World world, Actor unit, MonteCarloCandidate candidate, out string reason)
		{
			reason = null;
			switch (candidate.Kind)
			{
				case MonteCarloCandidateKind.Move:
					if (!world.Map.Contains(candidate.Destination.Value))
					{
						reason = "the destination is off the map";
						return null;
					}

					return new Order("Move", unit, Target.FromCell(world, candidate.Destination.Value), false);

				case MonteCarloCandidateKind.Attack:
					var target = world.GetActorById(candidate.TargetActorId.Value);
					if (target == null || target.IsDead || !target.IsInWorld)
					{
						reason = "the target is gone";
						return null;
					}

					return new Order("Attack", unit, Target.FromActor(target), false);

				default:
					reason = "Hold issues no order";
					return null;
			}
		}

		/// <summary>
		/// The guards a decision must pass. Returns the unit to decide for, or false with the reason it may not. Every
		/// refusal is a guard the rollout world, the eligibility rule, the selection or the agent socket can trigger.
		/// </summary>
		internal static bool ShouldDecide(World world, out Actor unit, out string reason)
		{
			unit = null;

			if (!MonteCarloMode.Enabled)
			{
				reason = "the mode is off";
				return false;
			}

			// A fork shares the live OrderManager, so IsEligible would pass for it. It must never decide or issue anything.
			if (world.IsRollout)
			{
				reason = "a rollout world never decides";
				return false;
			}

			if (!WorldForks.IsEligible(world.OrderManager, out reason))
				return false;

			if (MonteCarloTarget.Resolve(world, out unit) != MonteCarloState.Active)
			{
				unit = null;
				reason = "the selection is not one eligible unit";
				return false;
			}

			if (HasPendingAgentOrder(unit))
			{
				unit = null;
				reason = "the unit has an agent-socket order in flight";
				return false;
			}

			reason = null;
			return true;
		}

		/// <summary>
		/// Advances the cadence for a logic tick at <paramref name="worldTick"/>. Returns true when a decision is due and
		/// records it. The first tick is due at once. A tick earlier than the last decision means the world was rewound or
		/// replaced, so the schedule restarts from it rather than waiting out a stale gap.
		/// </summary>
		internal static bool TakeDue(int worldTick)
		{
			if (lastDecisionTick != NoDecisionYet && worldTick >= lastDecisionTick && worldTick - lastDecisionTick < CadenceTicks)
				return false;

			lastDecisionTick = worldTick;
			return true;
		}

		/// <summary>Forgets the last decision, so the next tick is due. Tests use this to start from a clean schedule.</summary>
		internal static void ForgetDecisions()
		{
			lastDecisionTick = NoDecisionYet;
		}
	}
}
