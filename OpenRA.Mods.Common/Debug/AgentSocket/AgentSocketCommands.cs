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
using System.Text.Json;
using System.Threading;

namespace OpenRA.Mods.Common.AgentSocket
{
	public static class AgentSocketCommands
	{
		sealed record Envelope(bool Ok, string Cmd, string Error);
		sealed record PingResponse(bool Ok, string Cmd, int Tick);
		sealed record SubscriptionV1(bool Ok, string Cmd, string[] Subscribed);
		sealed record HelpV1(bool Ok, string Cmd, string Banner);

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
			"ping", "help", "state", "actors", "maps", "load_map", "order", "orders", "wait",
			"production", "build", "place", "screenshot", "subscribe", "unsubscribe", "undo",
		];

		static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

		/// <summary>Serializes a response or event DTO to one JSON line, without the terminator.</summary>
		public static string Serialize(object value)
		{
			return JsonSerializer.Serialize(value, Json);
		}

		/// <summary>
		/// Parses one request line and returns the single response line for it. A root array is one request: its items run
		/// in order, and the response is one JSON array with one result per item, so a failing item does not stop the rest.
		/// Items run on the calling thread, so a slow awaited item delays the whole response until it settles or its own
		/// wall-clock limit passes. subscriber is the calling connection's subscription state; it is null only where no
		/// connection exists, and subscribe then returns an error.
		/// </summary>
		public static string Handle(string line, AgentEventSubscriber subscriber)
		{
			JsonDocument doc;
			try
			{
				doc = JsonDocument.Parse(line);
			}
			catch (JsonException)
			{
				return Error(null, "invalid json");
			}

			using (doc)
			{
				if (doc.RootElement.ValueKind == JsonValueKind.Array)
					return "[" + string.Join(",", doc.RootElement.EnumerateArray().Select(item => HandleItem(item, subscriber))) + "]";

				return HandleItem(doc.RootElement, subscriber);
			}
		}

		/// <summary>Answers one request object. A value that is not an object is an error for that item alone.</summary>
		static string HandleItem(JsonElement root, AgentEventSubscriber subscriber)
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
				"state" => Run(cmd, () => AgentStateDump.Snapshot(AgentWorldBridge.RequireWorld())),
				"actors" => Actors(cmd, root),
				"maps" => Run(cmd, AgentMaps.List),
				"load_map" => LoadMap(cmd, root),
				"order" => IssueOrder(cmd, root),
				"orders" => Orders(cmd, root),
				"wait" => Wait(cmd, root),
				"production" => Run(cmd, () => AgentProduction.List(AgentWorldBridge.RequireWorld())),
				"build" => StartBuild(cmd, root),
				"place" => StartPlace(cmd, root),
				"screenshot" => Screenshot(cmd, root),
				"subscribe" => Subscribe(cmd, root, subscriber),
				"unsubscribe" => Unsubscribe(cmd, root, subscriber),
				"undo" => SnapshotStep(cmd, root),
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

		/// <summary>Waits for the new world on the socket thread, so the game thread is never blocked for the 60 s load.</summary>
		static string LoadMap(string cmd, JsonElement root)
		{
			if (!root.TryGetProperty("map", out var element) || element.ValueKind != JsonValueKind.String)
				return Error(cmd, "map must be a string");

			try
			{
				return JsonSerializer.Serialize(AgentLoadMap.Load(element.GetString()), Json);
			}
			catch (Exception e)
			{
				return Error(cmd, e.Message);
			}
		}

		/// <summary>Parses the order on the socket thread, then validates and issues it on the game thread.</summary>
		static string IssueOrder(string cmd, JsonElement root)
		{
			var error = AgentOrder.Parse(root, out var request);
			if (error != null)
				return Error(cmd, error);

			try
			{
				// await:false takes a job instead: the game thread issues the Move and records it, and the reply is the job id.
				if (!request.Await)
					return Serialize(AgentMarshal.ExecuteOnGameThread(() => AgentOrderJobs.Issue(AgentWorldBridge.RequireWorld(), request)));

				// The game thread issues the order. The socket thread then parks until a Move settles or an Attack has its outcome, so the game keeps ticking.
				var issued = AgentMarshal.ExecuteOnGameThread(() => AgentOrder.Issue(AgentWorldBridge.RequireWorld(), request));
				if (issued.Await == null)
					return Serialize(issued.Ack);

				return Serialize(AgentOrder.Settle(issued));
			}
			catch (OrderRefusedException e)
			{
				return Serialize(new OrderRefusedV1(false, cmd, e.Message, e.Available));
			}
			catch (Exception e)
			{
				return Error(cmd, e.Message);
			}
		}

		/// <summary>Parses the item and count on the socket thread, then validates and issues the build on the game thread.</summary>
		static string StartBuild(string cmd, JsonElement root)
		{
			var error = AgentBuild.Parse(root, out var request);
			if (error != null)
				return Error(cmd, error);

			try
			{
				return Serialize(AgentMarshal.ExecuteOnGameThread(() => AgentBuild.Start(AgentWorldBridge.RequireWorld(), request)));
			}
			catch (BuildRefusedException e)
			{
				return Serialize(new BuildRefusedV1(false, cmd, e.Message, e.Available));
			}
			catch (OrderRefusedException e)
			{
				return Serialize(new OrderRefusedV1(false, cmd, e.Message, e.Available));
			}
			catch (Exception e)
			{
				return Error(cmd, e.Message);
			}
		}

		/// <summary>Parses the item and optional cell on the socket thread, then finds the cell and issues the placement on the game thread.</summary>
		static string StartPlace(string cmd, JsonElement root)
		{
			var error = AgentPlace.Parse(root, out var request);
			if (error != null)
				return Error(cmd, error);

			return Run(cmd, () => AgentPlace.Start(AgentWorldBridge.RequireWorld(), request));
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
		static string Wait(string cmd, JsonElement root)
		{
			if (!root.TryGetProperty("job", out var jobElement) || !jobElement.TryGetInt32(out var job))
				return Error(cmd, "job must be an integer");

			var timeoutMs = AgentOrderAwait.WallClockMs;
			if (root.TryGetProperty("timeout_ms", out var timeoutElement) && timeoutElement.ValueKind != JsonValueKind.Null &&
				(!timeoutElement.TryGetInt32(out timeoutMs) || timeoutMs < 0))
				return Error(cmd, "timeout_ms must be a non-negative integer");

			try
			{
				return Serialize(AgentOrderJobs.Wait(job, timeoutMs));
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

		/// <summary>Adds the named event groups. Runs on the connection thread: the subscription state is not game state.</summary>
		static string Subscribe(string cmd, JsonElement root, AgentEventSubscriber subscriber)
		{
			if (subscriber == null)
				return Error(cmd, "events need a connection");

			if (!root.TryGetProperty("events", out var element) || element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
				return Error(cmd, "events must be a non-empty array of group names");

			if (!TryGetGroups(element, out var names, out var error))
				return Error(cmd, error);

			if (!TryGetOptionalString(root, "owner", out var owner))
				return Error(cmd, "owner must be a string");

			return Serialize(new SubscriptionV1(true, cmd, subscriber.Subscribe(names, owner)));
		}

		/// <summary>Removes the named event groups, or every group with {"all":true}.</summary>
		static string Unsubscribe(string cmd, JsonElement root, AgentEventSubscriber subscriber)
		{
			if (subscriber == null)
				return Error(cmd, "events need a connection");

			if (root.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.True)
				return Serialize(new SubscriptionV1(true, cmd, subscriber.Unsubscribe(null)));

			if (!root.TryGetProperty("events", out var element) || element.ValueKind != JsonValueKind.Array)
				return Error(cmd, "unsubscribe needs events, or all:true");

			if (!TryGetGroups(element, out var names, out var error))
				return Error(cmd, error);

			return Serialize(new SubscriptionV1(true, cmd, subscriber.Unsubscribe(names)));
		}

		/// <summary>Checks every name before any is applied, so a bad name changes nothing.</summary>
		static bool TryGetGroups(JsonElement element, out string[] names, out string error)
		{
			names = null;
			error = null;
			var list = new List<string>();
			foreach (var item in element.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.String)
				{
					error = "event group names must be strings";
					return false;
				}

				var name = item.GetString();
				if (!AgentEvents.Groups.Contains(name))
				{
					error = $"unknown event group '{name}'";
					return false;
				}

				list.Add(name);
			}

			names = list.ToArray();
			return true;
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
			return JsonSerializer.Serialize(new Envelope(false, cmd, error), Json);
		}
	}
}
