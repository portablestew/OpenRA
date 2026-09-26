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
using System.IO;
using System.Linq;
using NUnit.Framework;
using OpenRA.Graphics;
using OpenRA.Network;
using OpenRA.Primitives;
using OpenRA.Support;
using OpenRA.Traits;
using OpenRA.Widgets;

namespace OpenRA.Test
{
	/// <summary>
	/// Boots the shipped <c>ra</c> mod once for the whole assembly. The engine keeps mod state in
	/// process-global statics (<see cref="Game.Settings"/>, <see cref="Game.ModData"/>,
	/// <see cref="Game.Sound"/>) that cannot be re-initialised within a process, so every fixture that
	/// needs a real <see cref="World"/> shares this one bootstrap rather than each doing its own.
	/// Mirrors the dedicated server bootstrap (OpenRA.Server/Program.cs).
	/// </summary>
	static class HeadlessMod
	{
		static bool initialized;
		static string unavailableReason;

		public static ModData ModData { get; private set; }

		/// <summary>The map every headless world in this assembly is built on.</summary>
		public static string MapUid { get; private set; }

		/// <summary>
		/// Idempotently initialises the mod. Call from a fixture's setup; if the mod or a playable map is
		/// unavailable the calling test is ignored rather than failed, since that is an environment
		/// problem (missing game content) and not a regression.
		/// </summary>
		public static void Require()
		{
			if (!initialized)
			{
				initialized = true;
				try
				{
					Initialize();
				}
				catch (IgnoreException e)
				{
					unavailableReason = e.Message;
				}
			}

			if (unavailableReason != null)
				Assert.Ignore(unavailableReason);
		}

		static void Initialize()
		{
			// The engine directory must point at the repository root: the test dll runs from bin/, but the
			// mods and their shared content (e.g. the ^EngineDir|mods/common package the ra mod mounts) live
			// at bin/../. The launch scripts do the same via Engine.EngineDir="..". A relative override is
			// resolved against BinDir. Both overrides are process-global one-shots that throw if the value has
			// already been accessed (e.g. by another fixture in the same run); tolerate that rather than reading
			// the property to probe it, which would itself lock the value in.
			try { Platform.OverrideEngineDir(".."); }
			catch (InvalidOperationException) { }

			// Deliberately do NOT override the support directory. An earlier version pointed it at an empty temp
			// dir to stay off the developer's real settings/replays, but that also hid installed game content
			// (e.g. ra's terrain sprites), which masked the real headless code path and produced spurious
			// content-not-found failures. The end goal is to boot a real, unmodified map with its full ruleset,
			// which requires the installed content, so we use the normal support dir. The tests only read content
			// and never write settings/replays (Sound is muted, no renderer, no NetworkConnection recorder).

			// The engine writes to named log channels from PerfTimer/PerfSample and various traits. Writing
			// to an unregistered channel throws on the logging thread and crashes the test host, so register
			// the channels the construction/tick path touches. A null filename makes the channel a no-op sink
			// (no file created), exactly as OpenRA.Utility does for headless runs.
			foreach (var channel in new[] { "perf", "debug", "sound", "graphics", "server", "sync", "traitreport", "nat", "geoip", "client", "lua" })
				Log.AddChannel(channel, null);

			// HACK: the engine assumes Game.Settings is set (see OpenRA.Server/Program.cs).
			Game.InitializeSettings(Arguments.Empty);
			Game.Settings.Sound.Mute = true;

			var mods = new InstalledMods([Path.Combine(Platform.EngineDir, "mods")], []);
			if (!mods.ContainsKey("ra"))
				Assert.Ignore("The 'ra' mod is not installed next to the test binaries.");

			// useLoadScreen defaults to false: no renderer is touched during construction.
			ModData = Game.ModData = new ModData(mods["ra"], mods);
			ModData.MapCache.LoadPreviewImages = false;
			ModData.MapCache.LoadMaps(ModData);

			// The World ctor builds the mod's DefaultOrderGenerator, and UnitOrderGenerator reads cursor names
			// from ChromeMetrics in its field initialisers. ChromeMetrics.Initialize only parses the metrics
			// yaml, so it is safe headlessly - unlike the rest of ModData.InitializeLoaders (ChromeProvider,
			// Ui, sound loaders), which we deliberately skip because it needs a renderer.
			ChromeMetrics.Initialize(ModData);

			// A dummy sound engine is enough: the World ctor writes Game.Sound.SoundVolumeModifier,
			// and traits may emit notifications during LoadComplete. The Default platform is loaded by
			// reflection from bin (exactly as the game does), and CreateSound falls back to a no-op
			// DummySoundEngine when no audio device is available - so this needs no compile-time reference
			// to OpenRA.Platforms.Default.
			//
			// DisableAllSounds is the engine's own headless-audio switch (World.LoadGameSave flips it the same
			// way while replaying a save). It is NOT the same as Settings.Sound.Mute, which only zeroes the
			// engine volume: Play/PlayPredefined still index the `sounds` dictionary, which is null here because
			// we deliberately skip ModData.InitializeLoaders (it loads sound content through a path that needs a
			// renderer). Weapons firing (Armament -> Sound.Play) and transforms (Transforms -> PlayNotification)
			// both reach that dictionary; DisableAllSounds makes both short-circuit before the null deref, which
			// is exactly what a headless sim wants - sound is pure presentation and never touches SharedRandom.
			var platform = Game.CreatePlatform("Default");
			Game.Sound = new Sound(platform, Game.Settings.Sound) { DisableAllSounds = true };

			MapUid = SelectSimpleMap();
		}

		/// <summary>
		/// Pick a small, simple, playable skirmish map deterministically. Prefer a short list of known
		/// two-player maps; otherwise fall back to the Available map with the fewest cells that still has
		/// at least one playable player.
		/// </summary>
		static string SelectSimpleMap()
		{
			var preferred = new[] { "sidestep", "singles", "doubles", "discovery", "pitfight" };

			var available = ModData.MapCache
				.Where(p => p.Status == MapStatus.Available && p.Players != null && p.Players.Players.Values.Any(pr => pr.Playable))
				.ToList();

			if (available.Count == 0)
				Assert.Ignore("No playable maps are available for the 'ra' mod.");

			foreach (var name in preferred)
			{
				var match = available.FirstOrDefault(p =>
					p.Uid != null &&
					(p.Title?.Contains(name, StringComparison.OrdinalIgnoreCase) == true));
				if (match != null)
					return match.Uid;
			}

			// Smallest map by area is a reasonable "simple" proxy and keeps ticking cheap.
			return available
				.OrderBy(p => (long)p.Bounds.Width * p.Bounds.Height)
				.First().Uid;
		}

		/// <summary>
		/// Releases the shared <see cref="ModData"/>. Called once per assembly by
		/// <see cref="HeadlessModTeardown"/>; individual fixtures must not dispose it.
		/// </summary>
		public static void Shutdown()
		{
			ModData?.Dispose();
			ModData = null;
		}
	}

	/// <summary>
	/// Assembly-level teardown for the shared mod. A SetUpFixture in the root namespace brackets every
	/// fixture in the assembly, which is what lets multiple fixtures share one <see cref="ModData"/>
	/// without any one of them disposing content the others still need.
	/// </summary>
	[SetUpFixture]
	sealed class HeadlessModTeardown
	{
		[OneTimeTearDown]
		public void DisposeMod()
		{
			HeadlessMod.Shutdown();
		}
	}

	/// <summary>
	/// A live, deterministic, headless <see cref="World"/> plus the machinery to drive it: advance ticks,
	/// inject orders as though a player had issued them, and spawn actors at chosen cells.
	/// <para>
	/// Order timing is the subtle part. <see cref="OrderManager.IssueOrder"/> only buffers; the order is
	/// sent during the next <see cref="OrderManager.TryTick"/>, and <see cref="EchoConnection"/> re-stamps
	/// it to <c>frame + 1</c>, so it is resolved (and its activity queued) at the start of the *following*
	/// tick, immediately before that tick's <see cref="World.Tick"/>. Net effect with
	/// <c>NetFrameInterval = 1</c>: an order issued before tick N first takes effect during tick N+1.
	/// <see cref="OrderLatencyTicks"/> names that constant so tests can assert on it instead of
	/// hard-coding it.
	/// </para>
	/// </summary>
	sealed class HeadlessWorld : IDisposable
	{
		/// <summary>
		/// World ticks between issuing an order and the tick in which its effect first appears. One, because
		/// <see cref="EchoConnection"/>'s receive step projects order packets forward by a single net frame
		/// and every net frame is a world tick here.
		/// </summary>
		public const int OrderLatencyTicks = 1;

		public World World { get; }
		public OrderManager OrderManager { get; }

		/// <summary>The player owned by the local client. Actors must be owned by this player for orders to validate.</summary>
		public Player LocalPlayer { get; }

		/// <summary>
		/// A second playable player, hostile to <see cref="LocalPlayer"/>, or null if the map has only one
		/// playable slot. Backed by a bot client so it does not stall the sim (a second *human* client would:
		/// <c>EchoConnection</c> only ever fills the local client's order queue, and
		/// <c>IsReadyForNextFrame</c> waits on every non-bot client). The bot has no module orders here, so it
		/// is an inert owner - useful purely as an enemy for combat tests.
		/// </summary>
		public Player EnemyPlayer { get; }

		/// <summary>Number of world ticks advanced so far.</summary>
		public int TicksAdvanced { get; private set; }

		HeadlessWorld(World world, OrderManager om, Player localPlayer, Player enemyPlayer)
		{
			World = world;
			OrderManager = om;
			LocalPlayer = localPlayer;
			EnemyPlayer = enemyPlayer;
		}

		/// <summary>
		/// Deep-clones this world's live simulation state into an independent fork that continues from the
		/// exact same point. This is the rung-5 payload: from one game position, spin up a rollout that ticks
		/// a divergent future without touching the parent. The proof of faithfulness is the oracle - tick the
		/// parent and the fork forward and their <see cref="World.SyncHash"/> sequences must agree.
		/// <para>
		/// The whole reachable mutable graph is cloned in one pass by <see cref="ObjectGraphCloner"/>: the
		/// <see cref="World"/>, every actor and trait, players, activities, effects, the actor map, the synced
		/// RNG, and the <see cref="OrderManager"/> + <see cref="EchoConnection"/> with their frame counters and
		/// in-flight order queues (so the fork's order pipeline is a faithful continuation, not a reset). The
		/// cloner remaps delegates onto the clones, so simulation callbacks (condition notifiers, actor-map
		/// triggers, <c>World.ActorAdded</c>) come across wired to the fork. Immutable, externally-owned asset
		/// data is aliased rather than copied via <see cref="ShareAsset"/> so a fork is cheap and never
		/// duplicates rules/map/sequences.
		/// </para>
		/// <para>
		/// Must be called between ticks. <see cref="World.Tick"/> drains its frame-end task queue before
		/// returning, so after any <see cref="TickOnce"/> there are no pending closures capturing the parent
		/// to carry across; the fork boundary is clean.
		/// </para>
		/// </summary>
		public HeadlessWorld Fork()
		{
			var cloner = new ObjectGraphCloner(ShareAsset);
			var forkedWorld = cloner.Clone(World);

			Assert.That(cloner.Unsupported, Is.Empty,
				"The world graph contained values the cloner could not copy, so the fork is not a faithful deep " +
				"copy. Each entry is a field path; either it is genuinely uncloneable (e.g. a multidimensional " +
				"array) and needs cloner support, or it is externally-owned asset data that ShareAsset should be " +
				"aliasing. Unsupported: " + string.Join(", ", cloner.Unsupported));

			// The OrderManager is part of the cloned graph, so the fork already has its own independent OM and
			// EchoConnection (sharing no state with the parent) while preserving the parent's frame counters.
			var forkedOm = forkedWorld.OrderManager;

			// Resolve the fork's players by the same client-index lookup Create uses, so the wrapper's
			// LocalPlayer/EnemyPlayer point at the cloned Player instances rather than the parent's.
			var localClientId = forkedOm.Connection.LocalClientId;
			var forkedLocal = forkedWorld.LocalPlayer ??
				forkedWorld.Players.First(p => p.ClientIndex == localClientId && p.PlayerReference.Playable);
			var forkedEnemy = EnemyPlayer == null
				? null
				: forkedWorld.Players.FirstOrDefault(p => p.ClientIndex == EnemyPlayer.ClientIndex && p.PlayerReference.Playable);

			return new HeadlessWorld(forkedWorld, forkedOm, forkedLocal, forkedEnemy) { TicksAdvanced = TicksAdvanced };
		}

		/// <summary>
		/// The <see cref="ObjectGraphCloner"/> share-instance policy for a world fork: returns true for
		/// instances that are immutable or externally owned after world load and so must be aliased rather
		/// than deep-copied. These are the mod's rules, map, terrain, sequences and asset definitions -
		/// large, read-only, and shared by every rollout. Everything not matched here is treated as mutable
		/// simulation state and cloned.
		/// </summary>
		/// <remarks>
		/// Aliasing an object also prunes the clone traversal at that point: the cloner does not recurse into
		/// a shared instance, so anything reachable only through these assets is shared too. The set is
		/// matched by concrete type. If a fork ever diverges from its parent's SyncHash, the first suspect is
		/// an object wrongly aliased here that in fact carries mutable per-world state.
		/// </remarks>
		static bool ShareAsset(object o)
		{
			switch (o)
			{
				// Reflection objects are immutable, process-wide singletons. They are also used as dictionary
				// keys - TraitDictionary is keyed by trait Type - so they MUST be shared: cloning a Type would
				// produce a key that no typeof(T) lookup could ever match, silently emptying the dictionary.
				// (System.Type derives from MemberInfo; both are listed for clarity.)
				case System.Reflection.MemberInfo:

				// The map and mod-wide services. Map is read-only terrain/rules after load; ModData owns the
				// object creator, asset loaders, rules cache and sequences - all shared, never per-world.
				case Map:
				case ModData:
				case Ruleset:
				case SequenceSet:
					return true;
			}

			var type = o.GetType();
			var ns = type.Namespace ?? "";

			// Immutable rules/asset info objects (TraitInfo, WeaponInfo, ActorInfo, projectile/warhead infos,
			// terrain info, sprite sequences, ...) live in the rules graph and are shared across every actor
			// of a type. They are constructed once at load from yaml and not mutated during the sim.
			if (type.Name.EndsWith("Info", StringComparison.Ordinal) ||
				type.Name.EndsWith("Infos", StringComparison.Ordinal))
				return true;

			// Sprite/terrain asset content that RenderSprites and the sequence layer reference. Cheap to share
			// and never part of synced state.
			if (ns.StartsWith("OpenRA.Graphics", StringComparison.Ordinal))
				return true;

			return false;
		}

		/// <summary>
		/// Builds a headless world for the shared map at the given seed, with a single local human client
		/// occupying the first playable slot. A single non-bot client is deliberate: the
		/// <see cref="EchoConnection"/> loopback only produces orders for the local client, and
		/// <c>OrderManager.IsReadyForNextFrame</c> requires every non-bot client's order queue to be filled,
		/// so more than one human client would stall the sim.
		/// </summary>
		public static HeadlessWorld Create(int seed)
		{
			var om = new OrderManager(new EchoConnection());

			// Note: we deliberately do NOT set the process-global Game.OrderManager. Engine code on the
			// construction/sim path resolves the local player and host status from the world's own
			// OrderManager (World.LocalClientId / World.IsHost), so a headless world stands on its own OM.
			// This is what lets multiple worlds coexist - e.g. concurrent MCTS rollouts on worker threads -
			// without a shared static to clobber.
			var modData = HeadlessMod.ModData;
			var mapUid = HeadlessMod.MapUid;

			var lobby = om.LobbyInfo;
			lobby.GlobalSettings.RandomSeed = seed;
			lobby.GlobalSettings.Map = mapUid;

			// Every local frame is a net frame, so the tick loop below advances one world tick per iteration.
			lobby.GlobalSettings.NetFrameInterval = 1;

			var preview = modData.MapCache[mapUid];
			var mapPlayers = preview.Players.Players;

			// Build the map up front (before wiring the lobby's bot client) so we can read the player actor's
			// rules to pick a bot type that actually exists in this mod.
			var map = preview.ToMap();

			var playableSlots = mapPlayers
				.Where(kv => kv.Value.Playable)
				.Select(kv => kv.Key)
				.OrderBy(k => k, StringComparer.Ordinal)
				.ToList();

			// The local client occupies the first playable slot; a bot occupies the second (if any) as the
			// enemy. Slot assignment is deterministic (ordinal order) so both clients - and therefore both
			// Players and their spawn locations - are stable across seeds.
			var localSlot = playableSlots[0];
			var enemySlot = playableSlots.Count > 1 ? playableSlots[1] : null;

			foreach (var kv in mapPlayers.Where(p => p.Value.Playable))
				lobby.Slots[kv.Key] = new Session.Slot
				{
					PlayerReference = kv.Key,
					Closed = false,

					// AllowBots must be true for the enemy slot or the bot client's Player would not be
					// created; leaving it true for every slot is harmless since we assign clients explicitly.
					AllowBots = true,
				};

			// Team 0 means "no team". CreateMapPlayers.SetupPlayerMasks only allies two players when both
			// share a non-zero team (pc.Team != 0 && pc.Team == qc.Team); with both on team 0 they fall to
			// the else branch and are marked mutually hostile - exactly what the combat test needs.
			lobby.Clients.Add(new Session.Client
			{
				Index = om.Connection.LocalClientId,
				Name = "TestPlayer",
				Faction = "Random",
				SpawnPoint = 0,
				Team = 0,
				Slot = localSlot,
				State = Session.ClientState.Ready,
			});

			// A distinct, positive client index for the bot. The value only has to differ from the local
			// client's; the bot never sends packets (StartGame excludes bot clients from pendingOrders).
			const int EnemyBotClientIndex = 2;
			if (enemySlot != null)
				lobby.Clients.Add(new Session.Client
				{
					Index = EnemyBotClientIndex,
					Name = "TestEnemyBot",
					Faction = "Random",
					SpawnPoint = 0,
					Team = 0,
					Slot = enemySlot,

					// Bot != null is what makes IsBot true, which is what keeps this client out of the
					// order-queue readiness check. The concrete bot type is irrelevant - we issue no bot
					// orders - but it must name a bot the mod actually defines or player setup would reject it.
					Bot = FirstBotType(map),
					BotControllerClientIndex = om.Connection.LocalClientId,
					State = Session.ClientState.Ready,
				});

			// Resolve the map's sprite sequences on the CPU. This mirrors ModData.PrepareMap (the renderer path)
			// minus the renderer-specific loaders: SpriteCache reads sprite files and packs them into CPU-side
			// sheet buffers, deferring GPU upload until a texture is actually drawn (Sheet.GetTexture), which
			// never happens headlessly. We need this because animation *frame counts* (ISpriteSequence.Length)
			// are only known after resolution, and animation completion drives simulation-affecting callbacks
			// (WithMakeAnimation -> sell/transform/deploy). Without it, RenderSprites cannot tick faithfully and
			// the headless sim would diverge in timing from the rendered game.
			map.Sequences.LoadSprites();

			// isHeadless: true still suppresses the truly renderer-bound work: dereferencing the (absent)
			// WorldRenderer, GPU post-process passes, and the ScreenMap/selection index. The simulation, including
			// animation timing, runs identically to the rendered game.
			var world = new World(map, modData, om, WorldType.Regular, isHeadless: true);
			om.World = world;

			// No WorldRenderer in a headless world; render-only traits no-op via World.IsHeadless.
			world.LoadComplete(null);
			om.StartGame();

			// ValidateOrder drops any order whose subject is not owned by the issuing client, so tests need the
			// client-backed player rather than any playable one. LocalPlayer is set by CreateMapPlayers from
			// World.LocalClientId (this world's own OM); fall back to the same lookup UnitOrders uses if that
			// ever changes.
			var localPlayer = world.LocalPlayer ??
				world.Players.First(p => p.ClientIndex == om.Connection.LocalClientId && p.PlayerReference.Playable);

			// The enemy is the bot-backed player, if the map had a second playable slot. Resolve by client
			// index rather than by "any non-local playable player" so map/neutral players are never picked.
			var enemyPlayer = enemySlot == null
				? null
				: world.Players.FirstOrDefault(p => p.ClientIndex == EnemyBotClientIndex && p.PlayerReference.Playable);

			return new HeadlessWorld(world, om, localPlayer, enemyPlayer);
		}

		/// <summary>
		/// The first bot type defined on the mod's player actor, or null if none. Used only to mark the enemy
		/// client as a bot so it is excluded from the order-queue readiness check; the bot's behaviour is
		/// irrelevant because it is never activated to issue orders here.
		/// </summary>
		static string FirstBotType(Map map)
		{
			return map.Rules.Actors[SystemActors.Player]
				.TraitInfos<IBotInfo>()
				.Select(b => b.Type)
				.FirstOrDefault();
		}

		/// <summary>
		/// Advances the world by one tick if the OrderManager is ready, returning whether it advanced.
		/// This is the canonical tick loop body: immediate orders are flushed and received, queued order
		/// packets for this frame are resolved inside <see cref="OrderManager.TryTick"/>, and only then does
		/// the simulation step.
		/// </summary>
		public bool TickOnce()
		{
			OrderManager.TickImmediate();
			if (!OrderManager.TryTick())
				return false;

			World.Tick();
			TicksAdvanced++;
			return true;
		}

		/// <summary>Advances up to <paramref name="ticks"/> ticks and returns how many actually advanced.</summary>
		public int Tick(int ticks)
		{
			var advanced = 0;
			for (var i = 0; i < ticks; i++)
				if (TickOnce())
					advanced++;

			return advanced;
		}

		/// <summary>Advances the world and returns the <see cref="World.SyncHash"/> after each advanced tick.</summary>
		public List<int> TickCollectingHashes(int ticks)
		{
			var hashes = new List<int>(ticks);
			for (var i = 0; i < ticks; i++)
				if (TickOnce())
					hashes.Add(World.SyncHash());

			return hashes;
		}

		/// <summary>
		/// Ticks until <paramref name="condition"/> holds, up to <paramref name="maxTicks"/>. Returns the
		/// value of <see cref="TicksAdvanced"/> at which the condition first held, or null if it never did.
		/// The condition is evaluated after each tick, so a returned tick number is the first tick whose
		/// simulation produced the awaited state.
		/// </summary>
		public int? TickUntil(Func<bool> condition, int maxTicks)
		{
			for (var i = 0; i < maxTicks; i++)
			{
				if (!TickOnce())
					continue;

				if (condition())
					return TicksAdvanced;
			}

			return null;
		}

		/// <summary>
		/// Injects an order exactly as if the local player had issued it through the UI: it is buffered now,
		/// serialized and looped back through the connection, validated, and resolved on the owning actor
		/// <see cref="OrderLatencyTicks"/> ticks later. The subject must be owned by <see cref="LocalPlayer"/>
		/// and must be in the world, because orders are serialized by ActorID and re-resolved on arrival.
		/// </summary>
		public void IssueOrder(Order order)
		{
			OrderManager.IssueOrder(order);
		}

		/// <summary>Issues a move order sending <paramref name="actor"/> to <paramref name="cell"/>.</summary>
		public void IssueMoveOrder(Actor actor, CPos cell, bool queued = false)
		{
			// Target.FromCell survives the order's byte round trip exactly (it serializes as a cell rather
			// than a world position), so the resolved destination is the cell we asked for.
			IssueOrder(new Order("Move", actor, Target.FromCell(World, cell), queued));
		}

		/// <summary>
		/// Spawns an actor of <paramref name="type"/> at <paramref name="cell"/>. Creating actors directly
		/// (rather than via <see cref="World.AddFrameEndTask"/>) is safe here because tests only do it
		/// between ticks, never during one; the actor joins the simulation on the next tick and is visible to
		/// <see cref="World.SyncHash"/> immediately.
		/// </summary>
		/// <remarks>
		/// Facing is deliberately not settable: <c>FacingInit</c> lives in the mod assembly, which this
		/// project must not reference (see the csproj comment). Units therefore spawn on their trait's
		/// configured <c>InitialFacing</c>, which is a fixed value and so still deterministic.
		/// </remarks>
		public Actor CreateActor(string type, CPos cell, Player owner = null)
		{
			TypeDictionary inits = [new OwnerInit(owner ?? LocalPlayer), new LocationInit(cell)];
			return World.CreateActor(type, inits);
		}

		/// <summary>The cells currently occupied by any actor in the world.</summary>
		public HashSet<CPos> OccupiedCells()
		{
			var occupied = new HashSet<CPos>();
			foreach (var a in World.Actors)
			{
				if (!a.IsInWorld || a.OccupiesSpace == null)
					continue;

				foreach (var (cell, _) in a.OccupiesSpace.OccupiedCells())
					occupied.Add(cell);
			}

			return occupied;
		}

		/// <summary>
		/// Finds a straight, unobstructed run of <paramref name="length"/> cells for a ground unit to walk,
		/// searching outward from <paramref name="near"/>. Returns the run's first and last cell.
		/// <para>
		/// Terrain is checked via the map's terrain type rather than a locomotor, because this assembly
		/// deliberately does not reference the mod assemblies (see the csproj comment) and so cannot touch
		/// <c>Mobile</c> or <c>Locomotor</c>. Requiring plain "Clear" terrain along the whole run and no
		/// occupying actors is stricter than any ground locomotor needs, which is what makes the resulting
		/// path trivially walkable and the arrival assertion unambiguous.
		/// </para>
		/// The search order is fully deterministic (rings outward, then a fixed direction order) so the same
		/// seed always yields the same cells.
		/// </summary>
		public (CPos Start, CPos Destination) FindClearRun(CPos near, int length, int searchRadius = 16)
		{
			// Axis-aligned before diagonal: a diagonal run is more likely to clip an obstacle corner.
			var directions = new[]
			{
				new CVec(1, 0), new CVec(-1, 0), new CVec(0, 1), new CVec(0, -1),
				new CVec(1, 1), new CVec(-1, -1), new CVec(1, -1), new CVec(-1, 1),
			};

			var occupied = OccupiedCells();
			bool IsClear(CPos c) => IsClearCell(c, occupied);

			// Rings outward from `near`, each ring ordered by (Y, X), so the result is stable.
			var candidates = Enumerable.Range(0, searchRadius + 1)
				.SelectMany(r => AllCellsInRing(near, r));

			foreach (var start in candidates)
			{
				if (!IsClear(start))
					continue;

				foreach (var dir in directions)
					if (Enumerable.Range(1, length).All(i => IsClear(start + i * dir)))
						return (start, start + length * dir);
			}

			throw new InvalidOperationException(
				$"No clear {length}-cell run found within {searchRadius} cells of {near} on map '{World.Map.Title}'.");
		}

		/// <summary>
		/// Finds the top-left cell of an unobstructed <paramref name="width"/>x<paramref name="height"/>
		/// rectangle of clear terrain, searching outward from <paramref name="near"/>. Used to place a
		/// building (or a unit that transforms into one), whose whole footprint must be buildable.
		/// <para>
		/// Like <see cref="FindClearRun"/>, this uses the map's terrain type rather than the mod's
		/// <c>BuildingInfo.TerrainTypes</c> (unreachable from this assembly). Requiring every cell to be plain
		/// "Clear" is stricter than any building's own terrain whitelist, so a rectangle found here is always
		/// placeable. The rectangle is one cell larger than the footprint on every side is not assumed - ask
		/// for exactly the footprint size you need.
		/// </para>
		/// </summary>
		public CPos FindClearArea(CPos near, int width, int height, int searchRadius = 20)
		{
			var occupied = OccupiedCells();

			bool RectClear(CPos topLeft) =>
				Enumerable.Range(0, height).All(dy =>
					Enumerable.Range(0, width).All(dx =>
						IsClearCell(topLeft + new CVec(dx, dy), occupied)));

			var candidates = Enumerable.Range(0, searchRadius + 1)
				.SelectMany(r => AllCellsInRing(near, r));

			foreach (var topLeft in candidates)
				if (RectClear(topLeft))
					return topLeft;

			throw new InvalidOperationException(
				$"No clear {width}x{height} area found within {searchRadius} cells of {near} on map '{World.Map.Title}'.");
		}

		/// <summary>
		/// A cell is "clear" for test placement if it is in bounds, on plain Clear terrain, and unoccupied.
		/// This is intentionally stricter than any locomotor's passability or any building's terrain whitelist,
		/// so anything placed on clear cells is trivially valid.
		/// </summary>
		bool IsClearCell(CPos c, HashSet<CPos> occupied) =>
			World.Map.Contains(c) &&
			World.Map.GetTerrainInfo(c).Type == "Clear" &&
			!occupied.Contains(c);

		/// <summary>Cells at exactly Chebyshev distance <paramref name="radius"/> from <paramref name="center"/>, ordered by (Y, X).</summary>
		static IEnumerable<CPos> AllCellsInRing(CPos center, int radius)
		{
			if (radius == 0)
				return [center];

			var cells = new List<CPos>();
			for (var dy = -radius; dy <= radius; dy++)
				for (var dx = -radius; dx <= radius; dx++)
					if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == radius)
						cells.Add(center + new CVec(dx, dy));

			return cells;
		}

		public void Dispose()
		{
			OrderManager.Dispose();
		}
	}
}
