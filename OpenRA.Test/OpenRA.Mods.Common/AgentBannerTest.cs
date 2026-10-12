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
			var end = Array.FindIndex(BannerLines, l => l.StartsWith("Event groups", StringComparison.Ordinal));

			// Command rows are indented two spaces; the wrapped continuation of a row is indented further.
			var listed = BannerLines[(start + 1)..end]
				.Where(l => l.StartsWith("  ", StringComparison.Ordinal) && !l.StartsWith("   ", StringComparison.Ordinal))
				.Select(l => l.Trim().Split(' ')[0])
				.ToArray();

			Assert.That(AgentBanner.CommandNames, Is.EquivalentTo(AgentSocketCommands.Names));
			Assert.That(listed, Is.EquivalentTo(AgentSocketCommands.Names));
		}

		[TestCase(TestName = "Every command the banner lists is handled, not answered as an unknown command")]
		public void EveryBannerCommandIsHandled()
		{
			foreach (var name in AgentSocketCommands.Names)
			{
				// Commands that need the game thread time out without a game; only the error text matters here.
				var response = AgentSocketCommands.Handle($"{{\"cmd\":\"{name}\"}}", null);
				Assert.That(response, Does.Not.Contain("unknown command"), name);
			}

			Assert.That(AgentSocketCommands.Handle("{\"cmd\":\"nope\"}", null), Does.Contain("unknown command"));
		}

		[TestCase(TestName = "The banner lists every event group the hub accepts, each with its event type")]
		public void BannerListsEveryEventGroup()
		{
			foreach (var group in AgentEvents.Groups)
				Assert.That(BannerLines.Any(l => l.TrimStart().StartsWith(group + " ", StringComparison.Ordinal)), Is.True, group);

			foreach (var type in AgentBanner.EventTypes)
				Assert.That(BannerLines.Any(l => l.Contains($" {type}: ")), Is.True, type);
		}

		[TestCase(TestName = "Every payload key a real event serializes is listed in the banner for its event type")]
		public void BannerListsEveryPayloadKey()
		{
			var events = new object[]
			{
				new DamagedV1("damaged", 1, 2, "e1", "P", null, null, null, 3, 4, 5, "Normal"),
				new KilledV1("killed", 1, 2, "e1", "P", 7, "e2", "Q", 100),
				new CapturedV1("captured", 1, 2, "e1", "P", "Q", 7),
				new OrderEventV1("order", 1, 1, true, 0, "Move", 2, "e1", "P", [3, 4], null, false),
				new ProducedV1("produced", 1, 2, "e1", "P", 7, "fact"),
				new StanceV1("stance", 1, 2, "e1", "P", "Defend", "HoldFire"),
				new PathV1("path", 1, 2, "e1", "P", [1, 1], [3, 3], "All", false, "found", [[1, 1], [2, 2], [3, 3]], null),
				new MoveResultV1("move_result", 1, 2, "e1", "P", [1, 1], [3, 3], "reached", 5, 0),
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

		[TestCase(TestName = "The banner states the lower-bound ack, the canceled-move cell count, and the path end cell")]
		public void BannerDocumentsTheKnownCaveats()
		{
			var banner = AgentSocketServer.Banner;

			Assert.That(banner, Does.Contain("min_effect_tick is a lower bound"));
			Assert.That(banner, Does.Contain("remaining_cells is 0 on canceled moves"));
			Assert.That(banner, Does.Contain("path.to is the cell the path actually ends on"));
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
