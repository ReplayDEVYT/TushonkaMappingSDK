using System.Collections.Generic;
using System.IO;
using System.Linq;
using EFT.Interactive;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Loot container types (TarkovContainerDefinition assets): new containers with their own grid, name and loot,
    // which CustomMapLoader's server mod makes from a retail container (Tarkov has no way to add one), and retail
    // types whose loot a map changes. A scene's LootableContainer uses one by its Template; Build Map exports the
    // types the map's containers use (TarkovExportJson "ContainerTypes").
    public static class TarkovLootContainers
    {
        public const string GearStashName = "Gear Stash";

        public static List<TarkovContainerDefinition> All() =>
            AssetDatabase.FindAssets("t:" + nameof(TarkovContainerDefinition))
                .Select(g => AssetDatabase.LoadAssetAtPath<TarkovContainerDefinition>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(d => d != null).OrderBy(d => d.name).ToList();

        public static TarkovContainerDefinition Find(string template)
        {
            if (string.IsNullOrEmpty(template))
                return null;
            template = template.Trim().ToLowerInvariant();
            return All().FirstOrDefault(d => (d.Template ?? "").Trim().ToLowerInvariant() == template);
        }

        // next to the active scene: <scene folder>/<scene>_LootContainers
        public static string Folder()
        {
            var scenePath = SceneManager.GetActiveScene().path;
            var folder = string.IsNullOrEmpty(scenePath) ? "Assets/LootContainers"
                : $"{Path.GetDirectoryName(scenePath).Replace('\\', '/')}/{Path.GetFileNameWithoutExtension(scenePath)}_LootContainers";
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder(Path.GetDirectoryName(folder).Replace('\\', '/'), Path.GetFileName(folder));
            return folder;
        }

        public static TarkovContainerDefinition Create(string name, bool newContainer, int width, int height, TarkovContainerDefinition.LootSource loot)
        {
            var definition = ScriptableObject.CreateInstance<TarkovContainerDefinition>();
            definition.NewContainer = newContainer;
            definition.Template = newContainer ? TarkovMongoId.New() : TarkovPrefabTools.WoodenCrateTemplate;
            definition.DisplayName = name;
            definition.ShortName = name;
            definition.Width = width;
            definition.Height = height;
            definition.Loot = loot;
            var path = AssetDatabase.GenerateUniqueAssetPath($"{Folder()}/{name}.asset");
            AssetDatabase.CreateAsset(definition, path);
            AssetDatabase.SaveAssets();
            return definition;
        }

        // the empty 10 x 16 container gear stripping moves gear into: holds a full kit
        public static TarkovContainerDefinition GearStash() =>
            All().FirstOrDefault(d => d.NewContainer && d.DisplayName == GearStashName && d.Loot == TarkovContainerDefinition.LootSource.Empty)
            ?? Create(GearStashName, true, 10, 16, TarkovContainerDefinition.LootSource.Empty);

        public static LootableContainer Place(TarkovContainerDefinition definition, Transform parent, Vector3 position)
        {
            var container = TarkovPrefabTools.CreateContainer(parent);
            container.transform.position = position;
            container.name = definition.DisplayName;
            Use(container, definition);
            Undo.RegisterCreatedObjectUndo(container.gameObject, "Place " + definition.DisplayName);
            return container;
        }

        public static void Use(LootableContainer container, TarkovContainerDefinition definition)
        {
            Undo.RecordObject(container, "Container type");
            container.Template = definition.Template;
            EditorUtility.SetDirty(container);
        }

        // the definitions the given containers use, by Template
        public static List<TarkovContainerDefinition> UsedBy(IEnumerable<LootableContainer> containers)
        {
            var templates = new HashSet<string>(containers.Where(c => c != null && !string.IsNullOrEmpty(c.Template)).Select(c => c.Template.Trim().ToLowerInvariant()));
            return All().Where(d => templates.Contains((d.Template ?? "").Trim().ToLowerInvariant())).ToList();
        }
    }

    // Tarkov SDK > Tools > Interactables > Loot Containers: makes and edits container types, and places them
    public class TarkovLootContainersWindow : EditorWindow
    {
        private TarkovContainerDefinition _selected;
        private UnityEditor.Editor _editor;
        private Vector2 _scroll;

        [MenuItem("Tarkov SDK/Tools/Interactables/Loot Containers...", priority = 117)]
        public static void Open() => GetWindow<TarkovLootContainersWindow>("Loot Containers").minSize = new Vector2(380f, 420f);

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Container types", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("A new type is a new item with its own grid, name and loot; a retail type keeps its item and only its loot changes on maps that use it here. " +
                                       "A container in the scene uses a type by its Template.", EditorStyles.wordWrappedMiniLabel);
            var all = TarkovLootContainers.All();
            foreach (var definition in all)
                using (new EditorGUILayout.HorizontalScope())
                {
                    var style = definition == _selected ? EditorStyles.boldLabel : EditorStyles.label;
                    if (GUILayout.Button($"{definition.DisplayName}  ({(definition.NewContainer ? $"{definition.Width} x {definition.Height}" : "retail loot")}, {definition.Loot})", style))
                        Select(definition);
                }
            if (all.Count == 0)
                EditorGUILayout.LabelField("None yet.", EditorStyles.miniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("New Container Type"))
                    EditorApplication.delayCall += () => Select(TarkovLootContainers.Create("New Container", true, 4, 4, TarkovContainerDefinition.LootSource.Custom));
                if (GUILayout.Button("Change Retail Container Loot"))
                    EditorApplication.delayCall += () => Select(TarkovLootContainers.Create("Retail Loot", false, 5, 2, TarkovContainerDefinition.LootSource.RetailPlusCustom));
            }

            if (_selected != null)
            {
                EditorGUILayout.Space();
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Place In Scene"))
                        EditorApplication.delayCall += () =>
                        {
                            var view = SceneView.lastActiveSceneView;
                            var at = view != null ? view.pivot : Vector3.zero;
                            Selection.activeGameObject = TarkovLootContainers.Place(_selected, null, at).gameObject;
                            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                        };
                    var containers = Selection.gameObjects.Select(g => g.GetComponent<LootableContainer>()).Where(c => c != null).ToList();
                    using (new EditorGUI.DisabledScope(containers.Count == 0))
                        if (GUILayout.Button(containers.Count > 1 ? $"Use On {containers.Count} Selected Containers" : "Use On Selected Container"))
                            foreach (var container in containers)
                                TarkovLootContainers.Use(container, _selected);
                }
                EditorGUILayout.Space();
                if (_editor == null || _editor.target != _selected)
                    UnityEditor.Editor.CreateCachedEditor(_selected, typeof(TarkovContainerDefinitionEditor), ref _editor);
                _editor.OnInspectorGUI();
            }
            EditorGUILayout.EndScrollView();
        }

        private void Select(TarkovContainerDefinition definition)
        {
            _selected = definition;
            Repaint();
        }

        private void OnSelectionChange() => Repaint();
    }

    [CustomEditor(typeof(TarkovContainerDefinition))]
    public class TarkovContainerDefinitionEditor : UnityEditor.Editor
    {
        private string _search = "";
        private Vector2 _scroll;

        public override void OnInspectorGUI()
        {
            var definition = (TarkovContainerDefinition)target;
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(TarkovContainerDefinition.NewContainer)));
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(TarkovContainerDefinition.Template)));
                if (definition.NewContainer && GUILayout.Button("New Id", GUILayout.Width(60f)))
                    serializedObject.FindProperty(nameof(TarkovContainerDefinition.Template)).stringValue = TarkovMongoId.New();
            }
            if (!TarkovMongoId.IsValid(definition.Template))
                EditorGUILayout.HelpBox("The Template must be 24 hex characters.", MessageType.Error);
            else if (definition.NewContainer && ItemName(definition.Template) is string taken)
                EditorGUILayout.HelpBox($"'{taken}' already uses this id; press New Id.", MessageType.Error);
            else if (!definition.NewContainer && ItemDatabaseLoaded && ItemName(definition.Template) == null)
                EditorGUILayout.HelpBox("No item has this id. A retail type is a container's item id, e.g. Wooden crate 578f87ad245977356274f2cc.", MessageType.Warning);

            if (definition.NewContainer)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(TarkovContainerDefinition.DisplayName)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(TarkovContainerDefinition.ShortName)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(TarkovContainerDefinition.Description)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(TarkovContainerDefinition.Width)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(TarkovContainerDefinition.Height)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(TarkovContainerDefinition.BaseTemplate)));
            }
            else
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(TarkovContainerDefinition.DisplayName)), new GUIContent("Name (editor only)"));

            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(TarkovContainerDefinition.Loot)));
            var loot = definition.Loot;
            if (loot == TarkovContainerDefinition.LootSource.CopyRetail || loot == TarkovContainerDefinition.LootSource.RetailPlusCustom)
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(TarkovContainerDefinition.RetailLootTemplate)), new GUIContent("Retail Loot From"));
            if (loot == TarkovContainerDefinition.LootSource.Custom)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(TarkovContainerDefinition.MinItems)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(TarkovContainerDefinition.MaxItems)));
            }
            if (loot == TarkovContainerDefinition.LootSource.Custom || loot == TarkovContainerDefinition.LootSource.RetailPlusCustom)
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(TarkovContainerDefinition.Items)), true);
            serializedObject.ApplyModifiedProperties();

            if (loot == TarkovContainerDefinition.LootSource.Custom || loot == TarkovContainerDefinition.LootSource.RetailPlusCustom)
                DrawItemPicker(definition);
        }

        private static bool ItemDatabaseLoaded => TarkovLootSpawnEditor.ItemDatabase.Items != null;

        private static string ItemName(string template)
        {
            var items = TarkovLootSpawnEditor.ItemDatabase.Items;
            if (items == null || string.IsNullOrEmpty(template))
                return null;
            var t = template.Trim().ToLowerInvariant();
            var match = items.FirstOrDefault(i => i.Template == t);
            return match.Template == null ? null : match.Name;
        }

        private void DrawItemPicker(TarkovContainerDefinition definition)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Add Items", EditorStyles.boldLabel);
            var items = TarkovLootSpawnEditor.ItemDatabase.Items;
            if (items == null)
            {
                EditorGUILayout.HelpBox(TarkovLootSpawnEditor.ItemDatabase.Error ?? "Item database not loaded.", MessageType.Info);
                if (GUILayout.Button("Load Item Database"))
                    TarkovLootSpawnEditor.ItemDatabase.Load();
                return;
            }
            _search = EditorGUILayout.TextField("Search", _search);
            if (_search.Length < 2)
                return;
            var matches = items.Where(i => i.Name.IndexOf(_search, System.StringComparison.OrdinalIgnoreCase) >= 0
                                           || i.Template.StartsWith(_search, System.StringComparison.OrdinalIgnoreCase)).Take(40).ToList();
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MaxHeight(220f));
            foreach (var item in matches)
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(item.Name, GUILayout.MinWidth(120f));
                    EditorGUILayout.LabelField(item.Category, EditorStyles.miniLabel, GUILayout.Width(110f));
                    if (GUILayout.Button("Add", GUILayout.Width(44f)))
                    {
                        Undo.RecordObject(definition, "Add loot item");
                        definition.Items.Add(new TarkovContainerDefinition.Item { Template = item.Template, Name = item.Name });
                        EditorUtility.SetDirty(definition);
                    }
                }
            EditorGUILayout.EndScrollView();
        }
    }
}
