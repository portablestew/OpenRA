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
using System.Reflection;
using NUnit.Framework;
using OpenRA.Graphics;
using OpenRA.Support;

namespace OpenRA.Test
{
	/// <summary>
	/// A voxel body's <see cref="ModelAnimation"/> across a fork. WithVoxelBody builds the animation's closures over
	/// its owning actor's traits, and RenderVoxels keys its wrapper table by the struct, so the fork's animation must
	/// read the fork's owner and be found by the fork's own key. The headless mod is RA, which has no voxel bodies, so
	/// this builds the same shape directly: an owner with state, a closure over it, and the component list and wrapper
	/// table RenderVoxels keeps. The clone uses the live capture's share policy (<c>WorldForks.ShareLiveAsset</c>).
	/// </summary>
	[TestFixture]
	sealed class WorldForkVoxelAnimationTest
	{
		sealed class VoxelOwner
		{
			public WAngle Facing = new(256);
			public bool Disabled;
		}

		/// <summary>The component bookkeeping RenderVoxels keeps for its animations.</summary>
		sealed class VoxelRig
		{
			public readonly VoxelOwner Owner;
			public readonly List<ModelAnimation> Components = [];
			public readonly Dictionary<ModelAnimation, int> Wrappers = [];

			public VoxelRig(VoxelOwner owner)
			{
				Owner = owner;
			}

			/// <summary>Adds a body animation whose closures read the owner, as WithVoxelBody's constructor does.</summary>
			public void AddBody()
			{
				var owner = Owner;
				var animation = new ModelAnimation(null, () => WVec.Zero,
					() => new WRot(WAngle.Zero, WAngle.Zero, owner.Facing),
					() => owner.Disabled, () => 0, true);

				Components.Add(animation);
				Wrappers.Add(animation, Wrappers.Count);
			}
		}

		static VoxelRig CloneRig(VoxelRig rig)
		{
			var shareLive = typeof(WorldForks).GetMethod("ShareLiveAsset", BindingFlags.NonPublic | BindingFlags.Static);
			Assert.That(shareLive, Is.Not.Null, "WorldForks.ShareLiveAsset was renamed; update this check.");

			var cloner = new ObjectGraphCloner((Func<object, bool>)Delegate.CreateDelegate(typeof(Func<object, bool>), shareLive));
			var fork = cloner.Clone(rig);

			Assert.That(cloner.Unsupported, Is.Empty,
				"The voxel rig's clone reported unsupported members: " + string.Join(", ", cloner.Unsupported));
			return fork;
		}

		[TestCase(TestName = "A forked voxel body's animation reads the fork's owner, not the outgoing one")]
		public void ForkedVoxelAnimationFollowsForkOwner()
		{
			var rig = new VoxelRig(new VoxelOwner());
			rig.AddBody();
			var outgoing = rig.Components[0];

			var fork = CloneRig(rig);
			var forked = fork.Components[0];

			// The outgoing owner turns after the fork is taken: the fork's copy must keep its own facing.
			rig.Owner.Facing = new WAngle(768);
			Assert.That(forked.RotationFunc().Yaw.Angle, Is.EqualTo(256),
				"The forked voxel animation reads the outgoing owner's facing, so it does not follow the fork.");
			Assert.That(outgoing.RotationFunc().Yaw.Angle, Is.EqualTo(768));

			// The fork's owner drives the fork's animation from here on.
			fork.Owner.Facing = new WAngle(512);
			Assert.That(forked.RotationFunc().Yaw.Angle, Is.EqualTo(512),
				"The forked voxel animation does not follow its own owner's facing.");

			// The same holds for the disable closure, which decides whether the body is drawn.
			rig.Owner.Disabled = true;
			Assert.That(forked.DisableFunc(), Is.False,
				"The forked voxel animation's disable closure reads the outgoing owner.");
		}

		[TestCase(TestName = "A forked voxel body's wrapper table is keyed by the fork's own animation")]
		public void ForkedVoxelWrapperLookupUsesForkKey()
		{
			var rig = new VoxelRig(new VoxelOwner());
			rig.AddBody();

			var fork = CloneRig(rig);
			var forked = fork.Components[0];

			Assert.That(fork.Wrappers.ContainsKey(forked), Is.True,
				"The fork's wrapper table cannot find the fork's own animation, so RenderVoxels would lose its bookkeeping.");
			Assert.That(fork.Wrappers.ContainsKey(rig.Components[0]), Is.False,
				"The fork's wrapper table is keyed by the outgoing animation.");

			Assert.That(fork.Components.Remove(forked), Is.True, "The fork's component list cannot remove its own animation.");
			Assert.That(fork.Wrappers.Remove(forked), Is.True, "The fork's wrapper table cannot remove its own animation.");
			Assert.That(rig.Components, Has.Count.EqualTo(1), "Editing the fork changed the outgoing world's components.");
			Assert.That(rig.Wrappers.ContainsKey(rig.Components[0]), Is.True, "Editing the fork changed the outgoing wrapper table.");
		}
	}
}
