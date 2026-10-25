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

using System.Linq;
using System.Text.Json;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// Every command the banner lists answers with its documented envelope. Read-only commands run against a headless
	/// world and must return ok:true with their response keys. The others are sent a request that fails validation, so
	/// they answer with the error envelope without touching the game thread.
	/// </summary>
	[TestFixture]
	sealed class AgentCommandEnvelopeTest
	{
		static readonly string[] PingKeys = ["ok", "cmd", "tick"];
		static readonly string[] StateAckKeys = ["ok", "cmd", "tick"];
		static readonly string[] HelpKeys = ["ok", "cmd", "banner"];
		static readonly string[] ActorsKeys = ["ok", "cmd", "units"];
		static readonly string[] MapsKeys = ["ok", "cmd", "maps"];
		static readonly string[] LookKeys = ["ok", "cmd", "cells", "actors", "summary"];
		static readonly string[] OrdersKeys = ["ok", "cmd", "actor", "type", "orders"];
		static readonly string[] ProductionKeys = ["ok", "cmd", "player", "funds", "cash_funds", "resource_funds", "power", "income_per_s", "spend_per_s", "queues"];
		static readonly string[] ErrorKeys = ["ok", "cmd", "error", "hint"];

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

		[TestCase(TestName = "Every read-only command the banner lists answers ok:true with its documented keys")]
		public void ReadOnlyCommandsAnswerTheirEnvelope()
		{
			using var hw = HeadlessWorld.Create(1);

			using (var ping = AgentStateDumpTest.Request("{\"cmd\":\"ping\"}"))
				Assert.That(Keys(ping.RootElement), Is.EquivalentTo(PingKeys));

			using (var help = AgentStateDumpTest.Request("{\"cmd\":\"help\"}"))
			{
				Assert.That(Keys(help.RootElement), Is.EquivalentTo(HelpKeys));
				Assert.That(help.RootElement.GetProperty("banner").GetString(), Does.Contain("Commands"));
			}

			using (var state = AgentStateDumpTest.Request("{\"cmd\":\"state\"}"))
				Assert.That(Keys(state.RootElement), Is.EquivalentTo(StateAckKeys));

			using (var actors = AgentStateDumpTest.Request("{\"cmd\":\"actors\"}"))
				Assert.That(Keys(actors.RootElement), Is.EquivalentTo(ActorsKeys));

			using (var maps = AgentStateDumpTest.Request("{\"cmd\":\"maps\"}"))
				Assert.That(Keys(maps.RootElement), Is.EquivalentTo(MapsKeys));

			using (var production = AgentStateDumpTest.Request("{\"cmd\":\"production\"}"))
				Assert.That(Keys(production.RootElement), Is.EquivalentTo(ProductionKeys));

			using (var look = AgentStateDumpTest.Request("{\"cmd\":\"look\",\"points\":[[50,50]]}"))
				Assert.That(Keys(look.RootElement), Is.EquivalentTo(LookKeys));

			var fact = hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);
			using (var orders = AgentStateDumpTest.Request($"{{\"cmd\":\"orders\",\"actor\":{fact.ActorID}}}"))
				Assert.That(Keys(orders.RootElement), Is.EquivalentTo(OrdersKeys));
		}

		[TestCase(TestName = "Every write command the banner lists answers with the error envelope for an invalid request")]
		public void WriteCommandsAnswerTheErrorEnvelope()
		{
			(string Line, string Cmd, string Error)[] invalid =
			[
				("{\"cmd\":\"order\"}", "order", "order must be a non-empty string"),
				("{\"cmd\":\"load_skirmish\",\"speed\":5}", "load_skirmish", "speed must be a string"),
				("{\"cmd\":\"build\"}", "build", "item must be a non-empty string"),
				("{\"cmd\":\"build\",\"item\":\"powr\",\"count\":0}", "build", "count must be a positive integer"),
				("{\"cmd\":\"place\"}", "place", "no finished item is waiting for placement: build it first"),
				("{\"cmd\":\"place\",\"item\":\"\"}", "place", "item must be a non-empty string"),
				("{\"cmd\":\"screenshot\",\"actor\":\"fact\"}", "screenshot", "actor must be an unsigned integer"),
				("{\"cmd\":\"screenshot\",\"actor\":-1}", "screenshot", "actor must be an unsigned integer"),
			];

			foreach (var (line, cmd, error) in invalid)
			{
				using var response = AgentStateDumpTest.Request(line);
				var root = response.RootElement;

				Assert.That(Keys(root), Is.EquivalentTo(ErrorKeys), cmd);
				Assert.That(root.GetProperty("ok").GetBoolean(), Is.False, cmd);
				Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo(cmd), cmd);
				Assert.That(root.GetProperty("error").GetString(), Is.EqualTo(error), cmd);
			}
		}

		[TestCase(TestName = "A root array answers one result per item, in order, and a failing item does not stop the rest")]
		public void ArrayRootGivesResultsInOrder()
		{
			using var hw = HeadlessWorld.Create(1);
			using var response = AgentStateDumpTest.RawRequest("[{\"cmd\":\"ping\"},{\"cmd\":\"bogus\"},{\"cmd\":\"ping\"}]");

			var results = response.RootElement.GetProperty("results").EnumerateArray().ToArray();
			Assert.That(results.Length, Is.EqualTo(3));
			Assert.That(Keys(results[0]), Is.EquivalentTo(PingKeys));
			Assert.That(results[0].GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(results[1].GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(results[1].GetProperty("error").GetString(), Is.EqualTo("unknown command"));
			Assert.That(results[2].GetProperty("cmd").GetString(), Is.EqualTo("ping"));
		}

		[TestCase(TestName = "A command sent as a string holding JSON is refused by name, as the root or as a batch item")]
		public void StringEncodedCommandIsRefused()
		{
			const string Refusal = "a command is a JSON object, or an array of objects, not a string holding JSON";

			using (var root = AgentStateDumpTest.Request("\"{\\\"cmd\\\":\\\"ping\\\"}\""))
			{
				Assert.That(Keys(root.RootElement), Is.EquivalentTo(ErrorKeys));
				Assert.That(root.RootElement.GetProperty("ok").GetBoolean(), Is.False);
				Assert.That(root.RootElement.GetProperty("error").GetString(), Is.EqualTo(Refusal));
			}

			using var batch = AgentStateDumpTest.Request("[\"{\\\"cmd\\\":\\\"ping\\\"}\"]");
			var item = batch.RootElement.EnumerateArray().Single();
			Assert.That(Keys(item), Is.EquivalentTo(ErrorKeys));
			Assert.That(item.GetProperty("error").GetString(), Is.EqualTo(Refusal));
		}
	}
}
