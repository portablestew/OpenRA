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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The screenshot command over the real socket line path (see AgentStateDumpTest for the request helper). The test host has
	/// no renderer, so a request that gets past the unit checks is refused with the no-renderer error and writes no file. The
	/// centring and the PNG are checked in a live run, not here.
	/// The capture decision is tested directly: Game.CaptureArmedScreenshots is the step RenderTick runs after drawing, and the
	/// file writer is replaced, so an abandoned request is shown to reach no writer.
	/// </summary>
	[TestFixture]
	sealed class AgentScreenshotTest
	{
		const string NoRenderer = "no renderer: the game has no window to capture";

		const string FakePath = "fake/ra-test.png";

		static readonly string[] ErrorKeys = ["ok", "cmd", "error"];

		bool savedAgentSocket;

		// Each batch of requests the capture step hands to the writer. The writer is replaced, so nothing is written to disk.
		List<IReadOnlyList<TaskCompletionSource<string>>> captures;

		Action<IReadOnlyList<TaskCompletionSource<string>>> savedWriter;

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

		[SetUp]
		public void ReplaceWriter()
		{
			captures = [];
			savedWriter = Game.ScreenshotWriter;

			// Stands in for the file writer: records the batch and completes each request as a successful write would.
			Game.ScreenshotWriter = requests =>
			{
				captures.Add(requests);
				foreach (var request in requests)
					request.TrySetResult(FakePath);
			};
		}

		[TearDown]
		public void RestoreWriter()
		{
			Game.ScreenshotWriter = savedWriter;
		}

		// The wait the socket thread does in AgentScreenshot.Await, repeated here because this project cannot reference the mod.
		static string AwaitPath(Task<string> written, int timeoutMs)
		{
			if (Task.WaitAny([written], timeoutMs) == -1)
				throw new TimeoutException($"the frame was not written within {timeoutMs} ms");

			return written.GetAwaiter().GetResult();
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

		[TestCase(TestName = "An abandoned screenshot that times out writes no file when the next frame renders")]
		public void AbandonedScreenshotWritesNoFile()
		{
			using var abandon = new CancellationTokenSource();
			var written = Game.RequestScreenshot(abandon.Token);

			// The socket handler's timeout path: the wait gives up, then the catch block cancels the request.
			Assert.That(() => AwaitPath(written, timeoutMs: 50), Throws.TypeOf<TimeoutException>());
			abandon.Cancel();

			Game.CaptureArmedScreenshots();

			Assert.That(captures, Is.Empty, "the writer must not run for an abandoned request");
			Assert.That(written.IsCanceled, Is.True);

			// Nothing is left pending: a later frame with no hotkey and no request captures nothing.
			Game.CaptureArmedScreenshots();
			Assert.That(captures, Is.Empty, "a cancelled request must not stay armed");
		}

		[TestCase(TestName = "A screenshot that is not cancelled is written by the next frame, and its reply carries the path")]
		public void LiveScreenshotIsWritten()
		{
			using var abandon = new CancellationTokenSource();
			var written = Game.RequestScreenshot(abandon.Token);

			Game.CaptureArmedScreenshots();

			// The companion case: the same steps without the cancel must reach the writer, so the test above is not vacuous.
			Assert.That(captures, Has.Count.EqualTo(1));
			Assert.That(captures[0].Count, Is.EqualTo(1));

			Assert.That(AwaitPath(written, timeoutMs: 1000), Is.EqualTo(FakePath));

			Game.CaptureArmedScreenshots();
			Assert.That(captures, Has.Count.EqualTo(1), "a written request must not stay armed");
		}
	}
}
