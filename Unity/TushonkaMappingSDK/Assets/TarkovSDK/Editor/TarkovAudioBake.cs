using System.Collections.Generic;
using System.IO;
using System.Linq;
using Audio.SpatialSystem;
using Audio.SpatialSystem.Data;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // The spatial audio's baked routes, as BSG's (stripped) baker writes them and AudioBakeDataReader reads
    // them; the layout was checked against retail factory, labyrinth and customs bakes:
    //
    //   int pairCount, then per pair:  uint key (= its ID) + RoomPair (uint ID, short FirstRoomID,
    //                                  short SecondRoomID, int routeCount, routes, int TotalPortalsCount,
    //                                  byte ShortestRouteLength); a route is int portalCount, short[] PortalIDs,
    //                                  float HeuristicCost, float TraverseDistance
    //   int roomCount, then per room:  short roomID, int count, uint[] the IDs of its pairs (ascending)
    //   int pairCount, then per pair:  uint pairID, int routeCount, AudioRouteData per route (short
    //                                  PortalDataLength, float HeuristicCost, ushort StartIndex = portals before
    //                                  it, ushort NodeStartIndex = (portals + 2) before it)
    //   LocationBakeSettings (6 ints), uint max routes of any pair, uint max total portals of any pair
    //
    // Pair IDs count from 1 over every pair of rooms (first room the lower ID) that has a route. A route lists
    // its portals from the first room to the second, which the game walks from the emitter to the listener
    // (reversed when the listener is in the first room). The game costs every route each time from the real
    // positions (PropagationJob); HeuristicCost only lets the older calculator skip routes that can't beat
    // the best so far, so 0 (never skip) is always right.
    public static class TarkovAudioBake
    {
        // the game evaluates at least the first 350 routes of a pair; the shortest of these are plenty
        public const int MaxRoutesPerPair = 48;
        // per pair: the outdoor room joins every building, so the paths through it multiply quickly; the
        // search tries the nearest portals first, so the short routes are found well within this
        private const int MaxSearchSteps = 4000;

        public class Result
        {
            public int Rooms, Portals, Pairs, Routes, MaxRoutes, MaxPortals;
            public override string ToString() => $"{Rooms} room(s), {Portals} portal(s), {Pairs} connected room pair(s), {Routes} route(s)";
        }

        private struct Route
        {
            public short[] Portals;
            public float Distance;
        }

        public static Result Bake(SpatialAudioSystem system, string path)
        {
            var rooms = system.GetComponentsInChildren<SpatialAudioRoom>(true)
                .Where(r => r.gameObject.activeInHierarchy && r._iD > 0).OrderBy(r => r._iD).ToList();
            var portals = system.GetComponentsInChildren<BaseSpatialAudioPortal>(true).Where(p => p._iD > 0).ToList();
            var settings = system._locationInfo.bakeSettings ?? new LocationBakeSettings();

            // the room graph: each room's portals and where they lead
            var links = rooms.ToDictionary(r => r, r => new List<(BaseSpatialAudioPortal Portal, SpatialAudioRoom To)>());
            foreach (var room in rooms)
                foreach (var connection in room.roomConnections ?? new List<SpatialAudioRoom.RoomConnection>())
                {
                    if (connection?.connectedRoom == null || !links.ContainsKey(connection.connectedRoom) || connection.connectedRoom == room)
                        continue;
                    foreach (var portal in connection.connectingPortals ?? new BaseSpatialAudioPortal[0])
                        if (portal != null && portal._iD > 0 && !links[room].Any(l => l.Portal == portal))
                            links[room].Add((portal, connection.connectedRoom));
                }
            var centers = rooms.ToDictionary(r => r, r => TarkovSpatialAudio.RoomBounds(r).center);

            var pairs = new List<(uint Id, SpatialAudioRoom A, SpatialAudioRoom B, List<Route> Routes)>();
            uint nextId = 1;
            for (int i = 0; i < rooms.Count; i++)
            {
                if (EditorUtility.DisplayCancelableProgressBar("Spatial Audio", "Baking sound routes", i / (float)rooms.Count))
                    break;
                for (int j = i + 1; j < rooms.Count; j++)
                {
                    var a = rooms[i];
                    var b = rooms[j];
                    bool ao = TarkovSpatialAudio.IsOutdoor(a), bo = TarkovSpatialAudio.IsOutdoor(b);
                    int depth = ao && bo ? settings.maxDepthOutdoorToOutdoor : ao || bo ? settings.maxDepthIndoorToOutdoor : settings.maxDepthIndoorToIndoor;
                    float distance = ao && bo ? settings.maxDistanceOutdoorToOutdoor : ao || bo ? settings.maxDistanceIndoorToOutdoor : settings.maxDistanceIndoorToIndoor;
                    // an outdoor room's centre says nothing about where it is
                    if (!ao && !bo && Vector3.Distance(centers[a], centers[b]) > distance)
                        continue;
                    var routes = FindRoutes(a, b, Mathf.Max(1, depth), distance * 2f, links, centers);
                    if (routes.Count > 0)
                        pairs.Add((nextId++, a, b, routes));
                }
            }
            EditorUtility.ClearProgressBar();

            var result = new Result
            {
                Rooms = rooms.Count,
                Portals = portals.Count,
                Pairs = pairs.Count,
                Routes = pairs.Sum(p => p.Routes.Count),
                MaxRoutes = pairs.Count == 0 ? 0 : pairs.Max(p => p.Routes.Count),
                MaxPortals = pairs.Count == 0 ? 0 : pairs.Max(p => p.Routes.Sum(r => r.Portals.Length)),
            };
            Write(path, pairs, settings, result);

            var info = system._locationInfo;
            info.roomsCount = (uint)rooms.Count;
            info.portalsCount = (uint)portals.Count;
            info.routesCount = (uint)result.Routes;
            // the load progress divides by it
            info.roomPairsCount = (uint)Mathf.Max(1, pairs.Count);
            info.maxRoutesCount = (uint)result.MaxRoutes;
            info.maxRoutePortalsCount = (uint)result.MaxPortals;
            EditorUtility.SetDirty(info);
            return result;
        }

        // the shortest simple paths (no room twice) from a to b within the portal depth, by travelled distance
        private static List<Route> FindRoutes(SpatialAudioRoom a, SpatialAudioRoom b, int maxDepth, float maxDistance,
                                              Dictionary<SpatialAudioRoom, List<(BaseSpatialAudioPortal Portal, SpatialAudioRoom To)>> links,
                                              Dictionary<SpatialAudioRoom, Vector3> centers)
        {
            var found = new List<Route>();
            var path = new List<BaseSpatialAudioPortal>();
            var visited = new HashSet<SpatialAudioRoom> { a };
            bool endOutdoor = TarkovSpatialAudio.IsOutdoor(b);
            int steps = 0;

            void Search(SpatialAudioRoom room, Vector3? from, float travelled)
            {
                if (path.Count >= maxDepth || steps > MaxSearchSteps)
                    return;
                foreach (var (portal, to) in links[room].OrderBy(l => from.HasValue ? Vector3.Distance(from.Value, TarkovSpatialAudio.PortalCenter(l.Portal)) : 0f))
                {
                    if (visited.Contains(to) || path.Contains(portal))
                        continue;
                    steps++;
                    var at = TarkovSpatialAudio.PortalCenter(portal);
                    float d = travelled + (from.HasValue ? Vector3.Distance(from.Value, at) : 0f);
                    if (d > maxDistance)
                        continue;
                    path.Add(portal);
                    if (to == b)
                    {
                        float total = d + (endOutdoor ? 0f : Vector3.Distance(at, centers[b]));
                        found.Add(new Route { Portals = path.Select(p => p._iD).ToArray(), Distance = total });
                    }
                    else
                    {
                        visited.Add(to);
                        Search(to, at, d);
                        visited.Remove(to);
                    }
                    path.RemoveAt(path.Count - 1);
                }
            }

            Search(a, TarkovSpatialAudio.IsOutdoor(a) ? (Vector3?)null : centers[a], 0f);
            return found.OrderBy(r => r.Distance).ThenBy(r => r.Portals.Length).Take(MaxRoutesPerPair).ToList();
        }

        private static void Write(string path, List<(uint Id, SpatialAudioRoom A, SpatialAudioRoom B, List<Route> Routes)> pairs,
                                  LocationBakeSettings settings, Result result)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            using var stream = new FileStream(path, FileMode.Create);
            using var w = new BinaryWriter(stream);

            w.Write(pairs.Count);
            foreach (var (id, a, b, routes) in pairs)
            {
                w.Write(id);
                w.Write(id);
                w.Write(a._iD);
                w.Write(b._iD);
                w.Write(routes.Count);
                foreach (var route in routes)
                {
                    w.Write(route.Portals.Length);
                    foreach (var portal in route.Portals)
                        w.Write(portal);
                    w.Write(0f);
                    w.Write(route.Distance);
                }
                w.Write(routes.Sum(r => r.Portals.Length));
                w.Write((byte)routes.Min(r => r.Portals.Length));
            }

            var byRoom = new SortedDictionary<short, List<uint>>();
            foreach (var (id, a, b, _) in pairs)
            {
                foreach (var room in new[] { a._iD, b._iD })
                {
                    if (!byRoom.TryGetValue(room, out var list))
                        byRoom[room] = list = new List<uint>();
                    list.Add(id);
                }
            }
            w.Write(byRoom.Count);
            foreach (var (room, ids) in byRoom)
            {
                w.Write(room);
                w.Write(ids.Count);
                foreach (var id in ids.OrderBy(x => x))
                    w.Write(id);
            }

            w.Write(pairs.Count);
            foreach (var (id, _, _, routes) in pairs)
            {
                w.Write(id);
                w.Write(routes.Count);
                int start = 0, node = 0;
                foreach (var route in routes)
                {
                    w.Write((short)route.Portals.Length);
                    w.Write(0f);
                    w.Write((ushort)start);
                    w.Write((ushort)node);
                    start += route.Portals.Length;
                    node += route.Portals.Length + 2;
                }
            }

            w.Write(settings.maxDepthIndoorToIndoor);
            w.Write(settings.maxDepthIndoorToOutdoor);
            w.Write(settings.maxDepthOutdoorToOutdoor);
            w.Write(settings.maxDistanceOutdoorToOutdoor);
            w.Write(settings.maxDistanceIndoorToOutdoor);
            w.Write(settings.maxDistanceIndoorToIndoor);
            w.Write((uint)result.MaxRoutes);
            w.Write((uint)result.MaxPortals);
        }
    }
}
