using System.Collections.Generic;
using System.IO;
using EFT.EnvironmentEffect;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Rain cover for the full weather system. The game decides whether the camera is under rain
    // (screen drops, wet screen, puddle ripples) by raycasting down onto WeatherObstacle's collider,
    // which it builds from the meshes of the DryPlane components under it: anywhere with a dry
    // plane above the camera counts as covered.
    //
    // The generator scans the map from above on a grid. A cell is covered when the first surface
    // the scan hits has more solid geometry under it with head room (a roof over a floor); a dry
    // plane quad is placed at that roof's height. Open ground has nothing under it and stays wet.
    public static class TarkovRainCover
    {
        public const string RootName = "RainCover";
        private const float CellSize = 1.5f;
        private const float HeadRoom = 1.8f;

        [MenuItem("Tarkov SDK/Tools/Generate/Rain Cover", priority = 223)]
        public static void GenerateMenu()
        {
            var scene = SceneManager.GetActiveScene();
            var root = Generate(scene);
            if (root != null)
            {
                Selection.activeGameObject = root;
                EditorSceneManager.MarkSceneDirty(scene);
            }
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

        // bounds of every solid collider in the scene (that include accepts): the area to scan
        internal static bool SolidBounds(Scene scene, int mask, out Bounds bounds, System.Func<Collider, bool> include = null)
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

        public static GameObject Generate(Scene scene)
        {
            int mask = SolidMask;
            if (!SolidBounds(scene, mask, out var bounds))
            {
                EditorUtility.DisplayDialog("Rain Cover", "The scene has no solid colliders to scan.", "OK");
                return null;
            }

            Physics.SyncTransforms();
            float top = bounds.max.y + 10f;
            int nx = Mathf.CeilToInt(bounds.size.x / CellSize), nz = Mathf.CeilToInt(bounds.size.z / CellSize);
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            int covered = 0;
            try
            {
                for (int iz = 0; iz < nz; iz++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Rain Cover", "Scanning for roofs", iz / (float)nz))
                        return null;
                    float z0 = bounds.min.z + iz * CellSize;
                    // merge neighbouring covered cells of one row at the same height into one quad
                    int runStart = -1;
                    float runHeight = 0f;
                    for (int ix = 0; ix <= nx; ix++)
                    {
                        float height = 0f;
                        bool cell = ix < nx && Covered(new Vector3(bounds.min.x + (ix + 0.5f) * CellSize, top, z0 + CellSize * 0.5f), mask, out height);
                        if (cell)
                            covered++;
                        bool continues = cell && runStart >= 0 && Mathf.Abs(height - runHeight) < 0.3f;
                        if (runStart >= 0 && !continues)
                        {
                            AddQuad(vertices, triangles, bounds.min.x + runStart * CellSize, bounds.min.x + ix * CellSize, z0, z0 + CellSize, runHeight);
                            runStart = -1;
                        }
                        if (cell && runStart < 0)
                        {
                            runStart = ix;
                            runHeight = height;
                        }
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            var old = Find(scene);
            if (old != null)
                Undo.DestroyObjectImmediate(old);
            if (covered == 0)
            {
                Debug.Log("[Tarkov SDK] Rain cover: nothing on the map is covered; the whole map counts as under rain.");
                return null;
            }

            var cover = CreateCover(scene, vertices, triangles, out var path);
            Debug.Log($"[Tarkov SDK] Rain cover: {covered} covered cell(s) of {CellSize} m, {vertices.Count / 4} dry plane quad(s), mesh saved to {path}");
            return cover;
        }

        // one dry plane above the whole area, so nowhere counts as under the sky (Entirely Indoors maps)
        internal static GameObject GenerateWholeMap(Scene scene, Bounds bounds)
        {
            var old = Find(scene);
            if (old != null)
                Undo.DestroyObjectImmediate(old);
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            AddQuad(vertices, triangles, bounds.min.x, bounds.max.x, bounds.min.z, bounds.max.z, bounds.max.y);
            return CreateCover(scene, vertices, triangles, out _);
        }

        private static GameObject CreateCover(Scene scene, List<Vector3> vertices, List<int> triangles, out string path)
        {
            var mesh = new Mesh { name = scene.name + "_RainCover" };
            if (vertices.Count > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            path = Path.Combine(Path.GetDirectoryName(scene.path) ?? "Assets", scene.name + "_RainCover.asset").Replace('\\', '/');
            if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null)
                AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);

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

        // the first surface hit from above, if more solid geometry lies under it with head room
        private static bool Covered(Vector3 from, int mask, out float height)
        {
            height = 0f;
            if (!Physics.Raycast(from, Vector3.down, out var roof, 2000f, mask, QueryTriggerInteraction.Ignore))
                return false;
            if (!Physics.Raycast(roof.point + Vector3.down * 0.05f, Vector3.down, out var floor, 500f, mask, QueryTriggerInteraction.Ignore))
                return false;
            if (roof.point.y - floor.point.y < HeadRoom)
                return false;
            height = roof.point.y - 0.05f;
            return true;
        }

        private static void AddQuad(List<Vector3> v, List<int> t, float x0, float x1, float z0, float z1, float y)
        {
            int i = v.Count;
            v.Add(new Vector3(x0, y, z0));
            v.Add(new Vector3(x0, y, z1));
            v.Add(new Vector3(x1, y, z1));
            v.Add(new Vector3(x1, y, z0));
            // facing up: the game raycasts onto it from above
            t.Add(i); t.Add(i + 1); t.Add(i + 2);
            t.Add(i); t.Add(i + 2); t.Add(i + 3);
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
