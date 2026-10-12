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
			return output[AgentSocketServer.Banner.Length..].Split('\n', StringSplitOptions.RemoveEmptyEntries);
		}

		[TestCase(TestName = "The banner is sent before any response, and ends with a blank line")]
		public void BannerIsSentFirst()
		{
			using var reader = new StringReader("");
			using var writer = new StringWriter { NewLine = "\n" };
			AgentSocketServer.Serve(reader, writer);

			Assert.That(writer.ToString(), Is.EqualTo(AgentSocketServer.Banner));
			Assert.That(AgentSocketServer.Banner.EndsWith("\n\n", StringComparison.Ordinal), Is.True);
		}

		[TestCase(TestName = "Each non-blank line gets exactly one response; blank lines get none; CRLF is accepted")]
		public void OneResponsePerLine()
		{
			var lines = Run("{bad\n\n   \n{\"cmd\":\"nope\"}\r\n[1]\n{\"x\":1}\n");

			Assert.That(lines, Is.EqualTo(new[]
			{
				"{\"ok\":false,\"cmd\":null,\"error\":\"invalid json\"}",
				"{\"ok\":false,\"cmd\":\"nope\",\"error\":\"unknown command\"}",
				"{\"ok\":false,\"cmd\":null,\"error\":\"expected a JSON object\"}",
				"{\"ok\":false,\"cmd\":null,\"error\":\"missing cmd\"}",
			}));
		}

		[TestCase(TestName = "A final line without a terminating newline is still answered")]
		public void UnterminatedFinalLine()
		{
			var lines = Run("{\"cmd\":\"nope\"}").Last();

			Assert.That(lines, Is.EqualTo("{\"ok\":false,\"cmd\":\"nope\",\"error\":\"unknown command\"}"));
		}
	}
}
