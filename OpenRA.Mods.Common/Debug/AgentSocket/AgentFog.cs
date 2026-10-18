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
using OpenRA.Traits;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// The fog policy's rule (P2.2): an actor is known to an observer when it is visible through the shroud, when the observer holds
	/// a valid last-known FrozenUnderFog snapshot of it, or when the observer's satellite is granted and the actor shows a GPS dot.
	/// The first two come from the engine's fog API, Shroud and FrozenActorLayer. The third is the one special case: the engine's
	/// radar dots, read through the IAgentGpsGrant and IAgentGpsDot seam.
	/// </summary>
	/// <remarks>
	/// A snapshot counts only when it is valid: FrozenActor.IsValid is true once the snapshot has been refreshed from the actor, so an
	/// actor that was never seen has no last-known entry. The GPS branch follows GpsDotEffect.ShouldRender: the observer's grant, a
	/// non-allied owner, an explored cell, and no visibility modifier that hides the actor.
	/// </remarks>
	public static class AgentFog
	{
		/// <summary>True when observer knows actor: visible, a valid last-known snapshot, or a GPS dot the satellite draws.</summary>
		public static bool Known(Actor actor, Player observer)
		{
			if (actor == null || observer == null)
				return false;

			if (observer.Shroud.IsVisible(actor.CenterPosition))
				return true;

			var frozen = observer.PlayerActor.TraitOrDefault<FrozenActorLayer>()?.FromID(actor.ActorID);
			if (frozen is { IsValid: true })
				return true;

			return GpsDotShown(actor, observer);
		}

		/// <summary>The engine's radar-dot rule for an actor the observer cannot see, as GpsDotEffect.ShouldRender applies it.</summary>
		static bool GpsDotShown(Actor actor, Player observer)
		{
			if (actor.TraitOrDefault<IAgentGpsDot>() == null)
				return false;

			if (observer.PlayerActor.TraitOrDefault<IAgentGpsGrant>() is not { GrantsRadarDots: true })
				return false;

			var owner = actor.EffectiveOwner?.Owner;
			if (owner != null && observer.IsAlliedWith(owner))
				return false;

			if (!observer.Shroud.IsExplored(actor.CenterPosition))
				return false;

			return actor.TraitsImplementing<IVisibilityModifier>().All(m => m.IsVisible(actor, observer));
		}

		/// <summary>
		/// True when the agent reports actor to observer: the observer's own actors always, and any other actor only when Known.
		/// This one rule covers the state's units and every event that names an actor (P2.3, 8.4). Without an observer nothing is reported.
		/// </summary>
		public static bool Reported(Actor actor, Player observer)
		{
			return observer != null && (actor.Owner == observer || Known(actor, observer));
		}
	}
}
