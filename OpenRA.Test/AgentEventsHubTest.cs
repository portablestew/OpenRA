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
using NUnit.Framework;
using OpenRA.Mods.Common.AgentSocket;

namespace OpenRA.Test
{
	/// <summary>
	/// The event hub (P3.6). Every open connection receives every raised event in its turn buffer, and a closed connection
	/// receives nothing. There are no subscriptions, so the hub has no groups or owner filters to test.
	/// </summary>
	[TestFixture]
	sealed class AgentEventsHubTest
	{
		sealed record TestEvent(string Event, int Tick, int N);

		bool savedEnabled;
		Action<object> savedSink;
		readonly List<AgentConnection> connections = [];

		[SetUp]
		public void EnableHub()
		{
			savedEnabled = AgentEvents.Enabled;
			savedSink = AgentEvents.Sink;
			AgentEvents.Enabled = true;
		}

		[TearDown]
		public void RestoreHub()
		{
			foreach (var connection in connections)
				connection.Close();

			connections.Clear();
			AgentEvents.Sink = savedSink;
			AgentEvents.Enabled = savedEnabled;
		}

		AgentConnection NewConnection()
		{
			var connection = new AgentConnection();
			connections.Add(connection);
			return connection;
		}

		[TestCase(TestName = "A raised event reaches every open connection, in raise order")]
		public void RaiseReachesEveryConnection()
		{
			var first = NewConnection();
			var second = NewConnection();

			AgentEvents.Raise(new TestEvent("damaged", 1, 1));
			AgentEvents.Raise(new TestEvent("killed", 2, 2));

			var expected = new object[] { new TestEvent("damaged", 1, 1), new TestEvent("killed", 2, 2) };
			Assert.That(first.Flush(), Is.EqualTo(expected), "The first connection should receive both events, in order.");
			Assert.That(second.Flush(), Is.EqualTo(expected), "The second connection should receive both events, in order.");
		}

		[TestCase(TestName = "A closed connection receives nothing raised after it closed, and leaves the connection count")]
		public void ClosedConnectionReceivesNothing()
		{
			var before = AgentEvents.ConnectionCount;
			var closing = NewConnection();
			var open = NewConnection();
			Assert.That(AgentEvents.ConnectionCount, Is.EqualTo(before + 2), "Two open connections should be counted.");

			closing.Close();
			AgentEvents.Raise(new TestEvent("damaged", 1, 1));

			Assert.That(closing.Flush(), Is.Empty, "A closed connection should receive nothing.");
			Assert.That(open.Flush(), Has.Count.EqualTo(1), "An open connection should still receive the event.");
			Assert.That(AgentEvents.ConnectionCount, Is.EqualTo(before + 1), "A closed connection should leave the count.");
		}

		[TestCase(TestName = "With the socket disabled, Raise delivers nothing to any connection")]
		public void DisabledSocketDeliversNothing()
		{
			var connection = NewConnection();
			AgentEvents.Enabled = false;

			AgentEvents.Raise(new TestEvent("damaged", 1, 1));

			Assert.That(connection.Flush(), Is.Empty, "With the socket disabled, no event should reach a connection.");
		}

		[TestCase(TestName = "The test sink sees every raised event, with no connection open")]
		public void SinkSeesEventsWithNoConnection()
		{
			var seen = new List<object>();
			AgentEvents.Sink = seen.Add;

			AgentEvents.Raise(new TestEvent("path", 3, 1));

			Assert.That(seen, Is.EqualTo(new object[] { new TestEvent("path", 3, 1) }),
				"The sink should see the event even when no connection is open.");
		}

		[TestCase(TestName = "A connection's buffer is empty until an event is raised after it opened")]
		public void NewConnectionStartsEmpty()
		{
			AgentEvents.Raise(new TestEvent("damaged", 1, 1));

			var connection = NewConnection();

			Assert.That(connection.Pending, Is.Zero, "An event raised before a connection opened should not be in its buffer.");
			Assert.That(connection.Flush(), Is.Empty);
		}
	}
}
