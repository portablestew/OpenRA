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

using System.Collections.Generic;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// Raises a spotted event the first time the local player comes to know an enemy actor (AgentFog.Known): visible, a valid
	/// last-known snapshot, or a GPS dot. Tick runs each world tick from AgentWorldBridge.
	/// </summary>
	/// <remarks>
	/// Each actor is spotted once per world. A re-reveal from fog is not a new sighting, because the actor stays in the seen set.
	/// Scenery and husks are never spotted. Sightings are tracked even with no connection open, as the healed record is, so a
	/// later connection does not get a sighting it could not have seen. The set belongs to one world, since actor ids restart in
	/// each world. The scan costs one set lookup per enemy actor after its first sighting, and runs on every tick.
	/// </remarks>
	public static class AgentSpotted
	{
		static World seenWorld;
		static readonly HashSet<uint> Seen = [];

		/// <summary>Raises spotted for each enemy actor the local player newly knows. Does nothing while the socket is disabled.</summary>
		public static void Tick(World world)
		{
			if (!AgentEvents.Enabled)
				return;

			if (seenWorld != world)
			{
				seenWorld = world;
				Seen.Clear();
			}

			var observer = world.LocalPlayer;
			if (observer == null)
				return;

			foreach (var actor in world.Actors)
			{
				if (actor.Owner == null || Seen.Contains(actor.ActorID) || observer.RelationshipWith(actor.Owner) != PlayerRelationship.Enemy)
					continue;

				if (AgentStateDump.IsScenery(actor) || AgentStateDump.IsHusk(actor) || !AgentFog.Known(actor, observer))
					continue;

				Seen.Add(actor.ActorID);
				AgentEvents.Raise(new SpottedV1("spotted", world.WorldTick, actor.ActorID, actor.Info.Name, actor.Owner.PlayerName,
					[actor.Location.X, actor.Location.Y]));
			}
		}
	}
}
