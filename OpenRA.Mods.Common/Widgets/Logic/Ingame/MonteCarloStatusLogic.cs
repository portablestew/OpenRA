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

using OpenRA.Mods.Common.MonteCarlo;
using OpenRA.Primitives;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets.Logic.Ingame
{
	/// <summary>
	/// Shows the Monte Carlo mode's state in the top-left corner. The label sits directly under the support-power
	/// palette, so it moves down as powers appear, and it stays at the top when there is no palette or no power.
	/// </summary>
	public class MonteCarloStatusLogic : ChromeLogic
	{
		const int TopY = 10;
		const int Gap = 4;

		readonly Widget container;
		readonly World world;
		readonly HotkeyReference toggleKey;
		readonly LabelWidget label;

		SupportPowersWidget palette;
		Hotkey lastKey;
		string keyText;
		MonteCarloState lastState;
		Actor lastTarget;
		bool shown;

		[ObjectCreator.UseCtor]
		public MonteCarloStatusLogic(Widget widget, ModData modData, World world)
		{
			container = widget;
			this.world = world;
			toggleKey = modData.Hotkeys["MonteCarloToggle"];
			label = widget.Get<LabelWidget>("MONTECARLO_TEXT");
			label.GetText = () => label.Text;
			label.GetColor = () => label.TextColor;

			widget.Get<LogicTickerWidget>("MONTECARLO_TICKER").OnTick = Refresh;
		}

		void Refresh()
		{
			if (palette == null)
			{
				palette = Ui.Root.GetOrNull<SupportPowersWidget>("SUPPORT_PALETTE");
				if (palette != null)
				{
					palette.OnIconCountChanged += (_, _) => Reposition();
					Reposition();
				}
			}

			var state = MonteCarloTarget.Resolve(world, out var target);
			var key = toggleKey.GetValue();
			if (!shown || state != lastState || target != lastTarget || key != lastKey)
			{
				shown = true;
				lastState = state;
				lastTarget = target;
				if (key != lastKey)
					keyText = key.DisplayString();
				lastKey = key;

				SetText(state, target);
			}
		}

		void SetText(MonteCarloState state, Actor target)
		{
			switch (state)
			{
				case MonteCarloState.Active:
					label.Text = FluentProvider.GetMessage("label-montecarlo-active", "unit", target.Info.Name, "key", keyText);
					label.TextColor = Color.LimeGreen;
					break;
				case MonteCarloState.Idle:
					label.Text = FluentProvider.GetMessage("label-montecarlo-idle", "key", keyText);
					label.TextColor = Color.Yellow;
					break;
				default:
					label.Text = FluentProvider.GetMessage("label-montecarlo-off", "key", keyText);
					label.TextColor = Color.Gray;
					break;
			}
		}

		void Reposition()
		{
			var y = TopY;
			if (palette != null && palette.IconCount > 0)
				y = palette.RenderBounds.Y + palette.IconCount * (palette.IconSize.Y + palette.IconMargin) + Gap;

			container.Bounds.Y = y;
		}
	}
}
