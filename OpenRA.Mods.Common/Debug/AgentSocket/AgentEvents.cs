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

	// Schema v1 payloads. Wire keys are snake_case (AgentSocketCommands.Json). Only primitives, so the turn buffer holds no live objects.
	public sealed record DamagedV1(string Event, int Tick, uint Actor, string Type, string Owner, uint? Attacker,
		string AttackerType, string AttackerOwner, int Damage, int Hp, int MaxHp, string DamageState, string AttackerBearing, bool AttackerShrouded);

	public sealed record KilledV1(string Event, int Tick, uint Actor, string Type, string Owner, uint? Attacker,
		string AttackerType, string AttackerOwner, int Cost);

	// Captor is null when the local player does not know the captor (AgentFog), so the event carries no id it cannot see.
	public sealed record CapturedV1(string Event, int Tick, uint Actor, string Type, string From, string To, uint? Captor);

	public sealed record OrderEventV1(string Event, int Tick, int EffectTick, bool Valid, int Client, string Order, uint Actor,
		string Type, string Owner, int[] Cell, uint? TargetActor, bool Queued);

	// Producer is null when the local player does not know the producer (AgentFog).
	public sealed record ProducedV1(string Event, int Tick, uint Actor, string Type, string Owner, uint? Producer, string ProducerType);

	public sealed record StanceV1(string Event, int Tick, uint Actor, string Type, string Owner, string From, string To);

	// Metrics is null for no_path, and when getPath returned input the metrics function rejects. The planned cells are not sent: the
	// order reply's path carries them.
	public sealed record PathV1(string Event, int Tick, uint Actor, string Type, string Owner, int[] From, int[] To, string Check,
		bool Repath, string Result, PathMetrics Metrics);

	// Kind is move, transform or attack. State is started, completed or canceled. Reason says how an activity ended, or null.
	public sealed record ActivityV1(string Event, int Tick, uint Actor, string Type, string Owner, string Kind, string State,
		string Reason);

	public sealed record PlacedV1(string Event, int Tick, uint Actor, string Type, string Owner, int[] Cell);

	// The owner sold the actor (Sell), before it was disposed, so it is in the world when the event is raised. Cell is its top-left cell.
	public sealed record SoldV1(string Event, int Tick, uint Actor, string Type, string Owner, int[] Cell);

	// Owner is the player whose power changed. State is the PowerState name: Normal, Low or Critical.
	public sealed record PowerEventV1(string Event, int Tick, string Owner, int Provided, int Drained, int Excess, string State);

	public sealed record DefeatedV1(string Event, int Tick, string Owner);

	public sealed record GameOverV1(string Event, int Tick, string Winner);

	/// <summary>
	/// The event hub (D12, P4). Engine hooks call Raise with a DTO, and Raise adds it to every open connection's turn buffer, so
	/// the connection's next response reports it (P3.6). With the socket disabled, Raise returns after one volatile read.
	/// </summary>
	public static class AgentEvents
	{
		static volatile bool enabled;
		static readonly Lock Gate = new();

		// Copy-on-write: writers replace the array under Gate, so Raise can read it without a lock.
		static AgentConnection[] connections = [];

		/// <summary>
		/// Test use only: sees every event raised while the socket is enabled, with or without a connection (P3.1). Null in a
		/// running game, so production pays nothing for it.
		/// </summary>
		public static Action<object> Sink;

		/// <summary>Set from Debug.AgentSocket by AgentWorldBridge at each world load (D12).</summary>
		public static bool Enabled
		{
			get => enabled;
			set => enabled = value;
		}

		/// <summary>The number of open connections. Each one receives every event.</summary>
		public static int ConnectionCount => Volatile.Read(ref connections).Length;

		/// <summary>Adds dto to every open connection's turn buffer. Runs on the game thread; never blocks on socket I/O.</summary>
		public static void Raise(object dto)
		{
			if (!enabled)
				return;

			Sink?.Invoke(dto);
			foreach (var connection in Volatile.Read(ref connections))
				connection.Receive(dto);
		}

		/// <summary>
		/// Drops every open connection's waiting events. A world change runs it: the events belong to the world that is gone, so
		/// no turn of the new world reports them (8.8). Connections and their initial flag are left as they are.
		/// </summary>
		internal static void DiscardWaiting()
		{
			foreach (var connection in Volatile.Read(ref connections))
				connection.Discard();
		}

		/// <summary>Adds a connection to the hub. Called once, when the connection is created.</summary>
		internal static void Register(AgentConnection connection)
		{
			lock (Gate)
				connections = [.. connections, connection];
		}

		/// <summary>Removes a connection from the hub. Called once, when the connection closes.</summary>
		internal static void Unregister(AgentConnection connection)
		{
			lock (Gate)
				connections = connections.Where(c => c != connection).ToArray();
		}
	}

	/// <summary>
	/// One per connection, from the moment it is created until it closes. Its turn buffer holds the events raised since the
	/// connection's last response (P3.2), and the envelope reports them. Every connection sees every event.
	/// </summary>
	public sealed class AgentConnection
	{
		readonly Lock gate = new();
		bool closed;

		/// <summary>The events raised since this connection's last response.</summary>
		internal AgentTurnBuffer Turn { get; } = new();

		/// <summary>
		/// The option signature each production queue last reported on this connection, keyed by actor and queue type (D-3).
		/// Only the game thread reads or writes it, and a world change clears it, so the next production read lists every queue again.
		/// </summary>
		internal Dictionary<(uint Actor, string QueueType), string[]> SeenOptions { get; } = new();

		/// <summary>
		/// Whether an envelope on this connection has carried the static data (the map, and each player's faction, bot and handicap)
		/// since the last world change (C). A world change clears it, so the next state read sends the static data again.
		/// </summary>
		internal bool StaticsSent { get; set; }

		public AgentConnection()
		{
			AgentEvents.Register(this);
		}

		/// <summary>
		/// Drops the events waiting for this connection's next turn (a world change, 8.8), the production options it has seen,
		/// and the static data it has sent (C), since the world they describe is gone.
		/// </summary>
		internal void Discard()
		{
			Turn.Discard();
			StaticsSent = false;
			SeenOptions.Clear();
		}

		internal void Receive(object dto)
		{
			lock (gate)
			{
				if (!closed)
					Turn.Add(dto);
			}
		}

		/// <summary>The events raised since this connection's last response, in response order, and clears them.</summary>
		public List<object> Flush()
		{
			return Turn.Flush();
		}

		/// <summary>The events waiting for this connection's next response.</summary>
		public int Pending => Turn.Pending;

		/// <summary>Stops receiving and unregisters from the hub. Called once, when the connection ends.</summary>
		public void Close()
		{
			lock (gate)
				closed = true;

			AgentEvents.Unregister(this);
		}
	}
}
