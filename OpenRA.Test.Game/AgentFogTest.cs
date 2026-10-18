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
	/// The fog-policy helper (P2.2) and the Q1 gate. An enemy is known to the local player when it is visible, when the local
	/// player holds a valid last-known FrozenUnderFog snapshot of it, or when the local player's satellite is granted and the enemy
	/// shows a GPS dot. The satellite is the real Cnc GpsWatcher, launched through GpsAdd and ReachedOrbit. Infantry have no
	/// last-known snapshot in ra, so the snapshot case uses a structure (powr).
	/// </summary>
	/// <remarks>
	/// The mod's types are reached by name through ObjectCreator, as the other agent tests do. This project references only
	/// OpenRA.Game, so a direct reference to a mod type would have a different identity from the one ObjectCreator loads.
	/// </remarks>
	[TestFixture]
	sealed class AgentFogTest
	{
		const int Seed = 0xF06;
		const string Structure = "powr";
		const string Infantry = "e1";
		const string Wall = "sbag";
		const string Satellite = "atek";

		/// <summary>Ticks to let the map's starting units settle, and to let visibility, frozen snapshots and the satellite update.</summary>
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

		/// <summary>The fog helper under test (AgentSocket.AgentFog.Known), called through the mod's type.</summary>
		static bool Known(Actor actor, Player observer)
		{
			return (bool)ModType("AgentSocket.AgentFog").GetMethod("Known").Invoke(null, [actor, observer]);
		}

		/// <summary>The reporting rule (AgentSocket.AgentFog.Reported), called through the mod's type.</summary>
		static bool Reported(Actor actor, Player observer)
		{
			return (bool)ModType("AgentSocket.AgentFog").GetMethod("Reported").Invoke(null, [actor, observer]);
		}

		/// <summary>The player's GpsWatcher trait, read through the mod's type.</summary>
		static object Watcher(Player player)
		{
			var trait = ModType("Traits.GpsWatcher");
			return typeof(Actor).GetMethod("Trait").MakeGenericMethod(trait).Invoke(player.PlayerActor, null);
		}

		/// <summary>The grant the satellite gives the player's radar dots: its own grant, or an allied watcher's.</summary>
		static bool GrantsRadarDots(Player player)
		{
			var watcher = Watcher(player);
			var type = watcher.GetType();
			return (bool)type.GetProperty("Granted").GetValue(watcher) || (bool)type.GetProperty("GrantedAllies").GetValue(watcher);
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

		[TestCase(TestName = "Q1: the real GPS launch widens known to a never-seen enemy unit, which is unknown before the launch and known after it")]
		public void GpsStyleRevealWidensKnown()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var area = hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3);
			var unit = hw.CreateActor(Infantry, area, hw.EnemyPlayer);
			var wall = hw.CreateActor(Wall, hw.FindClearArea(area, 2, 2, 12), hw.EnemyPlayer);
			var own = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			hw.Tick(SettleTicks);

			// Precondition: fog is on, and no local unit sees the enemy unit or the wall.
			Assert.That(local.Shroud.FogEnabled, Is.True, "The test needs fog on.");
			Assert.That(local.Shroud.IsVisible(unit.CenterPosition), Is.False, "The enemy unit should start out of sight.");
			Assert.That(local.Shroud.IsVisible(wall.CenterPosition), Is.False, "The enemy wall should start out of sight.");

			Assert.That(GrantsRadarDots(local), Is.False, "Before the launch, no satellite grants radar dots.");
			Assert.That(Known(unit, local), Is.False, "Before the launch, a hidden, never-seen enemy unit is unknown.");
			Assert.That(Known(wall, local), Is.False, "Before the launch, a hidden enemy wall is unknown.");
			Assert.That(Known(own, local), Is.True, "The local player's own unit is known.");

			LaunchSatellite(hw, local);
			hw.Tick(SettleTicks);

			Assert.That(GrantsRadarDots(local), Is.True, "The launched satellite should grant radar dots.");
			Assert.That(local.Shroud.IsExplored(unit.CenterPosition), Is.True, "The launch should explore the unit's cell.");
			Assert.That(local.Shroud.IsVisible(unit.CenterPosition), Is.False, "The launch must not make the enemy unit visible.");

			// The widening: the unit is now known, through the GPS dot rule alone.
			Assert.That(Known(unit, local), Is.True, "After the real GPS launch, the enemy unit must be known.");

			// No special case beyond the dot rule: a wall has no GPS dot, so the launch does not reveal it.
			Assert.That(Known(wall, local), Is.False, "A wall has no GPS dot, so the launch must not reveal it.");

			// The own unit is still known through its own sight.
			Assert.That(Known(own, local), Is.True, "The local player's own unit stays known after the launch.");
		}

		[TestCase(TestName = "An allied unit is known through shared vision, before and after a GPS launch, as allied units normally are")]
		public void AlliedUnitIsNotRevealedByGps()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var ally = hw.World.Players.FirstOrDefault(p => p != local && p != hw.EnemyPlayer && local.IsAlliedWith(p));
			if (ally == null)
				Assert.Inconclusive("The headless world has no allied player, so the allied case cannot run here.");

			// Allied vision is shared, so the unit goes where no unit or building of any player is: the cell farthest from all of them.
			var placed = hw.World.Actors.Where(a => a.OccupiesSpace != null).Select(a => a.Location).ToArray();
			var farthest = hw.World.Map.AllCells
				.Where((c, i) => i % 7 == 0)
				.OrderByDescending(c => placed.Min(p => Math.Max(Math.Abs(c.X - p.X), Math.Abs(c.Y - p.Y))))
				.First();
			var allied = hw.CreateActor(Infantry, farthest, ally);
			hw.Tick(SettleTicks);

			// The allied unit sees for its owner, and allied vision is shared, so the local player sees it through the alliance.
			Assert.That(local.Shroud.IsVisible(allied.CenterPosition), Is.True, "Shared allied vision should show the allied unit to the local player.");
			Assert.That(Known(allied, local), Is.True, "Before the launch, an allied unit is known through shared vision.");

			LaunchSatellite(hw, local);
			hw.Tick(SettleTicks);

			Assert.That(GrantsRadarDots(local), Is.True, "The launched satellite should grant radar dots.");
			Assert.That(Known(allied, local), Is.True, "An allied unit stays known through shared vision after the launch, as allied units normally are.");
		}

		[TestCase(TestName = "Q1: a visible enemy structure is known, and after it fogs its frozen last-known snapshot keeps it known")]
		public void VisibleThenLastKnown()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var area = hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3);
			var target = hw.CreateActor(Structure, area, hw.EnemyPlayer);
			hw.Tick(SettleTicks);
			Assert.That(local.Shroud.IsVisible(target.CenterPosition), Is.False, "The target should start out of sight.");
			Assert.That(Known(target, local), Is.False, "Before any sight, the target is unknown.");

			// A local scout next to the target makes it visible.
			var scout = hw.CreateActor(Infantry, area + new CVec(3, 0));
			hw.Tick(SettleTicks);

			Assert.That(local.Shroud.IsVisible(target.CenterPosition), Is.True, "The scout should make the target visible.");
			Assert.That(Known(target, local), Is.True, "A visible enemy must be known.");

			// The scout leaves: the target fogs again, and only the last-known snapshot remains.
			scout.Dispose();
			hw.Tick(SettleTicks);

			Assert.That(local.Shroud.IsVisible(target.CenterPosition), Is.False, "The target should be fogged again.");
			var frozen = local.PlayerActor.TraitOrDefault<FrozenActorLayer>()?.FromID(target.ActorID);
			Assert.That(frozen, Is.Not.Null, "A seen structure should have a frozen last-known entry.");
			Assert.That(frozen.IsValid, Is.True, "The frozen entry should be valid: it was refreshed while the target was seen.");
			Assert.That(Known(target, local), Is.True, "A last-known enemy must stay known.");
		}

		[TestCase(TestName = "An actor with no position is not known, but its owner still reports it: the bot's order on the Player actor")]
		public void PositionlessActorIsNotKnownButItsOwnerReportsIt()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			// The engine's Player actor has no IOccupySpace, so it has no CenterPosition. Bots order RepairBuilding
			// (BuildingRepairBotModule) and PlaceBuilding (BaseBuilderQueueManager) on it, and the order event's fog
			// check crashed on the null position before Known guarded it (found live with a load_skirmish bot).
			var playerActor = local.PlayerActor;
			Assert.That(playerActor.OccupiesSpace, Is.Null,
				"The Player actor must have no IOccupySpace for this test to mean anything.");

			Assert.That(Known(playerActor, local), Is.False, "A positionless actor has no shroud visibility.");
			Assert.That(Reported(playerActor, local), Is.True, "Its owner still reports it.");
			Assert.That(Reported(playerActor, hw.EnemyPlayer), Is.False, "Another player does not report it.");
		}

		[TestCase(TestName = "A unit that fogs after being seen is unknown in ra: FrozenUnderFog is on structures and walls, not infantry")]
		public void FoggedUnitHasNoLastKnown()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var area = hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3);
			var target = hw.CreateActor(Infantry, area, hw.EnemyPlayer);
			hw.Tick(SettleTicks);

			var scout = hw.CreateActor(Infantry, area + new CVec(3, 0));
			hw.Tick(SettleTicks);
			Assert.That(Known(target, local), Is.True, "A visible unit must be known.");

			scout.Dispose();
			hw.Tick(SettleTicks);

			Assert.That(local.Shroud.IsVisible(target.CenterPosition), Is.False, "The unit should be fogged again.");
			Assert.That(local.PlayerActor.TraitOrDefault<FrozenActorLayer>()?.FromID(target.ActorID), Is.Null,
				"A unit has no frozen snapshot in ra.");
			Assert.That(Known(target, local), Is.False, "A fogged unit with no snapshot is unknown.");
		}
	}
}
