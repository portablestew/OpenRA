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
using System.Linq;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// Rung 4: the first test in the repo to construct a real <see cref="World"/>. Before we can fork a
	/// world (rung 5) we need an oracle - a plain, deterministic, headless tick loop whose
	/// <see cref="World.SyncHash"/> sequence is reproducible from a fixed seed. This fixture stands up the
	/// shipped <c>ra</c> mod and a simple skirmish map with no renderer, sound device, or UI (see
	/// <see cref="HeadlessMod"/> and <see cref="HeadlessWorld"/>), ticks the world directly, and asserts
	/// that two same-seed worlds produce identical SyncHash sequences, that the hash is non-trivial and
	/// evolves (so the test cannot pass vacuously), and that different seeds diverge.
	/// </summary>
	[TestFixture]
	sealed class WorldSyncHashTest
	{
		// Number of world ticks to advance and compare. Kept small so the suite stays fast;
		// large enough that starting units settle and the hash evolves for a few frames.
		const int TickCount = 25;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		static List<int> RunHashes(int seed, int ticks)
		{
			using var w = HeadlessWorld.Create(seed);
			return w.TickCollectingHashes(ticks);
		}

		[TestCase(TestName = "A headless world advances and produces a non-trivial, evolving SyncHash")]
		public void HashIsNonTrivialAndEvolves()
		{
			var hashes = RunHashes(0x5EED, TickCount);

			Assert.That(hashes, Is.Not.Empty, "The world never advanced a frame.");
			Assert.That(hashes.Any(h => h != 0), Is.True,
				"Every SyncHash was 0 - the world has no synced state, so the reproducibility test would pass vacuously.");
			Assert.That(hashes.Distinct().Count(), Is.GreaterThan(1),
				"The SyncHash never changed across ticks - nothing in the world is evolving.");
		}

		[TestCase(TestName = "Two worlds from the same seed produce identical SyncHash sequences")]
		public void SameSeedIsReproducible()
		{
			var a = RunHashes(0x1234, TickCount);
			var b = RunHashes(0x1234, TickCount);

			Assert.That(b, Is.EqualTo(a), "Two same-seed headless worlds diverged - the simulation is not deterministic.");
		}

		[TestCase(TestName = "Two worlds from different seeds diverge")]
		public void DifferentSeedDiverges()
		{
			var a = RunHashes(0x1111, TickCount);
			var b = RunHashes(0x2222, TickCount);

			Assert.That(b, Is.Not.EqualTo(a),
				"Two different-seed worlds produced identical SyncHash sequences - the seed is not influencing the simulation.");
		}
	}
}
