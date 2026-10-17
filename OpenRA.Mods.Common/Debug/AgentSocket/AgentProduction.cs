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
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Mods.Common.AgentSocket
{
	// Schema v1, additive. Wire keys are snake_case (AgentSocketCommands.Json).

	/// <summary>The production command: the local player's queues with cash and power, so one call answers what can be built.</summary>
	public sealed record ProductionV1(bool Ok, string Cmd, string Player, int Cash, int Resources, PowerV1 Power, ProductionQueueV1[] Queues);

	/// <summary>
	/// One ProductionQueue the local player owns and can build with. Actor is the actor that holds the queue: the player
	/// actor for the RA queues. Producers are the owned actors whose Production trait produces the queue type, where an item is built.
	/// Options are the items the queue lists, in build palette order. Items are the live queue, head first. Enabled is
	/// false while the queue's production trait is disabled or the faction is wrong. Blocker is the name of the head item when
	/// it is finished and waits for placement, which holds the items behind it; null otherwise (P1.12).
	/// </summary>
	public sealed record ProductionQueueV1(uint Actor, uint[] Producers, string QueueType, bool Enabled, ProductionOptionV1[] Options, ProductionItemV1[] Items,
		string Blocker);

	/// <summary>
	/// Buildable is the engine's ProductionQueue.BuildableItems set: the item can be queued now. An option listed with
	/// buildable false is visible but not buildable, such as one whose prerequisites are not met. PowerAmount is
	/// PowerInfo.Amount: positive provides power, negative drains it, and it is null without a Power trait. CanAfford compares
	/// Cost with Cash + Resources, as PlayerResources.GetCashAndResources does.
	/// </summary>
	public sealed record ProductionOptionV1(string Name, int Cost, int? PowerAmount, string[] Prerequisites, bool Buildable, bool CanAfford);

	/// <summary>
	/// Progress is the built share of the build time, 0 to 100. State is "building" for the head of the queue, "paused",
	/// "done" when it waits for placement, and "waiting" otherwise.
	/// </summary>
	public sealed record ProductionItemV1(string Name, int Cost, int RemainingCost, int Progress, string State);

	/// <summary>Builds the production response. Must run on the game thread: it reads live world state.</summary>
	public static class AgentProduction
	{
		public static ProductionV1 List(World world)
		{
			var player = world.LocalPlayer ?? throw new InvalidOperationException("There is no local player");
			var resources = player.PlayerActor.TraitOrDefault<PlayerResources>();
			var cash = resources == null ? 0 : resources.GetCashAndResources();

			var queues = world.ActorsWithTrait<ProductionQueue>()
				.Where(t => t.Actor.Owner == player && t.Actor.IsInWorld && !t.Actor.IsDead)
				.OrderBy(t => t.Actor.ActorID)
				.ThenBy(t => t.Trait.Info.Type, StringComparer.Ordinal)
				.Select(t => QueueOf(world, player, t.Actor, t.Trait, cash))

				// A queue with no producer, such as the Ship queue of a player with no shipyard, has nothing to build with.
				.Where(q => q.Producers.Length > 0)
				.ToArray();

			return new ProductionV1(true, "production", player.PlayerName, resources?.Cash ?? 0, resources?.Resources ?? 0,
				AgentStateDump.Power(player.PlayerActor.TraitOrDefault<PowerManager>()), queues);
		}

		static ProductionQueueV1 QueueOf(World world, Player player, Actor actor, ProductionQueue queue, int cash)
		{
			// BuildableItems is the palette's set, and it is empty while the queue or its production trait is disabled.
			var buildable = queue.BuildableItems().Select(b => b.Name).ToHashSet();
			var options = queue.AllItems()
				.OrderBy(i => i.TraitInfo<BuildableInfo>().BuildPaletteOrder)
				.ThenBy(i => i.Name, StringComparer.Ordinal)
				.Select(i => OptionOf(queue, i, buildable, cash))
				.ToArray();

			var producers = world.ActorsWithTrait<Production>()
				.Where(t => t.Actor.Owner == player && t.Actor.IsInWorld && !t.Actor.IsDead && t.Trait.Info.Produces.Contains(queue.Info.Type))
				.Select(t => t.Actor.ActorID)
				.Order()
				.ToArray();

			// The engine ticks only the head of a queue, so a finished head holds everything behind it.
			var head = queue.AllQueued().FirstOrDefault();
			var blocker = head is { Done: true } ? head.Item : null;

			return new ProductionQueueV1(actor.ActorID, producers, queue.Info.Type, queue.Enabled, options,
				queue.AllQueued().Select(i => ItemOf(queue, i)).ToArray(), blocker);
		}

		static ProductionOptionV1 OptionOf(ProductionQueue queue, ActorInfo info, ISet<string> buildable, int cash)
		{
			var cost = queue.GetProductionCost(info);
			return new ProductionOptionV1(info.Name, cost, info.TraitInfoOrDefault<PowerInfo>()?.Amount,
				info.TraitInfo<BuildableInfo>().Prerequisites.ToArray(), buildable.Contains(info.Name), cash >= cost);
		}

		static ProductionItemV1 ItemOf(ProductionQueue queue, ProductionItem item)
		{
			var state = item.Done ? "done" : item.Paused ? "paused" : queue.IsProducing(item) ? "building" : "waiting";

			// TotalTime starts at 1 and is only replaced by a positive build time, so the division is safe.
			var progress = (item.TotalTime - item.RemainingTime) * 100 / item.TotalTime;
			return new ProductionItemV1(item.Item, item.TotalCost, item.RemainingCost, progress, state);
		}
	}
}
