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

namespace OpenRA.Mods.Common.MonteCarlo
{
	/// <summary>The three states the Monte Carlo mode can be in, as the label shows them.</summary>
	public enum MonteCarloState
	{
		/// <summary>The mode is switched off.</summary>
		Off,

		/// <summary>The mode is on, but the selection is not exactly one eligible unit.</summary>
		Idle,

		/// <summary>The mode is on and the selection is one eligible mobile unit.</summary>
		Active,
	}

	/// <summary>
	/// Decides which unit, if any, the Monte Carlo mode steers. The target is the single selected actor, and it
	/// must be in the world, alive, owned by the local player and mobile (it has an <see cref="IMoveInfo"/> trait).
	/// Nothing here changes the world, so it is safe to call every frame from the UI.
	/// </summary>
	public static class MonteCarloTarget
	{
		/// <summary>Returns the state for the world's current selection, and the target when the state is Active.</summary>
		public static MonteCarloState Resolve(World world, out Actor target)
		{
			target = null;
			if (!MonteCarloMode.Enabled)
				return MonteCarloState.Off;

			if (world.LocalPlayer == null || world.Selection.Actors.Count != 1)
				return MonteCarloState.Idle;

			var actor = world.Selection.Actors.Single();
			if (actor.Owner != world.LocalPlayer || !actor.IsInWorld || actor.IsDead || !actor.Info.HasTraitInfo<IMoveInfo>())
				return MonteCarloState.Idle;

			target = actor;
			return MonteCarloState.Active;
		}
	}
}
