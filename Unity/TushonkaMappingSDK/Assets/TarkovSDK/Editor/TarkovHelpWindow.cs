using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Tarkov SDK > Help: how to do things in the SDK, tab by tab, and the Errors tab: every message Build Map logs with
    // what it means and how to fix it, the last build's own messages explained, and a box to paste a Console message
    // into. Its text is TarkovHelpContent.
    public class TarkovHelpWindow : EditorWindow
    {
        private const string LastBuildKey = "TarkovSDK.Help.LastBuild";

        private int _tab;
        private string _search = "";
        private string _pasted = "";
        private Vector2 _scroll;
        private readonly HashSet<string> _open = new HashSet<string>();
        private GUIStyle _text;

        [MenuItem("Tarkov SDK/Help...", priority = 240)]
        public static void Open() => Open(null);

        // opens on a tab by name (Errors after a failed build)
        public static void Open(string tab)
        {
            var window = GetWindow<TarkovHelpWindow>("Tarkov SDK Help");
            window.minSize = new Vector2(480f, 420f);
            if (tab != null)
            {
                var names = TabNames();
                int index = Array.IndexOf(names, tab);
                if (index >= 0)
                    window._tab = index;
            }
            window.Show();
        }

        // Build Map's messages, kept for the Errors tab for the rest of the editor session
        public static void RememberBuild(IEnumerable<string> errors, IEnumerable<string> warnings)
        {
            var lines = errors.Select(e => "E" + e).Concat(warnings.Select(w => "W" + w));
            SessionState.SetString(LastBuildKey, string.Join("\n", lines.Select(l => l.Replace('\n', ' '))));
        }

        private static string[] TabNames() => TarkovHelpContent.Tabs.Select(t => t.Name).Append("Errors").ToArray();

        private void OnGUI()
        {
            _text ??= new GUIStyle(EditorStyles.wordWrappedLabel) { richText = true };
            var names = TabNames();
            _tab = GUILayout.SelectionGrid(_tab, names, Mathf.Min(names.Length, Mathf.Max(3, (int)(position.width / 105f))));
            _search = EditorGUILayout.TextField("Search", _search);
            EditorGUILayout.Space();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (_search.Length >= 2)
                DrawSearch();
            else if (_tab == names.Length - 1)
                DrawErrors();
            else
                DrawTab(TarkovHelpContent.Tabs[_tab]);
            EditorGUILayout.EndScrollView();
        }

        private void DrawTab(TarkovHelpContent.Tab tab)
        {
            foreach (var section in tab.Sections)
            {
                EditorGUILayout.LabelField(section.Title, EditorStyles.boldLabel);
                EditorGUILayout.LabelField(section.Text, _text);
                DrawLink(section);
                EditorGUILayout.Space();
            }
        }

        private static void DrawLink(TarkovHelpContent.Section section)
        {
            if (!string.IsNullOrEmpty(section.Link) && EditorGUILayout.LinkButton(section.Link))
                Application.OpenURL(section.Link);
        }

        private void DrawSearch()
        {
            bool any = false;
            foreach (var tab in TarkovHelpContent.Tabs)
                foreach (var section in tab.Sections.Where(s => Contains(s.Title, _search) || Contains(s.Text, _search)))
                {
                    any = true;
                    EditorGUILayout.LabelField($"{tab.Name}: {section.Title}", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(section.Text, _text);
                    DrawLink(section);
                    EditorGUILayout.Space();
                }
            foreach (var message in TarkovHelpContent.Messages.Where(m => Contains(m.Key, _search) || Contains(m.Meaning, _search) || Contains(m.Fix, _search) || Contains(m.Area, _search)))
            {
                any = true;
                DrawMessage(message, null);
            }
            if (!any)
                EditorGUILayout.LabelField("Nothing found.", EditorStyles.miniLabel);
        }

        private void DrawErrors()
        {
            EditorGUILayout.LabelField("Build Map's messages", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Errors (red) stop the build; warnings (yellow) don't, but say what won't work in the raid. Paste a message from the Console " +
                                       "to find it, or look through the list by area.", EditorStyles.wordWrappedMiniLabel);
            _pasted = EditorGUILayout.TextField("Message", _pasted);
            if (_pasted.Length > 0)
            {
                var found = Find(_pasted);
                if (found != null)
                    DrawMessage(found, _pasted, open: true);
                else
                    EditorGUILayout.HelpBox("Not a message Build Map knows. Messages that start with [Tarkov SDK] come from the SDK; others come from Unity or a package.", MessageType.Info);
            }

            var last = SessionState.GetString(LastBuildKey, "");
            if (last.Length > 0)
            {
                EditorGUILayout.Space();
                var lines = last.Split('\n').Where(l => l.Length > 1).ToList();
                EditorGUILayout.LabelField($"Your last build: {lines.Count(l => l[0] == 'E')} error(s), {lines.Count(l => l[0] == 'W')} warning(s)", EditorStyles.boldLabel);
                foreach (var line in lines)
                {
                    var text = line.Substring(1);
                    var message = Find(text);
                    if (message != null)
                        DrawMessage(message, text);
                    else
                        Box(text, line[0] == 'E' ? MessageType.Error : MessageType.Warning);
                }
            }

            EditorGUILayout.Space();
            foreach (var area in TarkovHelpContent.Messages.GroupBy(m => m.Area))
            {
                bool open = _open.Contains("area:" + area.Key);
                bool next = EditorGUILayout.Foldout(open, $"{area.Key} ({area.Count()})", true);
                if (next != open)
                {
                    if (next)
                        _open.Add("area:" + area.Key);
                    else
                        _open.Remove("area:" + area.Key);
                }
                if (!next)
                    continue;
                EditorGUI.indentLevel++;
                foreach (var message in area)
                    DrawMessage(message, null);
                EditorGUI.indentLevel--;
            }
        }

        private void DrawMessage(TarkovHelpContent.Message message, string actual, bool open = false)
        {
            var type = message.Severity == TarkovHelpContent.Severity.Error ? MessageType.Error : message.Severity == TarkovHelpContent.Severity.Warning ? MessageType.Warning : MessageType.Info;
            var title = actual ?? $"... {message.Key} ...";
            var key = message.Key + "|" + actual;
            bool expanded = open || _open.Contains(key);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var icon = EditorGUIUtility.IconContent(type == MessageType.Error ? "console.erroricon.sml" : type == MessageType.Warning ? "console.warnicon.sml" : "console.infoicon.sml");
                    GUILayout.Label(icon, GUILayout.Width(18f));
                    EditorGUILayout.LabelField(title, EditorStyles.wordWrappedLabel);
                    if (!open && GUILayout.Button(expanded ? "Less" : "More", GUILayout.Width(44f)))
                    {
                        if (expanded)
                            _open.Remove(key);
                        else
                            _open.Add(key);
                    }
                }
                if (!expanded)
                    return;
                if (!string.IsNullOrEmpty(message.Meaning))
                    EditorGUILayout.LabelField("<b>What it means:</b> " + message.Meaning, _text);
                if (!string.IsNullOrEmpty(message.Fix))
                    EditorGUILayout.LabelField("<b>Fix:</b> " + message.Fix, _text);
            }
        }

        private static void Box(string text, MessageType type) => EditorGUILayout.HelpBox(text, type);

        // the first entry whose key is in the message
        public static TarkovHelpContent.Message Find(string text) =>
            TarkovHelpContent.Messages.FirstOrDefault(m => text.IndexOf(m.Key, StringComparison.OrdinalIgnoreCase) >= 0);

        private static bool Contains(string text, string part) => !string.IsNullOrEmpty(text) && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
