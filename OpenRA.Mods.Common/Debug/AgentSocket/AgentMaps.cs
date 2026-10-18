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

namespace OpenRA.Mods.Common.AgentSocket
{
	// Schema v1. Wire keys are snake_case (AgentSocketCommands.Json); the maps entries reuse MapV1 (uid, title).
	public sealed record MapsV1(bool Ok, string Cmd, MapV1[] Maps);

	/// <summary>Lists the maps a player can pick in the lobby, so a client can choose one for load_skirmish.</summary>
	public static class AgentMaps
	{
		public static MapsV1 List()
		{
			var maps = Game.ModData.MapCache
				.Where(m => m.Status == MapStatus.Available && m.Visibility.HasFlag(MapVisibility.Lobby))
				.OrderBy(m => m.Title)
				.ThenBy(m => m.Uid)
				.Select(m => new MapV1(m.Uid, m.Title))
				.ToArray();

			return new MapsV1(true, "maps", maps);
		}
	}
}
