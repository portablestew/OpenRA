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
using System.Diagnostics;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Network;
using OpenRA.Support;
using OpenRA.Traits;
using OpenRA.Widgets;

namespace OpenRA
{
	/// <summary>
	/// Rolling window of deep-cloned <see cref="World"/> snapshots taken during live gameplay, plus the
	/// restore path that adopts one of them as the active game state - an interactive, memory-backed
	/// counterpart to the MCTS fork primitive. One snapshot is captured every
	/// <see cref="CaptureIntervalMs"/> of wall-clock time; the window holds at most <see cref="Capacity"/>,
	/// evicting the oldest. Pressing the rewind key (bare F6) pops the most recent snapshot, swaps it in
	/// for the live world, and restarts the capture timer, so repeated presses walk backwards through the
	/// window.
	/// <para>
	/// Restoring is a world swap, not a state transplant: the fork already *is* a complete world (its
	/// actors, traits, selection, synced RNG and effects are all clones), so the live game adopts it by
	/// re-pointing the three owners of a world - the <see cref="OrderManager"/>, the
	/// <see cref="WorldRenderer"/> and the widget tree - at the fork. The previous live world is dropped
	/// unreferenced (it is deliberately not disposed, because <see cref="World.Dispose"/> would dispose the
	/// <see cref="Map"/> that every snapshot in the window still shares).
	/// </para>
	/// <para>
	/// Two consequences are accepted for this proof-of-concept, both sanctioned: the order/recording
	/// pipeline keeps running at its current frame against a world whose simulation state jumped
	/// backwards, so replays and savegames recorded across a rewind describe a timeline that was never
	/// played (and in multiplayer the diverging sync hash would desync other clients - which is why
	/// capture and restore refuse to run when more than one human client is present, and never run for
	/// replays); and a capture costs a fork-sized hitch (tens to hundreds of milliseconds depending on
	/// map size) on the game loop, reported per snapshot in the debug log.
	/// </para>
	/// </summary>
	public static class WorldForks
	{
		/// <summary>Wall-clock milliseconds between snapshots during eligible gameplay.</summary>
		public const long CaptureIntervalMs = 10_000;

		/// <summary>Maximum snapshots held at once; the oldest is evicted when exceeded.</summary>
		public const int Capacity = 64;

		/// <summary>
		/// One captured fork plus the bookkeeping recorded alongside it: enough to reason about what the
		/// rewind would do (simulation position) and what the savegame machinery looked like at that
		/// moment (the recording/save buffer depth, the stretch metric).
		/// </summary>
		public sealed class Snapshot
		{
			public readonly World World;

			/// <summary><see cref="World.WorldTick"/> when the snapshot was captured.</summary>
			public readonly int WorldTick;

			/// <summary>Net frame of the shared order pipeline at capture time.</summary>
			public readonly int NetFrame;

			/// <summary>Net frame of the most recent server-side game save, or -1 if there is none yet.</summary>
			public readonly int SaveLastFrame;

			/// <summary>Net frames accumulated since the last game save, or -1 if no save exists.</summary>
			public readonly int FramesSinceSave;

			/// <summary>The in-process server's save buffer: last orders frame it flushed, or -1 if
			/// unavailable (no local server, or the buffer has not started).</summary>
			public readonly int ServerSaveLastFrame;

			/// <summary>Bytes of orders buffered in the in-process server's savegame, or -1 if unavailable.</summary>
			public readonly long ServerBufferedOrderBytes;

			/// <summary>Depth of the pending net-order queue at capture time.</summary>
			public readonly int OrderQueueDepth;

			/// <summary><see cref="Game.RunTime"/> when the snapshot was captured.</summary>
			public readonly long CapturedAtRunTimeMs;

			/// <summary>Wall-clock duration of the fork itself, in milliseconds.</summary>
			public readonly double ForkMs;

			/// <summary>Distinct instances the cloner registered - the size of the cloned state graph.</summary>
			public readonly int ClonedObjects;

			internal Snapshot(World world, int worldTick, int netFrame, int saveLastFrame, int framesSinceSave,
				int serverSaveLastFrame, long serverBufferedOrderBytes, int orderQueueDepth,
				long capturedAtRunTimeMs, double forkMs, int clonedObjects)
			{
				World = world;
				WorldTick = worldTick;
				NetFrame = netFrame;
				SaveLastFrame = saveLastFrame;
				FramesSinceSave = framesSinceSave;
				ServerSaveLastFrame = serverSaveLastFrame;
				ServerBufferedOrderBytes = serverBufferedOrderBytes;
				OrderQueueDepth = orderQueueDepth;
				CapturedAtRunTimeMs = capturedAtRunTimeMs;
				ForkMs = forkMs;
				ClonedObjects = clonedObjects;
			}

			public override string ToString()
			{
				return $"worldTick={WorldTick} netFrame={NetFrame} saveLastFrame={SaveLastFrame} " +
					$"framesSinceSave={FramesSinceSave} serverSaveFrame={ServerSaveLastFrame} " +
					$"serverBufferedBytes={ServerBufferedOrderBytes} queueDepth={OrderQueueDepth} " +
					$"fork={ForkMs:F1}ms objects={ClonedObjects}";
			}
		}

		static readonly List<Snapshot> Snapshots = [];

		/// <summary><see cref="Game.RunTime"/> before which no capture is attempted.</summary>
		static long nextCaptureAtRunTimeMs;

		/// <summary>Number of snapshots captured over the lifetime of the current window (for logging).</summary>
		static int capturedTotal;

		/// <summary>Number of snapshots currently held; 0 when the window is empty.</summary>
		public static int Count => Snapshots.Count;

		/// <summary>
		/// Drops every snapshot and schedules the next capture one interval from <paramref name="nowMs"/>.
		/// Called on game start and disconnect so a window never leaks across two games (snapshots share
		/// the outgoing game's <see cref="Map"/>).
		/// </summary>
		public static void Clear(long nowMs)
		{
			Snapshots.Clear();
			capturedTotal = 0;
			nextCaptureAtRunTimeMs = nowMs + CaptureIntervalMs;
		}

		/// <summary>
		/// Captures a snapshot if <paramref name="nowMs"/> is due and the world is eligible. Called once
		/// per logic tick from the game loop, between world ticks so the frame-end task queue is drained
		/// and the fork boundary is clean. A failed or skipped attempt still reschedules only when a
		/// capture was actually attempted, so gameplay that is ineligible (paused, loading a save) gets
		/// its first snapshot promptly once it becomes eligible again, while capture failures do not
		/// retry on every tick.
		/// </summary>
		public static void MaybeSnapshot(OrderManager orderManager, long nowMs)
		{
			if (orderManager == null || nowMs < nextCaptureAtRunTimeMs)
				return;

			if (!IsEligible(orderManager, out _))
				return;

			nextCaptureAtRunTimeMs = nowMs + CaptureIntervalMs;
			Capture(orderManager, nowMs);
		}

		/// <summary>
		/// Deep-clones the order manager's current world into the window unconditionally (no timer).
		/// Returns false - without storing anything - if the world cannot be forked faithfully, so the
		/// window never holds a snapshot that would restore into a broken game.
		/// </summary>
		public static bool Capture(OrderManager orderManager, long nowMs)
		{
			var world = orderManager.World;

			// Defense in depth: a capture is speculative bookkeeping, and the live game must survive
			// whatever the cloner meets in a particular world's graph (the first live capture crashed
			// on a delegate the cloner could not rebind). Any failure discards the attempt and logs.
			var stopwatch = Stopwatch.StartNew();
			ObjectGraphCloner cloner;
			World fork;
			try
			{
				cloner = new ObjectGraphCloner(ShareLiveAsset);
				fork = cloner.Clone(world);
			}
			catch (Exception e)
			{
				stopwatch.Stop();
				Log.Write("debug", $"[WorldForks] snapshot aborted: the cloner threw {e.GetType().Name}: {e.Message}");
				return false;
			}

			stopwatch.Stop();

			if (cloner.Unsupported.Count > 0 || fork == null)
			{
				Log.Write("debug", "[WorldForks] snapshot aborted: the world graph contains values the cloner " +
					"cannot copy faithfully. Unsupported: " + string.Join(", ", cloner.Unsupported));
				return false;
			}

			// The share policy aliases the live OrderManager into the fork; a violation here would mean
			// restoring a world whose frame counters silently disagree with the pipeline it rejoins.
			if (!ReferenceEquals(fork.OrderManager, orderManager))
			{
				Log.Write("debug", "[WorldForks] snapshot aborted: the fork did not share the live OrderManager.");
				return false;
			}

			var saveLastFrame = orderManager.GameSaveLastFrame;
			var serverSave = Game.GetActiveServer()?.GameSave;
			var snapshot = new Snapshot(
				fork,
				world.WorldTick,
				orderManager.NetFrameNumber,
				saveLastFrame,
				saveLastFrame < 0 ? -1 : orderManager.NetFrameNumber - saveLastFrame,
				serverSave?.LastOrdersFrame ?? -1,
				serverSave?.BufferedOrderBytes ?? -1,
				orderManager.OrderQueueLength,
				nowMs,
				stopwatch.Elapsed.TotalMilliseconds,
				cloner.ClonedObjectCount);

			Snapshots.Add(snapshot);
			capturedTotal++;
			while (Snapshots.Count > Capacity)
				Snapshots.RemoveAt(0);

			Log.Write("debug", $"[WorldForks] snapshot {capturedTotal} (holding {Snapshots.Count}/{Capacity}): {snapshot}");
			return true;
		}

		/// <summary>
		/// Pops the most recent snapshot and makes it the active game state: the shared
		/// <see cref="OrderManager"/> adopts it as its world, the renderer re-binds to it, world traits
		/// that opted in via <see cref="IWorldAdopted"/> re-bind (the in-game widget tree reloads against
		/// the restored world), and the capture timer restarts. Returns false with a reason when the
		/// window is empty or the live world is not in a state that can be swapped.
		/// </summary>
		public static bool TryRestore(OrderManager orderManager, WorldRenderer worldRenderer, long nowMs,
			out string failureReason)
		{
			if (Snapshots.Count == 0)
			{
				failureReason = "the snapshot window is empty";
				return false;
			}

			var world = orderManager?.World;
			if (orderManager == null || !orderManager.GameStarted || world == null)
			{
				failureReason = "no game is running";
				return false;
			}

			if (world.Type != WorldType.Regular || world.IsReplay || world.IsLoadingGameSave)
			{
				failureReason = "the active world cannot be replaced (not a live regular game)";
				return false;
			}

			if (world.IsGameOver)
			{
				failureReason = "the game has ended";
				return false;
			}

			var snapshot = Snapshots[^1];
			Snapshots.RemoveAt(Snapshots.Count - 1);
			var fork = snapshot.World;

			if (!ReferenceEquals(fork.OrderManager, orderManager))
			{
				Snapshots.Add(snapshot);
				failureReason = "the snapshot belongs to a different order pipeline";
				return false;
			}

			// The swap only re-points owners; the outgoing world is untouched (its SyncHash is checked
			// unchanged around the block by Sync.RunUnsynced), so the hash guard passes while still
			// asserting we did not mutate synced state in unsynced code.
			Sync.RunUnsynced(world, () =>
			{
				orderManager.World = fork;
				worldRenderer?.AdoptWorld(fork);

				// Looping audio belongs to the outgoing world's actors.
				Game.Sound?.StopAudio();

				foreach (var trait in fork.WorldActor.TraitsImplementing<IWorldAdopted>())
					trait.WorldAdopted(fork, worldRenderer);

				Game.OnWorldReplaced(fork);
			});

			nextCaptureAtRunTimeMs = nowMs + CaptureIntervalMs;

			Log.Write("debug", $"[WorldForks] restored snapshot at {snapshot} (previous worldTick={world.WorldTick}); " +
				$"next capture in {CaptureIntervalMs} ms; {Snapshots.Count} snapshot(s) remaining");
			failureReason = null;
			return true;
		}

		/// <summary>
		/// Restores the most recent snapshot into the active game and restarts the capture timer - the
		/// rewind action itself, independent of how it is triggered (the F6 key today, a UI button
		/// tomorrow). Returns false and logs when nothing could be restored.
		/// </summary>
		public static bool RestoreLatest()
		{
			if (TryRestore(Game.OrderManager, Game.GetActiveWorldRenderer(), Game.RunTime, out var failureReason))
				return true;

			Log.Write("debug", $"[WorldForks] restore requested but nothing was restored: {failureReason}");
			return false;
		}

		/// <summary>
		/// Handles the rewind key before widgets see it. Returns true when the key was consumed (a live
		/// game is running, so bare F6 belongs to the rewind regardless of whether a restore was
		/// possible), false when the key should continue through the normal UI path (menus, shellmap,
		/// save loading, or an active text field).
		/// </summary>
		/// <remarks>
		/// Bare F6 is not a free key: the shipped mods bind it to Production06 (build palette tab 6) for
		/// players and StatisticsCombat for observers. Consuming it here deliberately shadows those
		/// bindings while this proof-of-concept is in the build; CTRL+F6 and every other F-key keep
		/// their normal meaning.
		/// </remarks>
		public static bool HandleKeyInput(KeyInput input)
		{
			if (input.Event != KeyInputEvent.Down || input.IsRepeat ||
				input.Key != Keycode.F6 || input.Modifiers != Modifiers.None)
				return false;

			var orderManager = Game.OrderManager;
			var world = orderManager?.World;
			if (orderManager == null || !orderManager.GameStarted || world == null ||
				world.Type != WorldType.Regular || world.IsReplay || world.IsLoadingGameSave ||
				Ui.KeyboardFocusWidget != null)
				return false;

			RestoreLatest();
			return true;
		}

		/// <summary>
		/// Whether the current world may be captured: a live, unpaused, regular single-human game that
		/// is not mid-save-restore and has no live Lua scripts (a fork would share the native interpreter
		/// state behind <see cref="Scripting.ScriptContext"/>, which the cloner cannot copy).
		/// </summary>
		public static bool IsEligible(OrderManager orderManager, out string reason)
		{
			var world = orderManager?.World;
			if (orderManager == null || !orderManager.GameStarted || world == null)
			{
				reason = "no game is running";
				return false;
			}

			if (world.Type != WorldType.Regular)
			{
				reason = "not a regular game world";
				return false;
			}

			if (world.IsReplay)
			{
				reason = "replays compare recorded sync hashes and must not diverge";
				return false;
			}

			if (world.IsLoadingGameSave)
			{
				reason = "the savegame is still loading";
				return false;
			}

			if (world.IsGameOver)
			{
				reason = "the game has ended";
				return false;
			}

			if (world.Paused)
			{
				reason = "the game is paused";
				return false;
			}

			// A restored world diverges from the other clients' timelines, so restoring would desync
			// them. Bots have no client of their own and are driven by the host, so solo-plus-bots is safe.
			if (orderManager.LobbyInfo.Clients.Count(c => !c.IsBot) > 1)
			{
				reason = "more than one human client is present";
				return false;
			}

			if (HasLiveScripts(world))
			{
				reason = "the map runs Lua scripts, which a fork cannot isolate";
				return false;
			}

			reason = null;
			return true;
		}

		static bool HasLiveScripts(World world)
		{
			foreach (var holder in world.WorldActor.TraitsImplementing<IOwnsScriptContext>())
				if (holder.ScriptContext is { HasScripts: true })
					return true;

			return false;
		}

		/// <summary>
		/// The <see cref="ObjectGraphCloner"/> share-instance policy for live-game snapshots: everything
		/// the headless MCTS fork aliases (immutable rules, map, mod services, graphics assets) plus the
		/// live order pipeline. Sharing the <see cref="OrderManager"/> is what makes restore a pure
		/// re-point: the fork reads the same frame counters, lobby and connection the game continues on,
		/// and <see cref="World.IsLoadingGameSave"/> stays consistent with the live pipeline instead of
		/// freezing at the snapshot's frame. It also stops the traversal at the network connection, so no
		/// socket, thread or recorder is ever cloned.
		/// </summary>
		/// <remarks>
		/// Aliasing also prunes traversal: anything reachable only through a shared instance is shared
		/// too. If a restore ever renders or simulates wrongly, the first suspect is an object wrongly
		/// aliased here that in fact carries mutable per-world state (the rung-5 TraitDictionary lesson).
		/// </remarks>
		static bool ShareLiveAsset(object o)
		{
			switch (o)
			{
				// Reflection objects are immutable, process-wide singletons and TraitDictionary keys -
				// cloning a Type would silently empty every type-keyed lookup.
				case System.Reflection.MemberInfo:

				// The map and mod-wide services: read-only after load, shared by every world.
				case Map:
				case ModData:
				case Ruleset:
				case SequenceSet:

				// The live order pipeline and its connection (see the method docs).
				case OrderManager:
				case OpenRA.Network.IConnection:

				// A compiled condition/expression evaluator (BooleanExpression, IntegerExpression, ...):
				// pure over its symbols argument, with no world state captured, so sharing it costs
				// nothing and skips the DynamicMethod-backed function it holds - a clone cannot rebind
				// that to a cloned target (live capture failure #1).
				case VariableExpression:
					return true;
			}

			var type = o.GetType();

			// Immutable rules/asset info objects (TraitInfo, ActorInfo, weapon infos, ...).
			if (type.Name.EndsWith("Info", System.StringComparison.Ordinal) ||
				type.Name.EndsWith("Infos", System.StringComparison.Ordinal))
				return true;

			// Sprite/terrain/renderer asset content, including the WorldRenderer itself: the fork keeps
			// rendering through the same renderer instance, which is what lets AdoptWorld re-bind rather
			// than rebuild, and what leaves traits that cached a WorldRenderer pointing at the live one.
			if ((type.Namespace ?? "").StartsWith("OpenRA.Graphics", System.StringComparison.Ordinal))
				return true;

			// The widget tree is process-level UI. World events reach it (widgets subscribe to world
			// events, e.g. the chat logic's GameOver handler), but a fork never renders or drives it -
			// the live game rebuilds the UI against the restored world through IWorldAdopted. Aliasing
			// prunes traversal at the UI boundary: widget bases live in OpenRA.Widgets, mods' widgets
			// and their logic (including compiler-generated closures) in e.g.
			// OpenRA.Mods.Common.Widgets[.Logic].
			if ((type.Namespace ?? "").Contains(".Widgets", System.StringComparison.Ordinal))
				return true;

			return false;
		}
	}
}
