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
using System.Text.Json;
using System.Threading;

namespace OpenRA.Mods.Common.AgentSocket
{
	public static class AgentSocketCommands
	{
		sealed record Envelope(bool Ok, string Cmd, string Error, string Hint);
		sealed record PingResponse(bool Ok, string Cmd, int Tick);

		/// <summary>
		/// The events wait's reply: events when a turn event arrived, timeout when the tick budget ran out, paused when
		/// the game was paused, or world_changed when the live world was replaced. The events are in the envelope.
		/// </summary>
		sealed record WaitResultV1(bool Ok, string Cmd, string Result, int StartTick, int Tick, int RequestedTicks, int WaitedTicks);
		sealed record HelpV1(bool Ok, string Cmd, string Banner);

		/// <summary>Every error envelope and refusal carries this hint, so an agent that gets an error finds the command list on its own (P1.18).</summary>
		public const string Hint = "see {\"cmd\":\"help\"}";

		/// <summary>The refusal for a wait that names a job. Orders and deploys wait for their outcome, so there is no job to collect.</summary>
		const string JobRefused = "wait takes no job: an order or a deploy waits for its outcome, and wait reads events";

		/// <summary>
		/// The result of a snapshot step. Tick is the game clock of the world left active, RequestedTick the game
		/// tick the command aimed at, MovedTicks the signed change in the game clock, and Held the snapshots the
		/// window still holds.
		/// </summary>
		sealed record SnapshotV1(bool Ok, string Cmd, int Tick, int RequestedTick, int MovedTicks, int Held);

		/// <summary>Returned whenever the target is missing or malformed, so a caller sees how to write it.</summary>
		const string Usage = "usage: undo offset <ticks> to rewind or advance (a negative offset rewinds), " +
			"or undo at_tick <tick> for an absolute game tick";

		/// <summary>The command names the dispatcher answers. The banner lists the same names, and a headless test keeps them in step.</summary>
		public static readonly string[] Names =
		[
			"ping", "help", "state", "actors", "maps", "load_skirmish", "order", "orders", "wait",
			"production", "production_tree", "powers", "build", "cancel", "place", "deploy", "screenshot", "undo", "look", "map_state",
		];

		static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

		/// <summary>Serializes a response or event DTO to one JSON line, without the terminator.</summary>
		public static string Serialize(object value)
		{
			return JsonSerializer.Serialize(value, Json);
		}

		/// <summary>
		/// Parses one request line and returns the single response line for it, wrapped in the turn envelope (AgentEnvelope)
		/// except for help. A root array is one request: its items run in order, and the results are one JSON array with
		/// one result per item, so a failing item does not stop the rest. Items run on the calling thread, so a slow awaited
		/// item delays the whole response until it settles or its own tick budget passes. connection is the calling
		/// connection; it is null only where no connection exists, and a wait then has no events to wait for.
		/// </summary>
		public static string Handle(string line, AgentConnection connection)
		{
			var initial = false;
			return HandleTurn(line, connection, ref initial);
		}

		/// <summary>
		/// Handles one request line on a connection. initial is true until the connection's first envelope is written, and that
		/// envelope is then marked initial:true (P3.5). Its state is written in full, and the events raised before the request
		/// are dropped, so a connection never replays a backlog.
		/// </summary>
		internal static string HandleTurn(string line, AgentConnection connection, ref bool initial)
		{
			JsonDocument doc;
			try
			{
				doc = JsonDocument.Parse(line);
			}
			catch (JsonException)
			{
				return AgentEnvelope.Wrap(Error(null, "invalid json"), includeState: true, initial: TakeInitial(ref initial), connection: connection);
			}

			using (doc)
			{
				// The request's orders share one selection, so an array ends with the union of the actors it ordered (P1.15).
				var selection = new AgentSelection();
				var root = doc.RootElement;
				if (root.ValueKind == JsonValueKind.Array)
				{
					var first = StartTurn(ref initial, connection);
					var items = root.EnumerateArray().ToArray();
					var results = "[" + string.Join(",", items.Select((item, index) =>
						UndoRefusal(item, index, items.Length) ?? HandleItem(item, connection, selection))) + "]";
					return AgentEnvelope.Wrap(results, AgentEnvelope.WantsState(items), connection?.Turn.Flush(), first, connection,
						AgentEnvelope.DetailOf(items));
				}

				// help is documentation, not a turn: it answers with the banner alone, and leaves the first envelope to the next request.
				if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("cmd", out var cmdElement) &&
					cmdElement.ValueKind == JsonValueKind.String && cmdElement.GetString() == "help")
					return HandleItem(root, connection, selection);

				var initialTurn = StartTurn(ref initial, connection);
				return AgentEnvelope.Wrap(HandleItem(root, connection, selection), AgentEnvelope.WantsState(root), connection?.Turn.Flush(), initialTurn, connection,
					AgentEnvelope.DetailOf(root));
			}
		}

		/// <summary>
		/// Takes the first-envelope flag for a turn that is about to run. A first turn drops the backlog of events raised before
		/// it, so the events its envelope reports are only those its own request raised (P3.5).
		/// </summary>
		static bool StartTurn(ref bool initial, AgentConnection connection)
		{
			if (!initial)
				return false;

			connection?.Turn.Flush();
			initial = false;
			return true;
		}

		/// <summary>Takes the first-envelope flag for a reply that is not a turn, such as an invalid request line.</summary>
		static bool TakeInitial(ref bool initial)
		{
			var first = initial;
			initial = false;
			return first;
		}

		/// <summary>
		/// An undo swaps the world under the rest of its request, so an array refuses it unless it is the only item or the last
		/// (P1.19). The refusal is that item's result alone: the other items run as usual. Returns null when the item may run.
		/// </summary>
		static string UndoRefusal(JsonElement item, int index, int count)
		{
			if (count == 1 || index == count - 1 || !IsUndo(item))
				return null;

			return Error("undo", "undo must be the only item of an array, or its last: it swaps the world under the other items");
		}

		static bool IsUndo(JsonElement item)
		{
			return item.ValueKind == JsonValueKind.Object && item.TryGetProperty("cmd", out var cmd)
				&& cmd.ValueKind == JsonValueKind.String && cmd.GetString() == "undo";
		}

		/// <summary>Answers one request object. A value that is not an object is an error for that item alone; a string says so by name.</summary>
		static string HandleItem(JsonElement root, AgentConnection connection, AgentSelection selection)
		{
			if (root.ValueKind == JsonValueKind.String)
				return Error(null, "a command is a JSON object, or an array of objects, not a string holding JSON");

			if (root.ValueKind != JsonValueKind.Object)
				return Error(null, "expected a JSON object");

			if (!root.TryGetProperty("cmd", out var cmdElement) || cmdElement.ValueKind != JsonValueKind.String)
				return Error(null, "missing cmd");

			var cmd = cmdElement.GetString();
			return cmd switch
			{
				"ping" => Ping(cmd),
				"help" => Serialize(new HelpV1(true, cmd, AgentSocketServer.Banner)),
				"state" => State(cmd, root),
				"actors" => Actors(cmd, root),
				"maps" => Run(cmd, AgentMaps.List),
				"load_skirmish" => LoadSkirmish(cmd, root),
				"order" => IssueOrder(cmd, root, selection),
				"orders" => Orders(cmd, root),
				"wait" => Wait(cmd, root, connection),
				"production" => Run(cmd, () => AgentProduction.List(AgentWorldBridge.RequireWorld(), connection)),
				"production_tree" => Run(cmd, () => AgentProduction.Tree(AgentWorldBridge.RequireWorld())),
				"powers" => Run(cmd, () => AgentPowers.List(AgentWorldBridge.RequireWorld())),
				"build" => StartBuild(cmd, root, selection),
				"cancel" => StartCancel(cmd, root),
				"place" => StartPlace(cmd, root, selection),
				"deploy" => StartDeploy(cmd, root, selection),
				"screenshot" => Screenshot(cmd, root),
				"undo" => SnapshotStep(cmd, root),
				"look" => Look(cmd, root),
				"map_state" => MapState(cmd, root),
				_ => Error(cmd, "unknown command"),
			};
		}

		static string Ping(string cmd)
		{
			return Run(cmd, () => new PingResponse(true, cmd, AgentWorldBridge.RequireWorld().WorldTick));
		}

		/// <summary>
		/// The state refresh (9.3). detail is "full" (the default) or "summary" (step 8); the digest itself is the envelope's state, so
		/// the reply is the ack. Any other detail is refused, so a typo does not quietly answer in full.
		/// </summary>
		static string State(string cmd, JsonElement root)
		{
			if (root.TryGetProperty("detail", out var detail) && detail.ValueKind != JsonValueKind.Null &&
				(detail.ValueKind != JsonValueKind.String || detail.GetString() is not ("full" or "summary")))
				return Error(cmd, "detail must be \"full\" or \"summary\"");

			return Run(cmd, () => AgentStateDump.Ack(AgentWorldBridge.RequireWorld()));
		}

		/// <summary>Reads the optional owner and type filters on the socket thread, then lists the matching units on the game thread.</summary>
		static string Actors(string cmd, JsonElement root)
		{
			if (!TryGetOptionalString(root, "owner", out var owner) || !TryGetOptionalString(root, "type", out var type))
				return Error(cmd, "owner and type must be strings");

			return Run(cmd, () => AgentStateDump.Actors(AgentWorldBridge.RequireWorld(), owner, type));
		}

		/// <summary>Parses the request on the socket thread, then waits for the new world there, so the game thread is never blocked for the 60 s load.</summary>
		static string LoadSkirmish(string cmd, JsonElement root)
		{
			var error = AgentSkirmish.Parse(root, out var request);
			if (error != null)
				return Error(cmd, error);

			try
			{
				return JsonSerializer.Serialize(AgentLoadSkirmish.Load(request), Json);
			}
			catch (Exception e)
			{
				return Error(cmd, e.Message);
			}
		}

		/// <summary>Parses the order on the socket thread, then validates and issues it on the game thread.</summary>
		static string IssueOrder(string cmd, JsonElement root, AgentSelection selection)
		{
			var error = AgentOrder.Parse(root, out var request);
			if (error != null)
				return Error(cmd, error);

			try
			{
				// A group is one engine order for many actors. Its reply waits for each actor's outcome (P1.14).
				if (request.Grouped)
				{
					var grouped = AgentMarshal.ExecuteOnGameThread(() => AgentOrder.IssueGroup(AgentWorldBridge.RequireWorld(), request, selection));
					return Serialize(AgentOrder.SettleGroup(grouped));
				}

				// The game thread issues the order. Every order name registers an await (9.2), so the socket thread then parks
				// until that await resolves plus its settle window, and the game keeps ticking.
				var issued = AgentMarshal.ExecuteOnGameThread(() => AgentOrder.Issue(AgentWorldBridge.RequireWorld(), request, selection));
				return Serialize(AgentOrder.Settle(issued));
			}
			catch (PowerRefusedException e)
			{
				return Serialize(new PowerRefusedV1(false, cmd, e.Message, false, e.RemainingTicks, Hint));
			}
			catch (OrderRefusedException e)
			{
				return Serialize(new OrderRefusedV1(false, cmd, e.Message, e.Available, Hint));
			}
			catch (Exception e)
			{
				return Error(cmd, e.Message);
			}
		}

		/// <summary>Parses the item and count on the socket thread, then validates and issues the build on the game thread.</summary>
		static string StartBuild(string cmd, JsonElement root, AgentSelection selection)
		{
			var error = AgentBuild.Parse(root, out var request);
			if (error != null)
				return Error(cmd, error);

			try
			{
				return Serialize(AgentMarshal.ExecuteOnGameThread(() => AgentBuild.Start(AgentWorldBridge.RequireWorld(), request, selection)));
			}
			catch (BuildRefusedException e)
			{
				return Serialize(new BuildRefusedV1(false, cmd, e.Message, e.Available, e.MissingPrerequisites, null, null, e.BlockedBy, Hint));
			}
			catch (OrderRefusedException e)
			{
				return Serialize(new OrderRefusedV1(false, cmd, e.Message, e.Available, Hint));
			}
			catch (Exception e)
			{
				return Error(cmd, e.Message);
			}
		}

		/// <summary>Parses the item and count on the socket thread, then cancels the queued copies on the game thread. Nothing queued is ok:false with the reason.</summary>
		static string StartCancel(string cmd, JsonElement root)
		{
			var error = AgentCancel.Parse(root, out var request);
			if (error != null)
				return Error(cmd, error);

			try
			{
				return Serialize(AgentMarshal.ExecuteOnGameThread(() => AgentCancel.Start(AgentWorldBridge.RequireWorld(), request)));
			}
			catch (OrderRefusedException e)
			{
				return Serialize(new OrderRefusedV1(false, cmd, e.Message, e.Available, Hint));
			}
			catch (Exception e)
			{
				return Error(cmd, e.Message);
			}
		}

		/// <summary>
		/// Parses the item and optional cell on the socket thread, then finds the cell and issues the placement on the game thread.
		/// The reply then waits there for the placed building, so it can name the actor it placed (F6), and the game keeps ticking.
		/// </summary>
		static string StartPlace(string cmd, JsonElement root, AgentSelection selection)
		{
			var error = AgentPlace.Parse(root, out var request);
			if (error != null)
				return Error(cmd, error);

			try
			{
				var started = AgentMarshal.ExecuteOnGameThread(() => AgentPlace.Start(AgentWorldBridge.RequireWorld(), request, selection));
				if (started is PlaceRefusedV1 refusal)
					return Serialize(refusal);

				return Serialize(AgentPlace.Settle((PlaceIssued)started));
			}
			catch (Exception e)
			{
				return Error(cmd, e.Message);
			}
		}

		/// <summary>
		/// Parses the actor and optional cell on the socket thread, then issues the Move and the queued DeployTransform on the game
		/// thread. The reply waits for the deploy's outcome here, so the game keeps ticking (P1.13). No valid cell within the search
		/// radius is refused with the candidate reasons.
		/// </summary>
		static string StartDeploy(string cmd, JsonElement root, AgentSelection selection)
		{
			var error = AgentDeploy.Parse(root, out var request);
			if (error != null)
				return Error(cmd, error);

			try
			{
				var issued = AgentMarshal.ExecuteOnGameThread(() => AgentDeploy.Start(AgentWorldBridge.RequireWorld(), request, selection));
				return Serialize(AgentDeploy.Settle(issued));
			}
			catch (DeployRefusedException e)
			{
				return Serialize(e.Refusal);
			}
			catch (OrderRefusedException e)
			{
				return Serialize(new OrderRefusedV1(false, cmd, e.Message, e.Available, Hint));
			}
			catch (Exception e)
			{
				return Error(cmd, e.Message);
			}
		}

		/// <summary>
		/// Parses the optional actor on the socket thread, then centres and arms the capture on the game thread. The wait for
		/// the written file happens here, on the socket thread, so the game thread is never blocked on the render.
		/// </summary>
		static string Screenshot(string cmd, JsonElement root)
		{
			var error = AgentScreenshot.Parse(root, out var request);
			if (error != null)
				return Error(cmd, error);

			// Cancelled on any failure, so a request the game thread runs late (after ExecuteOnGameThread gave up) arms nothing,
			// and a frame that renders after the timeout is not written.
			using var abandon = new CancellationTokenSource();
			var cancellation = abandon.Token;
			try
			{
				var start = AgentMarshal.ExecuteOnGameThread(() =>
					AgentScreenshot.Start(AgentWorldBridge.RequireWorld(), AgentWorldBridge.ActiveRenderer, request, cancellation));
				return Serialize(AgentScreenshot.Await(start));
			}
			catch (Exception e)
			{
				abandon.Cancel();
				return Error(cmd, e.Message);
			}
		}

		/// <summary>
		/// Parses the at or points selector and the optional k on the socket thread, then looks on the game thread (P2.8). An empty or
		/// invalid selector is an error with the help hint, as every error is.
		/// </summary>
		static string Look(string cmd, JsonElement root)
		{
			var error = AgentLook.Parse(root, out var request);
			if (error != null)
				return Error(cmd, error);

			return Run(cmd, () => AgentLook.Build(AgentWorldBridge.RequireWorld(), AgentWorldBridge.RequireWorld().LocalPlayer, request));
		}

		/// <summary>
		/// The map state in the requested format (P2.11): png, the default, renders map-state.png; text returns the grid inline. The
		/// turn envelope still refreshes map-state.png on a request that carries state. Any other format is an error, as every bad request is.
		/// </summary>
		static string MapState(string cmd, JsonElement root)
		{
			var format = "png";
			if (root.TryGetProperty("format", out var formatElement) && formatElement.ValueKind != JsonValueKind.Null)
			{
				if (formatElement.ValueKind != JsonValueKind.String)
					return Error(cmd, "format must be \"png\" or \"text\"");

				format = formatElement.GetString();
			}

			return format switch
			{
				"png" => Run(cmd, () => AgentMapState.Render(AgentWorldBridge.RequireWorld(), AgentWorldBridge.RequireWorld().LocalPlayer)),
				"text" => Run(cmd, () => AgentMapText.Build(AgentWorldBridge.RequireWorld(), AgentWorldBridge.RequireWorld().LocalPlayer)),
				_ => Error(cmd, "format must be \"png\" or \"text\""),
			};
		}

		/// <summary>
		/// wait long-polls the connection's turn buffer: result is events when an event arrived, which is in this response's
		/// events, and timeout, paused, or world_changed otherwise. Every budget is game ticks: timeout_ticks is capped at
		/// AgentGameClock.MaxWaitTicks, and min_ticks holds the wait before it reports events. A job is refused, since an order
		/// and a deploy wait for their outcome. A connection-less request (no connection) has nothing to wait for, so it times
		/// out at once. The wait does not hold a lock the game thread needs; it runs on the connection's thread.
		/// </summary>
		static string Wait(string cmd, JsonElement root, AgentConnection connection)
		{
			if (root.TryGetProperty("job", out _))
				return Error(cmd, JobRefused);

			var timeoutTicks = AgentGameClock.MaxWaitTicks;
			if (root.TryGetProperty("timeout_ticks", out var timeoutElement) && timeoutElement.ValueKind != JsonValueKind.Null &&
				(timeoutElement.ValueKind != JsonValueKind.Number || !timeoutElement.TryGetInt32(out timeoutTicks) ||
				timeoutTicks < 0 || timeoutTicks > AgentGameClock.MaxWaitTicks))
				return Error(cmd, $"timeout_ticks must be an integer from 0 to {AgentGameClock.MaxWaitTicks}");

			var minTicks = 0;
			if (root.TryGetProperty("min_ticks", out var minElement) && minElement.ValueKind != JsonValueKind.Null &&
				(minElement.ValueKind != JsonValueKind.Number || !minElement.TryGetInt32(out minTicks) ||
				minTicks < 0 || minTicks > AgentGameClock.MaxWaitTicks))
				return Error(cmd, $"min_ticks must be an integer from 0 to {AgentGameClock.MaxWaitTicks}");

			if (minTicks > timeoutTicks)
				return Error(cmd, "min_ticks must not exceed timeout_ticks");

			try
			{
				var start = AgentGameClock.Read();
				if (start.World == null)
					return Error(cmd, "no game world is loaded");

				if (connection == null)
					return Serialize(new WaitResultV1(true, cmd, "timeout", start.Tick, start.Tick, timeoutTicks, 0));

				var result = connection.Turn.WaitForEvents(start, timeoutTicks, minTicks, AgentGameClock.Read);
				var end = AgentGameClock.Read();
				return Serialize(new WaitResultV1(true, cmd, result, start.Tick, end.Tick, timeoutTicks, end.Tick - start.Tick));
			}
			catch (Exception e)
			{
				return Error(cmd, e.Message);
			}
		}

		/// <summary>Reads the actor ID on the socket thread, then lists its accepted order names on the game thread.</summary>
		static string Orders(string cmd, JsonElement root)
		{
			if (!root.TryGetProperty("actor", out var element) || !element.TryGetUInt32(out var actor))
				return Error(cmd, "actor must be an unsigned integer");

			return Run(cmd, () => AgentOrder.List(AgentWorldBridge.RequireWorld(), actor));
		}

		/// <summary>
		/// Moves the snapshot clock in one step. offset is a signed game-tick count: a negative one rewinds, a positive one
		/// advances, and at_tick is an absolute game tick instead. Exactly one of them is required. The snapshot nearest the
		/// target game time is restored; nothing is consumed. The step runs on the game thread, because a restore swaps the
		/// active world; the socket thread is blocked for its duration.
		/// </summary>
		static string SnapshotStep(string cmd, JsonElement root)
		{
			var hasOffset = root.TryGetProperty("offset", out var offsetElement) && offsetElement.ValueKind != JsonValueKind.Null;
			var hasAtTick = root.TryGetProperty("at_tick", out var atElement) && atElement.ValueKind != JsonValueKind.Null;
			if (hasOffset == hasAtTick)
				return Error(cmd, Usage);

			int? offset = null;
			if (hasOffset)
			{
				if (offsetElement.ValueKind != JsonValueKind.Number || !offsetElement.TryGetInt32(out var value))
					return Error(cmd, Usage);

				offset = value;
			}

			int? atTick = null;
			if (hasAtTick)
			{
				if (atElement.ValueKind != JsonValueKind.Number || !atElement.TryGetInt32(out var value) || value < 0)
					return Error(cmd, Usage);

				atTick = value;
			}

			return Run(cmd, () =>
			{
				var world = AgentWorldBridge.RequireWorld();
				var liveTick = world.WorldTick;
				var targetTick = atTick ?? (int)Math.Clamp((long)liveTick + offset.Value, 0, int.MaxValue);

				var renderer = AgentWorldBridge.ActiveRenderer;
				if (!WorldForks.RestoreNearest(world, renderer, targetTick * (long)world.Timestep, out var failureReason))
					throw new InvalidOperationException(failureReason);

				// The bridge repoints at the restored world through IWorldAdopted, so this reads the new tick.
				var restoredTick = AgentWorldBridge.RequireWorld().WorldTick;
				return new SnapshotV1(true, cmd, restoredTick, targetTick, restoredTick - liveTick, WorldForks.Count);
			});
		}

		/// <summary>Absent or null is no filter. Any non-string value is rejected.</summary>
		static bool TryGetOptionalString(JsonElement root, string name, out string value)
		{
			value = null;
			if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
				return true;

			if (element.ValueKind != JsonValueKind.String)
				return false;

			value = element.GetString();
			return true;
		}

		/// <summary>Runs the request on the game thread. Any failure becomes an error envelope so the game keeps running.</summary>
		static string Run<T>(string cmd, Func<T> fn)
		{
			try
			{
				return JsonSerializer.Serialize(AgentMarshal.ExecuteOnGameThread(fn), Json);
			}
			catch (Exception e)
			{
				return Error(cmd, e.Message);
			}
		}

		static string Error(string cmd, string error)
		{
			return JsonSerializer.Serialize(new Envelope(false, cmd, error, Hint), Json);
		}
	}
}
