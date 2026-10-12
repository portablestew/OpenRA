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
	/// Raises damaged and killed events for one player's units (D2, P4). Health.InflictDamage notifies the owner's
	/// PlayerActor, so this trait sees each hit once, on the owner's side. Registered in the Player: block only.
	/// captured is raised from CaptureActor, because INotifyCapture is not dispatched to player actors.
	/// </summary>
	public sealed class AgentPlayerEventObserver : INotifyDamage, INotifyKilled
	{
		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			if (!AgentEvents.HasSubscribers)
				return;

			var health = self.TraitOrDefault<Health>();
			AgentEvents.Raise("damage", self.Owner.PlayerName, new DamagedV1("damaged", self.World.WorldTick, self.ActorID,
				self.Info.Name, self.Owner.PlayerName, e.Attacker?.ActorID, e.Attacker?.Info.Name, e.Attacker?.Owner.PlayerName,
				e.Damage.Value, health?.HP ?? 0, health?.MaxHP ?? 0, e.DamageState.ToString()));
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			if (!AgentEvents.HasSubscribers)
				return;

			var cost = self.Info.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0;
			AgentEvents.Raise("killed", self.Owner.PlayerName, new KilledV1("killed", self.World.WorldTick, self.ActorID,
				self.Info.Name, self.Owner.PlayerName, e.Attacker?.ActorID, e.Attacker?.Info.Name, e.Attacker?.Owner.PlayerName,
				cost));
		}

		/// <summary>Raises a captured event for an actor that changed owner. Called from CaptureActor behind the same guard.</summary>
		public static void Captured(Actor captured, Actor captor, Player oldOwner, Player newOwner)
		{
			AgentEvents.Raise("captured", newOwner.PlayerName, new CapturedV1("captured", captured.World.WorldTick,
				captured.ActorID, captured.Info.Name, oldOwner.PlayerName, newOwner.PlayerName, captor.ActorID));
		}
	}
}
