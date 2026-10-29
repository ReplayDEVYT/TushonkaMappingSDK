using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EFT.Ballistics;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // Tarkov SDK > Tools > Geometry > City Builder...: a basic city from a kit (TarkovCityKit: ground materials and the
    // prefabs to place, Tarkov's own through the Asset Importer's Add To City Kit) and a layout (TarkovCity, on an
    // EditorOnly SDK_City under the "City" object). Generate lays a grid of blocks out from the seed (block sizes drawn
    // between the min and max and stretched to fill the city, every Nth road an avenue, some blocks merged over their
    // road, some parks), then builds under "City":
    //   Ground: Roads (one plane under everything), Sidewalks + Lots/Parks (each block a slab at kerb height), Curbs (the
    //           slabs' sides), Markings (centre lines, crossings): static meshes on the Terrain layer with MeshColliders
    //           and BallisticColliders (asphalt, concrete, the lot's by its material name), saved in <scene>_City
    //   Buildings: the kit's buildings side by side along each block's edges, fronts to the street, measured by their
    //           meshes' bounds (none overlap; the corners go to the first edge), or placeholder boxes without any
    //   Street Lamps / Trees / Street Props: on the sidewalks; Vehicles: parked at the kerbs, driving on the right;
    //           Trees / Park Props: scattered in parks
    // Generate replaces those groups (not undoable); objects moved out of them stay.
    public static class TarkovCityBuilder
    {
        public static readonly string[] Groups = { "Ground", "Buildings", "Street Lamps", "Street Props", "Vehicles", "Trees", "Park Props" };

        // the kit's slots the Asset Importer can fill: field name, what it's called
        public static readonly (string Field, string Label)[] MaterialSlots =
        {
            ("Road", "Roads"), ("Sidewalk", "Sidewalks"), ("Curb", "Kerbs"), ("Lot", "Block ground"), ("Park", "Park ground"),
            ("Markings", "Road markings"), ("PlaceholderBuilding", "Placeholder buildings"),
        };

        public static readonly (string Field, string Label)[] PieceSlots =
        {
            ("Buildings", "Buildings"), ("Lamps", "Street lamps"), ("StreetProps", "Street props"), ("Vehicles", "Parked cars"),
            ("Trees", "Trees"), ("ParkProps", "Park props"),
        };

        // ------------------------------------------------------------ layout

        public sealed class Block
        {
            public Rect Rect;
            public bool Park;
            // a street along the side: bottom (-z), right (+x), top (+z), left (-x)
            public readonly bool[] Street = new bool[4];
        }

        // a stretch of road between two junctions (or a junction and the city's edge)
        public sealed class Segment
        {
            public bool AlongZ;
            public float AcrossA, AcrossB, AlongA, AlongB;
            public bool Avenue;
            public bool JunctionA, JunctionB;
            public float Width => AcrossB - AcrossA;
        }

        public sealed class Layout
        {
            public Rect Area;
            public readonly List<Block> Blocks = new List<Block>();
            public readonly List<Segment> Segments = new List<Segment>();
        }

        // the blocks and roads along one axis, from -size/2 to size/2: (from, to, avenue)
        private static void Cuts(TarkovCity c, float size, System.Random random, List<(float A, float B)> blocks, List<(float A, float B, bool Avenue)> roads)
        {
            float min = Mathf.Min(c.BlockMin, c.BlockMax), max = Mathf.Max(c.BlockMin, c.BlockMax);
            float edge = c.EdgeRoads ? c.RoadWidth : 0f;
            bool IsAvenue(int k) => c.AvenueEvery > 0 && k % c.AvenueEvery == 0;
            var lengths = new List<float>();
            var widths = new List<(float W, bool Avenue)>();
            float used = 2f * edge;
            while (true)
            {
                var road = lengths.Count == 0 ? (0f, false) : IsAvenue(lengths.Count) ? (c.AvenueWidth, true) : (c.RoadWidth, false);
                if (lengths.Count > 0 && used + road.Item1 + min > size)
                    break;
                if (lengths.Count > 0)
                    widths.Add(road);
                float length = Mathf.Lerp(min, max, (float)random.NextDouble());
                lengths.Add(length);
                used += road.Item1 + length;
                if (used >= size)
                    break;
            }
            // the blocks stretched (or squeezed) to fill the size exactly
            float scale = Mathf.Max(5f, size - 2f * edge - widths.Sum(w => w.W)) / lengths.Sum();
            float x = -size / 2f;
            if (c.EdgeRoads)
            {
                roads.Add((x, x + edge, false));
                x += edge;
            }
            for (int i = 0; i < lengths.Count; i++)
            {
                blocks.Add((x, x + lengths[i] * scale));
                x += lengths[i] * scale;
                if (i < widths.Count)
                {
                    roads.Add((x, x + widths[i].W, widths[i].Avenue));
                    x += widths[i].W;
                }
            }
            if (c.EdgeRoads)
                roads.Add((x, x + edge, false));
        }

        public static Layout Plan(TarkovCity c)
        {
            var random = new System.Random(c.Seed);
            var layout = new Layout { Area = new Rect(-c.Size.x / 2f, -c.Size.y / 2f, c.Size.x, c.Size.y) };
            var xb = new List<(float A, float B)>();
            var zb = new List<(float A, float B)>();
            var xr = new List<(float A, float B, bool Avenue)>();
            var zr = new List<(float A, float B, bool Avenue)>();
            Cuts(c, Mathf.Max(20f, c.Size.x), random, xb, xr);
            Cuts(c, Mathf.Max(20f, c.Size.y), random, zb, zr);
            int nx = xb.Count, nz = zb.Count;
            int edge = c.EdgeRoads ? 1 : 0;

            // merges: a cell joins its right or upper neighbour over the road between them, each cell at most once
            var owner = new int[nx, nz];
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < nz; j++)
                    owner[i, j] = -1;
            var coveredX = new bool[nx, nz]; // the road between column i and i + 1 is covered at row j
            var coveredZ = new bool[nx, nz]; // the road between row j and j + 1 is covered at column i
            var cells = new List<(int I, int J)>();
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < nz; j++)
                    cells.Add((i, j));
            foreach (var (i, j) in cells.OrderBy(_ => random.Next()).ToList())
            {
                if (owner[i, j] >= 0)
                    continue;
                var block = new Block { Rect = Rect.MinMaxRect(xb[i].A, zb[j].A, xb[i].B, zb[j].B) };
                owner[i, j] = layout.Blocks.Count;
                if (random.NextDouble() < c.MergeChance)
                {
                    bool right = random.Next(2) == 0;
                    int ni = right ? i + 1 : i, nj = right ? j : j + 1;
                    if (ni < nx && nj < nz && owner[ni, nj] < 0)
                    {
                        owner[ni, nj] = layout.Blocks.Count;
                        block.Rect = Rect.MinMaxRect(xb[i].A, zb[j].A, xb[ni].B, zb[nj].B);
                        if (right)
                            coveredX[i, j] = true;
                        else
                            coveredZ[i, j] = true;
                    }
                }
                layout.Blocks.Add(block);
            }
            var area = layout.Area;
            foreach (var block in layout.Blocks)
            {
                block.Park = random.NextDouble() < c.ParkChance;
                var r = block.Rect;
                block.Street[0] = c.EdgeRoads || r.yMin > area.yMin + 0.01f;
                block.Street[1] = c.EdgeRoads || r.xMax < area.xMax - 0.01f;
                block.Street[2] = c.EdgeRoads || r.yMax < area.yMax - 0.01f;
                block.Street[3] = c.EdgeRoads || r.xMin > area.xMin + 0.01f;
            }

            // road stretches between junctions, for the markings
            for (int k = 0; k < xr.Count; k++)
                for (int j = 0; j < nz; j++)
                {
                    int column = k - edge; // the road is between column `column` and column + 1
                    if (column >= 0 && column + 1 < nx && coveredX[column, j])
                        continue;
                    layout.Segments.Add(new Segment
                    {
                        AlongZ = true, AcrossA = xr[k].A, AcrossB = xr[k].B, AlongA = zb[j].A, AlongB = zb[j].B, Avenue = xr[k].Avenue,
                        JunctionA = j > 0 || c.EdgeRoads, JunctionB = j < nz - 1 || c.EdgeRoads,
                    });
                }
            for (int k = 0; k < zr.Count; k++)
                for (int i = 0; i < nx; i++)
                {
                    int row = k - edge;
                    if (row >= 0 && row + 1 < nz && coveredZ[i, row])
                        continue;
                    layout.Segments.Add(new Segment
                    {
                        AlongZ = false, AcrossA = zr[k].A, AcrossB = zr[k].B, AlongA = xb[i].A, AlongB = xb[i].B, Avenue = zr[k].Avenue,
                        JunctionA = i > 0 || c.EdgeRoads, JunctionB = i < nx - 1 || c.EdgeRoads,
                    });
                }
            return layout;
        }

        // ------------------------------------------------------------ generate

        private struct Side
        {
            public Vector2 Origin, Along, Out;
            public float Length, Depth;
        }

        // a rectangle's four sides, each walked from one corner with the rectangle on its left: bottom, right, top, left
        private static Side[] Sides(Rect r) => new[]
        {
            new Side { Origin = new Vector2(r.xMin, r.yMin), Along = Vector2.right, Out = Vector2.down, Length = r.width, Depth = r.height },
            new Side { Origin = new Vector2(r.xMax, r.yMin), Along = Vector2.up, Out = Vector2.right, Length = r.height, Depth = r.width },
            new Side { Origin = new Vector2(r.xMax, r.yMax), Along = Vector2.left, Out = Vector2.up, Length = r.width, Depth = r.height },
            new Side { Origin = new Vector2(r.xMin, r.yMax), Along = Vector2.down, Out = Vector2.left, Length = r.height, Depth = r.width },
        };

        private static Rect Inset(Rect r, float d)
        {
            d = Mathf.Min(d, r.width / 2f - 0.5f, r.height / 2f - 0.5f);
            return Rect.MinMaxRect(r.xMin + d, r.yMin + d, r.xMax - d, r.yMax - d);
        }

        private sealed class Context
        {
            public TarkovCity City;
            public TarkovCityKit Kit;
            public System.Random Random;
            public Transform Root;
            public readonly Dictionary<string, Transform> Groups = new Dictionary<string, Transform>();
            public int Placed;
            public readonly List<string> Notes = new List<string>();
        }

        // builds the city; null, or why it couldn't
        public static string Generate(TarkovCity city, out string summary)
        {
            summary = null;
            var root = city.transform.parent;
            if (root == null)
                return "The SDK_City object needs a parent (the City object the city is built under).";
            if (string.IsNullOrEmpty(root.gameObject.scene.path))
                return "Save the scene first: the city's ground meshes are saved next to it.";
            if (string.IsNullOrEmpty(city.Id))
            {
                city.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
                EditorUtility.SetDirty(city);
            }
            var cx = new Context { City = city, Kit = city.Kit, Root = root, Random = new System.Random(city.Seed * 7919 + 17) };
            var layout = Plan(city);
            try
            {
                EditorUtility.DisplayProgressBar("City Builder", "Clearing", 0f);
                Clear(root);
                foreach (var name in Groups)
                {
                    var go = new GameObject(name);
                    go.transform.SetParent(root, false);
                    cx.Groups[name] = go.transform;
                }
                EditorUtility.DisplayProgressBar("City Builder", "Ground", 0.05f);
                BuildGround(cx, layout);
                for (int i = 0; i < layout.Blocks.Count; i++)
                {
                    EditorUtility.DisplayProgressBar("City Builder", $"Block {i + 1} of {layout.Blocks.Count}", 0.1f + 0.9f * i / layout.Blocks.Count);
                    var block = layout.Blocks[i];
                    if (block.Park)
                        FillPark(cx, block);
                    else
                        FillBuildings(cx, block);
                    FillSidewalks(cx, block);
                    ParkCars(cx, block, layout);
                }
                // groups nothing went into go again
                foreach (var group in cx.Groups.Values)
                    if (group.childCount == 0)
                        Object.DestroyImmediate(group.gameObject);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
            }
            EditorUtility.SetDirty(root.gameObject);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
            summary = $"{layout.Blocks.Count} blocks ({layout.Blocks.Count(b => b.Park)} parks), {cx.Placed} objects placed." +
                      (cx.Notes.Count > 0 ? " " + string.Join(" ", cx.Notes.Distinct()) : "") +
                      " Bake the navmesh again (Generation > Essential > Bake NavMesh).";
            Debug.Log("[Tarkov SDK] City Builder: " + summary);
            return null;
        }

        public static void Clear(Transform root)
        {
            foreach (var name in Groups)
                for (var child = root.Find(name); child != null; child = root.Find(name))
                    Object.DestroyImmediate(child.gameObject);
        }

        // ------------------------------------------------------------ ground

        private static void BuildGround(Context cx, Layout layout)
        {
            var c = cx.City;
            float tex = cx.Kit != null ? Mathf.Max(0.25f, cx.Kit.TextureMetres) : 4f;
            float h = c.CurbHeight;
            var road = new TarkovGeneratedAssets.MeshBuilder();
            var sidewalks = new TarkovGeneratedAssets.MeshBuilder();
            var curbs = new TarkovGeneratedAssets.MeshBuilder();
            var lots = new TarkovGeneratedAssets.MeshBuilder();
            var parks = new TarkovGeneratedAssets.MeshBuilder();
            var markings = new TarkovGeneratedAssets.MeshBuilder();

            // the road is one plane under everything; the blocks stand on it
            Top(road, layout.Area, 0f, tex);
            float hw = TarkovCityRoads.WalkHeight(h);
            foreach (var block in layout.Blocks)
            {
                var r = block.Rect;
                // kerbstones round the block (mitred at its corners), the sidewalk inside them, the lot inside that
                float kw = Mathf.Min(c.CurbWidth, Mathf.Min(r.width, r.height) / 4f);
                var walkRect = Inset(r, kw);
                var inner = Inset(r, Mathf.Max(c.SidewalkWidth, kw));
                if (c.SidewalkWidth > kw)
                {
                    Top(sidewalks, Rect.MinMaxRect(walkRect.xMin, walkRect.yMin, walkRect.xMax, inner.yMin), hw, tex);
                    Top(sidewalks, Rect.MinMaxRect(walkRect.xMin, inner.yMax, walkRect.xMax, walkRect.yMax), hw, tex);
                    Top(sidewalks, Rect.MinMaxRect(walkRect.xMin, inner.yMin, inner.xMin, inner.yMax), hw, tex);
                    Top(sidewalks, Rect.MinMaxRect(inner.xMax, inner.yMin, walkRect.xMax, inner.yMax), hw, tex);
                }
                Top(block.Park ? parks : lots, inner, hw, tex);
                if (h > 0f)
                {
                    var profile = TarkovCityRoads.KerbProfile(h, kw);
                    foreach (var side in Sides(r))
                    {
                        var a = new Vector3(side.Origin.x, 0f, side.Origin.y);
                        var b = a + new Vector3(side.Along.x, 0f, side.Along.y) * side.Length;
                        var inward = -new Vector3(side.Out.x, 0f, side.Out.y);
                        TarkovCityRoads.KerbSection(curbs, a, b, inward, inward, Vector3.up, profile, -1f, -1f, 0f, side.Length / tex, tex);
                    }
                }
            }
            if (c.LaneLines || c.Crossings)
                foreach (var segment in layout.Segments)
                    Mark(markings, segment, c);

            var ground = cx.Groups["Ground"];
            var kit = cx.Kit;
            var lotMaterial = KitMaterial(kit, "Lot");
            var parkMaterial = KitMaterial(kit, "Park");
            var (lotType, lotSound) = TarkovModelConverter.Surface(lotMaterial != null ? lotMaterial.name : "");
            var (parkType, parkSound) = TarkovModelConverter.Surface(parkMaterial != null ? parkMaterial.name : "grass");
            if (parkType == MaterialType.Concrete)
                (parkType, parkSound) = (MaterialType.GrassLow, BaseBallistic.ESurfaceSound.Grass);
            GroundPart(cx, ground, "Roads", road, KitMaterial(kit, "Road"), (MaterialType.Asphalt, BaseBallistic.ESurfaceSound.Asphalt));
            GroundPart(cx, ground, "Sidewalks", sidewalks, KitMaterial(kit, "Sidewalk"), (MaterialType.Concrete, BaseBallistic.ESurfaceSound.Concrete));
            GroundPart(cx, ground, "Curbs", curbs, KitMaterial(kit, "Curb"), (MaterialType.Concrete, BaseBallistic.ESurfaceSound.Concrete));
            GroundPart(cx, ground, "Lots", lots, lotMaterial, (lotType, lotSound));
            GroundPart(cx, ground, "Parks", parks, parkMaterial, (parkType, parkSound));
            GroundPart(cx, ground, "Markings", markings, KitMaterial(kit, "Markings"), null);
        }

        // an upward quad over the rectangle (local x, z), textured by world metres
        private static void Top(TarkovGeneratedAssets.MeshBuilder b, Rect r, float y, float tex)
        {
            if (r.width <= 0.001f || r.height <= 0.001f)
                return;
            int V(float x, float z) => b.Add(new Vector3(x, y, z), Vector3.up, new Vector2(x / tex, z / tex));
            int a = V(r.xMin, r.yMin), c = V(r.xMin, r.yMax), d = V(r.xMax, r.yMax), e = V(r.xMax, r.yMin);
            b.Triangle(a, c, d);
            b.Triangle(a, d, e);
        }

        // a road stretch's centre line (dashed, or doubled on avenues) and zebra crossings at its junctions
        private static void Mark(TarkovGeneratedAssets.MeshBuilder b, Segment s, TarkovCity c)
        {
            const float y = 0.01f, line = 0.15f, zebraFrom = 1f, zebraLength = 3f;
            Rect R(float alongA, float alongB, float acrossA, float acrossB) => s.AlongZ
                ? Rect.MinMaxRect(acrossA, alongA, acrossB, alongB)
                : Rect.MinMaxRect(alongA, acrossA, alongB, acrossB);
            float mid = (s.AcrossA + s.AcrossB) / 2f;
            float start = s.AlongA + (s.JunctionA && c.Crossings ? zebraFrom + zebraLength + 1f : 1f);
            float end = s.AlongB - (s.JunctionB && c.Crossings ? zebraFrom + zebraLength + 1f : 1f);
            if (c.LaneLines && end > start)
            {
                if (s.Avenue)
                {
                    Top(b, R(start, end, mid - 0.1f - line, mid - 0.1f), y, 1f);
                    Top(b, R(start, end, mid + 0.1f, mid + 0.1f + line), y, 1f);
                }
                else
                {
                    for (float d = start; d < end; d += 8f)
                        Top(b, R(d, Mathf.Min(d + 3f, end), mid - line / 2f, mid + line / 2f), y, 1f);
                }
            }
            if (!c.Crossings || s.AlongB - s.AlongA < 2f * (zebraFrom + zebraLength) + 2f)
                return;
            for (int end2 = 0; end2 < 2; end2++)
            {
                if (end2 == 0 ? !s.JunctionA : !s.JunctionB)
                    continue;
                float a = end2 == 0 ? s.AlongA + zebraFrom : s.AlongB - zebraFrom - zebraLength;
                for (float x = s.AcrossA + 0.5f; x + 0.5f <= s.AcrossB - 0.4f; x += 1f)
                    Top(b, R(a, a + zebraLength, x, x + 0.5f), y, 1f);
            }
        }

        private static void GroundPart(Context cx, Transform parent, string name, TarkovGeneratedAssets.MeshBuilder b, Material material,
            (MaterialType Type, BaseBallistic.ESurfaceSound Sound)? surface)
        {
            string folder = TarkovCityRoads.MeshFolder(cx.Root.gameObject.scene);
            TarkovCityRoads.Part(parent, name, b, material, surface, $"{folder}/City_{cx.City.Id}_{name}.asset");
        }

        // the kit's material in the slot, else the SDK's plain one (explicit checks: a deleted material is Unity's fake null)
        public static Material KitMaterial(TarkovCityKit kit, string slot)
        {
            var material = kit != null ? typeof(TarkovCityKit).GetField(slot).GetValue(kit) as Material : null;
            return material != null ? material : DefaultMaterial(slot);
        }

        public static Material DefaultMaterial(string slot)
        {
            switch (slot)
            {
                case "Road": return TarkovGeneratedAssets.SurfaceMaterial("SDK_CityAsphalt", new Color(0.16f, 0.16f, 0.17f, 0.15f), 0.05f);
                case "Sidewalk": return TarkovGeneratedAssets.SurfaceMaterial("SDK_CitySidewalk", new Color(0.46f, 0.45f, 0.43f, 0.1f), 0.06f);
                case "Curb": return TarkovGeneratedAssets.SurfaceMaterial("SDK_CityCurb", new Color(0.58f, 0.57f, 0.55f, 0.1f), 0.04f);
                case "Lot": return TarkovGeneratedAssets.SurfaceMaterial("SDK_CityLotGravel", new Color(0.36f, 0.34f, 0.31f, 0.05f), 0.1f);
                case "Park": return TarkovGeneratedAssets.SurfaceMaterial("SDK_CityParkGrass", new Color(0.2f, 0.28f, 0.13f, 0.03f), 0.1f);
                case "Markings": return TarkovGeneratedAssets.SurfaceMaterial("SDK_CityMarkings", new Color(0.8f, 0.8f, 0.76f, 0.2f), 0.03f);
                case "PlaceholderBuilding": return TarkovGeneratedAssets.SurfaceMaterial("SDK_CityBuilding", new Color(0.52f, 0.5f, 0.47f, 0.08f), 0.05f);
            }
            return null;
        }

        // ------------------------------------------------------------ placing

        private static readonly Dictionary<int, Bounds> BoundsCache = new Dictionary<int, Bounds>();

        static TarkovCityBuilder() => EditorApplication.projectChanged += BoundsCache.Clear;

        // the prefab's meshes' bounds in its root's space (EditorOnly parts left out)
        public static Bounds LocalBounds(GameObject prefab)
        {
            if (BoundsCache.TryGetValue(prefab.GetInstanceID(), out var cached))
                return cached;
            var root = prefab.transform;
            var toRoot = root.worldToLocalMatrix;
            Bounds? total = null;
            void Add(Transform t, Mesh mesh)
            {
                if (mesh == null)
                    return;
                for (var p = t; p != null && p != root.parent; p = p.parent)
                    if (p.CompareTag("EditorOnly"))
                        return;
                var b = TarkovGameAssets.TransformBounds(mesh.bounds, toRoot * t.localToWorldMatrix);
                if (total == null)
                    total = b;
                else
                {
                    var u = total.Value;
                    u.Encapsulate(b);
                    total = u;
                }
            }
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
                if (filter.GetComponent<Renderer>() != null)
                    Add(filter.transform, filter.sharedMesh);
            foreach (var skinned in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                Add(skinned.transform, skinned.sharedMesh);
            var result = total ?? new Bounds(Vector3.up * 0.5f, Vector3.one);
            BoundsCache[prefab.GetInstanceID()] = result;
            return result;
        }

        // yaw that turns the piece's front to the direction (local x, z)
        private static float Yaw(TarkovCityKit.Piece piece, Vector2 direction) =>
            Mathf.Atan2(direction.x, direction.y) * Mathf.Rad2Deg - (int)piece.Front * 90f;

        public static Rect FootprintOf(Bounds local, float yaw) => Footprint(local, yaw);

        // the piece's footprint turned by yaw, relative to its pivot (x, z)
        private static Rect Footprint(Bounds local, float yaw)
        {
            var rotation = Quaternion.Euler(0f, yaw, 0f);
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                var corner = rotation * new Vector3(i % 2 == 0 ? local.min.x : local.max.x, 0f, i < 2 ? local.min.z : local.max.z);
                minX = Mathf.Min(minX, corner.x);
                maxX = Mathf.Max(maxX, corner.x);
                minZ = Mathf.Min(minZ, corner.z);
                maxZ = Mathf.Max(maxZ, corner.z);
            }
            return Rect.MinMaxRect(minX, minZ, maxX, maxZ);
        }

        private static List<TarkovCityKit.Piece> Usable(List<TarkovCityKit.Piece> pieces) =>
            pieces?.Where(p => p != null && p.Prefab != null && p.Weight > 0f).ToList() ?? new List<TarkovCityKit.Piece>();

        // the pieces in a weighted random order (heavier ones earlier more often)
        private static IEnumerable<TarkovCityKit.Piece> Shuffled(List<TarkovCityKit.Piece> pieces, System.Random random) =>
            pieces.Select(p => (p, Key: Math.Pow(random.NextDouble(), 1.0 / p.Weight))).OrderByDescending(x => x.Key).Select(x => x.p);

        private static TarkovCityKit.Piece Pick(List<TarkovCityKit.Piece> pieces, System.Random random) => Shuffled(pieces, random).FirstOrDefault();

        private static GameObject Place(Context cx, string group, TarkovCityKit.Piece piece, Vector2 position, float yaw)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(piece.Prefab, cx.Groups[group]);
            instance.transform.localPosition = new Vector3(position.x, TarkovCityRoads.WalkHeight(cx.City.CurbHeight) + piece.Height, position.y);
            instance.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            cx.Placed++;
            return instance;
        }

        // a piece set so its turned footprint fills the rectangle's corner at `min`
        private static Vector2 PivotFor(Rect wanted, Rect footprint) => wanted.min - footprint.min;

        private static Rect Between(Vector2 a, Vector2 b) => Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));

        private static bool Overlaps(List<Rect> placed, Rect r)
        {
            var shrunk = Rect.MinMaxRect(r.xMin + 0.01f, r.yMin + 0.01f, r.xMax - 0.01f, r.yMax - 0.01f);
            return placed.Any(p => p.Overlaps(shrunk));
        }

        // buildings along the lot's four edges, fronts out; the first edge takes the corners
        private static void FillBuildings(Context cx, Block block)
        {
            var c = cx.City;
            var lot = Inset(block.Rect, c.SidewalkWidth);
            if (lot.width < 4f || lot.height < 4f)
                return;
            var pieces = Usable(cx.Kit?.Buildings);
            var placed = new List<Rect>();
            float gap = c.BuildingGap;
            foreach (var side in Sides(lot))
            {
                float s = 0f;
                while (s < side.Length - 2f)
                {
                    if (cx.Random.NextDouble() > c.BuildingChance)
                    {
                        s += 6f + 6f * (float)cx.Random.NextDouble();
                        continue;
                    }
                    bool done = false;
                    if (pieces.Count == 0)
                    {
                        float w = Mathf.Lerp(8f, 20f, (float)cx.Random.NextDouble());
                        float d = Mathf.Min(side.Depth, Mathf.Lerp(8f, 16f, (float)cx.Random.NextDouble()));
                        w = Mathf.Min(w, side.Length - s);
                        var rect = Between(side.Origin + side.Along * s, side.Origin + side.Along * (s + w) - side.Out * d);
                        if (w >= 5f && d >= 5f && !Overlaps(placed, Inflate(rect, gap / 2f)))
                        {
                            Placeholder(cx, rect, side.Out);
                            placed.Add(Inflate(rect, gap / 2f));
                            s += w + gap;
                            done = true;
                        }
                    }
                    else
                    {
                        foreach (var piece in Shuffled(pieces, cx.Random).Take(8))
                        {
                            float yaw = Yaw(piece, side.Out);
                            var footprint = Footprint(LocalBounds(piece.Prefab), yaw);
                            float w = Mathf.Abs(Vector2.Dot(footprint.size, side.Along));
                            float d = Mathf.Abs(Vector2.Dot(footprint.size, side.Out));
                            if (s + w > side.Length + 0.01f || d > side.Depth + 0.01f)
                                continue;
                            var rect = Between(side.Origin + side.Along * s, side.Origin + side.Along * (s + w) - side.Out * d);
                            if (Overlaps(placed, Inflate(rect, gap / 2f)))
                                continue;
                            Place(cx, "Buildings", piece, PivotFor(rect, footprint), yaw);
                            placed.Add(Inflate(rect, gap / 2f));
                            s += w + gap;
                            done = true;
                            break;
                        }
                    }
                    if (!done)
                        s += 2f;
                }
            }
        }

        private static Rect Inflate(Rect r, float d) => Rect.MinMaxRect(r.xMin - d, r.yMin - d, r.xMax + d, r.yMax + d);

        // a plain box building filling the rectangle, front to `outward`, with collision for movement and bullets
        private static void Placeholder(Context cx, Rect rect, Vector2 outward)
        {
            var c = cx.City;
            int floors = cx.Random.Next(Mathf.Max(1, Mathf.Min(c.PlaceholderFloors.x, c.PlaceholderFloors.y)), Mathf.Max(1, Mathf.Max(c.PlaceholderFloors.x, c.PlaceholderFloors.y)) + 1);
            float height = floors * 3f;
            bool alongX = Mathf.Abs(outward.y) > 0.5f;
            var go = new GameObject("Building");
            go.transform.SetParent(cx.Groups["Buildings"], false);
            go.transform.localPosition = new Vector3(rect.center.x, TarkovCityRoads.WalkHeight(c.CurbHeight), rect.center.y);
            go.transform.localRotation = Quaternion.Euler(0f, Mathf.Atan2(outward.x, outward.y) * Mathf.Rad2Deg, 0f);
            var body = new GameObject("Body");
            body.transform.SetParent(go.transform, false);
            body.transform.localPosition = Vector3.up * (height / 2f);
            // local x runs along the street, z into the block
            body.transform.localScale = new Vector3(alongX ? rect.width : rect.height, height, alongX ? rect.height : rect.width);
            body.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            body.AddComponent<MeshRenderer>().sharedMaterial = KitMaterial(cx.Kit, "PlaceholderBuilding");
            body.AddComponent<BoxCollider>();
            TarkovSceneTools.MakeStaticGeometry(body, MaterialType.Concrete);
            cx.Placed++;
        }

        // lamps at the kerb, street trees between them, props near the buildings
        private static void FillSidewalks(Context cx, Block block)
        {
            var c = cx.City;
            var lamps = Usable(cx.Kit?.Lamps);
            var trees = Usable(cx.Kit?.Trees);
            var props = Usable(cx.Kit?.StreetProps);
            var sides = Sides(block.Rect);
            const float cornerClear = 4f;
            for (int i = 0; i < 4; i++)
            {
                if (!block.Street[i])
                    continue;
                var side = sides[i];
                float usable = side.Length - 2f * cornerClear;
                if (usable <= 0f)
                    continue;
                var taken = new List<float>();
                Vector2 At(float s, float inset) => side.Origin + side.Along * s - side.Out * inset;
                if (lamps.Count > 0 && c.LampSpacing > 0f)
                {
                    int count = Mathf.FloorToInt(usable / c.LampSpacing) + 1;
                    float step = count > 1 ? usable / (count - 1) : 0f;
                    for (int k = 0; k < count; k++)
                    {
                        float s = count > 1 ? cornerClear + k * step : side.Length / 2f;
                        var piece = Pick(lamps, cx.Random);
                        Place(cx, "Street Lamps", piece, At(s, Mathf.Min(0.6f, c.SidewalkWidth / 2f)), Yaw(piece, side.Out));
                        taken.Add(s);
                    }
                }
                if (trees.Count > 0 && c.TreeSpacing > 0f && c.SidewalkWidth >= 2.5f)
                    for (float s = cornerClear + c.TreeSpacing / 2f; s < side.Length - cornerClear; s += c.TreeSpacing)
                    {
                        if (taken.Any(t => Mathf.Abs(t - s) < 2f))
                            continue;
                        var piece = Pick(trees, cx.Random);
                        Place(cx, "Trees", piece, At(s, 1.2f), (float)cx.Random.NextDouble() * 360f);
                        taken.Add(s);
                    }
                if (props.Count > 0 && c.PropsPer100m > 0f)
                {
                    float wanted = side.Length / 100f * c.PropsPer100m;
                    int count = Mathf.FloorToInt(wanted) + (cx.Random.NextDouble() < wanted - Mathf.Floor(wanted) ? 1 : 0);
                    for (int k = 0, tries = 0; k < count && tries < count * 6; tries++)
                    {
                        float s = cornerClear + (float)cx.Random.NextDouble() * usable;
                        if (taken.Any(t => Mathf.Abs(t - s) < 2f))
                            continue;
                        var piece = Pick(props, cx.Random);
                        Place(cx, "Street Props", piece, At(s, Mathf.Max(0.6f, c.SidewalkWidth - 0.7f)), Yaw(piece, side.Out));
                        taken.Add(s);
                        k++;
                    }
                }
            }
        }

        // cars along the kerbs on the road side, the kerb on their right, clear of the junctions
        private static void ParkCars(Context cx, Block block, Layout layout)
        {
            var c = cx.City;
            var cars = Usable(cx.Kit?.Vehicles);
            if (cars.Count == 0 || c.ParkedCarChance <= 0f)
                return;
            var sides = Sides(block.Rect);
            const float junctionClear = 7f, kerbGap = 0.3f;
            for (int i = 0; i < 4; i++)
            {
                if (!block.Street[i])
                    continue;
                var side = sides[i];
                var forward = new Vector2(side.Out.y, -side.Out.x);
                float s = junctionClear;
                while (s < side.Length - junctionClear)
                {
                    var piece = Pick(cars, cx.Random);
                    float yaw = Yaw(piece, forward);
                    var footprint = Footprint(LocalBounds(piece.Prefab), yaw);
                    float length = Mathf.Abs(Vector2.Dot(footprint.size, side.Along));
                    float width = Mathf.Abs(Vector2.Dot(footprint.size, side.Out));
                    if (s + length > side.Length - junctionClear)
                        break;
                    // half the narrowest road at most, so the lane in the middle stays open
                    if (kerbGap + width > c.RoadWidth / 2f)
                    {
                        cx.Notes.Add($"{piece.Prefab.name} is too wide to park on a {c.RoadWidth:0} m road.");
                        s += length + 1.5f;
                        continue;
                    }
                    if (cx.Random.NextDouble() < c.ParkedCarChance)
                    {
                        var rect = Between(side.Origin + side.Along * s + side.Out * kerbGap, side.Origin + side.Along * (s + length) + side.Out * (kerbGap + width));
                        var go = Place(cx, "Vehicles", piece, PivotFor(rect, footprint), yaw);
                        // cars stand on the road, below the kerb
                        go.transform.localPosition -= Vector3.up * TarkovCityRoads.WalkHeight(c.CurbHeight);
                    }
                    s += length + 1.5f;
                }
            }
        }

        // trees scattered with some room between them, props in the gaps
        private static void FillPark(Context cx, Block block)
        {
            var c = cx.City;
            var lot = Inset(Inset(block.Rect, c.SidewalkWidth), 1.5f);
            if (lot.width < 2f || lot.height < 2f)
                return;
            var trees = Usable(cx.Kit?.Trees);
            var props = Usable(cx.Kit?.ParkProps);
            var points = new List<Vector2>();
            Vector2 Random() => new Vector2(Mathf.Lerp(lot.xMin, lot.xMax, (float)cx.Random.NextDouble()), Mathf.Lerp(lot.yMin, lot.yMax, (float)cx.Random.NextDouble()));
            float area = lot.width * lot.height;
            if (trees.Count > 0)
            {
                float spacing = Mathf.Max(2f, c.ParkTreeSpacing);
                int wanted = Mathf.FloorToInt(area / (spacing * spacing));
                for (int k = 0, tries = 0; k < wanted && tries < wanted * 8; tries++)
                {
                    var p = Random();
                    if (points.Any(q => (q - p).sqrMagnitude < spacing * spacing * 0.64f))
                        continue;
                    Place(cx, "Trees", Pick(trees, cx.Random), p, (float)cx.Random.NextDouble() * 360f);
                    points.Add(p);
                    k++;
                }
            }
            if (props.Count > 0)
            {
                float wanted = area / 1000f * c.ParkPropsPer1000m2;
                int count = Mathf.FloorToInt(wanted) + (cx.Random.NextDouble() < wanted - Mathf.Floor(wanted) ? 1 : 0);
                for (int k = 0, tries = 0; k < count && tries < count * 8; tries++)
                {
                    var p = Random();
                    if (points.Any(q => (q - p).sqrMagnitude < 9f))
                        continue;
                    Place(cx, "Park Props", Pick(props, cx.Random), p, cx.Random.Next(4) * 90f);
                    points.Add(p);
                    k++;
                }
            }
        }

        // ------------------------------------------------------------ objects

        public static TarkovCity Create(Vector3 position, TarkovCityKit kit)
        {
            var root = new GameObject("City");
            root.transform.position = position;
            var city = TarkovMapEvents.EditorOnlyChild(root.transform, "SDK_City").AddComponent<TarkovCity>();
            city.Kit = kit;
            city.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            Undo.RegisterCreatedObjectUndo(root, "Create City");
            return city;
        }

        public static List<TarkovCity> Cities()
        {
            var cities = new List<TarkovCity>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded)
                    foreach (var root in scene.GetRootGameObjects())
                        cities.AddRange(root.GetComponentsInChildren<TarkovCity>(true));
            }
            return cities;
        }

        // ------------------------------------------------------------ kits

        private const string KitPref = "TarkovSDK.CityKit";

        // the kit the City Builder last showed (the Asset Importer adds to it), else the project's first
        public static TarkovCityKit CurrentKit
        {
            get
            {
                var kit = AssetDatabase.LoadAssetAtPath<TarkovCityKit>(EditorPrefs.GetString(KitPref, ""));
                if (kit != null)
                    return kit;
                var guid = AssetDatabase.FindAssets("t:TarkovCityKit").FirstOrDefault();
                return guid != null ? AssetDatabase.LoadAssetAtPath<TarkovCityKit>(AssetDatabase.GUIDToAssetPath(guid)) : null;
            }
            set => EditorPrefs.SetString(KitPref, value != null ? AssetDatabase.GetAssetPath(value) : "");
        }

        // a new kit in the map's folder with the SDK's plain ground materials in its slots
        public static TarkovCityKit CreateKit()
        {
            string folder = TarkovPrefabLibrary.MapFolder() ?? "Assets";
            var kit = ScriptableObject.CreateInstance<TarkovCityKit>();
            foreach (var (field, _) in MaterialSlots)
                typeof(TarkovCityKit).GetField(field).SetValue(kit, DefaultMaterial(field));
            AssetDatabase.CreateAsset(kit, AssetDatabase.GenerateUniqueAssetPath(folder + "/City Kit.asset"));
            AssetDatabase.SaveAssets();
            CurrentKit = kit;
            Debug.Log("[Tarkov SDK] City Builder: made " + AssetDatabase.GetAssetPath(kit));
            return kit;
        }

        public static string SlotLabel(string field) =>
            MaterialSlots.Concat(PieceSlots).FirstOrDefault(s => s.Field == field).Label ?? field;

        // puts materials or prefabs in the kit's slot (a material slot takes the first material); how many went in
        public static int AddToKit(TarkovCityKit kit, string field, IEnumerable<Object> objects)
        {
            Undo.RecordObject(kit, "Add to " + kit.name);
            int added = 0;
            var info = typeof(TarkovCityKit).GetField(field);
            if (info == null)
                return 0;
            if (info.FieldType == typeof(Material))
            {
                var material = objects.OfType<Material>().FirstOrDefault();
                if (material != null)
                {
                    info.SetValue(kit, material);
                    added = 1;
                }
            }
            else if (info.GetValue(kit) is List<TarkovCityKit.Piece> list)
            {
                foreach (var prefab in objects.OfType<GameObject>())
                    if (list.All(p => p.Prefab != prefab))
                    {
                        list.Add(new TarkovCityKit.Piece { Prefab = prefab });
                        added++;
                    }
            }
            EditorUtility.SetDirty(kit);
            AssetDatabase.SaveAssets();
            return added;
        }

        // prefabs the Asset Importer made (menu path "<category>/<name>", asset path), refreshed when the project changes
        private static List<(string Menu, string Path)> _imported;
        private static List<(string Menu, string Path)> _importedMaterials;

        public static List<(string Menu, string Path)> ImportedPrefabs()
        {
            if (_imported != null)
                return _imported;
            _imported = new List<(string, string)>();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var data = AssetImporter.GetAtPath(path)?.userData;
                if (data == null || !data.StartsWith("TarkovGame:"))
                    continue;
                string category = Path.GetFileName(Path.GetDirectoryName(path));
                _imported.Add(($"{category}/{Path.GetFileNameWithoutExtension(path)}", path));
            }
            _imported.Sort((a, b) => string.CompareOrdinal(a.Menu, b.Menu));
            EditorApplication.projectChanged -= ForgetImported;
            EditorApplication.projectChanged += ForgetImported;
            return _imported;
        }

        // materials the Asset Importer made: "<game file>/<name>"
        public static List<(string Menu, string Path)> ImportedMaterials()
        {
            if (_importedMaterials != null)
                return _importedMaterials;
            _importedMaterials = new List<(string, string)>();
            if (AssetDatabase.IsValidFolder(TarkovGameAssets.Root))
                foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { TarkovGameAssets.Root }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var rest = path.Substring(TarkovGameAssets.Root.Length + 1).Split('/');
                    _importedMaterials.Add(($"{rest[0]}/{Path.GetFileNameWithoutExtension(path)}", path));
                }
            _importedMaterials.Sort((a, b) => string.CompareOrdinal(a.Menu, b.Menu));
            EditorApplication.projectChanged -= ForgetImported;
            EditorApplication.projectChanged += ForgetImported;
            return _importedMaterials;
        }

        private static void ForgetImported()
        {
            _imported = null;
            _importedMaterials = null;
        }
    }

    // ---------------------------------------------------------------- window

    public partial class TarkovCityBuilderWindow : EditorWindow
    {
        private TarkovCity _city;
        // the open scenes' cities, found again when the hierarchy changes
        private List<TarkovCity> _cities;
        private UnityEditor.Editor _cityEditor;
        private UnityEditor.Editor _kitEditor;
        private bool _showKit = true;
        private Vector2 _scroll;
        private string _status;
        private string _error;
        // the planned layout, remade when the settings change
        private TarkovCityBuilder.Layout _layout;
        private string _layoutKey;

        [MenuItem("Tarkov SDK/Tools/Geometry/City Builder...", priority = 133)]
        public static void Open() => GetWindow<TarkovCityBuilderWindow>("City Builder").minSize = new Vector2(380f, 480f);

        public static void RepaintOpen()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<TarkovCityBuilderWindow>())
                window.Repaint();
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGui;
            Selection.selectionChanged += OnSelection;
            EditorApplication.hierarchyChanged += ForgetCities;
            OnSelection();
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGui;
            Selection.selectionChanged -= OnSelection;
            EditorApplication.hierarchyChanged -= ForgetCities;
            DestroyImmediate(_cityEditor);
            DestroyImmediate(_kitEditor);
            DisableTools();
            SceneView.RepaintAll();
        }

        private void ForgetCities()
        {
            _cities = null;
            Repaint();
        }

        private void OnSelection()
        {
            TarkovCity picked = null;
            if (Selection.activeTransform != null)
            {
                picked = Selection.activeTransform.GetComponentInChildren<TarkovCity>(true);
                if (picked == null)
                    picked = Selection.activeTransform.GetComponentInParent<TarkovCity>();
            }
            if (picked == null && Selection.activeTransform != null && Selection.activeTransform.parent != null)
                picked = Selection.activeTransform.parent.GetComponentInChildren<TarkovCity>(true);
            if (picked != null && picked != _city)
            {
                _city = picked;
                Repaint();
            }
        }

        private void DrawGenerate()
        {
            var cities = _cities ?? (_cities = TarkovCityBuilder.Cities());
            if (_city == null)
                _city = cities.FirstOrDefault();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("A city of blocks and streets from a kit of materials and prefabs: yours, or Tarkov's through the Asset Importer " +
                                       "(pick objects or materials there and press Add To City Kit).", EditorStyles.wordWrappedMiniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                int index = cities.IndexOf(_city);
                var names = cities.Select((c, i) => $"{i + 1}: {(c.transform.parent != null ? c.transform.parent.name : c.name)} ({c.gameObject.scene.name})").ToArray();
                int picked = EditorGUILayout.Popup("City", index, names);
                if (picked != index && picked >= 0)
                    _city = cities[picked];
                if (GUILayout.Button("New City", GUILayout.Width(80f)))
                    EditorApplication.delayCall += () =>
                    {
                        var view = SceneView.lastActiveSceneView;
                        _city = TarkovCityBuilder.Create(view != null ? view.pivot : Vector3.zero, TarkovCityBuilder.CurrentKit);
                        Selection.activeGameObject = _city.transform.parent.gameObject;
                        Repaint();
                    };
            }
            if (_city == null)
            {
                EditorGUILayout.HelpBox("New City makes a City object where the Scene view looks; the city is built under it, centred on it.", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUILayout.Space();
            UnityEditor.Editor.CreateCachedEditor(_city, null, ref _cityEditor);
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                _cityEditor.OnInspectorGUI();
                if (check.changed)
                    SceneView.RepaintAll();
            }
            if (_city.Kit == null)
            {
                if (GUILayout.Button(new GUIContent("New Kit", "A City Kit in the map's folder, with the SDK's plain ground materials in it.")))
                    EditorApplication.delayCall += () =>
                    {
                        Undo.RecordObject(_city, "New City Kit");
                        _city.Kit = TarkovCityBuilder.CreateKit();
                        EditorUtility.SetDirty(_city);
                        Repaint();
                    };
                EditorGUILayout.HelpBox("Without a kit the city gets the SDK's plain ground and placeholder buildings.", MessageType.None);
            }
            else
            {
                if (TarkovCityBuilder.CurrentKit != _city.Kit)
                    TarkovCityBuilder.CurrentKit = _city.Kit;
                EditorGUILayout.Space();
                _showKit = EditorGUILayout.Foldout(_showKit, $"Kit: {_city.Kit.name}", true, EditorStyles.foldoutHeader);
                if (_showKit)
                {
                    UnityEditor.Editor.CreateCachedEditor(_city.Kit, typeof(TarkovCityKitEditor), ref _kitEditor);
                    _kitEditor.OnInspectorGUI();
                    if (AssetPreview.IsLoadingAssetPreviews())
                        Repaint();
                }
            }

            EditorGUILayout.Space();
            var layout = Layout();
            EditorGUILayout.LabelField($"{layout.Blocks.Count} blocks, {layout.Blocks.Count(b => b.Park)} of them parks; {layout.Segments.Count} stretches of road " +
                                       "(outlined in the Scene view)", EditorStyles.miniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Generate", "Builds the city under the City object, replacing what it built before."), GUILayout.Height(28f)))
                    EditorApplication.delayCall += () => Generate(false);
                if (GUILayout.Button(new GUIContent("New Seed + Generate", "Another layout and filling with the same settings."), GUILayout.Height(28f), GUILayout.Width(150f)))
                    EditorApplication.delayCall += () => Generate(true);
            }
            if (GUILayout.Button(new GUIContent("Clear", "Removes what the city built (the ground and everything placed).")))
                EditorApplication.delayCall += () =>
                {
                    if (_city != null && _city.transform.parent != null &&
                        EditorUtility.DisplayDialog("City Builder", "Remove the city's ground and every object it placed? This can't be undone.", "Clear", "Cancel"))
                    {
                        TarkovCityBuilder.Clear(_city.transform.parent);
                        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(_city.gameObject.scene);
                        _status = "Cleared.";
                        Repaint();
                    }
                };
            if (!string.IsNullOrEmpty(_error))
                EditorGUILayout.HelpBox(_error, MessageType.Error);
            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        private TarkovCityBuilder.Layout Layout()
        {
            string key = JsonUtility.ToJson(_city);
            if (_layout == null || key != _layoutKey)
            {
                _layout = TarkovCityBuilder.Plan(_city);
                _layoutKey = key;
            }
            return _layout;
        }

        private void Generate(bool newSeed)
        {
            if (_city == null)
                return;
            var root = _city.transform.parent;
            bool built = root != null && TarkovCityBuilder.Groups.Any(g => root.Find(g) != null);
            if (built && !EditorUtility.DisplayDialog("City Builder",
                    "Generate replaces the city's ground and every object it placed before (objects moved out of the city's groups stay). This can't be undone.",
                    "Generate", "Cancel"))
                return;
            if (newSeed)
            {
                Undo.RecordObject(_city, "New City Seed");
                _city.Seed = new System.Random().Next(1, 100000);
                EditorUtility.SetDirty(_city);
            }
            _error = TarkovCityBuilder.Generate(_city, out _status);
            Repaint();
            SceneView.RepaintAll();
        }

        private void OnSceneGui(SceneView view)
        {
            SceneTools(view);
            if (_tab != Tab.Generate)
                return;
            if (_city == null || _city.transform.parent == null || Event.current.type != EventType.Repaint)
                return;
            var layout = Layout();
            using (new Handles.DrawingScope(_city.transform.parent.localToWorldMatrix))
            {
                float y = _city.CurbHeight + 0.05f;
                Vector3[] Corners(Rect r) => new[]
                {
                    new Vector3(r.xMin, y, r.yMin), new Vector3(r.xMin, y, r.yMax), new Vector3(r.xMax, y, r.yMax), new Vector3(r.xMax, y, r.yMin),
                };
                Handles.DrawSolidRectangleWithOutline(Corners(layout.Area), new Color(1f, 1f, 1f, 0.02f), new Color(1f, 1f, 1f, 0.6f));
                foreach (var block in layout.Blocks)
                    Handles.DrawSolidRectangleWithOutline(Corners(block.Rect),
                        block.Park ? new Color(0.3f, 0.9f, 0.3f, 0.12f) : new Color(1f, 0.8f, 0.2f, 0.08f),
                        block.Park ? new Color(0.3f, 0.9f, 0.3f, 0.8f) : new Color(1f, 0.8f, 0.2f, 0.8f));
            }
        }
    }

    // ---------------------------------------------------------------- kit inspector

    [CustomEditor(typeof(TarkovCityKit))]
    public class TarkovCityKitEditor : UnityEditor.Editor
    {
        public override bool RequiresConstantRepaint() => AssetPreview.IsLoadingAssetPreviews();

        public override void OnInspectorGUI()
        {
            var kit = (TarkovCityKit)target;
            serializedObject.Update();
            EditorGUILayout.LabelField("Ground", EditorStyles.boldLabel);
            foreach (var (field, label) in TarkovCityBuilder.MaterialSlots)
                using (new EditorGUILayout.HorizontalScope())
                {
                    var property = serializedObject.FindProperty(field);
                    EditorGUILayout.PropertyField(property, new GUIContent(label, property.tooltip));
                    if (GUILayout.Button(new GUIContent("Tarkov", "A material imported from the game with the Asset Importer (its Materials kind)."), EditorStyles.miniButton, GUILayout.Width(52f)))
                        ImportedMenu(TarkovCityBuilder.ImportedMaterials(), "No game materials imported yet: pick some in the Asset Importer's Materials kind and press Add To City Kit.",
                            path => TarkovCityBuilder.AddToKit(kit, field, new Object[] { AssetDatabase.LoadAssetAtPath<Material>(path) }));
                }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("TextureMetres"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Prefabs", EditorStyles.boldLabel);
            foreach (var (field, label) in TarkovCityBuilder.PieceSlots)
            {
                var property = serializedObject.FindProperty(field);
                EditorGUILayout.PropertyField(property, new GUIContent($"{label} ({property.arraySize})", property.tooltip), true);
                // a row added with + starts empty: give it a weight, or it would never be picked
                for (int i = 0; i < property.arraySize; i++)
                {
                    var element = property.GetArrayElementAtIndex(i);
                    if (element.FindPropertyRelative("Prefab").objectReferenceValue == null && element.FindPropertyRelative("Weight").floatValue <= 0f)
                        element.FindPropertyRelative("Weight").floatValue = 1f;
                }
                if (!property.isExpanded)
                    continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(16f);
                    if (GUILayout.Button(new GUIContent("Add Selected Prefabs", "The prefabs selected in the Project window."), EditorStyles.miniButtonLeft))
                    {
                        var prefabs = Selection.objects.OfType<GameObject>().Where(EditorUtility.IsPersistent).Cast<Object>().ToList();
                        EditorApplication.delayCall += () => TarkovCityBuilder.AddToKit(kit, field, prefabs);
                    }
                    if (GUILayout.Button(new GUIContent("Add Tarkov Prefab", "A prefab imported from the game with the Asset Importer."), EditorStyles.miniButtonRight))
                        ImportedMenu(TarkovCityBuilder.ImportedPrefabs(), "No game prefabs imported yet: pick some in the Asset Importer and press Add To City Kit.",
                            path => TarkovCityBuilder.AddToKit(kit, field, new Object[] { AssetDatabase.LoadAssetAtPath<GameObject>(path) }));
                }
            }
            serializedObject.ApplyModifiedProperties();
        }

        private static void ImportedMenu(List<(string Menu, string Path)> items, string none, Action<string> pick)
        {
            var menu = new GenericMenu();
            foreach (var (label, path) in items)
                menu.AddItem(new GUIContent(label), false, () => pick(path));
            if (items.Count == 0)
                menu.AddDisabledItem(new GUIContent(none));
            menu.ShowAsContext();
        }
    }

    // a kit piece on two lines: the prefab with its thumbnail and size, then its front, weight and height
    [CustomPropertyDrawer(typeof(TarkovCityKit.Piece))]
    public class TarkovCityPieceDrawer : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) => 2f * EditorGUIUtility.singleLineHeight + 6f;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var prefab = property.FindPropertyRelative("Prefab");
            float line = EditorGUIUtility.singleLineHeight;
            var thumb = new Rect(position.x, position.y + 1f, position.height - 2f, position.height - 2f);
            var go = prefab.objectReferenceValue as GameObject;
            if (go != null)
            {
                var image = AssetPreview.GetAssetPreview(go) ?? AssetPreview.GetMiniThumbnail(go);
                if (image != null)
                    GUI.DrawTexture(thumb, image, ScaleMode.ScaleToFit);
            }
            float x = thumb.xMax + 4f, width = position.xMax - x;
            var top = new Rect(x, position.y + 2f, width, line);
            var bottom = new Rect(x, top.yMax + 2f, width, line);
            float sizeWidth = go != null ? 90f : 0f;
            EditorGUI.PropertyField(new Rect(top.x, top.y, top.width - sizeWidth, line), prefab, GUIContent.none);
            if (go != null)
            {
                var size = TarkovCityBuilder.LocalBounds(go).size;
                EditorGUI.LabelField(new Rect(top.xMax - sizeWidth + 4f, top.y, sizeWidth - 4f, line),
                    new GUIContent($"{size.x:0.#} x {size.z:0.#} m", $"{size.x:0.##} x {size.z:0.##} m, {size.y:0.##} m high (its meshes' bounds)"), EditorStyles.miniLabel);
            }
            float third = bottom.width / 3f;
            using (new EditorGUI.IndentLevelScope(-EditorGUI.indentLevel))
            {
                float labelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 40f;
                EditorGUI.PropertyField(new Rect(bottom.x, bottom.y, third * 1.4f - 4f, line), property.FindPropertyRelative("Front"));
                EditorGUIUtility.labelWidth = 46f;
                EditorGUI.PropertyField(new Rect(bottom.x + third * 1.4f, bottom.y, third * 0.8f - 4f, line), property.FindPropertyRelative("Weight"));
                EditorGUI.PropertyField(new Rect(bottom.x + third * 2.2f, bottom.y, third * 0.8f, line), property.FindPropertyRelative("Height"));
                EditorGUIUtility.labelWidth = labelWidth;
            }
        }
    }
}
