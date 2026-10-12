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
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// Builds the banner each connection receives, and the help command's text (M11). Event payload fields are read from
	/// the DTO records, so the banner lists the keys the hub serializes. The client reads the banner up to its first blank
	/// line, so the text has no blank line except the last one.
	/// </summary>
	public static class AgentBanner
	{
		const string OverflowEvent = "overflow";

		/// <summary>The commands the dispatcher answers, with their request keys and response keys.</summary>
		static readonly (string Name, string Summary)[] Commands =
		[
			("ping", "-> tick"),
			("help", "-> banner (this text, as one string)"),
			("state", "-> tick, net_frame, map, paused, game_over, local_player, players, units"),
			("actors", "owner, type (optional filters) -> units"),
			("maps", "-> maps"),
			("load_map", "map (string, uid or title) -> the state shape, once the new world is ready"),
			("order", "actor (uint); order (string); at most one of cell [x,y], pos [x,y,z], target_actor (uint);"
				+ " optional queued (bool)\n                -> tick, min_effect_tick"),
			("subscribe", "events (non-empty array of group names); optional owner (player name) -> subscribed\n"
				+ "                owner filters only the groups named in this request; omit it to receive every owner's events."
				+ " A later subscribe replaces a group's filter"),
			("unsubscribe", "events (array), or all:true -> subscribed"),
		];

		/// <summary>Each event group, the event type it carries, and the record that defines the payload.</summary>
		static readonly (string Group, string Event, Type Payload)[] Events =
		[
			("damage", "damaged", typeof(DamagedV1)),
			("killed", "killed", typeof(KilledV1)),
			("captured", "captured", typeof(CapturedV1)),
			("orders", "order", typeof(OrderEventV1)),
			("production", "produced", typeof(ProducedV1)),
			("stance", "stance", typeof(StanceV1)),
			("path", "path", typeof(PathV1)),
			("move_result", "move_result", typeof(MoveResultV1)),
		];

		/// <summary>The command names, in banner order.</summary>
		public static string[] CommandNames => Commands.Select(c => c.Name).ToArray();

		/// <summary>The event types, one per group, in banner order. The overflow marker is not a group and is listed separately.</summary>
		public static string[] EventTypes => Events.Select(e => e.Event).Append(OverflowEvent).ToArray();

		public static string Build()
		{
			var text = new StringBuilder();
			text.Append("OpenRA agent debug socket, schema v1. Send one JSON object per line; one response line per command.\n");
			text.Append("Responses echo cmd and carry ok. Errors carry ok:false and error.\n");
			text.Append("Commands (request keys, then response keys):\n");
			foreach (var (name, summary) in Commands)
				Row(text, name, 11, summary);

			text.Append("Event groups, for subscribe. Each event is one line; its event key names its type:\n");
			foreach (var (group, type, payload) in Events)
				Row(text, group, 12, $"{type}: {Fields(payload)}");

			Row(text, "(any)", 12, $"{OverflowEvent}: {Fields(typeof(OverflowV1))}");
			Row(text, "path", 12, $"metrics (in path events): {Fields(typeof(PathMetrics))}");

			text.Append("Notes:\n");
			text.Append("  order min_effect_tick is a lower bound (tick + 1). The order applies on or after it; network frame delay can make it later.\n");
			text.Append("  The order event's tick and effect_tick are the same value: the tick the order resolved on.\n");
			text.Append("  An overflow event is sent before the events that follow it. dropped counts the events a full queue discarded.\n");
			text.Append("  path.to is the cell the path actually ends on. It can differ from the cell you ordered.\n");
			text.Append("  path with result no_path has no cells. metrics is null when no path was found or the path was rejected.\n");
			text.Append("  move_result.remaining_cells is 0 on canceled moves, because a cancel clears the path before the result is sent.\n");
			text.Append('\n');
			return text.ToString();
		}

		static void Row(StringBuilder text, string head, int width, string body)
		{
			text.Append("  ").Append(head.PadRight(width)).Append(' ').Append(body).Append('\n');
		}

		/// <summary>The wire keys of a payload record, in declaration order. The event key is omitted: each line names it.</summary>
		static string Fields(Type payload)
		{
			var parameters = payload.GetConstructors(BindingFlags.Public | BindingFlags.Instance)[0].GetParameters();
			return string.Join(", ", parameters
				.Where(p => p.Name != "Event")
				.Select(p => JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name)));
		}
	}
}
