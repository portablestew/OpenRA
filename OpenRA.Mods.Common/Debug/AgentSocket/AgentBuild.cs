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

	/// <summary>The build command's success: the StartProduction order was issued to the queue, which then holds the count.</summary>
	public sealed record BuildV1(bool Ok, string Cmd, uint Actor, string QueueType, string Item, int Count, int Tick, int MinEffectTick);

	/// <summary>The refusal for a build the queue cannot start. Available lists the items build would accept now.</summary>
	public sealed record BuildRefusedV1(bool Ok, string Cmd, string Error, string[] Available);

	/// <summary>A parsed build request. Count is at least 1.</summary>
	public sealed record BuildRequest(string Item, int Count);

	/// <summary>Thrown on the game thread when a build cannot start. Carries the items build would accept now.</summary>
	public sealed class BuildRefusedException : Exception
	{
		public BuildRefusedException(string error, string[] available)
			: base(error)
		{
			Available = available;
		}

		public string[] Available { get; }
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
		public static BuildV1 Start(World world, BuildRequest request)
		{
			var production = AgentProduction.List(world);
			var item = request.Item;

			if (!world.Map.Rules.Actors.TryGetValue(item, out var info))
				throw Refused(world, $"unknown item '{item}'", production);

			// An actor with no Buildable trait has no queue type, so no queue builds it.
			var buildable = info.TraitInfoOrDefault<BuildableInfo>();
			var candidates = production.Queues.Where(q => buildable != null && buildable.Queue.Contains(q.QueueType)).ToArray();

			if (candidates.Length == 0)
				throw Refused(world, $"no queue builds '{item}': no owned producer makes its queue", production);

			if (candidates.All(q => !q.Enabled))
				throw Refused(world, $"the producer for '{item}' is disabled", production);

			var queue = candidates.FirstOrDefault(q => q.Enabled && q.Options.Any(o => o.Name == item && o.Buildable));
			if (queue == null)
				throw Refused(world, $"'{item}' is not buildable now: its prerequisites are not met", production);

			var option = queue.Options.First(o => o.Name == item);
			if (!option.CanAfford)
				throw Refused(world, $"not enough cash for '{item}': it costs {option.Cost}, and you have {production.Cash + production.Resources}", production);

			// The engine silently drops a StartProduction that the queue, item or build limit refuses, so ask the queue first.
			if (!QueueTrait(world, queue).CanQueue(info, out _, out _))
				throw Refused(world, $"'{item}' cannot be queued now: a queue, item or build limit is reached", production);

			// The same order-accept check as the order command, so a queue that does not take StartProduction is refused too.
			var actor = AgentOrder.Accept(world, queue.Actor, "StartProduction");
			world.IssueOrder(Order.StartProduction(actor, item, request.Count));

			return new BuildV1(true, "build", actor.ActorID, queue.QueueType, item, request.Count, world.WorldTick, world.WorldTick + 1);
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

		static BuildRefusedException Refused(World world, string error, ProductionV1 production)
		{
			return new BuildRefusedException(error, Available(world, production));
		}
	}
}
