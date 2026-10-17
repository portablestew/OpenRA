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
using System.Text.Json;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The deploy command (P1.13). An mcv on a valid footprint deploys with no Move. One whose footprint is blocked moves to the
	/// nearest valid cell and deploys in one reply. A radius with no valid cell is refused with candidate reasons. await:false
	/// returns a job id, and wait collects the same outcome. Blockers sit on a lattice spaced like the fact's 3x4 footprint, so
	/// every footprint whose top-left is inside the square covers one.
	/// </summary>
	[TestFixture]
	sealed class AgentDeployTest
	{
		const string DeployableUnit = "mcv";
		const string Blocker = "e1";

		/// <summary>The building the mcv deploys into (vehicles.yaml: Transforms IntoActor).</summary>
		const string Building = "fact";

		/// <summary>Ticks to let the map's starting units settle before placing the mcv.</summary>
		const int SettleTicks = 5;

		/// <summary>The clearing the mcv is placed in. Its fact footprint is 3x4, so a 6x6 clearing leaves a margin.</summary>
		const int Clearing = 6;

		/// <summary>The search radius deploy uses (AgentDeploy.SearchRadius, Q10).</summary>
		const int SearchRadius = 20;

		/// <summary>The mcv's transform offset (vehicles.yaml: Transforms Offset -1,-1). The test checks the footprint from it.</summary>
		static readonly CVec TransformOffset = new(-1, -1);

		bool savedAgentSocket;
		object subscriber;

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
			if (subscriber != null)
				subscriber.GetType().GetMethod("Close").Invoke(subscriber, null);

			subscriber = null;
			Game.Settings.Debug.AgentSocket = savedAgentSocket;
		}

		static string DeployLine(Actor mcv, string extra = "")
		{
			return $"{{\"cmd\":\"deploy\",\"actor\":{mcv.ActorID}{extra}}}";
		}

		/// <summary>Places an mcv in the middle of a clear patch, big enough for its fact to be built there.</summary>
		static Actor CreateMcv(HeadlessWorld hw)
		{
			var area = hw.FindClearArea(hw.LocalPlayer.HomeLocation, Clearing, Clearing);
			return hw.CreateActor(DeployableUnit, area + new CVec(2, 2));
		}

		/// <summary>Takes the activity events queued on the subscriber so far. Empty when none reached it.</summary>
		static List<object> TakeActivityEvents(object subscriber)
		{
			var pending = (int)subscriber.GetType().GetProperty("Pending").GetValue(subscriber);
			if (pending == 0)
				return [];

			return ((IList)subscriber.GetType().GetMethod("Take").Invoke(subscriber, null)).Cast<object>().ToList();
		}

		static int CountActors(HeadlessWorld hw, string name)
		{
			return hw.World.Actors.Count(a => !a.IsDead && a.Info.Name == name);
		}

		/// <summary>The top-left cell the transform checks: the mcv's cell plus its transform offset.</summary>
		static CPos Footprint(Actor mcv)
		{
			return mcv.Location + TransformOffset;
		}

		static int FloorDiv(int a, int b)
		{
			return (int)Math.Floor((double)a / b);
		}

		/// <summary>
		/// Places a blocker on each lattice cell that reaches the square of the given radius around start. The lattice steps 3
		/// across and 4 down, so every 3x4 footprint whose top-left is in the square covers one. The mcv's own cell is off the
		/// lattice, as the transform offset puts it at start + (1, 1).
		/// </summary>
		static void BlockSquare(HeadlessWorld hw, CPos start, int radius)
		{
			for (var k = FloorDiv(-radius, 3); k <= FloorDiv(radius + 2, 3); k++)
				for (var m = FloorDiv(-radius, 4); m <= FloorDiv(radius + 3, 4); m++)
				{
					var cell = new CPos(start.X + 3 * k, start.Y + 4 * m);
					if (hw.World.Map.Contains(cell))
						hw.CreateActor(Blocker, cell);
				}
		}

		[TestCase(TestName = "An mcv five cells from a valid spot moves there and deploys in one reply")]
		public void FiveCellsAwayMovesAndDeploysInOneReply()
		{
			using var hw = HeadlessWorld.Create(0xDEB1D);
			hw.Tick(SettleTicks);
			var mcv = CreateMcv(hw);
			var start = Footprint(mcv);

			// The radius-4 square is blocked, so the nearest valid cell is the one five out, straight down from start.
			BlockSquare(hw, start, 4);
			hw.Tick(1);

			using var response = AgentStateDumpTest.RequestWithTicks(hw, DeployLine(mcv));
			var root = response.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
			Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("deploy"));
			Assert.That(root.GetProperty("settled").GetBoolean(), Is.True);
			Assert.That(root.GetProperty("outcome").GetString(), Is.EqualTo("completed"));
			Assert.That(root.GetProperty("moved").GetBoolean(), Is.True, "The mcv was not on a valid cell, so it should have moved.");
			Assert.That(root.GetProperty("requested_cell").ValueKind, Is.EqualTo(JsonValueKind.Null),
				"No at was given, so requested_cell echoes null.");

			var cell = root.GetProperty("cell").EnumerateArray().Select(e => e.GetInt32()).ToArray();
			var distance = Math.Max(Math.Abs(cell[0] - start.X), Math.Abs(cell[1] - start.Y));
			Assert.That(distance, Is.EqualTo(5), "The resolved cell should be the nearest valid one, five cells from the start.");
			Assert.That(mcv.IsDead, Is.True, "The mcv should have been replaced.");
		}

		[TestCase(TestName = "A deploy with no valid cell in the radius is refused with candidate reasons and an unchanged mcv")]
		public void FullyBlockedRadiusRefusesWithCandidates()
		{
			using var hw = HeadlessWorld.Create(0xDEB1D);
			hw.Tick(SettleTicks);
			var mcv = CreateMcv(hw);
			BlockSquare(hw, Footprint(mcv), SearchRadius);
			hw.Tick(1);

			using var response = AgentStateDumpTest.RequestWithTicks(hw, DeployLine(mcv));
			var root = response.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False, root.ToString());
			Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("deploy"));
			Assert.That(root.GetProperty("error").GetString(), Does.Contain($"within {SearchRadius} cells"));
			Assert.That(root.GetProperty("hint").GetString(), Is.Not.Empty);
			Assert.That(root.GetProperty("cells_checked").GetInt32(), Is.EqualTo((2 * SearchRadius + 1) * (2 * SearchRadius + 1)));

			var candidates = root.GetProperty("candidates").EnumerateArray().ToArray();
			Assert.That(candidates.Length, Is.EqualTo(8), "The nearest 8 refused cells should be listed.");
			Assert.That(candidates.All(c => !string.IsNullOrEmpty(c.GetProperty("reason").GetString())), Is.True,
				"Every candidate should name its reason.");
			Assert.That(mcv.IsDead, Is.False, "The mcv should not have been replaced.");
		}

		[TestCase(TestName = "An mcv on a valid footprint deploys with no Move, at its own cell")]
		public void ValidCurrentCellIssuesNoMove()
		{
			using var hw = HeadlessWorld.Create(0xDEB1D);
			hw.Tick(SettleTicks);
			var mcv = CreateMcv(hw);
			var start = Footprint(mcv);

			using var response = AgentStateDumpTest.RequestWithTicks(hw, DeployLine(mcv));
			var root = response.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
			Assert.That(root.GetProperty("outcome").GetString(), Is.EqualTo("completed"));
			Assert.That(root.GetProperty("moved").GetBoolean(), Is.False, "A valid current cell needs no Move.");

			var cell = root.GetProperty("cell").EnumerateArray().Select(e => e.GetInt32()).ToArray();
			Assert.That(cell, Is.EqualTo(new[] { start.X, start.Y }));
			Assert.That(mcv.IsDead, Is.True, "The mcv should have been replaced.");
		}

		[TestCase(TestName = "A move that falls short of the chosen cell replies canceled with the cell it stopped on, not completed")]
		public void MoveShortOfChosenCellRepliesCanceled()
		{
			using var hw = HeadlessWorld.Create(0xDEB1D);
			hw.Tick(SettleTicks);
			var mcv = CreateMcv(hw);
			var start = Footprint(mcv);

			// As in the five-cell test, the chosen cell is five below start. Its Move ends at the mcv's cell, which is that cell
			// less the transform offset: start + (1, 6). Once the Move is queued, the lattice is removed and a blocker placed on
			// that destination. The Move falls short on a clear footprint, so a transform queued anyway would deploy there.
			var existing = hw.World.Actors.ToArray();
			BlockSquare(hw, start, 4);
			hw.Tick(1);
			var lattice = hw.World.Actors.Except(existing).ToArray();
			var chosen = new CPos(start.X, start.Y + 5);
			var moveDestination = start + new CVec(1, 6);
			var placed = false;
			var buildingsBefore = CountActors(hw, Building);

			// Subscribed before the request, so the transform's start is seen even if the deploy is refused at once.
			subscriber = Activator.CreateInstance(HeadlessMod.ModData.ObjectCreator.FindType("AgentSocket.AgentEventSubscriber"));
			subscriber.GetType().GetMethod("Subscribe").Invoke(subscriber, [new[] { "activity" }, null]);

			using var response = AgentStateDumpTest.RequestWithTicks(hw, DeployLine(mcv), () =>
			{
				if (!placed && mcv.CurrentActivity != null)
				{
					foreach (var b in lattice)
						b.Dispose();

					hw.CreateActor(Blocker, moveDestination);
					placed = true;
				}
			});
			var root = response.RootElement;

			Assert.That(placed, Is.True, "The mcv never got an activity, so the Move was never blocked.");
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False, root.ToString());
			Assert.That(root.GetProperty("moved").GetBoolean(), Is.True);
			Assert.That(root.GetProperty("outcome").GetString(), Is.EqualTo("canceled"));
			Assert.That(root.GetProperty("reason").GetString(), Does.Contain("not the chosen cell"));

			var cell = root.GetProperty("cell").EnumerateArray().Select(e => e.GetInt32()).ToArray();
			Assert.That(cell, Is.Not.EqualTo(new[] { chosen.X, chosen.Y }), "The reply should name where the mcv stopped, not the chosen cell.");

			// No DeployTransform ran: the mcv is still an mcv, no building was placed, and no transform is in its activities.
			Assert.That(mcv.IsDead, Is.False, "The mcv should not have been replaced.");
			Assert.That(mcv.Info.Name, Is.EqualTo(DeployableUnit));
			Assert.That(CountActors(hw, Building), Is.EqualTo(buildingsBefore), "No building should have been placed.");
			var labels = mcv.CurrentActivity?.DebugLabelComponents().ToArray() ?? [];
			Assert.That(labels, Does.Not.Contain("Transform"), "No transform should be queued on the mcv.");

			// A transform that started, even one refused at once, would report a started transform event.
			var started = TakeActivityEvents(subscriber)
				.Where(e => e.GetType().GetProperty("Kind").GetValue(e) as string == "transform"
					&& e.GetType().GetProperty("State").GetValue(e) as string == "started")
				.ToArray();
			Assert.That(started, Is.Empty, "A transform started, so a deploy was queued after the Move fell short.");
		}

		[TestCase(TestName = "A deploy that needs a Move, with await:false, returns a job id, and wait on that job returns the completed outcome")]
		public void MovedDeployJobWaitCollectsCompleted()
		{
			using var hw = HeadlessWorld.Create(0xDEB1D);
			hw.Tick(SettleTicks);
			var mcv = CreateMcv(hw);
			var start = Footprint(mcv);
			BlockSquare(hw, start, 4);
			hw.Tick(1);

			using var issued = AgentStateDumpTest.RequestWithTicks(hw, DeployLine(mcv, ",\"await\":false"));
			var root = issued.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
			var job = root.GetProperty("job").GetInt32();
			Assert.That(job, Is.GreaterThan(0), "await:false should return a job id.");

			using var waited = AgentStateDumpTest.RequestWithTicks(hw, $"{{\"cmd\":\"wait\",\"job\":{job}}}");
			var outcome = waited.RootElement;

			Assert.That(outcome.GetProperty("ok").GetBoolean(), Is.True, outcome.ToString());
			Assert.That(outcome.GetProperty("cmd").GetString(), Is.EqualTo("wait"));
			Assert.That(outcome.GetProperty("outcome").GetString(), Is.EqualTo("completed"));
			Assert.That(outcome.GetProperty("moved").GetBoolean(), Is.True);
			Assert.That(mcv.IsDead, Is.True, "The mcv should have been replaced.");
		}

		[TestCase(TestName = "await:false returns a job id, and wait on that job returns the deploy outcome")]
		public void AwaitFalseReturnsJobThatWaitCollects()
		{
			using var hw = HeadlessWorld.Create(0xDEB1D);
			hw.Tick(SettleTicks);
			var mcv = CreateMcv(hw);

			using var issued = AgentStateDumpTest.RequestWithTicks(hw, DeployLine(mcv, ",\"await\":false"));
			var root = issued.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
			Assert.That(root.GetProperty("cmd").GetString(), Is.EqualTo("deploy"));
			var job = root.GetProperty("job").GetInt32();
			Assert.That(job, Is.GreaterThan(0), "await:false should return a job id.");

			using var waited = AgentStateDumpTest.RequestWithTicks(hw, $"{{\"cmd\":\"wait\",\"job\":{job}}}");
			var outcome = waited.RootElement;

			Assert.That(outcome.GetProperty("ok").GetBoolean(), Is.True, outcome.ToString());
			Assert.That(outcome.GetProperty("cmd").GetString(), Is.EqualTo("wait"));
			Assert.That(outcome.GetProperty("outcome").GetString(), Is.EqualTo("completed"));
			Assert.That(mcv.IsDead, Is.True, "The mcv should have been replaced.");
		}
	}
}
