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
using OpenRA.Support;

namespace OpenRA.Test
{
	/// <summary>
	/// Holds a platform handle and an ordinary object, so one clone shows how the live capture's share policy treats each.
	/// </summary>
	sealed class PlatformShareHolder
	{
		public object Handle;
		public object Ordinary;
	}

	sealed class OrdinaryState
	{
		public int Value = 3;
	}

	[TestFixture]
	sealed class WorldForkPlatformShareTest
	{
		[TestCase(TestName = "A platform handle in a forked world is shared, not copied to a handle no render thread drains")]
		public void PlatformHandleIsSharedAndOrdinaryStateIsCloned()
		{
			var handle = new Platforms.Default.StandInPlatformHandle();
			var holder = new PlatformShareHolder { Handle = handle, Ordinary = new OrdinaryState() };

			var cloner = new ObjectGraphCloner(WorldForks.ShareLiveAsset);
			var clone = cloner.Clone(holder);

			Assert.That(clone.Handle, Is.SameAs(handle),
				"A platform handle was copied. A copy holds a GPU or render-queue reference that no thread drains.");
			Assert.That(clone.Ordinary, Is.Not.SameAs(holder.Ordinary),
				"Ordinary per-world state was shared, so a fork would write into the outgoing world.");
			Assert.That(((OrdinaryState)clone.Ordinary).Value, Is.EqualTo(3));
		}
	}
}
