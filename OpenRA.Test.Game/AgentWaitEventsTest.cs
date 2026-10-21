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
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenRA.Traits;

namespace OpenRA.Test
{
	/// <summary>
	/// wait with no job is a long poll on the connection's turn buffer. The budget is game ticks: the test thread ticks the
	/// world while the request is parked, so a timeout happens on game ticks and not on real time. An event raised while the wait
	/// is parked releases it, and the event is in the wait's own envelope.
	/// </summary>
	[TestFixture]
	sealed class AgentWaitEventsTest
	{
		const int Seed = 0x7A17;
		const string Infantry = "e1";
		const int SettleTicks = 5;

		/// <summary>The most ticks a served request may take before the test gives up.</summary>
		const int ServeDeadlineSeconds = 60;

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

		static Type ModType(string name)
		{
			return HeadlessMod.ModData.ObjectCreator.FindType(name);
		}

		/// <summary>
		/// Runs Serve over the input lines on a task. This thread ticks the world each loop and calls onTick with the tick count,
		/// so a test can act while a request is parked.
		/// </summary>
		static string Serve(HeadlessWorld hw, string input, Action<int> onTick = null)
		{
			var serve = ModType("AgentSocket.AgentSocketServer").GetMethod("Serve", [typeof(TextReader), typeof(TextWriter)]);
			var writer = new StringWriter();
			var task = Task.Run(() => serve.Invoke(null, [new StringReader(input), writer]));

			var ticks = 0;
			var deadline = DateTime.UtcNow.AddSeconds(ServeDeadlineSeconds);
			while (!task.IsCompleted && DateTime.UtcNow < deadline)
			{
				hw.TickOnce();
				onTick?.Invoke(++ticks);
				Game.PerformDelayedActions();
				Thread.Sleep(1);
			}

			task.Wait();
			return writer.ToString();
		}

		/// <summary>The responses in the output: the lines with a results key.</summary>
		static List<JsonElement> Responses(string output)
		{
			var lines = new List<JsonElement>();
			foreach (var line in output.Split('\n'))
			{
				if (!line.StartsWith('{'))
					continue;

				using var doc = JsonDocument.Parse(line);
				if (doc.RootElement.TryGetProperty("results", out _))
					lines.Add(doc.RootElement.Clone());
			}

			return lines;
		}

		/// <summary>The result of a wait reply, read from its results.</summary>
		static string ResultOf(JsonElement envelope)
		{
			return envelope.GetProperty("results").GetProperty("result").GetString();
		}

		/// <summary>The event names in an envelope's events.</summary>
		static string[] Names(JsonElement envelope)
		{
			return envelope.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("event").GetString()).ToArray();
		}

		[TestCase(TestName = "An event raised while a wait is parked releases it with result events, and the event is in its envelope")]
		public void EventReleasesParkedWait()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var unit = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			hw.Tick(SettleTicks);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);

			// The Move is issued on the third tick, while the wait is parked, so its events must release the wait.
			var responses = Responses(Serve(hw, "{\"cmd\":\"wait\",\"timeout_ticks\":500}\n", tick =>
			{
				if (tick == 3)
					hw.World.IssueOrder(new Order("Move", unit, Target.FromCell(hw.World, destination), false));
			}));

			Assert.That(responses, Has.Count.EqualTo(1));
			Assert.That(ResultOf(responses[0]), Is.EqualTo("events"), "An event raised while the wait is parked should release it with result events.");
			Assert.That(Names(responses[0]), Is.Not.Empty, "The released wait's envelope should carry the events that released it.");
		}

		[TestCase(TestName = "With no events, a wait times out with result timeout and an empty events once the tick budget passes")]
		public void SilentWaitTimesOut()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);
			var start = hw.World.WorldTick;

			var responses = Responses(Serve(hw, "{\"cmd\":\"wait\",\"timeout_ticks\":50}\n"));

			Assert.That(responses, Has.Count.EqualTo(1));
			Assert.That(ResultOf(responses[0]), Is.EqualTo("timeout"), "With no events, the wait should time out.");
			Assert.That(Names(responses[0]), Is.Empty, "A timed-out wait should carry no events.");
			Assert.That(hw.World.WorldTick - start, Is.GreaterThanOrEqualTo(50),
				"The wait should have run until the game clock passed its tick budget.");
		}

		[TestCase(TestName = "min_ticks holds an event wait before it reports events that arrived earlier")]
		public void MinTicksHoldsTheWait()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var unit = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			hw.Tick(SettleTicks);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);

			var responses = Responses(Serve(hw, "{\"cmd\":\"wait\",\"timeout_ticks\":40,\"min_ticks\":20}\n", tick =>
			{
				if (tick == 1)
					hw.World.IssueOrder(new Order("Move", unit, Target.FromCell(hw.World, destination), false));
			}));

			Assert.That(responses, Has.Count.EqualTo(1));
			Assert.That(ResultOf(responses[0]), Is.EqualTo("events"), "An event should be reported once min_ticks passed.");
			Assert.That(responses[0].GetProperty("results").GetProperty("waited_ticks").GetInt32(), Is.GreaterThanOrEqualTo(20),
				"The wait reported the event before min_ticks.");
		}

		[TestCase(TestName = "A tick budget above the 1000-tick cap is refused")]
		public void BudgetAboveCapIsRefused()
		{
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"wait\",\"timeout_ticks\":1001}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("error").GetString(), Does.Contain("1000"));
		}

		[TestCase(TestName = "min_ticks above the tick budget is refused")]
		public void MinTicksAboveBudgetIsRefused()
		{
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"wait\",\"timeout_ticks\":10,\"min_ticks\":20}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("error").GetString(), Does.Contain("min_ticks"));
		}
	}
}
