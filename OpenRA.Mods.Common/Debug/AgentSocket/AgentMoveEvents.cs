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
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// Builds the path and move_result payloads (M10, <c>3k-move-hooks</c>). Move calls these behind
	/// AgentEvents.HasSubscribers. They are pure observers: they read the path and the locomotor's terrain cost, and
	/// they touch no RNG and no world state.
	/// </summary>
	public static class AgentMoveEvents
	{
		/// <summary>
		/// Raises a path event for one path evaluation. pathTargetToSource is the list EvalPath returned, with the
		/// destination first and the source excluded. An empty list with atDestination set is the zero-length case. An
		/// empty list without it is no_path. The cells on the wire run source to target, starting at the source cell.
		/// </summary>
		public static void Path(Actor self, Locomotor locomotor, CPos src, CPos requested, BlockedByActor check, bool repath,
			bool atDestination, IReadOnlyList<CPos> pathTargetToSource)
		{
			var from = new[] { src.X, src.Y };
			var to = pathTargetToSource.Count > 0 ? pathTargetToSource[0] : requested;

			string result;
			int[][] cells = [];
			PathMetrics metrics = null;

			if (atDestination)
			{
				result = "found";
				cells = [from];
				metrics = AgentPathMetrics.Compute(Cell(src), Cell(src), [], _ => 0);
			}
			else if (pathTargetToSource.Count == 0)
				result = "no_path";
			else
			{
				result = "found";
				cells = new int[pathTargetToSource.Count + 1][];
				cells[0] = from;
				for (var i = 0; i < pathTargetToSource.Count; i++)
				{
					var c = pathTargetToSource[pathTargetToSource.Count - 1 - i];
					cells[i + 1] = [c.X, c.Y];
				}

				try
				{
					metrics = AgentPathMetrics.Compute(Cell(src), Cell(to), pathTargetToSource.Select(Cell).ToList(),
						c => locomotor.MovementCostForCell(new CPos(c.X, c.Y)));
				}
				catch (ArgumentException)
				{
					// getPath callers build their own paths. A path that is not a chain of adjacent cells that ends at its
					// destination is reported with metrics null, and the game thread never throws from the notify.
					metrics = null;
				}
			}

			AgentEvents.Raise("path", self.Owner.PlayerName, new PathV1("path", self.World.WorldTick, self.ActorID,
				self.Info.Name, self.Owner.PlayerName, from, [to.X, to.Y], check.ToString(), repath, result, cells, metrics));
		}

		/// <summary>Raises a move_result event when a Move activity completes or is canceled.</summary>
		public static void Completed(Actor self, CPos from, CPos to, MoveResult result, int startTicks, int remainingCells)
		{
			var outcome = result switch
			{
				MoveResult.CompleteCanceled => "canceled",
				MoveResult.CompleteDestinationBlocked => "blocked",
				_ => "reached",
			};

			AgentEvents.Raise("move_result", self.Owner.PlayerName, new MoveResultV1("move_result", self.World.WorldTick,
				self.ActorID, self.Info.Name, self.Owner.PlayerName, [from.X, from.Y], [to.X, to.Y], outcome,
				self.World.WorldTick - startTicks, remainingCells));
		}

		static PathCell Cell(CPos c)
		{
			return new PathCell(c.X, c.Y);
		}
	}
}
