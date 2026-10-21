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
using System.Linq;
using System.Text.Json;
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Mods.Common.AgentSocket
{
	// Schema v1, additive. Wire keys are snake_case (AgentSocketCommands.Json).

	/// <summary>
	/// The build command's success: the StartProduction order was issued to the queue, which then holds the count. Ready and
	/// EtaTicks describe the copy this request queued, the first of Count: it is queued behind every copy already in its queue, so
	/// Ready is false (a copy queued now is not done), and EtaTicks is the ticks until it is done, counting the items ahead of it
	/// at the power now. EtaTicks is null when a finished item at the head of that queue holds it until it is placed (BlockedBy
	/// names that item). BlockedBy names the finished item at the head of the queue the item joins, which holds the new item until
	/// it is placed (P1.12); null when that head is not finished.
	/// </summary>
	public sealed record BuildV1(bool Ok, string Cmd, uint Actor, string QueueType, string Item, int Count, int Tick, int MinEffectTick,
		bool? Ready, int? EtaTicks, string BlockedBy);

	/// <summary>
	/// The refusal for a build the queue cannot start. Available lists the items build would accept now. MissingPrerequisites
	/// names every prerequisite the player does not meet, and is null when the refusal is not about prerequisites. Ready and
	/// EtaTicks are always null here, since a refused build queues nothing. BlockedBy is as in BuildV1.
	/// </summary>
	public sealed record BuildRefusedV1(bool Ok, string Cmd, string Error, string[] Available, string[] MissingPrerequisites,
		bool? Ready, int? EtaTicks, string BlockedBy, string Hint);

	/// <summary>A parsed build request. Count is at least 1.</summary>
	public sealed record BuildRequest(string Item, int Count);

	/// <summary>Thrown on the game thread when a build cannot start. Carries the items build would accept now, and the answers BuildRefusedV1 reports.</summary>
	public sealed class BuildRefusedException : Exception
	{
		public BuildRefusedException(string error, string[] available, string[] missingPrerequisites, string blockedBy)
			: base(error)
		{
			Available = available;
			MissingPrerequisites = missingPrerequisites;
			BlockedBy = blockedBy;
		}

		public string[] Available { get; }
		public string[] MissingPrerequisites { get; }
		public string BlockedBy { get; }
	}

	/// <summary>
	/// Queues an item in the local player's production queue as a real StartProduction order, so it takes the same path as the
	/// build palette. The item must be buildable now in a queue with a producer: the same BuildableItems set the production
	/// command reports. Failures are checked in this order: unknown item, no queue, disabled producer, unmet prerequisites,
	/// unaffordable, queue or build limit reached. Only the first of these applies.
	/// </summary>
	public static class AgentBuild
	{
		/// <summary>Parses the request on the socket thread. Returns an error message, or null when the request is valid.</summary>
		public static string Parse(JsonElement root, out BuildRequest request)
		{
			request = null;
			if (!root.TryGetProperty("item", out var itemElement) || itemElement.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(itemElement.GetString()))
				return "item must be a non-empty string";

			var count = 1;
			if (AgentOrder.IsPresent(root, "count") && (!root.GetProperty("count").TryGetInt32(out count) || count < 1))
				return "count must be a positive integer";

			request = new BuildRequest(itemElement.GetString(), count);
			return null;
		}

		/// <summary>Validates the request against the production list, then issues it. Must run on the game thread.</summary>
		public static BuildV1 Start(World world, BuildRequest request, AgentSelection selection)
		{
			var production = AgentProduction.Tree(world);
			var item = request.Item;

			if (!world.Map.Rules.Actors.TryGetValue(item, out var info))
				throw Refused(world, item, $"unknown item '{item}'", production);

			// An actor with no Buildable trait has no queue type, so no queue builds it.
			var buildable = info.TraitInfoOrDefault<BuildableInfo>();
			var candidates = production.Queues.Where(q => buildable != null && buildable.Queue.Contains(q.QueueType)).ToArray();

			if (candidates.Length == 0)
				throw Refused(world, item, $"no queue builds '{item}': no owned producer makes its queue", production);

			if (candidates.All(q => !q.Enabled))
				throw Refused(world, item, $"the producer for '{item}' is disabled", production);

			var queue = candidates.FirstOrDefault(q => q.Enabled && q.Options.Any(o => o.Name == item && o.Buildable));
			if (queue == null)
			{
				var missing = MissingPrerequisites(world, info);
				var error = missing.Length > 0
					? $"'{item}' is not buildable now: missing prerequisites: {string.Join(", ", missing)}"
					: $"'{item}' is not buildable now: its prerequisites are not met";
				throw Refused(world, item, error, production, missing);
			}

			var option = queue.Options.First(o => o.Name == item);
			if (!option.CanAfford)
				throw Refused(world, item, $"not enough funds for '{item}': it costs {option.Cost}, and you have {production.Funds}", production);

			// The engine silently drops a StartProduction that the queue, item or build limit refuses, so ask the queue first.
			if (!QueueTrait(world, queue).CanQueue(info, out _, out _))
				throw Refused(world, item, $"'{item}' cannot be queued now: a queue, item or build limit is reached", production);

			// Read before the order is issued, since the copy joins the end of the queue as it stands now.
			var etaTicks = EtaOfNewCopy(world, queue, item);

			// The same order-accept check as the order command, so a queue that does not take StartProduction is refused too.
			var actor = AgentOrder.Accept(world, queue.Actor, "StartProduction");
			world.IssueOrder(Order.StartProduction(actor, item, request.Count));
			selection.Ordered(world, actor);

			// A copy queued now has not started, let alone finished, so it is never ready when the reply is sent.
			return new BuildV1(true, "build", actor.ActorID, queue.QueueType, item, request.Count, world.WorldTick, world.WorldTick + 1,
				false, etaTicks, queue.Blocker);
		}

		/// <summary>
		/// The prerequisites of info the player does not meet, read from the engine's own check. Each term is tested alone with
		/// TechTree.HasPrerequisites, so the "~" and "!" forms keep their meaning. Nothing here reimplements the rule.
		/// </summary>
		static string[] MissingPrerequisites(World world, ActorInfo info)
		{
			var techTree = world.LocalPlayer.PlayerActor.Trait<TechTree>();
			return info.TraitInfo<BuildableInfo>().Prerequisites
				.Where(p => !techTree.HasPrerequisites([p]))
				.ToArray();
		}

		/// <summary>
		/// The ETA of a copy of item that the build is about to queue at the end of queue: the ticks the items already in the queue
		/// still need, plus the copy's own build time, at the current power. A queue builds one item at a time, so every item ahead
		/// counts in full. Null when a finished item is in the queue: it holds the queue until it is placed, and nothing can say when that is.
		/// </summary>
		static int? EtaOfNewCopy(World world, ProductionQueueV1 queue, string item)
		{
			var trait = QueueTrait(world, queue);
			var items = trait.AllQueued().ToArray();
			if (items.Any(i => i.Done))
				return null;

			var total = 0;
			foreach (var ahead in items)
				total += RemainingTicks(world, trait, ahead);

			var info = world.Map.Rules.Actors[item];
			return total + trait.GetBuildTime(info, info.TraitInfo<BuildableInfo>());
		}

		/// <summary>The ticks an item still needs. An unstarted item has no build time until it starts, so its time comes from the rules.</summary>
		static int RemainingTicks(World world, ProductionQueue trait, ProductionItem item)
		{
			if (item.Started)
				return item.RemainingTimeActual;

			var info = world.Map.Rules.Actors[item.Item];
			return trait.GetBuildTime(info, info.TraitInfo<BuildableInfo>());
		}

		/// <summary>The items the build command would accept now: buildable, affordable and under the queue's limits, in production order, without duplicates.</summary>
		static string[] Available(World world, ProductionV1 production)
		{
			return production.Queues.SelectMany(q =>
				{
					var trait = QueueTrait(world, q);
					return q.Options
						.Where(o => o.Buildable && o.CanAfford && trait.CanQueue(world.Map.Rules.Actors[o.Name], out _, out _))
						.Select(o => o.Name);
				})
				.Distinct()
				.ToArray();
		}

		/// <summary>The ProductionQueue trait behind a queue in the production list.</summary>
		internal static ProductionQueue QueueTrait(World world, ProductionQueueV1 queue)
		{
			return world.ActorsWithTrait<ProductionQueue>()
				.First(t => t.Actor.ActorID == queue.Actor && t.Trait.Info.Type == queue.QueueType).Trait;
		}

		/// <summary>The blocker of the first queue the item would join whose head is finished (P1.12). Null when no such queue exists.</summary>
		static string BlockedBy(World world, string item, ProductionV1 production)
		{
			var buildable = world.Map.Rules.Actors.TryGetValue(item, out var info) ? info.TraitInfoOrDefault<BuildableInfo>() : null;
			return production.Queues
				.Where(q => buildable != null && buildable.Queue.Contains(q.QueueType))
				.Select(q => q.Blocker)
				.FirstOrDefault(b => b != null);
		}

		static BuildRefusedException Refused(World world, string item, string error, ProductionV1 production, string[] missing = null)
		{
			return new BuildRefusedException(error, Available(world, production), missing, BlockedBy(world, item, production));
		}
	}
}
