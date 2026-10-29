using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Surface = BaseBallistic.ESurfaceSound;

namespace TarkovSDK.Editor
{
    // Unity terrains as the game's (retail Customs' custom_Terrain, level17: Terrain layer, TerrainCollider and the
    // game's TerrainBallistic on the terrain object). A hit or footstep on a collider asks its BaseBallistic for the
    // material at the point; TerrainBallistic answers with the terrain layer that's strongest there
    // (TerrainTextureMixData.GetMainTexture over the splat maps) -> TerrainMaterials[layer] -> one of six shared
    // BallisticColliders it makes from EFTHardSettings' presets: Asphalt, Soil, GrassHigh, Concrete, Gravel, Water,
    // whose surface sounds are Asphalt, Soil, Grass, Concrete, Gravel, Puddle. Any other surface falls back to the first
    // (asphalt), so only those six are offered. The surface of each TerrainLayer asset is kept in its importer's
    // userData, so a "Grass" layer is grass on every terrain, whatever its index; Build Map writes TerrainMaterials.
    //
    // Retail terrains render with MicroSplat shaders generated per map; the game also always loads Unity's own
    // "Nature/Terrain/Diffuse" (with its AddPass/Base shaders) and the detail/tree billboard shaders from
    // globalgamemanagers.assets, so SDK terrains use SDK_Terrain (Nature/Terrain/Diffuse), and painted grass details work.
    public static class TarkovTerrains
    {
        private const string MaterialPath = "Assets/TarkovSDK/Materials/SDK_Terrain.mat";
        public const string TerrainShader = "Nature/Terrain/Diffuse";
        private const string UserDataKey = "TarkovSurface=";

        public static readonly Surface[] Surfaces = { Surface.Soil, Surface.Grass, Surface.Gravel, Surface.Asphalt, Surface.Concrete, Surface.Puddle };
        public static readonly string[] SurfaceLabels = { "Soil (dirt, mud, sand)", "Grass", "Gravel (rock, rubble)", "Asphalt", "Concrete", "Water (puddles)" };

        [MenuItem("GameObject/Tarkov SDK/Environment/Terrain", priority = 69)]
        private static void MenuTerrain(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => Create(p).gameObject);

        // A 256 x 256 m terrain with grass and dirt layers, set up for the game.
        public static Terrain Create(Transform parent)
        {
            var scene = parent != null ? parent.gameObject.scene : SceneManager.GetActiveScene();
            var folder = Folder(scene);
            var data = new TerrainData
            {
                heightmapResolution = 513,
                alphamapResolution = 512,
                baseMapResolution = 1024,
            };
            data.SetDetailResolution(1024, 32);
            data.size = new Vector3(256f, 60f, 256f);
            AssetDatabase.CreateAsset(data, AssetDatabase.GenerateUniqueAssetPath($"{folder}/Terrain.asset"));
            data.terrainLayers = new[]
            {
                Layer(folder, "Grass", new Color(0.30f, 0.38f, 0.18f), Surface.Grass),
                Layer(folder, "Dirt", new Color(0.40f, 0.32f, 0.23f), Surface.Soil),
            };
            var go = Terrain.CreateTerrainGameObject(data);
            go.name = "Terrain";
            if (parent != null)
                go.transform.SetParent(parent, false);
            var terrain = go.GetComponent<Terrain>();
            SetUp(terrain);
            return terrain;
        }

        private static TerrainLayer Layer(string folder, string name, Color color, Surface surface)
        {
            var texturePath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}.png");
            File.WriteAllBytes(texturePath, NoiseTexture(color).EncodeToPNG());
            AssetDatabase.ImportAsset(texturePath);
            var layer = new TerrainLayer { diffuseTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath), tileSize = new Vector2(4f, 4f) };
            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}.terrainlayer");
            AssetDatabase.CreateAsset(layer, path);
            SetSurface(layer, surface);
            return layer;
        }

        // a placeholder texture: the colour with some grain, so tiling and blending show
        private static Texture2D NoiseTexture(Color color)
        {
            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGB24, false);
            var random = new System.Random(color.GetHashCode());
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float n = 0.85f + 0.3f * (float)random.NextDouble();
                    texture.SetPixel(x, y, new Color(color.r * n, color.g * n, color.b * n));
                }
            texture.Apply();
            return texture;
        }

        private static string Folder(Scene scene)
        {
            var folder = string.IsNullOrEmpty(scene.path) ? "Assets/Terrain"
                : $"{Path.GetDirectoryName(scene.path).Replace('\\', '/')}/{Path.GetFileNameWithoutExtension(scene.path)}_Terrain";
            TarkovSceneTools.EnsureFolder(folder);
            return folder;
        }

        public static Material TerrainMaterial()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material != null)
                return material;
            var shader = Shader.Find(TerrainShader);
            if (shader == null)
            {
                Debug.LogError($"[Tarkov SDK] Unity's '{TerrainShader}' shader wasn't found.");
                return null;
            }
            TarkovSceneTools.EnsureFolder(Path.GetDirectoryName(MaterialPath));
            material = new Material(shader) { name = "SDK_Terrain" };
            AssetDatabase.CreateAsset(material, MaterialPath);
            return material;
        }

        // ------------------------------------------------------------ surfaces

        public static Surface GetSurface(TerrainLayer layer)
        {
            if (layer == null)
                return Surface.Soil;
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(layer));
            var data = importer != null ? importer.userData ?? "" : "";
            int at = data.IndexOf(UserDataKey, StringComparison.Ordinal);
            if (at >= 0)
            {
                var value = new string(data.Substring(at + UserDataKey.Length).TakeWhile(char.IsLetter).ToArray());
                if (Enum.TryParse<Surface>(value, out var surface) && Surfaces.Contains(surface))
                    return surface;
            }
            return Guess(layer);
        }

        public static bool HasSurface(TerrainLayer layer)
        {
            var importer = layer != null ? AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(layer)) : null;
            return importer != null && (importer.userData ?? "").Contains(UserDataKey);
        }

        public static void SetSurface(TerrainLayer layer, Surface surface)
        {
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(layer));
            if (importer == null)
                return;
            var data = importer.userData ?? "";
            int at = data.IndexOf(UserDataKey, StringComparison.Ordinal);
            if (at >= 0)
            {
                int end = at + UserDataKey.Length;
                while (end < data.Length && char.IsLetter(data[end]))
                    end++;
                data = data.Remove(at, end - at);
            }
            importer.userData = (data.Trim(';', ' ') + (data.Trim(';', ' ').Length > 0 ? ";" : "") + UserDataKey + surface).Trim(';');
            importer.SaveAndReimport();
        }

        // from the layer's and its texture's names
        public static Surface Guess(TerrainLayer layer)
        {
            var name = ((layer.name ?? "") + " " + (layer.diffuseTexture != null ? layer.diffuseTexture.name : "")).ToLowerInvariant();
            bool Has(params string[] words) => words.Any(name.Contains);
            if (Has("grass", "moss", "lawn", "meadow", "leaves", "leaf", "forest", "field"))
                return Surface.Grass;
            if (Has("asphalt", "road", "tarmac"))
                return Surface.Asphalt;
            if (Has("concrete", "pavement", "cement", "sidewalk", "tile", "brick"))
                return Surface.Concrete;
            if (Has("gravel", "pebble", "rock", "stone", "rubble", "cliff"))
                return Surface.Gravel;
            if (Has("water", "puddle", "swamp"))
                return Surface.Puddle;
            return Surface.Soil;
        }

        // ------------------------------------------------------------ setup

        // Terrain layer, collider and TerrainBallistic (with TerrainMaterials from the layers); optionally SDK_Terrain.
        public static void SetUp(Terrain terrain, bool material = true)
        {
            var go = terrain.gameObject;
            Undo.RecordObject(go, "Set up terrain");
            go.layer = LayerMask.NameToLayer("Terrain");
            GameObjectUtility.SetStaticEditorFlags(go, GameObjectUtility.GetStaticEditorFlags(go) | StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);
            var collider = go.GetComponent<TerrainCollider>() ?? Undo.AddComponent<TerrainCollider>(go);
            if (collider.terrainData != terrain.terrainData)
            {
                Undo.RecordObject(collider, "Set up terrain");
                collider.terrainData = terrain.terrainData;
            }
            if (material && !GameHasShader(terrain.materialTemplate))
            {
                Undo.RecordObject(terrain, "Set up terrain");
                terrain.materialTemplate = TerrainMaterial();
            }
            Sync(terrain);
        }

        // writes TerrainMaterials from the layers' surfaces
        public static TerrainBallistic Sync(Terrain terrain)
        {
            var ballistic = terrain.GetComponent<TerrainBallistic>() ?? Undo.AddComponent<TerrainBallistic>(terrain.gameObject);
            var layers = terrain.terrainData != null ? terrain.terrainData.terrainLayers : new TerrainLayer[0];
            var surfaces = layers.Select(GetSurface).ToArray();
            if (ballistic.TerrainData != terrain.terrainData || ballistic.SurfaceSound != Surface.Soil || ballistic.TerrainMaterials == null || !ballistic.TerrainMaterials.SequenceEqual(surfaces))
            {
                Undo.RecordObject(ballistic, "Terrain surfaces");
                ballistic.TerrainData = terrain.terrainData;
                ballistic.SurfaceSound = Surface.Soil;
                ballistic.TerrainMaterials = surfaces;
                EditorUtility.SetDirty(ballistic);
            }
            return ballistic;
        }

        // shaders the raid has: Unity's Diffuse terrain shader (globalgamemanagers.assets) or an SDK stand-in
        public static bool GameHasShader(Material material) =>
            material != null && material.shader != null && (material.shader.name == TerrainShader || material.shader.name.StartsWith("Tarkov/"));

        public static bool IsSetUp(Terrain terrain) =>
            terrain.gameObject.layer == LayerMask.NameToLayer("Terrain") && terrain.GetComponent<TerrainCollider>() != null
            && terrain.GetComponent<TerrainBallistic>() != null && GameHasShader(terrain.materialTemplate);

        // ------------------------------------------------------------ Build Map

        public static void Prepare(Scene scene, GameObject[] roots, TarkovScenePreparer.Report report)
        {
            foreach (var terrain in roots.SelectMany(r => r.GetComponentsInChildren<Terrain>(true)).Where(t => !t.CompareTag("EditorOnly")))
            {
                if (terrain.terrainData == null)
                {
                    report.Errors.Add($"{scene.name}: terrain '{terrain.name}' has no Terrain Data");
                    continue;
                }
                if (terrain.gameObject.layer != LayerMask.NameToLayer("Terrain") || terrain.GetComponent<TerrainCollider>() == null)
                    report.Warnings.Add($"{scene.name}: terrain '{terrain.name}' was set up for the game (Terrain layer, Terrain Collider)");
                SetUp(terrain, material: false);
                if (!GameHasShader(terrain.materialTemplate))
                    report.Warnings.Add($"{scene.name}: terrain '{terrain.name}' renders with '{(terrain.materialTemplate != null && terrain.materialTemplate.shader != null ? terrain.materialTemplate.shader.name : "Unity's default")}', " +
                                        "which the game doesn't load itself (it's bundled with the map as it is); Tools > Geometry > Terrain Surfaces > Set Up gives it SDK_Terrain (Unity's Diffuse terrain shader, which the game always loads)");
                var guessed = terrain.terrainData.terrainLayers.Where(l => l != null && !HasSurface(l)).Select(l => $"{l.name} = {GetSurface(l)}").ToList();
                if (guessed.Count > 0)
                    report.Warnings.Add($"{scene.name}: terrain '{terrain.name}': surfaces guessed from layer names ({string.Join(", ", guessed)}); set them in Tools > Geometry > Terrain Surfaces");
                if (terrain.terrainData.terrainLayers.Any(l => l == null))
                    report.Warnings.Add($"{scene.name}: terrain '{terrain.name}' has an empty terrain layer slot");
            }
        }

        // the surface list for a terrain, shared by the window and TerrainBallistic's inspector
        public static void DrawSurfaces(Terrain terrain)
        {
            var layers = terrain.terrainData != null ? terrain.terrainData.terrainLayers : new TerrainLayer[0];
            if (layers.Length == 0)
            {
                EditorGUILayout.HelpBox("No terrain layers yet: add them in the Terrain's Paint Texture tool.", MessageType.Info);
                return;
            }
            foreach (var layer in layers)
            {
                if (layer == null)
                    continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    var preview = layer.diffuseTexture != null ? AssetPreview.GetAssetPreview(layer.diffuseTexture) ?? AssetPreview.GetMiniThumbnail(layer.diffuseTexture) : null;
                    GUILayout.Label(preview, GUILayout.Width(32), GUILayout.Height(32));
                    using (new EditorGUILayout.VerticalScope())
                    {
                        EditorGUILayout.LabelField(layer.name + (HasSurface(layer) ? "" : "  (guessed)"), EditorStyles.boldLabel);
                        int current = Array.IndexOf(Surfaces, GetSurface(layer));
                        EditorGUI.BeginChangeCheck();
                        int picked = EditorGUILayout.Popup("Surface", Mathf.Max(0, current), SurfaceLabels);
                        bool changed = EditorGUI.EndChangeCheck();
                        // drawn on every event, so the layout matches
                        bool keep = !HasSurface(layer) && GUILayout.Button("Keep This Surface", GUILayout.Width(130));
                        if (changed || keep)
                        {
                            var l = layer;
                            var s = Surfaces[picked];
                            EditorApplication.delayCall += () =>
                            {
                                SetSurface(l, s);
                                foreach (var t in UnityEngine.Object.FindObjectsOfType<Terrain>(true).Where(t => t.terrainData != null && t.terrainData.terrainLayers.Contains(l)))
                                    if (t.GetComponent<TerrainBallistic>() != null)
                                        Sync(t);
                            };
                        }
                    }
                }
            }
        }
    }

    // Tools > Geometry > Terrain Surfaces: every terrain in the open scenes, its setup and each layer's surface.
    public class TarkovTerrainSurfacesWindow : EditorWindow
    {
        private Vector2 _scroll;

        [MenuItem("Tarkov SDK/Tools/Geometry/Terrain Surfaces...", priority = 127)]
        private static void Open() => GetWindow<TarkovTerrainSurfacesWindow>("Terrain Surfaces");

        private void OnGUI()
        {
            EditorGUILayout.HelpBox("Each terrain layer is a surface for bullets (penetration, ricochet, impact effects) and footsteps. " +
                                    "Paint layers with the Terrain's Paint Texture tool (or Terrain Tools' brushes); whatever layer is strongest at a spot is its surface.", MessageType.None);
            var terrains = FindObjectsOfType<Terrain>(true).Where(t => !t.CompareTag("EditorOnly")).ToList();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("New Terrain"))
                    EditorApplication.delayCall += () =>
                    {
                        var t = TarkovTerrains.Create(null);
                        Undo.RegisterCreatedObjectUndo(t.gameObject, "New terrain");
                        Selection.activeGameObject = t.gameObject;
                    };
                using (new EditorGUI.DisabledScope(terrains.All(TarkovTerrains.IsSetUp)))
                    if (GUILayout.Button(new GUIContent("Set Up All", "Terrain layer, Terrain Collider, the game's TerrainBallistic and SDK_Terrain for every terrain.")))
                        EditorApplication.delayCall += () => terrains.ForEach(t => TarkovTerrains.SetUp(t));
            }
            if (terrains.Count == 0)
            {
                EditorGUILayout.HelpBox("No terrains in the open scenes.", MessageType.Info);
                return;
            }
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var terrain in terrains)
            {
                EditorGUILayout.Space();
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.ObjectField(terrain, typeof(Terrain), true);
                    if (!TarkovTerrains.IsSetUp(terrain) && GUILayout.Button("Set Up", GUILayout.Width(70)))
                    {
                        var t = terrain;
                        EditorApplication.delayCall += () => TarkovTerrains.SetUp(t);
                    }
                }
                using (new EditorGUI.IndentLevelScope())
                    TarkovTerrains.DrawSurfaces(terrain);
            }
            EditorGUILayout.EndScrollView();
        }

        private void OnInspectorUpdate() => Repaint();
    }

    [CustomEditor(typeof(TerrainBallistic))]
    public class TerrainBallisticEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var terrain = ((TerrainBallistic)target).GetComponent<Terrain>();
            EditorGUILayout.HelpBox("The game's terrain ballistics: each layer's surface for bullets and footsteps. Build Map fills Terrain Materials from these.", MessageType.None);
            if (terrain == null)
            {
                EditorGUILayout.HelpBox("Needs a Terrain on the same object.", MessageType.Warning);
                return;
            }
            TarkovTerrains.DrawSurfaces(terrain);
        }
    }
}
