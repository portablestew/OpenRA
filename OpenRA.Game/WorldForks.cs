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
	/// evicting the snapshot farthest in game time from the live tick. <see cref="Undo"/> and <see cref="Redo"/>
	/// step to the nearest stored snapshot in their direction that is at least <see cref="MinGapMs"/> from the
	/// live tick, swap a fresh clone of it in for the live world, and restart the capture timer from the restored
	/// tick. Restoring never consumes a snapshot, so the window keeps every checkpoint for further steps.
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

		/// <summary>
		/// Maximum snapshots held at once; when exceeded, the one farthest in game time from the live tick is evicted.
		/// </summary>
		public const int Capacity = 32;

		/// <summary>Game-clock milliseconds a snapshot must be from another to be kept alongside it.</summary>
		public const long MinGapMs = 5000;

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

			/// <summary>Game time in milliseconds (<see cref="World.WorldTick"/> times <see cref="World.Timestep"/>); orders the window.</summary>
			public readonly long GameMs;

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

			/// <summary>Wall-clock duration of the fork itself, in milliseconds.</summary>
			public readonly double ForkMs;

			/// <summary>Distinct instances the cloner registered - the size of the cloned state graph.</summary>
			public readonly int ClonedObjects;

			internal Snapshot(World world, int worldTick, long gameMs, int netFrame, int saveLastFrame, int framesSinceSave,
				int serverSaveLastFrame, long serverBufferedOrderBytes, int orderQueueDepth,
				double forkMs, int clonedObjects)
			{
				World = world;
				WorldTick = worldTick;
				GameMs = gameMs;
				NetFrame = netFrame;
				SaveLastFrame = saveLastFrame;
				FramesSinceSave = framesSinceSave;
				ServerSaveLastFrame = serverSaveLastFrame;
				ServerBufferedOrderBytes = serverBufferedOrderBytes;
				OrderQueueDepth = orderQueueDepth;
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

		/// <summary>Snapshots ordered by <see cref="Snapshot.GameMs"/>, oldest first.</summary>
		static readonly List<Snapshot> Snapshots = [];

		/// <summary>Game time in milliseconds before which no capture is attempted.</summary>
		static long nextCaptureAtGameMs;

		/// <summary>Number of snapshots captured over the lifetime of the current window (for logging).</summary>
		static int capturedTotal;

		/// <summary>Number of snapshots currently held; 0 when the window is empty.</summary>
		public static int Count => Snapshots.Count;

		/// <summary>Game time of <paramref name="world"/> in milliseconds, the clock the window is ordered by.</summary>
		internal static long GameMsOf(World world) => (long)world.WorldTick * world.Timestep;

		/// <summary>
		/// Stores <paramref name="snapshot"/> in game-time order, first dropping every snapshot within
		/// <see cref="MinGapMs"/> of it so the window keeps one checkpoint per gap.
		/// </summary>
		internal static void InsertWithGapDedupe(Snapshot snapshot)
		{
			Snapshots.RemoveAll(s => Math.Abs(s.GameMs - snapshot.GameMs) < MinGapMs);

			var index = Snapshots.FindIndex(s => s.GameMs > snapshot.GameMs);
			if (index < 0)
				Snapshots.Add(snapshot);
			else
				Snapshots.Insert(index, snapshot);
		}

		/// <summary>
		/// Evicts the snapshots farthest in game time from <paramref name="liveMs"/> until the window holds at
		/// most <paramref name="capacity"/>. Ties go to the older snapshot, and <paramref name="keep"/> (the
		/// snapshot just written) is never evicted.
		/// </summary>
		internal static void EvictFarthest(long liveMs, Snapshot keep, int capacity)
		{
			while (Snapshots.Count > capacity)
			{
				var victim = -1;
				var victimDistance = -1L;

				// Snapshots are in ascending game time, so the strict comparison keeps the older of two equally distant ones.
				for (var i = 0; i < Snapshots.Count; i++)
				{
					if (ReferenceEquals(Snapshots[i], keep))
						continue;

					var distance = Math.Abs(Snapshots[i].GameMs - liveMs);
					if (distance > victimDistance)
					{
						victim = i;
						victimDistance = distance;
					}
				}

				if (victim < 0)
					return;

				Snapshots.RemoveAt(victim);
			}
		}

		/// <summary>The newest snapshot at least <see cref="MinGapMs"/> older than <paramref name="liveMs"/>, or null.</summary>
		internal static Snapshot FindUndo(long liveMs)
		{
			for (var i = Snapshots.Count - 1; i >= 0; i--)
				if (Snapshots[i].GameMs <= liveMs - MinGapMs)
					return Snapshots[i];

			return null;
		}

		/// <summary>The oldest snapshot at least <see cref="MinGapMs"/> newer than <paramref name="liveMs"/>, or null.</summary>
		internal static Snapshot FindRedo(long liveMs)
		{
			foreach (var snapshot in Snapshots)
				if (snapshot.GameMs >= liveMs + MinGapMs)
					return snapshot;

			return null;
		}

		/// <summary>
		/// Drops every snapshot and schedules the next capture one interval into game time, the clock a new
		/// world starts at. Called on game start and disconnect so a window never leaks across two games
		/// (snapshots share the outgoing game's <see cref="Map"/>).
		/// </summary>
		public static void Clear()
		{
			Snapshots.Clear();
			capturedTotal = 0;
			nextCaptureAtGameMs = CaptureIntervalMs;
		}

		/// <summary>
		/// Captures a snapshot if the live world's game time is due and the world is eligible. Called once
		/// per logic tick from the game loop, between world ticks so the frame-end task queue is drained
		/// and the fork boundary is clean. The timer runs on game time, so a pause does not advance it, and
		/// a restore moves it with the restored tick. A failed or skipped attempt still reschedules only when
		/// a capture was actually attempted, so gameplay that is ineligible (paused, loading a save) gets its
		/// first snapshot promptly once it becomes eligible again, while capture failures do not retry on
		/// every tick.
		/// </summary>
		public static void MaybeSnapshot(OrderManager orderManager)
		{
			if (orderManager?.World is { } world)
				MaybeSnapshot(orderManager, GameMsOf(world));
		}

		/// <summary>The timer gate of <see cref="MaybeSnapshot(OrderManager)"/>, with the live game time supplied.</summary>
		internal static void MaybeSnapshot(OrderManager orderManager, long liveMs)
		{
			if (orderManager == null || liveMs < nextCaptureAtGameMs)
				return;

			if (!IsEligible(orderManager, out _))
				return;

			nextCaptureAtGameMs = liveMs + CaptureIntervalMs;
			Capture(orderManager);
		}

		/// <summary>
		/// Deep-clones <paramref name="source"/> under the live share policy and checks that the copy can be
		/// adopted faithfully. Returns null, after logging why, when the cloner throws, when it met a value it
		/// cannot copy, or when the copy did not share <paramref name="orderManager"/>. Capture and restore both
		/// build their worlds here, so a restore adopts exactly what a capture would have produced.
		/// </summary>
		static World CloneChecked(World source, OrderManager orderManager, out int clonedObjects)
		{
			clonedObjects = 0;

			// Defense in depth: a capture is speculative bookkeeping, and the live game must survive
			// whatever the cloner meets in a particular world's graph (the first live capture crashed
			// on a delegate the cloner could not rebind). Any failure discards the attempt and logs.
			ObjectGraphCloner cloner;
			World fork;
			try
			{
				cloner = new ObjectGraphCloner(ShareLiveAsset);
				fork = cloner.Clone(source);
			}
			catch (Exception e)
			{
				Log.Write("debug", $"[WorldForks] clone aborted: the cloner threw {e.GetType().Name}: {e.Message}");
				return null;
			}

			if (cloner.Unsupported.Count > 0 || fork == null)
			{
				Log.Write("debug", "[WorldForks] clone aborted: the world graph contains values the cloner " +
					"cannot copy faithfully. Unsupported: " + string.Join(", ", cloner.Unsupported));
				return null;
			}

			// The share policy aliases the live OrderManager into the fork; a violation here would mean
			// restoring a world whose frame counters silently disagree with the pipeline it rejoins.
			if (!ReferenceEquals(fork.OrderManager, orderManager))
			{
				Log.Write("debug", "[WorldForks] clone aborted: the fork did not share the live OrderManager.");
				return null;
			}

			clonedObjects = cloner.ClonedObjectCount;
			return fork;
		}

		/// <summary>
		/// Deep-clones the order manager's current world into the window unconditionally (no timer).
		/// Returns false - without storing anything - if the world cannot be forked faithfully, so the
		/// window never holds a snapshot that would restore into a broken game.
		/// </summary>
		public static bool Capture(OrderManager orderManager)
		{
			var world = orderManager.World;

			var stopwatch = Stopwatch.StartNew();
			var fork = CloneChecked(world, orderManager, out var clonedObjects);
			stopwatch.Stop();

			if (fork == null)
				return false;

			var saveLastFrame = orderManager.GameSaveLastFrame;
			var serverSave = Game.GetActiveServer()?.GameSave;
			var snapshot = new Snapshot(
				fork,
				world.WorldTick,
				GameMsOf(world),
				orderManager.NetFrameNumber,
				saveLastFrame,
				saveLastFrame < 0 ? -1 : orderManager.NetFrameNumber - saveLastFrame,
				serverSave?.LastOrdersFrame ?? -1,
				serverSave?.BufferedOrderBytes ?? -1,
				orderManager.OrderQueueLength,
				stopwatch.Elapsed.TotalMilliseconds,
				clonedObjects);

			InsertWithGapDedupe(snapshot);
			capturedTotal++;
			EvictFarthest(GameMsOf(world), snapshot, Capacity);

			Log.Write("debug", $"[WorldForks] snapshot {capturedTotal} (holding {Snapshots.Count}/{Capacity}): {snapshot}");
			return true;
		}

		/// <summary>
		/// Makes the newest snapshot at least <see cref="MinGapMs"/> older than <paramref name="liveMs"/> the
		/// active game state. See <see cref="TryRestore"/> for what adopting a snapshot involves.
		/// </summary>
		public static bool Undo(OrderManager orderManager, WorldRenderer worldRenderer, long liveMs,
			out string failureReason)
		{
			return TryRestore(orderManager, worldRenderer, liveMs, FindUndo, out failureReason);
		}

		/// <summary>
		/// Makes the oldest snapshot at least <see cref="MinGapMs"/> newer than <paramref name="liveMs"/> the
		/// active game state. See <see cref="TryRestore"/> for what adopting a snapshot involves.
		/// </summary>
		public static bool Redo(OrderManager orderManager, WorldRenderer worldRenderer, long liveMs,
			out string failureReason)
		{
			return TryRestore(orderManager, worldRenderer, liveMs, FindRedo, out failureReason);
		}

		/// <summary>
		/// Adopts a fresh clone of the snapshot <paramref name="find"/> picks for <paramref name="liveMs"/> as the
		/// active game state: the shared <see cref="OrderManager"/> adopts it as its world, the renderer re-binds
		/// to it, world traits that opted in via <see cref="IWorldAdopted"/> re-bind (the in-game widget tree
		/// reloads against the restored world), and the capture timer restarts from the restored tick. The stored
		/// snapshot is cloned, never handed out, so it stays in the window and can be restored again. Returns false
		/// with a reason when the window is empty, no snapshot is in range, the clone fails, or the live world is
		/// not in a state that can be swapped; nothing changes in that case.
		/// </summary>
		static bool TryRestore(OrderManager orderManager, WorldRenderer worldRenderer, long liveMs,
			Func<long, Snapshot> find, out string failureReason)
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

			var snapshot = find(liveMs);
			if (snapshot == null)
			{
				failureReason = $"no snapshot is at least {MinGapMs} ms away from the live tick in that direction";
				return false;
			}

			// The clone is what gets adopted, so the stored snapshot's world is never mutated by play and a
			// later restore of the same snapshot starts from the same captured state.
			var fork = CloneChecked(snapshot.World, orderManager, out _);
			if (fork == null)
			{
				failureReason = "the snapshot could not be copied into a world that can be adopted";
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

			// The timer restarts from the tick the restore landed on, because the game clock has just jumped back.
			nextCaptureAtGameMs = GameMsOf(fork) + CaptureIntervalMs;

			Log.Write("debug", $"[WorldForks] restored snapshot at {snapshot} (previous worldTick={world.WorldTick}); " +
				$"next capture in {CaptureIntervalMs} ms of game time; {Snapshots.Count} snapshot(s) held");
			failureReason = null;
			return true;
		}

		/// <summary>
		/// Handles the snapshot undo and redo keys before widgets see them. Returns true only when a restore
		/// happened, so the key is consumed only then. Otherwise it continues through the normal UI path: no
		/// live game, a focused text field, or no snapshot in range to restore to.
		/// </summary>
		/// <remarks>
		/// The keys are the <c>SnapshotUndo</c>, <c>SnapshotRedo</c> and <c>SnapshotRedoAlt</c> hotkeys, matched
		/// exactly by <see cref="MatchesSnapshotHotkey"/>, so Ctrl+Z is not Ctrl+Shift+Z and bare Z stays with Sell.
		/// Key repeat is ignored, so holding a key steps back once.
		/// </remarks>
		public static bool HandleKeyInput(KeyInput input)
		{
			if (input.Event != KeyInputEvent.Down || input.IsRepeat)
				return false;

			var isUndo = MatchesSnapshotHotkey(input, "SnapshotUndo");
			var isRedo = !isUndo && (MatchesSnapshotHotkey(input, "SnapshotRedo") ||
				MatchesSnapshotHotkey(input, "SnapshotRedoAlt"));
			if (!isUndo && !isRedo)
				return false;

			var orderManager = Game.OrderManager;
			var world = orderManager?.World;
			if (orderManager == null || !orderManager.GameStarted || world == null ||
				world.Type != WorldType.Regular || world.IsReplay || world.IsLoadingGameSave ||
				Ui.KeyboardFocusWidget != null)
				return false;

			var restored = isUndo
				? Undo(orderManager, Game.GetActiveWorldRenderer(), GameMsOf(world), out var failureReason)
				: Redo(orderManager, Game.GetActiveWorldRenderer(), GameMsOf(world), out failureReason);

			if (!restored)
				Log.Write("debug", $"[WorldForks] snapshot key pressed but nothing was restored: {failureReason}");

			return restored;
		}

		/// <summary>
		/// Whether the key press is exactly the named hotkey. A name with no definition never matches, because the
		/// hotkey manager returns <see cref="Hotkey.Invalid"/> for it.
		/// </summary>
		internal static bool MatchesSnapshotHotkey(KeyInput input, string name)
		{
			var hotkey = Game.ModData.Hotkeys[name];
			return hotkey.GetValue() != Hotkey.Invalid && hotkey.IsActivatedBy(input);
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
		/// the headless MCTS fork aliases (immutable rules, the map's process-wide parts, mod services,
		/// graphics assets) plus the live order pipeline. Sharing the <see cref="OrderManager"/> is what
		/// makes restore a pure re-point: the fork reads the same frame counters, lobby and connection the
		/// game continues on, and <see cref="World.IsLoadingGameSave"/> stays consistent with the live pipeline
		/// instead of freezing at the snapshot's frame. It also stops the traversal at the network connection,
		/// so no socket, thread or recorder is ever cloned.
		/// </summary>
		/// <remarks>
		/// Aliasing also prunes traversal: anything reachable only through a shared instance is shared
		/// too. If a restore ever renders or simulates wrongly, the first suspect is an object wrongly
		/// aliased here that in fact carries mutable per-world state (the rung-5 TraitDictionary lesson).
		/// </remarks>
		internal static bool ShareLiveAsset(object o)
		{
			switch (o)
			{
				// Reflection objects are immutable, process-wide singletons and TraitDictionary keys -
				// cloning a Type would silently empty every type-keyed lookup.
				case System.Reflection.MemberInfo:

				// The mod-wide services: read-only after load, shared by every world.
				case ModData:
				case Ruleset:
				case SequenceSet:

				// The process-wide parts of the map. The map itself is not shared: its terrain layers, caches and
				// change subscribers are cloned, so a fork's terrain writes reach the fork's own Locomotor and
				// path finder, and the outgoing world's writes stay out of the fork. The grid is a mod-wide
				// singleton, the package holds open archive handles, and the yaml definitions are read-only after load.
				case MapGrid:
				case FileSystem.IReadOnlyPackage:
				case MiniYaml:
				case MiniYamlNode:

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

			// Animations and voxel model animations hold closures over their owning actor's traits (facing, offset,
			// disable). Sharing one would leave those closures reading the outgoing world's actors, so they are cloned
			// and the closures are remapped onto the fork's actors. The OpenRA.Graphics rule below must not catch them.
			// Arrays of them are excluded too: an array takes its element's namespace, so the backing array of a
			// List<ModelAnimation> (RenderVoxels' component list) would otherwise be shared and keep the outgoing structs.
			if (o is Animation or AnimationWithOffset or ModelAnimation or Animation[] or AnimationWithOffset[] or ModelAnimation[])
				return false;

			// The platform layer's GPU and audio handles (shaders, buffers, textures, the threaded graphics context and
			// its render queue). A clone would hold a copy of a GPU handle or a queue that no render thread drains, so
			// the post-process passes and other renderers that cache these must keep the live ones.
			if ((type.Namespace ?? "").StartsWith("OpenRA.Platforms", System.StringComparison.Ordinal))
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
