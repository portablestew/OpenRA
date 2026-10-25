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
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The finished-item notification, read from the socket's own reply. A finished structure is named by ConstructionComplete with
	/// placement_required true and no cell. A finished unit is named by UnitReady, the RA rules' name for a unit's ready sound, with
	/// the cell it was built on and no placement_required. The notification is read from the wait envelopes the socket writes.
	/// </summary>
	[TestFixture]
	sealed class AgentConstructionNotificationTest
	{
		const int Seed = 0xC0A5;
		const int SettleTicks = 5;

		/// <summary>How many waits follow a build, and the wait each one sends, so the build has time to finish.</summary>
		const int Waits = 30;
		const string WaitLine = "{\"cmd\":\"wait\",\"timeout_ticks\":100}\n";

		/// <summary>The most ticks a served request may take before the test gives up.</summary>
		const int ServeDeadlineSeconds = 60;

		const string StructureNotification = "ConstructionComplete";
		const string UnitNotification = "UnitReady";

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

		[TestCase(TestName = "A finished structure's ConstructionComplete names the item and its queue, and says it waits for placement, with no cell")]
		public void FinishedStructureWaitsForPlacement()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(SettleTicks);

			var note = FinishedNotification(hw, "{\"cmd\":\"build\",\"item\":\"powr\"}", StructureNotification);

			Assert.That(note.GetProperty("item").GetString(), Is.EqualTo("powr"), "The notification should name the structure's type.");
			Assert.That(note.GetProperty("queue_type").GetString(), Is.EqualTo("Building"), "The notification should name the queue that built the structure.");
			Assert.That(note.GetProperty("placement_required").GetBoolean(), Is.True, "A finished structure waits for placement.");
			Assert.That(note.TryGetProperty("cell", out _), Is.False, "A finished structure has no cell until place puts it.");
			Assert.That(note.GetProperty("owner").GetString(), Is.EqualTo(hw.LocalPlayer.PlayerName));
			Assert.That(PlaceableOf(note), Is.EqualTo(new[] { "powr" }), "The only finished structure is this one, so it is the one place accepts.");
			Assert.That(note.TryGetProperty("blocked_by", out _), Is.False, "Nothing is queued behind the structure, so nothing waits on it.");
		}

		[TestCase(TestName = "A finished structure with items queued behind it names itself in blocked_by")]
		public void FinishedStructureNamesItsQueuedBehind()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(SettleTicks);

			// The second powr waits behind the first, so the first holds it until it is placed.
			var notes = FinishedNotifications(hw, StructureNotification,
				"{\"cmd\":\"build\",\"item\":\"powr\"}", "{\"cmd\":\"build\",\"item\":\"powr\"}");
			var first = notes[0];

			Assert.That(first.GetProperty("blocked_by").GetString(), Is.EqualTo("powr"), "The finished structure holds the item queued behind it.");
			Assert.That(PlaceableOf(first), Is.EqualTo(new[] { "powr" }), "The held structure is still the one place accepts.");
		}

		[TestCase(TestName = "ConstructionComplete lists every finished queue head in placeable")]
		public void PlaceableSpansQueues()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);

			// A Defense item needs a barracks: TENT for the Allies, and BARR for the Soviets.
			hw.CreateActor(hw.LocalPlayer.Faction.InternalName == "soviet" ? "barr" : "tent", hw.FindClearArea(new CPos(56, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(SettleTicks);

			// The Building queue's powr and the Defense queue's pbox both finish and wait for placement. The second one to finish
			// names both, so the last ConstructionComplete is the one to check.
			var notes = FinishedNotifications(hw, StructureNotification,
				"{\"cmd\":\"build\",\"item\":\"pbox\"}", "{\"cmd\":\"build\",\"item\":\"powr\"}");
			var last = notes[^1];

			Assert.That(PlaceableOf(last), Is.EqualTo(new[] { "powr", "pbox" }),
				"Placeable should name the finished head of each queue, in the order the production reply lists the queues.");
			Assert.That(last.TryGetProperty("blocked_by", out _), Is.False, "Each queue holds one item, so nothing waits behind either.");
		}

		[TestCase(TestName = "A finished unit's UnitReady names the item, its queue and the cell it was built on, with no placement_required")]
		public void FinishedUnitHasItsCell()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.CreateActor("powr", hw.FindClearArea(new CPos(60, 60), 2, 2), hw.LocalPlayer);

			// Infantry come from a barracks: BARR for the Soviets, TENT for the Allies. Either one meets e1's prerequisite.
			var barracks = hw.CreateActor(hw.LocalPlayer.Faction.InternalName == "soviet" ? "barr" : "tent",
				hw.FindClearArea(new CPos(56, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(SettleTicks);

			var note = FinishedNotification(hw, "{\"cmd\":\"build\",\"item\":\"e1\"}", UnitNotification);

			Assert.That(note.GetProperty("item").GetString(), Is.EqualTo("e1"), "The notification should name the unit's type.");
			Assert.That(note.GetProperty("queue_type").GetString(), Is.EqualTo("Infantry"), "The notification should name the queue that built the unit.");
			Assert.That(note.TryGetProperty("placement_required", out _), Is.False, "A finished unit is not placed, so it has no placement_required.");
			Assert.That(note.TryGetProperty("placeable", out _), Is.False, "Placeable is for finished structures only.");
			Assert.That(note.TryGetProperty("blocked_by", out _), Is.False, "Blocked_by is for finished structures only.");

			var cell = note.GetProperty("cell").EnumerateArray().Select(c => c.GetInt32()).ToArray();
			Assert.That(cell, Has.Length.EqualTo(2), "The cell is [x, y].");
			Assert.That(hw.World.Map.Contains(new CPos(cell[0], cell[1])), Is.True, "The unit's cell should be on the map.");
			Assert.That(Math.Max(Math.Abs(cell[0] - barracks.Location.X), Math.Abs(cell[1] - barracks.Location.Y)), Is.LessThanOrEqualTo(3),
				"A unit is built at its barracks' exit, next to the barracks.");
		}

		[TestCase(TestName = "A barracks with its exits walled in raises NoBuild with the item, its queue, exit_blocked, the producer and blocked_by")]
		public void BlockedExitNamesTheProducer()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.CreateActor("powr", hw.FindClearArea(new CPos(60, 60), 2, 2), hw.LocalPlayer);

			// Both barracks exits are the cells below it (structures.yaml, BARR and TENT: ExitCell 0,2 and 1,2). Sandbags on them
			// block the unit, and a structure is not moved aside the way a blocking unit is told to move.
			var area = hw.FindClearArea(new CPos(56, 50), 4, 5);
			var barracks = hw.CreateActor(hw.LocalPlayer.Faction.InternalName == "soviet" ? "barr" : "tent", area + new CVec(1, 1), hw.LocalPlayer);
			hw.CreateActor("sbag", barracks.Location + new CVec(0, 2), hw.LocalPlayer);
			hw.CreateActor("sbag", barracks.Location + new CVec(1, 2), hw.LocalPlayer);
			hw.Tick(SettleTicks);

			// The second e1 waits behind the first, which cannot leave, so the first holds it.
			var note = FinishedNotifications(hw, "NoBuild",
				"{\"cmd\":\"build\",\"item\":\"e1\"}", "{\"cmd\":\"build\",\"item\":\"e1\"}")[0];

			Assert.That(note.GetProperty("item").GetString(), Is.EqualTo("e1"), "NoBuild should name the unit that cannot leave.");
			Assert.That(note.GetProperty("queue_type").GetString(), Is.EqualTo("Infantry"));
			Assert.That(note.GetProperty("reason").GetString(), Is.EqualTo("exit_blocked"));
			Assert.That(note.GetProperty("producer").GetUInt32(), Is.EqualTo(barracks.ActorID), "The producer is the barracks to set a rally point on.");
			Assert.That(note.GetProperty("blocked_by").GetString(), Is.EqualTo("e1"), "The stuck e1 holds the one queued behind it.");
			foreach (var absent in new[] { "cost", "remaining_cost", "funds", "short_by", "placement_required", "placeable", "cell" })
				Assert.That(note.TryGetProperty(absent, out _), Is.False, $"NoBuild has no {absent}.");
		}

		[TestCase(TestName = "Running out of cash mid-build raises one InsufficientFunds with the item, its queue, cost, remaining_cost, funds and short_by")]
		public void ShortfallNamesTheItemAndItsCost()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(SettleTicks);

			var started = Responses(Serve(hw, "{\"cmd\":\"build\",\"item\":\"powr\"}\n"));
			Assert.That(started[0].GetProperty("results").GetProperty("ok").GetBoolean(), Is.True, "The build was refused.");

			// The build is paid for a slice each tick, so with no cash or resources left the next slice falls short. The engine
			// plays InsufficientFunds for the first shortfall, then not again for 30 s of real time.
			AgentProductionTest.SetLocalCash(hw, 0);
			var lines = new StringBuilder();
			for (var i = 0; i < 3; i++)
				lines.Append(WaitLine);

			var notes = Notifications(Serve(hw, lines.ToString())).Where(n => n.GetProperty("name").GetString() == "InsufficientFunds").ToList();
			Assert.That(notes, Has.Count.EqualTo(1), "The engine's throttle lets one InsufficientFunds through.");
			var note = notes[0];

			using var production = AgentStateDumpTest.Request("{\"cmd\":\"production\"}");
			var item = AgentProductionTest.QueueOf(production.RootElement, "Building").GetProperty("items")[0];
			var remaining = item.GetProperty("remaining_cost").GetInt32();

			Assert.That(note.GetProperty("item").GetString(), Is.EqualTo("powr"));
			Assert.That(note.GetProperty("queue_type").GetString(), Is.EqualTo("Building"));
			Assert.That(note.GetProperty("reason").GetString(), Is.EqualTo("production"));
			Assert.That(note.GetProperty("cost").GetInt32(), Is.EqualTo(item.GetProperty("cost").GetInt32()), "Cost is the item's total, not the slice.");
			Assert.That(note.GetProperty("remaining_cost").GetInt32(), Is.EqualTo(remaining), "The stalled item still owes what it owed then.");
			Assert.That(remaining, Is.InRange(1, note.GetProperty("cost").GetInt32()));
			Assert.That(note.GetProperty("funds").GetInt32(), Is.Zero);
			Assert.That(note.GetProperty("short_by").GetInt32(), Is.EqualTo(remaining), "With no funds, the item is short its whole remaining cost.");
			Assert.That(note.TryGetProperty("producer", out _), Is.False, "InsufficientFunds names no producer.");
		}

		/// <summary>Queues the build, serves the waits that follow it, and returns the first notification with the given name.</summary>
		static JsonElement FinishedNotification(HeadlessWorld hw, string build, string name)
		{
			return FinishedNotifications(hw, name, build)[0];
		}

		/// <summary>
		/// Queues each build in order, serves the waits that follow them, and returns every notification with the given name, in the
		/// order the socket sent them.
		/// </summary>
		static List<JsonElement> FinishedNotifications(HeadlessWorld hw, string name, params string[] builds)
		{
			var lines = new StringBuilder();
			foreach (var build in builds)
				lines.Append(build).Append('\n');

			for (var i = 0; i < Waits; i++)
				lines.Append(WaitLine);

			var output = Serve(hw, lines.ToString());
			var replies = Responses(output);
			for (var i = 0; i < builds.Length; i++)
				Assert.That(replies[i].GetProperty("results").GetProperty("ok").GetBoolean(), Is.True, "The build was refused.");

			var found = Notifications(output).Where(n => n.GetProperty("name").GetString() == name).ToList();
			Assert.That(found, Is.Not.Empty, $"No {name} notification reached the socket.");
			return found;
		}

		/// <summary>The placeable names of a notification, in the order the socket lists them.</summary>
		static string[] PlaceableOf(JsonElement note)
		{
			return note.GetProperty("placeable").EnumerateArray().Select(p => p.GetString()).ToArray();
		}

		/// <summary>
		/// Runs Serve over the input lines on a task, ticking the world on this thread while it waits, and returns everything it
		/// wrote. Each request's wait needs ticks, which only this thread can give.
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

		/// <summary>The responses: the lines with a results key. Pushed event lines have none.</summary>
		static List<JsonElement> Responses(string output)
		{
			return Lines(output).Where(e => e.TryGetProperty("results", out _)).ToList();
		}

		/// <summary>Every notification in the output: the ones an envelope carries in its events, and the ones pushed as lines of their own.</summary>
		static List<JsonElement> Notifications(string output)
		{
			var found = new List<JsonElement>();
			foreach (var line in Lines(output))
			{
				if (line.TryGetProperty("event", out var type) && type.GetString() == "notification")
					found.Add(line);

				if (line.TryGetProperty("events", out var events))
					found.AddRange(events.EnumerateArray()
						.Where(e => e.TryGetProperty("event", out var eventType) && eventType.GetString() == "notification"));
			}

			return found;
		}

		/// <summary>The JSON objects of the output. Banner and prompt lines never start with a brace, so they are skipped.</summary>
		static List<JsonElement> Lines(string output)
		{
			var lines = new List<JsonElement>();
			foreach (var line in output.Split('\n'))
			{
				if (!line.StartsWith('{'))
					continue;

				using var doc = JsonDocument.Parse(line);
				lines.Add(doc.RootElement.Clone());
			}

			return lines;
		}
	}
}
