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
using OpenRA.Graphics;
using OpenRA.Mods.Common.AgentSocket;
using OpenRA.Network;
using OpenRA.Traits;

namespace OpenRA.Mods.Common
{
	public class AgentWorldBridgeInfo : TraitInfo<AgentWorldBridge> { }

	/// <summary>
	/// Tracks the world the agent socket talks to. Registered in the World: block only (D2). Does nothing unless
	/// Debug.AgentSocket is set (D12). Starts the listener on the first non-headless world load.
	/// </summary>
	public sealed class AgentWorldBridge : IWorldLoaded, IWorldAdopted, ITick
	{
		static World active;
		static WorldRenderer activeRenderer;

		/// <summary>True when Debug.AgentSocket was set at the last world load.</summary>
		public static bool Enabled { get; private set; }

		/// <summary>The live world, or null when none is loaded or it is being torn down. Read on the game thread only.</summary>
		public static World Active => active is { Disposing: false } ? active : null;

		/// <summary>True when a world is loaded. Safe to read from any thread; the world itself is read on the game thread only.</summary>
		public static bool Loaded => Volatile.Read(ref active) != null;

		/// <summary>The renderer for <see cref="Active"/>, or null when there is none (headless, or no world). Read on the game thread only.</summary>
		public static WorldRenderer ActiveRenderer => Active == null ? null : activeRenderer;

		public static World RequireWorld()
		{
			return Active ?? throw new InvalidOperationException("no game world is loaded");
		}

		public void WorldLoaded(World w, WorldRenderer wr)
		{
			// A load_skirmish disposes the world it replaces. Its pending awaits get the restored error, as in WorldAdopted.
			AgentOrderAwait.FailDisposedRestored();

			// The events waiting for a turn belong to the world this replaces (8.8). The new world's own events come after this.
			AgentEvents.DiscardWaiting();

			Enabled = Game.Settings.Debug.AgentSocket;
			AgentEvents.Enabled = Enabled;
			UnitOrders.OrderResolving = Enabled ? AgentWorldEventObserver.Ordered : null;
			OpenRA.Sound.NotificationPlayed = Enabled ? AgentNotifications.Raise : null;
			if (!Enabled)
				return;

			// The in-game world takes precedence. The shellmap is used only while no world is alive.
			if (w.Type == WorldType.Regular || Active == null)
			{
				active = w;
				activeRenderer = wr;
			}

			if (!w.IsHeadless)
				AgentSocketServer.Start(Game.Settings.Debug.AgentSocketPort);
		}

		// The settle clock for the socket's awaits on this world (P1.3). It runs on the world actor each tick.
		void ITick.Tick(Actor self)
		{
			AgentOrderAwait.Tick(self.World);
		}

		public void WorldAdopted(World w, WorldRenderer wr)
		{
			// An undo swaps the world, so no pending order resolves on the old one. Their waiters get the restored error.
			AgentOrderAwait.FailAllRestored();

			// An undo's events of the timeline it rewinds are not part of the restored world (8.8).
			AgentEvents.DiscardWaiting();

			if (Enabled)
			{
				active = w;
				activeRenderer = wr;
			}
		}
	}
}
