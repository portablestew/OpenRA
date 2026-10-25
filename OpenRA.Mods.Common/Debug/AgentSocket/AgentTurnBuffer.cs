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
using System.Text.Json.Serialization;
using System.Threading;
using System.Linq;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// The suppressed events of one type and name in one turn: how many, and the ticks they cover (P3.2). Event is the event type,
	/// as the event key names it, and Name is the name those events share, such as a notification's or a power's, and is null
	/// when they name none. The wire keys are event, aggregated, tick_from, tick_to, actors, and name. Actors names up to ten
	/// distinct actors those events covered, in arrival order, and is empty when the events name no actor.
	/// A damaged or healed marker for one victim also has damage_total (the sum of its hits), hp_from, hp_to, and attackers (up to
	/// ten distinct attackers, or healers, that were known). hp_from is the HP before the first suppressed hit, or for a heal the HP
	/// after the first suppressed heal; hp_to is the HP after the last. A residual marker, for victims past MaxVictimMarkers, has
	/// damage_total and attackers but no hp_from or hp_to. Each of the four keys is left out when it has no value.
	/// A notification's marker is per name and item, and carries the latest suppressed notification's payload: the keys a
	/// NotificationV1 adds (item, queue_type, reason, cost, and the rest), each left out when it has no value.
	/// </summary>
	public sealed record AggregatedV1(string Event, int Aggregated, int TickFrom, int TickTo, uint[] Actors,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string Name = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DamageTotal = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? HpFrom = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? HpTo = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint[] Attackers = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string Item = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string QueueType = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string Reason = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Cost = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RemainingCost = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Funds = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ShortBy = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? Producer = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string BlockedBy = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[] Cell = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PlacementRequired = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[] Options = null,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[] Placeable = null);

	/// <summary>
	/// The events one connection has seen since its last flush (P3.2). Add records an event as it is raised. Flush returns the
	/// turn in arrival order: the first FullPerType events of each event type and name in full, then one AggregatedV1 per type
	/// and name that had more, placed where its first suppressed event arrived. Damaged and healed events that are suppressed
	/// get one marker per victim (up to MaxVictimMarkers). A suppressed notification gets one marker per name and item, so a
	/// notification's name is never hidden in a marker with another notification's events. killed, defeated, and game_over
	/// are never aggregated. A full buffer
	/// drops its oldest event and counts it, and Flush puts one OverflowV1 first when any were dropped.
	/// </summary>
	public sealed class AgentTurnBuffer
	{
		/// <summary>The most events the buffer holds before it drops the oldest. The most events a connection holds between responses.</summary>
		public const int Capacity = 4096;

		/// <summary>How many events of each type a flush returns in full.</summary>
		public const int FullPerType = 2;

		/// <summary>The most distinct actors an aggregated marker names.</summary>
		const int MaxActors = 10;

		/// <summary>The most victims that get a damaged or healed marker of their own in a turn. Later victims share one residual marker per type.</summary>
		public const int MaxVictimMarkers = 10;

		static readonly string[] NeverAggregated = ["killed", "defeated", "game_over"];

		// Every event DTO has an Event string and a Tick, as its first two fields, and most have an Actor and a Name. Read them once per DTO type.
		static readonly Dictionary<Type, (PropertyInfo Event, PropertyInfo Tick, PropertyInfo Actor, PropertyInfo Name)> Accessors = [];

		readonly Lock gate = new();
		readonly Queue<(string Type, string Name, int Tick, uint? Actor, object Dto)> events = new();
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

			var (type, name, tick, actor) = read.Value;
			lock (gate)
			{
				if (events.Count == Capacity)
				{
					events.Dequeue();
					dropped++;
				}

				events.Enqueue((type, name, tick, actor, dto));
			}
		}

		/// <summary>
		/// Waits until the buffer holds an event at or after minTicks of game time, the tick budget runs out, the game pauses, or
		/// the live world is replaced. Returns "events", "timeout", "paused", or "world_changed". clock reads the game
		/// clock; the production caller passes AgentGameClock.Read. The buffer's lock is not held while the wait sleeps, so Add on
		/// the game thread never waits for it.
		/// </summary>
		public string WaitForEvents(AgentGameClock.Reading start, int timeoutTicks, int minTicks, Func<AgentGameClock.Reading> clock)
		{
			var now = start;
			while (true)
			{
				lock (gate)
				{
					if (events.Count != 0 && now.Tick - start.Tick >= minTicks)
						return "events";
				}

				if (now.Paused)
					return "paused";

				if (now.Tick - start.Tick >= timeoutTicks)
					return "timeout";

				Thread.Sleep(AgentGameClock.PollMs);
				now = clock();
				if (now.World != start.World)
					return "world_changed";
			}
		}

		/// <summary>
		/// Drops every event waiting for the next flush, and the overflow count. A world change runs it, so the events of the world
		/// that is gone never reach a turn of the new one (8.8).
		/// </summary>
		public void Discard()
		{
			lock (gate)
			{
				events.Clear();
				dropped = 0;
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

				var full = new Dictionary<(string, string), int>();
				var slots = new Dictionary<(string Type, string Name, uint? Victim, string Item), Slot>();
				while (events.Count > 0)
				{
					var (type, name, tick, actor, dto) = events.Dequeue();
					var key = (type, name);
					if (Array.IndexOf(NeverAggregated, type) < 0 && full.GetValueOrDefault(key) >= FullPerType)
					{
						var slotKey = MarkerKey(type, name, dto, actor, slots);
						if (!slots.TryGetValue(slotKey, out var slot))
						{
							slot = new Slot(type, name, slotKey.Victim, batch.Count, tick);
							slots[slotKey] = slot;
							batch.Add(null);
						}

						slot.Add(dto, tick, actor);
						continue;
					}

					full[key] = full.GetValueOrDefault(key) + 1;
					batch.Add(dto);
				}

				foreach (var slot in slots.Values)
					batch[slot.Index] = slot.Marker();

				return batch;
			}
		}

		/// <summary>
		/// The event type, name, tick, and actor of dto, or null when dto has no Event and Tick field. The name is null when dto has
		/// no Name field, and the actor is null when dto has no Actor field.
		/// </summary>
		static (string Type, string Name, int Tick, uint? Actor)? Read(object dto)
		{
			var type = dto.GetType();
			(PropertyInfo Event, PropertyInfo Tick, PropertyInfo Actor, PropertyInfo Name) accessors;
			lock (Accessors)
			{
				if (!Accessors.TryGetValue(type, out accessors))
				{
					accessors = (type.GetProperty("Event"), type.GetProperty("Tick"), type.GetProperty("Actor"), type.GetProperty("Name"));
					Accessors[type] = accessors;
				}
			}

			if (accessors.Event == null || accessors.Tick == null)
				return null;

			var actor = accessors.Actor?.GetValue(dto) is uint id ? id : (uint?)null;
			var name = (string)accessors.Name?.GetValue(dto);
			return ((string)accessors.Event.GetValue(dto), name, (int)accessors.Tick.GetValue(dto), actor);
		}

		/// <summary>
		/// The marker a suppressed event joins. A notification joins the one marker for its type, name, and item, so the marker's
		/// payload is the latest of that item's. A damaged or healed event joins its victim's marker, until MaxVictimMarkers victims
		/// have one; a later victim joins the residual marker for its type and name, which names no victim. Every other event
		/// joins the one marker for its type and name.
		/// </summary>
		static (string Type, string Name, uint? Victim, string Item) MarkerKey(string type, string name, object dto, uint? actor,
			Dictionary<(string Type, string Name, uint? Victim, string Item), Slot> slots)
		{
			if (dto is NotificationV1 notification)
				return (type, name, null, notification.Item);

			if (actor is not uint victim || dto is not (DamagedV1 or HealedV1))
				return (type, name, null, null);

			var key = (type, name, (uint?)victim, (string)null);
			var victims = slots.Keys.Count(k => k.Type == type && k.Name == name && k.Victim != null);
			return slots.ContainsKey(key) || victims < MaxVictimMarkers ? key : (type, name, null, null);
		}

		/// <summary>
		/// One aggregated run under construction: its position in the batch, the victim it is for (null for a residual or
		/// non-victim run), the ticks it covers, the distinct actors and attackers it names, the damage and HP it sums or tracks,
		/// and the latest notification whose payload it keeps.
		/// </summary>
		sealed class Slot
		{
			public readonly string Type;
			public readonly string Name;
			public readonly uint? Victim;
			public readonly int Index;
			public readonly List<uint> Actors = [];
			public readonly List<uint> Attackers = [];
			public int Count;
			public int From;
			public int To;
			int? damageTotal;
			int? hpFrom;
			int? hpTo;
			NotificationV1 latest;

			public Slot(string type, string name, uint? victim, int index, int tick)
			{
				Type = type;
				Name = name;
				Victim = victim;
				Index = index;
				From = tick;
				To = tick;
			}

			public void Add(object dto, int tick, uint? actor)
			{
				Count++;
				From = Math.Min(From, tick);
				To = Math.Max(To, tick);
				if (actor is uint id && Actors.Count < MaxActors && !Actors.Contains(id))
					Actors.Add(id);

				switch (dto)
				{
					case DamagedV1 hit:
						damageTotal = (damageTotal ?? 0) + hit.Damage;
						TrackHp(hit.Hp + hit.Damage, hit.Hp);
						AddAttacker(hit.Attacker);
						break;
					case HealedV1 heal:
						TrackHp(heal.Hp, heal.Hp);
						AddAttacker(heal.Healer);
						break;
					case NotificationV1 note:
						latest = note;
						break;
				}
			}

			public AggregatedV1 Marker() => new(Type, Count, From, To, Actors.ToArray(), Name, damageTotal, hpFrom, hpTo,
				Attackers.Count > 0 ? Attackers.ToArray() : null, latest?.Item, latest?.QueueType, latest?.Reason, latest?.Cost,
				latest?.RemainingCost, latest?.Funds, latest?.ShortBy, latest?.Producer, latest?.BlockedBy, latest?.Cell,
				latest?.PlacementRequired, latest?.Options, latest?.Placeable);

			// HP is tracked only for one victim, since a residual run spans several victims' HP.
			void TrackHp(int from, int to)
			{
				if (Victim is null)
					return;

				hpFrom ??= from;
				hpTo = to;
			}

			void AddAttacker(uint? attacker)
			{
				if (attacker is uint id && Attackers.Count < MaxActors && !Attackers.Contains(id))
					Attackers.Add(id);
			}
		}
	}
}
