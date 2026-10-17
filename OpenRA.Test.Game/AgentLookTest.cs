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
using System.Text.Json;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The look command (P2.8). The k nearest actors to each reference point, of any owner, under the fog policy; the points share k
	/// fairly; dist and pt appear only with reference points; each looked-at cell reports its terrain, buildability and resource. The
	/// satellite and the fog helper are reached the way the other agent tests reach them, since the mod's types load through ObjectCreator.
	/// </summary>
	[TestFixture]
	sealed class AgentLookTest
	{
		const int Seed = 0x100C;
		const string Infantry = "e1";
		const string Satellite = "atek";

		/// <summary>Ticks to let the map settle, and visibility and the satellite update.</summary>
		const int SettleTicks = 5;

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

		static Type ModType(string name)
		{
			return HeadlessMod.ModData.ObjectCreator.FindType(name);
		}

		/// <summary>The fog helper under test, read through the mod's type.</summary>
		static bool Known(Actor actor, Player observer)
		{
			return (bool)ModType("AgentSocket.AgentFog").GetMethod("Known").Invoke(null, [actor, observer]);
		}

		/// <summary>The player's GpsWatcher trait, read through the mod's type.</summary>
		static object Watcher(Player player)
		{
			var trait = ModType("Traits.GpsWatcher");
			return typeof(Actor).GetMethod("Trait").MakeGenericMethod(trait).Invoke(player.PlayerActor, null);
		}

		/// <summary>Launches the player's satellite through its real entry points: the satellite building is added, then it reaches orbit.</summary>
		static void LaunchSatellite(HeadlessWorld hw, Player player)
		{
			var watcher = Watcher(player);
			var satellite = hw.CreateActor(Satellite, hw.FindClearArea(player.HomeLocation, 4, 4), player);
			var type = watcher.GetType();
			type.GetMethod("GpsAdd").Invoke(watcher, [satellite]);
			type.GetMethod("ReachedOrbit").Invoke(watcher, [player]);
		}

		/// <summary>A look request for the given selector, answered by the socket. The caller disposes the document.</summary>
		static JsonDocument Look(string selector)
		{
			return AgentStateDumpTest.Request("{\"cmd\":\"look\"," + selector + "}");
		}

		static string Points(params CPos[] cells)
		{
			return "\"points\":[" + string.Join(",", cells.Select(c => $"[{c.X},{c.Y}]")) + "]";
		}

		/// <summary>The ids of the actors a look response names, in response order.</summary>
		static uint[] ActorIds(JsonElement response)
		{
			return response.GetProperty("actors").EnumerateArray().Select(a => a.GetProperty("id").GetUInt32()).ToArray();
		}

		/// <summary>The actors the observer knows, nearest first from a point, by the same rule the look applies: the local player's own, or fog-known.</summary>
		static Actor[] NearestKnown(HeadlessWorld hw, CPos point)
		{
			var center = hw.World.Map.CenterOfCell(point);
			var local = hw.LocalPlayer;
			return hw.World.Actors
				.Where(a => a.OccupiesSpace != null && !a.IsDead && a.Owner.PlayerReference?.OwnsWorld != true && a.Info.Name != "mpspawn"
					&& (a.Owner == local || Known(a, local)))
				.OrderBy(a => (a.CenterPosition - center).Length).ThenBy(a => a.ActorID)
				.ToArray();
		}

		[TestCase(TestName = "A fogged, never-seen enemy is absent from look until a real GPS launch makes it known")]
		public void FoggedNeverSeenAbsentUntilGpsLaunch()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			// The AgentFogTest setup: an enemy unit at the enemy's home, out of every local unit's sight.
			var enemy = hw.CreateActor(Infantry, hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3), hw.EnemyPlayer);
			hw.Tick(SettleTicks);
			Assert.That(local.Shroud.IsVisible(enemy.CenterPosition), Is.False, "Precondition: the enemy unit should be out of sight.");
			Assert.That(Known(enemy, local), Is.False, "Precondition: the enemy unit should be unknown before the launch.");

			using (var before = Look(Points(enemy.Location)))
				Assert.That(ActorIds(before.RootElement), Does.Not.Contain(enemy.ActorID),
					"A fogged, never-seen enemy must be absent from look.");

			LaunchSatellite(hw, local);
			hw.Tick(SettleTicks);
			Assert.That(Known(enemy, local), Is.True, "After the real GPS launch, the enemy unit should be known.");

			using var after = Look(Points(enemy.Location));
			Assert.That(ActorIds(after.RootElement), Does.Contain(enemy.ActorID),
				"After the real GPS launch, the enemy must appear in look.");
		}

		[TestCase(TestName = "Look returns the nearest known actors first, and 20 of them by default")]
		public void NearestFirstAndDefaultTwenty()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			// A row of 30 enemy infantry, one cell apart, starting one cell east of the point.
			var point = hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3, 30);
			for (var i = 1; i <= 30; i++)
				hw.CreateActor(Infantry, point + new CVec(i, 0), hw.EnemyPlayer);
			hw.Tick(SettleTicks);
			LaunchSatellite(hw, local);
			hw.Tick(SettleTicks);

			using var response = Look(Points(point));
			var root = response.RootElement;
			var actors = root.GetProperty("actors").EnumerateArray().ToArray();

			var expected = NearestKnown(hw, point).Take(20).Select(a => a.ActorID).ToArray();
			Assert.That(actors, Has.Length.EqualTo(20), "Without k, look returns the 20 nearest.");
			Assert.That(ActorIds(root), Is.EqualTo(expected), "The actors should be the twenty nearest known actors, nearest first.");

			var dists = actors.Select(a => a.GetProperty("dist").GetInt32()).ToArray();
			Assert.That(dists, Is.Ordered.Ascending, "The distances should be non-decreasing.");
		}

		[TestCase(TestName = "With two far-apart points, each gets a fair share of k, and no actor is listed twice")]
		public void PointsShareKFairly()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			// Two rows of 15 enemy infantry, one at each far-apart point.
			var first = hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3, 30);
			for (var i = 1; i <= 15; i++)
				hw.CreateActor(Infantry, first + new CVec(i, 0), hw.EnemyPlayer);

			var second = hw.FindClearArea(local.HomeLocation, 3, 3, 30);
			for (var i = 1; i <= 15; i++)
				hw.CreateActor(Infantry, second + new CVec(i, 0), hw.EnemyPlayer);
			hw.Tick(SettleTicks);
			LaunchSatellite(hw, local);
			hw.Tick(SettleTicks);

			using var response = Look(Points(first, second));
			var actors = response.RootElement.GetProperty("actors").EnumerateArray().ToArray();

			var ids = actors.Select(a => a.GetProperty("id").GetUInt32()).ToArray();
			Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Length), "No actor should be listed twice.");

			var perPoint = actors.GroupBy(a => (a.GetProperty("pt")[0].GetInt32(), a.GetProperty("pt")[1].GetInt32()))
				.ToDictionary(g => g.Key, g => g.Count());
			Assert.That(perPoint.Values.Sum(), Is.EqualTo(20), "The two points together should share the default twenty.");
			Assert.That(perPoint[(first.X, first.Y)], Is.EqualTo(10), "The first point should get its fair share of ten.");
			Assert.That(perPoint[(second.X, second.Y)], Is.EqualTo(10), "The second point should get its fair share of ten.");
		}

		[TestCase(TestName = "dist and pt are absent without reference points, and present with them")]
		public void DistAndPtOnlyWithReferencePoints()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);
			var at = hw.LocalPlayer.HomeLocation;

			using (var single = Look($"\"at\":[{at.X},{at.Y}]"))
			{
				var first = single.RootElement.GetProperty("actors").EnumerateArray().First();
				Assert.That(first.EnumerateObject().Select(p => p.Name), Does.Not.Contain("dist").And.Not.Contain("pt"),
					"With at, no reference point is given, so no dist or pt.");
			}

			using (var withPoints = Look(Points(at)))
			{
				var first = withPoints.RootElement.GetProperty("actors").EnumerateArray().First();
				Assert.That(first.EnumerateObject().Select(p => p.Name), Does.Contain("dist").And.Contain("pt"),
					"With points, each actor carries dist and pt.");
			}
		}

		[TestCase(TestName = "A looked-at cell reports its terrain and buildability, and a resource cell reports its resource and density")]
		public void CellsReportTerrainAndResource()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);
			var home = hw.LocalPlayer.HomeLocation;

			using (var plain = Look($"\"at\":[{home.X},{home.Y}]"))
			{
				var cell = plain.RootElement.GetProperty("cells")[0];
				Assert.That(cell.GetProperty("terrain").GetString(), Is.Not.Empty, "The cell should name its terrain.");
				Assert.That(cell.GetProperty("buildable").ValueKind, Is.EqualTo(JsonValueKind.True).Or.EqualTo(JsonValueKind.False),
					"The cell should say whether it is buildable.");
			}

			// Find a resource cell near the home by asking the socket about blocks of cells, 16 points at a time.
			CPos? resource = null;
			for (var y = home.Y - 20; y < home.Y + 20 && resource == null; y += 4)
			{
				var block = new List<CPos>();
				for (var x = home.X - 20; x < home.X + 20; x++)
					block.Add(new CPos(x, y));

				for (var offset = 0; offset < block.Count && resource == null; offset += 16)
				{
					using var response = Look(Points(block.Skip(offset).Take(16).Where(hw.World.Map.Contains).ToArray()));
					foreach (var cell in response.RootElement.GetProperty("cells").EnumerateArray())
						if (cell.TryGetProperty("resource", out var type) && type.GetString().Length > 0)
						{
							resource = new CPos(cell.GetProperty("cell")[0].GetInt32(), cell.GetProperty("cell")[1].GetInt32());
							break;
						}
				}
			}

			Assert.That(resource, Is.Not.Null, "The test map should have a resource cell near the home.");
			using var found = Look($"\"at\":[{resource.Value.X},{resource.Value.Y}]");
			var resourceCell = found.RootElement.GetProperty("cells")[0];
			Assert.That(resourceCell.GetProperty("resource").GetString(), Is.Not.Empty, "A resource cell should name its resource.");
			Assert.That(resourceCell.GetProperty("density").GetInt32(), Is.GreaterThan(0), "A resource cell should report its density.");
		}
	}
}
