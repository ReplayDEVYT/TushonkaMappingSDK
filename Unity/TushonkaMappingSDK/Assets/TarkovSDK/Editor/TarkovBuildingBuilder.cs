using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EFT.Ballistics;
using EFT.EnvironmentEffect;
using EFT.Interactive;
using TarkovSDK;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;
using Plan = TarkovSDK.TarkovBuilding;

namespace TarkovSDK.Editor
{
    // Builds a TarkovBuilding plan (see Runtime/TarkovBuilding.cs) under its building's Generated child, replacing what
    // was there; everything else under the building (props placed in it) is kept.
    //
    // Everything is boxes in the building's own space (x along Width, z along Depth, cells from the pivot):
    //  - walls stand on the cell edges where the rooms either side differ (outer walls next to no room), centred on the
    //    edge, from the floor to the slab above; a run of edges is one wall, cut round its openings into piers, the
    //    part under a window and the part over an opening. Walls along x run on over the corners by half the
    //    thickness of the walls along z meeting there, which stop short by half theirs, so corners close without
    //    overlapping. Each face takes the material of the room it faces (the outside's where there's none).
    //  - floors are slabs under each storey's cells (the ground floor's on the plinth), cut by (room above, room below)
    //    so each top shows its room's floor and each bottom the ceiling of the room under it; open over stairs and
    //    the cells below with an open ceiling. Cells with nothing above get a roof slab (none where the ceiling is open); a flat top has a parapet round its edge, a gable roof two sloped slabs over
    //    the top storey's outline with gable ends.
    //  - stairs are a stack of step boxes (bullets), with a ramp to walk on (movement), as retail stairs.
    //  - bevels round off (or cut) room corners with curved walls, and trims run mouldings along the walls: both are
    //    free-form geometry (TarkovBuildingCorners.cs, TarkovBuildingTrims.cs), merged into the same meshes, its
    //    collision a mesh collider per group.
    //  - outer walls with nothing over them carry their outer half up through the roof slab (else a notch shows
    //    under the roof's edge), topped with the trim material.
    // Faces hidden inside another box are dropped, and the rest merged into one mesh per LOD, a submesh per material:
    // LOD0 everything, LOD1 only what faces outside (culled when under 1% of the screen). Collision is the same boxes'
    // uncovered faces and the curved shapes as mesh colliders, one per storey for LowPolyCollider (movement) and one per
    // surface for HighPolyCollider with a BallisticCollider (bullets), as retail's _COLLIDER / _BALLISTIC_<material>. Doors are the SDK's (or the plan's prefab), windows the SDK's
    // breakable glass, with frames; a lamp lights each part of a room. Darkness volumes (StencilShadow) fill each room
    // 5 cm into its walls, ambient portals sit in every opening to the outside, a rain cover (DryPlane) is under every
    // roof, and indoor volumes (IndoorTrigger boxes on an EditorOnly object; Build Map copies them under the map's
    // EnvironmentManager, PrepareMap) cover the rooms.
    public static partial class TarkovBuildingBuilder
    {
        // faces hidden inside other boxes are dropped (off only for tests)
        internal static bool CullHidden = true;
        public const string GeneratedName = "Generated";
        public const string PlanName = "SDK_BuildingPlan";
        public const string IndoorName = "SDK_IndoorVolumes";
        public const string MapIndoorName = "SDK_BuildingIndoorTriggers";
        private const float Inset = 0.05f;

        [Flags]
        private enum Collide { None = 0, Low = 1, High = 2, Both = 3 }

        // a box in the building's space; faces +x, -x, +y, -y, +z, -z
        private sealed class Part
        {
            public Vector3 Center;
            public Quaternion Rotation = Quaternion.identity;
            public Vector3 Size;
            public readonly Material[] Faces = new Material[6];
            public readonly bool[] Outside = new bool[6];
            public Plan.Surface Surface;
            public Collide Collide = Collide.Both;
            // takes part in hiding other boxes' faces (frames and trims don't)
            public bool Solid = true;
            // the storey it's built with (the roof after the last)
            public int Group;
            // a piece of a cut cell instead of a box: this convex outline (anticlockwise, x z) from Center.y - Size.y / 2
            // to Center.y + Size.y / 2; only hides faces (its faces and collision are a Shape's)
            public Vector2[] Poly;

            public bool Contains(Vector3 p)
            {
                if (Poly != null)
                {
                    if (Mathf.Abs(p.y - Center.y) >= Size.y * 0.5f)
                        return false;
                    for (int i = 0; i < Poly.Length; i++)
                        if (Cross(Poly[(i + 1) % Poly.Length] - Poly[i], new Vector2(p.x, p.z) - Poly[i]) < 1e-5f)
                            return false;
                    return true;
                }
                var l = Quaternion.Inverse(Rotation) * (p - Center);
                return Mathf.Abs(l.x) < Size.x * 0.5f && Mathf.Abs(l.y) < Size.y * 0.5f && Mathf.Abs(l.z) < Size.z * 0.5f;
            }
        }

        private static readonly Vector3[] Normals = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };

        // the resolved materials (the plan's, else the SDK's)
        private sealed class Materials
        {
            public Material Exterior, Interior, Floor, Ceiling, Roof, Trim;
        }

        public sealed class Result
        {
            public int Parts, Triangles, Doors, Windows, Lamps, Rooms, Portals, Props, Chunks;
            public string Rebuilt = "";
            public readonly List<string> Notes = new List<string>();
            public override string ToString() => $"Rebuilt {Rebuilt}: {Rooms} room part(s), {Doors} door(s), {Windows} window(s), {Lamps} lamp(s), {Portals} ambient portal(s); " +
                                                 $"{Parts} boxes, {Triangles} triangles" + (Props > 0 ? $", {Props} furniture" : "") + (Chunks > 0 ? $", {Chunks} debris chunks" : "") + (Notes.Count > 0 ? ". " + string.Join("; ", Notes) : "");
        }

        // ------------------------------------------------------------ the plan's objects

        public static Plan PlanOf(GameObject go)
        {
            if (go == null)
                return null;
            var plan = go.GetComponentInChildren<Plan>(true);
            if (plan != null)
                return plan;
            return go.GetComponentInParent<Plan>(true) ?? go.transform.root.GetComponentInChildren<Plan>(true);
        }

        public static Transform Building(Plan plan) => plan.transform.parent != null ? plan.transform.parent : plan.transform;

        // a new building at the position with a starter plan: two rooms, a door, windows
        public static Plan Create(UnityEngine.SceneManagement.Scene scene, Vector3 position, string name)
        {
            var root = new GameObject(name);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            root.transform.position = position;
            var planGo = new GameObject(PlanName) { tag = "EditorOnly" };
            planGo.transform.SetParent(root.transform, false);
            var plan = planGo.AddComponent<Plan>();
            plan.Width = 10;
            plan.Depth = 8;
            plan.Rooms.Add(new Plan.Room { Name = "Hall", Color = new Color(0.45f, 0.7f, 1f) });
            plan.Rooms.Add(new Plan.Room { Name = "Room", Color = new Color(1f, 0.7f, 0.35f) });
            var storey = new Plan.Storey { Height = 3f };
            plan.Storeys.Add(storey);
            plan.Fit(storey);
            for (int z = 0; z < plan.Depth; z++)
                for (int x = 0; x < plan.Width; x++)
                    plan.Set(0, x, z, x < 6 ? 1 : 2);
            plan.Openings.Add(new Plan.Opening { Storey = 0, X = 2, Z = 0, Kind = Plan.OpeningKind.Door, Width = 1.1f, Height = 2.1f });
            plan.Openings.Add(new Plan.Opening { Storey = 0, X = 6, Z = 3, AlongZ = true, Kind = Plan.OpeningKind.Door, Width = 1.1f, Height = 2.1f });
            plan.Openings.Add(new Plan.Opening { Storey = 0, X = 4, Z = 8, Kind = Plan.OpeningKind.Window, Width = 1.2f, Height = 1.2f, Sill = 0.9f, Span = 2 });
            plan.Openings.Add(new Plan.Opening { Storey = 0, X = 10, Z = 3, AlongZ = true, Kind = Plan.OpeningKind.Window, Width = 1.2f, Height = 1.2f, Sill = 0.9f, Span = 2 });
            plan.Openings.Add(new Plan.Opening { Storey = 0, X = 0, Z = 3, AlongZ = true, Kind = Plan.OpeningKind.Window, Width = 1.2f, Height = 1.2f, Sill = 0.9f, Span = 2 });
            Undo.RegisterCreatedObjectUndo(root, "New building");
            return plan;
        }

        // What the building's look depends on, per group (each storey, then the roof): the shared settings, the storey's
        // place and height, its cells and its neighbours' (floors, roofs and hidden faces depend on them), its openings,
        // its stairs and the ones below. The stored hashes are left out (they'd change the hash they're compared with).
        public static List<string> GroupHashes(Plan plan)
        {
            var storeys = plan.Storeys;
            var openings = plan.Openings;
            var stairs = plan.Stairs;
            var bevels = plan.Bevels ?? new List<Plan.Bevel>();
            var paints = plan.Paints ?? new List<Plan.WallPaint>();
            var props = plan.Props ?? new List<Plan.Prop>();
            var holes = plan.Holes ?? new List<Plan.Hole>();
            var built = plan.BuiltHash;
            var groups = plan.BuiltGroups;
            string shared;
            try
            {
                plan.Storeys = new List<Plan.Storey>();
                plan.Openings = new List<Plan.Opening>();
                plan.Stairs = new List<Plan.Stair>();
                plan.Bevels = new List<Plan.Bevel>();
                plan.Paints = new List<Plan.WallPaint>();
                plan.Props = new List<Plan.Prop>();
                plan.Holes = new List<Plan.Hole>();
                plan.BuiltHash = null;
                plan.BuiltGroups = new List<string>();
                shared = JsonUtility.ToJson(plan) + storeys.Count;
            }
            finally
            {
                plan.Storeys = storeys;
                plan.Openings = openings;
                plan.Stairs = stairs;
                plan.Bevels = bevels;
                plan.Paints = paints;
                plan.Props = props;
                plan.Holes = holes;
                plan.BuiltHash = built;
                plan.BuiltGroups = groups;
            }
            // trims stop at openings on the storeys either side too
            bool trims = plan.Trims != null && plan.Trims.Any(t => t != null && t.Enabled);
            string Cells(int s) => s >= 0 && s < storeys.Count && storeys[s].Cells != null ? string.Join(",", storeys[s].Cells) : "-";
            var result = new List<string>();
            for (int s = 0; s <= storeys.Count; s++)
            {
                var sb = new System.Text.StringBuilder(shared);
                sb.Append('|').Append(s).Append('|').Append(plan.Base(s)).Append('|');
                if (s < storeys.Count)
                {
                    // the storey's height, cells and outside material
                    sb.Append(JsonUtility.ToJson(storeys[s])).Append('|').Append(Cells(s - 1)).Append('|').Append(Cells(s + 1));
                    // the floor opens where the storey below has no ceiling
                    if (s > 0 && storeys[s - 1].OpenCeiling != null)
                        sb.Append('|').Append(string.Join(";", storeys[s - 1].OpenCeiling));
                    foreach (var o in openings.Where(o => o.Storey == s || (trims && Mathf.Abs(o.Storey - s) == 1)))
                        sb.Append('|').Append(JsonUtility.ToJson(o));
                    foreach (var st in stairs.Where(st => st.Storey == s || st.Storey == s - 1))
                        sb.Append('|').Append(JsonUtility.ToJson(st));
                    // its bevels, and the ones above (its walls' tops depend on them)
                    foreach (var b in bevels.Where(b => b != null && (b.Storey == s || b.Storey == s + 1)))
                        sb.Append('|').Append(JsonUtility.ToJson(b));
                    foreach (var p in paints.Where(p => p != null && p.Storey == s))
                        sb.Append('|').Append(JsonUtility.ToJson(p));
                    foreach (var p in props.Where(p => p != null && p.Storey == s))
                        sb.Append('|').Append(JsonUtility.ToJson(p));
                    // the holes reaching into its boxes (its footings go down through the slab, its caps up to the
                    // next floor), and those whose debris it holds
                    float lo = plan.Base(s) - plan.SlabThickness - 0.5f, hi = plan.Base(s + 1) + 0.5f;
                    foreach (var h in holes.Where(h => h != null && ((h.Center.y + Mathf.Abs(h.Size.y) * 0.5f > lo && h.Center.y - Mathf.Abs(h.Size.y) * 0.5f < hi) || HoleStorey(plan, h) == s)))
                        sb.Append('|').Append(JsonUtility.ToJson(h));
                }
                else if (s > 0)
                    sb.Append(JsonUtility.ToJson(storeys[s - 1]));
                result.Add(Hash128.Compute(sb.ToString()).ToString());
            }
            return result;
        }

        public static string Hash(Plan plan) => Hash128.Compute(string.Join(",", GroupHashes(plan)) + Building(plan).name).ToString();

        private static string GroupName(Plan plan, int g) => g < plan.Storeys.Count ? $"Storey_{g + 1}" : "Roof";

        // where its meshes are saved: next to its prefab when it's open in Prefab Mode, else beside the scene
        internal static string PartsFolder(Plan plan)
        {
            var root = Building(plan).gameObject;
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            string name = Safe(root.name);
            if (stage != null && stage.scene == root.scene)
                return $"{Path.GetDirectoryName(stage.assetPath).Replace('\\', '/')}/{Path.GetFileNameWithoutExtension(stage.assetPath)}_Parts";
            var scenePath = root.scene.path;
            string sceneFolder = string.IsNullOrEmpty(scenePath) ? "Assets" : Path.GetDirectoryName(scenePath).Replace('\\', '/');
            string sceneName = string.IsNullOrEmpty(root.scene.name) ? "Untitled" : root.scene.name;
            return $"{sceneFolder}/{sceneName}_Buildings/{name}";
        }

        private static string Safe(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }

        // the building's materials (its own, else the SDK's)
        private static Materials Resolve(Plan plan) => new Materials
        {
            Exterior = plan.ExteriorMaterial != null ? plan.ExteriorMaterial : TarkovGeneratedAssets.SurfaceMaterial("SDK_Building_Brick", new Color(0.55f, 0.32f, 0.25f), 0.25f),
            Interior = plan.InteriorMaterial != null ? plan.InteriorMaterial : TarkovGeneratedAssets.SurfaceMaterial("SDK_Building_Plaster", new Color(0.78f, 0.75f, 0.68f), 0.08f),
            Floor = plan.FloorMaterial != null ? plan.FloorMaterial : TarkovGeneratedAssets.SurfaceMaterial("SDK_Building_Floor", new Color(0.42f, 0.33f, 0.24f), 0.15f),
            Ceiling = plan.CeilingMaterial != null ? plan.CeilingMaterial : TarkovGeneratedAssets.SurfaceMaterial("SDK_Building_Ceiling", new Color(0.85f, 0.84f, 0.8f), 0.04f),
            Roof = plan.RoofMaterial != null ? plan.RoofMaterial : TarkovGeneratedAssets.SurfaceMaterial("SDK_Building_Roof", new Color(0.28f, 0.27f, 0.27f), 0.2f),
            Trim = plan.TrimMaterial != null ? plan.TrimMaterial : TarkovGeneratedAssets.SurfaceMaterial("SDK_Building_Trim", new Color(0.9f, 0.9f, 0.88f), 0.03f),
        };

        // a storey's: the building's with its own outside
        private static Materials ForStorey(Materials m, Plan plan, int storey) => new Materials
        {
            Exterior = storey >= 0 && storey < plan.Storeys.Count && plan.Storeys[storey].Exterior != null ? plan.Storeys[storey].Exterior : m.Exterior,
            Interior = m.Interior, Floor = m.Floor, Ceiling = m.Ceiling, Roof = m.Roof, Trim = m.Trim,
        };

        // the plan's material options while it builds
        private static Dictionary<Material, Plan.MaterialOption> _options = new Dictionary<Material, Plan.MaterialOption>();

        private static void LoadOptions(Plan plan)
        {
            _options = new Dictionary<Material, Plan.MaterialOption>();
            foreach (var o in plan.MaterialOptions ?? new List<Plan.MaterialOption>())
                if (o != null && o.Material != null && !_options.ContainsKey(o.Material))
                    _options[o.Material] = o;
        }

        // a face's metre-per-repeat UVs laid out as the material's options say
        private static Vector2 Uv(Material material, Vector2 uv)
        {
            if (material == null || !_options.TryGetValue(material, out var o))
                return uv;
            if (Mathf.Abs(o.Rotation) > 0.01f)
                uv = (Vector2)(Quaternion.Euler(0f, 0f, o.Rotation) * uv);
            return new Vector2(uv.x / Mathf.Max(0.01f, o.Tile.x), uv.y / Mathf.Max(0.01f, o.Tile.y)) + o.Offset;
        }

        // what bullets hit where the material is used (its option's, else the given one)
        private static Plan.Surface Surf(Material material, Plan.Surface fallback) =>
            material != null && _options.TryGetValue(material, out var o) && o.OverrideSurface ? o.Surface : fallback;

        // the plan's wall paint, by (storey, x, z, along z, the + side)
        private static Dictionary<(int, int, int, bool, bool), Material> PaintMap(Plan plan)
        {
            var map = new Dictionary<(int, int, int, bool, bool), Material>();
            foreach (var p in plan.Paints ?? new List<Plan.WallPaint>())
                if (p != null && p.Material != null)
                    map[(p.Storey, p.X, p.Z, p.AlongZ, p.Plus)] = p.Material;
            return map;
        }

        private static Material Painted(Dictionary<(int, int, int, bool, bool), Material> paints, int s, int x, int z, bool alongZ, bool plus) =>
            paints.TryGetValue((s, x, z, alongZ, plus), out var m) ? m : null;

        // ------------------------------------------------------------ build

        // Builds what changed: each storey (and the roof) is its own group under Generated, rebuilt only when its hash
        // differs from the one it was built with (force: all of them).
        public static Result Build(Plan plan, bool force = false)
        {
            var result = new Result();
            var root = Building(plan);
            ForgetPrefabSizes();
            int strays = RemoveStrayDoorRoots();
            if (strays > 0)
                result.Notes.Add($"removed {strays} empty \"Door\" object(s) left in the scene by earlier rebuilds");
            LoadOptions(plan);
            var paints = PaintMap(plan);
            var building = Resolve(plan);
            foreach (var storey in plan.Storeys)
                plan.Fit(storey);

            var generated = root.Find(GeneratedName);
            // a building built before groups (everything straight under Generated): from scratch
            if (generated != null && (generated.Find("Visual") != null || generated.Find("Colliders") != null))
            {
                Object.DestroyImmediate(generated.gameObject);
                generated = null;
            }
            if (generated == null)
            {
                generated = new GameObject(GeneratedName).transform;
                generated.SetParent(root, false);
                force = true;
            }

            // every group's boxes (cheap), so faces are hidden across storeys as before
            var parts = new List<Part>();
            var shapes = new List<Shape>();
            var roofs = new List<(int Group, Vector2[] Poly, float Height)>();
            float cell = plan.CellSize;
            int storeys = plan.Storeys.Count;
            var corners = Enumerable.Range(0, storeys + 1).Select(s => Corners(plan, s, s < storeys ? result.Notes : null)).ToList();
            for (int s = 0; s < storeys; s++)
            {
                int first = parts.Count, firstShape = shapes.Count;
                var mats = ForStorey(building, plan, s);
                float floorTop = plan.Base(s);
                float next = plan.Base(s + 1);
                float wallTop = next - plan.SlabThickness;
                bool top = s == storeys - 1;
                // the cells bevels cut, in pieces (the rest are whole cells, merged into rectangles)
                var cut = Pieces(plan, corners[s]);

                // floors: the ground floor on the plinth, upper ones a slab under the storey, open over the stairs below
                // and where the storey below has no ceiling
                var holes = StairCells(plan, s - 1);
                holes.UnionWith(plan.OpenCeilings(s - 1));
                var open = plan.OpenCeilings(s);
                float floorBottom = s == 0 ? Mathf.Min(0f, plan.Plinth - plan.SlabThickness) : floorTop - plan.SlabThickness;
                Rects(plan.Width, plan.Depth, (x, z) =>
                {
                    int here = plan.Get(s, x, z);
                    if (here == 0 || holes.Contains(new Vector2Int(x, z)) || cut.ContainsKey(new Vector2Int(x, z)))
                        return 0;
                    int below = s == 0 ? 0 : plan.Get(s - 1, x, z);
                    return here * 1000 + below;
                }, (rect, key) =>
                {
                    int here = key / 1000, below = key % 1000;
                    var floor = RoomFloor(plan, here, mats);
                    // an upper floor's edges show only round stair holes, from inside
                    var p = Slab(rect, cell, floorBottom, floorTop, floor, s == 0 ? mats.Exterior : RoomCeiling(plan, below, mats), s == 0 ? mats.Exterior : mats.Interior, FloorSurfaceOf(plan, here, floor));
                    p.Outside[3] = s == 0;
                    parts.Add(p);
                });
                foreach (var kv in cut)
                {
                    if (holes.Contains(kv.Key))
                        continue;
                    int below = s == 0 ? 0 : plan.Get(s - 1, kv.Key.x, kv.Key.y);
                    foreach (var (room, poly) in kv.Value)
                    {
                        var floor = RoomFloor(plan, room, mats);
                        PieceSlab(shapes, parts, poly, floorBottom, floorTop, floor, s == 0 ? mats.Exterior : RoomCeiling(plan, below, mats), s == 0, FloorSurfaceOf(plan, room, floor));
                    }
                }

                // roofs over cells with nothing above (or the top storey), none where the ceiling is open; under a gable
                // the rain cover still goes there (the gable keeps the rain off)
                bool gable = top && plan.Roof == Plan.RoofKind.Gable;
                var roofTop = gable ? mats.Interior : mats.Roof;
                Rects(plan.Width, plan.Depth, (x, z) =>
                {
                    int here = plan.Get(s, x, z);
                    if (here == 0 || (!top && plan.Get(s + 1, x, z) != 0) || cut.ContainsKey(new Vector2Int(x, z)))
                        return 0;
                    return !open.Contains(new Vector2Int(x, z)) ? here : gable ? -here : 0;
                }, (rect, key) =>
                {
                    if (key > 0)
                    {
                        var p = Slab(rect, cell, wallTop, next, roofTop, RoomCeiling(plan, key, mats), mats.Exterior, Plan.Surface.Concrete);
                        p.Outside[2] = true;
                        parts.Add(p);
                    }
                    // the rain cover out to the walls' outer faces (and the eaves of a gable)
                    float grow = plan.WallThickness * 0.5f + (plan.Roof == Plan.RoofKind.Gable ? plan.Overhang : 0f);
                    float x0 = rect.x * cell - grow, x1 = (rect.x + rect.width) * cell + grow, z0 = rect.y * cell - grow, z1 = (rect.y + rect.height) * cell + grow;
                    roofs.Add((s, new[] { new Vector2(x0, z0), new Vector2(x1, z0), new Vector2(x1, z1), new Vector2(x0, z1) }, wallTop - Inset));
                });
                foreach (var kv in cut)
                {
                    bool opened = open.Contains(kv.Key);
                    if ((!top && plan.Get(s + 1, kv.Key.x, kv.Key.y) != 0) || (opened && !gable))
                        continue;
                    foreach (var (room, poly) in kv.Value)
                    {
                        if (!opened)
                            PieceSlab(shapes, parts, poly, wallTop, next, roofTop, RoomCeiling(plan, room, mats), false, Plan.Surface.Concrete);
                        roofs.Add((s, poly, wallTop - Inset));
                    }
                }

                Walls(plan, s, floorTop, wallTop, mats, corners[s], corners[s + 1], paints, parts, result);
                CornerWalls(plan, s, floorTop, wallTop, mats, corners[s], paints, shapes);
                if (top && plan.Roof == Plan.RoofKind.Flat && plan.Parapet > 0.05f)
                    Parapets(plan, s, next, mats, corners[s], parts, shapes);
                Stairs(plan, s, floorTop, next, mats, parts, result);
                TrimShapes(plan, s, corners[s], mats, shapes);
                for (int i = first; i < parts.Count; i++)
                    parts[i].Group = s;
                for (int i = firstShape; i < shapes.Count; i++)
                    shapes[i].Group = s;
            }

            // holes out of every group's boxes (the gable's slopes are added below, and aren't cut)
            var taken = CutHoles(plan, parts, shapes, result);

            var hashes = GroupHashes(plan);
            var folder = PartsFolder(plan);
            var rebuilt = new List<string>();
            for (int g = 0; g <= storeys; g++)
            {
                string name = GroupName(plan, g);
                var child = generated.Find(name);
                if (!force && child != null && g < plan.BuiltGroups.Count && plan.BuiltGroups[g] == hashes[g])
                    continue;
                if (child != null)
                    Object.DestroyImmediate(child.gameObject);
                child = Child(generated, name);
                rebuilt.Add(name);
                string prefix = g < storeys ? $"{folder}/S{g + 1}_" : $"{folder}/Roof_";
                if (g == storeys)
                {
                    // the roof: a gable's slopes and ends (a flat roof's slabs belong to the storeys)
                    if (storeys > 0 && plan.Roof == Plan.RoofKind.Gable)
                    {
                        var gable = new List<Part>();
                        Gable(plan, storeys - 1, plan.Base(storeys), ForStorey(building, plan, storeys - 1), gable, child, result);
                        foreach (var p in gable)
                            p.Group = g;
                        parts.AddRange(gable);
                    }
                }
                var own = parts.Where(p => p.Group == g).ToList();
                var ownShapes = shapes.Where(p => p.Group == g).ToList();
                result.Parts += own.Count(p => p.Poly == null);
                if (own.Count > 0 || ownShapes.Count > 0)
                {
                    BuildVisual(plan, own, ownShapes, parts, child, prefix, result);
                    BuildColliders(own, ownShapes, parts, child, prefix);
                }
                if (g < storeys)
                {
                    Fittings(plan, g, child, ForStorey(building, plan, g), corners[g], prefix, result);
                    PlaceProps(plan, g, child, root, result);
                    Debris(plan, g, taken, parts, child, prefix, result);
                    Volumes(plan, g, child, result);
                    var mine = roofs.Where(r => r.Group == g).ToList();
                    if (plan.RainCover && mine.Count > 0)
                        RainCover(mine.Select(r => r.Poly).ToList(), mine.Select(r => r.Height).ToList(), child, prefix);
                }
            }
            // storeys that are gone, and their meshes
            foreach (var child in generated.Cast<Transform>().ToList())
                if (!Enumerable.Range(0, storeys + 1).Any(g => child.name == GroupName(plan, g)))
                {
                    string gone = child.name;
                    Object.DestroyImmediate(child.gameObject);
                    if (gone.StartsWith("Storey_") && int.TryParse(gone.Substring(7), out int k) && AssetDatabase.IsValidFolder(folder))
                        foreach (var guid in AssetDatabase.FindAssets("t:Mesh", new[] { folder }))
                        {
                            var path = AssetDatabase.GUIDToAssetPath(guid);
                            if (Path.GetFileName(path).StartsWith($"S{k}_"))
                                AssetDatabase.DeleteAsset(path);
                        }
                }
            result.Rebuilt = rebuilt.Count == storeys + 1 ? "everything" : rebuilt.Count == 0 ? "nothing (no change)" : string.Join(", ", rebuilt);
            plan.BuiltGroups = hashes;
            plan.BuiltHash = Hash(plan);
            EditorUtility.SetDirty(plan);
            return result;
        }

        private static Material RoomFloor(Plan plan, int room, Materials m) => room > 0 && room <= plan.Rooms.Count && plan.Rooms[room - 1].Floor != null ? plan.Rooms[room - 1].Floor : m.Floor;
        private static Material RoomCeiling(Plan plan, int room, Materials m) => room > 0 && room <= plan.Rooms.Count && plan.Rooms[room - 1].Ceiling != null ? plan.Rooms[room - 1].Ceiling : m.Ceiling;
        private static Material RoomWalls(Plan plan, int room, Materials m) => room <= 0 ? m.Exterior : room <= plan.Rooms.Count && plan.Rooms[room - 1].Walls != null ? plan.Rooms[room - 1].Walls : m.Interior;

        private static Part Slab(RectInt rect, float cell, float y0, float y1, Material topFace, Material bottomFace, Material sides, Plan.Surface surface)
        {
            var p = new Part
            {
                Center = new Vector3((rect.x + rect.width * 0.5f) * cell, (y0 + y1) * 0.5f, (rect.y + rect.height * 0.5f) * cell),
                Size = new Vector3(rect.width * cell, Mathf.Max(0.02f, y1 - y0), rect.height * cell),
                Surface = surface,
            };
            for (int f = 0; f < 6; f++)
            {
                p.Faces[f] = f == 2 ? topFace : f == 3 ? bottomFace : sides;
                p.Outside[f] = f != 2 && f != 3;
            }
            return p;
        }

        // the cells a storey's stairs stand on
        private static HashSet<Vector2Int> StairCells(Plan plan, int storey)
        {
            var cells = new HashSet<Vector2Int>();
            foreach (var st in plan.Stairs.Where(st => st.Storey == storey))
                foreach (var c in StairFootprint(st))
                    cells.Add(c);
            return cells;
        }

        private static readonly Vector2Int[] Dirs = { new Vector2Int(0, 1), new Vector2Int(1, 0), new Vector2Int(0, -1), new Vector2Int(-1, 0) };

        public static IEnumerable<Vector2Int> StairFootprint(Plan.Stair st)
        {
            var along = Dirs[((st.Direction % 4) + 4) % 4];
            var across = new Vector2Int(along.y, -along.x);
            for (int i = 0; i < Mathf.Max(1, st.Length); i++)
                for (int w = 0; w < Mathf.Max(1, st.Width); w++)
                    yield return new Vector2Int(st.X, st.Z) + along * i + across * w;
        }

        // the grid covered by as few rectangles as greedy merging gives, per non-zero key
        private static void Rects(int w, int d, Func<int, int, int> key, Action<RectInt, int> emit)
        {
            var done = new bool[w, d];
            for (int z = 0; z < d; z++)
                for (int x = 0; x < w; x++)
                {
                    int k = key(x, z);
                    if (k == 0 || done[x, z])
                        continue;
                    int x1 = x + 1;
                    while (x1 < w && !done[x1, z] && key(x1, z) == k)
                        x1++;
                    int z1 = z + 1;
                    while (z1 < d && Enumerable.Range(x, x1 - x).All(xx => !done[xx, z1] && key(xx, z1) == k))
                        z1++;
                    for (int zz = z; zz < z1; zz++)
                        for (int xx = x; xx < x1; xx++)
                            done[xx, zz] = true;
                    emit(new RectInt(x, z, x1 - x, z1 - z), k);
                }
        }

        // ------------------------------------------------------------ walls

        internal struct EdgeWall
        {
            public int A, B; // rooms on the -side and the +side
            public bool Exists => A != B;
            public bool Exterior => A == 0 || B == 0;
        }

        // the edge along x at line z (cells (x, z-1) | (x, z)), or along z at line x (cells (x-1, z) | (x, z))
        private static EdgeWall Edge(Plan plan, int s, int x, int z, bool alongZ) =>
            alongZ ? new EdgeWall { A = plan.Get(s, x - 1, z), B = plan.Get(s, x, z) } : new EdgeWall { A = plan.Get(s, x, z - 1), B = plan.Get(s, x, z) };

        private static float Thickness(Plan plan, EdgeWall e) => e.Exists ? (e.Exterior ? plan.WallThickness : plan.InnerWallThickness) : 0f;

        // the wall's thickness on an edge (0: no wall there), for the window's furniture snapping
        internal static float WallOn(Plan plan, int s, int x, int z, bool alongZ) => Thickness(plan, Edge(plan, s, x, z, alongZ));

        private static void Walls(Plan plan, int s, float y0, float y1, Materials mats, List<Corner> corners, List<Corner> above,
                                  Dictionary<(int, int, int, bool, bool), Material> paints, List<Part> parts, Result result)
        {
            float cell = plan.CellSize;
            float next = plan.Base(s + 1);
            // the thickest wall of each direction at each grid vertex, for the corners
            var tx = new float[plan.Width + 1, plan.Depth + 1];
            var tz = new float[plan.Width + 1, plan.Depth + 1];
            for (int z = 0; z <= plan.Depth; z++)
                for (int x = 0; x < plan.Width; x++)
                {
                    float t = Thickness(plan, Edge(plan, s, x, z, false));
                    tx[x, z] = Mathf.Max(tx[x, z], t);
                    tx[x + 1, z] = Mathf.Max(tx[x + 1, z], t);
                }
            for (int x = 0; x <= plan.Width; x++)
                for (int z = 0; z < plan.Depth; z++)
                {
                    float t = Thickness(plan, Edge(plan, s, x, z, true));
                    tz[x, z] = Mathf.Max(tz[x, z], t);
                    tz[x, z + 1] = Mathf.Max(tz[x, z + 1], t);
                }

            foreach (bool alongZ in new[] { false, true })
            {
                int lines = alongZ ? plan.Width + 1 : plan.Depth + 1;
                int length = alongZ ? plan.Depth : plan.Width;
                for (int line = 0; line < lines; line++)
                {
                    int i = 0;
                    // the edge's paint, either side
                    Material Paint(int k, bool plus) => alongZ ? Painted(paints, s, line, k, true, plus) : Painted(paints, s, k, line, false, plus);
                    while (i < length)
                    {
                        var e = alongZ ? Edge(plan, s, line, i, true) : Edge(plan, s, i, line, false);
                        if (!e.Exists)
                        {
                            i++;
                            continue;
                        }
                        int j = i + 1;
                        while (j < length)
                        {
                            var n = alongZ ? Edge(plan, s, line, j, true) : Edge(plan, s, j, line, false);
                            if (n.A != e.A || n.B != e.B)
                                break;
                            j++;
                        }
                        float t = Thickness(plan, e);
                        float u0, u1;
                        if (alongZ)
                        {
                            // stops short of the walls along x at its ends
                            u0 = i * cell + tx[line, i] * 0.5f;
                            u1 = j * cell - tx[line, j] * 0.5f;
                        }
                        else
                        {
                            // runs on over the walls along z at its ends
                            u0 = i * cell - tz[i, line] * 0.5f;
                            u1 = j * cell + tz[j, line] * 0.5f;
                        }
                        // a bevel takes the wall over from its tangent point
                        ClipRun(corners, alongZ, line, i, j, cell, ref u0, ref u1);
                        if (u1 - u0 < 0.005f)
                        {
                            i = j;
                            continue;
                        }
                        // each edge's faces (paint, else the rooms'), and where they change along the run: the run's
                        // pieces are cut there after the openings, so paint never moves a hole
                        int first = i, last = j - 1;
                        Faces Look(float u)
                        {
                            int k = Mathf.Clamp(Mathf.FloorToInt(u / cell), first, last);
                            var plus = Paint(k, true) ?? RoomWalls(plan, e.B, mats);
                            var minus = Paint(k, false) ?? RoomWalls(plan, e.A, mats);
                            var outer = e.B == 0 ? plus : minus;
                            return new Faces { Plus = plus, Minus = minus, Outer = outer, Surface = e.Exterior ? OuterWallSurfaceOf(plan, outer, mats) : InnerWallSurfaceOf(plan, e.A, e.B, plus, minus, mats) };
                        }
                        var cuts = Enumerable.Range(i + 1, Mathf.Max(0, j - i - 1)).Where(k => Paint(k, true) != Paint(k - 1, true) || Paint(k, false) != Paint(k - 1, false)).Select(k => k * cell).ToList();
                        float lo = u0, hi = u1;
                        var openings = plan.Openings.Where(o => o.Storey == s && o.AlongZ == alongZ && (alongZ ? o.X : o.Z) == line)
                            .Select(o => (Opening: o, Centre: ((alongZ ? o.Z : o.X) + Mathf.Max(1, o.Span) * 0.5f) * cell + o.Offset))
                            .Where(o => o.Centre > lo && o.Centre < hi).OrderBy(o => o.Centre).ToList();
                        WallRun(plan, alongZ, line * cell, u0, u1, y0, y1, t, e, Look, cuts, openings, mats, parts);
                        // slabs stop at the edge, under the wall's middle: an outer wall carries on down through the slab
                        // (to the plinth's foot on the ground floor) so its outer half doesn't stand over a slit
                        float footing = s == 0 ? y0 - Mathf.Min(0f, plan.Plinth - plan.SlabThickness) : plan.SlabThickness;
                        if (e.Exterior && footing > 0.005f)
                            foreach (var (a, b) in Split(u0, u1, cuts))
                            {
                                var f = Look((a + b) * 0.5f);
                                // its underside shows from the storey below where the wall under it is thinner (or
                                // missing): that room's ceiling, the outside's when there's none
                                int k = Mathf.Clamp(Mathf.FloorToInt((a + b) * 0.5f / cell), i, j - 1);
                                var inside = e.A == 0 ? (alongZ ? new Vector2Int(line, k) : new Vector2Int(k, line)) : (alongZ ? new Vector2Int(line - 1, k) : new Vector2Int(k, line - 1));
                                int under = s == 0 ? 0 : plan.Get(s - 1, inside.x, inside.y);
                                var bottom = under != 0 ? RoomCeiling(plan, under, mats) : mats.Exterior;
                                Footing(alongZ, line * cell, a, b, y0 - footing, y0, t, e, f.Outer, e.B == 0 ? f.Minus : f.Plus, bottom, f.Surface, mats, parts);
                            }
                        // and up through the roof slab where nothing stands on it (the next storey's floor or footing
                        // covers it elsewhere); its outer half only, so its top doesn't fight the roof's. Walls along z
                        // run on over the corners too, else a corner's quarter is left open.
                        if (e.Exterior)
                        {
                            float c0 = alongZ ? i * cell - tx[line, i] * 0.5f : u0, c1 = alongZ ? j * cell + tx[line, j] * 0.5f : u1;
                            ClipRun(corners, alongZ, line, i, j, cell, ref c0, ref c1);
                            int k = i;
                            while (k < j)
                            {
                                if (!CapNeeded(plan, s, alongZ, line, k, e, above))
                                {
                                    k++;
                                    continue;
                                }
                                int k1 = k + 1;
                                while (k1 < j && CapNeeded(plan, s, alongZ, line, k1, e, above))
                                    k1++;
                                float a = k == i ? c0 : Mathf.Max(c0, k * cell), b = k1 == j ? c1 : Mathf.Min(c1, k1 * cell);
                                foreach (var (pa, pb) in Split(a, b, cuts))
                                {
                                    var f = Look((pa + pb) * 0.5f);
                                    if (pb - pa > 0.005f)
                                        Cap(alongZ, line * cell, pa, pb, y1, next, t, e, f.Outer, f.Surface, mats, parts);
                                }
                                k = k1;
                            }
                        }
                        i = j;
                    }
                }
            }
        }

        // whether the outer edge k on the line has nothing over its outer half: no room above outside it, and no
        // straight outer wall above (whose footing would cover it)
        private static bool CapNeeded(Plan plan, int s, bool alongZ, int line, int k, EdgeWall e, List<Corner> above)
        {
            var a = alongZ ? new Vector2Int(line - 1, k) : new Vector2Int(k, line - 1);
            var b = alongZ ? new Vector2Int(line, k) : new Vector2Int(k, line);
            var outside = e.A == 0 ? a : b;
            var inside = e.A == 0 ? b : a;
            if (plan.Get(s + 1, outside.x, outside.y) != 0)
                return false;
            return plan.Get(s + 1, inside.x, inside.y) == 0 || OnCorner(above, alongZ, line, (k + 0.5f) * plan.CellSize, plan.CellSize);
        }

        // a stretch of wall's face materials and what bullets hit there
        private struct Faces
        {
            public Material Plus, Minus, Outer;
            public Plan.Surface Surface;
        }

        // a..b cut where the run's paint changes
        private static IEnumerable<(float A, float B)> Split(float a, float b, List<float> cuts)
        {
            float from = a;
            foreach (var c in cuts)
                if (c > from + 0.005f && c < b - 0.005f)
                {
                    yield return (from, c);
                    from = c;
                }
            yield return (from, b);
        }

        // one run of wall, cut round its openings, then where its paint changes
        private static void WallRun(Plan plan, bool alongZ, float at, float u0, float u1, float y0, float y1, float t, EdgeWall e, Func<float, Faces> look, List<float> cuts,
                                    List<(Plan.Opening Opening, float Centre)> openings, Materials mats, List<Part> parts)
        {
            float height = y1 - y0;
            var pieces = new List<(float A, float B, float Y0, float Y1)>();
            float cursor = u0;
            foreach (var (o, c) in openings)
            {
                var (openWidth, openHeight, _) = Size(plan, o);
                float half = Mathf.Min(openWidth * 0.5f, (u1 - u0) * 0.5f - 0.05f);
                float a = Mathf.Max(u0 + 0.05f, c - half), b = Mathf.Min(u1 - 0.05f, c + half);
                if (b - a < 0.2f || a < cursor)
                    continue;
                float sill = o.Kind == Plan.OpeningKind.Window ? Mathf.Clamp(o.Sill, 0f, height - 0.3f) : 0f;
                float head = Mathf.Min(height - 0.05f, sill + openHeight);
                pieces.Add((cursor, a, 0f, height));
                if (sill > 0.01f)
                    pieces.Add((a, b, 0f, sill));
                if (head < height - 0.01f)
                    pieces.Add((a, b, head, height));
                cursor = b;
            }
            pieces.Add((cursor, u1, 0f, height));
            // the -side faces room A, the +side room B
            foreach (var (pa, pb, py0, py1) in pieces)
            foreach (var (a, b) in Split(pa, pb, cuts))
            {
                if (b - a < 0.005f || py1 - py0 < 0.005f)
                    continue;
                var faces = look((a + b) * 0.5f);
                var plus = faces.Plus;
                var minus = faces.Minus;
                var p = new Part
                {
                    Center = alongZ ? new Vector3(at, y0 + (py0 + py1) * 0.5f, (a + b) * 0.5f) : new Vector3((a + b) * 0.5f, y0 + (py0 + py1) * 0.5f, at),
                    Size = alongZ ? new Vector3(t, py1 - py0, b - a) : new Vector3(b - a, py1 - py0, t),
                    Surface = faces.Surface,
                };
                int plusFace = alongZ ? 0 : 4, minusFace = alongZ ? 1 : 5;
                for (int f = 0; f < 6; f++)
                    p.Faces[f] = e.Exterior ? mats.Exterior : mats.Interior;
                p.Faces[plusFace] = plus;
                p.Faces[minusFace] = minus;
                p.Outside[plusFace] = e.B == 0;
                p.Outside[minusFace] = e.A == 0;
                // jambs, sills and ends of outer walls show from outside too
                if (e.Exterior)
                    for (int f = 0; f < 6; f++)
                        if (f != plusFace && f != minusFace)
                            p.Outside[f] = true;
                parts.Add(p);
            }
        }

        // the outer wall through the slab under it: the outside face as the wall's, the inside face as the room's, the
        // underside as given (the ceiling of the room below where it shows), the ends outside
        private static void Footing(bool alongZ, float at, float u0, float u1, float y0, float y1, float t, EdgeWall e, Material outer, Material inner, Material bottom,
                                    Plan.Surface surface, Materials mats, List<Part> parts)
        {
            var p = new Part
            {
                Center = alongZ ? new Vector3(at, (y0 + y1) * 0.5f, (u0 + u1) * 0.5f) : new Vector3((u0 + u1) * 0.5f, (y0 + y1) * 0.5f, at),
                Size = alongZ ? new Vector3(t, y1 - y0, u1 - u0) : new Vector3(u1 - u0, y1 - y0, t),
                Surface = surface,
            };
            for (int f = 0; f < 6; f++)
            {
                p.Faces[f] = mats.Exterior;
                p.Outside[f] = true;
            }
            p.Faces[e.B == 0 ? (alongZ ? 0 : 4) : (alongZ ? 1 : 5)] = outer;
            p.Faces[e.B == 0 ? (alongZ ? 1 : 5) : (alongZ ? 0 : 4)] = inner;
            p.Faces[3] = bottom;
            p.Outside[3] = bottom == mats.Exterior;
            parts.Add(p);
        }

        // the outer half of an outer wall, from the top of the wall to the top of the slab over it
        private static void Cap(bool alongZ, float at, float u0, float u1, float y0, float y1, float t, EdgeWall e, Material outer, Plan.Surface surface, Materials mats, List<Part> parts)
        {
            float out1 = e.B == 0 ? 1f : -1f;
            var p = new Part
            {
                Center = alongZ ? new Vector3(at + out1 * t * 0.25f, (y0 + y1) * 0.5f, (u0 + u1) * 0.5f) : new Vector3((u0 + u1) * 0.5f, (y0 + y1) * 0.5f, at + out1 * t * 0.25f),
                Size = alongZ ? new Vector3(t * 0.5f, y1 - y0, u1 - u0) : new Vector3(u1 - u0, y1 - y0, t * 0.5f),
                Surface = surface,
            };
            for (int f = 0; f < 6; f++)
            {
                p.Faces[f] = mats.Exterior;
                p.Outside[f] = true;
            }
            p.Faces[e.B == 0 ? (alongZ ? 0 : 4) : (alongZ ? 1 : 5)] = outer;
            p.Faces[2] = mats.Trim;
            parts.Add(p);
        }

        // the bevels' walls: from the floor (an outer one from its footing) to under the slab, then its outer half
        // up through the roof where nothing stands on it, as the straight walls
        private static void CornerWalls(Plan plan, int s, float y0, float y1, Materials mats, List<Corner> corners, Dictionary<(int, int, int, bool, bool), Material> paints, List<Shape> shapes)
        {
            float next = plan.Base(s + 1);
            foreach (var c in corners)
            {
                float h = (c.Exterior ? plan.WallThickness : plan.InnerWallThickness) * 0.5f;
                // the paint of the corner's first edge (on the line x = V.x), the room's side and the other
                var first = c.Cell(0, 0);
                bool roomPlus = c.Dx > 0;
                var inner = Painted(paints, s, c.V.x, first.y, true, roomPlus) ?? RoomWalls(plan, c.Room, mats);
                var outer = Painted(paints, s, c.V.x, first.y, true, !roomPlus) ?? RoomWalls(plan, c.Outer, mats);
                var surface = c.Exterior ? OuterWallSurfaceOf(plan, outer, mats) : InnerWallSurfaceOf(plan, c.Outer, c.Room, inner, outer, mats);
                float bottom = !c.Exterior ? y0 : s == 0 ? Mathf.Min(0f, plan.Plinth - plan.SlabThickness) : y0 - plan.SlabThickness;
                ArcWall(shapes, c, bottom, y1, -h, h, inner, outer, null, false, c.Exterior, Collide.Both, surface);
                var outsideCell = new Vector2Int(c.Dx > 0 ? c.V.x - 1 : c.V.x, first.y);
                if (c.Exterior && plan.Get(s + 1, first.x, first.y) == 0 && plan.Get(s + 1, outsideCell.x, outsideCell.y) == 0)
                    ArcWall(shapes, c, y1, next, 0f, h, null, outer, mats.Trim, false, true, Collide.Both, surface);
            }
        }

        private static void Parapets(Plan plan, int s, float y0, Materials mats, List<Corner> corners, List<Part> parts, List<Shape> shapes)
        {
            float cell = plan.CellSize, t = plan.WallThickness;
            foreach (bool alongZ in new[] { false, true })
            {
                int lines = alongZ ? plan.Width + 1 : plan.Depth + 1, length = alongZ ? plan.Depth : plan.Width;
                for (int line = 0; line < lines; line++)
                    for (int i = 0; i < length; i++)
                    {
                        var e = alongZ ? Edge(plan, s, line, i, true) : Edge(plan, s, i, line, false);
                        if (!e.Exterior || !e.Exists)
                            continue;
                        float a = i * cell, b = (i + 1) * cell;
                        // a bevel's arc has its own; along x they run on over the corners, except where a bevel starts
                        bool clipA = false, clipB = false;
                        foreach (var c in corners)
                        {
                            if (!c.OnLine(alongZ, line))
                                continue;
                            var (from, to) = c.Span(alongZ, cell);
                            // over an end, or ending (starting) right at it: no running on there
                            if (from <= a + 1e-4f && to >= a - 1e-4f) { a = Mathf.Max(a, to); clipA = true; }
                            if (from <= b + 1e-4f && to >= b - 1e-4f) { b = Mathf.Min(b, from); clipB = true; }
                        }
                        if (!alongZ)
                        {
                            if (!clipA) a -= t * 0.5f;
                            if (!clipB) b += t * 0.5f;
                        }
                        if (b - a < 0.005f)
                            continue;
                        var p = new Part
                        {
                            Center = alongZ ? new Vector3(line * cell, y0 + plan.Parapet * 0.5f, (a + b) * 0.5f) : new Vector3((a + b) * 0.5f, y0 + plan.Parapet * 0.5f, line * cell),
                            Size = alongZ ? new Vector3(t, plan.Parapet, b - a) : new Vector3(b - a, plan.Parapet, t),
                            Surface = plan.ExteriorSurface,
                        };
                        for (int f = 0; f < 6; f++)
                        {
                            p.Faces[f] = mats.Exterior;
                            p.Outside[f] = true;
                        }
                        p.Faces[2] = mats.Trim;
                        parts.Add(p);
                    }
            }
            foreach (var c in corners)
                if (c.Exterior)
                    ArcWall(shapes, c, y0, y0 + plan.Parapet, -t * 0.5f, t * 0.5f, mats.Exterior, mats.Exterior, mats.Trim, true, true, Collide.Both, plan.ExteriorSurface);
        }

        // ------------------------------------------------------------ stairs

        private static void Stairs(Plan plan, int s, float y0, float y1, Materials mats, List<Part> parts, Result result)
        {
            float cell = plan.CellSize;
            foreach (var st in plan.Stairs.Where(st => st.Storey == s))
            {
                if (s + 1 >= plan.Storeys.Count)
                {
                    result.Notes.Add($"the stairs at {st.X},{st.Z} on storey {s + 1} lead nowhere (no storey above)");
                    continue;
                }
                int dir = ((st.Direction % 4) + 4) % 4;
                var along = new Vector3(Dirs[dir].x, 0f, Dirs[dir].y);
                var across = new Vector3(Dirs[dir].y, 0f, -Dirs[dir].x);
                int len = Mathf.Max(1, st.Length), wid = Mathf.Max(1, st.Width);
                // the flight's start edge, at the cell's corner, and its middle across
                var cellCentre = new Vector3((st.X + 0.5f) * cell, 0f, (st.Z + 0.5f) * cell);
                var start = cellCentre - along * (cell * 0.5f) + across * ((wid - 1) * cell * 0.5f);
                float run = len * cell, rise = y1 - y0, width = wid * cell - 0.02f;
                int steps = Mathf.Max(2, Mathf.RoundToInt(rise / 0.18f));
                float depth = run / steps;
                var rotation = Quaternion.LookRotation(along, Vector3.up);
                int room = plan.Get(s, st.X, st.Z);
                var tread = RoomFloor(plan, room, mats);
                // a stair model (placed with the fittings) takes the steps' place; they stay as its bullet collision
                // when it has none of its own
                var model = StairPrefabOf(plan, st);
                if (model != null && HasBallistics(model))
                    steps = 0;
                for (int i = 0; i < steps; i++)
                {
                    float h = rise * (i + 1) / steps;
                    var p = new Part
                    {
                        Center = start + along * (depth * (i + 0.5f)) + Vector3.up * (y0 + h * 0.5f),
                        Rotation = rotation,
                        Size = new Vector3(width, h, depth),
                        Surface = FloorSurfaceOf(plan, room, tread),
                        // bullets hit the steps; feet walk the ramp
                        Collide = Collide.High,
                        Solid = model == null,
                    };
                    for (int f = 0; f < 6; f++)
                        p.Faces[f] = model != null ? null : mats.Interior;
                    if (model == null)
                        p.Faces[2] = tread;
                    parts.Add(p);
                }
                float slope = Mathf.Atan2(rise, run);
                float rampLength = Mathf.Sqrt(run * run + rise * rise);
                var ramp = new Part
                {
                    Center = start + along * (run * 0.5f) + Vector3.up * (y0 + rise * 0.5f) - Quaternion.AngleAxis(-slope * Mathf.Rad2Deg, across) * Vector3.up * 0.1f,
                    Rotation = Quaternion.AngleAxis(-slope * Mathf.Rad2Deg, across) * rotation,
                    Size = new Vector3(width, 0.2f, rampLength),
                    Surface = FloorSurfaceOf(plan, room, tread),
                    Collide = Collide.Low,
                    Solid = false,
                };
                ramp.Faces[0] = null;
                parts.Add(ramp);
            }
        }

        // ------------------------------------------------------------ gable roof

        private static void Gable(Plan plan, int s, float y0, Materials mats, List<Part> parts, Transform generated, Result result)
        {
            float cell = plan.CellSize;
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = -1, maxZ = -1;
            for (int z = 0; z < plan.Depth; z++)
                for (int x = 0; x < plan.Width; x++)
                    if (plan.Get(s, x, z) != 0)
                    {
                        minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
                        minZ = Mathf.Min(minZ, z); maxZ = Mathf.Max(maxZ, z);
                    }
            if (maxX < 0)
                return;
            float x0 = minX * cell - plan.WallThickness * 0.5f, x1 = (maxX + 1) * cell + plan.WallThickness * 0.5f;
            float z0 = minZ * cell - plan.WallThickness * 0.5f, z1 = (maxZ + 1) * cell + plan.WallThickness * 0.5f;
            int filled = 0;
            for (int z = minZ; z <= maxZ; z++)
                for (int x = minX; x <= maxX; x++)
                    if (plan.Get(s, x, z) != 0)
                        filled++;
            if (filled < (maxX - minX + 1) * (maxZ - minZ + 1))
                result.Notes.Add("the gable roof covers the top storey's whole outline (it isn't a rectangle)");
            // the ridge runs along the longer side
            bool ridgeAlongX = x1 - x0 >= z1 - z0;
            float span = ridgeAlongX ? z1 - z0 : x1 - x0;
            float rise = span * 0.5f * Mathf.Tan(plan.RoofPitch * Mathf.Deg2Rad);
            float slopeLength = Mathf.Sqrt(span * span * 0.25f + rise * rise) + plan.Overhang / Mathf.Cos(plan.RoofPitch * Mathf.Deg2Rad);
            float lengthAlong = (ridgeAlongX ? x1 - x0 : z1 - z0) + plan.Overhang * 2f;
            var centre = new Vector3((x0 + x1) * 0.5f, y0, (z0 + z1) * 0.5f);
            const float thick = 0.15f;
            for (int side = -1; side <= 1; side += 2)
            {
                // each slope from the ridge down to past the eave
                var down = ridgeAlongX ? new Vector3(0f, 0f, side) : new Vector3(side, 0f, 0f);
                var axis = Vector3.Cross(Vector3.up, down);
                var tilt = Quaternion.AngleAxis(plan.RoofPitch, axis);
                var dirDown = tilt * down;
                var ridge = centre + Vector3.up * rise;
                var p = new Part
                {
                    Center = ridge + dirDown * (slopeLength * 0.5f) + tilt * Vector3.up * (thick * 0.5f),
                    Rotation = tilt * Quaternion.LookRotation(down, Vector3.up),
                    Size = new Vector3(lengthAlong, thick, slopeLength),
                    Surface = Plan.Surface.Wood,
                    Solid = false,
                };
                for (int f = 0; f < 6; f++)
                {
                    p.Faces[f] = mats.Trim;
                    p.Outside[f] = true;
                }
                p.Faces[2] = mats.Roof;
                p.Faces[3] = mats.Interior;
                parts.Add(p);
            }
            // the gable ends: triangles of wall on the short sides, as their own mesh and convex colliders
            var mesh = new Mesh { name = "GableEnds" };
            var v = new List<Vector3>();
            var tri = new List<int>();
            var faceV = new List<Vector3>();
            var faceN = new List<Vector3>();
            var faceU = new List<Vector2>();
            var faceT = new List<int>();
            foreach (float end in new[] { -1f, 1f })
            {
                float t = plan.WallThickness;
                Vector3 a, b, c;
                Vector3 n;
                if (ridgeAlongX)
                {
                    float x = end < 0 ? x0 + t * 0.5f : x1 - t * 0.5f;
                    a = new Vector3(x, y0, z0); b = new Vector3(x, y0, z1); c = new Vector3(x, y0 + rise, (z0 + z1) * 0.5f);
                    n = new Vector3(t * 0.5f, 0f, 0f);
                }
                else
                {
                    float z = end < 0 ? z0 + t * 0.5f : z1 - t * 0.5f;
                    a = new Vector3(x0, y0, z); b = new Vector3(x1, y0, z); c = new Vector3((x0 + x1) * 0.5f, y0 + rise, z);
                    n = new Vector3(0f, 0f, t * 0.5f);
                }
                Prism(v, tri, a, b, c, n);
                // the visible faces: both triangles and the two slopes, each flat, facing out of the prism
                var mid = (a + b + c) / 3f;
                Polygon(faceV, faceN, faceU, faceT, new[] { a - n, b - n, c - n }, -n);
                Polygon(faceV, faceN, faceU, faceT, new[] { a + n, b + n, c + n }, n);
                Polygon(faceV, faceN, faceU, faceT, new[] { a - n, c - n, c + n, a + n }, Out(c - a, n, (a + c) * 0.5f - mid));
                Polygon(faceV, faceN, faceU, faceT, new[] { b - n, c - n, c + n, b + n }, Out(c - b, n, (b + c) * 0.5f - mid));
            }
            mesh.SetVertices(faceV);
            mesh.SetNormals(faceN);
            mesh.SetUVs(0, faceU.Select(u => Uv(mats.Exterior, u)).ToList());
            mesh.SetTriangles(faceT, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            var ends = new GameObject("GableEnds");
            ends.transform.SetParent(generated, false);
            ends.AddComponent<MeshFilter>().sharedMesh = SaveMesh(PartsFolder(plan) + "/Roof_GableEnds.asset", mesh);
            ends.AddComponent<MeshRenderer>().sharedMaterial = mats.Exterior;
            Static(ends, navigation: false);
            // their collision: one convex prism per end, on both layers
            for (int k = 0; k < 2; k++)
                foreach (var layer in new[] { "LowPolyCollider", "HighPolyCollider" })
                {
                    var prism = new Mesh { name = $"GableEnd{k}" };
                    prism.SetVertices(v.Skip(k * 6).Take(6).ToList());
                    prism.SetTriangles(tri.Skip(k * 24).Take(24).Select(i => i - k * 6).ToList(), 0);
                    prism.RecalculateBounds();
                    var go = new GameObject($"GableEnd{k}_{(layer == "LowPolyCollider" ? "COLLIDER" : "BALLISTIC")}") { layer = LayerMask.NameToLayer(layer) };
                    go.transform.SetParent(ends.transform, false);
                    var mc = go.AddComponent<MeshCollider>();
                    mc.sharedMesh = SaveMesh(PartsFolder(plan) + $"/Roof_GableEnd{k}.asset", prism);
                    mc.convex = true;
                    if (layer == "HighPolyCollider")
                        Ballistic(go, plan.ExteriorSurface);
                    Static(go, navigation: false);
                }
        }

        // the normal of a slope along edge, across the wall's thickness n, on the side away from the triangle's middle
        private static Vector3 Out(Vector3 edge, Vector3 n, Vector3 fromMiddle)
        {
            var normal = Vector3.Cross(edge, n).normalized;
            return Vector3.Dot(normal, fromMiddle) < 0f ? -normal : normal;
        }

        // a flat convex polygon facing outward (a fan, wound so its front is that side: Unity's front normal is
        // Cross(b - a, c - a)), UVs projected along its main axis
        private static void Polygon(List<Vector3> v, List<Vector3> n, List<Vector2> u, List<int> t, Vector3[] poly, Vector3 outward)
        {
            if (Vector3.Dot(Vector3.Cross(poly[1] - poly[0], poly[2] - poly[0]), outward) < 0f)
                Array.Reverse(poly);
            int start = v.Count;
            var a = new Vector3(Mathf.Abs(outward.x), Mathf.Abs(outward.y), Mathf.Abs(outward.z));
            foreach (var p in poly)
            {
                v.Add(p);
                n.Add(outward.normalized);
                u.Add(a.x >= a.y && a.x >= a.z ? new Vector2(p.z, p.y) : a.y >= a.z ? new Vector2(p.x, p.z) : new Vector2(p.x, p.y));
            }
            for (int i = 1; i + 1 < poly.Length; i++)
            {
                t.Add(start);
                t.Add(start + i);
                t.Add(start + i + 1);
            }
        }

        // a triangle a, b, c pushed out by n both ways (the collider's points; a convex hull needs no winding)
        private static void Prism(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 n)
        {
            int i = v.Count;
            v.AddRange(new[] { a - n, b - n, c - n, a + n, b + n, c + n });
            void Tri(int p, int q, int r) { t.Add(i + p); t.Add(i + q); t.Add(i + r); }
            void Quad(int p, int q, int r, int s) { Tri(p, q, r); Tri(p, r, s); }
            // both caps and the two sloping sides and the bottom, wound both ways so it shows from either side
            Tri(0, 1, 2); Tri(3, 5, 4);
            Quad(0, 2, 5, 3); Quad(1, 4, 5, 2);
        }

        // ------------------------------------------------------------ meshes

        // the group's faces (hidden ones dropped against every group's boxes) and its shapes as LOD meshes saved at
        // prefix + LOD0/LOD1; its trims in meshes of their own (out of the navmesh), the outside ones in both LODs
        private static void BuildVisual(Plan plan, List<Part> parts, List<Shape> shapes, List<Part> everything, Transform generated, string prefix, Result result)
        {
            var solids = everything.Where(p => p.Solid).ToList();
            var lod0 = new Dictionary<Material, (List<Vector3> V, List<Vector3> N, List<Vector2> U, List<int> T)>();
            var lod1 = new Dictionary<Material, (List<Vector3> V, List<Vector3> N, List<Vector2> U, List<int> T)>();
            var corners = new Vector3[4];
            foreach (var p in parts)
            {
                for (int f = 0; f < 6; f++)
                {
                    var material = p.Faces[f];
                    if (material == null)
                        continue;
                    var n = p.Rotation * Normals[f];
                    FaceCorners(p, f, corners);
                    // dropped only when one other box covers the whole face: a face only partly covered (a floor under a
                    // wall, a footing under a doorway) stays
                    if (CullHidden && Covered(p, f, corners, solids))
                        continue;
                    Add(lod0, material, corners, n);
                    if (p.Outside[f])
                        Add(lod1, material, corners, n);
                }
            }
            var trims0 = new Dictionary<Material, (List<Vector3> V, List<Vector3> N, List<Vector2> U, List<int> T)>();
            var trims1 = new Dictionary<Material, (List<Vector3> V, List<Vector3> N, List<Vector2> U, List<int> T)>();
            foreach (var s in shapes)
            {
                if (s.Material == null || s.T.Count == 0)
                    continue;
                AddShape(s.Trim ? trims0 : lod0, s);
                if (s.Outside)
                    AddShape(s.Trim ? trims1 : lod1, s);
            }
            var visual = new GameObject("Visual");
            visual.transform.SetParent(generated, false);
            var r0 = lod0.Count > 0 ? MeshObject(visual.transform, "LOD0", lod0, $"{prefix}LOD0.asset", navigation: true) : null;
            var t0 = trims0.Count > 0 ? MeshObject(visual.transform, "Trims", trims0, $"{prefix}Trims.asset", navigation: false) : null;
            result.Triangles += lod0.Values.Sum(s => s.T.Count) / 3 + trims0.Values.Sum(s => s.T.Count) / 3;
            if (plan.Lods && (lod1.Count > 0 || trims1.Count > 0))
            {
                var r1 = lod1.Count > 0 ? MeshObject(visual.transform, "LOD1", lod1, $"{prefix}LOD1.asset", navigation: false) : null;
                var t1 = trims1.Count > 0 ? MeshObject(visual.transform, "TrimsOutside", trims1, $"{prefix}TrimsOutside.asset", navigation: false) : null;
                var group = visual.AddComponent<LODGroup>();
                group.SetLODs(new[]
                {
                    new LOD(0.15f, new Renderer[] { r0, t0 }.Where(r => r != null).ToArray()),
                    new LOD(0.01f, new Renderer[] { r1, t1 }.Where(r => r != null).ToArray()),
                });
                group.RecalculateBounds();
            }
        }

        private static void AddShape(Dictionary<Material, (List<Vector3> V, List<Vector3> N, List<Vector2> U, List<int> T)> lists, Shape shape)
        {
            if (!lists.TryGetValue(shape.Material, out var s))
                lists[shape.Material] = s = (new List<Vector3>(), new List<Vector3>(), new List<Vector2>(), new List<int>());
            int start = s.V.Count;
            s.V.AddRange(shape.V);
            s.N.AddRange(shape.N);
            s.U.AddRange(shape.U.Select(u => Uv(shape.Material, u)));
            s.T.AddRange(shape.T.Select(t => t + start));
        }

        private static float FaceOffset(Vector3 size, int f) => f < 2 ? size.x * 0.5f : f < 4 ? size.y * 0.5f : size.z * 0.5f;

        // the face's corners, wound clockwise seen from outside (Unity's front faces)
        private static void FaceCorners(Part p, int f, Vector3[] c)
        {
            var h = p.Size * 0.5f;
            Vector3 n = Normals[f], u, v;
            if (f < 2) { u = Vector3.forward; v = Vector3.up; }
            else if (f < 4) { u = Vector3.right; v = Vector3.forward; }
            else { u = Vector3.right; v = Vector3.up; }
            float hu = Mathf.Abs(Vector3.Dot(h, u)), hv = Mathf.Abs(Vector3.Dot(h, v)), hn = Mathf.Abs(Vector3.Dot(h, n));
            var centre = n * hn;
            c[0] = centre - u * hu - v * hv;
            c[1] = centre - u * hu + v * hv;
            c[2] = centre + u * hu + v * hv;
            c[3] = centre + u * hu - v * hv;
            // clockwise from outside: flip when u x v points along the normal
            if (Vector3.Dot(Vector3.Cross(u, v), n) > 0f)
                (c[1], c[3]) = (c[3], c[1]);
            for (int i = 0; i < 4; i++)
                c[i] = p.Center + p.Rotation * c[i];
        }

        private static void Add(Dictionary<Material, (List<Vector3> V, List<Vector3> N, List<Vector2> U, List<int> T)> lists, Material material, Vector3[] corners, Vector3 normal)
        {
            if (!lists.TryGetValue(material, out var s))
                lists[material] = s = (new List<Vector3>(), new List<Vector3>(), new List<Vector2>(), new List<int>());
            int i = s.V.Count;
            // a metre of texture per metre, projected along the face's main axis, so it runs on across pieces
            var a = new Vector3(Mathf.Abs(normal.x), Mathf.Abs(normal.y), Mathf.Abs(normal.z));
            foreach (var c in corners)
            {
                s.V.Add(c);
                s.N.Add(normal);
                s.U.Add(Uv(material, a.x >= a.y && a.x >= a.z ? new Vector2(c.z, c.y) : a.y >= a.z ? new Vector2(c.x, c.z) : new Vector2(c.x, c.y)));
            }
            s.T.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
        }

        private static MeshRenderer MeshObject(Transform parent, string name, Dictionary<Material, (List<Vector3> V, List<Vector3> N, List<Vector2> U, List<int> T)> lists, string path, bool navigation)
        {
            var mesh = new Mesh { name = name };
            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var u = new List<Vector2>();
            var materials = lists.Keys.ToList();
            foreach (var m in materials)
            {
                v.AddRange(lists[m].V);
                n.AddRange(lists[m].N);
                u.AddRange(lists[m].U);
            }
            mesh.indexFormat = v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(v);
            mesh.SetNormals(n);
            mesh.SetUVs(0, u);
            mesh.subMeshCount = materials.Count;
            int offset = 0;
            for (int i = 0; i < materials.Count; i++)
            {
                mesh.SetTriangles(lists[materials[i]].T.Select(t => t + offset).ToList(), i);
                offset += lists[materials[i]].V.Count;
            }
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = SaveMesh(path, mesh);
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = materials.ToArray();
            Static(go, navigation);
            return r;
        }

        private static Mesh SaveMesh(string path, Mesh mesh)
        {
            TarkovGeneratedAssets.EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            return TarkovGeneratedAssets.SaveMesh(path, mesh);
        }

        private static void Static(GameObject go, bool navigation)
        {
#pragma warning disable 618
            var flags = StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic;
            // NavigationStatic is deprecated but is still what the legacy scene bake collects
            if (navigation)
                flags |= StaticEditorFlags.NavigationStatic;
            GameObjectUtility.SetStaticEditorFlags(go, flags);
#pragma warning restore 618
        }

        // ------------------------------------------------------------ colliders

        // One mesh collider for movement (every box but the steps, the ramp instead, and the curved shapes) and one per
        // surface for bullets, each the boxes' faces that no other box on that layer covers: a few hundred boxes as
        // BoxColliders were as many colliders for the physics scene to keep.
        private static void BuildColliders(List<Part> parts, List<Shape> shapes, List<Part> everything, Transform generated, string prefix)
        {
            var root = new GameObject("Colliders").transform;
            root.SetParent(generated, false);
            var made = new HashSet<string>();
            // what covers a face on a layer: the boxes colliding on it, and cut cells' pieces (their hulls collide on both)
            List<Part> Covers(Collide layer) => everything.Where(q => q.Solid && (q.Poly != null || (q.Collide & layer) != 0)).ToList();
            void Collider(string name, string layer, Plan.Surface? surface, List<Part> boxes, List<Shape> curved, List<Part> covers)
            {
                var v = new List<Vector3>();
                var t = new List<int>();
                var corners = new Vector3[4];
                foreach (var p in boxes)
                    for (int f = 0; f < 6; f++)
                    {
                        FaceCorners(p, f, corners);
                        if (CullHidden && Covered(p, f, corners, covers))
                            continue;
                        int i = v.Count;
                        v.AddRange(corners);
                        t.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
                    }
                foreach (var s in curved.Where(s => s.T.Count > 0))
                {
                    t.AddRange(s.T.Select(i => i + v.Count));
                    v.AddRange(s.V);
                }
                if (t.Count == 0)
                    return;
                made.Add(name);
                var mesh = new Mesh { name = name, indexFormat = v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
                mesh.SetVertices(v);
                mesh.SetTriangles(t, 0);
                mesh.RecalculateBounds();
                var go = new GameObject(name) { layer = LayerMask.NameToLayer(layer) };
                go.transform.SetParent(root, false);
                go.AddComponent<MeshCollider>().sharedMesh = SaveMesh($"{prefix}{name}.asset", mesh);
                if (surface != null)
                    Ballistic(go, surface.Value);
                Static(go, navigation: false);
            }
            Collider("Building_COLLIDER", "LowPolyCollider", null, parts.Where(p => p.Poly == null && (p.Collide & Collide.Low) != 0).ToList(),
                     shapes.Where(s => (s.Collide & Collide.Low) != 0).ToList(), Covers(Collide.Low));
            var high = Covers(Collide.High);
            var surfaces = parts.Where(p => p.Poly == null && (p.Collide & Collide.High) != 0).Select(p => p.Surface)
                .Concat(shapes.Where(s => (s.Collide & Collide.High) != 0).Select(s => s.Surface)).Distinct().ToList();
            foreach (var surface in surfaces)
                Collider($"Building_BALLISTIC_{surface.ToString().ToLowerInvariant()}", "HighPolyCollider", surface,
                         parts.Where(p => p.Poly == null && (p.Collide & Collide.High) != 0 && p.Surface == surface).ToList(),
                         shapes.Where(s => (s.Collide & Collide.High) != 0 && s.Surface == surface).ToList(), high);
            // meshes of surfaces no longer used, and the curves' own colliders from before they were merged in
            foreach (var surface in (Plan.Surface[])Enum.GetValues(typeof(Plan.Surface)))
                foreach (var name in new[] { $"Building_BALLISTIC_{surface.ToString().ToLowerInvariant()}", $"Curved_BALLISTIC_{surface.ToString().ToLowerInvariant()}", "Curved_COLLIDER" })
                {
                    var path = $"{prefix}{name}.asset";
                    if (!made.Contains(name) && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path, AssetPathToGUIDOptions.OnlyExistingAssets)))
                        AssetDatabase.DeleteAsset(path);
                }
        }

        // a box's face lying inside one other box (its middle and its corners, a centimetre out), as the meshes drop them
        private static bool Covered(Part p, int f, Vector3[] corners, List<Part> by)
        {
            var n = p.Rotation * Normals[f];
            var centre = (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25f;
            foreach (var q in by)
            {
                if (q == p || !q.Contains(centre + n * 0.01f))
                    continue;
                bool all = true;
                for (int k = 0; k < 4 && all; k++)
                    all = q.Contains(Vector3.Lerp(corners[k], centre, 0.02f) + n * 0.01f);
                if (all)
                    return true;
            }
            return false;
        }

        private static void Ballistic(GameObject go, Plan.Surface surface)
        {
            var b = go.AddComponent<BallisticCollider>();
            switch (surface)
            {
                case Plan.Surface.Wood:
                    b._typeOfMaterial = MaterialType.WoodThick;
                    b.SurfaceSound = BaseBallistic.ESurfaceSound.WoodThick;
                    break;
                case Plan.Surface.Metal:
                    b._typeOfMaterial = MaterialType.MetalThick;
                    b.SurfaceSound = BaseBallistic.ESurfaceSound.Metal;
                    break;
                case Plan.Surface.Tile:
                    b._typeOfMaterial = MaterialType.Tile;
                    b.SurfaceSound = BaseBallistic.ESurfaceSound.Tile;
                    break;
                case Plan.Surface.Brick:
                    b._typeOfMaterial = MaterialType.Stone;
                    b.SurfaceSound = BaseBallistic.ESurfaceSound.Concrete;
                    break;
                default:
                    b._typeOfMaterial = MaterialType.Concrete;
                    b.SurfaceSound = BaseBallistic.ESurfaceSound.Concrete;
                    break;
            }
        }

        // ------------------------------------------------------------ doors, windows, lamps

        // an opening's place on its wall, in the building's space: the middle of its bottom on the wall's middle line,
        // the way out of room B (+side) into A (-side)... for outer walls, out = towards the outside. False where there's
        // no wall: the same room both sides, or a bevel's arc took it
        internal static bool Locate(Plan plan, Plan.Opening o, out Vector3 bottom, out Vector3 outward, out Vector3 along, out EdgeWall edge, out float wallHeight,
                                    List<Corner> corners = null)
        {
            float cell = plan.CellSize;
            int mid = Mathf.Max(1, o.Span) / 2;
            edge = o.AlongZ ? Edge(plan, o.Storey, o.X, o.Z + mid, true) : Edge(plan, o.Storey, o.X + mid, o.Z, false);
            float centre = ((o.AlongZ ? o.Z : o.X) + Mathf.Max(1, o.Span) * 0.5f) * cell + o.Offset;
            float sill = o.Kind == Plan.OpeningKind.Window ? o.Sill : 0f;
            float y = plan.Base(o.Storey) + sill;
            wallHeight = plan.Storeys[o.Storey].Height - plan.SlabThickness;
            bottom = o.AlongZ ? new Vector3(o.X * cell, y, centre) : new Vector3(centre, y, o.Z * cell);
            along = o.AlongZ ? Vector3.forward : Vector3.right;
            var plus = o.AlongZ ? Vector3.right : Vector3.forward;
            // out of the building, or from B into A between rooms
            outward = edge.B == 0 ? plus : -plus;
            return edge.Exists && !OnCorner(corners ?? Corners(plan, o.Storey), o.AlongZ, o.AlongZ ? o.X : o.Z, centre, cell);
        }

        private static void Fittings(Plan plan, int storey, Transform generated, Materials mats, List<Corner> corners, string prefix, Result result)
        {
            var root = Building(plan);
            Transform doors = null, windows = null;
            var frameParts = new List<Part>();
            foreach (var o in plan.Openings)
            {
                if (o.Storey != storey)
                    continue;
                if (!Locate(plan, o, out var bottom, out var outward, out var along, out var edge, out float wallHeight, corners))
                {
                    result.Notes.Add(edge.Exists
                        ? $"a {o.Kind.ToString().ToLowerInvariant()} on storey {o.Storey + 1} at {o.X},{o.Z} is on a bevel's curve (left out: put it on a straight wall)"
                        : $"a {o.Kind.ToString().ToLowerInvariant()} on storey {o.Storey + 1} at {o.X},{o.Z} has no wall (the same room both sides)");
                    continue;
                }
                float t = edge.Exterior ? plan.WallThickness : plan.InnerWallThickness;
                var (openWidth, openHeight, ownFrame) = Size(plan, o);
                float height = Mathf.Min(openHeight, wallHeight - (o.Kind == Plan.OpeningKind.Window ? o.Sill : 0f) - 0.05f);
                var prefab = PrefabOf(plan, o);
                // a prefab with its own frame (Tarkov's door and window units) gets none of the SDK's
                if (plan.Frames && o.Kind != Plan.OpeningKind.Gap && !ownFrame)
                    Frame(frameParts, bottom, along, outward, openWidth, height, t, o.Kind == Plan.OpeningKind.Window, mats.Trim);
                if (o.Kind == Plan.OpeningKind.Door)
                {
                    doors ??= Child(generated, "Doors");
                    GameObject door;
                    if (prefab != null)
                        door = PlacePrefab(prefab, doors, root, o, bottom, outward, along, o.ReverseSwing);
                    else
                    {
                        // the door's root (CreateDoor gives the leaf): the leaf alone left an empty root in the scene,
                        // and every leaf shared Doors as the parent that Door.KickOpen finds its BrokenDoor under
                        door = TarkovPrefabTools.CreateDoor(doors, openWidth - 0.06f, height - 0.03f, 0.05f, o.HingeRight, o.ReverseSwing).transform.parent.gameObject;
                        door.transform.localPosition = bottom;
                        door.transform.localRotation = Quaternion.LookRotation(outward, Vector3.up);
                    }
                    ApplyDoorSettings(door, o.Door);
                    result.Doors++;
                }
                else if (o.Kind == Plan.OpeningKind.Window && (prefab != null || plan.Glass))
                {
                    windows ??= Child(generated, "Windows");
                    GameObject window;
                    if (prefab != null)
                        window = PlacePrefab(prefab, windows, root, o, bottom, outward, along, false);
                    else
                    {
                        // the SDK's pane faces -Z with its pivot in the middle
                        window = TarkovWindows.CreateWindow(null, openWidth - 0.1f, height - 0.1f).gameObject;
                        window.transform.SetParent(windows, false);
                        window.transform.localPosition = bottom + Vector3.up * (height * 0.5f);
                        window.transform.localRotation = Quaternion.LookRotation(-outward, Vector3.up);
                    }
                    result.Windows++;
                }
            }

            Transform stairs = null;
            var ramps = new List<Vector3>();
            foreach (var st in plan.Stairs.Where(st => st.Storey == storey && storey + 1 < plan.Storeys.Count))
            {
                var model = StairPrefabOf(plan, st);
                if (model == null)
                    continue;
                stairs ??= Child(generated, "Stairs");
                PlaceStair(plan, st, model, stairs, root, plan.Base(storey), plan.Base(storey + 1));
                ramps.AddRange(RampQuad(plan, st, plan.Base(storey), plan.Base(storey + 1)));
            }
            if (ramps.Count > 0)
            {
                // the navmesh is baked from render meshes, and a model's steps may not be ones bots can climb: the
                // ramp's top as a renderer with no materials (it draws nothing) for the bake
                var mesh = new Mesh { name = "StairRamps" };
                mesh.SetVertices(ramps);
                mesh.SetTriangles(Enumerable.Range(0, ramps.Count / 4).SelectMany(q => new[] { q * 4, q * 4 + 1, q * 4 + 2, q * 4, q * 4 + 2, q * 4 + 3 }).ToList(), 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                var go = new GameObject("NavRamps");
                go.transform.SetParent(stairs, false);
                go.AddComponent<MeshFilter>().sharedMesh = SaveMesh($"{prefix}StairRamps.asset", mesh);
                go.AddComponent<MeshRenderer>().sharedMaterials = new Material[0];
                Static(go, navigation: true);
            }

            if (frameParts.Count > 0)
            {
                // all the frames as one mesh
                var lists = new Dictionary<Material, (List<Vector3> V, List<Vector3> N, List<Vector2> U, List<int> T)>();
                var quad = new Vector3[4];
                foreach (var p in frameParts)
                    for (int f = 0; f < 6; f++)
                    {
                        FaceCorners(p, f, quad);
                        Add(lists, p.Faces[f], quad, Normals[f]);
                    }
                MeshObject(generated, "Frames", lists, $"{prefix}Frames.asset", navigation: false);
            }

            // a lamp in the middle of each part of a room (its rectangles of 4 m² or more), under the ceiling: not under
            // the hole over this storey's stairs or an open ceiling (nothing to hang from), nor over the stairs below
            Transform lamps = null;
            for (int s = storey; s == storey; s++)
            {
                float ceiling = plan.Base(s + 1) - plan.SlabThickness;
                var none = StairCells(plan, s);
                none.UnionWith(StairCells(plan, s - 1));
                none.UnionWith(plan.OpenCeilings(s));
                var holes = (plan.Holes ?? new List<Plan.Hole>()).Where(h => h != null).Select(HoleBoxes).ToList();
                Rects(plan.Width, plan.Depth, (x, z) =>
                {
                    int room = plan.Get(s, x, z);
                    return room > 0 && room <= plan.Rooms.Count && plan.Rooms[room - 1].Lamp && !none.Contains(new Vector2Int(x, z)) ? room : 0;
                }, (rect, room) =>
                {
                    if (rect.width * rect.height * plan.CellSize * plan.CellSize < 4f)
                        return;
                    lamps ??= Child(generated, "Lamps");
                    var at = new Vector3((rect.x + rect.width * 0.5f) * plan.CellSize, ceiling - 0.05f, (rect.y + rect.height * 0.5f) * plan.CellSize);
                    // nor where a hole took the ceiling
                    if (InHole(holes, at + Vector3.up * 0.15f))
                        return;
                    GameObject lamp;
                    var own = plan.Rooms[room - 1].LampPrefab;
                    var prefab = own != null ? own : plan.LampPrefab;
                    if (prefab != null)
                        lamp = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.gameObject.scene);
                    else
                    {
                        var controller = TarkovLights.CreateLamp(null, LightType.Point);
                        lamp = controller.gameObject;
                        foreach (var light in lamp.GetComponentsInChildren<Light>(true))
                            light.range = Mathf.Max(plan.LampRange, new Vector2(rect.width, rect.height).magnitude * plan.CellSize * 0.75f);
                    }
                    lamp.transform.SetParent(lamps, false);
                    lamp.transform.localPosition = at;
                    if (own != null)
                        lamp.transform.localRotation = Quaternion.Euler(plan.Rooms[room - 1].LampRotation) * own.transform.localRotation;
                    ApplyLampSettings(lamp, plan.Rooms[room - 1], prefab != null);
                    result.Lamps++;
                });
            }
        }

        private static Transform Child(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        // ------------------------------------------------------------ door and window prefabs

        // A prefab measured as it's authored: its root's own rotation and scale kept (Tarkov's units often stand on a
        // root turned -90 about x, from their Z-up models), its pivot at the origin. A prefab cut out of such a unit (a
        // door leaf whose parent held the turn) still lies on its back: when its height isn't up (y its thinnest side),
        // it's stood up along the side that matches the height in its name (Tarkov's _210-100: 2.1 m tall, 1 m wide),
        // else z (Tarkov's models are Z-up). Then its thinner horizontal side is the way through the wall, and it has its own frame when
        // what doesn't move (outside the door leaf or the glass) is nearly as big as the whole. Hidden meshes (the
        // inactive hand helpers on Tarkov's handles) and far LODs don't count.
        private sealed class Unit
        {
            public Vector3 BottomCentre;
            public float Width, Height;
            // the way through the wall, in the measured space
            public Vector3 Through;
            public bool OwnFrame;
            // stands it up (identity when it already stands)
            public Quaternion Upright = Quaternion.identity;
        }

        private static readonly Dictionary<GameObject, Unit> Units = new Dictionary<GameObject, Unit>();

        internal static GameObject PrefabOf(Plan plan, Plan.Opening o) =>
            o.Kind == Plan.OpeningKind.Door ? (o.Prefab != null ? o.Prefab : plan.DefaultDoor) :
            o.Kind == Plan.OpeningKind.Window ? (o.Prefab != null ? o.Prefab : plan.DefaultWindow) : null;

        // the hole an opening needs: its prefab's frame when it has one, else the plan's size
        private static (float Width, float Height, bool OwnFrame) Size(Plan plan, Plan.Opening o)
        {
            var prefab = PrefabOf(plan, o);
            var unit = prefab != null && o.AutoFit ? Measure(prefab) : null;
            float scale = o.PrefabScale > 0f ? o.PrefabScale : 1f;
            return unit != null && unit.OwnFrame ? (unit.Width * scale, unit.Height * scale, true) : (Mathf.Max(0.3f, o.Width), Mathf.Max(0.3f, o.Height), false);
        }

        private static bool Shown(Transform t, Transform root)
        {
            for (var x = t; x != null; x = x.parent)
            {
                if (!x.gameObject.activeSelf)
                    return false;
                if (x == root)
                    break;
            }
            return true;
        }

        // the bounds of its meshes (all, and those that don't move) in the given space
        private static bool MeshBounds(GameObject prefab, Matrix4x4 space, out Bounds all, out Bounds still, out bool anyStill)
        {
            bool any = false;
            anyStill = false;
            all = default;
            still = default;
            foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
            {
                Mesh mesh = r is SkinnedMeshRenderer skinned ? skinned.sharedMesh : r.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
                if (mesh == null || !r.enabled || !Shown(r.transform, prefab.transform) || r.GetComponentInParent<LODGroup>(true) is var lods && lods != null && !InFirstLod(lods, r))
                    continue;
                var m = space * r.transform.localToWorldMatrix;
                var b = mesh.bounds;
                bool moves = r.GetComponentInParent<WorldInteractiveObject>(true) != null || r.GetComponentInParent<WindowBreaker>(true) != null;
                for (int i = 0; i < 8; i++)
                {
                    var p = m.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z));
                    if (!any) { all = new Bounds(p, Vector3.zero); any = true; }
                    else all.Encapsulate(p);
                    if (moves)
                        continue;
                    if (!anyStill) { still = new Bounds(p, Vector3.zero); anyStill = true; }
                    else still.Encapsulate(p);
                }
            }
            return any;
        }

        // Tarkov's unit names carry their size in centimetres, height first: _210-100
        private static float NamedHeight(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(name, @"_(\d{2,3})-(\d{2,3})(?!\d)");
            return m.Success ? int.Parse(m.Groups[1].Value) / 100f : 0f;
        }

        private static Unit Measure(GameObject prefab)
        {
            if (Units.TryGetValue(prefab, out var known))
                return known;
            var root = prefab.transform;
            var authored = Matrix4x4.TRS(Vector3.zero, root.rotation, root.lossyScale) * root.worldToLocalMatrix;
            Unit unit = null;
            if (MeshBounds(prefab, authored, out var all, out var still, out bool anyStill))
            {
                // lying down: stood up along the side that should be its height
                var upright = Quaternion.identity;
                var size = all.size;
                if (size.y < Mathf.Min(size.x, size.z))
                {
                    float named = NamedHeight(prefab.name);
                    var axes = new[] { (Axis: Vector3.right, Length: size.x), (Axis: Vector3.forward, Length: size.z) };
                    // no size in the name: z, the up of Tarkov's Z-up models (the longest side stood a wide window on its end)
                    var up = named > 0f ? axes.OrderBy(a => Mathf.Abs(a.Length - named)).First() : axes[1];
                    // models are made standing on their up axis's low end, so that end goes down
                    upright = Quaternion.FromToRotation(up.Axis, Vector3.up);
                    MeshBounds(prefab, Matrix4x4.Rotate(upright) * authored, out all, out still, out anyStill);
                }
                bool alongX = all.size.x >= all.size.z;
                unit = new Unit
                {
                    BottomCentre = new Vector3(all.center.x, all.min.y, all.center.z),
                    Width = alongX ? all.size.x : all.size.z,
                    Height = all.size.y,
                    Through = alongX ? Vector3.forward : Vector3.right,
                    OwnFrame = anyStill && still.size.y >= all.size.y * 0.6f && (alongX ? still.size.x : still.size.z) >= (alongX ? all.size.x : all.size.z) * 0.6f,
                    Upright = upright,
                };
            }
            Units[prefab] = unit;
            return unit;
        }

        private static bool InFirstLod(LODGroup group, Renderer r)
        {
            var lods = group.GetLODs();
            return lods.Length == 0 || lods[0].renderers.Contains(r) || !lods.Any(l => l.renderers.Contains(r));
        }

        // The prefab turned about the vertical so its through-the-wall side lies along the wall's normal (flip: the other
        // way round, e.g. a door that swings the other way), its bottom middle on the opening's; then the opening's own
        // adjustments: a turn and a scale about that point, an offset along the wall, up and out of it.
        private static GameObject PlacePrefab(GameObject prefab, Transform parent, Transform building, Plan.Opening o, Vector3 bottom, Vector3 outward, Vector3 along, bool flip)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, building.gameObject.scene);
            go.transform.SetParent(parent, false);
            var unit = o.AutoFit ? Measure(prefab) : null;
            var authored = prefab.transform.rotation;
            float scale = o.PrefabScale > 0f ? o.PrefabScale : 1f;
            Quaternion turn;
            Vector3 pivot;
            if (unit == null)
            {
                turn = Quaternion.LookRotation(outward, Vector3.up) * Quaternion.AngleAxis(flip ? 180f : 0f, Vector3.up);
                pivot = Vector3.zero;
            }
            else
            {
                turn = Quaternion.AngleAxis(Vector3.SignedAngle(unit.Through, outward, Vector3.up) + (flip ? 180f : 0f), Vector3.up);
                pivot = unit.BottomCentre;
            }
            var extra = Quaternion.AngleAxis(o.PrefabTurn, Vector3.up);
            var upright = unit != null ? unit.Upright : Quaternion.identity;
            go.transform.localRotation = extra * turn * upright * authored;
            go.transform.localScale = prefab.transform.localScale * scale;
            go.transform.localPosition = bottom + along * o.PrefabOffset.x + Vector3.up * o.PrefabOffset.y + outward * o.PrefabOffset.z - extra * turn * (pivot * scale);
            return go;
        }

        // jambs and a head round the opening, and a sill under a window, standing 2 cm proud of the wall each side
        private static void Frame(List<Part> parts, Vector3 bottom, Vector3 along, Vector3 outward, float width, float height, float wall, bool window, Material trim)
        {
            const float w = 0.07f;
            float depth = wall + 0.04f;
            Part Box(Vector3 centre, float a, float h, float d)
            {
                var p = new Part
                {
                    Center = centre,
                    Size = new Vector3(Mathf.Abs(along.x) * a + Mathf.Abs(outward.x) * d, h, Mathf.Abs(along.z) * a + Mathf.Abs(outward.z) * d),
                    Solid = false,
                    Collide = Collide.None,
                };
                for (int f = 0; f < 6; f++)
                {
                    p.Faces[f] = trim;
                    p.Outside[f] = true;
                }
                parts.Add(p);
                return p;
            }
            Box(bottom + along * (-width * 0.5f + w * 0.5f) + Vector3.up * (height * 0.5f), w, height, depth);
            Box(bottom + along * (width * 0.5f - w * 0.5f) + Vector3.up * (height * 0.5f), w, height, depth);
            Box(bottom + Vector3.up * (height - w * 0.5f), width, w, depth);
            if (window)
            {
                Box(bottom + Vector3.up * (w * 0.5f), width, w, depth);
                // the sill outside
                Box(bottom + outward * (wall * 0.5f + 0.04f) + Vector3.up * 0.02f, width + 0.1f, 0.04f, 0.1f);
            }
        }

        // ------------------------------------------------------------ volumes

        // the rooms' insides, each part of a room as a box 5 cm into its walls, floor and ceiling
        // yaw: the box turned about the vertical (degrees; the bevels' bands)
        private static List<(int Storey, int Room, Vector3 Centre, Vector3 Size, float Yaw)> RoomBoxes(Plan plan)
        {
            var boxes = new List<(int, int, Vector3, Vector3, float)>();
            float cell = plan.CellSize;
            for (int s = 0; s < plan.Storeys.Count; s++)
            {
                float y0 = plan.Base(s) - Inset, y1 = plan.Base(s + 1) - plan.SlabThickness + Inset;
                int storey = s;
                var corners = Corners(plan, s);
                // with bevels, rectangles on a quarter-cell grid round each bevel's square (its radius rounded up to the
                // grid), which is covered by its own boxes
                int fine = corners.Count > 0 ? 4 : 1;
                float g = cell / fine;
                var squares = corners.Select(c => (Corner: c, Size: Mathf.Ceil(c.Radius / g - 1e-3f) * g)).ToList();
                bool InSquare(float x, float z) => squares.Any(q =>
                    (x - q.Corner.V.x * cell) * q.Corner.Dx > 0f && (x - q.Corner.V.x * cell) * q.Corner.Dx < q.Size &&
                    (z - q.Corner.V.y * cell) * q.Corner.Dz > 0f && (z - q.Corner.V.y * cell) * q.Corner.Dz < q.Size);
                float Side(int x, int z, bool alongZ)
                {
                    var e = Edge(plan, storey, x, z, alongZ);
                    return e.Exists && !OnCorner(corners, alongZ, alongZ ? x : z, ((alongZ ? z : x) + 0.5f) * cell, cell) ? Thickness(plan, e) * 0.5f - Inset : 0f;
                }
                void Box(int room, float x0, float z0, float x1, float z1)
                {
                    if (x1 - x0 > 0.05f && z1 - z0 > 0.05f)
                        boxes.Add((storey, room, new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f), new Vector3(x1 - x0, y1 - y0, z1 - z0), 0f));
                }
                Rects(plan.Width * fine, plan.Depth * fine, (x, z) => InSquare((x + 0.5f) * g, (z + 0.5f) * g) ? 0 : plan.Get(storey, x / fine, z / fine), (rect, room) =>
                {
                    // each side pulled in to the wall's inner face (plus the inset into it) where it's on a wall, else
                    // left where it is (the room carries on in the next box)
                    float Pull(int at, int across, bool alongZ) => at % fine != 0 ? 0f : alongZ ? Side(at / fine, across / fine, true) : Side(across / fine, at / fine, false);
                    float west = Pull(rect.x, rect.y + rect.height / 2, true), east = Pull(rect.x + rect.width, rect.y + rect.height / 2, true);
                    float south = Pull(rect.y, rect.x + rect.width / 2, false), north = Pull(rect.y + rect.height, rect.x + rect.width / 2, false);
                    Box(room, rect.x * g + west, rect.y * g + south, (rect.x + rect.width) * g - east, (rect.y + rect.height) * g - north);
                });
                foreach (var (c, size) in squares)
                {
                    float pull = Mathf.Max(0f, (c.Exterior ? plan.WallThickness : plan.InnerWallThickness) * 0.5f - Inset);
                    float vx = c.V.x * cell, vz = c.V.y * cell, fx = vx + c.Dx * size, fz = vz + c.Dz * size;
                    var square = new List<Vector2> { new Vector2(Mathf.Min(vx, fx), Mathf.Min(vz, fz)), new Vector2(Mathf.Max(vx, fx), Mathf.Min(vz, fz)), new Vector2(Mathf.Max(vx, fx), Mathf.Max(vz, fz)), new Vector2(Mathf.Min(vx, fx), Mathf.Max(vz, fz)) };
                    // the room's part: inside the arc's wall, off the walls it carries on along, and off walls on the
                    // square's far sides when they're on grid lines
                    var region = square;
                    for (int k = 0; k + 1 < c.Arc.Count && region.Count >= 3; k++)
                        region = Clip(region, c.Arc[k] + c.Inward(k) * pull, c.Inward(k));
                    region = Clip(region, new Vector2(vx + c.Dx * pull, 0f), new Vector2(c.Dx, 0f));
                    region = Clip(region, new Vector2(0f, vz + c.Dz * pull), new Vector2(0f, c.Dz));
                    if (Mathf.Abs(size / cell - Mathf.Round(size / cell)) < 1e-3f)
                    {
                        int lx = c.V.x + c.Dx * Mathf.RoundToInt(size / cell), lz = c.V.y + c.Dz * Mathf.RoundToInt(size / cell);
                        float far = Side(lx, c.Cell(0, Mathf.RoundToInt(size / cell) / 2).y, true);
                        if (far > 0f)
                            region = Clip(region, new Vector2(fx - c.Dx * far, 0f), new Vector2(-c.Dx, 0f));
                        far = Side(c.Cell(Mathf.RoundToInt(size / cell) / 2, 0).x, lz, false);
                        if (far > 0f)
                            region = Clip(region, new Vector2(0f, fz - c.Dz * far), new Vector2(0f, -c.Dz));
                    }
                    // a band along the arc's wall face: boxes turned along runs of its chords, as many to a box as keep
                    // the arc within half the wall's thickness of the box's face (the box's ends reach into the wall)
                    float band = Mathf.Clamp((c.Radius - pull) * 0.3f, 0.3f, 0.8f);
                    var face = ArcFace(c, -pull);
                    float sag = (c.Exterior ? plan.WallThickness : plan.InnerWallThickness) * 0.5f;
                    for (int a = 0, b; a + 1 < face.Count; a = b)
                    {
                        b = a + 1;
                        while (b + 1 < face.Count && Bulge(face, a, b + 1, c.Centre) <= sag)
                            b++;
                        var u = (face[b] - face[a]).normalized;
                        float length = (face[b] - face[a]).magnitude, bulge = Bulge(face, a, b, c.Centre);
                        var inward = Left(u);
                        if (Vector2.Dot(c.Centre - face[a], inward) < 0f)
                            inward = -inward;
                        if (length > 0.02f)
                            boxes.Add((storey, c.Room, At(face[a] + u * (length * 0.5f) + inward * ((band - bulge) * 0.5f), (y0 + y1) * 0.5f),
                                new Vector3(band + bulge, y1 - y0, length), Mathf.Atan2(u.x, u.y) * Mathf.Rad2Deg));
                    }
                    // the rest square to the grid, in two halves split at the arc's middle (the half by B sliced along
                    // x, where the arc runs flat, the half by A along z); its curve ends inside the band
                    var core = region;
                    for (int k = 0; k + 1 < c.Arc.Count && core.Count >= 3; k++)
                        core = Clip(core, c.Arc[k] + c.Inward(k) * (pull + band - 0.05f), c.Inward(k));
                    float mx = c.Centre.x - c.Dx * c.Radius * 0.7071f;
                    foreach (var half in new[] { Clip(core, new Vector2(mx, 0f), new Vector2(c.Dx, 0f)), Clip(core, new Vector2(mx, 0f), new Vector2(-c.Dx, 0f)) })
                        foreach (var r in Cover(half, band - 0.1f))
                            Box(c.Room, r.xMin, r.yMin, r.xMax, r.yMax);
                    if (c.Exterior)
                        continue;
                    // the cut-off part, the room beside it's: a piece per chord, off the arc's wall
                    var rest = square;
                    for (int k = 0; k + 1 < c.Arc.Count && rest.Count >= 3; k++)
                    {
                        var n = c.Inward(k);
                        foreach (var r in Cover(Clip(rest, c.Arc[k] - n * pull, -n)))
                            Box(c.Outer, r.xMin, r.yMin, r.xMax, r.yMax);
                        rest = Clip(rest, c.Arc[k], n);
                    }
                }
            }
            return boxes;
        }

        private static void Volumes(Plan plan, int storey, Transform generated, Result result)
        {
            var root = Building(plan);
            var boxes = RoomBoxes(plan).Where(b => b.Storey == storey).ToList();
            result.Rooms += boxes.Count;
            if (plan.DarknessVolumes || plan.AmbientPortals)
            {
                var ambient = Child(generated, "Ambient");
                if (plan.DarknessVolumes)
                    foreach (var (_, _, centre, size, yaw) in boxes)
                        TarkovIndoorAmbient.CreateStencil(ambient, new TarkovIndoorAmbient.Box { Center = root.TransformPoint(centre), Rotation = root.rotation * Quaternion.Euler(0f, yaw, 0f), Size = Vector3.Scale(size, root.lossyScale) });
                if (plan.AmbientPortals)
                    foreach (var o in plan.Openings)
                    {
                        if (o.Storey != storey || !Locate(plan, o, out var bottom, out var outward, out var along, out var edge, out float wallHeight) || !edge.Exterior)
                            continue;
                        float t = plan.WallThickness;
                        float sill = o.Kind == Plan.OpeningKind.Window ? o.Sill : 0f;
                        var (openWidth, openHeight, _) = Size(plan, o);
                        float height = Mathf.Min(openHeight, wallHeight - sill - 0.05f);
                        var inner = bottom - outward * (t * 0.5f) + Vector3.up * (height * 0.5f);
                        // the room behind it: the box it opens into
                        var probe = inner - outward * 0.3f;
                        var box = boxes.Where(b => b.Storey == o.Storey && b.Yaw == 0f).OrderBy(b => (b.Centre - probe).sqrMagnitude).FirstOrDefault(b => Inside(b.Centre, b.Size, probe));
                        float depth = box.Size == Vector3.zero ? 4f : Mathf.Abs(Vector3.Dot(box.Size, outward));
                        float width = box.Size == Vector3.zero ? 4f : Mathf.Abs(Vector3.Dot(box.Size, along));
                        float roomHeight = wallHeight;
                        var opening = new TarkovIndoorAmbient.Opening
                        {
                            Center = root.TransformPoint(inner),
                            Inward = root.TransformDirection(-outward),
                            Width = openWidth,
                            Height = height,
                            Door = o.Kind != Plan.OpeningKind.Window,
                        };
                        TarkovIndoorAmbient.CreatePortal(ambient, opening, depth, width, roomHeight, sill + height * 0.5f);
                        result.Portals++;
                    }
            }
            if (plan.IndoorTriggers)
            {
                // inert here (the game only checks indoor triggers under the map's EnvironmentManager): Build Map copies them there
                var holder = Child(generated, IndoorName);
                holder.gameObject.tag = "EditorOnly";
                foreach (var (_, _, centre, size, yaw) in boxes)
                {
                    var go = new GameObject("IndoorTrigger");
                    go.transform.SetParent(holder, false);
                    go.transform.localPosition = centre;
                    go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
                    go.transform.localScale = size;
                    go.AddComponent<IndoorTrigger>();
                }
            }
        }

        private static bool Inside(Vector3 centre, Vector3 size, Vector3 p) =>
            Mathf.Abs(p.x - centre.x) <= size.x * 0.5f && Mathf.Abs(p.y - centre.y) <= size.y * 0.5f && Mathf.Abs(p.z - centre.z) <= size.z * 0.5f;

        // the roofs' outlines (convex, anticlockwise from above) as one flat mesh facing up
        private static void RainCover(List<Vector2[]> roofs, List<float> heights, Transform generated, string prefix)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            for (int i = 0; i < roofs.Count; i++)
            {
                var r = roofs[i];
                int k = v.Count;
                v.AddRange(r.Select(p => new Vector3(p.x, heights[i], p.y)));
                // a fan, facing up (clockwise from above)
                for (int j = 1; j + 1 < r.Length; j++)
                    t.AddRange(new[] { k, k + j + 1, k + j });
            }
            var mesh = new Mesh { name = "RainCover" };
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var go = new GameObject(TarkovRainCover.OwnName) { layer = LayerMask.NameToLayer("Triggers") };
            go.transform.SetParent(generated, false);
            go.AddComponent<DryPlane>().Mesh = SaveMesh($"{prefix}RainCover.asset", mesh);
        }

        // ------------------------------------------------------------ Build Map

        // With every map scene open, before Prepare: the indoor volumes of every building copied under the map's
        // EnvironmentManager (one group per building), remade on every build.
        internal static List<string> PrepareMap(List<UnityEngine.SceneManagement.Scene> scenes)
        {
            var messages = new List<string>();
            var holders = scenes.SelectMany(s => s.GetRootGameObjects()).SelectMany(r => r.GetComponentsInChildren<Transform>(true)).Where(t => t.name == IndoorName).ToList();
            var old = scenes.SelectMany(s => s.GetRootGameObjects()).SelectMany(r => r.GetComponentsInChildren<TriggerGroup>(true)).Where(g => g.name == MapIndoorName).ToList();
            foreach (var g in old)
                Object.DestroyImmediate(g.gameObject);
            if (holders.Count == 0 || scenes.Count == 0)
                return messages;
            var parent = TarkovMapScenes.IndoorTriggerGroup(scenes[0]);
            var all = new GameObject(MapIndoorName);
            all.transform.SetParent(parent.transform, false);
            all.AddComponent<TriggerGroup>();
            int count = 0;
            // a building's whole box first, so the game skips its triggers when the player isn't near
            foreach (var building in holders.GroupBy(BuildingOf))
            {
                var group = new GameObject(building.Key != null ? building.Key.name : "Building");
                group.transform.SetParent(all.transform, false);
                group.AddComponent<TriggerGroup>();
                foreach (var trigger in building.SelectMany(h => h.GetComponentsInChildren<IndoorTrigger>(true)))
                {
                    var copy = new GameObject("IndoorTrigger");
                    copy.transform.SetParent(group.transform, false);
                    copy.transform.SetPositionAndRotation(trigger.transform.position, trigger.transform.rotation);
                    copy.transform.localScale = trigger.transform.lossyScale;
                    copy.AddComponent<IndoorTrigger>();
                    count++;
                }
            }
            if (count > 0)
                Debug.Log($"[Tarkov SDK] Buildings: {count} indoor trigger(s) of {holders.Select(BuildingOf).Distinct().Count()} building(s) put under the EnvironmentManager");
            return messages;
        }

        // the building a generated object belongs to: the parent of its Generated
        private static Transform BuildingOf(Transform t)
        {
            for (var x = t; x != null; x = x.parent)
                if (x.name == GeneratedName)
                    return x.parent;
            return null;
        }

        public static bool IsBuildingIndoor(Transform t)
        {
            for (var x = t; x != null; x = x.parent)
                if (x.name == IndoorName)
                    return true;
            return false;
        }
    }
}
