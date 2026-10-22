using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using mizmii.CustomMapLoader.AI;
using mizmii.CustomMapLoader.Loading;

namespace mizmii.CustomMapLoader
{
    [BepInPlugin("mizmii.CustomMapLoader", "CustomMapLoader", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource LogSource;

        public static ConfigEntry<bool> GenerateAiData;
        public static ConfigEntry<bool> VerboseLogging;
        public static ConfigEntry<bool> RaidDiagnostics;

        private void Awake()
        {
            LogSource = Logger;

            GenerateAiData = Config.Bind("AI", "Generate AI navigation data", true,
                "On custom maps without baked AI data, generate cover points, voxels, door links and patrols from the navmesh at raid start.");
            VerboseLogging = Config.Bind("Debug", "Verbose logging", false,
                "Log every bundle served and per-stage AI generation counts.");
            RaidDiagnostics = Config.Bind("Debug", "Raid diagnostics", true,
                "On custom maps, log the lighting, camera, material and bot state 8 and 30 seconds into the raid.");

            MapRegistry.Init(Info.Location);
            if (MapRegistry.MapIds.Count == 0)
            {
                LogSource.LogWarning($"No custom maps found under {MapRegistry.BundleRoot}. Build a map in the Unity SDK and deploy it here.");
            }
            else
            {
                LogSource.LogInfo($"Custom maps: {string.Join(", ", MapRegistry.MapIds)}");
            }

            ShaderSwap.Install();
            Diagnostics.RaidDiagnostics.Install();
            Enable(new BundleHostPatch());
            Enable(new SpatialAudioInitPatch());
            Enable(new SceneSingletonPatch());
            Enable(new StationaryControllerPatch());
            Enable(new AmbientLightShaderPatch());
            Enable(new LevelSettingsFixPatch());
            Enable(new SimpleTimeOfDayPatch());
            Enable(new RainCameraPatch());
            Enable(new Diagnostics.BotActivationDiagnostics());
            Enable(new CameraEffectsPatch());
            foreach (var patch in CameraSettingSetterPatch.ForAllSetters())
                Enable(patch);
            foreach (var patch in OcclusionWhenUninitializedPatch.ForAllOverloads())
                Enable(patch);
            Enable(new RestoreCoversPatch());
            Enable(new CachePointsPatch());
            Enable(new StartCorePointPatch());
        }

        // one failing patch must not stop the others from attaching
        private static void Enable(SPT.Reflection.Patching.ModulePatch patch)
        {
            try
            {
                patch.Enable();
            }
            catch (Exception e)
            {
                LogSource.LogError($"Failed to enable {patch.GetType().Name}: {e}");
            }
        }

        internal static void Verbose(string message)
        {
            if (VerboseLogging != null && VerboseLogging.Value)
                LogSource.LogInfo(message);
        }
    }
}
