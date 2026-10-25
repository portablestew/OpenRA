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
using System.Text.Json;
using NUnit.Framework;
using OpenRA.Mods.Common.AgentSocket;

namespace OpenRA.Test
{
	/// <summary>
	/// The turn buffer's flush (P3.2): the first two of each type and name in full, one aggregated marker for the rest, never
	/// aggregating killed, defeated, or game_over, the overflow marker surviving a flush, and a stable order. The events are
	/// synthetic records with the Event and Tick fields every event DTO has.
	/// </summary>
	[TestFixture]
	sealed class AgentTurnBufferTest
	{
		static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

		/// <summary>The wire form of an event DTO. Records compare their actors array by reference, so markers are compared through it.</summary>
		static string Wire(object dto) => JsonSerializer.Serialize(dto, Json);

		sealed record Fake(string Event, int Tick, int Id);

		/// <summary>An event that names its actor, as the event DTOs do.</summary>
		sealed record Hit(string Event, int Tick, uint Actor);

		static Fake Damaged(int tick, int id) => new("damaged", tick, id);

		static Fake Killed(int tick, int id) => new("killed", tick, id);

		static Hit Damaged(int tick, uint actor) => new("damaged", tick, actor);

		[TestCase(TestName = "The third event of one type flushes as an aggregated marker, not in full")]
		public void ThirdEventAggregates()
		{
			var buffer = new AgentTurnBuffer();
			var threes = Enumerable.Range(1, 3).Select(i => Damaged(9 + i, i)).ToList();
			foreach (var e in threes)
				buffer.Add(e);

			var batch = buffer.Flush();

			Assert.That(batch, Has.Count.EqualTo(3), "Two in full, then one marker for the third.");
			Assert.That(batch.Take(2), Is.EqualTo(threes.Take(2)), "The first two should flush in full, in arrival order.");
			Assert.That(Wire(batch[2]), Is.EqualTo(Wire(new AggregatedV1("damaged", 1, 12, 12, []))),
				"The marker should count the third event and cover its tick, 12.");
		}

		[TestCase(TestName = "Five events of one type flush as two in full and one aggregated marker with the rest's ticks")]
		public void FifthAndFourthAggregate()
		{
			var buffer = new AgentTurnBuffer();
			var fives = Enumerable.Range(1, 5).Select(i => Damaged(9 + i, i)).ToList();
			foreach (var e in fives)
				buffer.Add(e);

			var batch = buffer.Flush();

			Assert.That(batch, Has.Count.EqualTo(3), "Two in full, then one marker for the three suppressed.");
			Assert.That(batch.Take(2), Is.EqualTo(fives.Take(2)), "The first two should flush in full, in arrival order.");
			Assert.That(Wire(batch[2]), Is.EqualTo(Wire(new AggregatedV1("damaged", 3, 12, 14, []))),
				"The marker should count the three suppressed events and cover their ticks, 12 to 14.");
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

			var expected = new object[] { d1, k1, d2, new AggregatedV1("damaged", 3, 11, 13, []), p1, k2 };
			Assert.That(batch.Select(Wire), Is.EqualTo(expected.Select(Wire)),
				"The turn should flush in arrival order, with the damaged marker where d3 arrived.");
			Assert.That(buffer.Pending, Is.Zero, "A flush should empty the buffer.");
			Assert.That(buffer.Flush(), Is.Empty, "A second flush with nothing added should be empty.");
		}

		[TestCase(TestName = "A wait with events already buffered returns at once, without reading the clock")]
		public void WaitReturnsAtOnceWhenBuffered()
		{
			var buffer = new AgentTurnBuffer();
			buffer.Add(Damaged(5, 1));

			var start = new AgentGameClock.Reading(null, 0, false);
			var reads = 0;
			var waited = buffer.WaitForEvents(start, 10, 0, () => { reads++; return start; });

			Assert.That(waited, Is.EqualTo("events"), "A buffered event should release the wait at once.");
			Assert.That(reads, Is.Zero, "The wait should return before it reads the clock.");
		}

		[TestCase(TestName = "A wait with no events returns timeout once the game clock passes its tick budget")]
		public void WaitTimesOutOnTheClock()
		{
			var buffer = new AgentTurnBuffer();
			var start = new AgentGameClock.Reading(null, 0, false);
			var now = 0;

			var waited = buffer.WaitForEvents(start, 200, 0, () =>
			{
				now += 50;
				return new AgentGameClock.Reading(null, now, false);
			});

			Assert.That(waited, Is.EqualTo("timeout"), "With no events, the wait should time out.");
			Assert.That(now, Is.GreaterThanOrEqualTo(200), "The wait should give up once the game clock has passed its tick budget.");
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

		[TestCase(TestName = "An aggregated marker names the actors its suppressed events covered, in arrival order")]
		public void MarkerNamesItsActors()
		{
			var buffer = new AgentTurnBuffer();
			foreach (var e in Enumerable.Range(1, 5).Select(i => Damaged(9 + i, (uint)(100 + i))))
				buffer.Add(e);

			var marker = (AggregatedV1)buffer.Flush()[2];

			Assert.That(marker.Aggregated, Is.EqualTo(3), "The marker should count the three suppressed events.");
			Assert.That(marker.Actors, Is.EqualTo(new uint[] { 103, 104, 105 }),
				"The marker should name the actors of the three suppressed events, in arrival order.");
		}

		[TestCase(TestName = "A marker names at most ten actors, and its count is still the true total")]
		public void MarkerCapsActorsAtTen()
		{
			var buffer = new AgentTurnBuffer();
			foreach (var e in Enumerable.Range(1, 15).Select(i => Damaged(i, (uint)i)))
				buffer.Add(e);

			var marker = buffer.Flush().OfType<AggregatedV1>().Single();

			Assert.That(marker.Aggregated, Is.EqualTo(13), "Fifteen events, two in full, leave thirteen suppressed, and the count should say so.");
			Assert.That(marker.Actors, Is.EqualTo(Enumerable.Range(3, 10).Select(i => (uint)i)),
				"The marker should name the first ten suppressed actors, not all thirteen.");

			using var wire = JsonDocument.Parse(Wire(marker));
			Assert.That(wire.RootElement.GetProperty("actors").GetArrayLength(), Is.EqualTo(10),
				"On the wire the marker's actors should hold the same ten ids.");
		}

		[TestCase(TestName = "An actor hit many times in one turn is named once in the marker")]
		public void MarkerNamesAnActorOnce()
		{
			var buffer = new AgentTurnBuffer();
			foreach (var e in Enumerable.Range(1, 5).Select(i => Damaged(i, 7u)))
				buffer.Add(e);

			var marker = (AggregatedV1)buffer.Flush()[2];

			Assert.That(marker.Aggregated, Is.EqualTo(3), "The three suppressed hits should still be counted.");
			Assert.That(marker.Actors, Is.EqualTo(new uint[] { 7 }), "Actor 7 should be named once.");
		}

		[TestCase(TestName = "A marker covering one suppressed event has aggregated 1 and one actor, and its first two events stay in full")]
		public void MarkerOfOneEventNamesItsActor()
		{
			var buffer = new AgentTurnBuffer();
			var hits = new[] { Damaged(1, 5u), Damaged(2, 6u), Damaged(3, 7u) };
			foreach (var e in hits)
				buffer.Add(e);

			var batch = buffer.Flush();

			Assert.That(batch.Take(2), Is.EqualTo(hits.Take(2)), "The first two of the type should flush in full.");
			var marker = (AggregatedV1)batch[2];
			Assert.That(marker.Aggregated, Is.EqualTo(1), "One event is suppressed, so the marker counts one.");
			Assert.That(marker.Actors, Is.EqualTo(new uint[] { 7 }), "The marker should name the actor of the one suppressed event.");

			using var wire = JsonDocument.Parse(Wire(marker));
			Assert.That(wire.RootElement.GetProperty("actors").ValueKind, Is.EqualTo(JsonValueKind.Array),
				"On the wire the marker's actors should be an array.");
			Assert.That(wire.RootElement.GetProperty("actors").EnumerateArray().Select(a => a.GetUInt32()), Is.EqualTo(new uint[] { 7 }));
		}

		[TestCase(TestName = "A marker for events with no actor field has an empty actors array on the wire")]
		public void MarkerWithoutAnActorHasEmptyActorsArray()
		{
			var buffer = new AgentTurnBuffer();
			foreach (var e in Enumerable.Range(1, 3).Select(i => Damaged(9 + i, i)))
				buffer.Add(e);

			var marker = buffer.Flush()[2];

			using var wire = JsonDocument.Parse(Wire(marker));
			Assert.That(wire.RootElement.TryGetProperty("actors", out var actors), Is.True, "The marker should always carry an actors key.");
			Assert.That(actors.ValueKind, Is.EqualTo(JsonValueKind.Array), "The actors key should hold an array.");
			Assert.That(actors.GetArrayLength(), Is.Zero, "Events without an actor field should name no actors.");
		}

		[TestCase(TestName = "Notifications aggregate per name, so a finished structure is never hidden behind other notifications")]
		public void NotificationsAggregatePerName()
		{
			var buffer = new AgentTurnBuffer();
			var silos = Enumerable.Range(1, 3).Select(i => new NotificationV1("notification", i, "SilosNeeded", "P", null, null, null, null)).ToList();
			var lowPower = Enumerable.Range(4, 3).Select(i => new NotificationV1("notification", i, "LowPower", "P", null, null, null, null)).ToList();
			var built = new NotificationV1("notification", 7, "ConstructionComplete", "P", "powr", [3, 4], true, "Building");
			foreach (var e in silos.Concat(lowPower).Append(built))
				buffer.Add(e);

			var batch = buffer.Flush();

			Assert.That(batch, Does.Contain(built), "ConstructionComplete should flush in full, not behind the SilosNeeded and LowPower markers.");
			Assert.That(batch.OfType<AggregatedV1>().Select(Wire), Is.EqualTo(new[]
			{
				Wire(new AggregatedV1("notification", 1, 3, 3, [], "SilosNeeded")),
				Wire(new AggregatedV1("notification", 1, 6, 6, [], "LowPower")),
			}), "Each suppressed name should get its own marker, and the marker should name it.");
		}

		[TestCase(TestName = "Suppressed notifications group by name and item, and each marker keeps its latest payload")]
		public void NotificationMarkersKeepTheLatestPayload()
		{
			var buffer = new AgentTurnBuffer();
			var first = new NotificationV1("notification", 1, "NoBuild", "P", Item: "e1", QueueType: "Vehicle", Reason: "exit_blocked", Producer: 7);
			var second = new NotificationV1("notification", 2, "NoBuild", "P", Item: "e2", QueueType: "Vehicle", Reason: "producer_paused");
			var e1Paused = new NotificationV1("notification", 3, "NoBuild", "P", Item: "e1", QueueType: "Vehicle", Reason: "producer_paused", Producer: 8);
			var e1Reserved = new NotificationV1("notification", 4, "NoBuild", "P", Item: "e1", QueueType: "Vehicle", Reason: "producer_reserved", Producer: 9, BlockedBy: "e1");
			var e2Funds = new NotificationV1("notification", 5, "NoBuild", "P", Item: "e2", QueueType: "Vehicle", Reason: "no_producer", Cost: 300, RemainingCost: 120, Funds: 50, ShortBy: 70);
			foreach (var e in new[] { first, second, e1Paused, e1Reserved, e2Funds })
				buffer.Add(e);

			var batch = buffer.Flush();

			Assert.That(batch, Has.Count.EqualTo(4), "Two in full, then one marker for each item the suppressed notifications named.");
			Assert.That(batch.Take(2), Is.EqualTo(new object[] { first, second }), "The first two NoBuild notifications should flush in full.");
			Assert.That(Wire(batch[2]), Is.EqualTo(Wire(new AggregatedV1("notification", 2, 3, 4, [], Name: "NoBuild", Item: "e1",
				QueueType: "Vehicle", Reason: "producer_reserved", Producer: 9, BlockedBy: "e1"))),
				"The e1 marker should count its two suppressed notifications and keep the latest one's payload.");
			Assert.That(Wire(batch[3]), Is.EqualTo(Wire(new AggregatedV1("notification", 1, 5, 5, [], Name: "NoBuild", Item: "e2",
				QueueType: "Vehicle", Reason: "no_producer", Cost: 300, RemainingCost: 120, Funds: 50, ShortBy: 70))),
				"The e2 marker should keep its own payload, not e1's.");
		}

		[TestCase(TestName = "A notification marker with no payload has none of the payload keys on the wire")]
		public void NotificationMarkerWithoutPayloadOmitsIt()
		{
			var buffer = new AgentTurnBuffer();
			foreach (var e in Enumerable.Range(1, 3).Select(i => new NotificationV1("notification", i, "SilosNeeded", "P", null, null, null, null)))
				buffer.Add(e);

			var json = Wire(buffer.Flush()[2]);

			Assert.That(json, Does.Not.Contain("item").And.Not.Contain("queue_type").And.Not.Contain("reason").And.Not.Contain("cost").And.Not.Contain("cell"),
				"A marker for notifications that carry no payload should not add the payload keys.");
		}

		[TestCase(TestName = "An event with no name field aggregates by its type alone, and its marker has no name key")]
		public void MarkerWithoutANameHasNoNameKey()
		{
			var buffer = new AgentTurnBuffer();
			foreach (var e in Enumerable.Range(1, 3).Select(i => Damaged(9 + i, i)))
				buffer.Add(e);

			var json = JsonSerializer.Serialize(buffer.Flush()[2], Json);

			Assert.That(json, Does.Not.Contain("name"), "A marker for events that name nothing should not add a name key.");
		}

		static DamagedV1 Strike(int tick, uint victim, uint? attacker, int damage, int hp) =>
			new("damaged", tick, victim, "e1", "P", attacker, null, null, damage, hp, 100, "Normal", null, attacker is null);

		static HealedV1 Heal(int tick, uint victim, uint? healer, int hp) =>
			new("healed", tick, victim, "e1", "P", healer, hp, 100, "Normal");

		[TestCase(TestName = "Damaged events past the first two aggregate per victim, with their damage, HP and attackers")]
		public void DamagedAggregatesPerVictim()
		{
			var buffer = new AgentTurnBuffer();
			var strikes = new[]
			{
				Strike(10, 1, 50, 10, 90),
				Strike(11, 2, 51, 5, 45),
				Strike(12, 1, 51, 20, 70),
				Strike(13, 2, 51, 15, 30),
				Strike(14, 1, 52, 30, 40),
				Strike(15, 2, null, 8, 22),
			};
			foreach (var e in strikes)
				buffer.Add(e);

			var batch = buffer.Flush();

			Assert.That(batch, Has.Count.EqualTo(4), "Two in full, then one marker for each of the two victims.");
			Assert.That(batch.Take(2), Is.EqualTo(strikes.Take(2)), "The first two damaged events should flush in full, in arrival order.");
			Assert.That(Wire(batch[2]), Is.EqualTo(Wire(new AggregatedV1("damaged", 2, 12, 14, [1], null, 50, 90, 40, [51, 52]))),
				"Victim A's marker should sum its 50 damage, run from the HP before its first suppressed hit (90) to its last (40), and name both attackers.");
			Assert.That(Wire(batch[3]), Is.EqualTo(Wire(new AggregatedV1("damaged", 2, 13, 15, [2], null, 23, 45, 22, [51]))),
				"Victim B's marker should sum its 23 damage and name only the attacker that was known.");
		}

		[TestCase(TestName = "Healed events past the first two aggregate per victim, and name the healers that are known")]
		public void HealedAggregatesPerVictim()
		{
			var buffer = new AgentTurnBuffer();
			var heals = new[]
			{
				Heal(10, 1, 60, 50),
				Heal(11, 2, 61, 30),
				Heal(12, 1, 62, 60),
				Heal(13, 1, 62, 70),
				Heal(14, 2, null, 40),
			};
			foreach (var e in heals)
				buffer.Add(e);

			var batch = buffer.Flush();

			Assert.That(batch, Has.Count.EqualTo(4), "Two in full, then one marker for each of the two victims.");
			Assert.That(Wire(batch[2]), Is.EqualTo(Wire(new AggregatedV1("healed", 2, 12, 13, [1], null, null, 60, 70, [62]))),
				"Victim A's marker should run from the HP after its first suppressed heal (60) to its last (70), and name healer 62 once, though it healed twice.");
			Assert.That(Wire(batch[3]), Is.EqualTo(Wire(new AggregatedV1("healed", 1, 14, 14, [2], null, null, 40, 40, null))),
				"Victim B's healer is not known, so the marker should have no attackers key.");
		}

		[TestCase(TestName = "A lethal first hit gives its marker no hp_from, since the HP before it is unknown")]
		public void LethalFirstHitOmitsHpFrom()
		{
			var buffer = new AgentTurnBuffer();
			var hits = new[]
			{
				Strike(10, 2, 50, 5, 95),
				Strike(11, 2, 50, 5, 90),
				Strike(12, 1, 50, 30, 0),
			};
			foreach (var e in hits)
				buffer.Add(e);

			var batch = buffer.Flush();

			Assert.That(batch, Has.Count.EqualTo(3), "Two in full, then one marker for the victim's lethal hit.");
			Assert.That(Wire(batch[2]), Is.EqualTo(Wire(new AggregatedV1("damaged", 1, 12, 12, [1], null, 30, null, 0, [50]))),
				"The victim had less than its 30 damage in HP, and the wire HP is clamped to 0, so hp_from is unknown and should be omitted, not 30.");
		}

		[TestCase(TestName = "A marker with no per-victim values has none of the damage, HP and attacker keys on the wire")]
		public void MarkerWithoutVictimDataHasNoVictimKeys()
		{
			var buffer = new AgentTurnBuffer();
			foreach (var e in Enumerable.Range(1, 3).Select(i => Damaged(9 + i, i)))
				buffer.Add(e);

			var json = Wire(buffer.Flush()[2]);

			Assert.That(json, Does.Not.Contain("damage_total").And.Not.Contain("hp_from").And.Not.Contain("hp_to").And.Not.Contain("attackers"),
				"A marker for events that carry no damage or attacker should not add the per-victim keys.");
		}

		[TestCase(TestName = "Victims past MaxVictimMarkers fold into one residual marker with a total but no HP range")]
		public void VictimsPastTheCapShareAResidualMarker()
		{
			var buffer = new AgentTurnBuffer();
			buffer.Add(Strike(1, 100, 9, 5, 95));
			buffer.Add(Strike(2, 101, 9, 5, 95));
			foreach (var i in Enumerable.Range(1, AgentTurnBuffer.MaxVictimMarkers + 2))
				buffer.Add(Strike(10 + i, (uint)i, 9, 3, 17));

			var markers = buffer.Flush().OfType<AggregatedV1>().ToList();

			Assert.That(markers, Has.Count.EqualTo(AgentTurnBuffer.MaxVictimMarkers + 1),
				"The first ten victims get a marker each, and the last two share one residual marker.");
			Assert.That(markers.Take(AgentTurnBuffer.MaxVictimMarkers).Select(m => m.Actors[0]),
				Is.EqualTo(Enumerable.Range(1, AgentTurnBuffer.MaxVictimMarkers).Select(i => (uint)i)),
				"Each per-victim marker should name its own victim.");

			var residual = markers[^1];
			Assert.That(residual.Actors, Is.EqualTo(new uint[] { 11, 12 }), "The residual marker should name the two victims past the cap.");
			Assert.That(residual.Aggregated, Is.EqualTo(2), "The residual marker should count the two suppressed hits.");
			Assert.That(residual.DamageTotal, Is.EqualTo(6), "The residual marker should sum the damage of its victims.");
			Assert.That(residual.Attackers, Is.EqualTo(new uint[] { 9 }), "The residual marker should name the attacker both victims share.");
			Assert.That(Wire(residual), Does.Not.Contain("hp_from").And.Not.Contain("hp_to"),
				"A residual marker spans several victims, so it should have no HP range on the wire.");
		}
	}
}
