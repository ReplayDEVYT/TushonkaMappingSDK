using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // ProBuilder (com.unity.probuilder, Tarkov SDK > Install Packages > ProBuilder) for map geometry, all by reflection so the SDK
    // compiles without it. Nothing special is needed at Build Map: BuildPipeline.BuildAssetBundles runs ProBuilder's own
    // [PostProcessScene] (UnityScenePostProcessor), which saves each ProBuilderMesh's mesh into the built scene and strips
    // ProBuilder's scripts (the game has no Unity.ProBuilder assembly), as for a player build. What the SDK adds:
    //  - SetUp: SDK_Default (the p0 SMap stand-in) as ProBuilder's default material (EditorMaterialUtility's
    //    "mesh.userMaterial" Pref; ProBuilder's own default is a package shader, bundled as-is and not lit like the game's surfaces), and a vertex
    //    colour palette for the game's vertex-paint shader.
    //  - Vertex painting: "Custom/Vert Paint Shader Solid" (stand-in "Tarkov/Custom/Vert Paint Shader Solid", swapped by
    //    the client) weights its three layers by vertex colour R, G, B (times their height maps), so ProBuilder's Vertex
    //    Colors tool paints layers: pure red is layer 0, green layer 1, blue layer 2, mixes blend.
    //  - Build Map warns about renderers still on ProBuilder's package shaders (they'd be bundled as they are, unlike the map's p0 surfaces).
    public static class TarkovProBuilder
    {
        private const string PalettePath = "Assets/TarkovSDK/ProBuilder/Tarkov Vertex Paint Layers.asset";
        public const string VertexPaintShader = "Tarkov/Custom/Vert Paint Shader Solid";

        private static Type Runtime(string name) => Type.GetType($"UnityEngine.ProBuilder.{name}, Unity.ProBuilder");
        private static Type Editor(string name) => Type.GetType($"UnityEditor.ProBuilder.{name}, Unity.ProBuilder.Editor");

        public static Type MeshType => Runtime("ProBuilderMesh");
        public static bool Available => MeshType != null && Editor("EditorMaterialUtility") != null;

        [MenuItem("Tarkov SDK/Quick Fixes/Set Up ProBuilder For Tarkov", priority = 206)]
        private static void SetUpMenu() => EditorApplication.delayCall += () =>
        {
            if (!Available)
            {
                if (EditorUtility.DisplayDialog("ProBuilder", "ProBuilder isn't installed. Install it now?", "Install", "Cancel"))
                    TarkovPackages.Install(TarkovPackages.ProBuilder, "5.2.4", "ProBuilder", false);
                return;
            }
            SetUp();
            int fixedCount = FixSceneMaterials();
            Debug.Log($"[Tarkov SDK] ProBuilder set up for Tarkov: new shapes get SDK_Default, the Vertex Colors window has the vertex-paint layers" +
                      (fixedCount > 0 ? $"; {fixedCount} renderer(s) moved off ProBuilder's shaders onto SDK_Default." : "."));
        };

        public static void SetUp()
        {
            if (!Available)
                return;
            var material = TarkovSceneTools.DefaultSurfaceMaterial();
            if (material != null && !SetPref(Editor("EditorMaterialUtility"), "s_DefaultMaterial", material))
                Debug.LogWarning("[Tarkov SDK] Couldn't set ProBuilder's default material (a different ProBuilder version?); set it in Preferences > ProBuilder > Mesh Settings.");
            var palette = Palette();
            if (palette != null)
            {
                SetPref(Editor("VertexColorPalette"), "m_PreviousColorPalette", AssetDatabase.GetAssetPath(palette));
                // an open Vertex Colors window keeps its own palette until told otherwise
                var window = Editor("VertexColorPalette");
                var instance = window?.GetField("s_Instance", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
                // (none open: it reads the pref above when it opens)
                if (instance != null)
                    window.GetField("m_ColorPalette", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(instance, palette);
            }
        }

        // a ProBuilder Pref<T> (settings-manager UserSetting<T>): SetValue(value, saveProjectSettingsImmediately)
        private static bool SetPref(Type owner, string field, object value)
        {
            var pref = owner?.GetField(field, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null);
            var set = pref?.GetType().GetMethods().FirstOrDefault(m => m.Name == "SetValue" && m.GetParameters().Length == 2);
            if (set == null)
                return false;
            set.Invoke(pref, new[] { value, true });
            return true;
        }

        // ProBuilder's ColorPalette (internal) with the vertex-paint layers
        private static ScriptableObject Palette()
        {
            var type = Runtime("ColorPalette");
            if (type == null)
                return null;
            var palette = AssetDatabase.LoadAssetAtPath(PalettePath, type) as ScriptableObject;
            if (palette == null)
            {
                TarkovSceneTools.EnsureFolder(Path.GetDirectoryName(PalettePath));
                palette = ScriptableObject.CreateInstance(type);
                AssetDatabase.CreateAsset(palette, PalettePath);
            }
            var colors = new[]
            {
                new Color(1f, 0f, 0f), new Color(0f, 1f, 0f), new Color(0f, 0f, 1f),
                new Color(1f, 1f, 0f), new Color(0f, 1f, 1f), new Color(1f, 0f, 1f), new Color(1f, 1f, 1f),
            };
            var so = new SerializedObject(palette);
            var list = so.FindProperty("m_Colors");
            if (list == null)
                return palette;
            list.arraySize = colors.Length;
            for (int i = 0; i < colors.Length; i++)
                list.GetArrayElementAtIndex(i).colorValue = colors[i];
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(palette);
            AssetDatabase.SaveAssets();
            return palette;
        }

        private static bool PackageShader(Material material) =>
            material != null && material.shader != null && material.shader.name.StartsWith("ProBuilder/");

        // renderers in the open scenes on ProBuilder's package shaders -> SDK_Default
        public static int FixSceneMaterials()
        {
            var replacement = TarkovSceneTools.DefaultSurfaceMaterial();
            if (replacement == null)
                return 0;
            int count = 0;
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                if (!materials.Any(PackageShader))
                    continue;
                Undo.RecordObject(renderer, "Replace ProBuilder materials");
                renderer.sharedMaterials = materials.Select(m => PackageShader(m) ? replacement : m).ToArray();
                count++;
            }
            return count;
        }

        // ------------------------------------------------------------ vertex paint material

        [MenuItem("Tarkov SDK/Tools/Geometry/New Vertex Paint Material (On Selected)", priority = 124)]
        private static void NewVertexPaintMenu() => EditorApplication.delayCall += () =>
        {
            var material = NewVertexPaintMaterial(SceneManager.GetActiveScene());
            if (material == null)
                return;
            var renderers = Selection.gameObjects.SelectMany(g => g.GetComponentsInChildren<Renderer>()).ToList();
            foreach (var renderer in renderers)
            {
                Undo.RecordObject(renderer, "Vertex paint material");
                renderer.sharedMaterials = Enumerable.Repeat(material, Mathf.Max(1, renderer.sharedMaterials.Length)).ToArray();
            }
            Selection.activeObject = material;
            Debug.Log($"[Tarkov SDK] {AssetDatabase.GetAssetPath(material)}" + (renderers.Count > 0 ? $" put on {renderers.Count} renderer(s)." : ".") +
                      " Set each layer's textures; paint red / green / blue vertex colours (ProBuilder's Vertex Colors) for layers 0 / 1 / 2.");
        };

        // The game's vertex-painted ground shader with three tinted layers (concrete, dirt, grass) so painting shows at once.
        public static Material NewVertexPaintMaterial(Scene scene)
        {
            var shader = Shader.Find(VertexPaintShader);
            if (shader == null)
            {
                Debug.LogError($"[Tarkov SDK] Stand-in shader '{VertexPaintShader}' not found.");
                return null;
            }
            var folder = string.IsNullOrEmpty(scene.path) ? "Assets/Materials"
                : $"{Path.GetDirectoryName(scene.path).Replace('\\', '/')}/{Path.GetFileNameWithoutExtension(scene.path)}_Materials";
            TarkovSceneTools.EnsureFolder(folder);
            var material = new Material(shader);
            material.SetColor("_Color0", new Color(0.55f, 0.55f, 0.53f));
            material.SetColor("_Color1", new Color(0.42f, 0.33f, 0.24f));
            material.SetColor("_Color2", new Color(0.32f, 0.40f, 0.20f));
            material.SetFloat("_BlendStrength", 4f);
            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/VertexPaint.mat");
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        // ------------------------------------------------------------ Build Map

        public static void Check(Scene scene, GameObject[] roots, TarkovScenePreparer.Report report)
        {
            var onPackageShaders = roots.SelectMany(r => r.GetComponentsInChildren<Renderer>(true))
                .Where(r => !r.CompareTag("EditorOnly") && r.sharedMaterials.Any(PackageShader)).ToList();
            if (onPackageShaders.Count > 0)
                report.Warnings.Add($"{scene.name}: {onPackageShaders.Count} renderer(s) use ProBuilder's own shaders " +
                                    $"({string.Join(", ", onPackageShaders.Take(5).Select(r => r.name))}); Quick Fixes > Set Up ProBuilder For Tarkov moves them to SDK_Default");
            var meshType = MeshType;
            if (meshType == null)
                return;
            // a ProBuilder object without a solid collider is invisible to bullets and walked through; its LowPoly /
            // HighPoly colliders are often children of it, which is fine
            var noCollider = roots.SelectMany(r => r.GetComponentsInChildren(meshType, true)).Cast<Component>()
                .Where(c => !c.CompareTag("EditorOnly") && !c.GetComponentsInChildren<Collider>(true).Any(col => !col.isTrigger)).ToList();
            if (noCollider.Count > 0)
                report.Warnings.Add($"{scene.name}: {noCollider.Count} ProBuilder object(s) have no collider ({string.Join(", ", noCollider.Take(5).Select(c => c.name))}); " +
                                    "give them LowPolyCollider + HighPolyCollider colliders (or one on the Terrain layer)");
        }
    }
}
