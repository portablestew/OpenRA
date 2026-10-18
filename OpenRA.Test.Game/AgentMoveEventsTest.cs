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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using NUnit.Framework;
using OpenRA.Activities;
using OpenRA.Traits;

namespace OpenRA.Test
{
	/// <summary>
	/// The path and activity payloads from the Move activity (M10, <c>3k-move-hooks</c>). Moves run through the real
	/// activity and the hub, and the mod's types are reached by reflection, as in AgentOrderStanceEventsTest. The custom
	/// path callbacks are built as expression trees, because BlockedByActor is a mod type.
	/// </summary>
	[TestFixture]
	sealed class AgentMoveEventsTest
	{
		bool savedAgentSocket;
		readonly List<object> held = [];
		bool savedEnabled;
		object connection;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[SetUp]
		public void CreateConnection()
		{
			savedAgentSocket = Game.Settings.Debug.AgentSocket;
			Game.Settings.Debug.AgentSocket = true;

			savedEnabled = (bool)ModType("AgentSocket.AgentEvents").GetProperty("Enabled").GetValue(null);
			connection = Activator.CreateInstance(ModType("AgentSocket.AgentConnection"));
			held.Clear();
		}

		[TearDown]
		public void RestoreHub()
		{
			Call(connection, "Close");
			ModType("AgentSocket.AgentEvents").GetProperty("Enabled").SetValue(null, savedEnabled);
			Game.Settings.Debug.AgentSocket = savedAgentSocket;
		}

		static Type ModType(string name)
		{
			return HeadlessMod.ModData.ObjectCreator.FindType(name);
		}

		static object Call(object target, string method, params object[] args)
		{
			return target.GetType().GetMethod(method).Invoke(target, args);
		}

		static T Field<T>(object target, string name)
		{
			return (T)target.GetType().GetProperty(name).GetValue(target);
		}

		static string KindOf(object payload)
		{
			return payload.GetType().Name;
		}

		/// <summary>The wanted events held so far. Reading them takes the connection's turn into the test's list, so a wait can
		/// see its event without losing it for Drain.</summary>
		/// <summary>
		/// Reads the connection's turn into the test's list, and counts the held events of the named types (every type when none
		/// is named). Reading takes the events, so Drain still gets them.
		/// </summary>
		int Pending(params string[] types)
		{
			held.AddRange(Only(((IList)Call(connection, "Flush")).Cast<object>(), "path", "activity"));
			return Wanted(types).Count();
		}

		/// <summary>The held events of the named types, or all of them when none is named.</summary>
		IEnumerable<object> Wanted(string[] types)
		{
			return types.Length == 0 ? held : Only(held, types);
		}

		/// <summary>Takes every queued event. Fails rather than blocking when nothing was delivered.</summary>
		/// <summary>Takes the wanted events held so far. Fails rather than blocking when none reached the connection.</summary>
		/// <summary>Takes the held events of the named types, or all of them. Fails rather than blocking when none reached the connection.</summary>
		List<object> Drain(params string[] types)
		{
			Pending(types);
			var events = Wanted(types).ToList();
			Assert.That(events, Is.Not.Empty, "No events reached the connection.");
			held.RemoveAll(events.Contains);
			return events;
		}

		/// <summary>
		/// Queues a Move whose path callback returns path (destination first, source excluded) for every check. The callback
		/// is an expression tree with the mod's BlockedByActor parameter, so the Move sees exactly what a trait would pass.
		/// </summary>
		static void QueueMoveWithPath(Actor unit, List<CPos> path)
		{
			var check = ModType("Traits.BlockedByActor");
			var checkParam = Expression.Parameter(check, "check");
			var tupleCtor = typeof(ValueTuple<bool, List<CPos>>).GetConstructor([typeof(bool), typeof(List<CPos>)]);
			var body = Expression.New(tupleCtor, Expression.Constant(false), Expression.Constant(path));
			var funcType = typeof(Func<,>).MakeGenericType(check, typeof(ValueTuple<bool, List<CPos>>));
			var getPath = Expression.Lambda(funcType, body, checkParam).Compile();

			unit.QueueActivity((Activity)Activator.CreateInstance(ModType("Activities.Move"), unit, getPath, null));
		}

		static void QueueMoveToOwnCell(Actor unit)
		{
			unit.QueueActivity((Activity)Activator.CreateInstance(ModType("Activities.Move"), unit, unit.Location, null));
		}

		[TestCase(TestName = "A Move order yields a started activity, one path event (found, not a repath), then a completed activity")]
		public void MoveProducesOnePathThenReachedEvent()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			var origin = unit.Location;
			var destination = hw.FindClearArea(origin, 1, 1, 6);
			hw.World.IssueOrder(new Order("Move", unit, Target.FromCell(hw.World, destination), false));

			// The activity completes a tick after the unit arrives, so wait for all three events.
			Assert.That(hw.TickUntil(() => Pending() >= 3, 500), Is.Not.Null, "The move did not complete.");
			Assert.That(unit.Location, Is.EqualTo(destination));

			var events = Drain();
			Assert.That(events.ConvertAll(KindOf), Is.EqualTo(new[] { "ActivityV1", "PathV1", "ActivityV1" }));

			var started = events[0];
			Assert.That(Field<string>(started, "Kind"), Is.EqualTo("move"));
			Assert.That(Field<string>(started, "State"), Is.EqualTo("started"));
			Assert.That(Field<string>(started, "Reason"), Is.Null);
			Assert.That(Field<uint>(started, "Actor"), Is.EqualTo(unit.ActorID));

			var path = events[1];
			Assert.That(Field<string>(path, "Event"), Is.EqualTo("path"));
			Assert.That(Field<bool>(path, "Repath"), Is.False);
			Assert.That(Field<string>(path, "Result"), Is.EqualTo("found"));
			Assert.That(Field<string>(path, "Check"), Is.EqualTo("All"));
			Assert.That(Field<uint>(path, "Actor"), Is.EqualTo(unit.ActorID));
			Assert.That(Field<int[]>(path, "From"), Is.EqualTo(new[] { origin.X, origin.Y }));
			Assert.That(Field<int[]>(path, "To"), Is.EqualTo(new[] { destination.X, destination.Y }));

			var cells = Field<int[][]>(path, "Cells");
			Assert.That(cells[0], Is.EqualTo(new[] { origin.X, origin.Y }), "Cells start at the source.");
			Assert.That(cells[^1], Is.EqualTo(new[] { destination.X, destination.Y }), "Cells end at the destination.");

			var metrics = Field<object>(path, "Metrics");
			Assert.That(metrics, Is.Not.Null);
			Assert.That(Field<int>(metrics, "PathLenCells"), Is.EqualTo(cells.Length - 1));
			Assert.That(Field<double>(metrics, "DetourRatio"), Is.GreaterThanOrEqualTo(1.0));

			var result = events[2];
			Assert.That(Field<string>(result, "Event"), Is.EqualTo("activity"));
			Assert.That(Field<string>(result, "State"), Is.EqualTo("completed"));
			Assert.That(Field<string>(result, "Reason"), Is.EqualTo("reached"));
		}

		[TestCase(TestName = "A move to the cell the unit already occupies reports a zero-length found path, then reached")]
		public void MoveToOwnCellIsZeroLengthFound()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			QueueMoveToOwnCell(unit);
			hw.Tick(3);

			var events = Drain();
			Assert.That(events.ConvertAll(KindOf), Is.EqualTo(new[] { "ActivityV1", "PathV1", "ActivityV1" }));

			var origin = unit.Location;
			var path = events[1];
			Assert.That(Field<string>(path, "Result"), Is.EqualTo("found"));
			Assert.That(Field<bool>(path, "Repath"), Is.False);
			Assert.That(Field<int[][]>(path, "Cells"), Has.Length.EqualTo(1));
			Assert.That(Field<int[]>(path, "To"), Is.EqualTo(new[] { origin.X, origin.Y }));

			var metrics = Field<object>(path, "Metrics");
			Assert.That(Field<int>(metrics, "PathLenCells"), Is.EqualTo(0));
			Assert.That(Field<double>(metrics, "CrowCells"), Is.EqualTo(0.0));
			Assert.That(Field<double>(metrics, "DetourRatio"), Is.EqualTo(1.0));

			Assert.That(Field<string>(events[2], "Reason"), Is.EqualTo("reached"));
		}

		[TestCase(TestName = "An empty path reports no_path with no cells and null metrics, then blocked")]
		public void EmptyPathReportsNoPathThenBlocked()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			QueueMoveWithPath(unit, []);
			hw.Tick(5);

			var events = Drain();
			Assert.That(events.ConvertAll(KindOf), Is.EqualTo(new[] { "ActivityV1", "PathV1", "ActivityV1" }));

			var path = events[1];
			Assert.That(Field<string>(path, "Result"), Is.EqualTo("no_path"));
			Assert.That(Field<int[][]>(path, "Cells"), Has.Length.EqualTo(0));
			Assert.That(Field<object>(path, "Metrics"), Is.Null);

			Assert.That(Field<string>(events[2], "Reason"), Is.EqualTo("blocked"));
		}

		[TestCase(TestName = "A path that is not a chain of adjacent cells reports null metrics and does not throw")]
		public void InvalidPathReportsNullMetricsWithoutThrowing()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			var origin = unit.Location;
			var near = new CPos(origin.X + 2, origin.Y);
			var far = new CPos(origin.X + 8, origin.Y);

			// path[0] is the destination. The step from far to near is 6 cells, and the step from origin to far is 8.
			QueueMoveWithPath(unit, [near, far]);
			Assert.That(() => hw.Tick(5), Throws.Nothing, "An invalid path must not throw on the game thread.");

			var events = Drain();
			Assert.That(events.ConvertAll(KindOf), Is.EqualTo(new[] { "ActivityV1", "PathV1", "PathV1", "ActivityV1" }));

			var first = events[1];
			Assert.That(Field<bool>(first, "Repath"), Is.False);
			Assert.That(Field<string>(first, "Result"), Is.EqualTo("found"));
			Assert.That(Field<object>(first, "Metrics"), Is.Null);
			Assert.That(Field<int[][]>(first, "Cells"), Has.Length.EqualTo(3));

			Assert.That(Field<bool>(events[2], "Repath"), Is.True);
			Assert.That(Field<object>(events[2], "Metrics"), Is.Null);

			Assert.That(Field<string>(events[3], "Reason"), Is.EqualTo("blocked"));
		}

		[TestCase(TestName = "A move cancelled with cells still on its path reports canceled with reason interrupted")]
		public void CanceledMoveReportsInterrupted()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			// Eight cells away, so the path still has cells on it after a couple of ticks.
			var destination = hw.FindClearArea(new CPos(unit.Location.X + 8, unit.Location.Y), 1, 1, 2);
			unit.QueueActivity((Activity)Activator.CreateInstance(ModType("Activities.Move"), unit, destination, null));
			hw.Tick(2);

			// The move's start is raised before the cancel, and only the cancel is under test.
			Pending();
			held.Clear();

			var move = unit.CurrentActivity;
			Assert.That(move.GetType().Name, Is.EqualTo("Move"));

			// Put the activity into Canceling without Move.Cancel, which clears the path first. That leaves cells on the path
			// when the cancel branch of Tick runs.
			var path = (IList)move.GetType().GetField("path", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(move);
			Assert.That(path.Count, Is.GreaterThan(0), "The move has no cells left on its path, so the cancel did not exercise the bug.");

			typeof(Activity).GetProperty("State").GetSetMethod(true).Invoke(move, [ActivityState.Canceling]);

			// Move hands its cancel branch to Tick only after its child move part finishes, so wait for the event.
			Assert.That(hw.TickUntil(() => Pending() > 0, 200), Is.Not.Null, "The canceled move never reported an activity event.");

			var events = Drain();
			Assert.That(events.ConvertAll(KindOf), Is.EqualTo(new[] { "ActivityV1" }));
			Assert.That(Field<string>(events[0], "Kind"), Is.EqualTo("move"));
			Assert.That(Field<string>(events[0], "State"), Is.EqualTo("canceled"));
			Assert.That(Field<string>(events[0], "Reason"), Is.EqualTo("interrupted"));
		}

		[TestCase(TestName = "A Stop order during a Move reports canceled with reason interrupted")]
		public void StopMidMoveReportsInterrupted()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			// Sixteen cells away, so the move is still under way when the Stop arrives.
			var destination = hw.FindClearArea(new CPos(unit.Location.X + 16, unit.Location.Y), 1, 1, 2);
			hw.World.IssueOrder(new Order("Move", unit, Target.FromCell(hw.World, destination), false));
			hw.Tick(2);

			// The move's start is raised before the Stop, and only the Stop's cancel is under test.
			Pending();
			held.Clear();

			hw.World.IssueOrder(new Order("Stop", unit, false));

			Assert.That(hw.TickUntil(() => Pending() > 0, 200), Is.Not.Null, "The stopped move never reported an activity event.");
			Assert.That(unit.Location, Is.Not.EqualTo(destination), "The unit reached the destination before the Stop took effect.");

			var events = Drain();
			Assert.That(events.ConvertAll(KindOf), Is.EqualTo(new[] { "ActivityV1" }));
			Assert.That(Field<string>(events[0], "Kind"), Is.EqualTo("move"));
			Assert.That(Field<string>(events[0], "State"), Is.EqualTo("canceled"));
			Assert.That(Field<string>(events[0], "Reason"), Is.EqualTo("interrupted"));
		}

		[TestCase(TestName = "With the socket disabled, a connection gets no path or activity events")]
		public void NoEventsWhenSocketDisabled()
		{
			Game.Settings.Debug.AgentSocket = false;
			ModType("AgentSocket.AgentEvents").GetProperty("Enabled").SetValue(null, false);

			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);
			hw.World.IssueOrder(new Order("Move", unit, Target.FromCell(hw.World, destination), false));
			Assert.That(hw.TickUntil(() => unit.Location == destination, 500), Is.Not.Null, "The unit never reached the destination.");

			Assert.That((int)connection.GetType().GetProperty("Pending").GetValue(connection), Is.EqualTo(0));
		}

		/// <summary>The events of the named types, in the order they were raised. The connection carries every event; a test looks at its own.</summary>
		static IEnumerable<object> Only(IEnumerable<object> events, params string[] types)
		{
			return events.Where(e => types.Contains(e.GetType().GetProperty("Event")?.GetValue(e) as string));
		}
	}
}
