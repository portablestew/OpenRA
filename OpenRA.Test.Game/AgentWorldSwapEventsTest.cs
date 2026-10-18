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
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// An event raised in one world is never delivered after the world changes (8.8). A load_map, a restore or a new game swaps the
	/// world, so events the old world buffered for a connection are dropped when the new world loads. Otherwise the next envelope
	/// would report phantom events from a world that no longer exists (part 3 defect 2).
	/// </summary>
	[TestFixture]
	sealed class AgentWorldSwapEventsTest
	{
		const int Seed = 0x5A9E;

		/// <summary>An owner no world has, so a stale event is easy to tell from any event the new world raises.</summary>
		const string StaleOwner = "StaleOwner";

		bool savedAgentSocket;
		bool savedEnabled;
		object connection;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[SetUp]
		public void CreateConnection()
		{
			savedAgentSocket = Game.Settings.Debug.AgentSocket;
			Game.Settings.Debug.AgentSocket = true;
			savedEnabled = (bool)ModType("AgentSocket.AgentEvents").GetProperty("Enabled").GetValue(null);
			connection = Activator.CreateInstance(ModType("AgentSocket.AgentConnection"));
		}

		[TearDown]
		public void RestoreState()
		{
			Call(connection, "Close");
			ModType("AgentSocket.AgentEvents").GetProperty("Enabled").SetValue(null, savedEnabled);
			Game.Settings.Debug.AgentSocket = savedAgentSocket;
		}

		static Type ModType(string name)
		{
			return HeadlessMod.ModData.ObjectCreator.FindType(name);
		}

		static object Call(object target, string method, params object[] args)
		{
			return target.GetType().GetMethod(method).Invoke(target, args);
		}

		/// <summary>Raises a power event for an owner the new world does not have, as the old world would have.</summary>
		static void RaiseStalePowerEvent(int tick)
		{
			var type = ModType("AgentSocket.PowerEventV1");
			var dto = Activator.CreateInstance(type, ["power_low", tick, StaleOwner, 0, 40, -40, "Critical"]);
			ModType("AgentSocket.AgentEvents").GetMethod("Raise").Invoke(null, [dto]);
		}

		/// <summary>The owners of the events the connection would send in its next turn, flushed as the envelope flushes them.</summary>
		List<string> NextTurnOwners()
		{
			var batch = ((IList)Call(connection, "Flush")).Cast<object>().ToList();
			return batch.Select(e => e.GetType().GetProperty("Owner")?.GetValue(e) as string).Where(o => o != null).ToList();
		}

		[TestCase(TestName = "An event buffered in one world is not in the next turn after the world changes")]
		public void StaleEventIsDroppedWhenTheWorldChanges()
		{
			// World one: the event is raised and waits in the connection's buffer, as it would between two requests.
			using (var first = HeadlessWorld.Create(Seed))
			{
				first.Tick(5);
				ModType("AgentSocket.AgentEvents").GetProperty("Enabled").SetValue(null, true);
				RaiseStalePowerEvent(first.World.WorldTick);
				var pending = (int)connection.GetType().GetProperty("Pending").GetValue(connection);
				Assert.That(pending, Is.GreaterThan(0), "Premise: the event is waiting in the connection's buffer.");
			}

			// World two: loading it is the world change. Its own events are not stale.
			using var second = HeadlessWorld.Create(Seed);
			second.Tick(5);

			Assert.That(NextTurnOwners(), Does.Not.Contain(StaleOwner),
				"An event raised in the previous world must not reach the next turn after the world changes.");
		}
	}
}
