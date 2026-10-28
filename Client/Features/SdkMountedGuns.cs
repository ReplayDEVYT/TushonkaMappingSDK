using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using Diz.Resources;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using mizmii.CustomMapLoader.Loading;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace mizmii.CustomMapLoader.Features
{
    // Mounted guns the SDK marks on a map. The game's Utes ships as a world prefab (with its StationaryWeapon, belts and
    // machinery) inside the weapon's own container bundle, which BaseLocalGame.SpawnLoot loads with the raid's loot (the
    // server lists the gun as a static weapon, so its prefab path is among the loot's resources). Just before
    // GameWorld.SpawnLoot matches the loot to the scene's static loot by Id, this places a copy at each marker, gives it
    // the marker's id, recomputes its yaw and pitch limits for its rotation (StationaryWeapon.OnValidate, which outside
    // BSG's editor only does that) and registers it in the map's LocationScene, which SpawnLoot and
    // FindStationaryWeapon read. Then the game initialises it like a retail gun. Every machine places its own in co-op.
    internal static class SdkMountedGuns
    {
        private const string UtesBundle = "assets/content/weapons/utes/weapon_ckib_nsv_utes_127x108_container.bundle";
        private static readonly FieldInfo TypeToContainer = AccessTools.Field(typeof(LocationScene), "_typeToContainer");
        private static GameWorld _placedFor;

        public static void Place(GameWorld world)
        {
            if (world == null || _placedFor == world || !MapRegistry.IsCustomMapActive())
                return;
            _placedFor = world;
            var guns = FeatureConfig.For(world.LocationId)?.MountedGuns;
            if (guns == null || guns.Count == 0)
                return;
            var easy = Singleton<IEasyAssets>.Instance;
            if (easy == null || !easy.IsAssetLoaded(UtesBundle))
            {
                Plugin.LogSource.LogWarning($"Mounted guns: the Utes bundle isn't loaded (is the gun in the raid's loot? is the server mod up to date?); {guns.Count} gun(s) left out");
                return;
            }
            // the bundle has two GameObjects of this name; the world one carries the StationaryWeapon
            var prefab = easy.System.GetNode(UtesBundle).Data.Assets.OfType<GameObject>()
                .FirstOrDefault(g => g.GetComponentInChildren<StationaryWeapon>(true) != null);
            var scene = LocationScene.LoadedScenes.FirstOrDefault(s => s != null && MapRegistry.IsCustomScene(s.gameObject.scene));
            if (prefab == null || scene == null)
            {
                Plugin.LogSource.LogWarning($"Mounted guns: {(prefab == null ? "no Utes world prefab in its bundle" : "the map has no LocationScene")}; {guns.Count} gun(s) left out");
                return;
            }

            var placed = new List<StationaryWeapon>();
            foreach (var gun in guns.Where(g => !string.IsNullOrEmpty(g?.Id)))
            {
                try
                {
                    var go = Object.Instantiate(prefab, gun.Position?.Value ?? Vector3.zero, Quaternion.Euler(gun.Rotation?.Value ?? Vector3.zero));
                    go.name = "MountedGun_" + gun.Id;
                    SceneManager.MoveGameObjectToScene(go, scene.gameObject.scene);
                    var weapon = go.GetComponentInChildren<StationaryWeapon>(true);
                    weapon._id = gun.Id;
                    weapon.OnValidate();
                    placed.Add(weapon);
                }
                catch (Exception e)
                {
                    Plugin.LogSource.LogError($"Mounted guns: '{gun.Id}' not placed: {e}");
                }
            }
            if (placed.Count == 0)
                return;
            var all = (scene.StationaryWeapons ?? new StationaryWeapon[0]).Concat(placed).ToArray();
            scene.StationaryWeapons = all;
            // Awake registered the scene's own array; SpawnLoot reads the registered one
            if (TypeToContainer?.GetValue(scene) is Dictionary<Type, Array> registered)
                registered[typeof(StationaryWeapon)] = all;
            Plugin.Verbose($"Mounted guns: {placed.Count} Utes placed");
        }
    }

    internal class MountedGunsSpawnLootPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(GameWorld), nameof(GameWorld.SpawnLoot));

        [PatchPrefix]
        private static void Prefix(GameWorld __instance, bool initial)
        {
            if (!initial)
                return;
            try
            {
                SdkMountedGuns.Place(__instance);
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogError($"Mounted guns failed: {e}");
            }
        }
    }
}
