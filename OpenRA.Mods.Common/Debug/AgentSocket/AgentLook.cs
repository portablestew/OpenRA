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
using System.Text.Json;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.AgentSocket
{
	/// <summary>
	/// A parsed look request (P2.8, 9.4). Points are the cells looked at. Reference is true for the points form, which names reference
	/// points, so each record carries dist and pt. The at form is a single cell with no reference points. Radius is how far the class
	/// summary reaches around each point.
	/// </summary>
	public sealed record LookRequest(CPos[] Points, bool Reference, int K, int Radius);

	/// <summary>A looked-at cell with no resource: its top-left cell, terrain type and whether a construction yard can stand there.</summary>
	public sealed record LookCellV1(int[] Cell, string Terrain, bool Buildable);

	/// <summary>A looked-at cell with a resource: as LookCellV1, plus the resource type and its density.</summary>
	public sealed record LookResourceCellV1(int[] Cell, string Terrain, bool Buildable, string Resource, int Density);

	/// <summary>A looked-at actor, for the at form: no reference point, so no dist or pt.</summary>
	public sealed record LookActorV1(uint Id, string Type, string Owner, int[] Cell, int? Hp);

	/// <summary>A looked-at actor, for the points form: dist is its distance (world units, 1024 per cell) from the reference point pt.</summary>
	public sealed record LookNearActorV1(uint Id, string Type, string Owner, int[] Cell, int? Hp, int Dist, int[] Pt);

	/// <summary>The nearest occurrence of a class: its cell, and its distance from the reference point (world units, 1024 per cell).</summary>
	public sealed record LookNearestV1(int[] Cell, int Distance);

	/// <summary>
	/// One class the summary found near a reference point (9.4). Kind is terrain, resource or blocking. Class is the engine's own
	/// name for it: the tileset's terrain type, the mod's resource type, or the map-editor category the occupying actor's rules
	/// declare. Count is how many cells of that class are within the radius, and Nearest is the closest of them.
	/// </summary>
	public sealed record LookClassV1(string Kind, string Class, int Count, LookNearestV1 Nearest);

	/// <summary>
	/// The class summary for one reference point (9.4): the classes within Radius of Pt, so an agent planning a placement or a
	/// deploy sees what is around a cell without asking cell by cell. Cells is how many on-map cells the counts cover, so the
	/// summary is a neighbourhood and never a whole-map survey: a point near the map edge covers fewer cells than the full disc.
	/// </summary>
	public sealed record LookSummaryV1(int[] Pt, int Radius, int Cells, LookClassV1[] Classes);

	/// <summary>
	/// The look response (P2.8, 9.4). Cells has one entry per looked-at cell, Actors holds the k nearest the observer knows, and
	/// Summary has one entry per requested point, in request order.
	/// </summary>
	public sealed record LookV1(bool Ok, string Cmd, object[] Cells, object[] Actors, LookSummaryV1[] Summary);

	/// <summary>
	/// The look command (P2.8, 9.4). It returns the k nearest actors to each reference point, regardless of owner, under the fog policy
	/// (AgentFog.Known; the observer's own actors always count). Points share the k: each takes its nearest unclaimed actor in turn, so
	/// every point gets a fair share and no actor is listed twice. Scenery is left out, as in the state, but a crate is not scenery to
	/// this command: it is a collectable the observer can act on, so a known crate is listed like any other actor. Each looked-at cell
	/// reports its terrain type and whether a construction yard can stand on it, and a resource cell adds its resource and density. Each
	/// point also gets a class summary: the classes within radius of it, with a count and the nearest occurrence, from the engine's own
	/// class names.
	/// </summary>
	public static class AgentLook
	{
		/// <summary>The default number of actors a look returns.</summary>
		public const int DefaultK = 20;

		/// <summary>The largest k a look accepts. A k of zero returns no actors, only the cells and the summary.</summary>
		public const int MaxK = 100;

		/// <summary>The most points one look takes.</summary>
		public const int MaxPoints = 16;

		/// <summary>The default reach, in cells, of the class summary around each point.</summary>
		public const int DefaultRadius = 10;

		/// <summary>The largest summary radius a look accepts.</summary>
		public const int MaxRadius = 20;

		/// <summary>Parses the request on the socket thread. Returns an error message, or null when the request is valid.</summary>
		public static string Parse(JsonElement root, out LookRequest request)
		{
			request = null;
			var hasAt = AgentOrder.IsPresent(root, "at");
			var hasPoints = AgentOrder.IsPresent(root, "points");
			if (hasAt == hasPoints)
				return "look takes exactly one of at ([x, y]) or points ([[x, y], ...])";

			var k = DefaultK;
			if (AgentOrder.IsPresent(root, "k") && (!root.GetProperty("k").TryGetInt32(out k) || k < 0 || k > MaxK))
				return $"k must be an integer from 0 to {MaxK}";

			var radius = DefaultRadius;
			if (AgentOrder.IsPresent(root, "radius") && (!root.GetProperty("radius").TryGetInt32(out radius) || radius < 1 || radius > MaxRadius))
				return $"radius must be an integer from 1 to {MaxRadius}";

			CPos[] points;
			if (hasAt)
			{
				if (!AgentOrder.TryGetInts(root.GetProperty("at"), 2, out var c))
					return "at must be [x, y]";

				points = [new CPos(c[0], c[1])];
			}
			else
			{
				var list = root.GetProperty("points");
				if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0 || list.GetArrayLength() > MaxPoints)
					return $"points must be a non-empty array of at most {MaxPoints} [x, y] pairs";

				var found = new List<CPos>();
				foreach (var element in list.EnumerateArray())
				{
					if (!AgentOrder.TryGetInts(element, 2, out var c))
						return "each point must be [x, y]";

					found.Add(new CPos(c[0], c[1]));
				}

				points = found.ToArray();
			}

			request = new LookRequest(points, hasPoints, k, radius);
			return null;
		}

		/// <summary>The look for a parsed request. Must run on the game thread. A point off the map is an error.</summary>
		public static LookV1 Build(World world, Player observer, LookRequest request)
		{
			if (observer == null)
				throw new InvalidOperationException("look needs a local player to look for");

			foreach (var p in request.Points)
				if (!world.Map.Contains(p))
					throw new InvalidOperationException($"the point {p.X},{p.Y} is not on the map");

			var candidates = world.Actors
				.Where(a => a.OccupiesSpace != null && !a.IsDead && (!AgentStateDump.IsScenery(a) || IsCrate(a))
					&& (a.Owner == observer || AgentFog.Known(a, observer)))
				.ToArray();

			var centers = request.Points.Select(world.Map.CenterOfCell).ToArray();
			var nearest = centers.Select(c => candidates
				.OrderBy(a => (a.CenterPosition - c).Length).ThenBy(a => a.ActorID).ToArray()).ToArray();

			// Round robin: each point claims its nearest unclaimed actor in turn, until k are claimed or the candidates run out.
			var next = new int[request.Points.Length];
			var claimed = new HashSet<uint>();
			var picks = new List<(Actor Actor, int Point)>();
			while (picks.Count < request.K)
			{
				var progressed = false;
				for (var i = 0; i < request.Points.Length && picks.Count < request.K; i++)
				{
					while (next[i] < nearest[i].Length && claimed.Contains(nearest[i][next[i]].ActorID))
						next[i]++;

					if (next[i] == nearest[i].Length)
						continue;

					var actor = nearest[i][next[i]++];
					claimed.Add(actor.ActorID);
					picks.Add((actor, i));
					progressed = true;
				}

				if (!progressed)
					break;
			}

			var actors = picks.Select(pick => request.Reference
				? (object)new LookNearActorV1(pick.Actor.ActorID, pick.Actor.Info.Name, pick.Actor.Owner.PlayerName,
					[pick.Actor.Location.X, pick.Actor.Location.Y], pick.Actor.TraitOrDefault<IHealth>()?.HP,
					(pick.Actor.CenterPosition - centers[pick.Point]).Length, [request.Points[pick.Point].X, request.Points[pick.Point].Y])
				: new LookActorV1(pick.Actor.ActorID, pick.Actor.Info.Name, pick.Actor.Owner.PlayerName,
					[pick.Actor.Location.X, pick.Actor.Location.Y], pick.Actor.TraitOrDefault<IHealth>()?.HP))
				.ToArray();

			var cells = request.Points.Select(p => Cell(world, p)).ToArray();
			var summary = request.Points.Select(p => Summary(world, observer, p, request.Radius)).ToArray();
			return new LookV1(true, "look", cells, actors, summary);
		}

		/// <summary>
		/// The class summary for one point (9.4). Every on-map cell within radius is counted once per axis: its terrain type, its
		/// resource type when it has one, and — for a cell an actor occupies — the class of that actor. The classes are the engine's
		/// own names, so nothing here guesses at a class the rules do not declare. Must run on the game thread.
		/// </summary>
		static LookSummaryV1 Summary(World world, Player observer, CPos point, int radius)
		{
			var terrain = new Dictionary<string, Counted>();
			var resource = new Dictionary<string, Counted>();
			var blocking = new Dictionary<string, Counted>();
			var blocked = new HashSet<(CPos Cell, string Class)>();

			var origin = world.Map.CenterOfCell(point);
			var reach = radius * 1024;
			var layer = world.WorldActor.TraitOrDefault<IResourceLayer>();
			var cells = 0;
			for (var y = point.Y - radius; y <= point.Y + radius; y++)
			{
				for (var x = point.X - radius; x <= point.X + radius; x++)
				{
					var cell = new CPos(x, y);
					if (!world.Map.Contains(cell))
						continue;

					var distance = (world.Map.CenterOfCell(cell) - origin).Length;
					if (distance > reach)
						continue;

					cells++;
					Add(terrain, world.Map.GetTerrainInfo(cell).Type, cell, distance);

					var contents = layer?.GetResource(cell) ?? default;
					if (!string.IsNullOrEmpty(contents.Type))
						Add(resource, contents.Type, cell, distance);
				}
			}

			// A cell is blocking when an actor the observer knows occupies it, and the class is the rules' own category for that
			// actor, so RA trees count as Tree. The cells are the engine's own occupancy, so a multi-cell footprint blocks all of them.
			foreach (var a in world.Actors)
			{
				if (a.OccupiesSpace == null || a.IsDead || !AgentFog.Reported(a, observer))
					continue;

				var cls = Class(a);
				foreach (var occupied in a.OccupiesSpace.OccupiedCells())
				{
					var cell = occupied.Cell;
					if (!world.Map.Contains(cell) || !blocked.Add((cell, cls)))
						continue;

					var distance = (world.Map.CenterOfCell(cell) - origin).Length;
					if (distance <= reach)
						Add(blocking, cls, cell, distance);
				}
			}

			// Classes sort by name alone, so the kinds interleave. A name that appears under more than one kind (RA's Ore is
			// both a terrain type and a resource) keeps the order above, terrain then resource then blocking: the sort is stable.
			var classes = terrain.Select(e => ClassOf("terrain", e))
				.Concat(resource.Select(e => ClassOf("resource", e)))
				.Concat(blocking.Select(e => ClassOf("blocking", e)))
				.OrderBy(c => c.Class, StringComparer.Ordinal)
				.ToArray();

			return new LookSummaryV1([point.X, point.Y], radius, cells, classes);
		}

		/// <summary>Adds one cell to a class's count, keeping the nearest occurrence. A tie goes to the smaller cell, so the reply is deterministic.</summary>
		static void Add(Dictionary<string, Counted> counts, string cls, CPos cell, int distance)
		{
			if (!counts.TryGetValue(cls, out var counted))
			{
				counts[cls] = new Counted(1, cell, distance);
				return;
			}

			var nearer = distance < counted.Distance
				|| (distance == counted.Distance && (cell.Y, cell.X).CompareTo((counted.Cell.Y, counted.Cell.X)) < 0);
			counts[cls] = new Counted(counted.Count + 1, nearer ? cell : counted.Cell, nearer ? distance : counted.Distance);
		}

		static LookClassV1 ClassOf(string kind, KeyValuePair<string, Counted> entry)
		{
			return new LookClassV1(kind, entry.Key, entry.Value.Count,
				new LookNearestV1([entry.Value.Cell.X, entry.Value.Cell.Y], entry.Value.Distance));
		}

		/// <summary>
		/// The engine's class for an occupying actor (9.4): the map-editor category its rules declare, so RA trees are Tree and
		/// buildings are Building. A crate declares none, and reports the crush class its Crate trait declares (RA: crate), the
		/// name its collectors crush by. An actor whose rules declare neither is grouped as "occupant". Never the actor type: a
		/// class, not an entity, which is what the owner asked for.
		/// </summary>
		static string Class(Actor a)
		{
			var crate = a.Info.TraitInfoOrDefault<CrateInfo>();
			if (crate != null)
				return crate.CrushClass;

			var categories = a.Info.TraitInfos<MapEditorDataInfo>().SelectMany(i => i.Categories).ToArray();
			return categories.Length > 0 ? categories[0] : "occupant";
		}

		/// <summary>True for a crate, which the scenery rule would otherwise swallow: a collectable, not a map prop.</summary>
		static bool IsCrate(Actor a)
		{
			return a.Info.HasTraitInfo<CrateInfo>();
		}

		/// <summary>A class's running total: how many cells, and the nearest one.</summary>
		sealed record Counted(int Count, CPos Cell, int Distance);

		/// <summary>One looked-at cell: its terrain and buildability, and its resource when it has one.</summary>
		static object Cell(World world, CPos cell)
		{
			var terrain = world.Map.GetTerrainInfo(cell).Type;
			var buildable = Buildable(world, cell);
			var position = new[] { cell.X, cell.Y };

			var contents = world.WorldActor.TraitOrDefault<IResourceLayer>()?.GetResource(cell) ?? default;
			if (string.IsNullOrEmpty(contents.Type))
				return new LookCellV1(position, terrain, buildable);

			return new LookResourceCellV1(position, terrain, buildable, contents.Type, contents.Density);
		}

		/// <summary>
		/// Whether a construction yard (the rules' "fact") could stand on the cell: its terrain, ramp and occupants, as
		/// IsCellBuildable checks them. False when the rules have no such building.
		/// </summary>
		static bool Buildable(World world, CPos cell)
		{
			if (!world.Map.Rules.Actors.TryGetValue("fact", out var info))
				return false;

			var building = info.TraitInfoOrDefault<BuildingInfo>();
			return building != null && world.IsCellBuildable(cell, info, building);
		}
	}
}
