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
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// Raises a power_seen event the first time the local player can see a support power's timer: the power has an instance (its
	/// structure exists) and AgentPowers.Shown is true for it. Tick runs each world tick from AgentWorldBridge.
	/// </summary>
	/// <remarks>
	/// Each power is seen once per world, per owner and key, so a timer that stays visible or re-reveals from fog raises nothing
	/// more. The rule is the engine's timer rule (DisplayTimerRelationships), not fog, so a power whose timer is hidden is never
	/// seen. The set belongs to one world, since actor ids restart in each world. Sightings are tracked even with no connection
	/// open, as AgentSpotted does.
	/// </remarks>
	public static class AgentPowerSeen
	{
		static World seenWorld;
		static readonly HashSet<(Player Owner, string Key)> Seen = [];

		/// <summary>Raises power_seen for each support power the local player newly sees. Does nothing while the socket is disabled.</summary>
		public static void Tick(World world)
		{
			if (!AgentEvents.Enabled)
				return;

			if (seenWorld != world)
			{
				seenWorld = world;
				Seen.Clear();
			}

			var viewer = world.LocalPlayer;
			if (viewer == null)
				return;

			foreach (var player in world.Players)
			{
				var manager = player.PlayerActor.TraitOrDefault<SupportPowerManager>();
				if (manager == null)
					continue;

				foreach (var (key, power) in manager.Powers)
				{
					if (power.Instances.Count == 0 || Seen.Contains((player, key)) || !AgentPowers.Shown(player, power.Info, viewer))
						continue;

					Seen.Add((player, key));
					AgentEvents.Raise(new PowerSeenV1("power_seen", world.WorldTick, player.PlayerName, key, power.Name,
						power.RemainingTicks, power.TotalTicks));
				}
			}
		}
	}
}
