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
using System.Text.Json;

namespace OpenRA.Mods.Common.AgentSocket
{
	public static class AgentSocketCommands
	{
		sealed record Envelope(bool Ok, string Cmd, string Error);
		sealed record PingResponse(bool Ok, string Cmd, int Tick);

		static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

		/// <summary>Parses one request line and returns the single response line for it.</summary>
		public static string Handle(string line)
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
					_ => Error(cmd, "unknown command"),
				};
			}
		}

		static string Ping(string cmd)
		{
			return Run(cmd, () => new PingResponse(true, cmd, AgentWorldBridge.RequireWorld().WorldTick));
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
