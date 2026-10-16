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

using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The rolling window's container rules in isolation: game-time ordering, the 5 s gap on write, and the
	/// undo/redo target searches around the exact gap boundary. Snapshots carry no world here, so these run
	/// without a headless mod. Ticks are 40 ms (the default game speed), so 125 ticks is exactly 5000 ms.
	/// </summary>
	[TestFixture]
	sealed class WorldForkWindowTest
	{
		const int Timestep = 40;
		const int LiveTick = 250;
		const int GapTicks = (int)(WorldForks.MinGapMs / Timestep);

		[SetUp]
		public void ClearWindow()
		{
			WorldForks.Clear();
		}

		[TearDown]
		public void ClearWindowAfter()
		{
			WorldForks.Clear();
		}

		[TestCase(TestName = "Undo selects a snapshot exactly 5000 ms older than the live tick")]
		public void UndoSelectsExactlyTheGapBoundary()
		{
			Insert(GapTicks);

			Assert.That(WorldForks.FindUndo(LiveTick * (long)Timestep)?.WorldTick, Is.EqualTo(GapTicks));
		}

		[TestCase(TestName = "Undo ignores a snapshot one tick inside the 5000 ms gap")]
		public void UndoRejectsOneTickInside()
		{
			Insert(GapTicks + 1);

			Assert.That(WorldForks.FindUndo(LiveTick * (long)Timestep), Is.Null);
		}

		[TestCase(TestName = "Undo accepts a snapshot one tick outside the 5000 ms gap")]
		public void UndoAcceptsOneTickOutside()
		{
			Insert(GapTicks - 1);

			Assert.That(WorldForks.FindUndo(LiveTick * (long)Timestep)?.WorldTick, Is.EqualTo(GapTicks - 1));
		}

		[TestCase(TestName = "Undo skips the snapshot at the live tick and takes the newest eligible one")]
		public void UndoPicksNewestEligible()
		{
			// Snapshots must be at least one gap apart, so the window is built from spaced checkpoints.
			Insert(0, GapTicks, LiveTick);

			Assert.That(WorldForks.FindUndo(LiveTick * (long)Timestep)?.WorldTick, Is.EqualTo(GapTicks));
		}

		[TestCase(TestName = "Redo selects a snapshot exactly 5000 ms newer than the live tick")]
		public void RedoSelectsExactlyTheGapBoundary()
		{
			Insert(LiveTick + GapTicks);

			Assert.That(WorldForks.FindRedo(LiveTick * (long)Timestep)?.WorldTick, Is.EqualTo(LiveTick + GapTicks));
		}

		[TestCase(TestName = "Redo ignores a snapshot one tick inside the 5000 ms gap")]
		public void RedoRejectsOneTickInside()
		{
			Insert(LiveTick + GapTicks - 1);

			Assert.That(WorldForks.FindRedo(LiveTick * (long)Timestep), Is.Null);
		}

		[TestCase(TestName = "Redo accepts a snapshot one tick outside the 5000 ms gap")]
		public void RedoAcceptsOneTickOutside()
		{
			Insert(LiveTick + GapTicks + 1);

			Assert.That(WorldForks.FindRedo(LiveTick * (long)Timestep)?.WorldTick, Is.EqualTo(LiveTick + GapTicks + 1));
		}

		[TestCase(TestName = "Redo skips the snapshot at the live tick and takes the oldest eligible one")]
		public void RedoPicksOldestEligible()
		{
			// Live at GapTicks: the snapshot at the live tick is too near, and the one at 2 * GapTicks is the oldest eligible.
			Insert(0, GapTicks, 2 * GapTicks);

			Assert.That(WorldForks.FindRedo(GapTicks * (long)Timestep)?.WorldTick, Is.EqualTo(2 * GapTicks));
		}

		[TestCase(TestName = "A write one tick inside the 5000 ms gap replaces the older checkpoint")]
		public void WriteInsideGapReplacesOlder()
		{
			Insert(GapTicks);
			WorldForks.InsertWithGapDedupe(At(GapTicks + GapTicks - 1));

			Assert.That(WindowTicks(), Is.EqualTo(new[] { GapTicks + GapTicks - 1 }));
		}

		[TestCase(TestName = "A write exactly 5000 ms away keeps the older checkpoint")]
		public void WriteAtGapKeepsOlder()
		{
			Insert(GapTicks);
			WorldForks.InsertWithGapDedupe(At(GapTicks + GapTicks));

			Assert.That(WindowTicks(), Is.EqualTo(new[] { GapTicks, GapTicks + GapTicks }));
		}

		[TestCase(TestName = "A write one tick outside the 5000 ms gap keeps the older checkpoint")]
		public void WriteOutsideGapKeepsOlder()
		{
			Insert(GapTicks);
			WorldForks.InsertWithGapDedupe(At(GapTicks + GapTicks + 1));

			Assert.That(WindowTicks(), Is.EqualTo(new[] { GapTicks, GapTicks + GapTicks + 1 }));
		}

		[TestCase(TestName = "Writes are held in game-time order regardless of arrival order")]
		public void WritesAreOrderedByGameTime()
		{
			Insert(500, 125, 300);

			Assert.That(WindowTicks(), Is.EqualTo(new[] { 125, 300, 500 }));
		}

		[TestCase(TestName = "Eviction keeps the window within capacity and leaves it untouched when it already fits")]
		public void EvictionLeavesAWindowThatFits()
		{
			Insert(GapTicks);
			Write(2 * GapTicks, liveTick: 0, capacity: 2);

			Assert.That(WindowTicks(), Is.EqualTo(new[] { GapTicks, 2 * GapTicks }));
		}

		[TestCase(TestName = "After a low-tick write, eviction drops the farthest future checkpoints and keeps the near ones")]
		public void EvictionAfterLowTickWriteDropsFarthest()
		{
			// Live has rewound to tick 0; the future checkpoints are the farthest from it, so they go first.
			Insert(GapTicks, 2 * GapTicks, 3 * GapTicks, 4 * GapTicks);
			Write(0, liveTick: 0, capacity: 4);

			Assert.That(WindowTicks(), Is.EqualTo(new[] { 0, GapTicks, 2 * GapTicks, 3 * GapTicks }));
		}

		[TestCase(TestName = "Eviction repeats until the window fits, removing the farthest each time")]
		public void EvictionRepeatsUntilWithinCapacity()
		{
			Insert(GapTicks, 2 * GapTicks, 3 * GapTicks, 4 * GapTicks, 5 * GapTicks);
			Write(0, liveTick: 0, capacity: 4);

			// Two evictions: 5 * GapTicks (distance 5 gaps), then 4 * GapTicks (distance 4 gaps).
			Assert.That(WindowTicks(), Is.EqualTo(new[] { 0, GapTicks, 2 * GapTicks, 3 * GapTicks }));
		}

		[TestCase(TestName = "Eviction breaks an equal-distance tie by removing the older snapshot")]
		public void EvictionTieRemovesOlder()
		{
			// Live at 250: 125 and 375 are both 5000 ms away, so the older one (125) goes.
			Insert(GapTicks, 3 * GapTicks);
			Write(5 * GapTicks, liveTick: 2 * GapTicks, capacity: 2);

			Assert.That(WindowTicks(), Is.EqualTo(new[] { 3 * GapTicks, 5 * GapTicks }));
		}

		[TestCase(TestName = "Eviction never removes the snapshot just written, even when it is the farthest")]
		public void EvictionKeepsTheJustWrittenSnapshot()
		{
			// The write at 625 is the farthest from live 0, but it is the newest checkpoint and must survive;
			// 250 is the farthest of the others, so it goes.
			Insert(GapTicks, 2 * GapTicks);
			Write(5 * GapTicks, liveTick: 0, capacity: 2);

			Assert.That(WindowTicks(), Is.EqualTo(new[] { GapTicks, 5 * GapTicks }));
		}

		[TestCase(TestName = "Eviction keeps the just-written snapshot even when capacity leaves no other slot")]
		public void EvictionAtCapacityOneKeepsOnlyTheWrite()
		{
			Insert(GapTicks, 2 * GapTicks);
			Write(5 * GapTicks, liveTick: 0, capacity: 1);

			Assert.That(WindowTicks(), Is.EqualTo(new[] { 5 * GapTicks }));
		}

		static void Write(int tick, int liveTick, int capacity)
		{
			var snapshot = At(tick);
			WorldForks.InsertWithGapDedupe(snapshot);
			WorldForks.EvictFarthest(liveTick * (long)Timestep, snapshot, capacity);
		}

		static void Insert(params int[] ticks)
		{
			foreach (var tick in ticks)
				WorldForks.InsertWithGapDedupe(At(tick));
		}

		static WorldForks.Snapshot At(int tick)
		{
			return new WorldForks.Snapshot(null, tick, tick * (long)Timestep, 0, -1, -1, -1, -1, 0, 0.0, 0);
		}

		// The window is private; read it the way WorldForkUnsupportedTest reaches its engine internals.
		static int[] WindowTicks()
		{
			var field = typeof(WorldForks).GetField("Snapshots", BindingFlags.NonPublic | BindingFlags.Static);
			Assert.That(field, Is.Not.Null, "WorldForks.Snapshots was renamed; update this check.");

			return ((IEnumerable)field.GetValue(null)).Cast<WorldForks.Snapshot>().Select(s => s.WorldTick).ToArray();
		}
	}
}
