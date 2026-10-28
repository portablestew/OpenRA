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

using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The Monte Carlo target rule: the mode is Off when disabled, Active for exactly one selected, alive, own,
	/// mobile actor, and Idle for any other selection. Built on a headless ra world, where "e1" is a mobile
	/// infantry unit and "powr" is a building with no mobility trait.
	/// </summary>
	[TestFixture]
	sealed class MonteCarloTargetTest
	{
		const int Seed = 0x3C1;
		const string Infantry = "e1";
		const string Building = "powr";

		HeadlessWorld hw;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[SetUp]
		public void SetUp()
		{
			MonteCarloMode.Clear();
			MonteCarloMode.Toggle();
			hw = HeadlessWorld.Create(Seed);
		}

		[TearDown]
		public void TearDown()
		{
			hw.Dispose();
			MonteCarloMode.Clear();
		}

		/// <summary>
		/// Runs the rule and returns the state's name, so the assertions read "Active" rather than the enum.
		/// </summary>
		(string State, Actor Target) Resolve()
		{
			var state = MonteCarloTarget.Resolve(hw.World, out var target);

			return (state.ToString(), target);
		}

		Actor CreateUnit(string type, Player owner, CPos near)
		{
			return hw.CreateActor(type, hw.FindClearArea(near, 1, 1), owner);
		}

		void Select(params Actor[] actors)
		{
			foreach (var actor in actors)
				hw.World.Selection.Add(actor);
		}

		[Test]
		public void OffIsOffForAnEligibleUnit()
		{
			MonteCarloMode.Clear();
			Select(CreateUnit(Infantry, hw.LocalPlayer, new CPos(56, 50)));

			var (state, target) = Resolve();

			Assert.That(state, Is.EqualTo("Off"));
			Assert.That(target, Is.Null);
		}

		[Test]
		public void NoSelectionIsIdle()
		{
			var (state, target) = Resolve();

			Assert.That(state, Is.EqualTo("Idle"));
			Assert.That(target, Is.Null);
		}

		[Test]
		public void TwoSelectedUnitsAreIdle()
		{
			Select(CreateUnit(Infantry, hw.LocalPlayer, new CPos(56, 50)),
				CreateUnit(Infantry, hw.LocalPlayer, new CPos(60, 50)));

			var (state, target) = Resolve();

			Assert.That(state, Is.EqualTo("Idle"));
			Assert.That(target, Is.Null);
		}

		[Test]
		public void OneOwnMobileUnitIsActive()
		{
			var unit = CreateUnit(Infantry, hw.LocalPlayer, new CPos(56, 50));
			Select(unit);

			var (state, target) = Resolve();

			Assert.That(state, Is.EqualTo("Active"));
			Assert.That(target, Is.SameAs(unit));
		}

		[Test]
		public void OneEnemyUnitIsIdle()
		{
			Select(CreateUnit(Infantry, hw.EnemyPlayer, new CPos(56, 50)));

			var (state, target) = Resolve();

			Assert.That(state, Is.EqualTo("Idle"));
			Assert.That(target, Is.Null);
		}

		[Test]
		public void OneOwnBuildingIsIdle()
		{
			Select(CreateUnit(Building, hw.LocalPlayer, new CPos(56, 50)));

			var (state, target) = Resolve();

			Assert.That(state, Is.EqualTo("Idle"));
			Assert.That(target, Is.Null);
		}

		[Test]
		public void OneDeadUnitIsIdle()
		{
			var unit = CreateUnit(Infantry, hw.LocalPlayer, new CPos(56, 50));
			var killer = CreateUnit(Infantry, hw.EnemyPlayer, new CPos(60, 50));
			Select(unit);
			unit.Kill(killer);

			var (state, target) = Resolve();

			Assert.That(state, Is.EqualTo("Idle"));
			Assert.That(target, Is.Null);
		}
	}
}
