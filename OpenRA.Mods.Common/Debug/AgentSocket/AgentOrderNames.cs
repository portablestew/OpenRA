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

using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// The order names an actor accepts. The orders command lists them, and order refuses any name outside them. Names come
	/// from the traits that handle them: IIssueOrder targeters and IIssueDeployOrder first, then the names that have no
	/// targeter, enabled by the same trait-presence rules CommandBarLogic uses. RejectsOrders removes names last.
	/// </summary>
	public static class AgentOrderNames
	{
		/// <summary>The order the player actor takes to repair a building. It is a toggle: on is the desired repair state.</summary>
		public const string RepairOrder = "RepairBuilding";

		/// <summary>The accepted names for an actor, in a stable order with no duplicates.</summary>
		public static string[] For(Actor actor)
		{
			var names = new List<string>();

			foreach (var trait in actor.TraitsImplementing<IIssueOrder>())
				names.AddRange(trait.Orders.Select(o => o.OrderID));

			foreach (var deploy in actor.TraitsImplementing<IIssueDeployOrder>())
				if (deploy.CanIssueDeployOrder(actor, false))
					names.Add(deploy.IssueDeployOrder(actor, false).OrderString);

			// Mirrors CommandBarLogic. Stop is refused only for an actor whose command bar blacklist disables it.
			var blacklist = actor.Info.TraitInfoOrDefault<CommandBarBlacklistInfo>();
			if (blacklist == null || !blacklist.DisableStop)
				names.Add("Stop");

			if (actor.Info.HasTraitInfo<IMoveInfo>())
				names.Add("Scatter");

			var autoTarget = actor.Info.TraitInfoOrDefault<AutoTargetInfo>();
			if (autoTarget != null)
			{
				if (autoTarget.EnableStances)
					names.Add("SetUnitStance");

				if (actor.Info.HasTraitInfo<AttackMoveInfo>())
					names.AddRange(["AttackMove", "AssaultMove"]);

				if (actor.Info.HasTraitInfo<GuardInfo>())
					names.Add("Guard");
			}

			if (actor.Info.HasTraitInfo<ProductionQueueInfo>())
				names.AddRange(["StartProduction", "PauseProduction", "CancelProduction"]);

			if (actor.Info.HasTraitInfo<PlaceBuildingInfo>())
				names.AddRange(["PlaceBuilding", "LineBuild", "PlacePlug"]);

			// Sellable and ToggleConditionOnOrder resolve their names without a targeter, so they are listed here.
			if (actor.Info.HasTraitInfo<SellableInfo>())
				names.Add("Sell");

			foreach (var toggle in actor.Info.TraitInfos<ToggleConditionOnOrderInfo>())
				names.Add(toggle.OrderName);

			// The player actor issues RepairBuilding, naming the building in target_actor. AllyRepair resolves it.
			if (actor.Info.HasTraitInfo<AllyRepairInfo>())
				names.Add(RepairOrder);

			// The player actor holds the support powers, and each is named by its key. A disabled power is not offered. A charging
			// one is, and Accept refuses it until it is ready.
			var support = actor.TraitOrDefault<SupportPowerManager>();
			if (support != null)
				names.AddRange(support.Powers.Where(p => !p.Value.Disabled).Select(p => p.Key));

			return names.Distinct().Where(actor.AcceptsOrder).ToArray();
		}

		/// <summary>True when the order name is the OrderName of a ToggleConditionOnOrder on the actor, or RepairBuilding on an actor with AllyRepair. Such an order takes the on key.</summary>
		public static bool IsToggle(Actor actor, string order)
		{
			if (order == RepairOrder)
				return actor.Info.HasTraitInfo<AllyRepairInfo>();

			return actor.Info.TraitInfos<ToggleConditionOnOrderInfo>().Any(t => t.OrderName == order);
		}
	}
}
