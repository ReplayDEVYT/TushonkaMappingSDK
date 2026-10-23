using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Pictures for the map select and loading screens (pack backgrounds, location icons, previews,
    // banners). The game loads them from the SPT server as files, so they are copied out of the
    // project as they are: PNG or JPG only, whatever Unity's import settings say.
    //
    // Copies are named <slot>_<content hash>.<ext>: a changed picture gets a new name, so the game
    // never shows one it cached under the old name.
    public static class TarkovMenuPictures
    {
        public static bool IsSupported(Texture2D texture, out string error)
        {
            error = null;
            if (texture == null)
                return true;
            var path = AssetDatabase.GetAssetPath(texture);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
                return true;
            error = $"'{texture.name}' is a {(string.IsNullOrEmpty(ext) ? "built-in" : ext)} texture; use a .png or .jpg file from the project.";
            return false;
        }

        // copies the texture's source file into folder and returns its new file name
        public static string Copy(Texture2D texture, string folder, string slot)
        {
            var source = AssetDatabase.GetAssetPath(texture);
            var bytes = File.ReadAllBytes(source);
            string hash;
            using (var md5 = MD5.Create())
                hash = string.Concat(md5.ComputeHash(bytes).Take(4).Select(b => b.ToString("x2")));
            var ext = Path.GetExtension(source).ToLowerInvariant();
            var name = $"{slot}_{hash}{(ext == ".jpeg" ? ".jpg" : ext)}";
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, name), bytes);
            return name;
        }

        // removes earlier copies nothing names any more
        public static void DeleteUnused(string folder, IEnumerable<string> keep, IEnumerable<string> slots)
        {
            if (!Directory.Exists(folder))
                return;
            var used = new HashSet<string>(keep.Where(k => !string.IsNullOrEmpty(k)), StringComparer.OrdinalIgnoreCase);
            var prefixes = slots.Select(s => s + "_").ToArray();
            foreach (var file in Directory.GetFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (!used.Contains(name) && prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    File.Delete(file);
            }
        }

        private static readonly Dictionary<string, (DateTime Time, Texture2D Texture)> Loaded = new Dictionary<string, (DateTime, Texture2D)>();

        // a picture file outside the project, for previews; null if missing
        public static Texture2D Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;
            var time = File.GetLastWriteTimeUtc(path);
            if (Loaded.TryGetValue(path, out var cached) && cached.Time == time && cached.Texture != null)
                return cached.Texture;
            var texture = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave, name = Path.GetFileName(path) };
            if (!texture.LoadImage(File.ReadAllBytes(path)))
            {
                UnityEngine.Object.DestroyImmediate(texture);
                return null;
            }
            Loaded[path] = (time, texture);
            return texture;
        }

        // a thumbnail with a texture picker; returns the picked texture (null to keep the current one)
        public static Texture2D Field(GUIContent label, Texture2D picked, Texture2D current, string hint, out bool clear)
        {
            clear = false;
            Texture2D result;
            using (new EditorGUILayout.HorizontalScope())
            {
                var shown = picked != null ? picked : current;
                var rect = GUILayoutUtility.GetRect(56f, 56f, GUILayout.Width(56f), GUILayout.Height(56f));
                EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.25f));
                if (shown != null)
                    GUI.DrawTexture(rect, shown, ScaleMode.ScaleToFit);
                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        result = (Texture2D)EditorGUILayout.ObjectField(picked, typeof(Texture2D), false);
                        using (new EditorGUI.DisabledScope(shown == null))
                            if (GUILayout.Button("Clear", GUILayout.Width(50f)))
                                clear = true;
                    }
                    EditorGUILayout.LabelField(picked != null ? "Copied on save" : current != null ? current.name : "None", EditorStyles.miniLabel);
                    if (!string.IsNullOrEmpty(hint))
                        EditorGUILayout.LabelField(hint, EditorStyles.wordWrappedMiniLabel);
                }
            }
            if (result != null && !IsSupported(result, out var error))
                EditorGUILayout.HelpBox(error, MessageType.Error);
            return result;
        }
    }
}
