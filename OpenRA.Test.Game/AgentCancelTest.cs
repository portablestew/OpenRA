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
	/// The cancel command over the real socket line path (see AgentStateDumpTest for the request helper), with the queue read
	/// back through the production command. The POWR helpers are shared with AgentBuildTest.
	/// </summary>
	[TestFixture]
	sealed class AgentCancelTest
	{
		static readonly string[] SuccessKeys = ["ok", "cmd", "actor", "queue_type", "item", "canceled", "tick", "min_effect_tick"];

		bool savedAgentSocket;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[SetUp]
		public void EnableAgentSocket()
		{
			savedAgentSocket = Game.Settings.Debug.AgentSocket;
			Game.Settings.Debug.AgentSocket = true;
		}

		[TearDown]
		public void RestoreAgentSocket()
		{
			Game.Settings.Debug.AgentSocket = savedAgentSocket;
		}

		[TestCase(TestName = "Cancelling an in-progress item removes its queue entry")]
		public void CancelRemovesTheInProgressItem()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);

			using (var build = AgentStateDumpTest.Request("{\"cmd\":\"build\",\"item\":\"powr\"}"))
				Assert.That(build.RootElement.GetProperty("ok").GetBoolean(), Is.True);

			Assert.That(hw.TickUntil(AgentBuildTest.PowrBuilding, 100), Is.Not.Null, "The POWR never started building.");

			using (var response = AgentStateDumpTest.Request("{\"cmd\":\"cancel\",\"item\":\"powr\"}"))
			{
				var root = response.RootElement;
				Assert.That(AgentProductionTest.Keys(root), Is.EquivalentTo(SuccessKeys));
				Assert.That(root.GetProperty("ok").GetBoolean(), Is.True);
				Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("cancel"));
				Assert.That(root.GetProperty("actor").GetUInt32(), Is.EqualTo(hw.LocalPlayer.PlayerActor.ActorID), "The Building queue sits on the player actor");
				Assert.That(root.GetProperty("queue_type").GetString(), Is.EqualTo("Building"));
				Assert.That(root.GetProperty("item").GetString(), Is.EqualTo("powr"));
				Assert.That(root.GetProperty("canceled").GetInt32(), Is.EqualTo(1));
			}

			hw.Tick(5);

			using var production = AgentStateDumpTest.Request("{\"cmd\":\"production\"}");
			Assert.That(AgentBuildTest.PowrItems(production.RootElement), Is.Empty, "The canceled POWR is gone from the queue");
		}

		[TestCase(TestName = "Cancelling an item that is not queued fails loudly, with the reason")]
		public void CancelWithNothingQueuedIsRefused()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.Request("{\"cmd\":\"cancel\",\"item\":\"powr\"}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("cancel"));
			Assert.That(root.GetProperty("error").GetString(), Is.EqualTo("nothing to cancel: 'powr' is not queued in any queue"));
			Assert.That(root.TryGetProperty("hint", out _), Is.True, "A refusal carries the hint");
		}
	}
}
