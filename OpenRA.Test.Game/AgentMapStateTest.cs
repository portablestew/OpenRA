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
using System.Text.Json;
using NUnit.Framework;
using OpenRA.FileFormats;

namespace OpenRA.Test
{
	/// <summary>
	/// The map-state terrain and fog layer (P2.11). The image is decoded with the engine's own PNG reader. A cell's colour is its top-left
	/// pixel: the terrain colour when visible, the shroud colour when unexplored, and the terrain under fog when explored but not seen.
	/// The workspace is a temporary directory per test, so the files written there are the ones the render wrote.
	/// </summary>
	[TestFixture]
	sealed class AgentMapStateTest
	{
		const int Seed = 0x3A9C;
		const string Infantry = "e1";
		const string AgentMapStateName = "map-state.png";
		const int AgentMapStateScale = 4;

		/// <summary>The shroud colour, opaque black, and the fog's alpha over the terrain, as RadarWidget has them.</summary>
		const byte FogAlpha = 128;

		/// <summary>Ticks to let the map settle, and visibility update.</summary>
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

			workspace = Path.Combine(Path.GetTempPath(), "openra-map-state-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(workspace);
			savedWorkspace = AgentWorkspaceRoot.Get();
			AgentWorkspaceRoot.Set(workspace);
		}

		[TearDown]
		public void RestoreSettings()
		{
			AgentWorkspaceRoot.Set(savedWorkspace);
			if (Directory.Exists(workspace))
				Directory.Delete(workspace, true);

			Game.Settings.Debug.AgentSocket = savedAgentSocket;
		}

		/// <summary>The map-state reply, from a fresh request. The caller disposes the document.</summary>
		static JsonDocument MapState()
		{
			return AgentStateDumpTest.Request("{\"cmd\":\"map_state\"}");
		}

		/// <summary>Decodes the PNG at a path with the engine's reader.</summary>
		static Png Decode(string path)
		{
			return new Png(new MemoryStream(File.ReadAllBytes(path)));
		}

		/// <summary>The RGBA bytes of a decoded image's pixel.</summary>
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

		/// <summary>The RGBA bytes of a colour under the fog: black at 128 over it, so each channel keeps 127/255 of its value.</summary>
		static byte[] Fogged(uint argb)
		{
			const int Keep = 255 - FogAlpha;
			return [(byte)(((argb >> 16) & 0xFF) * Keep / 255), (byte)(((argb >> 8) & 0xFF) * Keep / 255), (byte)((argb & 0xFF) * Keep / 255), 255];
		}

		/// <summary>The image pixel that is the top-left of a map cell: the cell's offset from the map's top-left, times the scale.</summary>
		static (int X, int Y) BlockOf(HeadlessWorld hw, CPos cell)
		{
			var bounds = hw.World.Map.Bounds;
			return ((cell.X - bounds.Left) * 4, (cell.Y - bounds.Top) * 4);
		}

		[TestCase(TestName = "The map-state image is the map's bounds times the scale, in pixels")]
		public void ImageSizeIsBoundsTimesScale()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);

			using var response = MapState();
			var root = response.RootElement;
			Assert.That(root.GetProperty("render").GetString(), Is.EqualTo("ok"), root.ToString());

			using var stream = File.OpenRead(root.GetProperty("path").GetString());
			var png = new Png(stream);
			var bounds = hw.World.Map.Bounds;
			Assert.That(png.Width, Is.EqualTo(bounds.Width * AgentMapStateScale), "The image width should be the map width times the scale.");
			Assert.That(png.Height, Is.EqualTo(bounds.Height * AgentMapStateScale), "The image height should be the map height times the scale.");
			Assert.That(root.GetProperty("size").EnumerateArray().Select(v => v.GetInt32()), Is.EqualTo(new[] { png.Width, png.Height }),
				"The reply should give the same size.");
		}

		[TestCase(TestName = "A visible cell's pixel is its terrain colour from the radar's colour source")]
		public void VisibleCellIsItsTerrainColour()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			// A unit beside the cell makes it visible to its owner, and leaves no blip on the cell itself.
			var cell = hw.FindClearArea(local.HomeLocation, 3, 3);
			hw.CreateActor(Infantry, new CPos(cell.X + 1, cell.Y), local);
			hw.Tick(SettleTicks);
			Assert.That(local.Shroud.IsVisible(cell.ToMPos(hw.World.Map)), Is.True, "Precondition: the cell should be visible.");

			using var response = MapState();
			using var stream = File.OpenRead(response.RootElement.GetProperty("path").GetString());
			var png = new Png(stream);

			var (x, y) = BlockOf(hw, cell);
			var expected = Rgba(hw.World.Map.GetTerrainColorPair(cell.ToMPos(hw.World.Map)).Left.ToArgb());
			Assert.That(PixelAt(png, x, y), Is.EqualTo(expected), "A visible cell's top-left pixel should be its terrain colour.");
		}

		[TestCase(TestName = "An unexplored cell is shroud-coloured, then under fog once explored, then its terrain when seen")]
		public void UnexploredIsShroudThenFogThenTerrain()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			// A cell at the enemy's home, which no local unit has explored yet.
			var cell = hw.FindClearArea(hw.EnemyPlayer.HomeLocation, 3, 3);
			var map = cell.ToMPos(hw.World.Map);
			var expected = hw.World.Map.GetTerrainColorPair(map).Left.ToArgb();
			Assert.That(local.Shroud.IsExplored(map), Is.False, "Precondition: the cell should start unexplored.");
			var (x, y) = BlockOf(hw, cell);

			using (var before = MapState())
			{
				var png = Decode(before.RootElement.GetProperty("path").GetString());
				Assert.That(PixelAt(png, x, y), Is.EqualTo(new byte[] { 0, 0, 0, 255 }),
					"An unexplored cell should be drawn in the opaque black shroud colour.");
			}

			// The explore the GPS launch makes: explored, not visible, so the terrain under fog.
			local.Shroud.ExploreAll();
			hw.Tick(1);
			using (var explored = MapState())
			{
				var png = Decode(explored.RootElement.GetProperty("path").GetString());
				Assert.That(local.Shroud.IsVisible(map), Is.False, "Precondition: the explored cell should not be visible.");
				Assert.That(PixelAt(png, x, y), Is.EqualTo(Fogged(expected)),
					"An explored cell out of sight should be its terrain under the fog, not the shroud colour.");
			}

			// A unit beside the cell makes it visible: the terrain colour, with no blip on the cell.
			hw.CreateActor(Infantry, new CPos(cell.X + 1, cell.Y), local);
			hw.Tick(SettleTicks);
			using (var seen = MapState())
			{
				var png = Decode(seen.RootElement.GetProperty("path").GetString());
				Assert.That(PixelAt(png, x, y), Is.EqualTo(Rgba(expected)),
					"A cell that is visible should be its terrain colour.");
			}
		}

		[TestCase(TestName = "The image is written through a temporary file: it replaces an old file, and no temporary file is left behind")]
		public void ReplacesThroughTemporaryFile()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);

			// An old file of the same name, which the render should replace.
			var old = Path.Combine(workspace, AgentMapStateName);
			File.WriteAllBytes(old, [1, 2, 3]);

			using var response = MapState();
			Assert.That(response.RootElement.GetProperty("path").GetString(), Is.EqualTo(old));

			var bytes = File.ReadAllBytes(old);
			Assert.That(bytes.Take(4).ToArray(), Is.EqualTo(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }),
				"The old file should be replaced by a PNG.");
			Assert.That(Directory.GetFiles(workspace).Select(Path.GetFileName).ToArray(), Is.EqualTo(new[] { AgentMapStateName }),
				"No temporary file should be left in the workspace.");
		}

		[TestCase(TestName = "A render that cannot write answers ok with render unavailable and no path, and does not fail the turn")]
		public void UnavailableRenderDoesNotFailTheTurn()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);

			// A file where the workspace should be: the render cannot create its directory.
			var blocker = Path.Combine(Path.GetTempPath(), "openra-map-state-blocker-" + Guid.NewGuid().ToString("N"));
			File.WriteAllText(blocker, "not a directory");
			try
			{
				AgentWorkspaceRoot.Set(blocker);
				using var response = MapState();
				var root = response.RootElement;
				Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, "The turn should not fail.");
				Assert.That(root.GetProperty("path").ValueKind, Is.EqualTo(JsonValueKind.Null), "No path should be given.");
				Assert.That(root.GetProperty("render").GetString(), Is.EqualTo("unavailable"));
				Assert.That(root.GetProperty("reason").GetString(), Is.Not.Empty, "The reason should be given.");
			}
			finally
			{
				File.Delete(blocker);
			}
		}
	}

	/// <summary>Sets the agent workspace root, which lives in the mod assembly. Test use.</summary>
	static class AgentWorkspaceRoot
	{
		public static string Get()
		{
			return (string)HeadlessMod.ModData.ObjectCreator.FindType("AgentSocket.AgentWorkspace").GetProperty("Root").GetValue(null);
		}

		public static void Set(string root)
		{
			HeadlessMod.ModData.ObjectCreator.FindType("AgentSocket.AgentWorkspace").GetProperty("Root").SetValue(null, root);
		}
	}
}
