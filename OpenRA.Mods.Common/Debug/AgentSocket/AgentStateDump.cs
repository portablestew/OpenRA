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
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.AgentSocket
{
	// Schema v1. Wire keys are snake_case (AgentSocketCommands.Json). The envelope fields ok and cmd are part of the
	// state and actors lines, so every key a client sees is declared here.
	public sealed record StateV1(bool Ok, string Cmd, int Tick, int NetFrame, MapV1 Map, bool Paused, bool GameOver,
		string LocalPlayer, PlayerV1[] Players, UnitV1[] Units);

	public sealed record ActorsV1(bool Ok, string Cmd, UnitV1[] Units);

	public sealed record MapV1(string Uid, string Title);

	/// <summary>Resources are 0 for a player without a PlayerResources trait. Dead means WinState.Lost. Power is null without a PowerManager trait.</summary>
	public sealed record PlayerV1(string Name, string Faction, int Cash, int Resources, int ResourceCapacity, bool Dead, PowerV1 Power);

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

	/// <summary>Builds the schema v1 snapshot. Must run on the game thread: it reads live world state.</summary>
	public static class AgentStateDump
	{
		public static StateV1 Snapshot(World world)
		{
			return new StateV1(true, "state", world.WorldTick, world.NetFrameNumber,
				new MapV1(world.Map.Uid, world.Map.Title), world.Paused, world.IsGameOver,
				world.LocalPlayer?.PlayerName, world.Players.Select(PlayerState).ToArray(), Units(world, null, null, includeScenery: false));
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
		/// Scenery is what the world owns (the neutral player that OwnsWorld, which map trees and buildings belong to), or an mpspawn
		/// marker. Creeps are not scenery: they are NonCombatant but they fight, so they stay in the digest.
		/// </summary>
		static bool IsScenery(Actor a)
		{
			return a.Owner?.PlayerReference?.OwnsWorld == true || a.Info.Name == "mpspawn";
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

		static PlayerV1 PlayerState(Player p)
		{
			var resources = p.PlayerActor.TraitOrDefault<PlayerResources>();
			var power = p.PlayerActor.TraitOrDefault<PowerManager>();

			return new PlayerV1(p.PlayerName, p.Faction?.InternalName,
				resources?.Cash ?? 0, resources?.Resources ?? 0, resources?.ResourceCapacity ?? 0,
				p.WinState == WinState.Lost,
				Power(power));
		}

		/// <summary>The PowerManager totals and state, or null for a player without a PowerManager.</summary>
		public static PowerV1 Power(PowerManager power)
		{
			return power == null ? null : new PowerV1(power.PowerProvided, power.PowerDrained, power.ExcessPower, power.PowerState.ToString());
		}
	}
}
