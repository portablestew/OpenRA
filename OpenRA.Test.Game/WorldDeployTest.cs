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
	/// Exercises the deploy transform - the "Category C" sim-feedback cycle that motivated last session's
	/// headless-content work. Deploying an <c>mcv</c> issues a <c>DeployTransform</c> order; the
	/// <c>Transform</c> activity plays the unit's make-animation <i>in reverse</i> and only on that
	/// animation's completion callback disposes the mcv and spawns a <c>fact</c> in its place.
	/// <para>
	/// That callback is the crux: the completion of a presentation-layer animation drives a
	/// simulation-affecting state change. If animations did not tick faithfully in a headless world - the
	/// bug fixed by resolving sprite content on the CPU and ticking <c>RenderSprites</c> unconditionally -
	/// the callback would never fire and the transform would hang forever. So this test is a direct,
	/// end-to-end guard on that fix: a green result means the animation ticked, its length was known, and
	/// its completion fed back into the sim exactly as it does in the rendered game.
	/// </para>
	/// </summary>
	[TestFixture]
	sealed class WorldDeployTest
	{
		const string DeployableUnit = "mcv";
		const string DeployedInto = "fact";

		/// <summary>
		/// Upper bound on ticks for the deploy to complete. The mcv first turns to its transform facing, then
		/// plays the make-animation; both are short, so this cap only trips if the animation-completion
		/// callback never fires (the exact failure mode the headless-content fix prevents).
		/// </summary>
		const int DeployTimeoutTicks = 300;

		/// <summary>
		/// Lower bound on the number of ticks the transform must spend *after the order is resolved*.
		/// <para>
		/// This is the assertion that actually pins the headless-content fix. Completing the transform at all
		/// only proves the callback eventually fired; it does not prove the make-animation ran frame by frame.
		/// If the animation collapsed to zero length (its frame count unknown because sprites were never
		/// resolved), <c>WithMakeAnimation.Reverse</c> would invoke its completion callback almost immediately
		/// and the transform would finish within a tick or two of the turn - the sim would be subtly faster
		/// than the rendered game. A healthy deploy instead spends many ticks playing the animation. Observed
		/// completion is ~21 ticks after the order resolves (a short turn plus the make-animation); requiring
		/// well more than ten guards the "animation actually played" property without pinning the exact frame
		/// count, which is a content detail that may legitimately change.
		/// </para>
		/// </summary>
		const int MinDeployTicksAfterOrder = 10;

		/// <summary>Ticks to let the map's starting units settle before placing the mcv.</summary>
		const int SettleTicks = 5;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		sealed record DeployOutcome(Actor Mcv, Actor Result, int? ResolvedAtTick, List<int> Hashes);

		/// <summary>
		/// Spawns an mcv on a clear patch big enough to become a fact, issues the deploy order, and ticks
		/// until the mcv has been replaced. Returns the original mcv, the actor it became (via
		/// <see cref="Actor.ReplacedByActor"/>), the tick the replacement appeared, and the hash trace.
		/// The caller owns <paramref name="w"/> and must dispose it.
		/// </summary>
		static DeployOutcome RunDeploy(HeadlessWorld w, bool issueOrder = true)
		{
			w.Tick(SettleTicks);

			// The mcv deploys into a fact, and Transforms.CanDeploy() only queues the transform activity if
			// the fact's ENTIRE footprint is buildable at mcvCell + (-1,-1). The fact is 3x4 (structures.yaml),
			// so a 3-cell clear run is nowhere near enough - that is why the earlier version silently took the
			// "cannot deploy here" branch and never transformed. Clear a generous square (margin around the
			// 3x4 footprint absorbs any rules change to the fact's size) and drop the mcv in the middle so the
			// offset footprint still lands entirely on cleared ground.
			const int Clearing = 6;
			var area = w.FindClearArea(w.LocalPlayer.HomeLocation, Clearing, Clearing);

			// Centre-ish of the clearing. The fact footprint extends up-and-left from here (offset -1,-1),
			// and the whole 3x4 stays well inside the 6x6 clear block.
			var mcvCell = area + new CVec(2, 2);
			var mcv = w.CreateActor(DeployableUnit, mcvCell);

			Assert.That(mcv.Info.Name, Is.EqualTo(DeployableUnit));
			Assert.That(mcv.Owner, Is.EqualTo(w.LocalPlayer),
				"The mcv must be owned by the local client's player or ValidateOrder will drop the deploy order.");

			if (issueOrder)
				w.IssueOrder(new Order("DeployTransform", mcv, queued: false));

			var hashes = new List<int>();
			var resolvedAt = w.TickUntil(() =>
			{
				hashes.Add(w.World.SyncHash());

				// The transform disposes the mcv and links the new actor via ReplacedByActor in the same
				// frame-end task, so "mcv is dead and has a replacement" is the completion signal.
				return mcv.IsDead && mcv.ReplacedByActor != null;
			}, DeployTimeoutTicks);

			return new DeployOutcome(mcv, mcv.ReplacedByActor, resolvedAt, hashes);
		}

		[TestCase(TestName = "Deploying an mcv transforms it into a construction yard")]
		public void DeployTransformsMcvIntoFact()
		{
			using var w = HeadlessWorld.Create(0xDEB1D);
			var outcome = RunDeploy(w);

			Assert.That(outcome.ResolvedAtTick, Is.Not.Null,
				$"The mcv did not finish deploying within {DeployTimeoutTicks} ticks. If the mcv is still alive and idle, " +
				"the make-animation completion callback never fired - the exact regression the headless-content fix guards against.");

			// The order is resolved OrderLatencyTicks after the tick it is sent on, which is the tick right
			// after the SettleTicks warm-up. Everything past that point is turn + make-animation. Requiring a
			// substantial gap here is what distinguishes "the animation played frame by frame" from "the
			// completion callback fired instantly on a zero-length animation".
			const int OrderResolvedTick = SettleTicks + HeadlessWorld.OrderLatencyTicks;
			var ticksSpentTransforming = outcome.ResolvedAtTick.Value - OrderResolvedTick;
			Assert.That(ticksSpentTransforming, Is.GreaterThan(MinDeployTicksAfterOrder),
				$"The transform completed only {ticksSpentTransforming} ticks after the order resolved. That is too fast for " +
				"the make-animation to have played through its frames - it suggests the animation collapsed to zero length " +
				"(sprite content not resolved headlessly), which would make the headless sim faster than the rendered game.");

			Assert.That(outcome.Mcv.IsDead, Is.True, "The original mcv should have been disposed by the transform.");
			Assert.That(outcome.Result, Is.Not.Null, "The transform produced no replacement actor.");
			Assert.That(outcome.Result.Info.Name, Is.EqualTo(DeployedInto),
				$"The mcv transformed into '{outcome.Result.Info.Name}' instead of '{DeployedInto}'.");
			Assert.That(outcome.Result.Owner, Is.EqualTo(outcome.Mcv.Owner),
				"The construction yard should belong to the mcv's owner.");
			Assert.That(outcome.Result.IsInWorld, Is.True, "The new construction yard is not in the world.");
		}

		[TestCase(TestName = "The deploy transform plays out identically in two same-seed worlds")]
		public void DeployIsDeterministic()
		{
			DeployOutcome a, b;
			using (var wa = HeadlessWorld.Create(0x5AFE))
				a = RunDeploy(wa);

			using (var wb = HeadlessWorld.Create(0x5AFE))
				b = RunDeploy(wb);

			Assert.That(a.ResolvedAtTick, Is.Not.Null, "The first deploy never completed, so there is nothing to compare.");
			Assert.That(b.ResolvedAtTick, Is.EqualTo(a.ResolvedAtTick),
				"The deploy completed on a different tick in two same-seed worlds - the animation-driven transform timing is not deterministic.");
			Assert.That(b.Hashes, Is.EqualTo(a.Hashes),
				"Two same-seed worlds deploying an mcv produced different SyncHash sequences.");
		}
	}
}
