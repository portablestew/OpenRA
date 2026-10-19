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

using System.Linq;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The state's map (P2.6). Bounds is the world's Map.Bounds as [x, y, width, height]. Spawns has one entry per player that has
	/// a home cell, naming the player and the cell. The maps command is unchanged and still lists uid and title only.
	/// </summary>
	[TestFixture]
	sealed class AgentStateMapTest
	{
		const int Seed = 0x3A9;

		/// <summary>Ticks to let the map's starting units settle before reading the state.</summary>
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

		[TestCase(TestName = "The state map carries Map.Bounds, and spawns has exactly one entry per player with a home cell, at that cell")]
		public void StateMapCarriesBoundsAndSpawns()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);

			var map = hw.World.Map.Bounds;
			var expectedSpawns = hw.World.Players
				.Where(p => p.HomeLocation != CPos.Zero)
				.Select(p => (Player: p.PlayerName, X: p.HomeLocation.X, Y: p.HomeLocation.Y))
				.ToArray();
			Assert.That(expectedSpawns.Length, Is.GreaterThan(1), "The test map should have at least two players with a home cell.");

			using var state = AgentStateDumpTest.State();
			var root = state.RootElement.GetProperty("map");

			// bounds is Map.Bounds, as [x, y, width, height].
			var bounds = root.GetProperty("bounds").EnumerateArray().Select(e => e.GetInt32()).ToArray();
			Assert.That(bounds, Is.EqualTo(new[] { map.X, map.Y, map.Width, map.Height }), "bounds should equal Map.Bounds.");

			// spawns: exactly one entry per player with a home cell, each at that player's HomeLocation.
			var spawns = root.GetProperty("spawns").EnumerateArray()
				.Select(s => (Player: s.GetProperty("player").GetString(), X: s.GetProperty("cell")[0].GetInt32(), Y: s.GetProperty("cell")[1].GetInt32()))
				.ToArray();
			Assert.That(spawns, Is.EquivalentTo(expectedSpawns), "spawns should be exactly the players with a home cell, at their cells.");
		}

		[TestCase(TestName = "The maps command still lists uid and title only, without bounds or spawns")]
		public void MapsCommandIsUnchanged()
		{
			using var hw = HeadlessWorld.Create(Seed);
			hw.Tick(SettleTicks);

			using var maps = AgentStateDumpTest.Request("{\"cmd\":\"maps\"}");
			var first = maps.RootElement.GetProperty("maps").EnumerateArray().First();
			Assert.That(first.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(["uid", "title"]));
		}
	}
}
