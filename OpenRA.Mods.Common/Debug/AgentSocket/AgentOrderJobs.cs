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
using System.Threading;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>The reply to order with await:false. Job is the id that wait takes. Tick and MinEffectTick are the order's ack.</summary>
	public sealed record JobV1(bool Ok, string Cmd, int Job, int Tick, int MinEffectTick);

	/// <summary>The reply to wait when the job has not settled by its timeout. The job stays open, so wait can be called again.</summary>
	public sealed record WaitPendingV1(bool Ok, string Cmd, int Job, string Status);

	/// <summary>
	/// What an issued request hands to the job table (P1.4, P1.13): the ack the job reply carries, the await wait parks on, and
	/// the reply builder that wait returns once the await settles. The builder takes the command name, so a wait replies as wait.
	/// </summary>
	public sealed record JobIssue(OrderV1 Ack, AgentOrderAwait Await, Func<string, object> Reply);

	/// <summary>
	/// The open order jobs (P1.4). order with await:false issues the order at once and keeps its await here under an id, so the
	/// socket thread can collect it later with wait. A job ends when wait returns its result or its error, or when its world is
	/// replaced: a job of a disposed world has already failed with restored, so Issue drops it. At most Cap jobs are open, so a
	/// client that never waits cannot grow the table without limit. Start runs on the game thread. Wait runs on the socket thread.
	/// </summary>
	public static class AgentOrderJobs
	{
		/// <summary>The most jobs that may be open at once. A further await:false order is refused until a wait ends one.</summary>
		public const int Cap = 16;

		sealed record Job(AgentOrderAwait Await, Func<string, object> Reply);

		static readonly Lock Gate = new();
		static readonly Dictionary<int, Job> Open = [];
		static int nextId = 1;

		/// <summary>
		/// Issues a Move and records it as a job. Must run on the game thread. The cap is checked before the order is issued,
		/// so a refused request changes nothing.
		/// </summary>
		public static JobV1 Issue(World world, OrderRequest request, AgentSelection selection)
		{
			return Start(world, "order", () =>
			{
				var issued = AgentOrder.Issue(world, request, selection);
				return new JobIssue(issued.Ack, issued.Await, cmd => AgentOrder.Reply(cmd, issued.Ack, issued.Await));
			});
		}

		/// <summary>
		/// Records whatever issue makes as a job, and replies with its id. Must run on the game thread. The cap is checked before
		/// issue runs, so a refused request changes nothing. Any exception issue throws passes to the caller, and no job is recorded.
		/// </summary>
		public static JobV1 Start(World world, string cmd, Func<JobIssue> issue)
		{
			lock (Gate)
			{
				foreach (var id in Open.Where(j => j.Value.Await.World.Disposing).Select(j => j.Key).ToArray())
					Open.Remove(id);

				if (Open.Count >= Cap)
					throw new InvalidOperationException($"{Cap} order jobs are open; wait on one to free a slot");
			}

			var job = issue();
			lock (Gate)
			{
				var id = nextId++;
				Open.Add(id, new Job(job.Await, job.Reply));
				return new JobV1(true, cmd, id, job.Ack.Tick, job.Ack.MinEffectTick);
			}
		}

		/// <summary>
		/// Waits on the socket thread for the job, up to timeoutMs of wall-clock time. Returns the job's reply once it settles, or a
		/// pending reply if the timeout passes first, and the job stays open. Throws for an unknown job, and for a failed await,
		/// which ends the job. Must not run on the game thread.
		/// </summary>
		public static object Wait(int id, int timeoutMs)
		{
			Job job;
			lock (Gate)
			{
				if (!Open.TryGetValue(id, out job))
					throw new InvalidOperationException($"unknown job {id}: jobs come from order with await:false, and a job ends once wait returns it"
						+ " or load_map or undo replaces its world");
			}

			if (!job.Await.TryWait(timeoutMs))
				return new WaitPendingV1(true, "wait", id, "pending");

			lock (Gate)
				Open.Remove(id);

			job.Await.ThrowIfFailed();
			return job.Reply("wait");
		}

		/// <summary>Drops every job. Test use.</summary>
		public static void Clear()
		{
			lock (Gate)
				Open.Clear();
		}
	}
}
