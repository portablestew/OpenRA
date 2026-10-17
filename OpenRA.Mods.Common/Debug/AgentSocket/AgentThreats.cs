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
	/// <summary>
	/// One known enemy in the state's threats (P2.7). Cell is the top-left cell. AttackTarget is the friendly this threat hit most
	/// recently, or null. Heading is the 8-way direction the threat faces. Bearing is the 8-way direction from the closest friendly
	/// toward the threat. FriendlyId and Distance name that closest friendly, and Distance is in world units (1024 per cell).
	/// </summary>
	public sealed record ThreatV1(uint Id, string Type, int[] Cell, int? Hp, uint? AttackTarget, string Heading, string Bearing,
		uint FriendlyId, int Distance);

	/// <summary>
	/// The threat list (P2.7): the top known enemies by standoff, at most three of each type and fifteen in all. Standoff is the least,
	/// over the local player's armed friendlies, of the distance less that friendly's weapon range against the threat, so an artillery
	/// unit in range scores lower than a nearer short-range one. A threat with no weapon against any friendly has no standoff and sorts
	/// last. A friendly that a threat hit recently is bumped to the front (AgentRecentDamage). Known enemies come from AgentFog.Known.
	/// </summary>
	public static class AgentThreats
	{
		/// <summary>The most entries the list holds.</summary>
		public const int Limit = 15;

		/// <summary>The most entries of one type the list holds.</summary>
		public const int PerType = 3;

		/// <summary>The threats the observer knows, in list order. Empty without an observer or a friendly.</summary>
		public static ThreatV1[] Build(World world, Player observer)
		{
			if (observer == null)
				return [];

			var friendlies = world.Actors
				.Where(a => a.Owner == observer && a.OccupiesSpace != null && !a.IsDead)
				.ToArray();
			if (friendlies.Length == 0)
				return [];

			var threats = world.Actors
				.Where(a => a.OccupiesSpace != null && !a.IsDead && a.Owner != null && !observer.IsAlliedWith(a.Owner)
					&& !AgentStateDump.IsScenery(a) && AgentFog.Known(a, observer))
				.ToArray();

			// The range of a threat type against a friendly is cached for this list, so each pair is measured once.
			var ranges = new Dictionary<(string Type, uint Friendly), int>();
			int RangeOf(Actor threat, Actor friendly)
			{
				if (!ranges.TryGetValue((threat.Info.Name, friendly.ActorID), out var range))
				{
					range = 0;
					foreach (var attack in threat.TraitsImplementing<AttackBase>())
						if (!attack.IsTraitDisabled)
							range = Math.Max(range, attack.GetMaximumRangeVersusTarget(Target.FromActor(friendly)).Length);

					ranges[(threat.Info.Name, friendly.ActorID)] = range;
				}

				return range;
			}

			var scored = threats.Select(threat =>
			{
				var nearest = friendlies.MinBy(f => Distance(f, threat));
				var distance = Distance(nearest, threat);

				// Standoff counts only the friendlies this threat can hit: those inside its weapon range, not every friendly.
				var armed = friendlies.Where(f => RangeOf(threat, f) > 0).ToArray();
				var standoff = armed.Length == 0 ? long.MaxValue : armed.Min(f => (long)Distance(f, threat) - RangeOf(threat, f));

				var recent = AgentRecentDamage.RecentVictim(world, threat.ActorID);
				var bump = recent != null && friendlies.Any(f => f.ActorID == recent);

				return (Threat: threat, Nearest: nearest, Distance: distance, Standoff: standoff, Bump: bump, Target: recent);
			})
			.OrderByDescending(s => s.Bump)
			.ThenBy(s => s.Standoff)
			.ThenBy(s => s.Distance)
			.ThenBy(s => s.Threat.ActorID);

			var perType = new Dictionary<string, int>();
			var list = new List<ThreatV1>();
			foreach (var s in scored)
			{
				if (list.Count == Limit)
					break;

				var type = s.Threat.Info.Name;
				if (perType.GetValueOrDefault(type) == PerType)
					continue;

				perType[type] = perType.GetValueOrDefault(type) + 1;
				list.Add(Entry(s.Threat, s.Nearest, s.Distance, s.Bump ? s.Target : null));
			}

			return list.ToArray();
		}

		static ThreatV1 Entry(Actor threat, Actor nearest, int distance, uint? attackTarget)
		{
			return new ThreatV1(threat.ActorID, threat.Info.Name, [threat.Location.X, threat.Location.Y],
				threat.TraitOrDefault<IHealth>()?.HP, attackTarget, Heading(threat), AgentBearing.Of(nearest.CenterPosition, threat.CenterPosition),
				nearest.ActorID, distance);
		}

		static int Distance(Actor from, Actor to)
		{
			return (to.CenterPosition - from.CenterPosition).Length;
		}

		/// <summary>
		/// The 8-way direction a threat faces, on map axes. The forward vector is -(sin, cos) of its facing, as aircraft landing uses it, so
		/// facing 0 is North and facing 768 is East. Null for an actor with no facing.
		/// </summary>
		static string Heading(Actor threat)
		{
			var facing = threat.TraitOrDefault<IFacing>();
			if (facing == null)
				return null;

			var angle = facing.Facing;
			return AgentBearing.Vector(-(long)angle.Sin(), -(long)angle.Cos());
		}
	}
}
