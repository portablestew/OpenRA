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
			("state", "-> tick, ok, cmd: a refresh ack, so this result carries no digest\n"
				+ "                -> the digest is the envelope's state: ok, cmd, tick, net_frame, map, paused, game_over, local_player,"
				+ " players, units, threats, selection\n"
				+ "                -> map is uid, title, bounds ([x, y, width, height], Map.Bounds in cells) and spawns (one per player with a home"
				+ " cell: player, cell [x, y]). Your own buildings in units are id, type, cell and hp only."
				+ " selection is the sorted ids of the actors your orders selected, empty when none."
				+ " players[].known_counts is the count by type of the actors you know: all of your own, and another player's only where known."
				+ " players[].bot is the AI type of a bot-owned player (null for a human), and players[].handicap its handicap percent."
				+ " A player not allied with you has null cash, resources, resource_capacity and power."
				+ " threats: the top 15 known enemy units by standoff (the engine's Enemy relationship: neutral civilians and allies are not listed; at most 3 per type; a recent attacker first), each with id, type, cell,"
				+ " hp, attack_target (the friendly it hit recently, or null: only a recent attacker has one), heading (its facing, not a bearing),"
				+ " bearing (from its closest friendly),"
				+ " friendly_id and distance (world units, 1024 per cell) of that friendly.",
				"{\"cmd\":\"state\"}"),
			("actors", "owner, type (optional filters) -> units", "{\"cmd\":\"actors\",\"type\":\"e1\"}"),
			("maps", "-> maps", "{\"cmd\":\"maps\"}"),
			("load_skirmish", "map (string: a uid or title from maps, or \"random\"; default random); speed (string: a game speed key, one of"
				+ " slowest, slower, default, fast, faster, fastest; default default); opponents (array of objects, default one"
				+ " {\"bot\":\"normal\",\"handicap\":70}; each is bot (string, an AI type the map defines) and handicap (int, 0 to 95 in steps"
				+ " of 5); an empty array is a solo world)\n"
				+ "                -> the state shape, plus skirmish (map, map_requested, speed, and opponents: each with player, bot, handicap"
				+ " and slot), once the new world is ready",
				"{\"cmd\":\"load_skirmish\"}"),
			("order", "exactly one of actor (uint), actors ([uint]), or type (string: a regex matched against the whole type name of"
				+ " each owned actor, so \"e1\" is e1 only and \"e.\" is every e-type); order (string); at most one of cell [x,y],"
				+ " pos [x,y,z], target_actor (uint); optional queued (bool), item (string), extra_data (uint), extra_location [x,y],"
				+ " settle_ticks (Move and AttackMove), await (bool, default true; false is Move and AttackMove only, and single actor only)\n"
					+ "                -> tick, min_effect_tick, settled, plus the order's resolution. Every order name reports the"
					+ " resolution, and nothing waits for travel or completion, which are events. Every name except an Attack and a"
					+ " DeployTransform reports valid, effect_tick and settled_ticks, and holds the settle window so trailing events"
					+ " land in this reply; valid:false adds reason and orders (the names the actor accepts)."
					+ " A Move adds path, the cells it planned."
					+ " An Attack replaces valid, effect_tick and settled_ticks with outcome (started | no_valid_target; null when"
					+ " unsettled) and returns at the attack's start or its resolve budget, with reason when it did not start."
					+ " An AttackMove adds outcome (started | no_path), with no path, since its travel is not awaited."
					+ " A DeployTransform replaces valid, effect_tick and settled_ticks with outcome (completed | canceled; null when"
					+ " unsettled) and waits for the transform to end, with reason when canceled."
				+ " await:false returns job (the id for wait) with tick, min_effect_tick, and does not wait."
				+ " A group returns ordered (the actor ids), matched, and results (one per actor: actor, valid, effect_tick, settled,"
				+ " settled_ticks, path, outcome, reason, orders, error; an Attack or a DeployTransform actor carries only actor,"
				+ " settled, outcome and reason). One actor that refuses the name refuses the whole group."
				+ " A name the actor does not accept is refused: ok:false, error, available (the names it accepts)."
				+ " The exception is Attack, which is never refused here: an attack that cannot start replies no_valid_target."
				+ " Zero or several selectors, a bad regex, or a type with no match are errors",
				"{\"cmd\":\"order\",\"actor\":12,\"order\":\"AttackMove\",\"cell\":[40,50]}"),
			("wait", "job (int), or no job; optional timeout_ms (non-negative int, default 10000)\n"
				+ "                -> with job: the Move reply (cmd:wait, same keys as a Move order reply) with result:\"job\", once the job settles, or"
				+ " status:\"pending\" at the timeout (the job stays open). An unknown job, or a job whose world load_skirmish or undo replaced: ok:false, error."
				+ " With no job: result:\"events\" when an event arrives (it is in this response's events), or result:\"timeout\"",
				"{\"cmd\":\"wait\",\"timeout_ms\":5000}"),
			("orders", "actor (uint) -> type, orders (the order names the actor accepts)", "{\"cmd\":\"orders\",\"actor\":12}"),
			("production", "-> player, cash, resources, power, queues (the local player's queues; see Production keys). A queue's options are listed only when they changed since this connection last read them (changed says whether they were); the first read after connect, and the first read after a world change, lists them all", "{\"cmd\":\"production\"}"),
			("production_tree", "-> the same keys as production: the full catalog, with every option of every queue in full", "{\"cmd\":\"production_tree\"}"),
			("build", "item (string); optional count (positive int, default 1)\n"
				+ "                -> ok, actor, queue_type, item, count, tick, min_effect_tick, ready, eta_ticks, blocked_by. Not buildable now: ok:false, error,"
				+ " available (the items build accepts now: buildable and affordable), missing_prerequisites (every unmet prerequisite;"
				+ " null if the refusal is not about prerequisites), ready and eta_ticks (both null: a refused build queues nothing)."
				+ " On success, ready is always false, and eta_ticks is the ticks until the first copy queued by this request is done"
				+ " (25 ticks per second at the default game speed),"
				+ " counting the items ahead of it in its queue at the current power; null while a finished item at the head of that queue"
				+ " holds it until it is placed. blocked_by names the finished item at the head of the queue this one joins, which holds"
				+ " the queue until it is placed (null when that head is not finished); a refusal carries it too",
				"{\"cmd\":\"build\",\"item\":\"e1\",\"count\":2}"),
			("cancel", "item (string); optional count (positive int, default 1)\n"
				+ "                -> ok, actor, queue_type, item, canceled, tick, min_effect_tick. Removes up to count queued copies of item from the"
				+ " first owned queue that holds it, last copy first. A copy in progress goes when it is the last copy. Nothing queued,"
				+ " or the producer disabled: ok:false, error",
				"{\"cmd\":\"cancel\",\"item\":\"powr\",\"count\":1}"),
			("place", "item (string); optional cell [x,y] (default: the producer's location)\n"
				+ "                -> ok, item, requested_cell, search_from, cell, actor, settled, tick, min_effect_tick. The nearest"
				+ " valid cell within 10 of search_from is used (cell is where it placed, not requested_cell), and the reply waits for the building, so actor names what it placed"
				+ " (null when unsettled, in a paused game). Not done: ok:false, error. No valid cell: ok:false, error,"
				+ " cells_checked, candidates (the nearest 8 cells, each with cell and reason), and look (the look command's reply over those"
				+ " cells, so the blockers show). A successful place has no look. A refusal has one, empty with no candidates.",
				"{\"cmd\":\"place\",\"item\":\"powr\",\"cell\":[40,50]}"),
			("deploy", "actor (uint, an mcv); optional at [x,y] (the top-left cell, anywhere on the map, no search); optional await (default"
				+ " true; false returns a job id, and wait {job} returns the outcome)\n"
				+ "                -> ok, cmd, actor, requested_cell, cell, moved (true when a Move took the actor there first),"
				+ " settled, outcome (completed or canceled; null when unsettled), reason (null unless canceled), tick, min_effect_tick."
				+ " The cell is the actor's own when its footprint is valid there, else the nearest valid, reachable cell within 20. One reply covers"
				+ " the Move and the deploy. No valid cell: ok:false, error, cells_checked, candidates (the nearest 8 cells, each with cell and reason),"
				+ " and look (the look reply over those cells, so the blockers show). A successful deploy has no look."
				+ " A refusal has one, empty with no candidates.",
				"{\"cmd\":\"deploy\",\"actor\":123}"),
			("screenshot", "optional actor (uint): centre the view on that live unit first; optional dir (string): the folder to write to,\n"
				+ "                default screenshots in the workspace\n"
				+ "                -> ok, cmd, path (the PNG, already written), actor, center_px, unit_px (map pixels, not window pixels; null when no unit was asked for),"
				+ " tick. Waits for the next rendered frame to be saved, so the file exists when the reply arrives."
				+ " No window (headless), no world view, the shellmap, an unknown or dead actor, or a failed write: ok:false, error",
				"{\"cmd\":\"screenshot\",\"actor\":12,\"dir\":\"shots\"}"),
			("undo", "offset (string): \"-<seconds>\" rewinds, \"+<seconds>\" advances, \"@<seconds>\" jumps to that"
				+ " absolute game time, read at 25 ticks per second\n"
				+ "                -> tick, requested_tick, moved_ticks (signed), held. The snapshot nearest the target game"
				+ " time is restored; nothing is consumed. Pending orders and jobs fail with error restored. In an array, undo must be the only or the last item,"
				+ " or it is refused. A missing or unprefixed offset, or no snapshot in range:"
				+ " ok:false, error (the usage)",
				"{\"cmd\":\"undo\",\"offset\":\"-10\"}"),
			("map_state", "-> ok, cmd, path (map-state.png in the workspace, or null), render (ok or unavailable), reason (only when"
				+ " unavailable), map_px_per_cell (4), origin [x, y] (the top-left map cell), size [w, h] (pixels), tick. Terrain: unexplored"
				+ " black, explored but unseen darkened. Overlays: blips in owner colour (actors you know); selected units get a green"
				+ " crosshair, a yellow path ending in a red X, and a red line to their attack target. Writes a file",
				"{\"cmd\":\"map_state\"}"),
			("look", "at [x, y] or points [[x, y], ...] (at most 16), an optional k (1 to 100, default 20), and an optional radius (1 to 20\n"
				+ "                cells, default 10) for the class summary\n"
				+ "                -> cells (per cell: cell, terrain, buildable, and resource and density when the cell has them), actors (the k\n"
				+ " nearest you know, of any owner: id, type, owner, cell, hp), and summary (one entry per point: pt, radius, cells, classes).\n"
				+ " With points, each actor also has dist (world units, 1024 per cell) and pt, the point it is nearest to, and the k is shared\n"
				+ " so each point gets a fair share. Scenery (trees, husks, map props, spawn markers) is left out. buildable means a construction\n"
				+ " yard could stand on the cell. Each summary class names kind (terrain, resource or blocking), the engine's own class, count\n"
				+ " (cells) and nearest (cell and distance in world units); cells is how many on-map cells within radius the counts cover, so a\n"
				+ " summary is a neighbourhood, not a survey. terrain is the tileset's type (RA: Clear, Road, Rock, Water, River, Beach, Bridge,\n"
				+ " Rough, Tree, Wall, Ore, Gems), resource is the mod's type (RA: Ore, Gems, at any density), and blocking is the map-editor\n"
				+ " category the occupying actor's rules declare (RA trees are Tree), counted only for actors you know, so an unseen doodad is\n"
				+ " not counted. An empty or invalid selector is ok:false, error",
				"{\"cmd\":\"look\",\"points\":[[40,50]],\"k\":5,\"radius\":10}"),
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
			("notification", "notification", typeof(NotificationV1)),
		];

		/// <summary>The command names, in banner order.</summary>
		public static string[] CommandNames => Commands.Select(c => c.Name).ToArray();

		/// <summary>The event types, one per group, in banner order. The overflow marker is not a group and is listed separately.</summary>
		public static string[] EventTypes => Events.Select(e => e.Event).Append(OverflowEvent).ToArray();

		public static string Build()
		{
			var text = new StringBuilder();
			text.Append("OpenRA agent debug socket, schema v1. Send one JSON object per line, or one JSON array of objects per line; one response line per request.\n");
			text.Append("Every response but help is an envelope: results, events, state, tick, map_state_png, map_state_tick, map_state_render,\n"
					+ "    map_px_per_cell, map_origin, map_size.\n");
			text.Append("Inside results, responses echo cmd and carry ok. Errors carry ok:false and error.\n");
			text.Append("Conventions, each stated once here:\n");
			text.Append("  Bearing: 8-way N/NE/E/SE/S/SW/W/NW on map axes (+X East, +Y South),\n");
			text.Append("    from a friendly reference actor toward an enemy or target.\n");
			text.Append("    heading is an actor's own facing or motion direction, never a bearing.\n");
			text.Append("    damaged.attacker_bearing runs from the victim toward the attacker.\n");
			text.Append("  Fog: known means visible, or a FrozenUnderFog last-known snapshot,\n");
			text.Append("    and extends to radar-dot actors once a GPS satellite is launched.\n");
			text.Append("    An attacker is named only when known. Otherwise attacker, attacker_type and attacker_owner are null,\n");
			text.Append("    attacker_shrouded is true, and attacker_bearing is given.\n");
			text.Append("    The same rule holds for events: damaged, killed, captured, order, produced, placed, stance, path and activity are sent only\n");
			text.Append("    for your own actors and known ones. A captor, producer, attacker or order target is named only when known, else null.\n");
			text.Append("    power, notification, defeated, game_over and overflow name no actor, so they are always sent.\n");
			text.Append("    A NewOptions notification lists options only for your side or an ally's: those newly buildable since its last one.\n");
			text.Append("  Null and omitted: a null key is present with no value (an unknown value, or an optional request key left out).\n");
			text.Append("    A key that does not apply is omitted:\n");
			text.Append("    dist and pt in look actors without reference points, and look on a refusal with no local player.\n");
			text.Append("Commands (request keys, then response keys):\n");
			foreach (var (name, summary, example) in Commands)
			{
				Row(text, name, 11, summary);
				Row(text, "", 11, $"example: {example}");
			}

			text.Append("Event types, in the envelope's events. Each event is one object; its event key names its type:\n");
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
			text.Append("  An array is one request: the items run in order and results is one JSON array, one result per item.\n");
			text.Append("  The envelope: results (the request's result, or the array), events (the turn's events, each once: the first 2 of a type in full,"
				+ " then {event, aggregated, tick_from, tick_to}), state (the digest: the fields the state\n"
				+ "    row lists), tick (the game clock after the request). The map image: map_state_png (its workspace path, or\n");
			text.Append("    null), map_state_tick (the tick it was rendered at, equal to tick after a world-changing request), map_state_render\n");
			text.Append("    (ok or unavailable), and map_px_per_cell, map_origin, map_size (its geometry).\n"
				+ "    A connection's first envelope has initial:true, its state in full, and no events from before its first request.\n"
				+ "    The static data (state.map, and players' faction, bot and handicap) rides with the state until a world change (an undo,\n"
				+ "    a load_skirmish, a new world) or a reconnect; in the envelopes between, those keys are null.\n"
				+ " With no world loaded, tick, state and\n");
			text.Append("    the image are null, and render is unavailable.\n");
			text.Append("  Add \"digest\":false to an object request to drop state and the map keys. An array drops them only when every item sets it.\n");
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
