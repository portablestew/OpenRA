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
	/// Builds the path payload (M10, <c>3k-move-hooks</c>). Move calls these behind
	/// AgentEvents.Enabled. They are pure observers: they read the path and the locomotor's terrain cost, and
	/// they touch no RNG and no world state.
	/// </summary>
	public static class AgentMoveEvents
	{
		/// <summary>
		/// Raises a path event for one path evaluation. pathTargetToSource is the list EvalPath returned, with the
		/// destination first and the source excluded. An empty list with atDestination set is the zero-length case. An
		/// empty list without it is no_path. The cells go to the order's await, not into the event.
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

			// A socket await for this Move takes the path whether or not a connection is open. An empty list means no path was found.
			AgentOrderAwait.CapturePath(self, cells);

			// The actor's path is sent only when the local player reports the actor (AgentFog), and only while a socket order
			// drives it: an autonomous move (a harvester's shuttle) is not the socket's to report.
			if (!AgentFog.Reported(self, self.World.LocalPlayer))
				return;

			if (!AgentOrderInterest.Tracked(self))
				return;

			// Naive by design: the declared speed, not the terrain- and modifier-adjusted movement speed.
			var speed = self.TraitOrDefault<Mobile>()?.Info.Speed ?? 0;
			AgentEvents.Raise(new PathV1("path", self.World.WorldTick, self.ActorID,
				self.Info.Name, self.Owner.PlayerName, from, [to.X, to.Y], check.ToString(), repath, result,
				AgentPathMetrics.EtaTicks(metrics, speed), metrics));
		}

		/// <summary>
		/// True when Path should run for self: a connection wants the event, or a socket Move awaiting its path wants it (P1.3).
		/// Move.cs gates its Path calls on this, so the path is captured without a connection.
		/// </summary>
		public static bool Wanted(Actor self)
		{
			return AgentEvents.Enabled || AgentOrderAwait.AwaitingPath(self);
		}

		static PathCell Cell(CPos c)
		{
			return new PathCell(c.X, c.Y);
		}
	}
}
