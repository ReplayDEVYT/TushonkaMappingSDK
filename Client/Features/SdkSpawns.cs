using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using mizmii.CustomMapLoader.Loading;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace mizmii.CustomMapLoader.Features
{
    // Triggered spawns and mortar strikes. Unlike the player features these act for whoever fires the trigger, once per
    // raid (not per player) when Once:
    //  - bots: BotCreationData for the role, difficulty and count (profiles from the server, as any bot), a position per
    //    bot from the points, then BotSpawner.method_10 into the nearest bot zone, as BotSpawner.method_7 does for wave
    //    spawns (it counts them into _inSpawnProcess first; ActivateBotCallback takes each one off). Their core point is
    //    0, which StartCorePointPatch replaces with the nearest generated one
    //  - loot: ItemFactory.GetPresetItem (a weapon's default preset, else the bare item) dropped with GameWorld.ThrowItem
    //    at rest, or SetupItem (kinematic) without gravity
    //  - prefabs: the object (or its switched-off template) found by scene and path, copied at the points or switched
    //  - mortar: the zone from the shelling controller's map configuration, its target points found as planned shelling
    //    finds them (only StartPlanedShelling does it for every zone), then StartEventShelling with the trigger's delay
    // Bots, loot and shelling are the host's in Fika co-op (it owns and syncs bots and shells; a new loose item can't be
    // sent to the others); prefabs run on every machine, so everyone sees them.
    internal static class SdkSpawns
    {
        private static readonly HashSet<string> Done = new HashSet<string>();
        private static bool? _host;

        public static void Reset()
        {
            Done.Clear();
            _host = null;
        }

        // the machine that owns bots and loot: always without Fika, Fika's host with it
        public static bool IsHost()
        {
            if (_host == null)
            {
                var utils = AccessTools.TypeByName("Fika.Core.Main.Utils.FikaBackendUtils");
                var property = utils != null ? AccessTools.Property(utils, "IsServer") : null;
                _host = property == null || (bool)property.GetValue(null);
            }
            return _host.Value;
        }

        public static bool FirstTime(bool once, string key) => !once || Done.Add(key);

        private static async Task Wait(float seconds)
        {
            float until = Time.time + seconds;
            while (Time.time < until)
                await Task.Yield();
        }

        private static List<(Vector3 Position, Quaternion Rotation)> Points(List<FeatureConfig.SpawnPoint> points) =>
            (points ?? new List<FeatureConfig.SpawnPoint>())
            .Where(p => p?.Position != null)
            .Select(p => (p.Position.Value, Quaternion.Euler(p.Rotation?.Value ?? Vector3.zero)))
            .ToList();

        // ------------------------------------------------------------ bots

        public static async void Bots(FeatureConfig.SpawnBotsLink link)
        {
            try
            {
                if (UnityEngine.Random.value > link.Chance)
                    return;
                if (link.Delay > 0f)
                    await Wait(link.Delay);
                var controller = Singleton<IBotGame>.Instance?.BotsController;
                var spawner = controller?.BotSpawner;
                var points = Points(link.Points);
                if (spawner == null || points.Count == 0)
                {
                    Plugin.LogSource.LogWarning($"Map features: bot spawn '{link.TriggerId}': {(spawner == null ? "the raid has no bot spawner" : "no spawn points")}");
                    return;
                }
                if (link.MaxAliveBots > 0 && controller.AliveAndLoadingBotsCount >= link.MaxAliveBots)
                {
                    Plugin.Verbose($"Map features: bot spawn '{link.TriggerId}' skipped, {controller.AliveAndLoadingBotsCount} bots alive");
                    return;
                }
                var role = Enum.TryParse<WildSpawnType>(link.Role, out var r) ? r : WildSpawnType.assault;
                var difficulty = Enum.TryParse<BotDifficulty>(link.Difficulty, out var d) ? d : BotDifficulty.normal;
                var side = role == WildSpawnType.pmcBEAR ? EPlayerSide.Bear : role == WildSpawnType.pmcUSEC ? EPlayerSide.Usec : EPlayerSide.Savage;
                int count = Mathf.Max(1, link.Count);
                var spawnParams = new BotSpawnParams();
                if (link.AsGroup && count > 1)
                    spawnParams.ShallBeGroup = new ShallBeGroupParams(true, true, count);

                var data = await BotCreationData.Create(new GetProfileDataParams(side, role, difficulty, 0f, spawnParams), spawner._botCreator, count, spawner);
                if (data == null)
                {
                    Plugin.LogSource.LogWarning($"Map features: bot spawn '{link.TriggerId}': the server gave no {role} profiles");
                    return;
                }
                for (int i = 0; i < count; i++)
                    data.AddPosition(points[i % points.Count].Position, 0);
                var zone = spawner.GetClosestZone(points[0].Position, out _) ?? spawner.GetRandomBotZone(false);
                if (zone == null)
                {
                    Plugin.LogSource.LogWarning($"Map features: bot spawn '{link.TriggerId}': the map has no bot zone");
                    return;
                }
                spawner._inSpawnProcess += data.Count;
                spawner.method_10(zone, data, null, spawner.GetCancelToken());
                Plugin.Verbose($"Map features: spawning {data.Count} {role} ({difficulty}) in {zone.name}");
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogError($"Map features: bot spawn '{link.TriggerId}' failed: {e}");
            }
        }

        // ------------------------------------------------------------ loot

        public static async void Loot(FeatureConfig.SpawnLootLink link)
        {
            try
            {
                if (UnityEngine.Random.value > link.Chance)
                    return;
                if (link.Delay > 0f)
                    await Wait(link.Delay);
                var world = Singleton<GameWorld>.Instance;
                var factory = Singleton<ItemFactory>.Instance;
                var items = (link.Items ?? new List<FeatureConfig.LootItem>()).Where(i => !string.IsNullOrEmpty(i?.Template)).ToList();
                var points = Points(link.Points);
                if (world == null || factory == null || items.Count == 0 || points.Count == 0)
                    return;
                var picks = link.Pick == "OnePerPoint"
                    ? points.Select(p => (Item: Weighted(items), Point: p)).ToList()
                    : items.Select((item, i) => (Item: item, Point: points[i % points.Count])).ToList();
                int spawned = 0;
                foreach (var (entry, point) in picks)
                {
                    var item = factory.GetPresetItem(entry.Template);
                    if (item == null)
                    {
                        Plugin.LogSource.LogWarning($"Map features: loot spawn '{link.TriggerId}': no item {entry.Template}");
                        continue;
                    }
                    if (item.StackMaxSize > 1)
                        item.StackObjectsCount = Mathf.Clamp(entry.StackCount, 1, item.StackMaxSize);
                    var rotation = link.RandomRotation ? UnityEngine.Random.rotation : point.Rotation;
                    var loot = link.UseGravity
                        ? world.ThrowItem(item, null, point.Position, rotation, Vector3.zero, Vector3.zero, true)
                        : world.SetupItem(item, null, point.Position, rotation);
                    if (loot != null)
                        spawned++;
                }
                Plugin.Verbose($"Map features: loot spawn '{link.TriggerId}': {spawned} item(s)");
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogError($"Map features: loot spawn '{link.TriggerId}' failed: {e}");
            }
        }

        private static FeatureConfig.LootItem Weighted(List<FeatureConfig.LootItem> items)
        {
            int total = items.Sum(i => Mathf.Max(1, i.Weight));
            int roll = UnityEngine.Random.Range(0, total);
            foreach (var item in items)
            {
                roll -= Mathf.Max(1, item.Weight);
                if (roll < 0)
                    return item;
            }
            return items[items.Count - 1];
        }

        // ------------------------------------------------------------ prefabs

        public static async void Prefab(FeatureConfig.SpawnPrefabLink link)
        {
            try
            {
                if (UnityEngine.Random.value > link.Chance)
                    return;
                if (link.Delay > 0f)
                    await Wait(link.Delay);
                var target = Find(link.Scene, link.Path);
                if (target == null)
                {
                    Plugin.LogSource.LogWarning($"Map features: prefab spawn '{link.TriggerId}': '{link.Path}' isn't in scene '{link.Scene}'");
                    return;
                }
                switch (link.Action)
                {
                    case "Show":
                        target.SetActive(true);
                        break;
                    case "Hide":
                        target.SetActive(false);
                        break;
                    default:
                        foreach (var (position, rotation) in Points(link.Points))
                        {
                            var copy = Object.Instantiate(target, position, rotation);
                            copy.SetActive(true);
                            if (link.Lifetime > 0f)
                                Object.Destroy(copy, link.Lifetime);
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogError($"Map features: prefab spawn '{link.TriggerId}' failed: {e}");
            }
        }

        // as Build Map checked it: the first root with the name, then the first child with each name (inactive included)
        internal static GameObject Find(string sceneName, string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            var names = path.Split('/');
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded || scene.name != sceneName || !MapRegistry.IsCustomScene(scene))
                    continue;
                var current = scene.GetRootGameObjects().FirstOrDefault(g => g.name == names[0])?.transform;
                for (int i = 1; i < names.Length && current != null; i++)
                {
                    Transform next = null;
                    foreach (Transform child in current)
                        if (child.name == names[i])
                        {
                            next = child;
                            break;
                        }
                    current = next;
                }
                if (current != null)
                    return current.gameObject;
            }
            return null;
        }

        // ------------------------------------------------------------ mortar

        public static void Mortar(FeatureConfig.MortarStrike link)
        {
            try
            {
                var server = Singleton<GameWorld>.Instance?.ServerShellingController;
                if (server == null)
                {
                    Plugin.LogSource.LogWarning($"Map features: mortar strike '{link.TriggerId}': the raid has no mortar shelling (is the map's mortar set up, and the server mod up to date?)");
                    return;
                }
                var zones = server._currentMapConfiguration.ShellingZones;
                int index = zones == null ? -1 : Array.FindIndex(zones, z => z.ID == link.ZoneId);
                if (index < 0)
                {
                    Plugin.LogSource.LogWarning($"Map features: mortar strike '{link.TriggerId}': the map has no mortar zone {link.ZoneId}");
                    return;
                }
                var zone = zones[index];
                server.FindShellingPointsByMapGeometryForZone(ref zone);
                Plugin.Verbose($"Map features: mortar strike on zone {link.ZoneId} in {link.Delay:0} s");
                Run(server.StartEventShelling(zone, Mathf.Max(link.Delay, 0f)), link.TriggerId);
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogError($"Map features: mortar strike '{link.TriggerId}' failed: {e}");
            }
        }

        private static async void Run(Task task, string what)
        {
            try
            {
                await task;
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogError($"Map features: mortar strike '{what}' failed: {e}");
            }
        }
    }
}
