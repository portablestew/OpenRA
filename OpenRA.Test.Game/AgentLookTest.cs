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
		const string CrateActor = "moneycrate";

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
				.Where(a => a.OccupiesSpace != null && !a.IsDead && a.Info.Name != "mpspawn"
					&& (a.Owner.PlayerReference?.OwnsWorld != true || a.Info.Name == CrateActor)
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
			var resource = FindResourceNear(hw, home);

			Assert.That(resource, Is.Not.Null, "The test map should have a resource cell near the home.");
			using var found = Look($"\"at\":[{resource.Value.X},{resource.Value.Y}]");
			var resourceCell = found.RootElement.GetProperty("cells")[0];
			Assert.That(resourceCell.GetProperty("resource").GetString(), Is.Not.Empty, "A resource cell should name its resource.");
			Assert.That(resourceCell.GetProperty("density").GetInt32(), Is.GreaterThan(0), "A resource cell should report its density.");
		}

		/// <summary>A resource cell near a point, found by asking the socket about blocks of cells, 16 points at a time.</summary>
		static CPos? FindResourceNear(HeadlessWorld hw, CPos home)
		{
			for (var y = home.Y - 20; y < home.Y + 20; y += 4)
			{
				var block = new List<CPos>();
				for (var x = home.X - 20; x < home.X + 20; x++)
					block.Add(new CPos(x, y));

				for (var offset = 0; offset < block.Count; offset += 16)
				{
					using var response = Look(Points(block.Skip(offset).Take(16).Where(hw.World.Map.Contains).ToArray()));
					foreach (var cell in response.RootElement.GetProperty("cells").EnumerateArray())
						if (cell.TryGetProperty("resource", out var type) && type.GetString().Length > 0)
							return new CPos(cell.GetProperty("cell")[0].GetInt32(), cell.GetProperty("cell")[1].GetInt32());
				}
			}

			return null;
		}

		[TestCase(TestName = "The summary names the resource at a resource cell, counted at any density")]
		public void SummaryNamesResourceClasses()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);
			var home = hw.LocalPlayer.HomeLocation;

			var resource = FindResourceNear(hw, home);
			Assert.That(resource, Is.Not.Null, "The test map should have a resource cell near the home.");

			using var response = Look($"\"at\":[{resource.Value.X},{resource.Value.Y}],\"radius\":6");
			var summary = Summary(response.RootElement);
			var resources = summary.GetProperty("classes").EnumerateArray()
				.Where(c => c.GetProperty("kind").GetString() == "resource").ToArray();

			Assert.That(resources, Is.Not.Empty, "The summary around a resource cell should name its resource: " + summary.ToString());
			Assert.That(resources.Sum(c => c.GetProperty("count").GetInt32()), Is.GreaterThanOrEqualTo(1),
				"At least the point itself has the resource.");

			// The nearest occurrence of the point's own resource is the point itself, at distance zero.
			var here = resources.Where(c => c.GetProperty("nearest").GetProperty("distance").GetInt32() == 0).ToArray();
			Assert.That(here, Is.Not.Empty, "The point's own resource class should be at distance zero.");
			Assert.That(here.Single().GetProperty("nearest").GetProperty("cell")[0].GetInt32(), Is.EqualTo(resource.Value.X));
			Assert.That(here.Single().GetProperty("nearest").GetProperty("cell")[1].GetInt32(), Is.EqualTo(resource.Value.Y));
		}

		/// <summary>The cells a summary covers for a point: the on-map cells whose centre is within radius of it (world units, 1024 per cell).</summary>
		static IEnumerable<CPos> Covered(HeadlessWorld hw, CPos point, int radius)
		{
			var origin = hw.World.Map.CenterOfCell(point);
			for (var y = point.Y - radius; y <= point.Y + radius; y++)
				for (var x = point.X - radius; x <= point.X + radius; x++)
				{
					var cell = new CPos(x, y);
					if (hw.World.Map.Contains(cell) && (hw.World.Map.CenterOfCell(cell) - origin).Length <= radius * 1024)
						yield return cell;
				}
		}

		/// <summary>The summary entry for the only point of an at-form look.</summary>
		static JsonElement Summary(JsonElement response)
		{
			return response.GetProperty("summary").EnumerateArray().Single();
		}

		[TestCase(TestName = "The summary counts the classes within radius of a point, and its scope says how many cells it covers")]
		public void SummaryCountsClassesWithinRadius()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);
			var home = hw.LocalPlayer.HomeLocation;

			using var response = Look($"\"at\":[{home.X},{home.Y}],\"radius\":6");
			var summary = Summary(response.RootElement);

			Assert.That(summary.GetProperty("pt")[0].GetInt32(), Is.EqualTo(home.X));
			Assert.That(summary.GetProperty("pt")[1].GetInt32(), Is.EqualTo(home.Y));
			Assert.That(summary.GetProperty("radius").GetInt32(), Is.EqualTo(6));
			Assert.That(summary.GetProperty("cells").GetInt32(), Is.EqualTo(Covered(hw, home, 6).Count()),
				"The scope should say how many on-map cells the counts cover.");

			var cells = summary.GetProperty("cells").GetInt32();
			var classes = summary.GetProperty("classes").EnumerateArray().ToArray();
			Assert.That(classes, Is.Not.Empty, "The test map should have terrain to count.");
			Assert.That(classes.Select(c => c.GetProperty("kind").GetString()).Distinct(),
				Is.SubsetOf(["terrain", "resource", "blocking"]), "Every class names one of the three kinds.");

			// Every counted cell has exactly one terrain type, so the terrain classes partition the covered cells.
			var terrain = classes.Where(c => c.GetProperty("kind").GetString() == "terrain").ToArray();
			Assert.That(terrain.Sum(c => c.GetProperty("count").GetInt32()), Is.EqualTo(cells),
				"The terrain counts should partition the cells the summary covers.");

			// Each terrain class names the nearest cell of that class, at the smallest distance in the covered disc.
			var origin = hw.World.Map.CenterOfCell(home);
			foreach (var cls in terrain)
			{
				var type = cls.GetProperty("class").GetString();
				var expected = Covered(hw, home, 6)
					.Where(c => hw.World.Map.GetTerrainInfo(c).Type == type)
					.Min(c => (hw.World.Map.CenterOfCell(c) - origin).Length);

				var nearest = cls.GetProperty("nearest");
				var named = new CPos(nearest.GetProperty("cell")[0].GetInt32(), nearest.GetProperty("cell")[1].GetInt32());

				Assert.That(nearest.GetProperty("distance").GetInt32(), Is.EqualTo(expected), $"The nearest {type} should be the closest one.");
				Assert.That((hw.World.Map.CenterOfCell(named) - origin).Length, Is.EqualTo(expected), "The named cell should be at the nearest distance.");
				Assert.That(hw.World.Map.GetTerrainInfo(named).Type, Is.EqualTo(type), "The named cell should be of that class.");
			}
		}

		[TestCase(TestName = "The summary names a blocking class from the occupying actor's rules, and counts the cells it occupies")]
		public void SummaryNamesBlockingClassFromRules()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);
			var neutral = hw.World.Players.First(p => p.PlayerReference.OwnsWorld);

			// A tree near the home, where the base already sees it, so the summary can count it.
			var cell = hw.FindClearArea(hw.LocalPlayer.HomeLocation, 2, 2, 3);
			var tree = hw.CreateActor("T01", cell, neutral);
			hw.Tick(SettleTicks);

			using var response = Look($"\"at\":[{cell.X},{cell.Y}],\"radius\":3");
			var summary = Summary(response.RootElement);
			var blocking = summary.GetProperty("classes").EnumerateArray()
				.Where(c => c.GetProperty("kind").GetString() == "blocking").ToArray();

			Assert.That(blocking, Is.Not.Empty, "The tree should be counted as a blocking class: " + summary.ToString());
			var counted = blocking.Single(c => c.GetProperty("class").GetString() == "Tree");

			// The count is the cells the tree occupies within the radius, by the engine's own occupancy.
			var covered = Covered(hw, cell, 3).ToHashSet();
			var expected = tree.OccupiesSpace.OccupiedCells().Select(o => o.Cell).Distinct().Count(covered.Contains);
			Assert.That(counted.GetProperty("count").GetInt32(), Is.EqualTo(expected),
				"The count should be the cells the tree occupies inside the radius: " + counted.ToString());
			Assert.That(expected, Is.GreaterThanOrEqualTo(1), "The tree should occupy at least one cell.");
			Assert.That(counted.GetProperty("nearest").GetProperty("distance").GetInt32(), Is.LessThanOrEqualTo(3 * 1024),
				"The nearest blocked cell should be within the radius.");
		}

		[TestCase(TestName = "A known crate is a look actor and a crate blocking class; a fogged crate is in neither")]
		public void CratesAreSurfaced()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);
			var neutral = hw.World.Players.First(p => p.PlayerReference.OwnsWorld);

			// A crate near the home, where the base already sees it, and one at the enemy home, out of sight.
			var cell = hw.FindClearArea(hw.LocalPlayer.HomeLocation, 2, 2, 3);
			var crate = hw.CreateActor(CrateActor, cell, neutral);
			var hidden = hw.CreateActor(CrateActor, hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 2, 2), neutral);
			hw.Tick(SettleTicks);

			Assert.That(Known(crate, hw.LocalPlayer), Is.True, "Precondition: the crate near the home should be known.");
			Assert.That(Known(hidden, hw.LocalPlayer), Is.False, "Precondition: the crate at the enemy home should be out of sight.");

			using var response = Look($"\"at\":[{cell.X},{cell.Y}],\"radius\":3");
			var root = response.RootElement;

			Assert.That(ActorIds(root), Does.Contain(crate.ActorID), "A known crate should be one of the actors.");
			Assert.That(ActorIds(root), Does.Not.Contain(hidden.ActorID), "A fogged crate must not be one of the actors.");

			var listed = root.GetProperty("actors").EnumerateArray().Single(a => a.GetProperty("id").GetUInt32() == crate.ActorID);
			Assert.That(listed.GetProperty("type").GetString(), Is.EqualTo(CrateActor), "The crate should be listed under its own type.");
			Assert.That(listed.GetProperty("hp").ValueKind, Is.EqualTo(JsonValueKind.Null), "A crate has no health.");

			// The class is the crush class its Crate trait declares: the name its collectors crush by.
			var summary = Summary(root);
			var counted = summary.GetProperty("classes").EnumerateArray()
				.Single(c => c.GetProperty("kind").GetString() == "blocking" && c.GetProperty("class").GetString() == "crate");
			Assert.That(counted.GetProperty("nearest").GetProperty("cell")[0].GetInt32(), Is.EqualTo(cell.X),
				"The summary's nearest crate should be the crate's cell.");
			Assert.That(counted.GetProperty("nearest").GetProperty("cell")[1].GetInt32(), Is.EqualTo(cell.Y),
				"The summary's nearest crate should be the crate's cell.");
		}

		[TestCase(TestName = "k of zero returns cells and summary with no actors, k of one returns the nearest known actor, and k outside 0 to 100 is refused")]
		public void KZeroIsSummaryOnly()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			// A row of three enemy infantry, one cell apart, starting one cell east of the point.
			var point = hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3, 30);
			for (var i = 1; i <= 3; i++)
				hw.CreateActor(Infantry, point + new CVec(i, 0), hw.EnemyPlayer);
			hw.Tick(SettleTicks);
			LaunchSatellite(hw, local);
			hw.Tick(SettleTicks);

			using (var none = Look("\"k\":0," + Points(point)))
			{
				var root = none.RootElement;
				Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, "k of zero should be accepted.");
				Assert.That(root.GetProperty("actors").GetArrayLength(), Is.Zero, "k of zero should return no actors.");
				Assert.That(root.GetProperty("cells").GetArrayLength(), Is.EqualTo(1), "k of zero should still report the looked-at cell.");
				Assert.That(Summary(root).GetProperty("classes").GetArrayLength(), Is.GreaterThan(0),
					"k of zero should still report the class summary.");
			}

			using (var one = Look("\"k\":1," + Points(point)))
			{
				var root = one.RootElement;
				var expected = NearestKnown(hw, point).Take(1).Select(a => a.ActorID).ToArray();
				Assert.That(ActorIds(root), Is.EqualTo(expected), "k of one should return the nearest known actor.");
				Assert.That(root.GetProperty("cells").GetArrayLength(), Is.EqualTo(1));
				Assert.That(Summary(root).GetProperty("classes").GetArrayLength(), Is.GreaterThan(0));
			}

			foreach (var bad in (int[])[-1, 101])
			{
				using var refused = Look($"\"k\":{bad}," + Points(point));
				Assert.That(refused.RootElement.GetProperty("ok").GetBoolean(), Is.False, $"k {bad} should be refused.");
				Assert.That(refused.RootElement.GetProperty("error").GetString(), Is.EqualTo("k must be an integer from 0 to 100"));
			}
		}

		[TestCase(TestName = "A radius outside 1 to 20 is refused, and the default radius is ten")]
		public void RadiusIsBounded()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);
			var home = hw.LocalPlayer.HomeLocation;

			foreach (var bad in (int[])[0, 21, -1])
			{
				using var refused = Look($"\"at\":[{home.X},{home.Y}],\"radius\":{bad}");
				Assert.That(refused.RootElement.GetProperty("ok").GetBoolean(), Is.False, $"radius {bad} should be refused.");
				Assert.That(refused.RootElement.GetProperty("error").GetString(), Is.EqualTo("radius must be an integer from 1 to 20"));
			}

			using (var byDefault = Look($"\"at\":[{home.X},{home.Y}]"))
				Assert.That(Summary(byDefault.RootElement).GetProperty("radius").GetInt32(), Is.EqualTo(10),
					"Without a radius, the summary covers ten cells.");

			using var one = Look($"\"at\":[{home.X},{home.Y}],\"radius\":1");
			var summary = Summary(one.RootElement);
			Assert.That(summary.GetProperty("radius").GetInt32(), Is.EqualTo(1));
			Assert.That(summary.GetProperty("cells").GetInt32(), Is.EqualTo(Covered(hw, home, 1).Count()),
				"A radius of one covers the point and its orthogonal neighbours.");
		}
	}
}
