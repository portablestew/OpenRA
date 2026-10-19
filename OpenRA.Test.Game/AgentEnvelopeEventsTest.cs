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

namespace OpenRA.Test
{
	/// <summary>
	/// The envelope's events are the turn's events for the connection, flushed once per request (P3.3). They are read through
	/// Serve, the way a real connection reads them: every connection gets its events in its envelope, and a turn of more than three
	/// of one type is aggregated in the response.
	/// </summary>
	[TestFixture]
	sealed class AgentEnvelopeEventsTest
	{
		const int Seed = 0xE7E7;
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
		/// Runs Serve over the input lines on a task, ticking the world on this thread while it waits, and returns everything
		/// it wrote. Each request's await needs ticks, which only this thread can give.
		/// </summary>
		static string Serve(HeadlessWorld hw, string input)
		{
			var serve = ModType("AgentSocket.AgentSocketServer").GetMethod("Serve", [typeof(TextReader), typeof(TextWriter)]);
			var writer = new StringWriter();
			var task = Task.Run(() => serve.Invoke(null, [new StringReader(input), writer]));

			var deadline = DateTime.UtcNow.AddSeconds(ServeDeadlineSeconds);
			while (!task.IsCompleted && DateTime.UtcNow < deadline)
			{
				hw.TickOnce();
				Game.PerformDelayedActions();
				Thread.Sleep(1);
			}

			task.Wait();
			return writer.ToString();
		}

		/// <summary>The JSON lines of the output that pass keep. Banner and prompt lines are skipped: they never start with a brace.</summary>
		static List<JsonElement> Lines(string output, Func<JsonElement, bool> keep)
		{
			var lines = new List<JsonElement>();
			foreach (var line in output.Split('\n'))
			{
				if (!line.StartsWith('{'))
					continue;

				using var doc = JsonDocument.Parse(line);
				if (keep(doc.RootElement))
					lines.Add(doc.RootElement.Clone());
			}

			return lines;
		}

		/// <summary>The responses: the lines with a results key. Pushed event lines have none.</summary>
		static List<JsonElement> Responses(string output)
		{
			return Lines(output, e => e.TryGetProperty("results", out _));
		}

		/// <summary>The event names in an envelope's events, one per event, including an aggregated marker's event type.</summary>
		static string[] Names(JsonElement envelope)
		{
			return envelope.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("event").GetString()).ToArray();
		}

		/// <summary>Places count infantry for the local player in a row at the top-left of a clear area, and returns the area.</summary>
		static CPos PlaceRow(HeadlessWorld hw, int count, out List<Actor> units)
		{
			var local = hw.LocalPlayer;
			var area = hw.FindClearArea(local.HomeLocation, count + 2, 3, 40);
			units = Enumerable.Range(0, count).Select(i => hw.CreateActor(Infantry, area + new CVec(i, 0), local)).ToList();
			return area;
		}

		[TestCase(TestName = "A Move's path and activity events land in the response that issued it, and not in the next")]
		public void MoveEventsLandInTheSameResponse()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var unit = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			hw.Tick(SettleTicks);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);

			var output = Serve(hw, $"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}]}}\n"
				+ "{\"cmd\":\"ping\"}\n");
			var responses = Responses(output);
			Assert.That(responses, Has.Count.EqualTo(2));

			var first = Names(responses[0]);
			Assert.That(first, Does.Contain("path"), "A Move's path should land in the response that issued it.");
			Assert.That(first, Does.Contain("activity"), "A Move's activity should land in the response that issued it.");
			Assert.That(Names(responses[1]), Is.Empty, "The next response should not repeat the events the last one carried.");
		}

		[TestCase(TestName = "Every connection gets its events in its envelope, with no event written outside a response")]
		public void EveryConnectionGetsEventsInItsEnvelope()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var unit = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			hw.Tick(SettleTicks);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);

			var output = Serve(hw, $"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}]}}\n");
			var responses = Responses(output);

			Assert.That(Lines(output, e => !e.TryGetProperty("results", out _)), Is.Empty, "No event should be written outside a response.");
			Assert.That(Names(responses[0]), Does.Contain("path"), "A connection should get the path in its envelope.");
		}

		[TestCase(TestName = "A path event carries from, to, check, result and metrics, and no cells key")]
		public void PathEventHasNoCellsKey()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var unit = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			hw.Tick(SettleTicks);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);

			var output = Serve(hw, $"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}]}}\n");
			var path = Responses(output)[0].GetProperty("events").EnumerateArray()
				.Single(e => e.GetProperty("event").GetString() == "path");

			Assert.That(path.TryGetProperty("cells", out _), Is.False, "A path event should not carry the planned cells.");
			Assert.That(path.TryGetProperty("from", out _), Is.True);
			Assert.That(path.TryGetProperty("to", out _), Is.True);
			Assert.That(path.GetProperty("result").GetString(), Is.EqualTo("found"));
		}

		[TestCase(TestName = "More than two events of one type in one turn come back as two in full and one aggregated marker")]
		public void MoreThanTwoOfATypeAggregate()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);

			var area = PlaceRow(hw, 5, out var units);
			hw.Tick(SettleTicks);
			var destination = new CPos(area.X + 2, area.Y + 4);
			var ids = string.Join(",", units.Select(u => u.ActorID));

			var output = Serve(hw, $"{{\"cmd\":\"order\",\"actors\":[{ids}],\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}]}}\n");
			var events = Responses(output)[0].GetProperty("events").EnumerateArray().ToList();

			var fullPaths = events.Where(e => e.GetProperty("event").GetString() == "path" && !e.TryGetProperty("aggregated", out _)).ToList();
			var markers = events.Where(e => e.TryGetProperty("aggregated", out _) && e.GetProperty("event").GetString() == "path").ToList();

			Assert.That(fullPaths, Has.Count.EqualTo(2), "The first two path events should be in full.");
			Assert.That(markers, Has.Count.EqualTo(1), "The rest of the path events should be one aggregated marker.");
			Assert.That(markers[0].GetProperty("event").GetString(), Is.EqualTo("path"));
			Assert.That(markers[0].GetProperty("aggregated").GetInt32(), Is.GreaterThanOrEqualTo(2),
				"The marker should count the path events past the first two.");
		}
	}
}
