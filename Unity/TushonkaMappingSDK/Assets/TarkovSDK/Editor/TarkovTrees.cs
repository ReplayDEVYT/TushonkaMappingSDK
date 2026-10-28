using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EFT.Ballistics;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // Trees as retail maps have them (Woods' pines, sharedassets165): a LODGroup over SpeedTree renderers on the game's
    // Nature/SpeedTreeEFT (each LOD with a TreeWind), and an AICollider child on the Foliage layer with TreeInteractive
    // and trigger spheres down the canopy (the rustle when a player walks through it, and BotOwner.AIData's EnterTree),
    // holding the trunk's capsule (LowPolyCollider: blocks movement) and LOD0Collider, a mesh collider with a
    // BallisticCollider (HighPolyCollider: bullets). TreeInteractivePart on the root points at it.
    //
    //  - Tree Builder (Tarkov SDK > Decompilation > Tree Builder): makes that out of a tree from another map (an
    //    AssetRipper import, SpeedTree meshes and materials kept as they are) or any tree model (its materials moved onto
    //    the stand-in as bark (GEOM_TYPE_BRANCH) or leaves (GEOM_TYPE_FROND, alpha cut, both sides)).
    //  - GameObject > Tarkov SDK > Environment > Tree (Pine / Broadleaf): the SDK's own trees, generated once into
    //    Assets/TarkovSDK/Trees (meshes, textures, materials, prefabs) and built the same way.
    //
    // SpeedTree's vertex shader moves BRANCH and FROND vertices towards TEXCOORD1 as an LOD fades out
    // (LOD_FADE_PERCENTAGE); real SpeedTree meshes carry their next LOD's positions there, any other mesh would fold up,
    // so those meshes are copied with TEXCOORD1 = their own positions and their LODGroup doesn't fade. Generic meshes
    // carry none of SpeedTree's branch or leaf wind data, so they get only the global sway (the whole tree bending by
    // height, which needs nothing from the mesh): _WindQuality Fastest, and a TreeWind on each LOD with retail Woods'
    // values (AddWind), which LevelSettings hands to the game's wind in the raid like any retail tree's.
    public static class TarkovTrees
    {
        public const string StandInShader = "Tarkov/Nature/SpeedTreeEFT";
        public const string AssetFolder = "Assets/TarkovSDK/Trees";
        public const string InteractiveName = "AICollider";

        public sealed class Options
        {
            [Tooltip("Bullets hitting the trunk.")]
            public MaterialType Wood = MaterialType.WoodThick;
            [Tooltip("The trigger spheres down the canopy that rustle when walked through (and tell bots a player is in the tree).")]
            public bool Canopy = true;
            [Tooltip("The last LOD stays until the tree is this small on screen (0.02 = 2% of its height).")]
            public float CullBelow = 0.02f;
            [Tooltip("Paint it on the scene's terrains too (adds it to their trees).")]
            public bool AddToTerrains;
        }

        // ------------------------------------------------------------ the SDK's own trees

        [MenuItem("GameObject/Tarkov SDK/Environment/Tree (Pine)", priority = 69)]
        private static void MenuPine(MenuCommand cmd) => Place(cmd, "SDK_Pine");

        [MenuItem("GameObject/Tarkov SDK/Environment/Tree (Broadleaf)", priority = 70)]
        private static void MenuBroadleaf(MenuCommand cmd) => Place(cmd, "SDK_Broadleaf");

        private static void Place(MenuCommand cmd, string name) => EditorApplication.delayCall += () =>
        {
            var prefab = SdkTree(name);
            if (prefab == null)
                return;
            var parent = (cmd.context as GameObject)?.transform;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent != null ? parent.gameObject.scene : SceneManager.GetActiveScene());
            if (parent != null)
                go.transform.SetParent(parent, false);
            else if (SceneView.lastActiveSceneView != null)
            {
                var pivot = SceneView.lastActiveSceneView.pivot;
                go.transform.position = Physics.Raycast(pivot + Vector3.up * 100f, Vector3.down, out var hit, 400f, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore) ? hit.point : pivot;
            }
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            Selection.activeGameObject = go;
        };

        // the generated tree's prefab, made on first use
        public static GameObject SdkTree(string name)
        {
            var path = $"{AssetFolder}/{name}.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null)
            {
                UpgradeSdkTrees();
                return existing;
            }
            if (Shader.Find(StandInShader) == null)
            {
                Debug.LogError($"[Tarkov SDK] The tree shader stand-in ({StandInShader}) is missing; reimport Assets/TarkovSDK/Shaders/Game.");
                return null;
            }
            Directory.CreateDirectory(AssetFolder);
            bool pine = name == "SDK_Pine";
            var bark = Material("SDK_TreeBark", Texture("SDK_TreeBark", 256, 256, BarkPixels, wrap: true), leaves: false);
            var foliage = pine
                ? Material("SDK_TreeNeedles", Texture("SDK_TreeNeedles", 256, 512, NeedlePixels, wrap: false), leaves: true)
                : Material("SDK_TreeLeaves", Texture("SDK_TreeLeaves", 512, 512, LeafPixels, wrap: false), leaves: true);
            var root = new GameObject(name);
            try
            {
                for (int lod = 0; lod < 3; lod++)
                {
                    var mesh = pine ? PineMesh(lod) : BroadleafMesh(lod);
                    mesh.name = $"{name}_LOD{lod}";
                    AssetDatabase.CreateAsset(mesh, $"{AssetFolder}/{mesh.name}.asset");
                    var go = new GameObject(mesh.name);
                    go.transform.SetParent(root.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterials = new[] { bark, foliage };
                }
                return Build(root, AssetFolder, name, new Options(), out _);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static Material Material(string name, Texture2D texture, bool leaves)
        {
            var path = $"{AssetFolder}/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
                return existing;
            var material = new Material(Shader.Find(StandInShader)) { name = name };
            material.SetTexture("_MainTex", texture);
            SetGeometry(material, leaves);
            material.EnableKeyword("EFFECT_HUE_VARIATION");
            material.SetColor("_HueVariation", leaves ? new Color(0.9f, 0.55f, 0.27f, 0.25f) : new Color(1f, 0.94f, 0.89f, 0.1f));
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        private static void SetGeometry(Material material, bool leaves)
        {
            foreach (var k in new[] { "GEOM_TYPE_BRANCH", "GEOM_TYPE_BRANCH_DETAIL", "GEOM_TYPE_FROND", "GEOM_TYPE_LEAF", "GEOM_TYPE_MESH" })
                material.DisableKeyword(k);
            material.EnableKeyword(leaves ? "GEOM_TYPE_FROND" : "GEOM_TYPE_BRANCH");
            material.SetFloat("_Cull", leaves ? 0f : 2f);
            material.SetFloat("_WindQuality", WindQuality);
            material.SetFloat("_Cutoff", 0.33f);
            // the retail fronds sit at 2452, after the opaque branches
            material.renderQueue = leaves ? 2452 : -1;
        }

        // textures: written as PNG and imported, like the fog puff
        private static Texture2D Texture(string name, int width, int height, Func<int, int, int, int, Color> pixel, bool wrap)
        {
            var path = $"{AssetFolder}/{name}.png";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null)
                return existing;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = pixel(x, y, width, height);
            texture.SetPixels(pixels);
            texture.Apply();
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.alphaIsTransparency = !wrap;
            importer.wrapMode = wrap ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
            importer.mipmapEnabled = true;
            // cut-out cards keep their coverage at a distance
            importer.mipMapsPreserveCoverage = !wrap;
            importer.alphaTestReferenceValue = 0.33f;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static float Noise(float x, float y)
        {
            float n = 0f, amplitude = 0.5f;
            for (int o = 0; o < 4; o++)
            {
                n += Mathf.PerlinNoise(x, y) * amplitude;
                x *= 2.03f;
                y *= 2.03f;
                amplitude *= 0.5f;
            }
            return n;
        }

        private static Color BarkPixels(int x, int y, int w, int h)
        {
            // ridges running up the trunk, tiling in both directions
            float u = x / (float)w, v = y / (float)h;
            float ridges = Mathf.Abs(Mathf.Sin((u * 9f + Noise(u * 3f, v * 2f) * 1.6f) * Mathf.PI));
            float t = Mathf.Clamp01(0.35f + 0.5f * ridges + 0.3f * (Noise(u * 8f + 7f, v * 30f) - 0.5f));
            return Color.Lerp(new Color(0.16f, 0.11f, 0.08f), new Color(0.42f, 0.33f, 0.25f), t);
        }

        // a fir branch: a stem up the middle, needles along it on both sides, tapering to the tip
        private static Color NeedlePixels(int x, int y, int w, int h)
        {
            float u = (x + 0.5f) / w * 2f - 1f, v = (y + 0.5f) / h;
            float half = (1f - v) * 0.85f + 0.08f;
            float side = Mathf.Abs(u);
            if (side > half)
                return Color.clear;
            float stem = side < 0.03f ? 1f : 0f;
            // needles slant up from the stem
            float needle = Mathf.Repeat(v * 40f - side * 22f, 1f);
            bool on = stem > 0f || needle < 0.42f && Noise(u * 12f, v * 60f) > 0.38f;
            if (!on)
                return Color.clear;
            float shade = Noise(u * 4f + 3f, v * 9f);
            var color = Color.Lerp(new Color(0.08f, 0.16f, 0.07f), new Color(0.2f, 0.32f, 0.12f), shade);
            if (stem > 0f)
                color = new Color(0.22f, 0.16f, 0.1f);
            color.a = 1f;
            return color;
        }

        // a cluster of leaves: overlapping ovals inside a round outline
        private static readonly Vector4[] Leaves = MakeLeaves();

        private static Vector4[] MakeLeaves()
        {
            var random = new System.Random(4207);
            var leaves = new Vector4[70];
            for (int i = 0; i < leaves.Length; i++)
            {
                float a = (float)random.NextDouble() * Mathf.PI * 2f, r = Mathf.Sqrt((float)random.NextDouble()) * 0.4f;
                leaves[i] = new Vector4(0.5f + Mathf.Cos(a) * r, 0.5f + Mathf.Sin(a) * r, (float)random.NextDouble() * Mathf.PI, 0.05f + (float)random.NextDouble() * 0.04f);
            }
            return leaves;
        }

        private static Color LeafPixels(int x, int y, int w, int h)
        {
            float u = (x + 0.5f) / w, v = (y + 0.5f) / h;
            for (int i = Leaves.Length - 1; i >= 0; i--)
            {
                var leaf = Leaves[i];
                float dx = u - leaf.x, dy = v - leaf.y, c = Mathf.Cos(leaf.z), s = Mathf.Sin(leaf.z);
                float lx = (dx * c + dy * s) / leaf.w, ly = (-dx * s + dy * c) / (leaf.w * 0.5f);
                if (lx * lx + ly * ly > 1f)
                    continue;
                float shade = 0.6f + 0.4f * (1f - Mathf.Abs(ly)) - (Mathf.Abs(ly) < 0.08f ? 0.2f : 0f);
                var color = Color.Lerp(new Color(0.12f, 0.22f, 0.06f), new Color(0.32f, 0.45f, 0.13f), shade * (0.7f + 0.3f * (i % 5) / 4f));
                color.a = 1f;
                return color;
            }
            return Color.clear;
        }

        // ------------------------------------------------------------ meshes

        private sealed class MeshBuilder
        {
            public readonly List<Vector3> Vertices = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            public readonly List<int>[] Triangles = { new List<int>(), new List<int>() };

            // a tapered tube from a to b
            public void Tube(Vector3 a, Vector3 b, float radiusA, float radiusB, int sides, float vTiling)
            {
                var axis = (b - a).normalized;
                var side = Vector3.Cross(axis, Mathf.Abs(axis.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
                var other = Vector3.Cross(axis, side);
                int start = Vertices.Count;
                float length = Vector3.Distance(a, b);
                for (int ring = 0; ring < 2; ring++)
                    for (int i = 0; i <= sides; i++)
                    {
                        float angle = i / (float)sides * Mathf.PI * 2f;
                        var normal = side * Mathf.Cos(angle) + other * Mathf.Sin(angle);
                        Vertices.Add((ring == 0 ? a : b) + normal * (ring == 0 ? radiusA : radiusB));
                        Normals.Add(normal);
                        Uvs.Add(new Vector2(i / (float)sides, ring * length / vTiling));
                    }
                // clockwise seen from outside (Unity's front faces)
                for (int i = 0; i < sides; i++)
                {
                    int a0 = start + i, a1 = a0 + 1, b0 = start + sides + 1 + i, b1 = b0 + 1;
                    Triangles[0].AddRange(new[] { a0, a1, b0, a1, b1, b0 });
                }
            }

            // a card (both sides drawn by the material) from 'origin' along 'along', 'width' wide, facing up-ish
            public void Card(Vector3 origin, Vector3 along, Vector3 across, float width)
            {
                int start = Vertices.Count;
                var halfWidth = across.normalized * (width * 0.5f);
                var normal = Vector3.Cross(along, across).normalized;
                if (normal.y < 0f)
                    normal = -normal;
                Vertices.AddRange(new[] { origin - halfWidth, origin + halfWidth, origin + along + halfWidth, origin + along - halfWidth });
                Normals.AddRange(new[] { normal, normal, normal, normal });
                Uvs.AddRange(new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) });
                Triangles[1].AddRange(new[] { start, start + 2, start + 1, start, start + 3, start + 2 });
            }

            public Mesh Mesh()
            {
                var mesh = new Mesh();
                mesh.SetVertices(Vertices);
                mesh.SetNormals(Normals);
                mesh.SetUVs(0, Uvs);
                // SpeedTree's LOD fade moves vertices towards TEXCOORD1: their own positions keep them still
                mesh.SetUVs(1, Vertices);
                mesh.subMeshCount = 2;
                mesh.SetTriangles(Triangles[0], 0);
                mesh.SetTriangles(Triangles[1], 1);
                mesh.RecalculateBounds();
                mesh.RecalculateTangents();
                return mesh;
            }
        }

        // a 14 m fir: a tapered trunk and whorls of drooping branch cards, fewer and wider at each LOD
        private static Mesh PineMesh(int lod)
        {
            var m = new MeshBuilder();
            const float height = 14f;
            m.Tube(Vector3.zero, Vector3.up * height, 0.28f, 0.04f, lod == 0 ? 8 : lod == 1 ? 6 : 4, 2f);
            var random = new System.Random(11 + lod);
            int whorls = lod == 0 ? 11 : lod == 1 ? 7 : 4;
            int perWhorl = lod == 0 ? 7 : lod == 1 ? 5 : 4;
            for (int w = 0; w < whorls; w++)
            {
                float t = w / (float)(whorls - 1);
                float y = Mathf.Lerp(3f, height - 0.6f, t);
                float length = Mathf.Lerp(3.6f, 0.9f, t) * (lod == 2 ? 1.15f : 1f);
                for (int b = 0; b < perWhorl; b++)
                {
                    float angle = (b + (float)random.NextDouble() * 0.5f) / perWhorl * Mathf.PI * 2f + w * 0.7f;
                    var outward = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                    var along = (outward - Vector3.up * 0.35f).normalized * length;
                    var across = Vector3.Cross(Vector3.up, outward);
                    m.Card(Vector3.up * y, along, across, length * (lod == 2 ? 1.1f : 0.8f));
                }
            }
            // a crown at the top
            m.Card(Vector3.up * (height - 1.4f), Vector3.up * 1.6f, Vector3.right, 0.9f);
            m.Card(Vector3.up * (height - 1.4f), Vector3.up * 1.6f, Vector3.forward, 0.9f);
            return m.Mesh();
        }

        // a 10 m broadleaf: a trunk with a few limbs and clusters of crossed leaf cards around them
        private static Mesh BroadleafMesh(int lod)
        {
            var m = new MeshBuilder();
            int sides = lod == 0 ? 8 : lod == 1 ? 6 : 4;
            m.Tube(Vector3.zero, Vector3.up * 5f, 0.3f, 0.18f, sides, 2f);
            var random = new System.Random(23);
            var limbEnds = new List<Vector3> { Vector3.up * 9.5f };
            m.Tube(Vector3.up * 5f, Vector3.up * 9.5f, 0.18f, 0.05f, sides, 2f);
            for (int i = 0; i < 4; i++)
            {
                float angle = i / 4f * Mathf.PI * 2f + 0.4f;
                var start = Vector3.up * (4.2f + i * 0.6f);
                var end = start + new Vector3(Mathf.Cos(angle) * 2.6f, 2.2f, Mathf.Sin(angle) * 2.6f);
                if (lod < 2)
                    m.Tube(start, end, 0.11f, 0.04f, Math.Max(4, sides - 2), 2f);
                limbEnds.Add(end);
            }
            int clusters = lod == 0 ? 46 : lod == 1 ? 24 : 10;
            int cards = lod == 0 ? 3 : 2;
            float size = lod == 0 ? 2.2f : lod == 1 ? 2.8f : 4f;
            for (int c = 0; c < clusters; c++)
            {
                // around the limbs' ends, filling a crown about 7 m across
                var anchor = limbEnds[c % limbEnds.Count];
                var offset = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.35f, (float)random.NextDouble() - 0.5f) * 3.2f;
                var center = anchor + offset;
                for (int k = 0; k < cards; k++)
                {
                    float angle = k / (float)cards * Mathf.PI + (float)random.NextDouble();
                    var across = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                    var along = (Vector3.up + across * 0.2f).normalized * size;
                    m.Card(center - along * 0.5f, along, across, size);
                }
            }
            return m.Mesh();
        }

        // ------------------------------------------------------------ building a tree

        // Makes a tree prefab from 'source' (a prefab, model or scene object; it's copied, never changed) in 'folder'.
        // Returns the prefab, or null; 'notes' says what was done.
        public static GameObject Build(GameObject source, string folder, string name, Options options, out List<string> notes)
        {
            notes = new List<string>();
            var log = notes;
            folder = folder.Replace('\\', '/').TrimEnd('/');
            Directory.CreateDirectory(folder);
            var assets = $"{folder}/{name}_Parts";
            GameObject tree = PrefabUtility.IsPartOfPrefabAsset(source) ? (GameObject)PrefabUtility.InstantiatePrefab(source) : Object.Instantiate(source);
            try
            {
                if (PrefabUtility.IsPartOfPrefabInstance(tree))
                    PrefabUtility.UnpackPrefabInstance(tree, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                tree.name = name;
                tree.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                tree.layer = 0;

                // what the SDK has no script for (retail impostors) goes, and the impostor objects with it
                int missing = tree.GetComponentsInChildren<Transform>(true).Sum(t => GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject));
                if (missing > 0)
                    notes.Add($"{missing} script(s) the SDK doesn't have removed (impostors)");
                foreach (var impostor in tree.GetComponentsInChildren<Transform>(true).Where(t => t != tree.transform && Regex.IsMatch(t.name, "impostor", RegexOptions.IgnoreCase) && t.GetComponent<Renderer>() == null).ToList())
                    Object.DestroyImmediate(impostor.gameObject);

                // the collider helpers' renderers (retail's trunk Cylinder has one) don't draw
                var helperLayers = new HashSet<int> { Layer("LowPolyCollider"), Layer("HighPolyCollider"), Layer("Foliage") };
                foreach (var r in tree.GetComponentsInChildren<Renderer>(true).Where(r => helperLayers.Contains(r.gameObject.layer)))
                    r.enabled = false;
                var visual = tree.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled && !helperLayers.Contains(r.gameObject.layer)).ToList();
                if (visual.Count == 0)
                {
                    notes.Add("it has no renderers");
                    return null;
                }

                // real SpeedTree meshes (retail trees keep their TreeWinds; a SpeedTree shader that isn't the SDK's stand-in)
                bool speedTree = tree.GetComponentsInChildren<EFT.SpeedTree.TreeWind>(true).Any() ||
                                 visual.SelectMany(r => r.sharedMaterials).Any(m => m != null && m.shader != null && m.shader.name != StandInShader &&
                                                                                    m.shader.name.IndexOf("SpeedTree", StringComparison.OrdinalIgnoreCase) >= 0);
                var converted = new Dictionary<Material, Material>();
                foreach (var r in visual)
                    r.sharedMaterials = r.sharedMaterials.Select(m => Convert(m, assets, converted, log)).ToArray();
                if (!speedTree)
                    KeepStillOnFade(visual, assets, notes);

                var group = SetUpLods(tree, visual, options, speedTree, notes);
                if (!speedTree && EnsureWind(tree))
                    notes.Add($"sways in the wind as a whole ({(Conifer(name) ? "pine" : "broadleaf")} values)");
                var lod0 = group.GetLODs()[0].renderers.Where(r => r != null).ToList();
                if (tree.GetComponentInChildren<TreeInteractive>(true) == null)
                    AddColliders(tree, lod0, assets, options, notes);
                else
                    notes.Add("its own colliders and canopy kept");
                var interactive = tree.transform.Find(InteractiveName) ?? tree.GetComponentInChildren<TreeInteractive>(true)?.transform;
                var part = tree.GetComponent<TreeInteractivePart>() ?? tree.AddComponent<TreeInteractivePart>();
                part.InteractivePart = interactive != null ? interactive.gameObject : null;
                foreach (var ballistic in tree.GetComponentsInChildren<BallisticCollider>(true).Where(b => b.SurfaceSound != BaseBallistic.ESurfaceSound.Wood))
                    ballistic.SurfaceSound = BaseBallistic.ESurfaceSound.Wood;

                var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}.prefab");
                var prefab = PrefabUtility.SaveAsPrefabAsset(tree, path);
                notes.Insert(0, $"saved {path}");
                if (options.AddToTerrains && prefab != null)
                    AddToTerrains(prefab, notes);
                return prefab;
            }
            finally
            {
                Object.DestroyImmediate(tree);
            }
        }

        private static int Layer(string name) => LayerMask.NameToLayer(name);

        // ------------------------------------------------------------ wind

        // SpeedTree's Fastest wind quality: the global sway only
        public const float WindQuality = 1f;

        public static bool Conifer(string name) => Regex.IsMatch(name, "pine|fir|spruce|conifer|cedar|larch|needle", RegexOptions.IgnoreCase);

        // A generic tree (its LODGroup's renderers all on the stand-in, none with a TreeWind) gets a TreeWind on each LOD
        // and its materials' wind turned on. False if it has wind already or isn't such a tree.
        public static bool EnsureWind(GameObject root)
        {
            var group = root.GetComponent<LODGroup>();
            if (group == null)
                return false;
            var renderers = group.GetLODs().SelectMany(l => l.renderers).Where(r => r != null).Distinct().ToList();
            if (renderers.Count == 0 || renderers.Any(r => r.GetComponent<EFT.SpeedTree.TreeWind>() != null))
                return false;
            var materials = renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().ToList();
            if (materials.Count == 0 || materials.Any(m => m.shader == null || m.shader.name != StandInShader))
                return false;
            foreach (var material in materials.Where(m => m.GetFloat("_WindQuality") <= 0f))
            {
                material.SetFloat("_WindQuality", WindQuality);
                EditorUtility.SetDirty(material);
            }
            // SpeedTree's height scale is 1 / the tree's height (the sway starts a quarter of the way up)
            float height = renderers.Select(r => r.GetComponent<MeshFilter>()).Where(f => f != null && f.sharedMesh != null)
                .Select(f => f.sharedMesh.bounds.max.y).DefaultIfEmpty(10f).Max();
            float heightScale = 1f / Mathf.Max(height, 1f);
            bool conifer = Conifer(root.name);
            foreach (var renderer in renderers)
                SetWind(renderer.gameObject.AddComponent<EFT.SpeedTree.TreeWind>(), conifer, heightScale);
            return true;
        }

        // Retail Woods (sharedassets17 LOD0s): pine05 for conifers, tree01 for broadleaves. Only what the global sway reads:
        // _ST_WindGlobal (time offset, sway distance, height scale, height exponent) calm and stormy, the branch adherence
        // (a steady lean downwind) and the time rates (_B stormy, _C calm, as LevelSettings' property block names them).
        public static void SetWind(EFT.SpeedTree.TreeWind wind, bool conifer, float heightScale)
        {
            float calm = conifer ? 0.00889f : 0.0103f;
            wind.BaseMinWindData = new EFT.SpeedTree.TreeWind.BaseTreeData
            {
                _ST_WindGlobal = new Vector4(0.726f, calm, heightScale, 1.75f),
                _ST_WindBranchAdherences = new Vector4(0.1f, 0f, 0f, 0f),
            };
            wind.BaseMaxWindData = new EFT.SpeedTree.TreeWind.BaseTreeData
            {
                _ST_WindGlobal = new Vector4(4.35f, 1.51f, heightScale, 1.75f),
                _ST_WindBranchAdherences = new Vector4(0.1f, 0f, 0f, 0f),
            };
            wind.MinWindData = new EFT.SpeedTree.TreeWind.FactorTreeData { _ST_WindGlobal_B = new Vector4(0.0298f, 0f, 0f, 0f) };
            wind.MaxWindData = new EFT.SpeedTree.TreeWind.FactorTreeData { _ST_WindGlobal_B = new Vector4(0.446f, 0f, 0f, 0f) };
        }

        // the SDK's own tree prefabs made before trees swayed
        public static void UpgradeSdkTrees()
        {
            foreach (var name in new[] { "SDK_Pine", "SDK_Broadleaf" })
            {
                var path = $"{AssetFolder}/{name}.prefab";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null || prefab.GetComponentInChildren<EFT.SpeedTree.TreeWind>(true) != null)
                    continue;
                var contents = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    if (EnsureWind(contents))
                    {
                        PrefabUtility.SaveAsPrefabAsset(contents, path);
                        Debug.Log($"[Tarkov SDK] {name} now sways in the wind");
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }
        }

        // Build Map: every generic tree in the scene gets its wind (before FillLocationScene lists the TreeWinds)
        public static void PrepareWind(Scene scene, GameObject[] roots)
        {
            UpgradeSdkTrees();
            int added = roots.SelectMany(r => r.GetComponentsInChildren<TreeInteractivePart>(true)).Count(part => EnsureWind(part.gameObject));
            if (added > 0)
                Debug.Log($"[Tarkov SDK] {scene.name}: wind added to {added} tree(s)");
        }

        // a material on the stand-in: SpeedTree materials keep their values and keywords; others become bark or leaves
        private static Material Convert(Material source, string folder, Dictionary<Material, Material> done, List<string> notes)
        {
            if (source == null)
                return null;
            if (source.shader != null && source.shader.name == StandInShader)
                return source;
            if (done.TryGetValue(source, out var existing))
                return existing;
            var shader = Shader.Find(StandInShader);
            var material = new Material(shader) { name = source.name };
            bool speedTree = source.shader != null && source.shader.name.IndexOf("SpeedTree", StringComparison.OrdinalIgnoreCase) >= 0;
            if (speedTree)
            {
                material.CopyPropertiesFromMaterial(source);
                material.shaderKeywords = source.shaderKeywords;
                material.renderQueue = source.renderQueue;
            }
            else
            {
                var main = FirstTexture(source, "_MainTex", "_BaseMap", "_BaseColorMap", "_Albedo", "_Diffuse");
                var normal = FirstTexture(source, "_BumpMap", "_NormalMap", "_Normal");
                material.SetTexture("_MainTex", main);
                material.SetTextureScale("_MainTex", source.HasProperty("_MainTex") ? source.GetTextureScale("_MainTex") : Vector2.one);
                if (normal != null)
                {
                    material.SetTexture("_BumpMap", normal);
                    material.EnableKeyword("EFFECT_BUMP");
                }
                if (source.HasProperty("_Color"))
                    material.SetColor("_Color", source.GetColor("_Color"));
                else if (source.HasProperty("_BaseColor"))
                    material.SetColor("_Color", source.GetColor("_BaseColor"));
                bool leaves = IsLeaves(source, main);
                SetGeometry(material, leaves);
                if (source.HasProperty("_Cutoff"))
                    material.SetFloat("_Cutoff", source.GetFloat("_Cutoff"));
                notes.Add($"{source.name}: {(leaves ? "leaves (alpha cut, both sides)" : "bark")}");
            }
            Directory.CreateDirectory(folder);
            AssetDatabase.CreateAsset(material, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{SafeName(source.name)}.mat"));
            done[source] = material;
            return material;
        }

        private static Texture FirstTexture(Material m, params string[] names) =>
            names.Where(m.HasProperty).Select(m.GetTexture).FirstOrDefault(t => t != null);

        private static bool IsLeaves(Material m, Texture main)
        {
            if (m.renderQueue >= 2450 || m.IsKeywordEnabled("_ALPHATEST_ON") || Regex.IsMatch(m.name, "leaf|leaves|frond|needle|foliage|canopy|twig|card", RegexOptions.IgnoreCase))
                return true;
            var importer = main != null ? AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(main)) as TextureImporter : null;
            return importer != null && importer.DoesSourceTextureHaveAlpha() && importer.alphaIsTransparency;
        }

        private static string SafeName(string name) => Regex.Replace(name, @"[^\w\-. ]", "_");

        // copies of the meshes with TEXCOORD1 = their positions (see above)
        private static void KeepStillOnFade(List<Renderer> renderers, string folder, List<string> notes)
        {
            var copies = new Dictionary<Mesh, Mesh>();
            foreach (var filter in renderers.Select(r => r.GetComponent<MeshFilter>()).Where(f => f != null && f.sharedMesh != null))
            {
                var mesh = filter.sharedMesh;
                if (!copies.TryGetValue(mesh, out var copy))
                {
                    if (mesh.isReadable && HoldsOwnPositions(mesh))
                    {
                        copies[mesh] = mesh;
                        continue;
                    }
                    if (!mesh.isReadable)
                    {
                        notes.Add($"{mesh.name}: not readable (turn on Read/Write in its model's import settings), kept as is; its LODs mustn't fade");
                        copies[mesh] = mesh;
                        continue;
                    }
                    copy = Object.Instantiate(mesh);
                    copy.name = mesh.name;
                    copy.SetUVs(1, mesh.vertices);
                    Directory.CreateDirectory(folder);
                    AssetDatabase.CreateAsset(copy, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{SafeName(mesh.name)}.asset"));
                    copies[mesh] = copy;
                }
                filter.sharedMesh = copy;
            }
        }

        // TEXCOORD1 already holds the vertices' positions (a mesh this made, or the SDK's own trees)
        private static bool HoldsOwnPositions(Mesh mesh)
        {
            var uvs = new List<Vector3>();
            mesh.GetUVs(1, uvs);
            var vertices = mesh.vertices;
            if (uvs.Count != vertices.Length || vertices.Length == 0)
                return false;
            for (int i = 0; i < vertices.Length; i += Math.Max(1, vertices.Length / 16))
                if ((uvs[i] - vertices[i]).sqrMagnitude > 1e-6f)
                    return false;
            return true;
        }

        // the LODGroup: the source's own, or one from renderers named ..LOD0, ..LOD1 (or one LOD); the last stays until CullBelow
        private static LODGroup SetUpLods(GameObject tree, List<Renderer> visual, Options options, bool speedTree, List<string> notes)
        {
            var group = tree.GetComponent<LODGroup>();
            LOD[] lods;
            if (group != null)
            {
                lods = group.GetLODs().Select(l => new LOD(l.screenRelativeTransitionHeight, l.renderers.Where(r => r != null && visual.Contains(r)).ToArray()))
                    .Where(l => l.renderers.Length > 0).ToArray();
            }
            else
            {
                group = tree.AddComponent<LODGroup>();
                var byLevel = visual.GroupBy(r => LodLevel(r.transform)).OrderBy(g => g.Key).ToList();
                // retail pines: 0.7, 0.45, 0.25 (then an impostor)
                var heights = new[] { 0.7f, 0.45f, 0.25f, 0.12f, 0.06f };
                lods = byLevel.Select((g, i) => new LOD(heights[Math.Min(i, heights.Length - 1)], g.ToArray())).ToArray();
                notes.Add($"{lods.Length} LOD(s) from the renderers' names");
            }
            if (lods.Length == 0)
                lods = new[] { new LOD(options.CullBelow, visual.ToArray()) };
            lods[lods.Length - 1].screenRelativeTransitionHeight = Mathf.Min(lods[lods.Length - 1].screenRelativeTransitionHeight, options.CullBelow);
            for (int i = lods.Length - 2; i >= 0; i--)
                lods[i].screenRelativeTransitionHeight = Mathf.Max(lods[i].screenRelativeTransitionHeight, lods[i + 1].screenRelativeTransitionHeight + 0.01f);
            group.SetLODs(lods);
            if (!speedTree)
                group.fadeMode = LODFadeMode.None;
            group.RecalculateBounds();
            return group;
        }

        // 0 for ..LOD0 or no LOD in the name (or its parents' names)
        private static int LodLevel(Transform t)
        {
            for (var x = t; x != null; x = x.parent)
            {
                var m = Regex.Match(x.name, @"LOD_?(\d+)", RegexOptions.IgnoreCase);
                if (m.Success)
                    return int.Parse(m.Groups[1].Value);
            }
            return 0;
        }

        // retail's layout, sized from the LOD0 bark (trunk) and leaves (canopy)
        private static void AddColliders(GameObject tree, List<Renderer> lod0, string folder, Options options, List<string> notes)
        {
            var barkPoints = new List<Vector3>();
            var leafBounds = new Bounds();
            bool hasLeaves = false;
            var bounds = new Bounds(tree.transform.position, Vector3.zero);
            var barkTriangles = new List<(Mesh Mesh, int SubMesh, Matrix4x4 Matrix)>();
            foreach (var r in lod0)
            {
                bounds.Encapsulate(r.bounds);
                var mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
                var materials = r.sharedMaterials;
                if (mesh == null)
                    continue;
                for (int s = 0; s < mesh.subMeshCount && s < materials.Length; s++)
                {
                    bool leaves = materials[s] != null && (materials[s].IsKeywordEnabled("GEOM_TYPE_FROND") || materials[s].IsKeywordEnabled("GEOM_TYPE_LEAF"));
                    if (!mesh.isReadable)
                        continue;
                    var matrix = tree.transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
                    var vertices = mesh.vertices;
                    var used = mesh.GetTriangles(s).Distinct().Select(i => matrix.MultiplyPoint3x4(vertices[i])).ToList();
                    if (leaves)
                    {
                        foreach (var p in used)
                        {
                            if (!hasLeaves)
                                leafBounds = new Bounds(p, Vector3.zero);
                            leafBounds.Encapsulate(p);
                            hasLeaves = true;
                        }
                    }
                    else
                    {
                        barkPoints.AddRange(used);
                        barkTriangles.Add((mesh, s, matrix));
                    }
                }
            }
            float bottom = barkPoints.Count > 0 ? barkPoints.Min(p => p.y) : 0f;
            float top = Mathf.Max(bounds.max.y, bottom + 1f);
            // the trunk: the bark within 1.5 m of the ground
            var basePoints = barkPoints.Where(p => p.y < bottom + 1.5f).ToList();
            var center = basePoints.Count > 0 ? new Vector3(basePoints.Average(p => p.x), 0f, basePoints.Average(p => p.z)) : new Vector3(bounds.center.x, 0f, bounds.center.z);
            float radius = basePoints.Count > 0 ? Mathf.Clamp(basePoints.Max(p => new Vector2(p.x - center.x, p.z - center.z).magnitude) * 0.85f, 0.08f, 1.5f) : 0.15f;
            float trunkHeight = Mathf.Max(1.5f, ((barkPoints.Count > 0 ? barkPoints.Max(p => p.y) : top) - bottom) * 0.75f);
            if (!hasLeaves)
                leafBounds = new Bounds(new Vector3(center.x, (bottom + top) * 0.65f, center.z), new Vector3(bounds.size.x, (top - bottom) * 0.6f, bounds.size.z));

            var interactive = new GameObject(InteractiveName) { layer = Layer("Foliage") };
            interactive.transform.SetParent(tree.transform, false);
            interactive.transform.localPosition = new Vector3(center.x, leafBounds.min.y, center.z);
            interactive.AddComponent<TreeInteractive>();
            if (options.Canopy)
            {
                // three triggers from the lowest branches up, narrowing like retail's (1.29, 1.0, 0.65 m on a pine)
                float span = Mathf.Max(1f, leafBounds.max.y - leafBounds.min.y);
                float width = Mathf.Max(leafBounds.extents.x, leafBounds.extents.z);
                for (int i = 0; i < 3; i++)
                {
                    var sphere = interactive.AddComponent<SphereCollider>();
                    sphere.isTrigger = true;
                    sphere.radius = Mathf.Clamp(width * (0.7f - i * 0.18f), 0.4f, 4f);
                    sphere.center = new Vector3(0f, span * (0.15f + i * 0.3f), 0f);
                }
            }

            // the trunk: blocks movement (retail's Cylinder: a capsule on LowPolyCollider)
            var trunk = new GameObject("Trunk") { layer = Layer("LowPolyCollider") };
            trunk.transform.SetParent(interactive.transform, false);
            trunk.transform.position = tree.transform.TransformPoint(new Vector3(center.x, bottom + trunkHeight * 0.5f, center.z));
            var capsule = trunk.AddComponent<CapsuleCollider>();
            capsule.direction = 1;
            capsule.radius = radius;
            capsule.height = trunkHeight;

            // bullets: the bark's own triangles (retail's LOD0Collider), or the capsule's shape
            var hitbox = new GameObject("LOD0Collider") { layer = Layer("HighPolyCollider") };
            hitbox.transform.SetParent(interactive.transform, false);
            hitbox.transform.position = tree.transform.position;
            var barkMesh = BarkCollider(barkTriangles, tree.name, folder);
            if (barkMesh != null)
                hitbox.AddComponent<MeshCollider>().sharedMesh = barkMesh;
            else
            {
                hitbox.transform.position = trunk.transform.position;
                var c = hitbox.AddComponent<CapsuleCollider>();
                c.direction = 1;
                c.radius = radius + 0.02f;
                c.height = trunkHeight;
            }
            var ballistic = hitbox.AddComponent<BallisticCollider>();
            ballistic._typeOfMaterial = options.Wood;
            ballistic.SurfaceSound = BaseBallistic.ESurfaceSound.Wood;
            notes.Add($"trunk {radius * 2f:0.00} m wide, {trunkHeight:0.0} m tall; canopy {(options.Canopy ? "triggers" : "none")} from {leafBounds.min.y - bottom:0.0} m");
        }

        // the bark triangles of LOD0 as one mesh, in the hitbox's space (which sits at the tree's origin)
        private static Mesh BarkCollider(List<(Mesh Mesh, int SubMesh, Matrix4x4 Matrix)> parts, string name, string folder)
        {
            if (parts.Count == 0)
                return null;
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            foreach (var (mesh, sub, matrix) in parts)
            {
                var source = mesh.vertices;
                var map = new Dictionary<int, int>();
                foreach (var i in mesh.GetTriangles(sub))
                {
                    if (!map.TryGetValue(i, out var j))
                    {
                        j = map[i] = vertices.Count;
                        vertices.Add(matrix.MultiplyPoint3x4(source[i]));
                    }
                    triangles.Add(j);
                }
            }
            if (triangles.Count == 0 || vertices.Count > 60000)
                return null;
            var collider = new Mesh { name = name + "LOD0Collider", indexFormat = vertices.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            collider.SetVertices(vertices);
            collider.SetTriangles(triangles, 0);
            collider.RecalculateBounds();
            Directory.CreateDirectory(folder);
            AssetDatabase.CreateAsset(collider, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{SafeName(collider.name)}.asset"));
            return collider;
        }

        private static void AddToTerrains(GameObject prefab, List<string> notes)
        {
            foreach (var terrain in Object.FindObjectsOfType<Terrain>().Where(t => t.terrainData != null))
            {
                var data = terrain.terrainData;
                if (data.treePrototypes.Any(p => p.prefab == prefab))
                    continue;
                Undo.RecordObject(data, "Add tree to terrain");
                data.treePrototypes = data.treePrototypes.Append(new TreePrototype { prefab = prefab }).ToArray();
                data.RefreshPrototypes();
                EditorUtility.SetDirty(data);
                notes.Add($"added to terrain '{terrain.name}' (paint it with the Paint Trees tool)");
            }
        }
    }

    // Tarkov SDK > Decompilation > Tree Builder
    public class TarkovTreeBuilderWindow : EditorWindow
    {
        private GameObject _source;
        private string _name = "";
        private string _folder = "Assets/Trees";
        private readonly TarkovTrees.Options _options = new TarkovTrees.Options();
        private List<string> _notes;

        [MenuItem("Tarkov SDK/Decompilation/Tree Builder...", priority = 152)]
        private static void Open()
        {
            var window = GetWindow<TarkovTreeBuilderWindow>("Tree Builder");
            window.minSize = new Vector2(380f, 330f);
            if (Selection.activeGameObject != null)
                window._source = Selection.activeGameObject;
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Tree Builder", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Makes a game-ready tree prefab: the game's tree shader (Nature/SpeedTreeEFT), LODs, a trunk that blocks players and " +
                                       "bullets (wood), and the canopy triggers that rustle, as on Woods. Give it a tree from another map (an AssetRipper import: " +
                                       "its SpeedTree meshes and materials are kept) or any tree model (bark and leaf materials are worked out from their " +
                                       "textures and names). The source isn't changed.", EditorStyles.wordWrappedMiniLabel);
            EditorGUI.BeginChangeCheck();
            _source = (GameObject)EditorGUILayout.ObjectField("Tree", _source, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck() && _source != null)
                _name = _source.name.Replace(" ", "_");
            _name = EditorGUILayout.TextField("Prefab Name", _name);
            _folder = EditorGUILayout.TextField("Save Into", _folder);
            _options.Wood = (EFT.Ballistics.MaterialType)EditorGUILayout.EnumPopup(new GUIContent("Trunk Material", "What bullets hit (WoodThick for trunks, WoodThin for saplings)."), _options.Wood);
            _options.Canopy = EditorGUILayout.Toggle(new GUIContent("Canopy Triggers", "Rustles when walked through and tells bots a player is in the tree."), _options.Canopy);
            _options.CullBelow = EditorGUILayout.Slider(new GUIContent("Cull Below", "The last LOD stays until the tree is this small on screen."), _options.CullBelow, 0.005f, 0.2f);
            _options.AddToTerrains = EditorGUILayout.Toggle(new GUIContent("Add To Terrains", "Adds it to the open scene's terrains, to paint with the Paint Trees tool."), _options.AddToTerrains);
            using (new EditorGUI.DisabledScope(_source == null || string.IsNullOrWhiteSpace(_name) || !_folder.StartsWith("Assets", StringComparison.Ordinal)))
                if (GUILayout.Button("Build Tree"))
                    EditorApplication.delayCall += () =>
                    {
                        var prefab = TarkovTrees.Build(_source, _folder, _name.Trim(), _options, out _notes);
                        if (prefab != null)
                        {
                            Selection.activeObject = prefab;
                            EditorGUIUtility.PingObject(prefab);
                        }
                        Debug.Log($"[Tarkov SDK] Tree Builder: {(prefab != null ? "built" : "not built")} {_name}: " + string.Join("; ", _notes));
                        Repaint();
                    };
            if (_notes != null)
                foreach (var note in _notes)
                    EditorGUILayout.LabelField("- " + note, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Trees placed as objects get everything. Trees painted on a terrain are drawn and collide as Unity draws them; " +
                                       "the canopy triggers only work on placed trees.", EditorStyles.wordWrappedMiniLabel);
        }
    }
}
