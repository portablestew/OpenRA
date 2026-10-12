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
using System.Threading.Tasks;

namespace OpenRA.Mods.Common.AgentSocket
{
	public static class AgentMarshal
	{
		public const int DefaultTimeoutMs = 5000;

		/// <summary>
		/// Runs fn on the game thread (via Game.RunAfterTick) and blocks the calling thread until it has run.
		/// The simulation is single-threaded, so socket threads must never touch the world directly.
		/// </summary>
		public static T ExecuteOnGameThread<T>(Func<T> fn, int timeoutMs = DefaultTimeoutMs)
		{
			var result = new TaskCompletionSource<T>();
			Game.RunAfterTick(() =>
			{
				try
				{
					result.SetResult(fn());
				}
				catch (Exception e)
				{
					result.SetException(e);
				}
			});

			// WaitAny reports completion without rethrowing a handler failure; GetResult below rethrows it unwrapped.
			if (Task.WaitAny([result.Task], timeoutMs) == -1)
				throw new TimeoutException($"the game thread did not run the request within {timeoutMs} ms");

			return result.Task.GetAwaiter().GetResult();
		}
	}
}
