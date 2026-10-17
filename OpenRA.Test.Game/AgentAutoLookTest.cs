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
using System.Linq;
using System.Text.Json;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The auto-look (P2.9). A refused place or deploy carries a look over the refused cells, the nearest first, so the reply names what
	/// blocks them. A successful place or deploy carries no look. The setups are those of the place and deploy tests.
	/// </summary>
	[TestFixture]
	sealed class AgentAutoLookTest
	{
		const int PlaceRadius = 10;

		/// <summary>The most cells one look takes (AgentLook.MaxPoints): the auto-look covers at most this many candidates.</summary>
		const int LookPointLimit = 16;
		const int DeployRadius = 20;
		const string Blocker = "e1";
		const string Mcv = "mcv";

		/// <summary>Ticks to let the map's starting units settle before placing the mcv.</summary>
		const int SettleTicks = 5;

		/// <summary>The clearing the mcv is placed in. Its fact footprint is 3x4, so a 6x6 clearing leaves a margin.</summary>
		const int Clearing = 6;

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

		static int FloorDiv(int a, int b)
		{
			return (int)Math.Floor((double)a / b);
		}

		/// <summary>Places a blocker on each lattice cell reaching the square of the given radius around start, as the deploy tests do.</summary>
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

		static Actor CreateMcv(HeadlessWorld hw)
		{
			var area = hw.FindClearArea(hw.LocalPlayer.HomeLocation, Clearing, Clearing);
			return hw.CreateActor(Mcv, area + new CVec(2, 2));
		}

		/// <summary>The look a refusal carries: its cells, and its actors as the id and cell of each.</summary>
		static (JsonElement Look, uint[] Ids, int[][] Cells) LookOf(JsonElement refusal)
		{
			var look = refusal.GetProperty("look");
			var ids = look.GetProperty("actors").EnumerateArray().Select(a => a.GetProperty("id").GetUInt32()).ToArray();
			var cells = look.GetProperty("actors").EnumerateArray()
				.Select(a => a.GetProperty("cell").EnumerateArray().Select(c => c.GetInt32()).ToArray()).ToArray();
			return (look, ids, cells);
		}

		[TestCase(TestName = "A refused place names the blockers over the refused cells, through its look")]
		public void RefusedPlaceHasLookNamingTheBlockers()
		{
			using var hw = HeadlessWorld.Create(1);
			var fact = hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
			hw.Tick(1);
			AgentPlaceTest.QueueAndFinishPowr(hw);

			// The same square as the place test: every cell in it holds an infantry blocker, so no cell is valid.
			var from = fact.Location;
			var blockers = new System.Collections.Generic.List<Actor>();
			for (var dy = -PlaceRadius; dy <= PlaceRadius + 1; dy++)
				for (var dx = -PlaceRadius; dx <= PlaceRadius + 1; dx++)
				{
					var cell = new CPos(from.X + dx, from.Y + dy);
					if (!hw.World.ActorMap.GetActorsAt(cell).Any())
						blockers.Add(hw.CreateActor(Blocker, cell));
				}

			hw.Tick(1);

			using var response = AgentStateDumpTest.Request($"{{\"cmd\":\"place\",\"item\":\"powr\",\"cell\":[{from.X},{from.Y}]}}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False, root.ToString());

			Assert.That(root.TryGetProperty("look", out var look) && look.ValueKind == JsonValueKind.Object, Is.True,
				"A refused place must carry a look.");

			var candidates = root.GetProperty("candidates").EnumerateArray().ToArray();
			var lookCells = look.GetProperty("cells").EnumerateArray().ToArray();
			Assert.That(lookCells, Has.Length.EqualTo(Math.Min(LookPointLimit, candidates.Length)),
				"The look should be over the refused cells, one per candidate up to the point limit.");
			Assert.That(lookCells[0].GetProperty("cell").EnumerateArray().Select(c => c.GetInt32()),
				Is.EqualTo(candidates[0].GetProperty("cell").EnumerateArray().Select(c => c.GetInt32())),
				"The look's first cell should be the nearest refused cell.");

			var (_, ids, cells) = LookOf(root);
			var refused = candidates.Select(c => c.GetProperty("cell").EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToArray();
			Assert.That(cells.Any(c => refused.Any(r => r[0] == c[0] && r[1] == c[1])), Is.True,
				"The look should name a blocker standing on a refused cell.");
			Assert.That(ids.Length, Is.GreaterThan(0), "The look should name the blockers.");
		}

		[TestCase(TestName = "A refused deploy with nothing valid in radius has a look over the rejected cells")]
		public void RefusedDeployHasLookOverRejectedCells()
		{
			using var hw = HeadlessWorld.Create(1);
			hw.Tick(SettleTicks);
			var mcv = CreateMcv(hw);
			var start = new CPos(mcv.Location.X - 1, mcv.Location.Y - 1);
			BlockSquare(hw, start, DeployRadius);
			hw.Tick(1);

			using var response = AgentStateDumpTest.Request($"{{\"cmd\":\"deploy\",\"actor\":{mcv.ActorID}}}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False, root.ToString());
			Assert.That(root.TryGetProperty("look", out var look) && look.ValueKind == JsonValueKind.Object, Is.True,
				"A refused deploy must carry a look.");

			var (_, ids, cells) = LookOf(root);
			Assert.That(look.GetProperty("cells").GetArrayLength(), Is.GreaterThan(0), "The look should cover the rejected cells.");
			Assert.That(cells.Any(c => Math.Max(Math.Abs(c[0] - start.X), Math.Abs(c[1] - start.Y)) <= DeployRadius), Is.True,
				"The look should name blockers inside the searched radius.");
			Assert.That(ids.Length, Is.GreaterThan(0), "The look should name the blockers.");
		}

		[TestCase(TestName = "A successful place and a successful deploy carry no look")]
		public void SuccessfulPlaceAndDeployHaveNoLook()
		{
			// Place: the place test's setup, with a clear cell that is valid.
			using (var hw = HeadlessWorld.Create(1))
			{
				var fact = hw.CreateActor("fact", hw.FindClearArea(new CPos(50, 50), 3, 3), hw.LocalPlayer);
				hw.Tick(1);
				AgentPlaceTest.QueueAndFinishPowr(hw);

				var cell = hw.FindClearArea(fact.Location, 2, 2, 6);
				using var placed = AgentStateDumpTest.Request($"{{\"cmd\":\"place\",\"item\":\"powr\",\"cell\":[{cell.X},{cell.Y}]}}");
				Assert.That(placed.RootElement.GetProperty("ok").GetBoolean(), Is.True, placed.RootElement.ToString());
				Assert.That(placed.RootElement.TryGetProperty("look", out _), Is.False, "A successful place must carry no look.");
			}

			// Deploy: the clear-footprint mcv, as the deploy outcome test places it.
			using (var hw = HeadlessWorld.Create(1))
			{
				hw.Tick(SettleTicks);
				var mcv = CreateMcv(hw);
				using var deployed = AgentStateDumpTest.RequestWithTicks(hw, $"{{\"cmd\":\"deploy\",\"actor\":{mcv.ActorID}}}");
				Assert.That(deployed.RootElement.GetProperty("outcome").GetString(), Is.EqualTo("completed"), deployed.RootElement.ToString());
				Assert.That(deployed.RootElement.TryGetProperty("look", out _), Is.False, "A successful deploy must carry no look.");
			}
		}
	}
}
