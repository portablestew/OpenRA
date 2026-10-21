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
	/// A connection's first envelope is initial:true, with its state in full and no events from before its first request (P3.5).
	/// Later envelopes on the connection do not carry the key. Each Serve call is a new connection.
	/// </summary>
	[TestFixture]
	sealed class AgentInitialEnvelopeTest
	{
		const int Seed = 0x1217;
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
		/// A reader that signals when Serve first asks for a line, then waits for the test before it gives the lines. It lets a test
		/// raise events on a registered connection before the connection's first request.
		/// </summary>
		sealed class GatedReader : TextReader
		{
			readonly Queue<string> lines;
			readonly SemaphoreSlim reading = new(0);
			readonly SemaphoreSlim release = new(0);
			bool gated;

			public GatedReader(params string[] lines)
			{
				this.lines = new Queue<string>(lines);
			}

			public void WaitUntilReading()
			{
				reading.Wait();
			}

			public void Continue()
			{
				release.Release();
			}

			public override string ReadLine()
			{
				if (!gated)
				{
					gated = true;
					reading.Release();
					release.Wait();
				}

				return lines.Count > 0 ? lines.Dequeue() : null;
			}
		}

		/// <summary>
		/// Runs Serve over the input on a task, ticking the world on this thread while it waits, and calls onTick with the tick
		/// count. Returns everything Serve wrote.
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

		/// <summary>Whether an envelope carries initial:true. A missing key is false, not an error.</summary>
		static bool IsInitial(JsonElement envelope)
		{
			return envelope.TryGetProperty("initial", out var flag) && flag.GetBoolean();
		}

		/// <summary>The event names in an envelope's events.</summary>
		static string[] Names(JsonElement envelope)
		{
			return envelope.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("event").GetString()).ToArray();
		}

		[TestCase(TestName = "A connection's first envelope is initial:true with the state in full, and its second has no initial key")]
		public void FirstEnvelopeIsInitialWithFullState()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);

			var responses = Responses(Serve(hw, "{\"cmd\":\"ping\",\"digest\":false}\n{\"cmd\":\"ping\"}\n"));
			Assert.That(responses, Has.Count.EqualTo(2));

			Assert.That(IsInitial(responses[0]), Is.True,
				"The first response on a connection should carry initial:true.");
			Assert.That(responses[0].GetProperty("state").ValueKind, Is.EqualTo(JsonValueKind.Object),
				"The first response should carry the state in full, even though its request set digest:false.");
			Assert.That(responses[0].GetProperty("state").TryGetProperty("units", out _), Is.True,
				"The first response's state should have its units.");

			Assert.That(responses[1].TryGetProperty("initial", out _), Is.False,
				"The second response on a connection should not carry an initial key.");
		}

		[TestCase(TestName = "Events raised before a connection's first request are not in its initial envelope")]
		public void BacklogBeforeFirstRequestIsDropped()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var unit = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			hw.Tick(SettleTicks);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);

			// The connection is registered and waiting for its first line. The Move's events are raised on that connection's backlog.
			var reader = new GatedReader("{\"cmd\":\"ping\"}");
			var serve = ModType("AgentSocket.AgentSocketServer").GetMethod("Serve", [typeof(TextReader), typeof(TextWriter)]);
			var writer = new StringWriter();
			var task = Task.Run(() => serve.Invoke(null, [reader, writer]));
			reader.WaitUntilReading();

			hw.World.IssueOrder(new Order("Move", unit, Target.FromCell(hw.World, destination), false));
			hw.Tick(SettleTicks);
			reader.Continue();
			task.Wait();

			var responses = Responses(writer.ToString());
			Assert.That(responses, Has.Count.EqualTo(1));
			Assert.That(Names(responses[0]), Is.Empty,
				"Events raised before the first request should not appear in the initial envelope.");
		}

		[TestCase(TestName = "Events raised before a connection registered, including by an earlier connection, do not appear in a new connection's first envelope")]
		public void EventsBeforeConnectDoNotAppear()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var unit = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			hw.Tick(SettleTicks);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);

			// An earlier connection orders a Move, so its events reach that connection and not this one.
			var earlier = Responses(Serve(hw, $"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}]}}\n"));
			Assert.That(Names(earlier[0]), Does.Contain("path"), "Precondition: the earlier connection should receive its Move's path.");

			// Then a Move with no connection, so its events are raised before the next connection registers.
			var back = hw.FindClearArea(destination, 1, 1, 6);
			hw.World.IssueOrder(new Order("Move", unit, Target.FromCell(hw.World, back), false));
			hw.Tick(SettleTicks);

			var responses = Responses(Serve(hw, "{\"cmd\":\"ping\"}\n"));
			Assert.That(IsInitial(responses[0]), Is.True, "A new connection's first envelope should be initial.");
			Assert.That(Names(responses[0]), Does.Not.Contain("path"),
				"A new connection's first envelope should not carry a path raised before it connected.");
		}

		[TestCase(TestName = "Each Serve call is a new connection, so each of two sequential calls gets its own initial:true")]
		public void EachServeCallGetsItsOwnInitial()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);

			var first = Responses(Serve(hw, "{\"cmd\":\"ping\"}\n{\"cmd\":\"ping\"}\n"));
			var second = Responses(Serve(hw, "{\"cmd\":\"ping\"}\n"));

			Assert.That(IsInitial(first[0]), Is.True, "The first connection's first envelope should be initial.");
			Assert.That(first[1].TryGetProperty("initial", out _), Is.False, "The first connection's second envelope should not be initial.");
			Assert.That(IsInitial(second[0]), Is.True,
				"A second connection's first envelope should be initial too.");
		}

		/// <summary>The state's player entry for a name.</summary>
		static JsonElement PlayerEntry(JsonElement envelope, string name)
		{
			return envelope.GetProperty("state").GetProperty("players").EnumerateArray()
				.Single(p => p.GetProperty("name").GetString() == name);
		}

		[TestCase(TestName = "A connection's first envelope carries the static data, and a later envelope on it leaves that data null")]
		public void StaticsOnlyInFirstEnvelopeOfAConnection()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var responses = Responses(Serve(hw, "{\"cmd\":\"ping\"}\n{\"cmd\":\"ping\"}\n"));
			Assert.That(responses, Has.Count.EqualTo(2));

			Assert.That(responses[0].GetProperty("state").GetProperty("map").ValueKind, Is.EqualTo(JsonValueKind.Object),
				"The first envelope should carry the map.");
			var firstPlayer = PlayerEntry(responses[0], local.PlayerName);
			Assert.That(firstPlayer.GetProperty("faction").ValueKind, Is.EqualTo(JsonValueKind.String),
				"The first envelope should carry the player's faction.");
			Assert.That(firstPlayer.GetProperty("handicap").ValueKind, Is.EqualTo(JsonValueKind.Number),
				"The first envelope should carry the player's handicap.");
			Assert.That(firstPlayer.GetProperty("color").GetString(), Does.Match("^#[0-9A-F]{6}$"),
				"The first envelope should carry the player's #RRGGBB colour.");

			Assert.That(responses[1].GetProperty("state").GetProperty("map").ValueKind, Is.EqualTo(JsonValueKind.Null),
				"A later envelope on the connection should leave the map null.");
			var laterPlayer = PlayerEntry(responses[1], local.PlayerName);
			Assert.That(laterPlayer.GetProperty("faction").ValueKind, Is.EqualTo(JsonValueKind.Null),
				"A later envelope should leave the faction null.");
			Assert.That(laterPlayer.GetProperty("bot").ValueKind, Is.EqualTo(JsonValueKind.Null),
				"A later envelope should leave the bot null.");
			Assert.That(laterPlayer.GetProperty("handicap").ValueKind, Is.EqualTo(JsonValueKind.Null),
				"A later envelope should leave the handicap null.");
			Assert.That(laterPlayer.GetProperty("color").ValueKind, Is.EqualTo(JsonValueKind.Null),
				"A later envelope should leave the colour null.");
			Assert.That(laterPlayer.GetProperty("name").GetString(), Is.EqualTo(local.PlayerName),
				"A later envelope should keep the player's name, the key its entry is found by.");
		}

		[TestCase(TestName = "A reconnect sends the static data again, in its first envelope")]
		public void ReconnectResendsStatics()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var first = Responses(Serve(hw, "{\"cmd\":\"ping\"}\n"));
			Assert.That(first[0].GetProperty("state").GetProperty("map").ValueKind, Is.EqualTo(JsonValueKind.Object),
				"Precondition: the first connection's first envelope should carry the map.");

			var second = Responses(Serve(hw, "{\"cmd\":\"ping\"}\n{\"cmd\":\"ping\"}\n"));
			Assert.That(second[0].GetProperty("state").GetProperty("map").ValueKind, Is.EqualTo(JsonValueKind.Object),
				"A reconnect's first envelope should carry the map again.");
			Assert.That(PlayerEntry(second[0], local.PlayerName).GetProperty("faction").ValueKind, Is.EqualTo(JsonValueKind.String),
				"A reconnect's first envelope should carry the faction again.");
			Assert.That(second[1].GetProperty("state").GetProperty("map").ValueKind, Is.EqualTo(JsonValueKind.Null),
				"The reconnect's second envelope should leave the map null.");
		}
	}
}
