using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // The Meta XR Audio SDK at the version the game ships (native MetaXRAudioUnity 1.103.0 = package 71.0.0;
    // a newer package's serialized fields and bake files don't match the game's). It comes from Meta's npm
    // registry, which Package Manager's Add by name can't add, so this writes the scoped registry and the
    // pinned dependency into Packages/manifest.json and lets Package Manager resolve it.
    public static class TarkovMetaXrPackage
    {
        public const string PackageName = "com.meta.xr.sdk.audio";
        public const string Version = "71.0.0";
        private const string RegistryUrl = "https://npm.developer.oculus.com";
        private const string RegistryScope = "com.meta.xr.sdk";
        private const string ManifestPath = "Packages/manifest.json";

        [MenuItem("Tarkov SDK/Quick Fixes/Install Meta XR Audio SDK (71.0.0)", priority = 203)]
        private static void InstallMenu() => EditorApplication.delayCall += () => Install();

        public static void Install()
        {
            var manifest = JObject.Parse(File.ReadAllText(ManifestPath));
            bool changed = false;

            var registries = manifest["scopedRegistries"] as JArray;
            if (registries == null)
                manifest["scopedRegistries"] = registries = new JArray();
            var registry = registries.OfType<JObject>().FirstOrDefault(r => ((string)r["url"])?.TrimEnd('/') == RegistryUrl);
            if (registry == null)
            {
                registries.Add(new JObject { ["name"] = "Meta XR", ["url"] = RegistryUrl, ["scopes"] = new JArray(RegistryScope) });
                changed = true;
            }
            else
            {
                var scopes = registry["scopes"] as JArray;
                if (scopes == null)
                    registry["scopes"] = scopes = new JArray();
                if (!scopes.Values<string>().Any(s => s == RegistryScope || RegistryScope.StartsWith(s + ".")))
                {
                    scopes.Add(RegistryScope);
                    changed = true;
                }
            }

            var dependencies = manifest["dependencies"] as JObject;
            if (dependencies == null)
                manifest["dependencies"] = dependencies = new JObject();
            var current = (string)dependencies[PackageName];
            if (current != Version)
            {
                dependencies[PackageName] = Version;
                changed = true;
            }

            if (changed)
            {
                File.WriteAllText(ManifestPath, manifest.ToString(Formatting.Indented));
                Debug.Log(current == null
                    ? $"[Tarkov SDK] Meta XR Audio SDK {Version} added to {ManifestPath}; Package Manager is installing it."
                    : $"[Tarkov SDK] Meta XR Audio SDK set from {current} to {Version} (the game's version) in {ManifestPath}; Package Manager is installing it.");
            }
            else if (TarkovAcoustics.Available)
            {
                Debug.Log($"[Tarkov SDK] Meta XR Audio SDK {Version} is already installed.");
                return;
            }
            else
                Debug.Log($"[Tarkov SDK] Meta XR Audio SDK {Version} is in {ManifestPath} but not loaded; asking Package Manager to install it.");
            Client.Resolve();
        }
    }
}
