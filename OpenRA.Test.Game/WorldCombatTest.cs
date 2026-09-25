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
using OpenRA.Traits;

namespace OpenRA.Test
{
	/// <summary>
	/// Exercises combat - the first order-driven behaviour whose outcome depends on the synced RNG. Two
	/// identical <c>e1</c>s owned by mutually hostile players are placed within weapon range; their
	/// <c>AutoTarget</c> traits acquire and fire without any explicit order, one is destroyed, and the test
	/// asserts on that.
	/// <para>
	/// The two assertions here are deliberately different in kind:
	/// </para>
	/// <list type="bullet">
	/// <item><description>
	/// <b>Behavioural invariant</b> - exactly one of the two dies within a bounded time. This is a claim
	/// about combat working at all, and it should survive any legitimate engine change.
	/// </description></item>
	/// <item><description>
	/// <b>Determinism canary</b> - the survivor is <i>always the same one</i>, and two same-seed worlds
	/// produce identical SyncHash sequences. With identical units the winner is decided purely by actor
	/// tick order and <c>SharedRandom</c> draw order, so a change to which one wins means RNG consumption in
	/// the damage path moved. Nothing else enforces that invariant, and the forked worlds in rung 5 will
	/// depend on it, so this canary is the point of the test as much as the behaviour is. The winner is
	/// pinned to a value observed on the first green run and must not be edited afterwards; if it starts
	/// failing, the fix is to understand what changed the RNG ordering, not to re-pin the value.
	/// </description></item>
	/// </list>
	/// </summary>
	[TestFixture]
	sealed class WorldCombatTest
	{
		/// <summary>Cells between the two combatants. Well inside the M1Carbine's 5-cell range so they engage promptly.</summary>
		const int Separation = 2;

		/// <summary>
		/// Upper bound on ticks for one e1 to kill another. Each has 5000 HP and the M1Carbine fires every
		/// 20 ticks; a kill takes well under this, so the cap only trips when combat has genuinely stalled
		/// (e.g. they never became hostile and so never fired).
		/// </summary>
		const int CombatTimeoutTicks = 500;

		/// <summary>Ticks to let the map's starting units settle before placing the combatants.</summary>
		const int SettleTicks = 5;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		/// <summary>
		/// Which of the two combatants survived, identified by the fixed label the scenario assigns at
		/// creation (not by ActorID, which is not stable to reason about from the outside).
		/// </summary>
		enum Survivor { Neither, Both, Mine, Enemy }

		sealed record CombatOutcome(Survivor Survivor, int? ResolvedAtTick, List<int> Hashes);

		/// <summary>
		/// Spawns two identical e1s two cells apart - "mine" owned by the local player, "theirs" by the
		/// hostile bot player - and ticks until one dies. The creation order (mine first, then theirs) is
		/// fixed so ActorIDs, and therefore any tie-breaking, are identical every run.
		/// </summary>
		static CombatOutcome RunCombat(int seed)
		{
			using var w = HeadlessWorld.Create(seed);

			Assert.That(w.EnemyPlayer, Is.Not.Null,
				"The selected map has only one playable slot, so there is no hostile player to fight. " +
				"SelectSimpleMap should be preferring a two-player map.");
			Assert.That(w.LocalPlayer.RelationshipWith(w.EnemyPlayer), Is.EqualTo(PlayerRelationship.Enemy),
				"The two players are not hostile, so AutoTarget will never engage. Check the Team=0 / SetupPlayerMasks reasoning.");

			w.Tick(SettleTicks);

			// A clear run gives us adjacent walkable cells with nothing in the way; put the two units at its
			// ends (Separation cells apart), both on plain terrain and in each other's line of fire.
			var (mineCell, _) = w.FindClearRun(w.LocalPlayer.HomeLocation, Separation);
			var theirsCell = mineCell + new CVec(Separation, 0);

			var mine = w.CreateActor("e1", mineCell);
			var theirs = w.CreateActor("e1", theirsCell, w.EnemyPlayer);

			Assert.That(mine.Owner, Is.EqualTo(w.LocalPlayer));
			Assert.That(theirs.Owner, Is.EqualTo(w.EnemyPlayer));

			var hashes = new List<int>();
			var resolvedAt = w.TickUntil(() =>
			{
				hashes.Add(w.World.SyncHash());
				return mine.IsDead || theirs.IsDead;
			}, CombatTimeoutTicks);

			var survivor =
				mine.IsDead && theirs.IsDead ? Survivor.Neither :
				!mine.IsDead && !theirs.IsDead ? Survivor.Both :
				mine.IsDead ? Survivor.Enemy : Survivor.Mine;

			return new CombatOutcome(survivor, resolvedAt, hashes);
		}

		[TestCase(TestName = "Two hostile e1s fight and exactly one dies")]
		public void CombatKillsExactlyOne()
		{
			var outcome = RunCombat(0xC0FFEE);

			Assert.That(outcome.ResolvedAtTick, Is.Not.Null,
				$"Neither e1 died within {CombatTimeoutTicks} ticks. Combat never resolved - most likely the units " +
				"never became hostile or never acquired a target.");

			// Behavioural invariant: exactly one survivor. "Both" means combat never dealt lethal damage;
			// "Neither" means they somehow killed each other on the same tick, which for asymmetric tick
			// order should not happen and would itself be worth investigating.
			Assert.That(outcome.Survivor, Is.AnyOf(Survivor.Mine, Survivor.Enemy),
				$"Expected exactly one e1 to survive, but the outcome was '{outcome.Survivor}'.");
		}

		[TestCase(TestName = "The same e1 always wins the identical fight (determinism canary)")]
		public void CombatWinnerIsDeterministic()
		{
			var a = RunCombat(0xC0FFEE);
			var b = RunCombat(0xC0FFEE);

			Assert.That(a.Survivor, Is.AnyOf(Survivor.Mine, Survivor.Enemy), "The first run did not resolve to a single survivor.");

			// PINNED on the first green run. Do not edit this to match a new observed value - a change here
			// means the synced RNG draw order in the damage path moved, which is exactly what this canary
			// exists to catch.
			Assert.That(a.Survivor, Is.EqualTo(Survivor.Mine),
				"The survivor of the identical fight changed. This is a determinism regression in combat RNG " +
				"ordering, not a reason to re-pin the expected winner.");

			Assert.That(b.Survivor, Is.EqualTo(a.Survivor),
				"Two same-seed worlds produced different combat winners - combat is not deterministic within a run pair.");
			Assert.That(b.ResolvedAtTick, Is.EqualTo(a.ResolvedAtTick),
				"The kill happened on a different tick in two same-seed worlds.");
			Assert.That(b.Hashes, Is.EqualTo(a.Hashes),
				"Two same-seed worlds fighting the identical battle produced different SyncHash sequences.");
		}
	}
}
