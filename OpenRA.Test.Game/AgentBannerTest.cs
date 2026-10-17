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
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The banner's examples (P1.21). Every example invocation the help banner prints is parsed from the banner text itself, and
	/// each is sent through the socket. A request that dispatches to its own command echoes that command and is not an unknown
	/// command. Read-only examples must answer ok:true. load_map and screenshot are not dispatched, for the reasons in Unexercised.
	/// </summary>
	[TestFixture]
	sealed class AgentBannerTest
	{
		const int Seed = 0xBA44;

		/// <summary>Examples that are parsed and named, but not sent: each would change state the test cannot restore, with the reason.</summary>
		static readonly Dictionary<string, string> Unexercised = new()
		{
			["load_map"] = "it replaces the running world with a new game, which a headless test cannot restore",
			["screenshot"] = "it writes a frame file to disk",
		};

		/// <summary>Commands whose examples answer ok:true on a headless world with no selection, so their examples must succeed.</summary>
		static readonly string[] ReadOnly = ["ping", "help", "state", "actors", "maps", "production", "look"];

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

		static string Banner()
		{
			return (string)ModType("AgentSocket.AgentBanner").GetMethod("Build").Invoke(null, null);
		}

		static string[] CommandNames()
		{
			return (string[])ModType("AgentSocket.AgentBanner").GetProperty("CommandNames").GetValue(null);
		}

		/// <summary>The example invocations the banner prints, one per command row, in banner order.</summary>
		static string[] Examples()
		{
			return Regex.Matches(Banner(), @"^\s+example: (\{.*\})$", RegexOptions.Multiline)
				.Select(m => m.Groups[1].Value)
				.ToArray();
		}

		static string CmdOf(string request)
		{
			using var doc = JsonDocument.Parse(request);
			return doc.RootElement.GetProperty("cmd").GetString();
		}

		[TestCase(TestName = "The banner prints one example per command, and each example names its own command")]
		public void EveryCommandHasAnExampleNamingIt()
		{
			var examples = Examples();

			Assert.That(examples.Length, Is.EqualTo(CommandNames().Length), "The banner should print one example per command.");
			Assert.That(examples.Select(CmdOf), Is.EquivalentTo(CommandNames()));
		}

		[TestCase(TestName = "Every banner example is a parseable request that dispatches to its own command")]
		public void EveryExampleDispatchesToItsCommand()
		{
			using var hw = HeadlessWorld.Create(Seed);

			foreach (var example in Examples())
			{
				var cmd = CmdOf(example);
				if (Unexercised.ContainsKey(cmd))
					continue;

				// A request the socket answers, not a parse failure. Its echoed cmd is its own, and it is not the unknown-command error.
				using var response = AgentStateDumpTest.RequestWithTicks(hw, example);
				var root = response.RootElement;
				Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo(cmd), $"{example} did not echo its command: {root}");

				var error = root.TryGetProperty("error", out var errorElement) ? errorElement.GetString() : null;
				Assert.That(error, Is.Not.EqualTo("unknown command"), $"{example} dispatched to no command: {root}");
			}
		}

		[TestCase(TestName = "The read-only banner examples answer ok:true on a headless world")]
		public void ReadOnlyExamplesAnswerOk()
		{
			using var hw = HeadlessWorld.Create(Seed);

			foreach (var example in Examples().Where(e => ReadOnly.Contains(CmdOf(e))))
			{
				using var response = AgentStateDumpTest.RequestWithTicks(hw, example);
				Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.True, $"{example}: {response.RootElement}");
			}
		}

		[TestCase(TestName = "Every example the test does not send is still parseable, and its reason is stated")]
		public void UnexercisedExamplesAreParseableWithReasons()
		{
			var unsent = Examples().Where(e => Unexercised.ContainsKey(CmdOf(e))).ToArray();

			Assert.That(unsent.Select(CmdOf), Is.EquivalentTo(Unexercised.Keys));
			Assert.That(Unexercised.Values, Has.All.Not.Empty);
		}
	}
}
