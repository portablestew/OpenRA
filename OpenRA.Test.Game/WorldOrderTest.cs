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
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// Broadens the rung-4 oracle from "a world ticks deterministically" to "a world executes orders
	/// deterministically". <see cref="WorldSyncHashTest"/> only proves that an untouched world evolves
	/// reproducibly; nothing there drives the order pipeline, so an order-resolution or activity bug would
	/// go unseen. This fixture issues real orders into the headless world and asserts on the resulting
	/// unit behaviour.
	/// <para>
	/// The move order is deliberately first: it is the only interesting order whose outcome involves no
	/// randomness at all, so an exact-cell assertion is legitimate and a failure points squarely at the
	/// order pipeline or the movement activity rather than at a sampling difference.
	/// </para>
	/// </summary>
	[TestFixture]
	sealed class WorldOrderTest
	{
		/// <summary>How far the unit is asked to walk. Short enough to finish quickly, long enough that arrival is not trivial.</summary>
		const int MoveDistance = 4;

		/// <summary>
		/// Generous upper bound on ticks to walk <see cref="MoveDistance"/> cells. E1 moves at Speed 100,
		/// so roughly ten ticks per cell; the cap only needs to fail the test when movement has actually
		/// stalled rather than merely being slow.
		/// </summary>
		const int MoveTimeoutTicks = 250;

		/// <summary>Ticks to let the map's starting units finish spawning and settle before the test spawns its own actor.</summary>
		const int SettleTicks = 5;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		/// <summary>
		/// A move scenario in progress: an <c>e1</c> spawned on a clear run near the local player's spawn,
		/// with a move order to the far end of that run already issued (but not yet resolved - see
		/// <see cref="HeadlessWorld.OrderLatencyTicks"/>).
		/// </summary>
		sealed record MoveScenario(HeadlessWorld World, Actor Unit, CPos Start, CPos Destination);

		static MoveScenario StartMoveScenario(int seed)
		{
			var w = HeadlessWorld.Create(seed);

			// Let the map's own starting units spawn and settle first, so the clear-run search does not pick
			// cells they are about to occupy.
			w.Tick(SettleTicks);

			var (start, destination) = w.FindClearRun(w.LocalPlayer.HomeLocation, MoveDistance);

			var unit = w.CreateActor("e1", start);

			Assert.That(unit.Location, Is.EqualTo(start),
				"The spawned unit did not land on the requested cell, so the move assertions would be measuring the wrong thing.");
			Assert.That(unit.Owner, Is.EqualTo(w.LocalPlayer),
				"The unit must be owned by the local client's player or ValidateOrder will silently drop its orders.");
			Assert.That(unit.IsIdle, Is.True,
				"A freshly spawned e1 was not idle. The order-latency assertions use idleness to detect the move activity " +
				"appearing, so they would be meaningless if something else had already queued an activity.");

			w.IssueMoveOrder(unit, destination);

			return new MoveScenario(w, unit, start, destination);
		}

		[TestCase(TestName = "A move order walks a unit to the target cell")]
		public void MoveOrderReachesTargetCell()
		{
			var s = StartMoveScenario(0x0FF1CE);
			using var w = s.World;

			var arrivedAt = w.TickUntil(() => s.Unit.Location == s.Destination && s.Unit.IsIdle, MoveTimeoutTicks);

			Assert.That(arrivedAt, Is.Not.Null,
				$"The unit did not reach {s.Destination} within {MoveTimeoutTicks} ticks (it stopped at {s.Unit.Location}, " +
				$"idle: {s.Unit.IsIdle}). Either the order was dropped or the movement activity stalled.");

			Assert.That(s.Unit.Location, Is.EqualTo(s.Destination));
			Assert.That(s.Unit.Location, Is.Not.EqualTo(s.Start),
				"The unit ended where it started, so the test would pass without any movement having happened.");
			Assert.That(s.Unit.IsDead, Is.False, "The unit died during the move.");
		}

		[TestCase(TestName = "A move order takes effect exactly one tick after the tick it is sent on")]
		public void MoveOrderLatencyIsOneTick()
		{
			var s = StartMoveScenario(0x1234);
			using var w = s.World;

			// Tick 1 after issuing only *sends* the order: IssueOrder merely buffered it, TryTick hands it to
			// the connection, and EchoConnection projects the packet forward one net frame. So no activity can
			// exist yet.
			Assert.That(w.TickOnce(), Is.True, "The world failed to advance.");
			Assert.That(s.Unit.IsIdle, Is.True,
				"The unit already had an activity one tick after the order was issued, but the order cannot have been " +
				"received yet. HeadlessWorld.OrderLatencyTicks understates the real latency.");

			// Tick 2 receives the projected packet, resolves the order onto the actor, and runs the resulting
			// activity - all within the same iteration.
			Assert.That(w.TickOnce(), Is.True, "The world failed to advance.");
			Assert.That(s.Unit.IsIdle, Is.False,
				$"The move order had still not produced an activity {1 + HeadlessWorld.OrderLatencyTicks} ticks after being " +
				"issued. Either it was dropped (check ValidateOrder and the actor's owner) or the latency has grown, " +
				"in which case HeadlessWorld.OrderLatencyTicks is stale and every timing assertion built on it is wrong.");
		}

		[TestCase(TestName = "The same move order in two same-seed worlds plays out identically")]
		public void MoveOrderIsDeterministic()
		{
			var a = RunMoveToCompletion(0x4242);
			var b = RunMoveToCompletion(0x4242);

			Assert.That(a.ArrivedAt, Is.Not.Null, "The first run never arrived, so there is nothing to compare.");

			// The scenario itself must be seed-stable: same spawn, same clear-run search result, same target.
			Assert.That(b.Start, Is.EqualTo(a.Start), "The clear-run search picked a different start cell for the same seed.");
			Assert.That(b.Destination, Is.EqualTo(a.Destination), "The clear-run search picked a different destination for the same seed.");

			Assert.That(b.ArrivedAt, Is.EqualTo(a.ArrivedAt),
				"The unit took a different number of ticks to make the same journey in two same-seed worlds.");
			Assert.That(b.Hashes, Is.EqualTo(a.Hashes),
				"Two same-seed worlds executing the same move order produced different SyncHash sequences - " +
				"order execution is not deterministic.");
		}

		sealed record MoveResult(int? ArrivedAt, List<int> Hashes, CPos Start, CPos Destination);

		/// <summary>Runs a move scenario to arrival, recording the SyncHash after every ticked frame.</summary>
		static MoveResult RunMoveToCompletion(int seed)
		{
			var s = StartMoveScenario(seed);
			using var w = s.World;

			var hashes = new List<int>();
			var arrivedAt = w.TickUntil(() =>
			{
				hashes.Add(w.World.SyncHash());
				return s.Unit.Location == s.Destination && s.Unit.IsIdle;
			}, MoveTimeoutTicks);

			return new MoveResult(arrivedAt, hashes, s.Start, s.Destination);
		}
	}
}
