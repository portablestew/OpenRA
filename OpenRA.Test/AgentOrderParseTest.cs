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

using System.Text.Json;
using NUnit.Framework;
using OpenRA.Mods.Common.AgentSocket;

namespace OpenRA.Test
{
	/// <summary>
	/// The order command's optional item, extra_data and extra_location keys, as the socket thread parses them. The parse needs
	/// no world. The game-thread hand-off to World.IssueOrder is covered by the headless tests in OpenRA.Test.Game.
	/// </summary>
	[TestFixture]
	sealed class AgentOrderParseTest
	{
		[TestCase(TestName = "The order's item, extra_data and extra_location are parsed into the request")]
		public void ExtraFieldsAreParsed()
		{
			using var document = JsonDocument.Parse("{\"cmd\":\"order\",\"actor\":5,\"order\":\"PlaceBuilding\","
				+ "\"item\":\"powr\",\"extra_data\":7,\"extra_location\":[3,4]}");

			Assert.That(AgentOrder.Parse(document.RootElement, out var request), Is.Null);
			Assert.That(request.Item, Is.EqualTo("powr"));
			Assert.That(request.ExtraData, Is.EqualTo(7u));
			Assert.That(request.ExtraLocation, Is.EqualTo(new CPos(3, 4)));
		}

		[TestCase(TestName = "Absent extra fields are null in the request, so the order keeps its engine defaults")]
		public void AbsentExtraFieldsAreNull()
		{
			using var document = JsonDocument.Parse("{\"cmd\":\"order\",\"actor\":5,\"order\":\"Stop\"}");

			Assert.That(AgentOrder.Parse(document.RootElement, out var request), Is.Null);
			Assert.That(request.Item, Is.Null);
			Assert.That(request.ExtraData, Is.Null);
			Assert.That(request.ExtraLocation, Is.Null);
		}

		[TestCase(TestName = "The on key is parsed as the desired toggle state, true or false")]
		public void OnIsParsedAsBoolean()
		{
			using var on = JsonDocument.Parse("{\"cmd\":\"order\",\"actor\":5,\"order\":\"RepairBuilding\",\"on\":true}");
			using var off = JsonDocument.Parse("{\"cmd\":\"order\",\"actor\":5,\"order\":\"RepairBuilding\",\"on\":false}");

			Assert.That(AgentOrder.Parse(on.RootElement, out var onRequest), Is.Null);
			Assert.That(onRequest.On, Is.True);

			Assert.That(AgentOrder.Parse(off.RootElement, out var offRequest), Is.Null);
			Assert.That(offRequest.On, Is.False);
		}

		[TestCase(TestName = "An absent on is null in the request, so the order is not a toggle request")]
		public void AbsentOnIsNull()
		{
			using var document = JsonDocument.Parse("{\"cmd\":\"order\",\"actor\":5,\"order\":\"Stop\"}");

			Assert.That(AgentOrder.Parse(document.RootElement, out var request), Is.Null);
			Assert.That(request.On, Is.Null);
		}

		[TestCase(TestName = "A non-boolean on is refused at parse, with no request")]
		public void NonBooleanOnIsRefused()
		{
			using var text = JsonDocument.Parse("{\"cmd\":\"order\",\"actor\":5,\"order\":\"RepairBuilding\",\"on\":\"yes\"}");
			using var number = JsonDocument.Parse("{\"cmd\":\"order\",\"actor\":5,\"order\":\"RepairBuilding\",\"on\":1}");

			Assert.That(AgentOrder.Parse(text.RootElement, out var textRequest), Is.EqualTo("on must be a boolean"));
			Assert.That(textRequest, Is.Null);

			Assert.That(AgentOrder.Parse(number.RootElement, out var numberRequest), Is.EqualTo("on must be a boolean"));
			Assert.That(numberRequest, Is.Null);
		}
	}
}
