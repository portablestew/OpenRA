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
	/// A toggle order as its desired state (3.3): on is compared with the toggle's state, one engine toggle is issued when they
	/// differ, and the reply says what the state is now. Runs in a real world, so the state is read after the order settles.
	/// </summary>
	[TestFixture]
	sealed class AgentToggleOrderTest
	{
		// The toggle lives in OpenRA.Mods.Common, which this project reaches only by reflection.
		static readonly Lazy<Type> ToggleType = new(() => HeadlessMod.ModData.ObjectCreator.FindType("Traits.ToggleConditionOnOrder"));
		static readonly Lazy<Type> RepairableType = new(() => HeadlessMod.ModData.ObjectCreator.FindType("Traits.RepairableBuilding"));

		/// <summary>Enough damage that a dome is still damaged after the settle window, when a repair step is 700 hit points.</summary>
		const int RepairDamage = 50000;

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

		static bool Enabled(Actor actor)
		{
			// ToggleConditionOnOrder implements IResolveOrder, so it is found through that interface and then by its type.
			var toggle = actor.TraitsImplementing<IResolveOrder>().Single(t => t.GetType() == ToggleType.Value);
			return (bool)ToggleType.Value.GetProperty("Enabled").GetValue(toggle);
		}

		static string Line(Actor actor, bool on)
		{
			return $"{{\"cmd\":\"order\",\"actor\":{actor.ActorID},\"order\":\"PowerDown\",\"on\":{(on ? "true" : "false")}}}";
		}

		[TestCase(TestName = "on: true from off turns the toggle on, reports state on and unchanged false")]
		public void OnFromOffTurnsTheToggleOn()
		{
			using var hw = HeadlessWorld.Create(1);
			var dome = hw.CreateActor("dome", hw.FindClearArea(new CPos(50, 50), 2, 2), hw.LocalPlayer);
			hw.Tick(1);
			Assert.That(Enabled(dome), Is.False);

			using var response = AgentStateDumpTest.RequestWithTicks(hw, Line(dome, true));
			var root = response.RootElement;
			Assert.That(root.TryGetProperty("error", out _), Is.False, root.ToString());
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(root.GetProperty("state").GetString(), Is.EqualTo("on"));
			Assert.That(root.GetProperty("unchanged").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("settled").GetBoolean(), Is.True);
			Assert.That(Enabled(dome), Is.True);
		}

		[TestCase(TestName = "on: true again is unchanged, issues no second toggle, and leaves the toggle on")]
		public void OnWhenAlreadyOnIsUnchangedAndDoesNotFlip()
		{
			using var hw = HeadlessWorld.Create(1);
			var dome = hw.CreateActor("dome", hw.FindClearArea(new CPos(50, 50), 2, 2), hw.LocalPlayer);
			hw.Tick(1);

			using (AgentStateDumpTest.RequestWithTicks(hw, Line(dome, true)))
				Assert.That(Enabled(dome), Is.True);

			using var response = AgentStateDumpTest.RequestWithTicks(hw, Line(dome, true));
			var root = response.RootElement;
			Assert.That(root.TryGetProperty("error", out _), Is.False, root.ToString());
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(root.GetProperty("state").GetString(), Is.EqualTo("on"));
			Assert.That(root.GetProperty("unchanged").GetBoolean(), Is.True);

			// A second engine toggle would turn it off. Running more ticks shows it stays on.
			hw.Tick(10);
			Assert.That(Enabled(dome), Is.True);
		}

		[TestCase(TestName = "on: false returns the toggle to off, reports state off and unchanged false")]
		public void OnFalseReturnsTheToggleToOff()
		{
			using var hw = HeadlessWorld.Create(1);
			var dome = hw.CreateActor("dome", hw.FindClearArea(new CPos(50, 50), 2, 2), hw.LocalPlayer);
			hw.Tick(1);

			using (AgentStateDumpTest.RequestWithTicks(hw, Line(dome, true)))
				Assert.That(Enabled(dome), Is.True);

			using var response = AgentStateDumpTest.RequestWithTicks(hw, Line(dome, false));
			var root = response.RootElement;
			Assert.That(root.TryGetProperty("error", out _), Is.False, root.ToString());
			Assert.That(root.GetProperty("state").GetString(), Is.EqualTo("off"));
			Assert.That(root.GetProperty("unchanged").GetBoolean(), Is.False);
			Assert.That(Enabled(dome), Is.False);
		}

		[TestCase(TestName = "on: false on a toggle that is already off is unchanged, and it is not flipped on")]
		public void OnFalseWhenAlreadyOffIsUnchanged()
		{
			using var hw = HeadlessWorld.Create(1);
			var dome = hw.CreateActor("dome", hw.FindClearArea(new CPos(50, 50), 2, 2), hw.LocalPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.RequestWithTicks(hw, Line(dome, false));
			var root = response.RootElement;
			Assert.That(root.GetProperty("state").GetString(), Is.EqualTo("off"));
			Assert.That(root.GetProperty("unchanged").GetBoolean(), Is.True);
			hw.Tick(10);
			Assert.That(Enabled(dome), Is.False);
		}

		/// <summary>The RepairBuilding line: the player actor is the subject, and target_actor names the building. The on key is optional here so the refusal can be tested.</summary>
		static string RepairLine(Actor player, Actor building, bool? on)
		{
			var onKey = on is { } value ? $",\"on\":{(value ? "true" : "false")}" : "";
			return $"{{\"cmd\":\"order\",\"actor\":{player.ActorID},\"order\":\"RepairBuilding\",\"target_actor\":{building.ActorID}{onKey}}}";
		}

		/// <summary>Whether the player is among the building's repairers, read by reflection like Enabled (the test project cannot reference the mod).</summary>
		static bool IsRepairedBy(Actor building, Player player)
		{
			var repairable = RepairableOf(building);
			var repairers = (System.Collections.IEnumerable)repairable.GetType().GetField("Repairers").GetValue(repairable);
			return repairers.Cast<object>().Contains(player);
		}

		static object RepairableOf(Actor building)
		{
			var method = typeof(Actor).GetMethods().Single(m => m.Name == "TraitOrDefault" && m.GetParameters().Length == 0);
			return method.MakeGenericMethod(RepairableType.Value).Invoke(building, null);
		}

		/// <summary>Damages a building well beyond one repair step, so it stays damaged, and so stays repairable, through the settle window.</summary>
		static void Damage(HeadlessWorld hw, Actor building)
		{
			var enemy = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 60), 1, 1), hw.EnemyPlayer);
			building.TraitOrDefault<IHealth>().InflictDamage(building, enemy, new Damage(RepairDamage), true);
			AgentProductionTest.SetLocalCash(hw, 10000);
		}

		[TestCase(TestName = "RepairBuilding on from off makes the player a repairer: state on, unchanged false, and settled")]
		public void RepairOnFromOffMakesThePlayerARepairer()
		{
			using var hw = HeadlessWorld.Create(1);
			var dome = hw.CreateActor("dome", hw.FindClearArea(new CPos(50, 50), 2, 2), hw.LocalPlayer);
			hw.Tick(1);
			Damage(hw, dome);
			Assert.That(IsRepairedBy(dome, hw.LocalPlayer), Is.False);

			using var response = AgentStateDumpTest.RequestWithTicks(hw, RepairLine(hw.LocalPlayer.PlayerActor, dome, true));
			var root = response.RootElement;
			Assert.That(root.TryGetProperty("error", out _), Is.False, root.ToString());
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(root.GetProperty("state").GetString(), Is.EqualTo("on"));
			Assert.That(root.GetProperty("unchanged").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("settled").GetBoolean(), Is.True);
			Assert.That(IsRepairedBy(dome, hw.LocalPlayer), Is.True);
		}

		[TestCase(TestName = "RepairBuilding on when already repairing is unchanged, issues no second toggle, and keeps the player repairing")]
		public void RepairOnWhenAlreadyRepairingIsUnchanged()
		{
			using var hw = HeadlessWorld.Create(1);
			var dome = hw.CreateActor("dome", hw.FindClearArea(new CPos(50, 50), 2, 2), hw.LocalPlayer);
			hw.Tick(1);
			Damage(hw, dome);

			using (AgentStateDumpTest.RequestWithTicks(hw, RepairLine(hw.LocalPlayer.PlayerActor, dome, true)))
				Assert.That(IsRepairedBy(dome, hw.LocalPlayer), Is.True);

			using var response = AgentStateDumpTest.RequestWithTicks(hw, RepairLine(hw.LocalPlayer.PlayerActor, dome, true));
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(root.GetProperty("state").GetString(), Is.EqualTo("on"));
			Assert.That(root.GetProperty("unchanged").GetBoolean(), Is.True);

			// A second engine toggle would remove the player. Running more ticks shows the player is still a repairer.
			hw.Tick(10);
			Assert.That(IsRepairedBy(dome, hw.LocalPlayer), Is.True);
		}

		[TestCase(TestName = "RepairBuilding off removes the player: state off, unchanged false")]
		public void RepairOffRemovesThePlayer()
		{
			using var hw = HeadlessWorld.Create(1);
			var dome = hw.CreateActor("dome", hw.FindClearArea(new CPos(50, 50), 2, 2), hw.LocalPlayer);
			hw.Tick(1);
			Damage(hw, dome);

			using (AgentStateDumpTest.RequestWithTicks(hw, RepairLine(hw.LocalPlayer.PlayerActor, dome, true)))
				Assert.That(IsRepairedBy(dome, hw.LocalPlayer), Is.True);

			using var response = AgentStateDumpTest.RequestWithTicks(hw, RepairLine(hw.LocalPlayer.PlayerActor, dome, false));
			var root = response.RootElement;
			Assert.That(root.GetProperty("state").GetString(), Is.EqualTo("off"));
			Assert.That(root.GetProperty("unchanged").GetBoolean(), Is.False);
			Assert.That(IsRepairedBy(dome, hw.LocalPlayer), Is.False);
		}

		[TestCase(TestName = "RepairBuilding without target_actor is refused, since there is no building whose state to read")]
		public void RepairWithoutTargetIsRefused()
		{
			using var hw = HeadlessWorld.Create(1);
			var dome = hw.CreateActor("dome", hw.FindClearArea(new CPos(50, 50), 2, 2), hw.LocalPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.Request($"{{\"cmd\":\"order\",\"actor\":{hw.LocalPlayer.PlayerActor.ActorID},\"order\":\"RepairBuilding\",\"on\":true}}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo("RepairBuilding needs target_actor, the building to repair"));
			Assert.That(IsRepairedBy(dome, hw.LocalPlayer), Is.False);
		}

		[TestCase(TestName = "RepairBuilding on a unit is refused, since a unit cannot be repaired by a building repair")]
		public void RepairOnAUnitIsRefused()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.Request(RepairLine(hw.LocalPlayer.PlayerActor, unit, true));
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo($"actor {unit.ActorID} is not a building that can be repaired"));
		}

		[TestCase(TestName = "on with a group selector is refused, since a group's actors may disagree about the state")]
		public void OnIsRefusedForAGroup()
		{
			using var hw = HeadlessWorld.Create(1);
			var dome = hw.CreateActor("dome", hw.FindClearArea(new CPos(50, 50), 2, 2), hw.LocalPlayer);
			hw.Tick(1);

			using var response = AgentStateDumpTest.Request("{\"cmd\":\"order\",\"type\":\"dome\",\"order\":\"PowerDown\",\"on\":true}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("error").GetString(), Is.EqualTo("on applies to a single actor, not a group"));
			Assert.That(Enabled(dome), Is.False);
		}
	}
}
