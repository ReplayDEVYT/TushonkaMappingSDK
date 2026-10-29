using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // One-click starting point: a scene with the map skeleton and a baked navmesh, plus a Map
    // Definition that builds it.
    public class TarkovNewMapWindow : EditorWindow
    {
        private string _mapId = "sdk_example";

        [MenuItem("Tarkov SDK/Map Settings/New Map...", priority = 137)]
        private static void Open()
        {
            var window = GetWindow<TarkovNewMapWindow>(true, "New Tarkov Map");
            window.minSize = window.maxSize = new Vector2(400f, 160f);
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox("Creates Assets/Maps/<id>/ with a scene, an example map and a Map Definition. The example has cover, a shed " +
                                    "with a breachable door, switch and loot, a bunker with a light shaft, spawns, a bot zone, every kind of extract, a minefield and a " +
                                    "claymore, a fire barrel, an explosive barrel a world event sets off, a container group, a mounted gun, wires on poles, " +
                                    "airdrop points, a BTR route, a triggered mortar strike, spatial audio rooms, the ambient audio system and a baked navmesh.", MessageType.None);
            _mapId = EditorGUILayout.TextField("Map Id", _mapId).Trim().ToLowerInvariant();
            bool valid = Regex.IsMatch(_mapId, "^[a-z0-9_]+$");
            if (!valid)
                EditorGUILayout.HelpBox("Lowercase letters, digits and underscores only.", MessageType.Warning);
            using (new EditorGUI.DisabledScope(!valid))
                if (GUILayout.Button("Create"))
                {
                    var id = _mapId;
                    Close();
                    EditorApplication.delayCall += () => Create(id);
                }
        }

        // the SDK's own trees west of the shed (their prefabs are made once, in Assets/TarkovSDK/Trees)
        private static void CreateExampleTrees(UnityEngine.SceneManagement.Scene scene)
        {
            var root = new GameObject("Trees");
            foreach (var (name, position) in new[] { ("SDK_Pine", new Vector3(-68f, 0f, 12f)), ("SDK_Pine", new Vector3(-75f, 0f, 22f)), ("SDK_Broadleaf", new Vector3(-62f, 0f, 27f)) })
            {
                var prefab = TarkovTrees.SdkTree(name);
                if (prefab == null)
                    continue;
                var tree = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                tree.transform.SetParent(root.transform, false);
                tree.transform.position = position;
            }
            Undo.RegisterCreatedObjectUndo(root, "Example trees");
        }

        // the map features beside the shed and in the west, before the navmesh bake so it goes round them: a fire barrel, an
        // explosive gas cylinder that a world event sets off a few minutes in (after a warning), three crates of which one
        // or two spawn, a mounted Utes facing north, a line of wires on poles, birds (crows on paths, a gull flock), a light
        // shaft in the bunker, and the shed's door made breachable
        private static void CreateExampleFeatures(UnityEngine.SceneManagement.Scene scene)
        {
            var root = new GameObject("Features");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);

            TarkovFire.CreateFireBarrel(root.transform).transform.position = new Vector3(-18f, 0f, 12f);

            var cylinder = TarkovExplosives.CreateExplosiveCylinder(root.transform);
            cylinder.transform.position = new Vector3(-6f, 0f, -12f);
            var blast = cylinder.GetComponent<EFT.GameTriggers.TriggerBallistic>()._triggerId;

            var group = TarkovContainerGroups.CreateGroup(root.transform);
            group.name = "ContainerGroup_Crates";
            group.transform.position = new Vector3(-43f, 0f, 20f);
            group.Min = 1;
            group.Max = 2;
            for (int i = 0; i < 3; i++)
            {
                var crate = TarkovPrefabTools.CreateContainer(group.transform);
                crate.name = "Crate_" + (i + 1);
                crate.transform.localPosition = new Vector3((i - 1) * 2f, 0f, 0f);
                crate.SpawnChance = 50;
                crate.SpawnType = EFT.Interactive.ELootableContainerSpawnType.MayNotSpawn;
            }

            var gun = TarkovMountedGuns.Create(root.transform);
            gun.transform.parent.position = new Vector3(-35f, 0f, 35f);

            Physics.SyncTransforms();
            var wires = TarkovWires.Create(root.transform, new[] { new Vector3(-40f, 7f, 45f), new Vector3(-62f, 7f, 46f), new Vector3(-85f, 7f, 44f) }, null);
            wires.transform.parent.name = "Wires_Example";

            // crows circling the middle of the map, and a flock of gulls over the east
            var birds = TarkovBirds.CreatePaths(root.transform);
            birds.transform.position = new Vector3(0f, 0f, 20f);
            var flock = TarkovBirds.CreateFlock(root.transform, TarkovBirds.Kind.Gull);
            flock.transform.parent.position = new Vector3(60f, 0f, -40f);

            // straight down into the bunker's room
            var shaft = TarkovVolumetricLights.CreateShaft(root.transform);
            shaft.transform.SetPositionAndRotation(new Vector3(-30f, 2.6f, -40f), Quaternion.Euler(90f, 0f, 0f));
            shaft.range = 4f;
            shaft.spotAngle = 60f;

            foreach (var door in Object.FindObjectsOfType<EFT.Interactive.Door>())
                if (door.gameObject.scene == scene)
                    TarkovBreachableDoors.MakeBreachable(door, 6, true);

            var evt = TarkovWorldEvents.Create(root.transform);
            evt.EventName = "Gas Leak";
            evt.transform.position = cylinder.transform.position + Vector3.up;
            evt.EarliestMinute = 2f;
            evt.LatestMinute = 4f;
            evt.Steps[0].Notification.Message = "You smell gas near the old cylinder...";
            evt.Steps.Add(new TarkovWorldEvent.Step { Kind = TarkovWorldEvent.StepKind.FireTrigger, TriggerId = blast, Wait = 10f });
            Undo.RegisterCreatedObjectUndo(root, "Example features");
        }

        // a small mortar zone in the north-east, struck 15 s after a player walks into the zone beside it, which also
        // spawns two scavs; planned shelling is off so test raids aren't shelled at random
        private static void CreateExampleStrike(UnityEngine.SceneManagement.Scene scene)
        {
            const string id = "Example_MortarStrike";
            var mortar = TarkovMortar.EnsureRoot(scene);
            mortar.PlannedShelling = false;
            var zone = TarkovMortar.CreateZone(mortar.transform);
            zone.name = "MortarZone_Example";
            zone.transform.position = new Vector3(70f, 0f, 65f);
            zone.Planned = false;
            zone.Points = new Vector2Int(4, 4);
            zone.GridStep = new Vector2(8f, 8f);
            zone.PointRadius = 6f;
            zone.PointsPerShelling = new Vector2Int(6, 10);
            zone.Rounds = 2;
            zone.ShotsPerRound = 6;
            zone.TriggerId = id;
            zone.TriggerDelay = 15f;
            TarkovMortar.CreateBattery(mortar.transform, new Vector3(400f, 0f, 65f));

            var trigger = TarkovFacilitySystems.Zone(null, "MortarAndScavs_Zone", new Vector3(0f, 1.5f, 0f), new Vector3(6f, 3f, 6f));
            trigger.transform.position = new Vector3(45f, 0f, 62f);
            TarkovFacilitySystems.AddZone(trigger, EFT.GameTriggers.TriggerZone.ETriggerZoneMode.FirstEnter, id);
            Undo.RegisterCreatedObjectUndo(trigger, "Example trigger");

            var bots = TarkovTriggeredSpawns.CreateBots(null);
            bots.transform.position = new Vector3(25f, 0f, 30f);
            bots.TriggerId = id;
            Undo.RegisterCreatedObjectUndo(bots.gameObject, "Example bot spawn");
        }

        private static void Create(string mapId)
        {
            var folder = $"Assets/Maps/{mapId}";
            var scenePath = $"{folder}/{mapId}.unity";
            var definitionPath = $"{folder}/{mapId}.asset";
            if (File.Exists(scenePath) || File.Exists(definitionPath))
            {
                EditorUtility.DisplayDialog("Tarkov SDK", $"{folder} already contains a map named '{mapId}'.", "OK");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            Directory.CreateDirectory(folder);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            TarkovSceneTools.CreateSkeleton();
            EditorSceneManager.SaveScene(scene, scenePath);
            // these keep their data next to the scene, so they come once it has a path
            TarkovAmbientAudioSystem.Create(scene);
            TarkovLightingPreview.ApplyRetailReflection();
            CreateExampleTrees(scene);
            CreateExampleFeatures(scene);
            TarkovSceneTools.BakeNavMesh();
            Physics.SyncTransforms();
            TarkovRainCover.Generate(scene);
            TarkovAirdrops.Generate(scene, 20f);
            TarkovAirdrops.EnsurePhysicsSetup(scene, new TarkovScenePreparer.Report());
            // the roads follow the navmesh, so they come after the bake
            var btr = TarkovBtr.Create(null, new Vector3(45f, 0f, -40f));
            Undo.RegisterCreatedObjectUndo(btr.gameObject, "BTR route");
            var failed = TarkovBtr.GenerateRoads(btr, replace: true);
            if (failed.Count > 0)
                Debug.LogWarning("[Tarkov SDK] Example BTR roads not made: " + string.Join("; ", failed));
            // the bot spawn's points want the navmesh
            CreateExampleStrike(scene);
            // rooms for the shed and the bunker (its trigger has Is Bunker) and the outdoor room; the
            // shed's door and the bunker's doorway become portals
            TarkovAudioWindow.Generate(scene);
            TarkovAudioSources.Fix(scene);
            EditorSceneManager.SaveScene(scene);

            var definition = CreateInstance<TarkovMapDefinition>();
            definition.MapId = mapId;
            definition.Scenes = new List<SceneAsset> { AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) };
            AssetDatabase.CreateAsset(definition, definitionPath);
            AssetDatabase.SaveAssets();

            Selection.activeObject = definition;
            EditorGUIUtility.PingObject(definition);
            Debug.Log($"[Tarkov SDK] Created map '{mapId}' in {folder}. Select {definitionPath} and press Build Map. " +
                      "Add ambience clips in Tarkov SDK > Generation > Ambience > Ambient Sounds..., pick the indoor and bunker room tones in " +
                      "Generation > Audio > Spatial Audio..., and simple or full weather in Generation > Time > Time of Day / Weather....");
        }
    }
}
