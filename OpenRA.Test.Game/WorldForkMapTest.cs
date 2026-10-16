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
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Test
{
	/// <summary>
	/// Terrain written in a restored world must reach that world's own map layers and pathing caches, and
	/// nothing the outgoing world wrote after the capture may leak into the restored one. The map layers and
	/// their change subscribers (the Locomotor's cost cache, the HierarchicalPathFinder) are only correct for
	/// a fork when the fork has its own <see cref="Map"/>; a shared map routes the fork's terrain writes to the
	/// outgoing world's subscribers.
	/// </summary>
	[TestFixture]
	sealed class WorldForkMapTest
	{
		const int Seed = 0xC0FFEE;
		const int TicksBeforeCapture = 20;

		/// <summary>The Locomotor cost of an impassable cell (IPathGraph.cs:41).</summary>
		const short UnreachableCost = short.MaxValue;

		/// <summary>A live clock 5 s past the parent's current tick, so a snapshot taken at that tick is in range for undo.</summary>
		static long LiveAfterGap(HeadlessWorld parent) => WorldForks.GameMsOf(parent.World) + WorldForks.MinGapMs;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		/// <summary>The window is process-global; isolate every test from whatever a previous one left.</summary>
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

		[TestCase(TestName = "A restored world has its own map layers, unaffected by terrain written in the outgoing world after the capture")]
		public void RestoredMapLayersAreNotShared()
		{
			using var parent = HeadlessWorld.Create(Seed);
			parent.Tick(TicksBeforeCapture);

			var cell = parent.FindClearArea(parent.LocalPlayer.HomeLocation, 1, 1);
			var water = parent.World.Map.Rules.TerrainInfo.GetTerrainIndex("Water");
			var capturedValue = parent.World.Map.CustomTerrain[cell];

			Assert.That(WorldForks.Capture(parent.OrderManager), Is.True, "The capture was refused.");

			parent.World.Map.CustomTerrain[cell] = water;
			Assert.That(WorldForks.Undo(parent.OrderManager, null, LiveAfterGap(parent), out var restoreFailure), Is.True, restoreFailure);

			var restored = parent.OrderManager.World;
			Assert.That(restored.Map, Is.Not.SameAs(parent.World.Map),
				"The restored world shares its Map with the outgoing world, so the outgoing world's terrain writes " +
				"and change subscribers belong to the restored world too.");
			Assert.That(restored.Map.CustomTerrain[cell], Is.EqualTo(capturedValue),
				"The restored world's CustomTerrain carries a write made in the outgoing world after the capture.");
		}

		[TestCase(TestName = "A restored foot locomotor prices a cell by terrain written in the restored world")]
		public void RestoredLocomotorFollowsTerrainWrittenInRestoredWorld()
		{
			using var parent = HeadlessWorld.Create(Seed);
			parent.Tick(TicksBeforeCapture);

			var cell = parent.FindClearArea(parent.LocalPlayer.HomeLocation, 1, 1);
			var water = parent.World.Map.Rules.TerrainInfo.GetTerrainIndex("Water");

			Assert.That(WorldForks.Capture(parent.OrderManager), Is.True, "The capture was refused.");
			Assert.That(WorldForks.Undo(parent.OrderManager, null, LiveAfterGap(parent), out var restoreFailure), Is.True, restoreFailure);

			var restored = parent.OrderManager.World;
			var foot = FootLocomotorOf(restored);
			Assert.That(MovementCostOf(foot, cell), Is.Not.EqualTo(UnreachableCost),
				"Precondition: a clear cell must be passable for foot infantry.");

			// Water is not in the foot locomotor's TerrainSpeeds, so it is impassable.
			restored.Map.CustomTerrain[cell] = water;

			Assert.That(MovementCostOf(foot, cell), Is.EqualTo(UnreachableCost),
				"The restored foot locomotor still prices the cell at its captured cost, because its terrain " +
				"subscription belongs to the outgoing world and never sees the restored world's write.");
		}

		[TestCase(TestName = "A restored world's map terrain subscribers are the restored world's own traits")]
		public void RestoredMapSubscribersBelongToRestoredWorld()
		{
			using var parent = HeadlessWorld.Create(Seed);
			parent.Tick(TicksBeforeCapture);

			var outgoingTraits = new HashSet<object>(parent.World.WorldActor.TraitsImplementing<IWorldLoaded>(), ReferenceEqualityComparer.Instance);

			Assert.That(WorldForks.Capture(parent.OrderManager), Is.True, "The capture was refused.");
			Assert.That(WorldForks.Undo(parent.OrderManager, null, LiveAfterGap(parent), out var restoreFailure), Is.True, restoreFailure);

			var restored = parent.OrderManager.World;
			var restoredTraits = new HashSet<object>(restored.WorldActor.TraitsImplementing<IWorldLoaded>(), ReferenceEqualityComparer.Instance);

			var subscribers = SubscriberTargets(restored.Map.Tiles)
				.Concat(SubscriberTargets(restored.Map.CustomTerrain))
				.Concat(SubscriberTargets(restored.Map.Height))
				.ToList();

			Assert.That(subscribers.Where(outgoingTraits.Contains), Is.Empty,
				"The restored Map still delivers terrain changes to traits of the outgoing world.");
			Assert.That(subscribers.Count(restoredTraits.Contains), Is.GreaterThan(0),
				"No terrain subscriber of the restored Map is a trait of the restored world.");
		}

		[TestCase(TestName = "A restored world's shared terrain sprite layer positions cells from the restored map, not the outgoing one")]
		public void SharedTerrainSpriteLayerPositionsFromRestoredMap()
		{
			using var parent = HeadlessWorld.Create(Seed);
			parent.Tick(TicksBeforeCapture);

			// The layer needs a graphics device, which the headless host does not have, so the layer and its
			// WorldRenderer are created without running their constructors. Only the state that cell positioning
			// reads is set, and the renderer's World is re-pointed the way WorldRenderer.AdoptWorld does on restore.
			var renderer = (WorldRenderer)RuntimeHelpers.GetUninitializedObject(typeof(WorldRenderer));
			typeof(WorldRenderer).GetProperty(nameof(WorldRenderer.World)).SetValue(renderer, parent.World);
			var layer = (TerrainSpriteLayer)RuntimeHelpers.GetUninitializedObject(typeof(TerrainSpriteLayer));
			typeof(TerrainSpriteLayer).GetField("worldRenderer", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(layer, renderer);

			var cell = parent.World.Map.AllCells.First(parent.World.Map.CustomTerrain.Contains);
			var water = parent.World.Map.Rules.TerrainInfo.GetTerrainIndex("Water");
			Assert.That(WorldForks.Capture(parent.OrderManager), Is.True, "The capture was refused.");
			Assert.That(WorldForks.Undo(parent.OrderManager, null, LiveAfterGap(parent), out var restoreFailure), Is.True, restoreFailure);

			var restored = parent.OrderManager.World;
			typeof(WorldRenderer).GetProperty(nameof(WorldRenderer.World)).SetValue(renderer, restored);

			// The fork writes terrain after the restore. The layer must see that write through the restored map,
			// not the outgoing map that the layer cached before the restore.
			restored.Map.CustomTerrain[cell] = water;
			Assert.That(restored.Map.CustomTerrain[cell], Is.Not.EqualTo(parent.World.Map.CustomTerrain[cell]),
				"Precondition: the restored terrain write must differ from the outgoing world's terrain.");

			var layerMap = (Map)typeof(TerrainSpriteLayer).GetProperty("CurrentMap", BindingFlags.Instance | BindingFlags.NonPublic)
				.GetValue(layer);
			Assert.That(layerMap, Is.SameAs(restored.Map),
				"The shared sprite layer reads its map from the outgoing world, not the restored world.");
			Assert.That(layerMap.CustomTerrain[cell], Is.EqualTo(water),
				"The shared sprite layer does not see the restored world's terrain write.");
		}

		/// <summary>The receivers of a layer's <c>CellEntryChanged</c> handlers, found by reflection on the event's backing field.</summary>
		static IEnumerable<object> SubscriberTargets(object cellLayer)
		{
			var handler = (Delegate)cellLayer.GetType().GetField("CellEntryChanged", BindingFlags.Instance | BindingFlags.NonPublic)
				.GetValue(cellLayer);
			return handler?.GetInvocationList().Select(d => d.Target).Where(t => t != null) ?? [];
		}

		/// <summary>
		/// The foot <c>Locomotor</c> of a world. It lives in OpenRA.Mods.Common, which this project cannot
		/// reference, so it is found by its trait type name and its <c>Info.Name</c> by reflection.
		/// </summary>
		static object FootLocomotorOf(World world)
		{
			return world.WorldActor.TraitsImplementing<IWorldLoaded>()
				.Single(t => t.GetType().Name == "Locomotor" && LocomotorName(t) == "foot");
		}

		static string LocomotorName(object locomotor)
		{
			var info = locomotor.GetType().GetField("Info", BindingFlags.Instance | BindingFlags.Public).GetValue(locomotor);
			return (string)info.GetType().GetField("Name", BindingFlags.Instance | BindingFlags.Public).GetValue(info);
		}

		static short MovementCostOf(object locomotor, CPos cell)
		{
			var method = locomotor.GetType().GetMethod("MovementCostForCell", [typeof(CPos)]);
			return (short)method.Invoke(locomotor, [cell]);
		}
	}
}
