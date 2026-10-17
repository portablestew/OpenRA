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

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// The workspace the agent socket writes files into (P2.11, Q8). The root is the directory the game was started from, so the
	/// agent's workspace is where its files land and the path it is given is inside that workspace. Tests set the root to a directory
	/// of their own. The same root serves map-state.png and, later, screenshots.
	/// </summary>
	public static class AgentWorkspace
	{
		static string root;

		/// <summary>The directory files are written into. Defaults to the game's working directory when no root is set.</summary>
		public static string Root
		{
			get => root ?? Environment.CurrentDirectory;
			set => root = value;
		}
	}
}
