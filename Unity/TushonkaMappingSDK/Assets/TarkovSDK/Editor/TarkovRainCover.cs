using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using EFT.EnvironmentEffect;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // Rain cover for the full weather system. The game decides whether the camera is under rain
    // (screen drops, wet screen, puddle ripples) by raycasting down onto WeatherObstacle's collider,
    // which it builds from the meshes of the DryPlane components under it: anywhere with a dry
    // plane above the camera counts as covered. The same mesh is drawn into the rain's depth map
    // (DepthPhotograper), which keeps rain and splashes out from under it.
    //
    // How the game reads it (WeatherObstacle.Awake, CombineMeshes): the active DryPlanes under the one
    // WeatherObstacle (the first to Start; a second logs an error) are combined in world space and the
    // result is put on a MeshCollider on the obstacle's own object, which applies the obstacle's
    // transform a second time, so the obstacle must sit at the origin unrotated and unscaled; and a
    // DryPlane anywhere else (inside a prop, a building prefab) is ignored. Build Map (PrepareMap) fixes
    // both: the obstacle is put back at the origin, and every other DryPlane on the map is copied under
    // it where it stands (SDK_GatheredDryPlanes, remade each build).
    //
    // Two ways to make covers:
    //  - Generate (Tarkov SDK > Generation > Geometry > Rain Cover): the whole map from above on a grid.
    //    A cell is covered when the first surface the scan hits has more solid geometry under it with
    //    head room (a roof over a floor). Heights are taken at the cells' corners so covers follow
    //    sloped roofs, and the cells along a roof's edge are scanned again at a third of the size.
    //    Objects that carry their own cover are left to it.
    //  - For This Object (GameObject > Tarkov SDK > Environment > Rain Cover For This Object): the same
    //    scan of one prop or building in its own frame (lined up with its walls), saved inside it, so the
    //    cover turns and moves with it and comes with every copy of its prefab.
    public static class TarkovRainCover
    {
        public const string RootName = "RainCover";
        public const string GatheredName = "SDK_GatheredDryPlanes";
        // an object's own cover (For This Object)
        public const string OwnName = "RainCover";
        private const float CellSize = 1.5f;
        private const float ObjectCellSize = 0.5f;
        private const float HeadRoom = 1.8f;
        // edge cells are scanned again in Sub x Sub parts
        private const int Sub = 3;

        [MenuItem("Tarkov SDK/Generation/Geometry/Rain Cover", priority = 110)]
        public static void GenerateMenu()
        {
            var scene = SceneManager.GetActiveScene();
            TarkovMapScenes.OpenMapScenes(scene);
            var root = Generate(scene);
            if (root != null)
            {
                Selection.activeGameObject = root;
                EditorSceneManager.MarkSceneDirty(root.scene);
            }
        }

        [MenuItem("GameObject/Tarkov SDK/Environment/Rain Cover For This Object", priority = 78)]
        private static void ForObjectMenu(MenuCommand cmd)
        {
            var target = cmd.context as GameObject ?? Selection.activeGameObject;
            if (target != null)
                EditorApplication.delayCall += () => ForObjectInteractive(target);
        }

        public static GameObject Find(Scene scene)
        {
            foreach (var go in scene.GetRootGameObjects())
                if (go.GetComponentInChildren<WeatherObstacle>(true) != null)
                    return go;
            return null;
        }

        // layers of the solid geometry the scans hit
        internal static int SolidMask => LayerMask.GetMask("Default", "Terrain", "HighPolyCollider", "LowPolyCollider");

        // the map's rain cover, in any of its loaded scenes
        public static GameObject FindInMap(Scene scene) =>
            TarkovMapScenes.MapScenes(scene).Select(Find).FirstOrDefault(c => c != null);

        // SolidBounds over the loaded scenes of the scene's map (TarkovMapScenes.MapScenes)
        internal static bool MapSolidBounds(Scene scene, int mask, out Bounds bounds, Func<Collider, bool> include = null)
        {
            bounds = new Bounds();
            bool any = false;
            foreach (var s in TarkovMapScenes.MapScenes(scene))
                if (SolidBounds(s, mask, out var b, include))
                {
                    if (!any) { bounds = b; any = true; }
                    else bounds.Encapsulate(b);
                }
            return any;
        }

        // bounds of every solid collider in the scene (that include accepts): the area to scan
        internal static bool SolidBounds(Scene scene, int mask, out Bounds bounds, Func<Collider, bool> include = null)
        {
            bounds = new Bounds();
            bool any = false;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var collider in root.GetComponentsInChildren<Collider>())
                {
                    if (collider.isTrigger || (mask & (1 << collider.gameObject.layer)) == 0 || (include != null && !include(collider)))
                        continue;
                    if (!any) { bounds = collider.bounds; any = true; }
                    else bounds.Encapsulate(collider.bounds);
                }
            return any;
        }

        // ------------------------------------------------------------ the whole map

        // over the whole map (every loaded scene of it), saved in its first scene
        public static GameObject Generate(Scene scene)
        {
            int mask = SolidMask;
            if (!MapSolidBounds(scene, mask, out var bounds))
            {
                EditorUtility.DisplayDialog("Rain Cover", "The scene has no solid colliders to scan.", "OK");
                return null;
            }

            // objects with their own cover keep it; their roofs aren't covered twice
            var owners = new HashSet<Transform>(TarkovMapScenes.MapScenes(scene).SelectMany(s => s.GetRootGameObjects())
                .SelectMany(r => r.GetComponentsInChildren<DryPlane>(true))
                .Where(p => p.GetComponentInParent<WeatherObstacle>(true) == null && p.transform.parent != null)
                .Select(p => p.transform.parent));
            Physics.SyncTransforms();
            var scan = new Scanner
            {
                Physics = Physics.defaultPhysicsScene,
                Frame = Matrix4x4.identity,
                Cell = CellSize,
                Mask = mask,
                Top = bounds.max.y + 10f,
                Roof = owners.Count == 0 ? (Func<Collider, bool>)null : c => !OwnedBy(c.transform, owners),
            };
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            int covered;
            try
            {
                covered = scan.Run(bounds.min.x, bounds.min.z, bounds.max.x, bounds.max.z, Matrix4x4.identity, vertices, triangles, "Scanning for roofs");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            if (covered < 0)
                return null;

            // one per map: covers made in other scenes before replace with it too
            foreach (var s in TarkovMapScenes.MapScenes(scene))
            {
                var old = Find(s);
                if (old != null)
                    Undo.DestroyObjectImmediate(old);
            }
            scene = TarkovMapScenes.FirstScene(scene);
            if (covered == 0)
            {
                Debug.Log("[Tarkov SDK] Rain cover: nothing on the map is covered; the whole map counts as under rain." +
                          (owners.Count > 0 ? $" ({owners.Count} object(s) carry their own cover; Build Map gathers them.)" : ""));
                return null;
            }

            var cover = CreateCover(scene, vertices, triangles, out var path);
            Debug.Log($"[Tarkov SDK] Rain cover: {covered} m² covered, {triangles.Count / 3} triangle(s), mesh saved to {path}" +
                      (owners.Count > 0 ? $"; {owners.Count} object(s) with their own cover left to it" : ""));
            return cover;
        }

        private static bool OwnedBy(Transform t, HashSet<Transform> owners)
        {
            for (var x = t; x != null; x = x.parent)
                if (owners.Contains(x))
                    return true;
            return false;
        }

        // one dry plane above the whole area, so nowhere counts as under the sky (Entirely Indoors maps)
        internal static GameObject GenerateWholeMap(Scene scene, Bounds bounds)
        {
            var old = Find(scene);
            if (old != null)
                Undo.DestroyObjectImmediate(old);
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            AddQuad(vertices, triangles, new Vector3(bounds.min.x, bounds.max.y, bounds.min.z), new Vector3(bounds.min.x, bounds.max.y, bounds.max.z),
                    new Vector3(bounds.max.x, bounds.max.y, bounds.max.z), new Vector3(bounds.max.x, bounds.max.y, bounds.min.z));
            return CreateCover(scene, vertices, triangles, out _);
        }

        private static GameObject CreateCover(Scene scene, List<Vector3> vertices, List<int> triangles, out string path)
        {
            path = Path.Combine(Path.GetDirectoryName(scene.path) ?? "Assets", scene.name + "_RainCover.asset").Replace('\\', '/');
            var mesh = SaveMesh(path, scene.name + "_RainCover", vertices, triangles);

            // its collider never blocks players: the Triggers layer doesn't collide with them
            var cover = new GameObject(RootName) { layer = LayerMask.NameToLayer("Triggers") };
            SceneManager.MoveGameObjectToScene(cover, scene);
            cover.AddComponent<WeatherObstacle>();
            var plane = new GameObject("DryPlane") { layer = cover.layer };
            plane.transform.SetParent(cover.transform, false);
            plane.AddComponent<DryPlane>().Mesh = mesh;
            Undo.RegisterCreatedObjectUndo(cover, "Generate rain cover");
            return cover;
        }

        // the mesh written into the asset at path, in place when it's there (its GUID, and so every reference, stays)
        private static Mesh SaveMesh(string path, string name, List<Vector3> vertices, List<int> triangles)
        {
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            bool created = mesh == null;
            if (created)
                mesh = new Mesh();
            mesh.Clear();
            mesh.name = name;
            mesh.indexFormat = vertices.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            if (created)
            {
                TarkovGeneratedAssets.EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
                AssetDatabase.CreateAsset(mesh, path);
            }
            else
                EditorUtility.SetDirty(mesh);
            AssetDatabase.SaveAssets();
            return mesh;
        }

        // ------------------------------------------------------------ one object

        private static void ForObjectInteractive(GameObject target)
        {
            var result = ForObject(target, out var message);
            Debug.Log("[Tarkov SDK] Rain cover for " + target.name + ": " + message);
            if (result == null)
                EditorUtility.DisplayDialog("Rain Cover", message, "OK");
            else
                Selection.activeGameObject = result;
        }

        // A cover made for the object and kept inside it (a child named RainCover with a DryPlane, its mesh in the
        // object's own space). In Prefab Mode, or on a prefab instance's root, it goes into the prefab. Null if nothing
        // on the object is a roof (message says why).
        public static GameObject ForObject(GameObject target, out string message)
        {
            int mask = SolidMask;
            var colliders = target.GetComponentsInChildren<Collider>()
                .Where(c => !c.isTrigger && c.enabled && (mask & (1 << c.gameObject.layer)) != 0 && c.GetComponentInParent<WeatherObstacle>(true) == null).ToList();
            if (colliders.Count == 0)
            {
                message = "it has no solid colliders (Default, Terrain, LowPolyCollider or HighPolyCollider) to find its roof on.";
                return null;
            }

            // a frame turned with the object about the vertical only: rain falls straight down whatever its tilt
            var t = target.transform;
            var forward = Vector3.ProjectOnPlane(t.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f)
                forward = Vector3.ProjectOnPlane(t.up, Vector3.up);
            var frame = Matrix4x4.TRS(t.position, Quaternion.LookRotation(forward.sqrMagnitude < 1e-4f ? Vector3.forward : forward.normalized, Vector3.up), Vector3.one);
            var toFrame = frame.inverse;
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue, top = float.MinValue, bottom = float.MaxValue;
            foreach (var c in colliders)
            {
                var b = c.bounds;
                top = Mathf.Max(top, b.max.y);
                bottom = Mathf.Min(bottom, b.min.y);
                for (int i = 0; i < 8; i++)
                {
                    var p = toFrame.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z));
                    minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                    minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
                }
            }
            // an old cover of its own doesn't count as a roof
            var own = new HashSet<Collider>(colliders);
            var physics = target.scene.GetPhysicsScene();
            Physics.SyncTransforms();
            var scan = new Scanner
            {
                Physics = physics,
                Frame = frame,
                Cell = ObjectCellSize,
                Mask = mask,
                Top = top + 5f,
                Roof = own.Contains,
                // a prefab on its own (Prefab Mode) stands on nothing: its bottom is the ground
                Ground = bottom,
            };
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            int covered;
            try
            {
                covered = scan.Run(minX - 0.25f, minZ - 0.25f, maxX + 0.25f, maxZ + 0.25f, t.worldToLocalMatrix, vertices, triangles, "Scanning " + target.name);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            if (covered <= 0)
            {
                message = covered < 0 ? "cancelled." : $"nothing on it is a roof with {HeadRoom} m of head room under it.";
                return null;
            }

            // where the mesh lives: next to the prefab it goes into, else in the scene's folder
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            string prefabPath = stage != null && stage.scene == target.scene ? stage.assetPath
                : PrefabUtility.IsOutermostPrefabInstanceRoot(target) ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(target) : null;
            if (prefabPath != null && (prefabPath.StartsWith("Packages/") || AssetDatabase.IsOpenForEdit(prefabPath) == false))
                prefabPath = null;
            string folder = prefabPath != null ? Path.GetDirectoryName(prefabPath)
                : Path.Combine(Path.GetDirectoryName(target.scene.path) ?? "Assets", (string.IsNullOrEmpty(target.scene.name) ? "Untitled" : target.scene.name) + "_RainCover");
            string baseName = prefabPath != null ? Path.GetFileNameWithoutExtension(prefabPath) : Sanitize(target.name);
            string meshPath = (folder + "/" + baseName + "_RainCover.asset").Replace('\\', '/');
            // a scene object without a prefab: one asset per object (two objects of the same name don't share)
            if (prefabPath == null && ExistingOwn(target) == null && AssetDatabase.LoadAssetAtPath<Mesh>(meshPath) != null)
                meshPath = AssetDatabase.GenerateUniqueAssetPath(meshPath);
            var existing = ExistingOwn(target);
            if (existing != null && existing.Mesh != null && AssetDatabase.Contains(existing.Mesh))
                meshPath = AssetDatabase.GetAssetPath(existing.Mesh);
            var mesh = SaveMesh(meshPath, baseName + "_RainCover", vertices, triangles);

            GameObject cover;
            if (existing != null)
            {
                Undo.RecordObject(existing, "Rain cover");
                existing.Mesh = mesh;
                cover = existing.gameObject;
            }
            else
            {
                cover = new GameObject(OwnName) { layer = LayerMask.NameToLayer("Triggers") };
                cover.transform.SetParent(t, false);
                cover.AddComponent<DryPlane>().Mesh = mesh;
                Undo.RegisterCreatedObjectUndo(cover, "Rain cover");
            }
            string where;
            if (stage != null && stage.scene == target.scene)
            {
                EditorSceneManager.MarkSceneDirty(stage.scene);
                where = "in the prefab (save it in Prefab Mode)";
            }
            else if (prefabPath != null && existing == null)
            {
                // into the prefab, so every copy of it has the cover
                PrefabUtility.ApplyAddedGameObject(cover, prefabPath, InteractionMode.UserAction);
                where = $"in its prefab {prefabPath} (every copy has it)";
            }
            else
            {
                if (prefabPath != null)
                    PrefabUtility.ApplyObjectOverride(cover.GetComponent<DryPlane>(), prefabPath, InteractionMode.UserAction);
                EditorSceneManager.MarkSceneDirty(target.scene);
                where = prefabPath != null ? $"in its prefab {prefabPath}" : "on the object";
            }
            message = $"{covered} m² covered, {triangles.Count / 3} triangle(s), {where}; mesh {meshPath}. Build Map gathers it into the map's rain cover.";
            return cover;
        }

        private static DryPlane ExistingOwn(GameObject target) =>
            target.transform.Cast<Transform>().Where(c => c.name == OwnName).Select(c => c.GetComponent<DryPlane>()).FirstOrDefault(p => p != null);

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }

        // ------------------------------------------------------------ the scan

        // A grid over a frame's x/z (the world's for the map, an object's turned one for its own cover), scanned
        // with rays straight down from Top.
        private sealed class Scanner
        {
            public PhysicsScene Physics;
            public Matrix4x4 Frame;
            public float Cell;
            public int Mask;
            public float Top;
            // the colliders that can be a roof (null: any)
            public Func<Collider, bool> Roof;
            // the world height that counts as the floor when nothing solid is under a roof (null: none, not covered)
            public float? Ground;

            private readonly RaycastHit[] _hits = new RaycastHit[64];
            private static readonly IComparer<RaycastHit> ByDistance = Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance));

            private Vector3 World(float x, float z)
            {
                var p = Frame.MultiplyPoint3x4(new Vector3(x, 0f, z));
                return new Vector3(p.x, Top, p.z);
            }

            // the first roof surface over the point, in world height
            private bool RoofHit(float x, float z, out RaycastHit roof)
            {
                roof = default;
                int n = Physics.Raycast(World(x, z), Vector3.down, _hits, Top + 2000f, Mask, QueryTriggerInteraction.Ignore);
                if (n == 0)
                    return false;
                Array.Sort(_hits, 0, n, ByDistance);
                for (int i = 0; i < n; i++)
                    if (Roof == null || Roof(_hits[i].collider))
                    {
                        roof = _hits[i];
                        return true;
                    }
                return false;
            }

            public bool RoofAt(float x, float z, out float height)
            {
                height = 0f;
                if (!RoofHit(x, z, out var roof))
                    return false;
                height = roof.point.y - 0.05f;
                return true;
            }

            // covered: a roof with something solid (or the ground) under it, HeadRoom down
            public bool Covered(float x, float z, out float height)
            {
                height = 0f;
                if (!RoofHit(x, z, out var roof))
                    return false;
                // one hit per collider in a RaycastAll, so the floor is a ray of its own (a building is often one collider)
                float floor;
                if (Physics.Raycast(roof.point + Vector3.down * 0.05f, Vector3.down, out var below, 500f, Mask, QueryTriggerInteraction.Ignore))
                    floor = below.point.y;
                else if (Ground.HasValue && Ground.Value < roof.point.y)
                    floor = Ground.Value;
                else
                    return false;
                if (roof.point.y - floor < HeadRoom)
                    return false;
                height = roof.point.y - 0.05f;
                return true;
            }

            // Scans [x0, x1] x [z0, z1] of the frame and adds the cover's triangles, their vertices through toOutput (from
            // world space). Returns the square metres covered (rounded), or -1 if cancelled.
            public int Run(float x0, float z0, float x1, float z1, Matrix4x4 toOutput, List<Vector3> vertices, List<int> triangles, string progress)
            {
                int nx = Mathf.Max(1, Mathf.CeilToInt((x1 - x0) / Cell)), nz = Mathf.Max(1, Mathf.CeilToInt((z1 - z0) / Cell));
                var centre = new float[nx, nz];
                var corner = new float[nx + 1, nz + 1];
                for (int iz = 0; iz <= nz; iz++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Rain Cover", progress, iz / (float)(nz + 1) * 0.6f))
                        return -1;
                    for (int ix = 0; ix <= nx; ix++)
                    {
                        corner[ix, iz] = RoofAt(x0 + ix * Cell, z0 + iz * Cell, out var c) ? c : float.NaN;
                        if (ix < nx && iz < nz)
                            centre[ix, iz] = Covered(x0 + (ix + 0.5f) * Cell, z0 + (iz + 0.5f) * Cell, out var h) ? h : float.NaN;
                    }
                }

                bool IsCovered(int ix, int iz) => ix >= 0 && iz >= 0 && ix < nx && iz < nz && !float.IsNaN(centre[ix, iz]);
                bool Edge(int ix, int iz) => !IsCovered(ix - 1, iz) || !IsCovered(ix + 1, iz) || !IsCovered(ix, iz - 1) || !IsCovered(ix, iz + 1);
                float area = 0f;
                float subCell = Cell / Sub;
                // a corner follows the roof (a slope) when its own hit is near the cell's: within what a 40-degree pitch
                // rises over half a cell's diagonal; past that it's another surface (the ground past an eave, a step)
                float near = Cell * 0.6f;
                for (int iz = 0; iz < nz; iz++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Rain Cover", progress, 0.6f + iz / (float)nz * 0.4f))
                        return -1;
                    int runStart = -1;
                    float runHeight = 0f;
                    for (int ix = 0; ix <= nx; ix++)
                    {
                        bool flat = false;
                        float h = 0f, c00 = 0f, c01 = 0f, c11 = 0f, c10 = 0f;
                        bool whole = ix < nx && IsCovered(ix, iz) && !Edge(ix, iz);
                        if (whole)
                        {
                            h = centre[ix, iz];
                            float Corner(int cx, int cz) => !float.IsNaN(corner[cx, cz]) && Mathf.Abs(corner[cx, cz] - h) <= near ? corner[cx, cz] : h;
                            c00 = Corner(ix, iz); c01 = Corner(ix, iz + 1); c11 = Corner(ix + 1, iz + 1); c10 = Corner(ix + 1, iz);
                            flat = Mathf.Abs(c00 - h) < 0.02f && Mathf.Abs(c01 - h) < 0.02f && Mathf.Abs(c11 - h) < 0.02f && Mathf.Abs(c10 - h) < 0.02f;
                        }
                        // flat cells in a row at one height become one quad
                        bool continues = whole && flat && runStart >= 0 && Mathf.Abs(h - runHeight) < 0.02f;
                        if (runStart >= 0 && !continues)
                        {
                            Quad(x0 + runStart * Cell, x0 + ix * Cell, z0 + iz * Cell, z0 + (iz + 1) * Cell, runHeight, runHeight, runHeight, runHeight, toOutput, vertices, triangles);
                            runStart = -1;
                        }
                        if (ix >= nx)
                            break;
                        if (whole && flat)
                        {
                            if (runStart < 0)
                            {
                                runStart = ix;
                                runHeight = h;
                            }
                            area += Cell * Cell;
                        }
                        else if (whole)
                        {
                            // a slope: the quad's corners on the roof
                            Quad(x0 + ix * Cell, x0 + (ix + 1) * Cell, z0 + iz * Cell, z0 + (iz + 1) * Cell, c00, c01, c11, c10, toOutput, vertices, triangles);
                            area += Cell * Cell;
                        }
                        else if (IsCovered(ix, iz) || IsCovered(ix - 1, iz) || IsCovered(ix + 1, iz) || IsCovered(ix, iz - 1) || IsCovered(ix, iz + 1))
                        {
                            // along an edge, in and out: scanned again finer, so a roof at an angle to the grid is followed
                            for (int sz = 0; sz < Sub; sz++)
                                for (int sx = 0; sx < Sub; sx++)
                                {
                                    float ax = x0 + ix * Cell + sx * subCell, az = z0 + iz * Cell + sz * subCell;
                                    if (!Covered(ax + subCell * 0.5f, az + subCell * 0.5f, out var sh))
                                        continue;
                                    Quad(ax, ax + subCell, az, az + subCell, sh, sh, sh, sh, toOutput, vertices, triangles);
                                    area += subCell * subCell;
                                }
                        }
                    }
                }
                return area <= 0f ? 0 : Mathf.Max(1, Mathf.RoundToInt(area));
            }

            // a quad over frame [xa, xb] x [za, zb] at the corner heights given (world), facing up
            private void Quad(float xa, float xb, float za, float zb, float h00, float h01, float h11, float h10, Matrix4x4 toOutput, List<Vector3> v, List<int> t)
            {
                Vector3 P(float x, float z, float y)
                {
                    var w = World(x, z);
                    w.y = y;
                    return toOutput.MultiplyPoint3x4(w);
                }
                AddQuad(v, t, P(xa, za, h00), P(xa, zb, h01), P(xb, zb, h11), P(xb, za, h10));
            }
        }

        private static void AddQuad(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i = v.Count;
            v.Add(a);
            v.Add(b);
            v.Add(c);
            v.Add(d);
            // facing up: the game raycasts onto it from above
            t.Add(i); t.Add(i + 1); t.Add(i + 2);
            t.Add(i); t.Add(i + 2); t.Add(i + 3);
        }

        // ------------------------------------------------------------ Build Map

        // With every map scene open, before Prepare: what the game needs of its WeatherObstacle (see the top). Returns
        // warnings.
        internal static List<string> PrepareMap(List<Scene> scenes)
        {
            var warnings = new List<string>();
            var obstacles = scenes.SelectMany(s => s.GetRootGameObjects()).SelectMany(r => r.GetComponentsInChildren<WeatherObstacle>(true)).ToList();
            foreach (var o in obstacles)
            {
                var old = o.transform.Find(GatheredName);
                if (old != null)
                    Object.DestroyImmediate(old.gameObject);
            }

            // the DryPlanes the game would miss: outside the obstacle, and in the raid (active, not editor-only)
            var loose = scenes.SelectMany(s => s.GetRootGameObjects()).SelectMany(r => r.GetComponentsInChildren<DryPlane>(true))
                .Where(p => p.GetComponentInParent<WeatherObstacle>(true) == null && p.gameObject.activeInHierarchy && p.Mesh != null && !EditorOnly(p.transform)).ToList();
            if (obstacles.Count == 0 && loose.Count == 0)
                return warnings;

            WeatherObstacle obstacle;
            if (obstacles.Count == 0)
            {
                // covers only on objects: the map's obstacle is made to hold them
                var root = new GameObject(RootName) { layer = LayerMask.NameToLayer("Triggers") };
                SceneManager.MoveGameObjectToScene(root, scenes[0]);
                obstacle = root.AddComponent<WeatherObstacle>();
            }
            else
            {
                obstacle = obstacles[0];
                if (obstacles.Count > 1)
                    warnings.Add($"The map has {obstacles.Count} rain covers (WeatherObstacle: {string.Join(", ", obstacles.Select(o => o.gameObject.scene.name + "/" + o.name))}); the game uses one, " +
                                 $"so only {obstacle.gameObject.scene.name}/{obstacle.name} counts. Remove the others or regenerate (Tarkov SDK > Generation > Geometry > Rain Cover)");
            }

            // its own transform is applied on top of the world-space mesh: back to the origin, its planes kept where they are
            var t = obstacle.transform;
            if (t.parent != null || t.position != Vector3.zero || t.rotation != Quaternion.identity || t.localScale != Vector3.one)
            {
                var children = t.Cast<Transform>().ToList();
                foreach (var c in children)
                    c.SetParent(null, true);
                t.SetParent(null, false);
                t.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                t.localScale = Vector3.one;
                foreach (var c in children)
                    c.SetParent(t, true);
                warnings.Add($"{obstacle.gameObject.scene.name}: the rain cover '{obstacle.name}' was moved, turned, scaled or under another object; the game would have put its cover " +
                             "off where it is, so it's back at the origin with its dry planes kept in place");
            }

            int skewed = 0, unreadable = 0;
            if (loose.Count > 0)
            {
                var gathered = new GameObject(GatheredName) { layer = obstacle.gameObject.layer };
                gathered.transform.SetParent(t, false);
                foreach (var plane in loose)
                {
                    var copy = new GameObject(TarkovTriggeredSpawns.PathOf(plane.transform).Replace('/', '>')) { layer = gathered.layer };
                    copy.transform.SetParent(gathered.transform, false);
                    var source = plane.transform;
                    copy.transform.SetPositionAndRotation(source.position, source.rotation);
                    copy.transform.localScale = source.lossyScale;
                    var mesh = plane.Mesh;
                    // a plane under a non-uniformly scaled, turned parent has a skew no transform can carry: its mesh goes
                    // in baked (saved into the scene)
                    if (!Same(copy.transform.localToWorldMatrix, source.localToWorldMatrix) && mesh.isReadable)
                    {
                        copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                        copy.transform.localScale = Vector3.one;
                        mesh = Baked(mesh, source.localToWorldMatrix);
                        skewed++;
                    }
                    if (!mesh.isReadable)
                        unreadable++;
                    copy.AddComponent<DryPlane>().Mesh = mesh;
                }
                Debug.Log($"[Tarkov SDK] Rain cover: {loose.Count} dry plane(s) from objects gathered into {obstacle.gameObject.scene.name}/{obstacle.name}" +
                          (skewed > 0 ? $" ({skewed} baked: skewed by their parents' scale)" : ""));
            }
            unreadable += obstacle.GetComponentsInChildren<DryPlane>(true).Count(p => p.Mesh != null && !p.Mesh.isReadable && !(p.transform.parent != null && p.transform.parent.name == GatheredName));
            if (unreadable > 0)
                warnings.Add($"{unreadable} dry plane(s) use a mesh without Read/Write enabled; the game can't combine them, so the rain cover fails in the raid. " +
                             "Turn on Read/Write in their model's import settings, or make the cover with Rain Cover For This Object");
            return warnings;
        }

        private static bool EditorOnly(Transform t)
        {
            for (var x = t; x != null; x = x.parent)
                if (x.CompareTag("EditorOnly"))
                    return true;
            return false;
        }

        private static bool Same(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
                if (Mathf.Abs(a[i] - b[i]) > 1e-3f * Mathf.Max(1f, Mathf.Abs(b[i])))
                    return false;
            return true;
        }

        private static Mesh Baked(Mesh source, Matrix4x4 matrix)
        {
            var vertices = source.vertices.Select(v => matrix.MultiplyPoint3x4(v)).ToArray();
            var mesh = new Mesh { name = source.name + " (world)", indexFormat = source.indexFormat, vertices = vertices };
            mesh.subMeshCount = 1;
            mesh.SetTriangles(Enumerable.Range(0, source.subMeshCount).SelectMany(s => source.GetTriangles(s)).ToArray(), 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.InSelectionHierarchy)]
        private static void DrawDryPlane(DryPlane plane, GizmoType type)
        {
            if (plane.Mesh == null)
                return;
            Gizmos.color = new Color(0.3f, 0.6f, 1f, 0.35f);
            Gizmos.matrix = plane.transform.localToWorldMatrix;
            Gizmos.DrawMesh(plane.Mesh);
            Gizmos.color = new Color(0.3f, 0.6f, 1f, 0.9f);
            Gizmos.DrawWireMesh(plane.Mesh);
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
