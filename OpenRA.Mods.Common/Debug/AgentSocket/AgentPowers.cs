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
	// Schema v1, additive. Wire keys are snake_case (AgentSocketCommands.Json).

	/// <summary>
	/// The powers command: each support power the local player can see, with its charge. Powers lists the local player's own
	/// powers, and an enemy's only when its timer is shown to the local player (SupportPowerV1).
	/// </summary>
	public sealed record PowersV1(bool Ok, string Cmd, int Tick, SupportPowerV1[] Powers);

	/// <summary>
	/// One support power. Order is the key an order names, and it is listed among the player actor's orders. Owner is the player
	/// who holds it. Ready is true once the charge is complete and the power can be activated; it is then false again after
	/// activation. RemainingTicks counts down to zero while Active, and TotalTicks is the charge. Active is false while the power
	/// is paused, such as a silo that is powered down. Disabled is true when the power cannot be used at all.
	/// </summary>
	public sealed record SupportPowerV1(string Order, string Owner, string Name, string Description, bool Ready, int RemainingTicks,
		int TotalTicks, bool Active, bool Disabled, bool OneShot);

	/// <summary>Builds the powers reply, and holds the visibility rule that power_ready shares. Must run on the game thread.</summary>
	public static class AgentPowers
	{
		/// <summary>The powers the local player can see, in player order and then key order. Must run on the game thread.</summary>
		public static PowersV1 List(World world)
		{
			var viewer = world.LocalPlayer ?? throw new InvalidOperationException("There is no local player");
			var found = new List<SupportPowerV1>();
			foreach (var player in world.Players)
			{
				var manager = player.PlayerActor.TraitOrDefault<SupportPowerManager>();
				if (manager == null)
					continue;

				foreach (var (key, power) in manager.Powers.OrderBy(p => p.Key, StringComparer.Ordinal))
				{
					if (power.Instances.Count == 0 || !Shown(player, power.Info, viewer))
						continue;

					found.Add(new SupportPowerV1(key, player.PlayerName, power.Name, power.Description, power.Ready, power.RemainingTicks,
						power.TotalTicks, power.Active, power.Disabled, power.Info.OneShot));
				}
			}

			return new PowersV1(true, "powers", world.WorldTick, found.ToArray());
		}

		/// <summary>
		/// The engine's rule for who sees a power's timer (SupportPowerTimerWidget): the owner always, and anyone else only when the
		/// power's DisplayTimerRelationships includes their relationship with the owner. No local player sees nothing.
		/// </summary>
		internal static bool Shown(Player owner, SupportPowerInfo info, Player viewer)
		{
			return viewer != null && (owner == viewer || info.DisplayTimerRelationships.HasRelationship(owner.RelationshipWith(viewer)));
		}
	}
}
