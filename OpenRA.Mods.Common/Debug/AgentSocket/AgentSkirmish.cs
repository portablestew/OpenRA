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
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.AgentSocket
{
	// Schema v1, additive. Wire keys are snake_case (AgentSocketCommands.Json).

	/// <summary>One requested opponent (9.1): a bot type and its handicap percent.</summary>
	public sealed record SkirmishOpponent(string Bot, int Handicap);

	/// <summary>
	/// A parsed load_skirmish request before it is resolved: the map name (a uid, a title, or "random"), the game speed
	/// key, and the opponents in the order they are placed.
	/// </summary>
	public sealed record SkirmishRequest(string Map, string Speed, SkirmishOpponent[] Opponents);

	/// <summary>
	/// A resolved request: the chosen map's uid and title, the map name the request gave (so the echo can show that
	/// "random" was asked for), the speed key, and the opponents.
	/// </summary>
	public sealed record SkirmishPlan(string MapUid, string MapTitle, string MapRequested, string Speed, SkirmishOpponent[] Opponents);

	/// <summary>One placed opponent in the skirmish echo: the player it became, and the slot it occupies.</summary>
	public sealed record SkirmishOpponentV1(string Player, string Bot, int Handicap, string Slot);

	/// <summary>The skirmish echo (plan §3.3): what was actually started, which the random map makes unguessable.</summary>
	public sealed record SkirmishV1(string Map, string MapRequested, string Speed, SkirmishOpponentV1[] Opponents);

	/// <summary>
	/// The load_skirmish reply (plan §3.3, shape A): the flat state shape with cmd load_skirmish, plus the skirmish
	/// echo. Built from a StateV1 so the two shapes cannot drift.
	/// </summary>
	public sealed record LoadSkirmishV1(bool Ok, string Cmd, int Tick, int NetFrame, StateMapV1 Map, bool Paused, bool GameOver,
		string LocalPlayer, PlayerV1[] Players, object[] Units, ThreatV1[] Threats, uint[] Selection, SkirmishV1 Skirmish)
	{
		public static LoadSkirmishV1 From(StateV1 state, SkirmishV1 skirmish)
		{
			return new LoadSkirmishV1(state.Ok, "load_skirmish", state.Tick, state.NetFrame, state.Map, state.Paused,
				state.GameOver, state.LocalPlayer, state.Players, state.Units, state.Threats, state.Selection, skirmish);
		}
	}

	/// <summary>
	/// The load_skirmish request (9.1): the map, the game speed, and the opponents. Parsing is pure and runs on the
	/// socket thread; resolution reads the mod's map cache and rules and so must run on the game thread.
	/// </summary>
	public static class AgentSkirmish
	{
		/// <summary>The map sentinel that asks for a suitable random skirmish map.</summary>
		public const string RandomMap = "random";

		/// <summary>The speed key whose label is "normal" (mods/ra/mod.yaml, GameSpeeds).</summary>
		public const string DefaultSpeed = "default";

		public const string DefaultBot = "normal";
		public const int DefaultHandicap = 70;

		/// <summary>The default request's opponents: one CPU at 70% handicap (the owner's 1v1 default).</summary>
		public static SkirmishOpponent[] DefaultOpponents() => [new SkirmishOpponent(DefaultBot, DefaultHandicap)];

		/// <summary>Handicaps the engine accepts: 0 to 95 in steps of 5 (LobbyCommands.Handicap).</summary>
		const int MaxHandicap = 95;

		/// <summary>Parses the request on the socket thread. Returns an error message, or null when the request is valid.</summary>
		public static string Parse(JsonElement root, out SkirmishRequest request)
		{
			request = null;

			var map = RandomMap;
			if (AgentOrder.IsPresent(root, "map"))
			{
				var element = root.GetProperty("map");
				if (element.ValueKind != JsonValueKind.String)
					return "map must be a string";

				map = element.GetString();
			}

			var speed = DefaultSpeed;
			if (AgentOrder.IsPresent(root, "speed"))
			{
				var element = root.GetProperty("speed");
				if (element.ValueKind != JsonValueKind.String)
					return "speed must be a string";

				speed = element.GetString();
			}

			var opponents = DefaultOpponents();
			if (AgentOrder.IsPresent(root, "opponents"))
			{
				var element = root.GetProperty("opponents");
				if (element.ValueKind != JsonValueKind.Array)
					return "opponents must be an array of objects";

				var parsed = new List<SkirmishOpponent>();
				var index = 0;
				foreach (var item in element.EnumerateArray())
				{
					if (item.ValueKind != JsonValueKind.Object)
						return $"opponents[{index}] must be an object";

					var bot = DefaultBot;
					if (AgentOrder.IsPresent(item, "bot"))
					{
						var botElement = item.GetProperty("bot");
						if (botElement.ValueKind != JsonValueKind.String)
							return $"opponents[{index}].bot must be a string";

						bot = botElement.GetString();
					}

					var handicap = DefaultHandicap;
					if (AgentOrder.IsPresent(item, "handicap"))
					{
						// TryGetInt32 throws, rather than returning false, for a non-number, so the kind is checked first.
						var handicapElement = item.GetProperty("handicap");
						if (handicapElement.ValueKind != JsonValueKind.Number || !handicapElement.TryGetInt32(out handicap))
							return $"opponents[{index}].handicap must be an integer";

						if (handicap < 0 || handicap > MaxHandicap || handicap % 5 != 0)
							return $"opponents[{index}].handicap must be 0 to {MaxHandicap} in steps of 5";
					}

					parsed.Add(new SkirmishOpponent(bot, handicap));
					index++;
				}

				opponents = parsed.ToArray();
			}

			request = new SkirmishRequest(map, speed, opponents);
			return null;
		}

		/// <summary>
		/// Resolves the request against the mod's map cache and rules. Must run on the game thread. Throws
		/// ArgumentException with the reason for a bad map, an unknown speed, an unknown bot, or opponents the map
		/// cannot hold; the running world is untouched in every one of those cases.
		/// </summary>
		public static SkirmishPlan Resolve(SkirmishRequest request)
		{
			var map = ResolveMap(request.Map, request.Opponents.Length);
			ResolveSpeed(request.Speed);
			ResolveBots(map, request.Opponents);

			var placement = PlacementError(map, request.Opponents.Length);
			if (placement != null)
				throw new ArgumentException(placement);

			return new SkirmishPlan(map.Uid, map.Title, request.Map, request.Speed, request.Opponents);
		}

		/// <summary>Matches a uid first, then a title, or picks a suitable random map for the "random" sentinel.</summary>
		static MapPreview ResolveMap(string name, int opponents)
		{
			if (name == RandomMap)
				return PickRandomMap(opponents, Game.CosmeticRandom);

			var maps = Game.ModData.MapCache
				.Where(m => m.Status == MapStatus.Available && m.Visibility.HasFlag(MapVisibility.Lobby))
				.ToArray();

			var match = maps.Where(m => m.Uid == name).ToArray();
			if (match.Length == 0)
				match = maps.Where(m => m.Title == name).ToArray();

			if (match.Length == 0)
				throw new ArgumentException($"Unknown map '{name}'");

			if (match.Length > 1)
				throw new ArgumentException($"Ambiguous map title '{name}'; use the uid");

			return match[0];
		}

		/// <summary>The game speed keys the engine defines, so the error list cannot go stale.</summary>
		static string[] SpeedKeys()
		{
			return Game.ModData.GetOrCreate<GameSpeeds>().Speeds.Keys.ToArray();
		}

		static void ResolveSpeed(string speed)
		{
			if (!SpeedKeys().Contains(speed))
				throw new ArgumentException($"unknown speed '{speed}'; use one of: {string.Join(", ", SpeedKeys())}");
		}

		/// <summary>The bot types the chosen map's player actor defines (IBotInfo), so the error list cannot go stale.</summary>
		static string[] BotTypes(MapPreview map)
		{
			return map.PlayerActorInfo.TraitInfos<IBotInfo>().Select(b => b.Type).ToArray();
		}

		static void ResolveBots(MapPreview map, SkirmishOpponent[] opponents)
		{
			var types = BotTypes(map);
			foreach (var opponent in opponents)
				if (!types.Contains(opponent.Bot))
					throw new ArgumentException($"unknown bot '{opponent.Bot}'; use one of: {string.Join(", ", types)}");
		}

		/// <summary>
		/// Whether the map can hold one human plus <paramref name="opponents"/> bots: enough playable slots that allow
		/// bots, and (when the map defines spawn points) enough of those. Null when it can. Used both to refuse an
		/// explicit map and to filter the candidates for the random pick.
		/// </summary>
		internal static string PlacementError(MapPreview map, int opponents)
		{
			var playable = map.Players?.Players?.Values.Count(p => p.Playable && p.AllowBots) ?? 0;
			if (playable < opponents + 1)
			{
				var slots = playable == 1
					? "1 free playable slot that allows bots"
					: $"{playable} free playable slots that allow bots";
				return $"cannot place {opponents} opponents: the map has {slots}";
			}

			var spawns = map.SpawnPoints.Length;
			if (spawns > 0 && spawns < opponents + 1)
			{
				var points = spawns == 1 ? "1 spawn point" : $"{spawns} spawn points";
				return $"cannot place {opponents} opponents: the map has {points}";
			}

			return null;
		}

		/// <summary>
		/// The maps a random 1v1 may land on: the map cache's own initial-map policy (lobby-visible, Conquest, every
		/// player allows bots, at most 128x128) plus room for the host and the opponents.
		/// </summary>
		internal static bool IsSuitableRandomMap(MapPreview map, int opponents)
		{
			if (map.Status != MapStatus.Available || !map.Visibility.HasFlag(MapVisibility.Lobby))
				return false;

			if (!map.Categories.Contains("Conquest"))
				return false;

			if (map.Bounds.Width > 128 || map.Bounds.Height > 128)
				return false;

			if (map.Players?.Players == null || map.Players.Players.Values.Any(p => !p.AllowBots))
				return false;

			return PlacementError(map, opponents) == null;
		}

		/// <summary>
		/// The random map for a host plus <paramref name="opponents"/> bots. The RNG is a parameter so a test can pin
		/// the pick; production passes Game.CosmeticRandom.
		/// </summary>
		internal static MapPreview PickRandomMap(int opponents, MersenneTwister random)
		{
			var candidates = Game.ModData.MapCache.Where(m => IsSuitableRandomMap(m, opponents)).ToArray();
			if (candidates.Length == 0)
				throw new ArgumentException($"no map is suitable for a random skirmish against {opponents} opponents");

			return candidates.RandomOrDefault(random);
		}

		/// <summary>
		/// The new world must carry exactly the requested bots, each with the requested handicap (plan §4 item 5).
		/// Returns the reason it does not, or null. A map that locks handicap would otherwise pass unnoticed.
		/// </summary>
		internal static string VerifyOpponents(World world, SkirmishOpponent[] opponents)
		{
			var bots = world.Players.Where(p => p.IsBot && p.Playable).ToList();
			if (bots.Count != opponents.Length)
				return $"the new world has {bots.Count} of {opponents.Length} requested bots";

			var pool = new List<Player>(bots);
			foreach (var opponent in opponents)
			{
				var match = pool.FirstOrDefault(p => p.BotType == opponent.Bot && p.Handicap == opponent.Handicap);
				if (match == null)
				{
					var sameType = pool.FirstOrDefault(p => p.BotType == opponent.Bot);
					return sameType != null
						? $"the new world has bot '{opponent.Bot}' at handicap {sameType.Handicap}, not {opponent.Handicap}"
						: $"the new world has no bot of type '{opponent.Bot}'";
				}

				pool.Remove(match);
			}

			return null;
		}

		/// <summary>
		/// The load_skirmish reply: the new world's state plus the echo of what was started (plan §3.3). Must run on
		/// the game thread.
		/// </summary>
		public static LoadSkirmishV1 Reply(World world, SkirmishPlan plan)
		{
			var bots = world.Players.Where(p => p.IsBot && p.Playable).ToList();
			var pool = new List<Player>(bots);
			var placed = new List<SkirmishOpponentV1>(plan.Opponents.Length);
			foreach (var opponent in plan.Opponents)
			{
				var match = pool.FirstOrDefault(p => p.BotType == opponent.Bot && p.Handicap == opponent.Handicap)
					?? pool.FirstOrDefault();
				if (match != null)
					pool.Remove(match);

				var slot = match == null
					? null
					: world.LobbyInfo.Clients.FirstOrDefault(c => c.Index == match.ClientIndex)?.Slot;
				placed.Add(new SkirmishOpponentV1(match?.PlayerName, opponent.Bot, opponent.Handicap, slot));
			}

			var echo = new SkirmishV1(plan.MapUid, plan.MapRequested, plan.Speed, placed.ToArray());
			return LoadSkirmishV1.From(AgentStateDump.Snapshot(world), echo);
		}
	}
}
