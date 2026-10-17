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
	/// The envelope's map image keys (P2.13). Every response but help names map-state.png: its path, the tick it was rendered
	/// at, and its geometry. The image is refreshed when the world's tick moves, so map_state_tick matches tick after a
	/// world-changing request. A render that fails is reported as unavailable, and the turn still answers. digest:false drops
	/// the map keys with the state. A refused place with no candidates still carries a look.
	/// </summary>
	[TestFixture]
	sealed class AgentEnvelopeMapTest
	{
		const int Seed = 0xE2E;
		const string Infantry = "e1";

		/// <summary>Ticks to let the map settle, and the visibility update.</summary>
		const int SettleTicks = 5;

		bool savedAgentSocket;
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
			savedWorkspace = AgentWorkspaceRoot.Get();
		}

		[TearDown]
		public void RestoreSettings()
		{
			AgentWorkspaceRoot.Set(savedWorkspace);
			Game.Settings.Debug.AgentSocket = savedAgentSocket;
		}

		static string[] Keys(JsonElement element)
		{
			return element.EnumerateObject().Select(p => p.Name).ToArray();
		}

		static readonly string[] MapKeys = ["map_state_png", "map_state_tick", "map_state_render", "map_px_per_cell", "map_origin", "map_size"];

		/// <summary>A refusal for a place with no candidates, built with the given look, through the mod's record.</summary>
		static object Refusal(object look)
		{
			var type = HeadlessMod.ModData.ObjectCreator.FindType("AgentSocket.PlaceRefusedV1");
			var candidates = Array.CreateInstance(HeadlessMod.ModData.ObjectCreator.FindType("AgentSocket.PlaceCandidateV1"), 0);
			return Activator.CreateInstance(type, [false, "place", "no cell", "hint", 0, candidates, look]);
		}

		[TestCase(TestName = "A response's envelope names the map image: a file that exists, whose size is the announced geometry")]
		public void EnvelopeNamesAnExistingMapImage()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);

			using var response = AgentStateDumpTest.RawRequest("{\"cmd\":\"ping\"}");
			var root = response.RootElement;

			var path = root.GetProperty("map_state_png").GetString();
			Assert.That(File.Exists(path), Is.True, "The envelope should name a map image that exists.");

			var perCell = root.GetProperty("map_px_per_cell").GetInt32();
			var size = root.GetProperty("map_size").EnumerateArray().Select(e => e.GetInt32()).ToArray();
			var bounds = hw.World.Map.Bounds;
			Assert.That(root.GetProperty("map_origin").EnumerateArray().Select(e => e.GetInt32()).ToArray(),
				Is.EqualTo(new[] { bounds.Left, bounds.Top }), "The announced origin should be the map's top-left cell.");

			using var stream = File.OpenRead(path);
			var png = new Png(stream);
			Assert.That(new[] { png.Width, png.Height }, Is.EqualTo(size),
				"The image's size should match the announced map_size.");
			Assert.That(size, Is.EqualTo(new[] { bounds.Width * perCell, bounds.Height * perCell }));
		}

		[TestCase(TestName = "After a world-changing request, map_state_tick equals the envelope tick, and the file is the one rendered then")]
		public void MapTickFollowsTheWorld()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			// A first request renders the image at this tick. The world then moves on before the order.
			using (var first = AgentStateDumpTest.RawRequest("{\"cmd\":\"ping\"}"))
				Assert.That(first.RootElement.GetProperty("map_state_tick").GetInt32(), Is.EqualTo(hw.World.WorldTick));
			hw.Tick(SettleTicks);

			var unit = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			hw.Tick(SettleTicks);
			var destination = new CPos(unit.Location.X + 2, unit.Location.Y);

			using var response = AgentStateDumpTest.RawRequestWithTicks(hw,
				$"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"Move\",\"cell\":[{destination.X},{destination.Y}],\"await\":false}}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("map_state_tick").GetInt32(), Is.EqualTo(root.GetProperty("tick").GetInt32()),
				"After a world-changing request, map_state_tick should equal the envelope tick.");
			Assert.That(File.Exists(root.GetProperty("map_state_png").GetString()), Is.True);
		}

		[TestCase(TestName = "A render that cannot write leaves map_state_png null with render unavailable, and the results still answer")]
		public void UnavailableMapDoesNotFailTheTurn()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);

			// A file where the workspace should be: the render cannot create its directory.
			var blocker = Path.Combine(Path.GetTempPath(), "openra-envelope-blocker-" + Guid.NewGuid().ToString("N"));
			File.WriteAllText(blocker, "not a directory");
			try
			{
				AgentWorkspaceRoot.Set(blocker);
				using var response = AgentStateDumpTest.RawRequest("{\"cmd\":\"ping\"}");
				var root = response.RootElement;

				Assert.That(root.GetProperty("results").GetProperty("ok").GetBoolean(), Is.True, "The result should still be ok.");
				Assert.That(root.GetProperty("map_state_png").ValueKind, Is.EqualTo(JsonValueKind.Null));
				Assert.That(root.GetProperty("map_state_render").GetString(), Is.EqualTo("unavailable"));
				Assert.That(root.GetProperty("map_state_tick").ValueKind, Is.EqualTo(JsonValueKind.Null));
			}
			finally
			{
				AgentWorkspaceRoot.Set(savedWorkspace);
				File.Delete(blocker);
			}
		}

		[TestCase(TestName = "digest:false drops the map keys with the state, and keeps the results, events and tick")]
		public void DigestFalseDropsTheMapKeys()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);

			using var response = AgentStateDumpTest.RawRequest("{\"cmd\":\"ping\",\"digest\":false}");
			var keys = Keys(response.RootElement);

			Assert.That(keys, Is.EqualTo(new[] { "results", "events", "tick" }),
				"digest:false should omit the state and every map key.");
			Assert.That(keys.Intersect(MapKeys), Is.Empty);
		}

		[TestCase(TestName = "A refused place with no candidates still carries a look, with no cells: the key is present")]
		public void RefusalWithNoCandidatesHasAnEmptyLook()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);

			var refusal = Refusal(null);
			var withLook = HeadlessMod.ModData.ObjectCreator.FindType("AgentSocket.AgentPlace")
				.GetMethod("WithLook").Invoke(null, [hw.World, refusal]);

			var look = withLook.GetType().GetProperty("Look").GetValue(withLook);
			Assert.That(look, Is.Not.Null, "A refusal with no candidates should still carry a look.");
			var cells = (Array)look.GetType().GetProperty("Cells").GetValue(look);
			Assert.That(cells.Length, Is.Zero, "With no candidates, the look should have no cells.");
		}

		[TestCase(TestName = "A refusal with no look leaves the look key out of its JSON, rather than writing null")]
		public void RefusalWithoutLookOmitsTheKey()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);

			var json = (string)HeadlessMod.ModData.ObjectCreator.FindType("AgentSocket.AgentSocketCommands")
				.GetMethod("Serialize").Invoke(null, [Refusal(null)]);
			using var doc = JsonDocument.Parse(json);
			Assert.That(doc.RootElement.TryGetProperty("look", out _), Is.False,
				"A refusal with no look should omit the look key.");
		}
	}
}
