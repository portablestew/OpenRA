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
using System.Linq;

namespace OpenRA.Mods.Common.AgentSocket
{
	// Schema v1, additive. Wire keys are snake_case (AgentSocketCommands.Json).

	/// <summary>
	/// The reply to the montecarlo and select commands. Status is the label's state (off, idle or active), from
	/// MonteCarloTarget.Resolve. Unit is the active unit's type, and null unless the status is active. Selection is the
	/// sorted ids of the selected actors.
	/// </summary>
	public sealed record MonteCarloV1(bool Ok, string Cmd, bool Enabled, string Status, string Unit, uint[] Selection);

	/// <summary>
	/// The two local, unsynced commands that let the agent walk the Monte Carlo label through its states without a key press.
	/// montecarlo sets the mode on or off, as the hotkey does. select sets the world selection to one live actor, as a click
	/// does, and issues no order. Both change no synced state. Must run on the game thread.
	/// </summary>
	public static class AgentMonteCarlo
	{
		/// <summary>Sets the mode to on or off. A request for the state already held changes nothing.</summary>
		public static MonteCarloV1 SetMode(World world, string cmd, bool enable)
		{
			if (MonteCarloMode.Enabled != enable)
				MonteCarloMode.Toggle();

			return Status(world, cmd);
		}

		/// <summary>Selects one live actor, replacing the selection. Unknown, dead or removed actors are an error.</summary>
		public static MonteCarloV1 Select(World world, string cmd, uint actorId)
		{
			var actor = world.GetActorById(actorId);
			if (actor == null || actor.IsDead || !actor.IsInWorld)
				throw new InvalidOperationException($"no live actor {actorId} in the world");

			world.Selection.Combine(world, [actor], false, false);
			return Status(world, cmd);
		}

		static MonteCarloV1 Status(World world, string cmd)
		{
			var state = MonteCarloTarget.Resolve(world, out var target);
			var selection = world.Selection.Actors.Select(a => a.ActorID).Order().ToArray();

			return new MonteCarloV1(true, cmd, MonteCarloMode.Enabled, state.ToString().ToLowerInvariant(),
				target?.Info.Name, selection);
		}
	}
}
