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
using OpenRA.Network;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// The outcome of one lobby phase (9.1). Commands are the orders to issue, Slots are the free bot slots phase 1
	/// chose (phase 2 matches the placed bots against them), and Error is set when the phase cannot run at all. A
	/// phase 2 with Commands null is not an error: the bots it waits for are not in the lobby yet.
	/// </summary>
	public sealed record LobbyStep(string[] Commands, string[] Slots, string Error)
	{
		public static LobbyStep Failed(string error) => new(null, null, error);
	}

	/// <summary>
	/// The lobby side of load_skirmish (plan §2.4, §4 item 3): the two phases of lobby orders that fill playable slots
	/// with bots carrying per-opponent handicaps. Both phases are pure functions of the request and a lobby snapshot,
	/// so they are testable with no server; <see cref="Driver"/> is the small state machine that runs them from
	/// Game.LobbyInfoChanged. The order strings are the same ones the lobby UI issues (LobbyUtils, LobbyLogic).
	/// </summary>
	public static class AgentSkirmishLobby
	{
		/// <summary>
		/// Phase 1: the speed option, then one slot_bot per opponent in the first free slots that allow bots, in
		/// PlayerReference name order so the choice is deterministic (Session.FirstEmptyBotSlot has the right predicate
		/// but no documented order). A slot that locks handicap is refused here rather than silently keeping its value.
		/// </summary>
		public static LobbyStep PhaseOne(SkirmishPlan plan, Session lobby, int controllerIndex)
		{
			var free = lobby.Slots
				.Where(kv => !kv.Value.Closed && kv.Value.AllowBots && lobby.ClientInSlot(kv.Key) == null)
				.Select(kv => kv.Key)
				.OrderBy(k => k, StringComparer.Ordinal)
				.ToList();

			if (free.Count < plan.Opponents.Length)
				return LobbyStep.Failed($"cannot place {plan.Opponents.Length} opponents: {free.Count} free slots allow bots");

			var slots = free.Take(plan.Opponents.Length).ToArray();
			var commands = new List<string>(plan.Opponents.Length + 1) { $"option gamespeed {plan.Speed}" };
			for (var i = 0; i < plan.Opponents.Length; i++)
			{
				if (lobby.Slots[slots[i]].LockHandicap)
					return LobbyStep.Failed($"slot {slots[i]} locks handicap");

				commands.Add($"slot_bot {slots[i]} {controllerIndex} {plan.Opponents[i].Bot}");
			}

			return new LobbyStep(commands.ToArray(), slots, null);
		}

		/// <summary>
		/// Phase 2: one handicap order per placed bot (the bot's client index only exists once the server has synced
		/// the slot_bot back), then the ready order that starts the game. Commands is null while any chosen slot does
		/// not hold the requested bot yet.
		/// </summary>
		public static LobbyStep PhaseTwo(SkirmishPlan plan, string[] slots, Session lobby)
		{
			var commands = new List<string>(plan.Opponents.Length + 1);
			for (var i = 0; i < plan.Opponents.Length; i++)
			{
				var client = lobby.ClientInSlot(slots[i]);
				if (client == null || client.Bot != plan.Opponents[i].Bot)
					return new LobbyStep(null, slots, null);

				commands.Add($"handicap {client.Index} {plan.Opponents[i].Handicap}");
			}

			commands.Add($"state {Session.ClientState.Ready}");
			return new LobbyStep(commands.ToArray(), slots, null);
		}

		/// <summary>
		/// Runs the two phases from Game.LobbyInfoChanged until the ready order is sent, then unsubscribes. The first
		/// callback issues phase 1 and then evaluates phase 2 in the same callback, so a request with no opponents
		/// starts at once (the option order does not sync when the speed is already the default). Every exit
		/// unsubscribes, so a failed or timed-out load leaves no listener behind.
		/// </summary>
		public sealed class Driver : IDisposable
		{
			static int active;

			/// <summary>Live subscriptions. A test asserts a failed load leaves this at zero.</summary>
			public static int Active => Volatile.Read(ref active);

			readonly SkirmishPlan plan;
			readonly OrderManager orderManager;
			string[] slots;
			bool phaseOneSent;
			bool subscribed;
			bool disposed;

			/// <summary>The reason the lobby could not be set up, or null. Read by the load's poll.</summary>
			public string Error { get; private set; }

			/// <summary>True once the ready order has been issued.</summary>
			public bool Done { get; private set; }

			public Driver(SkirmishPlan plan, OrderManager orderManager)
			{
				this.plan = plan;
				this.orderManager = orderManager;
				subscribed = true;
				Interlocked.Increment(ref active);
				Game.LobbyInfoChanged += OnLobbyInfoChanged;
			}

			void OnLobbyInfoChanged()
			{
				if (disposed || Done || Error != null)
					return;

				try
				{
					var lobby = orderManager.LobbyInfo;
					if (lobby == null)
						return;

					if (!phaseOneSent)
					{
						// The admin is the host: the UI takes the bot controller the same way (LobbyUtils).
						var controller = lobby.Clients.FirstOrDefault(c => c.IsAdmin)?.Index ?? orderManager.Connection.LocalClientId;
						var one = PhaseOne(plan, lobby, controller);
						if (one.Error != null)
						{
							Error = one.Error;
							Unsubscribe();
							return;
						}

						slots = one.Slots;
						foreach (var command in one.Commands)
							orderManager.IssueOrder(Order.Command(command));

						phaseOneSent = true;
					}

					var two = PhaseTwo(plan, slots, lobby);
					if (two.Error != null)
					{
						Error = two.Error;
						Unsubscribe();
						return;
					}

					if (two.Commands == null)
						return;

					foreach (var command in two.Commands)
						orderManager.IssueOrder(Order.Command(command));

					Done = true;
					Unsubscribe();
				}
				catch (Exception e)
				{
					Error = e.Message;
					Unsubscribe();
				}
			}

			void Unsubscribe()
			{
				if (!subscribed)
					return;

				subscribed = false;
				Game.LobbyInfoChanged -= OnLobbyInfoChanged;
				Interlocked.Decrement(ref active);
			}

			public void Dispose()
			{
				disposed = true;
				Unsubscribe();
			}
		}
	}
}
