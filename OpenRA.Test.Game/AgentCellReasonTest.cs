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
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The cell-reason mirror (P1.9). Each reason has a fixture that gives exactly that reason, and a property test checks the
	/// mirror against the engine over every cell of a fully explored map. The socket's mirror and the Mods.Common types it uses
	/// are reached by reflection, as the other headless tests reach them.
	/// </summary>
	[TestFixture]
	sealed class AgentCellReasonTest
	{
		/// <summary>A 3x4 building that requires a buildable area, so distance applies to it.</summary>
		const string Factory = "fact";

		/// <summary>A 1x1 wall. It also requires a buildable area, so distance applies to it too.</summary>
		const string Wall = "sbag";

		/// <summary>The clearing a base building is placed in. A 6x6 clearing leaves a margin around a 3x4 footprint.</summary>
		const int Clearing = 6;

		/// <summary>Ticks to let the map's starting units settle before placing anything.</summary>
		const int SettleTicks = 5;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		/// <summary>Reaches the socket's mirror and the Mods.Common members the test needs.</summary>
		static class Mirror
		{
			static Type Find(string name) => HeadlessMod.ModData.ObjectCreator.FindType(name);

			public static string Reason(string field)
			{
				return (string)Find("AgentSocket.AgentCellReason").GetField(field).GetRawConstantValue();
			}

			public static string Of(HeadlessWorld hw, string type, CPos cell)
			{
				var ai = hw.World.Map.Rules.Actors[type];
				var bi = BuildingInfo(ai);
				return (string)Find("AgentSocket.AgentCellReason").GetMethod("Of")
					.Invoke(null, [hw.World, ai, bi, cell, null, hw.LocalPlayer]);
			}

			public static object BuildingInfo(ActorInfo ai)
			{
				return TraitInfo(ai, "BuildingInfo");
			}

			public static object TraitInfo(ActorInfo ai, string name)
			{
				var method = typeof(ActorInfo).GetMethods().First(m => m.Name == "TraitInfo" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
				return method.MakeGenericMethod(Find("Traits." + name)).Invoke(ai, null);
			}

			public static bool HasTraitInfo(ActorInfo ai, string name)
			{
				var method = typeof(ActorInfo).GetMethods().First(m => m.Name == "HasTraitInfo" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
				return (bool)method.MakeGenericMethod(Find("Traits." + name)).Invoke(ai, null);
			}

			/// <summary>The engine's bool API, CanPlaceBuilding, through its extension method.</summary>
			public static bool CanPlaceBuilding(HeadlessWorld hw, string type, CPos cell)
			{
				var ai = hw.World.Map.Rules.Actors[type];
				return (bool)Find("Traits.BuildingUtils").GetMethod("CanPlaceBuilding")
					.Invoke(null, [hw.World, cell, ai, BuildingInfo(ai), null]);
			}

			public static bool IsCloseEnoughToBase(HeadlessWorld hw, string type, CPos cell)
			{
				var ai = hw.World.Map.Rules.Actors[type];
				var bi = BuildingInfo(ai);
				return (bool)bi.GetType().GetMethod("IsCloseEnoughToBase")
					.Invoke(bi, [hw.World, hw.LocalPlayer, ai, cell]);
			}

			/// <summary>The resource type on a cell, or null when the cell has none.</summary>
			public static string ResourceType(HeadlessWorld hw, CPos cell)
			{
				var layer = hw.World.WorldActor.GetType().GetMethods()
					.First(m => m.Name == "Trait" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
					.MakeGenericMethod(Find("Traits.IResourceLayer")).Invoke(hw.World.WorldActor, null);
				var contents = Find("Traits.IResourceLayer").GetMethod("GetResource").Invoke(layer, [cell]);
				return (string)contents.GetType().GetField("Type").GetValue(contents);
			}
		}

		/// <summary>Explores the whole map, then ticks once so the shroud applies the exploration.</summary>
		static void ExploreMap(HeadlessWorld hw)
		{
			hw.LocalPlayer.Shroud.ExploreAll();
			hw.Tick(1);
		}

		/// <summary>
		/// Places a base building for the local player, so a buildable area exists. A headless start has none, and without one
		/// every cell is too far from the base for a building that requires one.
		/// </summary>
		static void PlaceBase(HeadlessWorld hw)
		{
			var area = hw.FindClearArea(hw.LocalPlayer.HomeLocation, Clearing, Clearing);
			hw.CreateActor(Factory, area + new CVec(1, 1), hw.LocalPlayer);
			hw.Tick(1);
		}

		/// <summary>The first cell the type is placeable on and close enough to the base. The map must be explored first.</summary>
		static CPos ValidCell(HeadlessWorld hw, string type)
		{
			return hw.World.Map.AllCells.First(c => Mirror.CanPlaceBuilding(hw, type, c) && Mirror.IsCloseEnoughToBase(hw, type, c));
		}

		[TestCase(TestName = "A placeable, explored footprint close to the base has no reason")]
		public void ValidCellHasNoReason()
		{
			using var hw = HeadlessWorld.Create(0xCE11);
			hw.Tick(SettleTicks);
			PlaceBase(hw);
			ExploreMap(hw);
			var cell = ValidCell(hw, Factory);

			Assert.That(Mirror.CanPlaceBuilding(hw, Factory, cell), Is.True, "The fixture cell should be placeable.");
			Assert.That(Mirror.Of(hw, Factory, cell), Is.Null);
		}

		[TestCase(TestName = "A footprint whose first tile holds an actor gives the occupied reason")]
		public void OccupiedReason()
		{
			using var hw = HeadlessWorld.Create(0xCE11);
			hw.Tick(SettleTicks);
			PlaceBase(hw);
			ExploreMap(hw);
			var cell = ValidCell(hw, Factory);
			hw.CreateActor("e1", cell);
			hw.Tick(1);

			Assert.That(Mirror.CanPlaceBuilding(hw, Factory, cell), Is.False);
			Assert.That(Mirror.Of(hw, Factory, cell), Is.EqualTo(Mirror.Reason("Occupied")));
		}

		[TestCase(TestName = "A footprint off the map gives the terrain reason")]
		public void TerrainReason()
		{
			using var hw = HeadlessWorld.Create(0xCE11);
			hw.Tick(SettleTicks);
			var cell = new CPos(-1, -1);

			Assert.That(Mirror.CanPlaceBuilding(hw, Factory, cell), Is.False);
			Assert.That(Mirror.Of(hw, Factory, cell), Is.EqualTo(Mirror.Reason("Terrain")));
		}

		[TestCase(TestName = "A footprint on a ramp gives the ramp reason")]
		public void RampReason()
		{
			using var hw = HeadlessWorld.Create(0xCE11);
			hw.Tick(SettleTicks);
			PlaceBase(hw);
			ExploreMap(hw);
			var cell = ValidCell(hw, Factory);
			hw.World.Map.Ramp[cell] = 1;

			Assert.That(Mirror.CanPlaceBuilding(hw, Factory, cell), Is.False);
			Assert.That(Mirror.Of(hw, Factory, cell), Is.EqualTo(Mirror.Reason("Ramp")));
		}

		[TestCase(TestName = "A cell on a resource terrain the building does not accept gives the resource reason")]
		public void ResourceReason()
		{
			using var hw = HeadlessWorld.Create(0xCE11);
			hw.Tick(SettleTicks);
			var bi = Mirror.BuildingInfo(hw.World.Map.Rules.Actors[Wall]);
			var terrainTypes = (IEnumerable<string>)bi.GetType().GetField("TerrainTypes").GetValue(bi);

			// A 1x1 wall makes the cell the only tile, so the first failing tile is the ore cell itself.
			var cell = hw.World.Map.AllCells.First(c =>
				Mirror.ResourceType(hw, c) != null
				&& !terrainTypes.Contains(hw.World.Map.GetTerrainInfo(c).Type)
				&& !hw.World.ActorMap.GetActorsAt(c).Any());

			Assert.That(Mirror.CanPlaceBuilding(hw, Wall, cell), Is.False);
			Assert.That(Mirror.Of(hw, Wall, cell), Is.EqualTo(Mirror.Reason("Resource")));
		}

		[TestCase(TestName = "A placeable footprint over unexplored shroud gives the shroud reason")]
		public void ShroudReason()
		{
			using var hw = HeadlessWorld.Create(0xCE11);
			hw.Tick(SettleTicks);
			var shroud = hw.LocalPlayer.Shroud;

			var cell = hw.World.Map.AllCells.First(c => !shroud.IsExplored(c) && Mirror.CanPlaceBuilding(hw, Wall, c));

			Assert.That(Mirror.CanPlaceBuilding(hw, Wall, cell), Is.True, "The engine does not check shroud, so the cell must be placeable.");
			Assert.That(Mirror.Of(hw, Wall, cell), Is.EqualTo(Mirror.Reason("Shroud")));
		}

		[TestCase(TestName = "A placeable, explored footprint far from any buildable area gives the distance reason")]
		public void DistanceReason()
		{
			using var hw = HeadlessWorld.Create(0xCE11);
			hw.Tick(SettleTicks);
			ExploreMap(hw);

			Assert.That(Mirror.HasTraitInfo(hw.World.Map.Rules.Actors[Factory], "RequiresBuildableAreaInfo"), Is.True,
				"The fixture needs a building that requires a buildable area.");
			var cell = hw.World.Map.AllCells.First(c => Mirror.CanPlaceBuilding(hw, Factory, c) && !Mirror.IsCloseEnoughToBase(hw, Factory, c));

			Assert.That(Mirror.CanPlaceBuilding(hw, Factory, cell), Is.True, "The engine does not check distance, so the cell must be placeable.");
			Assert.That(Mirror.Of(hw, Factory, cell), Is.EqualTo(Mirror.Reason("Distance")));
		}

		[TestCase(TestName = "Over every cell of an explored map, the mirror is null if and only if CanPlaceBuilding is true, where distance cannot apply")]
		public void MirrorAgreesWithEngineOnEveryCell()
		{
			using var hw = HeadlessWorld.Create(0xCE11);
			hw.Tick(SettleTicks);
			PlaceBase(hw);
			ExploreMap(hw);

			// Shroud cannot differ from the engine on an explored map. Distance can, since CanPlaceBuilding does not check it: a cell
			// too far from the base must name distance when the engine allows it, and only the cells the base reaches are compared.
			var mismatches = new List<string>();
			var judged = 0;
			var nulls = 0;
			var distant = 0;

			foreach (var cell in hw.World.Map.AllCells)
			{
				var reason = Mirror.Of(hw, Factory, cell);
				var allowed = Mirror.CanPlaceBuilding(hw, Factory, cell);
				if (!Mirror.IsCloseEnoughToBase(hw, Factory, cell))
				{
					if (allowed)
						distant++;

					if (allowed && reason != Mirror.Reason("Distance"))
						mismatches.Add($"{cell}: reason={reason ?? "null"} canPlace=True distant");

					continue;
				}

				judged++;
				var isNull = reason == null;
				if (isNull)
					nulls++;

				if (isNull != allowed)
					mismatches.Add($"{cell}: reason={reason ?? "null"} canPlace={allowed}");
			}

			Assert.That(mismatches, Is.Empty, string.Join("\n", mismatches.Take(20)));
			Assert.That(nulls, Is.GreaterThan(0), "The property holds trivially if no judged cell is placeable.");
			Assert.That(judged - nulls, Is.GreaterThan(0), "The property holds trivially if every judged cell is placeable.");
			Assert.That(distant, Is.GreaterThan(0), "The distance branch was never exercised.");
		}
	}
}
