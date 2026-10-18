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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenRA.Graphics;

namespace OpenRA.Mods.Common.AgentSocket
{
	// Schema v1, additive. Wire keys are snake_case (AgentSocketCommands.Json).

	/// <summary>
	/// The screenshot command's success. Actor is the unit the view was centred on, or null when none was requested. CenterPx
	/// is the view centre after clamping to the map, so it can differ from UnitPx near an edge. UnitPx is the unit's position,
	/// or null when no unit was requested. Both are map pixels (WorldRenderer.ScreenPosition, the space of Viewport.CenterLocation),
	/// not window pixels: a window pixel is the value minus Viewport.TopLeft.
	/// </summary>
	public sealed record ScreenshotV1(bool Ok, string Cmd, string Path, uint? Actor, int[] CenterPx, int[] UnitPx, int Tick);

	/// <summary>A parsed screenshot request. Actor is null when the request omitted it. Dir is null when the request omitted it (P2.14).</summary>
	public sealed record ScreenshotRequest(uint? Actor, string Dir);

	/// <summary>
	/// The game-thread half of a screenshot: the view is already centred and the capture armed. Written completes with the
	/// PNG path once the file is on disk, or faults if the write fails.
	/// </summary>
	public sealed record ScreenshotStart(Task<string> Written, uint? Actor, int[] CenterPx, int[] UnitPx, int Tick);

	/// <summary>
	/// Centres the view on a live unit, then writes the next rendered frame to a PNG in the screenshots folder of the workspace,
	/// or in the folder the request's dir names (P2.14). The render that captures the frame runs after this request, so the
	/// response waits for the file to be written.
	/// </summary>
	public static class AgentScreenshot
	{
		/// <summary>The folder inside the workspace that screenshots go to, unless a request names another with dir.</summary>
		public const string DefaultDirectory = "screenshots";

		/// <summary>Parses the request on the socket thread. Returns an error message, or null when the request is valid.</summary>
		public static string Parse(JsonElement root, out ScreenshotRequest request)
		{
			request = null;

			// A string dir names the folder to write to. It is checked before actor, so a bad dir is reported first.
			string dir = null;
			if (AgentOrder.IsPresent(root, "dir"))
			{
				var element = root.GetProperty("dir");
				if (element.ValueKind != JsonValueKind.String)
					return "dir must be a string";

				dir = element.GetString();
				if (string.IsNullOrWhiteSpace(dir))
					return "dir must not be empty";
			}

			if (!AgentOrder.IsPresent(root, "actor"))
			{
				request = new ScreenshotRequest(null, dir);
				return null;
			}

			// TryGetUInt32 throws, rather than returning false, for a non-number, so the kind is checked first.
			var actorElement = root.GetProperty("actor");
			if (actorElement.ValueKind != JsonValueKind.Number || !actorElement.TryGetUInt32(out var actor))
				return "actor must be an unsigned integer";

			request = new ScreenshotRequest(actor, dir);
			return null;
		}

		/// <summary>
		/// The full path of the folder a screenshot is written to: dir when given, else DefaultDirectory inside the workspace root,
		/// the same root map-state.png uses (P2.14). Not created here: the writer creates it when it writes.
		/// </summary>
		public static string DirectoryFor(string dir)
		{
			return Path.GetFullPath(dir ?? Path.Combine(AgentWorkspace.Root, DefaultDirectory));
		}

		/// <summary>
		/// Checks the request, centres the view on the unit, and arms the capture. Must run on the game thread, and must not wait
		/// on the capture: the render that writes the frame runs on this same thread. If the socket thread has already given up
		/// on the request (cancelled), nothing is armed, so a late run writes no file.
		/// </summary>
		public static ScreenshotStart Start(World world, WorldRenderer wr, ScreenshotRequest request, CancellationToken cancellation)
		{
			if (cancellation.IsCancellationRequested)
				throw new OperationCanceledException("the screenshot request was abandoned before the game thread ran it");

			if (world.Type == WorldType.Shellmap)
				throw new InvalidOperationException("screenshot is not available on the shellmap");

			// The unit is resolved before the renderer is checked, so a bad id gets its own error in a headless world too.
			Actor actor = null;
			if (request.Actor is { } id)
			{
				actor = AgentOrder.Live(world, id);
				if (actor.OccupiesSpace == null)
					throw new InvalidOperationException($"Actor {id} has no position to centre on");
			}

			if (Game.Renderer == null)
				throw new InvalidOperationException("no renderer: the game has no window to capture");

			if (wr == null)
				throw new InvalidOperationException("no world renderer: the world has no view to centre");

			int[] unitPx = null;
			if (actor != null)
			{
				wr.Viewport.Center(actor.CenterPosition);
				var unit = wr.ScreenPxPosition(actor.CenterPosition);
				unitPx = [unit.X, unit.Y];
			}

			var centre = int2.FromVector(wr.Viewport.CenterLocation);
			return new ScreenshotStart(Game.RequestScreenshot(cancellation, DirectoryFor(request.Dir)), request.Actor, [centre.X, centre.Y], unitPx, world.WorldTick);
		}

		/// <summary>Waits on the socket thread for the file to be written. The game thread is not involved in the wait.</summary>
		public static ScreenshotV1 Await(ScreenshotStart start, int timeoutMs = AgentMarshal.DefaultTimeoutMs)
		{
			if (Task.WaitAny([start.Written], timeoutMs) == -1)
				throw new TimeoutException($"the frame was not written within {timeoutMs} ms");

			var path = start.Written.GetAwaiter().GetResult();
			return new ScreenshotV1(true, "screenshot", path, start.Actor, start.CenterPx, start.UnitPx, start.Tick);
		}
	}
}
