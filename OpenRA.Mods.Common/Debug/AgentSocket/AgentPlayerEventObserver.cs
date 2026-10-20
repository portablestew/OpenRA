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

using OpenRA.Mods.Common.AgentSocket;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common
{
	public class AgentPlayerEventObserverInfo : TraitInfo<AgentPlayerEventObserver> { }

	/// <summary>
	/// Raises damaged and killed events for one player's units (D2, P4), and the player-level placed, power and
	/// defeated/game_over events. Health.InflictDamage notifies the owner's PlayerActor, so this trait sees each hit once,
	/// on the owner's side. Registered in the Player: block only. captured is raised from CaptureActor, because
	/// INotifyCapture is not dispatched to player actors. placed comes from PlaceBuilding, which notifies the player actor.
	/// The power notify carries no state, so this trait keeps the low-power state it last reported.
	/// </summary>
	public sealed class AgentPlayerEventObserver : INotifyDamage, INotifyKilled, INotifyBuildingPlaced, INotifyPowerLevelChanged,
		INotifyWinStateChanged
	{
		bool powerLow;

		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			// The threat list reads recent hits from the state, so a hit is recorded whether or not a connection is open (P2.7).
			if (e.Attacker != null)
				AgentRecentDamage.Record(self, e.Attacker);

			DamagedEvent(self, e);
		}

		static void DamagedEvent(Actor self, AttackInfo e)
		{
			if (!AgentEvents.Enabled || !AgentFog.Reported(self, self.World.LocalPlayer))
				return;

			var health = self.TraitOrDefault<Health>();
			var attacker = e.Attacker;

			// The fog policy (P2.3): the attacker is named only when the local player knows it (AgentFog). Its bearing is always
			// given, from the damaged actor (AgentBearing). That actor is the friendly reference when it is the local player's.
			var known = attacker != null && AgentFog.Known(attacker, self.World.LocalPlayer);
			var attackerId = known ? (uint?)attacker.ActorID : null;
			var attackerType = known ? attacker.Info.Name : null;
			var attackerOwner = known ? attacker.Owner.PlayerName : null;
			var shrouded = attacker != null && !known;
			var bearing = attacker == null ? null : AgentBearing.Of(self.CenterPosition, attacker.CenterPosition);

			AgentEvents.Raise(new DamagedV1("damaged", self.World.WorldTick, self.ActorID,
				self.Info.Name, self.Owner.PlayerName, attackerId, attackerType, attackerOwner,
				e.Damage.Value, health?.HP ?? 0, health?.MaxHP ?? 0, e.DamageState.ToString(), bearing, shrouded));
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			// A kill is sent when the local player reported the victim before it died (AgentFog), which includes its own units. The
			// attacker is named only when the local player reports it too.
			var observer = self.World.LocalPlayer;
			if (!AgentEvents.Enabled || !AgentFog.Reported(self, observer))
				return;

			var attacker = e.Attacker;
			// A removed attacker has no live state to check, so it is not named.
			var attackerKnown = attacker != null && attacker.IsInWorld && AgentFog.Reported(attacker, observer);
			var cost = self.Info.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0;
			AgentEvents.Raise(new KilledV1("killed", self.World.WorldTick, self.ActorID,
				self.Info.Name, self.Owner.PlayerName, attackerKnown ? attacker.ActorID : null,
				attackerKnown ? attacker.Info.Name : null, attackerKnown ? attacker.Owner.PlayerName : null, cost));
		}

		void INotifyBuildingPlaced.BuildingPlaced(Actor self, Actor building)
		{
			// Resolves the socket's pending place await, so its reply can name the building (F6). Runs without a connection.
			AgentOrderAwait.Placed(self, building.ActorID);

			if (!AgentEvents.Enabled || !AgentFog.Reported(building, building.World.LocalPlayer))
				return;

			AgentEvents.Raise(new PlacedV1("placed", building.World.WorldTick,
				building.ActorID, building.Info.Name, building.Owner.PlayerName, [building.Location.X, building.Location.Y]));
		}

		void INotifyPowerLevelChanged.PowerLevelChanged(Actor self)
		{
			// The notify fires on each flip of low power, and once when the world is created. It is reported only when the
			// low state differs from the last one, and the state is kept even with no connection, so a later connection
			// sees the correct transition.
			var power = self.TraitOrDefault<PowerManager>();
			if (power == null)
				return;

			var low = power.PowerState != PowerState.Normal;
			if (low == powerLow)
				return;

			powerLow = low;
			if (!AgentEvents.Enabled)
				return;

			AgentEvents.Raise(new PowerEventV1(low ? "power_low" : "power_restored", self.World.WorldTick,
				self.Owner.PlayerName, power.PowerProvided, power.PowerDrained, power.ExcessPower, power.PowerState.ToString()));
		}

		void INotifyWinStateChanged.OnPlayerWon(Player player)
		{
			if (!AgentEvents.Enabled)
				return;

			AgentEvents.Raise(new GameOverV1("game_over", player.World.WorldTick, player.PlayerName));
		}

		void INotifyWinStateChanged.OnPlayerLost(Player player)
		{
			if (!AgentEvents.Enabled)
				return;

			AgentEvents.Raise(new DefeatedV1("defeated", player.World.WorldTick, player.PlayerName));
		}

		/// <summary>
		/// Raises a sold event for an actor its owner sold (3.4). Sell calls it before the actor is disposed, so the actor is still in
		/// the world. It resolves the socket's pending sell await first, so that reply reports sold. Runs without a connection.
		/// </summary>
		public static void Sold(Actor sold)
		{
			AgentOrderAwait.Sold(sold);

			if (!AgentEvents.Enabled || !AgentFog.Reported(sold, sold.World.LocalPlayer))
				return;

			AgentEvents.Raise(new SoldV1("sold", sold.World.WorldTick,
				sold.ActorID, sold.Info.Name, sold.Owner.PlayerName, [sold.Location.X, sold.Location.Y]));
		}

		/// <summary>Raises a captured event for an actor that changed owner. Called from CaptureActor behind the same guard.</summary>
		public static void Captured(Actor captured, Actor captor, Player oldOwner, Player newOwner)
		{
			// The captured actor must be one the local player reports (AgentFog); the captor is named only when it is too.
			var observer = captured.World.LocalPlayer;
			if (!AgentFog.Reported(captured, observer))
				return;

			AgentEvents.Raise(new CapturedV1("captured", captured.World.WorldTick,
				captured.ActorID, captured.Info.Name, oldOwner.PlayerName, newOwner.PlayerName,
				AgentFog.Reported(captor, observer) ? captor.ActorID : null));
		}
	}
}
