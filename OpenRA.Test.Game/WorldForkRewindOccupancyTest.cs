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
	/// Occupancy and shroud across more than one rewind. A rewind adopts a fresh clone of a snapshot, so the
	/// timeline that was live before the rewind (a restored fork) and the next rewind's clone come from the same
	/// snapshot. Any per-world state that a restored fork mutates through an object the snapshot shares would leak
	/// into the next rewind. These checks mutate the first restored fork, rewind again, and compare the second fork
	/// against the captured state.
	/// <para>
	/// They pass on the current tree, so they are guards for the rewind path and not a repro of the reported bugs.
	/// The reported bugs are not reproduced headlessly; see docs/10-research.md.
	/// </para>
	/// </summary>
	[TestFixture]
	sealed class WorldForkRewindOccupancyTest
	{
		const int Seed = 0xC0FFEE;
		const int TicksBeforeCapture = 20;
		const int TicksInFirstFork = 10;
		const int SettleTicks = 150;

		// Long enough that the newer snapshot is at least the 5 s gap past the older one's tick, so redo can reach it.
		const int TicksToNewerSnapshot = 300;

		/// <summary>Occupancy and pathing traits of the world actor, found by name because they live in OpenRA.Mods.Common.</summary>
		static readonly string[] OccupancyTraitNames = ["Locomotor", "PathFinder", "ActorMap", "BuildingInfluence", "HierarchicalPathFinderOverlay"];

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

		/// <summary>A live clock 5 s past the given world, so the snapshot taken before it is in range for undo.</summary>
		static long LiveAfterGap(World world) => WorldForks.GameMsOf(world) + WorldForks.MinGapMs;

		[TestCase(TestName = "A second rewind frees the cells a building occupied in the first restored timeline")]
		public void SecondRewindFreesCellsOfBuildingPlacedInFirstRestore()
		{
			using var parent = HeadlessWorld.Create(Seed);
			var probe = parent.CreateActor("e1", parent.FindClearArea(parent.LocalPlayer.HomeLocation, 1, 1));
			var probeId = probe.ActorID;
			var footprintTopLeft = parent.FindClearArea(parent.LocalPlayer.HomeLocation + new CVec(3, 0), 2, 1);
			parent.Tick(TicksBeforeCapture);

			var orderManager = parent.OrderManager;
			Assert.That(WorldForks.Capture(orderManager), Is.True, "The capture was refused.");

			// First rewind: the live game is now a fork. It places a building on the captured clear cells.
			Assert.That(WorldForks.Undo(orderManager, null, LiveAfterGap(parent.World), out var firstFailure), Is.True, firstFailure);
			var firstFork = orderManager.World;
			var building = firstFork.CreateActor("powr", [new OwnerInit(firstFork.LocalPlayer), new LocationInit(footprintTopLeft)]);
			TickThroughOrderManager(orderManager, TicksInFirstFork);

			var footprint = building.OccupiesSpace.OccupiedCells().Select(c => c.Cell).ToArray();
			Assert.That(footprint, Is.Not.Empty, "The building has no footprint to check.");
			foreach (var cell in footprint)
				Assert.That(firstFork.ActorMap.AnyActorsAt(cell), Is.True,
					$"Sanity: the first fork's building does not block {cell}.");

			// Second rewind to the same snapshot: the adopted clone must not carry the vanished building.
			Assert.That(WorldForks.Undo(orderManager, null, LiveAfterGap(firstFork), out var secondFailure), Is.True, secondFailure);
			var secondFork = orderManager.World;
			var probeInSecondFork = secondFork.Actors.Single(a => a.ActorID == probeId);
			var buildingInfluence = BuildingInfluenceOf(secondFork);

			foreach (var cell in footprint)
			{
				Assert.That(secondFork.ActorMap.AnyActorsAt(cell), Is.False,
					$"Second rewind's ActorMap still holds an actor at {cell}, a cell the first fork's building occupied.");
				Assert.That(AnyBuildingAt(buildingInfluence, cell), Is.False,
					$"Second rewind's BuildingInfluence still reports a building at {cell}, a cell the first fork's building occupied.");
				Assert.That(CanEnterCell(probeInSecondFork, cell), Is.True,
					$"Second rewind's Mobile.CanEnterCell says {cell} is blocked, a cell the first fork's building occupied.");
			}
		}

		[TestCase(TestName = "A group ordered to one spot after a second rewind spreads across distinct sub-cells, as an unrewound group does")]
		public void SecondRewindGroupMoveSpreadsLikeUnrewoundGroup()
		{
			using var control = HeadlessWorld.Create(Seed);
			var controlUnits = SetUpGroup(control);
			var destination = DestinationFor(control);
			foreach (var unit in controlUnits)
				control.IssueMoveOrder(unit, destination);
			control.Tick(SettleTicks);

			var controlSpots = controlUnits.Select(u => u.OccupiesSpace.OccupiedCells().First()).Distinct().Count();
			Assert.That(controlSpots, Is.EqualTo(controlUnits.Length),
				"Control: an unrewound group did not spread, so this test cannot tell a stacking bug apart from the engine.");

			using var parent = HeadlessWorld.Create(Seed);
			var unitIds = SetUpGroup(parent).Select(u => u.ActorID).ToArray();
			var destinationInParent = DestinationFor(parent);
			parent.Tick(TicksBeforeCapture);

			var orderManager = parent.OrderManager;
			Assert.That(WorldForks.Capture(orderManager), Is.True, "The capture was refused.");

			// First rewind: the first fork orders the group to the destination, as a player would before rewinding again.
			Assert.That(WorldForks.Undo(orderManager, null, LiveAfterGap(parent.World), out var firstFailure), Is.True, firstFailure);
			var firstFork = orderManager.World;
			foreach (var id in unitIds)
				IssueMove(orderManager, firstFork.Actors.Single(a => a.ActorID == id), destinationInParent);
			TickThroughOrderManager(orderManager, TicksInFirstFork);

			// Second rewind: the group is ordered to the same spot again in the adopted clone.
			Assert.That(WorldForks.Undo(orderManager, null, LiveAfterGap(firstFork), out var secondFailure), Is.True, secondFailure);
			var secondFork = orderManager.World;
			var units = unitIds.Select(id => secondFork.Actors.Single(a => a.ActorID == id)).ToArray();
			foreach (var unit in units)
				IssueMove(orderManager, unit, destinationInParent);
			TickThroughOrderManager(orderManager, SettleTicks);

			var spots = units.Select(u => u.OccupiesSpace.OccupiedCells().First()).Distinct().Count();
			Assert.That(spots, Is.EqualTo(units.Length),
				$"After a second rewind the group stacked: {units.Length} units share {spots} distinct spot(s) at the destination.");

			foreach (var unit in units)
				Assert.That(PathBetween(secondFork, unit, unit.Location, destinationInParent), Is.Not.Empty,
					$"After a second rewind no path leads from {unit.Location} to the group's destination.");
		}

		[TestCase(TestName = "A rewound world's shroud keeps the cells it had explored at capture, after the outgoing timeline explored more")]
		public void RewoundShroudKeepsCapturedExploredCells()
		{
			using var parent = HeadlessWorld.Create(Seed);
			var probe = parent.CreateActor("e1", parent.FindClearArea(parent.LocalPlayer.HomeLocation, 1, 1));
			var farCell = parent.FindClearArea(parent.LocalPlayer.HomeLocation + new CVec(0, 14), 1, 1);
			parent.Tick(TicksBeforeCapture);

			var orderManager = parent.OrderManager;
			var capturedExplored = ExploredCells(parent.LocalPlayer.Shroud, parent.World.Map);
			Assert.That(capturedExplored, Is.Not.Empty, "The shroud explored nothing at capture, so the check below would prove nothing.");
			Assert.That(WorldForks.Capture(orderManager), Is.True, "The capture was refused.");

			// The outgoing timeline walks the probe away, exploring cells the capture never saw.
			parent.IssueMoveOrder(probe, farCell);
			parent.Tick(SettleTicks);
			Assert.That(ExploredCells(parent.LocalPlayer.Shroud, parent.World.Map), Is.Not.EquivalentTo(capturedExplored),
				"The outgoing timeline did not explore anything new, so the restore below would prove nothing.");

			Assert.That(WorldForks.Undo(orderManager, null, LiveAfterGap(parent.World), out var restoreFailure), Is.True, restoreFailure);
			var restored = orderManager.World;
			Assert.That(ExploredCells(restored.LocalPlayer.Shroud, restored.Map), Is.EquivalentTo(capturedExplored),
				"The restored shroud does not hold the explored cells it had at capture.");
		}

		[TestCase(TestName = "Every World and Map an occupancy structure holds in a rewound world is that rewound world's, not the outgoing one's")]
		public void RewoundOccupancyStructuresReferenceTheRewoundWorld()
		{
			using var parent = HeadlessWorld.Create(Seed);
			parent.Tick(TicksBeforeCapture);

			var orderManager = parent.OrderManager;
			Assert.That(WorldForks.Capture(orderManager), Is.True, "The capture was refused.");
			parent.Tick(TicksInFirstFork);

			Assert.That(WorldForks.Undo(orderManager, null, LiveAfterGap(parent.World), out var restoreFailure), Is.True, restoreFailure);
			var restored = orderManager.World;
			var outgoing = parent.World;

			var references = new List<(string Path, bool IsOutgoing)>();
			foreach (var trait in restored.WorldActor.TraitsImplementing<object>()
				.Where(t => OccupancyTraitNames.Contains(t.GetType().Name)))
				CollectWorldReferences(trait, outgoing, trait.GetType().Name, references, 0);

			// Positive control: the walk must reach the restored world's own references, or an empty result proves nothing.
			Assert.That(references.Count(r => !r.IsOutgoing), Is.GreaterThan(0),
				"The walk found no World or Map references in the restored occupancy structures, so it proves nothing.");
			Assert.That(references.Where(r => r.IsOutgoing).Select(r => r.Path), Is.Empty,
				"Occupancy state in the restored world still points at the outgoing world.");
		}

		[TestCase(TestName = "A path across a building placed after the capture, rewound away and placed again, matches a world that never had it")]
		public void PathAcrossRewoundFootprintMatchesWorldWithoutBuilding()
		{
			using var control = HeadlessWorld.Create(Seed);
			var (controlProbe, controlSource, controlDestination, controlFootprint) = PathFixture(control);
			control.Tick(TicksBeforeCapture);
			var controlPath = PathBetween(control.World, controlProbe, controlSource, controlDestination);
			Assert.That(controlPath, Is.Not.Empty, "Control: no path exists in an empty world, so the check below proves nothing.");
			Assert.That(controlPath.Intersect(FootprintCells(controlFootprint)).Any(), Is.True,
				"Control: the empty-world path does not cross the footprint, so the check below proves nothing.");

			using var parent = HeadlessWorld.Create(Seed);
			var (probe, source, destination, footprint) = PathFixture(parent);
			var probeId = probe.ActorID;
			parent.Tick(TicksBeforeCapture);

			var orderManager = parent.OrderManager;
			Assert.That(WorldForks.Capture(orderManager), Is.True, "The capture was refused.");

			Assert.That(WorldForks.Undo(orderManager, null, LiveAfterGap(parent.World), out var firstFailure), Is.True, firstFailure);
			var firstFork = orderManager.World;
			PlaceBuilding(firstFork, footprint);
			TickThroughOrderManager(orderManager, TicksInFirstFork);

			Assert.That(WorldForks.Undo(orderManager, null, LiveAfterGap(firstFork), out var secondFailure), Is.True, secondFailure);
			var secondFork = orderManager.World;
			var probeInSecondFork = secondFork.Actors.Single(a => a.ActorID == probeId);
			var restoredPath = PathBetween(secondFork, probeInSecondFork, source, destination);

			Assert.That(restoredPath, Is.EqualTo(controlPath),
				"After a second rewind the path across the freed footprint differs from a world that never had the building.");
		}

		[TestCase(TestName = "A path around a building that existed at capture and was removed afterwards routes around it again after a rewind")]
		public void PathAroundBuildingRemovedAfterCaptureMatchesControl()
		{
			using var control = HeadlessWorld.Create(Seed);
			var (controlProbe, controlSource, controlDestination, controlFootprint) = PathFixture(control);
			PlaceBuilding(control.World, controlFootprint);
			control.Tick(TicksBeforeCapture);
			var controlPath = PathBetween(control.World, controlProbe, controlSource, controlDestination);
			Assert.That(controlPath, Is.Not.Empty, "Control: no path exists around the building, so the check below proves nothing.");

			using var parent = HeadlessWorld.Create(Seed);
			var (probe, source, destination, footprint) = PathFixture(parent);
			var probeId = probe.ActorID;
			var building = PlaceBuilding(parent.World, footprint);
			var buildingId = building.ActorID;
			parent.Tick(TicksBeforeCapture);

			var orderManager = parent.OrderManager;
			Assert.That(WorldForks.Capture(orderManager), Is.True, "The capture was refused.");

			// The outgoing timeline removes the building the capture held.
			parent.World.Remove(building);
			parent.Tick(TicksInFirstFork);
			Assert.That(parent.World.Actors.Any(a => a.ActorID == buildingId), Is.False,
				"Sanity: the outgoing timeline did not remove the building.");

			Assert.That(WorldForks.Undo(orderManager, null, LiveAfterGap(parent.World), out var restoreFailure), Is.True, restoreFailure);
			var restored = orderManager.World;
			Assert.That(restored.Actors.Any(a => a.ActorID == buildingId), Is.True,
				"Sanity: the restored world does not hold the building the capture held.");

			var probeInRestored = restored.Actors.Single(a => a.ActorID == probeId);
			var restoredPath = PathBetween(restored, probeInRestored, source, destination);
			Assert.That(restoredPath, Is.EqualTo(controlPath),
				"The restored path does not route around the building that the capture held.");
		}

		[TestCase(TestName = "Redo after undo restores the newer snapshot's path and drops a building placed in between")]
		public void RedoAfterUndoRestoresNewerSnapshotPath()
		{
			using var control = HeadlessWorld.Create(Seed);
			var (controlProbe, controlSource, controlDestination, controlFootprint) = PathFixture(control);
			control.Tick(TicksBeforeCapture);
			var controlPath = PathBetween(control.World, controlProbe, controlSource, controlDestination);
			Assert.That(controlPath, Is.Not.Empty, "Control: no path exists in an empty world, so the check below proves nothing.");

			using var parent = HeadlessWorld.Create(Seed);
			var (probe, source, destination, footprint) = PathFixture(parent);
			var probeId = probe.ActorID;
			parent.Tick(TicksBeforeCapture);

			var orderManager = parent.OrderManager;
			Assert.That(WorldForks.Capture(orderManager), Is.True, "The first capture was refused.");
			parent.Tick(TicksToNewerSnapshot);
			Assert.That(WorldForks.Capture(orderManager), Is.True, "The second capture was refused.");
			var newerTick = parent.World.WorldTick;

			// Undo from the live clock lands on the older snapshot. The newer one is kept for redo.
			Assert.That(WorldForks.Undo(orderManager, null, WorldForks.GameMsOf(parent.World), out var undoFailure), Is.True, undoFailure);
			var firstFork = orderManager.World;
			Assert.That(firstFork.WorldTick, Is.EqualTo(TicksBeforeCapture), "Sanity: undo did not land on the older snapshot.");
			PlaceBuilding(firstFork, footprint);
			TickThroughOrderManager(orderManager, TicksInFirstFork);

			Assert.That(WorldForks.Redo(orderManager, null, WorldForks.GameMsOf(firstFork), out var redoFailure), Is.True, redoFailure);
			var redone = orderManager.World;
			Assert.That(redone.WorldTick, Is.EqualTo(newerTick), "Redo did not land on the newer snapshot.");

			var probeInRedone = redone.Actors.Single(a => a.ActorID == probeId);
			Assert.That(redone.ActorMap.AnyActorsAt(footprint), Is.False,
				"The building placed in the undone timeline still blocks its footprint after redo.");
			Assert.That(PathBetween(redone, probeInRedone, source, destination), Is.EqualTo(controlPath),
				"The redone path differs from a world that never had the building.");
		}

		/// <summary>
		/// The probe and the cells a path runs between across a 2x1 footprint, placed the same way in every world
		/// so that the control and the rewound worlds can be compared cell for cell.
		/// </summary>
		static (Actor Probe, CPos Source, CPos Destination, CPos Footprint) PathFixture(HeadlessWorld w)
		{
			var probe = w.CreateActor("e1", w.FindClearArea(w.LocalPlayer.HomeLocation, 1, 1));
			var footprint = w.FindClearArea(w.LocalPlayer.HomeLocation + new CVec(3, 0), 2, 1);
			var destination = w.FindClearArea(footprint + new CVec(4, 0), 1, 1);
			return (probe, probe.Location, destination, footprint);
		}

		/// <summary>The two cells of the 2x1 footprint that <see cref="PathFixture"/> returns the top-left of.</summary>
		static CPos[] FootprintCells(CPos topLeft) => [topLeft, topLeft + new CVec(1, 0)];

		static Actor PlaceBuilding(World world, CPos footprint)
		{
			return world.CreateActor("powr", [new OwnerInit(world.LocalPlayer), new LocationInit(footprint)]);
		}

		/// <summary>The world's <c>PathFinder</c> trait, found by name because it lives in OpenRA.Mods.Common.</summary>
		static object PathFinderOf(World world) => world.WorldActor.TraitsImplementing<object>().First(t => t.GetType().Name == "PathFinder");

		/// <summary>
		/// The path the probe's locomotor takes from source to target, through <c>PathFinder.FindPathToTargetCell</c>
		/// with <c>BlockedByActor.All</c>. Empty when no path exists. Called by reflection because the
		/// <c>PathFinder</c> and <c>BlockedByActor</c> types live in OpenRA.Mods.Common.
		/// </summary>
		static List<CPos> PathBetween(World world, Actor probe, CPos source, CPos target)
		{
			var pathFinder = PathFinderOf(world);
			var blockedByActor = pathFinder.GetType().Assembly.GetType("OpenRA.Mods.Common.Traits.BlockedByActor");
			var all = Enum.Parse(blockedByActor, "All");
			var method = pathFinder.GetType().GetMethods().Single(m => m.Name == "FindPathToTargetCell" && m.GetParameters().Length == 7);
			return (List<CPos>)method.Invoke(pathFinder, [probe, new[] { source }, target, all, null, null, true]);
		}

		/// <summary>
		/// Walks the fields of an occupancy object (and the OpenRA.Mods.Common objects it holds, to a shallow depth)
		/// and records each <see cref="World"/> or <see cref="Map"/> reference, marking whether it is the outgoing world's.
		/// </summary>
		static void CollectWorldReferences(object value, World outgoing, string path, List<(string Path, bool IsOutgoing)> references, int depth)
		{
			if (value == null || depth > 3)
				return;

			foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
			{
				var fieldValue = field.GetValue(value);
				var fieldPath = $"{path}.{field.Name}";

				if (fieldValue is World world)
					references.Add((fieldPath, ReferenceEquals(world, outgoing)));
				else if (fieldValue is Map map)
					references.Add((fieldPath, ReferenceEquals(map, outgoing.Map)));
				else if (fieldValue is IDictionary dictionary)
				{
					foreach (DictionaryEntry entry in dictionary)
						CollectWorldReferences(entry.Value, outgoing, $"{fieldPath}[{entry.Key}]", references, depth + 1);
				}
				else if (fieldValue != null && (fieldValue.GetType().Namespace ?? "").StartsWith("OpenRA.Mods.Common", StringComparison.Ordinal))
					CollectWorldReferences(fieldValue, outgoing, fieldPath, references, depth + 1);
			}
		}

		static HashSet<PPos> ExploredCells(Shroud shroud, Map map)
		{
			return map.ProjectedCells
				.Where(puv => shroud.GetVisibility(puv).HasFlag(Shroud.CellVisibility.Explored))
				.ToHashSet();
		}

		/// <summary>Three infantry in a clear row on the local player's home, with a settle so each is placed.</summary>
		static Actor[] SetUpGroup(HeadlessWorld w)
		{
			var start = w.FindClearArea(w.LocalPlayer.HomeLocation, 3, 1);
			var units = Enumerable.Range(0, 3).Select(i => w.CreateActor("e1", start + new CVec(i, 0))).ToArray();
			w.Tick(5);
			return units;
		}

		static CPos DestinationFor(HeadlessWorld w) => w.FindClearArea(w.LocalPlayer.HomeLocation + new CVec(0, 6), 2, 2);

		static void IssueMove(OrderManager orderManager, Actor actor, CPos cell)
		{
			orderManager.IssueOrder(new Order("Move", actor, Target.FromCell(actor.World, cell), false));
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

		/// <summary>The world's <c>BuildingInfluence</c> trait, found by name because it lives in OpenRA.Mods.Common.</summary>
		static object BuildingInfluenceOf(World world)
		{
			return world.WorldActor.TraitsImplementing<object>().First(t => t.GetType().Name == "BuildingInfluence");
		}

		static bool AnyBuildingAt(object buildingInfluence, CPos cell)
		{
			return (bool)buildingInfluence.GetType().GetMethod("AnyBuildingAt").Invoke(buildingInfluence, [cell]);
		}

		/// <summary>
		/// The canonical tick loop body against an order manager, mirroring <see cref="HeadlessWorld.TickOnce"/>
		/// without the wrapper, which caches the world it was built around and so cannot follow a restore.
		/// </summary>
		static void TickThroughOrderManager(OrderManager orderManager, int ticks)
		{
			for (var i = 0; i < ticks; i++)
			{
				orderManager.TickImmediate();
				if (!orderManager.TryTick())
					continue;

				orderManager.World.Tick();
			}
		}
	}
}
