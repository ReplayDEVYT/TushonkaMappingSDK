using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // A map pack: an extra page of the map select screen with its own background, for maps that
    // aren't in Tarkov (or a group of them). Players turn the pages with the PREVIOUS / NEXT buttons
    // in the screen's bottom button row. A map joins a pack in the Server Data Editor's Map Select tab; maps without
    // one stay on Tarkov's page.
    //
    // Deployed as <SptRoot>/SPT_Runtime/user/mods/CustomMapLoader/packs/<PackId>/ (pack.json and the
    // pictures). Build Map deploys the pack of the map it builds and puts it in the map's package.
    public class TarkovMapPack : ScriptableObject
    {
        [MenuItem("Tarkov SDK/Map Settings/New Map Pack...", priority = 134)]
        private static void Create()
        {
            var path = EditorUtility.SaveFilePanelInProject("New Map Pack", "NewMapPack", "asset", "Where to save the map pack.");
            if (string.IsNullOrEmpty(path))
                return;
            var pack = CreateInstance<TarkovMapPack>();
            var id = Regex.Replace(Path.GetFileNameWithoutExtension(path).ToLowerInvariant(), "[^a-z0-9_]", "_");
            pack.PackId = id;
            pack.DisplayName = ObjectNames.NicifyVariableName(Path.GetFileNameWithoutExtension(path));
            AssetDatabase.CreateAsset(pack, path);
            AssetDatabase.SaveAssets();
            Selection.activeObject = pack;
            EditorGUIUtility.PingObject(pack);
        }

        [Tooltip("The pack's folder name. Lowercase letters, digits and underscores. Maps name it to join the pack.")]
        public string PackId = "my_pack";

        [Tooltip("Shown under SELECT LOCATION on the map select screen while this page is open.")]
        public string DisplayName = "My Maps";

        [Tooltip("Pages are sorted by this, then by name. Tarkov's own page is always first.")]
        public int Order;

        [Tooltip("Replaces the Tarkov map picture on this page. PNG or JPG; Tarkov's is 800x800, any shape works. " +
                 "Location buttons are placed on it by their Icon X/Y, in thousandths of its width and height.")]
        public Texture2D Background;

        [Tooltip("Optional: replaces the white dot on the location buttons of every map on this page that has no icon of its own. " +
                 "PNG with transparency; the dot is drawn 49x49.")]
        public Texture2D LocationIcon;

        [Tooltip("SPT install to deploy to with Deploy To SPT (Build Map uses its Map Definition's instead).")]
        public string SptRoot = "C:/spt41";

        public const string PackJson = "pack.json";

        public static bool IsValidId(string id) => !string.IsNullOrEmpty(id) && Regex.IsMatch(id, "^[a-z0-9_]+$");

        public static string PacksDirectory(string sptRoot) => Path.Combine(TarkovMapBuilder.ServerModDirectory(sptRoot), "packs");

        public static string PackDirectory(string sptRoot, string packId) => Path.Combine(PacksDirectory(sptRoot), packId);

        public static IEnumerable<TarkovMapPack> All() =>
            AssetDatabase.FindAssets("t:" + nameof(TarkovMapPack))
                .Select(guid => AssetDatabase.LoadAssetAtPath<TarkovMapPack>(AssetDatabase.GUIDToAssetPath(guid)))
                .Where(p => p != null);

        public static TarkovMapPack Find(string packId) =>
            string.IsNullOrEmpty(packId) ? null : All().FirstOrDefault(p => string.Equals(p.PackId, packId, StringComparison.OrdinalIgnoreCase));

        public bool Validate(out string error)
        {
            error = null;
            if (!IsValidId(PackId))
                error = "Pack Id must be lowercase letters, digits and underscores.";
            else if (Background == null)
                error = "Pick a Background picture.";
            else if (!TarkovMenuPictures.IsSupported(Background, out var e1))
                error = "Background: " + e1;
            else if (!TarkovMenuPictures.IsSupported(LocationIcon, out var e2))
                error = "Location Icon: " + e2;
            return error == null;
        }

        // writes packs/<PackId>/ into the SPT install's CustomMapLoader mod
        public bool Deploy(string sptRoot)
        {
            if (!Validate(out var error))
            {
                Debug.LogError($"[Tarkov SDK] Map pack '{name}' was not deployed: {error}");
                return false;
            }
            var dir = PackDirectory(sptRoot, PackId);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, PackJson);
            var data = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
            data["Name"] = string.IsNullOrWhiteSpace(DisplayName) ? PackId : DisplayName;
            data["Order"] = Order;
            data["Background"] = TarkovMenuPictures.Copy(Background, dir, "background");
            data["Icon"] = LocationIcon != null ? TarkovMenuPictures.Copy(LocationIcon, dir, "icon") : "";
            File.WriteAllText(path, data.ToString(Formatting.Indented));
            TarkovMenuPictures.DeleteUnused(dir, new[] { (string)data["Background"], (string)data["Icon"] }, new[] { "background", "icon" });
            Debug.Log($"[Tarkov SDK] Deployed map pack '{PackId}' to {dir}. Restart the SPT server to pick it up.");
            return true;
        }

        // ids and names of the packs in the project and the ones deployed to an SPT install
        public static List<(string Id, string Name)> Available(string sptRoot)
        {
            var packs = new List<(string, string)>();
            foreach (var pack in All())
                if (IsValidId(pack.PackId) && packs.All(p => p.Item1 != pack.PackId))
                    packs.Add((pack.PackId, string.IsNullOrWhiteSpace(pack.DisplayName) ? pack.PackId : pack.DisplayName));
            var dir = PacksDirectory(sptRoot ?? "");
            if (Directory.Exists(dir))
                foreach (var folder in Directory.GetDirectories(dir))
                {
                    var id = Path.GetFileName(folder).ToLowerInvariant();
                    if (packs.Any(p => p.Item1 == id) || !File.Exists(Path.Combine(folder, PackJson)))
                        continue;
                    string packName = id;
                    try
                    {
                        packName = JObject.Parse(File.ReadAllText(Path.Combine(folder, PackJson))).Value<string>("Name") ?? id;
                    }
                    catch (Exception)
                    {
                        // a broken pack.json still lists the pack by its folder
                    }
                    packs.Add((id, packName));
                }
            return packs;
        }

        // the pack's background: the project asset's, else the deployed one's
        public static Texture2D BackgroundOf(string sptRoot, string packId)
        {
            var asset = Find(packId);
            if (asset != null && asset.Background != null)
                return asset.Background;
            var dir = PackDirectory(sptRoot ?? "", packId ?? "");
            try
            {
                var file = JObject.Parse(File.ReadAllText(Path.Combine(dir, PackJson))).Value<string>("Background");
                return string.IsNullOrEmpty(file) ? null : TarkovMenuPictures.Load(Path.Combine(dir, file));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    [CustomEditor(typeof(TarkovMapPack))]
    public class TarkovMapPackEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var pack = (TarkovMapPack)target;
            DrawDefaultInspector();
            EditorGUILayout.Space();
            if (!pack.Validate(out var error))
                EditorGUILayout.HelpBox(error, MessageType.Error);

            if (pack.Background != null)
            {
                float width = Mathf.Min(EditorGUIUtility.currentViewWidth - 40f, 300f);
                float height = width * pack.Background.height / Mathf.Max(1, pack.Background.width);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    var rect = GUILayoutUtility.GetRect(width, height, GUILayout.ExpandWidth(false));
                    GUI.DrawTexture(rect, pack.Background, ScaleMode.StretchToFill);
                    GUILayout.FlexibleSpace();
                }
            }

            EditorGUILayout.HelpBox("Maps join this pack in Tarkov SDK > Map Settings > Data > Server Data Editor, Map Select tab, where you also place " +
                                    "their buttons on this background. Build Map deploys and packages the pack with the map.", MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(error != null))
                    if (GUILayout.Button("Deploy To SPT", GUILayout.Height(24f)))
                        EditorApplication.delayCall += () => pack.Deploy(pack.SptRoot);
                if (GUILayout.Button("Open Folder", GUILayout.Height(24f)))
                {
                    var dir = TarkovMapPack.PackDirectory(pack.SptRoot, pack.PackId);
                    EditorUtility.RevealInFinder(Directory.Exists(dir) ? dir : TarkovMapPack.PacksDirectory(pack.SptRoot));
                }
            }
        }
    }
}
