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

using OpenRA.Widgets;

namespace OpenRA
{
	public class NullInputHandler : IInputHandler
	{
		// ignore all input
		public void ModifierKeys(Modifiers mods) { }
		public void OnKeyInput(KeyInput input) { }
		public void OnTextInput(string text) { }
		public void OnMouseInput(MouseInput input) { }
	}

	public class DefaultInputHandler : IInputHandler
	{
		readonly World world;
		public DefaultInputHandler(World world)
		{
			this.world = world;
		}

		public void ModifierKeys(Modifiers mods)
		{
			Game.HandleModifierKeys(mods);
		}

		public void OnKeyInput(KeyInput input)
		{
			// The rolling-rewind key is handled above the widget tree so it shadows any widget binding
			// on the same key (bare F6 = Production06 / StatisticsCombat) while a live game runs.
			if (WorldForks.HandleKeyInput(input))
				return;

			Sync.RunUnsynced(world, () => Ui.HandleKeyPress(input));
		}

		public void OnTextInput(string text)
		{
			Sync.RunUnsynced(world, () => Ui.HandleTextInput(text));
		}

		public void OnMouseInput(MouseInput input)
		{
			Sync.RunUnsynced(world, () => Ui.HandleInput(input));
		}
	}
}
