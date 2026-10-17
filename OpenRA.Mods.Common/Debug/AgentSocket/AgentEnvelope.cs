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
using System.Linq;
using System.Text;
using System.Text.Json;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// The turn envelope (P1.16). Every response except help is wrapped here, once per request, so its keys live in one
	/// place. results is the result of an object request, or an array of results for an array request. events is empty
	/// until P3. state is the schema v1 digest, left out when the request sets digest:false. tick is the game clock after
	/// the request ran. map_state_png and map_state_tick are null until P2.
	/// </summary>
	public static class AgentEnvelope
	{
		/// <summary>
		/// Wraps one serialized result, or an array of them, after the request has run. The game thread is asked for the
		/// tick (and the digest, when wanted) only when a world is loaded, so a request with no world never waits on it.
		/// A read that fails leaves tick and state null rather than failing the request.
		/// </summary>
		public static string Wrap(string results, bool includeState)
		{
			var (tick, state) = Read(includeState);

			var text = new StringBuilder("{\"results\":").Append(results).Append(",\"events\":[]");
			if (includeState)
				text.Append(",\"state\":").Append(state ?? "null");

			text.Append(",\"tick\":").Append(tick.HasValue ? tick.Value.ToString() : "null")
				.Append(",\"map_state_png\":null,\"map_state_tick\":null}");
			return text.ToString();
		}

		/// <summary>An object request sets digest:false to drop state. Every other request keeps it.</summary>
		public static bool WantsState(JsonElement request)
		{
			return !(request.ValueKind == JsonValueKind.Object && request.TryGetProperty("digest", out var digest)
				&& digest.ValueKind == JsonValueKind.False);
		}

		/// <summary>An array request drops state only when every item sets digest:false. An empty array keeps it.</summary>
		public static bool WantsState(JsonElement[] items)
		{
			return items.Length == 0 || items.Any(WantsState);
		}

		static (int? Tick, string State) Read(bool includeState)
		{
			if (!AgentWorldBridge.Loaded)
				return (null, null);

			try
			{
				return AgentMarshal.ExecuteOnGameThread(() =>
				{
					var world = AgentWorldBridge.Active;
					if (world == null)
						return ((int?)null, (string)null);

					var state = includeState ? AgentSocketCommands.Serialize(AgentStateDump.Snapshot(world)) : null;
					return ((int?)world.WorldTick, state);
				});
			}
			catch (Exception)
			{
				return (null, null);
			}
		}
	}
}
