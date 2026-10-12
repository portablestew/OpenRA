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

using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>Loopback TCP listener that speaks newline-delimited JSON with one response line per request.</summary>
	public static class AgentSocketServer
	{
		public const string Banner = "OpenRA agent debug socket. Send one JSON object per line; one response line per command.\n\n";

		static bool started;

		public static void Start(int port)
		{
			if (started)
				return;

			TcpListener listener;
			try
			{
				listener = new TcpListener(IPAddress.Loopback, port);
				listener.Start();
			}
			catch (SocketException ex)
			{
				Log.Write("debug", $"Agent socket could not listen on 127.0.0.1:{port}: {ex.Message}");
				return;
			}

			started = true;
			Log.Write("debug", $"Agent socket listening on 127.0.0.1:{port}");

			new Thread(() => AcceptLoop(listener)) { Name = "Agent socket listener", IsBackground = true }.Start();
		}

		static void AcceptLoop(TcpListener listener)
		{
			while (true)
			{
				TcpClient client;
				try
				{
					client = listener.AcceptTcpClient();
				}
				catch (SocketException)
				{
					return;
				}

				new Thread(() => ServeClient(client)) { Name = "Agent socket connection", IsBackground = true }.Start();
			}
		}

		static void ServeClient(TcpClient client)
		{
			try
			{
				using (client)
				using (var stream = client.GetStream())
				using (var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true))
				using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" })
					Serve(reader, writer);
			}
			catch (IOException) { }
			catch (SocketException) { }
		}

		/// <summary>Writes the banner, then answers each non-blank input line with exactly one response line.</summary>
		public static void Serve(TextReader reader, TextWriter writer)
		{
			writer.Write(Banner);

			string line;
			while ((line = reader.ReadLine()) != null)
			{
				if (line.Trim().Length == 0)
					continue;

				writer.WriteLine(AgentSocketCommands.Handle(line));
			}
		}
	}
}
