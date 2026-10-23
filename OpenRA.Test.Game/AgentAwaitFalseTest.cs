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
	/// The refusals for the removed job form. An order or a deploy always waits for its outcome, so await:false is refused on both,
	/// and a wait that names a job is refused, since there is no job to collect. Each refusal is a parse error, so nothing is issued.
	/// </summary>
	[TestFixture]
	sealed class AgentAwaitFalseTest
	{
		const string AwaitFalseRefused = "await:false is not supported: an order waits for its outcome, so omit await or send true";
		const string JobRefused = "wait takes no job: an order or a deploy waits for its outcome, and wait reads events";

		[TestCase(TestName = "await:false on an order is refused, since an order always waits for its outcome")]
		public void AwaitFalseOnOrderIsRefused()
		{
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"order\",\"actor\":1,\"order\":\"Move\",\"cell\":[5,5],\"await\":false}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False, root.ToString());
			Assert.That(root.GetProperty("error").GetString(), Is.EqualTo(AwaitFalseRefused));
		}

		[TestCase(TestName = "await:false on a group order is refused with the same error")]
		public void AwaitFalseOnGroupIsRefused()
		{
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"order\",\"actors\":[1],\"order\":\"Move\",\"cell\":[5,5],\"await\":false}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False, root.ToString());
			Assert.That(root.GetProperty("error").GetString(), Is.EqualTo(AwaitFalseRefused));
		}

		[TestCase(TestName = "await:false on a deploy is refused, since a deploy always waits for its outcome")]
		public void AwaitFalseOnDeployIsRefused()
		{
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"deploy\",\"actor\":1,\"await\":false}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False, root.ToString());
			Assert.That(root.GetProperty("error").GetString(), Is.EqualTo(AwaitFalseRefused));
		}

		[TestCase(TestName = "A wait that names a job is refused, and says that wait reads events")]
		public void WaitWithJobIsRefused()
		{
			using var response = AgentStateDumpTest.Request("{\"cmd\":\"wait\",\"job\":1}");
			var root = response.RootElement;
			Assert.That(root.GetProperty("ok").GetBoolean(), Is.False, root.ToString());
			Assert.That(root.GetProperty("error").GetString(), Is.EqualTo(JobRefused));
		}
	}
}
