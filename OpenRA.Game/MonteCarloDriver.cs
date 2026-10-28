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

namespace OpenRA
{
	/// <summary>
	/// The live driver for the Monte Carlo mode. Each logic tick, while the mode is on, it checks every 100 ticks whether
	/// the selection is one eligible unit and whether a decision may run. It holds no actor or world references: it keeps
	/// only the tick of its last decision, so a rewind or a new game can never leave it with a stale object.
	/// This step stops at the guards. A due decision is logged, and it neither evaluates nor issues an order yet.
	/// </summary>
	public static class MonteCarloDriver
	{
		/// <summary>The logic ticks between decisions.</summary>
		public const int CadenceTicks = 100;

		const int NoDecisionYet = int.MinValue;

		static int lastDecisionTick = NoDecisionYet;

		/// <summary>
		/// Whether the unit has an agent-socket order still in flight. The socket lives in OpenRA.Mods.Common, which
		/// OpenRA.Game cannot reference, so the mod must wire this up. Until it does, the driver fails closed and decides
		/// nothing for any unit.
		/// </summary>
		public static Func<Actor, bool> HasPendingAgentOrder = _ => true;

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

			// Step 1 stops here. Step 2 evaluates the unit and logs every candidate's score.
			Log.Write("debug", $"[MonteCarlo] decision due at tick {world.WorldTick} for unit #{unit.ActorID}; the evaluator is not wired yet");
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
