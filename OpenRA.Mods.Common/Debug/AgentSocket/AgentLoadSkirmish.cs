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
using System.Threading;
using OpenRA.Network;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// Starts a fresh local skirmish and waits for its Regular world (D7, D10): the map, the game speed and the CPU
	/// opponents come from the request (AgentSkirmish). Never records a replay and never rejoins one. The game thread
	/// runs only the short start and check steps; the waiting happens on the socket thread, so the game loop is never
	/// blocked. The lobby orders that fill the opponent slots are issued by AgentSkirmishLobby.Driver.
	/// </summary>
	public static class AgentLoadSkirmish
	{
		public const int TimeoutMs = 60000;
		const int PollIntervalMs = 50;

		/// <summary>The load in flight, or null. Read and written on the game thread only.</summary>
		static Pending pending;

		sealed class Pending
		{
			public SkirmishPlan Plan;
			public World Previous;
			public AgentSkirmishLobby.Driver Driver;
		}

		/// <summary>
		/// Starts the load and returns the new world's reply. Blocks only the calling (socket) thread. A timed-out step
		/// is not the end of the step: it may still run on the game thread later, so the clear of the pending load is
		/// queued behind it, and the next load_skirmish is not refused as already in progress.
		/// </summary>
		public static LoadSkirmishV1 Load(SkirmishRequest request)
		{
			return Run(request, TimeoutMs, StartSkirmish);
		}

		/// <summary>Load with the game-side connect step given, so the poll and its cleanup can be tested without a server.</summary>
		internal static LoadSkirmishV1 Load(string map, Func<OrderManager> connect)
		{
			return Run(DefaultRequest(map), TimeoutMs, _ => connect());
		}

		/// <summary>
		/// Load with the connect step and the poll deadline given, so the deadline path can be exercised in seconds
		/// rather than 60. Production always passes <see cref="TimeoutMs"/>; the behavior is otherwise identical.
		/// </summary>
		internal static LoadSkirmishV1 Load(string map, Func<OrderManager> connect, int timeoutMs)
		{
			return Run(DefaultRequest(map), timeoutMs, _ => connect());
		}

		static SkirmishRequest DefaultRequest(string map)
		{
			return new SkirmishRequest(map, AgentSkirmish.DefaultSpeed, AgentSkirmish.DefaultOpponents());
		}

		static LoadSkirmishV1 Run(SkirmishRequest request, int timeoutMs, Func<SkirmishPlan, OrderManager> connect)
		{
			Pending claimed = null;
			try
			{
				claimed = AgentMarshal.ExecuteOnGameThread(() => Begin(request, connect));

				var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
				while (true)
				{
					var reply = AgentMarshal.ExecuteOnGameThread(() => Check(claimed));
					if (reply != null)
						return reply;

					if (DateTime.UtcNow >= deadline)
						throw new TimeoutException($"the new game did not start within {timeoutMs / 1000} s");

					Thread.Sleep(PollIntervalMs);
				}
			}
			catch (TimeoutException)
			{
				// Either the game thread stalled on a step or the poll deadline passed. Clear the pending load (and its
				// lobby subscription) behind whatever step is still queued, so the next load is not refused.
				Game.RunAfterTick(() =>
				{
					var stale = pending;
					pending = null;
					stale?.Driver?.Dispose();
				});

				throw;
			}
			catch
			{
				// A failure after this load was claimed (a world that started without the requested bots, or a lobby
				// error) ends this load only. A failure before Begin claimed it leaves any other load alone.
				if (claimed != null)
				{
					Game.RunAfterTick(() =>
					{
						if (pending == claimed)
						{
							pending = null;
							claimed.Driver?.Dispose();
						}
					});
				}

				throw;
			}
		}

		/// <summary>Resolves the request and claims the pending load, then runs the start step. Game thread.</summary>
		static Pending Begin(SkirmishRequest request, Func<SkirmishPlan, OrderManager> connect)
		{
			var plan = AgentSkirmish.Resolve(request);
			if (pending != null)
				throw new InvalidOperationException("a load_skirmish is already in progress");

			var claimed = new Pending { Plan = plan, Previous = AgentWorldBridge.Active };
			pending = claimed;
			try
			{
				var orderManager = connect(plan);
				if (orderManager != null)
					claimed.Driver = new AgentSkirmishLobby.Driver(plan, orderManager);
			}
			catch
			{
				pending = null;
				throw;
			}

			return claimed;
		}

		/// <summary>
		/// Tears down the current game and starts a fresh local skirmish on the plan's map, and returns its order
		/// manager. Game thread.
		/// </summary>
		static OrderManager StartSkirmish(SkirmishPlan plan)
		{
			Game.Disconnect();
			Ui.ResetAll();

			// A Local (not Skirmish) server, so SkirmishLogic does not restore the player's last saved skirmish setup
			// over the requested map and bots (its handlers are gated on ServerType.Skirmish).
			return Game.JoinServer(Game.CreateLocalServer(plan.MapUid), "", recordReplay: false);
		}

		/// <summary>
		/// Returns the new world's reply once it is a live Regular world for the requested map that carries the
		/// requested bots, otherwise null. Throws when the world is up but wrong, so the load fails with a reason
		/// rather than reporting a lone world as a success. Game thread.
		/// </summary>
		static LoadSkirmishV1 Check(Pending claimed)
		{
			if (claimed.Driver?.Error != null)
				throw new InvalidOperationException(claimed.Driver.Error);

			var world = AgentWorldBridge.Active;
			if (world == null || world == claimed.Previous || world.Type != WorldType.Regular || world.Map.Uid != claimed.Plan.MapUid)
				return null;

			var error = AgentSkirmish.VerifyOpponents(world, claimed.Plan.Opponents);
			if (error != null)
				throw new InvalidOperationException(error);

			pending = null;
			claimed.Driver?.Dispose();
			return AgentSkirmish.Reply(world, claimed.Plan);
		}
	}
}
