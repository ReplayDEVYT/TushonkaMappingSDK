using System.Collections.Generic;
using System.Linq;
using EFT.EnvironmentEffect;
using EFT.Weather;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // "Entirely Indoors" on the Map Definition, applied by Build Map before the scenes are prepared.
    // The map is set up like Labs, the game's one map without an outside:
    //
    //  - one indoor trigger over the whole map (indoor sound, exposure, rain heard muffled), in its
    //    own TriggerGroup, SDK_WholeMapIndoor, under the EnvironmentManager's indoor triggers
    //  - the simple time of day's TODSkySimple._forceIndoor = 1 and TodLightSetter._forceIndoorIntensity
    //    = 0.5 (Labs' values; Factory and Labyrinth, which have outdoor parts, use 0 and 1): the game's
    //    shaders light every surface as indoors
    //  - with the full weather system (whose sky can't force indoors), a rain cover over the whole map,
    //    so rain never falls on the screen
    //
    // Unticking it removes the trigger and puts the simple sky back to outdoor values; the rain cover
    // stays until it is regenerated.
    public static class TarkovIndoorMap
    {
        public const string RootName = "SDK_WholeMapIndoor";
        private const float Margin = 10f;

        public static void Apply(TarkovMapDefinition def, List<Scene> scenes, TarkovScenePreparer.Report report)
        {
            var existing = scenes.SelectMany(s => s.GetRootGameObjects())
                .SelectMany(g => g.GetComponentsInChildren<TriggerGroup>(true))
                .Where(g => g.name == RootName).Select(g => g.gameObject).ToList();
            if (!def.EntirelyIndoors)
            {
                if (existing.Count == 0)
                    return;
                existing.ForEach(Object.DestroyImmediate);
                SetSky(scenes, 0f, 1f);
                Debug.Log($"[Tarkov SDK] '{def.MapId}' is no longer entirely indoors: removed the whole-map indoor trigger and reset the sky's indoor lighting.");
                return;
            }

            // everything solid in every scene of the map, with room around it
            bool any = false;
            var bounds = new Bounds();
            foreach (var scene in scenes)
                if (TarkovRainCover.SolidBounds(scene, TarkovRainCover.SolidMask, out var b))
                {
                    if (!any) { bounds = b; any = true; }
                    else bounds.Encapsulate(b);
                }
            if (!any)
            {
                report.Warnings.Add("Entirely Indoors: the map has no solid colliders, so no indoor trigger was made");
                return;
            }
            bounds.Expand(Margin * 2f);

            existing.ForEach(Object.DestroyImmediate);
            var root = new GameObject(RootName);
            root.transform.SetParent(TarkovSceneTools.IndoorTriggerGroup().transform, false);
            root.transform.SetPositionAndRotation(bounds.center, Quaternion.identity);
            root.AddComponent<TriggerGroup>();
            var trigger = new GameObject("IndoorTrigger");
            trigger.transform.SetParent(root.transform, false);
            trigger.transform.SetPositionAndRotation(bounds.center, Quaternion.identity);
            // the trigger is the box of its transform
            var parentScale = root.transform.lossyScale;
            trigger.transform.localScale = new Vector3(bounds.size.x / parentScale.x, bounds.size.y / parentScale.y, bounds.size.z / parentScale.z);
            trigger.AddComponent<IndoorTrigger>();

            int skies = SetSky(scenes, 1f, 0.5f);
            var weatherScene = scenes.FirstOrDefault(s => TarkovWeatherBuilder.Find(s) != null);
            if (weatherScene.IsValid())
                TarkovRainCover.GenerateWholeMap(weatherScene, bounds);
            Debug.Log($"[Tarkov SDK] '{def.MapId}' is entirely indoors: one indoor trigger of {bounds.size.x:0} x {bounds.size.y:0} x {bounds.size.z:0} m" +
                      (skies > 0 ? ", simple sky set to indoor lighting" : "") + (weatherScene.IsValid() ? ", rain cover over the whole map" : "") + ".");
        }

        private static int SetSky(List<Scene> scenes, float forceIndoor, float indoorIntensity)
        {
            int count = 0;
            foreach (var root in scenes.SelectMany(s => s.GetRootGameObjects()))
            {
                foreach (var sky in root.GetComponentsInChildren<TODSkySimple>(true))
                {
                    sky._forceIndoor = forceIndoor;
                    EditorUtility.SetDirty(sky);
                    count++;
                }
                foreach (var setter in root.GetComponentsInChildren<TodLightSetter>(true))
                {
                    setter._forceIndoorIntensity = indoorIntensity;
                    EditorUtility.SetDirty(setter);
                }
            }
            return count;
        }
    }
}
