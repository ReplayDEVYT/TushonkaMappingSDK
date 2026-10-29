using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Restore Components (AssetRipper Import window): puts back the ripped components the import couldn't keep, with every
    // value they had in the game, once the project has their class (a stub added to Tools/StubGenerator/stub-roots.txt
    // and regenerated, or a script of your own with the same namespace and name).
    //
    // The import copies prefabs and scenes as text, so their objects keep the export's file IDs; the export still has
    // each component as it was. Per prefab or scene that came from the export (same GUID), every MonoBehaviour of the
    // export whose script now maps onto a project script (TarkovRipImport.MapScript):
    //  - removed at import (its file ID is gone, its GameObject is there): its document goes back into the file and onto
    //    the GameObject's component list
    //  - still there as a missing script: its document is replaced by the export's, with the script pointed at the project's
    //  - re-added by hand (the same script on that GameObject under another file ID): its values are replaced by the
    //    export's, if Overwrite Hand-Made is on (its file ID stays, so what points at it still does)
    // References inside a component to other objects of the same file keep working, since those file IDs are the export's
    // too; references to assets keep their GUIDs. Open scenes are skipped (Unity would write its copy over the file).
    // Imports also keep each prefab they take components off, as imported, in <project>/RipBackup/<guid>.prefab with the
    // export's script map (scripts.json), so Restore works from that when the export is gone.
    public partial class TarkovRipImport
    {
        private bool _overwriteHandMade;
        private string _restoreFolder;

        private void DrawRestore()
        {
            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Restore components", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Components whose class the SDK didn't have were left off at import. Once the project has the class " +
                                       "(add it to Tools/StubGenerator/stub-roots.txt and regenerate the stubs, or add a script of the same namespace and name), " +
                                       "this puts them back on the imported prefabs and scenes with every value they had, read from the export (or from the backup imports keep of what they take off).", EditorStyles.wordWrappedMiniLabel);
            _restoreFolder ??= _destination;
            _restoreFolder = EditorGUILayout.TextField(new GUIContent("Prefabs And Scenes In", "The folder the export was imported into (or any folder under it)."), _restoreFolder);
            _overwriteHandMade = EditorGUILayout.ToggleLeft(new GUIContent("Overwrite components added by hand",
                "A component of the same script you added since gets the export's values instead of yours."), _overwriteHandMade);
            if (_index != null)
            {
                if (GUILayout.Button("Restore Components From This Export"))
                    EditorApplication.delayCall += () => Restore(_index, _restoreFolder, _overwriteHandMade);
            }
            else if (HasBackup)
            {
                if (GUILayout.Button(new GUIContent("Restore Components From The Import's Backup",
                        $"Imports keep the prefabs they take components off in {BackupFolder}, so this works without the export.")))
                    EditorApplication.delayCall += () => Restore(Index.FromBackup(), _restoreFolder, _overwriteHandMade);
            }
            else
                EditorGUILayout.HelpBox("Read the export above first. Imports from now on also keep a backup of what they take off " +
                                        $"({BackupFolder}), so later you can restore without the export.", MessageType.None);
        }

        // ------------------------------------------------------------ the import's backup

        // outside Assets (Unity doesn't import it), beside the project's Library
        public static string BackupFolder => Path.Combine(Path.GetDirectoryName(Application.dataPath), "RipBackup");
        private static string BackupScripts => Path.Combine(BackupFolder, "scripts.json");
        private static bool HasBackup => File.Exists(BackupScripts);

        // a prefab as imported, before Remove Missing Scripts, under its GUID
        private static void BackUp(string guid, string text)
        {
            Directory.CreateDirectory(BackupFolder);
            File.WriteAllText(Path.Combine(BackupFolder, guid + ".prefab"), text);
        }

        // the export's script GUIDs and DLLs, which the backup's unmapped references point at; merged over earlier imports
        private static void SaveBackupScripts(Index index)
        {
            if (!Directory.Exists(BackupFolder))
                return;
            var saved = HasBackup ? Newtonsoft.Json.JsonConvert.DeserializeObject<BackupScriptMap>(File.ReadAllText(BackupScripts)) : null;
            saved ??= new BackupScriptMap();
            foreach (var p in index.Scripts)
                saved.Scripts[p.Key] = p.Value;
            foreach (var p in index.Dlls)
                saved.Dlls[p.Key] = p.Value;
            File.WriteAllText(BackupScripts, Newtonsoft.Json.JsonConvert.SerializeObject(saved));
        }

        private sealed class BackupScriptMap
        {
            public Dictionary<string, string> Scripts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Dlls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public sealed partial class Index
        {
            // the backup read as an export: <guid>.prefab files and the saved script map
            public static Index FromBackup()
            {
                var index = new Index { Root = BackupFolder };
                var map = Newtonsoft.Json.JsonConvert.DeserializeObject<BackupScriptMap>(File.ReadAllText(BackupScripts)) ?? new BackupScriptMap();
                foreach (var p in map.Scripts)
                    index.Scripts[p.Key] = p.Value;
                foreach (var p in map.Dlls)
                    index.Dlls[p.Key] = p.Value;
                foreach (var file in Directory.GetFiles(BackupFolder, "*.prefab"))
                {
                    var name = Path.GetFileName(file);
                    var guid = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    index.Files[name] = guid;
                    index.PathOf[guid] = name;
                }
                return index;
            }
        }

        public static void Restore(Index index, string folder, bool overwriteHandMade)
        {
            if (!AssetDatabase.IsValidFolder(folder))
            {
                EditorUtility.DisplayDialog("Restore Components", $"{folder} isn't a folder in the project.", "OK");
                return;
            }
            var targets = Targets.Read();
            var paths = AssetDatabase.FindAssets("t:Prefab t:Scene", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath).Distinct()
                .Where(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)).ToList();
            var total = new RestoreCount();
            var stillMissing = new Dictionary<string, int>();
            var openScenes = new List<string>();
            var changed = new List<string>();
            try
            {
                AssetDatabase.StartAssetEditing();
                for (int i = 0; i < paths.Count; i++)
                {
                    if (i % 25 == 0 && EditorUtility.DisplayCancelableProgressBar("Restore Components", paths[i], i / (float)Math.Max(1, paths.Count)))
                        break;
                    if (paths[i].EndsWith(".unity", StringComparison.OrdinalIgnoreCase) && SceneManager.GetSceneByPath(paths[i]).isLoaded)
                    {
                        openScenes.Add(paths[i]);
                        continue;
                    }
                    var count = RestoreFile(index, targets, paths[i], overwriteHandMade, stillMissing);
                    if (count.Any)
                    {
                        changed.Add(paths[i]);
                        total.Add(count);
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
            }
            foreach (var path in changed)
                AssetDatabase.ImportAsset(path);

            var log = new StringBuilder($"[Tarkov SDK] Restore Components in {folder}: {changed.Count} of {paths.Count} prefab(s)/scene(s) changed; " +
                                        $"{total.Added} component(s) put back, {total.Relinked} missing script(s) fixed, {total.Overwritten} hand-made one(s) given the export's values" +
                                        (total.HandMade > 0 ? $", {total.HandMade} hand-made one(s) left as they are" : "") + ".");
            if (openScenes.Count > 0)
                log.Append($"\nSkipped {openScenes.Count} open scene(s) (close them and run it again): " + string.Join(", ", openScenes.Select(Path.GetFileNameWithoutExtension)));
            if (stillMissing.Count > 0)
                log.Append($"\nStill no class in the project for {stillMissing.Values.Sum()} component(s); the most common: " +
                           string.Join(", ", stillMissing.OrderByDescending(p => p.Value).Take(40).Select(p => $"{p.Key} ({p.Value})")) +
                           ". Add the game's ones to Tools/StubGenerator/stub-roots.txt and regenerate the stubs.");
            Debug.Log(log.ToString());
            EditorUtility.DisplayDialog("Restore Components", $"{total.Added + total.Relinked + total.Overwritten} component(s) restored in {changed.Count} prefab(s) and scene(s). " +
                                                              "The Console lists what's still missing.", "OK");
        }

        private sealed class RestoreCount
        {
            public int Added, Relinked, Overwritten, HandMade;
            public bool Any => Added + Relinked + Overwritten > 0;

            public void Add(RestoreCount o)
            {
                Added += o.Added;
                Relinked += o.Relinked;
                Overwritten += o.Overwritten;
                HandMade += o.HandMade;
            }
        }

        // one YAML document of a Unity file: "--- !u!<class> &<file id>[ stripped]" and what follows
        private sealed class Doc
        {
            public string Header;
            public int Class;
            public long Id;
            public bool Stripped;
            public string Body;
        }

        private static readonly Regex DocHeader = new Regex(@"^--- !u!(\d+) &(-?\d+)( stripped)?[^\n]*\n", RegexOptions.Multiline);
        private static readonly Regex GameObjectRef = new Regex(@"^  m_GameObject: \{fileID: (-?\d+)\}", RegexOptions.Multiline);

        private static (string Prefix, List<Doc> Docs) Parse(string text)
        {
            var docs = new List<Doc>();
            var matches = DocHeader.Matches(text);
            string prefix = matches.Count > 0 ? text.Substring(0, matches[0].Index) : text;
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                int start = m.Index + m.Length, end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
                docs.Add(new Doc
                {
                    Header = m.Value,
                    Class = int.Parse(m.Groups[1].Value),
                    Id = long.Parse(m.Groups[2].Value),
                    Stripped = m.Groups[3].Success,
                    Body = text.Substring(start, end - start),
                });
            }
            return (prefix, docs);
        }

        private static long? GameObjectOf(Doc doc)
        {
            var m = GameObjectRef.Match(doc.Body);
            return m.Success ? long.Parse(m.Groups[1].Value) : (long?)null;
        }

        private static string ScriptOf(Doc doc)
        {
            var m = ScriptRef.Match(doc.Body);
            return m.Success ? m.Value.Substring("m_Script: ".Length) : null;
        }

        // whether a project document's script is one the project has (by its GUID)
        private static bool ScriptResolves(Doc doc)
        {
            var m = ScriptRef.Match(doc.Body);
            return m.Success && !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(m.Groups[2].Value.ToLowerInvariant()));
        }

        private static readonly Regex ComponentList = new Regex(@"^  m_Component:(?: \[\])?\n((?:  - component: \{fileID: -?\d+\}\n)*)", RegexOptions.Multiline);

        // the component added at the end of the GameObject's list (the Transform stays first)
        private static string WithComponent(string gameObjectBody, long id)
        {
            var m = ComponentList.Match(gameObjectBody);
            if (!m.Success)
                return null;
            var entry = $"  - component: {{fileID: {id}}}\n";
            return gameObjectBody.Substring(0, m.Index) + "  m_Component:\n" + m.Groups[1].Value + entry + gameObjectBody.Substring(m.Index + m.Length);
        }

        private static RestoreCount RestoreFile(Index index, Targets targets, string path, bool overwriteHandMade, Dictionary<string, int> stillMissing)
        {
            var count = new RestoreCount();
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (!index.PathOf.TryGetValue(guid, out var relative))
                return count;
            var source = Path.Combine(index.Root, relative);
            if (!File.Exists(source) || !IsText(source) || !IsText(path))
                return count;
            var (prefix, ours) = Parse(File.ReadAllText(path).Replace("\r\n", "\n"));
            var (_, theirs) = Parse(File.ReadAllText(source).Replace("\r\n", "\n"));
            var byId = new Dictionary<long, Doc>();
            foreach (var d in ours)
                byId[d.Id] = d;
            var originalIds = new HashSet<long>(theirs.Select(d => d.Id));

            foreach (var original in theirs)
            {
                if (original.Class != 114 || original.Stripped)
                    continue;
                var script = ScriptRef.Match(original.Body);
                if (!script.Success)
                    continue;
                var mapped = MapScript(index, targets, script.Groups[1].Value, script.Groups[2].Value.ToLowerInvariant(), out var cls);
                if (mapped == null)
                {
                    if (cls != null)
                        stillMissing[cls] = stillMissing.TryGetValue(cls, out var n) ? n + 1 : 1;
                    continue;
                }
                var body = ScriptRef.Replace(original.Body, "m_Script: " + mapped, 1);
                var gameObject = GameObjectOf(original);

                if (byId.TryGetValue(original.Id, out var kept))
                {
                    // there with a missing script: the export's, on the project's script
                    if (kept.Class == 114 && !kept.Stripped && !ScriptResolves(kept))
                    {
                        kept.Body = body;
                        count.Relinked++;
                    }
                    continue;
                }
                if (gameObject == null || !byId.TryGetValue(gameObject.Value, out var owner) || owner.Class != 1 || owner.Stripped)
                    continue;
                // the same script added since by hand
                var hand = ours.FirstOrDefault(d => d.Class == 114 && !d.Stripped && !originalIds.Contains(d.Id) && GameObjectOf(d) == gameObject && ScriptOf(d) == mapped);
                if (hand != null)
                {
                    if (overwriteHandMade)
                    {
                        hand.Body = body;
                        count.Overwritten++;
                    }
                    else
                        count.HandMade++;
                    continue;
                }
                var withIt = WithComponent(owner.Body, original.Id);
                if (withIt == null)
                    continue;
                owner.Body = withIt;
                var restored = new Doc { Header = original.Header, Class = 114, Id = original.Id, Body = body };
                ours.Add(restored);
                byId[original.Id] = restored;
                count.Added++;
            }
            if (count.Any)
                File.WriteAllText(path, prefix + string.Concat(ours.Select(d => d.Header + d.Body)));
            return count;
        }
    }
}
