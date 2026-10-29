using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EFT.Interactive;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Tarkov SDK > Tools > Geometry > Culling Helper...: the three ways retail maps keep the game from drawing what the
    // player can't see, set up for the open scenes.
    //  - Baked occlusion culling (Unity's): Factory, Labs, Shoreline and Interchange bake it over all their scenes into
    //    one OcclusionCullingData (Labs' 40 scenes share one), and the raid camera uses it only when base.json's
    //    OcculsionCullingEnabled is on (CameraManager.SetOcclusionCullingEnabled from Location.OcculsionCullingEnabled).
    //    A scene bundle carries the data (checked: Unity builds the shared OcclusionCullingData into it, each scene's
    //    OcclusionCullingSettings pointing at it). The helper marks Occluder/Occludee Static, bakes with every map
    //    scene open and turns the base.json flag on; Build Map warns when a bake is missing, unused or out of date
    //    (a hash of the static objects, saved at bake time in <scene folder>/<first scene>_Occlusion.json).
    //  - Lights by distance: every retail map light carries a CullingLightObject (Customs' custom_Light 3224 of 3225),
    //    which the game's CullingManager switches off beyond its distance.
    //  - LOD culling: every retail LODGroup culls at its last LOD (none at 0); Customs and Shoreline's props at 2% of
    //    the screen's height (median, all sizes), Factory's larger ones from 10 to 20%. Props without an LODGroup are
    //    drawn to the camera's far plane, so the helper gives them a one-level LODGroup that culls.
    public class TarkovCullingWindow : EditorWindow
    {
        private float _occluderSize = 1f;
        private float _lightDistance = 50f;
        private float _cullHeight = 2f;
        private float _maxSize = TarkovCulling.UnitSize;
        private TarkovCulling.Scan _scan;
        private Vector2 _scroll;
        private string _status;

        [MenuItem("Tarkov SDK/Tools/Geometry/Culling Helper...", priority = 132)]
        public static void Open() => GetWindow<TarkovCullingWindow>("Culling Helper").minSize = new Vector2(380f, 460f);

        private void OnEnable()
        {
            EditorSceneManager.sceneOpened += OnScenes;
            EditorSceneManager.sceneClosed += OnScene;
            _scan = null;
        }

        private void OnDisable()
        {
            EditorSceneManager.sceneOpened -= OnScenes;
            EditorSceneManager.sceneClosed -= OnScene;
        }

        private void OnScenes(Scene scene, OpenSceneMode mode) => _scan = null;
        private void OnScene(Scene scene) => _scan = null;
        private void OnFocus() => _scan = null;

        private void OnGUI()
        {
            if (_scan == null)
                _scan = TarkovCulling.Scan.Of(_occluderSize, _maxSize);
            var def = TarkovCulling.Definition();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("What the game skips drawing, for the open scenes.", EditorStyles.wordWrappedMiniLabel);

            // ---- occlusion
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Occlusion culling (baked)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Hides what's behind walls and buildings. Best for indoor and dense maps (retail: Factory, Labs, Shoreline, Interchange). " +
                                       "Bake it last, when the map's static objects are where they'll stay: anything moved after the bake can vanish in the raid.",
                EditorStyles.wordWrappedMiniLabel);
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                _occluderSize = EditorGUILayout.Slider(new GUIContent("Occluders From (m)", "Objects at least this big (their largest side) can hide others."), _occluderSize, 0.25f, 10f);
                if (check.changed)
                    _scan = null;
            }
            EditorGUILayout.LabelField($"{_scan.Occluders} occluder(s) and {_scan.Occludees} occludee(s) to mark; {_scan.Unflag} moving or see-through object(s) to unmark.",
                EditorStyles.miniLabel);
            if (GUILayout.Button(new GUIContent("Mark Static Objects", "Occluder Static on solid, opaque, unmoving renderers and terrains; Occludee Static on every unmoving renderer. " +
                                                                       "Doors, containers, physics props, animated objects and glass lose Occluder Static (they move or can be seen through).")))
                EditorApplication.delayCall += () =>
                {
                    _status = TarkovCulling.MarkStatic(_occluderSize);
                    _scan = null;
                };

            var state = TarkovCulling.State(def);
            EditorGUILayout.HelpBox(state.Text, state.Type);
            // Unity's bake settings, saved in the active scene: written only when changed
            float occluder = EditorGUILayout.FloatField(new GUIContent("Smallest Occluder", "Unity's bake setting (m): smaller finds more occluders but bakes longer and bigger."), StaticOcclusionCulling.smallestOccluder);
            float hole = EditorGUILayout.FloatField(new GUIContent("Smallest Hole", "Unity's bake setting (m): the smallest gap to see through (keep it under a window's or a door's size)."), StaticOcclusionCulling.smallestHole);
            if (!Mathf.Approximately(occluder, StaticOcclusionCulling.smallestOccluder))
                StaticOcclusionCulling.smallestOccluder = Mathf.Max(0.1f, occluder);
            if (!Mathf.Approximately(hole, StaticOcclusionCulling.smallestHole))
                StaticOcclusionCulling.smallestHole = Mathf.Max(0.01f, hole);
            if (StaticOcclusionCulling.isRunning)
            {
                EditorGUILayout.HelpBox("Baking...", MessageType.None);
                if (GUILayout.Button("Cancel Bake"))
                    StaticOcclusionCulling.Cancel();
                Repaint();
            }
            else
            {
                var missing = def != null ? TarkovMapScenes.Paths(def).Where(p => !SceneManager.GetSceneByPath(p).isLoaded).ToList() : new List<string>();
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (missing.Count > 0)
                    {
                        if (GUILayout.Button(new GUIContent($"Open All Map Scenes ({missing.Count} closed)", "The bake covers the scenes that are open; the map's scenes are baked together.")))
                            EditorApplication.delayCall += () =>
                            {
                                TarkovMapScenes.OpenAll(def);
                                _scan = null;
                            };
                    }
                    else if (GUILayout.Button(new GUIContent("Bake", "Bakes the open scenes together (Unity's occlusion bake, in the background).")))
                        EditorApplication.delayCall += () => _status = TarkovCulling.Bake(def);
                    if (GUILayout.Button(new GUIContent("Clear", "Removes the baked data from the open scenes.")))
                        EditorApplication.delayCall += () =>
                        {
                            StaticOcclusionCulling.Clear();
                            TarkovCulling.ForgetBake(def);
                            _status = "Cleared the occlusion data of the open scenes.";
                        };
                    if (GUILayout.Button(new GUIContent("Visualize", "Unity's Occlusion Culling window: its Visualization tab shows what a camera would cull.")))
                        EditorApplication.delayCall += () => EditorApplication.ExecuteMenuItem("Window/Rendering/Occlusion Culling");
                }
            }
            if (def != null)
            {
                bool? on = TarkovCulling.BaseFlag(def);
                if (on != null && GUILayout.Button(on.Value ? "Turn Off In base.json (OcculsionCullingEnabled)" : "Turn On In base.json (OcculsionCullingEnabled)"))
                    EditorApplication.delayCall += () => _status = TarkovCulling.SetBaseFlag(def, !on.Value);
            }

            // ---- lights
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Lights by distance", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Every retail light switches off beyond a distance (CullingLightObject; retail from 15 m for small lights to 200 m for street lamps).",
                EditorStyles.wordWrappedMiniLabel);
            _lightDistance = EditorGUILayout.Slider(new GUIContent("Distance (m)", "Off beyond this, fading out over its last 20%."), _lightDistance, 10f, 200f);
            using (new EditorGUI.DisabledScope(_scan.Lights.Count == 0))
                if (GUILayout.Button(_scan.Lights.Count == 0 ? "Every Light Has Culling" : $"Add Culling To {_scan.Lights.Count} Light(s)"))
                {
                    var lights = _scan.Lights.ToList();
                    EditorApplication.delayCall += () =>
                    {
                        _status = TarkovCulling.CullLights(lights, _lightDistance);
                        _scan = null;
                    };
                }

            // ---- LOD culling
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Small objects by screen size", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Retail props stop drawing when they get small on screen (their LOD group's last level). Objects without an LOD group are drawn as far as the camera sees.",
                EditorStyles.wordWrappedMiniLabel);
            _cullHeight = EditorGUILayout.Slider(new GUIContent("Cull Below (% of screen)", "Retail: 2% on Customs and Shoreline; Factory culls big objects from 10%."), _cullHeight, 0.5f, 20f);
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                _maxSize = EditorGUILayout.Slider(new GUIContent("Objects Up To (m)", "Bigger objects (buildings, terrain pieces) are left alone."), _maxSize, 1f, 50f);
                if (check.changed)
                    _scan = null;
            }
            EditorGUILayout.LabelField($"{_scan.Units.Count} object(s) without an LOD group, {_scan.NeverCulled.Count} LOD group(s) that never cull.", EditorStyles.miniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_scan.Units.Count == 0))
                    if (GUILayout.Button(new GUIContent("Add Culling LOD Groups", "A one-level LOD group on each object (its renderers), culled below the size above.")))
                    {
                        var units = _scan.Units.ToList();
                        EditorApplication.delayCall += () =>
                        {
                            _status = TarkovCulling.AddLodGroups(units, _cullHeight / 100f);
                            _scan = null;
                        };
                    }
                using (new EditorGUI.DisabledScope(_scan.NeverCulled.Count == 0))
                    if (GUILayout.Button(new GUIContent("Make LOD Groups Cull", "LOD groups whose last level never culls get culled below the size above.")))
                    {
                        var groups = _scan.NeverCulled.ToList();
                        EditorApplication.delayCall += () =>
                        {
                            _status = TarkovCulling.FixLodGroups(groups, _cullHeight / 100f);
                            _scan = null;
                        };
                    }
            }
            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(_status, MessageType.Info);
            }
            EditorGUILayout.EndScrollView();
        }
    }

    public static class TarkovCulling
    {
        public const float UnitSize = 8f;

        // ---- what's in the open scenes

        public sealed class Scan
        {
            public int Occluders, Occludees, Unflag;
            public readonly List<Light> Lights = new List<Light>();
            public readonly List<GameObject> Units = new List<GameObject>();
            public readonly List<LODGroup> NeverCulled = new List<LODGroup>();

            public static Scan Of(float occluderSize, float maxSize)
            {
                var scan = new Scan();
                foreach (var (go, flags) in WantedFlags(occluderSize))
                {
                    var now = GameObjectUtility.GetStaticEditorFlags(go);
                    if ((flags & StaticEditorFlags.OccluderStatic) != 0 && (now & StaticEditorFlags.OccluderStatic) == 0)
                        scan.Occluders++;
                    if ((flags & StaticEditorFlags.OccludeeStatic) != 0 && (now & StaticEditorFlags.OccludeeStatic) == 0)
                        scan.Occludees++;
                    if ((flags & StaticEditorFlags.OccluderStatic) == 0 && (now & StaticEditorFlags.OccluderStatic) != 0)
                        scan.Unflag++;
                }
                foreach (var light in All<Light>())
                    if (light.type != LightType.Directional && light.lightmapBakeType != LightmapBakeType.Baked && !EditorOnly(light.transform) &&
                        light.GetComponent<CullingObject>() == null)
                        scan.Lights.Add(light);
                scan.Units.AddRange(UnculledUnits(maxSize));
                foreach (var group in All<LODGroup>())
                {
                    var lods = group.GetLODs();
                    if (lods.Length > 0 && lods[lods.Length - 1].screenRelativeTransitionHeight <= 0f && !EditorOnly(group.transform))
                        scan.NeverCulled.Add(group);
                }
                return scan;
            }
        }

        private static IEnumerable<Scene> OpenScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded)
                    yield return scene;
            }
        }

        private static IEnumerable<T> All<T>() where T : Component =>
            OpenScenes().SelectMany(s => s.GetRootGameObjects()).SelectMany(r => r.GetComponentsInChildren<T>(true));

        private static bool EditorOnly(Transform t)
        {
            for (; t != null; t = t.parent)
                if (t.CompareTag("EditorOnly"))
                    return true;
            return false;
        }

        // moves in the raid (or its renderers are swapped): never static
        private static bool Moves(Transform t) =>
            t.GetComponentInParent<Rigidbody>(true) != null || t.GetComponentInParent<Animator>(true) != null || t.GetComponentInParent<Animation>(true) != null ||
            t.GetComponentInParent<WorldInteractiveObject>(true) != null || t.GetComponentInParent<LampController>(true) != null;

        private static bool Opaque(Renderer renderer)
        {
            foreach (var m in renderer.sharedMaterials)
            {
                if (m == null || m.shader == null || m.renderQueue >= 2450)
                    return false;
                var name = m.shader.name;
                if (new[] { "Transparent", "Cutout", "Particle", "Glass", "Decal", "SpeedTree", "Foliage", "Additive" }.Any(w => name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                    return false;
            }
            return renderer.sharedMaterials.Length > 0;
        }

        // LOD0 or no LOD group: the levels Unity's bake draws as occluders
        private static bool FirstLod(Renderer renderer)
        {
            var group = renderer.GetComponentInParent<LODGroup>();
            if (group == null)
                return true;
            var lods = group.GetLODs();
            return lods.Length == 0 || lods[0].renderers.Contains(renderer) || !lods.Any(l => l.renderers.Contains(renderer));
        }

        // every GameObject's wanted occlusion flags (only the occluder/occludee bits)
        private static IEnumerable<(GameObject, StaticEditorFlags)> WantedFlags(float occluderSize)
        {
            var wanted = new Dictionary<GameObject, StaticEditorFlags>();
            foreach (var renderer in All<Renderer>())
            {
                if (!(renderer is MeshRenderer) || !renderer.enabled || !renderer.gameObject.activeInHierarchy || EditorOnly(renderer.transform))
                    continue;
                var go = renderer.gameObject;
                if (Moves(renderer.transform))
                {
                    wanted[go] = 0;
                    continue;
                }
                var flags = StaticEditorFlags.OccludeeStatic;
                var size = renderer.bounds.size;
                if (Opaque(renderer) && FirstLod(renderer) && go.GetComponentInParent<WindowBreaker>(true) == null && Mathf.Max(size.x, size.y, size.z) >= occluderSize)
                    flags |= StaticEditorFlags.OccluderStatic;
                wanted[go] = flags;
            }
            foreach (var terrain in All<Terrain>())
                if (!EditorOnly(terrain.transform))
                    wanted[terrain.gameObject] = StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic;
            return wanted.Select(p => (p.Key, p.Value));
        }

        public static string MarkStatic(float occluderSize)
        {
            int changed = 0;
            const StaticEditorFlags mask = StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic;
            foreach (var (go, flags) in WantedFlags(occluderSize).ToList())
            {
                var now = GameObjectUtility.GetStaticEditorFlags(go);
                var next = (now & ~mask) | flags;
                if (next == now)
                    continue;
                Undo.RecordObject(go, "Mark occlusion static");
                GameObjectUtility.SetStaticEditorFlags(go, next);
                EditorSceneManager.MarkSceneDirty(go.scene);
                changed++;
            }
            return changed == 0 ? "Every object already had the right occlusion flags." : $"Set the occlusion flags of {changed} object(s). Bake again for them to count.";
        }

        // ---- lights

        public static string CullLights(List<Light> lights, float distance)
        {
            int made = 0;
            foreach (var light in lights.Where(l => l != null && l.GetComponent<CullingObject>() == null))
            {
                var culling = Undo.AddComponent<CullingLightObject>(light.gameObject);
                TarkovLights.ConfigureCulling(culling, light, distance);
                EditorSceneManager.MarkSceneDirty(light.gameObject.scene);
                made++;
            }
            return $"Added a CullingLightObject ({distance:0} m) to {made} light(s).";
        }

        // ---- LOD culling

        // the objects to give an LOD group: the largest group above renderers that stays under maxSize and holds no
        // LOD group (the Prefab Library's units), skipping moving, EditorOnly and terrain
        private static List<GameObject> UnculledUnits(float maxSize)
        {
            var units = new List<GameObject>();
            var taken = new HashSet<Transform>();
            foreach (var renderer in All<Renderer>())
            {
                if (!(renderer is MeshRenderer) || renderer.GetComponentInParent<LODGroup>(true) != null || EditorOnly(renderer.transform) ||
                    renderer.GetComponent<ParticleSystem>() != null || Moves(renderer.transform) || Size(renderer.transform) > maxSize)
                    continue;
                var unit = renderer.transform;
                for (var parent = unit.parent; parent != null; parent = parent.parent)
                {
                    if (parent.parent == null && parent.childCount > 8)
                        break;
                    if (parent.GetComponentInChildren<LODGroup>(true) != null || Size(parent) > maxSize || Moves(parent))
                        break;
                    unit = parent;
                }
                bool inside = false;
                for (var t = unit; t != null && !inside; t = t.parent)
                    inside = taken.Contains(t);
                if (inside)
                    continue;
                taken.Add(unit);
                units.Add(unit.gameObject);
            }
            // a unit picked before one of its parents was is inside it
            return units.Where(u => !units.Any(o => o != u && u.transform.IsChildOf(o.transform))).ToList();
        }

        private static float Size(Transform t)
        {
            var renderers = t.GetComponentsInChildren<MeshRenderer>(true);
            if (renderers.Length == 0)
                return 0f;
            var b = renderers[0].bounds;
            foreach (var r in renderers)
                b.Encapsulate(r.bounds);
            return Mathf.Max(b.size.x, b.size.y, b.size.z);
        }

        public static string AddLodGroups(List<GameObject> units, float height)
        {
            int made = 0;
            foreach (var unit in units.Where(u => u != null && u.GetComponentInChildren<LODGroup>(true) == null))
            {
                var renderers = unit.GetComponentsInChildren<MeshRenderer>(true).Cast<Renderer>().ToArray();
                if (renderers.Length == 0)
                    continue;
                var group = Undo.AddComponent<LODGroup>(unit);
                group.SetLODs(new[] { new LOD(height, renderers) });
                group.RecalculateBounds();
                EditorSceneManager.MarkSceneDirty(unit.scene);
                made++;
            }
            return $"Gave {made} object(s) an LOD group culled below {height * 100f:0.#}% of the screen.";
        }

        public static string FixLodGroups(List<LODGroup> groups, float height)
        {
            int fixedCount = 0;
            foreach (var group in groups.Where(g => g != null))
            {
                var lods = group.GetLODs();
                if (lods.Length == 0)
                    continue;
                // keep the levels in order: the last one culls at the given size, or just under the one before it
                float before = lods.Length > 1 ? lods[lods.Length - 2].screenRelativeTransitionHeight : 1f;
                lods[lods.Length - 1].screenRelativeTransitionHeight = Mathf.Min(height, before * 0.9f);
                Undo.RecordObject(group, "Make LOD group cull");
                group.SetLODs(lods);
                EditorSceneManager.MarkSceneDirty(group.gameObject.scene);
                fixedCount++;
            }
            return $"{fixedCount} LOD group(s) now cull below {height * 100f:0.#}% of the screen.";
        }

        // ---- the bake

        public static TarkovMapDefinition Definition()
        {
            var scene = SceneManager.GetActiveScene();
            foreach (var guid in AssetDatabase.FindAssets("t:TarkovMapDefinition"))
            {
                var definition = AssetDatabase.LoadAssetAtPath<TarkovMapDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (definition != null && definition.Scenes.Any(s => s != null && AssetDatabase.GetAssetPath(s) == scene.path))
                    return definition;
            }
            return null;
        }

        private static List<string> MapScenes(TarkovMapDefinition def) =>
            def != null ? TarkovMapScenes.Paths(def) : OpenScenes().Select(s => s.path).Where(p => !string.IsNullOrEmpty(p)).ToList();

        // Unity writes a multi-scene bake to <scene folder>/<a scene's name>/OcclusionCullingData.asset
        public static bool HasBake(TarkovMapDefinition def) => MapScenes(def).Any(p =>
            File.Exists(Path.Combine(Path.GetDirectoryName(p) ?? "", Path.GetFileNameWithoutExtension(p), "OcclusionCullingData.asset")));

        private static string StampPath(TarkovMapDefinition def)
        {
            var first = MapScenes(def).FirstOrDefault();
            return first == null ? null : Path.Combine(Path.GetDirectoryName(first) ?? "", Path.GetFileNameWithoutExtension(first) + "_Occlusion.json").Replace('\\', '/');
        }

        [Serializable]
        private class Stamp
        {
            public string Hash;
            public string Baked;
            public List<string> Scenes = new List<string>();
        }

        // the open scenes of the map (all open scenes without a definition)
        private static List<Scene> BakedScenes(TarkovMapDefinition def)
        {
            if (def == null)
                return OpenScenes().ToList();
            var paths = TarkovMapScenes.Paths(def);
            return OpenScenes().Where(s => paths.Contains(s.path)).ToList();
        }

        // the static objects the bake depends on, in the map's open scenes (another open scene isn't the map's)
        public static string StaticHash(TarkovMapDefinition def)
        {
            var lines = new List<string>();
            var scenes = BakedScenes(def);
            IEnumerable<T> InMap<T>() where T : Component =>
                scenes.SelectMany(s => s.GetRootGameObjects()).SelectMany(r => r.GetComponentsInChildren<T>(true));
            foreach (var renderer in InMap<Renderer>())
            {
                var flags = GameObjectUtility.GetStaticEditorFlags(renderer.gameObject);
                if ((flags & (StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic)) == 0)
                    continue;
                var t = renderer.transform;
                var p = t.position;
                var r = t.rotation.eulerAngles;
                var s = t.lossyScale;
                var mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
                lines.Add($"{renderer.gameObject.scene.name}/{TarkovAudioSources.Path(t)}|{(int)flags}|{p.x:F2},{p.y:F2},{p.z:F2}|{r.x:F1},{r.y:F1},{r.z:F1}|{s.x:F2},{s.y:F2},{s.z:F2}|{mesh?.name}");
            }
            foreach (var terrain in InMap<Terrain>())
                lines.Add($"terrain {terrain.gameObject.scene.name}/{terrain.name}|{terrain.transform.position}|{terrain.terrainData?.name}");
            lines.Sort(StringComparer.Ordinal);
            ulong h = 14695981039346656037UL;
            foreach (var line in lines)
                foreach (char c in line)
                {
                    h ^= c;
                    h *= 1099511628211UL;
                }
            return h.ToString("x16");
        }

        private static TarkovMapDefinition _baking;

        public static string Bake(TarkovMapDefinition def)
        {
            // Unity bakes every loaded scene together into one data set the map's scenes then share
            var others = def == null ? new List<Scene>() : OpenScenes().Except(BakedScenes(def)).ToList();
            if (others.Count > 0)
                return "Close the scenes that aren't part of the map first (" + string.Join(", ", others.Select(s => string.IsNullOrEmpty(s.name) ? "Untitled" : s.name)) + "): Unity bakes every open scene together.";
            var first = MapScenes(def).Select(SceneManager.GetSceneByPath).FirstOrDefault(s => s.isLoaded);
            if (first.IsValid())
                SceneManager.SetActiveScene(first);
            if (!StaticOcclusionCulling.GenerateInBackground())
                return "Unity didn't start the bake (see the Console).";
            _baking = def;
            EditorApplication.update -= WatchBake;
            EditorApplication.update += WatchBake;
            return "Baking the open scenes' occlusion in the background.";
        }

        private static void WatchBake()
        {
            if (StaticOcclusionCulling.isRunning)
                return;
            EditorApplication.update -= WatchBake;
            var def = _baking;
            _baking = null;
            if (!HasBake(def))
            {
                Debug.LogWarning("[Tarkov SDK] The occlusion bake made no data (cancelled, or no Occluder Static objects).");
                return;
            }
            var path = StampPath(def);
            if (path != null)
                File.WriteAllText(path, JsonUtility.ToJson(new Stamp { Hash = StaticHash(def), Baked = DateTime.Now.ToString("u"), Scenes = MapScenes(def) }, true));
            AssetDatabase.ImportAsset(path);
            foreach (var scene in BakedScenes(def))
                EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log("[Tarkov SDK] Occlusion baked for " + string.Join(", ", BakedScenes(def).Select(s => s.name)) +
                      (def != null && BaseFlag(def) == false ? ". Turn it on in base.json (Culling Helper) for the raid to use it." : "."));
        }

        public static void ForgetBake(TarkovMapDefinition def)
        {
            var path = StampPath(def);
            if (path != null && File.Exists(path))
                AssetDatabase.DeleteAsset(path);
        }

        public static (string Text, MessageType Type) State(TarkovMapDefinition def)
        {
            bool baked = HasBake(def);
            bool? on = def != null ? BaseFlag(def) : null;
            string flag = on == null ? "" : on.Value ? " base.json has it on." : " base.json has it off, so the raid doesn't use it yet.";
            if (!baked)
                return ("Not baked." + (on == true ? " base.json has it on: bake, or turn it off." : ""), on == true ? MessageType.Warning : MessageType.None);
            var stamp = ReadStamp(def);
            // the hash covers the whole map; with part of it closed it can't be compared
            if (stamp != null && def != null && BakedScenes(def).Count < TarkovMapScenes.Paths(def).Count)
                return ("Baked" + (stamp.Baked != null ? " " + stamp.Baked : "") + ". Open all the map's scenes to check it's up to date." + flag, MessageType.Info);
            if (stamp != null && stamp.Hash != StaticHash(def))
                return ("Baked, but static objects have changed since: bake again, or objects can vanish in the raid." + flag, MessageType.Warning);
            return ("Baked" + (stamp != null ? " " + stamp.Baked : "") + "." + flag, on == false ? MessageType.Warning : MessageType.Info);
        }

        private static Stamp ReadStamp(TarkovMapDefinition def)
        {
            var path = StampPath(def);
            try
            {
                return path != null && File.Exists(path) ? JsonUtility.FromJson<Stamp>(File.ReadAllText(path)) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---- base.json (edited in place, as the Server Data Editor does)

        private static string BasePath(TarkovMapDefinition def) => Path.Combine(TarkovMapBuilder.ServerMapDirectory(def), "base.json");

        public static bool? BaseFlag(TarkovMapDefinition def)
        {
            try
            {
                var path = BasePath(def);
                return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)).Value<bool?>("OcculsionCullingEnabled") ?? false : (bool?)null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string SetBaseFlag(TarkovMapDefinition def, bool on)
        {
            var path = BasePath(def);
            var data = JObject.Parse(File.ReadAllText(path));
            data["OcculsionCullingEnabled"] = on;
            File.WriteAllText(path, data.ToString(Formatting.Indented));
            return $"OcculsionCullingEnabled is now {(on ? "on" : "off")} in {path}. Restart the SPT server for it to count.";
        }

        // ---- Build Map (all map scenes open, before Gather moves anything)

        public static IEnumerable<string> Check(TarkovMapDefinition def)
        {
            bool baked = HasBake(def);
            bool? on = BaseFlag(def);
            if (on == true && !baked)
                yield return "Occlusion culling is on in base.json (OcculsionCullingEnabled), but the map has no baked occlusion data, so nothing is culled: bake it (Tools > Geometry > Culling Helper) or turn it off";
            if (baked && on == false)
                yield return "The map has baked occlusion data, but OcculsionCullingEnabled is off in base.json, so the raid doesn't use it (Culling Helper > Turn On In base.json)";
            var stamp = baked ? ReadStamp(def) : null;
            if (stamp != null && stamp.Hash != StaticHash(def))
                yield return "Static objects changed since the occlusion bake; where the old data says something is hidden it vanishes in the raid. Bake again (Tools > Geometry > Culling Helper)";
        }
    }
}
