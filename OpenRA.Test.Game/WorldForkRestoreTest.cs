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

using System.Linq;
using System.Reflection;
using NUnit.Framework;
using OpenRA.Graphics;
using OpenRA.Network;
using OpenRA.Traits;

namespace OpenRA.Test
{
	/// <summary>
	/// The rolling rewind window (<see cref="WorldForks"/>) driven against a live headless world: capture
	/// a snapshot through the same engine code path the game loop uses, advance the world so its state has
	/// really moved, then restore and prove the adopted world is *exactly* the captured state - its
	/// <see cref="World.SyncHash"/> must equal the capture point's - while the outgoing world is left
	/// untouched and the shared order pipeline keeps ticking the restored world forward.
	/// <para>
	/// The engine's share policy differs from <see cref="HeadlessWorld.Fork"/>'s in one deliberate way
	/// (the live OrderManager is aliased into the fork rather than cloned), so this exercises the restore
	/// contract the real game depends on: a snapshot rejoins its own order pipeline.
	/// </para>
	/// </summary>
	[TestFixture]
	sealed class WorldForkRestoreTest
	{
		const int Seed = 0xC0FFEE;
		const int Separation = 2;
		const int SettleTicks = 5;
		const int TicksBeforeCapture = 20;
		const int TicksAfterCapture = 30;
		const int TicksAfterRestore = 20;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		/// <summary>The window is process-global; isolate every test from whatever a previous one left.</summary>
		[SetUp]
		public void ResetWindow()
		{
			WorldForks.Clear(0);
		}

		[TearDown]
		public void ClearWindow()
		{
			WorldForks.Clear(0);
		}

		static void SetUpCombat(HeadlessWorld w)
		{
			Assert.That(w.EnemyPlayer, Is.Not.Null,
				"The selected map has only one playable slot, so there is no hostile player to fight.");

			w.Tick(SettleTicks);

			var (mineCell, _) = w.FindClearRun(w.LocalPlayer.HomeLocation, Separation);
			var theirsCell = mineCell + new CVec(Separation, 0);

			w.CreateActor("e1", mineCell);
			w.CreateActor("e1", theirsCell, w.EnemyPlayer);
		}

		[TestCase(TestName = "Restoring a captured fork reproduces the capture state exactly and leaves the outgoing world untouched")]
		public void RestoreReproducesCaptureState()
		{
			using var parent = HeadlessWorld.Create(Seed);
			SetUpCombat(parent);
			parent.Tick(TicksBeforeCapture);

			var orderManager = parent.OrderManager;
			Assert.That(WorldForks.IsEligible(orderManager, out var ineligibleReason), Is.True,
				"A live, unpaused, single-human headless game must be eligible for capture: {0}", ineligibleReason);

			var capturePointHash = parent.World.SyncHash();
			var capturePointTick = parent.World.WorldTick;

			Assert.That(WorldForks.Capture(orderManager, 0), Is.True, "The capture was refused.");
			Assert.That(WorldForks.Count, Is.EqualTo(1), "The captured snapshot was not held in the window.");

			// Let the battle move on, so restoring is observable and not a no-op.
			parent.Tick(TicksAfterCapture);
			Assert.That(parent.World.SyncHash(), Is.Not.EqualTo(capturePointHash),
				"Combat did not advance the parent's state, so the restore below would prove nothing.");
			Assert.That(parent.World.WorldTick, Is.EqualTo(capturePointTick + TicksAfterCapture),
				"The parent did not tick as far as expected.");
			var outgoingHash = parent.World.SyncHash();

			Assert.That(WorldForks.TryRestore(orderManager, null, 1, out var restoreFailure), Is.True, restoreFailure);

			var restored = orderManager.World;
			Assert.That(restored, Is.Not.SameAs(parent.World),
				"Restore must adopt the cloned fork, not the outgoing world.");
			Assert.That(restored.WorldTick, Is.EqualTo(capturePointTick),
				"The restored world is not at the capture tick.");
			Assert.That(restored.SyncHash(), Is.EqualTo(capturePointHash),
				"The restored world's SyncHash differs from the capture point, so the swap did not reproduce " +
				"the captured state exactly.");
			Assert.That(parent.World.SyncHash(), Is.EqualTo(outgoingHash),
				"Restoring mutated the outgoing world; the swap must only re-point owners.");

			// The restored world now lives on the shared order pipeline and must keep ticking through it.
			var advanced = TickThroughOrderManager(orderManager, TicksAfterRestore);
			Assert.That(advanced, Is.EqualTo(TicksAfterRestore),
				"The shared order pipeline stalled after the restore.");
			Assert.That(orderManager.World, Is.SameAs(restored), "The restored world was replaced again.");
			Assert.That(restored.WorldTick, Is.EqualTo(capturePointTick + TicksAfterRestore),
				"The restored world did not advance from the capture tick.");
			Assert.That(restored.SyncHash(), Is.Not.EqualTo(capturePointHash),
				"The restored world's combat did not evolve, so the ticks above did not simulate.");

			// Restoring pops the snapshot: a second restore finds the window empty.
			Assert.That(WorldForks.Count, Is.Zero, "Restore did not pop the snapshot.");
			Assert.That(WorldForks.TryRestore(orderManager, null, 2, out var emptyReason), Is.False);
			Assert.That(emptyReason, Is.Not.Empty);
		}

		[TestCase(TestName = "A restored infantry animation draws the restored actor's facing, not the outgoing actor's")]
		public void RestoredAnimationFollowsRestoredFacing()
		{
			using var parent = HeadlessWorld.Create(Seed);
			SetUpCombat(parent);
			parent.Tick(TicksBeforeCapture);

			var orderManager = parent.OrderManager;
			var outgoingActor = parent.World.Actors.Single(a => a.Info.Name == "e1" && a.Owner == parent.LocalPlayer);
			var actorId = outgoingActor.ActorID;
			var capturedFacing = outgoingActor.TraitOrDefault<IFacing>().Facing;

			Assert.That(WorldForks.Capture(orderManager, 0), Is.True, "The capture was refused.");

			// Turn the outgoing actor away after the capture: the fork's copy must keep the captured facing.
			var outgoingFacing = new WAngle((capturedFacing.Angle + 512) % 1024);
			outgoingActor.TraitOrDefault<IFacing>().Facing = outgoingFacing;
			var outgoingAnimation = DefaultAnimationOf(outgoingActor);

			Assert.That(WorldForks.TryRestore(orderManager, null, 1, out var restoreFailure), Is.True, restoreFailure);

			var restoredActor = orderManager.World.Actors.Single(a => a.ActorID == actorId);
			Assert.That(restoredActor.TraitOrDefault<IFacing>().Facing, Is.EqualTo(capturedFacing),
				"The restored actor did not come back facing its captured direction.");

			var restoredAnimation = DefaultAnimationOf(restoredActor);
			Assert.That(restoredAnimation, Is.Not.SameAs(outgoingAnimation),
				"The restored actor's animation is the outgoing world's object, so its facing still reads the outgoing actor.");

			var expected = restoredAnimation.CurrentSequence.GetSprite(restoredAnimation.CurrentFrame, capturedFacing);
			Assert.That(restoredAnimation.Image, Is.SameAs(expected),
				"The restored animation drew a facing other than the restored actor's.");
		}

		/// <summary>
		/// Renderers whose drawn layers are shared with the outgoing world while their state is cloned at capture.
		/// Headlessly they build no layers, so the pixels cannot be checked here; this proves the restore reaches
		/// each one's <see cref="IWorldAdopted"/> hook, which is where they mark their cells for redraw.
		/// </summary>
		static readonly string[] SharedLayerRenderers = ["ShroudRenderer", "ResourceRenderer", "SmudgeLayer"];

		[TestCase(TestName = "A restored world's shared-layer renderers are told to adopt it")]
		public void SharedLayerRenderersAdoptRestoredWorld()
		{
			using var parent = HeadlessWorld.Create(Seed);
			SetUpCombat(parent);
			parent.Tick(TicksBeforeCapture);

			Assert.That(WorldForks.Capture(parent.OrderManager, 0), Is.True, "The capture was refused.");
			Assert.That(WorldForks.TryRestore(parent.OrderManager, null, 1, out var restoreFailure), Is.True, restoreFailure);

			var adopting = parent.OrderManager.World.WorldActor.TraitsImplementing<IWorldAdopted>()
				.Select(t => t.GetType().Name)
				.ToList();
			Assert.That(adopting, Is.SupersetOf(SharedLayerRenderers),
				"A renderer that shares its layer with the outgoing world does not adopt the restored world, so it keeps " +
				"showing the outgoing world's last redraw.");
		}

		/// <summary>
		/// The default body animation of an infantry actor. WithInfantryBody lives in OpenRA.Mods.Common, which
		/// this project cannot reference, so it is found by name and its protected field is read by reflection.
		/// </summary>
		static Animation DefaultAnimationOf(Actor actor)
		{
			var trait = actor.TraitsImplementing<ITick>().First(t => t.GetType().Name == "WithInfantryBody");
			return (Animation)trait.GetType().GetField("DefaultAnimation", BindingFlags.Instance | BindingFlags.NonPublic)
				.GetValue(trait);
		}

		[TestCase(TestName = "The capture timer fires once per interval and restarts after a restore")]
		public void CaptureTimerGate()
		{
			using var world = HeadlessWorld.Create(0xA11CE);
			world.Tick(5);
			var orderManager = world.OrderManager;

			WorldForks.Clear(0);
			WorldForks.MaybeSnapshot(orderManager, 0);
			Assert.That(WorldForks.Count, Is.Zero, "A snapshot was captured before the first interval elapsed.");

			WorldForks.MaybeSnapshot(orderManager, WorldForks.CaptureIntervalMs - 1);
			Assert.That(WorldForks.Count, Is.Zero, "A snapshot was captured before the interval came due.");

			WorldForks.MaybeSnapshot(orderManager, WorldForks.CaptureIntervalMs);
			Assert.That(WorldForks.Count, Is.EqualTo(1), "No snapshot was captured when the interval came due.");

			// Restoring restarts the timer: nothing may be captured until a full interval after the restore.
			const long RestoreAt = WorldForks.CaptureIntervalMs + 5;
			Assert.That(WorldForks.TryRestore(orderManager, null, RestoreAt, out var restoreFailure), Is.True,
				restoreFailure);

			WorldForks.MaybeSnapshot(orderManager, RestoreAt + 1);
			Assert.That(WorldForks.Count, Is.Zero, "The restore did not restart the capture timer.");

			WorldForks.MaybeSnapshot(orderManager, RestoreAt + WorldForks.CaptureIntervalMs);
			Assert.That(WorldForks.Count, Is.EqualTo(1), "The timer did not fire one interval after the restore.");
		}

		/// <summary>
		/// The canonical tick loop body against an order manager, mirroring
		/// <see cref="HeadlessWorld.TickOnce"/> without the wrapper - the wrapper caches the world it was
		/// built around, and this test deliberately replaces that world mid-run.
		/// </summary>
		static int TickThroughOrderManager(OrderManager orderManager, int ticks)
		{
			var advanced = 0;
			for (var i = 0; i < ticks; i++)
			{
				orderManager.TickImmediate();
				if (!orderManager.TryTick())
					continue;

				orderManager.World.Tick();
				advanced++;
			}

			return advanced;
		}
	}
}
