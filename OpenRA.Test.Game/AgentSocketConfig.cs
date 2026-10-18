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

namespace OpenRA.Test
{
	/// <summary>
	/// Runs a headless scenario under a chosen agent socket configuration, for the determinism comparisons that show the
	/// socket does not change the simulation. The mod's event hub is reached by reflection, as in AgentMoveEventsTest.
	/// </summary>
	static class AgentSocketConfig
	{
		/// <summary>The scenario's result, and how many events the connection queued while it ran (0 when there is no connection).</summary>
		internal sealed record Run<T>(T Result, int Events);

		/// <summary>
		/// Runs scenario with the agent socket setting and hub state chosen, and reports how many events the connection holds.
		/// The setting is read when the world loads, so scenario must create its world after this call sets it.
		/// </summary>
		public static Run<T> Execute<T>(bool agentSocket, bool connect, Func<T> scenario)
		{
			var savedAgentSocket = Game.Settings.Debug.AgentSocket;
			var hub = ModType("AgentSocket.AgentEvents");
			var savedEnabled = (bool)hub.GetProperty("Enabled").GetValue(null);

			object connection = null;
			try
			{
				Game.Settings.Debug.AgentSocket = agentSocket;

				if (connect)
					connection = Activator.CreateInstance(ModType("AgentSocket.AgentConnection"));

				var result = scenario();
				var events = connection == null ? 0 : (int)connection.GetType().GetProperty("Pending").GetValue(connection);
				return new Run<T>(result, events);
			}
			finally
			{
				connection?.GetType().GetMethod("Close").Invoke(connection, null);
				hub.GetProperty("Enabled").SetValue(null, savedEnabled);
				Game.Settings.Debug.AgentSocket = savedAgentSocket;
			}
		}

		static Type ModType(string name)
		{
			return HeadlessMod.ModData.ObjectCreator.FindType(name);
		}
	}
}
