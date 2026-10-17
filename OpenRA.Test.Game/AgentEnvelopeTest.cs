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
	/// The turn envelope (P1.16), checked through the whole socket path: an object request, an array request, digest:false,
	/// help (which has none), a request with no world loaded, and the help hint on errors (P1.18).
	/// </summary>
	[TestFixture]
	sealed class AgentEnvelopeTest
	{
		const string Hint = "see {\"cmd\":\"help\"}";
		static readonly string[] EnvelopeKeys = ["results", "events", "state", "tick", "map_state_png", "map_state_tick", "map_state_render",
			"map_px_per_cell", "map_origin", "map_size"];
		static readonly string[] PingKeys = ["ok", "cmd", "tick"];
		static readonly string[] HelpKeys = ["ok", "cmd", "banner"];
		static readonly string[] StateKeys = ["ok", "cmd", "tick", "net_frame", "map", "paused", "game_over", "local_player", "players", "units", "threats"];

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

		[TestCase(TestName = "An object request answers one envelope: its result, no events yet, the state digest, the tick, and the map image")]
		public void ObjectRequestHasTheEnvelope()
		{
			using var hw = HeadlessWorld.Create(1);
			using var response = AgentStateDumpTest.RawRequest("{\"cmd\":\"ping\"}");
			var root = response.RootElement;

			Assert.That(Keys(root), Is.EqualTo(EnvelopeKeys));
			Assert.That(Keys(root.GetProperty("results")), Is.EquivalentTo(PingKeys));
			Assert.That(root.GetProperty("events").GetArrayLength(), Is.Zero);
			Assert.That(Keys(root.GetProperty("state")), Is.EquivalentTo(StateKeys));
			Assert.That(root.GetProperty("tick").GetInt32(), Is.EqualTo(hw.World.WorldTick));
			Assert.That(root.GetProperty("map_state_render").GetString(), Is.EqualTo("ok"));
			Assert.That(root.GetProperty("map_state_tick").GetInt32(), Is.EqualTo(hw.World.WorldTick));
		}

		[TestCase(TestName = "An array request answers one envelope whose results is the array of per-item results")]
		public void ArrayRequestHasOneEnvelope()
		{
			using var hw = HeadlessWorld.Create(1);
			using var response = AgentStateDumpTest.RawRequest("[{\"cmd\":\"ping\"},{\"cmd\":\"state\"}]");
			var root = response.RootElement;

			Assert.That(Keys(root), Is.EqualTo(EnvelopeKeys));
			var results = root.GetProperty("results").EnumerateArray().ToArray();
			Assert.That(results.Length, Is.EqualTo(2));
			Assert.That(Keys(results[0]), Is.EquivalentTo(PingKeys));
			Assert.That(Keys(results[1]), Is.EquivalentTo(StateKeys));
			Assert.That(Keys(root.GetProperty("state")), Is.EquivalentTo(StateKeys));
		}

		[TestCase(TestName = "digest:false drops state from an object request, and from an array only when every item sets it")]
		public void DigestFalseOmitsState()
		{
			using var hw = HeadlessWorld.Create(1);

			using (var off = AgentStateDumpTest.RawRequest("{\"cmd\":\"ping\",\"digest\":false}"))
			{
				Assert.That(Keys(off.RootElement), Is.EqualTo(new[] { "results", "events", "tick" }));
				Assert.That(off.RootElement.GetProperty("tick").GetInt32(), Is.EqualTo(hw.World.WorldTick));
			}

			using (var allOff = AgentStateDumpTest.RawRequest("[{\"cmd\":\"ping\",\"digest\":false},{\"cmd\":\"ping\",\"digest\":false}]"))
				Assert.That(Keys(allOff.RootElement), Does.Not.Contain("state"));

			using (var mixed = AgentStateDumpTest.RawRequest("[{\"cmd\":\"ping\",\"digest\":false},{\"cmd\":\"ping\"}]"))
				Assert.That(Keys(mixed.RootElement), Does.Contain("state"));
		}

		[TestCase(TestName = "help answers the banner with no envelope; state answers the envelope with its digest as state")]
		public void HelpHasNoEnvelopeAndStateHasTheSameShape()
		{
			using var hw = HeadlessWorld.Create(1);

			using (var help = AgentStateDumpTest.RawRequest("{\"cmd\":\"help\"}"))
				Assert.That(Keys(help.RootElement), Is.EquivalentTo(HelpKeys));

			using var state = AgentStateDumpTest.RawRequest("{\"cmd\":\"state\"}");
			Assert.That(Keys(state.RootElement), Is.EqualTo(EnvelopeKeys));
			Assert.That(Keys(state.RootElement.GetProperty("results")), Is.EquivalentTo(StateKeys));
			Assert.That(Keys(state.RootElement.GetProperty("state")), Is.EquivalentTo(StateKeys));
		}

		[TestCase(TestName = "With no world loaded the envelope carries a null tick and state, and its results still answer")]
		public void NoWorldEnvelope()
		{
			var hw = HeadlessWorld.Create(1);
			hw.World.Dispose();
			hw.Dispose();

			using var response = AgentStateDumpTest.RawRequest("{\"cmd\":\"state\"}");
			var root = response.RootElement;

			Assert.That(Keys(root), Is.EqualTo(EnvelopeKeys));
			Assert.That(root.GetProperty("tick").ValueKind, Is.EqualTo(JsonValueKind.Null));
			Assert.That(root.GetProperty("state").ValueKind, Is.EqualTo(JsonValueKind.Null));
			Assert.That(root.GetProperty("map_state_render").GetString(), Is.EqualTo("unavailable"));
			Assert.That(root.GetProperty("results").GetProperty("ok").GetBoolean(), Is.False);
		}

		[TestCase(TestName = "An error carries the help hint: an unknown command, malformed JSON, and an error item in an array; a success carries none")]
		public void ErrorsCarryTheHelpHint()
		{
			using var hw = HeadlessWorld.Create(1);

			using (var unknown = AgentStateDumpTest.RawRequest("{\"cmd\":\"nope\"}"))
				Assert.That(unknown.RootElement.GetProperty("results").GetProperty("hint").GetString(), Is.EqualTo(Hint));

			using (var malformed = AgentStateDumpTest.RawRequest("{not json"))
				Assert.That(malformed.RootElement.GetProperty("results").GetProperty("hint").GetString(), Is.EqualTo(Hint));

			using (var array = AgentStateDumpTest.RawRequest("[{\"cmd\":\"ping\"},{\"cmd\":\"nope\"}]"))
			{
				var results = array.RootElement.GetProperty("results").EnumerateArray().ToArray();
				Assert.That(results[0].TryGetProperty("hint", out _), Is.False, "A success has no hint.");
				Assert.That(results[1].GetProperty("hint").GetString(), Is.EqualTo(Hint));
			}

			using var ping = AgentStateDumpTest.RawRequest("{\"cmd\":\"ping\"}");
			Assert.That(ping.RootElement.GetProperty("results").TryGetProperty("hint", out _), Is.False, "A success has no hint.");
		}
	}
}
