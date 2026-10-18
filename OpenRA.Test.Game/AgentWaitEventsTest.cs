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
	/// wait with no job is a long poll on the connection's turn buffer (P3.4). The wait reads a fake clock that the test advances
	/// once per tick, so a timeout happens on ticks and not on real time. An event raised while the wait is parked releases it,
	/// and the event is in the wait's own envelope.
	/// </summary>
	[TestFixture]
	sealed class AgentWaitEventsTest
	{
		const int Seed = 0x7A17;
		const string Infantry = "e1";
		const int SettleTicks = 5;

		/// <summary>The fake clock's step per tick, in milliseconds.</summary>
		const long TickMs = 100;

		/// <summary>The most ticks a served request may take before the test gives up.</summary>
		const int ServeDeadlineSeconds = 60;

		bool savedAgentSocket;
		Func<long> savedClock;
		long now;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[SetUp]
		public void InstallFakeClock()
		{
			savedAgentSocket = Game.Settings.Debug.AgentSocket;
			Game.Settings.Debug.AgentSocket = true;

			var awaitType = ModType("AgentSocket.AgentOrderAwait");
			savedClock = (Func<long>)awaitType.GetProperty("Clock").GetValue(null);
			now = 0;
			awaitType.GetProperty("Clock").SetValue(null, new Func<long>(() => Interlocked.Read(ref now)));
		}

		[TearDown]
		public void RestoreClock()
		{
			ModType("AgentSocket.AgentOrderAwait").GetProperty("Clock").SetValue(null, savedClock);
			Game.Settings.Debug.AgentSocket = savedAgentSocket;
		}

		static Type ModType(string name)
		{
			return HeadlessMod.ModData.ObjectCreator.FindType(name);
		}

		/// <summary>
		/// Runs Serve over the input lines on a task. This thread ticks the world and advances the fake clock by TickMs each tick,
		/// and calls onTick with the tick count, so a test can act while a request is parked.
		/// </summary>
		string Serve(HeadlessWorld hw, string input, Action<int> onTick = null)
		{
			var serve = ModType("AgentSocket.AgentSocketServer").GetMethod("Serve", [typeof(TextReader), typeof(TextWriter)]);
			var writer = new StringWriter();
			var task = Task.Run(() => serve.Invoke(null, [new StringReader(input), writer]));

			var ticks = 0;
			var deadline = DateTime.UtcNow.AddSeconds(ServeDeadlineSeconds);
			while (!task.IsCompleted && DateTime.UtcNow < deadline)
			{
				hw.TickOnce();
				Interlocked.Add(ref now, TickMs);
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
			var responses = Responses(Serve(hw, "{\"cmd\":\"wait\",\"timeout_ms\":5000}\n", tick =>
			{
				if (tick == 3)
					hw.World.IssueOrder(new Order("Move", unit, Target.FromCell(hw.World, destination), false));
			}));

			Assert.That(responses, Has.Count.EqualTo(1));
			Assert.That(ResultOf(responses[0]), Is.EqualTo("events"), "An event raised while the wait is parked should release it with result events.");
			Assert.That(Names(responses[0]), Is.Not.Empty, "The released wait's envelope should carry the events that released it.");
		}

		[TestCase(TestName = "With no events, a wait times out with result timeout and an empty events once the clock passes timeout_ms")]
		public void SilentWaitTimesOut()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);
			var start = Interlocked.Read(ref now);

			var responses = Responses(Serve(hw, "{\"cmd\":\"wait\",\"timeout_ms\":500}\n"));

			Assert.That(responses, Has.Count.EqualTo(1));
			Assert.That(ResultOf(responses[0]), Is.EqualTo("timeout"), "With no events, the wait should time out.");
			Assert.That(Names(responses[0]), Is.Empty, "A timed-out wait should carry no events.");
			Assert.That(Interlocked.Read(ref now) - start, Is.GreaterThanOrEqualTo(500),
				"The wait should have run until the clock passed its timeout_ms.");
		}
	}
}
