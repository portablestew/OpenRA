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
		static readonly (string Name, string Summary, string Example)[] Commands =
		[
			("ping", "-> tick", "{\"cmd\":\"ping\"}"),
			("help", "-> banner (this text, as one string)", "{\"cmd\":\"help\"}"),
			("state", "-> tick, net_frame, map, paused, game_over, local_player, players, units", "{\"cmd\":\"state\"}"),
			("actors", "owner, type (optional filters) -> units", "{\"cmd\":\"actors\",\"type\":\"e1\"}"),
			("maps", "-> maps", "{\"cmd\":\"maps\"}"),
			("load_map", "map (string, uid or title) -> the state shape, once the new world is ready",
				"{\"cmd\":\"load_map\",\"map\":\"<a uid from maps>\"}"),
			("order", "exactly one of actor (uint), actors ([uint]), or type (string: a regex matched against the whole type name of"
				+ " each owned actor, so \"e1\" is e1 only and \"e.\" is every e-type); order (string); at most one of cell [x,y],"
				+ " pos [x,y,z], target_actor (uint); optional queued (bool), item (string), extra_data (uint), extra_location [x,y],"
				+ " settle_ticks (Move only), await (bool, default true; false is Move only and single actor only)\n"
					+ "                -> tick, min_effect_tick, and for a Move, valid, effect_tick, settled, settled_ticks, path."
					+ " valid:false adds reason and orders (the names the actor accepts)."
						+ " For an Attack, outcome (started | no_valid_target) and settled; no_valid_target adds reason."
				+ " await:false returns job (the id for wait) with tick, min_effect_tick, and does not wait."
				+ " A group returns ordered (the actor ids), matched, and results (one per actor: actor, valid, effect_tick, settled,"
				+ " settled_ticks, path, outcome, reason, orders, error). One actor that refuses the name refuses the whole group."
				+ " A name the actor does not accept is refused: ok:false, error, available (the names it accepts)."
				+ " The exception is Attack, which is never refused here: an attack that cannot start replies no_valid_target."
				+ " Zero or several selectors, a bad regex, or a type with no match are errors",
				"{\"cmd\":\"order\",\"actor\":12,\"order\":\"Move\",\"cell\":[40,50],\"await\":false}"),
			("wait", "job (int); optional timeout_ms (non-negative int, default 10000)\n"
				+ "                -> the Move reply (cmd:wait, same keys as a Move order reply) once the job settles, or"
				+ " status:\"pending\" if it has not settled by the timeout (the job stays open). An unknown job, or a job whose"
				+ " world load_map or undo replaced: ok:false, error",
				"{\"cmd\":\"wait\",\"job\":1,\"timeout_ms\":5000}"),
			("orders", "actor (uint) -> type, orders (the order names the actor accepts)", "{\"cmd\":\"orders\",\"actor\":12}"),
			("production", "-> player, cash, resources, power, queues (the local player's queues; see Production keys)", "{\"cmd\":\"production\"}"),
			("build", "item (string); optional count (positive int, default 1)\n"
				+ "                -> ok, actor, queue_type, item, count, tick, min_effect_tick, ready, eta_ticks, blocked_by. Not buildable now: ok:false, error,"
				+ " available (the items build accepts now: buildable and affordable), missing_prerequisites (every unmet prerequisite;"
				+ " null if the refusal is not about prerequisites), ready, eta_ticks. ready and eta_ticks describe a copy of the item already"
				+ " queued: ready:true when it is done, else ready:false with eta_ticks, the ticks until it is done (null when none is queued)."
				+ " blocked_by names the finished item at the head of the queue this one joins, which holds the queue until it is placed"
				+ " (null when that head is not finished); a refusal carries it too",
				"{\"cmd\":\"build\",\"item\":\"e1\",\"count\":2}"),
			("cancel", "item (string); optional count (positive int, default 1)\n"
				+ "                -> ok, actor, queue_type, item, canceled, tick, min_effect_tick. Removes up to count queued copies of item from the"
				+ " first owned queue that holds it, last copy first. A copy in progress goes when it is the last copy. Nothing queued,"
				+ " or the producer disabled: ok:false, error",
				"{\"cmd\":\"cancel\",\"item\":\"powr\",\"count\":1}"),
			("place", "item (string); optional cell [x,y] (default: the producer's location)\n"
				+ "                -> ok, item, requested_cell (null if omitted), search_from, cell, tick, min_effect_tick. The nearest"
				+ " valid cell within 10 of search_from is used. Not done: ok:false, error. No valid cell: ok:false, error,"
				+ " cells_checked, candidates (the nearest 8 cells, each with cell and reason)",
				"{\"cmd\":\"place\",\"item\":\"powr\",\"cell\":[40,50]}"),
			("screenshot", "optional actor (uint): centre the view on that live unit first\n"
				+ "                -> ok, cmd, path (the PNG, already written), actor (null if omitted), center_px, unit_px (map pixels, not window pixels; null if"
				+ " omitted), tick. Waits for the next rendered frame to be saved, so the file exists when the reply arrives."
				+ " No window (headless), no world view, the shellmap, an unknown or dead actor, or a failed write: ok:false, error",
				"{\"cmd\":\"screenshot\",\"actor\":12}"),
			("subscribe", "events (non-empty array of group names); optional owner (player name) -> subscribed\n"
				+ "                owner filters only the groups named in this request; omit it to receive every owner's events."
				+ " A later subscribe replaces a group's filter",
				"{\"cmd\":\"subscribe\",\"events\":[\"damage\",\"killed\"],\"owner\":\"Multi0\"}"),
			("unsubscribe", "events (array), or all:true -> subscribed", "{\"cmd\":\"unsubscribe\",\"events\":[\"damage\"]}"),
			("undo", "offset (string): \"-<seconds>\" rewinds, \"+<seconds>\" advances, \"@<seconds>\" jumps to that"
				+ " absolute game time, read at 25 ticks per second\n"
				+ "                -> tick, requested_tick, moved_ticks (signed), held. The snapshot nearest the target game"
				+ " time is restored; nothing is consumed. A missing or unprefixed offset, or no snapshot in range:"
				+ " ok:false, error (the usage)",
				"{\"cmd\":\"undo\",\"offset\":\"-10\"}"),
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
			text.Append("OpenRA agent debug socket, schema v1. Send one JSON object per line, or one JSON array of objects per line; one response line per request.\n");
			text.Append("Every response but help is an envelope: results, events, state, tick, map_state_png, map_state_tick.\n");
			text.Append("Inside results, responses echo cmd and carry ok. Errors carry ok:false and error.\n");
			text.Append("Commands (request keys, then response keys):\n");
			foreach (var (name, summary, example) in Commands)
			{
				Row(text, name, 11, summary);
				Row(text, "", 11, $"example: {example}");
			}

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
			text.Append("  Bearings: 8-way N/NE/E/SE/S/SW/W/NW on map axes (+X East, +Y South), from a friendly actor toward the target.\n");
			text.Append("  After the banner, and after every response, the server sends a lone > line as a prompt: it is your turn.\n");
			text.Append("  A > line is not a response and not an event; skip lines that are just >.\n");
			text.Append("  An array is one request: the items run in order and results is one JSON array, one result per item.\n");
			text.Append("  The envelope: results (the request's result, or the array), events (since the last response; empty until P3),\n");
			text.Append("    state (the state digest, as the state command returns it), tick (the game clock after the request), map_state_png\n");
			text.Append("    and map_state_tick (null until P2). With no world loaded, tick and state are null.\n");
			text.Append("  Add \"digest\":false to an object request to drop state. An array drops it only when every item sets digest:false.\n");
			text.Append("  Selection: an issued order selects its actor, so world.Selection is the actors a request ordered (an array: their union).\n");
			text.Append("    A request with no order leaves the selection alone.\n");
			text.Append("  A failing item does not stop the rest. A slow awaited item delays the response until it settles or times out.\n");
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
