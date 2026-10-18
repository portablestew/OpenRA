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
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The order jobs (P1.4). order with await:false returns a job id at once, wait collects the Move's reply, and the job
	/// table is capped. The mod's types are reached by reflection, as in AgentOrderAwaitTest.
	/// </summary>
	[TestFixture]
	sealed class AgentOrderJobTest
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
			ClearRegistries();
		}

		[TearDown]
		public void RestoreAgentSocket()
		{
			ClearRegistries();
			Game.Settings.Debug.AgentSocket = savedAgentSocket;
		}

		static Type ModType(string name)
		{
			return HeadlessMod.ModData.ObjectCreator.FindType(name);
		}

		static void ClearRegistries()
		{
			ModType("AgentSocket.AgentOrderJobs").GetMethod("Clear").Invoke(null, null);
			ModType("AgentSocket.AgentOrderAwait").GetMethod("Clear").Invoke(null, null);
		}

		static int PendingCount(World world)
		{
			return (int)ModType("AgentSocket.AgentOrderAwait").GetMethod("PendingCount").Invoke(null, [world]);
		}

		static string MoveLine(Actor unit, CPos destination, string extra = "")
		{
			return $"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}]{extra}}}";
		}

		[TestCase(TestName = "await:false returns a job at once, and wait returns the Move's resolution")]
		public void AwaitFalseReturnsJobAndWaitReturnsResolution()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);
			var start = unit.Location;

			// Request, not RequestWithTicks: the job must return without the Move settling.
			using var issued = AgentStateDumpTest.Request(MoveLine(unit, destination, ",\"await\":false"));
			var ack = issued.RootElement;
			Assert.That(ack.GetProperty("ok").GetBoolean(), Is.True, ack.ToString());
			Assert.That(ack.TryGetProperty("settled", out _), Is.False, "An await:false order waited for the Move to settle.");
			var job = ack.GetProperty("job").GetInt32();
			Assert.That(PendingCount(hw.World), Is.EqualTo(1), "The job's await is not registered.");

			using var reply = AgentStateDumpTest.RequestWithTicks(hw, $"{{\"cmd\":\"wait\",\"job\":{job}}}");
			var root = reply.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
			Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("wait"));
			Assert.That(root.GetProperty("valid").GetBoolean(), Is.True);
			Assert.That(root.GetProperty("settled").GetBoolean(), Is.True);
			Assert.That(root.GetProperty("settled_ticks").GetInt32(), Is.EqualTo(3));
			Assert.That(root.GetProperty("effect_tick").GetInt32(), Is.GreaterThanOrEqualTo(ack.GetProperty("tick").GetInt32() + 1));

			var path = root.GetProperty("path");
			Assert.That(path.GetArrayLength(), Is.GreaterThan(0), "The path is empty.");
			Assert.That(new[] { path[0][0].GetInt32(), path[0][1].GetInt32() }, Is.EqualTo(new[] { start.X, start.Y }), "The path does not start at the actor's cell.");
			Assert.That(PendingCount(hw.World), Is.EqualTo(0), "The waited job left its await pending.");
		}

		[TestCase(TestName = "wait on an unknown job is an error that says where jobs come from")]
		public void UnknownJobIsErrorWithHint()
		{
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"wait\",\"job\":987654}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(root.GetProperty("error").GetString(), Does.StartWith("unknown job 987654").And.Contains("await:false"));
		}

		[TestCase(TestName = "wait with timeout_ms on a job that has not settled returns pending, and the job stays open")]
		public void WaitTimeoutReturnsPendingAndKeepsJob()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);

			// A paused game never ticks, so the job cannot settle until it is resumed.
			hw.World.SetLocalPauseState(true);
			using var issued = AgentStateDumpTest.Request(MoveLine(unit, destination, ",\"await\":false"));
			var job = issued.RootElement.GetProperty("job").GetInt32();

			using var pending = AgentStateDumpTest.Request($"{{\"cmd\":\"wait\",\"job\":{job},\"timeout_ms\":50}}");
			var root = pending.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
			Assert.That(root.GetProperty("status").GetString(), Is.EqualTo("pending"));
			Assert.That(PendingCount(hw.World), Is.EqualTo(1), "The timed-out wait closed the job's await.");

			hw.World.SetLocalPauseState(false);
			using var settled = AgentStateDumpTest.RequestWithTicks(hw, $"{{\"cmd\":\"wait\",\"job\":{job}}}");
			Assert.That(settled.RootElement.GetProperty("settled").GetBoolean(), Is.True, settled.RootElement.ToString());
		}

		[TestCase(TestName = "Order jobs are capped: an await:false order is refused, and issues nothing, until a wait ends a job")]
		public void JobCapRefusesOrderUntilAJobEnds()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);
			var cap = (int)ModType("AgentSocket.AgentOrderJobs").GetField("Cap").GetValue(null);

			// Paused, so no job can settle and only the cap limits them.
			hw.World.SetLocalPauseState(true);
			var first = -1;
			for (var i = 0; i < cap; i++)
			{
				using var issued = AgentStateDumpTest.Request(MoveLine(unit, destination, ",\"await\":false"));
				Assert.That(issued.RootElement.GetProperty("ok").GetBoolean(), Is.True, $"Job {i + 1} of {cap} was refused: {issued.RootElement}");
				if (first < 0)
					first = issued.RootElement.GetProperty("job").GetInt32();
			}

			using var refused = AgentStateDumpTest.Request(MoveLine(unit, destination, ",\"await\":false"));
			var refusal = refused.RootElement;
			Assert.That(refusal.GetProperty("ok").GetBoolean(), Is.False, "A job beyond the cap was accepted.");
			Assert.That(refusal.GetProperty("error").GetString(), Does.Contain("wait on one to free a slot"));
			Assert.That(PendingCount(hw.World), Is.EqualTo(cap), "The refused order was issued anyway.");

			// Waiting a job to its end frees its slot, so the next order is accepted.
			hw.World.SetLocalPauseState(false);
			using var waited = AgentStateDumpTest.RequestWithTicks(hw, $"{{\"cmd\":\"wait\",\"job\":{first}}}");
			Assert.That(waited.RootElement.GetProperty("settled").GetBoolean(), Is.True, waited.RootElement.ToString());

			using var accepted = AgentStateDumpTest.Request(MoveLine(unit, destination, ",\"await\":false"));
			Assert.That(accepted.RootElement.GetProperty("ok").GetBoolean(), Is.True, accepted.RootElement.ToString());
		}

		[TestCase(TestName = "A job whose world load_skirmish replaces fails its wait with restored, and the job ends")]
		public void JobOfReplacedWorldFailsWaitWithRestored()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);
			var replaced = hw.World;

			using var issued = AgentStateDumpTest.Request(MoveLine(unit, destination, ",\"await\":false"));
			var job = issued.RootElement.GetProperty("job").GetInt32();

			// The replaced world is disposed, then the new one loads through LoadComplete, as a load_skirmish does.
			replaced.Dispose();
			using var next = HeadlessWorld.Create(2);

			using var response = AgentStateDumpTest.Request($"{{\"cmd\":\"wait\",\"job\":{job}}}");
			Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
			Assert.That(response.RootElement.GetProperty("error").GetString(), Is.EqualTo("restored"));

			using var again = AgentStateDumpTest.Request($"{{\"cmd\":\"wait\",\"job\":{job}}}");
			Assert.That(again.RootElement.GetProperty("error").GetString(), Does.StartWith($"unknown job {job}"), "The ended job is still open.");
		}
	}
}
