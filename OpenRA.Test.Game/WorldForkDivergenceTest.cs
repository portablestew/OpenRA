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
using System.Reflection;
using NUnit.Framework;
using OpenRA.Network;
using OpenRA.Traits;

namespace OpenRA.Test
{
	/// <summary>
	/// A divergence oracle for the rewind path. A control world is built, ticked to the capture tick, and then runs a
	/// script of real orders (a sale or a production placement, a group move) with no capture and no restore. A live
	/// world captures at the same tick, runs the script plus outgoing-only residue (a placed and sold building, a second
	/// group move) that the replay never repeats, and is then restored. The restored world must match the control at
	/// the capture tick, tick by tick through the replay, and in full state at the end, both after one restore and after
	/// a second restore from the replayed timeline. Any state the restored fork shares with the outgoing timeline shows
	/// up as a mismatch.
	/// </summary>
	[TestFixture]
	sealed class WorldForkDivergenceTest
	{
		const int Seed = 0xC0FFEE;
		const int TicksBeforeCapture = 20;
		const int ReplayTicks = 150;
		const int OutgoingTicks = 150;
		const int SellTicks = 50;
		const int MaxBuildTicks = 3000;
		const int MaxPlaceTicks = 50;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[SetUp]
		public void ResetWindow()
		{
			WorldForks.Clear();
		}

		[TearDown]
		public void ClearWindow()
		{
			WorldForks.Clear();
		}

		[TestCase(true, TestName = "A building that existed at capture and was sold after it stays sold in every restore")]
		[TestCase(false, TestName = "A building placed after capture and absent at capture is gone after restore and placed again on replay")]
		public void RestoredReplayMatchesControl(bool buildingAtCapture)
		{
			using var parent = HeadlessWorld.Create(Seed);
			var layout = Setup(parent, buildingAtCapture);
			parent.Tick(TicksBeforeCapture);
			var captureTick = parent.World.WorldTick;
			var captureHash = parent.World.SyncHash();
			Assert.That(WorldForks.Capture(parent.OrderManager), Is.True, "The capture was refused.");

			// Control: the same setup and ticks, never captured and never restored.
			using var control = HeadlessWorld.Create(Seed);
			Setup(control, buildingAtCapture);
			control.Tick(TicksBeforeCapture);
			Assert.That(control.World.SyncHash(), Is.EqualTo(captureHash),
				"The control did not reach the captured state, so the comparison below is not like for like.");
			var controlAtCapture = Fingerprint(control.World, layout);

			var controlReplay = new Timeline(control.OrderManager);
			Script(controlReplay, layout, buildingAtCapture);
			AssertScriptLanded(controlReplay.World, layout, buildingAtCapture);
			var controlAtEnd = Fingerprint(controlReplay.World, layout);

			// Outgoing: the script, then residue the replay never repeats.
			var outgoing = new Timeline(parent.OrderManager);
			Script(outgoing, layout, buildingAtCapture);
			PlaceBuilding(outgoing, layout, layout.ExtraFootprint);
			var extra = outgoing.World.Actors.Single(a => a.Info.Name == "powr" && !a.IsDead && a.Location == layout.ExtraFootprint);
			Sell(outgoing, extra.ActorID);
			outgoing.Run(SellTicks);
			foreach (var unit in Units(outgoing.World, layout))
				outgoing.Issue(MoveTo(unit, layout.ExtraDestination));
			outgoing.Run(OutgoingTicks);

			Assert.That(Fingerprint(parent.World, layout), Is.Not.EqualTo(controlAtEnd),
				"The outgoing timeline left the same state as the control, so the restore below would prove nothing.");

			// First restore: must land on the captured state, then replay like the control.
			Assert.That(WorldForks.Undo(parent.OrderManager, null, LiveAfterGap(parent.World), out var firstFailure), Is.True, firstFailure);
			var firstRestore = new Timeline(parent.OrderManager);
			Assert.That(firstRestore.World.WorldTick, Is.EqualTo(captureTick), "The first restore did not land on the captured tick.");
			var restoredAtCapture = Fingerprint(firstRestore.World, layout);
			Assert.That(CanEnterCellUsesLocomotorOf(firstRestore.World, layout), Is.True,
				"The restored units answer CanEnterCell with a Locomotor that belongs to another world.");
			AssertSame("The first restore differs from the captured state", controlAtCapture, restoredAtCapture);

			Script(firstRestore, layout, buildingAtCapture);
			AssertTimelineSame("The first restore's replay", controlReplay, firstRestore);
			AssertSame("The first restore's replay ends in a different state", controlAtEnd, Fingerprint(firstRestore.World, layout));

			// Second restore from the replayed timeline: the snapshot must still hold the captured state.
			Assert.That(WorldForks.Undo(parent.OrderManager, null, LiveAfterGap(firstRestore.World), out var secondFailure), Is.True, secondFailure);
			var secondRestore = new Timeline(parent.OrderManager);
			AssertSame("The second restore differs from the captured state", controlAtCapture, Fingerprint(secondRestore.World, layout));

			Script(secondRestore, layout, buildingAtCapture);
			AssertTimelineSame("The second restore's replay", controlReplay, secondRestore);
			AssertSame("The second restore's replay ends in a different state", controlAtEnd, Fingerprint(secondRestore.World, layout));
		}

		/// <summary>
		/// Builds the fixture: a production base, three infantry, and the footprints the script and the residue use.
		/// Every footprint is checked to be disjoint from the others, so no placement can hide another.
		/// </summary>
		static Layout Setup(HeadlessWorld w, bool buildingAtCapture)
		{
			var fact = w.CreateActor("fact", w.FindClearArea(w.LocalPlayer.HomeLocation, 3, 3));
			w.Tick(1);

			// Footprints where the real placement rules accept a POWR, as the agent placement code searches for them.
			var footprint = ValidPlacement(w.World, fact.Location, []);
			var extraFootprint = ValidPlacement(w.World, fact.Location, Rect(footprint, 2, 2));

			var groupStart = w.FindClearArea(fact.Location + new CVec(0, 10), 3, 1);
			var units = Enumerable.Range(0, 3).Select(i => w.CreateActor("e1", groupStart + new CVec(i, 0))).ToArray();
			var destination = w.FindClearArea(fact.Location + new CVec(0, 14), 2, 2);
			var extraDestination = w.FindClearArea(fact.Location + new CVec(12, 14), 2, 2);

			var layout = new Layout
			{
				// The building queues live on the player actor (ClassicProductionQueue@Building), not on the base.
				Player = w.LocalPlayer.PlayerActor.ActorID,
				Group = units.Select(u => u.ActorID).ToArray(),
				Footprint = footprint,
				Destination = destination,
				ExtraFootprint = extraFootprint,
				ExtraDestination = extraDestination,
				GroupStart = groupStart,
			};

			var rects = new[] { Rect(footprint, 2, 2), Rect(destination, 2, 2), Rect(extraFootprint, 2, 2), Rect(extraDestination, 2, 2) };
			Assert.That(rects.SelectMany(r => r).Distinct().Count(), Is.EqualTo(rects.Sum(r => r.Count())),
				$"Setup: two of the test footprints overlap: footprint {footprint}, extra footprint {extraFootprint}, " +
				$"destination {destination}, extra destination {extraDestination}.");

			if (buildingAtCapture)
				layout.CapturedBuilding = w.CreateActor("powr", footprint).ActorID;

			w.Tick(5);
			return layout;
		}

		/// <summary>The orders both the control and the restored timelines are given, in the same order and on the same ticks.</summary>
		static void Script(Timeline t, Layout layout, bool buildingAtCapture)
		{
			if (buildingAtCapture)
				Sell(t, layout.CapturedBuilding.Value);
			else
				PlaceBuilding(t, layout, layout.Footprint);

			foreach (var unit in Units(t.World, layout))
				t.Issue(MoveTo(unit, layout.Destination));

			t.Run(ReplayTicks);
		}

		/// <summary>
		/// Starts a POWR on the player's building queue with the real order, waits for it to finish, and places it on
		/// <paramref name="cell"/> with the real PlaceBuilding order.
		/// </summary>
		static void PlaceBuilding(Timeline t, Layout layout, CPos cell)
		{
			var player = t.World.Actors.Single(a => a.ActorID == layout.Player);
			t.Issue(Order.StartProduction(player, "powr", 1));
			Assert.That(t.RunUntil(() => PowrDone(t.World, layout.Player), MaxBuildTicks), Is.True,
				"The POWR never finished building.");

			t.Issue(new Order("PlaceBuilding", player, Target.FromCell(t.World, cell), false)
			{
				TargetString = "powr",
				ExtraData = player.ActorID,
			});
			Assert.That(t.RunUntil(() => PowrAt(t.World, cell) != null, MaxPlaceTicks), Is.True,
				$"The PlaceBuilding order never put a POWR on {cell}.");
		}

		/// <summary>
		/// The first top-left cell, searching outward from <paramref name="near"/>, where the game's own placement rule
		/// (<c>BuildingUtils.CanPlaceBuilding</c>, in OpenRA.Mods.Common) accepts a POWR, and whose footprint avoids
		/// <paramref name="avoid"/>. Called by reflection because the rule lives in OpenRA.Mods.Common.
		/// </summary>
		static CPos ValidPlacement(World world, CPos near, IEnumerable<CPos> avoid)
		{
			var ai = world.Map.Rules.Actors["powr"];
			var bi = ai.TraitInfos<ITraitInfoInterface>().First(i => i.GetType().Name == "BuildingInfo");
			var buildingUtils = PathFinderOf(world).GetType().Assembly.GetType("OpenRA.Mods.Common.Traits.BuildingUtils");
			var canPlace = buildingUtils.GetMethod("CanPlaceBuilding");
			var taken = avoid.ToHashSet();

			for (var radius = 0; radius <= 10; radius++)
				foreach (var topLeft in Around(near, radius))
					if (!Rect(topLeft, 2, 2).Any(taken.Contains) && (bool)canPlace.Invoke(null, [world, topLeft, ai, bi, null]))
						return topLeft;

			throw new InvalidOperationException($"No POWR placement found within 10 cells of {near}.");
		}

		/// <summary>The cells at exactly Chebyshev distance <paramref name="radius"/> from <paramref name="center"/>.</summary>
		static IEnumerable<CPos> Around(CPos center, int radius)
		{
			if (radius == 0)
				return [center];

			return Enumerable.Range(-radius, 2 * radius + 1)
				.SelectMany(dy => Enumerable.Range(-radius, 2 * radius + 1)
					.Where(dx => Math.Max(Math.Abs(dx), Math.Abs(dy)) == radius)
					.Select(dx => center + new CVec(dx, dy)));
		}

		static Actor PowrAt(World world, CPos cell) =>
			world.Actors.FirstOrDefault(a => a.Info.Name == "powr" && !a.IsDead && a.Location == cell);

		/// <summary>
		/// Removes a building the way a sale does, by taking it out of the world. The real Sell order is not used: its
		/// refund text is built with the renderer, which a headless world does not have, and the sale ends in the same
		/// <see cref="World.Remove(Actor)"/> either way.
		/// </summary>
		static void Sell(Timeline t, uint buildingId)
		{
			t.World.Remove(t.World.Actors.Single(a => a.ActorID == buildingId));
		}

		static void AssertScriptLanded(World world, Layout layout, bool buildingAtCapture)
		{
			if (buildingAtCapture)
				Assert.That(world.Actors.Any(a => a.ActorID == layout.CapturedBuilding && !a.IsDead), Is.False,
					"Sanity: the sale in the script did not remove the building that existed at capture.");
			else
				Assert.That(world.Actors.Any(a => a.Info.Name == "powr" && !a.IsDead && a.Location == layout.Footprint), Is.True,
					"Sanity: the placement in the script did not put a POWR on its footprint.");
		}

		static Order MoveTo(Actor unit, CPos cell) => new("Move", unit, Target.FromCell(unit.World, cell), false);

		static IEnumerable<Actor> Units(World world, Layout layout) =>
			layout.Group.Select(id => world.Actors.Single(a => a.ActorID == id));

		/// <summary>True when a POWR in any of the player's production queues is finished and waits for placement.</summary>
		static bool PowrDone(World world, uint playerId)
		{
			var player = world.Actors.Single(a => a.ActorID == playerId);
			foreach (var queue in player.TraitsImplementing<object>().Where(t => t.GetType().Name.EndsWith("ProductionQueue", StringComparison.Ordinal)))
			{
				var allQueued = (IEnumerable)queue.GetType().GetMethods().First(m => m.Name == "AllQueued" && m.GetParameters().Length == 0)
					.Invoke(queue, null);

				foreach (var item in allQueued)
				{
					var type = item.GetType();
					if ((string)type.GetField("Item").GetValue(item) == "powr" && (bool)type.GetProperty("Done").GetValue(item))
						return true;
				}
			}

			return false;
		}

		static long LiveAfterGap(World world) => WorldForks.GameMsOf(world) + WorldForks.MinGapMs;

		/// <summary>
		/// The state the oracle compares: the sync hash, every actor with its cells and sub-cells, the map's per-cell
		/// layers, the ActorMap and BuildingInfluence and Mobile blocking near the footprints, and PathFinder results from
		/// each unit to the cells around the footprints.
		/// </summary>
		static List<string> Fingerprint(World world, Layout layout)
		{
			var lines = new List<string> { $"tick {world.WorldTick} sync {world.SyncHash()}" };

			foreach (var a in world.Actors.OrderBy(a => a.ActorID))
			{
				var cells = a.OccupiesSpace?.OccupiedCells().Select(c => $"{c.Cell}/{c.SubCell}") ?? [];
				lines.Add($"actor {a.ActorID} {a.Info.Name} owner={a.Owner?.InternalName} inWorld={a.IsInWorld} dead={a.IsDead} " +
					$"loc={a.OccupiesSpace?.TopLeft} center={a.OccupiesSpace?.CenterPosition} occupies=[{string.Join(" ", cells)}]");
			}

			var units = layout.Group.Select(id => world.Actors.FirstOrDefault(a => a.ActorID == id)).Where(a => a != null && !a.IsDead).ToArray();
			var buildingInfluence = BuildingInfluenceOf(world);

			foreach (var cell in Region(layout, world.Map))
			{
				var map = world.Map;
				var tile = map.Tiles[cell];
				var resource = map.Resources[cell];
				lines.Add($"cell {cell} custom={map.CustomTerrain[cell]} tile={tile.Type}/{tile.Index} resource={resource.Type}/{resource.Index} ramp={map.Ramp[cell]}");

				var full = string.Join(",", world.ActorMap.GetActorsAt(cell, SubCell.FullCell).Select(a => a.ActorID));
				var first = string.Join(",", world.ActorMap.GetActorsAt(cell, SubCell.First).Select(a => a.ActorID));
				var enter = string.Join("", units.Select(u => CanEnterCell(u, cell) ? '1' : '0'));
				lines.Add($"cell {cell} any={world.ActorMap.AnyActorsAt(cell)} full=[{full}] first=[{first}] " +
					$"building={AnyBuildingAt(buildingInfluence, cell)} enter={enter}");
			}

			foreach (var unit in units)
				foreach (var target in PathTargets(layout))
					lines.Add($"path {unit.ActorID} {unit.Location} -> {target}: {string.Join(" ", PathBetween(world, unit, unit.Location, target))}");

			return lines;
		}

		/// <summary>The rectangle around every anchor the script and residue use, with a margin, clipped to the map.</summary>
		static IEnumerable<CPos> Region(Layout layout, Map map)
		{
			var anchors = new[] { layout.GroupStart, layout.Footprint, layout.Destination, layout.ExtraFootprint, layout.ExtraDestination };
			var minX = anchors.Min(c => c.X) - 2;
			var minY = anchors.Min(c => c.Y) - 2;
			var maxX = anchors.Max(c => c.X) + 4;
			var maxY = anchors.Max(c => c.Y) + 4;

			for (var y = minY; y <= maxY; y++)
				for (var x = minX; x <= maxX; x++)
					if (map.Contains(new CPos(x, y)))
						yield return new CPos(x, y);
		}

		/// <summary>The destination cells and the ring of cells around each footprint, for the path checks.</summary>
		static IEnumerable<CPos> PathTargets(Layout layout)
		{
			return Rect(layout.Destination, 2, 2)
				.Concat(Rect(layout.ExtraDestination, 2, 2))
				.Concat(Ring(layout.Footprint))
				.Concat(Ring(layout.ExtraFootprint));
		}

		static IEnumerable<CPos> Rect(CPos topLeft, int width, int height)
		{
			for (var dy = 0; dy < height; dy++)
				for (var dx = 0; dx < width; dx++)
					yield return topLeft + new CVec(dx, dy);
		}

		/// <summary>The cells one step outside a 2x2 footprint.</summary>
		static IEnumerable<CPos> Ring(CPos topLeft)
		{
			for (var dy = -1; dy <= 2; dy++)
				for (var dx = -1; dx <= 2; dx++)
					if (dx < 0 || dx > 1 || dy < 0 || dy > 1)
						yield return topLeft + new CVec(dx, dy);
		}

		/// <summary>
		/// True when the Locomotor that answers the units' <c>Mobile.CanEnterCell</c> is the given world's own. Each unit's
		/// <c>Mobile.Locomotor</c> must be the world's Locomotor, and the shared <c>MobileInfo</c> must not cache a Locomotor
		/// from another world. That cache is read by reflection, because <c>Mobile</c> lives in OpenRA.Mods.Common; it is
		/// absent once the cache is removed, which passes.
		/// </summary>
		static bool CanEnterCellUsesLocomotorOf(World world, Layout layout)
		{
			const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
			var expected = world.WorldActor.TraitsImplementing<object>().First(t => t.GetType().Name == "Locomotor");

			return Units(world, layout).All(unit =>
			{
				var mobile = unit.TraitsImplementing<IOccupySpace>().First(t => t.GetType().Name == "Mobile");
				var info = mobile.GetType().GetField("Info", Any)?.GetValue(mobile) ?? mobile.GetType().GetProperty("Info", Any)?.GetValue(mobile);
				var own = mobile.GetType().GetProperty("Locomotor", Any)?.GetValue(mobile);
				var cached = info?.GetType().GetField("locomotor", Any)?.GetValue(info);
				return ReferenceEquals(own, expected) && (cached == null || ReferenceEquals(cached, expected));
			});
		}

		/// <summary>
		/// Asserts the two fingerprints are identical, reporting the number of differing lines and the first one.
		/// </summary>
		static void AssertSame(string what, IList<string> expected, IList<string> actual)
		{
			var differing = Enumerable.Range(0, Math.Max(expected.Count, actual.Count))
				.Where(i => i >= expected.Count || i >= actual.Count || expected[i] != actual[i])
				.ToList();

			if (differing.Count == 0)
				return;

			var first = differing[0];
			Assert.Fail($"{what}: {differing.Count} line(s) differ. First at line {first}:\n" +
				$"  control : {(first < expected.Count ? expected[first] : "<none>")}\n" +
				$"  restored: {(first < actual.Count ? actual[first] : "<none>")}");
		}

		/// <summary>Asserts the restored timeline advanced through the same world ticks with the same sync hashes as the control.</summary>
		static void AssertTimelineSame(string what, Timeline expected, Timeline actual)
		{
			var count = Math.Min(expected.Hashes.Count, actual.Hashes.Count);
			for (var i = 0; i < count; i++)
			{
				if (expected.Hashes[i] == actual.Hashes[i])
					continue;

				Assert.Fail($"{what} diverged at replay step {i}: control tick {expected.Hashes[i].WorldTick} " +
					$"sync {expected.Hashes[i].SyncHash}, restored tick {actual.Hashes[i].WorldTick} sync {actual.Hashes[i].SyncHash}.");
			}

			Assert.That(actual.Hashes.Count, Is.EqualTo(expected.Hashes.Count), $"{what} advanced a different number of ticks.");
		}

		static object PathFinderOf(World world) => world.WorldActor.TraitsImplementing<object>().First(t => t.GetType().Name == "PathFinder");

		/// <summary>The path the probe's locomotor takes from source to target, through <c>PathFinder.FindPathToTargetCell</c>.</summary>
		static List<CPos> PathBetween(World world, Actor probe, CPos source, CPos target)
		{
			var pathFinder = PathFinderOf(world);
			var blockedByActor = pathFinder.GetType().Assembly.GetType("OpenRA.Mods.Common.Traits.BlockedByActor");
			var all = Enum.Parse(blockedByActor, "All");
			var method = pathFinder.GetType().GetMethods().Single(m => m.Name == "FindPathToTargetCell" && m.GetParameters().Length == 7);
			return (List<CPos>)method.Invoke(pathFinder, [probe, new[] { source }, target, all, null, null, true]);
		}

		/// <summary>
		/// <c>Mobile.CanEnterCell</c> lives in OpenRA.Mods.Common, which this project cannot reference, so it is
		/// called by reflection. The optional <c>check</c> is left to its default (<c>BlockedByActor.All</c>).
		/// </summary>
		static bool CanEnterCell(Actor actor, CPos cell)
		{
			var mobile = actor.TraitsImplementing<IOccupySpace>().First(t => t.GetType().Name == "Mobile");
			var method = mobile.GetType().GetMethods().First(m => m.Name == "CanEnterCell" && m.GetParameters().Length == 3);
			return (bool)method.Invoke(mobile, [cell, null, Type.Missing]);
		}

		static object BuildingInfluenceOf(World world) =>
			world.WorldActor.TraitsImplementing<object>().First(t => t.GetType().Name == "BuildingInfluence");

		static bool AnyBuildingAt(object buildingInfluence, CPos cell) =>
			(bool)buildingInfluence.GetType().GetMethod("AnyBuildingAt").Invoke(buildingInfluence, [cell]);

		/// <summary>The ids and cells the script needs, all taken from the parent world at setup.</summary>
		sealed class Layout
		{
			public uint Player;
			public uint[] Group = [];
			public uint? CapturedBuilding;
			public CPos GroupStart;
			public CPos Footprint;
			public CPos Destination;
			public CPos ExtraFootprint;
			public CPos ExtraDestination;
		}

		/// <summary>
		/// Drives one timeline through the canonical tick loop against an order manager, so the timeline follows a restore
		/// (the manager's World changes). Records the sync hash of every tick it advances.
		/// </summary>
		sealed class Timeline
		{
			public readonly OrderManager OrderManager;
			public readonly List<(int WorldTick, int SyncHash)> Hashes = [];

			public Timeline(OrderManager orderManager)
			{
				OrderManager = orderManager;
			}

			public World World => OrderManager.World;

			public void Issue(Order order) => OrderManager.IssueOrder(order);

			public void Run(int ticks)
			{
				for (var i = 0; i < ticks; i++)
					Step();
			}

			public bool RunUntil(Func<bool> condition, int maxTicks)
			{
				for (var i = 0; i < maxTicks; i++)
				{
					Step();
					if (condition())
						return true;
				}

				return false;
			}

			void Step()
			{
				OrderManager.TickImmediate();
				if (!OrderManager.TryTick())
					return;

				World.Tick();
				Hashes.Add((World.WorldTick, World.SyncHash()));
			}
		}
	}
}
