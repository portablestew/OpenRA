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
	/// The path and move_result payloads from the Move activity (M10, <c>3k-move-hooks</c>). Moves run through the real
	/// activity and the hub, and the mod's types are reached by reflection, as in AgentOrderStanceEventsTest. The custom
	/// path callbacks are built as expression trees, because BlockedByActor is a mod type.
	/// </summary>
	[TestFixture]
	sealed class AgentMoveEventsTest
	{
		bool savedAgentSocket;
		bool savedEnabled;
		object subscriber;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[SetUp]
		public void CreateSubscriber()
		{
			savedAgentSocket = Game.Settings.Debug.AgentSocket;
			Game.Settings.Debug.AgentSocket = true;

			savedEnabled = (bool)ModType("AgentSocket.AgentEvents").GetProperty("Enabled").GetValue(null);
			subscriber = Activator.CreateInstance(ModType("AgentSocket.AgentEventSubscriber"));
		}

		[TearDown]
		public void RestoreHub()
		{
			Call(subscriber, "Close");
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

		int Pending()
		{
			return (int)subscriber.GetType().GetProperty("Pending").GetValue(subscriber);
		}

		/// <summary>Takes every queued event. Fails rather than blocking when nothing was delivered.</summary>
		List<object> Drain()
		{
			var pending = (int)subscriber.GetType().GetProperty("Pending").GetValue(subscriber);
			Assert.That(pending, Is.GreaterThan(0), "No events reached the subscriber.");
			return ((IList)Call(subscriber, "Take")).Cast<object>().ToList();
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

		[TestCase(TestName = "A Move order yields one path event (found, not a repath) then one reached move_result")]
		public void MoveProducesOnePathThenReachedEvent()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			Call(subscriber, "Subscribe", new[] { "path", "move_result" }, null);

			var origin = unit.Location;
			var destination = hw.FindClearArea(origin, 1, 1, 6);
			hw.World.IssueOrder(new Order("Move", unit, Target.FromCell(hw.World, destination), false));

			// move_result is raised when the activity completes, a tick after the unit arrives, so wait for both events.
			Assert.That(hw.TickUntil(() => Pending() >= 2, 500), Is.Not.Null, "The move did not complete.");
			Assert.That(unit.Location, Is.EqualTo(destination));

			var events = Drain();
			Assert.That(events.ConvertAll(KindOf), Is.EqualTo(new[] { "PathV1", "MoveResultV1" }));

			var path = events[0];
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

			var result = events[1];
			Assert.That(Field<string>(result, "Event"), Is.EqualTo("move_result"));
			Assert.That(Field<string>(result, "Outcome"), Is.EqualTo("reached"));
			Assert.That(Field<int>(result, "RemainingCells"), Is.EqualTo(0));
			Assert.That(Field<int[]>(result, "From"), Is.EqualTo(new[] { origin.X, origin.Y }));
			Assert.That(Field<int[]>(result, "To"), Is.EqualTo(new[] { destination.X, destination.Y }));
			Assert.That(Field<int>(result, "Ticks"), Is.GreaterThanOrEqualTo(0));
		}

		[TestCase(TestName = "A move to the cell the unit already occupies reports a zero-length found path, then reached")]
		public void MoveToOwnCellIsZeroLengthFound()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			Call(subscriber, "Subscribe", new[] { "path", "move_result" }, null);

			QueueMoveToOwnCell(unit);
			hw.Tick(3);

			var events = Drain();
			Assert.That(events.ConvertAll(KindOf), Is.EqualTo(new[] { "PathV1", "MoveResultV1" }));

			var origin = unit.Location;
			var path = events[0];
			Assert.That(Field<string>(path, "Result"), Is.EqualTo("found"));
			Assert.That(Field<bool>(path, "Repath"), Is.False);
			Assert.That(Field<int[][]>(path, "Cells"), Has.Length.EqualTo(1));
			Assert.That(Field<int[]>(path, "To"), Is.EqualTo(new[] { origin.X, origin.Y }));

			var metrics = Field<object>(path, "Metrics");
			Assert.That(Field<int>(metrics, "PathLenCells"), Is.EqualTo(0));
			Assert.That(Field<double>(metrics, "CrowCells"), Is.EqualTo(0.0));
			Assert.That(Field<double>(metrics, "DetourRatio"), Is.EqualTo(1.0));

			Assert.That(Field<string>(events[1], "Outcome"), Is.EqualTo("reached"));
		}

		[TestCase(TestName = "An empty path reports no_path with no cells and null metrics, then blocked")]
		public void EmptyPathReportsNoPathThenBlocked()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			Call(subscriber, "Subscribe", new[] { "path", "move_result" }, null);

			QueueMoveWithPath(unit, []);
			hw.Tick(5);

			var events = Drain();
			Assert.That(events.ConvertAll(KindOf), Is.EqualTo(new[] { "PathV1", "MoveResultV1" }));

			var path = events[0];
			Assert.That(Field<string>(path, "Result"), Is.EqualTo("no_path"));
			Assert.That(Field<int[][]>(path, "Cells"), Has.Length.EqualTo(0));
			Assert.That(Field<object>(path, "Metrics"), Is.Null);

			Assert.That(Field<string>(events[1], "Outcome"), Is.EqualTo("blocked"));
		}

		[TestCase(TestName = "A path that is not a chain of adjacent cells reports null metrics and does not throw")]
		public void InvalidPathReportsNullMetricsWithoutThrowing()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			Call(subscriber, "Subscribe", new[] { "path", "move_result" }, null);

			var origin = unit.Location;
			var near = new CPos(origin.X + 2, origin.Y);
			var far = new CPos(origin.X + 8, origin.Y);

			// path[0] is the destination. The step from far to near is 6 cells, and the step from origin to far is 8.
			QueueMoveWithPath(unit, [near, far]);
			Assert.That(() => hw.Tick(5), Throws.Nothing, "An invalid path must not throw on the game thread.");

			var events = Drain();
			Assert.That(events.ConvertAll(KindOf), Is.EqualTo(new[] { "PathV1", "PathV1", "MoveResultV1" }));

			var first = events[0];
			Assert.That(Field<bool>(first, "Repath"), Is.False);
			Assert.That(Field<string>(first, "Result"), Is.EqualTo("found"));
			Assert.That(Field<object>(first, "Metrics"), Is.Null);
			Assert.That(Field<int[][]>(first, "Cells"), Has.Length.EqualTo(3));

			Assert.That(Field<bool>(events[1], "Repath"), Is.True);
			Assert.That(Field<object>(events[1], "Metrics"), Is.Null);

			Assert.That(Field<string>(events[2], "Outcome"), Is.EqualTo("blocked"));
		}

		[TestCase(TestName = "A move cancelled with cells still on its path reports canceled with zero remaining cells")]
		public void CanceledMoveReportsZeroRemainingCells()
		{
			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			// Eight cells away, so the path still has cells on it after a couple of ticks.
			var destination = hw.FindClearArea(new CPos(unit.Location.X + 8, unit.Location.Y), 1, 1, 2);
			unit.QueueActivity((Activity)Activator.CreateInstance(ModType("Activities.Move"), unit, destination, null));
			hw.Tick(2);

			var move = unit.CurrentActivity;
			Assert.That(move.GetType().Name, Is.EqualTo("Move"));

			// Put the activity into Canceling without Move.Cancel, which clears the path first. That leaves cells on the path
			// when the cancel branch of Tick runs, the state the event must still report as zero remaining.
			var path = (IList)move.GetType().GetField("path", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(move);
			Assert.That(path.Count, Is.GreaterThan(0), "The move has no cells left on its path, so the cancel did not exercise the bug.");

			typeof(Activity).GetProperty("State").GetSetMethod(true).Invoke(move, [ActivityState.Canceling]);

			Call(subscriber, "Subscribe", new[] { "move_result" }, null);

			// Move hands its cancel branch to Tick only after its child move part finishes, so wait for the result.
			Assert.That(hw.TickUntil(() => Pending() > 0, 200), Is.Not.Null, "The canceled move never reported a result.");

			var events = Drain();
			Assert.That(events.ConvertAll(KindOf), Is.EqualTo(new[] { "MoveResultV1" }));
			Assert.That(Field<string>(events[0], "Outcome"), Is.EqualTo("canceled"));
			Assert.That(Field<int>(events[0], "RemainingCells"), Is.EqualTo(0));
		}

		[TestCase(TestName = "With the socket disabled, a subscriber gets no path or move_result events")]
		public void NoEventsWhenSocketDisabled()
		{
			Game.Settings.Debug.AgentSocket = false;
			ModType("AgentSocket.AgentEvents").GetProperty("Enabled").SetValue(null, false);

			using var hw = HeadlessWorld.Create(1);
			var unit = hw.CreateActor("e1", hw.FindClearArea(new CPos(50, 50), 1, 1), hw.LocalPlayer);
			hw.Tick(1);

			Call(subscriber, "Subscribe", new[] { "path", "move_result" }, null);
			Assert.That((bool)ModType("AgentSocket.AgentEvents").GetProperty("HasSubscribers").GetValue(null), Is.False);

			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);
			hw.World.IssueOrder(new Order("Move", unit, Target.FromCell(hw.World, destination), false));
			Assert.That(hw.TickUntil(() => unit.Location == destination, 500), Is.Not.Null, "The unit never reached the destination.");

			Assert.That((int)subscriber.GetType().GetProperty("Pending").GetValue(subscriber), Is.EqualTo(0));
		}
	}
}
