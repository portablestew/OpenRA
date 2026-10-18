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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The player-actor observer's payloads (M7, <c>3h-player-observer</c>). The combat fight is the same as
	/// <c>WorldCombatTest</c>: two hostile e1s in range, one dies. The events come from the real engine hooks, through
	/// the hub, into a connection's queue.
	/// </summary>
	/// <remarks>
	/// The mod loads OpenRA.Mods.Common into its own load context, so the hub and payload types it uses are not the
	/// ones this assembly would compile against. Everything goes through the mod's copy, as AgentStateDumpTest does.
	/// </remarks>
	[TestFixture]
	sealed class AgentPlayerObserverTest
	{
		const int Separation = 2;
		const int CombatTimeoutTicks = 500;
		const int SettleTicks = 5;

		bool savedEnabled;
		object connection;
		readonly List<object> held = [];

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[SetUp]
		public void CreateConnection()
		{
			savedEnabled = (bool)ModType("AgentSocket.AgentEvents").GetProperty("Enabled").GetValue(null);
			connection = Activator.CreateInstance(ModType("AgentSocket.AgentConnection"));
			held.Clear();
		}

		[TearDown]
		public void RestoreHub()
		{
			Call(connection, "Close");
			ModType("AgentSocket.AgentEvents").GetProperty("Enabled").SetValue(null, savedEnabled);
		}

		/// <summary>
		/// Turns the hub on. Must run after HeadlessWorld.Create: the world's AgentWorldBridge sets Enabled from the settings
		/// (false here) when the world loads, so an earlier setting is overwritten.
		/// </summary>
		static void EnableHub()
		{
			ModType("AgentSocket.AgentEvents").GetProperty("Enabled").SetValue(null, true);
		}

		static Type ModType(string name)
		{
			return HeadlessMod.ModData.ObjectCreator.FindType(name);
		}

		static object Call(object target, string method, params object[] args)
		{
			return target.GetType().GetMethod(method).Invoke(target, args);
		}

		/// <summary>Takes every queued event. Fails rather than blocking when nothing was delivered.</summary>
		/// <summary>Takes the wanted events held so far. Fails rather than blocking when none reached the connection.</summary>
		/// <summary>Takes the held events of the named types, or all of them. Fails rather than blocking when none reached the connection.</summary>
		List<object> Drain(params string[] types)
		{
			Pending(types);
			var events = Wanted(types).ToList();
			Assert.That(events, Is.Not.Empty, "No events reached the connection.");
			held.RemoveAll(events.Contains);
			return events;
		}

		/// <summary>The wanted events held so far. Reading them takes the connection's turn into the test's list, so a wait can
		/// see its event without losing it for Drain.</summary>
		/// <summary>
		/// Reads the connection's turn into the test's list, and counts the held events of the named types (every type when none
		/// is named). Reading takes the events, so Drain still gets them.
		/// </summary>
		int Pending(params string[] types)
		{
			held.AddRange(Only(((IList)Call(connection, "Flush")).Cast<object>(),
				"damaged", "killed", "captured", "produced", "placed", "power_low", "power_restored", "defeated", "game_over"));
			return Wanted(types).Count();
		}

		/// <summary>The held events of the named types, or all of them when none is named.</summary>
		IEnumerable<object> Wanted(string[] types)
		{
			return types.Length == 0 ? held : Only(held, types);
		}

		/// <summary>Runs body with Debug.AgentSocket on, which the socket commands need. It is read at world load, so body creates the world.</summary>
		static void WithAgentSocket(Action body)
		{
			var saved = Game.Settings.Debug.AgentSocket;
			Game.Settings.Debug.AgentSocket = true;
			try
			{
				body();
			}
			finally
			{
				Game.Settings.Debug.AgentSocket = saved;
			}
		}

		/// <summary>The trait of a mod type on an actor. The test project cannot name mod types, so the trait is found by name.</summary>
		static object TraitNamed(Actor actor, string name)
		{
			var method = typeof(Actor).GetMethods().Single(m => m.Name == "TraitOrDefault" && m.IsGenericMethodDefinition);
			return method.MakeGenericMethod(ModType(name)).Invoke(actor, null);
		}

		/// <summary>The local player's power state, read through the production command.</summary>
		static string PowerStateOnSocket()
		{
			using var production = AgentStateDumpTest.Request("{\"cmd\":\"production\"}");
			return production.RootElement.GetProperty("power").GetProperty("state").GetString();
		}

		static T Field<T>(object target, string name)
		{
			return (T)target.GetType().GetProperty(name).GetValue(target);
		}

		static string KindOf(object payload)
		{
			return payload.GetType().Name;
		}

		[TestCase(TestName = "Two hostile e1s fight: one killed event and at least one damaged event, with payloads")]
		public void CombatRaisesDamagedAndKilled()
		{
			using var w = HeadlessWorld.Create(0xC0FFEE);

			Assert.That(w.EnemyPlayer, Is.Not.Null, "The selected map has only one playable slot.");

			w.Tick(SettleTicks);

			EnableHub();

			var (mineCell, _) = w.FindClearRun(w.LocalPlayer.HomeLocation, Separation);
			var theirsCell = mineCell + new CVec(Separation, 0);

			var mine = w.CreateActor("e1", mineCell);
			var theirs = w.CreateActor("e1", theirsCell, w.EnemyPlayer);

			var resolvedAt = w.TickUntil(() =>
			{
				// Read every tick, so no turn holds more than three damaged events and each one reaches the test in full.
				Pending("damaged", "killed");
				return mine.IsDead || theirs.IsDead;
			}, CombatTimeoutTicks);
			Assert.That(resolvedAt, Is.Not.Null, "The fight did not resolve within the timeout.");

			var batch = Drain("damaged", "killed");

			var dead = mine.IsDead ? mine : theirs;
			var survivor = mine.IsDead ? theirs : mine;

			var killed = batch.Where(e => KindOf(e) == "KilledV1").ToList();
			Assert.That(killed, Has.Count.EqualTo(1), "Expected exactly one killed event.");

			var kill = killed[0];
			Assert.That(Field<string>(kill, "Event"), Is.EqualTo("killed"));
			Assert.That(Field<uint>(kill, "Actor"), Is.EqualTo(dead.ActorID));
			Assert.That(Field<string>(kill, "Type"), Is.EqualTo("e1"));
			Assert.That(Field<string>(kill, "Owner"), Is.EqualTo(dead.Owner.PlayerName));
			Assert.That(Field<uint?>(kill, "Attacker"), Is.EqualTo(survivor.ActorID));
			Assert.That(Field<string>(kill, "AttackerType"), Is.EqualTo("e1"));
			Assert.That(Field<string>(kill, "AttackerOwner"), Is.EqualTo(survivor.Owner.PlayerName));
			Assert.That(Field<int>(kill, "Cost"), Is.GreaterThan(0));
			Assert.That(Field<int>(kill, "Tick"), Is.LessThanOrEqualTo(w.World.WorldTick));

			var damaged = batch.Where(e => KindOf(e) == "DamagedV1").ToList();
			Assert.That(damaged, Is.Not.Empty, "Expected at least one damaged event.");

			// The e1 may also hit other units in range, so a hit can name an actor that is not one of the two combatants.
			var ownerOf = new Dictionary<uint, string>
			{
				[mine.ActorID] = mine.Owner.PlayerName,
				[theirs.ActorID] = theirs.Owner.PlayerName,
			};

			foreach (var hit in damaged)
			{
				Assert.That(Field<string>(hit, "Event"), Is.EqualTo("damaged"));
				Assert.That(Field<string>(hit, "Type"), Is.Not.Null.And.Not.Empty);
				Assert.That(Field<int>(hit, "Damage"), Is.GreaterThan(0));
				Assert.That(Field<int>(hit, "MaxHp"), Is.GreaterThan(0));
				Assert.That(Field<int>(hit, "Hp"), Is.InRange(0, Field<int>(hit, "MaxHp")));
				Assert.That(Field<string>(hit, "DamageState"), Is.Not.Null.And.Not.Empty);

				if (ownerOf.TryGetValue(Field<uint>(hit, "Actor"), out var owner))
					Assert.That(Field<string>(hit, "Owner"), Is.EqualTo(owner));
			}

			// The killing blow is the last hit on the dead actor, and it is recorded before the kill.
			var lastHitOnDead = damaged.LastOrDefault(h => Field<uint>(h, "Actor") == dead.ActorID);
			Assert.That(lastHitOnDead, Is.Not.Null);
			Assert.That(Field<int>(lastHitOnDead, "Hp"), Is.EqualTo(0));
			Assert.That(batch.IndexOf(lastHitOnDead), Is.LessThan(batch.IndexOf(kill)));
		}

		[TestCase(TestName = "Captured payload names the old and new owner and the captor")]
		public void CapturedPayloadNamesOwners()
		{
			using var w = HeadlessWorld.Create(0xC0FFEE);

			Assert.That(w.EnemyPlayer, Is.Not.Null, "The selected map has only one playable slot.");

			var (cell, _) = w.FindClearRun(w.LocalPlayer.HomeLocation, Separation);
			var captured = w.CreateActor("e1", cell, w.EnemyPlayer);
			var captor = w.CreateActor("e1", cell + new CVec(Separation, 0), w.LocalPlayer);

			// CaptureActor changes the owner before it raises captured, so the captured actor is the local player's when the event is built.
			captured.ChangeOwnerSync(w.LocalPlayer);

			EnableHub();
			ModType("AgentPlayerEventObserver").GetMethod("Captured")
				.Invoke(null, [captured, captor, w.EnemyPlayer, w.LocalPlayer]);

			var batch = Drain("captured");
			Assert.That(batch, Has.Count.EqualTo(1));

			var capture = batch[0];
			Assert.That(KindOf(capture), Is.EqualTo("CapturedV1"));
			Assert.That(Field<string>(capture, "Event"), Is.EqualTo("captured"));
			Assert.That(Field<uint>(capture, "Actor"), Is.EqualTo(captured.ActorID));
			Assert.That(Field<string>(capture, "Type"), Is.EqualTo("e1"));
			Assert.That(Field<string>(capture, "From"), Is.EqualTo(w.EnemyPlayer.PlayerName));
			Assert.That(Field<string>(capture, "To"), Is.EqualTo(w.LocalPlayer.PlayerName));
			Assert.That(Field<uint>(capture, "Captor"), Is.EqualTo(captor.ActorID));
		}

		[TestCase(TestName = "A placed building raises a placed event on the production group, with its id, type and cell")]
		public void PlacedRaisesPlacedEvent()
		{
			// The place command issues the PlaceBuilding order, which notifies the player actor, so this runs the real placement.
			WithAgentSocket(() =>
			{
				using var w = HeadlessWorld.Create(1);
				var fact = w.CreateActor("fact", w.FindClearArea(new CPos(50, 50), 3, 3), w.LocalPlayer);
				w.Tick(1);
				AgentPlaceTest.QueueAndFinishPowr(w);

				EnableHub();

				var cell = w.FindClearArea(fact.Location, 2, 2, 6);
				using (var response = AgentStateDumpTest.Request($"{{\"cmd\":\"place\",\"item\":\"powr\",\"cell\":[{cell.X},{cell.Y}]}}"))
					Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.True, "The place command was refused");

				Assert.That(w.TickUntil(() => Pending("placed") > 0, 20), Is.Not.Null, "No placed event was raised.");

				var building = w.World.ActorMap.GetActorsAt(cell).Single(a => a.Info.Name == "powr");
				var placed = Drain("placed").Where(e => KindOf(e) == "PlacedV1").ToList();
				Assert.That(placed, Has.Count.EqualTo(1), "Expected exactly one placed event.");

				Assert.That(Field<string>(placed[0], "Event"), Is.EqualTo("placed"));
				Assert.That(Field<uint>(placed[0], "Actor"), Is.EqualTo(building.ActorID));
				Assert.That(Field<string>(placed[0], "Type"), Is.EqualTo("powr"));
				Assert.That(Field<string>(placed[0], "Owner"), Is.EqualTo(w.LocalPlayer.PlayerName));
				Assert.That(Field<int[]>(placed[0], "Cell"), Is.EqualTo(new[] { cell.X, cell.Y }));
			});
		}

		[TestCase(TestName = "Power going short raises power_low, and a provider covering the draw raises power_restored")]
		public void PowerShortfallAndRestoreRaisePowerEvents()
		{
			WithAgentSocket(() =>
			{
				using var w = HeadlessWorld.Create(0xC0FFEE);
				w.Tick(SettleTicks);
				Assert.That(PowerStateOnSocket(), Is.EqualTo("Normal"), "The test world must start with enough power.");

				EnableHub();

				// TSLA draws power and nothing provides any yet, so the state drops below Normal.
				w.CreateActor("tsla", w.FindClearArea(new CPos(50, 50), 2, 2), w.LocalPlayer);
				Assert.That(w.TickUntil(() => Pending("power_low") > 0, 20), Is.Not.Null, "No power event was raised for the shortfall.");

				var low = Drain("power_low").Where(e => Field<string>(e, "Owner") == w.LocalPlayer.PlayerName).ToList();

				// The power state can flip more than once while the shortfall lasts, so the first event is the shortfall.
				Assert.That(low, Is.Not.Empty, "Expected a power event for the shortfall.");
				Assert.That(KindOf(low[0]), Is.EqualTo("PowerEventV1"));
				Assert.That(Field<string>(low[0], "Event"), Is.EqualTo("power_low"));
				Assert.That(Field<string>(low[0], "Owner"), Is.EqualTo(w.LocalPlayer.PlayerName));
				Assert.That(Field<int>(low[0], "Drained"), Is.GreaterThan(0));
				Assert.That(Field<int>(low[0], "Excess"), Is.EqualTo(Field<int>(low[0], "Provided") - Field<int>(low[0], "Drained")));
				Assert.That(Field<int>(low[0], "Excess"), Is.LessThan(0));
				Assert.That(Field<string>(low[0], "State"), Is.Not.EqualTo("Normal"));
				Assert.That(Field<string>(low[0], "State"), Is.EqualTo(PowerStateOnSocket()), "The event disagrees with the socket's power state");

				w.CreateActor("powr", w.FindClearArea(new CPos(50, 50), 2, 2), w.LocalPlayer);
				Assert.That(w.TickUntil(() => Pending("power_restored") > 0, 20), Is.Not.Null, "No power event was raised for the restored power.");

				var restored = Drain("power_restored").Where(e => Field<string>(e, "Owner") == w.LocalPlayer.PlayerName).ToList();
				Assert.That(restored, Is.Not.Empty, "Expected a power event for the restored power.");
				Assert.That(Field<string>(restored[0], "Event"), Is.EqualTo("power_restored"));
				Assert.That(Field<string>(restored[0], "Owner"), Is.EqualTo(w.LocalPlayer.PlayerName));
				Assert.That(Field<int>(restored[0], "Excess"), Is.GreaterThanOrEqualTo(0));
				Assert.That(Field<string>(restored[0], "State"), Is.EqualTo("Normal"));
			});
		}

		[TestCase(TestName = "A failed required objective raises a defeated event for the player that lost")]
		public void FailedObjectiveRaisesDefeated()
		{
			using var w = HeadlessWorld.Create(0xC0FFEE);
			w.Tick(SettleTicks);

			EnableHub();

			// MarkFailed on a required objective is the path the conquest rule takes when a player has no units left.
			var objectives = TraitNamed(w.LocalPlayer.PlayerActor, "Traits.MissionObjectives");
			var id = (int)Call(objectives, "Add", w.LocalPlayer, "Test objective", "Primary", true, true);
			Call(objectives, "MarkFailed", w.LocalPlayer, id);

			var defeated = Drain().Where(e => KindOf(e) == "DefeatedV1" && Field<string>(e, "Owner") == w.LocalPlayer.PlayerName).ToList();
			Assert.That(defeated, Has.Count.EqualTo(1), "Expected exactly one defeated event for the local player.");
			Assert.That(Field<string>(defeated[0], "Event"), Is.EqualTo("defeated"));
			Assert.That(Field<int>(defeated[0], "Tick"), Is.LessThanOrEqualTo(w.World.WorldTick));
		}

		[TestCase(TestName = "Completing every required objective raises a game_over event that names the winner")]
		public void CompletedObjectivesRaiseGameOver()
		{
			using var w = HeadlessWorld.Create(0xC0FFEE);
			w.Tick(SettleTicks);

			EnableHub();

			// Conquest added its required Primary objective on the first tick. A win needs every required objective complete,
			// and the last MarkCompleted raises OnPlayerWon.
			var objectives = TraitNamed(w.LocalPlayer.PlayerActor, "Traits.MissionObjectives");
			var list = (IList)objectives.GetType().GetProperty("Objectives").GetValue(objectives);
			Assert.That(list.Count, Is.GreaterThan(0), "No objective was added.");

			for (var i = 0; i < list.Count; i++)
			{
				var objective = list[i];
				var required = (bool)objective.GetType().GetField("Required").GetValue(objective);
				var state = objective.GetType().GetField("State").GetValue(objective).ToString();
				if (required && state == "Incomplete")
					Call(objectives, "MarkCompleted", w.LocalPlayer, i);
			}

			var wins = Drain().Where(e => KindOf(e) == "GameOverV1").ToList();
			Assert.That(wins, Has.Count.EqualTo(1), "Expected exactly one game_over event.");
			Assert.That(Field<string>(wins[0], "Event"), Is.EqualTo("game_over"));
			Assert.That(Field<string>(wins[0], "Winner"), Is.EqualTo(w.LocalPlayer.PlayerName));
		}

		/// <summary>The events of the named types, in the order they were raised. The connection carries every event; a test looks at its own.</summary>
		static IEnumerable<object> Only(IEnumerable<object> events, params string[] types)
		{
			return events.Where(e => types.Contains(e.GetType().GetProperty("Event")?.GetValue(e) as string));
		}
	}
}
