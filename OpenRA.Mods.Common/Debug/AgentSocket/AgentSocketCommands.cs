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

namespace OpenRA.Mods.Common.AgentSocket
{
	public static class AgentSocketCommands
	{
		sealed record Envelope(bool Ok, string Cmd, string Error);
		sealed record PingResponse(bool Ok, string Cmd, int Tick);
		sealed record SubscriptionV1(bool Ok, string Cmd, string[] Subscribed);

		static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

		/// <summary>Serializes a response or event DTO to one JSON line, without the terminator.</summary>
		public static string Serialize(object value)
		{
			return JsonSerializer.Serialize(value, Json);
		}

		/// <summary>
		/// Parses one request line and returns the single response line for it. subscriber is the calling connection's
		/// subscription state; it is null only where no connection exists, and subscribe then returns an error.
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
				if (doc.RootElement.ValueKind != JsonValueKind.Object)
					return Error(null, "expected a JSON object");

				if (!doc.RootElement.TryGetProperty("cmd", out var cmdElement) || cmdElement.ValueKind != JsonValueKind.String)
					return Error(null, "missing cmd");

				var cmd = cmdElement.GetString();
				return cmd switch
				{
					"ping" => Ping(cmd),
					"state" => Run(cmd, () => AgentStateDump.Snapshot(AgentWorldBridge.RequireWorld())),
					"actors" => Actors(cmd, doc.RootElement),
					"maps" => Run(cmd, AgentMaps.List),
					"load_map" => LoadMap(cmd, doc.RootElement),
					"order" => IssueOrder(cmd, doc.RootElement),
					"subscribe" => Subscribe(cmd, doc.RootElement, subscriber),
					"unsubscribe" => Unsubscribe(cmd, doc.RootElement, subscriber),
					_ => Error(cmd, "unknown command"),
				};
			}
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

			return Run(cmd, () => AgentOrder.Issue(AgentWorldBridge.RequireWorld(), request));
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
