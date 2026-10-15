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
using System.Reflection;
using NUnit.Framework;
using OpenRA.Support;

namespace OpenRA.Test
{
	/// <summary>
	/// Direct check of the live capture's cloner: <see cref="WorldForks.Capture"/> refuses a snapshot whose
	/// cloner reports unsupported members, but only exposes success/failure. This clones a live headless
	/// world with the same share policy (<c>WorldForks.ShareLiveAsset</c>, reached by reflection) and asserts
	/// the unsupported list itself is empty.
	/// </summary>
	[TestFixture]
	sealed class WorldForkUnsupportedTest
	{
		const int Seed = 0xC0FFEE;
		const int TicksBeforeCapture = 20;

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		[TestCase(TestName = "A live capture's cloner reports no unsupported members")]
		public void LiveCaptureReportsNoUnsupportedMembers()
		{
			using var parent = HeadlessWorld.Create(Seed);
			parent.Tick(TicksBeforeCapture);

			var shareLive = typeof(WorldForks).GetMethod("ShareLiveAsset", BindingFlags.NonPublic | BindingFlags.Static);
			Assert.That(shareLive, Is.Not.Null, "WorldForks.ShareLiveAsset was renamed; update this check.");

			var cloner = new ObjectGraphCloner((Func<object, bool>)Delegate.CreateDelegate(typeof(Func<object, bool>), shareLive));
			var fork = cloner.Clone(parent.World);

			Assert.That(fork, Is.Not.Null, "The live world could not be cloned.");
			Assert.That(cloner.Unsupported, Is.Empty,
				"The live capture's cloner reported unsupported members: " + string.Join(", ", cloner.Unsupported));
		}
	}
}
