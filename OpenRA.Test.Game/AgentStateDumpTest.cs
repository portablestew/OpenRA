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
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// Drives the real socket line path against a headless world, so the assertions cover the wire keys and not just
	/// the DTOs. The test thread plays the game loop, as in AgentMarshalTest.
	/// <para>
	/// This project must not reference OpenRA.Mods.Common (see the note in its csproj), so the handler is looked up
	/// through the mod's ObjectCreator, which is the only loader of the mod assemblies. That is the same assembly
	/// the bridge trait runs in.
	/// </para>
	/// </summary>
	[TestFixture]
	sealed class AgentStateDumpTest
	{
		static readonly string[] StateKeys = ["ok", "cmd", "tick", "net_frame", "map", "paused", "game_over", "local_player", "players", "units", "threats", "selection"];
		static readonly string[] UnitKeys = ["id", "type", "owner", "cell", "pos", "facing", "hp", "max_hp", "damage", "idle", "activity"];
		static readonly string[] PowerKeys = ["provided", "drained", "excess", "state"];

		sealed record PowerReading(int Provided, int Drained, int Excess, string State);

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

		static readonly Lazy<MethodInfo> Handle = new(() => HeadlessMod.ModData.ObjectCreator
			.FindType("AgentSocket.AgentSocketCommands")
			.GetMethod("Handle", BindingFlags.Public | BindingFlags.Static));

		/// <summary>
		/// Runs one request line on a worker thread while this thread pumps the game loop, and returns the result: the
		/// envelope's results for an object request, which is the shape these tests check. Use <see cref="RawRequest"/>
		/// for the whole envelope.
		/// </summary>
		internal static JsonDocument Request(string line)
		{
			using var envelope = RawRequest(line);
			return Results(envelope);
		}

		/// <summary>Runs one request line like <see cref="Request"/>, and returns the whole response: the turn envelope, or help's banner.</summary>
		internal static JsonDocument RawRequest(string line)
		{
			var request = Task.Run(() => (string)Handle.Value.Invoke(null, [line, null]));

			var deadline = DateTime.UtcNow.AddSeconds(10);
			while (!request.IsCompleted && DateTime.UtcNow < deadline)
			{
				Game.PerformDelayedActions();
				Thread.Sleep(1);
			}

			return JsonDocument.Parse(request.GetAwaiter().GetResult());
		}

		/// <summary>The results of an envelope, or the response itself when it has none (help). The caller disposes the result.</summary>
		internal static JsonDocument Results(JsonDocument response)
		{
			return JsonDocument.Parse(response.RootElement.TryGetProperty("results", out var results)
				? results.GetRawText()
				: response.RootElement.GetRawText());
		}

		/// <summary>
		/// Like <see cref="Request"/>, but the test thread also advances the world each loop, so a request that waits
		/// for world ticks (every await) can complete. Each loop ticks before it performs delayed actions, so a request
		/// handled on the game thread always sees at least one tick since it was issued.
		/// </summary>
		internal static JsonDocument RequestWithTicks(HeadlessWorld hw, string line)
		{
			return RequestWithTicks(hw, line, null);
		}

		/// <summary>Like <see cref="RequestWithTicks(HeadlessWorld, string)"/>, and onTick runs on the test thread after each tick while the request waits.</summary>
		internal static JsonDocument RequestWithTicks(HeadlessWorld hw, string line, Action onTick)
		{
			using var envelope = RawRequestWithTicks(hw, line, onTick);
			return Results(envelope);
		}

		/// <summary>Like <see cref="RequestWithTicks(HeadlessWorld, string)"/>, and returns the whole turn envelope, as <see cref="RawRequest"/> does (P2.13).</summary>
		internal static JsonDocument RawRequestWithTicks(HeadlessWorld hw, string line, Action onTick = null)
		{
			var request = Task.Run(() => (string)Handle.Value.Invoke(null, [line, null]));

			var deadline = DateTime.UtcNow.AddSeconds(10);
			while (!request.IsCompleted && DateTime.UtcNow < deadline)
			{
				hw.TickOnce();
				onTick?.Invoke();
				Game.PerformDelayedActions();
				Thread.Sleep(1);
			}

			return JsonDocument.Parse(request.GetAwaiter().GetResult());
		}

		static IEnumerable<string> Keys(JsonElement element)
		{
			return element.EnumerateObject().Select(p => p.Name);
		}

		[TestCase(TestName = "The state line carries exactly the schema v1 keys, with the nested map, player and unit keys")]
		public void StateLineMatchesSchema()
		{
			using var hw = HeadlessWorld.Create(1);
			using var response = Request("{\"cmd\":\"state\"}");
			var root = response.RootElement;

			Assert.That(Keys(root), Is.EquivalentTo(StateKeys));
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("state"));
			Assert.That(Keys(root.GetProperty("map")), Is.EquivalentTo(["uid", "title", "bounds", "spawns"]));
			Assert.That(root.GetProperty("map").GetProperty("uid").GetString(), Is.EqualTo(hw.World.Map.Uid));

			var players = root.GetProperty("players");
			Assert.That(players.GetArrayLength(), Is.GreaterThan(0));
			Assert.That(Keys(players[0]), Is.EquivalentTo(["name", "faction", "cash", "resources", "resource_capacity", "dead", "power", "known_counts"]));
			Assert.That(Keys(LocalPlayer(root).GetProperty("power")), Is.EquivalentTo(PowerKeys));

			var units = root.GetProperty("units");
			Assert.That(units.GetArrayLength(), Is.GreaterThan(0), "The starting map has no units to check.");
			Assert.That(Keys(units[0]), Is.EquivalentTo(UnitKeys));
		}

		[TestCase(TestName = "A power plant and a consumer move the local player's provided and drained, and excess and state follow")]
		public void StatePowerFollowsPlantAndConsumer()
		{
			using var hw = HeadlessWorld.Create(1);
			var before = Power();

			// POWR provides 100 and PBOX drains 20 (mods/ra/rules/structures.yaml, POWR at :1650 and PBOX at :768).
			hw.CreateActor("powr", hw.FindClearArea(new CPos(50, 50), 2, 2), hw.LocalPlayer);
			hw.Tick(1);
			var withPlant = Power();
			Assert.That(withPlant.Provided - before.Provided, Is.EqualTo(100));
			Assert.That(withPlant.Drained, Is.EqualTo(before.Drained));

			hw.CreateActor("pbox", hw.FindClearArea(new CPos(60, 60), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var withConsumer = Power();
			Assert.That(withConsumer.Drained - withPlant.Drained, Is.EqualTo(20));
			Assert.That(withConsumer.Excess, Is.EqualTo(withConsumer.Provided - withConsumer.Drained));

			var expected = withConsumer.Provided >= withConsumer.Drained ? "Normal"
				: withConsumer.Provided > withConsumer.Drained / 2 ? "Low" : "Critical";
			Assert.That(withConsumer.State, Is.EqualTo(expected));
		}

		static JsonElement LocalPlayer(JsonElement root)
		{
			var local = root.GetProperty("local_player").GetString();
			return root.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("name").GetString() == local);
		}

		static PowerReading Power()
		{
			using var state = Request("{\"cmd\":\"state\"}");
			var power = LocalPlayer(state.RootElement).GetProperty("power");
			return new PowerReading(power.GetProperty("provided").GetInt32(), power.GetProperty("drained").GetInt32(),
				power.GetProperty("excess").GetInt32(), power.GetProperty("state").GetString());
		}

		[TestCase(TestName = "The type filter returns only units of that type, and the owner filter only that owner's units")]
		public void ActorsFiltersByTypeAndOwner()
		{
			using var hw = HeadlessWorld.Create(1);

			using var state = Request("{\"cmd\":\"state\"}");
			var units = state.RootElement.GetProperty("units").EnumerateArray().ToList();
			var type = units[0].GetProperty("type").GetString();
			var owner = units[0].GetProperty("owner").GetString();

			// The expected count comes from the unfiltered actors command: state now omits the fogged enemies, and actors does not.
			using var all = Request("{\"cmd\":\"actors\"}");
			var expected = all.RootElement.GetProperty("units").EnumerateArray().Count(u => u.GetProperty("type").GetString() == type);

			using var byType = Request($"{{\"cmd\":\"actors\",\"type\":\"{type}\"}}");
			var typed = byType.RootElement.GetProperty("units").EnumerateArray().ToList();
			Assert.That(typed, Has.Count.EqualTo(expected));
			Assert.That(typed.All(u => u.GetProperty("type").GetString() == type), Is.True);

			using var byOwner = Request($"{{\"cmd\":\"actors\",\"owner\":\"{owner}\"}}");
			var owned = byOwner.RootElement.GetProperty("units").EnumerateArray().ToList();
			Assert.That(owned, Is.Not.Empty);
			Assert.That(owned.All(u => u.GetProperty("owner").GetString() == owner), Is.True);
		}

		[TestCase(TestName = "A Move request through the ticking pump is handled after the world has advanced past its issue tick")]
		public void TickingPumpAdvancesWorldWhileRequestPending()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);
			var issueTick = hw.World.WorldTick;

			var line = $"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}]}}";
			using var response = RequestWithTicks(hw, line);

			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(response.RootElement.GetProperty("tick").GetInt32(), Is.GreaterThan(issueTick));
		}

		[TestCase(TestName = "The maps line lists lobby-available maps as uid and title pairs")]
		public void MapsListsLobbyMaps()
		{
			using var response = Request("{\"cmd\":\"maps\"}");
			var root = response.RootElement;

			Assert.That(Keys(root), Is.EquivalentTo(["ok", "cmd", "maps"]));
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("maps"));

			var maps = root.GetProperty("maps");
			Assert.That(maps.GetArrayLength(), Is.GreaterThanOrEqualTo(1));
			Assert.That(Keys(maps[0]), Is.EquivalentTo(["uid", "title"]));
		}

		[TestCase(TestName = "An unknown load_map returns an error envelope and leaves the running world in place")]
		public void LoadMapUnknownKeepsWorld()
		{
			using var hw = HeadlessWorld.Create(1);

			using var load = Request("{\"cmd\":\"load_map\",\"map\":\"no such map\"}");
			Assert.That(load.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(load.RootElement.GetProperty("cmd").GetString(), Is.EqualTo("load_map"));
			Assert.That(load.RootElement.GetProperty("error").GetString(), Is.EqualTo("Unknown map 'no such map'"));

			using var state = Request("{\"cmd\":\"state\"}");
			Assert.That(state.RootElement.GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(state.RootElement.GetProperty("map").GetProperty("uid").GetString(), Is.EqualTo(hw.World.Map.Uid));
		}

		[TestCase(TestName = "A load_map whose map is not a string returns an error envelope")]
		public void LoadMapRejectsNonStringMap()
		{
			using var response = Request("{\"cmd\":\"load_map\",\"map\":5}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo("map must be a string"));
		}

		[TestCase(TestName = "A state or ping request with no live world returns an error envelope")]
		public void NoWorldReturnsErrorEnvelope()
		{
			var hw = HeadlessWorld.Create(1);

			// The bridge tracks the Regular world it loaded. Disposing it leaves no live world behind.
			hw.World.Dispose();
			hw.Dispose();

			using var state = Request("{\"cmd\":\"state\"}");
			Assert.That(state.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(state.RootElement.GetProperty("cmd").GetString(), Is.EqualTo("state"));
			Assert.That(state.RootElement.GetProperty("error").GetString(), Is.EqualTo("no game world is loaded"));

			using var ping = Request("{\"cmd\":\"ping\"}");
			Assert.That(ping.RootElement.GetProperty("ok").GetBoolean(), Is.False);
		}
	}
}
