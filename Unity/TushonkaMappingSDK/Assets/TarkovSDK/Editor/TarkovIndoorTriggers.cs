using System.Collections.Generic;
using System.Linq;
using EFT.EnvironmentEffect;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Indoor triggers generated from the map's geometry.
    //
    // 1. A grid of rays from above finds every upward-facing surface of each column (roof, upper
    //    floors, ground, basement floors). For each one, a ray up from it finds the ceiling as seen
    //    from inside. Mesh colliders are one-sided, so from under a hill that ray passes up through
    //    the ground and finds sky; Unity terrain never counts as a ceiling either. The soil between
    //    a hill's surface and a basement roof is therefore never mistaken for a room.
    // 2. A floor with a ceiling and head room is a room cell when walls surround it: of eight rays
    //    out from it (at knee and head height, so a doorway or window doesn't open it up), at least
    //    WallsNeeded hit something within MaxRoomSize. Carports, overhangs and bridges stay outdoors.
    // 3. Neighbouring room cells at the same storey join into one room. Gaps inside it with
    //    something overhead (under a table, a pillar) are filled, and the room is covered with as
    //    few boxes as its shape allows: one for a rectangular room, two for an L. Rooms that touch
    //    become one TriggerGroup per building (the game tests a group's bounds before its triggers).
    //
    // Only real geometry counts: colliders under an EditorOnly object, and colliders without a
    // mesh (invisible volumes and blockers), are ignored. Everything goes in a generated group
    // under the map's IndoorTriggers, which regenerating replaces. Hand-placed triggers are kept,
    // and floors already inside one are skipped.
    public static class TarkovIndoorTriggers
    {
        public const string RootName = "GeneratedIndoorTriggers";
        private const float CellSize = 1f;
        private const float HeadRoom = 1.8f;
        private const float MaxCeiling = 30f;
        private const float MaxRoomSize = 40f;
        private const int WallsNeeded = 6;
        private const int MaxSurfaces = 24;
        private const float MinRoomArea = 4f;

        private static readonly Vector3[] Directions =
            Enumerable.Range(0, 8).Select(i => Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward).ToArray();
        private static readonly RaycastHit[] Hits = new RaycastHit[64];
        private static readonly Dictionary<Collider, bool> Counted = new Dictionary<Collider, bool>();

        private class Cell
        {
            public float Floor, Ceiling;
            public int Room = -1;
        }

        private class Room
        {
            public readonly List<Vector2Int> Cells = new List<Vector2Int>();
            public float Floor = float.MaxValue, Ceiling = float.MinValue;
        }

        [MenuItem("Tarkov SDK/Tools/Generate Indoor Triggers", priority = 222)]
        public static void GenerateMenu()
        {
            var scene = SceneManager.GetActiveScene();
            var root = Generate(scene);
            if (root != null)
            {
                Selection.activeGameObject = root;
                EditorSceneManager.MarkSceneDirty(root.scene);
            }
        }

        public static GameObject Find() =>
            Object.FindObjectsOfType<TriggerGroup>(true).Select(g => g.gameObject).FirstOrDefault(g => g.name == RootName);

        public static GameObject Generate(Scene scene)
        {
            Counted.Clear();
            int mask = TarkovRainCover.SolidMask;
            // closed doors count as walls
            int wallMask = mask | LayerMask.GetMask("Interactive");
            if (!TarkovRainCover.SolidBounds(scene, mask, out var bounds, Counts))
            {
                EditorUtility.DisplayDialog("Indoor Triggers", "The scene has no solid mesh colliders to scan.", "OK");
                return null;
            }

            Physics.SyncTransforms();
            var old = Find();
            var manual = Object.FindObjectsOfType<IndoorTrigger>(true)
                .Where(t => old == null || !t.transform.IsChildOf(old.transform))
                .Select(t => TarkovScenePreparer.TransformBounds(t.transform)).ToList();
            float top = bounds.max.y + 10f;
            int nx = Mathf.CeilToInt(bounds.size.x / CellSize), nz = Mathf.CeilToInt(bounds.size.z / CellSize);
            var cells = new List<Cell>[nx, nz];
            var boxes = new List<Bounds>();
            int roomCount = 0;
            // ceilings are found from below, which only works when a mesh's back faces aren't hit
            bool backfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = false;
            try
            {
                for (int iz = 0; iz < nz; iz++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Indoor Triggers", "Scanning for rooms", iz / (float)nz))
                        return null;
                    for (int ix = 0; ix < nx; ix++)
                        cells[ix, iz] = Scan(new Vector3(bounds.min.x + (ix + 0.5f) * CellSize, top, bounds.min.z + (iz + 0.5f) * CellSize),
                            mask, wallMask, manual);
                }
                EditorUtility.DisplayProgressBar("Indoor Triggers", "Building rooms", 1f);
                foreach (var room in FindRooms(cells, nx, nz))
                {
                    var roomBoxes = CoverRoom(room, bounds.min, mask);
                    if (roomBoxes.Sum(b => b.size.x * b.size.z) < MinRoomArea)
                        continue;
                    boxes.AddRange(roomBoxes);
                    roomCount++;
                }
            }
            finally
            {
                Physics.queriesHitBackfaces = backfaces;
                EditorUtility.ClearProgressBar();
                Counted.Clear();
            }

            var buildings = GroupBuildings(boxes);
            if (old != null)
                Undo.DestroyObjectImmediate(old);
            if (buildings.Count == 0)
            {
                Debug.Log("[Tarkov SDK] Indoor triggers: found no rooms (a ceiling with walls around it); the whole map counts as outdoors.");
                return null;
            }

            var root = new GameObject(RootName);
            root.transform.SetParent(TarkovSceneTools.IndoorTriggerGroup().transform, false);
            root.AddComponent<TriggerGroup>();
            for (int i = 0; i < buildings.Count; i++)
            {
                var all = buildings[i][0];
                foreach (var box in buildings[i])
                    all.Encapsulate(box);
                var group = new GameObject($"Building_{i + 1}");
                group.transform.SetParent(root.transform, false);
                group.transform.SetPositionAndRotation(all.center, Quaternion.identity);
                group.AddComponent<TriggerGroup>();
                foreach (var box in buildings[i])
                {
                    var trigger = new GameObject("IndoorTrigger");
                    trigger.transform.SetParent(group.transform, false);
                    trigger.transform.SetPositionAndRotation(box.center, Quaternion.identity);
                    // the trigger is the box of its transform
                    var parentScale = group.transform.lossyScale;
                    trigger.transform.localScale = new Vector3(box.size.x / parentScale.x, box.size.y / parentScale.y, box.size.z / parentScale.z);
                    trigger.AddComponent<IndoorTrigger>();
                }
            }
            Undo.RegisterCreatedObjectUndo(root, "Generate indoor triggers");
            Debug.Log($"[Tarkov SDK] Indoor triggers: {roomCount} room(s), {boxes.Count} trigger box(es) in {buildings.Count} building group(s). " +
                      "Check them in the Scene view and delete or resize any that are wrong.");
            return root;
        }

        // ------------------------------------------------------------ scan

        // the room cells of one column, one per storey
        private static List<Cell> Scan(Vector3 from, int mask, int wallMask, List<Bounds> manual)
        {
            List<Cell> rooms = null;
            for (int i = 0; i < MaxSurfaces && Cast(from, Vector3.down, 2000f, mask, out var floor); i++)
            {
                from = floor.point + Vector3.down * 0.05f;
                if (floor.normal.y < 0.7f)
                    continue;
                if (!Cast(floor.point + Vector3.up * 0.1f, Vector3.up, MaxCeiling, mask, out var ceiling) || ceiling.collider is TerrainCollider)
                    continue;
                float height = ceiling.point.y - floor.point.y;
                if (height >= HeadRoom && Enclosed(floor.point, height, wallMask) && !manual.Any(b => b.Contains(floor.point + Vector3.up)))
                    (rooms ??= new List<Cell>()).Add(new Cell { Floor = floor.point.y, Ceiling = ceiling.point.y });
            }
            return rooms;
        }

        private static bool Enclosed(Vector3 floor, float height, int mask)
        {
            var knee = floor + Vector3.up * 0.5f;
            var head = floor + Vector3.up * Mathf.Min(1.5f, height - 0.2f);
            int walls = 0;
            for (int i = 0; i < Directions.Length; i++)
            {
                if (Cast(knee, Directions[i], MaxRoomSize, mask, out _) || Cast(head, Directions[i], MaxRoomSize, mask, out _))
                    walls++;
                if (walls + Directions.Length - 1 - i < WallsNeeded)
                    return false;
            }
            return true;
        }

        // the nearest hit on a collider that counts
        private static bool Cast(Vector3 from, Vector3 direction, float distance, int mask, out RaycastHit hit)
        {
            int count = Physics.RaycastNonAlloc(from, direction, Hits, distance, mask, QueryTriggerInteraction.Ignore);
            hit = default;
            bool found = false;
            for (int i = 0; i < count; i++)
                if ((!found || Hits[i].distance < hit.distance) && Counts(Hits[i].collider))
                {
                    hit = Hits[i];
                    found = true;
                }
            return found;
        }

        // real geometry only: not editor-only, and a mesh (a mesh collider, terrain, or a rendered mesh)
        private static bool Counts(Collider collider)
        {
            if (Counted.TryGetValue(collider, out var counts))
                return counts;
            bool editorOnly = false;
            for (var t = collider.transform; t != null && !editorOnly; t = t.parent)
                editorOnly = t.CompareTag("EditorOnly");
            bool meshed = collider is TerrainCollider
                          || (collider is MeshCollider meshCollider && meshCollider.sharedMesh != null)
                          || (collider.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null && collider.TryGetComponent<Renderer>(out _));
            return Counted[collider] = !editorOnly && meshed;
        }

        // ------------------------------------------------------------ rooms

        // room cells joined to their neighbours on the same storey (similar floor and ceiling)
        private static List<Room> FindRooms(List<Cell>[,] cells, int nx, int nz)
        {
            var rooms = new List<Room>();
            var queue = new Queue<(int x, int z, Cell cell)>();
            for (int iz = 0; iz < nz; iz++)
                for (int ix = 0; ix < nx; ix++)
                {
                    if (cells[ix, iz] == null)
                        continue;
                    foreach (var seed in cells[ix, iz])
                    {
                        if (seed.Room >= 0)
                            continue;
                        var room = new Room();
                        seed.Room = rooms.Count;
                        rooms.Add(room);
                        queue.Enqueue((ix, iz, seed));
                        while (queue.Count > 0)
                        {
                            var (x, z, cell) = queue.Dequeue();
                            room.Cells.Add(new Vector2Int(x, z));
                            room.Floor = Mathf.Min(room.Floor, cell.Floor);
                            room.Ceiling = Mathf.Max(room.Ceiling, cell.Ceiling);
                            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                            {
                                int x2 = x + dx, z2 = z + dz;
                                if (x2 < 0 || z2 < 0 || x2 >= nx || z2 >= nz || cells[x2, z2] == null)
                                    continue;
                                var next = cells[x2, z2].FirstOrDefault(c => c.Room < 0 &&
                                    Mathf.Abs(c.Floor - cell.Floor) < 0.5f && Mathf.Abs(c.Ceiling - cell.Ceiling) < 1f);
                                if (next == null)
                                    continue;
                                next.Room = seed.Room;
                                queue.Enqueue((x2, z2, next));
                            }
                        }
                    }
                }
            return rooms;
        }

        // as few boxes as cover the room's floor plan, from its lowest floor to its highest ceiling
        private static List<Bounds> CoverRoom(Room room, Vector3 origin, int mask)
        {
            int x0 = room.Cells.Min(c => c.x), z0 = room.Cells.Min(c => c.y);
            int w = room.Cells.Max(c => c.x) - x0 + 1, h = room.Cells.Max(c => c.y) - z0 + 1;
            var plan = new bool[w, h];
            foreach (var c in room.Cells)
                plan[c.x - x0, c.y - z0] = true;
            FillHoles(plan, w, h, (x, z) =>
                Cast(new Vector3(origin.x + (x0 + x + 0.5f) * CellSize, room.Floor + 0.5f, origin.z + (z0 + z + 0.5f) * CellSize),
                    Vector3.up, room.Ceiling - room.Floor, mask, out var hit) && !(hit.collider is TerrainCollider));

            var boxes = new List<Bounds>();
            var done = new bool[w, h];
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    if (!plan[x, z] || done[x, z])
                        continue;
                    int x1 = x + 1;
                    while (x1 < w && plan[x1, z] && !done[x1, z])
                        x1++;
                    int z1 = z + 1;
                    while (z1 < h && Enumerable.Range(x, x1 - x).All(xx => plan[xx, z1] && !done[xx, z1]))
                        z1++;
                    for (int zz = z; zz < z1; zz++)
                        for (int xx = x; xx < x1; xx++)
                            done[xx, zz] = true;
                    var min = new Vector3(origin.x + (x0 + x) * CellSize, room.Floor - 0.2f, origin.z + (z0 + z) * CellSize);
                    var max = new Vector3(origin.x + (x0 + x1) * CellSize, room.Ceiling, origin.z + (z0 + z1) * CellSize);
                    boxes.Add(new Bounds((min + max) * 0.5f, max - min));
                }
            return boxes;
        }

        // cells the room surrounds (not reachable from the plan's edge) that have something overhead;
        // an open courtyard inside a building has sky above and stays out
        private static void FillHoles(bool[,] plan, int w, int h, System.Func<int, int, bool> covered)
        {
            var outside = new bool[w, h];
            var queue = new Queue<Vector2Int>();
            for (int x = 0; x < w; x++)
                for (int z = 0; z < h; z++)
                    if ((x == 0 || z == 0 || x == w - 1 || z == h - 1) && !plan[x, z])
                    {
                        outside[x, z] = true;
                        queue.Enqueue(new Vector2Int(x, z));
                    }
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                foreach (var d in new[] { Vector2Int.right, Vector2Int.left, Vector2Int.up, Vector2Int.down })
                {
                    var n = c + d;
                    if (n.x < 0 || n.y < 0 || n.x >= w || n.y >= h || plan[n.x, n.y] || outside[n.x, n.y])
                        continue;
                    outside[n.x, n.y] = true;
                    queue.Enqueue(n);
                }
            }
            for (int x = 0; x < w; x++)
                for (int z = 0; z < h; z++)
                    if (!plan[x, z] && !outside[x, z] && covered(x, z))
                        plan[x, z] = true;
        }

        // boxes that touch belong to one building
        private static List<List<Bounds>> GroupBuildings(List<Bounds> boxes)
        {
            var parent = Enumerable.Range(0, boxes.Count).ToArray();
            int Root(int i)
            {
                while (parent[i] != i)
                    i = parent[i] = parent[parent[i]];
                return i;
            }
            for (int i = 0; i < boxes.Count; i++)
            {
                var a = boxes[i];
                a.Expand(0.1f);
                for (int j = i + 1; j < boxes.Count; j++)
                    if (a.Intersects(boxes[j]))
                        parent[Root(i)] = Root(j);
            }
            return Enumerable.Range(0, boxes.Count).GroupBy(Root).Select(g => g.Select(i => boxes[i]).ToList()).ToList();
        }
    }
}
