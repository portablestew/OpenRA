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

namespace OpenRA
{
	/// <summary>
	/// The global opt-in flag for the Monte Carlo mode. Toggled by a hotkey and read by the in-game widgets.
	/// It is local and unsynced: it never becomes an order, so clients may differ on it without desyncing.
	/// The flag lives here, rather than with the mode's logic in OpenRA.Mods.Common, because Game resets it
	/// and OpenRA.Game cannot reference the mods.
	/// </summary>
	public static class MonteCarloMode
	{
		/// <summary>Whether the Monte Carlo mode is on. Starts off, and is switched off again on game start or join.</summary>
		public static bool Enabled { get; private set; }

		/// <summary>Flips the flag.</summary>
		public static void Toggle()
		{
			Enabled = !Enabled;
		}

		/// <summary>Switches the mode off. Called on game start and join, so a new game never inherits the flag.</summary>
		public static void Clear()
		{
			Enabled = false;
		}
	}
}
