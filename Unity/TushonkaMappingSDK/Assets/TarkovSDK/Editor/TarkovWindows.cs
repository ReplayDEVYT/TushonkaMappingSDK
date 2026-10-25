using System.Collections.Generic;
using System.IO;
using EFT.Ballistics;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Breakable windows, set up like retail ones: the glass object carries its mesh, a non-convex
    // MeshCollider on the TransparentCollider layer, a glass BallisticCollider and the WindowBreaker.
    //
    // WindowBreaker needs geometry BSG's editor precomputes from the glass mesh (WindowBreaker.method_5,
    // not run in the game): which mesh axes span the pane (WindowBreakingConfig.DetectPlane: the
    // thinnest is the normal), the pane's two surfaces, the UV mapping of its corners and its 2D box.
    // Setup ports that exactly (checked against retail windows), so any glass mesh works: GameObject >
    // GameObject > Tarkov SDK > Interactables > Breakable Window makes a pane, and Build Map sets up every WindowBreaker.
    //
    // The crack patterns are the game's own (Customs' BrokenWindow config, shipped in Windows/BrokenWindow.json).
    public static class TarkovWindows
    {
        private const string Folder = "Assets/TarkovSDK/Windows";
        private const string ConfigPath = Folder + "/BrokenWindow.asset";
        private const string MaterialPath = "Assets/TarkovSDK/Materials/SDK_Glass.mat";
        private const float Thickness = 0.004f;

        [MenuItem("GameObject/Tarkov SDK/Interactables/Breakable Window", priority = 31)]
        private static void MenuWindow(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateWindow(p, 1.2f, 1f).gameObject);

        // a pane width x height, its pivot in the middle, facing -Z
        public static WindowBreaker CreateWindow(Transform parent, float width, float height)
        {
            var go = new GameObject("BreakableWindow") { layer = LayerMask.NameToLayer("TransparentCollider") };
            if (parent != null)
                go.transform.SetParent(parent, false);
            var mesh = PaneMesh(width, height);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = GlassMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            var ballistic = go.AddComponent<BallisticCollider>();
            ballistic._typeOfMaterial = MaterialType.Glass;
            ballistic.SurfaceSound = 0; // as retail glass: the window plays its own breaking sounds
            var window = go.AddComponent<WindowBreaker>();
            Setup(window, out _);
            // Id is assigned when the map is built
            return window;
        }

        // fills the window's references and its precomputed geometry; false (with why) when it can't
        public static bool Setup(WindowBreaker window, out string problem)
        {
            problem = null;
            if (window.Renderer == null)
                window.Renderer = window.GetComponent<Renderer>();
            if (window.GlassBallisticCollider == null)
                window.GlassBallisticCollider = window.GetComponent<BallisticCollider>();
            if (window.Material == null && window.Renderer != null)
                window.Material = window.Renderer.sharedMaterial;
            if (window.BrokenWindow == null)
                window.BrokenWindow = Config();
            EditorUtility.SetDirty(window);

            var mesh = window.Renderer != null && window.Renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
            if (mesh == null || mesh.vertexCount == 0)
                return Fail(out problem, "no glass mesh (the WindowBreaker needs a MeshRenderer and MeshFilter on its object)");
            var vertices = mesh.vertices;
            var uv = mesh.uv;
            if (uv.Length != vertices.Length)
                return Fail(out problem, "the glass mesh has no UVs");
            if (window.GlassBallisticCollider == null)
                return Fail(out problem, "no BallisticCollider on the glass, so bullets can't break it");

            ComputeGeometry(window, vertices, uv);
            return true;
        }

        private static bool Fail(out string problem, string why)
        {
            problem = why;
            return false;
        }

        // WindowBreaker.method_5
        private static void ComputeGeometry(WindowBreaker w, Vector3[] v, Vector2[] uv)
        {
            var bounds = new Bounds(v[0], Vector3.zero);
            foreach (var p in v)
                bounds.Encapsulate(p);
            DetectPlane(bounds.size, out w.AxisX, out w.AxisY, out w.AxisZ);

            float mid = (bounds.min[w.AxisZ] + bounds.max[w.AxisZ]) * 0.5f;
            float half = Mathf.Max((bounds.max[w.AxisZ] - mid) * w.ThicknessMultyplier, w.MinThickness);
            w.ZSurfs = new Vector2(mid - half, mid + half);

            // three corners of the pane's near surface: lowest and highest x+y, highest x-y
            float sumMin = v[0][w.AxisX] + v[0][w.AxisY], sumMax = sumMin, diffMax = v[0][w.AxisX] - v[0][w.AxisY];
            int iMin = 0, iMax = 0, iDiff = 0;
            for (int i = 0; i < v.Length; i++)
            {
                if (v[i][w.AxisZ] > mid)
                    continue;
                float sum = v[i][w.AxisX] + v[i][w.AxisY], diff = v[i][w.AxisX] - v[i][w.AxisY];
                if (sum > sumMax) { iMax = i; sumMax = sum; }
                if (sum < sumMin) { iMin = i; sumMin = sum; }
                if (diff > diffMax) { iDiff = i; diffMax = diff; }
            }

            // the pane's axes swap when its UVs run the other way (NeedToSwapCalc)
            var a = Plane2D(v[iDiff], w) - Plane2D(v[iMin], w);
            var b = uv[iDiff] - uv[iMin];
            w.NeedToSwap = (Mathf.Abs(a.x) > Mathf.Abs(a.y)) != (Mathf.Abs(b.x) > Mathf.Abs(b.y));
            if (w.NeedToSwap)
                (w.AxisX, w.AxisY) = (w.AxisY, w.AxisX);

            var p0 = Plane2D(v[iMin], w);
            var p1 = Plane2D(v[iMax], w);
            var span = p1 - p0;
            var uvSpan = uv[iMax] - uv[iMin];
            var x = Vector3.zero;
            x[w.AxisX] = 1f;
            var y = Vector3.zero;
            y[w.AxisY] = 1f;
            w.Rotation = Quaternion.LookRotation(Vector3.Cross(x, y), y);
            w.UvMult = new Vector2(uvSpan.x / span.x, uvSpan.y / span.y);
            w.UvAdd = uv[iMin] - Vector2.Scale(p0, w.UvMult);
            var min = Vector2.Min(p0, p1);
            var max = Vector2.Max(p0, p1);
            w.Box = new Vector4(min.x, min.y, max.x, max.y);
        }

        private static Vector2 Plane2D(Vector3 p, WindowBreaker w) => new Vector2(p[w.AxisX], p[w.AxisY]);

        // WindowBreakingConfig.DetectPlane: the thinnest axis is the normal, the other two span the pane
        private static void DetectPlane(Vector3 size, out int axisX, out int axisY, out int axisZ)
        {
            axisZ = 0;
            for (int i = 1; i < 3; i++)
                if (size[i] < size[axisZ])
                    axisZ = i;
            (axisX, axisY) = axisZ == 0 ? (1, 2) : axisZ == 1 ? (0, 2) : (0, 1);
        }

        // ------------------------------------------------------------ assets

        // the game's crack patterns, built once from the shipped JSON
        public static WindowBreakingConfig Config()
        {
            var config = AssetDatabase.LoadAssetAtPath<WindowBreakingConfig>(ConfigPath);
            if (config != null)
                return config;
            config = ScriptableObject.CreateInstance<WindowBreakingConfig>();
            config.name = "BrokenWindow";
            JsonUtility.FromJsonOverwrite(File.ReadAllText(Folder + "/BrokenWindow.json"), config);
            AssetDatabase.CreateAsset(config, ConfigPath);
            return config;
        }

        private static Material GlassMaterial()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material != null)
                return material;
            material = new Material(Shader.Find("Tarkov/Global Fog/Transparent Reflective Specular")) { name = "SDK_Glass" };
            material.SetColor("_Color", new Color(0.75f, 0.85f, 0.9f, 0.25f));
            material.SetFloat("_Smoothness", 0.9f);
            material.SetFloat("_Metallic", 0.1f);
            AssetDatabase.CreateAsset(material, MaterialPath);
            return material;
        }

        // a thin pane: a front face (-Z) and a back face (+Z), UVs 0-1 across, shared per size
        private static Mesh PaneMesh(float width, float height)
        {
            var path = $"{Folder}/Meshes/Glass_{width:0.##}x{height:0.##}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
                return existing;
            float x = width * 0.5f, y = height * 0.5f, z = Thickness * 0.5f;
            var mesh = new Mesh { name = Path.GetFileNameWithoutExtension(path) };
            mesh.SetVertices(new List<Vector3>
            {
                new Vector3(-x, -y, -z), new Vector3(-x, y, -z), new Vector3(x, y, -z), new Vector3(x, -y, -z),
                new Vector3(x, -y, z), new Vector3(x, y, z), new Vector3(-x, y, z), new Vector3(-x, -y, z),
            });
            mesh.SetUVs(0, new List<Vector2>
            {
                new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f),
                new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f),
            });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 }, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }
    }
}
