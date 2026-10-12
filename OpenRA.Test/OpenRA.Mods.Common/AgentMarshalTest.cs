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
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenRA.Mods.Common.AgentSocket;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class AgentMarshalTest
	{
		/// <summary>Issues the request from a worker thread, as the socket side does, while this thread plays the game loop.</summary>
		static T RequestWhilePumping<T>(Func<T> fn, int timeoutMs = AgentMarshal.DefaultTimeoutMs)
		{
			var request = Task.Run(() => AgentMarshal.ExecuteOnGameThread(fn, timeoutMs));

			var deadline = DateTime.UtcNow.AddSeconds(10);
			while (!request.IsCompleted && DateTime.UtcNow < deadline)
			{
				Game.PerformDelayedActions();
				Thread.Sleep(1);
			}

			return request.GetAwaiter().GetResult();
		}

		[TestCase(TestName = "The handler runs on the game-loop thread and the caller receives its result")]
		public void HandlerRunsOnPumpingThread()
		{
			var loopThread = Environment.CurrentManagedThreadId;

			var (handlerThread, value) = RequestWhilePumping(() => (Environment.CurrentManagedThreadId, 42));

			Assert.That(handlerThread, Is.EqualTo(loopThread), "The request ran on a thread other than the one pumping the game loop.");
			Assert.That(value, Is.EqualTo(42));
		}

		[TestCase(TestName = "A handler exception reaches the caller unchanged")]
		public void HandlerExceptionReachesCaller()
		{
			Assert.That(() => RequestWhilePumping<int>(() => throw new InvalidOperationException("boom")),
				Throws.InstanceOf<InvalidOperationException>().With.Message.EqualTo("boom"));
		}

		[TestCase(TestName = "A request that is never pumped times out for the caller")]
		public void UnpumpedRequestTimesOut()
		{
			Assert.Throws<TimeoutException>(() => AgentMarshal.ExecuteOnGameThread(() => 1, timeoutMs: 50));

			// The abandoned request stays queued until pumped; run it so it cannot leak into later tests.
			Game.PerformDelayedActions();
		}
	}
}
