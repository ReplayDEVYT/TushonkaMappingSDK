using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.EnvironmentEffect;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace mizmii.CustomMapLoader.Loading
{
    // Scene-level managers that retail maps ship and the game dereferences without checks.
    // Maps built with the current SDK include an EnvironmentManager; this covers maps built
    // without one, and adds the ObservedCullingManager the SDK cannot author yet.
    //
    //  - EnvironmentManager: Player.Init reads EnvironmentManager.Instance (indoor/outdoor state).
    //    A bare one treats the whole map as outdoors.
    //  - ObservedCullingManager: drives visibility of observed bodies (bots); without it they can
    //    render invisible.
    //
    // Runs before every Player.Init (player and bots); after the first call it is two null checks.
    // Approach from ManimalIcebreaker's Patch_EnsureEnvBeforeAnyPlayerInit, used with permission.
    internal class SceneSingletonPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(Player), nameof(Player.Init));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            if (!MapRegistry.IsCustomMapActive())
                return;

            if (EnvironmentManager.Instance == null)
            {
                new GameObject("SDK_EnvironmentManager").AddComponent<EnvironmentManager>();
                Plugin.LogSource.LogWarning("Custom map has no EnvironmentManager; added one (the whole map counts as outdoors). Rebuild the map with the current SDK.");
            }

            if (!Singleton<ObservedCullingManager>.Instantiated)
            {
                new GameObject("SDK_ObservedCullingManager").AddComponent<ObservedCullingManager>();
                Plugin.Verbose("Added ObservedCullingManager for the custom map");
            }
        }
    }

    // BotsController.Init calls FindObjectOfType<AIStationaryController>().Init(...) without a null
    // check, so a map without one fails the raid start. Current SDK builds include one; this covers
    // maps built before that. An empty controller has no stationary weapons.
    internal class StationaryControllerPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotsController), nameof(BotsController.Init));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            if (!MapRegistry.IsCustomMapActive() || Object.FindObjectOfType<AIStationaryController>() != null)
                return;
            new GameObject("SDK_AIStationaryController").AddComponent<AIStationaryController>();
            Plugin.LogSource.LogWarning("Custom map has no AIStationaryController; added an empty one. Rebuild the map with the current SDK.");
        }
    }
}
