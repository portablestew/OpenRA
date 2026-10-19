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
using System.Linq;
using NUnit.Framework;
using OpenRA.Traits;

namespace OpenRA.Test
{
	/// <summary>
	/// The threat list (P2.7). Each case launches the real satellite, so enemy units are known, and places the friendlies in a clear
	/// area near the enemy's home, away from the local base. Then the state's threats are read through the socket. The fog-known
	/// check and the satellite are reached the way the other agent tests reach them, since the mod's types load through ObjectCreator.
	/// </summary>
	[TestFixture]
	sealed class AgentThreatsTest
	{
		const int Seed = 0x7A15;
		const string Infantry = "e1";
		const string Artillery = "arty";
		const string Satellite = "atek";

		/// <summary>Ticks to let the map settle, and visibility and the satellite update.</summary>
		const int SettleTicks = 5;

		/// <summary>A threat as the state reports it, with the fields these tests check.</summary>
		sealed record Entry(uint Id, string Type, uint? AttackTarget, string Heading, string Bearing, uint FriendlyId, int Distance);

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

		/// <summary>The fog helper under test, read through the mod's type.</summary>
		static bool Known(Actor actor, Player observer)
		{
			return (bool)ModType("AgentSocket.AgentFog").GetMethod("Known").Invoke(null, [actor, observer]);
		}

		/// <summary>The player's GpsWatcher trait, read through the mod's type.</summary>
		static object Watcher(Player player)
		{
			var trait = ModType("Traits.GpsWatcher");
			return typeof(Actor).GetMethod("Trait").MakeGenericMethod(trait).Invoke(player.PlayerActor, null);
		}

		/// <summary>Launches the player's satellite through its real entry points: the satellite building is added, then it reaches orbit.</summary>
		static void LaunchSatellite(HeadlessWorld hw, Player player)
		{
			var watcher = Watcher(player);
			var satellite = hw.CreateActor(Satellite, hw.FindClearArea(player.HomeLocation, 4, 4), player);
			var type = watcher.GetType();
			type.GetMethod("GpsAdd").Invoke(watcher, [satellite]);
			type.GetMethod("ReachedOrbit").Invoke(watcher, [player]);
		}

		/// <summary>A clear area near the enemy's home, away from the local base, where the test units stand.</summary>
		static CPos Clearing(HeadlessWorld hw)
		{
			return hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3, 30);
		}

		/// <summary>The state's threats, read through a fresh state request.</summary>
		static List<Entry> Threats()
		{
			using var state = AgentStateDumpTest.State();
			return state.RootElement.GetProperty("threats").EnumerateArray()
				.Select(t => new Entry(
					t.GetProperty("id").GetUInt32(),
					t.GetProperty("type").GetString(),
					t.GetProperty("attack_target").ValueKind == System.Text.Json.JsonValueKind.Null ? null : t.GetProperty("attack_target").GetUInt32(),
					t.GetProperty("heading").GetString(),
					t.GetProperty("bearing").GetString(),
					t.GetProperty("friendly_id").GetUInt32(),
					t.GetProperty("distance").GetInt32()))
				.ToList();
		}

		static int IndexOf(List<Entry> threats, Actor actor)
		{
			return threats.FindIndex(t => t.Id == actor.ActorID);
		}

		[TestCase(TestName = "The threat list holds at most 15 threats, and at most 3 of any type")]
		public void CapsAtFifteenAndThreePerType()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			var enemy = hw.EnemyPlayer;
			hw.Tick(SettleTicks);

			var friend = hw.CreateActor(Infantry, Clearing(hw), local);

			// Six types, four each, in a block south of the friendly: 24 known enemies, so both caps can bind.
			string[] types = ["e1", "e3", "jeep", "1tnk", "dog", "ftur"];
			var cells = new List<CPos>();
			for (var j = 1; j <= 4; j++)
				for (var i = -2; i <= 3; i++)
					cells.Add(friend.Location + new CVec(i, j));

			for (var k = 0; k < cells.Count; k++)
				hw.CreateActor(types[k % types.Length], cells[k], enemy);

			hw.Tick(SettleTicks);
			LaunchSatellite(hw, local);
			hw.Tick(SettleTicks);

			var threats = Threats();

			Assert.That(threats, Has.Count.EqualTo(15), "The list should hold exactly the fifteen nearest threats, not all 24 known enemies.");
			Assert.That(threats.GroupBy(t => t.Type).Max(g => g.Count()), Is.LessThanOrEqualTo(3),
				"No type should appear more than three times.");
		}

		[TestCase(TestName = "An artillery unit 8 cells away, inside its 12-cell range, ranks above an infantry 6 cells away, outside its 5-cell range")]
		public void StandoffRanksInRangeArtilleryAboveNearerInfantry()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			var enemy = hw.EnemyPlayer;
			hw.Tick(SettleTicks);

			var friend = hw.CreateActor(Infantry, Clearing(hw), local);
			var near = hw.CreateActor(Infantry, friend.Location + new CVec(6, 0), enemy);
			var far = hw.CreateActor(Artillery, friend.Location + new CVec(0, 8), enemy);
			hw.Tick(SettleTicks);
			LaunchSatellite(hw, local);
			hw.Tick(SettleTicks);

			var threats = Threats();
			var nearIndex = IndexOf(threats, near);
			var farIndex = IndexOf(threats, far);

			// Premise: nothing has hit a friendly, so the recent-attacker bump cannot be what orders these two.
			Assert.That(threats.All(t => t.AttackTarget == null), Is.True, "Premise: no threat has hit a friendly yet.");

			Assert.That(nearIndex, Is.GreaterThanOrEqualTo(0), "The near infantry should be a threat.");
			Assert.That(farIndex, Is.GreaterThanOrEqualTo(0), "The far artillery should be a threat.");
			Assert.That((far.CenterPosition - friend.CenterPosition).Length, Is.GreaterThan((near.CenterPosition - friend.CenterPosition).Length),
				"Premise: the artillery is the farther of the two.");
			Assert.That(farIndex, Is.LessThan(nearIndex),
				"By standoff, the artillery (8 - 12 = -4 cells) should rank above the infantry (6 - 5 = 1 cell), though it is farther.");
		}

		[TestCase(TestName = "A fogged, never-seen enemy is absent from the threats until a real GPS launch makes it known")]
		public void FoggedNeverSeenEnemyAppearsAfterLaunch()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			// The AgentFogTest setup: an enemy unit at the enemy's home, out of every local unit's sight.
			var enemy = hw.CreateActor(Infantry, hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3), hw.EnemyPlayer);
			hw.Tick(SettleTicks);

			Assert.That(local.Shroud.IsVisible(enemy.CenterPosition), Is.False, "Precondition: the enemy unit should be out of sight.");
			Assert.That(Known(enemy, local), Is.False, "Precondition: the enemy unit should be unknown before the launch.");
			Assert.That(IndexOf(Threats(), enemy), Is.EqualTo(-1), "A hidden, never-seen enemy must be absent from the threats.");

			LaunchSatellite(hw, local);
			hw.Tick(SettleTicks);

			Assert.That(Known(enemy, local), Is.True, "After the real GPS launch, the enemy unit should be known.");
			Assert.That(IndexOf(Threats(), enemy), Is.GreaterThanOrEqualTo(0), "After the launch, the known enemy should be a threat.");
		}

		[TestCase(TestName = "A threat's bearing is from its closest friendly toward it, its heading is the way it faces, and the entry has exactly the threat keys")]
		public void BearingFromClosestFriendlyAndHeadingFromFacing()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var threat = hw.CreateActor(Infantry, Clearing(hw), hw.EnemyPlayer);
			var closest = hw.CreateActor(Infantry, threat.Location + new CVec(3, 0), local);
			var farther = hw.CreateActor(Infantry, threat.Location + new CVec(0, 10), local);
			threat.TraitOrDefault<IFacing>().Facing = new WAngle(768);
			hw.Tick(SettleTicks);
			LaunchSatellite(hw, local);
			hw.Tick(SettleTicks);

			using var state = AgentStateDumpTest.State();
			var entry = state.RootElement.GetProperty("threats").EnumerateArray().Single(t => t.GetProperty("id").GetUInt32() == threat.ActorID);

			Assert.That(entry.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(
				["id", "type", "cell", "hp", "attack_target", "heading", "bearing", "friendly_id", "distance"]),
				"A threat entry should carry exactly these keys.");

			Assert.That(entry.GetProperty("friendly_id").GetUInt32(), Is.EqualTo(closest.ActorID),
				"The named friendly should be the closest one, three cells away, not the one ten cells away.");
			Assert.That(entry.GetProperty("bearing").GetString(), Is.EqualTo("W"),
				"From the closest friendly, the threat lies to the west.");
			Assert.That(entry.GetProperty("heading").GetString(), Is.EqualTo("E"),
				"The threat faces east (facing 768), which is distinct from its bearing.");
			Assert.That(entry.GetProperty("distance").GetInt32(), Is.InRange(3072 - 512, 3072 + 512),
				"The distance to the closest friendly is about three cells, in world units.");
			Assert.That(farther.IsDead, Is.False);
		}

		[TestCase(TestName = "A threat that recently hit a friendly is bumped to the front, with attack_target set to that friendly")]
		public void RecentAttackerIsBumpedToTheFront()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			var enemy = hw.EnemyPlayer;
			hw.Tick(SettleTicks);

			// The decoy and the attacker stand outside infantry range (5.1 cells), so neither shoots during the settle ticks and only
			// the injected hit below makes the attacker recent. The decoy (6 - 5 = 1) ranks above the attacker (9 - 5 = 4) by standoff.
			var victim = hw.CreateActor(Infantry, Clearing(hw), local);
			var decoy = hw.CreateActor(Infantry, victim.Location + new CVec(6, 0), enemy);
			var attacker = hw.CreateActor(Infantry, victim.Location + new CVec(0, 9), enemy);
			hw.Tick(SettleTicks);
			LaunchSatellite(hw, local);
			hw.Tick(SettleTicks);

			var before = Threats();
			Assert.That(IndexOf(before, decoy), Is.LessThan(IndexOf(before, attacker)),
				"Premise: by standoff, the decoy (6 - 5 = 1 cell) ranks above the attacker (9 - 5 = 4 cells).");
			Assert.That(before.All(t => t.AttackTarget == null), Is.True, "Before any hit, no threat names a target.");

			// The attacker hits the victim, through the real health path.
			victim.TraitOrDefault<IHealth>().InflictDamage(victim, attacker, new Damage(100), true);

			var after = Threats();
			Assert.That(after[0].Id, Is.EqualTo(attacker.ActorID), "The recent attacker should be bumped to the front.");
			Assert.That(after[0].AttackTarget, Is.EqualTo(victim.ActorID), "The bumped attacker should name the friendly it hit.");
		}
	}
}
