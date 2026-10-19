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
using System.Text;
using System.Text.Json;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// The turn envelope (P1.16). Every response except help is wrapped here, once per request, so its keys live in one
	/// place. results is the result of an object request, or an array of results for an array request. events is the turn's events for this
	/// connection, flushed once per request (P3.3). state is the schema v1 digest, left out when the request sets digest:false. tick is the game clock after
	/// the request ran. The map keys name the map-state image (P2.13): map_state_png is its workspace path, map_state_tick
	/// the tick it was rendered at, map_state_render "ok" or "unavailable", and map_px_per_cell, map_origin and map_size its
	/// geometry. They are left out when the request sets digest:false.
	/// </summary>
	public static class AgentEnvelope
	{
		/// <summary>
		/// Wraps one serialized result, or an array of them, after the request has run. The game thread is asked for the
		/// tick (and the digest and map image, when wanted) only when a world is loaded, so a request with no world never
		/// waits on it. A read that fails leaves tick and state null rather than failing the request; a render that fails
		/// reports map_state_render "unavailable" and does not fail the request either. events is the turn's events, already
		/// flushed and aggregated by the caller; null, or no events, writes an empty array.
		/// </summary>
		public static string Wrap(string results, bool includeState, IReadOnlyList<object> events = null, bool initial = false,
			AgentConnection connection = null)
		{
			// A connection's first envelope always carries the state in full, whatever digest the request set (P3.5).
			includeState |= initial;

			// The static data (C) rides with the state until the connection has been sent it. A null connection always gets it.
			var statics = connection == null || !connection.StaticsSent;
			var (tick, state, map) = Read(includeState, statics);
			if (statics && state != null && connection != null)
				connection.StaticsSent = true;

			var text = new StringBuilder("{\"results\":").Append(results).Append(",\"events\":")
				.Append(AgentSocketCommands.Serialize(events ?? []));
			if (includeState)
				text.Append(",\"state\":").Append(state ?? "null");

			text.Append(",\"tick\":").Append(tick.HasValue ? tick.Value.ToString() : "null");
			if (includeState)
				AppendMap(text, map);

			if (initial)
				text.Append(",\"initial\":true");

			return text.Append('}').ToString();
		}

		/// <summary>Appends the map keys. The tick is null when there is no image to name, so the tick always matches the file.</summary>
		static void AppendMap(StringBuilder text, MapStateV1 map)
		{
			var named = map?.Path != null;
			text.Append(",\"map_state_png\":").Append(AgentSocketCommands.Serialize(map?.Path))
				.Append(",\"map_state_tick\":").Append(AgentSocketCommands.Serialize(named ? (int?)map.Tick : null))
				.Append(",\"map_state_render\":").Append(AgentSocketCommands.Serialize(map?.Render ?? "unavailable"))
				.Append(",\"map_px_per_cell\":").Append(AgentSocketCommands.Serialize(map == null ? null : (int?)map.MapPxPerCell))
				.Append(",\"map_origin\":").Append(AgentSocketCommands.Serialize(map?.Origin))
				.Append(",\"map_size\":").Append(AgentSocketCommands.Serialize(map?.Size));
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

		static (int? Tick, string State, MapStateV1 Map) Read(bool includeState, bool statics)
		{
			if (!AgentWorldBridge.Loaded)
				return (null, null, null);

			try
			{
				return AgentMarshal.ExecuteOnGameThread(() =>
				{
					var world = AgentWorldBridge.Active;
					if (world == null)
						return ((int?)null, (string)null, (MapStateV1)null);

					var state = includeState ? AgentSocketCommands.Serialize(AgentStateDump.Snapshot(world, statics)) : null;
					return ((int?)world.WorldTick, state, includeState ? RefreshMap(world) : null);
				});
			}
			catch (Exception)
			{
				return (null, null, null);
			}
		}

		/// <summary>The map image as of this tick. A render that throws leaves it null, which the envelope reports as unavailable.</summary>
		static MapStateV1 RefreshMap(World world)
		{
			try
			{
				return AgentMapState.Refresh(world, world.LocalPlayer);
			}
			catch (Exception)
			{
				return null;
			}
		}
	}
}
