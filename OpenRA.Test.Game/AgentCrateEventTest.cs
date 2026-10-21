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
using OpenRA.Activities;

namespace OpenRA.Test
{
	/// <summary>
	/// The crate event (P4). A unit collecting a known crate by crushing it reports one crate event: the crate, its cell, the
	/// collector, and the CrateAction type that fired. A collection out of the local player's sight raises nothing. The crate and
	/// both units are real actors in a headless world, moved by the Move activity the way AgentMoveEventsTest queues it, and the
	/// mod's types are reached by reflection.
	/// </summary>
	[TestFixture]
	sealed class AgentCrateEventTest
	{
		const int Seed = 0xC8A7E;
		const string Infantry = "e1";

		/// <summary>The crate with exactly one action, so the fired action is not a weighted draw. moneycrate would draw one too,
		/// but its cash tick is a FloatingText, which needs a renderer the headless world has not.</summary>
		const string CrateActor = "healcrate";
		const string ExpectedAction = "HealActorsCrateAction";

		/// <summary>Ticks to let the map's starting units settle before adding the test actors.</summary>
		const int SettleTicks = 5;

		/// <summary>Upper bound on ticks for a one-cell move to collect the crate.</summary>
		const int CollectTimeoutTicks = 300;

		bool savedAgentSocket;
		readonly List<object> held = [];
		bool savedEnabled;
		object connection;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[SetUp]
		public void CreateConnection()
		{
			savedAgentSocket = Game.Settings.Debug.AgentSocket;
			Game.Settings.Debug.AgentSocket = true;

			savedEnabled = (bool)ModType("AgentSocket.AgentEvents").GetProperty("Enabled").GetValue(null);
			connection = Activator.CreateInstance(ModType("AgentSocket.AgentConnection"));
			held.Clear();
		}

		[TearDown]
		public void RestoreHub()
		{
			Call(connection, "Close");
			ModType("AgentSocket.AgentEvents").GetProperty("Enabled").SetValue(null, savedEnabled);
			Game.Settings.Debug.AgentSocket = savedAgentSocket;
		}

		static Type ModType(string name)
		{
			return HeadlessMod.ModData.ObjectCreator.FindType(name);
		}

		static object Call(object target, string method, params object[] args)
		{
			return target.GetType().GetMethod(method).Invoke(target, args);
		}

		static T Field<T>(object target, string name)
		{
			return (T)target.GetType().GetProperty(name).GetValue(target);
		}

		/// <summary>Sends the unit to the cell, the way AgentMoveEventsTest queues a move.</summary>
		static void MoveTo(Actor unit, CPos cell)
		{
			unit.QueueActivity((Activity)Activator.CreateInstance(ModType("Activities.Move"), unit, cell, null));
		}

		/// <summary>The crate events raised so far, reading the connection's turn into the test's list.</summary>
		List<object> Crates()
		{
			held.AddRange(((IList)Call(connection, "Flush")).Cast<object>());
			return held.Where(e => Field<string>(e, "Event") == "crate").ToList();
		}

		[TestCase(TestName = "A unit collecting a known crate reports one crate event with its collector and action")]
		public void KnownCollectionIsReported()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var neutral = hw.World.Players.First(p => p.PlayerReference.OwnsWorld);
			hw.Tick(SettleTicks);

			var unit = hw.CreateActor(Infantry, hw.FindClearArea(hw.LocalPlayer.HomeLocation, 3, 3), hw.LocalPlayer);
			var cell = hw.FindClearArea(unit.Location, 2, 2, 3);
			var crate = hw.CreateActor(CrateActor, cell, neutral);
			hw.Tick(SettleTicks);

			Assert.That(hw.LocalPlayer.Shroud.IsVisible(crate.CenterPosition), Is.True,
				"Precondition: the crate beside the unit should be visible.");
			Assert.That(crate.IsDead, Is.False, "Precondition: the crate should be waiting.");

			MoveTo(unit, cell);

			Assert.That(hw.TickUntil(() => crate.IsDead, CollectTimeoutTicks), Is.Not.Null, "The unit never collected the crate.");

			var crates = Crates();
			Assert.That(crates, Has.Count.EqualTo(1), "One collection should report one crate event.");
			var e = crates[0];
			Assert.That(Field<string>(e, "State"), Is.EqualTo("collected"));
			Assert.That(Field<uint>(e, "Actor"), Is.EqualTo(crate.ActorID));
			Assert.That(Field<string>(e, "Type"), Is.EqualTo(CrateActor));
			Assert.That(Field<string>(e, "Owner"), Is.EqualTo(neutral.PlayerName));
			Assert.That(Field<int[]>(e, "Cell"), Is.EqualTo(new[] { cell.X, cell.Y }));
			Assert.That(Field<uint?>(e, "By"), Is.EqualTo(unit.ActorID));
			Assert.That(Field<string>(e, "ByType"), Is.EqualTo(Infantry));
			Assert.That(Field<string>(e, "Action"), Is.EqualTo(ExpectedAction));
		}

		[TestCase(TestName = "A crate collected out of the local player's sight raises no crate event")]
		public void FoggedCollectionIsSilent()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var neutral = hw.World.Players.First(p => p.PlayerReference.OwnsWorld);
			hw.Tick(SettleTicks);

			var unit = hw.CreateActor(Infantry, hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3), hw.EnemyPlayer);
			var cell = hw.FindClearArea(unit.Location, 2, 2, 3);
			var crate = hw.CreateActor(CrateActor, cell, neutral);
			hw.Tick(SettleTicks);

			Assert.That(hw.LocalPlayer.Shroud.IsVisible(crate.CenterPosition), Is.False,
				"Precondition: the crate at the enemy home should be out of sight.");

			MoveTo(unit, cell);

			Assert.That(hw.TickUntil(() => crate.IsDead, CollectTimeoutTicks), Is.Not.Null, "The enemy unit never collected the crate.");
			Assert.That(Crates(), Is.Empty, "A collection out of sight must raise no crate event.");
		}
	}
}
