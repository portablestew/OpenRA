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
using System.Linq;
using NUnit.Framework;
using OpenRA.Traits;

namespace OpenRA.Test
{
	/// <summary>
	/// Neutral civilians are units (8.6): they appear in the state units and in look under the fog policy (AgentFog), exactly as
	/// other units do. Neutral scenery (a tree) stays out of both, whether or not the local player knows it.
	/// </summary>
	[TestFixture]
	sealed class AgentCivilianTest
	{
		const int Seed = 0xC1C1;
		const string Civilian = "c1";
		const string Tree = "t01";
		const string Infantry = "e1";

		/// <summary>Ticks to let the map settle, and let visibility update.</summary>
		const int SettleTicks = 5;

		bool savedAgentSocket;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[SetUp]
		public void EnableAgentSocket()
		{
			savedAgentSocket = Game.Settings.Debug.AgentSocket;
			Game.Settings.Debug.AgentSocket = true;
		}

		[TearDown]
		public void RestoreAgentSocket()
		{
			Game.Settings.Debug.AgentSocket = savedAgentSocket;
		}

		static Type ModType(string name)
		{
			return HeadlessMod.ModData.ObjectCreator.FindType(name);
		}

		/// <summary>The fog helper (AgentFog.Known), called through the mod's type.</summary>
		static bool Known(Actor actor, Player observer)
		{
			return (bool)ModType("AgentSocket.AgentFog").GetMethod("Known").Invoke(null, [actor, observer]);
		}

		/// <summary>The ids of the state's units, read through a fresh state request.</summary>
		static uint[] StateUnitIds()
		{
			using var state = AgentStateDumpTest.State();
			return state.RootElement.GetProperty("units").EnumerateArray().Select(u => u.GetProperty("id").GetUInt32()).ToArray();
		}

		/// <summary>The ids of the actors look reports at a cell, read through a fresh look request.</summary>
		static uint[] LookActorIds(CPos cell)
		{
			using var look = AgentStateDumpTest.Request($"{{\"cmd\":\"look\",\"points\":[[{cell.X},{cell.Y}]],\"k\":100}}");
			return look.RootElement.GetProperty("actors").EnumerateArray().Select(a => a.GetProperty("id").GetUInt32()).ToArray();
		}

		/// <summary>The ids of the state's threats, read through a fresh state request.</summary>
		static uint[] ThreatIds()
		{
			using var state = AgentStateDumpTest.State();
			return state.RootElement.GetProperty("threats").EnumerateArray().Select(t => t.GetProperty("id").GetUInt32()).ToArray();
		}

		[TestCase(TestName = "state.threats lists known enemy units only: a known neutral civilian is in the state units but not in the threats")]
		public void ThreatsListOnlyEnemies()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var neutral = hw.World.Players.First(p => p.PlayerReference.OwnsWorld);
			var ours = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			var civilian = hw.CreateActor(Civilian, hw.FindClearArea(ours.Location + new CVec(2, 0), 1, 1, 4), neutral);
			var enemy = hw.CreateActor(Infantry, hw.FindClearArea(ours.Location + new CVec(0, 2), 1, 1, 4), hw.EnemyPlayer);
			hw.Tick(SettleTicks);

			// The engine's stance decides who a threat is: the enemy is an Enemy to us, and the neutral civilian is not.
			Assert.That(local.RelationshipWith(hw.EnemyPlayer), Is.EqualTo(PlayerRelationship.Enemy), "Premise: the enemy player is an enemy.");
			Assert.That(local.RelationshipWith(neutral), Is.Not.EqualTo(PlayerRelationship.Enemy), "Premise: the neutral player is not an enemy.");
			Assert.That(Known(enemy, local), Is.True, "Premise: the enemy is known.");
			Assert.That(Known(civilian, local), Is.True, "Premise: the civilian is known.");

			Assert.That(ThreatIds(), Does.Contain(enemy.ActorID), "A known enemy unit must be in the threats.");
			Assert.That(ThreatIds(), Does.Not.Contain(civilian.ActorID), "A known neutral civilian must not be in the threats.");

			using var state = AgentStateDumpTest.State();
			var units = state.RootElement.GetProperty("units").EnumerateArray().Select(u => u.GetProperty("id").GetUInt32()).ToArray();
			Assert.That(units, Does.Contain(civilian.ActorID), "The known neutral civilian must still be in the state units.");
		}

		[TestCase(TestName = "A neutral civilian is in look and the state units once known, absent while unknown; neutral scenery is absent from both")]
		public void CivilianIsReportedOnlyWhenKnown()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var neutral = hw.World.Players.First(p => p.PlayerReference.OwnsWorld);
			var civilian = hw.CreateActor(Civilian, hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3), neutral);
			var tree = hw.CreateActor(Tree, hw.FindClearArea(civilian.Location, 2, 2, 6), neutral);
			hw.Tick(SettleTicks);

			// Unknown: the civilian is out of sight and absent from both reports. The tree is absent too.
			Assert.That(local.Shroud.IsVisible(civilian.CenterPosition), Is.False, "Premise: the civilian is out of sight.");
			Assert.That(Known(civilian, local), Is.False, "Premise: the civilian is unknown.");
			Assert.That(StateUnitIds(), Does.Not.Contain(civilian.ActorID), "An unknown civilian must not be in the state units.");
			Assert.That(LookActorIds(civilian.Location), Does.Not.Contain(civilian.ActorID), "An unknown civilian must not be in look.");
			Assert.That(StateUnitIds(), Does.Not.Contain(tree.ActorID), "Neutral scenery must not be in the state units.");
			Assert.That(LookActorIds(civilian.Location), Does.Not.Contain(tree.ActorID), "Neutral scenery must not be in look.");

			// Known: a scout beside the civilian makes it visible, so it is in both reports. The tree stays out.
			var scout = hw.CreateActor(Infantry, hw.FindClearArea(civilian.Location + new CVec(3, 0), 1, 1, 3));
			hw.Tick(SettleTicks);
			Assert.That(local.Shroud.IsVisible(civilian.CenterPosition), Is.True, "The scout should make the civilian visible.");
			Assert.That(StateUnitIds(), Does.Contain(civilian.ActorID), "A known civilian must be in the state units.");
			Assert.That(LookActorIds(civilian.Location), Does.Contain(civilian.ActorID), "A known civilian must be in look.");
			Assert.That(StateUnitIds(), Does.Not.Contain(tree.ActorID), "Neutral scenery must still not be in the state units.");
			Assert.That(LookActorIds(civilian.Location), Does.Not.Contain(tree.ActorID), "Neutral scenery must still not be in look.");

			scout.Dispose();
		}
	}
}
