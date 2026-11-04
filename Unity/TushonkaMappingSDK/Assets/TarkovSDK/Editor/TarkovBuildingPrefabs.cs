using System.Collections.Generic;
using System.Linq;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using Plan = TarkovSDK.TarkovBuilding;

namespace TarkovSDK.Editor
{
    // The Building Builder's prefabs and per-room settings: what bullets hit in each room (its own surface, else its
    // material's name, else the building's), the doors' settings put on whatever door is placed, room lamps' colour and
    // reach, stair models in place of the SDK's steps, and furniture.
    public static partial class TarkovBuildingBuilder
    {
        // ------------------------------------------------------------ surfaces

        // what a material's name says it is (Tarkov's and most packs' names carry it), null when it says nothing
        internal static Plan.Surface? NamedSurface(Material m)
        {
            if (m == null)
                return null;
            string n = m.name.ToLowerInvariant();
            if (n.Contains("wood") || n.Contains("plank") || n.Contains("parquet") || n.Contains("laminat") || n.Contains("board") || n.Contains("timber"))
                return Plan.Surface.Wood;
            if (n.Contains("metal") || n.Contains("steel") || n.Contains("iron") || n.Contains("rust") || n.Contains("tin_") || n.Contains("alumin"))
                return Plan.Surface.Metal;
            if (n.Contains("tile") || n.Contains("ceramic") || n.Contains("kafel"))
                return Plan.Surface.Tile;
            if (n.Contains("brick"))
                return Plan.Surface.Brick;
            if (n.Contains("plaster") || n.Contains("stucco") || n.Contains("wallpaper") || n.Contains("paint"))
                return Plan.Surface.Plaster;
            if (n.Contains("concrete") || n.Contains("cement") || n.Contains("beton"))
                return Plan.Surface.Concrete;
            return null;
        }

        private static Plan.Room RoomOf(Plan plan, int room) => room > 0 && room <= plan.Rooms.Count ? plan.Rooms[room - 1] : null;

        private static Plan.Surface Chosen(Plan.SurfaceChoice c) => (Plan.Surface)((int)c - 1);

        // a room's floor: the material's option, else the room's choice, else (a room's own material) its name, else the building's
        private static Plan.Surface FloorSurfaceOf(Plan plan, int room, Material floor)
        {
            if (floor != null && _options.TryGetValue(floor, out var o) && o.OverrideSurface)
                return o.Surface;
            var r = RoomOf(plan, room);
            if (r != null && r.FloorSurface != Plan.SurfaceChoice.Auto)
                return Chosen(r.FloorSurface);
            if (r != null && r.Floor != null && r.Floor == floor)
                return NamedSurface(floor) ?? plan.FloorSurface;
            return plan.FloorSurface;
        }

        // an inside wall between rooms A (-side) and B (+side) with these faces: either face's option, else either
        // room's choice, else a face that isn't the building's own inside material by its name, else the building's
        private static Plan.Surface InnerWallSurfaceOf(Plan plan, int a, int b, Material plus, Material minus, Materials mats)
        {
            foreach (var m in new[] { plus, minus })
                if (m != null && _options.TryGetValue(m, out var o) && o.OverrideSurface)
                    return o.Surface;
            foreach (var room in new[] { b, a })
            {
                var r = RoomOf(plan, room);
                if (r != null && r.WallSurface != Plan.SurfaceChoice.Auto)
                    return Chosen(r.WallSurface);
            }
            foreach (var m in new[] { plus, minus })
                if (m != null && m != mats.Interior && NamedSurface(m) is Plan.Surface named)
                    return named;
            return plan.InteriorSurface;
        }

        // an outer wall: its outer face's option, else a painted outside's name, else the building's
        private static Plan.Surface OuterWallSurfaceOf(Plan plan, Material outer, Materials mats)
        {
            if (outer != null && _options.TryGetValue(outer, out var o) && o.OverrideSurface)
                return o.Surface;
            if (outer != null && outer != mats.Exterior && NamedSurface(outer) is Plan.Surface named)
                return named;
            return plan.ExteriorSurface;
        }

        // ------------------------------------------------------------ doors and lamps

        // the opening's door settings on every door in what was placed (a prefab's are recorded as its overrides)
        private static void ApplyDoorSettings(GameObject go, Plan.DoorSettings d)
        {
            if (d == null || !d.Override)
                return;
            foreach (var door in go.GetComponentsInChildren<Door>(true))
            {
                door._doorState = d.State == Plan.DoorState.Open ? EDoorState.Open : d.State == Plan.DoorState.Locked ? EDoorState.Locked : EDoorState.Shut;
                door.KeyId = d.State == Plan.DoorState.Locked ? (d.KeyId ?? "").Trim() : "";
                door.CanBeBreached = d.CanBeBreached;
                door.CanInteractWithBreach = d.CanBeBreached;
                door.Operatable = d.Interactable;
                // the angle's sign is which way it swings
                door.OpenAngle = (door.OpenAngle < 0f ? -1f : 1f) * d.OpenAngle;
                if (PrefabUtility.IsPartOfPrefabInstance(door))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(door);
            }
        }

        // the room's lamp colour, brightness and reach on the lamp's lights
        private static void ApplyLampSettings(GameObject lamp, Plan.Room room, bool own)
        {
            if (room == null)
                return;
            foreach (var light in lamp.GetComponentsInChildren<Light>(true))
            {
                if (room.LampTint)
                    light.color = room.LampColor;
                light.intensity *= Mathf.Max(0f, room.LampIntensity);
                if (room.LampRange > 0f)
                    light.range = room.LampRange;
                if (own && PrefabUtility.IsPartOfPrefabInstance(light))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(light);
            }
        }

        // ------------------------------------------------------------ stair models

        internal static GameObject StairPrefabOf(Plan plan, Plan.Stair st) => st.Prefab != null ? st.Prefab : plan.DefaultStair;

        // whether a prefab takes bullets itself (else the SDK's steps stay as its bullet collision)
        private static bool HasBallistics(GameObject prefab) =>
            prefab.GetComponentsInChildren<Collider>(true).Any(c => c.gameObject.layer == LayerMask.NameToLayer("HighPolyCollider"));

        private static readonly Dictionary<GameObject, Quaternion> Climbs = new Dictionary<GameObject, Quaternion>();

        // The turn about the vertical that points the way a stair model climbs along +z, in its authored space (its root's
        // rotation and scale kept): of +-x and +-z, the one its vertices' height rises fastest along.
        private static Quaternion ClimbOf(GameObject prefab)
        {
            if (Climbs.TryGetValue(prefab, out var known))
                return known;
            var root = prefab.transform;
            var authored = Matrix4x4.TRS(Vector3.zero, root.rotation, root.lossyScale) * root.worldToLocalMatrix;
            var points = new List<Vector3>();
            foreach (var f in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (f.sharedMesh == null || !Shown(f.transform, root))
                    continue;
                var m = authored * f.transform.localToWorldMatrix;
                var v = f.sharedMesh.vertices;
                int step = Mathf.Max(1, v.Length / 4000);
                for (int i = 0; i < v.Length; i += step)
                    points.Add(m.MultiplyPoint3x4(v[i]));
            }
            var best = Vector3.forward;
            float most = float.MinValue;
            if (points.Count > 3)
            {
                float my = points.Average(p => p.y);
                foreach (var d in new[] { Vector3.forward, Vector3.back, Vector3.right, Vector3.left })
                {
                    float mx = points.Average(p => Vector3.Dot(p, d));
                    float cov = points.Sum(p => (Vector3.Dot(p, d) - mx) * (p.y - my));
                    float var = points.Sum(p => (Vector3.Dot(p, d) - mx) * (Vector3.Dot(p, d) - mx));
                    float slope = var > 1e-6f ? cov / var : 0f;
                    if (slope > most)
                    {
                        most = slope;
                        best = d;
                    }
                }
            }
            var turn = Quaternion.Inverse(Quaternion.LookRotation(best, Vector3.up));
            Climbs[prefab] = turn;
            return turn;
        }

        // The stair model on the flight: turned so it climbs along the flight, its bottom front edge's middle on the
        // flight's start; stretched to the run, rise and width when the flight says so (per axis when its turns are
        // quarter turns, else evenly by the run).
        private static GameObject PlaceStair(Plan plan, Plan.Stair st, GameObject prefab, Transform parent, Transform building, float y0, float y1)
        {
            float cell = plan.CellSize;
            int dir = ((st.Direction % 4) + 4) % 4;
            var along = new Vector3(Dirs[dir].x, 0f, Dirs[dir].y);
            var across = new Vector3(Dirs[dir].y, 0f, -Dirs[dir].x);
            int len = Mathf.Max(1, st.Length), wid = Mathf.Max(1, st.Width);
            var cellCentre = new Vector3((st.X + 0.5f) * cell, y0, (st.Z + 0.5f) * cell);
            var start = cellCentre - along * (cell * 0.5f) + across * ((wid - 1) * cell * 0.5f);
            var target = new Vector3(wid * cell - 0.02f, y1 - y0, len * cell);

            var root = prefab.transform;
            var climb = Quaternion.AngleAxis(90f * st.PrefabTurn, Vector3.up) * ClimbOf(prefab);
            var authored = Matrix4x4.TRS(Vector3.zero, root.rotation, root.lossyScale) * root.worldToLocalMatrix;
            if (!MeshBounds(prefab, Matrix4x4.Rotate(climb) * authored, out var b, out _, out _))
                b = new Bounds(Vector3.zero, Vector3.one);
            var stretch = Vector3.one;
            if (st.FitToFlight)
                stretch = new Vector3(target.x / Mathf.Max(0.01f, b.size.x), target.y / Mathf.Max(0.01f, b.size.y), target.z / Mathf.Max(0.01f, b.size.z));

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, building.gameObject.scene);
            go.transform.SetParent(parent, false);
            var turn = climb * root.localRotation;
            // each of the instance's own axes lands on one of the flight's (across, up, along) when the turns are square
            var scale = root.localScale;
            bool square = true;
            var k = Vector3.one;
            for (int i = 0; i < 3; i++)
            {
                var axis = turn * (i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward);
                int j = Mathf.Abs(axis.x) > 0.99f ? 0 : Mathf.Abs(axis.y) > 0.99f ? 1 : Mathf.Abs(axis.z) > 0.99f ? 2 : -1;
                if (j < 0)
                    square = false;
                else
                    k[i] = stretch[j];
            }
            if (!square)
            {
                stretch = Vector3.one * stretch.z;
                k = stretch;
            }
            go.transform.localScale = Vector3.Scale(scale, k);
            var front = new Vector3(b.center.x, b.min.y, b.min.z);
            var frame = Quaternion.LookRotation(along, Vector3.up);
            go.transform.localRotation = frame * turn;
            go.transform.localPosition = start - frame * Vector3.Scale(front, stretch);
            return go;
        }

        // the flight's walking slope, floor to floor, as a quad facing up (clockwise from above)
        private static Vector3[] RampQuad(Plan plan, Plan.Stair st, float y0, float y1)
        {
            float cell = plan.CellSize;
            int dir = ((st.Direction % 4) + 4) % 4;
            var along = new Vector3(Dirs[dir].x, 0f, Dirs[dir].y);
            var across = new Vector3(Dirs[dir].y, 0f, -Dirs[dir].x);
            int len = Mathf.Max(1, st.Length), wid = Mathf.Max(1, st.Width);
            var cellCentre = new Vector3((st.X + 0.5f) * cell, y0, (st.Z + 0.5f) * cell);
            var start = cellCentre - along * (cell * 0.5f) + across * ((wid - 1) * cell * 0.5f);
            var half = across * (wid * cell * 0.5f - 0.01f);
            var end = start + along * (len * cell) + Vector3.up * (y1 - y0);
            // across is along's right: start-left, end-left, end-right, start-right runs clockwise seen from above
            return new[] { start - half, end - half, end + half, start + half };
        }

        // ------------------------------------------------------------ furniture

        private static readonly Dictionary<GameObject, Bounds> PropBounds = new Dictionary<GameObject, Bounds>();

        // a prefab's meshes' bounds with its root's rotation and scale, round its pivot (for the window's footprints
        // and snapping to walls)
        internal static Bounds PrefabBounds(GameObject prefab)
        {
            if (prefab == null)
                return new Bounds(Vector3.up * 0.5f, Vector3.one);
            if (PropBounds.TryGetValue(prefab, out var known))
                return known;
            var root = prefab.transform;
            var authored = Matrix4x4.TRS(Vector3.zero, root.rotation, root.lossyScale) * root.worldToLocalMatrix;
            if (!MeshBounds(prefab, authored, out var b, out _, out _))
                b = new Bounds(Vector3.up * 0.5f, Vector3.one);
            PropBounds[prefab] = b;
            return b;
        }

        internal static void ForgetPrefabSizes()
        {
            PropBounds.Clear();
            Climbs.Clear();
            Units.Clear();
        }

        // the storey's furniture, as prefab copies under Furniture
        private static void PlaceProps(Plan plan, int storey, Transform group, Transform building, Result result)
        {
            Transform holder = null;
            foreach (var p in plan.Props ?? new List<Plan.Prop>())
            {
                if (p == null || p.Storey != storey || p.Prefab == null)
                    continue;
                holder ??= Child(group, "Furniture");
                var go = (GameObject)PrefabUtility.InstantiatePrefab(p.Prefab, building.gameObject.scene);
                go.transform.SetParent(holder, false);
                go.transform.localPosition = p.Position + Vector3.up * plan.Base(storey);
                go.transform.localRotation = Quaternion.Euler(0f, p.Yaw, 0f) * p.Prefab.transform.localRotation;
                go.transform.localScale = p.Prefab.transform.localScale * (p.Scale > 0f ? p.Scale : 1f);
                result.Props++;
            }
        }

        // ------------------------------------------------------------ before 1.10

        // A rebuilt door used to leave CreateDoor's root ("Door", the leaf moved out of it) at the top of the active
        // scene, one per door per rebuild: removed wherever they are, empty and alone as they were left.
        private static int RemoveStrayDoorRoots()
        {
            int removed = 0;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;
                int before = removed;
                foreach (var root in scene.GetRootGameObjects())
                    if (root.name == "Door" && root.transform.childCount == 0 && root.GetComponents<Component>().Length == 1)
                    {
                        Object.DestroyImmediate(root);
                        removed++;
                    }
                if (removed > before)
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            }
            return removed;
        }
    }
}
