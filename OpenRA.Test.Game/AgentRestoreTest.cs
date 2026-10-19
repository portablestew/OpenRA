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
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The restore rules (P1.19). An undo swaps the world, so every pending order and job fails with the restored error, and its
	/// socket thread is released rather than left to time out. An undo inside an array is refused unless it is the only or the last
	/// item, and the other items still run. Every envelope carries the tick, which moves backwards after a rewind.
	/// </summary>
	[TestFixture]
	sealed class AgentRestoreTest
	{
		const int Seed = 0x7E57;
		const string Infantry = "e1";
		const string Ping = "{\"cmd\":\"ping\"}";

		/// <summary>Ticks to let the map's starting units settle before placing the infantry.</summary>
		const int SettleTicks = 5;

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

		static int GapSeconds => (int)(WorldForks.MinGapMs / 1000);

		/// <summary>Two checkpoints one gap apart, with the live clock a further gap past the second, so a one-gap undo has a target.</summary>
		static HeadlessWorld SeedWindow()
		{
			var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);

			var gap = (int)(WorldForks.MinGapMs / hw.World.Timestep);
			Assert.That(WorldForks.Capture(hw.OrderManager), Is.True, "The first capture was refused.");
			hw.Tick(gap);
			Assert.That(WorldForks.Capture(hw.OrderManager), Is.True, "The second capture was refused.");
			hw.Tick(gap);

			return hw;
		}

		static Actor CreateUnit(HeadlessWorld hw)
		{
			var area = hw.FindClearArea(hw.LocalPlayer.HomeLocation, 6, 6);
			return hw.CreateActor(Infantry, area + new CVec(2, 2));
		}

		static string UndoLine(string offset)
		{
			return $"{{\"cmd\":\"undo\",\"offset\":\"{offset}\"}}";
		}

		static string MoveLine(Actor unit, CPos cell, string extra = "")
		{
			return $"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"cell\":[{cell.X},{cell.Y}]{extra}}}";
		}

		/// <summary>The number of unresolved awaits on the world. Test use.</summary>
		static int PendingAwaits(HeadlessWorld hw)
		{
			var awaitType = HeadlessMod.ModData.ObjectCreator.FindType("AgentSocket.AgentOrderAwait");
			return (int)awaitType.GetMethod("PendingCount").Invoke(null, [hw.World]);
		}

		static int EnvelopeTick(JsonDocument envelope)
		{
			return envelope.RootElement.GetProperty("tick").GetInt32();
		}

		[TestCase(TestName = "An order parked on its Move replies ok:false with error restored when undo rewinds the world")]
		public void ParkedOrderFailsRestoredOnUndo()
		{
			using var hw = SeedWindow();
			var unit = CreateUnit(hw);
			hw.Tick(1);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);

			// Once the Move is parked, undo runs on the test thread while the socket thread waits for the Move's settle.
			string undoRaw = null;
			using var response = AgentStateDumpTest.RequestWithTicks(hw, MoveLine(unit, destination), () =>
			{
				if (undoRaw != null || PendingAwaits(hw) == 0)
					return;

				using var undo = AgentStateDumpTest.Request(UndoLine($"-{GapSeconds}"));
				undoRaw = undo.RootElement.GetRawText();
			});

			Assert.That(undoRaw, Is.Not.Null, "No undo ran while the Move was parked.");
			Assert.That(JsonDocument.Parse(undoRaw).RootElement.GetProperty("ok").GetBoolean(), Is.True, undoRaw);

			// The reply is the restored error, not a timeout: a timeout would read "did not settle".
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False, root.ToString());
			Assert.That(root.GetProperty("error").GetString(), Is.EqualTo("restored"));
		}

		[TestCase(TestName = "A job started with await:false and collected by wait after an undo reports error restored")]
		public void JobCollectedAfterUndoReportsRestored()
		{
			using var hw = SeedWindow();
			var unit = CreateUnit(hw);
			hw.Tick(1);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);

			using var issued = AgentStateDumpTest.RequestWithTicks(hw, MoveLine(unit, destination, ",\"await\":false"));
			var job = issued.RootElement.GetProperty("job").GetInt32();
			Assert.That(job, Is.GreaterThan(0), issued.RootElement.ToString());
			Assert.That(PendingAwaits(hw), Is.GreaterThan(0), "The job's Move had already settled, so the undo would not fail it.");

			using (var undo = AgentStateDumpTest.Request(UndoLine($"-{GapSeconds}")))
				Assert.That(undo.RootElement.GetProperty("ok").GetBoolean(), Is.True, undo.RootElement.ToString());

			using var waited = AgentStateDumpTest.RequestWithTicks(hw, $"{{\"cmd\":\"wait\",\"job\":{job}}}");
			var root = waited.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False, root.ToString());
			Assert.That(root.GetProperty("error").GetString(), Is.EqualTo("restored"));
		}

		[TestCase(TestName = "An undo as the first of three array items is refused, and the other two items still run")]
		public void UndoMidArrayIsRefusedOthersRun()
		{
			using var hw = SeedWindow();
			var liveTick = hw.OrderManager.World.WorldTick;

			using var refused = AgentStateDumpTest.Request($"[{UndoLine($"-{GapSeconds}")},{Ping},{Ping}]");
			var results = refused.RootElement.EnumerateArray().ToArray();

			Assert.That(results.Length, Is.EqualTo(3));
			Assert.That(results[0].GetProperty("ok").GetBoolean(), Is.False, results[0].ToString());
			Assert.That(results[0].GetProperty("error").GetString(), Does.Contain("undo"));
			Assert.That(results[1].GetProperty("ok").GetBoolean(), Is.True, results[1].ToString());
			Assert.That(results[2].GetProperty("ok").GetBoolean(), Is.True, results[2].ToString());
			Assert.That(hw.OrderManager.World.WorldTick, Is.EqualTo(liveTick), "A refused undo still moved the world.");
			Assert.That(WorldForks.Count, Is.EqualTo(2), "A refused undo consumed a snapshot.");

			// Last in the array, undo is allowed: the earlier item runs, and undo rewinds.
			using var last = AgentStateDumpTest.Request($"[{Ping},{UndoLine($"-{GapSeconds}")}]");
			var lastResults = last.RootElement.EnumerateArray().ToArray();
			Assert.That(lastResults[0].GetProperty("ok").GetBoolean(), Is.True, lastResults[0].ToString());
			Assert.That(lastResults[1].GetProperty("ok").GetBoolean(), Is.True, lastResults[1].ToString());
			Assert.That(hw.OrderManager.World.WorldTick, Is.LessThan(liveTick), "An undo last in the array did not rewind.");
		}

		[TestCase(TestName = "The envelope tick moves backwards after an undo, and stays at the rewound tick")]
		public void EnvelopeTickMovesBackwardsAfterUndo()
		{
			using var hw = SeedWindow();
			using var before = AgentStateDumpTest.RawRequest(Ping);
			var liveTick = EnvelopeTick(before);

			using var undo = AgentStateDumpTest.RawRequest(UndoLine($"-{GapSeconds}"));
			var rewoundTick = EnvelopeTick(undo);
			Assert.That(rewoundTick, Is.LessThan(liveTick), "The undo envelope did not show a lower tick.");

			using var after = AgentStateDumpTest.RawRequest(Ping);
			Assert.That(EnvelopeTick(after), Is.EqualTo(rewoundTick), "The next envelope did not keep the rewound tick.");
		}

		/// <summary>Runs the lines as one connection through AgentSocketServer.Serve, and returns its envelopes in order.</summary>
		static JsonDocument[] ServeConnection(params string[] lines)
		{
			var serve = HeadlessMod.ModData.ObjectCreator.FindType("AgentSocket.AgentSocketServer")
				.GetMethod("Serve", [typeof(TextReader), typeof(TextWriter)]);
			var writer = new StringWriter();
			var task = Task.Run(() => serve.Invoke(null, [new StringReader(string.Join("\n", lines) + "\n"), writer]));

			var deadline = DateTime.UtcNow.AddSeconds(10);
			while (!task.IsCompleted && DateTime.UtcNow < deadline)
			{
				Game.PerformDelayedActions();
				Thread.Sleep(1);
			}

			task.Wait();
			return writer.ToString().Split('\n')
				.Where(line => line.StartsWith('{'))
				.Select(line => JsonDocument.Parse(line))
				.Where(doc => doc.RootElement.TryGetProperty("results", out _))
				.ToArray();
		}

		[TestCase(TestName = "The static data comes back in the envelope after an undo, and stays out of the envelopes after that")]
		public void StaticsResentAfterUndo()
		{
			using var hw = SeedWindow();
			var envelopes = ServeConnection(Ping, UndoLine($"-{GapSeconds}"), Ping);
			try
			{
				Assert.That(envelopes, Has.Length.EqualTo(3));
				Assert.That(envelopes[0].RootElement.GetProperty("state").GetProperty("map").ValueKind, Is.EqualTo(JsonValueKind.Object),
					"Precondition: the connection's first envelope should carry the map.");
				Assert.That(envelopes[1].RootElement.GetProperty("state").GetProperty("map").ValueKind, Is.EqualTo(JsonValueKind.Object),
					"The envelope after an undo should carry the map again.");
				Assert.That(envelopes[2].RootElement.GetProperty("state").GetProperty("map").ValueKind, Is.EqualTo(JsonValueKind.Null),
					"The envelope after the one that resent the map should leave it null.");
			}
			finally
			{
				foreach (var envelope in envelopes)
					envelope.Dispose();
			}
		}
	}
}
