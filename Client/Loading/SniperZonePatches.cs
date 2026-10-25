using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace mizmii.CustomMapLoader.Loading
{
    // Sniper zones on SDK maps:
    //
    //  - SniperImmuneItemsPatch: a zone spares players carrying one of the items the SDK lists as its
    //    "SDK_ImmuneItem <template>" children. The game itself only spares flare-immune players, bots set to
    //    SNIPER_FIRE_IMMUNE and BTR passengers.
    //  - SniperImitatorEffectsPatch: the SniperImitator plays the shot with the SVD's SoundBank and muzzle flash,
    //    game assets an SDK map can't reference. A missing flash becomes a disabled FirearmsEffects (the game
    //    skips it), a missing sound one of the sniper banks loaded in the raid; with none loaded the shot is
    //    skipped rather than throwing (the zone's damage doesn't depend on it).
    internal class SniperImmuneItemsPatch : ModulePatch
    {
        private const string Prefix = "SDK_ImmuneItem ";
        private static readonly Dictionary<int, string[]> Items = new Dictionary<int, string[]>();

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(SniperFiringZone), nameof(SniperFiringZone.IsPlayerCanBeDamaged));

        [PatchPostfix]
        private static void Postfix(SniperFiringZone __instance, IPlayer player, ref bool __result)
        {
            if (!__result || !MapRegistry.IsCustomMapActive())
                return;
            var templates = ImmuneItems(__instance);
            if (templates.Length == 0 || !Singleton<GameWorld>.Instantiated)
                return;
            var alive = Singleton<GameWorld>.Instance.GetAlivePlayerByProfileID(player.ProfileId);
            if (alive?.Inventory == null)
                return;
            foreach (var item in alive.Inventory.GetPlayerItems(EPlayerItems.InRaidItems))
                if (Array.IndexOf(templates, item.TemplateId.ToString()) >= 0)
                {
                    __result = false;
                    return;
                }
        }

        private static string[] ImmuneItems(SniperFiringZone zone)
        {
            int key = zone.GetInstanceID();
            if (Items.TryGetValue(key, out var cached))
                return cached;
            var list = zone.transform.Cast<Transform>().Where(t => t.name.StartsWith(Prefix, StringComparison.Ordinal))
                           .Select(t => t.name.Substring(Prefix.Length).Trim().Split(' ')[0].ToLowerInvariant()).ToArray();
            Items[key] = list;
            if (list.Length > 0)
                Plugin.Verbose($"Sniper zone {zone.name}: players carrying {string.Join(", ", list)} aren't shot");
            return list;
        }
    }

    internal class SniperImitatorEffectsPatch : ModulePatch
    {
        // the game's own is the SVD's; then other bolt-action / DMR banks bots and players commonly carry
        private static readonly string[] Preferred = { "svd_main", "mosin", "sv98", "m700", "t5000", "vpo", "sks", "m1a" };

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(SniperImitator), nameof(SniperImitator.OnPlayerShotEvent));

        [PatchPrefix]
        private static bool Prefix(SniperImitator __instance)
        {
            if (!MapRegistry.IsCustomMapActive())
                return true;
            if (__instance.FirearmsEffects == null)
            {
                var effects = __instance.gameObject.AddComponent<FirearmsEffects>();
                effects.enabled = false;
                __instance.FirearmsEffects = effects;
            }
            if (__instance.SoundBank == null)
            {
                __instance.SoundBank = FindBank();
                if (__instance.SoundBank == null)
                    return false;
                Plugin.Verbose($"Sniper {__instance.name}: shot sound from {__instance.SoundBank.name}");
            }
            return true;
        }

        private static SoundBank FindBank()
        {
            var banks = Resources.FindObjectsOfTypeAll<SoundBank>();
            foreach (var name in Preferred)
            {
                var bank = banks.FirstOrDefault(b => b != null && b.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 && b.name.EndsWith("_main", StringComparison.OrdinalIgnoreCase));
                if (bank != null)
                    return bank;
            }
            return banks.FirstOrDefault(b => b != null && b.name.EndsWith("_main", StringComparison.OrdinalIgnoreCase));
        }
    }
}
