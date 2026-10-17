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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenRA.Mods.Common.AgentSocket;

namespace OpenRA.Test
{
	/// <summary>
	/// The event hub, its per-connection queue, and the subscribe/unsubscribe wire replies. Serve runs end to end over
	/// in-memory readers and writers, so the event thread and the response lock are exercised without a socket.
	/// </summary>
	[TestFixture]
	sealed class AgentEventsHubTest
	{
		sealed record TestEvent(string Event, int N);

		bool savedEnabled;
		readonly List<AgentEventSubscriber> subscribers = [];

		[SetUp]
		public void EnableHub()
		{
			savedEnabled = AgentEvents.Enabled;
			AgentEvents.Enabled = true;
		}

		[TearDown]
		public void RestoreHub()
		{
			foreach (var subscriber in subscribers)
				subscriber.Close();

			subscribers.Clear();
			AgentEvents.Enabled = savedEnabled;
		}

		AgentEventSubscriber NewSubscriber()
		{
			var subscriber = new AgentEventSubscriber();
			subscribers.Add(subscriber);
			return subscriber;
		}

		[TestCase(TestName = "A raise with no subscriber leaves every queue empty")]
		public void RaiseWithNoSubscriberLeavesQueueEmpty()
		{
			Assert.That(AgentEvents.SubscriberCount, Is.Zero, "Another test left a subscriber registered.");

			var idle = NewSubscriber();
			for (var i = 0; i < 10; i++)
				AgentEvents.Raise("damage", new TestEvent("damaged", i));

			Assert.That(AgentEvents.SubscriberCount, Is.Zero);
			Assert.That(idle.Pending, Is.Zero);
		}

		[TestCase(TestName = "A subscriber receives only the groups it subscribed to")]
		public void SubscriberReceivesOnlyItsGroups()
		{
			var killedOnly = NewSubscriber();
			killedOnly.Subscribe(["killed"]);

			AgentEvents.Raise("damage", new TestEvent("damaged", 1));
			AgentEvents.Raise("killed", new TestEvent("killed", 2));

			Assert.That(AgentEvents.SubscriberCount, Is.EqualTo(1));
			Assert.That(killedOnly.Pending, Is.EqualTo(1));
		}

		[TestCase(TestName = "A full queue drops the oldest events and emits one overflow marker with the drop count")]
		public void OverflowDropsOldestAndEmitsOneMarker()
		{
			var subscriber = NewSubscriber();
			subscriber.Subscribe(["damage"]);

			const int Dropped = 10;
			for (var i = 0; i < AgentEventSubscriber.Capacity + Dropped; i++)
				AgentEvents.Raise("damage", new TestEvent("damaged", i));

			Assert.That(subscriber.Pending, Is.EqualTo(AgentEventSubscriber.Capacity));

			var batch = subscriber.Take();
			Assert.That(batch, Has.Count.EqualTo(AgentEventSubscriber.Capacity + 1));
			Assert.That(AgentSocketCommands.Serialize(batch[0]), Is.EqualTo("{\"event\":\"overflow\",\"dropped\":10}"));
			Assert.That(((TestEvent)batch[1]).N, Is.EqualTo(Dropped), "The oldest events are the ones dropped.");
			Assert.That(((TestEvent)batch[^1]).N, Is.EqualTo(AgentEventSubscriber.Capacity + Dropped - 1));

			// A second burst reports only its own drops, in a single marker.
			for (var i = 0; i < AgentEventSubscriber.Capacity + 3; i++)
				AgentEvents.Raise("damage", new TestEvent("damaged", i));

			var second = subscriber.Take();
			Assert.That(AgentSocketCommands.Serialize(second[0]), Is.EqualTo("{\"event\":\"overflow\",\"dropped\":3}"));
			Assert.That(second.Skip(1).Count(e => e is OverflowV1), Is.Zero);
		}

		[TestCase(TestName = "Unsubscribing every group restores the subscriber count and stops delivery")]
		public void UnsubscribeRestoresZeroCount()
		{
			Assert.That(AgentEvents.SubscriberCount, Is.Zero);

			var subscriber = NewSubscriber();
			Assert.That(subscriber.Subscribe(["damage", "killed"]), Is.EqualTo(new[] { "damage", "killed" }));
			Assert.That(AgentEvents.SubscriberCount, Is.EqualTo(1));

			Assert.That(subscriber.Unsubscribe(null), Is.Empty);
			Assert.That(AgentEvents.SubscriberCount, Is.Zero);

			AgentEvents.Raise("damage", new TestEvent("damaged", 1));
			Assert.That(subscriber.Pending, Is.Zero);
		}

		[TestCase(TestName = "An owner filter passes only that owner's events and events with no owner, for its own groups only")]
		public void OwnerFilterIsPerGroup()
		{
			var subscriber = NewSubscriber();
			subscriber.Subscribe(["damage"], "Player");
			subscriber.Subscribe(["killed"]);

			AgentEvents.Raise("damage", "Enemy", new TestEvent("damaged", 1));
			AgentEvents.Raise("damage", "Player", new TestEvent("damaged", 2));
			AgentEvents.Raise("damage", null, new TestEvent("damaged", 3));
			AgentEvents.Raise("killed", "Enemy", new TestEvent("killed", 4));

			var batch = subscriber.Take();
			Assert.That(batch.Cast<TestEvent>().Select(e => e.N), Is.EqualTo(new[] { 2, 3, 4 }),
				"The later subscribe to killed must not change damage's filter, and killed has no filter.");

			subscriber.Subscribe(["damage"]);
			AgentEvents.Raise("damage", "Enemy", new TestEvent("damaged", 5));
			Assert.That(subscriber.Take().Cast<TestEvent>().Select(e => e.N), Is.EqualTo(new[] { 5 }),
				"Re-subscribing a group without an owner replaces its filter.");

			subscriber.Unsubscribe(["damage"]);
			subscriber.Subscribe(["damage"]);
			AgentEvents.Raise("damage", "Enemy", new TestEvent("damaged", 6));
			Assert.That(subscriber.Take().Cast<TestEvent>().Select(e => e.N), Is.EqualTo(new[] { 6 }),
				"Unsubscribing a group removes its filter.");
		}

		/// <summary>The results of a handled request: the envelope's results, which for an object request is its one result.</summary>
		static string Result(string response)
		{
			using var doc = JsonDocument.Parse(response);
			return doc.RootElement.GetProperty("results").GetRawText();
		}

		[TestCase(TestName = "Subscribe and unsubscribe reply with the active groups in canonical order")]
		public void SubscribeRepliesWithActiveGroups()
		{
			var subscriber = NewSubscriber();

			Assert.That(Result(AgentSocketCommands.Handle("{\"cmd\":\"subscribe\",\"events\":[\"path\",\"damage\"]}", subscriber)),
				Is.EqualTo("{\"ok\":true,\"cmd\":\"subscribe\",\"subscribed\":[\"damage\",\"path\"]}"));
			Assert.That(Result(AgentSocketCommands.Handle("{\"cmd\":\"unsubscribe\",\"events\":[\"damage\"]}", subscriber)),
				Is.EqualTo("{\"ok\":true,\"cmd\":\"unsubscribe\",\"subscribed\":[\"path\"]}"));
			Assert.That(Result(AgentSocketCommands.Handle("{\"cmd\":\"unsubscribe\",\"all\":true}", subscriber)),
				Is.EqualTo("{\"ok\":true,\"cmd\":\"unsubscribe\",\"subscribed\":[]}"));
			Assert.That(AgentEvents.SubscriberCount, Is.Zero);
		}

		[TestCase(TestName = "A subscribe with an unknown group is refused and changes nothing")]
		public void UnknownGroupChangesNothing()
		{
			var subscriber = NewSubscriber();

			using var response = JsonDocument.Parse(Result(AgentSocketCommands.Handle("{\"cmd\":\"subscribe\",\"events\":[\"damage\",\"nope\"]}", subscriber)));
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo("unknown event group 'nope'"));
			Assert.That(subscriber.Active(), Is.Empty);
			Assert.That(AgentEvents.SubscriberCount, Is.Zero);
		}

		[TestCase(TestName = "Subscribe without a connection returns an error")]
		public void SubscribeWithoutConnectionIsAnError()
		{
			Assert.That(Result(AgentSocketCommands.Handle("{\"cmd\":\"subscribe\",\"events\":[\"damage\"]}", null)),
				Is.EqualTo("{\"ok\":false,\"cmd\":\"subscribe\",\"error\":\"events need a connection\",\"hint\":\"see {\\u0022cmd\\u0022:\\u0022help\\u0022}\"}"));
		}

		[TestCase(TestName = "A subscribed connection receives its events as lines, and ends cleanly when its input closes")]
		public void ServeDeliversSubscribedEventsAsLines()
		{
			var reader = new LineSource();
			var writer = new CollectingWriter();
			var serve = Task.Run(() => AgentSocketServer.Serve(reader, writer));

			reader.Add("{\"cmd\":\"subscribe\",\"events\":[\"damage\"]}");
			WaitUntil(() => writer.Lines.Any(l => l.Contains("\"subscribed\":[\"damage\"]")));

			AgentEvents.Raise("damage", new TestEvent("damaged", 7));
			WaitUntil(() => writer.Lines.Any(l => l == "{\"event\":\"damaged\",\"n\":7}"));

			reader.Complete();
			Assert.That(serve.Wait(TimeSpan.FromSeconds(5)), Is.True, "Serve did not return after its input closed.");
			Assert.That(AgentEvents.SubscriberCount, Is.Zero, "The connection's subscription outlived it.");
		}

		static void WaitUntil(Func<bool> condition)
		{
			var deadline = DateTime.UtcNow.AddSeconds(5);
			while (!condition() && DateTime.UtcNow < deadline)
				Thread.Sleep(5);

			Assert.That(condition(), Is.True, "The expected line did not arrive.");
		}

		/// <summary>A reader whose lines are added by the test. ReadLine blocks until one is available or the input completes.</summary>
		sealed class LineSource : TextReader
		{
			readonly BlockingCollection<string> lines = [];

			public void Add(string line) => lines.Add(line);

			public void Complete() => lines.CompleteAdding();

			public override string ReadLine()
			{
				try
				{
					return lines.Take();
				}
				catch (InvalidOperationException)
				{
					return null;
				}
			}
		}

		/// <summary>A writer that keeps every line for the test to inspect, from any thread.</summary>
		sealed class CollectingWriter : TextWriter
		{
			public readonly ConcurrentQueue<string> Lines = new();

			public override Encoding Encoding => Encoding.UTF8;

			public override void Write(string value)
			{
				if (value != null)
					Lines.Enqueue(value);
			}

			public override void WriteLine(string value)
			{
				Lines.Enqueue(value ?? string.Empty);
			}
		}
	}
}
