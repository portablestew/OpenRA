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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// An order the socket issued that has not settled yet. Registered on the game thread at issue. Resolve moves it from
	/// Pending to Settling when the order resolves: AgentWorldEventObserver.Ordered runs for every resolved order, before the
	/// connection check. Tick, the settle clock, completes it once settleTicks have passed, or fails it when the resolve budget
	/// runs out or its subject dies. The socket thread parks in Wait until then. The registry is world-scoped: a resolution only
	/// matches an await on the same world, because Undo swaps the world and actor IDs are not unique across worlds.
	/// The socket thread abandons its await on a wall-clock timeout, so every list access takes Gate.
	/// An Attack await (P1.6) has no settle: a valid Attack stays Pending, and Tick finishes it with an outcome. It is started
	/// once its attack activity is on the subject, or no_valid_target when the resolve budget runs out first.
	/// </summary>
	public sealed class AgentOrderAwait
	{
		/// <summary>Game ticks an order may take to resolve before its await fails (Q13).</summary>
		public const int ResolveBudgetTicks = 50;

		/// <summary>The wall-clock backstop for one Wait. The socket thread gives up after this, even if the game does not tick.</summary>
		public const int WallClockMs = 10000;

		/// <summary>The ticks a Move holds after it resolves, unless the request says otherwise.</summary>
		public const int DefaultSettleTicks = 3;

		/// <summary>The attack outcome when the attack activity appeared on the subject (P1.6).</summary>
		public const string Started = "started";

		/// <summary>The attack outcome when the attack never started. Reason says why (P1.6).</summary>
		public const string NoValidTarget = "no_valid_target";

		/// <summary>The deploy outcome when the transform replaced the actor (P1.8).</summary>
		public const string Completed = "completed";

		/// <summary>The deploy outcome when the transform did not finish. Reason says why (P1.8).</summary>
		public const string Canceled = "canceled";

		/// <summary>The sell outcome when the owner sold the actor (3.4). The actor is disposed right after, so the reply names no state of it.</summary>
		public const string SoldOutcome = "sold";

		/// <summary>The AttackMove outcome when the unit found no path to the cell (P3.0a).</summary>
		public const string NoPathOutcome = "no_path";

		/// <summary>
		/// The clock the event long-poll reads, in milliseconds (P3.4). The wall clock by default. A test sets a fake clock, so a
		/// wait times out on the test's ticks and not on real time. The job wait keeps its own limit.
		/// </summary>
		public static Func<long> Clock { get; set; } = () => Environment.TickCount64;

		static readonly Lock Gate = new();
		static readonly List<AgentOrderAwait> Pending = [];
		static readonly List<AgentOrderAwait> Settling = [];

		readonly Actor subject;
		readonly string order;
		readonly Target target;
		int registeredTick;
		readonly TaskCompletionSource<bool> done = new(TaskCreationOptions.RunContinuationsAsynchronously);

		// A resolved deploy's stall clock (P1.13): the cell the subject last stood on, and the tick it last changed.
		CPos lastCell;
		int lastCellTick;

		// A deploy held for its Move (P1.14c): the order to issue once the Move ends on the chosen cell, and whether the Move has ended.
		Order heldOrder;
		bool moveEnded;

		// An AttackMove that starts on its cell has no travel, so an empty path there is not a failure (P3.0a).
		readonly bool startedAtDestination;

		AgentOrderAwait(Actor subject, string order, Target target, int settleTicks)
		{
			this.subject = subject;
			this.order = order;
			this.target = target;
			SettleTicks = settleTicks;
			registeredTick = subject.World.WorldTick;
			startedAtDestination = order == "AttackMove" && subject.Location == subject.World.Map.CellContaining(target.CenterPosition);
		}

		/// <summary>The world the order was issued on.</summary>
		public World World => subject.World;

		/// <summary>The ticks the await holds after it resolves.</summary>
		public int SettleTicks { get; }

		/// <summary>True once the order has resolved, whether or not its validators passed.</summary>
		public bool Resolved { get; private set; }

		/// <summary>The validator result for the resolved order. Only meaningful once Resolved is true.</summary>
		public bool Valid { get; private set; }

		/// <summary>The world tick the order resolved on. Only meaningful once Resolved is true.</summary>
		public int ResolvedTick { get; private set; }

		/// <summary>True once the await has held its settle ticks. A failed await never settles.</summary>
		public bool Settled { get; private set; }

		/// <summary>The path a Move planned, source cell first. Null until the Move's first path evaluation is captured.</summary>
		public int[][] Path { get; private set; }

		/// <summary>The order names the subject accepted when the validators refused the order. Null unless Valid is false.</summary>
		public string[] Orders { get; private set; }

		/// <summary>True for an Attack await. Its outcome is set by Tick or Resolve, not by settling (P1.6).</summary>
		public bool IsAttack => order == "Attack";

		/// <summary>True for an AttackMove await (P3.0a). It settles on its order's resolution, as a Move does, and does not wait for travel.</summary>
		public bool IsAttackMove => order == "AttackMove";

		/// <summary>
		/// True for an AttackMove whose unit planned no path to the cell and was not already on it (P3.0a). Path is set by the move the
		/// order starts, so this is only known once that path has been captured.
		/// </summary>
		public bool NoPath => IsAttackMove && Path is { Length: 0 } && !startedAtDestination;

		/// <summary>True for a Move, or an AttackMove whose move the order starts (P3.0a): both plan a path the await reports.</summary>
		public bool IsMoveOrder => order is "Move" or "AttackMove";

		/// <summary>True for a DeployTransform await. Its outcome is set by the transform activity's end, or by a refusal (P1.8).</summary>
		public bool IsDeploy => order == "DeployTransform";

		/// <summary>True for a PlaceBuilding await (F6). Its outcome is the new building's id, set by the placed notify.</summary>
		public bool IsPlace => order == "PlaceBuilding";

		/// <summary>True for a Sell await (3.4). A valid one stays Pending until the sold notify ends it, before the actor is disposed.</summary>
		public bool IsSell => order == "Sell";

		/// <summary>The id of the building a PlaceBuilding await placed (F6). Null until the placed notify, and for other awaits.</summary>
		public uint? PlacedActor { get; private set; }

		/// <summary>
		/// The cell a deploy must end on (P1.14), the top-left cell the request resolved to. Null for other awaits, and for a deploy
		/// that was not checked. Set by AgentDeploy before the transform can start.
		/// </summary>
		public CPos? ExpectedCell { get; set; }

		/// <summary>
		/// True when the place command issued this PlaceBuilding await, so its reply needs the new building's id (F6). An
		/// `order` PlaceBuilding leaves it false, so that reply reports the resolution like any other order name (9.2) and does
		/// not wait for the building.
		/// </summary>
		public bool AwaitPlacedActor { get; set; }

		/// <summary>
		/// The top-left cell the transform ended on (P1.14), where the actor deployed or stopped. Null until the transform ends. The
		/// deploy reply reports it as the cell, so a move that ends elsewhere is not reported at the chosen cell.
		/// </summary>
		public CPos? DeployedCell { get; private set; }

		/// <summary>
		/// True while a deploy waits for its Move to end (P1.14c). The transform is not queued until then, so a Move that falls
		/// short never starts one. Read and written on the game thread.
		/// </summary>
		public bool AwaitingMove { get; private set; }

		/// <summary>
		/// Holds deploy until its Move ends (P1.14c). Tick issues it once the Move has ended on ExpectedCell, and never when it has
		/// not. Must run on the game thread, before the Move is issued, so the Move's end finds this await.
		/// </summary>
		public void HoldUntilMoved(Order deploy)
		{
			heldOrder = deploy;
			AwaitingMove = true;
			lastCell = subject.Location;
			lastCellTick = subject.World.WorldTick;
		}

		/// <summary>The outcome of a finished Attack await: Started or NoValidTarget. Null for other awaits, and for an attack in a paused game.</summary>
		public string Outcome { get; private set; }

		/// <summary>Why an attack did not start. Null when it started, and for other awaits.</summary>
		public string Reason { get; private set; }

		/// <summary>
		/// Registers an await for an order that is about to be issued on its subject's world. Must run on the game thread.
		/// </summary>
		public static AgentOrderAwait Register(Order order, int settleTicks)
		{
			var await = new AgentOrderAwait(order.Subject, order.OrderString, order.Target, settleTicks);
			lock (Gate)
				Pending.Add(await);

			return await;
		}

		/// <summary>True while an order the socket issued to subject with this name has not resolved yet. Must run on the game thread.</summary>
		public static bool Unresolved(Actor subject, string order)
		{
			lock (Gate)
				return Pending.Exists(p => ReferenceEquals(p.subject, subject) && p.order == order);
		}

		/// <summary>
		/// Parks the calling thread until the await settles or fails, or until timeoutMs of wall-clock time passes. A failed
		/// await throws its reason. Must not run on the game thread: the game thread is what completes it.
		/// </summary>
		public void Wait(int timeoutMs = WallClockMs)
		{
			if (!TryWait(timeoutMs))
			{
				Abandon();
				throw new TimeoutException($"the order did not settle within {timeoutMs} ms of wall-clock time");
			}

			ThrowIfFailed();
		}

		/// <summary>
		/// Parks the calling thread until the await settles or fails, or until timeoutMs passes. Returns false on timeout and
		/// leaves the await registered, so an order job can wait again. Must not run on the game thread.
		/// </summary>
		public bool TryWait(int timeoutMs)
		{
			return Task.WaitAny([done.Task], timeoutMs) != -1;
		}

		/// <summary>Throws the failure reason of an await that has finished. Call it only after TryWait returned true, or it blocks.</summary>
		public void ThrowIfFailed()
		{
			done.Task.GetAwaiter().GetResult();
		}

		/// <summary>
		/// Resolves the oldest pending await that matches the order's world, subject and order name. An order with no
		/// matching await (issued by something other than the socket) is ignored. Must run on the game thread.
		/// </summary>
		public static void Resolve(Order order, bool valid)
		{
			var world = order.Subject.World;
			lock (Gate)
			{
				var index = Pending.FindIndex(p => p.World == world && p.subject.ActorID == order.Subject.ActorID && p.order == order.OrderString && !p.Resolved);
				if (index < 0)
					return;

				var await = Pending[index];
				await.Resolved = true;
				await.Valid = valid;
				await.ResolvedTick = world.WorldTick;

				// A deploy that can start stays Pending: its transform activity ends it (P1.8). A refused one never starts an activity.
				if (await.IsDeploy)
				{
					await.lastCell = order.Subject.Location;
					await.lastCellTick = world.WorldTick;
					var refusal = valid ? DeployRefusal(order.Subject, order.Queued) : AgentOrder.ValidatorRejected;
					if (refusal != null)
						Finish(await, Canceled, refusal);

					return;
				}

				// A valid Attack stays Pending: Tick finishes it from the attack activity (P1.6). A refused one has no activity to wait for.
				if (await.IsAttack)
				{
					if (!valid)
						Finish(await, NoValidTarget, AttackRefusal(order.Subject, order.Target));

					return;
				}

				// A valid PlaceBuilding the place command issued stays Pending: the placed notify ends it with the new building's
				// id (F6). A refused one places nothing, and one an `order` issued resolves like any other order name (9.2).
				if (await.IsPlace && await.AwaitPlacedActor)
				{
					if (!valid)
						Fail(await, new InvalidOperationException(AgentOrder.ValidatorRejected));

					return;
				}

				// A valid Sell stays Pending: the sold notify ends it before the actor is disposed (3.4). Its activity may first wait
				// for a reverse animation, so the settle clock does not apply to it.
				if (await.IsSell && valid)
					return;

				Pending.RemoveAt(index);
				if (!valid)
					await.Orders = AgentOrderNames.For(order.Subject);

				Settling.Add(await);
			}
		}

		/// <summary>
		/// True while a resolved Move for subject still waits for its path. Move.cs asks this before it builds a path event,
		/// so the path is captured without a connection. Must run on the game thread.
		/// </summary>
		public static bool AwaitingPath(Actor subject)
		{
			lock (Gate)
				return Settling.Exists(p => p.IsMoveOrder && p.Path == null && ReferenceEquals(p.subject, subject));
		}

		/// <summary>Stores the path a Move planned, for the await on subject. An empty cell list is a path that found nothing. Must run on the game thread.</summary>
		public static void CapturePath(Actor subject, int[][] cells)
		{
			lock (Gate)
			{
				var await = Settling.Find(p => p.IsMoveOrder && p.Path == null && ReferenceEquals(p.subject, subject));
				if (await != null)
					await.Path = cells;
			}
		}

		/// <summary>
		/// The settle clock. AgentWorldBridge calls it on every world tick. Completes the awaits on world that have held their
		/// settle ticks, and fails the ones whose subject died or whose resolve budget ran out. Must run on the game thread.
		/// </summary>
		public static void Tick(World world)
		{
			List<Order> toIssue = null;
			lock (Gate)
			{
				if (Pending.Count == 0 && Settling.Count == 0)
					return;

				foreach (var p in Pending.Where(p => p.World == world).ToArray())
				{
					if (p.AwaitingMove)
					{
						if (TickHeldDeploy(p) is { } held)
							(toIssue ??= []).Add(held);
					}
					else if (p.IsDeploy && p.Resolved)
					{
						// The transform activity ends the deploy. A dead subject can end no activity, so it fails here. A subject that has
						// not changed cell for the resolve budget has stalled, and its Move or transform will not end (Q13, in game ticks).
						if (p.subject.IsDead)
							Fail(p, new InvalidOperationException($"actor {p.subject.ActorID} died before the transform ended"));
						else if (p.subject.Location != p.lastCell)
						{
							p.lastCell = p.subject.Location;
							p.lastCellTick = world.WorldTick;
						}
						else if (world.WorldTick - p.lastCellTick >= ResolveBudgetTicks)
							Fail(p, new TimeoutException($"the deploy stalled: the actor did not move for {ResolveBudgetTicks} ticks"));
					}
					else if (p.IsAttack && p.Resolved)
					{
						// The attack outcome: started once its activity is on the subject, and no_valid_target when the budget runs out first.
						if (AttackStarted(p.subject, p.target))
							Finish(p, Started, null);
						else if (p.subject.IsDead)
							Fail(p, new InvalidOperationException($"actor {p.subject.ActorID} died before the attack started"));
						else if (world.WorldTick - p.registeredTick >= ResolveBudgetTicks)
							Finish(p, NoValidTarget, AttackRefusal(p.subject, p.target));
					}
					else if (p.IsPlace && p.AwaitPlacedActor && p.Resolved)
					{
						// The placed notify ends a place. A dead subject can place nothing, and a subject that has not placed for the
						// resolve budget has stalled, so both fail here (F6).
						if (p.subject.IsDead)
							Fail(p, new InvalidOperationException($"actor {p.subject.ActorID} died before the building was placed"));
						else if (world.WorldTick - p.registeredTick >= ResolveBudgetTicks)
							Fail(p, new TimeoutException($"the building was not placed within {ResolveBudgetTicks} ticks"));
					}
					else if (p.IsSell && p.Resolved)
					{
						// The sold notify ends a sell before the actor is disposed, so a disposed subject here was not sold, and no activity
						// will sell it now (3.4). A sell that never starts is bounded by the waiter's budget, not the resolve budget.
						if (p.subject.IsDead)
							Fail(p, new InvalidOperationException($"actor {p.subject.ActorID} died before it was sold"));
					}
					else if (p.subject.IsDead)
						Fail(p, new InvalidOperationException($"actor {p.subject.ActorID} died before the order resolved"));
					else if (world.WorldTick - p.registeredTick >= ResolveBudgetTicks)
						Fail(p, new TimeoutException($"the order did not resolve within {ResolveBudgetTicks} ticks"));
				}

				foreach (var p in Settling.Where(p => p.World == world).ToArray())
					if (world.WorldTick - p.ResolvedTick >= p.SettleTicks)
						Complete(p);
			}

			// Issued off the lock: an order may resolve on this thread, and Resolve takes the lock.
			if (toIssue != null)
				foreach (var order in toIssue)
					world.IssueOrder(order);
		}

		/// <summary>
		/// Ticks a deploy held for its Move (P1.14c). While the Move runs, the stall clock watches the actor's cell. When the Move
		/// has ended, the transform is queued only if the actor stands on the chosen cell. Otherwise the await finishes canceled, and
		/// no transform is queued. Returns the order to issue, or null. Called under Gate.
		/// </summary>
		static Order TickHeldDeploy(AgentOrderAwait p)
		{
			if (p.subject.IsDead)
			{
				Fail(p, new InvalidOperationException($"actor {p.subject.ActorID} died before its move ended"));
				return null;
			}

			var world = p.World;
			if (!p.moveEnded)
			{
				if (p.subject.Location != p.lastCell)
				{
					p.lastCell = p.subject.Location;
					p.lastCellTick = world.WorldTick;
				}
				else if (world.WorldTick - p.lastCellTick >= ResolveBudgetTicks)
					Fail(p, new TimeoutException($"the deploy stalled: the actor did not move for {ResolveBudgetTicks} ticks"));

				return null;
			}

			p.AwaitingMove = false;
			var transforms = p.subject.TraitOrDefault<Transforms>();
			var ended = transforms == null ? p.subject.Location : p.subject.Location + transforms.Info.Offset;
			p.DeployedCell = ended;
			if (p.ExpectedCell is { } expected && expected != ended)
			{
				Finish(p, Canceled, $"the actor stopped at {ended.X},{ended.Y}, not the chosen cell {expected.X},{expected.Y}: the move did not reach it");
				return null;
			}

			// The deploy's own budget starts now, so the transform gets ResolveBudgetTicks from its issue, as a queued deploy did.
			p.registeredTick = world.WorldTick;
			var order = p.heldOrder;
			p.heldOrder = null;
			return order;
		}

		/// <summary>
		/// The transform activity reports its end here (P1.8): completed when the actor was replaced, or canceled with a reason. A
		/// cannot_deploy cancel takes the cell reason the footprint fails, so the reply names the cell. Called for every transform
		/// event, with or without a connection. Must run on the game thread.
		/// </summary>
		public static void Activity(Actor self, string kind, string state, string reason)
		{
			// A held deploy waits for its Move to end, and Tick reads where the actor stands then (P1.14c).
			if (kind == "move")
			{
				if (state != "started")
				{
					lock (Gate)
					{
						var held = Pending.Find(a => a.AwaitingMove && ReferenceEquals(a.subject, self));
						if (held != null)
							held.moveEnded = true;
					}
				}

				return;
			}

			if (kind != "transform" || state == "started")
				return;

			lock (Gate)
			{
				var await = Pending.Find(a => a.IsDeploy && a.Resolved && ReferenceEquals(a.subject, self));
				if (await == null)
					return;

				// The transform ends on the cell the actor stands on. When that is not the chosen cell, the Move did not reach it (P1.14),
				// and the reply says where the actor ended up, whatever the transform's own outcome.
				var transforms = self.TraitOrDefault<Transforms>();
				var ended = transforms == null ? self.Location : self.Location + transforms.Info.Offset;
				await.DeployedCell = ended;
				if (await.ExpectedCell is { } expected && expected != ended)
				{
					var verb = state == "completed" ? "deployed" : "stopped";
					Finish(await, Canceled, $"the actor {verb} at {ended.X},{ended.Y}, not the chosen cell {expected.X},{expected.Y}: the move did not reach it");
					return;
				}

				if (state == "completed")
					Finish(await, Completed, null);
				else
					Finish(await, Canceled, reason == "cannot_deploy" ? transforms?.DeployRefusalReason() ?? reason : reason);
			}
		}

		/// <summary>
		/// The placed notify: a PlaceBuilding order created a building, so the socket's pending place await on the notifier gets the
		/// new actor's id and settles (F6). Called for every placed building, with or without a connection, so a reply can name what
		/// it placed. The notifier is the actor the order resolved on, which is the await's subject. Must run on the game thread.
		/// </summary>
		public static void Placed(Actor notifier, uint buildingId)
		{
			lock (Gate)
			{
				var await = Pending.Find(a => a.IsPlace && ReferenceEquals(a.subject, notifier));
				if (await == null)
					return;

				await.PlacedActor = buildingId;
				Finish(await, Completed, null);
			}
		}

		/// <summary>
		/// The sold notify: the owner sold the actor, before it is disposed, so the socket's pending sell await on it finishes as sold
		/// (3.4). Called for every sale, with or without a connection. Must run on the game thread.
		/// </summary>
		public static void Sold(Actor sold)
		{
			lock (Gate)
			{
				var await = Pending.Find(a => a.IsSell && a.Resolved && ReferenceEquals(a.subject, sold));
				if (await != null)
					Finish(await, SoldOutcome, null);
			}
		}

		/// <summary>
		/// Why a DeployTransform cannot start, or null when it can. A refused non-queued deploy never starts an activity, since
		/// DeployTransform plays the refusal and returns. A paused or disabled trait ignores the order, so nothing starts either.
		/// A queued deploy on a clear footprint starts, and a blocked one is refused at its tick (Transform.Tick), so it is not refused here.
		/// Must run on the game thread.
		/// </summary>
		static string DeployRefusal(Actor subject, bool queued)
		{
			var transforms = subject.TraitOrDefault<Transforms>();
			if (transforms == null)
				return "actor has no transform";

			if (transforms.IsTraitPaused || transforms.IsTraitDisabled)
				return "transform is paused or disabled";

			if (queued || transforms.CanDeploy())
				return null;

			return transforms.DeployRefusalReason() ?? "cannot_deploy";
		}

		/// <summary>
		/// Fails every await with the restored error. WorldAdopted calls it: an undo swaps the world, so no pending order will
		/// resolve on the old one. Must run on the game thread.
		/// </summary>
		public static void FailAllRestored()
		{
			lock (Gate)
			{
				foreach (var p in Pending.Concat(Settling).ToArray())
					Fail(p, new InvalidOperationException("restored"));
			}
		}

		/// <summary>
		/// Fails the awaits on every disposed world with the restored error. WorldLoaded calls it: a load_skirmish disposes the world it
		/// replaces, so no pending order will resolve there. Awaits on a world that is still alive are left alone. Must run on the game thread.
		/// </summary>
		public static void FailDisposedRestored()
		{
			lock (Gate)
			{
				foreach (var p in Pending.Concat(Settling).Where(p => p.World.Disposing).ToArray())
					Fail(p, new InvalidOperationException("restored"));
			}
		}

		/// <summary>The number of unresolved awaits on world. Test use.</summary>
		public static int PendingCount(World world)
		{
			lock (Gate)
				return Pending.Count(p => p.World == world);
		}

		/// <summary>Drops every await. Test use.</summary>
		public static void Clear()
		{
			lock (Gate)
			{
				Pending.Clear();
				Settling.Clear();
			}
		}

		/// <summary>Removes this await from the registry once no one will wait on it: its waiter gave up, or the order can never settle (a paused game). Runs on the socket thread.</summary>
		public void Abandon()
		{
			lock (Gate)
			{
				Pending.Remove(this);
				Settling.Remove(this);
			}
		}

		// Both are called under Gate. Removing an await here keeps it from leaking after its waiter has gone.
		static void Complete(AgentOrderAwait await)
		{
			Settling.Remove(await);
			await.Settled = true;
			await.done.TrySetResult(true);
		}

		static void Fail(AgentOrderAwait await, Exception reason)
		{
			Pending.Remove(await);
			Settling.Remove(await);
			await.done.TrySetException(reason);
		}

		// Called under Gate. An attack that finishes has an outcome, not an error, so its waiter gets a reply.
		static void Finish(AgentOrderAwait await, string outcome, string reason)
		{
			Pending.Remove(await);
			Settling.Remove(await);
			await.Outcome = outcome;
			await.Reason = reason;
			await.Settled = true;
			await.done.TrySetResult(true);
		}

		/// <summary>
		/// True when the subject carries an attack activity (AttackFrontal's Attack, or AttackFollow's nested AttackActivity) and the
		/// attack can proceed. An immobile subject out of range has an activity that waits and never closes in, so it has not started.
		/// Must run on the game thread.
		/// </summary>
		static bool AttackStarted(Actor subject, Target target)
		{
			if (subject.CurrentActivity == null || !subject.CurrentActivity.ActivitiesImplementing<IActivityInterface>()
				.Any(a => a is Activities.Attack || a.GetType().DeclaringType == typeof(AttackFollow)))
				return false;

			return subject.Info.HasTraitInfo<IMoveInfo>() || target.IsInRange(subject.CenterPosition, MaxRange(subject, target));
		}

		/// <summary>The longest range any enabled armament of subject has against target. Zero when none has one.</summary>
		static WDist MaxRange(Actor subject, Target target)
		{
			var max = WDist.Zero;
			foreach (var attack in subject.TraitsImplementing<AttackBase>())
			{
				if (attack.IsTraitDisabled)
					continue;

				var range = attack.GetMaximumRangeVersusTarget(target);
				if (max < range)
					max = range;
			}

			return max;
		}

		/// <summary>
		/// Why an attack did not start, from the preconditions AttackBase checks: an armament, a valid target for its weapons, and
		/// range (AttackBase.cs:151-158, :203, :330). Must run on the game thread.
		/// </summary>
		static string AttackRefusal(Actor subject, Target target)
		{
			var attacks = subject.TraitsImplementing<AttackBase>().Where(a => !a.IsTraitDisabled).ToArray();
			if (attacks.Length == 0)
				return "actor has no armament";

			// IsValidFor alone accepts a friendly unit. ChooseArmamentsForTarget applies the stance, so it decides the attack too.
			if (!target.IsValidFor(subject) || !attacks.Any(a => a.ChooseArmamentsForTarget(target, false).Any()))
				return "target not valid for self";

			var max = MaxRange(subject, target);
			if (max == WDist.Zero)
				return "no armament can attack the target";

			if (!target.IsInRange(subject.CenterPosition, max))
				return $"target out of range: max range {max.Length}, distance {(target.CenterPosition - subject.CenterPosition).Length} (world units)";

			return "no armament can attack the target";
		}
	}
}
