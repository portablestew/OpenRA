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

using NUnit.Framework;
using OpenRA.Support;

namespace OpenRA.Test
{
	/// <summary>
	/// Forking a simulation requires forking its synced random number generator. These cover the
	/// properties a fork depends on: the copy continues the same sequence, and the two are independent.
	/// </summary>
	[TestFixture]
	sealed class MersenneTwisterForkTest
	{
		// Enough draws to cross the 624-value regeneration boundary, so that a fork is taken partway
		// through the state array rather than at a boundary.
		const int PartialSequenceLength = 700;

		[TestCase(TestName = "A fork taken before any draw reproduces the sequence")]
		public void ForkFromUnusedGeneratorReproducesSequence()
		{
			var source = new MersenneTwister(12345);
			var fork = new MersenneTwister(source);

			for (var i = 0; i < 2000; i++)
			{
				var expected = source.Next();
				var actual = fork.Next();
				Assert.That(actual, Is.EqualTo(expected), $"Sequences diverged at draw {i}.");
			}
		}

		[TestCase(TestName = "A fork taken mid-sequence reproduces the remainder")]
		public void ForkMidSequenceReproducesRemainder()
		{
			var source = new MersenneTwister(999);
			for (var i = 0; i < PartialSequenceLength; i++)
				source.Next();

			var fork = new MersenneTwister(source);

			for (var i = 0; i < 2000; i++)
			{
				var expected = source.Next();
				var actual = fork.Next();
				Assert.That(actual, Is.EqualTo(expected), $"Sequences diverged at draw {i} after the fork point.");
			}
		}

		[TestCase(TestName = "Re-seeding is not equivalent to forking mid-sequence")]
		public void ReseedingIsNotEquivalentToForking()
		{
			// Guards the copy constructor against being "simplified" into a re-seed from the original
			// seed, which would silently reset every fork to the start of the sequence.
			var source = new MersenneTwister(999);
			for (var i = 0; i < PartialSequenceLength; i++)
				source.Next();

			var reseeded = new MersenneTwister(999);

			Assert.That(reseeded.Next(), Is.Not.EqualTo(source.Next()),
				"A re-seeded generator must not be treated as a valid fork of a generator already in use.");
		}

		[TestCase(TestName = "Advancing a fork does not disturb the generator it came from")]
		public void ForkIsIndependentOfSource()
		{
			var source = new MersenneTwister(4242);
			for (var i = 0; i < PartialSequenceLength; i++)
				source.Next();

			// Two forks from the same point: one is advanced heavily, the other is left untouched as a
			// reference for what the source should still produce.
			var reference = new MersenneTwister(source);
			var fork = new MersenneTwister(source);

			for (var i = 0; i < 5000; i++)
				fork.Next();

			for (var i = 0; i < 500; i++)
			{
				var expected = reference.Next();
				var actual = source.Next();
				Assert.That(actual, Is.EqualTo(expected), $"The source was disturbed by the fork at draw {i}.");
			}
		}

		[TestCase(TestName = "A fork preserves the counters that are visible to the sync hash")]
		public void ForkPreservesObservableCounters()
		{
			var source = new MersenneTwister(7);
			for (var i = 0; i < 50; i++)
				source.Next();

			var fork = new MersenneTwister(source);

			// World.SyncHash folds in SharedRandom.Last, and sync reports record TotalCount, so a fork
			// that did not carry these across would not compare equal to the state it was taken from.
			Assert.That(fork.Last, Is.EqualTo(source.Last));
			Assert.That(fork.TotalCount, Is.EqualTo(source.TotalCount));
		}
	}
}
