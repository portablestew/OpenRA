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
		/// <summary>The scenario's result, and how many events the subscriber queued while it ran (0 when there is no subscriber).</summary>
		internal sealed record Run<T>(T Result, int Events);

		/// <summary>
		/// Runs scenario with the agent socket setting and hub state chosen, and reports how many events the subscriber queued.
		/// The setting is read when the world loads, so scenario must create its world after this call sets it.
		/// </summary>
		public static Run<T> Execute<T>(bool agentSocket, bool subscribe, Func<T> scenario)
		{
			var savedAgentSocket = Game.Settings.Debug.AgentSocket;
			var hub = ModType("AgentSocket.AgentEvents");
			var savedEnabled = (bool)hub.GetProperty("Enabled").GetValue(null);

			object subscriber = null;
			try
			{
				Game.Settings.Debug.AgentSocket = agentSocket;

				if (subscribe)
				{
					subscriber = Activator.CreateInstance(ModType("AgentSocket.AgentEventSubscriber"));
					var groups = (string[])hub.GetField("Groups").GetValue(null);
					subscriber.GetType().GetMethod("Subscribe").Invoke(subscriber, [groups, null]);
				}

				var result = scenario();
				var events = subscriber == null ? 0 : (int)subscriber.GetType().GetProperty("Pending").GetValue(subscriber);
				return new Run<T>(result, events);
			}
			finally
			{
				subscriber?.GetType().GetMethod("Close").Invoke(subscriber, null);
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
