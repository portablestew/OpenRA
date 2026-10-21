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

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// The game clock every socket tick budget reads. A tick is the only unit a game-time request uses: 1000 ticks is
	/// the longest wait a request may ask for. Each read runs on the game thread, so it is safe while the world is paused: the
	/// game loop performs delayed actions on every logic tick, paused or not. PollMs is the wait mechanism's step, not a budget;
	/// a request never ends because host time passed.
	/// </summary>
	public static class AgentGameClock
	{
		/// <summary>The longest wait a request may ask for, in game ticks.</summary>
		public const int MaxWaitTicks = 1000;

		/// <summary>How long a parked wait sleeps between clock reads, in milliseconds. This is the wait mechanism, not a budget.</summary>
		public const int PollMs = 20;

		/// <summary>One clock reading: the live world, its tick, and whether it is paused. World is null when none is loaded.</summary>
		public readonly record struct Reading(World World, int Tick, bool Paused);

		/// <summary>Reads the live world's clock on the game thread. The world may be null, so a caller can report it rather than throw.</summary>
		public static Reading Read()
		{
			return AgentMarshal.ExecuteOnGameThread(() =>
			{
				var world = AgentWorldBridge.Active;
				return world == null ? new Reading(null, 0, true) : new Reading(world, world.WorldTick, world.Paused);
			});
		}
	}

	/// <summary>Thrown when a wait cannot spend game ticks because the game is paused. A paused wait ends at once; it does not hold.</summary>
	public sealed class AgentPausedException : Exception
	{
		public AgentPausedException()
			: base("the game paused before the request completed") { }
	}
}
