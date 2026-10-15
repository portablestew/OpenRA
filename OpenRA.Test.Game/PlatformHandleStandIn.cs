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

namespace OpenRA.Platforms.Default
{
	// Stands in for a platform handle in WorldForkPlatformShareTest. The real handles are internal and need a GPU or
	// sound device to construct. The share rule matches by namespace, so this type is treated as a handle.
	sealed class StandInPlatformHandle
	{
		public int Id = 7;
	}
}
