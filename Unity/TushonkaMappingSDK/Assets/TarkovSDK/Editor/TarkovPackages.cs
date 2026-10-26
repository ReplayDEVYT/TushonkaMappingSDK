using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Unity packages the SDK works with but doesn't need: ProBuilder (TarkovProBuilder), Polybrush (TarkovPolybrush), Terrain Tools (extra terrain
    // brushes; TarkovTerrains sets terrains up for the game either way). Pinned to the last versions for Unity 2022.3 and written into Packages/manifest.json like the
    // Meta XR one (TarkovMetaXrPackage), so Package Manager resolves them; the SDK reaches them by reflection and
    // compiles without them.
    public static class TarkovPackages
    {
        private const string ManifestPath = "Packages/manifest.json";
        // a setup to run once the package's assemblies are loaded (Package Manager reloads the domain after installing)
        private const string PendingKey = "TarkovSDK.PendingPackageSetup";

        public const string ProBuilder = "com.unity.probuilder";
        public const string TerrainTools = "com.unity.terrain-tools";

        [MenuItem("Tarkov SDK/Install Packages/ProBuilder (5.2.4)", priority = 221)]
        private static void InstallProBuilder() => EditorApplication.delayCall += () => Install(ProBuilder, "5.2.4", "ProBuilder", TarkovProBuilder.Available);

        [MenuItem("Tarkov SDK/Install Packages/Terrain Tools (5.0.6)", priority = 223)]
        private static void InstallTerrainTools() => EditorApplication.delayCall += () =>
            Install(TerrainTools, "5.0.6", "Terrain Tools", Type.GetType("UnityEditor.TerrainTools.TerrainToolsAnalytics, Unity.TerrainTools.Editor") != null);

        public static void Install(string package, string version, string label, bool loaded)
        {
            var manifest = JObject.Parse(File.ReadAllText(ManifestPath));
            var dependencies = manifest["dependencies"] as JObject;
            if (dependencies == null)
                manifest["dependencies"] = dependencies = new JObject();
            var current = (string)dependencies[package];
            if (current != null && loaded)
            {
                Debug.Log($"[Tarkov SDK] {label} {current} is already installed.");
                AfterInstall(package);
                return;
            }
            if (current == null)
            {
                dependencies[package] = version;
                File.WriteAllText(ManifestPath, manifest.ToString(Formatting.Indented));
                Debug.Log($"[Tarkov SDK] {label} {version} added to {ManifestPath}; Package Manager is installing it.");
            }
            else
                Debug.Log($"[Tarkov SDK] {label} {current} is in {ManifestPath} but not loaded; asking Package Manager to install it.");
            if (package == ProBuilder)
                EditorPrefs.SetString(PendingKey, package);
            Client.Resolve();
        }

        private static void AfterInstall(string package)
        {
            if (package == ProBuilder)
                TarkovProBuilder.SetUp();
        }

        [InitializeOnLoadMethod]
        private static void RunPending()
        {
            var pending = EditorPrefs.GetString(PendingKey, "");
            if (pending == ProBuilder && TarkovProBuilder.Available)
            {
                EditorPrefs.DeleteKey(PendingKey);
                EditorApplication.delayCall += () => AfterInstall(pending);
            }
        }
    }
}
