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

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// The selection one request leaves behind (P1.15). Each order the request issues selects the actor it was issued to, through
	/// the engine's Selection.Combine, as a control group or a click does. The first ordered actor replaces the selection, and the
	/// rest join it, so an array's orders end as the union of the actors they ordered. A request that issues no order leaves the
	/// selection alone. Create one per request line. Ordered must run on the game thread.
	/// </summary>
	public sealed class AgentSelection
	{
		bool ordered;

		/// <summary>Selects the actor of an order that was issued. A refused order throws before it gets here, so it selects nothing.</summary>
		public void Ordered(World world, Actor actor)
		{
			world.Selection.Combine(world, [actor], ordered, false);
			ordered = true;
		}
	}
}
