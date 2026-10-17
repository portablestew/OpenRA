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

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// Raises the activity lifecycle events (P1.7). An activity starts once, then completes or is canceled once. Trait-level
	/// aborts pass a reason, so a silent abort shows up as canceled. Callers test AgentEvents.HasSubscribers before they call
	/// Raise, as Move does for its path event. Raise only reads the actor, so it touches no RNG and no world state.
	/// </summary>
	public static class AgentActivityEvents
	{
		/// <summary>
		/// Raises an activity event for self. kind is move, transform or attack. state is started, completed or canceled.
		/// reason is null unless the activity says why it ended.
		/// </summary>
		public static void Raise(Actor self, string kind, string state, string reason = null)
		{
			AgentEvents.Raise("activity", self.Owner.PlayerName, new ActivityV1("activity", self.World.WorldTick,
				self.ActorID, self.Info.Name, self.Owner.PlayerName, kind, state, reason));
		}
	}
}
