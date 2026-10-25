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
using OpenRA.Mods.Common.AgentSocket;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class AgentBannerTest
	{
		static readonly string[] BannerLines = AgentSocketServer.Banner.Split('\n');

		/// <summary>The banner line for an event type, such as "damaged: tick, actor, ...".</summary>
		static string EventLine(string type)
		{
			return BannerLines.Single(l => l.Contains($" {type}: "));
		}

		static string[] PayloadKeys(string line)
		{
			return line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..].Split(", ");
		}

		[TestCase(TestName = "The banner lists every command the dispatcher answers, and no others")]
		public void BannerListsEveryCommand()
		{
			var start = Array.FindIndex(BannerLines, l => l.StartsWith("Commands", StringComparison.Ordinal));
			var end = Array.FindIndex(BannerLines, l => l.StartsWith("Event types", StringComparison.Ordinal));

			// Command rows are indented two spaces; the wrapped continuation of a row is indented further.
			var listed = BannerLines[(start + 1)..end]
				.Where(l => l.StartsWith("  ", StringComparison.Ordinal) && !l.StartsWith("   ", StringComparison.Ordinal))
				.Select(l => l.Trim().Split(' ')[0])
				.ToArray();

			Assert.That(AgentBanner.CommandNames, Is.EquivalentTo(AgentSocketCommands.Names));
			Assert.That(listed, Is.EquivalentTo(AgentSocketCommands.Names));
		}

		[TestCase(TestName = "Every command has exactly one example line in the banner, and each example is a request for that command")]
		public void EveryCommandHasAnExample()
		{
			const string Prefix = "example: ";

			// Each command's example sits on the line under its row, indented past the command column.
			var examples = BannerLines.Where(l => l.TrimStart().StartsWith(Prefix, StringComparison.Ordinal))
				.Select(l => l.TrimStart()[Prefix.Length..])
				.ToArray();

			var cmds = examples.Select(e =>
			{
				using var doc = JsonDocument.Parse(e);
				return doc.RootElement.GetProperty("cmd").GetString();
			}).ToArray();

			Assert.That(cmds, Is.Unique);
			Assert.That(cmds, Is.EquivalentTo(AgentSocketCommands.Names));
		}

		[TestCase(TestName = "An unknown command is answered with the unknown command error")]
		public void UnknownCommandIsAnError()
		{
			Assert.That(AgentSocketCommands.Handle("{\"cmd\":\"nope\"}", null), Does.Contain("unknown command"));
		}

		[TestCase(TestName = "The banner lists every event type the hub raises, with its event key")]
		public void BannerListsEveryEventType()
		{
			foreach (var type in AgentBanner.EventTypes)
				Assert.That(BannerLines.Any(l => l.Contains($" {type}: ")), Is.True, type);
		}

		[TestCase(TestName = "Every payload key a real event serializes is listed in the banner for its event type")]
		public void BannerListsEveryPayloadKey()
		{
			var events = new object[]
			{
				new DamagedV1("damaged", 1, 2, "e1", "P", null, null, null, 3, 4, 5, "Normal", null, false),
				new KilledV1("killed", 1, 2, "e1", "P", 7, "e2", "Q", 100),
				new CapturedV1("captured", 1, 2, "e1", "P", "Q", 7),
				new OrderEventV1("order", 1, 1, true, 0, "Move", 2, "e1", "P", [3, 4], null, false),
				new ProducedV1("produced", 1, 2, "e1", "P", 7, "fact"),
				new StanceV1("stance", 1, 2, "e1", "P", "Defend", "HoldFire"),
				new PathV1("path", 1, 2, "e1", "P", [1, 1], [3, 3], "All", false, "found", 10, null),
				new ActivityV1("activity", 1, 2, "e1", "P", "move", "completed", "reached"),
				new PlacedV1("placed", 1, 2, "powr", "P", [3, 4]),
				new PowerEventV1("power_low", 1, "P", 10, 30, -20, "Low"),
				new PowerEventV1("power_restored", 1, "P", 30, 10, 20, "Normal"),
				new PowerReadyV1("power_ready", 1, "P", "NukePowerInfoOrder", "Nuclear Strike"),
				new DefeatedV1("defeated", 1, "P"),
				new GameOverV1("game_over", 1, "P"),

				// One sample sets every optional key, so the banner's keys are checked in full.
				new NotificationV1("notification", 1, "ConstructionComplete", "P", "powr", [3, 4], true, "Building", ["proc"], "powr", ["powr"],
					"exit_blocked", 300, 120, 50, 70, 9, 12u, 2),
				new CrateV1("crate", 1, "collected", 2, "crate", "Neutral", [3, 4], 7, "e1", "GiveCashCrateAction"),
				new HealedV1("healed", 1, 2, "e1", "P", 7, 30, 100, "Normal"),
				new SoldV1("sold", 1, 2, "e1", "P", [3, 4]),
				new OverflowV1("overflow", 3),
			};

			foreach (var e in events)
			{
				using var doc = JsonDocument.Parse(AgentSocketCommands.Serialize(e));
				var type = doc.RootElement.GetProperty("event").GetString();
				var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).Where(k => k != "event").ToArray();

				Assert.That(PayloadKeys(EventLine(type)), Is.EquivalentTo(keys), type);
			}
		}

		[TestCase(TestName = "The banner notes the prompt line, the lower-bound ack, and the overflow marker")]
		public void BannerDocumentsTheKnownCaveats()
		{
			var banner = AgentSocketServer.Banner;

			Assert.That(banner, Does.Contain("the server sends a lone > line as a prompt"));
			Assert.That(banner, Does.Contain("min_effect_tick is a lower bound"));
			Assert.That(banner, Does.Contain("An overflow event is sent before the events that follow it"));
		}

		[TestCase(TestName = "The banner says the static data rides with the first state of a connection and after a world change")]
		public void BannerDocumentsTheStaticData()
		{
			var banner = AgentSocketServer.Banner;

			Assert.That(banner, Does.Contain("The static data (state.map, state.ms_per_tick, and players' faction, color, bot and handicap) rides with the state"));
			Assert.That(banner, Does.Contain("a reconnect; in the envelopes between, those keys are null."));
		}

		[TestCase(TestName = "The banner has one blank line, at its end, so a client can read up to the blank line")]
		public void BannerEndsWithTheOnlyBlankLine()
		{
			var banner = AgentSocketServer.Banner;

			Assert.That(banner.EndsWith("\n\n", StringComparison.Ordinal), Is.True);
			Assert.That(banner[..^2], Does.Not.Contain("\n\n"));
		}

		[TestCase(TestName = "The banner does not name the superseded Game.AgentSocket setting")]
		public void BannerUsesTheDebugSettingNames()
		{
			Assert.That(AgentSocketServer.Banner, Does.Not.Contain("Game.AgentSocket"));
		}

		[TestCase(TestName = "The banner documents the structure state flags, and that power_state is omitted when the structure is on")]
		public void BannerDocumentsTheStructureFlags()
		{
			var banner = AgentSocketServer.Banner;

			Assert.That(banner, Does.Contain("repairing (true while you are among its repairers)"));
			Assert.That(banner, Does.Contain("power_state (\"down\" when its PowerDown toggle is on, \"starved\" when low power applies to it; omitted when it is on)"));
		}

		[TestCase(TestName = "The banner documents the on key, the toggle and Sell replies, and the healed rule")]
		public void BannerDocumentsToggleSellAndHealed()
		{
			var banner = AgentSocketServer.Banner;

			Assert.That(banner, Does.Contain("on (bool; required by a toggle order such as PowerDown, RepairBuilding or PrimaryProducer, and refused for any other name)"));
			Assert.That(banner, Does.Contain("state (on or off: the state it left once settled, or the state already held)"));
			Assert.That(banner, Does.Contain("unchanged (true when that state already held, so nothing was issued)"));
			Assert.That(banner, Does.Contain("RepairBuilding is a toggle on the player actor, with target_actor the building: its"
				+ " state is on while you are among the building's repairers, so a building at its repairer cap stays off."));
			Assert.That(banner, Does.Contain("Sell on a sellable structure replaces valid, effect_tick and settled_ticks with outcome (sold; null when unsettled)"));
			Assert.That(banner, Does.Contain("healed is any heal other than a repair"));
		}

		[TestCase(TestName = "The banner documents the group skip, the unknown target refusal, husks in threats, and the wait header's powers")]
		public void BannerDocumentsTheOrderAndThreatChanges()
		{
			var banner = AgentSocketServer.Banner;

			Assert.That(banner, Does.Contain("skipped (omitted when none) lists each one with error"));
			Assert.That(banner, Does.Contain("refused as an unknown target: ok:false, error \"Unknown target_actor <id>\", for a single order and a group alike."));
			Assert.That(banner, Does.Contain("husks are never listed"));
			Assert.That(banner, Does.Contain("and powers when one is shown: each other player's power whose timer you can see"));
		}

		[TestCase(TestName = "The banner documents the spotted and power_seen notifications, and the actors array on aggregates")]
		public void BannerDocumentsTheNotificationChanges()
		{
			var banner = AgentSocketServer.Banner;

			Assert.That(banner, Does.Contain("Scenery and husks are never spotted."));
			Assert.That(banner, Does.Contain("power_seen is sent once per power, the first time its timer is shown to you"));
			Assert.That(banner, Does.Contain("actors is always present:"));
			Assert.That(banner, Does.Contain("up to 10 of the distinct actors they covered, and [] when they name none."));
		}

		[TestCase(TestName = "The banner documents a LevelUp's item, actor and level, the level being the new rank")]
		public void BannerDocumentsTheLevelUpFields()
		{
			Assert.That(AgentSocketServer.Banner, Does.Contain("LevelUp names item, actor (the unit's id) and level (the new rank, 1-based)."));
		}

		[TestCase(TestName = "The banner documents the payload a notification marker keeps: the latest suppressed one's keys, per name and item")]
		public void BannerDocumentsTheNotificationMarkerPayload()
		{
			Assert.That(AgentSocketServer.Banner, Does.Contain("A notification's marker is per name and item, and carries the latest suppressed one's item, queue_type, reason, cost,"
				+ " remaining_cost, funds, short_by, producer, blocked_by, cell, placement_required, options, placeable, actor and level, each left out when unknown."));
		}

		[TestCase(TestName = "The banner documents placeable and blocked_by on a structure's ConstructionComplete, and NewOptions' options")]
		public void BannerDocumentsTheConstructionFields()
		{
			var banner = AgentSocketServer.Banner;

			Assert.That(banner, Does.Contain("A structure of yours also has placeable, every finished structure place accepts now,"
				+ " and blocked_by when items wait behind it."));
			Assert.That(banner, Does.Contain("NewOptions names in options the options that became buildable since the last one (empty when none)."));
		}

		[TestCase(TestName = "The banner lists Sell and the toggle names in orders, and says where the player actor's id is")]
		public void BannerDocumentsTheOrdersAndPlayerActor()
		{
			var banner = AgentSocketServer.Banner;

			Assert.That(banner, Does.Contain("Sell on a sellable structure, a toggle's name such as PowerDown on a toggle building,"
				+ " RepairBuilding on the player actor"));
			Assert.That(banner, Does.Contain("The player actor is production.queues[].actor"));
		}

		[TestCase(TestName = "help returns the banner as one string")]
		public void HelpReturnsTheBanner()
		{
			using var doc = JsonDocument.Parse(AgentSocketCommands.Handle("{\"cmd\":\"help\"}", null));
			var root = doc.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("help"));
			Assert.That(root.GetProperty("banner").GetString(), Is.EqualTo(AgentSocketServer.Banner));
		}
	}
}
