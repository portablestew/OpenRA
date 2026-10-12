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
using System.Collections.Concurrent;
using System.Linq;
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

		[TestCase(TestName = "Two worker threads issuing 200 requests each run every handler on the pumping thread and return each result to its own caller")]
		public void ConcurrentRequestsRunOnPumpingThreadAndReturnToCaller()
		{
			const int Workers = 2;
			const int RequestsPerWorker = 200;

			var loopThread = Environment.CurrentManagedThreadId;
			var callerThreads = new ConcurrentBag<int>();
			var handlerThreads = new ConcurrentBag<int>();

			// Each worker sends its requests one after another, so the requests of both workers overlap on the queue.
			var workers = Enumerable.Range(0, Workers).Select(worker => Task.Run(() =>
			{
				callerThreads.Add(Environment.CurrentManagedThreadId);
				return Enumerable.Range(0, RequestsPerWorker).Select(i =>
				{
					var expected = worker * RequestsPerWorker + i;
					var echoed = AgentMarshal.ExecuteOnGameThread(() =>
					{
						handlerThreads.Add(Environment.CurrentManagedThreadId);
						return expected;
					});
					return (Expected: expected, Echoed: echoed);
				}).ToArray();
			})).ToArray();

			var all = Task.WhenAll(workers);
			var deadline = DateTime.UtcNow.AddSeconds(60);
			while (!all.IsCompleted && DateTime.UtcNow < deadline)
			{
				Game.PerformDelayedActions();
				Thread.Sleep(1);
			}

			Assert.That(all.IsCompleted, Is.True, "The workers did not finish within 60 s, so some requests were lost or never pumped.");
			var results = all.GetAwaiter().GetResult().SelectMany(r => r).ToList();

			Assert.That(results, Has.Count.EqualTo(Workers * RequestsPerWorker), "Some requests did not return to their caller.");
			Assert.That(results.Where(r => r.Echoed != r.Expected).ToList(), Is.Empty,
				"A caller received a result that belonged to a different request.");

			Assert.That(callerThreads.Distinct().ToArray(), Has.Length.EqualTo(Workers),
				"The requests were not issued from the two worker threads.");
			Assert.That(callerThreads, Does.Not.Contain(loopThread),
				"A worker ran on the pumping thread, so the requests did not cross threads.");

			Assert.That(handlerThreads, Has.Count.EqualTo(Workers * RequestsPerWorker),
				"Some handlers did not run exactly once.");
			Assert.That(handlerThreads.Distinct().ToArray(), Is.EqualTo(new[] { loopThread }),
				"A handler ran on a thread other than the one pumping the game loop.");
		}
	}
}
