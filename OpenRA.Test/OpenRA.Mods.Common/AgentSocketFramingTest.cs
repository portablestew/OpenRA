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

		[TestCase(TestName = "The banner is sent first, then the prompt, and the banner ends with a blank line")]
		public void BannerIsSentFirst()
		{
			using var reader = new StringReader("");
			using var writer = new StringWriter { NewLine = "\n" };
			AgentSocketServer.Serve(reader, writer);

			Assert.That(writer.ToString(), Is.EqualTo(AgentSocketServer.Banner + AgentSocketServer.Prompt + "\n"));
			Assert.That(AgentSocketServer.Banner.EndsWith("\n\n", StringComparison.Ordinal), Is.True);
		}

		[TestCase(TestName = "Every response line is followed by a lone > prompt line")]
		public void PromptAfterEachResponse()
		{
			using var reader = new StringReader("{\"cmd\":\"nope\"}\n{\"x\":1}\n");
			using var writer = new StringWriter { NewLine = "\n" };
			AgentSocketServer.Serve(reader, writer);

			var tail = writer.ToString()[AgentSocketServer.Banner.Length..];
			Assert.That(tail.Split('\n', StringSplitOptions.RemoveEmptyEntries), Is.EqualTo(new[]
			{
				AgentSocketServer.Prompt,
				"{\"ok\":false,\"cmd\":\"nope\",\"error\":\"unknown command\"}",
				AgentSocketServer.Prompt,
				"{\"ok\":false,\"cmd\":null,\"error\":\"missing cmd\"}",
				AgentSocketServer.Prompt,
			}));
		}

		[TestCase(TestName = "Each non-blank line gets exactly one response; blank lines get none; CRLF is accepted")]
		public void OneResponsePerLine()
		{
			var lines = Run("{bad\n\n   \n{\"cmd\":\"nope\"}\r\n[1]\n{\"x\":1}\n");

			Assert.That(lines, Is.EqualTo(new[]
			{
				"{\"ok\":false,\"cmd\":null,\"error\":\"invalid json\"}",
				"{\"ok\":false,\"cmd\":\"nope\",\"error\":\"unknown command\"}",
				"[{\"ok\":false,\"cmd\":null,\"error\":\"expected a JSON object\"}]",
				"{\"ok\":false,\"cmd\":null,\"error\":\"missing cmd\"}",
			}));
		}

		[TestCase(TestName = "A root array is one request: one line holds a result per item, and a failing item does not stop the rest")]
		public void ArrayIsOneRequest()
		{
			using var reader = new StringReader("[{\"cmd\":\"ping\"},{\"cmd\":\"bogus\"},{\"cmd\":\"ping\"}]\n");
			using var writer = new StringWriter { NewLine = "\n" };
			AgentSocketServer.Serve(reader, writer);

			var tail = writer.ToString()[AgentSocketServer.Banner.Length..];
			// The banner's prompt, then the one response line, then the prompt for the next command.
			var response = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries);
			Assert.That(response, Has.Length.EqualTo(3));
			Assert.That(response[0], Is.EqualTo(AgentSocketServer.Prompt));
			Assert.That(response[2], Is.EqualTo(AgentSocketServer.Prompt));

			using var doc = JsonDocument.Parse(response[1]);
			var results = doc.RootElement.EnumerateArray().ToArray();
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

			Assert.That(lines, Is.EqualTo("{\"ok\":false,\"cmd\":\"nope\",\"error\":\"unknown command\"}"));
		}
	}
}
