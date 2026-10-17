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
using OpenRA.Network;

namespace OpenRA.Test
{
	/// <summary>
	/// Drives the agent socket's undo command against a headless world with a real snapshot window: seed two
	/// checkpoints, step with -/+ offsets and address one absolutely with @, and check both the reported ticks
	/// and that the window is never consumed. The wire path and the game-thread marshalling are the live ones.
	/// </summary>
	[TestFixture]
	sealed class AgentSnapshotTest
	{
		const int Seed = 0x5A9;

		/// <summary>Seconds in one snapshot gap at the 25 ticks per second the offset is read in.</summary>
		static int GapSeconds => (int)(WorldForks.MinGapMs / 1000);

		bool savedAgentSocket;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[SetUp]
		public void EnableAgentSocketAndClearWindow()
		{
			savedAgentSocket = Game.Settings.Debug.AgentSocket;
			Game.Settings.Debug.AgentSocket = true;
			WorldForks.Clear();
		}

		[TearDown]
		public void RestoreSettings()
		{
			WorldForks.Clear();
			Game.Settings.Debug.AgentSocket = savedAgentSocket;
		}

		/// <summary>
		/// A world with two checkpoints one <see cref="WorldForks.MinGapMs"/> apart and the live clock a further
		/// gap past the second, so a one-gap rewind lands on the second and a second one on the first.
		/// </summary>
		sealed record Window(HeadlessWorld World, OrderManager OrderManager, int Gap, int First, int Second) : System.IDisposable
		{
			public void Dispose() { World.Dispose(); }
		}

		static Window SeedWindow()
		{
			var hw = HeadlessWorld.Create(Seed);
			var om = hw.OrderManager;
			hw.Tick(5);

			var gap = (int)(WorldForks.MinGapMs / hw.World.Timestep);
			var first = om.World.WorldTick;
			Assert.That(WorldForks.Capture(om), Is.True, "The first capture was refused.");
			hw.Tick(gap);
			var second = om.World.WorldTick;
			Assert.That(WorldForks.Capture(om), Is.True, "The second capture was refused.");
			hw.Tick(gap);

			return new Window(hw, om, gap, first, second);
		}

		[TestCase(TestName = "A - offset rewinds and a + offset advances, reporting the tick and holding the window")]
		public void StepByOffset()
		{
			using var window = SeedWindow();
			var om = window.OrderManager;

			var before = om.World.WorldTick;
			using (var undo = AgentStateDumpTest.Request($"{{\"cmd\":\"undo\",\"offset\":\"-{GapSeconds}\"}}"))
			{
				var root = undo.RootElement;
				Assert.That(root.GetProperty("ok").GetBoolean(), Is.True);
				Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("undo"));
				Assert.That(root.GetProperty("tick").GetInt32(), Is.EqualTo(window.Second));
				Assert.That(root.GetProperty("requested_tick").GetInt32(), Is.EqualTo(window.Second));
				Assert.That(root.GetProperty("moved_ticks").GetInt32(), Is.EqualTo(window.Second - before));
				Assert.That(root.GetProperty("held").GetInt32(), Is.EqualTo(2));
			}

			Assert.That(om.World.WorldTick, Is.EqualTo(window.Second), "The rewind did not leave the second snapshot active.");

			using (var undo = AgentStateDumpTest.Request($"{{\"cmd\":\"undo\",\"offset\":\"-{GapSeconds}\"}}"))
			{
				Assert.That(undo.RootElement.GetProperty("ok").GetBoolean(), Is.True);
				Assert.That(undo.RootElement.GetProperty("tick").GetInt32(), Is.EqualTo(window.First));
				Assert.That(undo.RootElement.GetProperty("moved_ticks").GetInt32(), Is.EqualTo(-window.Gap));
			}

			using (var redo = AgentStateDumpTest.Request($"{{\"cmd\":\"undo\",\"offset\":\"+{GapSeconds}\"}}"))
			{
				Assert.That(redo.RootElement.GetProperty("ok").GetBoolean(), Is.True);
				Assert.That(redo.RootElement.GetProperty("tick").GetInt32(), Is.EqualTo(window.Second));
				Assert.That(redo.RootElement.GetProperty("moved_ticks").GetInt32(), Is.EqualTo(window.Gap));
			}

			// The window still holds both checkpoints: a restore clones, it does not pop.
			Assert.That(WorldForks.Count, Is.EqualTo(2), "A restore consumed a snapshot.");
		}

		[TestCase(TestName = "An @ offset addresses an absolute game time, not a span from the live tick")]
		public void AbsoluteOffset()
		{
			using var window = SeedWindow();

			// From the live clock (two gaps past the first checkpoint), @0 lands on the first checkpoint: the
			// nearest stored snapshot to game time zero, which is an absolute target, not a relative step.
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"undo\",\"offset\":\"@0\"}");
			var root = response.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(root.GetProperty("tick").GetInt32(), Is.EqualTo(window.First));
			Assert.That(root.GetProperty("requested_tick").GetInt32(), Is.Zero);
		}

		[TestCase(TestName = "A missing or unprefixed offset is refused with the usage")]
		public void BadOffsetIsRefused()
		{
			foreach (var request in new[]
			{
				"{\"cmd\":\"undo\"}",
				"{\"cmd\":\"undo\",\"offset\":\"10\"}",
				"{\"cmd\":\"undo\",\"offset\":\"abc\"}",
				"{\"cmd\":\"undo\",\"offset\":\"+\"}",
				"{\"cmd\":\"undo\",\"offset\":5}",
			})
			{
				using var response = AgentStateDumpTest.Request(request);
				var root = response.RootElement;
				Assert.That(root.GetProperty("ok").GetBoolean(), Is.False, request);
				Assert.That(root.GetProperty("error").GetString(), Does.Contain("usage"), request);
			}
		}
	}
}
