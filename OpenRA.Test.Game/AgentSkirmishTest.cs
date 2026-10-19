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
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using OpenRA.Network;
using OpenRA.Support;

namespace OpenRA.Test
{
	/// <summary>
	/// load_skirmish (9.1): the request schema and its errors through the real socket line, the pure lobby phases that
	/// place the bots, the new-world bot verification, and the reply's skirmish echo. The mod assembly is reached
	/// through the ObjectCreator, as in AgentStateDumpTest (this project must not reference it).
	/// </summary>
	[TestFixture]
	sealed class AgentSkirmishTest
	{
		const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

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

		static Type ModType(string name) => HeadlessMod.ModData.ObjectCreator.FindType(name);

		static object Prop(object value, string name) => value.GetType().GetProperty(name).GetValue(value);

		/// <summary>Builds the mod's SkirmishOpponent[] from plain values, so this assembly needs no reference to it.</summary>
		static object Opponents(params (string Bot, int Handicap)[] values)
		{
			var type = ModType("AgentSocket.SkirmishOpponent");
			var array = Array.CreateInstance(type, values.Length);
			for (var i = 0; i < values.Length; i++)
				array.SetValue(Activator.CreateInstance(type, [values[i].Bot, values[i].Handicap]), i);

			return array;
		}

		static object Plan(object opponents)
		{
			return Activator.CreateInstance(ModType("AgentSocket.SkirmishPlan"), ["uid", "title", "random", "default", opponents]);
		}

		static object ParseRequest(JsonElement root)
		{
			var parse = ModType("AgentSocket.AgentSkirmish").GetMethod("Parse", Static);
			var args = new object[] { root, null };
			var error = (string)parse.Invoke(null, args);
			Assert.That(error, Is.Null, "The request was rejected at parse time.");
			return args[1];
		}

		static LobbyStepView PhaseOne(object plan, Session lobby, int controllerIndex)
		{
			var method = ModType("AgentSocket.AgentSkirmishLobby").GetMethod("PhaseOne", Static);
			return new LobbyStepView(method.Invoke(null, [plan, lobby, controllerIndex]));
		}

		static LobbyStepView PhaseTwo(object plan, string[] slots, Session lobby)
		{
			var method = ModType("AgentSocket.AgentSkirmishLobby").GetMethod("PhaseTwo", Static);
			return new LobbyStepView(method.Invoke(null, [plan, slots, lobby]));
		}

		sealed record LobbyStepView(object Step)
		{
			public string[] Commands => (string[])Prop(Step, "Commands");
			public string[] Slots => (string[])Prop(Step, "Slots");
			public string Error => (string)Prop(Step, "Error");
		}

		static string PlacementError(MapPreview map, int opponents)
		{
			var method = ModType("AgentSocket.AgentSkirmish").GetMethod("PlacementError", Static);
			return (string)method.Invoke(null, [map, opponents]);
		}

		static MapPreview PickRandomMap(int opponents, int seed)
		{
			var method = ModType("AgentSocket.AgentSkirmish")
				.GetMethod("PickRandomMap", Static, [typeof(int), typeof(MersenneTwister)]);
			return (MapPreview)method.Invoke(null, [opponents, new MersenneTwister(seed)]);
		}

		static string VerifyOpponents(World world, object opponents)
		{
			var method = ModType("AgentSocket.AgentSkirmish").GetMethod("VerifyOpponents", Static);
			return (string)method.Invoke(null, [world, opponents]);
		}

		/// <summary>A lobby with the given slots, all allowing bots, and the host in the first one.</summary>
		static Session Lobby(params string[] slots)
		{
			var lobby = new Session();
			foreach (var slot in slots)
				lobby.Slots[slot] = new Session.Slot { PlayerReference = slot, AllowBots = true };

			lobby.Clients.Add(new Session.Client
			{
				Index = 0,
				Name = "Host",
				Slot = slots[0],
				IsAdmin = true,
				State = Session.ClientState.Ready,
			});

			return lobby;
		}

		/// <summary>The uid of the first map the maps command lists, for the resolution-error cases.</summary>
		static string LobbyMapUid()
		{
			using var maps = AgentStateDumpTest.Request("{\"cmd\":\"maps\"}");
			return maps.RootElement.GetProperty("maps")[0].GetProperty("uid").GetString();
		}

		[TestCase(TestName = "Every load_skirmish request error is the documented error envelope, and the running world is left in place")]
		public void RequestErrorsLeaveTheWorldAlone()
		{
			using var hw = HeadlessWorld.Create(1);

			(string Line, string Error)[] cases =
			[
				("{\"cmd\":\"load_skirmish\",\"map\":5}", "map must be a string"),
				("{\"cmd\":\"load_skirmish\",\"speed\":5}", "speed must be a string"),
				("{\"cmd\":\"load_skirmish\",\"opponents\":1}", "opponents must be an array of objects"),
				("{\"cmd\":\"load_skirmish\",\"opponents\":[1]}", "opponents[0] must be an object"),
				("{\"cmd\":\"load_skirmish\",\"opponents\":[{\"bot\":5}]}", "opponents[0].bot must be a string"),
				("{\"cmd\":\"load_skirmish\",\"opponents\":[{\"handicap\":\"x\"}]}", "opponents[0].handicap must be an integer"),
				("{\"cmd\":\"load_skirmish\",\"opponents\":[{\"handicap\":100}]}", "opponents[0].handicap must be 0 to 95 in steps of 5"),
				("{\"cmd\":\"load_skirmish\",\"opponents\":[{\"handicap\":72}]}", "opponents[0].handicap must be 0 to 95 in steps of 5"),
			];

			foreach (var (line, error) in cases)
			{
				using var response = AgentStateDumpTest.Request(line);
				var root = response.RootElement;
				Assert.That(root.GetProperty("ok").GetBoolean(), Is.False, line);
				Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("load_skirmish"), line);
				Assert.That(root.GetProperty("error").GetString(), Is.EqualTo(error), line);
				Assert.That(root.GetProperty("hint").GetString(), Is.EqualTo("see {\"cmd\":\"help\"}"), line);
			}

			using var state = AgentStateDumpTest.State();
			Assert.That(state.RootElement.GetProperty("map").GetProperty("uid").GetString(), Is.EqualTo(hw.World.Map.Uid),
				"A refused request must leave the running world in place.");
		}

		[TestCase(TestName = "A load_skirmish resolution error is the documented error envelope and starts nothing")]
		public void ResolutionErrors()
		{
			using var hw = HeadlessWorld.Create(1);
			var uid = LobbyMapUid();

			using var unknownMap = AgentStateDumpTest.Request("{\"cmd\":\"load_skirmish\",\"map\":\"no such map\"}");
			Assert.That(unknownMap.RootElement.GetProperty("error").GetString(), Is.EqualTo("Unknown map 'no such map'"));

			using var unknownSpeed = AgentStateDumpTest.Request($"{{\"cmd\":\"load_skirmish\",\"map\":\"{uid}\",\"speed\":\"warp\"}}");
			Assert.That(unknownSpeed.RootElement.GetProperty("error").GetString(), Does.StartWith("unknown speed 'warp'; use one of: "));
			Assert.That(unknownSpeed.RootElement.GetProperty("error").GetString(), Does.Contain("default"));

			using var unknownBot = AgentStateDumpTest.Request($"{{\"cmd\":\"load_skirmish\",\"map\":\"{uid}\",\"opponents\":[{{\"bot\":\"hard\"}}]}}");
			Assert.That(unknownBot.RootElement.GetProperty("error").GetString(), Does.StartWith("unknown bot 'hard'; use one of: "));

			using var state = AgentStateDumpTest.State();
			Assert.That(state.RootElement.GetProperty("map").GetProperty("uid").GetString(), Is.EqualTo(hw.World.Map.Uid));
		}

		[TestCase(TestName = "The load_skirmish defaults are a random map, the default speed, and one normal bot at 70 percent")]
		public void Defaults()
		{
			using var doc = JsonDocument.Parse("{\"cmd\":\"load_skirmish\"}");
			var request = ParseRequest(doc.RootElement);

			Assert.That(Prop(request, "Map"), Is.EqualTo("random"));
			Assert.That(Prop(request, "Speed"), Is.EqualTo("default"));

			var opponents = (Array)Prop(request, "Opponents");
			Assert.That(opponents.Length, Is.EqualTo(1));
			Assert.That(Prop(opponents.GetValue(0), "Bot"), Is.EqualTo("normal"));
			Assert.That(Prop(opponents.GetValue(0), "Handicap"), Is.EqualTo(70));
		}

		[TestCase(TestName = "An explicit load_skirmish parses its map, speed and opponents, and an empty opponents array is legal")]
		public void ExplicitRequest()
		{
			using var doc = JsonDocument.Parse(
				"{\"cmd\":\"load_skirmish\",\"map\":\"abc\",\"speed\":\"fast\",\"opponents\":[{\"bot\":\"turtle\",\"handicap\":50},{\"bot\":\"naval\"}]}");
			var request = ParseRequest(doc.RootElement);

			Assert.That(Prop(request, "Map"), Is.EqualTo("abc"));
			Assert.That(Prop(request, "Speed"), Is.EqualTo("fast"));

			var opponents = (Array)Prop(request, "Opponents");
			Assert.That(opponents.Length, Is.EqualTo(2));
			Assert.That(Prop(opponents.GetValue(0), "Bot"), Is.EqualTo("turtle"));
			Assert.That(Prop(opponents.GetValue(0), "Handicap"), Is.EqualTo(50));
			Assert.That(Prop(opponents.GetValue(1), "Bot"), Is.EqualTo("naval"));
			Assert.That(Prop(opponents.GetValue(1), "Handicap"), Is.EqualTo(70));

			using var solo = JsonDocument.Parse("{\"cmd\":\"load_skirmish\",\"opponents\":[]}");
			Assert.That(((Array)Prop(ParseRequest(solo.RootElement), "Opponents")).Length, Is.EqualTo(0));
		}

		[TestCase(TestName = "Phase 1 issues the speed option and one slot_bot per opponent, and phase 2 one handicap per bot then ready")]
		public void LobbyCommandSequence()
		{
			var plan = Plan(Opponents(("normal", 70)));
			var lobby = Lobby("Multi0", "Multi1", "Multi2");

			var one = PhaseOne(plan, lobby, 0);
			Assert.That(one.Error, Is.Null);
			Assert.That(one.Commands, Is.EqualTo(new[] { "option gamespeed default", "slot_bot Multi1 0 normal" }));
			Assert.That(one.Slots, Is.EqualTo(new[] { "Multi1" }));

			// Phase 2 waits until the server has synced the bot back, so it has a client index to name.
			Assert.That(PhaseTwo(plan, one.Slots, lobby).Commands, Is.Null, "The bot is not in the lobby yet.");

			lobby.Clients.Add(new Session.Client { Index = 1, Name = "Normal AI", Slot = "Multi1", Bot = "normal" });
			var two = PhaseTwo(plan, one.Slots, lobby);
			Assert.That(two.Commands, Is.EqualTo(new[] { "handicap 1 70", "state Ready" }));
		}

		[TestCase(TestName = "Two opponents get two slot_bot lines then two handicap lines, in the same order")]
		public void TwoOpponents()
		{
			var plan = Plan(Opponents(("normal", 70), ("turtle", 50)));
			var lobby = Lobby("Multi0", "Multi1", "Multi2");

			var one = PhaseOne(plan, lobby, 0);
			Assert.That(one.Commands, Is.EqualTo(new[]
			{
				"option gamespeed default",
				"slot_bot Multi1 0 normal",
				"slot_bot Multi2 0 turtle",
			}));

			lobby.Clients.Add(new Session.Client { Index = 1, Slot = "Multi1", Bot = "normal" });
			lobby.Clients.Add(new Session.Client { Index = 2, Slot = "Multi2", Bot = "turtle" });
			var two = PhaseTwo(plan, one.Slots, lobby);
			Assert.That(two.Commands, Is.EqualTo(new[] { "handicap 1 70", "handicap 2 50", "state Ready" }));
		}

		[TestCase(TestName = "The chosen slots skip occupied and bot-disabled slots, in PlayerReference name order")]
		public void SlotChoice()
		{
			var plan = Plan(Opponents(("normal", 70)));

			// The host occupies Multi0, and Multi1 forbids bots, so the first usable slot is Multi2.
			var lobby = Lobby("Multi0", "Multi1", "Multi2");
			lobby.Slots["Multi1"].AllowBots = false;
			Assert.That(PhaseOne(plan, lobby, 0).Slots, Is.EqualTo(new[] { "Multi2" }));

			// Name order, not insertion order.
			var unordered = new Session();
			foreach (var slot in new[] { "Multi2", "Multi0", "Multi1" })
				unordered.Slots[slot] = new Session.Slot { PlayerReference = slot, AllowBots = true };
			unordered.Clients.Add(new Session.Client { Index = 0, Slot = "Multi2", IsAdmin = true });
			Assert.That(PhaseOne(plan, unordered, 0).Slots, Is.EqualTo(new[] { "Multi0" }));
		}

		[TestCase(TestName = "A slot that locks handicap is refused rather than silently keeping its value")]
		public void LockedHandicapIsRefused()
		{
			var plan = Plan(Opponents(("normal", 70)));
			var lobby = Lobby("Multi0", "Multi1");
			lobby.Slots["Multi1"].LockHandicap = true;

			var step = PhaseOne(plan, lobby, 0);
			Assert.That(step.Commands, Is.Null);
			Assert.That(step.Error, Is.EqualTo("slot Multi1 locks handicap"));
		}

		[TestCase(TestName = "A lobby with too few free bot slots is refused")]
		public void TooFewSlots()
		{
			var plan = Plan(Opponents(("normal", 70), ("normal", 70)));
			var lobby = Lobby("Multi0", "Multi1");
			var step = PhaseOne(plan, lobby, 0);
			Assert.That(step.Error, Does.Contain("cannot place 2 opponents"));
		}

		[TestCase(TestName = "The new world must carry the requested bots with their requested handicaps")]
		public void OpponentVerification()
		{
			using var hw = HeadlessWorld.Create(1);
			Assume.That(hw.EnemyPlayer, Is.Not.Null, "The test map has no second playable slot.");

			var bot = hw.EnemyPlayer.BotType;
			Assert.That(bot, Is.Not.Null);
			Assert.That(hw.EnemyPlayer.Handicap, Is.EqualTo(0));

			Assert.That(VerifyOpponents(hw.World, Opponents((bot, 0))), Is.Null);
			Assert.That(VerifyOpponents(hw.World, Opponents((bot, 70))), Does.Contain("not 70"));
			Assert.That(VerifyOpponents(hw.World, Opponents((bot, 0), (bot, 0))), Does.Contain("has 1 of 2 requested bots"));
			Assert.That(VerifyOpponents(hw.World, Opponents(("naval", 0))), Does.Contain("no bot of type 'naval'"));
		}

		[TestCase(TestName = "The state digest carries each player's bot type and handicap, so a later envelope proves the opponents")]
		public void StateCarriesBotAndHandicap()
		{
			using var hw = HeadlessWorld.Create(1);
			Assume.That(hw.EnemyPlayer, Is.Not.Null, "The test map has no second playable slot.");

			using var state = AgentStateDumpTest.State();
			var players = state.RootElement.GetProperty("players").EnumerateArray().ToList();

			var human = players.Single(p => p.GetProperty("name").GetString() == "TestPlayer");
			Assert.That(human.GetProperty("bot").ValueKind, Is.EqualTo(JsonValueKind.Null));
			Assert.That(human.GetProperty("handicap").GetInt32(), Is.EqualTo(0));

			var enemy = players.Single(p => p.GetProperty("bot").ValueKind != JsonValueKind.Null);
			Assert.That(enemy.GetProperty("bot").GetString(), Is.EqualTo(hw.EnemyPlayer.BotType));
			Assert.That(enemy.GetProperty("handicap").GetInt32(), Is.EqualTo(0));
		}

		[TestCase(TestName = "The load_skirmish reply is the state shape plus the skirmish echo")]
		public void ReplyShape()
		{
			using var hw = HeadlessWorld.Create(1);

			var snapshot = ModType("AgentSocket.AgentStateDump").GetMethod("Snapshot", Static).Invoke(null, [hw.World, true]);
			var opponentType = ModType("AgentSocket.SkirmishOpponentV1");
			var opponents = Array.CreateInstance(opponentType, 1);
			opponents.SetValue(Activator.CreateInstance(opponentType, ["Normal AI", "normal", 70, "Multi1"]), 0);
			var echo = Activator.CreateInstance(ModType("AgentSocket.SkirmishV1"), ["uid", "random", "default", opponents]);
			var reply = ModType("AgentSocket.LoadSkirmishV1").GetMethod("From", Static).Invoke(null, [snapshot, echo]);
			var json = (string)ModType("AgentSocket.AgentSocketCommands").GetMethod("Serialize", Static).Invoke(null, [reply]);

			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("load_skirmish"));
			Assert.That(root.GetProperty("skirmish").GetProperty("map").GetString(), Is.EqualTo("uid"));
			Assert.That(root.GetProperty("skirmish").GetProperty("map_requested").GetString(), Is.EqualTo("random"));
			Assert.That(root.GetProperty("skirmish").GetProperty("speed").GetString(), Is.EqualTo("default"));

			var placed = root.GetProperty("skirmish").GetProperty("opponents")[0];
			Assert.That(placed.GetProperty("player").GetString(), Is.EqualTo("Normal AI"));
			Assert.That(placed.GetProperty("bot").GetString(), Is.EqualTo("normal"));
			Assert.That(placed.GetProperty("handicap").GetInt32(), Is.EqualTo(70));
			Assert.That(placed.GetProperty("slot").GetString(), Is.EqualTo("Multi1"));

			// The state half is the state shape, so a client reads it the same way.
			Assert.That(root.GetProperty("players").GetArrayLength(), Is.GreaterThan(0));
			Assert.That(root.GetProperty("map").GetProperty("uid").GetString(), Is.EqualTo(hw.World.Map.Uid));
		}

		[TestCase(TestName = "load_map is gone: it is an unknown command with the help hint, and the banner lists load_skirmish instead")]
		public void LoadMapIsGone()
		{
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"load_map\",\"map\":\"x\"}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("error").GetString(), Is.EqualTo("unknown command"));
			Assert.That(root.GetProperty("hint").GetString(), Is.EqualTo("see {\"cmd\":\"help\"}"));

			var names = (string[])ModType("AgentSocket.AgentSocketCommands").GetField("Names", Static).GetValue(null);
			Assert.That(names, Does.Contain("load_skirmish"));
			Assert.That(names, Does.Not.Contain("load_map"));

			var bannerNames = (string[])ModType("AgentSocket.AgentBanner").GetProperty("CommandNames", Static).GetValue(null);
			Assert.That(bannerNames, Is.EquivalentTo(names));

			var banner = (string)ModType("AgentSocket.AgentSocketServer").GetField("Banner", Static).GetValue(null);
			Assert.That(banner, Does.Not.Contain("load_map"));
			Assert.That(banner, Does.Contain("load_skirmish"));
		}

		[TestCase(TestName = "The random pick is deterministic and returns a lobby-visible Conquest map with room for the opponents")]
		public void RandomMapFilter()
		{
			var first = PickRandomMap(1, 0x1234);
			var again = PickRandomMap(1, 0x1234);
			Assert.That(again.Uid, Is.EqualTo(first.Uid));

			Assert.That(first.Status, Is.EqualTo(MapStatus.Available));
			Assert.That(first.Visibility.HasFlag(MapVisibility.Lobby), Is.True);
			Assert.That(first.Categories, Does.Contain("Conquest"));
			Assert.That(first.Players.Players.Values.Any(p => !p.AllowBots), Is.False);
			Assert.That(first.Bounds.Width, Is.LessThanOrEqualTo(128));
			Assert.That(first.Bounds.Height, Is.LessThanOrEqualTo(128));

			var playable = first.Players.Players.Values.Count(p => p.Playable && p.AllowBots);
			Assert.That(playable, Is.GreaterThanOrEqualTo(2));
		}

		[TestCase(TestName = "PlacementError refuses more opponents than the map can hold and passes when it can")]
		public void Placement()
		{
			var map = PickRandomMap(1, 0x1234);
			var playable = map.Players.Players.Values.Count(p => p.Playable && p.AllowBots);

			Assert.That(PlacementError(map, playable - 1), Is.Null);
			Assert.That(PlacementError(map, playable), Does.StartWith($"cannot place {playable} opponents: "));

			// A map that cannot hold a second player at all is refused for a single opponent too.
			var solo = Game.ModData.MapCache
				.FirstOrDefault(m => m.Status == MapStatus.Available && m.Visibility.HasFlag(MapVisibility.Lobby)
					&& (m.Players?.Players?.Values.Count(p => p.Playable && p.AllowBots) ?? 0) < 2);
			if (solo != null)
				Assert.That(PlacementError(solo, 1), Does.Contain("cannot place 1 opponents"));
		}
	}
}
