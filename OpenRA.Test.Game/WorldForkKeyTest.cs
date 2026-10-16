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
	/// The snapshot undo and redo keys: each key matches exactly one hotkey, F6 is no longer a rewind key, and the
	/// handler consumes nothing when no game is running. Runs against the shipped hotkey definitions, with no game.
	/// </summary>
	[TestFixture]
	sealed class WorldForkKeyTest
	{
		static readonly string[] SnapshotHotkeys = ["SnapshotUndo", "SnapshotRedo", "SnapshotRedoAlt"];

		[OneTimeSetUp]
		public void InitializeMod()
		{
			HeadlessMod.Require();
		}

		static KeyInput Press(Keycode key, Modifiers modifiers)
		{
			return new KeyInput
			{
				Event = KeyInputEvent.Down,
				Key = key,
				Modifiers = modifiers,
			};
		}

		[TestCase(Keycode.Z, Modifiers.Ctrl, "SnapshotUndo", true, TestName = "Ctrl+Z matches undo")]
		[TestCase(Keycode.Y, Modifiers.Ctrl, "SnapshotRedo", true, TestName = "Ctrl+Y matches redo")]
		[TestCase(Keycode.Z, Modifiers.Ctrl | Modifiers.Shift, "SnapshotRedoAlt", true, TestName = "Ctrl+Shift+Z matches the alternate redo")]
		[TestCase(Keycode.Z, Modifiers.None, "SnapshotUndo", false, TestName = "Bare Z does not match undo, so Sell keeps it")]
		[TestCase(Keycode.Z, Modifiers.Ctrl | Modifiers.Shift, "SnapshotUndo", false, TestName = "Ctrl+Shift+Z does not match undo")]
		[TestCase(Keycode.Z, Modifiers.Ctrl, "SnapshotRedoAlt", false, TestName = "Ctrl+Z does not match the alternate redo")]
		[TestCase(Keycode.Y, Modifiers.Ctrl | Modifiers.Shift, "SnapshotRedo", false, TestName = "Ctrl+Shift+Y does not match redo")]
		[TestCase(Keycode.F6, Modifiers.None, "SnapshotUndo", false, TestName = "Bare F6 does not match undo")]
		public void KeyMatchesOnlyItsExactHotkey(Keycode key, Modifiers modifiers, string name, bool expected)
		{
			Assert.That(WorldForks.MatchesSnapshotHotkey(Press(key, modifiers), name), Is.EqualTo(expected));
		}

		[TestCase(Keycode.F6, Modifiers.None, TestName = "Bare F6")]
		[TestCase(Keycode.F6, Modifiers.Ctrl, TestName = "Ctrl+F6")]
		[TestCase(Keycode.F6, Modifiers.Shift, TestName = "Shift+F6")]
		public void F6MatchesNoSnapshotHotkey(Keycode key, Modifiers modifiers)
		{
			foreach (var name in SnapshotHotkeys)
				Assert.That(WorldForks.MatchesSnapshotHotkey(Press(key, modifiers), name), Is.False, name);
		}

		[TestCase(Keycode.Z, Modifiers.Ctrl, TestName = "Ctrl+Z is not consumed without a game")]
		[TestCase(Keycode.Y, Modifiers.Ctrl, TestName = "Ctrl+Y is not consumed without a game")]
		[TestCase(Keycode.F6, Modifiers.None, TestName = "Bare F6 is not consumed without a game")]
		public void KeyIsNotConsumedWithoutAGame(Keycode key, Modifiers modifiers)
		{
			Assert.That(WorldForks.HandleKeyInput(Press(key, modifiers)), Is.False);
		}
	}
}
