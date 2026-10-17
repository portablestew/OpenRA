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
				+ " optional queued (bool), item (string), extra_data (uint), extra_location [x,y], settle_ticks (Move only),"
				+ " await (bool, default true; false is Move only)\n"
					+ "                -> tick, min_effect_tick, and for a Move, valid, effect_tick, settled, settled_ticks, path."
					+ " valid:false adds reason and orders (the names the actor accepts)."
						+ " For an Attack, outcome (started | no_valid_target) and settled; no_valid_target adds reason."
				+ " await:false returns job (the id for wait) with tick, min_effect_tick, and does not wait."
				+ " A name the actor does not accept is refused: ok:false, error, available (the names it accepts)."
				+ " The exception is Attack, which is never refused here: an attack that cannot start replies no_valid_target"),
			("wait", "job (int); optional timeout_ms (non-negative int, default 10000)\n"
				+ "                -> the Move reply (cmd:wait, same keys as a Move order reply) once the job settles, or"
				+ " status:\"pending\" if it has not settled by the timeout (the job stays open). An unknown job, or a job whose"
				+ " world load_map or undo replaced: ok:false, error"),
			("orders", "actor (uint) -> type, orders (the order names the actor accepts)"),
			("production", "-> player, cash, resources, power, queues (the local player's queues; see Production keys)"),
			("build", "item (string); optional count (positive int, default 1)\n"
				+ "                -> ok, actor, queue_type, item, count, tick, min_effect_tick. Not buildable now: ok:false, error,"
				+ " available (the items build accepts now: buildable and affordable)"),
			("place", "item (string); optional cell [x,y] (default: the producer's location)\n"
				+ "                -> ok, item, requested_cell (null if omitted), search_from, cell, tick, min_effect_tick. The nearest"
				+ " valid cell within 10 of search_from is used. Not done or no valid cell: ok:false, error"),
			("screenshot", "optional actor (uint): centre the view on that live unit first\n"
				+ "                -> ok, cmd, path (the PNG, already written), actor (null if omitted), center_px, unit_px (map pixels, not window pixels; null if"
				+ " omitted), tick. Waits for the next rendered frame to be saved, so the file exists when the reply arrives."
				+ " No window (headless), no world view, the shellmap, an unknown or dead actor, or a failed write: ok:false, error"),
			("subscribe", "events (non-empty array of group names); optional owner (player name) -> subscribed\n"
				+ "                owner filters only the groups named in this request; omit it to receive every owner's events."
				+ " A later subscribe replaces a group's filter"),
			("unsubscribe", "events (array), or all:true -> subscribed"),
			("undo", "offset (string): \"-<seconds>\" rewinds, \"+<seconds>\" advances, \"@<seconds>\" jumps to that"
				+ " absolute game time, read at 25 ticks per second\n"
				+ "                -> tick, requested_tick, moved_ticks (signed), held. The snapshot nearest the target game"
				+ " time is restored; nothing is consumed. A missing or unprefixed offset, or no snapshot in range:"
				+ " ok:false, error (the usage)"),
		];

		/// <summary>Each event group, the event type it carries, and the record that defines the payload.</summary>
		static readonly (string Group, string Event, Type Payload)[] Events =
		[
			("damage", "damaged", typeof(DamagedV1)),
			("killed", "killed", typeof(KilledV1)),
			("captured", "captured", typeof(CapturedV1)),
			("orders", "order", typeof(OrderEventV1)),
			("production", "produced", typeof(ProducedV1)),
			("production", "placed", typeof(PlacedV1)),
			("stance", "stance", typeof(StanceV1)),
			("path", "path", typeof(PathV1)),
			("activity", "activity", typeof(ActivityV1)),
			("power", "power_low", typeof(PowerEventV1)),
			("power", "power_restored", typeof(PowerEventV1)),
			("player", "defeated", typeof(DefeatedV1)),
			("player", "game_over", typeof(GameOverV1)),
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
			Row(text, "activity", 12, "kind: move or transform; state: started, completed or canceled; reason (null unless the activity"
				+ " says why it ended): reached or blocked (a move completed), interrupted (a Stop or a new order canceled it),"
				+ " cannot_deploy or destroyed (a transform canceled)");

			text.Append("Player keys, in state.players:\n");
			Row(text, "player", 12, Fields(typeof(PlayerV1)));
			Row(text, "power", 12, $"{Fields(typeof(PowerV1))} (power is null without a PowerManager)");

			text.Append("Production keys, in production.queues:\n");
			Row(text, "queue", 12, Fields(typeof(ProductionQueueV1)));
			Row(text, "option", 12, Fields(typeof(ProductionOptionV1)));
			Row(text, "item", 12, Fields(typeof(ProductionItemV1)));

			text.Append("Notes:\n");
			text.Append("  After the banner, and after every response, the server sends a lone > line as a prompt: it is your turn.\n");
			text.Append("  A > line is not a response and not an event; skip lines that are just >.\n");
			text.Append("  order min_effect_tick is a lower bound (tick + 1). The order applies on or after it; network frame delay can make it later.\n");
			text.Append("  Example: {\"cmd\":\"order\",\"actor\":12,\"order\":\"Move\",\"cell\":[40,50],\"await\":false}\n")
				.Append("    -> {\"ok\":true,\"cmd\":\"order\",\"job\":1,...}\n");
			text.Append("  then {\"cmd\":\"wait\",\"job\":1,\"timeout_ms\":5000} -> the Move reply, or {\"status\":\"pending\"} after 5 s.\n");
			text.Append("  An overflow event is sent before the events that follow it. dropped counts the events a full queue discarded.\n");
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
