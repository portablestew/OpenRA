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
using System.Text.Json;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The orders command, and the hard reject in order for a name the actor does not accept. Requests go over the real
	/// socket line path (see AgentStateDumpTest for the request helper).
	/// </summary>
	[TestFixture]
	sealed class AgentOrderNamesTest
	{
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

		static string[] Keys(JsonElement element)
		{
			return element.EnumerateObject().Select(p => p.Name).ToArray();
		}

		static string[] Names(JsonElement root)
		{
			return root.GetProperty("orders").EnumerateArray().Select(e => e.GetString()).ToArray();
		}

		[TestCase(TestName = "The orders command lists DeployTransform for an mcv, with the documented keys")]
		public void OrdersListsDeployForAnMcv()
		{
			using var hw = HeadlessWorld.Create(1);
			var mcv = hw.CreateActor("mcv", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.Request($"{{\"cmd\":\"orders\",\"actor\":{mcv.ActorID}}}");
			var root = response.RootElement;

			Assert.That(Keys(root), Is.EquivalentTo(["ok", "cmd", "actor", "type", "orders"]));
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("orders"));
			Assert.That(root.GetProperty("actor").GetUInt32(), Is.EqualTo(mcv.ActorID));
			Assert.That(root.GetProperty("type").GetString(), Is.EqualTo(mcv.Info.Name));
			Assert.That(Names(root), Does.Contain("DeployTransform"));
			Assert.That(Names(root), Does.Contain("Move"));
			Assert.That(Names(root), Does.Contain("Stop"));
			Assert.That(Names(root), Does.Contain("Scatter"));
		}

		[TestCase(TestName = "The orders list leaves out names the actor has no trait for")]
		public void OrdersExcludesNamesTheActorLacks()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.Request($"{{\"cmd\":\"orders\",\"actor\":{unit.ActorID}}}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(Names(response.RootElement), Does.Contain("Move"));
			Assert.That(Names(response.RootElement), Does.Not.Contain("DeployTransform"));
			Assert.That(Names(response.RootElement), Does.Not.Contain("Deploy"));
		}

		[TestCase(TestName = "An order name the actor does not accept is refused, and the refusal lists the names it accepts")]
		public void UnacceptedOrderIsRefusedWithAvailable()
		{
			using var hw = HeadlessWorld.Create(1);
			var mcv = hw.CreateActor("mcv", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.Request($"{{\"cmd\":\"order\",\"actor\":{mcv.ActorID},\"order\":\"Deploy\"}}");
			var root = response.RootElement;

			Assert.That(Keys(root), Is.EquivalentTo(["ok", "cmd", "error", "available"]));
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("order"));
			Assert.That(root.GetProperty("error").GetString(), Is.EqualTo($"actor {mcv.ActorID} does not accept order 'Deploy'"));

			var available = root.GetProperty("available").EnumerateArray().Select(e => e.GetString());
			Assert.That(available, Does.Contain("DeployTransform"));
			Assert.That(available, Does.Not.Contain("Deploy"));
		}

		[TestCase(TestName = "A refused order is refused even when the request asks to allow unlisted names")]
		public void RefusalHasNoBypass()
		{
			using var hw = HeadlessWorld.Create(1);
			var mcv = hw.CreateActor("mcv", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.Request($"{{\"cmd\":\"order\",\"actor\":{mcv.ActorID},\"order\":\"Deploy\",\"allow_unlisted\":true}}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo($"actor {mcv.ActorID} does not accept order 'Deploy'"));
		}

		[TestCase(TestName = "A Move order for an mcv is accepted, and so is the DeployTransform name the orders list gives")]
		public void AcceptedOrdersStillPass()
		{
			using var hw = HeadlessWorld.Create(1);
			var mcv = hw.CreateActor("mcv", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var destination = hw.FindClearArea(mcv.Location, 1, 1, 6);

			var line = $"{{\"cmd\":\"order\",\"actor\":{mcv.ActorID},\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}]}}";
			using var move = AgentStateDumpTest.RequestWithTicks(hw, line);
			Assert.That(Keys(move.RootElement), Is.EquivalentTo(["ok", "cmd", "tick", "min_effect_tick", "valid", "effect_tick", "settled", "settled_ticks", "path"]));
			Assert.That(move.RootElement.GetProperty("ok").GetBoolean(), Is.True);

			using var deploy = AgentStateDumpTest.Request($"{{\"cmd\":\"order\",\"actor\":{mcv.ActorID},\"order\":\"DeployTransform\"}}");
			Assert.That(deploy.RootElement.GetProperty("ok").GetBoolean(), Is.True);
		}
	}
}
