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
using OpenRA.Effects;

namespace OpenRA.Test
{
	/// <summary>
	/// Effects that were in flight when a snapshot was captured. A restored effect must keep animating and must
	/// be removed when its sequence ends; one that is neither drawn nor removed reads as a frozen sprite.
	/// </summary>
	[TestFixture]
	sealed class WorldForkEffectTest
	{
		const int Seed = 0xC0FFEE;
		const int SettleTicks = 5;
		const int TicksBeforeCapture = 2;
		const int TicksAfterRestore = 500;

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

		[TestCase(TestName = "An effect mid-animation at capture keeps animating after a restore and is then removed")]
		public void RestoredMidAnimationEffectCompletesAndIsRemoved()
		{
			using var parent = HeadlessWorld.Create(Seed);
			parent.Tick(SettleTicks);

			// SpriteEffect lives in OpenRA.Mods.Common, which this project cannot reference; it is built by name.
			var effectType = HeadlessMod.ModData.ObjectCreator.GetTypes().Single(t => t.FullName == "OpenRA.Mods.Common.Effects.SpriteEffect");
			var position = parent.World.Map.CenterOfCell(parent.LocalPlayer.HomeLocation);
			parent.World.AddFrameEndTask(w => w.Add((IEffect)Activator.CreateInstance(effectType,
				position, w, "explosion", "med_explosion", "effect", false, 0)));
			parent.Tick(TicksBeforeCapture);

			Assert.That(SpriteEffectsOf(parent.World).Count(), Is.EqualTo(1),
				"The effect was not in flight at capture; the test needs it mid-animation.");
			Assert.That(WorldForks.Capture(parent.OrderManager), Is.True, "The capture was refused.");

			// Let the outgoing timeline finish the effect, so the restore has to bring it back.
			parent.Tick(TicksAfterRestore);
			Assert.That(SpriteEffectsOf(parent.World), Is.Empty,
				"The outgoing effect did not finish; raise TicksAfterRestore.");

			var orderManager = parent.OrderManager;
			Assert.That(WorldForks.Undo(orderManager, null, WorldForks.GameMsOf(parent.World) + WorldForks.MinGapMs,
				out var restoreFailure), Is.True, restoreFailure);

			var restored = SpriteEffectsOf(orderManager.World).ToArray();
			Assert.That(restored.Length, Is.EqualTo(1), "The restored world did not carry the in-flight effect.");

			orderManager.World.Tick();
			Assert.That(SpriteEffectsOf(orderManager.World), Is.Not.Empty,
				"The restored effect ended on its first tick; the capture was not mid-animation.");

			for (var i = 0; i < TicksAfterRestore && SpriteEffectsOf(orderManager.World).Any(); i++)
				orderManager.World.Tick();

			Assert.That(SpriteEffectsOf(orderManager.World), Is.Empty,
				"The restored effect was never removed after its sequence ended: it would stay on screen as a frozen sprite.");
		}

		static IEnumerable<IEffect> SpriteEffectsOf(World world) => world.Effects.Where(e => e.GetType().Name == "SpriteEffect");
	}
}
