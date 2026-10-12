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
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>Loopback TCP listener that speaks newline-delimited JSON with one response line per request.</summary>
	public static class AgentSocketServer
	{
		/// <summary>Generated from the command list and the event table (AgentBanner), so it matches what the server handles.</summary>
		public static readonly string Banner = AgentBanner.Build();

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

		/// <summary>
		/// Writes the banner, then answers each non-blank input line with exactly one response line. Event lines for this
		/// connection's subscriptions come from a second thread, so responses and events share one write lock.
		/// </summary>
		public static void Serve(TextReader reader, TextWriter writer)
		{
			var subscriber = new AgentEventSubscriber();
			var writeGate = new object();
			var events = new Thread(() => PumpEvents(subscriber, writer, writeGate)) { Name = "Agent socket events", IsBackground = true };

			lock (writeGate)
				writer.Write(Banner);

			events.Start();
			try
			{
				string line;
				while ((line = reader.ReadLine()) != null)
				{
					if (line.Trim().Length == 0)
						continue;

					var response = AgentSocketCommands.Handle(line, subscriber);
					lock (writeGate)
						writer.WriteLine(response);
				}
			}
			finally
			{
				subscriber.Close();
				events.Join();
			}
		}

		/// <summary>Serializes queued events on this connection's thread, so the game thread only enqueues DTOs.</summary>
		static void PumpEvents(AgentEventSubscriber subscriber, TextWriter writer, object writeGate)
		{
			try
			{
				while (subscriber.Take() is { } batch)
				{
					var lines = batch.Select(AgentSocketCommands.Serialize).ToArray();
					lock (writeGate)
						foreach (var line in lines)
							writer.WriteLine(line);
				}
			}
			catch (IOException) { }
			catch (ObjectDisposedException) { }
		}
	}
}
