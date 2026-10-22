using System;
using System.Reflection;
using HarmonyLib;
using mizmii.CustomMapLoader.Loading;
using SPT.Reflection.Patching;

namespace mizmii.CustomMapLoader.AI
{
    // BotsController.Init runs AICoversData.CreateOrFind -> RestoreData -> CachePoints. Retail maps
    // ship baked cover/voxel/patrol data that RestoreData wires up. SDK maps ship none, so on a
    // custom map with empty data we build it from the navmesh instead and skip the original.
    internal class RestoreCoversPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(AICoversData), nameof(AICoversData.RestoreData));
        }

        [PatchPrefix]
        private static bool Prefix(AICoversData __instance, ref bool __result)
        {
            if (!Plugin.GenerateAiData.Value || !MapRegistry.IsCustomMapActive())
                return true;
            // a map that does ship baked data is restored by the game as usual
            if (__instance.Points != null && __instance.Points.Count > 0)
                return true;

            try
            {
                __result = NavGraphBuilder.Build(__instance);
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogError($"AI data generation failed, bots may not move: {e}");
                __result = false;
            }
            return false;
        }
    }

    // CachePoints pre-warms a cache on top of RestoreData's output. On a custom map a failure here
    // only costs a cold cache, so don't let it abort raid loading.
    internal class CachePointsPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(AICoversData), nameof(AICoversData.CachePoints));
        }

        [PatchFinalizer]
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null || !MapRegistry.IsCustomMapActive())
                return __exception;
            Plugin.LogSource.LogWarning($"AICoversData.CachePoints failed on a custom map, continuing with a cold cache: {__exception.Message}");
            return null;
        }
    }
}
