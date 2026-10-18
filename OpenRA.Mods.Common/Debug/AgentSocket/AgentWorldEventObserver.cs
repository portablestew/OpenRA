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

using OpenRA.Mods.Common.AgentSocket;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common
{
	public class AgentWorldEventObserverInfo : TraitInfo<AgentWorldEventObserver> { }

	/// <summary>
	/// Raises produced events (D2, P4). Registered in the World: block only. The order and stance events come from
	/// static notifies: AgentWorldBridge installs Ordered on UnitOrders.OrderResolving (D3), and AutoTarget.SetStance
	/// calls Stanced directly. Both check AgentEvents.Enabled before they build a DTO.
	/// </summary>
	public sealed class AgentWorldEventObserver : INotifyOtherProduction
	{
		void INotifyOtherProduction.UnitProducedByOther(Actor self, Actor producer, Actor produced, string productionType, TypeDictionary init)
		{
			Produced(producer, produced);
		}

		/// <summary>Raises a produced event for a unit that a producer has just built. Called from the production notify.</summary>
		public static void Produced(Actor producer, Actor produced)
		{
			if (!AgentEvents.Enabled)
				return;

			AgentEvents.Raise("production", produced.Owner.PlayerName, new ProducedV1("produced", produced.World.WorldTick,
				produced.ActorID, produced.Info.Name, produced.Owner.PlayerName, producer.ActorID, producer.Info.Name));
		}

		/// <summary>
		/// Raises an order event for a resolved order. valid is the result of the validators. The notify runs before the
		/// world advances, so the current tick is the tick the order resolved on. The D11 ack's min_effect_tick (issue tick
		/// + 1) is only a lower bound, so this event's effect_tick is the actual tick, which can be later than the ack.
		/// </summary>
		public static void Ordered(Order order, int clientId, bool valid)
		{
			// Resolves the socket's pending await. It runs without a subscriber, so an await never waits on an event.
			AgentOrderAwait.Resolve(order, valid);

			if (!AgentEvents.Enabled)
				return;

			var world = order.Subject.World;
			var cell = order.Target.Type == TargetType.Terrain ? world.Map.CellContaining(order.Target.CenterPosition) : (CPos?)null;
			var targetActor = order.Target.Type == TargetType.Actor ? order.Target.Actor?.ActorID : null;

			AgentEvents.Raise("orders", order.Subject.Owner.PlayerName, new OrderEventV1("order", world.WorldTick, world.WorldTick,
				valid, clientId, order.OrderString, order.Subject.ActorID, order.Subject.Info.Name, order.Subject.Owner.PlayerName,
				cell is { } c ? [c.X, c.Y] : null, targetActor, order.Queued));
		}

		/// <summary>Raises a stance event when an actor's stance changes. Called from AutoTarget.SetStance behind the same guard.</summary>
		public static void Stanced(Actor self, UnitStance from, UnitStance to)
		{
			AgentEvents.Raise("stance", self.Owner.PlayerName, new StanceV1("stance", self.World.WorldTick, self.ActorID,
				self.Info.Name, self.Owner.PlayerName, from.ToString(), to.ToString()));
		}
	}
}
