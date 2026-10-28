using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EFT;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Builds a map the way the game loads one:
    //
    //   base.json  Scene = { path: "maps/<id>.bundle", rcid: "<id>.scenespreset.asset" }
    //   maps/<id>.bundle          -> EFT.ScenesPreset listing the scenes (first = Single, rest Additive)
    //   maps/<id>_scenes.bundle   -> every scene of the map, self-contained
    //
    // The client plugin serves both bundles from BepInEx/plugins/<plugin>/bundles/, keyed by their
    // path relative to that folder.
    public static class TarkovMapBuilder
    {
        public const string GeneratedRoot = "Assets/TarkovSDK/Generated";

        public static string PresetBundleKey(string mapId) => $"maps/{mapId}.bundle";
        public static string ScenesBundleKey(string mapId) => $"maps/{mapId}_scenes.bundle";
        public static string PresetRcid(string mapId) => $"{mapId}.scenespreset.asset";

        [MenuItem("Tarkov SDK/Build Selected Map", priority = 0)]
        private static void BuildSelected()
        {
            var def = Selection.activeObject as TarkovMapDefinition;
            if (def == null)
            {
                EditorUtility.DisplayDialog("Tarkov SDK", "Select a Map Definition asset first (Create > Tarkov SDK > Map Definition).", "OK");
                return;
            }
            Build(def);
        }

        [MenuItem("Tarkov SDK/Build Selected Map", true)]
        private static bool BuildSelectedValidate() => Selection.activeObject is TarkovMapDefinition;

        public static bool Build(TarkovMapDefinition def) => Run(def, buildBundles: true);

        public static bool PrepareOnly(TarkovMapDefinition def) => Run(def, buildBundles: false);

        private static bool Run(TarkovMapDefinition def, bool buildBundles)
        {
            // a running preview has moved objects; put them back before anything is saved or built
            TarkovPreview.StopAll();
            if (!ValidateDefinition(def, out var scenePaths))
                return false;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return false;

            var setup = EditorSceneManager.GetSceneManagerSetup();
            var report = new TarkovScenePreparer.Report();
            // a scene the mapper works in but never added isn't built (its objects are missing in the raid)
            foreach (var stray in TarkovMapScenes.NotInMap(def))
                report.Warnings.Add($"{stray.name}: is open but not one of the Map Definition's scenes, so it isn't in the map (add it to Scenes, or close it)");
            try
            {
                EditorUtility.DisplayProgressBar("Tarkov SDK", "Preparing scenes", 0.1f);
                var scenes = new List<Scene>();
                for (int i = 0; i < scenePaths.Count; i++)
                    scenes.Add(EditorSceneManager.OpenScene(scenePaths[i], i == 0 ? OpenSceneMode.Single : OpenSceneMode.Additive));

                // before Prepare, which adds a missing manager or time of day to the first scene
                report.Warnings.AddRange(TarkovMapScenes.Gather(scenes));
                // before Prepare, which sizes indoor triggers and checks the rain cover
                TarkovIndoorMap.Apply(def, scenes, report);
                for (int i = 0; i < scenes.Count; i++)
                    TarkovScenePreparer.Prepare(scenes[i], i == 0, report);

                // all map scenes are open together here, so this sees a navmesh baked into any of them
                if (UnityEngine.AI.NavMesh.CalculateTriangulation().indices.Length == 0)
                    report.Warnings.Add("No navmesh found in any map scene. Bake one (Window > AI > Navigation) or bots cannot move");
                else if (scenes.Count > 1 && scenes.Skip(1).Any(s => TarkovMapScenes.NavMeshCovers(s) == false))
                    report.Warnings.Add("Part of the map has no navmesh under it: " + string.Join(", ", scenes.Skip(1).Where(s => TarkovMapScenes.NavMeshCovers(s) == false).Select(s => s.name)) +
                                        ". Bake with all the map's scenes open (Map Definition > Open All Scenes), or bots can't walk there");
                if (report.SpawnPoints.Count == 0)
                    report.Errors.Add("The map has no SpawnPointMarker; the game cannot spawn the player");

                foreach (var scene in scenes)
                    EditorSceneManager.SaveScene(scene);

                foreach (var w in report.Warnings)
                    Debug.LogWarning("[Tarkov SDK] " + w);
                foreach (var e in report.Errors)
                    Debug.LogError("[Tarkov SDK] " + e);
                // Tarkov SDK > Help > Errors explains them
                TarkovHelpWindow.RememberBuild(report.Errors, report.Warnings);
                if (report.Errors.Count > 0)
                {
                    if (EditorUtility.DisplayDialog("Tarkov SDK", $"{report.Errors.Count} error(s) in the map, see the Console.", "Explain Them", "OK"))
                        EditorApplication.delayCall += () => TarkovHelpWindow.Open("Errors");
                    return false;
                }
                if (!buildBundles)
                {
                    Debug.Log($"[Tarkov SDK] Prepared {scenes.Count} scene(s) for '{def.MapId}': {report.SpawnPoints.Count} spawn point(s), {report.Exfils.Count} exfil(s).");
                    return true;
                }

                // serialize the export now: building scene bundles reloads the open scenes, which
                // destroys the markers and exfils the report points to
                var exportJson = TarkovExportJson.Build(def.MapId, report);
                int spawnCount = report.SpawnPoints.Count, exfilCount = report.Exfils.Count;

                EditorUtility.DisplayProgressBar("Tarkov SDK", "Writing scenes preset", 0.3f);
                var presetPath = WritePreset(def.MapId, scenePaths);

                EditorUtility.DisplayProgressBar("Tarkov SDK", "Building bundles", 0.5f);
                var outDir = OutputDirectory(def);
                if (!BuildBundles(def.MapId, scenePaths, presetPath, outDir))
                    return false;

                var exportPath = Path.Combine(outDir, "maps", def.MapId, "sdk_export.json");
                Directory.CreateDirectory(Path.GetDirectoryName(exportPath));
                File.WriteAllText(exportPath, exportJson);
                WriteAudio(def.MapId, report.AudioFiles, outDir);

                if (def.DeployToGame)
                {
                    EditorUtility.DisplayProgressBar("Tarkov SDK", "Deploying to SPT", 0.9f);
                    Deploy(def, outDir);
                }

                if (def.CreatePackage)
                {
                    EditorUtility.DisplayProgressBar("Tarkov SDK", "Packaging", 0.95f);
                    Package(def, outDir);
                }

                // the Console's "Clear on Build" wipes everything logged before the bundle build, so the
                // warnings are repeated here, after it
                var summary = $"[Tarkov SDK] Built '{def.MapId}' to {outDir}: {spawnCount} spawn point(s), " +
                              $"{exfilCount} exfil(s), {report.Warnings.Count} warning(s).";
                if (report.Warnings.Count == 0)
                {
                    Debug.Log(summary);
                }
                else
                {
                    foreach (var w in report.Warnings)
                        Debug.LogWarning("[Tarkov SDK] " + w);
                    Debug.LogWarning(summary + "\n  - " + string.Join("\n  - ", report.Warnings));
                    if (!EditorUtility.DisplayDialog("Tarkov SDK",
                            $"Built '{def.MapId}' with {report.Warnings.Count} warning(s):\n\n- " + string.Join("\n- ", report.Warnings), "OK", "Explain Them"))
                        EditorApplication.delayCall += () => TarkovHelpWindow.Open("Errors");
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Tarkov SDK", "Build failed: " + e.Message, "OK");
                return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (setup != null && setup.Length > 0)
                    EditorSceneManager.RestoreSceneManagerSetup(setup);
            }
        }

        private static bool ValidateDefinition(TarkovMapDefinition def, out List<string> scenePaths)
        {
            scenePaths = new List<string>();
            if (def == null)
                return false;
            if (string.IsNullOrEmpty(def.MapId) || !Regex.IsMatch(def.MapId, "^[a-z0-9_]+$"))
            {
                EditorUtility.DisplayDialog("Tarkov SDK", "Map Id must be lowercase letters, digits and underscores.", "OK");
                return false;
            }
            foreach (var scene in def.Scenes)
            {
                var path = scene != null ? AssetDatabase.GetAssetPath(scene) : null;
                if (string.IsNullOrEmpty(path))
                    continue;
                scenePaths.Add(path);
            }
            if (scenePaths.Count == 0)
            {
                EditorUtility.DisplayDialog("Tarkov SDK", "Add at least one scene to the Map Definition.", "OK");
                return false;
            }
            var names = scenePaths.Select(Path.GetFileNameWithoutExtension).ToList();
            var duplicate = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null)
            {
                // the game loads scenes by file name
                EditorUtility.DisplayDialog("Tarkov SDK", $"Two scenes are named '{duplicate.Key}'. Scene names must be unique.", "OK");
                return false;
            }
            return true;
        }

        public static string OutputDirectory(TarkovMapDefinition def)
        {
            var folder = string.IsNullOrEmpty(def.OutputFolder) ? "Build" : def.OutputFolder;
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.GetFullPath(Path.IsPathRooted(folder) ? folder : Path.Combine(projectRoot, folder));
        }

        private static string WritePreset(string mapId, List<string> scenePaths)
        {
            var folder = $"{GeneratedRoot}/{mapId}";
            Directory.CreateDirectory(folder);
            // the game strips ".ScenesPreset" from the asset name to get the preset name
            var path = $"{folder}/{mapId}.ScenesPreset.asset";

            var preset = AssetDatabase.LoadAssetAtPath<ScenesPreset>(path);
            bool created = preset == null;
            if (created)
                preset = ScriptableObject.CreateInstance<ScenesPreset>();

            preset.ServerName = mapId;
            preset.ChildPresets = new ScenesPreset[0];
            preset._activeSceneName = Path.GetFileNameWithoutExtension(scenePaths[0]);
            preset._scenesResourceKeys = scenePaths
                .Select(p => new SceneResourceKey { path = ScenesBundleKey(mapId), rcid = p.ToLowerInvariant() })
                .ToList();
            preset.ScenesGuids = scenePaths
                .Select(p => new ScenesPreset.Guid { guid = AssetDatabase.AssetPathToGUID(p) })
                .ToList();
            preset.ActiveSceneGuid = preset.ScenesGuids[0];

            if (created)
                AssetDatabase.CreateAsset(preset, path);
            else
                EditorUtility.SetDirty(preset);
            AssetDatabase.SaveAssets();
            return path;
        }

        private static bool BuildBundles(string mapId, List<string> scenePaths, string presetPath, string outDir)
        {
            Directory.CreateDirectory(outDir);
            var builds = new[]
            {
                new AssetBundleBuild { assetBundleName = ScenesBundleKey(mapId), assetNames = scenePaths.ToArray() },
                new AssetBundleBuild { assetBundleName = PresetBundleKey(mapId), assetNames = new[] { presetPath } },
            };
            var manifest = BuildPipeline.BuildAssetBundles(outDir, builds,
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode,
                BuildTarget.StandaloneWindows64);
            if (manifest == null)
            {
                EditorUtility.DisplayDialog("Tarkov SDK", "Asset bundle build failed, see the Console.", "OK");
                return false;
            }
            return true;
        }

        private static void Deploy(TarkovMapDefinition def, string outDir)
        {
            var root = def.SptRoot;
            if (string.IsNullOrEmpty(root) || !File.Exists(Path.Combine(root, "EscapeFromTarkov.exe")))
            {
                Debug.LogError($"[Tarkov SDK] Deploy skipped: '{root}' is not an SPT install (no EscapeFromTarkov.exe).");
                return;
            }

            if (!LoaderInstalled(root, out var missingLoader))
                Debug.LogWarning($"[Tarkov SDK] The CustomMapLoader mod is not installed in {root} (missing {missingLoader}). " +
                                 "Install it once from its release; maps are add-ons and do not include it.");

            var bundleDir = ClientBundleDirectory(root);
            Directory.CreateDirectory(bundleDir);
            foreach (var key in new[] { PresetBundleKey(def.MapId), ScenesBundleKey(def.MapId) })
            {
                var src = Path.Combine(outDir, key);
                var dst = Path.Combine(bundleDir, Path.GetFileName(key));
                File.Copy(src, dst, overwrite: true);
            }

            var audioDir = Path.Combine(bundleDir, AudioFolderName(def.MapId));
            if (Directory.Exists(audioDir))
                Directory.Delete(audioDir, true);
            CopyFolder(Path.Combine(outDir, AudioFolderKey(def.MapId)), audioDir);

            var serverMapDir = ServerMapDirectory(def);
            Directory.CreateDirectory(serverMapDir);
            File.Copy(Path.Combine(outDir, "maps", def.MapId, "sdk_export.json"), Path.Combine(serverMapDir, "sdk_export.json"), overwrite: true);
            if (!File.Exists(Path.Combine(serverMapDir, "base.json")))
                Debug.LogWarning($"[Tarkov SDK] {serverMapDir} has no base.json. Open Edit Server Data on the Map Definition and use Create From sdk_example Template.");

            // the map's pack, when it is made in this project
            var packId = MenuPackId(serverMapDir);
            if (packId != null)
            {
                var pack = TarkovMapPack.Find(packId);
                if (pack != null)
                    pack.Deploy(root);
                else if (!Directory.Exists(TarkovMapPack.PackDirectory(root, packId)))
                    Debug.LogWarning($"[Tarkov SDK] '{def.MapId}' is on map pack '{packId}', which is neither in this project nor installed in {root}. " +
                                     "The map shows on Tarkov's page until the pack is installed.");
            }

            Debug.Log($"[Tarkov SDK] Deployed '{def.MapId}' to {root}. Restart the SPT server to pick up spawn/exfil changes.");
        }

        // ------------------------------------------------------------ CustomMapLoader layout

        // Maps are add-ons of the CustomMapLoader mod: no code of their own, just files in its folders.
        // The mod is installed once; every map goes into the same two folders.
        public const string LoaderName = "CustomMapLoader";
        public const string InstallNote =
            "Maps are add-ons for the CustomMapLoader mod. Install CustomMapLoader (client plugin and server mod) " +
            "once from its release before installing or deploying maps.";

        private static string ClientPluginRelative => Path.Combine("BepInEx", "plugins", LoaderName);
        private static string ServerModRelative => Path.Combine("SPT_Runtime", "user", "mods", LoaderName);

        public static string ClientBundleDirectory(string sptRoot) => Path.Combine(sptRoot, ClientPluginRelative, "bundles", "maps");

        public static string ServerMapDirectory(TarkovMapDefinition def) =>
            Path.Combine(def.SptRoot, ServerModRelative, "maps", def.MapId);

        public static string ServerModDirectory(string sptRoot) => Path.Combine(sptRoot, ServerModRelative);

        // the pack a map joins in its menu.json, or null
        public static string MenuPackId(string serverMapDir)
        {
            var path = Path.Combine(serverMapDir, "menu.json");
            if (!File.Exists(path))
                return null;
            try
            {
                var id = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path)).Value<string>("Pack");
                return string.IsNullOrWhiteSpace(id) ? null : id.Trim().ToLowerInvariant();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool LoaderInstalled(string sptRoot, out string missing)
        {
            var parts = new List<string>();
            if (!File.Exists(Path.Combine(sptRoot, ClientPluginRelative, LoaderName + ".dll")))
                parts.Add("the client plugin");
            if (!File.Exists(Path.Combine(sptRoot, ServerModRelative, LoaderName + ".dll")))
                parts.Add("the server mod");
            missing = string.Join(" and ", parts);
            return parts.Count == 0;
        }

        // Writes <output>/<MapId>.zip, laid out like an SPT install so players extract it into theirs:
        //
        //   BepInEx/plugins/CustomMapLoader/bundles/maps/<id>.bundle, <id>_scenes.bundle
        //   SPT_Runtime/user/mods/CustomMapLoader/maps/<id>/base.json, locale.json, sdk_export.json, menu.json, images/
        //   SPT_Runtime/user/mods/CustomMapLoader/packs/<pack>/            the map's pack, if it has one
        //   <id>_README.txt
        //
        // The server files come from the SPT install the definition deploys to.
        private static void Package(TarkovMapDefinition def, string outDir)
        {
            var staging = Path.Combine(outDir, "package", def.MapId);
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);

            var bundleDir = Path.Combine(staging, ClientPluginRelative, "bundles", "maps");
            var mapDir = Path.Combine(staging, ServerModRelative, "maps", def.MapId);
            Directory.CreateDirectory(bundleDir);
            Directory.CreateDirectory(mapDir);

            foreach (var key in new[] { PresetBundleKey(def.MapId), ScenesBundleKey(def.MapId) })
                File.Copy(Path.Combine(outDir, key), Path.Combine(bundleDir, Path.GetFileName(key)), true);
            CopyFolder(Path.Combine(outDir, AudioFolderKey(def.MapId)), Path.Combine(bundleDir, AudioFolderName(def.MapId)));
            File.Copy(Path.Combine(outDir, "maps", def.MapId, "sdk_export.json"), Path.Combine(mapDir, "sdk_export.json"), true);
            File.WriteAllText(Path.Combine(staging, def.MapId + "_README.txt"),
                $"{def.MapId}: a custom map for SPT.\r\n\r\nRequires the CustomMapLoader mod. Install it first, then extract this archive " +
                "into your SPT folder (the one containing EscapeFromTarkov.exe) and restart the SPT server.\r\n");

            var missing = new List<string>();
            var serverMapDir = ServerMapDirectory(def);
            foreach (var file in new[] { "base.json", "locale.json", "menu.json" })
            {
                var src = Path.Combine(serverMapDir, file);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(mapDir, file), true);
                else if (file == "base.json")
                    missing.Add("base.json (create it with Edit Server Data)");
            }
            CopyFolder(Path.Combine(serverMapDir, "images"), Path.Combine(mapDir, "images"));
            var packId = MenuPackId(serverMapDir);
            if (packId != null)
            {
                var packDir = TarkovMapPack.PackDirectory(def.SptRoot, packId);
                if (Directory.Exists(packDir))
                    CopyFolder(packDir, Path.Combine(staging, ServerModRelative, "packs", packId));
                else
                    missing.Add($"map pack '{packId}' (deploy it from its Map Pack asset)");
            }

            var zip = Path.Combine(outDir, def.MapId + ".zip");
            if (File.Exists(zip))
                File.Delete(zip);
            System.IO.Compression.ZipFile.CreateFromDirectory(staging, zip);

            if (missing.Count > 0)
                Debug.LogWarning($"[Tarkov SDK] Package {zip} is incomplete, missing: {string.Join(", ", missing)}");
            else
                Debug.Log($"[Tarkov SDK] Packaged '{def.MapId}' to {zip}. Extract it into an SPT install folder to play the map.");
        }

        // the map's baked audio (spatial audio routes, Meta acoustic map and geometry) in one folder next to its
        // bundles, bundles/maps/<id>_audio/, where CustomMapLoader looks them up by file name
        public static string AudioFolderName(string mapId) => mapId + "_audio";
        private static string AudioFolderKey(string mapId) => Path.Combine("maps", AudioFolderName(mapId));

        private static void WriteAudio(string mapId, List<string> files, string outDir)
        {
            var folder = Path.Combine(outDir, AudioFolderKey(mapId));
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
            if (files.Count == 0)
                return;
            Directory.CreateDirectory(folder);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files.Where(File.Exists))
            {
                var name = Path.GetFileName(file);
                if (!names.Add(name))
                    Debug.LogWarning($"[Tarkov SDK] Two audio files are named {name}; only the first ships. Rename one (its acoustic geometry's file path).");
                else
                    File.Copy(file, Path.Combine(folder, name), true);
            }
        }

        private static void CopyFolder(string source, string destination)
        {
            if (!Directory.Exists(source))
                return;
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        }
    }
}
