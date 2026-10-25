using System.Reflection;
using EFT.Vehicle;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace mizmii.CustomMapLoader.Loading
{
    // The BTR's route (MapPathConfig: stops and the roads between them) is a Resources prefab per retail map,
    // "MapPathsConfigurations/<map>/<map>MapPathsConfiguration". An SDK map has its route in its scene; the
    // server lists the map in BTRSettings.LocationsWithBTR and gives its routes and timing.
    internal class BtrMapPathPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BtrController), nameof(BtrController.LoadMapPathsConfiguration));

        [PatchPrefix]
        private static bool Prefix(BtrController __instance)
        {
            if (__instance.MapPathsConfiguration != null || !MapRegistry.IsCustomMapActive())
                return true;
            var config = Object.FindObjectOfType<MapPathConfig>();
            if (config == null)
            {
                Plugin.LogSource.LogWarning("BTR: the server enabled it on this map, but the map has no BTR route (MapPathConfig); no BTR");
                return true;
            }
            __instance.MapPathsConfiguration = config;
            Plugin.Verbose($"BTR: route from the scene ({config.PathDestinations.Count} stops, {config.PathSplines.Count} roads)");
            return false;
        }
    }

    // A game bug that only "once" routes reach (every retail route is a circle): CalculatePricesToDestination
    // asks GetNearestDestinationIndex for "<map>/<stop>", which compares it with the route's bare stop ids, finds
    // none and indexes an empty list. The throw leaves the taxi without destinations, and with no ride paid,
    // cover fire is never offered. This looks the stop up by its id.
    internal class BtrOnceRoutePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BtrController), nameof(BtrController.GetNearestDestinationIndex));

        [PatchPrefix]
        private static bool Prefix(BtrController __instance, string __0, int __1, ref int __result)
        {
            var points = __instance.BtrVehicle?.CurrentPathConfig?.pathPoints;
            if (points == null)
                return true;
            var id = __0 ?? "";
            int slash = id.LastIndexOf('/');
            if (slash >= 0)
                id = id.Substring(slash + 1);
            int best = -1, bestDistance = int.MaxValue;
            for (int i = 0; i < points.Count; i++)
                if (points[i] == id && System.Math.Abs(__1 - i) < bestDistance)
                {
                    best = i;
                    bestDistance = System.Math.Abs(__1 - i);
                }
            __result = best >= 0 ? best : __1;
            return false;
        }
    }
}
