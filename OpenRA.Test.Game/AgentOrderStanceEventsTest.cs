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
using OpenRA.Traits;

namespace OpenRA.Test
{
	/// <summary>
	/// The world observer's order, stance, and produced payloads (M8, <c>3i-world-observer-stance</c>). The order is issued
	/// through World.IssueOrder, and the stance through AutoTarget.SetStance. Both go through the real engine notifies, the
	/// hub, and a connection's queue. The mod's types are reached by reflection, as in AgentPlayerObserverTest.
	/// </summary>
	[TestFixture]
	sealed class AgentOrderStanceEventsTest
	{
		bool savedAgentSocket;
		bool savedEnabled;
		object subscriber;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[SetUp]
		public void CreateSubscriber()
		{
			savedAgentSocket = Game.Settings.Debug.AgentSocket;
			Game.Settings.Debug.AgentSocket = true;

			savedEnabled = (bool)ModType("AgentSocket.AgentEvents").GetProperty("Enabled").GetValue(null);
			subscriber = Activator.CreateInstance(ModType("AgentSocket.AgentEventSubscriber"));
		}

		[TearDown]
		public void RestoreHub()
		{
			Call(subscriber, "Close");
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

		static string KindOf(object payload)
		{
			return payload.GetType().Name;
		}

		/// <summary>Takes every queued event. Fails rather than blocking when nothing was delivered.</summary>
		List<object> Drain()
		{
			var pending = (int)subscriber.GetType().GetProperty("Pending").GetValue(subscriber);
			Assert.That(pending, Is.GreaterThan(0), "No events reached the subscriber.");
			return ((IList)Call(subscriber, "Take")).Cast<object>().ToList();
		}

		/// <summary>The mod's copy of a trait on the actor, found by its type name.</summary>
		static object Trait(Actor actor, string typeName)
		{
			var type = ModType(typeName);
			Assert.That(type, Is.Not.Null, $"{typeName} is not in the mod.");

			var method = typeof(Actor).GetMethods().Single(m => m.Name == "TraitOrDefault" && m.IsGenericMethodDefinition);
			var trait = method.MakeGenericMethod(type).Invoke(actor, null);
			Assert.That(trait, Is.Not.Null, $"{actor.Info.Name} has no {typeName}.");
			return trait;
		}

		[TestCase(TestName = "A Move order yields one valid order event at effect_tick, with its payload")]
		public void MoveRaisesOneOrderEvent()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			Call(subscriber, "Subscribe", new[] { "orders" }, null);

			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);
			var issuedAt = hw.World.WorldTick;
			hw.World.IssueOrder(new Order("Move", unit, Target.FromCell(hw.World, destination), false));

			Assert.That(hw.TickUntil(() => unit.Location == destination, 500), Is.Not.Null, "The unit never reached the destination.");

			var orders = Drain().Where(e => KindOf(e) == "OrderEventV1").ToList();
			Assert.That(orders, Has.Count.EqualTo(1), "Expected exactly one order event.");

			var order = orders[0];
			Assert.That(Field<string>(order, "Event"), Is.EqualTo("order"));
			Assert.That(Field<bool>(order, "Valid"), Is.True);
			Assert.That(Field<string>(order, "Order"), Is.EqualTo("Move"));
			Assert.That(Field<uint>(order, "Actor"), Is.EqualTo(unit.ActorID));
			Assert.That(Field<string>(order, "Type"), Is.EqualTo("e1"));
			Assert.That(Field<string>(order, "Owner"), Is.EqualTo(unit.Owner.PlayerName));
			Assert.That(Field<int[]>(order, "Cell"), Is.EqualTo(new[] { destination.X, destination.Y }));
			Assert.That(Field<uint?>(order, "TargetActor"), Is.Null);
			Assert.That(Field<bool>(order, "Queued"), Is.False);

			// The ack reports effect_tick as issue tick + 1 (D11). The event lands on that same tick.
			Assert.That(Field<int>(order, "EffectTick"), Is.EqualTo(issuedAt + 1));
			Assert.That(Field<int>(order, "Tick"), Is.EqualTo(Field<int>(order, "EffectTick")));
		}

		[TestCase(TestName = "SetStance yields one stance event with from and to")]
		public void SetStanceRaisesOneStanceEvent()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			var autoTarget = Trait(unit, "Traits.AutoTarget");
			var stanceType = ModType("Traits.UnitStance");
			var from = Field<object>(autoTarget, "Stance");
			var to = Enum.GetValues(stanceType).Cast<object>().First(v => !v.Equals(from));

			Call(subscriber, "Subscribe", new[] { "stance" }, null);

			autoTarget.GetType().GetMethod("SetStance").Invoke(autoTarget, [unit, to]);

			var stances = Drain();
			Assert.That(stances, Has.Count.EqualTo(1), "Expected exactly one stance event.");

			var stance = stances[0];
			Assert.That(KindOf(stance), Is.EqualTo("StanceV1"));
			Assert.That(Field<string>(stance, "Event"), Is.EqualTo("stance"));
			Assert.That(Field<uint>(stance, "Actor"), Is.EqualTo(unit.ActorID));
			Assert.That(Field<string>(stance, "Type"), Is.EqualTo("e1"));
			Assert.That(Field<string>(stance, "Owner"), Is.EqualTo(unit.Owner.PlayerName));
			Assert.That(Field<string>(stance, "From"), Is.EqualTo(from.ToString()));
			Assert.That(Field<string>(stance, "To"), Is.EqualTo(to.ToString()));
		}

		[TestCase(TestName = "An owner filter drops a stance event for an actor owned by someone else")]
		public void OwnerFilterDropsOtherOwnersStance()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			var autoTarget = Trait(unit, "Traits.AutoTarget");
			var stanceType = ModType("Traits.UnitStance");
			var from = Field<object>(autoTarget, "Stance");
			var to = Enum.GetValues(stanceType).Cast<object>().First(v => !v.Equals(from));

			Assert.That(hw.EnemyPlayer, Is.Not.Null, "The selected map has only one playable slot.");
			Call(subscriber, "Subscribe", new[] { "stance" }, hw.EnemyPlayer.PlayerName);

			autoTarget.GetType().GetMethod("SetStance").Invoke(autoTarget, [unit, to]);

			Assert.That(subscriber.GetType().GetProperty("Pending").GetValue(subscriber), Is.EqualTo(0));
		}

		[TestCase(TestName = "A produced unit yields a produced event naming the unit and its producer")]
		public void ProducedPayloadNamesUnitAndProducer()
		{
			using var hw = HeadlessWorld.Create(1);
			var producer = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			var produced = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 52), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			Call(subscriber, "Subscribe", new[] { "production" }, null);
			ModType("AgentWorldEventObserver").GetMethod("Produced").Invoke(null, [producer, produced]);

			var batch = Drain();
			Assert.That(batch, Has.Count.EqualTo(1));

			var built = batch[0];
			Assert.That(KindOf(built), Is.EqualTo("ProducedV1"));
			Assert.That(Field<string>(built, "Event"), Is.EqualTo("produced"));
			Assert.That(Field<uint>(built, "Actor"), Is.EqualTo(produced.ActorID));
			Assert.That(Field<string>(built, "Type"), Is.EqualTo("e1"));
			Assert.That(Field<string>(built, "Owner"), Is.EqualTo(hw.LocalPlayer.PlayerName));
			Assert.That(Field<uint>(built, "Producer"), Is.EqualTo(producer.ActorID));
			Assert.That(Field<string>(built, "ProducerType"), Is.EqualTo("e1"));
		}
	}
}
