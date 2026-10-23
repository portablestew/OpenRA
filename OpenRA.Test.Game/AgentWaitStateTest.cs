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
using System.Linq;
using System.Text.Json;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The wait header and the state detail (step 8), checked on the serialized replies. A wait's envelope carries the summary
	/// header and no units list; state with no detail, or detail "full", is the digest it was before; detail "summary" is the header.
	/// </summary>
	[TestFixture]
	sealed class AgentWaitStateTest
	{
		/// <summary>The header key only an opponent's shown power timer adds; it is absent otherwise.</summary>
		const string PowersKey = "powers";

		static readonly string[] SummaryKeys = ["tick", "game_over", "winner", "local_player", "funds", "capacity", "power",
			"known_counts", "threats", "unit_counts"];
		static readonly string[] FullOnlyKeys = ["units", "map", "players", "selection", "net_frame", "ms_per_tick", "paused"];
		static readonly string[] MapEnvelopeKeys = ["map_state_png", "map_state_tick", "map_state_render", "map_px_per_cell", "map_origin", "map_size"];

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

		[TestCase(TestName = "A wait reply's envelope carries the summary header, and no units, map or player data")]
		public void WaitCarriesTheHeaderNotTheUnits()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			using var response = AgentStateDumpTest.RawRequest("{\"cmd\":\"wait\",\"timeout_ticks\":0}");
			var root = response.RootElement;

			Assert.That(root.GetProperty("results").GetProperty("result").GetString(), Is.EqualTo("timeout"));
			Assert.That(Keys(root), Does.Contain("state"), "A wait still carries the header as its envelope's state.");
			Assert.That(Keys(root).Intersect(MapEnvelopeKeys), Is.Empty, "A wait envelope renders no map image.");

			var header = root.GetProperty("state");
			Assert.That(Keys(header), Is.EquivalentTo(SummaryKeys), "The header has exactly the summary keys.");
			Assert.That(Keys(header).Intersect(FullOnlyKeys), Is.Empty, "The full units list and the static data are absent from a wait.");
		}

		[TestCase(TestName = "The wait header reads the local player's economy, the unit counts, and the game-over state")]
		public void WaitHeaderHasTheLocalPlayersView()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.CreateActor("e1", hw.FindClearArea(new CPos(60, 60), 1, 1), hw.EnemyPlayer);
			using var response = AgentStateDumpTest.RawRequest("{\"cmd\":\"wait\",\"timeout_ticks\":0}");
			var header = response.RootElement.GetProperty("state");

			Assert.That(header.GetProperty("tick").GetInt32(), Is.EqualTo(hw.World.WorldTick));
			Assert.That(header.GetProperty("game_over").GetBoolean(), Is.False);
			Assert.That(header.GetProperty("winner").ValueKind, Is.EqualTo(JsonValueKind.Null), "No winner is named while the game runs.");
			Assert.That(header.GetProperty("local_player").GetString(), Is.EqualTo(hw.LocalPlayer.PlayerName));

			// The full digest is the reference: its local player's funds and capacity are the economy the header reports.
			using var full = AgentStateDumpTest.State();
			var local = full.RootElement.GetProperty("players").EnumerateArray()
				.First(p => p.GetProperty("name").GetString() == hw.LocalPlayer.PlayerName);
			Assert.That(header.GetProperty("funds").GetInt32(), Is.EqualTo(local.GetProperty("funds").GetInt32()));
			Assert.That(header.GetProperty("capacity").GetInt32(), Is.EqualTo(local.GetProperty("resource_funds_capacity").GetInt32()));

			// The full digest lists the same units, so its own units and known counts are the reference for the header's.
			var listed = full.RootElement.GetProperty("units").EnumerateArray()
				.Count(u => u.TryGetProperty("owner", out var owner) && owner.GetString() == hw.LocalPlayer.PlayerName);
			Assert.That(header.GetProperty("unit_counts").GetProperty(hw.LocalPlayer.PlayerName).GetInt32(), Is.EqualTo(listed),
				"unit_counts is the number of the local player's units the full digest lists.");
			Assert.That(header.GetProperty("known_counts").GetProperty(hw.LocalPlayer.PlayerName).GetRawText(),
				Is.EqualTo(local.GetProperty("known_counts").GetRawText()), "known_counts is the full digest's known counts, per player.");
		}

		[TestCase(TestName = "A wait header lists an opponent's shown power timer, as the powers command reports it, and the summary has it too")]
		public void WaitHeaderListsAnOpponentsShownTimer()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("mslo", hw.FindClearArea(new CPos(50, 50), 2, 1), hw.EnemyPlayer);
			hw.CreateActor("powr", hw.FindClearArea(new CPos(40, 50), 2, 2), hw.EnemyPlayer);
			hw.CreateActor("powr", hw.FindClearArea(new CPos(40, 53), 2, 2), hw.EnemyPlayer);
			hw.Tick(5);

			using var waited = AgentStateDumpTest.RawRequest("{\"cmd\":\"wait\",\"timeout_ticks\":0}");
			var header = waited.RootElement.GetProperty("state");
			Assert.That(Keys(header), Is.EquivalentTo(SummaryKeys.Append(PowersKey)), "An opponent's shown timer adds exactly the powers key.");

			var timer = header.GetProperty(PowersKey).EnumerateArray().Single();
			Assert.That(Keys(timer), Is.EqualTo(new[] { "order", "owner", "remaining_ticks", "ready" }));

			// The powers command is the reference: the header's timer is the same power, read at the same tick.
			var nuke = EnemyNuke(hw);
			Assert.That(timer.GetProperty("order").GetString(), Is.EqualTo(nuke.GetProperty("order").GetString()));
			Assert.That(timer.GetProperty("owner").GetString(), Is.EqualTo(hw.EnemyPlayer.PlayerName));
			Assert.That(timer.GetProperty("remaining_ticks").GetInt32(), Is.EqualTo(nuke.GetProperty("remaining_ticks").GetInt32()));
			Assert.That(timer.GetProperty("ready").GetBoolean(), Is.False, "The silo's charge has barely started, so the power is not ready.");

			using var summary = AgentStateDumpTest.RawRequest("{\"cmd\":\"state\",\"detail\":\"summary\"}");
			Assert.That(summary.RootElement.GetProperty("state").GetRawText(), Is.EqualTo(header.GetRawText()));
		}

		[TestCase(TestName = "A wait header has no powers key when the local player's own power is the only one, or no power is built")]
		public void WaitHeaderOmitsPowersWithNoOpponentTimer()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.Tick(1);
			using var empty = AgentStateDumpTest.RawRequest("{\"cmd\":\"wait\",\"timeout_ticks\":0}");
			Assert.That(Keys(empty.RootElement.GetProperty("state")), Is.EquivalentTo(SummaryKeys), "With no power built there is no powers key.");

			// The local player's own silo is listed by the powers command, but the header leaves own powers out.
			hw.CreateActor("mslo", hw.FindClearArea(new CPos(50, 50), 2, 1), hw.LocalPlayer);
			hw.Tick(5);
			Assert.That(PowersOf().Any(p => p.GetProperty("owner").GetString() == hw.LocalPlayer.PlayerName), Is.True,
				"The own silo is listed, so the absence below is the header's rule and not a missing power.");
			using var own = AgentStateDumpTest.RawRequest("{\"cmd\":\"wait\",\"timeout_ticks\":0}");
			Assert.That(Keys(own.RootElement.GetProperty("state")), Is.EquivalentTo(SummaryKeys), "Own powers are left out of the header.");
		}

		[TestCase(TestName = "A wait header leaves out an opponent's power whose timer the local player is not shown")]
		public void WaitHeaderOmitsAHiddenOpponentTimer()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("powerproxy.parabombs", hw.FindClearArea(new CPos(50, 60), 1, 1), hw.EnemyPlayer);
			hw.Tick(5);

			Assert.That(PowersOf().Any(p => p.GetProperty("owner").GetString() == hw.EnemyPlayer.PlayerName), Is.False,
				"The enemy airstrike's timer is shown only to its owner, so the powers command does not list it either.");
			using var waited = AgentStateDumpTest.RawRequest("{\"cmd\":\"wait\",\"timeout_ticks\":0}");
			Assert.That(Keys(waited.RootElement.GetProperty("state")), Is.EquivalentTo(SummaryKeys), "A hidden timer adds no powers key.");
		}

		/// <summary>The powers command's entries, as JsonElements, for the local player's view at this tick.</summary>
		static JsonElement[] PowersOf()
		{
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"powers\"}");
			return response.RootElement.GetProperty("powers").EnumerateArray().Select(p => p.Clone()).ToArray();
		}

		/// <summary>The enemy's nuke as the powers command reports it.</summary>
		static JsonElement EnemyNuke(HeadlessWorld hw)
		{
			return PowersOf().Single(p => p.GetProperty("owner").GetString() == hw.EnemyPlayer.PlayerName
				&& p.GetProperty("order").GetString().StartsWith("NukePowerInfo", StringComparison.Ordinal));
		}

		[TestCase(TestName = "state with no detail is the full digest, byte for byte the same as detail full")]
		public void StateDefaultIsTheFullDigest()
		{
			using var hw = HeadlessWorld.Create(1);
			using var defaulted = AgentStateDumpTest.RawRequest("{\"cmd\":\"state\"}");
			using var full = AgentStateDumpTest.RawRequest("{\"cmd\":\"state\",\"detail\":\"full\"}");

			var digest = defaulted.RootElement.GetProperty("state");
			Assert.That(Keys(digest), Does.Contain("units"), "The default digest carries the units list.");
			Assert.That(Keys(digest), Does.Contain("map"));
			Assert.That(Keys(defaulted.RootElement), Does.Contain("map_state_png"), "The default state keeps the map image keys.");
			Assert.That(digest.GetRawText(), Is.EqualTo(full.RootElement.GetProperty("state").GetRawText()));
		}

		[TestCase(TestName = "state with detail summary answers the ack, and its envelope's state is the header")]
		public void StateSummaryIsTheHeader()
		{
			using var hw = HeadlessWorld.Create(1);
			using var response = AgentStateDumpTest.RawRequest("{\"cmd\":\"state\",\"detail\":\"summary\"}");
			var root = response.RootElement;

			Assert.That(root.GetProperty("results").GetProperty("cmd").GetString(), Is.EqualTo("state"));
			Assert.That(Keys(root.GetProperty("state")), Is.EquivalentTo(SummaryKeys));
			Assert.That(Keys(root).Intersect(MapEnvelopeKeys), Is.Empty);
		}

		[TestCase(TestName = "A wait's header is the same header state summary returns, tick aside")]
		public void WaitAndSummaryShareTheHeader()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			using var waited = AgentStateDumpTest.RawRequest("{\"cmd\":\"wait\",\"timeout_ticks\":0}");
			using var summary = AgentStateDumpTest.RawRequest("{\"cmd\":\"state\",\"detail\":\"summary\"}");

			Assert.That(waited.RootElement.GetProperty("state").GetRawText(), Is.EqualTo(summary.RootElement.GetProperty("state").GetRawText()));
		}

		[TestCase(TestName = "state refuses a detail that is neither full nor summary, and says what it takes")]
		public void StateRefusesAnUnknownDetail()
		{
			using var hw = HeadlessWorld.Create(1);
			using var response = AgentStateDumpTest.RawRequest("{\"cmd\":\"state\",\"detail\":\"lean\"}");
			var result = response.RootElement.GetProperty("results");

			Assert.That(result.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(result.GetProperty("error").GetString(), Does.Contain("detail must be"));
		}

		[TestCase(TestName = "An array carries the summary only when every item asks for it; a full item keeps the full digest")]
		public void ArrayDigestFollowsEveryItem()
		{
			using var hw = HeadlessWorld.Create(1);

			using (var waits = AgentStateDumpTest.RawRequest("[{\"cmd\":\"wait\",\"timeout_ticks\":0},{\"cmd\":\"state\",\"detail\":\"summary\"}]"))
				Assert.That(Keys(waits.RootElement.GetProperty("state")), Is.EquivalentTo(SummaryKeys));

			using (var mixed = AgentStateDumpTest.RawRequest("[{\"cmd\":\"wait\",\"timeout_ticks\":0},{\"cmd\":\"ping\"}]"))
				Assert.That(Keys(mixed.RootElement.GetProperty("state")), Does.Contain("units"));
		}

		[TestCase(TestName = "The wait reply is smaller than the full digest it replaces, and the bytes are reported")]
		public void WaitReplyIsSmallerThanTheFullDigest()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.CreateActor("e1", hw.FindClearArea(new CPos(60, 60), 1, 1), hw.EnemyPlayer);

			// A ping carries the full digest and the map keys, and no events, so it is the reply a wait used to send.
			using var before = AgentStateDumpTest.RawRequest("{\"cmd\":\"ping\"}");
			using var after = AgentStateDumpTest.RawRequest("{\"cmd\":\"wait\",\"timeout_ticks\":0}");
			var beforeBytes = before.RootElement.GetRawText().Length;
			var afterBytes = after.RootElement.GetRawText().Length;

			TestContext.Out.WriteLine($"wait bytes before (full digest, ping): {beforeBytes}");
			TestContext.Out.WriteLine($"wait bytes after (header, wait): {afterBytes}");
			Assert.That(afterBytes, Is.LessThan(beforeBytes), "The header is smaller than the full digest it replaces.");
		}
	}
}
