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
	// Schema v1, additive. Wire keys are snake_case (AgentSocketCommands.Json).

	/// <summary>
	/// The production command: the local player's queues with cash and power, so one call answers what can be built. A queue's
	/// options come only when they changed for this connection (ProductionQueueV1.Changed). production_tree lists them all.
	/// </summary>
	public sealed record ProductionV1(bool Ok, string Cmd, string Player, int Cash, int Resources, PowerV1 Power, ProductionQueueV1[] Queues);

	/// <summary>
	/// One ProductionQueue the local player owns and can build with. Actor is the actor that holds the queue: the player
	/// actor for the RA queues. Producers are the owned actors whose Production trait produces the queue type, where an item is built.
	/// Options are the items the queue lists, in build palette order. Options is null, and left out of the JSON, when they are
	/// unchanged for this connection; Changed is true when they are listed. Items are the live queue, head first. Enabled is
	/// false while the queue's production trait is disabled or the faction is wrong. Blocker is the name of the head item when
	/// it is finished and waits for placement, which holds the items behind it; null otherwise (P1.12).
	/// </summary>
	public sealed record ProductionQueueV1(uint Actor, uint[] Producers, string QueueType, bool Enabled,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProductionOptionV1[] Options, bool Changed,
		ProductionItemV1[] Items, string Blocker);

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
		/// <summary>
		/// The production command's reply. On a connection, a queue lists its options only when they differ from what that
		/// connection last saw, and the connection then records what it saw. Without a connection, every queue lists them.
		/// </summary>
		public static ProductionV1 List(World world, AgentConnection connection)
		{
			return Build(world, "production", connection?.SeenOptions);
		}

		/// <summary>
		/// The production_tree reply: the full catalog, with every option of every queue in full. It reads the connection's seen
		/// options no more than it changes them, so the next production read still compares with the last production read.
		/// </summary>
		public static ProductionV1 Tree(World world)
		{
			return Build(world, "production_tree", null);
		}

		/// <summary>
		/// The options the player can build now, one per name, in palette order. Only queues with a producer count, as in the
		/// production reply. Must run on the game thread.
		/// </summary>
		internal static ProductionOptionV1[] Buildable(Player player)
		{
			var resources = player.PlayerActor.TraitOrDefault<PlayerResources>();
			var cash = resources == null ? 0 : resources.GetCashAndResources();

			var found = new Dictionary<string, (ActorInfo Info, ProductionOptionV1 Option)>();
			var queues = player.World.ActorsWithTrait<ProductionQueue>()
				.Where(t => t.Actor.Owner == player && t.Actor.IsInWorld && !t.Actor.IsDead && ProducersOf(player.World, player, t.Trait).Length > 0);

			foreach (var queue in queues)
			{
				var buildable = queue.Trait.BuildableItems().ToArray();
				var names = buildable.Select(b => b.Name).ToHashSet();
				foreach (var info in buildable)
					found.TryAdd(info.Name, (info, OptionOf(queue.Trait, info, names, cash)));
			}

			return found.Values
				.OrderBy(f => f.Info.TraitInfo<BuildableInfo>().BuildPaletteOrder)
				.ThenBy(f => f.Info.Name, StringComparer.Ordinal)
				.Select(f => f.Option)
				.ToArray();
		}

		// The ids of the player's owned producers that build the queue's type, in ascending order. Empty when there is none.
		static uint[] ProducersOf(World world, Player player, ProductionQueue queue)
		{
			return world.ActorsWithTrait<Production>()
				.Where(t => t.Actor.Owner == player && t.Actor.IsInWorld && !t.Actor.IsDead && t.Trait.Info.Produces.Contains(queue.Info.Type))
				.Select(t => t.Actor.ActorID)
				.Order()
				.ToArray();
		}

		// seen holds the option signature each queue last reported on its connection. Null lists every queue's options.
		static ProductionV1 Build(World world, string cmd, Dictionary<(uint Actor, string QueueType), string[]> seen)
		{
			var player = world.LocalPlayer ?? throw new InvalidOperationException("There is no local player");
			var resources = player.PlayerActor.TraitOrDefault<PlayerResources>();
			var cash = resources == null ? 0 : resources.GetCashAndResources();

			var reported = new Dictionary<(uint Actor, string QueueType), string[]>();
			var queues = world.ActorsWithTrait<ProductionQueue>()
				.Where(t => t.Actor.Owner == player && t.Actor.IsInWorld && !t.Actor.IsDead)
				.OrderBy(t => t.Actor.ActorID)
				.ThenBy(t => t.Trait.Info.Type, StringComparer.Ordinal)
				.Select(t => QueueOf(world, player, t.Actor, t.Trait, cash, seen, reported))

				// A queue with no producer, such as the Ship queue of a player with no shipyard, has nothing to build with.
				.Where(q => q != null)
				.ToArray();

			// The connection holds what this reply reported, so a queue that left the reply is forgotten and lists its options again.
			if (seen != null)
			{
				seen.Clear();
				foreach (var (key, signature) in reported)
					seen[key] = signature;
			}

			return new ProductionV1(true, cmd, player.PlayerName, resources?.Cash ?? 0, resources?.Resources ?? 0,
				AgentStateDump.Power(player.PlayerActor.TraitOrDefault<PowerManager>()), queues);
		}

		// Null when the queue has no producer. Such a queue is not reported, so it is not recorded as seen either.
		static ProductionQueueV1 QueueOf(World world, Player player, Actor actor, ProductionQueue queue, int cash,
			Dictionary<(uint Actor, string QueueType), string[]> seen, Dictionary<(uint Actor, string QueueType), string[]> reported)
		{
			var producers = ProducersOf(world, player, queue);
			if (producers.Length == 0)
				return null;

			// BuildableItems is the palette's set, and it is empty while the queue or its production trait is disabled.
			var buildable = queue.BuildableItems().Select(b => b.Name).ToHashSet();
			var ordered = queue.AllItems()
				.OrderBy(i => i.TraitInfo<BuildableInfo>().BuildPaletteOrder)
				.ThenBy(i => i.Name, StringComparer.Ordinal)
				.ToArray();

			// The option set is each option's name and buildable, in palette order. can_afford is left out: it flips whenever cash
			// moves, so it would mark the set changed on nearly every read.
			var signature = ordered.Select(i => i.Name + (buildable.Contains(i.Name) ? "+" : "-")).ToArray();
			var key = (actor.ActorID, queue.Info.Type);
			reported[key] = signature;

			var changed = seen == null || !seen.TryGetValue(key, out var previous) || !previous.SequenceEqual(signature);
			var options = changed ? ordered.Select(i => OptionOf(queue, i, buildable, cash)).ToArray() : null;

			// The engine ticks only the head of a queue, so a finished head holds everything behind it.
			var head = queue.AllQueued().FirstOrDefault();
			var blocker = head is { Done: true } ? head.Item : null;

			return new ProductionQueueV1(actor.ActorID, producers, queue.Info.Type, queue.Enabled, options, changed,
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
