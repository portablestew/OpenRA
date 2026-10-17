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
using System.Text.Json;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.AgentSocket
{
	// Schema v1, additive. Wire keys are snake_case (AgentSocketCommands.Json).

	/// <summary>
	/// A parsed deploy request. At is the top-left cell the request gave, anywhere on the map, or null to search near the actor.
	/// Await is false for a job: the reply is then the job id, and wait collects the deploy's outcome.
	/// </summary>
	public sealed record DeployRequest(uint Actor, CPos? At, bool Await);

	/// <summary>
	/// The deploy command's reply (P1.13). RequestedCell is the at the request gave, or null when it was omitted. Cell is the
	/// top-left cell the transform ended on (P1.14): the resolved cell when the actor got there, else where it stopped. Moved is true when a Move
	/// took the actor there first. Settled, Outcome and Reason are the DeployTransform outcome (P1.8): completed, or canceled with
	/// the cell reason, or the misplaced reason when the Move fell short of the chosen cell. Outcome and Reason are null when the reply came before the deploy settled, as in a paused game.
	/// </summary>
	public sealed record DeployedV1(bool Ok, string Cmd, uint Actor, int[] RequestedCell, int[] Cell, bool Moved, bool Settled,
		string Outcome, string Reason, int Tick, int MinEffectTick);

	/// <summary>The deploy command's refusal when no footprint is valid, with the candidate reasons (P1.13).</summary>
	public sealed class DeployRefusedException : Exception
	{
		public PlaceRefusedV1 Refusal { get; }

		public DeployRefusedException(PlaceRefusedV1 refusal)
			: base(refusal.Error)
		{
			Refusal = refusal;
		}
	}

	/// <summary>
	/// What Start issued on the game thread (P1.13). Await is the one wait for the whole sequence: when the actor must move, the
	/// DeployTransform is held until the Move ends on Cell (P1.14c), so the await settles when the transform ends, or when the Move falls short.
	/// Paused is true when the game is
	/// paused, so nothing will tick the await. The transform's end is checked against Cell (P1.14): a Move that falls short replies canceled.
	/// </summary>
	public sealed record DeployIssued(OrderV1 Ack, AgentOrderAwait Await, uint Actor, int[] RequestedCell, CPos Cell, bool Moved, bool Paused);

	/// <summary>
	/// The deploy command (P1.13). It validates the transformed footprint at the actor's current cell with the footprint check
	/// Transforms.CanDeploy makes, the actor itself ignored. If that cell is not valid, it searches the nearest cell within
	/// SearchRadius that is valid and that the actor can reach, reusing AgentPlace.FindValidCell. AgentPlace.MaxRadius is unchanged.
	/// An explicit at names the cell and skips the search. A refusal lists the candidate reasons, as place does.
	/// </summary>
	public static class AgentDeploy
	{
		/// <summary>How far, in cells, deploy searches from the actor for a valid cell. Q10 sets it apart from place's MaxRadius.</summary>
		public const int SearchRadius = 20;

		/// <summary>Parses the request on the socket thread. Returns an error message, or null when the request is valid.</summary>
		public static string Parse(JsonElement root, out DeployRequest request)
		{
			request = null;
			if (!root.TryGetProperty("actor", out var actorElement) || !actorElement.TryGetUInt32(out var actor))
				return "actor must be an unsigned integer";

			CPos? at = null;
			if (AgentOrder.IsPresent(root, "at"))
			{
				if (!AgentOrder.TryGetInts(root.GetProperty("at"), 2, out var c))
					return "at must be [x, y]";

				at = new CPos(c[0], c[1]);
			}

			var awaits = true;
			if (AgentOrder.IsPresent(root, "await"))
			{
				var awaitElement = root.GetProperty("await");
				if (awaitElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
					return "await must be a boolean";

				awaits = awaitElement.ValueKind == JsonValueKind.True;
			}

			request = new DeployRequest(actor, at, awaits);
			return null;
		}

		/// <summary>
		/// Finds the cell, queues the Move (when the cell is not the actor's own) and the DeployTransform, and registers the one
		/// await over both. Must run on the game thread. Throws DeployRefusedException when no cell is valid.
		/// </summary>
		public static DeployIssued Start(World world, DeployRequest request, AgentSelection selection)
		{
			var actor = AgentOrder.Accept(world, request.Actor, "DeployTransform");
			var transforms = actor.TraitOrDefault<Transforms>()
				?? throw new InvalidOperationException($"actor {actor.ActorID} has no transform to deploy");

			var info = transforms.Info;
			var into = world.Map.Rules.Actors[info.IntoActor];
			var building = into.TraitInfoOrDefault<BuildingInfo>();
			var offset = info.Offset;
			var owner = actor.Owner;
			var here = actor.Location + offset;
			var mobile = actor.TraitOrDefault<Mobile>();

			// The top-left cell is the one the transform checks: its footprint at Location + Offset, with the actor ignored, as
			// Transforms.CanDeploy checks it. Base proximity is left out: the transform never applies it, and place's IsValid does.
			// A cell the actor already stands on needs no route.
			bool Fits(CPos cell) => world.CanPlaceBuilding(cell, into, building, actor);
			bool Reaches(CPos cell) => cell == here || (mobile != null && mobile.CanEnterCell(cell - offset));
			string Reason(CPos cell) => Fits(cell)
				? "the footprint is clear, but the actor cannot reach it"
				: AgentCellReason.Of(world, into, building, cell, actor, owner);

			CPos cell;
			if (building == null)
				cell = here;
			else if (request.At is { } at)
			{
				if (!Fits(at) || !Reaches(at))
				{
					var error = $"no valid cell for '{actor.Info.Name}' at {at.X},{at.Y}";
					throw new DeployRefusedException(AgentPlace.WithLook(world, AgentPlace.Refuse("deploy", error, at, 0, Reason)));
				}

				cell = at;
			}
			else if (AgentPlace.FindValidCell(here, SearchRadius, c => Fits(c) && Reaches(c)) is { } found)
				cell = found;
			else
			{
				var error = $"no valid cell for '{actor.Info.Name}' within {SearchRadius} cells of {here.X},{here.Y}";
				throw new DeployRefusedException(AgentPlace.WithLook(world, AgentPlace.Refuse("deploy", error, here, SearchRadius, Reason)));
			}

			var moved = cell != here;

			// When the actor must move, the deploy is held until the Move ends on the chosen cell, so a Move that falls short queues
			// no transform (P1.14c). Without a Move, the deploy is issued at once.
			var deploy = new Order("DeployTransform", actor, false);
			var await = AgentOrderAwait.Register(deploy, 0);
			await.ExpectedCell = cell;
			if (moved)
			{
				await.HoldUntilMoved(deploy);
				world.IssueOrder(new Order("Move", actor, Target.FromCell(world, cell - offset), false));
			}
			else
				world.IssueOrder(deploy);

			selection.Ordered(world, actor);

			var requested = request.At is { } given ? new[] { given.X, given.Y } : null;
			var ack = new OrderV1(true, "deploy", world.WorldTick, world.WorldTick + 1);
			return new DeployIssued(ack, await, actor.ActorID, requested, cell, moved, world.Paused);
		}

		/// <summary>The deploy reply, built from the await's state. Cmd names the request that asked for it, deploy or wait.</summary>
		public static DeployedV1 Reply(string cmd, DeployIssued issued)
		{
			var await = issued.Await;
			var settled = await.Settled;
			var outcome = settled ? await.Outcome : null;
			var reason = settled ? await.Reason : null;

			// The cell the actor deployed or stopped on, which is the chosen cell unless the Move fell short (P1.14).
			var at = settled && await.DeployedCell is { } ended ? ended : issued.Cell;
			var cell = new[] { at.X, at.Y };
			return new DeployedV1(outcome != AgentOrderAwait.Canceled, cmd, issued.Actor, issued.RequestedCell, cell, issued.Moved,
				settled, outcome, reason, issued.Ack.Tick, issued.Ack.MinEffectTick);
		}

		/// <summary>The job a deploy with await:false records (P1.4): its ack, its await, and the reply wait builds.</summary>
		public static JobIssue ToJob(DeployIssued issued)
		{
			return new JobIssue(issued.Ack, issued.Await, cmd => Reply(cmd, issued));
		}

		/// <summary>
		/// Parks the socket thread until the deploy settles, then builds the reply. Throws when the await fails: restored, a stall,
		/// a dead subject, or the wall-clock backstop. In a paused game it returns at once, unsettled. Must not run on the game thread.
		/// </summary>
		public static DeployedV1 Settle(DeployIssued issued)
		{
			if (issued.Paused)
			{
				// Nothing will tick the await in a paused game, so drop it here, or it stays in the registry until the world is disposed.
				issued.Await.Abandon();
				return Reply("deploy", issued);
			}

			issued.Await.Wait();
			return Reply("deploy", issued);
		}
	}
}
