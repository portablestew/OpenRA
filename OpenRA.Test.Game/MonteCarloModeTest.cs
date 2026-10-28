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

using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The Monte Carlo mode flag: it starts off, Toggle flips it, and Clear switches it off whatever its state.
	/// </summary>
	[TestFixture]
	sealed class MonteCarloModeTest
	{
		[SetUp]
		public void SetUp()
		{
			MonteCarloMode.Clear();
		}

		[TearDown]
		public void TearDown()
		{
			MonteCarloMode.Clear();
		}

		[Test]
		public void StartsOff()
		{
			Assert.That(MonteCarloMode.Enabled, Is.False);
		}

		[Test]
		public void ToggleFlipsTheFlagEachTime()
		{
			MonteCarloMode.Toggle();
			Assert.That(MonteCarloMode.Enabled, Is.True);

			MonteCarloMode.Toggle();
			Assert.That(MonteCarloMode.Enabled, Is.False);
		}

		[Test]
		public void ClearSwitchesOffWhenOn()
		{
			MonteCarloMode.Toggle();
			MonteCarloMode.Clear();

			Assert.That(MonteCarloMode.Enabled, Is.False);
		}

		[Test]
		public void ClearLeavesAnOffFlagOff()
		{
			MonteCarloMode.Clear();

			Assert.That(MonteCarloMode.Enabled, Is.False);
		}
	}
}
