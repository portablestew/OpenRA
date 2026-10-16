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

namespace OpenRA.Widgets
{
	/// <summary>
	/// A real widget, in the widget namespace the restore treats as the outgoing UI, that subscribes to world and
	/// shroud events the way the in-game widgets do (for example <c>ProductionTabsLogic</c> and <c>RadarWidget</c>).
	/// </summary>
	sealed class UiSubscriberWidget : Widget
	{
		public int Notifications { get; private set; }

		public Actor LastActor { get; private set; }

		public PPos LastShroudCell { get; private set; }

		public void ActorChanged(Actor a)
		{
			Notifications++;
			LastActor = a;
		}

		public void ShroudChanged(PPos uv)
		{
			Notifications++;
			LastShroudCell = uv;
		}
	}
}
