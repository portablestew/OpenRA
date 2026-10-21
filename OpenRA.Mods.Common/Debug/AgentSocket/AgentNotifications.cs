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

using System.Text.Json.Serialization;
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Mods.Common.AgentSocket
{
	// Schema v1 payload. Wire keys are snake_case (AgentSocketCommands.Json).
	/// <summary>
	/// A notification the engine played (P2.10): its name, such as BuildingCannotPlaceAudio, and the owner it was played for, which is
	/// null when the caller gave no player. Tick is the world tick it was played on. Unit barks are voices, not notifications, so they
	/// are not reported. A notification that announces a finished item (ConstructionComplete for a structure, UnitReady for a unit,
	/// as the rules name them) also names it in Item. A finished structure waits for placement: PlacementRequired is true, and place
	/// puts it. A finished unit has its Cell, the cell it was built on. Those keys are left out of the JSON otherwise. A NewOptions
	/// notification names no options: production lists the ones that became buildable, because it reports changed options.
	/// </summary>
	public sealed record NotificationV1(string Event, int Tick, string Name, string Owner,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string Item = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[] Cell = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PlacementRequired = null);

	public static class AgentNotifications
	{
		// The finished item a production queue is announcing: its type, and the owner that built it. Set only while the queue
		// plays its ready notification, so Raise can name the item. The engine plays the sound synchronously, so Raise sees it.
		static (Player Owner, ActorInfo Item)? finishing;

		// A unit's ready notification, held until the unit exists. The production creates a unit at frame end, after its ready
		// sound plays, so the cell is known only then: Produced sends the notification with it.
		static NotificationV1 heldUnit;

		/// <summary>Marks the item a production queue has finished, for the ready notification it plays next. Pair with Finished.</summary>
		public static void Finishing(Player owner, ActorInfo item)
		{
			finishing = (owner, item);
		}

		/// <summary>Ends the mark Finishing set, once the ready notification has played.</summary>
		public static void Finished()
		{
			finishing = null;
		}

		/// <summary>
		/// Sends a held unit's ready notification with its cell, once the production trait has created the unit. Called for every
		/// unit a production creates, and sends nothing unless it is the one a ready notification held.
		/// </summary>
		public static void Produced(Actor produced)
		{
			if (heldUnit is not { } held || produced.Owner.PlayerName != held.Owner || produced.Info.Name != held.Item)
				return;

			heldUnit = null;
			int[] cell = null;
			if (produced.OccupiesSpace != null)
				cell = [produced.Location.X, produced.Location.Y];

			AgentEvents.Raise(held with { Cell = cell });
		}

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

			// A nameless notification, such as the stop that RA's structures play, has no name an agent can match on.
			if (name is null)
				return;

			var tick = player?.World.WorldTick ?? AgentWorldBridge.Active?.WorldTick ?? 0;
			var finished = finishing is { } announced && announced.Owner == player ? announced.Item : null;

			// A unit's cell is known only once it exists, so the notification waits for Produced.
			if (finished != null && !finished.HasTraitInfo<BuildingInfo>())
			{
				heldUnit = new NotificationV1("notification", tick, name, player?.PlayerName, finished.Name);
				return;
			}

			AgentEvents.Raise(new NotificationV1("notification", tick, name, player?.PlayerName, finished?.Name,
				null, finished == null ? null : true));
		}
	}
}
