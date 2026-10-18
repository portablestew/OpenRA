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
	// Schema v1 payload. Wire keys are snake_case (AgentSocketCommands.Json).
	/// <summary>
	/// A notification the engine played (P2.10): its name, such as BuildingCannotPlaceAudio, and the owner it was played for, which is
	/// null when the caller gave no player. Tick is the world tick it was played on. Unit barks are voices, not notifications, so they
	/// are not reported.
	/// </summary>
	public sealed record NotificationV1(string Event, int Tick, string Name, string Owner);

	public static class AgentNotifications
	{
		/// <summary>
		/// Raises a notification event for a notification the engine plays. Sound.PlayNotification calls it before its guards, so it sees
		/// every notification, whether or not the local player hears it. Raised only when a subscriber is listening.
		/// </summary>
		public static void Raise(Player player, string name)
		{
			if (!AgentEvents.Enabled)
				return;

			var tick = player?.World.WorldTick ?? AgentWorldBridge.Active?.WorldTick ?? 0;
			AgentEvents.Raise("notification", player?.PlayerName, new NotificationV1("notification", tick, name, player?.PlayerName));
		}
	}
}
