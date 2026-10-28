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
using System.Reflection;
using NUnit.Framework;
using OpenRA.Network;

namespace OpenRA.Test
{
	/// <summary>
	/// The rollout fork primitive (<see cref="WorldForks.ForkForRollout"/>) and the sinks that drop a rollout's output.
	/// A fork shares the live <see cref="OrderManager"/>, so these tests check that a rollout cannot reach it.
	/// </summary>
	[TestFixture]
	sealed class WorldForkRolloutTest
	{
		const int Seed = 0x4F0A;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

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

		/// <summary>The parent's buffered, not yet sent, orders. Read by reflection: the buffer is private and has no count.</summary>
		static int PendingLocalOrders(OrderManager om)
		{
			var field = typeof(OrderManager).GetField("localOrders", BindingFlags.Instance | BindingFlags.NonPublic);
			return ((List<Order>)field.GetValue(om)).Count;
		}

		[TestCase(TestName = "ForkForRollout leaves the snapshot window unchanged")]
		public void ForkForRolloutLeavesWindowUnchanged()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(5);
			Assert.That(WorldForks.Capture(hw.OrderManager), Is.True, "The seed capture was refused.");

			var before = WorldForks.Count;
			var fork = WorldForks.ForkForRollout(hw.OrderManager);

			Assert.That(fork, Is.Not.Null, "The rollout fork was refused.");
			Assert.That(WorldForks.Count, Is.EqualTo(before), "A rollout fork was stored in the window.");
		}

		[TestCase(TestName = "IssueOrder on a rollout fork drops the order and leaves the parent's pending orders unchanged")]
		public void IssueOrderOnRolloutForkDropsOrder()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(5);
			var om = hw.OrderManager;
			var fork = WorldForks.ForkForRollout(om);
			Assert.That(fork, Is.Not.Null, "The rollout fork was refused.");

			var pendingBefore = PendingLocalOrders(om);
			fork.IssueOrder(new Order("Stop", null, false));

			Assert.That(PendingLocalOrders(om), Is.EqualTo(pendingBefore), "A rollout order reached the parent OrderManager.");
			Assert.That(fork.RolloutOrdersDropped, Is.EqualTo(1), "The dropped order was not counted.");
		}

		[TestCase(TestName = "IssueOrder on the live world still reaches the OrderManager")]
		public void IssueOrderOnLiveWorldReachesOrderManager()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(5);
			var om = hw.OrderManager;

			var pendingBefore = PendingLocalOrders(om);
			hw.World.IssueOrder(new Order("Stop", null, false));

			Assert.That(PendingLocalOrders(om), Is.EqualTo(pendingBefore + 1), "The live order did not reach the OrderManager.");
			Assert.That(hw.World.RolloutOrdersDropped, Is.Zero, "The live world counted a dropped order.");
		}

		[TestCase(TestName = "IsRollout is false on a Capture snapshot and on the live world, and true on a rollout fork")]
		public void IsRolloutOnlyOnRolloutForks()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(5);
			Assert.That(WorldForks.Capture(hw.OrderManager), Is.True, "The capture was refused.");

			var snapshot = WorldForks.FindNearest(long.MaxValue);
			Assert.That(snapshot, Is.Not.Null, "The capture was not stored.");

			Assert.That(snapshot.World.IsRollout, Is.False, "A Capture snapshot is marked as a rollout.");
			Assert.That(hw.World.IsRollout, Is.False, "The live world is marked as a rollout.");

			var fork = WorldForks.ForkForRollout(hw.OrderManager);
			Assert.That(fork.IsRollout, Is.True, "The rollout fork is not marked as a rollout.");
		}

		[TestCase(TestName = "A rollout scope drops text lines that name no player")]
		public void RolloutScopeDropsTextLines()
		{
			var before = TextNotificationsManager.Notifications.Count;

			WorldForks.EnterRolloutScope();
			try
			{
				TextNotificationsManager.AddSystemLine("Rollout", "dropped");
			}
			finally
			{
				WorldForks.ExitRolloutScope();
			}

			Assert.That(TextNotificationsManager.Notifications.Count, Is.EqualTo(before), "A rollout line was shown.");
			Assert.That(WorldForks.InRolloutScope, Is.False, "The rollout scope was not closed.");
		}
	}
}
