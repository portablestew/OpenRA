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
using System.Threading;
using OpenRA.Network;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// Starts a fresh local skirmish on a map and waits for its Regular world (D7, D10). Never records a replay and never
	/// rejoins one. The game thread runs only the short start and check steps; the waiting happens on the socket thread,
	/// so the game loop is never blocked.
	/// </summary>
	public static class AgentLoadMap
	{
		public const int TimeoutMs = 60000;
		const int PollIntervalMs = 50;

		/// <summary>The load in flight, or null. Read and written on the game thread only.</summary>
		static Pending pending;

		sealed record Pending(string Uid, World Previous);

		/// <summary>Starts the load and returns the state of its new world. Blocks only the calling (socket) thread.</summary>
		public static StateV1 Load(string name)
		{
			var request = AgentMarshal.ExecuteOnGameThread(() => Begin(name));

			var deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);
			while (true)
			{
				var state = AgentMarshal.ExecuteOnGameThread(() => Check(request));
				if (state != null)
					return state;

				if (DateTime.UtcNow >= deadline)
				{
					AgentMarshal.ExecuteOnGameThread(() => pending = null);
					throw new TimeoutException($"the new game did not start within {TimeoutMs / 1000} s");
				}

				Thread.Sleep(PollIntervalMs);
			}
		}

		/// <summary>Resolves the map, then tears down the current game and starts the new skirmish. Game thread.</summary>
		static Pending Begin(string name)
		{
			var map = Resolve(name);
			if (pending != null)
				throw new InvalidOperationException("a load_map is already in progress");

			var request = new Pending(map.Uid, AgentWorldBridge.Active);
			pending = request;
			try
			{
				Game.Disconnect();
				Ui.ResetAll();

				var om = Game.JoinServer(Game.CreateLocalServer(map.Uid, isSkirmish: true), "", recordReplay: false);

				// Same setup as Game.LoadMap. The server starts the game once this local client is ready (CheckAutoStart).
				void LobbyReady()
				{
					Game.LobbyInfoChanged -= LobbyReady;
					om.IssueOrder(Order.Command("option gamespeed default"));
					om.IssueOrder(Order.Command($"state {Session.ClientState.Ready}"));
				}

				Game.LobbyInfoChanged += LobbyReady;
			}
			catch
			{
				pending = null;
				throw;
			}

			return request;
		}

		/// <summary>Returns the new world's state once it is a live Regular world for the requested map, otherwise null. Game thread.</summary>
		static StateV1 Check(Pending request)
		{
			var world = AgentWorldBridge.Active;
			if (world == null || world == request.Previous || world.Type != WorldType.Regular || world.Map.Uid != request.Uid)
				return null;

			pending = null;
			return AgentStateDump.Snapshot(world) with { Cmd = "load_map" };
		}

		/// <summary>Matches a uid first, then a title. Only lobby-available maps can be loaded. Game thread.</summary>
		static MapPreview Resolve(string name)
		{
			var maps = Game.ModData.MapCache
				.Where(m => m.Status == MapStatus.Available && m.Visibility.HasFlag(MapVisibility.Lobby))
				.ToArray();

			var match = maps.Where(m => m.Uid == name).ToArray();
			if (match.Length == 0)
				match = maps.Where(m => m.Title == name).ToArray();

			if (match.Length == 0)
				throw new ArgumentException($"Unknown map '{name}'");

			if (match.Length > 1)
				throw new ArgumentException($"Ambiguous map title '{name}'; use the uid");

			return match[0];
		}
	}
}
