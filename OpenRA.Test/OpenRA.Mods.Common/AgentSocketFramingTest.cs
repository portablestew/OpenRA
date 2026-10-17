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
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using OpenRA.Mods.Common.AgentSocket;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class AgentSocketFramingTest
	{
		/// <summary>The hint key and value every error envelope carries (P1.18), as the JSON text that follows the error field.</summary>
		const string HintJson = "\"hint\":\"see {\\u0022cmd\\u0022:\\u0022help\\u0022}\"}";

		/// <summary>The keys of the turn envelope (P1.16), in the order the server writes them.</summary>
		static readonly string[] EnvelopeKeys = ["results", "events", "state", "tick", "map_state_png", "map_state_tick", "map_state_render",
			"map_px_per_cell", "map_origin", "map_size"];

		static string[] Run(string input)
		{
			using var reader = new StringReader(input);
			using var writer = new StringWriter { NewLine = "\n" };
			AgentSocketServer.Serve(reader, writer);
			var output = writer.ToString();
			Assert.That(output.StartsWith(AgentSocketServer.Banner, StringComparison.Ordinal), Is.True);
			return Lines(output[AgentSocketServer.Banner.Length..]);
		}

		/// <summary>The non-empty lines of a served tail, with the lone "&gt;" prompt lines removed.</summary>
		static string[] Lines(string tail)
		{
			return tail.Split('\n', StringSplitOptions.RemoveEmptyEntries)
				.Where(l => l != AgentSocketServer.Prompt)
				.ToArray();
		}

		/// <summary>The results raw text of one envelope line. With no world loaded, tick and state are null.</summary>
		static string ResultsOf(string envelope)
		{
			using var doc = JsonDocument.Parse(envelope);
			Assert.That(doc.RootElement.EnumerateObject().Select(p => p.Name), Is.EqualTo(EnvelopeKeys));
			return doc.RootElement.GetProperty("results").GetRawText();
		}

		[TestCase(TestName = "The banner is sent first, then the prompt, and the banner ends with a blank line")]
		public void BannerIsSentFirst()
		{
			using var reader = new StringReader("");
			using var writer = new StringWriter { NewLine = "\n" };
			AgentSocketServer.Serve(reader, writer);

			Assert.That(writer.ToString(), Is.EqualTo(AgentSocketServer.Banner + AgentSocketServer.Prompt + "\n"));
			Assert.That(AgentSocketServer.Banner.EndsWith("\n\n", StringComparison.Ordinal), Is.True);
		}

		[TestCase(TestName = "Every response line is a turn envelope, followed by a lone > prompt line")]
		public void PromptAfterEachResponse()
		{
			using var reader = new StringReader("{\"cmd\":\"nope\"}\n{\"x\":1}\n");
			using var writer = new StringWriter { NewLine = "\n" };
			AgentSocketServer.Serve(reader, writer);

			var tail = writer.ToString()[AgentSocketServer.Banner.Length..];
			var lines = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries);
			Assert.That(lines.Length, Is.EqualTo(5));
			Assert.That(lines[0], Is.EqualTo(AgentSocketServer.Prompt));
			Assert.That(ResultsOf(lines[1]), Is.EqualTo("{\"ok\":false,\"cmd\":\"nope\",\"error\":\"unknown command\"," + HintJson));
			Assert.That(lines[2], Is.EqualTo(AgentSocketServer.Prompt));
			Assert.That(ResultsOf(lines[3]), Is.EqualTo("{\"ok\":false,\"cmd\":null,\"error\":\"missing cmd\"," + HintJson));
			Assert.That(lines[4], Is.EqualTo(AgentSocketServer.Prompt));
		}

		[TestCase(TestName = "Each non-blank line gets exactly one response; blank lines get none; CRLF is accepted")]
		public void OneResponsePerLine()
		{
			var lines = Run("{bad\n\n   \n{\"cmd\":\"nope\"}\r\n[1]\n{\"x\":1}\n");

			Assert.That(lines.Length, Is.EqualTo(4));
			Assert.That(ResultsOf(lines[0]), Is.EqualTo("{\"ok\":false,\"cmd\":null,\"error\":\"invalid json\"," + HintJson));
			Assert.That(ResultsOf(lines[1]), Is.EqualTo("{\"ok\":false,\"cmd\":\"nope\",\"error\":\"unknown command\"," + HintJson));
			Assert.That(ResultsOf(lines[2]), Is.EqualTo("[{\"ok\":false,\"cmd\":null,\"error\":\"expected a JSON object\"," + HintJson + "]"));
			Assert.That(ResultsOf(lines[3]), Is.EqualTo("{\"ok\":false,\"cmd\":null,\"error\":\"missing cmd\"," + HintJson));
		}

		[TestCase(TestName = "A root array is one request: one envelope holds a result per item, and a failing item does not stop the rest")]
		public void ArrayIsOneRequest()
		{
			using var reader = new StringReader("[{\"cmd\":\"ping\"},{\"cmd\":\"bogus\"},{\"cmd\":\"ping\"}]\n");
			using var writer = new StringWriter { NewLine = "\n" };
			AgentSocketServer.Serve(reader, writer);

			// The banner's prompt, then the one envelope line, then the prompt for the next command.
			var tail = writer.ToString()[AgentSocketServer.Banner.Length..];
			var lines = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries);
			Assert.That(lines, Has.Length.EqualTo(3));
			Assert.That(lines[0], Is.EqualTo(AgentSocketServer.Prompt));
			Assert.That(lines[2], Is.EqualTo(AgentSocketServer.Prompt));

			using var doc = JsonDocument.Parse(lines[1]);
			var results = doc.RootElement.GetProperty("results").EnumerateArray().ToArray();
			Assert.That(results.Length, Is.EqualTo(3));
			Assert.That(results[0].GetProperty("cmd").GetString(), Is.EqualTo("ping"));
			Assert.That(results[1].GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(results[1].GetProperty("error").GetString(), Is.EqualTo("unknown command"));
			Assert.That(results[2].GetProperty("cmd").GetString(), Is.EqualTo("ping"));
		}

		[TestCase(TestName = "A final line without a terminating newline is still answered")]
		public void UnterminatedFinalLine()
		{
			var lines = Run("{\"cmd\":\"nope\"}").Last();

			Assert.That(ResultsOf(lines), Is.EqualTo("{\"ok\":false,\"cmd\":\"nope\",\"error\":\"unknown command\"," + HintJson));
		}
	}
}
