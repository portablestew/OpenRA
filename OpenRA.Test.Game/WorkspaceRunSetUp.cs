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
using System.IO;
using NUnit.Framework;

namespace OpenRA.Test
{
	/// <summary>
	/// Points the agent workspace at one temporary directory for the whole run (P2.13). Every request renders map-state.png into
	/// the workspace, so without this the tests would leave it in the working directory.
	/// </summary>
	[SetUpFixture]
	sealed class WorkspaceRunSetUp
	{
		string directory;

		[OneTimeSetUp]
		public void SetWorkspace()
		{
			HeadlessMod.Require();
			directory = Path.Combine(Path.GetTempPath(), "openra-agent-run-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			AgentWorkspaceRoot.Set(directory);
		}

		[OneTimeTearDown]
		public void RestoreWorkspace()
		{
			AgentWorkspaceRoot.Set(null);
			if (Directory.Exists(directory))
				Directory.Delete(directory, true);
		}
	}
}
