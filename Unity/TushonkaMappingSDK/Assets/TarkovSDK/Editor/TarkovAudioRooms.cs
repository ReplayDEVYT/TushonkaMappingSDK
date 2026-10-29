using System.Collections.Generic;
using System.Linq;
using Audio.SpatialSystem;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Cell = TarkovSDK.Editor.TarkovIndoorTriggers.Cell;

namespace TarkovSDK.Editor
{
    // Spatial audio rooms and portals generated from the map's geometry.
    //
    // 1. The indoor trigger scan (TarkovIndoorTriggers.Scan): a 1 m grid of columns, each giving its storeys
    //    that have a ceiling and walls around them.
    // 2. Neighbouring cells of a storey join into one room only when nothing stands between them: rays at
    //    knee and head height from one cell's centre to the next. Walls and closed doors (the Interactive
    //    layer) split rooms, so every door ends up between two rooms. Small pieces that are open to each
    //    other (stair steps, one cell each) join into one stairwell; slivers left over join the room they
    //    open into.
    // 3. Each room is covered with as few trigger boxes as its shape allows (TarkovIndoorTriggers.CoverRoom).
    //    One outdoor room covers the whole map around them: a position in several rooms is in the smallest.
    // 4. Portals: every door (Opening, opened and closed with it), every breakable window (Window, open once
    //    broken), and every opening without either: between two rooms, and from a room to the outside (a
    //    doorway, a hole in the wall). An opening's width and height are measured by rays across it.
    //
    // Generated rooms and portals live under Rooms/Generated and Portals/Generated and are replaced when
    // generating again. Rooms made by hand elsewhere under Rooms are kept and their floor isn't scanned.
    public static class TarkovAudioRooms
    {
        private const float CellSize = TarkovIndoorTriggers.CellSize;
        private const float StairsArea = 6f;
        private const float MinRoomArea = 2f;
        private const float OutdoorProbe = 1.5f;
        private const float SampleStep = 0.25f;

        private static readonly Vector2Int[] Directions = { Vector2Int.right, Vector2Int.left, Vector2Int.up, Vector2Int.down };

        private class Region
        {
            public readonly List<(int X, int Z, Cell Cell)> Cells = new List<(int, int, Cell)>();
            public float Floor = float.MaxValue, Ceiling = float.MinValue;
            public bool Stairs;
            public SpatialAudioRoom Room;
            public float Area => Cells.Count * CellSize * CellSize;
        }

        private struct Edge
        {
            public int X, Z;
            public Vector2Int Dir;
            public int From, To; // region indices; To = -1 for outdoors
            public float Floor, Ceiling;
        }

        public class Result
        {
            public int Rooms, Doors, Windows, Openings, OutdoorOpenings;
            public override string ToString() =>
                $"{Rooms} room(s) and the outdoor room, {Doors} door portal(s), {Windows} window portal(s), {Openings} opening(s) between rooms, {OutdoorOpenings} to the outside";
        }

        public static Result Generate(Scene scene)
        {
            // one system for the whole map, its rooms over every scene of it
            scene = TarkovSpatialAudio.AudioScene(scene);
            var system = TarkovSpatialAudio.Find(scene) ?? TarkovSpatialAudio.Create(scene);
            var roomsRoot = TarkovSpatialAudio.Rooms(system);
            var portalsRoot = TarkovSpatialAudio.Portals(system);

            TarkovIndoorTriggers.ResetCache();
            int mask = TarkovRainCover.SolidMask;
            int wallMask = mask | LayerMask.GetMask("Interactive");
            int glassMask = LayerMask.GetMask("TransparentCollider");
            if (!TarkovRainCover.MapSolidBounds(scene, mask, out var bounds, TarkovIndoorTriggers.Counts))
            {
                EditorUtility.DisplayDialog("Spatial Audio", "The map has no solid mesh colliders to scan.", "OK");
                return null;
            }

            // what generating again replaces; rooms made by hand stay and keep their floor
            var oldRooms = roomsRoot.Find(TarkovSpatialAudio.GeneratedName);
            var oldPortals = portalsRoot.Find(TarkovSpatialAudio.GeneratedName);
            var manual = roomsRoot.GetComponentsInChildren<SpatialAudioRoom>(true)
                .Where(r => oldRooms == null || !r.transform.IsChildOf(oldRooms))
                .Where(r => !TarkovSpatialAudio.IsOutdoor(r))
                .SelectMany(r => (r.Areas ?? new List<AudioTriggerArea>()).Where(a => a != null).Select(a => TarkovScenePreparer.TransformBounds(a.transform)))
                .ToList();
            if (oldPortals != null)
            {
                foreach (var portal in oldPortals.GetComponentsInChildren<BaseSpatialAudioPortal>(true))
                    TarkovSpatialAudio.Disconnect(portal);
                Undo.DestroyObjectImmediate(oldPortals.gameObject);
            }
            if (oldRooms != null)
                Undo.DestroyObjectImmediate(oldRooms.gameObject);
            foreach (var room in roomsRoot.GetComponentsInChildren<SpatialAudioRoom>(true))
                room.roomConnections?.RemoveAll(c => c == null || c.connectedRoom == null);

            Physics.SyncTransforms();
            int nx = Mathf.CeilToInt(bounds.size.x / CellSize), nz = Mathf.CeilToInt(bounds.size.z / CellSize);
            var cells = new List<Cell>[nx, nz];
            float top = bounds.max.y + 10f;
            bool backfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = false;
            var result = new Result();
            try
            {
                for (int iz = 0; iz < nz; iz++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Spatial Audio", "Scanning for rooms", iz / (float)nz))
                        return null;
                    for (int ix = 0; ix < nx; ix++)
                        cells[ix, iz] = TarkovIndoorTriggers.Scan(new Vector3(bounds.min.x + (ix + 0.5f) * CellSize, top, bounds.min.z + (iz + 0.5f) * CellSize),
                                                                  mask, wallMask, manual);
                }

                EditorUtility.DisplayProgressBar("Spatial Audio", "Finding rooms", 0.6f);
                Vector3 CellPoint(int x, int z, float y) => new Vector3(bounds.min.x + (x + 0.5f) * CellSize, y, bounds.min.z + (z + 0.5f) * CellSize);
                bool Open(int x, int z, Cell a, Vector2Int d)
                {
                    float knee = a.Floor + 1f, head = a.Floor + Mathf.Min(1.6f, a.Ceiling - a.Floor - 0.2f);
                    var dir = new Vector3(d.x, 0f, d.y);
                    return !TarkovIndoorTriggers.Cast(CellPoint(x, z, knee), dir, CellSize, wallMask, out _)
                           && !TarkovIndoorTriggers.Cast(CellPoint(x, z, head), dir, CellSize, wallMask, out _);
                }

                var regions = FindRegions(cells, nx, nz, Open);
                MergeSmall(regions, cells, nx, nz, Open);

                EditorUtility.DisplayProgressBar("Spatial Audio", "Building rooms", 0.75f);
                var generatedRooms = TarkovSpatialAudio.Folder(roomsRoot, TarkovSpatialAudio.GeneratedName);
                var outdoorBox = new Bounds(bounds.center, bounds.size + new Vector3(120f, 0f, 120f));
                outdoorBox.SetMinMax(new Vector3(outdoorBox.min.x, bounds.min.y - 30f, outdoorBox.min.z), new Vector3(outdoorBox.max.x, bounds.max.y + 150f, outdoorBox.max.z));
                var outdoor = roomsRoot.GetComponentsInChildren<SpatialAudioRoom>(true).FirstOrDefault(TarkovSpatialAudio.IsOutdoor)
                              ?? TarkovSpatialAudio.CreateRoom(generatedRooms, TarkovSpatialAudio.OutdoorRoomName, new[] { outdoorBox }, outdoor: true);
                int number = 0;
                foreach (var region in regions)
                {
                    var plan = new TarkovIndoorTriggers.Room();
                    plan.Floor = region.Floor;
                    plan.Ceiling = region.Ceiling;
                    plan.Cells.AddRange(region.Cells.Select(c => new Vector2Int(c.X, c.Z)));
                    var boxes = TarkovIndoorTriggers.CoverRoom(plan, bounds.min, mask);
                    region.Room = TarkovSpatialAudio.CreateRoom(generatedRooms, region.Stairs ? $"Stairs {++number}" : $"Room {++number}", boxes, outdoor: false,
                                                                region.Stairs ? EAudioRoomTypeMask.IndoorStairs : EAudioRoomTypeMask.IndoorGeneric);
                }
                result.Rooms = regions.Count;

                EditorUtility.DisplayProgressBar("Spatial Audio", "Finding doors, windows and openings", 0.85f);
                var generatedPortals = TarkovSpatialAudio.Folder(portalsRoot, TarkovSpatialAudio.GeneratedName);
                SpatialAudioRoom RoomAt(Vector3 p)
                {
                    int x = Mathf.FloorToInt((p.x - bounds.min.x) / CellSize), z = Mathf.FloorToInt((p.z - bounds.min.z) / CellSize);
                    if (x < 0 || z < 0 || x >= nx || z >= nz || cells[x, z] == null)
                        return outdoor;
                    var cell = cells[x, z].FirstOrDefault(c => c.Room >= 0 && p.y >= c.Floor - 0.3f && p.y <= c.Ceiling + 0.2f);
                    return cell != null ? regions[cell.Room].Room : outdoor;
                }

                // the map's doors and windows, in any of its scenes (the portals live with the one system)
                var mapScenes = TarkovMapScenes.MapScenes(scene);
                foreach (var door in Object.FindObjectsOfType<Door>(true).Where(d => mapScenes.Contains(d.gameObject.scene)))
                {
                    if (!Plane(door.transform, door.GetComponentsInChildren<Renderer>(true), out var center, out var normal, out var size))
                        continue;
                    var front = RoomAt(center - normal * 0.8f);
                    var back = RoomAt(center + normal * 0.8f);
                    if (front == back)
                        continue;
                    var portal = TarkovSpatialAudio.CreatePortal(generatedPortals, "Door " + door.name, center, Quaternion.LookRotation(normal, Vector3.up), size,
                                                                 BaseSpatialAudioPortal.PortalType.Opening, front, back,
                                                                 closed: door._doorState != EDoorState.Open);
                    portal.DoorID = door.Id ?? "";
                    result.Doors++;
                }
                foreach (var window in Object.FindObjectsOfType<WindowBreaker>(true).Where(w => mapScenes.Contains(w.gameObject.scene)))
                {
                    var renderer = window.GetComponent<Renderer>();
                    if (renderer == null || !Plane(window.transform, new[] { renderer }, out var center, out var normal, out var size))
                        continue;
                    var front = RoomAt(center - normal * 0.6f);
                    var back = RoomAt(center + normal * 0.6f);
                    if (front == back)
                        continue;
                    var portal = TarkovSpatialAudio.CreatePortal(generatedPortals, "Window " + window.name, center, Quaternion.LookRotation(normal, Vector3.up), size,
                                                                 BaseSpatialAudioPortal.PortalType.Window, front, back, closed: true);
                    portal.DoorID = window.Id ?? "";
                    result.Windows++;
                }

                // openings: edges of a room that open into another room or out of the building
                var edges = new List<Edge>();
                for (int r = 0; r < regions.Count; r++)
                    foreach (var (x, z, cell) in regions[r].Cells)
                        foreach (var d in Directions)
                        {
                            int x2 = x + d.x, z2 = z + d.y;
                            var next = x2 >= 0 && z2 >= 0 && x2 < nx && z2 < nz && cells[x2, z2] != null
                                ? cells[x2, z2].FirstOrDefault(c => c.Room >= 0 && Mathf.Abs(c.Floor - cell.Floor) < 1.2f) : null;
                            if (next != null)
                            {
                                if (next.Room <= r || !Open(x, z, cell, d))
                                    continue;
                                edges.Add(new Edge { X = x, Z = z, Dir = d, From = r, To = next.Room, Floor = Mathf.Max(cell.Floor, next.Floor), Ceiling = Mathf.Min(cell.Ceiling, next.Ceiling) });
                            }
                            else if (OpenToOutside(CellPoint(x, z, 0f), cell, d, wallMask, glassMask))
                            {
                                edges.Add(new Edge { X = x, Z = z, Dir = d, From = r, To = -1, Floor = cell.Floor, Ceiling = cell.Ceiling });
                            }
                        }
                foreach (var run in Runs(edges))
                {
                    var first = run[0];
                    var from = regions[first.From].Room;
                    var to = first.To >= 0 ? regions[first.To].Room : outdoor;
                    if (!Opening(run, CellPoint, wallMask, out var center, out var size))
                        continue;
                    var normal = new Vector3(first.Dir.x, 0f, first.Dir.y);
                    TarkovSpatialAudio.CreatePortal(generatedPortals, $"Opening {from.name} - {to.name}", center, Quaternion.LookRotation(normal, Vector3.up), size,
                                                    BaseSpatialAudioPortal.PortalType.Static, from, to);
                    if (first.To >= 0)
                        result.Openings++;
                    else
                        result.OutdoorOpenings++;
                }
            }
            finally
            {
                Physics.queriesHitBackfaces = backfaces;
                EditorUtility.ClearProgressBar();
                TarkovIndoorTriggers.ResetCache();
            }
            EditorUtility.SetDirty(system);
            return result;
        }

        // ------------------------------------------------------------ rooms

        private static List<Region> FindRegions(List<Cell>[,] cells, int nx, int nz, System.Func<int, int, Cell, Vector2Int, bool> open)
        {
            var regions = new List<Region>();
            var queue = new Queue<(int, int, Cell)>();
            for (int iz = 0; iz < nz; iz++)
                for (int ix = 0; ix < nx; ix++)
                {
                    if (cells[ix, iz] == null)
                        continue;
                    foreach (var seed in cells[ix, iz])
                    {
                        if (seed.Room >= 0)
                            continue;
                        var region = new Region();
                        seed.Room = regions.Count;
                        regions.Add(region);
                        queue.Enqueue((ix, iz, seed));
                        while (queue.Count > 0)
                        {
                            var (x, z, cell) = queue.Dequeue();
                            region.Cells.Add((x, z, cell));
                            region.Floor = Mathf.Min(region.Floor, cell.Floor);
                            region.Ceiling = Mathf.Max(region.Ceiling, cell.Ceiling);
                            foreach (var d in Directions)
                            {
                                int x2 = x + d.x, z2 = z + d.y;
                                if (x2 < 0 || z2 < 0 || x2 >= nx || z2 >= nz || cells[x2, z2] == null)
                                    continue;
                                var next = cells[x2, z2].FirstOrDefault(c => c.Room < 0 && Mathf.Abs(c.Floor - cell.Floor) < 0.5f && Mathf.Abs(c.Ceiling - cell.Ceiling) < 1f);
                                if (next == null || !open(x, z, cell, d))
                                    continue;
                                next.Room = seed.Room;
                                queue.Enqueue((x2, z2, next));
                            }
                        }
                    }
                }
            return regions;
        }

        // small pieces open to each other (stair steps) become one stairwell; what is still tiny joins the
        // room it opens into, or is dropped when it opens into none
        private static void MergeSmall(List<Region> regions, List<Cell>[,] cells, int nx, int nz, System.Func<int, int, Cell, Vector2Int, bool> open)
        {
            var parent = Enumerable.Range(0, regions.Count).ToArray();
            int Root(int i)
            {
                while (parent[i] != i)
                    i = parent[i] = parent[parent[i]];
                return i;
            }
            var neighbours = new HashSet<(int, int)>();
            for (int r = 0; r < regions.Count; r++)
                foreach (var (x, z, cell) in regions[r].Cells)
                    foreach (var d in Directions)
                    {
                        int x2 = x + d.x, z2 = z + d.y;
                        if (x2 < 0 || z2 < 0 || x2 >= nx || z2 >= nz || cells[x2, z2] == null)
                            continue;
                        var next = cells[x2, z2].FirstOrDefault(c => c.Room >= 0 && c.Room != r && Mathf.Abs(c.Floor - cell.Floor) < 1.2f);
                        if (next != null && open(x, z, cell, d))
                            neighbours.Add((Mathf.Min(r, next.Room), Mathf.Max(r, next.Room)));
                    }
            foreach (var (a, b) in neighbours)
                if (regions[a].Area < StairsArea && regions[b].Area < StairsArea)
                    parent[Root(a)] = Root(b);
            var merged = Enumerable.Range(0, regions.Count).GroupBy(Root).Select(g => g.ToList()).ToList();
            var joined = new List<Region>();
            var index = new Dictionary<int, int>();
            foreach (var group in merged)
            {
                var region = new Region();
                foreach (var i in group)
                {
                    region.Cells.AddRange(regions[i].Cells);
                    region.Floor = Mathf.Min(region.Floor, regions[i].Floor);
                    region.Ceiling = Mathf.Max(region.Ceiling, regions[i].Ceiling);
                    index[i] = joined.Count;
                }
                region.Stairs = group.Count > 1 && region.Ceiling - region.Floor > 2.5f + 0.8f;
                joined.Add(region);
            }
            // slivers into the largest room they open into
            var joinedNeighbours = neighbours.Select(n => (index[n.Item1], index[n.Item2])).Where(n => n.Item1 != n.Item2).ToList();
            var keep = new List<Region>();
            var target = new Dictionary<int, int>();
            for (int i = 0; i < joined.Count; i++)
            {
                if (joined[i].Area >= MinRoomArea)
                    continue;
                var best = joinedNeighbours.Where(n => n.Item1 == i || n.Item2 == i).Select(n => n.Item1 == i ? n.Item2 : n.Item1)
                    .Where(j => joined[j].Area >= MinRoomArea).OrderByDescending(j => joined[j].Area).DefaultIfEmpty(-1).First();
                target[i] = best;
            }
            foreach (var (i, j) in target)
                if (j >= 0)
                {
                    joined[j].Cells.AddRange(joined[i].Cells);
                    joined[j].Floor = Mathf.Min(joined[j].Floor, joined[i].Floor);
                    joined[j].Ceiling = Mathf.Max(joined[j].Ceiling, joined[i].Ceiling);
                }
            for (int i = 0; i < joined.Count; i++)
                if (!target.ContainsKey(i))
                    keep.Add(joined[i]);
            regions.Clear();
            regions.AddRange(keep);
            foreach (var column in cells)
                if (column != null)
                    foreach (var cell in column)
                        cell.Room = -1;
            for (int r = 0; r < regions.Count; r++)
                foreach (var (_, _, cell) in regions[r].Cells)
                    cell.Room = r;
        }

        // ------------------------------------------------------------ openings

        // out of the building: clear at knee or head height for a while past the edge, not through glass
        // (a window is its own portal)
        private static bool OpenToOutside(Vector3 cellFloor, Cell cell, Vector2Int d, int wallMask, int glassMask)
        {
            var dir = new Vector3(d.x, 0f, d.y);
            float distance = CellSize * 0.5f + OutdoorProbe;
            foreach (float h in new[] { 1f, Mathf.Min(1.6f, cell.Ceiling - cell.Floor - 0.2f) })
            {
                var from = new Vector3(cellFloor.x, cell.Floor + h, cellFloor.z);
                if (!TarkovIndoorTriggers.Cast(from, dir, distance, wallMask, out _) && !Physics.Raycast(from, dir, distance, glassMask, QueryTriggerInteraction.Ignore))
                    return true;
            }
            return false;
        }

        // edges in a row along a wall (same rooms, same direction, side by side) make one opening
        private static IEnumerable<List<Edge>> Runs(List<Edge> edges)
        {
            foreach (var group in edges.GroupBy(e => (e.From, e.To, e.Dir)))
            {
                bool alongX = group.Key.Dir.y != 0; // a wall facing ±Z runs along X
                var sorted = group.OrderBy(e => alongX ? e.Z : e.X).ThenBy(e => alongX ? e.X : e.Z).ToList();
                var run = new List<Edge>();
                foreach (var e in sorted)
                {
                    var last = run.Count > 0 ? run[run.Count - 1] : default;
                    bool continues = run.Count > 0 && (alongX ? e.Z == last.Z && e.X == last.X + 1 : e.X == last.X && e.Z == last.Z + 1)
                                     && Mathf.Abs(e.Floor - last.Floor) < 0.6f;
                    if (run.Count > 0 && !continues)
                    {
                        yield return run;
                        run = new List<Edge>();
                    }
                    run.Add(e);
                }
                if (run.Count > 0)
                    yield return run;
            }
        }

        // the open part of a run: its width from rays across the edge every 25 cm at knee and head height,
        // its height from rays across at its middle every 20 cm
        private static bool Opening(List<Edge> run, System.Func<int, int, float, Vector3> cellPoint, int wallMask, out Vector3 center, out Vector2 size)
        {
            var first = run[0];
            var dir = new Vector3(first.Dir.x, 0f, first.Dir.y);
            var along = new Vector3(first.Dir.y != 0 ? 1f : 0f, 0f, first.Dir.x != 0 ? 1f : 0f);
            float floor = run.Min(e => e.Floor), ceiling = run.Max(e => e.Ceiling);
            var start = cellPoint(first.X, first.Z, floor) + dir * (CellSize * 0.5f) - along * (CellSize * 0.5f);
            float length = run.Count * CellSize;
            bool Clear(Vector3 at) => !TarkovIndoorTriggers.Cast(at - dir * 0.3f, dir, 0.6f, wallMask, out _);

            float min = float.MaxValue, max = float.MinValue;
            for (float t = SampleStep * 0.5f; t < length; t += SampleStep)
                if (Clear(start + along * t + Vector3.up * 1f) || Clear(start + along * t + Vector3.up * Mathf.Min(1.6f, ceiling - floor - 0.2f)))
                {
                    min = Mathf.Min(min, t - SampleStep * 0.5f);
                    max = Mathf.Max(max, t + SampleStep * 0.5f);
                }
            center = default;
            size = default;
            if (max <= min)
                return false;
            float mid = (min + max) * 0.5f;
            float bottom = float.MaxValue, top = float.MinValue;
            for (float h = 0.1f; h < ceiling - floor; h += 0.2f)
                if (Clear(start + along * mid + Vector3.up * h))
                {
                    bottom = Mathf.Min(bottom, h - 0.1f);
                    top = Mathf.Max(top, h + 0.1f);
                }
            if (top <= bottom)
            {
                bottom = 0f;
                top = Mathf.Min(2.2f, ceiling - floor);
            }
            center = start + along * mid + Vector3.up * ((bottom + top) * 0.5f);
            size = new Vector2(max - min, top - bottom);
            return true;
        }

        // a door leaf's or a pane's plane: its thinnest axis is the normal; the size is the other two, the
        // vertical one as the height
        private static bool Plane(Transform t, IEnumerable<Renderer> renderers, out Vector3 center, out Vector3 normal, out Vector2 size)
        {
            center = default;
            normal = default;
            size = default;
            bool any = false;
            Bounds local = default;
            foreach (var renderer in renderers)
            {
                if (renderer == null)
                    continue;
                var b = renderer.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = t.InverseTransformPoint(corner);
                    if (!any)
                        local = new Bounds(p, Vector3.zero);
                    local.Encapsulate(p);
                    any = true;
                }
            }
            if (!any)
                return false;
            var s = Vector3.Scale(local.size, t.lossyScale);
            var axes = new[] { t.right, t.up, t.forward };
            var sizes = new[] { Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z) };
            int thin = sizes[0] <= sizes[1] && sizes[0] <= sizes[2] ? 0 : sizes[1] <= sizes[2] ? 1 : 2;
            normal = axes[thin];
            normal.y = 0f;
            if (normal.sqrMagnitude < 0.01f)
                return false;
            normal.Normalize();
            var others = Enumerable.Range(0, 3).Where(i => i != thin).ToArray();
            // the more vertical of the other two is the height
            int up = Mathf.Abs(axes[others[0]].y) >= Mathf.Abs(axes[others[1]].y) ? others[0] : others[1];
            int across = others[0] == up ? others[1] : others[0];
            center = t.TransformPoint(local.center);
            size = new Vector2(sizes[across], sizes[up]);
            return true;
        }
    }
}
