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
            public readonly List<LootableContainer> Containers = new List<LootableContainer>();
            // CustomMapLoader's own features (sdk_export.json "Features") and the container types the map uses
            public readonly List<TarkovSDK.TarkovForceTransit> ForceTransits = new List<TarkovSDK.TarkovForceTransit>();
            public readonly List<TarkovSDK.TarkovGearStrip> GearStrips = new List<TarkovSDK.TarkovGearStrip>();
            public readonly List<TarkovSDK.TarkovNotification> Notifications = new List<TarkovSDK.TarkovNotification>();
            public readonly List<TarkovSDK.TarkovCaption> Captions = new List<TarkovSDK.TarkovCaption>();
            public readonly List<TarkovSDK.TarkovHealthChange> HealthChanges = new List<TarkovSDK.TarkovHealthChange>();
            public readonly List<TarkovSDK.TarkovEffect> Effects = new List<TarkovSDK.TarkovEffect>();
            public readonly List<TarkovSDK.TarkovSpawnBots> SpawnBots = new List<TarkovSDK.TarkovSpawnBots>();
            public readonly List<TarkovSDK.TarkovSpawnLoot> SpawnLoot = new List<TarkovSDK.TarkovSpawnLoot>();
            // with the scene and hierarchy path the client finds the object (or its template) by
            public readonly List<(TarkovSDK.TarkovSpawnPrefab Spawn, string Scene, string Path)> SpawnPrefabs = new List<(TarkovSDK.TarkovSpawnPrefab, string, string)>();
            // the map's mortar shelling (sdk_export.json "Mortar"), if it has one
            public TarkovSDK.TarkovMortarSettings Mortar;
            public readonly List<TarkovSDK.TarkovMortarZone> MortarZones = new List<TarkovSDK.TarkovMortarZone>();
            public readonly List<TarkovSDK.TarkovMortarBattery> MortarBatteries = new List<TarkovSDK.TarkovMortarBattery>();
            public readonly List<TarkovSDK.TarkovContainerDefinition> ContainerTypes = new List<TarkovSDK.TarkovContainerDefinition>();
            // container groups (Min..Max of their containers spawn per raid; sdk_export.json "ContainerGroups")
            public readonly List<LootableContainersGroup> ContainerGroups = new List<LootableContainersGroup>();
            // mounted guns: their static weapon id and weapon ("Utes", "AGS"); the SDK's markers are placed by the client,
            // StationaryWeapons already in the scene (ripped) only need their item
            public readonly List<(string Id, string Weapon, Transform Marker, bool InScene)> MountedGuns = new List<(string, string, Transform, bool)>();
            // scripted world events with their steps (scene and hierarchy path of what a step switches or plays)
            public readonly List<(TarkovSDK.TarkovWorldEvent Event, string Name, List<(TarkovSDK.TarkovWorldEvent.Step Step, string Scene, string Path)> Steps)> WorldEvents =
                new List<(TarkovSDK.TarkovWorldEvent, string, List<(TarkovSDK.TarkovWorldEvent.Step, string, string)>)>();
            // the baked audio files the raid loads (spatial audio routes, Meta acoustics), shipped next to the bundles
            public readonly List<string> AudioFiles = new List<string>();
            // the map's BTR route, if it has one
            public EFT.Vehicle.MapPathConfig Btr;
            public long UnixDateTime;
            // ids the game looks up map-wide, so unique across the map's scenes (a copied scene shares all of them):
            // interactive objects, lamps and windows; sync objects' random part; quest zones by type
            public readonly HashSet<string> Ids = new HashSet<string>();
            public readonly HashSet<uint> SyncRandoms = new HashSet<uint>();
            public readonly HashSet<string> QuestZoneIds = new HashSet<string>();
        }

        // LocationScene arrays the game fills with active objects only
        private static readonly HashSet<string> ActiveOnlyArrays = new HashSet<string> { "BorderZones", "Windows" };

        // LocationScene arrays that BSG fills from interfaces we do not stub; empty is valid for them
        private static readonly HashSet<string> AlwaysEmptyArrays = new HashSet<string>
        {
            "AudioSources", "BroadcastPlayers", "EventSoundContentChangers",
        };

        public static void Prepare(Scene scene, bool isActiveScene, Report report)
        {
            // the last build's spawn templates go before anything collects the scene's components
            TarkovTriggeredSpawns.ClearTemplates(scene);
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
                TarkovAirdrops.EnsurePhysicsSetup(scene, report);

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
                    // the lighting rig supplies ambient; Unity's is added to every surface day and night (Customs: Flat, black, 0)
                    var ambient = level.SkyColor.maxColorComponent * level.AmbientIntensity;
                    if (level.AmbientMode != UnityEngine.Rendering.AmbientMode.Flat)
                        report.Warnings.Add($"{scene.name}: LevelSettings ambient mode is {level.AmbientMode}; retail maps use Flat (Skybox bakes a fixed sky, Trilight adds its colours day and night, both glow at night). Tarkov SDK > Quick Fixes > Fix Night Lighting");
                    else if (fullWeather ? ambient > 0f : ambient > TarkovQuickFixes.SimpleRigAmbientLimit)
                        report.Warnings.Add($"{scene.name}: LevelSettings ambient (Sky Color x Ambient Intensity = {ambient:0.###}) lights the map day and night and makes it glow at night; " +
                                            (fullWeather ? "full weather maps keep it black (Customs)" : "Factory uses Sky Color 0.03 at intensity 0.6") + ". Tarkov SDK > Quick Fixes > Fix Night Lighting");
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

                // p0 materials without a cubemap reflect Unity's default grey day and night
                var noCube = roots.SelectMany(r => r.GetComponentsInChildren<Renderer>(true)).SelectMany(r => r.sharedMaterials)
                    .Where(TarkovReflectionCube.IsMissing).Distinct().ToList();
                if (noCube.Count > 0)
                    report.Warnings.Add($"{scene.name}: {noCube.Count} p0 material(s) have no reflection cubemap and shine flat grey day and night in game " +
                                        $"(e.g. {string.Join(", ", noCube.Take(3).Select(m => m.name))}). Tarkov SDK > Quick Fixes > Fix Shiny Materials");

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
                                        "(Tarkov SDK > Generation > Time > Time of Day / Weather...)");
                if (!simple && !full)
                {
                    TarkovLightingRig.Create(scene);
                    report.Warnings.Add($"{scene.name}: had no time of day system, the simple one was added under 'Weather' " +
                                        "(Tarkov SDK > Generation > Time > Time of Day / Weather... for the full weather system)");
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
                if (full)
                    foreach (var clouds in TarkovWeatherBuilder.Find(scene).GetComponentsInChildren<EFT.Rendering.Clouds.CloudController>(true))
                        if (clouds._settings == null || clouds._settings.LayerA == null || clouds._settings.LayerA.CloudTexture == null)
                            // a weather system built before 1.9.2 lost its assets when another one was built
                            report.Warnings.Add($"{scene.name}: the weather's clouds ('{clouds.name}') have no cloud layer or texture, so the raid has no clouds; " +
                                                "rebuild the full weather system (Tarkov SDK > Generation > Time > Time of Day / Weather...)");
                var ambientSystem = TarkovAmbientAudioSystem.Find(scene);
                if (ambientSystem != null)
                {
                    var soundData = TarkovAmbientAudioSystem.SoundData(ambientSystem);
                    if (soundData == null || TarkovAmbientAudioSystem.MixerEffects(ambientSystem) == null)
                        report.Warnings.Add($"{scene.name}: the ambient audio system is missing its data assets; remove it and add it again (Tarkov SDK > Generation > Ambience > Ambient Sounds...)");
                    else if (TarkovAmbientAudioSystem.GetClip(soundData, TarkovAmbientAudioSystem.Slot.Day) == null)
                        report.Warnings.Add($"{scene.name}: the ambient audio system has no Outdoor Day clip (Tarkov SDK > Generation > Ambience > Ambient Sounds...)");
                    if (scene.GetRootGameObjects().Any(g => g.name == TarkovAmbienceWindow.RootName))
                        report.Warnings.Add($"{scene.name}: has both {TarkovAmbienceWindow.RootName} beds and the ambient audio system; the beds play on top of it");
                }
                if (full && TarkovRainCover.FindInMap(scene) == null)
                    report.Warnings.Add($"{scene.name}: no rain cover, so rain falls on the screen indoors too " +
                                        "(Tarkov SDK > Generation > Geometry > Rain Cover)");
                if (full && TarkovWeatherBuilder.Find(scene).activeSelf)
                {
                    // the client activates it after swapping its shaders; active, it would wake on stand-ins
                    TarkovWeatherBuilder.Find(scene).SetActive(false);
                    report.Warnings.Add($"{scene.name}: {TarkovWeatherBuilder.RootName} must be saved inactive (the game activates it); it was deactivated");
                }
            }

            // before the ids (they give sync objects their random part) and FillLocationScene (it lists what these make)
            TarkovExplosives.Prepare(scene, roots, report);
            TarkovWires.Prepare(scene, roots, report);
            TarkovTrees.PrepareWind(scene, roots);
            roots = scene.GetRootGameObjects();
            AssignInteractiveIds(scene, roots, report);
            TarkovContainerGroups.Prepare(scene, roots, report);
            TarkovMountedGuns.Prepare(scene, roots, report);
            TarkovBreachableDoors.Prepare(scene, roots, report);
            SyncPatrolWays(roots);
            SyncSpawnPoints(scene, roots, report);
            SyncBotZones(roots);
            SyncExfils(scene, roots, report);
            SyncTransits(scene, roots, report);
            SyncLootSpawns(scene, roots, report);
            SyncIndoorTriggers(scene, roots, report);
            FillLocationScene(locationScene, roots);
            // after FillLocationScene: the spawn templates aren't the raid's own objects (copies are made of them)
            TarkovTriggeredSpawns.Prepare(scene, roots, report);
            roots = scene.GetRootGameObjects();
            // before Validate: it gives terrains their TerrainBallistic
            TarkovTerrains.Prepare(scene, roots, report);
            TarkovProBuilder.Check(scene, roots, report);
            Validate(scene, roots, report);
            TarkovHazards.Prepare(roots, scene.name, report);
            TarkovExtracts.Prepare(roots, scene.name, report);
            TarkovBtr.Prepare(roots, scene.name, report);
            // after the ids: a gear strip names its container by Id
            TarkovMapEvents.Check(scene, roots, report);
            TarkovMortar.Prepare(scene, roots, report);
            TarkovVolumetricLights.Prepare(scene, roots, report);
            TarkovBirds.Prepare(scene, roots, report);
            // after the spawns and mortar zones: it reports steps nothing listens to
            TarkovWorldEvents.Prepare(scene, roots, report);
            TarkovAnimatedObjects.Prepare(scene, roots, report);
            foreach (var type in TarkovLootContainers.UsedBy(All<LootableContainer>(roots)))
                if (!report.ContainerTypes.Contains(type))
                    report.ContainerTypes.Add(type);
            // after the ids: the audio portals take their doors' and windows' Ids
            var bake = TarkovSpatialAudio.Prepare(scene, report);
            if (bake != null)
                report.AudioFiles.Add(bake);
            // Meta's files are the whole map's (terrains carry their geometry in their own scene): taken once
            if (TarkovSpatialAudio.AudioScene(scene) == scene)
            {
                report.AudioFiles.AddRange(TarkovAcoustics.Files(scene).Where(f => !report.AudioFiles.Contains(f)));
                if (TarkovAcoustics.FindMap(scene) != null && TarkovAcoustics.Files(scene).Count == 0)
                    report.Warnings.Add($"{scene.name}: room acoustics are set up but not baked (Tarkov SDK > Generation > Audio > Spatial Audio..., Bake Acoustics); the raid has no Meta reverb");
            }
            var unspatialized = TarkovAudioSources.Broken(scene);
            if (unspatialized.Count > 0)
                report.Warnings.Add($"{scene.name}: {unspatialized.Count} audio source(s) not set up for Meta XR Audio (e.g. '{TarkovAudioSources.Path(unspatialized[0].transform)}': " +
                                    $"{TarkovAudioSources.Problem(unspatialized[0])}); Tarkov SDK > Generation > Audio > Spatial Audio..., Fix Audio Sources");
            var blendSystem = TarkovAmbientAudioSystem.Find(scene);
            if (blendSystem != null && blendSystem.GetComponentsInChildren<Audio.AmbientSubsystem.AmbientSoundBlender>(true).Any(b => b._fadeOutDuration < b._blendDurationMult))
                report.Warnings.Add($"{scene.name}: the indoor/outdoor blend's Fade Up Outdoors is too short for the game to fade, so the outdoor ambience jumps to full " +
                                    "volume on leaving a room (Tarkov SDK > Generation > Audio > Spatial Audio..., Fade Smoothly)");

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
            var seen = report.Ids;
            foreach (var wio in All<WorldInteractiveObject>(roots))
            {
                if (string.IsNullOrEmpty(wio.Id) || !seen.Add(wio.Id))
                {
                    wio.Id = $"{scene.name}_{wio.gameObject.name}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
                    seen.Add(wio.Id);
                    EditorUtility.SetDirty(wio);
                }
                if (wio is LootableContainer container)
                {
                    // the server fills listed containers with loot; one it doesn't list opens with no inventory
                    if (string.IsNullOrEmpty(container.Template))
                        report.Warnings.Add($"{scene.name}/{wio.name}: LootableContainer has no Template (item tpl id), so it can't be searched");
                    else if (!System.Text.RegularExpressions.Regex.IsMatch(container.Template.Trim(), "^[0-9a-fA-F]{24}$"))
                        report.Warnings.Add($"{scene.name}/{wio.name}: LootableContainer Template '{container.Template}' is not a 24-character item id, so it can't be searched");
                    else
                        report.Containers.Add(container);
                    // opening moves it to ClosedPosition/OpenPosition; zero sends it to its parent's origin
                    if (TarkovPrefabTools.KeepContainerInPlace(container))
                        EditorUtility.SetDirty(container);
                    // a HighPoly box flush with the interaction box makes the interact prompt flicker
                    if (container.GetComponent<Collider>() is BoxCollider box && TarkovPrefabTools.EncloseHighPoly(box))
                        EditorUtility.SetDirty(box);
                }
                // switches made before the SDK set this curve have an empty one, and the game throws on it
                if (wio is Switch sw && (sw.CustomProgressCurve == null || sw.CustomProgressCurve.length == 0))
                {
                    sw.CustomProgressCurve = TarkovPrefabTools.SwitchCurve();
                    EditorUtility.SetDirty(sw);
                }
            }
            // lamps are switched and broken by id, like retail "lamp_<scene>_<name>"
            foreach (var lamp in All<Turnable>(roots))
                if (string.IsNullOrEmpty(lamp.Id) || !seen.Add(lamp.Id))
                {
                    lamp.Id = $"lamp_{scene.name}_{lamp.gameObject.name}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
                    seen.Add(lamp.Id);
                    EditorUtility.SetDirty(lamp);
                }
            // breakable windows: their precomputed glass geometry and references, and ids like retail "window_<scene>_<n>"
            foreach (var window in All<WindowBreaker>(roots))
            {
                if (!TarkovWindows.Setup(window, out var problem))
                    report.Warnings.Add($"{scene.name}/{window.name}: breakable window can't break: {problem}");
                if (string.IsNullOrEmpty(window.Id) || !seen.Add(window.Id))
                {
                    window.Id = $"window_{scene.name}_{window.gameObject.name}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
                    seen.Add(window.Id);
                    EditorUtility.SetDirty(window);
                }
            }
            // game triggers (automatic doors, elevators, PA announcements, sound zones)
            foreach (var press in All<EFT.GameTriggers.HandlerInteractable>(roots))
            {
                // the zone finds the object it presses by id, which may have just been assigned above
                if (press._interactable == null)
                    report.Warnings.Add($"{scene.name}/{press.name}: HandlerInteractable has no Interactable, so its trigger does nothing");
                else if (press._interactableId != press._interactable.Id)
                {
                    press._interactableId = press._interactable.Id;
                    EditorUtility.SetDirty(press);
                }
            }
            // an ISyncAble's scene id is its path hash with this random part
            foreach (var sync in All<ISyncAble>(roots))
                if (sync._random == 0 || !report.SyncRandoms.Add(sync._random))
                {
                    do sync._random = BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(), 0) | 1u;
                    while (!report.SyncRandoms.Add(sync._random));
                    EditorUtility.SetDirty(sync);
                }
            foreach (var pa in All<QueuePlayer>(roots))
                foreach (var speaker in pa.GetComponentsInChildren<AudioSource>(true))
                {
                    int tag = speaker.name.LastIndexOf('@');
                    if (tag >= 0 && TarkovFacilitySystems.AmbientOnlyGroup(speaker.name.Substring(tag + 1).Trim()))
                        report.Warnings.Add($"{scene.name}/{speaker.name}: PA speaker is on an indoor or outdoor ambience mixer group, so it goes quiet on the other side; " +
                                            "use @CommonSounds (Fix Broken Interactables does this)");
                }
            var lampHandlers = All<EFT.GameTriggers.HandlerLamp>(roots);
            foreach (var group in All<ControlledLampGroup>(roots))
            {
                if (TarkovLights.LampGroupIsEmpty(group))
                    report.Warnings.Add($"{scene.name}/{group.name}: lamp group switches nothing (press Fill in its inspector)");
                // only HandlerLamp switches a group (references here; the game's id list holds scene ids)
                if (!lampHandlers.Any(h => h._controlledLampGroups != null && h._controlledLampGroups.Contains(group)))
                    report.Warnings.Add($"{scene.name}/{group.name}: nothing switches this lamp group, so it stays {(group._initialLightState ? "on" : "off")} " +
                                        "(Add Light Switch in its inspector, or Tools > Triggers > Triggers & Sounds)");
            }
            // keycard doors: the game reads Proxies[0] unchecked when unlocking, and a proxy's Start needs its lights
            foreach (var door in All<KeycardDoor>(roots))
            {
                if (door.Proxies == null || door.Proxies.Length == 0 || door.Proxies.Any(p => p == null))
                    report.Errors.Add($"{scene.name}/{door.name}: keycard door without a card reader in Proxies; unlocking it throws");
                if (string.IsNullOrEmpty(door.KeyId))
                    report.Warnings.Add($"{scene.name}/{door.name}: keycard door has no KeyId (the keycard's item id), so no card opens it");
            }
            int interactive = LayerMask.NameToLayer("Interactive");
            foreach (var proxy in All<InteractiveProxy>(roots))
            {
                if (TarkovKeycardDoors.RefreshProxy(proxy))
                    EditorUtility.SetDirty(proxy);
                if (proxy.Link == null)
                    report.Errors.Add($"{scene.name}/{proxy.name}: card reader has no Link (its door); the game throws on it at raid start");
                if (proxy.StatusLight == null || proxy.FlickerLight == null)
                    report.Errors.Add($"{scene.name}/{proxy.name}: card reader needs both its Status Light and Flicker Light; the game throws on it at raid start");
                if (proxy.gameObject.layer != interactive || proxy.GetComponent<Collider>() == null)
                    report.Warnings.Add($"{scene.name}/{proxy.name}: card reader needs a collider on the Interactive layer on its own object, or players can't use it");
            }
            // quest zones are found by id
            var questIds = report.QuestZoneIds;
            foreach (var zone in All<TriggerWithId>(roots))
            {
                if (string.IsNullOrWhiteSpace(zone._id))
                    report.Warnings.Add($"{scene.name}/{zone.name}: quest zone has no Id, so no quest can use it");
                else if (!questIds.Add(zone.GetType().Name + "/" + zone._id))
                    report.Warnings.Add($"{scene.name}/{zone.name}: another {zone.GetType().Name} has the Id '{zone._id}'");
                if (!(zone.GetComponent<Collider>()?.isTrigger ?? false))
                    report.Warnings.Add($"{scene.name}/{zone.name}: quest zone has no trigger collider, so players are never in it");
            }
            foreach (var wire in All<BarbedWire>(roots))
                if (!(wire.GetComponent<Collider>()?.isTrigger ?? false))
                    report.Warnings.Add($"{scene.name}/{wire.name}: barbed wire has no trigger collider, so it never catches anyone");
            foreach (var subscriber in All<InteractiveSubscriber>(roots))
                if (subscriber.Subscribee == null)
                    report.Errors.Add($"{scene.name}/{subscriber.name}: InteractiveSubscriber has no Subscribee; the game throws on it at raid start");
            int triggersLayer = LayerMask.NameToLayer("Triggers");
            foreach (var zone in All<EFT.GameTriggers.TriggerZone>(roots))
            {
                var collider = zone.GetComponent<Collider>();
                if (collider == null || !collider.isTrigger)
                    report.Warnings.Add($"{scene.name}/{zone.name}: TriggerZone needs a trigger collider on its object, or players never set it off");
                else if (zone.gameObject.layer != triggersLayer)
                    report.Warnings.Add($"{scene.name}/{zone.name}: TriggerZone is not on the Triggers layer, so players may not set it off");
                if (string.IsNullOrEmpty(zone._triggerId))
                    report.Warnings.Add($"{scene.name}/{zone.name}: TriggerZone has no trigger id");
            }
            // trigger ids nothing sends (it never fires) or nothing hears
            foreach (var (where, problem) in TarkovTriggerLinks.Problems())
                if (where != null && where.gameObject.scene == scene)
                    report.Warnings.Add($"{scene.name}/{where.name}: {problem}");
            foreach (var sound in All<EFT.GameTriggers.HandlerPlaySoundAdvanced>(roots))
                if (sound._soundsPool != null && sound._soundsPool.Any(c => c.AudioClip != null && Mathf.Approximately(c.Pitch, 0f)))
                    report.Warnings.Add($"{scene.name}/{sound.name}: a sound has Pitch 0, which the game plays silent; use 1");

            // lens flares draw only through a FlareSceneSettings, in any scene of the map
            if (All<MultiFlare.FlareLight>(roots).Count > 0 && UnityEngine.Object.FindObjectsOfType<MultiFlare.FlareSceneSettings>(true).Length == 0)
                report.Warnings.Add($"{scene.name}: has lens flares but no flare settings, so they don't show (Tarkov SDK > Generation > Misc > Lens Flare Settings)");
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
            // across the map: the server keys spawn points by id
            var ids = new HashSet<string>(report.SpawnPoints.Select(m => m._spawnPoint?.Id));
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
                                        "so the game never checks it. Create it with GameObject > Tarkov SDK > Environment > Indoor Trigger");
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
            // across the map: the server matches exits to base.json by name
            var names = new HashSet<string>(report.Exfils.Select(e => e.Settings?.Name));
            foreach (var exfil in All<ExfiltrationPoint>(roots))
            {
                if (exfil.Settings == null)
                    exfil.Settings = new ExitTriggerSettings();
                var s = exfil.Settings;
                if (string.IsNullOrEmpty(s.Name))
                    s.Name = exfil.gameObject.name;
                if (!names.Add(s.Name))
                    report.Errors.Add($"{scene.name}/{exfil.name}: duplicate exfil name '{s.Name}'" + (report.Exfils.Any(e => e.Settings?.Name == s.Name && e.gameObject.scene != scene) ? " (in another of the map's scenes)" : ""));
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
                if (field.Name == "TriggerEntities")
                {
                    value = TriggerEntities(roots);
                }
                else if (field.Name == "treeWindSettingsPresets")
                {
                    value = TreeWindPresets(roots);
                }
                else if (AlwaysEmptyArrays.Contains(field.Name) || element == null || !typeof(Component).IsAssignableFrom(element))
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

        // LocationScene.FillTreeWinds: each TreeWind's four wind sets copied into its settings, and the distinct ones;
        // LevelSettings makes one property block per preset and gives each TreeWind the one matching its settings
        // (without a match it logs "_mpb == null" and makes its own)
        private static System.Array TreeWindPresets(GameObject[] roots)
        {
            var presets = new List<EFT.SpeedTree.TreeWind.Settings>();
            foreach (var wind in roots.SelectMany(r => r.GetComponentsInChildren<EFT.SpeedTree.TreeWind>(true)))
            {
                wind.settings = new EFT.SpeedTree.TreeWind.Settings
                {
                    BaseMinWindData = wind.BaseMinWindData,
                    BaseMaxWindData = wind.BaseMaxWindData,
                    MinWindData = wind.MinWindData,
                    MaxWindData = wind.MaxWindData,
                };
                EditorUtility.SetDirty(wind);
                if (!presets.Contains(wind.settings))
                    presets.Add(wind.settings);
            }
            return presets.ToArray();
        }

        // LocationScene.FillArraysFrom: the objects with an ITriggerEntity that has trigger ids (the stubs
        // don't carry the interface, so these are the game's implementers)
        private static GameObject[] TriggerEntities(GameObject[] roots)
        {
            var found = new List<GameObject>();
            foreach (var root in roots)
                foreach (var c in root.GetComponentsInChildren<Component>(true))
                {
                    bool hasIds = c switch
                    {
                        EFT.GameTriggers.TriggerZone z => !string.IsNullOrEmpty(z._triggerId),
                        EFT.GameTriggers.BaseTriggerHandler h => !string.IsNullOrEmpty(h._triggerId),
                        EFT.GameTriggers.HandlerAnimator a => a._animatorActions != null && a._animatorActions.Length > 0,
                        EFT.GameTriggers.HandlerPlaySoundAdvanced p => !string.IsNullOrEmpty(p._playTriggerId) || !string.IsNullOrEmpty(p._stopTriggerId),
                        EFT.GameTriggers.HandlerFilterOnce f => f._inputTriggerIds != null && f._inputTriggerIds.Length > 0,
                        EFT.GameTriggers.HandlerDamage d => !string.IsNullOrEmpty(d._triggerIdStartDamage) || !string.IsNullOrEmpty(d._triggerIdFinishDamage),
                        EFT.GameTriggers.HandlerEffect e => !string.IsNullOrEmpty(e._triggerId),
                        EFT.GameTriggers.HandlerTriggerState s => !string.IsNullOrEmpty(s._triggerId) || s._controlledTriggerId != null && s._controlledTriggerId.Length > 0,
                        WorldInteractiveObject w => w.TriggersMap != null && w.TriggersMap.Length > 0,
                        _ => false,
                    };
                    if (hasIds && !found.Contains(c.gameObject))
                        found.Add(c.gameObject);
                }
            return found.ToArray();
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
                // a BallisticCollider, or a terrain's TerrainBallistic (both BaseBallistic, which hits ask)
                if ((layer == highPoly || layer == terrain) && collider.GetComponent<BaseBallistic>() == null)
                    missingBallistic.Add(PathOf(collider.transform));
            }
            if (defaultLayer.Count > 0)
                report.Warnings.Add($"{scene.name}: {defaultLayer.Count} solid collider(s) on the Default layer; bullets and bots ignore them " +
                                    $"({Names(defaultLayer)}). Use Terrain, or LowPolyCollider (movement) + HighPolyCollider (bullets, AI sight); " +
                                    "Tarkov SDK > Tools > Geometry > Make Selected Static Geometry does this");
            if (missingBallistic.Count > 0)
                report.Warnings.Add($"{scene.name}: {missingBallistic.Count} HighPolyCollider/Terrain collider(s) without a BallisticCollider " +
                                    $"({Names(missingBallistic)}); the game reads surface material and penetration from it. " +
                                    "Tarkov SDK > Tools > Geometry > Add BallisticCollider To Selected Colliders");
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
