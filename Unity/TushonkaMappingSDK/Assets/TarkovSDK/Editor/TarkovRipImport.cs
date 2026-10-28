using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Tarkov SDK > Decompilation > Import AssetRipper Export: brings prefabs, models, materials and textures from an
    // AssetRipper export of the game ("Export Unity Project": ExportedProject/Assets/...) into the SDK project without
    // its scripts, which would clash with the SDK's own game stubs and stop the project compiling.
    //
    //  - scripts: AssetRipper writes a component's script either as its decompiled .cs (m_Script {fileID: 11500000,
    //    guid: <the .cs>}) or inside its DLL ({fileID: <Unity's FileID of the class>, guid: <the .dll>}; the FileID is the
    //    first 4 bytes of MD4("s\0\0\0" + namespace + class name)). Either way the reference is pointed at the project's
    //    script of the same class: the SDK's stubs for the game's components (same field names, so the values carry), the
    //    packages' for theirs. Components with no such class are left missing and removed from the prefabs.
    //  - shaders: materials on a game shader the SDK has a stand-in for ("Tarkov/<name>") are moved onto it; the others'
    //    ripped shader is renamed "Tarkov/<name>", so the client still swaps the game's real shader in at raid start
    //    (or, with that off, they go on Unity's Standard, which the game also has)
    //  - assets keep their GUIDs (their .meta files are copied), so everything that points at them still does; what the
    //    project already has (the same GUID) isn't copied again
    // The tools under it fix folders already in the project: Fix Ripped Materials, Remove Missing Scripts.
    public class TarkovRipImport : EditorWindow
    {
        private const string Menu = "Tarkov SDK/Decompilation/";
        private const string ScriptFileId = "11500000";
        private static readonly string StandardShaderRef = "{fileID: 46, guid: 0000000000000000f000000000000000, type: 0}";

        // YAML assets whose references are rewritten and followed
        private static readonly HashSet<string> YamlExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".prefab", ".unity", ".mat", ".asset", ".controller", ".overrideController", ".anim", ".mask", ".playable",
            ".physicMaterial", ".physicsMaterial2D", ".lighting", ".terrainlayer", ".preset", ".mixer", ".spriteatlas", ".renderTexture", ".cubemap", ".flare",
        };

        private static readonly string[] Kinds = { "Prefabs", "Models", "Materials", "Textures", "Scenes", "Everything" };

        private string _export = "";
        private string _destination = "Assets/Ripped";
        private Index _index;
        private int _kind;
        private string _search = "";
        private Vector2 _scroll;
        private readonly HashSet<string> _selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _withDependencies = true;
        private bool _renameShaders = true;
        private bool _removeMissing = true;

        [MenuItem(Menu + "Import AssetRipper Export...", priority = 146)]
        private static void Open()
        {
            var window = GetWindow<TarkovRipImport>("AssetRipper Import");
            window.minSize = new Vector2(460f, 480f);
            window._export = EditorPrefs.GetString("TarkovSDK.RipExport", "");
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Import from an AssetRipper export", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Rip the game with AssetRipper (Export > Export All Files, Unity project), then pick the folder it wrote. Prefabs, models, " +
                                       "materials and textures come in without the game's scripts: components are moved onto the SDK's own game stubs and " +
                                       "materials onto its shader stand-ins, so they work in a map. Don't copy the export into Assets by hand: its scripts " +
                                       "clash with the SDK's and the project stops compiling.", EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _export = EditorGUILayout.TextField("Export Folder", _export);
                if (GUILayout.Button("...", GUILayout.Width(28f)))
                {
                    var picked = EditorUtility.OpenFolderPanel("AssetRipper export", _export, "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        _export = picked;
                        _index = null;
                    }
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                _destination = EditorGUILayout.TextField("Import Into", _destination);
                if (GUILayout.Button("...", GUILayout.Width(28f)))
                {
                    var picked = EditorUtility.OpenFolderPanel("Import into", "Assets", "");
                    var assets = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
                    if (!string.IsNullOrEmpty(picked) && picked.Replace('\\', '/').StartsWith(assets, StringComparison.OrdinalIgnoreCase))
                        _destination = "Assets" + picked.Replace('\\', '/').Substring(assets.Length);
                }
            }
            if (GUILayout.Button(_index == null ? "Read Export" : $"Read Export Again ({_index.Files.Count} assets, {_index.ScriptCount} scripts)"))
                EditorApplication.delayCall += () =>
                {
                    _index = Index.Read(_export);
                    if (_index != null)
                        EditorPrefs.SetString("TarkovSDK.RipExport", _export);
                    Repaint();
                };
            if (_index == null)
                return;

            EditorGUILayout.Space();
            _kind = GUILayout.Toolbar(_kind, Kinds);
            _search = EditorGUILayout.TextField("Search", _search);
            var shown = _index.Files.Keys.Where(p => Matches(p, Kinds[_kind]) && (_search.Length == 0 || p.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(400).ToList();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"{_selected.Count} picked", GUILayout.Width(90f));
                if (GUILayout.Button("Pick All Shown"))
                    foreach (var p in shown)
                        _selected.Add(p);
                if (GUILayout.Button("Clear"))
                    _selected.Clear();
            }
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(160f));
            foreach (var path in shown)
            {
                bool on = _selected.Contains(path);
                bool next = EditorGUILayout.ToggleLeft(path, on);
                if (next != on)
                {
                    if (next)
                        _selected.Add(path);
                    else
                        _selected.Remove(path);
                }
            }
            if (shown.Count == 400)
                EditorGUILayout.LabelField("(the first 400; search to narrow it down)", EditorStyles.miniLabel);
            EditorGUILayout.EndScrollView();

            _withDependencies = EditorGUILayout.ToggleLeft(new GUIContent("Bring what they use", "The meshes, materials, textures, sounds, animations and prefabs the picked assets reference."), _withDependencies);
            _renameShaders = EditorGUILayout.ToggleLeft(new GUIContent("Keep game shaders the SDK has no stand-in for",
                "Their ripped shader comes in renamed Tarkov/<name>, so the game's real shader replaces it in the raid. Off: those materials go on Unity's Standard shader."), _renameShaders);
            _removeMissing = EditorGUILayout.ToggleLeft(new GUIContent("Remove scripts the SDK doesn't have from the prefabs", "Leaves no missing scripts (they'd log warnings and do nothing in game)."), _removeMissing);
            using (new EditorGUI.DisabledScope(_selected.Count == 0))
                if (GUILayout.Button($"Import {_selected.Count} Picked"))
                    EditorApplication.delayCall += () => Import(_index, _selected.ToList(), _destination, _withDependencies, _renameShaders, _removeMissing);
            if (GUILayout.Button(new GUIContent("Import Everything (No Scripts)", "Every asset in the export but its scripts and DLLs. Large rips take a long time to import.")))
                EditorApplication.delayCall += () =>
                {
                    if (EditorUtility.DisplayDialog("AssetRipper Import", $"Import all {_index.Files.Count} assets into {_destination}? A whole map takes a long time and a lot of disk.", "Import", "Cancel"))
                        Import(_index, _index.Files.Keys.ToList(), _destination, false, _renameShaders, _removeMissing);
                };
        }

        private static bool Matches(string path, string kind)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            switch (kind)
            {
                case "Prefabs": return ext == ".prefab";
                case "Models": return ext == ".fbx" || ext == ".obj" || ext == ".dae" || ext == ".blend" || path.IndexOf("/Mesh/", StringComparison.OrdinalIgnoreCase) >= 0;
                case "Materials": return ext == ".mat";
                case "Textures": return ext == ".png" || ext == ".tga" || ext == ".jpg" || ext == ".exr" || ext == ".psd" || ext == ".dds";
                case "Scenes": return ext == ".unity";
                default: return true;
            }
        }

        // ------------------------------------------------------------ the export

        public sealed class Index
        {
            public string Root;
            // path under the export's Assets -> guid
            public readonly Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> PathOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // ripped .cs guid -> class's full name; ripped .dll guid -> assembly name
            public readonly Dictionary<string, string> Scripts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> Dlls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // ripped shader guid -> shader name
            public readonly Dictionary<string, string> Shaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public int ScriptCount => Scripts.Count + Dlls.Count;

            public static Index Read(string folder)
            {
                var root = FindAssets(folder);
                if (root == null)
                {
                    EditorUtility.DisplayDialog("AssetRipper Import", "No Assets folder found there. Pick the folder AssetRipper exported to (it has ExportedProject/Assets).", "OK");
                    return null;
                }
                var index = new Index { Root = root };
                var metas = Directory.GetFiles(root, "*.meta", SearchOption.AllDirectories);
                try
                {
                    for (int i = 0; i < metas.Length; i++)
                    {
                        if (i % 500 == 0 && EditorUtility.DisplayCancelableProgressBar("AssetRipper Import", "Reading the export", i / (float)metas.Length))
                            return null;
                        var file = metas[i].Substring(0, metas[i].Length - 5);
                        if (!File.Exists(file))
                            continue;
                        var guid = Guid(metas[i]);
                        if (guid == null)
                            continue;
                        var relative = file.Substring(root.Length + 1).Replace('\\', '/');
                        var ext = Path.GetExtension(file).ToLowerInvariant();
                        if (ext == ".cs")
                            index.Scripts[guid] = ClassOf(file);
                        else if (ext == ".dll")
                            index.Dlls[guid] = Path.GetFileNameWithoutExtension(file);
                        else if (ext == ".asmdef" || ext == ".asmref" || ext == ".rsp")
                            continue;
                        else
                        {
                            if (ext == ".shader")
                                index.Shaders[guid] = ShaderName(file);
                            index.Files[relative] = guid;
                            index.PathOf[guid] = relative;
                        }
                    }
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
                Debug.Log($"[Tarkov SDK] AssetRipper export {root}: {index.Files.Count} assets, {index.Scripts.Count} scripts, {index.Dlls.Count} DLLs, {index.Shaders.Count} shaders.");
                return index;
            }

            private static string FindAssets(string folder)
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                    return null;
                // the folder AssetRipper wrote to, its ExportedProject, or the Assets folder itself
                foreach (var candidate in new[] { Path.Combine(folder, "ExportedProject", "Assets"), Path.Combine(folder, "Assets") })
                    if (Directory.Exists(candidate))
                        return Path.GetFullPath(candidate).TrimEnd('\\', '/');
                return Path.GetFileName(folder.TrimEnd('\\', '/')) == "Assets" ? Path.GetFullPath(folder).TrimEnd('\\', '/') : null;
            }
        }

        private static readonly Regex GuidLine = new Regex(@"^guid:\s*([0-9a-fA-F]{32})", RegexOptions.Multiline);

        private static string Guid(string meta)
        {
            using (var reader = new StreamReader(meta))
                for (int i = 0; i < 4; i++)
                {
                    var line = reader.ReadLine();
                    if (line == null)
                        break;
                    var m = GuidLine.Match(line);
                    if (m.Success)
                        return m.Groups[1].Value.ToLowerInvariant();
                }
            return null;
        }

        // AssetRipper names a decompiled script after its class; the namespace is the file's
        private static string ClassOf(string file)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var text = File.ReadAllText(file);
            var ns = Regex.Match(text, @"^\s*namespace\s+([\w.]+)", RegexOptions.Multiline);
            return ns.Success ? ns.Groups[1].Value + "." + name : name;
        }

        private static string ShaderName(string file)
        {
            using (var reader = new StreamReader(file))
                for (int i = 0; i < 40; i++)
                {
                    var line = reader.ReadLine();
                    if (line == null)
                        break;
                    var m = Regex.Match(line, "^\\s*Shader\\s+\"([^\"]+)\"");
                    if (m.Success)
                        return m.Groups[1].Value;
                }
            return null;
        }

        // ------------------------------------------------------------ the project's scripts and shaders

        private sealed class Targets
        {
            // a class's full name -> its script reference; Unity's FileID of the class -> the same
            public readonly Dictionary<string, string> ByName = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<int, string> ByFileId = new Dictionary<int, string>();
            // a game shader's name -> its SDK stand-in's reference
            public readonly Dictionary<string, string> StandIns = new Dictionary<string, string>(StringComparer.Ordinal);

            public static Targets Read()
            {
                var t = new Targets();
                foreach (var script in MonoImporter.GetAllRuntimeMonoScripts())
                {
                    var type = script != null ? script.GetClass() : null;
                    if (type == null || type.IsNested || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(script, out string guid, out long local))
                        continue;
                    var reference = $"{{fileID: {local}, guid: {guid}, type: 3}}";
                    var full = string.IsNullOrEmpty(type.Namespace) ? type.Name : type.Namespace + "." + type.Name;
                    if (!t.ByName.ContainsKey(full))
                        t.ByName[full] = reference;
                    int id = TarkovFileId.Compute(type.Namespace ?? "", type.Name);
                    if (!t.ByFileId.ContainsKey(id))
                        t.ByFileId[id] = reference;
                }
                foreach (var guid in AssetDatabase.FindAssets("t:Shader", new[] { "Assets/TarkovSDK" }))
                {
                    var shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid));
                    if (shader != null && shader.name.StartsWith("Tarkov/", StringComparison.Ordinal))
                        t.StandIns[shader.name.Substring("Tarkov/".Length)] = $"{{fileID: 4800000, guid: {guid}, type: 3}}";
                }
                return t;
            }
        }

        // ------------------------------------------------------------ import

        private static readonly Regex ScriptRef = new Regex(@"m_Script: \{fileID: (-?\d+), guid: ([0-9a-fA-F]{32}), type: \d\}");
        private static readonly Regex ShaderRef = new Regex(@"m_Shader: \{fileID: (-?\d+), guid: ([0-9a-fA-F]{32}), type: \d\}");
        private static readonly Regex AnyGuid = new Regex(@"guid: ([0-9a-fA-F]{32})");

        public static void Import(Index index, List<string> picked, string destination, bool withDependencies, bool renameShaders, bool removeMissing)
        {
            destination = destination.Replace('\\', '/').TrimEnd('/');
            if (!destination.StartsWith("Assets", StringComparison.Ordinal))
            {
                EditorUtility.DisplayDialog("AssetRipper Import", "Import into a folder under Assets.", "OK");
                return;
            }
            var targets = Targets.Read();
            var files = new HashSet<string>(picked, StringComparer.OrdinalIgnoreCase);
            try
            {
                if (withDependencies)
                    Follow(index, files);

                int copied = 0, already = 0, scripts = 0, unknownScripts = 0, standIns = 0, renamed = 0, standard = 0;
                var unknownClasses = new HashSet<string>();
                var prefabs = new List<string>();
                var list = files.ToList();
                for (int i = 0; i < list.Count; i++)
                {
                    var relative = list[i];
                    if (i % 50 == 0 && EditorUtility.DisplayCancelableProgressBar("AssetRipper Import", relative, i / (float)list.Count))
                        break;
                    if (!index.Files.TryGetValue(relative, out var guid))
                        continue;
                    // the project has it already (an earlier import): references to it still work
                    if (!string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)))
                    {
                        already++;
                        continue;
                    }
                    var source = Path.Combine(index.Root, relative);
                    var ext = Path.GetExtension(relative).ToLowerInvariant();
                    string shaderName = null;
                    if (ext == ".shader")
                        index.Shaders.TryGetValue(guid, out shaderName);
                    // a game shader the SDK has a stand-in for: its materials go on the stand-in instead
                    if (shaderName != null && (targets.StandIns.ContainsKey(shaderName) || !renameShaders || shaderName.StartsWith("Tarkov/", StringComparison.Ordinal)))
                        continue;
                    var target = Path.Combine(destination, relative).Replace('\\', '/');
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (ext == ".shader" && shaderName != null)
                    {
                        // renamed, so the client swaps the game's shader of the old name in
                        var text = File.ReadAllText(source);
                        text = Regex.Replace(text, "^(\\s*Shader\\s+\")([^\"]+)\"", m => m.Groups[1].Value + "Tarkov/" + m.Groups[2].Value + "\"", RegexOptions.Multiline);
                        File.WriteAllText(target, text);
                        renamed++;
                    }
                    else if (YamlExtensions.Contains(ext) && IsText(source))
                    {
                        var text = File.ReadAllText(source);
                        if (text.Contains("m_Script:"))
                            text = ScriptRef.Replace(text, m =>
                            {
                                var replaced = MapScript(index, targets, m.Groups[1].Value, m.Groups[2].Value.ToLowerInvariant(), out var cls);
                                if (replaced != null)
                                {
                                    scripts++;
                                    return "m_Script: " + replaced;
                                }
                                if (cls != null)
                                {
                                    unknownScripts++;
                                    unknownClasses.Add(cls);
                                }
                                return m.Value;
                            });
                        if (ext == ".mat")
                            text = ShaderRef.Replace(text, m =>
                            {
                                var shaderGuid = m.Groups[2].Value.ToLowerInvariant();
                                if (!index.Shaders.TryGetValue(shaderGuid, out var name) || name == null)
                                    return m.Value;
                                if (targets.StandIns.TryGetValue(name, out var standIn))
                                {
                                    standIns++;
                                    return "m_Shader: " + standIn;
                                }
                                if (renameShaders)
                                    return m.Value;
                                standard++;
                                return "m_Shader: " + StandardShaderRef;
                            });
                        File.WriteAllText(target, text);
                        if (ext == ".prefab")
                            prefabs.Add(target);
                    }
                    else
                    {
                        File.Copy(source, target, true);
                    }
                    File.Copy(source + ".meta", target + ".meta", true);
                    copied++;
                }
                EditorUtility.DisplayProgressBar("AssetRipper Import", "Importing into Unity", 1f);
                AssetDatabase.Refresh();

                int stripped = 0;
                if (removeMissing)
                    for (int i = 0; i < prefabs.Count; i++)
                    {
                        if (i % 20 == 0)
                            EditorUtility.DisplayProgressBar("AssetRipper Import", "Removing missing scripts", i / (float)Math.Max(1, prefabs.Count));
                        stripped += RemoveMissingScripts(prefabs[i]);
                    }
                Debug.Log($"[Tarkov SDK] AssetRipper import into {destination}: {copied} asset(s) copied, {already} already in the project; {scripts} script reference(s) moved onto " +
                          $"the SDK's scripts, {unknownScripts} to classes the SDK doesn't have{(stripped > 0 ? $" ({stripped} removed from prefabs)" : "")}; materials: {standIns} onto SDK stand-ins, " +
                          $"{renamed} game shader(s) kept as Tarkov/ stand-ins, {standard} onto Standard." +
                          (unknownClasses.Count > 0 ? "\nClasses the SDK has no script for: " + string.Join(", ", unknownClasses.OrderBy(c => c).Take(60)) : ""));
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        // the picked assets and everything they reference, recursively
        private static void Follow(Index index, HashSet<string> files)
        {
            var queue = new Queue<string>(files);
            int done = 0;
            while (queue.Count > 0)
            {
                var relative = queue.Dequeue();
                if (++done % 200 == 0)
                    EditorUtility.DisplayProgressBar("AssetRipper Import", "Finding what the picked assets use: " + relative, Mathf.Clamp01(done / (float)(done + queue.Count)));
                var ext = Path.GetExtension(relative).ToLowerInvariant();
                var source = Path.Combine(index.Root, relative);
                if (!YamlExtensions.Contains(ext) || !IsText(source) || IsMeshOrTexture(source))
                    continue;
                foreach (Match m in AnyGuid.Matches(File.ReadAllText(source)))
                    if (index.PathOf.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out var used) && files.Add(used))
                        queue.Enqueue(used);
            }
        }

        private static string MapScript(Index index, Targets targets, string fileId, string guid, out string cls)
        {
            cls = null;
            if (index.Scripts.TryGetValue(guid, out var name) && fileId == ScriptFileId)
            {
                cls = name;
                return targets.ByName.TryGetValue(name, out var r) ? r : null;
            }
            if (index.Dlls.TryGetValue(guid, out var dll) && int.TryParse(fileId, out var id))
            {
                cls = $"{dll}:{id}";
                return targets.ByFileId.TryGetValue(id, out var r) ? r : null;
            }
            return null;
        }

        private static bool IsText(string file)
        {
            using (var stream = File.OpenRead(file))
            {
                var head = new byte[5];
                return stream.Read(head, 0, 5) == 5 && Encoding.ASCII.GetString(head) == "%YAML";
            }
        }

        // meshes and textures are big and point at nothing
        private static bool IsMeshOrTexture(string file)
        {
            using (var reader = new StreamReader(file))
            {
                var buffer = new char[400];
                int n = reader.Read(buffer, 0, buffer.Length);
                var head = new string(buffer, 0, n);
                return head.Contains("--- !u!43 ") || head.Contains("--- !u!28 ") || head.Contains("--- !u!89 ");
            }
        }

        // ------------------------------------------------------------ fixes for folders already in the project

        [MenuItem(Menu + "Fix Ripped Materials In Selected Folder", priority = 148)]
        private static void MenuFixMaterials() => EditorApplication.delayCall += () =>
        {
            var folder = SelectedFolder();
            if (folder == null)
                return;
            var standIns = Targets.Read().StandIns;
            int moved = 0, renamedShaders = 0;
            // ripped shaders in the folder become Tarkov/ stand-ins, or give way to the SDK's own
            foreach (var guid in AssetDatabase.FindAssets("t:Shader", new[] { folder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader == null || shader.name.StartsWith("Tarkov/", StringComparison.Ordinal) || standIns.ContainsKey(shader.name) || !path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
                    continue;
                var text = File.ReadAllText(path);
                var updated = Regex.Replace(text, "^(\\s*Shader\\s+\")([^\"]+)\"", m => m.Groups[1].Value + "Tarkov/" + m.Groups[2].Value + "\"", RegexOptions.Multiline);
                if (updated != text)
                {
                    File.WriteAllText(path, updated);
                    renamedShaders++;
                }
            }
            AssetDatabase.Refresh();
            moved = FixMaterials(AssetDatabase.FindAssets("t:Material", new[] { folder }).Select(g => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g))));
            AssetDatabase.SaveAssets();
            Debug.Log($"[Tarkov SDK] {folder}: {moved} material(s) moved onto the SDK's shader stand-ins, {renamedShaders} ripped shader(s) renamed Tarkov/<name> " +
                      "(the game's shader of the name replaces them in the raid). Materials showing the pink error shader lost their shader in the rip; set one by hand.");
        };

        // moves materials on a game shader the SDK has a stand-in for onto the stand-in; the count moved
        internal static int FixMaterials(IEnumerable<Material> materials)
        {
            var standIns = Targets.Read().StandIns;
            int moved = 0;
            foreach (var material in materials.Where(m => m != null && m.shader != null).Distinct())
            {
                if (!standIns.ContainsKey(material.shader.name))
                    continue;
                var standIn = Shader.Find("Tarkov/" + material.shader.name);
                if (standIn == null)
                    continue;
                Undo.RecordObject(material, "Fix ripped material");
                int queue = material.renderQueue;
                material.shader = standIn;
                material.renderQueue = queue;
                EditorUtility.SetDirty(material);
                moved++;
            }
            return moved;
        }

        [MenuItem(Menu + "Remove Missing Scripts In Selected Folder", priority = 149)]
        private static void MenuRemoveMissing() => EditorApplication.delayCall += () =>
        {
            var folder = SelectedFolder();
            if (folder == null)
                return;
            var prefabs = AssetDatabase.FindAssets("t:Prefab", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath).ToList();
            int removed = 0;
            try
            {
                for (int i = 0; i < prefabs.Count; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Remove Missing Scripts", prefabs[i], i / (float)Math.Max(1, prefabs.Count)))
                        break;
                    removed += RemoveMissingScripts(prefabs[i]);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            Debug.Log($"[Tarkov SDK] {folder}: {removed} missing script(s) removed from {prefabs.Count} prefab(s).");
        };

        public static int RemoveMissingScripts(string prefabPath)
        {
            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                int removed = 0;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                if (removed > 0)
                    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                return removed;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tarkov SDK] {prefabPath}: missing scripts not removed ({e.Message})");
                return 0;
            }
            finally
            {
                if (root != null)
                    PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static string SelectedFolder()
        {
            var path = Selection.activeObject != null ? AssetDatabase.GetAssetPath(Selection.activeObject) : null;
            if (string.IsNullOrEmpty(path) || !AssetDatabase.IsValidFolder(path))
            {
                EditorUtility.DisplayDialog("Tarkov SDK", "Select a folder in the Project window first.", "OK");
                return null;
            }
            return path;
        }
    }

    // Unity's FileID of a class inside a DLL: the first four bytes (little-endian) of MD4("s\0\0\0" + namespace + name).
    // MD4 as RFC 1320; .NET has none.
    public static class TarkovFileId
    {
        public static int Compute(string ns, string name)
        {
            var hash = Md4(Encoding.UTF8.GetBytes("s\0\0\0" + ns + name));
            return BitConverter.ToInt32(hash, 0);
        }

        private static uint Rotl(uint x, int n) => (x << n) | (x >> (32 - n));

        public static byte[] Md4(byte[] data)
        {
            int padded = ((data.Length + 8) / 64 + 1) * 64;
            var msg = new byte[padded];
            Buffer.BlockCopy(data, 0, msg, 0, data.Length);
            msg[data.Length] = 0x80;
            ulong bits = (ulong)data.Length * 8;
            for (int i = 0; i < 8; i++)
                msg[padded - 8 + i] = (byte)(bits >> (8 * i));
            uint a = 0x67452301, b = 0xEFCDAB89, c = 0x98BADCFE, d = 0x10325476;
            var x = new uint[16];
            for (int offset = 0; offset < padded; offset += 64)
            {
                for (int i = 0; i < 16; i++)
                    x[i] = BitConverter.ToUInt32(msg, offset + i * 4);
                uint aa = a, bb = b, cc = c, dd = d;
                foreach (int i in new[] { 0, 4, 8, 12 })
                {
                    a = Rotl(a + ((b & c) | (~b & d)) + x[i], 3);
                    d = Rotl(d + ((a & b) | (~a & c)) + x[i + 1], 7);
                    c = Rotl(c + ((d & a) | (~d & b)) + x[i + 2], 11);
                    b = Rotl(b + ((c & d) | (~c & a)) + x[i + 3], 19);
                }
                foreach (int i in new[] { 0, 1, 2, 3 })
                {
                    a = Rotl(a + ((b & c) | (b & d) | (c & d)) + x[i] + 0x5A827999, 3);
                    d = Rotl(d + ((a & b) | (a & c) | (b & c)) + x[i + 4] + 0x5A827999, 5);
                    c = Rotl(c + ((d & a) | (d & b) | (a & b)) + x[i + 8] + 0x5A827999, 9);
                    b = Rotl(b + ((c & d) | (c & a) | (d & a)) + x[i + 12] + 0x5A827999, 13);
                }
                foreach (int i in new[] { 0, 2, 1, 3 })
                {
                    a = Rotl(a + (b ^ c ^ d) + x[i] + 0x6ED9EBA1, 3);
                    d = Rotl(d + (a ^ b ^ c) + x[i + 8] + 0x6ED9EBA1, 9);
                    c = Rotl(c + (d ^ a ^ b) + x[i + 4] + 0x6ED9EBA1, 11);
                    b = Rotl(b + (c ^ d ^ a) + x[i + 12] + 0x6ED9EBA1, 15);
                }
                a += aa;
                b += bb;
                c += cc;
                d += dd;
            }
            var result = new byte[16];
            Buffer.BlockCopy(BitConverter.GetBytes(a), 0, result, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(b), 0, result, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(c), 0, result, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(d), 0, result, 12, 4);
            return result;
        }
    }
}
