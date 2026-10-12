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
using System.Threading;

namespace OpenRA.Mods.Common.AgentSocket
{
	// Schema v1. Wire keys are snake_case (AgentSocketCommands.Json).
	public sealed record OverflowV1(string Event, int Dropped);

	// Schema v1 payloads. Wire keys are snake_case (AgentSocketCommands.Json). Only primitives, so the queue holds no live objects.
	public sealed record DamagedV1(string Event, int Tick, uint Actor, string Type, string Owner, uint? Attacker,
		string AttackerType, string AttackerOwner, int Damage, int Hp, int MaxHp, string DamageState);

	public sealed record KilledV1(string Event, int Tick, uint Actor, string Type, string Owner, uint? Attacker,
		string AttackerType, string AttackerOwner, int Cost);

	public sealed record CapturedV1(string Event, int Tick, uint Actor, string Type, string From, string To, uint Captor);

	public sealed record OrderEventV1(string Event, int Tick, int EffectTick, bool Valid, int Client, string Order, uint Actor,
		string Type, string Owner, int[] Cell, uint? TargetActor, bool Queued);

	public sealed record ProducedV1(string Event, int Tick, uint Actor, string Type, string Owner, uint Producer, string ProducerType);

	public sealed record StanceV1(string Event, int Tick, uint Actor, string Type, string Owner, string From, string To);

	/// <summary>
	/// Static event hub (D12, P4). Engine hooks call Raise. With no subscriber, or with the socket disabled, Raise returns
	/// after one volatile read, so an unsubscribed game pays almost nothing. Subscriptions are per connection and are
	/// process-static, so they survive world changes (D8).
	/// </summary>
	public static class AgentEvents
	{
		/// <summary>The v1 event groups, in the canonical order that subscribe echoes.</summary>
		public static readonly string[] Groups = ["damage", "killed", "captured", "orders", "production", "stance", "path", "move_result"];

		static volatile bool enabled;
		static volatile int subscriberCount;
		static readonly Lock Gate = new();

		// Copy-on-write: writers replace the array under Gate, so Raise can read it without a lock.
		static AgentEventSubscriber[] subscribers = [];

		/// <summary>
		/// True when the socket is enabled and at least one connection is subscribed. Engine hooks test this before they
		/// build a DTO, so an unsubscribed game does no event work.
		/// </summary>
		public static bool HasSubscribers => enabled && subscriberCount != 0;

		/// <summary>Set from Debug.AgentSocket by AgentWorldBridge at each world load (D12).</summary>
		public static bool Enabled
		{
			get => enabled;
			set => enabled = value;
		}

		/// <summary>The number of connections with at least one subscribed group.</summary>
		public static int SubscriberCount => subscriberCount;

		/// <summary>Queues dto for every subscriber to group. Runs on the game thread; never blocks on socket I/O.</summary>
		public static void Raise(string group, object dto)
		{
			Raise(group, null, dto);
		}

		/// <summary>
		/// Queues dto for every subscriber to group whose owner filter, if any, matches owner. owner is the player name of
		/// the actor the event is about; null means the event has no owner and every filter passes it.
		/// </summary>
		public static void Raise(string group, string owner, object dto)
		{
			if (!HasSubscribers)
				return;

			foreach (var subscriber in Volatile.Read(ref subscribers))
				subscriber.Enqueue(group, owner, dto);
		}

		internal static void SetRegistered(AgentEventSubscriber subscriber, bool registered)
		{
			lock (Gate)
			{
				var current = subscribers;
				var present = Array.IndexOf(current, subscriber) >= 0;
				if (registered == present)
					return;

				subscribers = registered ? [.. current, subscriber] : current.Where(s => s != subscriber).ToArray();
				subscriberCount = subscribers.Length;
			}
		}
	}

	/// <summary>
	/// One per connection. Holds the subscribed groups and a bounded queue of event DTOs. Raise (game thread) enqueues;
	/// Take (the connection's event thread) dequeues and serializes. Only primitive DTOs may be queued.
	/// </summary>
	public sealed class AgentEventSubscriber
	{
		public const int Capacity = 4096;

		readonly object gate = new();
		readonly HashSet<string> groups = [];
		readonly Queue<object> queue = new();
		string owner;
		int dropped;
		bool closed;

		/// <summary>Events waiting to be taken. Test use.</summary>
		public int Pending
		{
			get
			{
				lock (gate)
					return queue.Count;
			}
		}

		/// <summary>The owner filter, or null when every owner's events pass.</summary>
		public string Owner
		{
			get
			{
				lock (gate)
					return owner;
			}
		}

		/// <summary>
		/// Adds the groups (already validated) and returns the active groups in canonical order. A non-null owner replaces
		/// the owner filter; null leaves it as it was.
		/// </summary>
		public string[] Subscribe(IEnumerable<string> names, string ownerFilter = null)
		{
			bool registered;
			lock (gate)
			{
				if (closed)
					return [];

				foreach (var name in names)
					groups.Add(name);

				if (ownerFilter != null)
					owner = ownerFilter;

				registered = groups.Count > 0;
			}

			AgentEvents.SetRegistered(this, registered);
			return Active();
		}

		/// <summary>
		/// Removes the groups, or every group when names is null. Unsubscribing everything also clears the owner filter.
		/// Returns the active groups in canonical order.
		/// </summary>
		public string[] Unsubscribe(IEnumerable<string> names)
		{
			bool registered;
			lock (gate)
			{
				if (names == null)
				{
					groups.Clear();
					owner = null;
				}
				else
					foreach (var name in names)
						groups.Remove(name);

				registered = groups.Count > 0;
			}

			AgentEvents.SetRegistered(this, registered);
			return Active();
		}

		public string[] Active()
		{
			lock (gate)
				return AgentEvents.Groups.Where(groups.Contains).ToArray();
		}

		/// <summary>
		/// Adds dto when group is subscribed and the owner filter passes. A full queue drops its oldest entry and counts it,
		/// so the next Take reports one overflow marker for the whole burst.
		/// </summary>
		internal void Enqueue(string group, string eventOwner, object dto)
		{
			lock (gate)
			{
				if (closed || !groups.Contains(group))
					return;

				if (owner != null && eventOwner != null && eventOwner != owner)
					return;

				if (queue.Count == Capacity)
				{
					queue.Dequeue();
					dropped++;
				}

				queue.Enqueue(dto);
				Monitor.Pulse(gate);
			}
		}

		/// <summary>
		/// Blocks until events are queued or the subscriber is closed. Returns the queued DTOs, preceded by one overflow
		/// marker if any were dropped. Returns null once the subscriber is closed.
		/// </summary>
		public List<object> Take()
		{
			lock (gate)
			{
				while (!closed && queue.Count == 0 && dropped == 0)
					Monitor.Wait(gate);

				if (closed)
					return null;

				var batch = new List<object>(queue.Count + 1);
				if (dropped > 0)
				{
					batch.Add(new OverflowV1("overflow", dropped));
					dropped = 0;
				}

				while (queue.Count > 0)
					batch.Add(queue.Dequeue());

				return batch;
			}
		}

		/// <summary>Drops the groups and the queue and unregisters from the hub. Called once, when the connection ends.</summary>
		public void Close()
		{
			lock (gate)
			{
				closed = true;
				groups.Clear();
				owner = null;
				queue.Clear();
				dropped = 0;
				Monitor.PulseAll(gate);
			}

			AgentEvents.SetRegistered(this, false);
		}
	}
}
