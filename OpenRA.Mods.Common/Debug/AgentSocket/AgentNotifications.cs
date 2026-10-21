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
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace OpenRA.Mods.Common.AgentSocket
{
	// Schema v1 payload. Wire keys are snake_case (AgentSocketCommands.Json).
	/// <summary>
	/// A notification the engine played (P2.10): its name, such as BuildingCannotPlaceAudio, and the owner it was played for, which is
	/// null when the caller gave no player. Tick is the world tick it was played on. Unit barks are voices, not notifications, so they
	/// are not reported. Options is set only on a NewOptions notification for the local player or an ally: the options that became
	/// buildable since that player's previous NewOptions, in palette order. It is left out of the JSON otherwise.
	/// </summary>
	public sealed record NotificationV1(string Event, int Tick, string Name, string Owner,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProductionOptionV1[] Options = null);

	public static class AgentNotifications
	{
		// The rules name of the notification that a player's buildable options changed, as PlaceBuilding plays it in every mod.
		const string NewOptions = "NewOptions";

		// The options each player could build when its last NewOptions was played. A world's players are new objects, so the
		// table starts empty in each world. Only allied players are tracked, because only they report options.
		static readonly ConditionalWeakTable<Player, HashSet<string>> BuildableAtLastNewOptions = new();

		/// <summary>
		/// Raises a notification event for a notification the engine plays. Sound.PlayNotification calls it before its guards, so it
		/// sees every notification, whether or not the local player hears it. Only the local player's and its allies' notifications
		/// are reported: an opponent's economy and tech state must not leak, and they must not wake a wait. The cash counter's tick
		/// notifications are dropped because cash is in the state and production replies. Raised only when a connection is listening.
		/// </summary>
		public static void Raise(Player player, string name)
		{
			if (!AgentEvents.Enabled)
				return;

			// The counter plays one of these per animation tick, and every reply already carries cash.
			if (name is "CashTickUp" or "CashTickDown")
				return;

			// A null player is a system notification. A player is reported only to its own side.
			var local = player?.World.LocalPlayer;
			if (player != null && local != null && !local.IsAlliedWith(player))
				return;

			var tick = player?.World.WorldTick ?? AgentWorldBridge.Active?.WorldTick ?? 0;
			var options = name == NewOptions && IsAlliedWithLocal(player) ? NewlyBuildable(player) : null;

			// A nameless notification, such as the stop that RA's structures play, has no name an agent can match on.
			if (name is null)
				return;

			AgentEvents.Raise(new NotificationV1("notification", tick, name, player?.PlayerName, options));
		}

		// Tech progression is reported only for the local player and allies, so an enemy's unlocks do not leak (D-4).
		static bool IsAlliedWithLocal(Player player)
		{
			var local = player?.World.LocalPlayer;
			return local != null && local.IsAlliedWith(player);
		}

		// The options buildable now that were not buildable at this player's previous NewOptions. The first one in a world lists them all.
		static ProductionOptionV1[] NewlyBuildable(Player player)
		{
			var now = AgentProduction.Buildable(player);
			var before = BuildableAtLastNewOptions.TryGetValue(player, out var previous) ? previous : new HashSet<string>();
			BuildableAtLastNewOptions.AddOrUpdate(player, now.Select(o => o.Name).ToHashSet());

			return now.Where(o => !before.Contains(o.Name)).ToArray();
		}
	}
}
