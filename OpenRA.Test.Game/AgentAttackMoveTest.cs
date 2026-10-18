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
using System.Text.Json;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// An AttackMove order is awaited on its resolution, as a Move is (P3.0a). It replies valid, effect_tick, settled and
	/// settled_ticks, with no path, and it does not wait for the unit to travel. An unreachable cell is valid false with no_path.
	/// A group reports each actor's own result.
	/// </summary>
	[TestFixture]
	sealed class AgentAttackMoveTest
	{
		const int Seed = 0xA77A;
		const string Infantry = "e1";
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

		/// <summary>The kind of a reply field, or Undefined when the reply omits it (a null is not written).</summary>
		static JsonValueKind Kind(JsonElement root, string name)
		{
			return root.TryGetProperty(name, out var value) ? value.ValueKind : JsonValueKind.Undefined;
		}

		/// <summary>
		/// A land cell with no land neighbour: a unit placed there has no path anywhere, so an AttackMove from it finds none. Null when
		/// the map has no such cell.
		/// </summary>
		static CPos? Trapped(HeadlessWorld hw)
		{
			var map = hw.World.Map;
			bool Land(CPos c) => map.Contains(c) && map.GetTerrainInfo(c).Type != "Water";
			CVec[] steps = [new(1, 0), new(-1, 0), new(0, 1), new(0, -1)];
			return map.AllCells.Where(c => Land(c) && steps.All(step => !Land(c + step))).Cast<CPos?>().FirstOrDefault();
		}

		[TestCase(TestName = "A valid AttackMove replies valid with an effect_tick and settled, and carries no path")]
		public void ValidAttackMoveSettles()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var unit = hw.CreateActor(Infantry, hw.FindClearArea(local.HomeLocation, 3, 3), local);
			hw.Tick(SettleTicks);
			var destination = hw.FindClearArea(unit.Location, 1, 1, 6);

			using var results = AgentStateDumpTest.RequestWithTicks(hw,
				$"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"AttackMove\",\"cell\":[{destination.X},{destination.Y}]}}");
			var root = results.RootElement;

			Assert.That(root.GetProperty("ok").GetBoolean(), Is.True, root.ToString());
			Assert.That(Kind(root, "valid"), Is.EqualTo(JsonValueKind.True), "A valid AttackMove should reply valid:true.");
			Assert.That(root.GetProperty("effect_tick").GetInt32(), Is.GreaterThanOrEqualTo(root.GetProperty("tick").GetInt32()),
				"A valid AttackMove should reply with its effect_tick.");
			Assert.That(Kind(root, "settled"), Is.EqualTo(JsonValueKind.True), "A valid AttackMove should reply settled:true.");
			Assert.That(root.GetProperty("settled_ticks").GetInt32(), Is.GreaterThan(0));
			Assert.That(root.TryGetProperty("path", out _), Is.False, "An AttackMove reply should carry no path: its travel is not awaited.");
		}

		[TestCase(TestName = "An AttackMove from a unit with no path replies valid false, with outcome no_path and a reason")]
		public void UnreachableAttackMoveIsNoPath()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var trapped = Trapped(hw);
			Assume.That(trapped, Is.Not.Null, "The test map should have a land cell with no land neighbour.");

			// The unit stands on a cell with no land neighbour, so no path leads from it anywhere.
			var unit = hw.CreateActor(Infantry, trapped.Value, local);
			hw.Tick(SettleTicks);
			var elsewhere = hw.FindClearArea(local.HomeLocation, 1, 1, 40);

			using var results = AgentStateDumpTest.RequestWithTicks(hw,
				$"{{\"cmd\":\"order\",\"actor\":{unit.ActorID},\"order\":\"AttackMove\",\"cell\":[{elsewhere.X},{elsewhere.Y}]}}");
			var root = results.RootElement;

			Assert.That(root.GetProperty("valid").GetBoolean(), Is.False,
				"An AttackMove from a unit with no path should reply valid:false.");
			Assert.That(root.GetProperty("outcome").GetString(), Is.EqualTo("no_path"),
				"An AttackMove with no path should report the no_path outcome.");
			Assert.That(root.GetProperty("reason").GetString(), Is.EqualTo("no path to the cell"),
				"An AttackMove with no path should say why.");
		}

		[TestCase(TestName = "A group AttackMove reports each actor's own result")]
		public void GroupAttackMoveReportsEachActor()
		{
			using var hw = HeadlessWorld.Create(Seed);
			var local = hw.LocalPlayer;
			hw.Tick(SettleTicks);

			var area = hw.FindClearArea(local.HomeLocation, 5, 3, 40);
			var units = Enumerable.Range(0, 3).Select(i => hw.CreateActor(Infantry, area + new CVec(i, 0), local)).ToList();
			hw.Tick(SettleTicks);
			var destination = new CPos(area.X + 2, area.Y + 2);
			var ids = string.Join(",", units.Select(u => u.ActorID));

			using var results = AgentStateDumpTest.RequestWithTicks(hw,
				$"{{\"cmd\":\"order\",\"actors\":[{ids}],\"order\":\"AttackMove\",\"cell\":[{destination.X},{destination.Y}]}}");
			var entries = results.RootElement.GetProperty("results").EnumerateArray().ToArray();

			Assert.That(entries, Has.Length.EqualTo(3), "A group AttackMove should report one result per actor.");
			foreach (var entry in entries)
			{
				Assert.That(entry.GetProperty("settled").GetBoolean(), Is.True, "Each actor's result should be settled.");
				Assert.That(entry.GetProperty("valid").GetBoolean(), Is.True, "Each actor's AttackMove should be valid.");
				Assert.That(entry.TryGetProperty("effect_tick", out _), Is.True, "Each actor's result should carry its effect_tick.");
			}
		}
	}
}
