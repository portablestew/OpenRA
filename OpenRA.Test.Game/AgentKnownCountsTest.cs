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
using System.Text.Json;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The state digest's own structures and known counts (P2.5). An own structure in state units carries only id, type, cell and
	/// hp. Each player's known_counts is the count by type the local player knows: all of its own units, and another player's only
	/// where AgentFog says they are known. A fogged, never-seen enemy is not counted until a real GPS launch shows it.
	/// </summary>
	[TestFixture]
	sealed class AgentKnownCountsTest
	{
		const int Seed = 0x4C0E;
		const string Infantry = "e1";
		const string Structure = "powr";
		const string Satellite = "atek";

		/// <summary>How far the never-seen enemy stands from the local unit, in cells: outside its sight.</summary>
		const int Separation = 15;

		/// <summary>Ticks to let the map settle, and visibility and the satellite update.</summary>
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

		/// <summary>
		/// The expected counts for a player, computed from the world with AgentFog: the observer's own units all count, and another
		/// player's count only when known. The same filters as the digest: occupying and not scenery.
		/// </summary>
		static Dictionary<string, int> Expected(HeadlessWorld hw, Player owner, Player observer)
		{
			return hw.World.Actors
				.Where(a => a.Owner == owner && a.OccupiesSpace != null && !a.IsDead && a.Owner.PlayerReference?.OwnsWorld != true
					&& a.Info.Name != "mpspawn" && (owner == observer || Known(a, observer)))
				.GroupBy(a => a.Info.Name)
				.ToDictionary(g => g.Key, g => g.Count());
		}

		static JsonElement PlayerEntry(JsonElement state, string name)
		{
			return state.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("name").GetString() == name);
		}

		static Dictionary<string, int> KnownCountsOf(JsonElement state, string name)
		{
			return PlayerEntry(state, name).GetProperty("known_counts").EnumerateObject()
				.ToDictionary(p => p.Name, p => p.Value.GetInt32());
		}

		static HashSet<string> Keys(JsonElement element)
		{
			return element.EnumerateObject().Select(p => p.Name).ToHashSet();
		}

		[TestCase(TestName = "Own structures are slim, and known counts match the known actors: a fogged enemy counts only after a GPS launch")]
		public void SlimStructuresAndKnownCounts()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			var enemy = hw.EnemyPlayer;
			hw.Tick(SettleTicks);

			var unit = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			var structure = hw.CreateActor(Structure, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			var near = hw.CreateActor(Infantry, unit.Location + new CVec(2, 0), enemy);
			var far = hw.CreateActor(Infantry, unit.Location + new CVec(Separation, 0), enemy);
			hw.Tick(SettleTicks);

			Assert.That(local.Shroud.IsVisible(near.CenterPosition), Is.True, "Precondition: the near enemy should be visible.");
			Assert.That(local.Shroud.IsVisible(far.CenterPosition), Is.False, "Precondition: the far enemy should be out of sight.");
			Assert.That(Known(far, local), Is.False, "Precondition: the far enemy should be unknown before the launch.");

			// Before the launch.
			using var before = AgentStateDumpTest.State();
			var units = before.RootElement.GetProperty("units").EnumerateArray().ToArray();

			var slim = units.Single(u => u.GetProperty("id").GetUInt32() == structure.ActorID);
			Assert.That(Keys(slim), Is.EquivalentTo(["id", "type", "cell", "hp", "repairing"]),
				"An own structure should carry only id, type, cell, hp and repairing (powered_down only with a PowerDown toggle).");

			var full = units.Single(u => u.GetProperty("id").GetUInt32() == unit.ActorID);
			Assert.That(Keys(full), Does.Contain("pos").And.Contain("facing").And.Contain("activity"),
				"An own unit keeps its full entry.");

			var beforeEnemy = KnownCountsOf(before.RootElement, enemy.PlayerName);
			Assert.That(beforeEnemy, Is.EqualTo(Expected(hw, enemy, local)),
				"The enemy's known counts should equal the enemy actors the local player knows, by type.");
			Assert.That(KnownCountsOf(before.RootElement, local.PlayerName), Is.EqualTo(Expected(hw, local, local)),
				"The local player's known counts should cover everything it owns.");

			// The far enemy is never seen: it is not counted.
			var beforeE1 = beforeEnemy.GetValueOrDefault(Infantry);
			Assert.That(beforeE1, Is.EqualTo(Expected(hw, enemy, local).GetValueOrDefault(Infantry)));

			// The real GPS launch makes the far enemy known, and it is counted from then on.
			LaunchSatellite(hw, local);
			hw.Tick(SettleTicks);
			Assert.That(Known(far, local), Is.True, "After the GPS launch, the far enemy should be known.");

			using var after = AgentStateDumpTest.State();
			var afterEnemy = KnownCountsOf(after.RootElement, enemy.PlayerName);
			Assert.That(afterEnemy, Is.EqualTo(Expected(hw, enemy, local)),
				"After the launch, the enemy's known counts should equal the enemy actors now known, by type.");
			Assert.That(afterEnemy.GetValueOrDefault(Infantry), Is.GreaterThan(beforeE1),
				"After the launch, the far enemy should be counted in the enemy's infantry.");
		}

		[TestCase(TestName = "A player not allied with the local player has no funds, cash_funds, resource_funds, "
			+ "resource_funds_capacity or power, but keeps known_counts")]
		public void EnemyEconomyIsHiddenButKnownCountsRemain()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			var enemy = hw.EnemyPlayer;
			hw.Tick(SettleTicks);

			using var state = AgentStateDumpTest.State();
			var root = state.RootElement;

			var hidden = PlayerEntry(root, enemy.PlayerName);
			foreach (var key in new[] { "funds", "cash_funds", "resource_funds", "resource_funds_capacity", "power" })
				Assert.That(hidden.GetProperty(key).ValueKind, Is.EqualTo(JsonValueKind.Null), $"The enemy's {key} should be null.");

			Assert.That(hidden.GetProperty("known_counts").ValueKind, Is.EqualTo(JsonValueKind.Object),
				"The enemy's known_counts should still be present.");
			Assert.That(Keys(hidden), Is.SupersetOf(["name", "faction", "dead", "bot", "handicap", "known_counts"]));

			var own = PlayerEntry(root, local.PlayerName);
			Assert.That(own.GetProperty("funds").ValueKind, Is.EqualTo(JsonValueKind.Number), "The local player's funds should be an int.");
			Assert.That(own.GetProperty("cash_funds").ValueKind, Is.EqualTo(JsonValueKind.Number), "The local player's cash_funds should be an int.");

			var ally = hw.World.Players.FirstOrDefault(p => p != local && p != enemy && local.IsAlliedWith(p));
			if (ally == null)
				Assert.Inconclusive("The headless world has no allied player, so an ally's economy cannot be checked here.");

			var allied = PlayerEntry(root, ally.PlayerName);
			Assert.That(allied.GetProperty("cash_funds").ValueKind, Is.EqualTo(JsonValueKind.Number), "An allied player's cash_funds should still be an int.");
		}
	}
}
