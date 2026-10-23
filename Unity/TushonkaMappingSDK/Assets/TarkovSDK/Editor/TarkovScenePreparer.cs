using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Ballistics;
using EFT.Game.Spawning;
using EFT.Interactive;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Does what BSG's editor preprocess does before a map ships: the game never searches the scene
    // at runtime, it reads the arrays and ids serialized here.
    public static class TarkovScenePreparer
    {
        public sealed class Report
        {
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public readonly List<SpawnPointMarker> SpawnPoints = new List<SpawnPointMarker>();
            public readonly List<ExfiltrationPoint> Exfils = new List<ExfiltrationPoint>();
            public readonly List<TransitPoint> Transits = new List<TransitPoint>();
            public readonly List<TarkovSDK.TarkovLootSpawn> LootSpawns = new List<TarkovSDK.TarkovLootSpawn>();
            public long UnixDateTime;
        }

        // LocationScene arrays the game fills with active objects only
        private static readonly HashSet<string> ActiveOnlyArrays = new HashSet<string> { "BorderZones", "Windows" };

        // LocationScene arrays that BSG fills from interfaces we do not stub; empty is valid for them
        private static readonly HashSet<string> AlwaysEmptyArrays = new HashSet<string>
        {
            "TriggerEntities", "AudioSources", "BroadcastPlayers", "EventSoundContentChangers", "treeWindSettingsPresets",
        };

        public static void Prepare(Scene scene, bool isActiveScene, Report report)
        {
            var roots = scene.GetRootGameObjects();

            var locationScenes = All<LocationScene>(roots);
            LocationScene locationScene;
            if (locationScenes.Count == 0)
            {
                var go = new GameObject("LocationScene");
                SceneManager.MoveGameObjectToScene(go, scene);
                locationScene = go.AddComponent<LocationScene>();
                report.Warnings.Add($"{scene.name}: had no LocationScene, one was added");
                roots = scene.GetRootGameObjects();
            }
            else
            {
                locationScene = locationScenes[0];
                if (locationScenes.Count > 1)
                    report.Errors.Add($"{scene.name}: has {locationScenes.Count} LocationScene components, keep exactly one per scene");
            }

            if (isActiveScene)
            {
                var exportInfo = All<LocationExportInfo>(roots).FirstOrDefault()
                                 ?? locationScene.gameObject.AddComponent<LocationExportInfo>();
                // the game only applies backend spawn points if the scene is not newer than the backend
                // data; the exporter writes this same value into the server's base.json
                exportInfo._unixDateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                report.UnixDateTime = exportInfo._unixDateTime;
                EditorUtility.SetDirty(exportInfo);

                if (All<LevelSettings>(roots).Count == 0)
                    report.Warnings.Add($"{scene.name}: no LevelSettings in the active scene; ambient light and airdrops will not initialise");

                // the camera's GlobalFog applies LevelSettings' fog whether or not fog is on, and an
                // unset mode (0) fogs every opaque pixel in fogColor
                foreach (var level in All<LevelSettings>(roots))
                {
                    if (level.fogMode >= FogMode.Linear && level.fogMode <= FogMode.ExponentialSquared)
                        continue;
                    level.fogMode = FogMode.Exponential;
                    level.fogDensity = 0f;
                    EditorUtility.SetDirty(level);
                    report.Warnings.Add($"{scene.name}: LevelSettings had no fog mode, which renders the map black in game; set to Exponential with density 0 (no fog)");
                }

                // retail scenes: no default reflection. Unity's default reflects the skybox on every
                // surface, a static daytime cubemap that makes the map shine at night
                if (SceneManager.GetActiveScene() == scene)
                    TarkovLightingPreview.ApplyRetailReflection();

                bool fullWeather = TarkovWeatherBuilder.Find(scene) != null;
                foreach (var level in All<LevelSettings>(roots))
                {
                    // the full weather system's TOD sky draws the sky dome; a skybox would paint over it and the clouds
                    if (fullWeather)
                    {
                        if (level.skybox != null)
                        {
                            level.skybox = null;
                            EditorUtility.SetDirty(level);
                            report.Warnings.Add($"{scene.name}: LevelSettings skybox removed; with the full weather system it would hide the TOD sky and clouds");
                        }
                    }
                    else if (level.skybox == null && (level.skybox = TarkovLightingRig.DefaultSkybox()) != null)
                    {
                        EditorUtility.SetDirty(level);
                        report.Warnings.Add($"{scene.name}: LevelSettings had no skybox (a black sky in game); set to Assets/TarkovSDK/Materials/SDK_Skybox.mat");
                    }
                    // the lighting rig supplies ambient; retail maps keep Unity's ambient near 0.02
                    var ambient = level.SkyColor.maxColorComponent * level.AmbientIntensity;
                    if (ambient > 0.15f)
                        report.Warnings.Add($"{scene.name}: LevelSettings ambient (Sky Color x Ambient Intensity = {ambient:0.##}) is far above retail maps (about 0.02) and washes the map out in game; Factory uses Sky Color 0.03 at intensity 0.6");
                }

                // Player.Init reads EnvironmentManager.Instance unchecked; a raid cannot start without one
                if (All<EFT.EnvironmentEffect.EnvironmentManager>(roots).Count == 0)
                {
                    var env = new GameObject("EnvironmentManager");
                    SceneManager.MoveGameObjectToScene(env, scene);
                    env.AddComponent<EFT.EnvironmentEffect.EnvironmentManager>();
                    report.Warnings.Add($"{scene.name}: had no EnvironmentManager, one was added");
                    roots = scene.GetRootGameObjects();
                }

                // gives an older SDK_Default its textures (white placeholders render white and glossy in game)
                TarkovSceneTools.DefaultSurfaceMaterial();

                // BotsController.Init calls FindObjectOfType<AIStationaryController>().Init(...) unchecked
                if (All<AIStationaryController>(roots).Count == 0)
                {
                    var stationary = new GameObject("AIStationaryController");
                    SceneManager.MoveGameObjectToScene(stationary, scene);
                    stationary.AddComponent<AIStationaryController>().Weapons = new StationaryWeaponLink[0];
                    roots = scene.GetRootGameObjects();
                }

                // BSG shaders get ambient light only from a time of day system feeding AmbientLight;
                // without one the map is black
                bool simple = TarkovLightingRig.Find(scene) != null, full = TarkovWeatherBuilder.Find(scene) != null;
                if (simple && full)
                    report.Warnings.Add($"{scene.name}: has both the simple time of day rig and the full weather system; remove one " +
                                        "(Tarkov SDK > Map > Time of Day / Weather...)");
                if (!simple && !full)
                {
                    TarkovLightingRig.Create(scene);
                    report.Warnings.Add($"{scene.name}: had no time of day system, the simple one was added under 'Weather' " +
                                        "(Tarkov SDK > Map > Time of Day / Weather... for the full weather system)");
                    roots = scene.GetRootGameObjects();
                }
                if (full)
                    foreach (var components in TarkovWeatherBuilder.Find(scene).GetComponentsInChildren<TOD_Components>(true))
                        if (components.Light == null)
                        {
                            // TOD_Sky drives this light; without it the weather throws and the raid never starts
                            components.Light = TarkovWeatherBuilder.CreateSunLight(components.transform);
                            EditorUtility.SetDirty(components);
                            report.Warnings.Add($"{scene.name}: the sky's sun light ('Light' under '{components.name}') was missing and has been recreated; " +
                                                "it is part of the weather system, don't delete it");
                        }
                var ambientSystem = TarkovAmbientAudioSystem.Find(scene);
                if (ambientSystem != null)
                {
                    var soundData = TarkovAmbientAudioSystem.SoundData(ambientSystem);
                    if (soundData == null || TarkovAmbientAudioSystem.MixerEffects(ambientSystem) == null)
                        report.Warnings.Add($"{scene.name}: the ambient audio system is missing its data assets; remove it and add it again (Tarkov SDK > Map > Ambient Sounds...)");
                    else if (TarkovAmbientAudioSystem.GetClip(soundData, TarkovAmbientAudioSystem.Slot.Day) == null)
                        report.Warnings.Add($"{scene.name}: the ambient audio system has no Outdoor Day clip (Tarkov SDK > Map > Ambient Sounds...)");
                    if (scene.GetRootGameObjects().Any(g => g.name == TarkovAmbienceWindow.RootName))
                        report.Warnings.Add($"{scene.name}: has both {TarkovAmbienceWindow.RootName} beds and the ambient audio system; the beds play on top of it");
                }
                if (full && TarkovRainCover.Find(scene) == null)
                    report.Warnings.Add($"{scene.name}: no rain cover, so rain falls on the screen indoors too " +
                                        "(Tarkov SDK > Tools > Generate Rain Cover)");
                if (full && TarkovWeatherBuilder.Find(scene).activeSelf)
                {
                    // the client activates it after swapping its shaders; active, it would wake on stand-ins
                    TarkovWeatherBuilder.Find(scene).SetActive(false);
                    report.Warnings.Add($"{scene.name}: {TarkovWeatherBuilder.RootName} must be saved inactive (the game activates it); it was deactivated");
                }
            }

            AssignInteractiveIds(scene, roots, report);
            SyncPatrolWays(roots);
            SyncSpawnPoints(scene, roots, report);
            SyncBotZones(roots);
            SyncExfils(scene, roots, report);
            SyncTransits(scene, roots, report);
            SyncLootSpawns(scene, roots, report);
            SyncIndoorTriggers(scene, roots, report);
            FillLocationScene(locationScene, roots);
            Validate(scene, roots, report);

            EditorSceneManager.MarkSceneDirty(scene);
        }

        private static List<T> All<T>(GameObject[] roots) where T : Component
        {
            var list = new List<T>();
            foreach (var root in roots)
                list.AddRange(root.GetComponentsInChildren<T>(true));
            return list;
        }

        private static void AssignInteractiveIds(Scene scene, GameObject[] roots, Report report)
        {
            var seen = new HashSet<string>();
            foreach (var wio in All<WorldInteractiveObject>(roots))
            {
                if (string.IsNullOrEmpty(wio.Id) || !seen.Add(wio.Id))
                {
                    wio.Id = $"{scene.name}_{wio.gameObject.name}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
                    seen.Add(wio.Id);
                    EditorUtility.SetDirty(wio);
                }
                if (wio is LootableContainer container && string.IsNullOrEmpty(container.Template))
                    report.Warnings.Add($"{scene.name}/{wio.name}: LootableContainer has no Template (item tpl id)");
            }
            // lamps are switched and broken by id, like retail "lamp_<scene>_<name>"
            foreach (var lamp in All<Turnable>(roots))
                if (string.IsNullOrEmpty(lamp.Id) || !seen.Add(lamp.Id))
                {
                    lamp.Id = $"lamp_{scene.name}_{lamp.gameObject.name}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
                    seen.Add(lamp.Id);
                    EditorUtility.SetDirty(lamp);
                }
            // lens flares draw only through a FlareSceneSettings, in any scene of the map
            if (All<MultiFlare.FlareLight>(roots).Count > 0 && UnityEngine.Object.FindObjectsOfType<MultiFlare.FlareSceneSettings>(true).Length == 0)
                report.Warnings.Add($"{scene.name}: has lens flares but no flare settings, so they don't show (Tarkov SDK > Map > Lens Flare Settings)");
        }

        private static void SyncPatrolWays(GameObject[] roots)
        {
            foreach (var way in All<PatrolWay>(roots))
            {
                // a way's points are its child PatrolPoints, in hierarchy order
                way.Points = new List<PatrolPoint>();
                int id = 0;
                foreach (Transform child in way.transform)
                {
                    var point = child.GetComponent<PatrolPoint>();
                    if (point == null)
                        continue;
                    point.PatrolWay = way;
                    point.Id = id++;
                    if (point.subPoints == null)
                        point.subPoints = new List<PatrolPoint>();
                    way.Points.Add(point);
                    EditorUtility.SetDirty(point);
                }
                EditorUtility.SetDirty(way);
            }
        }

        private static void SyncSpawnPoints(Scene scene, GameObject[] roots, Report report)
        {
            var ids = new HashSet<string>();
            foreach (var marker in All<SpawnPointMarker>(roots))
            {
                if (marker._spawnPoint == null)
                    marker._spawnPoint = new SpawnPoint();
                var sp = marker._spawnPoint;
                if (string.IsNullOrEmpty(sp.Id) || !ids.Add(sp.Id))
                {
                    sp.Id = Guid.NewGuid().ToString();
                    ids.Add(sp.Id);
                }
                var t = marker.transform;
                sp.Name = marker.gameObject.name;
                sp.Position = t.position;
                sp.Rotation = Quaternion.Euler(0f, t.eulerAngles.y, 0f);
                var zone = marker.GetComponentInParent<BotZone>(true);
                if (zone != null)
                    sp.BotZone = zone;
                if (marker._collider == null)
                    marker._collider = marker.GetComponent<Collider>();

                if (sp.Sides == EPlayerSideMask.None)
                    report.Warnings.Add($"{scene.name}/{marker.name}: spawn point has no Sides");
                if (sp.Categories == ESpawnCategoryMask.None)
                    report.Warnings.Add($"{scene.name}/{marker.name}: spawn point has no Categories");
                if ((sp.Categories & ESpawnCategoryMask.Player) != 0 && (sp.Sides & EPlayerSideMask.Pmc) != 0
                    && string.IsNullOrEmpty(sp.Infiltration))
                    report.Warnings.Add($"{scene.name}/{marker.name}: PMC player spawn has no Infiltration (entry point name)");

                marker.gameObject.layer = LayerMask.NameToLayer("Triggers");
                EditorUtility.SetDirty(marker);
                report.SpawnPoints.Add(marker);
            }
        }

        private static void SyncBotZones(GameObject[] roots)
        {
            var markers = All<SpawnPointMarker>(roots);
            foreach (var zone in All<BotZone>(roots))
            {
                zone.SpawnPointMarkers = markers
                    .Where(m => m._spawnPoint != null && m._spawnPoint.BotZone == zone)
                    .ToList();

                var ways = new List<PatrolWay>();
                if (zone.PatrolWays != null)
                    ways.AddRange(zone.PatrolWays.Where(w => w != null));
                foreach (var way in zone.GetComponentsInChildren<PatrolWay>(true))
                    if (!ways.Contains(way))
                        ways.Add(way);
                zone.PatrolWays = ways.ToArray();
                if (zone.UnSpawnPoints == null)
                    zone.UnSpawnPoints = new UnspawnPoint[0];
                EditorUtility.SetDirty(zone);
            }
        }

        // loot spawns are exported for the server and never ship in the bundle
        private static void SyncLootSpawns(Scene scene, GameObject[] roots, Report report)
        {
            foreach (var spawn in All<TarkovSDK.TarkovLootSpawn>(roots))
            {
                if (!spawn.CompareTag("EditorOnly"))
                {
                    spawn.tag = "EditorOnly";
                    EditorUtility.SetDirty(spawn.gameObject);
                }
                var valid = spawn.Items.Where(i => System.Text.RegularExpressions.Regex.IsMatch(i.Template ?? "", "^[0-9a-fA-F]{24}$")).ToList();
                if (valid.Count < spawn.Items.Count)
                    report.Warnings.Add($"{scene.name}/{spawn.name}: {spawn.Items.Count - valid.Count} loot item(s) without a valid 24-character template id are skipped");
                if (valid.Count == 0)
                {
                    report.Warnings.Add($"{scene.name}/{spawn.name}: loot spawn has no items");
                    continue;
                }
                report.LootSpawns.Add(spawn);
            }
        }

        // TransitController keys points by parameters.id, so a duplicate id aborts the raid's transits
        private static void SyncTransits(Scene scene, GameObject[] roots, Report report)
        {
            var ids = new HashSet<int>(report.Transits.Select(t => t.parameters.id));
            foreach (var transit in All<TransitPoint>(roots))
            {
                var p = transit.parameters;
                if (p == null)
                {
                    report.Errors.Add($"{scene.name}/{transit.name}: TransitPoint has no parameters");
                    continue;
                }
                if (!ids.Add(p.id))
                    report.Errors.Add($"{scene.name}/{transit.name}: duplicate transit id {p.id}; every transit on the map needs its own");
                if (string.IsNullOrEmpty(p.location))
                    report.Warnings.Add($"{scene.name}/{transit.name}: transit has no destination; set parameters.location to a map id (e.g. bigmap, Woods, or a custom map's id)");
                var trigger = transit.GetComponent<Collider>();
                if (trigger == null || !trigger.isTrigger)
                    report.Errors.Add($"{scene.name}/{transit.name}: transit needs a trigger collider on the same GameObject");
                report.Transits.Add(transit);
            }
        }

        // Indoor detection is a lookup, not physics: EnvironmentManager takes the first TriggerGroup
        // under it, and each IndoorTrigger is the box of its transform (a unit cube, scaled). The
        // bounds are normally baked by BSG's editor; at runtime a group can wake before its triggers
        // and cache empty bounds, so they are computed here.
        private static void SyncIndoorTriggers(Scene scene, GameObject[] roots, Report report)
        {
            foreach (var trigger in All<EFT.EnvironmentEffect.IndoorTrigger>(roots))
            {
                trigger.Bounds = TransformBounds(trigger.transform);
                EditorUtility.SetDirty(trigger);
                var group = trigger.GetComponentInParent<EFT.EnvironmentEffect.TriggerGroup>(true);
                if (group == null || group.GetComponentInParent<EFT.EnvironmentEffect.EnvironmentManager>(true) == null)
                    report.Warnings.Add($"{scene.name}/{trigger.name}: IndoorTrigger is not under a TriggerGroup inside the EnvironmentManager, " +
                                        "so the game never checks it. Create it with GameObject > Tarkov SDK > Indoor Trigger");
            }
            // deepest groups first, so a parent encloses its children's final bounds
            foreach (var group in All<EFT.EnvironmentEffect.TriggerGroup>(roots).OrderByDescending(Depth))
            {
                group._childrenTriggers = group.GetComponentsInChildren<EFT.EnvironmentEffect.IndoorTrigger>(true)
                    .Where(t => t.transform.parent == group.transform).ToList();
                group._childrenGroups = group.GetComponentsInChildren<EFT.EnvironmentEffect.TriggerGroup>(true)
                    .Where(g => g.transform.parent == group.transform).ToList();
                var bounds = new Bounds();
                bool any = false;
                foreach (var t in group.GetComponentsInChildren<EFT.EnvironmentEffect.IndoorTrigger>(true))
                {
                    if (!any) { bounds = t.Bounds; any = true; }
                    else bounds.Encapsulate(t.Bounds);
                }
                group.Bounds = bounds;
                EditorUtility.SetDirty(group);
            }
        }

        internal static Bounds TransformBounds(Transform t)
        {
            var bounds = new Bounds(t.TransformPoint(new Vector3(-0.5f, -0.5f, -0.5f)), Vector3.zero);
            for (int i = 1; i < 8; i++)
                bounds.Encapsulate(t.TransformPoint(new Vector3((i & 1) - 0.5f, ((i >> 1) & 1) - 0.5f, ((i >> 2) & 1) - 0.5f)));
            return new Bounds(t.position, bounds.size); // the game centres the box on the transform
        }

        private static int Depth(Component c)
        {
            int depth = 0;
            for (var t = c.transform; t != null; t = t.parent)
                depth++;
            return depth;
        }

        private static void SyncExfils(Scene scene, GameObject[] roots, Report report)
        {
            var names = new HashSet<string>();
            foreach (var exfil in All<ExfiltrationPoint>(roots))
            {
                if (exfil.Settings == null)
                    exfil.Settings = new ExitTriggerSettings();
                var s = exfil.Settings;
                if (string.IsNullOrEmpty(s.Name))
                    s.Name = exfil.gameObject.name;
                if (!names.Add(s.Name))
                    report.Errors.Add($"{scene.name}/{exfil.name}: duplicate exfil name '{s.Name}'");
                if (string.IsNullOrEmpty(s.EntryPoints))
                    report.Warnings.Add($"{scene.name}/{exfil.name}: exfil has no EntryPoints, no PMC will be able to use it");

                // the game lowercases the player's entry point before comparing
                exfil.EligibleEntryPoints = (s.EntryPoints ?? "")
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(e => e.Trim().ToLowerInvariant())
                    .Where(e => e.Length > 0)
                    .ToArray();
                if (exfil.Requirements == null)
                    exfil.Requirements = new ExfiltrationRequirement[0];

                exfil.gameObject.layer = LayerMask.NameToLayer("Triggers");
                var collider = exfil.GetComponent<Collider>();
                if (collider == null)
                    report.Errors.Add($"{scene.name}/{exfil.name}: exfil needs a trigger collider on the same GameObject");
                else
                    collider.isTrigger = true;

                EditorUtility.SetDirty(exfil);
                report.Exfils.Add(exfil);
            }
        }

        private static void FillLocationScene(LocationScene locationScene, GameObject[] roots)
        {
            foreach (var field in typeof(LocationScene).GetFields())
            {
                if (!field.FieldType.IsArray)
                    continue;
                var element = field.FieldType.GetElementType();
                Array value;
                if (AlwaysEmptyArrays.Contains(field.Name) || element == null || !typeof(Component).IsAssignableFrom(element))
                {
                    value = Array.CreateInstance(element ?? typeof(object), 0);
                }
                else
                {
                    bool activeOnly = ActiveOnlyArrays.Contains(field.Name);
                    var found = new List<Component>();
                    foreach (var root in roots)
                        foreach (var c in root.GetComponentsInChildren(element, true))
                            if (!activeOnly || c.gameObject.activeInHierarchy)
                                found.Add(c);
                    value = Array.CreateInstance(element, found.Count);
                    for (int i = 0; i < found.Count; i++)
                        value.SetValue(found[i], i);
                }
                field.SetValue(locationScene, value);
            }
            EditorUtility.SetDirty(locationScene);
        }

        private static void Validate(Scene scene, GameObject[] roots, Report report)
        {
            var defaultLayer = new List<string>();
            var missingBallistic = new List<string>();
            int highPoly = LayerMask.NameToLayer("HighPolyCollider");
            int terrain = LayerMask.NameToLayer("Terrain");
            foreach (var collider in All<Collider>(roots))
            {
                // EditorOnly objects never ship, so their colliders don't matter
                if (collider.isTrigger || collider.CompareTag("EditorOnly"))
                    continue;
                int layer = collider.gameObject.layer;
                if (layer == 0)
                    defaultLayer.Add(PathOf(collider.transform));
                if ((layer == highPoly || layer == terrain) && collider.GetComponent<BallisticCollider>() == null)
                    missingBallistic.Add(PathOf(collider.transform));
            }
            if (defaultLayer.Count > 0)
                report.Warnings.Add($"{scene.name}: {defaultLayer.Count} solid collider(s) on the Default layer; bullets and bots ignore them " +
                                    $"({Names(defaultLayer)}). Use Terrain, or LowPolyCollider (movement) + HighPolyCollider (bullets, AI sight); " +
                                    "Tarkov SDK > Tools > Make Selected Static Geometry does this");
            if (missingBallistic.Count > 0)
                report.Warnings.Add($"{scene.name}: {missingBallistic.Count} HighPolyCollider/Terrain collider(s) without a BallisticCollider " +
                                    $"({Names(missingBallistic)}); the game reads surface material and penetration from it. " +
                                    "Tarkov SDK > Tools > Add BallisticCollider To Selected Colliders");
        }

        private static string PathOf(Transform t)
        {
            var path = t.name;
            for (var p = t.parent; p != null; p = p.parent)
                path = p.name + "/" + path;
            return path;
        }

        private static string Names(List<string> paths) =>
            paths.Count <= 5 ? string.Join(", ", paths) : string.Join(", ", paths.GetRange(0, 5)) + $" and {paths.Count - 5} more";
    }
}
