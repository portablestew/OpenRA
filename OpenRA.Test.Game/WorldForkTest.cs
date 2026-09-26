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

namespace OpenRA.Test
{
	/// <summary>
	/// Rung 5: forking a live, mid-tick <see cref="World"/>. This is the payload the whole headless-world
	/// effort was building toward - the ability to take one game position and spin up an independent rollout
	/// that ticks a divergent future, which is what Monte Carlo Tree Search needs.
	/// <para>
	/// The rung-4 oracle is the instrument of proof here. A faithful fork must, by definition, evolve
	/// identically to the parent it was copied from: tick the parent forward N ticks, tick the fork forward
	/// the same N, and their <see cref="World.SyncHash"/> sequences must agree tick-for-tick. If a single
	/// field of mutable simulation state were shared, aliased, or dropped, the sequences would diverge.
	/// </para>
	/// <para>
	/// The subjects are chosen to stress the parts of the graph a naive copy gets wrong: combat draws the
	/// synced RNG and spawns projectile effects, and both exercise activities and condition state in flight.
	/// A pure idle world would prove almost nothing.
	/// </para>
	/// </summary>
	[TestFixture]
	sealed class WorldForkTest
	{
		const int Separation = 2;
		const int SettleTicks = 5;

		/// <summary>Ticks to advance the parent before forking, so the fork happens from a non-trivial mid-game state.</summary>
		const int TicksBeforeFork = 20;

		/// <summary>Ticks to advance parent and fork in lockstep after the fork while comparing their hashes.</summary>
		const int TicksAfterFork = 60;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		/// <summary>
		/// Places two hostile e1s within weapon range so <c>AutoTarget</c> engages, exactly as the combat
		/// oracle does. Creation order (mine, then theirs) is fixed so ActorIDs are stable across runs.
		/// </summary>
		static (Actor Mine, Actor Theirs) SetUpCombat(HeadlessWorld w)
		{
			Assert.That(w.EnemyPlayer, Is.Not.Null,
				"The selected map has only one playable slot, so there is no hostile player to fight.");

			w.Tick(SettleTicks);

			var (mineCell, _) = w.FindClearRun(w.LocalPlayer.HomeLocation, Separation);
			var theirsCell = mineCell + new CVec(Separation, 0);

			var mine = w.CreateActor("e1", mineCell);
			var theirs = w.CreateActor("e1", theirsCell, w.EnemyPlayer);

			return (mine, theirs);
		}

		[TestCase(TestName = "A fork of an idle world continues identically to its parent")]
		public void IdleForkMatchesParent()
		{
			using var parent = HeadlessWorld.Create(0xF0F0);
			parent.Tick(TicksBeforeFork);

			using var fork = parent.Fork();

			var parentHashes = parent.TickCollectingHashes(TicksAfterFork);
			var forkHashes = fork.TickCollectingHashes(TicksAfterFork);

			Assert.That(forkHashes, Is.EqualTo(parentHashes),
				"A forked idle world diverged from its parent. The fork is not a faithful deep copy of the " +
				"world's synced state.");
		}

		[TestCase(TestName = "A fork taken mid-combat continues the battle identically to its parent")]
		public void CombatForkMatchesParent()
		{
			using var parent = HeadlessWorld.Create(0xC0FFEE);
			SetUpCombat(parent);

			// Advance into the fight so the fork is taken with an in-flight battle: units targeting, the RNG
			// already consumed, and projectiles potentially in the air.
			parent.Tick(TicksBeforeFork);

			using var fork = parent.Fork();

			var parentHashes = parent.TickCollectingHashes(TicksAfterFork);
			var forkHashes = fork.TickCollectingHashes(TicksAfterFork);

			Assert.That(forkHashes, Is.EqualTo(parentHashes),
				"A world forked mid-combat diverged from its parent. Combat draws the synced RNG and spawns " +
				"projectile effects, so a divergence means some of that state was shared, aliased, or dropped by " +
				"the fork rather than deep-copied.");
		}

		[TestCase(TestName = "A fork is independent: ticking it does not perturb the parent")]
		public void ForkIsIndependentOfParent()
		{
			using var parent = HeadlessWorld.Create(0xBEEF);
			SetUpCombat(parent);
			parent.Tick(TicksBeforeFork);

			// Snapshot the parent's hash, fork, tick ONLY the fork a long way, then confirm the parent's hash
			// is unchanged. If the fork shared any mutable state, advancing it would have mutated the parent.
			var parentHashBeforeForkTicks = parent.World.SyncHash();

			using (var fork = parent.Fork())
				fork.Tick(TicksAfterFork);

			Assert.That(parent.World.SyncHash(), Is.EqualTo(parentHashBeforeForkTicks),
				"Ticking the fork changed the parent's SyncHash, so the fork is sharing mutable state with it.");
		}

		[TestCase(TestName = "Two forks of the same parent diverge from each other only as their inputs differ")]
		public void ForksAreMutuallyIndependent()
		{
			using var parent = HeadlessWorld.Create(0x1234);
			SetUpCombat(parent);
			parent.Tick(TicksBeforeFork);

			using var forkA = parent.Fork();
			using var forkB = parent.Fork();

			// With no differing input, two forks of the same position must evolve identically - this is the
			// determinism guarantee MCTS relies on when it runs many rollouts from one node.
			var a = forkA.TickCollectingHashes(TicksAfterFork);
			var b = forkB.TickCollectingHashes(TicksAfterFork);

			Assert.That(b, Is.EqualTo(a),
				"Two forks of the same parent, ticked with no differing input, produced different SyncHash " +
				"sequences. The fork carries hidden nondeterministic state.");
		}
	}
}
