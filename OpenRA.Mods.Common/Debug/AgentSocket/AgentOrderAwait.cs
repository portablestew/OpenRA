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

using System.Collections.Generic;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// An order the socket issued that has not resolved yet. Registered on the game thread at issue; resolved from
	/// AgentWorldEventObserver.Ordered, which runs for every resolved order before the subscriber check. The registry is
	/// world-scoped: a resolution only matches an await registered on the same world, because Undo swaps the world and
	/// actor IDs are not unique across worlds.
	/// </summary>
	public sealed class AgentOrderAwait
	{
		static readonly List<AgentOrderAwait> Pending = [];

		readonly Actor subject;
		readonly string order;

		AgentOrderAwait(Actor subject, string order)
		{
			this.subject = subject;
			this.order = order;
		}

		/// <summary>The world the order was issued on.</summary>
		public World World => subject.World;

		/// <summary>True once the order has resolved, whether or not its validators passed.</summary>
		public bool Resolved { get; private set; }

		/// <summary>The validator result for the resolved order. Only meaningful once Resolved is true.</summary>
		public bool Valid { get; private set; }

		/// <summary>The world tick the order resolved on. Only meaningful once Resolved is true.</summary>
		public int ResolvedTick { get; private set; }

		/// <summary>
		/// Registers an await for an order that is about to be issued on its subject's world. Also drops awaits whose
		/// subject has died since it was registered, since the engine never resolves an order for a dead subject.
		/// Must run on the game thread.
		/// </summary>
		public static AgentOrderAwait Register(Order order)
		{
			Pending.RemoveAll(p => p.subject.IsDead);

			var await = new AgentOrderAwait(order.Subject, order.OrderString);
			Pending.Add(await);
			return await;
		}

		/// <summary>
		/// Resolves the oldest pending await that matches the order's world, subject and order name. An order with no
		/// matching await (issued by something other than the socket) is ignored. Must run on the game thread.
		/// </summary>
		public static void Resolve(Order order, bool valid)
		{
			var world = order.Subject.World;
			var index = Pending.FindIndex(p => p.World == world && p.subject.ActorID == order.Subject.ActorID && p.order == order.OrderString);
			if (index < 0)
				return;

			var await = Pending[index];
			Pending.RemoveAt(index);
			await.Resolved = true;
			await.Valid = valid;
			await.ResolvedTick = world.WorldTick;
		}

		/// <summary>The number of pending awaits on world. Test use.</summary>
		public static int PendingCount(World world)
		{
			return Pending.FindAll(p => p.World == world).Count;
		}

		/// <summary>Drops every pending await. Test use.</summary>
		public static void Clear()
		{
			Pending.Clear();
		}
	}
}
