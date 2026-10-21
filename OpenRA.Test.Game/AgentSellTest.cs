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

namespace OpenRA.Test
{
	/// <summary>
	/// A Sell order (3.4): the owner sells a building, the sold notify ends the order's await with outcome sold, and one sold
	/// event reports it. The sale disposes the building, so the reply and the event are read from the socket and the sink.
	/// </summary>
	[TestFixture]
	sealed class AgentSellTest
	{
		bool savedAgentSocket;
		bool savedHub;
		object savedSink;
		Type hub;

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

		static string Line(Actor actor)
		{
			return $"{{\"cmd\":\"order\",\"actor\":{actor.ActorID},\"order\":\"Sell\"}}";
		}

		/// <summary>
		/// Turns the hub on and collects every event it raises from now on. Must run after HeadlessWorld.Create: the world's
		/// AgentWorldBridge sets Enabled from the settings when the world loads, so an earlier setting is overwritten.
		/// </summary>
		List<object> WatchEvents()
		{
			var seen = new List<object>();
			savedHub = (bool)hub.GetProperty("Enabled").GetValue(null);
			savedSink = hub.GetField("Sink").GetValue(null);
			hub.GetProperty("Enabled").SetValue(null, true);
			hub.GetField("Sink").SetValue(null, new Action<object>(dto => seen.Add(dto)));
			return seen;
		}

		static object Field(object target, string name)
		{
			return target.GetType().GetProperty(name).GetValue(target);
		}

		static bool IsEvent(object dto, string type, uint actor)
		{
			return dto.GetType().Name == type && (uint)Field(dto, "Actor") == actor;
		}

		[TestCase(TestName = "A Sell on an owned building replies valid with outcome sold, and the building is disposed")]
		public void SellOfAnOwnedBuildingRepliesSold()
		{
			using var hw = HeadlessWorld.Create(1);
			var wall = hw.CreateActor("BRIK", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var events = WatchEvents();
			Assert.That(events, Is.Empty);

			using var response = AgentStateDumpTest.RequestWithTicks(hw, Line(wall));
			var root = response.RootElement;
			Assert.That(root.TryGetProperty("error", out _), Is.False, root.ToString());
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(root.GetProperty("valid").GetBoolean(), Is.True);
			Assert.That(root.GetProperty("outcome").GetString(), Is.EqualTo("sold"));
			Assert.That(root.GetProperty("settled").GetBoolean(), Is.True);
			Assert.That(wall.Disposed, Is.True, "The sale did not dispose the wall.");
		}

		[TestCase(TestName = "A sale raises exactly one sold event for the building, and no killed event for it")]
		public void SaleRaisesOneSoldEventAndNoKilled()
		{
			using var hw = HeadlessWorld.Create(1);
			var wall = hw.CreateActor("BRIK", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var id = wall.ActorID;
			var events = WatchEvents();

			using (AgentStateDumpTest.RequestWithTicks(hw, Line(wall)))
			{
				// The reply has settled, so the sale and its event have happened.
			}

			var sold = events.Where(e => IsEvent(e, "SoldV1", id)).ToArray();
			Assert.That(sold, Has.Length.EqualTo(1), "The sale should raise exactly one sold event.");
			Assert.That(Field(sold[0], "Event"), Is.EqualTo("sold"));
			Assert.That(Field(sold[0], "Type"), Is.EqualTo("brik"));
			Assert.That(Field(sold[0], "Owner"), Is.EqualTo(hw.LocalPlayer.PlayerName));
			Assert.That(events.Count(e => IsEvent(e, "KilledV1", id)), Is.EqualTo(0), "A sale is not a kill.");
		}

		[TestCase(TestName = "A Sell on a building whose Sellable is disabled is refused, and nothing is sold")]
		public void SellOfADisabledSellableIsRefused()
		{
			using var hw = HeadlessWorld.Create(1);
			var dome = hw.CreateActor("dome", hw.FindClearArea(new CPos(50, 50), 2, 2), hw.LocalPlayer);
			hw.Tick(1);

			// being-demolished is one of the conditions that disable the RA building's Sellable.
			dome.GrantCondition("being-demolished");
			hw.Tick(1);
			var events = WatchEvents();

			using var response = AgentStateDumpTest.Request(Line(dome));
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("error").GetString(), Does.Contain("does not accept order 'Sell'"));
			Assert.That(dome.IsDead, Is.False, "A refused sell sold the building.");
			Assert.That(events.Count(e => e.GetType().Name == "SoldV1"), Is.EqualTo(0));
		}

		[TestCase(TestName = "The orders list leaves Sell out for a building whose Sellable is disabled")]
		public void OrdersLeavesOutSellWhileSellableIsDisabled()
		{
			using var hw = HeadlessWorld.Create(1);
			var dome = hw.CreateActor("dome", hw.FindClearArea(new CPos(50, 50), 2, 2), hw.LocalPlayer);
			hw.Tick(1);

			dome.GrantCondition("being-demolished");
			hw.Tick(1);
			using var response = AgentStateDumpTest.Request($"{{\"cmd\":\"orders\",\"actor\":{dome.ActorID}}}");
			Assert.That(response.RootElement.GetProperty("orders").EnumerateArray().Select(e => e.GetString()), Does.Not.Contain("Sell"));
		}

		[TestCase(TestName = "A Sell on a building whose Sellable is enabled is accepted, lists Sell, and sells it")]
		public void SellOfAnEnabledSellableStillSells()
		{
			using var hw = HeadlessWorld.Create(1);
			var wall = hw.CreateActor("BRIK", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			using (var orders = AgentStateDumpTest.Request($"{{\"cmd\":\"orders\",\"actor\":{wall.ActorID}}}"))
				Assert.That(orders.RootElement.GetProperty("orders").EnumerateArray().Select(e => e.GetString()), Does.Contain("Sell"));

			using var response = AgentStateDumpTest.RequestWithTicks(hw, Line(wall));
			Assert.That(response.RootElement.TryGetProperty("error", out _), Is.False, response.RootElement.ToString());
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.True);
			Assert.That(response.RootElement.GetProperty("outcome").GetString(), Is.EqualTo("sold"));
			Assert.That(wall.Disposed, Is.True, "The sale did not dispose the wall.");
		}

		[TestCase(TestName = "A Sell on an actor that cannot be sold is refused at the order, and nothing is sold")]
		public void SellOfANonSellableActorIsRefused()
		{
			using var hw = HeadlessWorld.Create(1);
			var rifle = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var events = WatchEvents();

			using var response = AgentStateDumpTest.Request(Line(rifle));
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("error").GetString(), Does.Contain("does not accept order 'Sell'"));
			Assert.That(rifle.IsDead, Is.False, "A refused sell sold the unit.");
			Assert.That(events.Count(e => e.GetType().Name == "SoldV1"), Is.EqualTo(0));
		}
	}
}
