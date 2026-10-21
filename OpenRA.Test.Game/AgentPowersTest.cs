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
	/// Support powers: the powers command lists each power the local player can see, an order for a charging power is
	/// refused with its time left, a ready power activates, and power_ready fires once per charge. An enemy's power is listed
	/// and reported only when its DisplayTimerRelationships allows the local player's relationship (the engine's timer rule).
	/// </summary>
	[TestFixture]
	sealed class AgentPowersTest
	{
		/// <summary>The key prefix of the nuke power: the info class name, then Order (SupportPowerInfo). The key has no actor id.</summary>
		const string NukePrefix = "NukePowerInfo";

		/// <summary>The key prefix of the parabombs proxy's airstrike. AllowMultiple, so the key ends with the actor id.</summary>
		const string AirstrikePrefix = "AirstrikePowerInfo";

		/// <summary>Longer than the nuke's 13500-tick charge, so a charge always completes within it.</summary>
		const int ChargeBudget = 14000;

		bool savedAgentSocket;
		bool savedHub;
		object savedSink;
		Type hub;

		sealed record Power(string Order, string Owner, bool Ready, int RemainingTicks, int TotalTicks, bool Active, bool Disabled, bool OneShot);

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
			hub = HeadlessMod.ModData.ObjectCreator.FindType("AgentSocket.AgentEvents");
		}

		[SetUp]
		public void EnableAgentSocket()
		{
			savedAgentSocket = Game.Settings.Debug.AgentSocket;
			Game.Settings.Debug.AgentSocket = true;
		}

		[TearDown]
		public void RestoreHub()
		{
			Game.Settings.Debug.AgentSocket = savedAgentSocket;
			if (savedSink != null)
			{
				hub.GetField("Sink").SetValue(null, savedSink);
				hub.GetProperty("Enabled").SetValue(null, savedHub);
				savedSink = null;
			}
		}

		/// <summary>Turns the hub on and collects every event it raises from now on. Must run after HeadlessWorld.Create.</summary>
		List<object> WatchEvents()
		{
			var seen = new List<object>();
			savedHub = (bool)hub.GetProperty("Enabled").GetValue(null);
			savedSink = hub.GetField("Sink").GetValue(null);
			hub.GetProperty("Enabled").SetValue(null, true);
			hub.GetField("Sink").SetValue(null, new Action<object>(dto => seen.Add(dto)));
			return seen;
		}

		static Type ModType(string name)
		{
			return HeadlessMod.ModData.ObjectCreator.FindType(name);
		}

		static object Field(object target, string name)
		{
			return target.GetType().GetProperty(name).GetValue(target);
		}

		/// <summary>A trait of a mod type on the actor, read by reflection since the test project cannot reference the mod.</summary>
		static object ModTrait(Actor actor, string name)
		{
			var type = ModType(name);
			var method = typeof(Actor).GetMethods().Single(m => m.Name == "TraitOrDefault" && m.GetParameters().Length == 0);
			return method.MakeGenericMethod(type).Invoke(actor, null);
		}

		/// <summary>The local player's powers as the powers command reports them, one entry per power.</summary>
		static Power[] PowersOf()
		{
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"powers\"}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());

			return root.GetProperty("powers").EnumerateArray().Select(p => new Power(
				p.GetProperty("order").GetString(), p.GetProperty("owner").GetString(), p.GetProperty("ready").GetBoolean(),
				p.GetProperty("remaining_ticks").GetInt32(), p.GetProperty("total_ticks").GetInt32(), p.GetProperty("active").GetBoolean(),
				p.GetProperty("disabled").GetBoolean(), p.GetProperty("one_shot").GetBoolean())).ToArray();
		}

		/// <summary>Whether the owner's power with this key prefix is ready, read from the live trait so a tick loop stays cheap.</summary>
		static bool Ready(HeadlessWorld hw, string prefix)
		{
			var manager = ModTrait(hw.LocalPlayer.PlayerActor, "Traits.SupportPowerManager");
			var powers = (IDictionary)manager.GetType().GetField("Powers").GetValue(manager);
			foreach (DictionaryEntry entry in powers)
				if (((string)entry.Key).StartsWith(prefix, StringComparison.Ordinal))
					return (bool)Field(entry.Value, "Ready");

			return false;
		}

		/// <summary>The live support powers and the power state, for a failure message: what a charge is waiting on.</summary>
		static string Describe(HeadlessWorld hw)
		{
			var manager = ModTrait(hw.LocalPlayer.PlayerActor, "Traits.SupportPowerManager");
			var powers = (IDictionary)manager.GetType().GetField("Powers").GetValue(manager);
			var lines = new List<string>();
			foreach (DictionaryEntry entry in powers)
			{
				var power = entry.Value;
				lines.Add($"{entry.Key} active={Field(power, "Active")} disabled={Field(power, "Disabled")} remaining={Field(power, "RemainingTicks")}");
			}

			var grid = ModTrait(hw.LocalPlayer.PlayerActor, "Traits.PowerManager");
			lines.Add($"power state={Field(grid, "PowerState")}");
			return string.Join("; ", lines);
		}

		/// <summary>An order on the player actor, which holds the support powers, for the power key at a cell.</summary>
		static string PowerOrder(HeadlessWorld hw, string key, CPos cell)
		{
			return $"{{\"cmd\":\"order\",\"actor\":{hw.LocalPlayer.PlayerActor.ActorID},\"order\":\"{key}\",\"cell\":[{cell.X},{cell.Y}]}}";
		}

		[TestCase(TestName = "A charging power reports ready false, and its remaining ticks fall as the world ticks")]
		public void ChargingPowerCountsDown()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("mslo", hw.FindClearArea(new CPos(50, 50), 2, 1), hw.LocalPlayer);

			// A silo on low power is disabled (DisableOnLowPower), which pauses its charge, so the nuke needs two plants.
			hw.CreateActor("powr", hw.FindClearArea(new CPos(40, 50), 2, 2), hw.LocalPlayer);
			hw.CreateActor("powr", hw.FindClearArea(new CPos(40, 53), 2, 2), hw.LocalPlayer);
			hw.Tick(1);

			var first = PowersOf().Single(p => p.Order.StartsWith(NukePrefix, StringComparison.Ordinal) && p.Owner == hw.LocalPlayer.PlayerName);
			Assert.That(first.Ready, Is.False);
			Assert.That(first.Active, Is.True);
			Assert.That(first.RemainingTicks, Is.GreaterThan(0));
			Assert.That(first.TotalTicks, Is.EqualTo(13500));
			Assert.That(first.OneShot, Is.False);

			hw.Tick(100);
			var later = PowersOf().Single(p => p.Order == first.Order);
			Assert.That(later.Ready, Is.False);
			Assert.That(later.RemainingTicks, Is.EqualTo(first.RemainingTicks - 100));
		}

		[TestCase(TestName = "The player actor's orders list the power keys, so an agent can find them")]
		public void OrdersListThePowerKeys()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("mslo", hw.FindClearArea(new CPos(50, 50), 2, 1), hw.LocalPlayer);

			// A silo on low power is disabled (DisableOnLowPower), which pauses its charge, so the nuke needs two plants.
			hw.CreateActor("powr", hw.FindClearArea(new CPos(40, 50), 2, 2), hw.LocalPlayer);
			hw.CreateActor("powr", hw.FindClearArea(new CPos(40, 53), 2, 2), hw.LocalPlayer);
			hw.Tick(1);

			var key = PowersOf().Single(p => p.Order.StartsWith(NukePrefix, StringComparison.Ordinal)).Order;
			using var response = AgentStateDumpTest.Request($"{{\"cmd\":\"orders\",\"actor\":{hw.LocalPlayer.PlayerActor.ActorID}}}");
			var orders = response.RootElement.GetProperty("orders").EnumerateArray().Select(o => o.GetString()).ToArray();

			Assert.That(orders, Does.Contain(key));
		}

		[TestCase(TestName = "An order for a charging power is refused with ok false, an error, ready false and the ticks left")]
		public void OrderForChargingPowerIsRefused()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("mslo", hw.FindClearArea(new CPos(50, 50), 2, 1), hw.LocalPlayer);

			// A silo on low power is disabled (DisableOnLowPower), which pauses its charge, so the nuke needs two plants.
			hw.CreateActor("powr", hw.FindClearArea(new CPos(40, 50), 2, 2), hw.LocalPlayer);
			hw.CreateActor("powr", hw.FindClearArea(new CPos(40, 53), 2, 2), hw.LocalPlayer);
			hw.Tick(1);

			var key = PowersOf().Single(p => p.Order.StartsWith(NukePrefix, StringComparison.Ordinal)).Order;
			var target = new CPos(60, 60);
			using var response = AgentStateDumpTest.RequestWithTicks(hw, PowerOrder(hw, key, target));
			var root = response.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False, root.ToString());
			Assert.That(root.GetProperty("error").GetString(), Does.Contain("not ready"));
			Assert.That(root.GetProperty("ready").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("remaining_ticks").GetInt32(), Is.GreaterThan(0));
		}

		[TestCase(TestName = "A ready power activates, and its charge restarts")]
		public void ReadyPowerActivates()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.CreateActor("mslo", hw.FindClearArea(new CPos(50, 50), 2, 1), hw.LocalPlayer);

			// A silo on low power is disabled (DisableOnLowPower), which pauses its charge, so the nuke needs two plants.
			hw.CreateActor("powr", hw.FindClearArea(new CPos(40, 50), 2, 2), hw.LocalPlayer);
			hw.CreateActor("powr", hw.FindClearArea(new CPos(40, 53), 2, 2), hw.LocalPlayer);
			hw.Tick(1);

			Assert.That(hw.TickUntil(() => Ready(hw, NukePrefix), ChargeBudget), Is.Not.Null, Describe(hw));
			var key = PowersOf().Single(p => p.Order.StartsWith(NukePrefix, StringComparison.Ordinal)).Order;

			using var response = AgentStateDumpTest.RequestWithTicks(hw, PowerOrder(hw, key, new CPos(60, 60)));
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
			Assert.That(root.GetProperty("valid").GetBoolean(), Is.True, root.ToString());

			var after = PowersOf().Single(p => p.Order == key);
			Assert.That(after.Ready, Is.False);
			Assert.That(after.RemainingTicks, Is.GreaterThan(0));
		}

		[TestCase(TestName = "power_ready fires once per charge, and again only after the next charge completes")]
		public void PowerReadyFiresOncePerCharge()
		{
			using var hw = HeadlessWorld.Create(1);
			var events = WatchEvents();
			hw.CreateActor("mslo", hw.FindClearArea(new CPos(50, 50), 2, 1), hw.LocalPlayer);

			// A silo on low power is disabled (DisableOnLowPower), which pauses its charge, so the nuke needs two plants.
			hw.CreateActor("powr", hw.FindClearArea(new CPos(40, 50), 2, 2), hw.LocalPlayer);
			hw.CreateActor("powr", hw.FindClearArea(new CPos(40, 53), 2, 2), hw.LocalPlayer);
			hw.Tick(1);

			Assert.That(hw.TickUntil(() => Ready(hw, NukePrefix), ChargeBudget), Is.Not.Null, Describe(hw));
			hw.Tick(200);
			Assert.That(ReadyEvents(events, NukePrefix), Is.EqualTo(1), "One charge completed, so one power_ready.");

			var key = PowersOf().Single(p => p.Order.StartsWith(NukePrefix, StringComparison.Ordinal)).Order;
			using var response = AgentStateDumpTest.RequestWithTicks(hw, PowerOrder(hw, key, new CPos(60, 60)));
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.True, response.RootElement.ToString());

			Assert.That(hw.TickUntil(() => Ready(hw, NukePrefix), ChargeBudget), Is.Not.Null, Describe(hw));
			hw.Tick(200);
			Assert.That(ReadyEvents(events, NukePrefix), Is.EqualTo(2), "A second charge completed, so a second power_ready.");
		}

		[TestCase(TestName = "A power the local player does not see is not listed or reported, and its owner's own power is")]
		public void EnemyPowerIsShownOnlyWhenItsTimerRelationshipAllowsIt()
		{
			using var hw = HeadlessWorld.Create(1);
			var events = WatchEvents();

			// The enemy nuke shows its timer to enemies (DisplayTimerRelationships: Enemy). The parabombs airstrike shows only to its owner.
			hw.CreateActor("mslo", hw.FindClearArea(new CPos(50, 50), 2, 1), hw.EnemyPlayer);
			hw.CreateActor("powerproxy.parabombs", hw.FindClearArea(new CPos(50, 60), 1, 1), hw.EnemyPlayer);
			hw.CreateActor("powerproxy.parabombs", hw.FindClearArea(new CPos(60, 60), 1, 1), hw.LocalPlayer);
			hw.Tick(5);

			var powers = PowersOf();
			var enemy = hw.EnemyPlayer.PlayerName;
			var own = hw.LocalPlayer.PlayerName;

			Assert.That(powers.Any(p => p.Owner == enemy && p.Order.StartsWith(NukePrefix, StringComparison.Ordinal)), Is.True,
				"An enemy nuke is listed, since its timer is shown to enemies.");
			Assert.That(powers.Any(p => p.Owner == enemy && p.Order.StartsWith(AirstrikePrefix, StringComparison.Ordinal)), Is.False,
				"An enemy airstrike is not listed, since its timer is shown only to its owner.");
			Assert.That(powers.Any(p => p.Owner == own && p.Order.StartsWith(AirstrikePrefix, StringComparison.Ordinal)), Is.True,
				"The local player's own power is always listed.");

			Assert.That(events.Count(e => e.GetType().Name == "PowerReadyV1" && (string)Field(e, "Owner") == enemy
				&& ((string)Field(e, "Order")).StartsWith(AirstrikePrefix, StringComparison.Ordinal)), Is.EqualTo(0),
				"An enemy airstrike that finished charging is not reported.");
			Assert.That(events.Count(e => e.GetType().Name == "PowerReadyV1" && (string)Field(e, "Owner") == own), Is.GreaterThanOrEqualTo(1),
				"The local player's own airstrike is reported when it finishes charging.");
		}

		/// <summary>The power_ready events for the owner's power with this key prefix.</summary>
		static int ReadyEvents(List<object> events, string prefix)
		{
			return events.Count(e => e.GetType().Name == "PowerReadyV1" && ((string)Field(e, "Order")).StartsWith(prefix, StringComparison.Ordinal));
		}
	}
}
