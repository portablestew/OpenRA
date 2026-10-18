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

using System.Linq;
using NUnit.Framework;
using OpenRA.Mods.Common.AgentSocket;

namespace OpenRA.Test
{
	/// <summary>
	/// The turn buffer's flush (P3.2): the first three of each type in full, one aggregated marker for the rest, never
	/// aggregating killed, defeated, or game_over, the overflow marker surviving a flush, and a stable order. The events are
	/// synthetic records with the Event and Tick fields every event DTO has.
	/// </summary>
	[TestFixture]
	sealed class AgentTurnBufferTest
	{
		sealed record Fake(string Event, int Tick, int Id);

		static Fake Damaged(int tick, int id) => new("damaged", tick, id);

		static Fake Killed(int tick, int id) => new("killed", tick, id);

		[TestCase(TestName = "Five events of one type flush as three in full and one aggregated marker with the rest's ticks")]
		public void FifthAndFourthAggregate()
		{
			var buffer = new AgentTurnBuffer();
			var fives = Enumerable.Range(1, 5).Select(i => Damaged(9 + i, i)).ToList();
			foreach (var e in fives)
				buffer.Add(e);

			var batch = buffer.Flush();

			Assert.That(batch, Has.Count.EqualTo(4), "Three in full, then one marker for the two suppressed.");
			Assert.That(batch.Take(3), Is.EqualTo(fives.Take(3)), "The first three should flush in full, in arrival order.");
			Assert.That(batch[3], Is.EqualTo(new AggregatedV1("damaged", 2, 13, 14)),
				"The marker should count the two suppressed events and cover their ticks, 13 to 14.");
		}

		[TestCase(TestName = "Ten killed events flush as ten, and none is aggregated")]
		public void KilledIsNeverAggregated()
		{
			var buffer = new AgentTurnBuffer();
			var kills = Enumerable.Range(1, 10).Select(i => Killed(i, i)).ToList();
			foreach (var e in kills)
				buffer.Add(e);

			var batch = buffer.Flush();

			Assert.That(batch.OfType<AggregatedV1>(), Is.Empty, "killed events should never be aggregated.");
			Assert.That(batch, Is.EqualTo(kills), "All ten killed events should flush in full, in arrival order.");
		}

		[TestCase(TestName = "The overflow marker survives a flush, comes first, and is reported once")]
		public void OverflowSurvivesFlush()
		{
			var buffer = new AgentTurnBuffer();
			for (var i = 0; i < AgentTurnBuffer.Capacity + 2; i++)
				buffer.Add(Damaged(i, i));

			var batch = buffer.Flush();
			Assert.That(batch[0], Is.EqualTo(new OverflowV1("overflow", 2)),
				"Two events were dropped when the buffer was full, and the flush should say so first.");

			buffer.Add(Damaged(0, 0));
			var next = buffer.Flush();
			Assert.That(next.OfType<OverflowV1>(), Is.Empty, "The dropped count should reset after one flush.");
		}

		[TestCase(TestName = "Event order is stable: full events keep arrival order, and a marker sits where its first suppressed event arrived")]
		public void OrderIsStable()
		{
			var buffer = new AgentTurnBuffer();
			var d1 = Damaged(10, 1);
			var k1 = Killed(10, 2);
			var d2 = Damaged(11, 3);
			var d3 = Damaged(11, 4);
			var d4 = Damaged(12, 5);
			var p1 = new Fake("path", 12, 6);
			var d5 = Damaged(13, 7);
			var k2 = Killed(13, 8);
			foreach (var e in new[] { d1, k1, d2, d3, d4, p1, d5, k2 })
				buffer.Add(e);

			var batch = buffer.Flush();

			var expected = new object[] { d1, k1, d2, d3, new AggregatedV1("damaged", 2, 12, 13), p1, k2 };
			Assert.That(batch, Is.EqualTo(expected),
				"The turn should flush in arrival order, with the damaged marker where d4 arrived.");
			Assert.That(buffer.Pending, Is.Zero, "A flush should empty the buffer.");
			Assert.That(buffer.Flush(), Is.Empty, "A second flush with nothing added should be empty.");
		}

		[TestCase(TestName = "A payload with no Event and Tick field is not an event, so the buffer skips it")]
		public void NonEventsAreSkipped()
		{
			var buffer = new AgentTurnBuffer();
			buffer.Add("a reply line");
			buffer.Add(42);

			Assert.That(buffer.Pending, Is.Zero, "Only event payloads should be buffered.");
			Assert.That(buffer.Flush(), Is.Empty);
		}
	}
}
