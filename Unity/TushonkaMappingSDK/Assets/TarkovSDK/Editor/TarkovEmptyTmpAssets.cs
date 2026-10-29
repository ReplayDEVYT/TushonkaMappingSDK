using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Deletes the empty TextMeshPro fonts and sprite assets AssetRipper writes (TarkovRipImport.IsEmptyTmpAsset) wherever
    // they come from: TMP queues every font asset it loads for a refresh and throws NullReferenceException in
    // TMP_FontAsset.ReadFontAssetDefinition for each empty one, again on every redraw, flooding the Console. Checked when
    // assets are imported, and once per editor session over the whole project for ones already there.
    public class TarkovEmptyTmpAssets : AssetPostprocessor
    {
        private const string ScannedKey = "TarkovSDK.EmptyTmpAssetsScanned";
        private static readonly HashSet<string> Pending = new HashSet<string>();

        [InitializeOnLoadMethod]
        private static void ScanOnce()
        {
            if (SessionState.GetBool(ScannedKey, false))
                return;
            SessionState.SetBool(ScannedKey, true);
            EditorApplication.delayCall += () => Queue(Directory.GetFiles("Assets", "*.asset", SearchOption.AllDirectories));
        }

        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom) =>
            Queue(imported.Where(p => p.EndsWith(".asset", System.StringComparison.OrdinalIgnoreCase)));

        private static void Queue(IEnumerable<string> paths)
        {
            foreach (var path in paths)
                if (IsEmpty(path))
                    Pending.Add(path.Replace('\\', '/'));
            if (Pending.Count > 0)
                EditorApplication.delayCall += DeletePending;
        }

        private static bool IsEmpty(string path)
        {
            try
            {
                // an empty one is its header alone, a few hundred bytes
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 4096)
                    return false;
                var text = File.ReadAllText(path);
                return text.StartsWith("%YAML") && TarkovRipImport.IsEmptyTmpAsset(text);
            }
            catch (IOException)
            {
                return false;
            }
        }

        // outside the import (deleting from inside OnPostprocessAllAssets would start another one)
        private static void DeletePending()
        {
            if (Pending.Count == 0)
                return;
            var paths = Pending.ToList();
            Pending.Clear();
            var failed = new List<string>();
            AssetDatabase.DeleteAssets(paths.ToArray(), failed);
            int deleted = paths.Count - failed.Count;
            if (deleted > 0)
                Debug.Log($"[Tarkov SDK] Deleted {deleted} empty TextMeshPro font or sprite asset(s) AssetRipper couldn't read (TextMeshPro throws " +
                          "NullReferenceException in ReadFontAssetDefinition on them); texts that used them draw in TextMeshPro's default font: " +
                          string.Join(", ", paths.Except(failed).Take(10).Select(Path.GetFileNameWithoutExtension)) + (deleted > 10 ? " ..." : ""));
            foreach (var path in failed)
                Debug.LogWarning($"[Tarkov SDK] {path} is an empty TextMeshPro asset that makes TextMeshPro throw, and couldn't be deleted; delete it by hand.");
        }
    }
}
