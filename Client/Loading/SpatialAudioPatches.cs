using System.Reflection;
using System.Threading.Tasks;
using Audio.SpatialSystem;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace mizmii.CustomMapLoader.Loading
{
    // Retail maps ship a configured SpatialAudioSystem plus a baked .audiobakedata file. SDK maps
    // have neither, so the game creates a blank system whose OcclusionSettings/poolsConfig are null
    // and Initialize throws, aborting the raid load. On custom maps, skip Initialize: the system
    // stays uninitialised and sounds play without room/portal occlusion.
    //
    // Approach from ManimalIcebreaker's RaidFixPatches (Patch_SpatialAudioInit and
    // Patch_OcclusionWhenUninitialized), used with the author's permission.
    internal class SpatialAudioInitPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(SpatialAudioSystem), nameof(SpatialAudioSystem.Initialize));
        }

        [PatchPrefix]
        private static bool Prefix(ref Task __result)
        {
            if (!MapRegistry.IsCustomMapActive())
            {
                Plugin.Verbose("SpatialAudioSystem.Initialize: not a custom map, running the original");
                return true;
            }
            Plugin.LogSource.LogInfo("Custom map: spatial audio (occlusion) is not initialised; sounds play unoccluded.");
            __result = Task.CompletedTask;
            return false;
        }
    }

    // BetterAudio still routes impacts and gunshots through ProcessSourceOcclusion, which throws on
    // an uninitialised system (and the effect replays the failing sound every frame). Return -1,
    // the game's own "no occlusion" result, until the system is initialised. Real maps initialise,
    // so this never changes their behaviour.
    internal class OcclusionWhenUninitializedPatch : ModulePatch
    {
        private readonly MethodBase _target;

        public OcclusionWhenUninitializedPatch(MethodBase target)
        {
            _target = target;
        }

        protected override MethodBase GetTargetMethod() => _target;

        public static OcclusionWhenUninitializedPatch[] ForAllOverloads()
        {
            var patches = new System.Collections.Generic.List<OcclusionWhenUninitializedPatch>();
            foreach (var method in typeof(SpatialAudioSystem).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (method.Name == nameof(SpatialAudioSystem.ProcessSourceOcclusion) && method.ReturnType == typeof(int))
                    patches.Add(new OcclusionWhenUninitializedPatch(method));
            return patches.ToArray();
        }

        [PatchPrefix]
        private static bool Prefix(ref int __result)
        {
            if (SpatialAudioSystem.Initialized)
                return true;
            __result = -1;
            return false;
        }
    }

    // Interactive objects (breakable windows, doors) occlude their sounds through
    // ProcessInteractiveObjectOcclusion, which also throws on an uninitialised system. For a window
    // the throw comes from PlayHitSound inside WindowBreaker.BreakByHit, after the impact effect but
    // before the glass breaks: only the first shot cracked it, later shots just spawned particles.
    // Skipped until the system is initialised; the sound then plays unoccluded.
    internal class InteractiveOcclusionWhenUninitializedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(SpatialAudioSystem), nameof(SpatialAudioSystem.ProcessInteractiveObjectOcclusion));

        [PatchPrefix]
        private static bool Prefix() => SpatialAudioSystem.Initialized;
    }
}
