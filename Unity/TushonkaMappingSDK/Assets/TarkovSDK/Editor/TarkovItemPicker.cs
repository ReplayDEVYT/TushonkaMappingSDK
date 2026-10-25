using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Search SPT's item database by name and pick one, as the loot spawn inspector and the Server Data
    // Editor's key search do. Keep a State per field so each search remembers its text and scroll.
    public static class TarkovItemPicker
    {
        public class State
        {
            public string Search = "";
            public Vector2 Scroll;
        }

        // returns the picked item's template, or null
        public static string Draw(State state, string button = "Pick")
        {
            var items = TarkovLootSpawnEditor.ItemDatabase.Items;
            if (items == null)
            {
                EditorGUILayout.HelpBox(TarkovLootSpawnEditor.ItemDatabase.Error ?? "Load the item database to search items by name.", MessageType.Info);
                if (GUILayout.Button("Load Item Database"))
                    TarkovLootSpawnEditor.ItemDatabase.Load();
                return null;
            }
            state.Search = EditorGUILayout.TextField("Search Items", state.Search);
            if (state.Search.Length < 2)
                return null;
            string picked = null;
            var matches = items.Where(i => i.Name.IndexOf(state.Search, StringComparison.OrdinalIgnoreCase) >= 0
                                           || i.Template.StartsWith(state.Search, StringComparison.OrdinalIgnoreCase)).Take(40).ToList();
            state.Scroll = EditorGUILayout.BeginScrollView(state.Scroll, GUILayout.MaxHeight(180f));
            foreach (var item in matches)
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(item.Name, GUILayout.MinWidth(120f));
                    EditorGUILayout.LabelField(item.Category, EditorStyles.miniLabel, GUILayout.Width(110f));
                    if (GUILayout.Button(button, GUILayout.Width(50f)))
                        picked = item.Template;
                }
            EditorGUILayout.EndScrollView();
            return picked;
        }

        public static string Name(string template)
        {
            var items = TarkovLootSpawnEditor.ItemDatabase.Items;
            if (string.IsNullOrEmpty(template))
                return "(none)";
            if (items == null)
                return template;
            int index = items.FindIndex(i => string.Equals(i.Template, template, StringComparison.OrdinalIgnoreCase));
            return index >= 0 ? items[index].Name : $"{template} (unknown item)";
        }
    }
}
