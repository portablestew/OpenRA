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

using System.Collections.Generic;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// The most recent hit each attacker landed on one of ours (P2.7). The threat list bumps a recent attacker to the front. Hits are
	/// recorded whether or not anyone subscribes, since the threat list reads them from the state digest. The record belongs to one
	/// world: a different world clears it, since ActorIDs are compared only within their own world.
	/// </summary>
	public static class AgentRecentDamage
	{
		/// <summary>How many ticks a hit counts as recent: 4 seconds at 25 ticks per second.</summary>
		public const int RecentTicks = 100;

		sealed record Hit(uint Victim, int Tick);

		static World recordWorld;
		static readonly Dictionary<uint, Hit> Hits = [];

		/// <summary>Records that attacker hit victim on the current tick. Called from the damage notify on the game thread.</summary>
		public static void Record(Actor victim, Actor attacker)
		{
			Switch(victim.World);
			Hits[attacker.ActorID] = new Hit(victim.ActorID, victim.World.WorldTick);
		}

		/// <summary>The friendly that attacker hit most recently, if that hit is within RecentTicks, or null.</summary>
		public static uint? RecentVictim(World world, uint attackerId)
		{
			Switch(world);
			if (!Hits.TryGetValue(attackerId, out var hit))
				return null;

			return world.WorldTick - hit.Tick <= RecentTicks ? hit.Victim : null;
		}

		/// <summary>Clears the hits when the world changes, so they never refer to another world's ActorIDs.</summary>
		static void Switch(World world)
		{
			if (recordWorld == world)
				return;

			recordWorld = world;
			Hits.Clear();
		}
	}
}
