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
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.AgentSocket
{
	// Schema v1. Wire keys are snake_case (AgentSocketCommands.Json). The envelope fields ok and cmd are part of the
	// state and actors lines, so every key a client sees is declared here.
	public sealed record StateV1(bool Ok, string Cmd, int Tick, int NetFrame, StateMapV1 Map, bool Paused, bool GameOver,
		string LocalPlayer, PlayerV1[] Players, object[] Units, ThreatV1[] Threats, uint[] Selection);

	/// <summary>
	/// The state command's own reply (9.3): a refresh ack. The digest is the envelope's state, which every non-help reply
	/// carries, so this result does not repeat it. Tick is the game clock, as in every other ack.
	/// </summary>
	public sealed record StateAckV1(bool Ok, string Cmd, int Tick);

	public sealed record ActorsV1(bool Ok, string Cmd, UnitV1[] Units);

	public sealed record MapV1(string Uid, string Title);

	/// <summary>
	/// The state's map (P2.6). Bounds is Map.Bounds as [x, y, width, height]. Spawns has one entry per player that has a home cell.
	/// The maps command keeps the uid and title only (MapV1). The map is static (C): null in an envelope that does not carry it.
	/// </summary>
	public sealed record StateMapV1(string Uid, string Title, int[] Bounds, SpawnV1[] Spawns);

	/// <summary>A player's home cell (P2.6): Cell is the top-left cell of Player's HomeLocation.</summary>
	public sealed record SpawnV1(string Player, int[] Cell);

	/// <summary>
	/// Cash, Resources and ResourceCapacity are null for a player who is not allied with the local player: their economy is hidden,
	/// as is Power. For an allied player, Resources is 0 without a PlayerResources trait. Dead means WinState.Lost. Power is null
	/// without a PowerManager trait. KnownCounts is the number of each type the local player knows of this player's units (P2.5):
	/// all of them for the local player, and for another player only those AgentFog says are known. Null when there is no local
	/// player. Bot is the AI type of a bot-owned player and null for a human, so a later envelope proves which opponents
	/// load_skirmish started. Handicap is the player's handicap percent (Player.Handicap, 0 for a player without one).
	/// Faction, Bot and Handicap are static (C): null in an envelope that does not carry the static data, which is every envelope
	/// after a connection's first one, until a world change. Name, the economy and the counts are always sent.
	/// </summary>
	public sealed record PlayerV1(string Name, string Faction, int? Cash, int? Resources, int? ResourceCapacity, bool Dead, PowerV1 Power,
		Dictionary<string, int> KnownCounts, string Bot, int? Handicap);

	/// <summary>
	/// Provided and Drained are the PowerManager totals. Excess is Provided - Drained. State is the PowerState name:
	/// Normal when Provided is at least Drained, Low when Provided is above half of Drained, otherwise Critical.
	/// </summary>
	public sealed record PowerV1(int Provided, int Drained, int Excess, string State);

	/// <summary>
	/// Cell is the top-left cell. Pos is the centre in world units (1024 per cell). Facing is WAngle.Angle (1024 per turn).
	/// Hp, MaxHp, Damage are null for an actor without IHealth. Activity is the class name of the current activity, or null.
	/// </summary>
	public sealed record UnitV1(uint Id, string Type, string Owner, int[] Cell, int[] Pos, int? Facing, int? Hp, int? MaxHp,
		string Damage, bool Idle, string Activity);

	/// <summary>
	/// An own structure in the state units (P2.5): only the keys the agent needs to address it and see its health. Cell is the
	/// top-left cell. Hp is null for a structure without IHealth. The full UnitV1 entry is kept for every other unit.
	/// </summary>
	public sealed record StructureV1(uint Id, string Type, int[] Cell, int? Hp);

	/// <summary>Builds the schema v1 snapshot. Must run on the game thread: it reads live world state.</summary>
	public static class AgentStateDump
	{
		/// <summary>
		/// The snapshot. statics is whether it carries the static data (C): the map, and each player's faction, bot and handicap.
		/// Without it those come out null. A null connection always asks for them.
		/// </summary>
		public static StateV1 Snapshot(World world, bool statics = true)
		{
			return new StateV1(true, "state", world.WorldTick, world.NetFrameNumber,
				statics ? StateMap(world) : null, world.Paused, world.IsGameOver,
				world.LocalPlayer?.PlayerName, world.Players.Select(p => PlayerState(p, world.LocalPlayer, statics)).ToArray(),
				StateUnits(world), AgentThreats.Build(world, world.LocalPlayer), Selected(world));
		}

		/// <summary>
		/// The state command's ack (9.3). The digest follows in the envelope's state, so the reply carries it once, not
		/// twice. Must run on the game thread: it reads the game clock.
		/// </summary>
		public static StateAckV1 Ack(World world)
		{
			return new StateAckV1(true, "state", world.WorldTick);
		}

		/// <summary>The ids of the actors the world has selected, in ActorID order. Empty when nothing is selected.</summary>
		static uint[] Selected(World world)
		{
			return world.Selection.Actors.Where(a => !a.IsDead).Select(a => a.ActorID).Order().ToArray();
		}

		/// <summary>The state's map: uid, title, Map.Bounds, and each player's home cell (P2.6).</summary>
		static StateMapV1 StateMap(World world)
		{
			var bounds = world.Map.Bounds;
			var spawns = world.Players.Where(p => p.HomeLocation != CPos.Zero)
				.Select(p => new SpawnV1(p.PlayerName, [p.HomeLocation.X, p.HomeLocation.Y]))
				.ToArray();

			return new StateMapV1(world.Map.Uid, world.Map.Title, [bounds.X, bounds.Y, bounds.Width, bounds.Height], spawns);
		}

		public static ActorsV1 Actors(World world, string owner, string type)
		{
			return new ActorsV1(true, "actors", Units(world, owner, type, includeScenery: true));
		}

		/// <summary>
		/// Live, non-disposed world actors that occupy space, in ActorID order. Null filters match everything. The state digest
		/// leaves out scenery (P2.4): the world's own actors and spawn markers. The actors command keeps them, since it filters by
		/// owner and type, so a request for the neutral owner still finds its actors.
		/// </summary>
		static UnitV1[] Units(World world, string owner, string type, bool includeScenery)
		{
			return world.Actors
				.Where(a => a.OccupiesSpace != null && !a.IsDead && (owner == null || a.Owner?.PlayerName == owner)
					&& (type == null || a.Info.Name == type) && (includeScenery || !IsScenery(a)))
				.OrderBy(a => a.ActorID)
				.Select(UnitState)
				.ToArray();
		}

		/// <summary>
		/// Scenery is what the world owns (the neutral player that OwnsWorld) and that is not a unit (IsUnit), or an mpspawn marker.
		/// Neutral civilians are units, so they are not scenery. Creeps are not scenery either: they are not owned by the world.
		/// </summary>
		internal static bool IsScenery(Actor a)
		{
			return a.Info.Name == "mpspawn" || (a.Owner?.PlayerReference?.OwnsWorld == true && !IsUnit(a));
		}

		/// <summary>
		/// A unit moves or fights: it has Mobile (infantry, civilians, vehicles) or Armament (armed civilians, turrets). Trees, husks
		/// and map props have neither, so they stay scenery (8.6).
		/// </summary>
		static bool IsUnit(Actor a)
		{
			return a.Info.HasTraitInfo<MobileInfo>() || a.Info.HasTraitInfo<ArmamentInfo>();
		}

		/// <summary>
		/// The state's units (P2.5, fog policy): the occupying, non-scenery actors the local player owns or knows (IsInState), in
		/// ActorID order. The local player's structures are slim (StructureV1); every other unit keeps the full UnitV1 entry.
		/// </summary>
		static object[] StateUnits(World world)
		{
			var observer = world.LocalPlayer;
			return world.Actors
				.Where(a => a.OccupiesSpace != null && !a.IsDead && !IsScenery(a) && IsInState(a, observer))
				.OrderBy(a => a.ActorID)
				.Select(a => IsOwnStructure(a, observer) ? (object)StructureState(a) : UnitState(a))
				.ToArray();
		}

		/// <summary>
		/// The fog policy for the state's units (P2.7 defers enemy detail to the threat list): the local player's own actors always
		/// appear, and every other actor only when AgentFog says the local player knows it. Without a local player nothing appears.
		/// </summary>
		static bool IsInState(Actor a, Player observer)
		{
			return AgentFog.Reported(a, observer);
		}

		static bool IsOwnStructure(Actor a, Player observer)
		{
			return observer != null && a.Owner == observer && a.Info.HasTraitInfo<BuildingInfo>();
		}

		static StructureV1 StructureState(Actor a)
		{
			return new StructureV1(a.ActorID, a.Info.Name, [a.Location.X, a.Location.Y], a.TraitOrDefault<IHealth>()?.HP);
		}

		/// <summary>
		/// The number of each type the observer knows of a player's units (P2.5), by type. The observer's own units all count. Another
		/// player's count only when AgentFog says the observer knows the unit: visible, a valid last-known snapshot, or a GPS dot.
		/// Scenery is left out, as in the units list. Null without an observer.
		/// </summary>
		static Dictionary<string, int> KnownCounts(Player player, Player observer)
		{
			if (observer == null)
				return null;

			return player.World.Actors
				.Where(a => a.Owner == player && a.OccupiesSpace != null && !a.IsDead && !IsScenery(a)
					&& (player == observer || AgentFog.Known(a, observer)))
				.GroupBy(a => a.Info.Name)
				.OrderBy(g => g.Key, StringComparer.Ordinal)
				.ToDictionary(g => g.Key, g => g.Count());
		}

		static UnitV1 UnitState(Actor a)
		{
			var health = a.TraitOrDefault<IHealth>();
			var facing = a.TraitOrDefault<IFacing>();
			var pos = a.CenterPosition;

			return new UnitV1(a.ActorID, a.Info.Name, a.Owner?.PlayerName,
				[a.Location.X, a.Location.Y], [pos.X, pos.Y, pos.Z],
				facing?.Facing.Angle, health?.HP, health?.MaxHP, health?.DamageState.ToString(),
				a.IsIdle, a.CurrentActivity?.GetType().Name);
		}

		static PlayerV1 PlayerState(Player p, Player observer, bool statics)
		{
			// The economy is hidden from every player who is not allied with the observer, and so is the power.
			var own = observer != null && observer.IsAlliedWith(p);
			var resources = own ? p.PlayerActor.TraitOrDefault<PlayerResources>() : null;
			var power = own ? p.PlayerActor.TraitOrDefault<PowerManager>() : null;

			return new PlayerV1(p.PlayerName, statics ? p.Faction?.InternalName : null,
				own ? resources?.Cash ?? 0 : null, own ? resources?.Resources ?? 0 : null, own ? resources?.ResourceCapacity ?? 0 : null,
				p.WinState == WinState.Lost,
				Power(power), KnownCounts(p, observer), statics ? p.BotType : null, statics ? p.Handicap : null);
		}

		/// <summary>The PowerManager totals and state, or null for a player without a PowerManager.</summary>
		public static PowerV1 Power(PowerManager power)
		{
			return power == null ? null : new PowerV1(power.PowerProvided, power.PowerDrained, power.ExcessPower, power.PowerState.ToString());
		}
	}
}
