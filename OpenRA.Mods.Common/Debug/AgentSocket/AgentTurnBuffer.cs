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
using System.Reflection;
using System.Threading;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// The suppressed events of one type in one turn: how many, and the ticks they cover (P3.2). Event is the event type,
	/// as the event key names it. The wire keys are event, aggregated, tick_from, and tick_to.
	/// </summary>
	public sealed record AggregatedV1(string Event, int Aggregated, int TickFrom, int TickTo);

	/// <summary>
	/// The events one connection has seen since its last flush (P3.2). Add records an event as it is raised. Flush returns the
	/// turn in arrival order: the first FullPerType events of each type in full, then one AggregatedV1 per type that had more,
	/// placed where its first suppressed event arrived. killed, defeated, and game_over are never aggregated. A full buffer
	/// drops its oldest event and counts it, and Flush puts one OverflowV1 first when any were dropped.
	/// </summary>
	public sealed class AgentTurnBuffer
	{
		/// <summary>The most events the buffer holds before it drops the oldest. The same bound as the subscriber queue.</summary>
		public const int Capacity = AgentEventSubscriber.Capacity;

		/// <summary>How many events of each type a flush returns in full.</summary>
		public const int FullPerType = 3;

		/// <summary>How long a parked wait sleeps before it checks the clock and the buffer again, in milliseconds.</summary>
		const int PollMs = 5;

		static readonly string[] NeverAggregated = ["killed", "defeated", "game_over"];

		// Every event DTO has an Event string and a Tick, as its first two fields. Read them once per DTO type.
		static readonly Dictionary<Type, (PropertyInfo Event, PropertyInfo Tick)> Accessors = [];

		readonly object gate = new();
		readonly Queue<(string Type, int Tick, object Dto)> events = new();
		int dropped;

		/// <summary>
		/// Records one event. A payload with no Event and Tick field is not an event, and is skipped. A full buffer drops its
		/// oldest event and counts it.
		/// </summary>
		public void Add(object dto)
		{
			var read = Read(dto);
			if (read is null)
				return;

			var (type, tick) = read.Value;
			lock (gate)
			{
				if (events.Count == Capacity)
				{
					events.Dequeue();
					dropped++;
				}

				events.Enqueue((type, tick, dto));
				Monitor.PulseAll(gate);
			}
		}

		/// <summary>
		/// Waits until the buffer holds an event, or timeoutMs has passed on clock (P3.4). Returns true when an event is waiting,
		/// and false at the timeout. The wait releases the buffer's lock while it sleeps, so Add on the game thread never waits
		/// for it.
		/// </summary>
		public bool WaitForEvents(long timeoutMs, Func<long> clock)
		{
			var deadline = clock() + timeoutMs;
			lock (gate)
			{
				while (events.Count == 0)
				{
					var remaining = deadline - clock();
					if (remaining <= 0)
						return false;

					Monitor.Wait(gate, (int)Math.Min(remaining, PollMs));
				}

				return true;
			}
		}

		/// <summary>The events waiting for the next flush. Test use.</summary>
		public int Pending
		{
			get
			{
				lock (gate)
					return events.Count;
			}
		}

		/// <summary>
		/// Empties the buffer and returns its turn, aggregated as the class summary describes. The overflow marker, when present,
		/// comes first and resets the dropped count.
		/// </summary>
		public List<object> Flush()
		{
			lock (gate)
			{
				var batch = new List<object>(events.Count + 1);
				if (dropped > 0)
				{
					batch.Add(new OverflowV1("overflow", dropped));
					dropped = 0;
				}

				var full = new Dictionary<string, int>();
				var slots = new Dictionary<string, Slot>();
				while (events.Count > 0)
				{
					var (type, tick, dto) = events.Dequeue();
					if (Array.IndexOf(NeverAggregated, type) < 0 && full.GetValueOrDefault(type) >= FullPerType)
					{
						if (!slots.TryGetValue(type, out var slot))
						{
							slot = new Slot(type, batch.Count, tick);
							slots[type] = slot;
							batch.Add(null);
						}

						slot.Add(tick);
						continue;
					}

					full[type] = full.GetValueOrDefault(type) + 1;
					batch.Add(dto);
				}

				foreach (var slot in slots.Values)
					batch[slot.Index] = new AggregatedV1(slot.Type, slot.Count, slot.From, slot.To);

				return batch;
			}
		}

		/// <summary>The event type and tick of dto, or null when dto has no Event and Tick field.</summary>
		static (string Type, int Tick)? Read(object dto)
		{
			var type = dto.GetType();
			(PropertyInfo Event, PropertyInfo Tick) accessors;
			lock (Accessors)
			{
				if (!Accessors.TryGetValue(type, out accessors))
				{
					accessors = (type.GetProperty("Event"), type.GetProperty("Tick"));
					Accessors[type] = accessors;
				}
			}

			if (accessors.Event == null || accessors.Tick == null)
				return null;

			return ((string)accessors.Event.GetValue(dto), (int)accessors.Tick.GetValue(dto));
		}

		/// <summary>One aggregated run under construction: its position in the batch, and the ticks it covers.</summary>
		sealed class Slot
		{
			public readonly string Type;
			public readonly int Index;
			public int Count;
			public int From;
			public int To;

			public Slot(string type, int index, int tick)
			{
				Type = type;
				Index = index;
				From = tick;
				To = tick;
			}

			public void Add(int tick)
			{
				Count++;
				From = Math.Min(From, tick);
				To = Math.Max(To, tick);
			}
		}
	}
}
