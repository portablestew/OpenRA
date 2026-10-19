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
		/// <summary>The events wait's reply (P3.4): events when a turn event arrived, timeout otherwise. The events are in the envelope.</summary>
		sealed record WaitResultV1(bool Ok, string Cmd, string Result);
		sealed record HelpV1(bool Ok, string Cmd, string Banner);

		/// <summary>Every error envelope and refusal carries this hint, so an agent that gets an error finds the command list on its own (P1.18).</summary>
		public const string Hint = "see {\"cmd\":\"help\"}";

		/// <summary>
		/// The result of a snapshot step. Tick is the game clock of the world left active, RequestedTick the game
		/// tick the command aimed at, MovedTicks the signed change in the game clock, and Held the snapshots the
		/// window still holds.
		/// </summary>
		sealed record SnapshotV1(bool Ok, string Cmd, int Tick, int RequestedTick, int MovedTicks, int Held);

		/// <summary>Game ticks per second an offset is read in, the default game speed.</summary>
		const int TicksPerSecond = 25;

		/// <summary>Returned whenever the offset is missing or malformed, so a caller sees how to write it.</summary>
		const string Usage = "usage: undo offset \"-<seconds>\" to rewind, \"+<seconds>\" to advance, " +
			"or \"@<seconds>\" for an absolute game time, read at 25 ticks per second";

		/// <summary>The command names the dispatcher answers. The banner lists the same names, and a headless test keeps them in step.</summary>
		public static readonly string[] Names =
		[
			"ping", "help", "state", "actors", "maps", "load_skirmish", "order", "orders", "wait",
			"production", "production_tree", "build", "cancel", "place", "deploy", "screenshot", "undo", "look", "map_state",
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
		/// item delays the whole response until it settles or its own wall-clock limit passes. connection is the calling
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
				return AgentEnvelope.Wrap(Error(null, "invalid json"), includeState: true, initial: TakeInitial(ref initial));
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
					return AgentEnvelope.Wrap(results, AgentEnvelope.WantsState(items), connection?.Turn.Flush(), first);
				}

				// help is documentation, not a turn: it answers with the banner alone, and leaves the first envelope to the next request.
				if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("cmd", out var cmdElement) &&
					cmdElement.ValueKind == JsonValueKind.String && cmdElement.GetString() == "help")
					return HandleItem(root, connection, selection);

				var initialTurn = StartTurn(ref initial, connection);
				return AgentEnvelope.Wrap(HandleItem(root, connection, selection), AgentEnvelope.WantsState(root), connection?.Turn.Flush(), initialTurn);
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

		/// <summary>Answers one request object. A value that is not an object is an error for that item alone.</summary>
		static string HandleItem(JsonElement root, AgentConnection connection, AgentSelection selection)
		{
			if (root.ValueKind != JsonValueKind.Object)
				return Error(null, "expected a JSON object");

			if (!root.TryGetProperty("cmd", out var cmdElement) || cmdElement.ValueKind != JsonValueKind.String)
				return Error(null, "missing cmd");

			var cmd = cmdElement.GetString();
			return cmd switch
			{
				"ping" => Ping(cmd),
				"help" => Serialize(new HelpV1(true, cmd, AgentSocketServer.Banner)),
				"state" => Run(cmd, () => AgentStateDump.Ack(AgentWorldBridge.RequireWorld())),
				"actors" => Actors(cmd, root),
				"maps" => Run(cmd, AgentMaps.List),
				"load_skirmish" => LoadSkirmish(cmd, root),
				"order" => IssueOrder(cmd, root, selection),
				"orders" => Orders(cmd, root),
				"wait" => Wait(cmd, root, connection),
				"production" => Run(cmd, () => AgentProduction.List(AgentWorldBridge.RequireWorld())),
				"production_tree" => Run(cmd, () => AgentProduction.Tree(AgentWorldBridge.RequireWorld())),
				"build" => StartBuild(cmd, root, selection),
				"cancel" => StartCancel(cmd, root),
				"place" => StartPlace(cmd, root, selection),
				"deploy" => StartDeploy(cmd, root, selection),
				"screenshot" => Screenshot(cmd, root),
				"undo" => SnapshotStep(cmd, root),
				"look" => Look(cmd, root),
				"map_state" => Run(cmd, () => AgentMapState.Render(AgentWorldBridge.RequireWorld(), AgentWorldBridge.RequireWorld().LocalPlayer)),
				_ => Error(cmd, "unknown command"),
			};
		}

		static string Ping(string cmd)
		{
			return Run(cmd, () => new PingResponse(true, cmd, AgentWorldBridge.RequireWorld().WorldTick));
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

				// await:false takes a job instead: the game thread issues the Move and records it, and the reply is the job id.
				if (!request.Await)
					return Serialize(AgentMarshal.ExecuteOnGameThread(() => AgentOrderJobs.Issue(AgentWorldBridge.RequireWorld(), request, selection)));

				// The game thread issues the order. Every order name registers an await (9.2), so the socket thread then parks
				// until that await resolves plus its settle window, and the game keeps ticking.
				var issued = AgentMarshal.ExecuteOnGameThread(() => AgentOrder.Issue(AgentWorldBridge.RequireWorld(), request, selection));
				return Serialize(AgentOrder.Settle(issued));
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
		/// thread. The reply waits for the deploy's outcome here, so the game keeps ticking. await:false takes a job instead, and wait
		/// collects its outcome (P1.13). No valid cell within the search radius is refused with the candidate reasons.
		/// </summary>
		static string StartDeploy(string cmd, JsonElement root, AgentSelection selection)
		{
			var error = AgentDeploy.Parse(root, out var request);
			if (error != null)
				return Error(cmd, error);

			try
			{
				if (!request.Await)
					return Serialize(AgentMarshal.ExecuteOnGameThread(() => AgentOrderJobs.Start(AgentWorldBridge.RequireWorld(), cmd,
						() => AgentDeploy.ToJob(AgentDeploy.Start(AgentWorldBridge.RequireWorld(), request, selection)))));

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
		/// Reads the job id and optional timeout on the socket thread, then waits for the job there, so the game keeps ticking.
		/// A timeout is a pending reply, not an error: the job stays open for another wait.
		/// </summary>
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
		/// wait with a job is the job's reply (P1.4), with result:"job" at its front. wait with no job long-polls the connection's turn
		/// buffer (P3.4): result is events when an event arrived, which is in this response's events, and timeout otherwise. A
		/// connection-less request (no connection) has nothing to wait for, so it times out at once. The events wait does not hold a
		/// lock the game thread needs; it runs on the connection's thread.
		/// </summary>
		static string Wait(string cmd, JsonElement root, AgentConnection connection)
		{
			var timeoutMs = AgentOrderAwait.WallClockMs;
			if (root.TryGetProperty("timeout_ms", out var timeoutElement) && timeoutElement.ValueKind != JsonValueKind.Null &&
				(!timeoutElement.TryGetInt32(out timeoutMs) || timeoutMs < 0))
				return Error(cmd, "timeout_ms must be a non-negative integer");

			if (!root.TryGetProperty("job", out var jobElement))
			{
				var eventful = connection != null && connection.Turn.WaitForEvents(timeoutMs, AgentOrderAwait.Clock);
				return Serialize(new WaitResultV1(true, cmd, eventful ? "events" : "timeout"));
			}

			if (!jobElement.TryGetInt32(out var job))
				return Error(cmd, "job must be an integer");

			try
			{
				var reply = Serialize(AgentOrderJobs.Wait(job, timeoutMs));
				return string.Concat("{\"result\":\"job\",", reply.AsSpan(1));
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
		/// Moves the snapshot clock in one step. The offset string names the direction by its first character:
		/// "-s" restores the snapshot nearest s seconds before the live tick, "+s" the nearest s seconds after
		/// it, and "@s" the nearest snapshot to the absolute game time s. A missing or unprefixed offset is
		/// refused with the usage. The step runs on the game thread, because a restore swaps the active world;
		/// the socket thread is blocked for its duration.
		/// </summary>
		static string SnapshotStep(string cmd, JsonElement root)
		{
			if (!root.TryGetProperty("offset", out var element) || element.ValueKind != JsonValueKind.String)
				return Error(cmd, Usage);

			var text = element.GetString();
			if (string.IsNullOrEmpty(text) || (text[0] != '-' && text[0] != '+' && text[0] != '@') ||
				!int.TryParse(text.AsSpan(1), out var seconds) || seconds < 0)
				return Error(cmd, Usage);

			var prefix = text[0];
			return Run(cmd, () =>
			{
				var world = AgentWorldBridge.RequireWorld();
				var liveTick = world.WorldTick;
				var deltaTicks = (long)seconds * TicksPerSecond;

				var targetTick = prefix switch
				{
					'@' => deltaTicks,
					'-' => Math.Max(0, liveTick - deltaTicks),
					_ => liveTick + deltaTicks,
				};

				var renderer = AgentWorldBridge.ActiveRenderer;
				if (!WorldForks.RestoreNearest(world, renderer, targetTick * world.Timestep, out var failureReason))
					throw new InvalidOperationException(failureReason);

				// The bridge repoints at the restored world through IWorldAdopted, so this reads the new tick.
				var restoredTick = AgentWorldBridge.RequireWorld().WorldTick;
				var requestedTick = (int)Math.Clamp(targetTick, 0, int.MaxValue);
				return new SnapshotV1(true, cmd, restoredTick, requestedTick, restoredTick - liveTick, WorldForks.Count);
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
