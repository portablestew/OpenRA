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
	/// A parsed look request (P2.8). Points are the cells looked at. Reference is true for the points form, which names reference
	/// points, so each record carries dist and pt. The at form is a single cell with no reference points.
	/// </summary>
	public sealed record LookRequest(CPos[] Points, bool Reference, int K);

	/// <summary>A looked-at cell with no resource: its top-left cell, terrain type and whether a construction yard can stand there.</summary>
	public sealed record LookCellV1(int[] Cell, string Terrain, bool Buildable);

	/// <summary>A looked-at cell with a resource: as LookCellV1, plus the resource type and its density.</summary>
	public sealed record LookResourceCellV1(int[] Cell, string Terrain, bool Buildable, string Resource, int Density);

	/// <summary>A looked-at actor, for the at form: no reference point, so no dist or pt.</summary>
	public sealed record LookActorV1(uint Id, string Type, string Owner, int[] Cell, int? Hp);

	/// <summary>A looked-at actor, for the points form: dist is its distance (world units, 1024 per cell) from the reference point pt.</summary>
	public sealed record LookNearActorV1(uint Id, string Type, string Owner, int[] Cell, int? Hp, int Dist, int[] Pt);

	/// <summary>The look response (P2.8). Cells has one entry per looked-at cell; Actors holds the k nearest the observer knows.</summary>
	public sealed record LookV1(bool Ok, string Cmd, object[] Cells, object[] Actors);

	/// <summary>
	/// The look command (P2.8). It returns the k nearest actors to each reference point, regardless of owner, under the fog policy
	/// (AgentFog.Known; the observer's own actors always count). Points share the k: each takes its nearest unclaimed actor in turn, so
	/// every point gets a fair share and no actor is listed twice. Scenery is left out, as in the state. Each looked-at cell reports its
	/// terrain type and whether a construction yard can stand on it, and a resource cell adds its resource and density.
	/// </summary>
	public static class AgentLook
	{
		/// <summary>The default number of actors a look returns.</summary>
		public const int DefaultK = 20;

		/// <summary>The largest k a look accepts.</summary>
		public const int MaxK = 100;

		/// <summary>The most points one look takes.</summary>
		public const int MaxPoints = 16;

		/// <summary>Parses the request on the socket thread. Returns an error message, or null when the request is valid.</summary>
		public static string Parse(JsonElement root, out LookRequest request)
		{
			request = null;
			var hasAt = AgentOrder.IsPresent(root, "at");
			var hasPoints = AgentOrder.IsPresent(root, "points");
			if (hasAt == hasPoints)
				return "look takes exactly one of at ([x, y]) or points ([[x, y], ...])";

			var k = DefaultK;
			if (AgentOrder.IsPresent(root, "k") && (!root.GetProperty("k").TryGetInt32(out k) || k < 1 || k > MaxK))
				return $"k must be an integer from 1 to {MaxK}";

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

			request = new LookRequest(points, hasPoints, k);
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
				.Where(a => a.OccupiesSpace != null && !a.IsDead && !AgentStateDump.IsScenery(a)
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
			return new LookV1(true, "look", cells, actors);
		}

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
