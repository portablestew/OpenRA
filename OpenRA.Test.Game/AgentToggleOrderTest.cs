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
