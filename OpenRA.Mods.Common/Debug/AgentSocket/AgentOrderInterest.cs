#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version.
 * For more information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using OpenRA.Activities;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// The actors a socket order is driving. Path and move activity events are raised only for them, so autonomous work (a
	/// harvester's shuttle, a bot's manoeuvres) does not fill the turn and does not wake a wait. Interest is taken when a
	/// socket order registers its await, and ends when the actor's activity queue empties, the actor dies, or the world
	/// changes. An interest also lapses after <see cref="MaxTicks"/>, so an actor that never goes idle cannot report forever.
	/// </summary>
	public static class AgentOrderInterest
	{
		/// <summary>Ticks an interest lasts at most, in game ticks (25 per second), even while the actor stays busy.</summary>
		public const int MaxTicks = 5000;

		/// <summary>Ticks after issue during which an idle actor is kept: the order resolves and starts its activity then.</summary>
		const int StartGraceTicks = 3;

		static readonly Dictionary<(World World, uint Actor), int> Interests = [];

		/// <summary>Takes interest in the subject of a socket order. Must run on the game thread.</summary>
		public static void Track(Order order)
		{
			Track(order.Subject);
		}

		/// <summary>
		/// Takes interest in an actor without an order. Called by <see cref="Track(Order)"/>, and by tests that queue an
		/// activity directly. Must run on the game thread.
		/// </summary>
		public static void Track(Actor self)
		{
			Interests[(self.World, self.ActorID)] = self.World.WorldTick;
		}

		/// <summary>
		/// True when the actor's path or move activity belongs to a socket order. Must run on the game thread.
		/// </summary>
		public static bool Tracked(Actor self)
		{
			return Interests.ContainsKey((self.World, self.ActorID));
		}

		/// <summary>
		/// Ends interest in an actor whose Move activity was its whole queue: the order that started it is finished. A Move
		/// that is a child of an order activity (an AttackMove's travel), or that leaves a queued order behind, keeps the
		/// interest. Must run on the game thread.
		/// </summary>
		public static void MoveEnded(Actor self, Activity activity)
		{
			if (ReferenceEquals(self.CurrentActivity, activity) && activity.NextActivity == null)
				Interests.Remove((self.World, self.ActorID));
		}

		/// <summary>
		/// Releases interests whose actor has gone idle or died, or that have outlived <see cref="MaxTicks"/>. Called on every
		/// world tick by <see cref="AgentWorldBridge"/>, so a finished order stops reporting even when no further event arrives.
		/// Must run on the game thread.
		/// </summary>
		public static void Tick(World world)
		{
			if (Interests.Count == 0)
				return;

			List<(World World, uint Actor)> released = null;
			foreach (var (key, taken) in Interests)
			{
				// Another world's interest is dropped by Clear; leave it alone here.
				if (key.World != world)
					continue;

				var age = world.WorldTick - taken;
				var actor = world.GetActorById(key.Actor);
				if (age > MaxTicks || actor == null || actor.IsDead || (age > StartGraceTicks && actor.CurrentActivity == null))
					(released ??= []).Add(key);
			}

			if (released == null)
				return;

			foreach (var key in released)
				Interests.Remove(key);
		}

		/// <summary>Drops every interest. A world change replaces every tracked actor. Must run on the game thread.</summary>
		public static void Clear()
		{
			Interests.Clear();
		}
	}
}
