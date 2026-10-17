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
	/// The GPS seam (P2.2, Q1). Common cannot reference Cnc, so the satellite's grant reaches the socket through this interface,
	/// which Cnc's GpsWatcher implements. It is the only Cnc dependency the fog policy has.
	/// </summary>
	public interface IAgentGpsGrant
	{
		/// <summary>True while the observer's satellite grants radar dots: launched and with an actor, or an allied watcher granted.</summary>
		bool GrantsRadarDots { get; }
	}

	/// <summary>
	/// Marks an actor that carries the GPS dot trait (Cnc's GpsDot). The dot is the radar indicator the satellite draws, and the
	/// fog policy reads the same marker, so the two cannot disagree.
	/// </summary>
	public interface IAgentGpsDot { }
}
