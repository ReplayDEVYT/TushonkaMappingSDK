using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Inspector for loot spawns: search the game's items by name (from the SPT install's database)
    // and add them, instead of typing template ids.
    [CustomEditor(typeof(TarkovLootSpawn)), CanEditMultipleObjects]
    public class TarkovLootSpawnEditor : UnityEditor.Editor
    {
        private string _search = "";
        private Vector2 _scroll;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var spawn = (TarkovLootSpawn)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Add Items", EditorStyles.boldLabel);
            var items = ItemDatabase.Items;
            if (items == null)
            {
                EditorGUILayout.HelpBox(ItemDatabase.Error ?? "Item database not loaded.", MessageType.Info);
                if (GUILayout.Button("Load Item Database"))
                    ItemDatabase.Load();
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
                        foreach (var t in targets)
                        {
                            var s = (TarkovLootSpawn)t;
                            Undo.RecordObject(s, "Add loot item");
                            s.Items.Add(new TarkovLootSpawn.Item { Template = item.Template, Name = item.Name });
                            EditorUtility.SetDirty(s);
                        }
                }
            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------ item database

        public static class ItemDatabase
        {
            public struct Entry
            {
                public string Template, Name, Category;
            }

            public static List<Entry> Items { get; private set; }
            public static string Error { get; private set; }

            public static void Load()
            {
                var root = SptRoot();
                var itemsPath = Path.Combine(root ?? "", "SPT_Runtime", "SPT_Data", "database", "templates", "items.json");
                var localePath = Path.Combine(root ?? "", "SPT_Runtime", "SPT_Data", "database", "locales", "global", "en.json");
                if (root == null || !File.Exists(itemsPath))
                {
                    Error = $"No SPT item database at {itemsPath}. Set Spt Root on a Map Definition.";
                    return;
                }
                try
                {
                    EditorUtility.DisplayProgressBar("Tarkov SDK", "Loading the item database", 0.5f);
                    var items = JObject.Parse(File.ReadAllText(itemsPath));
                    var locale = File.Exists(localePath) ? JObject.Parse(File.ReadAllText(localePath)) : new JObject();
                    var list = new List<Entry>();
                    foreach (var p in items.Properties())
                    {
                        if (p.Value.Value<string>("_type") != "Item")
                            continue;
                        var parent = p.Value.Value<string>("_parent");
                        var category = parent != null ? items[parent]?.Value<string>("_name") ?? "" : "";
                        var name = locale.Value<string>($"{p.Name} Name") ?? p.Value.Value<string>("_name") ?? p.Name;
                        list.Add(new Entry { Template = p.Name, Name = name, Category = category });
                    }
                    Items = list.OrderBy(e => e.Name).ToList();
                    Error = null;
                }
                catch (System.Exception e)
                {
                    Error = "Could not read the item database: " + e.Message;
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
            }

            private static string SptRoot()
            {
                foreach (var guid in AssetDatabase.FindAssets("t:TarkovMapDefinition"))
                {
                    var def = AssetDatabase.LoadAssetAtPath<TarkovMapDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                    if (def != null && !string.IsNullOrEmpty(def.SptRoot))
                        return def.SptRoot;
                }
                return null;
            }
        }
    }
}
