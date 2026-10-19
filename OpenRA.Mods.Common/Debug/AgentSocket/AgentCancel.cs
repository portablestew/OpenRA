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

namespace OpenRA.Mods.Common.AgentSocket
{
	// Schema v1, additive. Wire keys are snake_case (AgentSocketCommands.Json).

	/// <summary>
	/// The cancel command's success: the CancelProduction order was issued to the queue that holds the item. Canceled is the
	/// number of queued copies the order removes, at most the requested count.
	/// </summary>
	public sealed record CancelV1(bool Ok, string Cmd, uint Actor, string QueueType, string Item, int Canceled, int Tick, int MinEffectTick);

	/// <summary>A parsed cancel request. Count is at least 1.</summary>
	public sealed record CancelRequest(string Item, int Count);

	/// <summary>
	/// Cancels queued copies of an item through the engine's CancelProduction order, the same order the production palette
	/// issues. The order goes to the first owned queue, in production order, that holds the item, and the engine removes that
	/// queue's last copies first (ProductionQueue.CancelProductionInner). A copy in progress is removed when it is the last one.
	/// Nothing queued, or a disabled producer, is an error. Must run on the game thread.
	/// </summary>
	public static class AgentCancel
	{
		/// <summary>Parses the request on the socket thread. Returns an error message, or null when the request is valid.</summary>
		public static string Parse(JsonElement root, out CancelRequest request)
		{
			request = null;
			if (!root.TryGetProperty("item", out var itemElement) || itemElement.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(itemElement.GetString()))
				return "item must be a non-empty string";

			var count = 1;
			if (AgentOrder.IsPresent(root, "count") && (!root.GetProperty("count").TryGetInt32(out count) || count < 1))
				return "count must be a positive integer";

			request = new CancelRequest(itemElement.GetString(), count);
			return null;
		}

		/// <summary>Finds the queue that holds the item and issues the cancel order to it. Must run on the game thread.</summary>
		public static CancelV1 Start(World world, CancelRequest request)
		{
			var production = AgentProduction.Tree(world);
			var item = request.Item;

			foreach (var queue in production.Queues)
			{
				var copies = AgentBuild.QueueTrait(world, queue).AllQueued().Count(i => i.Item == item);
				if (copies == 0)
					continue;

				// The engine ignores a CancelProduction order while the queue is disabled, so say so rather than report a cancel that did nothing.
				if (!queue.Enabled)
					throw new InvalidOperationException($"the producer for '{item}' is disabled, so nothing was canceled");

				var canceled = Math.Min(copies, request.Count);
				var actor = AgentOrder.Accept(world, queue.Actor, "CancelProduction");
				world.IssueOrder(Order.CancelProduction(actor, item, canceled));

				return new CancelV1(true, "cancel", actor.ActorID, queue.QueueType, item, canceled, world.WorldTick, world.WorldTick + 1);
			}

			throw new InvalidOperationException($"nothing to cancel: '{item}' is not queued in any queue");
		}
	}
}
