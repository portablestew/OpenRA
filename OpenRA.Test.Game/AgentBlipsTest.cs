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
using System.IO;
using System.Linq;
using NUnit.Framework;
using OpenRA.FileFormats;
using OpenRA.Traits;

namespace OpenRA.Test
{
	/// <summary>
	/// The map-state overlays (P2.12). Blips are owner-coloured, for the actors the observer knows, so a fogged never-seen enemy has none
	/// until a real GPS launch. The selected units get a green crosshair spanning the map, a yellow path ending in a red X, and a red line
	/// to their attack target. The image is decoded with the engine's reader. Mod types are reached by name, as the other agent tests do.
	/// </summary>
	[TestFixture]
	sealed class AgentBlipsTest
	{
		const int Seed = 0xB11B;
		const int Scale = 4;
		const string Infantry = "e1";
		const string Satellite = "atek";

		/// <summary>Ticks to let the map settle, and visibility and the satellite update.</summary>
		const int SettleTicks = 5;

		bool savedAgentSocket;
		string workspace;
		string savedWorkspace;

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

			workspace = Path.Combine(Path.GetTempPath(), "openra-blips-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(workspace);
			savedWorkspace = AgentWorkspaceRoot.Get();
			SetWorkspace(workspace);
		}

		[TearDown]
		public void RestoreSettings()
		{
			SetWorkspace(savedWorkspace);
			if (Directory.Exists(workspace))
				Directory.Delete(workspace, true);

			Game.Settings.Debug.AgentSocket = savedAgentSocket;
		}

		static Type ModType(string name)
		{
			return HeadlessMod.ModData.ObjectCreator.FindType(name);
		}

		static void SetWorkspace(string root)
		{
			AgentWorkspaceRoot.Set(root);
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

		/// <summary>Renders the map-state image and decodes it. The reply is the render's, so its path is the file read.</summary>
		static Png RenderNow()
		{
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"map_state\"}");
			Assert.That(response.RootElement.GetProperty("render").GetString(), Is.EqualTo("ok"), response.RootElement.ToString());
			return new Png(new MemoryStream(File.ReadAllBytes(response.RootElement.GetProperty("path").GetString())));
		}

		/// <summary>The RGBA bytes of a pixel of the image.</summary>
		static byte[] PixelAt(Png png, int x, int y)
		{
			var index = (y * png.Width + x) * 4;
			return [png.Data[index], png.Data[index + 1], png.Data[index + 2], png.Data[index + 3]];
		}

		/// <summary>The RGBA bytes of an ARGB colour.</summary>
		static byte[] Rgba(uint argb)
		{
			return [(byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24)];
		}

		static readonly byte[] Green = [0, 255, 0, 255];
		static readonly byte[] Yellow = [255, 255, 0, 255];
		static readonly byte[] Red = [255, 0, 0, 255];

		/// <summary>The image pixel at a cell's block, offset by (px, py) within the block.</summary>
		static (int X, int Y) BlockPixel(HeadlessWorld hw, CPos cell, int px, int py)
		{
			var bounds = hw.World.Map.Bounds;
			return ((cell.X - bounds.Left) * Scale + px, (cell.Y - bounds.Top) * Scale + py);
		}

		/// <summary>The cells that carry a blip: an occupying, non-scenery actor the local player owns or knows.</summary>
		static bool[] BlipCells(HeadlessWorld hw)
		{
			var bounds = hw.World.Map.Bounds;
			var drawn = new bool[bounds.Width * bounds.Height];
			var local = hw.LocalPlayer;
			foreach (var actor in hw.World.Actors)
			{
				if (actor.OccupiesSpace == null || actor.IsDead || actor.Owner.PlayerReference?.OwnsWorld == true || actor.Info.Name == "mpspawn")
					continue;

				if (actor.Owner == local || Known(actor, local))
					drawn[(actor.Location.Y - bounds.Top) * bounds.Width + (actor.Location.X - bounds.Left)] = true;
			}

			return drawn;
		}

		[TestCase(TestName = "An own unit's blip is drawn in its owner's colour")]
		public void OwnUnitBlipIsOwnerColour()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var unit = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			hw.Tick(SettleTicks);

			var png = RenderNow();
			var (x, y) = BlockPixel(hw, unit.Location, 1, 1);
			Assert.That(PixelAt(png, x, y), Is.EqualTo(Rgba(local.Color.ToArgb())),
				"An own unit's blip should be drawn in its owner's colour.");
		}

		[TestCase(TestName = "A fogged, never-seen enemy has no blip until a real GPS launch draws it")]
		public void FoggedEnemyHasNoBlipUntilGps()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			// The AgentFogTest setup: an enemy unit at the enemy's home, out of every local unit's sight.
			var enemy = hw.CreateActor(Infantry, hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3), hw.EnemyPlayer);
			hw.Tick(SettleTicks);
			Assert.That(Known(enemy, local), Is.False, "Precondition: the enemy unit should be unknown before the launch.");

			var (x, y) = BlockPixel(hw, enemy.Location, 1, 1);
			Assert.That(PixelAt(RenderNow(), x, y), Is.Not.EqualTo(Rgba(hw.EnemyPlayer.Color.ToArgb())),
				"A fogged, never-seen enemy must have no blip.");

			LaunchSatellite(hw, local);
			hw.Tick(SettleTicks);
			Assert.That(Known(enemy, local), Is.True, "After the real GPS launch, the enemy unit should be known.");

			(x, y) = BlockPixel(hw, enemy.Location, 1, 1);
			Assert.That(PixelAt(RenderNow(), x, y), Is.EqualTo(Rgba(hw.EnemyPlayer.Color.ToArgb())),
				"After the real GPS launch, the enemy unit should have a blip in its owner's colour.");
		}

		[TestCase(TestName = "A selected unit's green crosshair runs the full row and column of its cell, except where a blip is")]
		public void SelectionCrosshairSpansRowAndColumn()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var unit = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			hw.Tick(SettleTicks);
			hw.World.Selection.Add(unit);

			var png = RenderNow();
			var bounds = hw.World.Map.Bounds;
			var drawn = BlipCells(hw);
			var mismatches = new System.Collections.Generic.List<string>();

			var row = (unit.Location.Y - bounds.Top) * Scale + Scale / 2;
			for (var x = 0; x < png.Width; x++)
			{
				var cell = new CPos(bounds.Left + x / Scale, unit.Location.Y);
				if (!drawn[(cell.Y - bounds.Top) * bounds.Width + (cell.X - bounds.Left)] && !PixelAt(png, x, row).SequenceEqual(Green))
					mismatches.Add($"row x={x}");
			}

			var column = (unit.Location.X - bounds.Left) * Scale + Scale / 2;
			for (var y = 0; y < png.Height; y++)
			{
				var cell = new CPos(unit.Location.X, bounds.Top + y / Scale);
				if (!drawn[(cell.Y - bounds.Top) * bounds.Width + (cell.X - bounds.Left)] && !PixelAt(png, column, y).SequenceEqual(Green))
					mismatches.Add($"column y={y}");
			}

			Assert.That(mismatches, Is.Empty, "Every pixel of the selected unit's row and column should be green, outside blips.");
		}

		[TestCase(TestName = "A moving selected unit's path is yellow, ending in a red X at its destination")]
		public void PathIsYellowAndEndsInRedX()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var area = hw.FindClearArea(local.HomeLocation, 9, 3, 30);
			var start = new CPos(area.X + 1, area.Y + 1);
			var unit = hw.CreateActor(Infantry, start, local);
			hw.Tick(SettleTicks);
			hw.World.Selection.Add(unit);

			var destination = new CPos(start.X + 6, start.Y);
			using (var order = AgentStateDumpTest.RequestWithTicks(hw,
				$"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}],\"await\":false}}"))
				Assert.That(order.RootElement.GetProperty("ok").GetBoolean(), Is.True, order.RootElement.ToString());

			hw.Tick(1);
			var png = RenderNow();

			var middle = new CPos(start.X + 3, start.Y);
			var (mx, my) = BlockPixel(hw, middle, 2, 2);
			Assert.That(PixelAt(png, mx, my), Is.EqualTo(Yellow), "A cell on the path should carry a yellow segment.");

			var (ex, ey) = BlockPixel(hw, destination, 0, 0);
			Assert.That(PixelAt(png, ex, ey), Is.EqualTo(Red), "The path's end should carry a red X.");
			var (dx, dy) = BlockPixel(hw, destination, Scale - 1, Scale - 1);
			Assert.That(PixelAt(png, dx, dy), Is.EqualTo(Red), "The red X should cross the end cell.");
		}

		[TestCase(TestName = "A selected unit attacking a target has a red line between them")]
		public void AttackTargetIsRedLine()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			// Near the local home, so the target is in the attacker's sight: an attack on a target only in GPS view is dropped.
			var area = hw.FindClearArea(local.HomeLocation, 9, 3, 30);
			var attacker = hw.CreateActor(Infantry, new CPos(area.X + 1, area.Y + 1), local);
			var target = hw.CreateActor(Infantry, new CPos(area.X + 3, area.Y + 1), hw.EnemyPlayer);
			hw.Tick(SettleTicks);
			LaunchSatellite(hw, local);
			hw.Tick(SettleTicks);
			Assert.That(Known(target, local), Is.True, "Precondition: the target should be known.");
			hw.World.Selection.Add(attacker);

			hw.World.IssueOrder(new Order("Attack", attacker, Target.FromActor(target), false));
			hw.Tick(3);

			// The attack is still running, so the line is sampled between the live positions, within a pixel of the midpoint.
			var png = RenderNow();
			var (ax, ay) = BlockPixel(hw, attacker.Location, 2, 2);
			var (tx, ty) = BlockPixel(hw, target.Location, 2, 2);
			var (mx, my) = ((ax + tx) / 2, (ay + ty) / 2);
			var nearMidpoint = Enumerable.Range(-1, 3).SelectMany(dy => Enumerable.Range(-1, 3).Select(dx => PixelAt(png, mx + dx, my + dy)));
			Assert.That(nearMidpoint.Any(p => p.SequenceEqual(Red)), Is.True, "The line from the attacker to its target should be red.");
		}
	}
}
