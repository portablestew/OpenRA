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
using OpenRA.Network;
using OpenRA.Support;

namespace OpenRA.Test
{
	/// <summary>
	/// The load_skirmish poll and its cleanup (9.1). The load runs through the real pending bookkeeping and the real
	/// map resolution, but with a connect step the test supplies, so no client or server is needed. The test thread
	/// plays the game loop, and it stops pumping for one step so that step times out, as it would on a stalled game
	/// thread.
	/// </summary>
	[TestFixture]
	sealed class AgentLoadSkirmishTest
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

		static readonly Lazy<MethodInfo> LoadWithConnect = new(() => HeadlessMod.ModData.ObjectCreator
			.FindType("AgentSocket.AgentLoadSkirmish")
			.GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(Func<OrderManager>)]));

		static readonly Lazy<MethodInfo> LoadWithDeadline = new(() => HeadlessMod.ModData.ObjectCreator
			.FindType("AgentSocket.AgentLoadSkirmish")
			.GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(Func<OrderManager>), typeof(int)]));

		static readonly Lazy<FieldInfo> PendingLoad = new(() => HeadlessMod.ModData.ObjectCreator
			.FindType("AgentSocket.AgentLoadSkirmish")
			.GetField("pending", BindingFlags.NonPublic | BindingFlags.Static));

		static readonly Lazy<PropertyInfo> LobbySubscriptions = new(() => HeadlessMod.ModData.ObjectCreator
			.FindType("AgentSocket.AgentSkirmishLobby")
			.GetNestedType("Driver")
			.GetProperty("Active", BindingFlags.Public | BindingFlags.Static));

		static readonly Lazy<MethodInfo> PickRandomMap = new(() => HeadlessMod.ModData.ObjectCreator
			.FindType("AgentSocket.AgentSkirmish")
			.GetMethod("PickRandomMap", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, [typeof(int), typeof(MersenneTwister)]));

		/// <summary>A lobby-available map that the request resolves to, so the tests exercise the real resolution path.</summary>
		static string SuitableMapUid()
		{
			var map = (MapPreview)PickRandomMap.Value.Invoke(null, [1, new MersenneTwister(0x9E11)]);
			return map.Uid;
		}

		static int LobbySubscriptionCount() => (int)LobbySubscriptions.Value.GetValue(null);

		/// <summary>A connect step that starts no server, so the load polls and never finds a new world.</summary>
		static readonly Func<OrderManager> NoServer = () => null;

		/// <summary>Runs one load on a worker thread without pumping, and returns the exception it ends with, or null.</summary>
		static Task<Exception> LoadWithoutGame(string uid)
		{
			return Task.Run(() =>
			{
				try
				{
					LoadWithConnect.Value.Invoke(null, [uid, NoServer]);
					return null;
				}
				catch (TargetInvocationException e)
				{
					return e.InnerException;
				}
			});
		}

		/// <summary>Runs one load on a worker thread with the poll deadline given, and returns the exception it ends with, or null.</summary>
		static Task<Exception> LoadPastDeadline(string uid, int timeoutMs, Func<OrderManager> connect)
		{
			return Task.Run(() =>
			{
				try
				{
					LoadWithDeadline.Value.Invoke(null, [uid, connect, timeoutMs]);
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

		static void PumpUntil(Task task, TimeSpan span)
		{
			var deadline = DateTime.UtcNow + span;
			while (!task.IsCompleted && DateTime.UtcNow < deadline)
			{
				Game.PerformDelayedActions();
				Thread.Sleep(1);
			}
		}

		[TestCase(TestName = "A load_skirmish that times out on the game thread does not refuse the next load_skirmish as already in progress")]
		public void LoadAfterTimeoutIsNotRefused()
		{
			var uid = SuitableMapUid();

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

		[TestCase(TestName = "A load_skirmish that never gets its world fails at the poll deadline, and clears pending so the next load is not refused")]
		public void LoadPastPollDeadlineClearsPending()
		{
			var uid = SuitableMapUid();

			// The other case in this fixture leaves a load in flight, so the shared pending load must be cleared
			// before this case runs. Clearing it is exactly the contract under test: the deadline path does it.
			Game.RunAfterTick(() => PendingLoad.Value.SetValue(null, null));
			PumpFor(TimeSpan.FromMilliseconds(100));

			// A 2 s deadline, so the real poll loop reaches its own timeout in seconds. The test thread keeps
			// pumping, so the game thread answers every step, but no new world ever appears and Check returns
			// null until the deadline. That is the branch the 5 s marshal-step timeout never reaches.
			var first = LoadPastDeadline(uid, 2000, NoServer);
			PumpUntil(first, TimeSpan.FromSeconds(30));

			Assert.That(first.IsCompleted, Is.True, "The first load did not return.");
			Assert.That(first.Result?.Message ?? string.Empty, Does.Contain("did not start within 2 s"),
				"The load must fail at its own poll deadline, not the game-thread step timeout.");

			// The catch queues the clear of pending behind the last poll step; run it before the next load.
			PumpFor(TimeSpan.FromMilliseconds(200));

			// The next load must be accepted (pending cleared) and run its own real loop to its own deadline.
			var second = LoadPastDeadline(uid, 1000, NoServer);
			PumpUntil(second, TimeSpan.FromSeconds(15));

			Assert.That(second.IsCompleted, Is.True, "The second load did not return.");
			var secondError = second.Result?.Message ?? string.Empty;
			Assert.That(secondError, Does.Not.Contain("already in progress"),
				"The timed-out load must clear pending so the next load_skirmish is not refused.");
			Assert.That(secondError, Does.Contain("did not start within 1 s"),
				"The second load must reach its own poll deadline, proving it ran the real loop.");

			// Run the second load's own queued clear, so this case leaves shared state clean for the other case.
			PumpFor(TimeSpan.FromMilliseconds(200));
		}

		[TestCase(TestName = "A load_skirmish that fails disposes its lobby subscription, so no handler outlives the load")]
		public void FailedLoadLeavesNoLobbySubscription()
		{
			var uid = SuitableMapUid();

			Game.RunAfterTick(() => PendingLoad.Value.SetValue(null, null));
			PumpFor(TimeSpan.FromMilliseconds(100));
			Assert.That(LobbySubscriptionCount(), Is.EqualTo(0), "A previous case left a lobby subscription behind.");

			// A real order manager, so the load subscribes to the lobby and then has something to clean up.
			using var hw = HeadlessWorld.Create(1);
			var load = LoadPastDeadline(uid, 1000, () => hw.OrderManager);

			// Let the load claim the pending load and subscribe.
			PumpFor(TimeSpan.FromMilliseconds(200));
			Assert.That(LobbySubscriptionCount(), Is.EqualTo(1),
				"The load must subscribe to the lobby before its cleanup can be observed.");

			PumpUntil(load, TimeSpan.FromSeconds(15));
			Assert.That(load.IsCompleted, Is.True, "The load did not return.");
			Assert.That(load.Result?.Message ?? string.Empty, Does.Contain("did not start within 1 s"));

			// The clear is queued behind the last poll step.
			PumpFor(TimeSpan.FromMilliseconds(200));
			Assert.That(LobbySubscriptionCount(), Is.EqualTo(0),
				"The failed load left its Game.LobbyInfoChanged handler subscribed.");
		}
	}
}
