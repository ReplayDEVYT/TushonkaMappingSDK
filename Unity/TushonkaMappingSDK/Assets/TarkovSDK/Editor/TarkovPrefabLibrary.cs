using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EFT.GameTriggers;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // Tarkov SDK > Decompilation > Prefab Library...: a map's own prefabs, browsable and dragged into the Scene view.
    //  - Extract From Open Scenes: the important objects of a ripped (or any) scene become prefabs, one per distinct
    //    object (500 identical crates make one prefab): doors, containers, switches, lamps, windows, mounted guns,
    //    explosives, fires, trees, props and, optionally, buildings. An object's unit is the largest group above it that
    //    stays small and holds no second object of its kind (a door with its frame, not the house).
    //  - Make Prefabs From Selected: the selected scene objects, as they are, into a category.
    // Prefabs go in the map's folder (its Map Definition's, else the active scene's) under Prefabs/<Category>/; ids
    // (doors, containers, guns) are cleared, so Build Map gives each placed copy its own; meshes only in the scene
    // are saved under Prefabs/_Meshes. The scene itself isn't changed.
    public class TarkovPrefabLibrary : EditorWindow
    {
        private static readonly string[] Categories =
            { "Doors", "Containers", "Switches", "Lamps", "Windows", "Mounted Guns", "Explosives", "Fires", "Trees", "Props", "Buildings", "Custom" };

        private string _folder;
        private int _category;
        private string _search = "";
        private float _size = 96f;
        private Vector2 _scroll;
        private List<GameObject> _prefabs = new List<GameObject>();
        private bool _buildings;
        private bool _connect;
        private int _customCategory = Categories.Length - 1;
        private string _status;

        [MenuItem("Tarkov SDK/Decompilation/Prefab Library...", priority = 150)]
        public static void Open() => GetWindow<TarkovPrefabLibrary>("Prefab Library").minSize = new Vector2(420f, 360f);

        [MenuItem("Tarkov SDK/Decompilation/Make Prefabs From Selected", priority = 151)]
        private static void MenuFromSelected() => EditorApplication.delayCall += () =>
        {
            var folder = MapFolder();
            if (folder == null)
            {
                EditorUtility.DisplayDialog("Prefab Library", "Save the scene first: prefabs go in its map's folder.", "OK");
                return;
            }
            int made = FromSelected(folder, "Custom", false);
            Debug.Log($"[Tarkov SDK] {made} prefab(s) made in {folder}/Prefabs/Custom");
        };

        [MenuItem("Tarkov SDK/Decompilation/Make Prefabs From Selected", true)]
        private static bool CanFromSelected() => Selection.gameObjects.Any(g => g.scene.IsValid());

        private void OnEnable()
        {
            _folder = MapFolder();
            Refresh();
        }

        private void OnFocus() => Refresh();

        // the map's folder: its Map Definition's (the one listing the active scene), else the active scene's
        public static string MapFolder()
        {
            var scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path))
                return null;
            foreach (var guid in AssetDatabase.FindAssets("t:TarkovMapDefinition"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var definition = AssetDatabase.LoadAssetAtPath<TarkovMapDefinition>(path);
                if (definition != null && definition.Scenes.Any(s => s != null && AssetDatabase.GetAssetPath(s) == scene.path))
                    return Path.GetDirectoryName(path).Replace('\\', '/');
            }
            return Path.GetDirectoryName(scene.path).Replace('\\', '/');
        }

        private string PrefabsFolder => _folder + "/Prefabs";

        private void Refresh()
        {
            _prefabs = new List<GameObject>();
            if (string.IsNullOrEmpty(_folder) || !AssetDatabase.IsValidFolder(PrefabsFolder))
                return;
            _prefabs = AssetDatabase.FindAssets("t:Prefab", new[] { PrefabsFolder })
                .Select(g => AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(g))).Where(p => p != null).OrderBy(p => p.name).ToList();
        }

        private static string CategoryOf(GameObject prefab)
        {
            var path = AssetDatabase.GetAssetPath(prefab);
            var parts = path.Split('/');
            int i = Array.IndexOf(parts, "Prefabs");
            return i >= 0 && i + 2 < parts.Length ? parts[i + 1] : "Custom";
        }

        // ------------------------------------------------------------ window

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label(string.IsNullOrEmpty(_folder) ? "(save the scene first)" : PrefabsFolder, EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Folder...", EditorStyles.toolbarButton))
                {
                    var picked = EditorUtility.OpenFolderPanel("Map folder (prefabs go in its Prefabs/)", string.IsNullOrEmpty(_folder) ? "Assets" : _folder, "");
                    var project = Path.GetFullPath(".").Replace('\\', '/') + "/";
                    if (!string.IsNullOrEmpty(picked) && picked.Replace('\\', '/').StartsWith(project))
                    {
                        _folder = picked.Replace('\\', '/').Substring(project.Length);
                        Refresh();
                    }
                }
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton))
                {
                    _folder = string.IsNullOrEmpty(_folder) ? MapFolder() : _folder;
                    Refresh();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_folder)))
                {
                    if (GUILayout.Button(new GUIContent("Extract From Open Scenes", "Every distinct door, container, switch, lamp, window, mounted gun, explosive, fire, tree and prop becomes a prefab.")))
                        EditorApplication.delayCall += () =>
                        {
                            var counts = Extract(_folder, _buildings);
                            _status = counts.Count == 0 ? "Nothing found to extract." : "Made: " + string.Join(", ", counts.Select(c => $"{c.Value} {c.Key.ToLowerInvariant()}"));
                            Refresh();
                            Repaint();
                        };
                    if (GUILayout.Button(new GUIContent("Make Prefabs From Selected", "The selected scene objects, each as one prefab, in the category on the right.")))
                        EditorApplication.delayCall += () =>
                        {
                            int made = FromSelected(_folder, Categories[_customCategory], _connect);
                            _status = $"{made} prefab(s) made in {Categories[_customCategory]}.";
                            Refresh();
                            Repaint();
                        };
                }
                _customCategory = EditorGUILayout.Popup(_customCategory, Categories, GUILayout.Width(110f));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                _buildings = EditorGUILayout.ToggleLeft(new GUIContent("Extract buildings too", "Large objects (over 12 m) become prefabs in Buildings."), _buildings, GUILayout.Width(160f));
                _connect = EditorGUILayout.ToggleLeft(new GUIContent("Selected become instances", "Make Prefabs From Selected links the selected objects to their new prefabs."), _connect);
            }
            if (_status != null)
                EditorGUILayout.HelpBox(_status, MessageType.Info);

            var present = new[] { "All" }.Concat(Categories.Where(c => _prefabs.Any(p => CategoryOf(p) == c))).ToArray();
            _category = Mathf.Clamp(_category, 0, present.Length - 1);
            _category = GUILayout.Toolbar(_category, present, EditorStyles.toolbarButton);
            using (new EditorGUILayout.HorizontalScope())
            {
                _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);
                _size = GUILayout.HorizontalSlider(_size, 56f, 160f, GUILayout.Width(90f));
            }

            var shown = _prefabs.Where(p => p != null && (_category == 0 || CategoryOf(p) == present[_category]) &&
                                            (string.IsNullOrEmpty(_search) || p.name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            if (shown.Count == 0)
            {
                EditorGUILayout.HelpBox(_prefabs.Count == 0 ? "No prefabs yet. Open a ripped scene and press Extract From Open Scenes, or select objects and press Make Prefabs From Selected."
                    : "No prefab matches.", MessageType.None);
                return;
            }
            EditorGUILayout.LabelField("Drag one into the Scene view, or double-click to place it at the view's centre.", EditorStyles.miniLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            int columns = Mathf.Max(1, (int)((position.width - 20f) / (_size + 6f)));
            bool loading = false;
            for (int i = 0; i < shown.Count; i += columns)
                using (new EditorGUILayout.HorizontalScope())
                    for (int j = i; j < Mathf.Min(i + columns, shown.Count); j++)
                        loading |= Cell(shown[j]);
            EditorGUILayout.EndScrollView();
            if (loading || AssetPreview.IsLoadingAssetPreviews())
                Repaint();
        }

        // a thumbnail with its name; true while its preview is loading
        private bool Cell(GameObject prefab)
        {
            var rect = GUILayoutUtility.GetRect(_size, _size + 16f, GUILayout.Width(_size), GUILayout.Height(_size + 16f));
            var image = new Rect(rect.x, rect.y, _size, _size);
            var preview = AssetPreview.GetAssetPreview(prefab);
            bool loading = preview == null;
            GUI.Box(image, preview != null ? (Texture)preview : AssetPreview.GetMiniThumbnail(prefab));
            GUI.Label(new Rect(rect.x, rect.y + _size, _size, 16f), new GUIContent(prefab.name, prefab.name), EditorStyles.centeredGreyMiniLabel);
            var e = Event.current;
            if (rect.Contains(e.mousePosition))
            {
                if (e.type == EventType.MouseDown && e.button == 0)
                {
                    if (e.clickCount == 2)
                        EditorApplication.delayCall += () => Place(prefab);
                    else
                        EditorGUIUtility.PingObject(prefab);
                    e.Use();
                }
                else if (e.type == EventType.MouseDrag)
                {
                    DragAndDrop.PrepareStartDrag();
                    DragAndDrop.objectReferences = new Object[] { prefab };
                    DragAndDrop.paths = new[] { AssetDatabase.GetAssetPath(prefab) };
                    DragAndDrop.StartDrag(prefab.name);
                    e.Use();
                }
            }
            return loading;
        }

        // an instance on whatever is under the Scene view's centre
        private static void Place(GameObject prefab)
        {
            var view = SceneView.lastActiveSceneView;
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, SceneManager.GetActiveScene());
            if (view != null)
            {
                var at = view.pivot;
                if (Physics.Raycast(view.camera.transform.position, view.camera.transform.forward, out var hit, 2000f, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore))
                    at = hit.point;
                instance.transform.position = at;
            }
            Undo.RegisterCreatedObjectUndo(instance, "Place " + prefab.name);
            Selection.activeGameObject = instance;
        }

        // ------------------------------------------------------------ extract

        private const float UnitSize = 8f;
        private const float BuildingSize = 12f;

        // the categories' kinds: the component that makes an object one, and how big its unit may get
        private static readonly (string Category, Type Type, float MaxSize)[] Kinds =
        {
            ("Doors", typeof(Door), UnitSize),
            ("Containers", typeof(LootableContainer), UnitSize),
            ("Switches", typeof(Switch), 3f),
            ("Lamps", typeof(LampController), UnitSize),
            ("Windows", typeof(WindowBreaker), UnitSize),
            ("Mounted Guns", typeof(StationaryWeapon), UnitSize),
            ("Explosives", typeof(TriggerBallistic), 4f),
            ("Fires", typeof(FlameDamageTrigger), 6f),
        };

        public static Dictionary<string, int> Extract(string folder, bool buildings)
        {
            var counts = new Dictionary<string, int>();
            var taken = new HashSet<Transform>();
            var signatures = new HashSet<string>();
            var units = new List<(string Category, Transform Root)>();
            var roots = Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Where(s => s.isLoaded)
                .SelectMany(s => s.GetRootGameObjects()).ToList();

            foreach (var (category, type, maxSize) in Kinds)
                foreach (var component in roots.SelectMany(r => r.GetComponentsInChildren(type, true)))
                {
                    if (IsSdk(component.transform))
                        continue;
                    var root = Unit(component.transform, type, maxSize);
                    if (taken.Any(t => root.IsChildOf(t)))
                        continue;
                    taken.Add(root);
                    units.Add((category, root));
                }
            // trees: LOD groups with the game's wind (or a SpeedTree shader); props and buildings: other LOD groups
            foreach (var group in roots.SelectMany(r => r.GetComponentsInChildren<LODGroup>(true)))
            {
                var t = group.transform;
                if (IsSdk(t) || taken.Any(x => t.IsChildOf(x)) || t.GetComponentsInParent<LODGroup>(true).Length > 1)
                    continue;
                var size = Bounds(t).size;
                float largest = Mathf.Max(size.x, size.y, size.z);
                bool tree = t.GetComponentInChildren<EFT.SpeedTree.TreeWind>(true) != null ||
                            t.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Any(m => m != null && m.shader != null && m.shader.name.Contains("SpeedTree"));
                string category = tree ? "Trees" : largest > BuildingSize ? "Buildings" : "Props";
                if (category == "Buildings" && !buildings)
                    continue;
                taken.Add(t);
                units.Add((category, t));
            }

            try
            {
                for (int i = 0; i < units.Count; i++)
                {
                    var (category, root) = units[i];
                    if (i % 20 == 0 && EditorUtility.DisplayCancelableProgressBar("Extract Prefabs", root.name, i / (float)units.Count))
                        break;
                    var signature = category + "|" + Signature(root);
                    if (!signatures.Add(signature))
                        continue;
                    if (Save(root.gameObject, folder, category) != null)
                        counts[category] = counts.TryGetValue(category, out var n) ? n + 1 : 1;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[Tarkov SDK] Prefab Library: {units.Count} object(s) found, " +
                      (counts.Count == 0 ? "no new prefabs" : string.Join(", ", counts.Select(c => $"{c.Value} {c.Key.ToLowerInvariant()}")) + " prefab(s) made") + $" in {folder}/Prefabs");
            return counts;
        }

        // the SDK's own generated and EditorOnly objects stay out
        private static bool IsSdk(Transform t) => t.GetComponentsInParent<Transform>(true).Any(x => x.CompareTag("EditorOnly") || x.name.StartsWith("SDK_"));

        // the largest group above the object that stays within maxSize and holds no other object of its kind
        private static Transform Unit(Transform t, Type kind, float maxSize)
        {
            var unit = t;
            for (var parent = t.parent; parent != null; parent = parent.parent)
            {
                if (parent.parent == null && parent.childCount > 8)
                    break;
                if (parent.GetComponentsInChildren(kind, true).Length > 1)
                    break;
                var size = Bounds(parent).size;
                if (Mathf.Max(size.x, size.y, size.z) > maxSize)
                    break;
                unit = parent;
            }
            return unit;
        }

        private static Bounds Bounds(Transform t)
        {
            var renderers = t.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return new Bounds(t.position, Vector3.zero);
            var bounds = renderers[0].bounds;
            foreach (var r in renderers)
                bounds.Encapsulate(r.bounds);
            return bounds;
        }

        // what makes two objects the same prefab: their meshes, materials and components, in hierarchy order
        private static string Signature(Transform root)
        {
            var parts = new List<string>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var filter = t.GetComponent<MeshFilter>();
                var renderer = t.GetComponent<Renderer>();
                parts.Add(string.Join(",", t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name)) +
                          (filter != null && filter.sharedMesh != null ? ":" + filter.sharedMesh.name + filter.sharedMesh.vertexCount : "") +
                          (renderer != null ? ":" + string.Join("+", renderer.sharedMaterials.Select(m => m != null ? m.name : "-")) : ""));
            }
            return string.Join("/", parts).GetHashCode() + ":" + parts.Count;
        }

        // ------------------------------------------------------------ save

        public static int FromSelected(string folder, string category, bool connect)
        {
            int made = 0;
            foreach (var go in Selection.gameObjects.Where(g => g.scene.IsValid()))
            {
                if (connect)
                {
                    var path = PathFor(folder, category, go.name);
                    StoreMeshes(go, folder);
                    if (PrefabUtility.IsPartOfPrefabInstance(go))
                        PrefabUtility.UnpackPrefabInstance(PrefabUtility.GetOutermostPrefabInstanceRoot(go), PrefabUnpackMode.Completely, InteractionMode.UserAction);
                    if (PrefabUtility.SaveAsPrefabAssetAndConnect(go, path, InteractionMode.UserAction) != null)
                        made++;
                }
                else if (Save(go, folder, category) != null)
                    made++;
            }
            AssetDatabase.SaveAssets();
            return made;
        }

        // a copy of the object, at the origin, ids cleared, saved as a prefab; the scene object is left alone
        public static GameObject Save(GameObject source, string folder, string category)
        {
            var copy = Object.Instantiate(source);
            try
            {
                copy.name = Clean(source.name);
                copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                copy.transform.localScale = source.transform.lossyScale;
                foreach (var t in copy.GetComponentsInChildren<Transform>(true))
                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                // Build Map gives every placed copy its own
                foreach (var wio in copy.GetComponentsInChildren<WorldInteractiveObject>(true))
                    wio.Id = "";
                foreach (var weapon in copy.GetComponentsInChildren<StationaryWeapon>(true))
                    weapon._id = "";
                foreach (var group in copy.GetComponentsInChildren<LootableContainersGroup>(true))
                    group.Id = "";
                foreach (var sync in copy.GetComponentsInChildren<ISyncAble>(true))
                    sync._random = 0;
                StoreMeshes(copy, folder);
                var path = PathFor(folder, category, copy.name);
                return PrefabUtility.SaveAsPrefabAsset(copy, path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tarkov SDK] Prefab Library: '{source.name}' not saved ({e.Message})");
                return null;
            }
            finally
            {
                Object.DestroyImmediate(copy);
            }
        }

        // "Crate_02 (17)" -> "Crate_02"
        private static string Clean(string name)
        {
            name = Regex.Replace(name, @"\s*\(\d+\)$", "");
            name = Regex.Replace(name, @"\(Clone\)", "");
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "Prefab" : name.Trim();
        }

        private static string PathFor(string folder, string category, string name)
        {
            var dir = $"{folder}/Prefabs/{category}";
            TarkovGeneratedAssets.EnsureFolder(dir);
            return AssetDatabase.GenerateUniqueAssetPath($"{dir}/{Clean(name)}.prefab");
        }

        // meshes that only live in the scene (generated, ProBuilder) are saved as assets so the prefab keeps them
        private static void StoreMeshes(GameObject go, string folder)
        {
            var dir = $"{folder}/Prefabs/_Meshes";
            var saved = new Dictionary<Mesh, Mesh>();
            Mesh Asset(Mesh mesh)
            {
                if (mesh == null || AssetDatabase.Contains(mesh))
                    return mesh;
                if (saved.TryGetValue(mesh, out var done))
                    return done;
                TarkovGeneratedAssets.EnsureFolder(dir);
                var copy = Object.Instantiate(mesh);
                copy.name = mesh.name;
                AssetDatabase.CreateAsset(copy, AssetDatabase.GenerateUniqueAssetPath($"{dir}/{Clean(mesh.name)}.asset"));
                saved[mesh] = copy;
                return copy;
            }
            foreach (var filter in go.GetComponentsInChildren<MeshFilter>(true))
                filter.sharedMesh = Asset(filter.sharedMesh);
            foreach (var collider in go.GetComponentsInChildren<MeshCollider>(true))
                collider.sharedMesh = Asset(collider.sharedMesh);
            foreach (var skinned in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                skinned.sharedMesh = Asset(skinned.sharedMesh);
        }
    }
}
