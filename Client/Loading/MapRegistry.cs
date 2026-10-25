using System;
using System.Collections.Generic;
using System.IO;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace mizmii.CustomMapLoader.Loading
{
    // Indexes the bundles shipped next to the plugin DLL:
    //
    //   BepInEx/plugins/CustomMapLoader/bundles/maps/<id>.bundle          (scenes preset)
    //   BepInEx/plugins/CustomMapLoader/bundles/maps/<id>_scenes.bundle   (scenes)
    //
    // Each file is registered under its path relative to bundles/, lowercase with forward slashes,
    // which is the key the game asks BundlesManager for (base.json Scene.path).
    internal static class MapRegistry
    {
        private const string ScenesSuffix = "_scenes";

        public static string BundleRoot { get; private set; }

        public static readonly Dictionary<string, string> BundleFiles =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static readonly HashSet<string> MapIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void Init(string pluginDllPath)
        {
            BundleRoot = Path.Combine(Path.GetDirectoryName(pluginDllPath) ?? ".", "bundles");
            BundleFiles.Clear();
            MapIds.Clear();
            if (!Directory.Exists(BundleRoot))
                return;

            foreach (var file in Directory.GetFiles(BundleRoot, "*.bundle", SearchOption.AllDirectories))
            {
                var key = file.Substring(BundleRoot.Length + 1).Replace('\\', '/').ToLowerInvariant();
                BundleFiles[key] = file;

                if (key.StartsWith("maps/", StringComparison.Ordinal))
                {
                    var name = Path.GetFileNameWithoutExtension(key);
                    if (!name.EndsWith(ScenesSuffix, StringComparison.Ordinal))
                        MapIds.Add(name);
                }
            }
        }

        public static bool TryGetBundleFile(string key, out string file)
        {
            file = null;
            if (string.IsNullOrEmpty(key))
                return false;
            return BundleFiles.TryGetValue(key.Replace('\\', '/'), out file);
        }

        // scene names contained in our scene bundles, recorded as the bundles are loaded
        private static readonly HashSet<string> SceneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void RegisterScenes(AssetBundle bundle)
        {
            foreach (var path in bundle.GetAllScenePaths())
                SceneNames.Add(Path.GetFileNameWithoutExtension(path));
        }

        public static bool IsCustomScene(Scene scene) => SceneNames.Contains(scene.name);

        // true while a raid on one of our maps is loading or running
        public static bool IsCustomMapActive()
        {
            try
            {
                // GameWorld.LocationId is only assigned in BaseLocalGame.Run, after the map has loaded
                var world = Singleton<GameWorld>.Instance;
                var id = world != null ? world.LocationId : null;
                if (!string.IsNullOrEmpty(id))
                    return MapIds.Contains(id);

                // during loading, recognise the map by its scenes
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene.isLoaded && SceneNames.Contains(scene.name))
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        // a map's baked audio (spatial audio routes, Meta acoustics) sits in bundles/maps/<id>_audio/; its
        // files are named after the map's scenes, so a name is enough to find one
        public static string FindAudioFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(BundleRoot))
                return null;
            var maps = Path.Combine(BundleRoot, "maps");
            if (!Directory.Exists(maps))
                return null;
            foreach (var folder in Directory.GetDirectories(maps, "*_audio"))
            {
                var file = Path.Combine(folder, fileName);
                if (File.Exists(file))
                    return file;
            }
            return null;
        }
    }
}
