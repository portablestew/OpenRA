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
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Mods.Common.AgentSocket
{
	// Schema v1 payload. Wire keys are snake_case (AgentSocketCommands.Json).
	/// <summary>
	/// A notification the engine played (P2.10): its name, such as BuildingCannotPlaceAudio, and the owner it was played for, which is
	/// null when the caller gave no player. Tick is the world tick it was played on. Unit barks are voices, not notifications, so they
	/// are not reported. A notification that announces a finished item (ConstructionComplete for a structure, UnitReady for a unit,
	/// as the rules name them) also names it in Item, and QueueType names the queue that built it. A finished structure waits for
	/// placement: PlacementRequired is true, and place puts it. A finished unit has its Cell, the cell it was built on. Those keys are
	/// left out of the JSON otherwise. A NewOptions notification of the local player names in Options the options that became buildable
	/// since the last one (empty when none); an ally's has no Options. A finished structure of the local player also names in Placeable
	/// every finished queue head the player can place now, this one included, and BlockedBy names it when items wait behind it. An
	/// ally's ConstructionComplete has neither. A NoBuild a queue plays for a finished unit it cannot put out names the Item, its
	/// QueueType, the Reason (exit_blocked, producer_paused, producer_reserved or no_producer), the Producer to set a rally point on
	/// when one is known, and BlockedBy when items wait behind it. An InsufficientFunds names the Item it could not pay for, the
	/// Reason (production or repair), the Cost (a production item's total, or a repair step's), the production's RemainingCost, the
	/// Funds (cash and stored resources) and ShortBy. The engine throttles InsufficientFunds to one per 30 seconds of real time.
	/// A LevelUp names the Item that gained a level, its Actor (the unit's id) and the Level it reached, the new rank, 1-based.
	/// A key with no known value is left out.
	/// </summary>
	public sealed record NotificationV1(string Event, int Tick, string Name, string Owner,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string Item = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[] Cell = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PlacementRequired = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string QueueType = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[] Options = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string BlockedBy = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[] Placeable = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string Reason = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Cost = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RemainingCost = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Funds = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ShortBy = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? Producer = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? Actor = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Level = null);

	/// <summary>
	/// What the engine knows about a notification it is about to play: the notification's name and owner, which Raise matches on,
	/// and the keys it adds. A null key is not known and is left out.
	/// </summary>
	public sealed record NotificationContext(string Name, Player Owner, string Item = null, string QueueType = null,
		string Reason = null, int? Cost = null, int? RemainingCost = null, int? Funds = null, int? ShortBy = null,
		uint? Producer = null, string BlockedBy = null,
		uint? Actor = null, int? Level = null);

	public static class AgentNotifications
	{
		// The notification the production that PlaceBuilding plays when the buildable options change. Its name comes from the rules.
		const string NewOptionsName = "NewOptions";

		// The finished item a production queue is announcing: its type, the owner that built it, and the queue that built it. Set only
		// while the queue plays its ready notification, so Raise can name the item. The engine plays the sound synchronously, so Raise sees it.
		static (Player Owner, ActorInfo Item, string QueueType)? finishing;

		// The options the local player could build at the last NewOptions, so the next one can name only what became buildable.
		static (Player Owner, HashSet<string> Names)? lastBuildable;

		// A unit's ready notification, held until the unit exists. The production creates a unit at frame end, after its ready
		// sound plays, so the cell is known only then: Produced sends the notification with it.
		static NotificationV1 heldUnit;

		// The context of the notification the engine plays next, set only around that call. Kept apart from finishing, whose item
		// Raise would hold as a unit's ready notification.
		static NotificationContext context;

		/// <summary>Sets the context for the notification played next. Pair with Explained. Does nothing when no connection is listening.</summary>
		public static void Explain(NotificationContext explained)
		{
			if (AgentEvents.Enabled)
				context = explained;
		}

		/// <summary>Ends the context Explain set, whether or not its notification played.</summary>
		public static void Explained()
		{
			context = null;
		}

		/// <summary>
		/// Explains the NoBuild a queue plays when it cannot put out a finished unit. The producers are found as the classic queue
		/// finds them: the owner's enabled producers of the unit's production type, primary first, then the newest. With none the
		/// item was cancelled; when all are paused or reserved nothing could take it; otherwise no exit was free.
		/// </summary>
		public static void ExplainBlocked(ProductionQueue queue, ActorInfo unit)
		{
			if (!AgentEvents.Enabled)
				return;

			var owner = queue.Actor.Owner;
			var bi = unit.TraitInfo<BuildableInfo>();
			var type = owner.PlayerActor.Trait<DeveloperMode>().AllTech ? queue.Info.Type : (bi.BuildAtProductionType ?? queue.Info.Type);
			var playerQueue = queue.Actor == owner.PlayerActor;
			var producers = owner.World.ActorsWithTrait<Production>()
				.Where(p => p.Actor.Owner == owner && (playerQueue || p.Actor == queue.Actor)
					&& !p.Trait.IsTraitDisabled && p.Trait.Info.Produces.Contains(type))
				.OrderByDescending(p => p.Actor.IsPrimaryBuilding())
				.ThenByDescending(p => p.Actor.ActorID)
				.ToList();

			var unpaused = producers.Where(p => !p.Trait.IsTraitPaused).ToList();
			var free = unpaused.FirstOrDefault(p => !Reservable.IsReserved(p.Actor));
			string reason;
			Actor producer = null;
			if (producers.Count == 0)
				reason = "no_producer";
			else if (unpaused.Count == 0)
				reason = "producer_paused";
			else if (free.Actor == null)
				(reason, producer) = ("producer_reserved", unpaused[0].Actor);
			else
				(reason, producer) = ("exit_blocked", free.Actor);

			// A cancelled item holds nothing; one still waiting at the head holds the items behind it.
			var queued = queue.AllQueued().ToList();
			var blockedBy = producers.Count > 0 && queued.Count > 1 && queued[0].Item == unit.Name ? unit.Name : null;

			Explain(new NotificationContext(queue.Info.BlockedAudio, owner, unit.Name, queue.Info.Type, reason,
				Producer: producer?.ActorID, BlockedBy: blockedBy));
		}

		/// <summary>
		/// Explains the InsufficientFunds a production item's payment may play: set only when the funds fall short of the payment.
		/// ShortBy is what the rest of the item costs beyond the funds, not this tick's slice.
		/// </summary>
		public static void ExplainProductionFunds(PlayerResources resources, ProductionItem item, int payment)
		{
			if (!AgentEvents.Enabled)
				return;

			var funds = resources.GetCashAndResources();
			if (funds >= payment)
				return;

			Explain(new NotificationContext(resources.Info.InsufficientFundsNotification, item.Queue.Actor.Owner, item.Item,
				item.Queue.Info.Type, "production", item.TotalCost, item.RemainingCost, funds, Math.Max(0, item.RemainingCost - funds)));
		}

		/// <summary>Explains the InsufficientFunds a repair step may play: set only when the funds fall short of the step's cost.</summary>
		public static void ExplainRepairFunds(Player owner, string item, int cost)
		{
			if (!AgentEvents.Enabled)
				return;

			var resources = owner.PlayerActor.Trait<PlayerResources>();
			var funds = resources.GetCashAndResources();
			if (funds >= cost)
				return;

			Explain(new NotificationContext(resources.Info.InsufficientFundsNotification, owner, item,
				Reason: "repair", Cost: cost, Funds: funds, ShortBy: cost - funds));
		}

		/// <summary>Explains the LevelUp a unit's experience plays when it gains a level: the unit's type, its actor id and the new rank.</summary>
		public static void ExplainLevelUp(Actor self, string notification, int level)
		{
			if (!AgentEvents.Enabled)
				return;

			Explain(new NotificationContext(notification, self.Owner, self.Info.Name, Actor: self.ActorID, Level: level));
		}

		/// <summary>Marks the item a production queue has finished, and the queue it is in, for the ready notification it plays next. Pair with Finished.</summary>
		public static void Finishing(Player owner, ActorInfo item, string queueType)
		{
			finishing = (owner, item, queueType);
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

			// A notification the engine explained carries what it knew. Matched on name and owner, so a notification played
			// meanwhile for another reason never takes it.
			if (context is { } explained && explained.Name == name && explained.Owner == player)
			{
				AgentEvents.Raise(new NotificationV1("notification", tick, name, player?.PlayerName, explained.Item, null, null,
					explained.QueueType, null, explained.BlockedBy, null, explained.Reason, explained.Cost, explained.RemainingCost,
					explained.Funds, explained.ShortBy, explained.Producer, explained.Actor, explained.Level));
				return;
			}

			var item = finishing is { } announced && announced.Owner == player ? announced.Item : null;
			var queueType = finishing is { } queue && queue.Owner == player ? queue.QueueType : null;

			// A unit's cell is known only once it exists, so the notification waits for Produced.
			if (item != null && !item.HasTraitInfo<BuildingInfo>())
			{
				heldUnit = new NotificationV1("notification", tick, name, player?.PlayerName, item.Name, null, null, queueType);
				return;
			}

			var options = name == NewOptionsName && player != null && player == local ? BecameBuildable(player) : null;

			// A finished structure of the local player names what place accepts now, and the head it holds items behind.
			string[] placeable = null;
			string blockedBy = null;
			if (item != null && player != null && player == local)
			{
				var heads = AgentProduction.FinishedHeads(local.World, player);
				placeable = heads.Select(h => h.Head.Item).ToArray();
				var holder = heads.Where(h => h.Queue.Info.Type == queueType && h.Head.Item == item.Name)
					.Select(h => h.Queue).FirstOrDefault();
				blockedBy = holder != null && holder.AllQueued().Skip(1).Any() ? item.Name : null;
			}

			AgentEvents.Raise(new NotificationV1("notification", tick, name, player?.PlayerName, item?.Name,
				null, item == null ? null : true, queueType, options, blockedBy, placeable));
		}

		/// <summary>
		/// The options the player can build now that the last NewOptions did not list, in palette order, and the set it leaves for
		/// the next one. The first one lists every option buildable then. Must run on the game thread.
		/// </summary>
		static string[] BecameBuildable(Player player)
		{
			var now = AgentProduction.Buildable(player).Select(o => o.Name).ToArray();
			var before = lastBuildable is { } last && last.Owner == player ? last.Names : [];
			lastBuildable = (player, now.ToHashSet());
			return now.Where(n => !before.Contains(n)).ToArray();
		}
	}
}
