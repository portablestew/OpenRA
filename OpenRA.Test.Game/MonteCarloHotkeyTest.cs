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

using System.Linq;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// The Monte Carlo toggle key: Ctrl+M is bound to MonteCarloToggle and to nothing else, bare M still mutes,
	/// and the binding is not duplicated in an overlapping context. Runs against the shipped hotkey definitions.
	/// </summary>
	[TestFixture]
	sealed class MonteCarloHotkeyTest
	{
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

		[TestCase(Keycode.M, Modifiers.Ctrl, "MonteCarloToggle", true, TestName = "Ctrl+M toggles Monte Carlo mode")]
		[TestCase(Keycode.M, Modifiers.None, "MonteCarloToggle", false, TestName = "Bare M does not toggle Monte Carlo mode")]
		[TestCase(Keycode.M, Modifiers.Ctrl | Modifiers.Shift, "MonteCarloToggle", false, TestName = "Ctrl+Shift+M does not toggle Monte Carlo mode")]
		[TestCase(Keycode.M, Modifiers.Ctrl, "ToggleMute", false, TestName = "Ctrl+M is not mute")]
		[TestCase(Keycode.M, Modifiers.None, "ToggleMute", true, TestName = "Bare M still mutes")]
		public void KeyMatchesOnlyItsExactHotkey(Keycode key, Modifiers modifiers, string name, bool expected)
		{
			var hotkey = HeadlessMod.ModData.Hotkeys[name];

			Assert.That(hotkey.IsActivatedBy(Press(key, modifiers)), Is.EqualTo(expected));
		}

		[Test]
		public void MonteCarloToggleIsBoundAndNotDuplicated()
		{
			var hotkeys = HeadlessMod.ModData.Hotkeys;
			var definition = hotkeys.Definitions.Single(d => d.Name == "MonteCarloToggle");
			var value = hotkeys["MonteCarloToggle"].GetValue();

			Assert.That(value, Is.Not.EqualTo(Hotkey.Invalid), "MonteCarloToggle resolved to no key, so it cannot be pressed.");
			Assert.That(hotkeys.GetFirstDuplicate(definition, value), Is.Null,
				"MonteCarloToggle shares its key with another hotkey in an overlapping context.");
		}
	}
}
