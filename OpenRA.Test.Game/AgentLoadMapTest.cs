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
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The load_map poll after a game-thread timeout. The load runs through the real pending bookkeeping, but with a start
	/// step that starts no game, so no client or server is needed. The test thread plays the game loop, and it stops
	/// pumping for one step so that step times out, as it would on a stalled game thread.
	/// </summary>
	[TestFixture]
	sealed class AgentLoadMapTest
	{
		bool savedAgentSocket;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[SetUp]
		public void EnableAgentSocket()
		{
			savedAgentSocket = Game.Settings.Debug.AgentSocket;
			Game.Settings.Debug.AgentSocket = true;
		}

		[TearDown]
		public void RestoreAgentSocket()
		{
			Game.Settings.Debug.AgentSocket = savedAgentSocket;
		}

		static readonly Lazy<MethodInfo> LoadWithStart = new(() => HeadlessMod.ModData.ObjectCreator
			.FindType("AgentSocket.AgentLoadMap")
			.GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(Action<string>)]));

		static readonly Action<string> StartNoGame = _ => { };

		/// <summary>Runs one load on a worker thread without pumping, and returns the exception it ends with, or null.</summary>
		static Task<Exception> LoadWithoutGame(string uid)
		{
			return Task.Run(() =>
			{
				try
				{
					LoadWithStart.Value.Invoke(null, [uid, StartNoGame]);
					return null;
				}
				catch (TargetInvocationException e)
				{
					return e.InnerException;
				}
			});
		}

		static void PumpFor(TimeSpan span)
		{
			var deadline = DateTime.UtcNow + span;
			while (DateTime.UtcNow < deadline)
			{
				Game.PerformDelayedActions();
				Thread.Sleep(1);
			}
		}

		[TestCase(TestName = "A load_map that times out on the game thread does not refuse the next load_map as already in progress")]
		public void LoadAfterTimeoutIsNotRefused()
		{
			string uid;
			using (var maps = AgentStateDumpTest.Request("{\"cmd\":\"maps\"}"))
				uid = maps.RootElement.GetProperty("maps")[0].GetProperty("uid").GetString();

			// Pump while the load starts and its first poll steps run.
			var first = LoadWithoutGame(uid);
			PumpFor(TimeSpan.FromMilliseconds(500));

			// Stop pumping: the next poll step is queued but never runs, so its caller times out after 5 s.
			Assert.That(first.Wait(TimeSpan.FromSeconds(15)), Is.True, "The first load did not return.");
			Assert.That(first.Result?.Message ?? string.Empty, Does.Contain("did not run the request within"),
				"The first load must fail by a game-thread timeout for this test to mean anything.");

			// Let the queued steps run. The stale poll step runs first, then the clear that the timeout queued behind it.
			PumpFor(TimeSpan.FromMilliseconds(200));

			var second = LoadWithoutGame(uid);
			var deadline = DateTime.UtcNow.AddSeconds(2);
			while (!second.IsCompleted && DateTime.UtcNow < deadline)
			{
				Game.PerformDelayedActions();
				Thread.Sleep(1);
			}

			// An accepted load keeps polling for its world, so it does not return within this window.
			var secondError = second.IsCompleted ? second.Result?.Message : null;
			Assert.That(secondError ?? string.Empty, Does.Not.Contain("already in progress"));
		}
	}
}
