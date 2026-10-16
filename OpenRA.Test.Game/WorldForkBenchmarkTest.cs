#region Copyright & License Information
/*
 *  Copyright (c) The OpenRA Developers and Contributors
 *  This file is part of OpenRA, which is free software. It is made
 *  available under the terms of the GNU General Public License
 *  as published by the Free Software Foundation, either version 3 of
 *  the License, or (at your option) any later version. For more
 *  information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// Throughput benchmark for the MCTS rollout primitive. It answers one question: what a fork costs
	/// relative to the two alternatives MCTS has for putting a world into a given state - building one
	/// from scratch (<see cref="HeadlessWorld.Create"/>) and ticking one forward. Search forks a node
	/// once per rollout and then ticks that rollout hundreds of times, so the fork price only matters if
	/// it approaches either of those.
	/// <para>
	/// This is a benchmark, not a guard: it is <c>[Explicit]</c> so it never runs as part of the suite,
	/// and it reports medians instead of asserting exact times. Run it deliberately:
	/// <code>dotnet test OpenRA.Test.Game --filter "FullyQualifiedName~WorldForkBenchmarkTest"</code>
	/// </para>
	/// <para>
	/// The subject is the rung-5 combat scenario from <see cref="WorldForkTest"/> - same seed, same
	/// two-e1 fight, same fork point: two e1s at tick 20 with live fire and the synced RNG already
	/// consumed. That is what an MCTS rollout forks from, and its state exercises the cloner hardest; an
	/// idle world would understate the fork. The parent is prepared once and never ticked again, so every
	/// fork sample starts from the identical state.
	/// </para>
	/// </summary>
	[TestFixture]
	sealed class WorldForkBenchmarkTest
	{
		/// <summary>The WorldForkTest combat fork's seed, reused so this measures the same scenario.</summary>
		const int Seed = 0xC0FFEE;

		/// <summary>Cells between the two combatants; well inside the M1Carbine's range so they engage promptly.</summary>
		const int Separation = 2;

		/// <summary>Ticks to let the map's starting units settle before placing the combatants.</summary>
		const int SettleTicks = 5;

		/// <summary>Ticks the parent is advanced before forking: the fork point, matching WorldForkTest.</summary>
		const int TicksBeforeFork = 20;

		/// <summary>Ticks in a rollout; matches WorldForkTest and is the depth a rollout's cost is dominated by.</summary>
		const int RolloutTicks = 60;

		/// <summary>
		/// Samples for Create. Deliberately few: a Create parses the map and constructs every actor on it,
		/// is expensive, and its variance does not decide the fork-versus-tick question.
		/// </summary>
		const int CreateSamples = 5;

		/// <summary>Samples for a fork. The median is reported because fork allocations invite GC pauses.</summary>
		const int ForkSamples = 25;

		/// <summary>Samples for the per-tick cost, each a full-depth rollout from a fresh fork.</summary>
		const int TickSamples = 5;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[Test]
		[Explicit("Throughput benchmark: reports measurements instead of guarding them, and costs seconds " +
			"of world construction per Create sample. Run deliberately with: " +
			"dotnet test OpenRA.Test.Game --filter \"FullyQualifiedName~WorldForkBenchmarkTest\"")]
		public void ForkVersusCreateVersusTick()
		{
			// Warm every path before timing anything: the JIT, the file cache behind map and sprite
			// loading, and the cloner's reflection over the world graph. The first Create in a process
			// pays one-time costs that must not land in a sample.
			using (var warm = HeadlessWorld.Create(Seed))
			{
				SetUpCombat(warm);
				using var warmFork = warm.Fork();
				warmFork.Tick(RolloutTicks);
			}

			var createSamples = MeasureCreate();

			// Prepared once, then never ticked again, so every fork below starts from this exact
			// mid-combat state - the position an MCTS node would be expanded from repeatedly.
			using var parent = HeadlessWorld.Create(Seed);
			SetUpCombat(parent);

			var forkSamples = new List<double>(ForkSamples);
			for (var i = 0; i < ForkSamples; i++)
			{
				var stopwatch = Stopwatch.StartNew();
				var sample = parent.Fork();
				stopwatch.Stop();

				forkSamples.Add(stopwatch.Elapsed.TotalMilliseconds);
				sample.Dispose();
			}

			var tickSamples = MeasureRolloutTicks(parent);

			// The timings are only meaningful if what they measured is still a faithful fork, so re-prove
			// the rung-5 oracle once here (WorldForkTest guards it on every run).
			AssertForkMatchesParent(parent);

			var createMedian = Median(createSamples);
			var forkMedian = Median(forkSamples);
			var tickMedian = Median(tickSamples);
			var forkedState = parent.LastForkClonedObjectCount;

			Report(createSamples, forkSamples, tickSamples, createMedian, forkMedian, tickMedian, forkedState);

			Assert.That(forkedState, Is.GreaterThan(0),
				"The fork cloned nothing, so the benchmark measured a clone of nothing.");

			Assert.That(forkMedian, Is.LessThan(createMedian / 2),
				$"A fork (median {forkMedian:F1} ms) is no longer dramatically cheaper than constructing a " +
				$"world from scratch (median {createMedian:F1} ms). Forking must stay the cheap way to reach " +
				"a state for MCTS rollouts; if this trips, the cloner needs optimizing (compiled per-type " +
				"copiers) or Create has become cheaper for a reason worth understanding.");
		}

		/// <summary>
		/// Retained heap cost of one entry in the rolling rewind window (<see cref="WorldForks"/>): capture
		/// several snapshots through the real capture path while the parent world is alive, force full
		/// collections on both sides of the captures, and divide the heap delta by the sample count. The
		/// assets a snapshot aliases (map, rules, graphics) were already retained before the first capture,
		/// so the delta is exactly what one window entry costs - the number that decides whether a window
		/// of <see cref="WorldForks.Capacity"/> fits in memory.
		/// <para>
		/// Like the throughput benchmark this reports rather than guards, and it is <c>[Explicit]</c> for
		/// the same reason. Run it deliberately:
		/// <code>dotnet test OpenRA.Test.Game --filter "FullyQualifiedName~RollingWindowMemoryFootprint"</code>
		/// </para>
		/// </summary>
		[Test]
		[Explicit("Memory benchmark: reports the retained bytes per forked snapshot instead of guarding " +
			"them, and forces full GCs while holding a window's worth of forks. Run deliberately with: " +
			"dotnet test OpenRA.Test.Game --filter \"FullyQualifiedName~RollingWindowMemoryFootprint\"")]
		public void RollingWindowMemoryFootprint()
		{
			const int Samples = 8;

			// Warm the cloner's reflection paths, the JIT and the file caches without measuring anything.
			using (var warm = HeadlessWorld.Create(Seed))
			{
				SetUpCombat(warm);
				Assert.That(WorldForks.Capture(warm.OrderManager), Is.True, "The warm-up capture was refused.");
			}

			WorldForks.Clear();

			using var parent = HeadlessWorld.Create(Seed);
			SetUpCombat(parent);

			try
			{
				GC.Collect();
				GC.WaitForPendingFinalizers();
				var before = GC.GetTotalMemory(true);

				// Each capture must be at least MinGapMs of game time after the last, or the write rule dedupes it
				// away and the figure would measure one snapshot divided by Samples. Ticking between captures
				// also grows the parent's own state inside the measured span; that is accepted as a small bias.
				var gapTicks = (int)Math.Ceiling(WorldForks.MinGapMs / (double)parent.World.Timestep);
				for (var i = 0; i < Samples; i++)
				{
					if (i > 0)
						parent.Tick(gapTicks);

					Assert.That(WorldForks.Capture(parent.OrderManager), Is.True,
						$"Capture {i} was refused.");
				}

				Assert.That(WorldForks.Count, Is.EqualTo(Samples),
					"The window deduped some captures, so the per-snapshot figure would be wrong.");

				GC.Collect();
				GC.WaitForPendingFinalizers();
				var after = GC.GetTotalMemory(true);

				// The object count comes from the headless fork path over the same state; it sizes the
				// graph whose retained bytes were just measured.
				using (parent.Fork()) { }
				var objects = parent.LastForkClonedObjectCount;

				var perFork = (after - before) / Samples;
				var window = perFork * WorldForks.Capacity;

				TestContext.Progress.WriteLine(string.Empty);
				TestContext.Progress.WriteLine($"== Rolling rewind window memory (scenario at tick {TicksBeforeFork}) ==");
				TestContext.Progress.WriteLine($"  per snapshot:      {perFork:N0} bytes ({(double)perFork / objects:F0} bytes/object, {objects:N} objects)");
				TestContext.Progress.WriteLine($"  window of {WorldForks.Capacity,2}:       {window:N0} bytes ({window / (1024.0 * 1024.0):F0} MiB)");
				var scaledLow = 2 * window / (1024.0 * 1024.0);
				var scaledHigh = 4 * window / (1024.0 * 1024.0);
				TestContext.Progress.WriteLine($"  x2-x4 map scale:   {scaledLow:F0}-{scaledHigh:F0} MiB " +
					"(a full skirmish map clones more state)");

				Assert.That(perFork, Is.GreaterThan(0),
					"The measured heap delta was not positive, so the benchmark captured nothing.");
				Assert.That(objects, Is.GreaterThan(0),
					"The fork cloned nothing, so the per-object figures would divide by zero.");
			}
			finally
			{
				WorldForks.Clear();
			}
		}

		/// <summary>
		/// Drives a world to the benchmark's fork point: the two-e1 fight advanced
		/// <see cref="TicksBeforeFork"/> ticks, i.e. live fire with the synced RNG mid-consumption -
		/// exactly the state <see cref="WorldForkTest"/> forks from.
		/// </summary>
		static void SetUpCombat(HeadlessWorld w)
		{
			Assert.That(w.EnemyPlayer, Is.Not.Null,
				"The selected map has only one playable slot, so there is no hostile player to fight.");

			w.Tick(SettleTicks);

			var (mineCell, _) = w.FindClearRun(w.LocalPlayer.HomeLocation, Separation);
			var theirsCell = mineCell + new CVec(Separation, 0);

			w.CreateActor("e1", mineCell);
			w.CreateActor("e1", theirsCell, w.EnemyPlayer);

			w.Tick(TicksBeforeFork);
		}

		/// <summary>
		/// Times the full cost of being at the fork state with nothing pre-built: construct a world,
		/// settle, place the combatants, and advance to the fork point.
		/// </summary>
		static List<double> MeasureCreate()
		{
			var samples = new List<double>(CreateSamples);
			for (var i = 0; i < CreateSamples; i++)
			{
				var stopwatch = Stopwatch.StartNew();
				using var w = HeadlessWorld.Create(Seed);
				SetUpCombat(w);
				stopwatch.Stop();
				samples.Add(stopwatch.Elapsed.TotalMilliseconds);
			}

			return samples;
		}

		/// <summary>
		/// Times a rollout from the parent's current state: fork (not timed here - the fork loop in the
		/// test measures that), then tick the fork <see cref="RolloutTicks"/> ticks, reported per tick.
		/// No <see cref="World.SyncHash"/> is computed while timing: hashing is the oracle's instrument,
		/// not something a rollout pays for.
		/// </summary>
		static List<double> MeasureRolloutTicks(HeadlessWorld parent)
		{
			var samples = new List<double>(TickSamples);
			for (var i = 0; i < TickSamples; i++)
			{
				using var fork = parent.Fork();
				var stopwatch = Stopwatch.StartNew();
				fork.Tick(RolloutTicks);
				stopwatch.Stop();
				samples.Add(stopwatch.Elapsed.TotalMilliseconds / RolloutTicks);
			}

			return samples;
		}

		/// <summary>
		/// The rung-5 oracle applied once to the benchmark's own parent: a fork ticked
		/// <see cref="RolloutTicks"/> ticks must match the parent ticked the same distance, hash for hash.
		/// </summary>
		static void AssertForkMatchesParent(HeadlessWorld parent)
		{
			using var fork = parent.Fork();

			var parentHashes = parent.TickCollectingHashes(RolloutTicks);
			var forkHashes = fork.TickCollectingHashes(RolloutTicks);

			Assert.That(forkHashes, Is.EqualTo(parentHashes),
				"The fork this benchmark measured diverged from its parent, so its timings describe an " +
				"unfaithful copy. WorldForkTest guards this invariant on every run.");
		}

		/// <summary>Prints the measurements and the ratios that answer the benchmark's question.</summary>
		static void Report(List<double> createSamples, List<double> forkSamples, List<double> tickSamples,
			double create, double fork, double tick, int forkedState)
		{
			var forkRollout = fork + RolloutTicks * tick;
			var createRollout = create + RolloutTicks * tick;

			TestContext.Progress.WriteLine(string.Empty);
			TestContext.Progress.WriteLine($"== Fork throughput (combat scenario, fork at tick {TicksBeforeFork}) ==");
			WriteSamples("create to fork state", CreateSamples, createSamples, create);
			WriteSamples("fork", ForkSamples, forkSamples, fork);
			WriteSamples("rollout tick", TickSamples, tickSamples, tick);
			TestContext.Progress.WriteLine($"  forked state: {forkedState:D} objects ({fork * 1e6 / forkedState:F0} ns/object)");
			TestContext.Progress.WriteLine("  --");
			TestContext.Progress.WriteLine($"  a fork is {create / fork:F0}x cheaper than constructing a world");
			TestContext.Progress.WriteLine($"  a fork costs the equivalent of {fork / tick:F1} rollout ticks");
			TestContext.Progress.WriteLine($"  {RolloutTicks}-tick rollout: fork-based {forkRollout:F1} ms vs create-based {createRollout:F1} ms");
			TestContext.Progress.WriteLine($"  fork overhead is {100 * fork / forkRollout:F1}% of a fork-based rollout");
		}

		static void WriteSamples(string label, int count, List<double> samples, double median)
		{
			TestContext.Progress.WriteLine(
				$"  {label,-22} ({count,3} samples): median {median,10:F3} ms  (min {samples.Min(),10:F3} / max {samples.Max(),10:F3})");
		}

		/// <summary>Median of the samples; the distributions are right-skewed by GC pauses.</summary>
		static double Median(List<double> samples)
		{
			var sorted = samples.Order().ToList();
			var middle = sorted.Count / 2;
			return sorted.Count % 2 == 0
				? (sorted[middle - 1] + sorted[middle]) / 2
				: sorted[middle];
		}
	}
}
