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
using System.Threading.Tasks;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// An order the socket issued that has not settled yet. Registered on the game thread at issue. Resolve moves it from
	/// Pending to Settling when the order resolves: AgentWorldEventObserver.Ordered runs for every resolved order, before the
	/// subscriber check. Tick, the settle clock, completes it once settleTicks have passed, or fails it when the resolve budget
	/// runs out or its subject dies. The socket thread parks in Wait until then. The registry is world-scoped: a resolution only
	/// matches an await on the same world, because Undo swaps the world and actor IDs are not unique across worlds.
	/// The socket thread abandons its await on a wall-clock timeout, so every list access takes Gate.
	/// </summary>
	public sealed class AgentOrderAwait
	{
		/// <summary>Game ticks an order may take to resolve before its await fails (Q13).</summary>
		public const int ResolveBudgetTicks = 50;

		/// <summary>The wall-clock backstop for one Wait. The socket thread gives up after this, even if the game does not tick.</summary>
		public const int WallClockMs = 10000;

		/// <summary>The ticks a Move holds after it resolves, unless the request says otherwise.</summary>
		public const int DefaultSettleTicks = 3;

		static readonly Lock Gate = new();
		static readonly List<AgentOrderAwait> Pending = [];
		static readonly List<AgentOrderAwait> Settling = [];

		readonly Actor subject;
		readonly string order;
		readonly int registeredTick;
		readonly TaskCompletionSource<bool> done = new(TaskCreationOptions.RunContinuationsAsynchronously);

		AgentOrderAwait(Actor subject, string order, int settleTicks)
		{
			this.subject = subject;
			this.order = order;
			SettleTicks = settleTicks;
			registeredTick = subject.World.WorldTick;
		}

		/// <summary>The world the order was issued on.</summary>
		public World World => subject.World;

		/// <summary>The ticks the await holds after it resolves.</summary>
		public int SettleTicks { get; }

		/// <summary>True once the order has resolved, whether or not its validators passed.</summary>
		public bool Resolved { get; private set; }

		/// <summary>The validator result for the resolved order. Only meaningful once Resolved is true.</summary>
		public bool Valid { get; private set; }

		/// <summary>The world tick the order resolved on. Only meaningful once Resolved is true.</summary>
		public int ResolvedTick { get; private set; }

		/// <summary>True once the await has held its settle ticks. A failed await never settles.</summary>
		public bool Settled { get; private set; }

		/// <summary>The path a Move planned, source cell first. Null until the Move's first path evaluation is captured.</summary>
		public int[][] Path { get; private set; }

		/// <summary>
		/// Registers an await for an order that is about to be issued on its subject's world. Must run on the game thread.
		/// </summary>
		public static AgentOrderAwait Register(Order order, int settleTicks)
		{
			var await = new AgentOrderAwait(order.Subject, order.OrderString, settleTicks);
			lock (Gate)
				Pending.Add(await);

			return await;
		}

		/// <summary>
		/// Parks the calling thread until the await settles or fails, or until timeoutMs of wall-clock time passes. A failed
		/// await throws its reason. Must not run on the game thread: the game thread is what completes it.
		/// </summary>
		public void Wait(int timeoutMs = WallClockMs)
		{
			if (Task.WaitAny([done.Task], timeoutMs) == -1)
			{
				Abandon();
				throw new TimeoutException($"the order did not settle within {timeoutMs} ms of wall-clock time");
			}

			done.Task.GetAwaiter().GetResult();
		}

		/// <summary>
		/// Resolves the oldest pending await that matches the order's world, subject and order name. An order with no
		/// matching await (issued by something other than the socket) is ignored. Must run on the game thread.
		/// </summary>
		public static void Resolve(Order order, bool valid)
		{
			var world = order.Subject.World;
			lock (Gate)
			{
				var index = Pending.FindIndex(p => p.World == world && p.subject.ActorID == order.Subject.ActorID && p.order == order.OrderString);
				if (index < 0)
					return;

				var await = Pending[index];
				Pending.RemoveAt(index);
				await.Resolved = true;
				await.Valid = valid;
				await.ResolvedTick = world.WorldTick;
				Settling.Add(await);
			}
		}

		/// <summary>
		/// True while a resolved Move for subject still waits for its path. Move.cs asks this before it builds a path event,
		/// so the path is captured without a subscriber. Must run on the game thread.
		/// </summary>
		public static bool AwaitingPath(Actor subject)
		{
			lock (Gate)
				return Settling.Exists(p => p.order == "Move" && p.Path == null && ReferenceEquals(p.subject, subject));
		}

		/// <summary>Stores the path a Move planned, for the await on subject. An empty cell list is a path that found nothing. Must run on the game thread.</summary>
		public static void CapturePath(Actor subject, int[][] cells)
		{
			lock (Gate)
			{
				var await = Settling.Find(p => p.order == "Move" && p.Path == null && ReferenceEquals(p.subject, subject));
				if (await != null)
					await.Path = cells;
			}
		}

		/// <summary>
		/// The settle clock. AgentWorldBridge calls it on every world tick. Completes the awaits on world that have held their
		/// settle ticks, and fails the ones whose subject died or whose resolve budget ran out. Must run on the game thread.
		/// </summary>
		public static void Tick(World world)
		{
			lock (Gate)
			{
				if (Pending.Count == 0 && Settling.Count == 0)
					return;

				foreach (var p in Pending.Where(p => p.World == world).ToArray())
				{
					if (p.subject.IsDead)
						Fail(p, new InvalidOperationException($"actor {p.subject.ActorID} died before the order resolved"));
					else if (world.WorldTick - p.registeredTick >= ResolveBudgetTicks)
						Fail(p, new TimeoutException($"the order did not resolve within {ResolveBudgetTicks} ticks"));
				}

				foreach (var p in Settling.Where(p => p.World == world).ToArray())
					if (world.WorldTick - p.ResolvedTick >= p.SettleTicks)
						Complete(p);
			}
		}

		/// <summary>
		/// Fails every await with the restored error. WorldAdopted calls it: an undo swaps the world, so no pending order will
		/// resolve on the old one. Must run on the game thread.
		/// </summary>
		public static void FailAllRestored()
		{
			lock (Gate)
			{
				foreach (var p in Pending.Concat(Settling).ToArray())
					Fail(p, new InvalidOperationException("restored"));
			}
		}

		/// <summary>
		/// Fails the awaits on every disposed world with the restored error. WorldLoaded calls it: a load_map disposes the world it
		/// replaces, so no pending order will resolve there. Awaits on a world that is still alive are left alone. Must run on the game thread.
		/// </summary>
		public static void FailDisposedRestored()
		{
			lock (Gate)
			{
				foreach (var p in Pending.Concat(Settling).Where(p => p.World.Disposing).ToArray())
					Fail(p, new InvalidOperationException("restored"));
			}
		}

		/// <summary>The number of unresolved awaits on world. Test use.</summary>
		public static int PendingCount(World world)
		{
			lock (Gate)
				return Pending.Count(p => p.World == world);
		}

		/// <summary>Drops every await. Test use.</summary>
		public static void Clear()
		{
			lock (Gate)
			{
				Pending.Clear();
				Settling.Clear();
			}
		}

		/// <summary>Removes this await from the registry after its waiter gave up. Runs on the socket thread.</summary>
		void Abandon()
		{
			lock (Gate)
			{
				Pending.Remove(this);
				Settling.Remove(this);
			}
		}

		// Both are called under Gate. Removing an await here keeps it from leaking after its waiter has gone.
		static void Complete(AgentOrderAwait await)
		{
			Settling.Remove(await);
			await.Settled = true;
			await.done.TrySetResult(true);
		}

		static void Fail(AgentOrderAwait await, Exception reason)
		{
			Pending.Remove(await);
			Settling.Remove(await);
			await.done.TrySetException(reason);
		}
	}
}
