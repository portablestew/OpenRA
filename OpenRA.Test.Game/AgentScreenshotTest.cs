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
	/// The screenshot command over the real socket line path (see AgentStateDumpTest for the request helper). The test host has
	/// no renderer, so a request that gets past the unit checks is refused with the no-renderer error and writes no file. The
	/// centring, the PNG, and the timeout cancel are checked in a live run, not here.
	/// </summary>
	[TestFixture]
	sealed class AgentScreenshotTest
	{
		const string NoRenderer = "no renderer: the game has no window to capture";

		static readonly string[] ErrorKeys = ["ok", "cmd", "error"];

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

		static void AssertRefused(JsonElement root, string error)
		{
			Assert.That(root.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(ErrorKeys));
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("screenshot"));
			Assert.That(root.GetProperty("error").GetString(), Is.EqualTo(error));
		}

		[TestCase(TestName = "A screenshot with no actor is refused for the missing renderer, not for its request")]
		public void AbsentActorReachesTheRendererCheck()
		{
			using var hw = HeadlessWorld.Create(1);

			using var response = AgentStateDumpTest.Request("{\"cmd\":\"screenshot\"}");
			AssertRefused(response.RootElement, NoRenderer);
		}

		[TestCase(TestName = "An explicit null actor means no unit, so it is accepted and refused for the missing renderer")]
		public void NullActorMeansNoUnit()
		{
			using var hw = HeadlessWorld.Create(1);

			using var response = AgentStateDumpTest.Request("{\"cmd\":\"screenshot\",\"actor\":null}");
			AssertRefused(response.RootElement, NoRenderer);
		}

		[TestCase(TestName = "A live unit id is parsed and resolved, then refused for the missing renderer")]
		public void LiveUnitIdReachesTheRendererCheck()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.Request($"{{\"cmd\":\"screenshot\",\"actor\":{unit.ActorID}}}");
			AssertRefused(response.RootElement, NoRenderer);
		}

		[TestCase(TestName = "An enemy unit id is resolved like any live unit, so it is also refused only for the missing renderer")]
		public void EnemyUnitIdReachesTheRendererCheck()
		{
			using var hw = HeadlessWorld.Create(1);
			var enemy = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.EnemyPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.Request($"{{\"cmd\":\"screenshot\",\"actor\":{enemy.ActorID}}}");
			AssertRefused(response.RootElement, NoRenderer);
		}

		[TestCase(TestName = "An unknown unit id is refused with the unknown-actor error")]
		public void UnknownUnitIdIsAnError()
		{
			using var hw = HeadlessWorld.Create(1);

			using var response = AgentStateDumpTest.Request("{\"cmd\":\"screenshot\",\"actor\":999999}");
			AssertRefused(response.RootElement, "Unknown actor 999999");
		}

		[TestCase(TestName = "A dead unit id is refused with the dead-actor error, distinct from unknown")]
		public void DeadUnitIdIsAnError()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			unit.Kill(unit);
			Assert.That(unit.IsDead, Is.True);

			using var response = AgentStateDumpTest.Request($"{{\"cmd\":\"screenshot\",\"actor\":{unit.ActorID}}}");
			AssertRefused(response.RootElement, $"Actor {unit.ActorID} is dead");
		}
	}
}
