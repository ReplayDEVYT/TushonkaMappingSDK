using EFT;
using EFT.Ballistics;
using EFT.Game.Spawning;
using EFT.Interactive;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Menu items that place correctly configured game components. Everything here only sets
    // serialized fields; the game supplies all behaviour at runtime.
    public static class TarkovSceneTools
    {
        private const string Menu = "GameObject/Tarkov SDK/";

        [MenuItem("Tarkov SDK/Generation/Essential/Map Skeleton In Active Scene", priority = 100)]
        private static void CreateSkeletonMenu()
        {
            if (Object.FindObjectOfType<LocationScene>() != null &&
                !EditorUtility.DisplayDialog("Tarkov SDK", "This scene already has a LocationScene. Add the skeleton anyway?", "Add", "Cancel"))
                return;
            CreateSkeleton();
        }

        [MenuItem("Tarkov SDK/Generation/Essential/Bake NavMesh", priority = 101)]
        public static void BakeNavMesh()
        {
            // legacy scene bake with the "Humanoid" agent (type 0), the agent bots path with;
            // the NavMeshData asset is referenced by the scene and ships in the scene bundle
#pragma warning disable 618
            UnityEditor.AI.NavMeshBuilder.BuildNavMesh();
#pragma warning restore 618
            Debug.Log("[Tarkov SDK] NavMesh baked for the active scene. Only Navigation Static geometry is included.");
        }

        public static void CreateSkeleton()
        {
            var scene = EditorSceneManager.GetActiveScene();

            var location = new GameObject("LocationScene");
            location.AddComponent<LocationScene>();
            location.AddComponent<LocationExportInfo>();
            Undo.RegisterCreatedObjectUndo(location, "Tarkov map skeleton");

            var level = new GameObject("LevelSettings");
            var settings = level.AddComponent<LevelSettings>();
            // Factory's values: Unity's own ambient stays almost black because the lighting rig's
            // AmbientLight supplies the real ambient; bright values here wash the map out
            settings.AmbientMode = UnityEngine.Rendering.AmbientMode.Flat;
            settings.SkyColor = new Color(0.03f, 0.03f, 0.03f);
            settings.EquatorColor = new Color(0.175f, 0.231f, 0.255f);
            settings.GroundColor = new Color(0.152f, 0.132f, 0.097f);
            settings.AmbientIntensity = 0.6f;
            settings.SunColor = new Color(1f, 0.82f, 0.569f);
            settings.NightVisionSkyColor = new Color(0.115f, 0.172f, 0.206f);
            settings.NightVisionEquatorColor = settings.EquatorColor;
            settings.NightVisionGroundColor = settings.GroundColor;
            settings.NightVisionAmbientIntensity = 0.6f;
            settings.skybox = TarkovLightingRig.DefaultSkybox();
            settings.RainBounds = new Bounds(Vector3.zero, new Vector3(220f, 100f, 220f));
            // the camera's GlobalFog always applies these; an unset fogMode (0) fogs everything black
            settings.fog = true;
            settings.fogMode = FogMode.Exponential;
            settings.fogDensity = 0.004f;
            settings.fogColor = new Color(0.55f, 0.6f, 0.65f);
            settings.DirectionLightShadowForUnshadowedShaders = 0.832f;
            Undo.RegisterCreatedObjectUndo(level, "Tarkov map skeleton");

            // indoor/outdoor state for every player; Player.Init requires it
            var environment = new GameObject("EnvironmentManager");
            environment.AddComponent<EFT.EnvironmentEffect.EnvironmentManager>();
            Undo.RegisterCreatedObjectUndo(environment, "Tarkov map skeleton");

            // sun and ambient; without it BSG-shaded surfaces render black
            if (TarkovLightingRig.Find(scene) == null)
                Undo.RegisterCreatedObjectUndo(TarkovLightingRig.Create(scene), "Tarkov map skeleton");

            var surface = DefaultSurfaceMaterial();
            var geometry = new GameObject("Geometry");
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(geometry.transform, false);
            // 200 x 200 m: room for the BTR's route and turns, and the extracts at the edges
            ground.transform.localScale = new Vector3(20f, 1f, 20f);
            MakeStaticGeometry(ground, MaterialType.Concrete);
            if (surface != null)
                ground.GetComponent<Renderer>().sharedMaterial = surface;
            // something to take cover behind; the client generates AI cover points along walls
            var blocks = new[]
            {
                (new Vector3(0f, 1f, 0f), new Vector3(8f, 2f, 1f)),
                (new Vector3(-12f, 1.5f, 8f), new Vector3(1f, 3f, 10f)),
                (new Vector3(12f, 0.6f, -8f), new Vector3(6f, 1.2f, 1f)),
                (new Vector3(25f, 1.5f, 25f), new Vector3(10f, 3f, 1f)),
            };
            for (int i = 0; i < blocks.Length; i++)
            {
                var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
                block.name = "CoverBlock_" + (i + 1);
                block.transform.SetParent(geometry.transform, false);
                block.transform.localPosition = blocks[i].Item1;
                block.transform.localScale = blocks[i].Item2;
                MakeStaticGeometry(block, MaterialType.Concrete);
                if (surface != null)
                    block.GetComponent<Renderer>().sharedMaterial = surface;
            }
            Undo.RegisterCreatedObjectUndo(geometry, "Tarkov map skeleton");
            // bullets hit the detailed collider, players and bots collide with the simple one
            TarkovPrefabTools.AddLowHigh(geometry.transform.Find("CoverBlock_4").GetComponent<MeshFilter>(), true);

            CreateExampleBuilding(geometry.transform, new Vector3(-28f, 0f, 0f), surface);
            CreateExampleBunker(geometry.transform, new Vector3(-30f, 0f, -40f), surface);

            var zones = new GameObject("BotZones");
            var zone = CreateBotZone(zones.transform, "ZoneMain", Vector3.zero);
            CreateSpawnPoint(zone.transform, "Spawn_PMC_1", new Vector3(-20f, 0f, -20f), pmc: true);
            CreateSpawnPoint(zone.transform, "Spawn_Bot_1", new Vector3(20f, 0f, 20f), pmc: false);
            var way = CreatePatrolWay(zone.transform, "PatrolWay_Main", Vector3.zero);
            CreatePatrolPoint(way.transform, new Vector3(-15f, 0f, 15f));
            CreatePatrolPoint(way.transform, new Vector3(15f, 0f, 15f));
            CreatePatrolPoint(way.transform, new Vector3(15f, 0f, -15f));
            Undo.RegisterCreatedObjectUndo(zones, "Tarkov map skeleton");

            var exfils = new GameObject("Exfils");
            CreateExfil(exfils.transform, "SDK_Exit_Main", new Vector3(40f, 0f, 40f));
            // one of each of the other kinds the Extracts window makes; their rules go to the server with Build Map
            TarkovExtracts.Create(TarkovExtracts.Kind.Scav, exfils.transform).transform.position = new Vector3(90f, 0f, -90f);
            TarkovExtracts.Create(TarkovExtracts.Kind.Coop, exfils.transform).transform.position = new Vector3(-50f, 0f, -90f);
            // the stand-in car drives north (+Z) along its Drive Path when it leaves
            var car = TarkovExtracts.Create(TarkovExtracts.Kind.Car, exfils.transform);
            car.transform.position = new Vector3(-85f, 0f, -60f);
            TarkovExtracts.UpdateDrivePath(car.GetComponentInChildren<CarExtraction>(true));
            // flare zone, sniper zone and exit in a row along +X
            TarkovExtracts.Create(TarkovExtracts.Kind.Flare, exfils.transform).transform.position = new Vector3(10f, 0f, 85f);
            Undo.RegisterCreatedObjectUndo(exfils, "Tarkov map skeleton");

            // a minefield in the north-west corner, with a claymore's tripwires along its open side
            var hazards = new GameObject("Hazards");
            TarkovHazards.CreateMinefield(hazards.transform).transform.position = new Vector3(-78f, 4f, 78f);
            TarkovHazards.CreateClaymore(hazards.transform).transform.position = new Vector3(-55f, 0f, 55f);
            Undo.RegisterCreatedObjectUndo(hazards, "Tarkov map skeleton");

            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log("[Tarkov SDK] Map skeleton created. Next: bake the navmesh (Humanoid agent), then build the map from a Map Definition.");
        }

        private static void ExampleBox(Transform parent, Material surface, string name, Vector3 center, Vector3 size)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(parent, false);
            box.transform.localPosition = center;
            box.transform.localScale = size;
            MakeStaticGeometry(box, MaterialType.Concrete);
            if (surface != null)
                box.GetComponent<Renderer>().sharedMaterial = surface;
        }

        // A one-room shed showing the interactive and indoor features: a door with a switch beside it,
        // an indoor trigger over the room (indoor ambience, no rain on the screen) and a loot spawn.
        private static void CreateExampleBuilding(Transform parent, Vector3 position, Material surface)
        {
            const float width = 8f, depth = 6f, height = 3f, wall = 0.2f, doorWidth = 1.2f, doorHeight = 2.2f;
            var building = new GameObject("ExampleShed").transform;
            building.SetParent(parent, false);
            building.localPosition = position;

            void Box(string name, Vector3 center, Vector3 size) => ExampleBox(building, surface, name, center, size);

            float front = -depth * 0.5f, side = (width - doorWidth) * 0.5f;
            Box("Wall_Back", new Vector3(0f, height * 0.5f, depth * 0.5f), new Vector3(width, height, wall));
            Box("Wall_Left", new Vector3(-width * 0.5f, height * 0.5f, 0f), new Vector3(wall, height, depth));
            Box("Wall_Right", new Vector3(width * 0.5f, height * 0.5f, 0f), new Vector3(wall, height, depth));
            Box("Wall_Front_L", new Vector3(-(doorWidth + side) * 0.5f, height * 0.5f, front), new Vector3(side, height, wall));
            Box("Wall_Front_R", new Vector3((doorWidth + side) * 0.5f, height * 0.5f, front), new Vector3(side, height, wall));
            Box("Wall_Front_Top", new Vector3(0f, (height + doorHeight) * 0.5f, front), new Vector3(doorWidth, height - doorHeight, wall));
            Box("Roof", new Vector3(0f, height + wall * 0.5f, 0f), new Vector3(width + wall, wall, depth + wall));

            var door = TarkovPrefabTools.CreateDoor(building, 1f, 2.1f);
            door.transform.parent.localPosition = new Vector3(0f, 0f, front);
            // inside, beside the doorway, at hand height
            var sw = TarkovPrefabTools.CreateSwitch(building, door);
            sw.transform.localPosition = new Vector3(doorWidth * 0.5f + 0.3f, 1.2f, front + wall * 0.5f + 0.05f);

            var trigger = CreateIndoorTrigger(IndoorTriggerGroup().transform, Vector3.zero);
            trigger.name = "IndoorTrigger_ExampleShed";
            trigger.transform.position = building.TransformPoint(new Vector3(0f, height * 0.5f, 0f));
            trigger.transform.localScale = new Vector3(width - wall, height, depth - wall);

            var loot = new GameObject("LootSpawn") { tag = "EditorOnly" };
            loot.transform.SetParent(building, false);
            loot.transform.localPosition = new Vector3(width * 0.5f - 1f, 0.5f, depth * 0.5f - 1f);
            var spawn = loot.AddComponent<TarkovLootSpawn>();
            spawn.Probability = 0.5f;
            spawn.Items.Add(new TarkovLootSpawn.Item { Template = "57347d7224597744596b4e72", Name = "Can of beef stew (Small)", Weight = 3 });
            spawn.Items.Add(new TarkovLootSpawn.Item { Template = "5734773724597737fd047c14", Name = "Can of condensed milk", Weight = 2 });
            spawn.Items.Add(new TarkovLootSpawn.Item { Template = "544fb45d4bdc2dee738b4568", Name = "Salewa first aid kit", Weight = 1 });
        }

        // A thick concrete room with an open doorway: its indoor trigger has Is Bunker on, so the spatial
        // audio room over it becomes a bunker (bunker room tone, sound from outside cut off).
        private static void CreateExampleBunker(Transform parent, Vector3 position, Material surface)
        {
            const float width = 10f, depth = 8f, height = 3f, wall = 0.5f, doorWidth = 1.6f, doorHeight = 2.2f;
            var bunker = new GameObject("ExampleBunker").transform;
            bunker.SetParent(parent, false);
            bunker.localPosition = position;

            void Box(string name, Vector3 center, Vector3 size) => ExampleBox(bunker, surface, name, center, size);

            float east = width * 0.5f, side = (depth - doorWidth) * 0.5f;
            Box("Wall_West", new Vector3(-east, height * 0.5f, 0f), new Vector3(wall, height, depth + wall));
            Box("Wall_North", new Vector3(0f, height * 0.5f, depth * 0.5f), new Vector3(width + wall, height, wall));
            Box("Wall_South", new Vector3(0f, height * 0.5f, -depth * 0.5f), new Vector3(width + wall, height, wall));
            Box("Wall_East_N", new Vector3(east, height * 0.5f, (doorWidth + side) * 0.5f), new Vector3(wall, height, side));
            Box("Wall_East_S", new Vector3(east, height * 0.5f, -(doorWidth + side) * 0.5f), new Vector3(wall, height, side));
            Box("Wall_East_Top", new Vector3(east, (height + doorHeight) * 0.5f, 0f), new Vector3(wall, height - doorHeight, doorWidth));
            Box("Roof", new Vector3(0f, height + wall * 0.5f, 0f), new Vector3(width + wall, wall, depth + wall));

            var trigger = CreateIndoorTrigger(IndoorTriggerGroup().transform, Vector3.zero);
            trigger.name = "IndoorTrigger_ExampleBunker";
            trigger.IsBunker = true;
            trigger.transform.position = bunker.TransformPoint(new Vector3(0f, height * 0.5f, 0f));
            trigger.transform.localScale = new Vector3(width - wall, height, depth - wall);
        }

        [MenuItem(Menu + "Spawns/Spawn Point (PMC)", priority = 10)]
        private static void MenuSpawnPmc(MenuCommand cmd) => Place(cmd, p => CreateSpawnPoint(p, "Spawn_PMC", Vector3.zero, pmc: true).gameObject);

        [MenuItem(Menu + "Spawns/Spawn Point (Bot)", priority = 11)]
        private static void MenuSpawnBot(MenuCommand cmd) => Place(cmd, p => CreateSpawnPoint(p, "Spawn_Bot", Vector3.zero, pmc: false).gameObject);

        [MenuItem(Menu + "AI/Bot Zone", priority = 23)]
        private static void MenuZone(MenuCommand cmd) => Place(cmd, p => CreateBotZone(p, "ZoneNew", Vector3.zero).gameObject);

        [MenuItem(Menu + "AI/Patrol Way", priority = 24)]
        private static void MenuWay(MenuCommand cmd) => Place(cmd, p => CreatePatrolWay(p, "PatrolWay", Vector3.zero).gameObject);

        [MenuItem(Menu + "AI/Patrol Point", priority = 25)]
        private static void MenuPoint(MenuCommand cmd) => Place(cmd, p => CreatePatrolPoint(p, Vector3.zero).gameObject);

        [MenuItem(Menu + "Exfils/Exfil", priority = 13)]
        private static void MenuExfil(MenuCommand cmd) => Place(cmd, p => CreateExfil(p, "SDK_Exit", Vector3.zero).gameObject);

        [MenuItem(Menu + "AI/Nav Blocker", priority = 26)]
        private static void MenuNavBlocker(MenuCommand cmd) => Place(cmd, p => CreateNavBlocker(p, Vector3.zero));

        [MenuItem(Menu + "Exfils/Transit Point", priority = 21)]
        private static void MenuTransit(MenuCommand cmd) => Place(cmd, p => CreateTransit(p, Vector3.zero).gameObject);

        // a group per building keeps the game's indoor lookup fast: it tests a group's bounds before
        // its triggers. Created under the map's root group, or inside the group that was right-clicked.
        [MenuItem(Menu + "Environment/Indoor Trigger Group", priority = 67)]
        private static void MenuIndoorTriggerGroup(MenuCommand cmd)
        {
            var parent = (cmd.context as GameObject)?.GetComponent<EFT.EnvironmentEffect.TriggerGroup>() ?? IndoorTriggerGroup();
            var go = new GameObject("TriggerGroup");
            go.transform.SetParent(parent.transform, false);
            if (SceneView.lastActiveSceneView != null)
                go.transform.position = SceneView.lastActiveSceneView.pivot;
            go.AddComponent<EFT.EnvironmentEffect.TriggerGroup>();
            Undo.RegisterCreatedObjectUndo(go, "Create Indoor Trigger Group");
            Selection.activeGameObject = go;
        }

        // indoor triggers only work under a TriggerGroup inside the EnvironmentManager, so they are
        // always created there (or in the TriggerGroup that was right-clicked) at the Scene view centre
        [MenuItem(Menu + "Environment/Indoor Trigger", priority = 66)]
        private static void MenuIndoorTrigger(MenuCommand cmd)
        {
            var group = (cmd.context as GameObject)?.GetComponent<EFT.EnvironmentEffect.TriggerGroup>() ?? IndoorTriggerGroup();
            var position = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.pivot : Vector3.zero;
            var trigger = CreateIndoorTrigger(group.transform, position);
            Undo.RegisterCreatedObjectUndo(trigger.gameObject, "Create Indoor Trigger");
            Selection.activeGameObject = trigger.gameObject;
        }

        [MenuItem("Tarkov SDK/Tools/Geometry/Make Selected Static Geometry (Terrain layer)", priority = 123)]
        private static void MenuStaticGeometry()
        {
            foreach (var go in Selection.gameObjects)
            {
                Undo.RegisterFullObjectHierarchyUndo(go, "Make static geometry");
                foreach (var collider in go.GetComponentsInChildren<Collider>(true))
                    if (!collider.isTrigger)
                        MakeStaticGeometry(collider.gameObject, MaterialType.Concrete);
            }
        }

        [MenuItem("Tarkov SDK/Tools/Geometry/Add BallisticCollider To Selected Colliders", priority = 125)]
        private static void MenuAddBallistic()
        {
            foreach (var go in Selection.gameObjects)
                foreach (var collider in go.GetComponentsInChildren<Collider>(true))
                    if (!collider.isTrigger && collider.GetComponent<BallisticCollider>() == null)
                        SetupBallistic(Undo.AddComponent<BallisticCollider>(collider.gameObject), MaterialType.Concrete);
        }

        // ------------------------------------------------------------ factories

        // creates an asset folder and its parents through the AssetDatabase (CreateAsset needs it to know the folder)
        public static void EnsureFolder(string folder)
        {
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(folder))
                return;
            var parent = System.IO.Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(folder));
        }

        // a concrete-grey material on the stand-in for the game's most used prop/building shader; the
        // client swaps it for the real "p0/Reflective/Bumped Specular SMap" in the raid. That shader
        // reads albedo from _MainTex RGB, specular from _MainTex alpha and gloss from _SpecMap, so
        // Unity's plain white textures would make a fully white, fully glossy surface that blooms.
        [MenuItem("Tarkov SDK/Tools/Geometry/Refresh Default Surface Material", priority = 126)]
        private static void RefreshDefaultSurfaceMenu() => DefaultSurfaceMaterial();

        public static Material DefaultSurfaceMaterial()
        {
            const string folder = "Assets/TarkovSDK/Materials";
            const string path = folder + "/SDK_Default.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                var shader = Shader.Find("Tarkov/p0/Reflective/Bumped Specular SMap");
                if (shader == null)
                {
                    Debug.LogWarning("[Tarkov SDK] Stand-in shader 'Tarkov/p0/Reflective/Bumped Specular SMap' not found; the skeleton keeps Unity's default material.");
                    return null;
                }
                System.IO.Directory.CreateDirectory(folder);
                material = new Material(shader) { name = "SDK_Default" };
                AssetDatabase.CreateAsset(material, path);
            }
            if (material.HasProperty("_MainTex") && material.GetTexture("_MainTex") == null)
            {
                // grey albedo with a little noise; alpha 0.12 = low specular
                material.SetTexture("_MainTex", SolidTexture(folder + "/SDK_Default_Albedo.png", new Color(0.42f, 0.41f, 0.39f, 0.12f), 0.05f));
                material.SetTexture("_SpecMap", SolidTexture(folder + "/SDK_Default_Gloss.png", new Color(0.2f, 0.2f, 0.2f, 1f), 0f));
                material.SetColor("_Color", Color.white);
                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssets();
            }
            if (TarkovReflectionCube.IsMissing(material))
            {
                TarkovReflectionCube.Assign(material);
                AssetDatabase.SaveAssets();
            }
            return material;
        }

        internal static Texture2D SolidTexture(string path, Color color, float noise)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null)
                return existing;
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var random = new System.Random(1);
            var pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++)
            {
                float n = ((float)random.NextDouble() - 0.5f) * 2f * noise;
                pixels[i] = new Color(color.r + n, color.g + n, color.b + n, color.a);
            }
            texture.SetPixels(pixels);
            System.IO.File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        internal static void Place(MenuCommand cmd, System.Func<Transform, GameObject> create)
        {
            var parent = (cmd.context as GameObject)?.transform;
            var go = create(parent);
            if (go == null)
                return;
            if (parent == null && SceneView.lastActiveSceneView != null)
                go.transform.position = SceneView.lastActiveSceneView.pivot;
            Undo.RegisterCreatedObjectUndo(go, "Create " + go.name);
            Selection.activeGameObject = go;
        }

        private static GameObject Child(Transform parent, string name, Vector3 localPosition)
        {
            var go = new GameObject(name);
            if (parent != null)
                go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go;
        }

        public static BotZone CreateBotZone(Transform parent, string name, Vector3 localPosition)
        {
            var go = Child(parent, name, localPosition);
            var zone = go.AddComponent<BotZone>();
            zone.PatrolWays = new PatrolWay[0];
            zone.UnSpawnPoints = new UnspawnPoint[0];
            zone.SpawnPointMarkers = new System.Collections.Generic.List<SpawnPointMarker>();
            return zone;
        }

        public static SpawnPointMarker CreateSpawnPoint(Transform parent, string name, Vector3 localPosition, bool pmc)
        {
            var go = Child(parent, name, localPosition);
            go.layer = LayerMask.NameToLayer("Triggers");
            var sphere = go.AddComponent<SphereCollider>();
            sphere.isTrigger = true;
            sphere.radius = 20f;
            var marker = go.AddComponent<SpawnPointMarker>();
            marker._collider = sphere;
            marker.Color = pmc ? Color.cyan : Color.yellow;
            marker._spawnPoint = new SpawnPoint
            {
                Id = System.Guid.NewGuid().ToString(),
                Name = name,
                Sides = pmc ? EPlayerSideMask.Pmc : EPlayerSideMask.Savage,
                Categories = pmc ? ESpawnCategoryMask.Player : ESpawnCategoryMask.Player | ESpawnCategoryMask.Bot,
                Infiltration = pmc ? "Main" : "",
                DelayToCanSpawnSec = 4f,
            };
            return marker;
        }

        public static PatrolWay CreatePatrolWay(Transform parent, string name, Vector3 localPosition)
        {
            var go = Child(parent, name, localPosition);
            var way = go.AddComponent<PatrolWay>();
            way.PatrolType = PatrolType.patrolling;
            return way;
        }

        public static PatrolPoint CreatePatrolPoint(Transform parent, Vector3 localPosition)
        {
            int index = parent != null ? parent.childCount : 0;
            var go = Child(parent, "PatrolPoint_" + index, localPosition);
            var point = go.AddComponent<PatrolPoint>();
            point.PatrolWay = parent != null ? parent.GetComponent<PatrolWay>() : null;
            return point;
        }

        public static ExfiltrationPoint CreateExfil(Transform parent, string name, Vector3 localPosition)
        {
            var go = Child(parent, name, localPosition);
            go.layer = LayerMask.NameToLayer("Triggers");
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(6f, 4f, 6f);
            box.center = new Vector3(0f, 2f, 0f);
            var exfil = go.AddComponent<ExfiltrationPoint>();
            exfil.Settings = new ExitTriggerSettings
            {
                Name = name,
                EntryPoints = "Main",
                ExfiltrationType = EExfiltrationType.Individual,
                ExfiltrationTime = 8f,
                Chance = 100f,
            };
            exfil.EligibleEntryPoints = new[] { "main" };
            exfil.Requirements = new ExfiltrationRequirement[0];
            return exfil;
        }

        // A transit to another map. "location" is the destination's map id (bigmap, Woods, or a
        // custom map's id); the server mod fills in "target" (its _Id). name/description/conditions
        // are locale keys: Edit Server Data > Add Missing Locale Keys adds them.
        public static TransitPoint CreateTransit(Transform parent, Vector3 localPosition)
        {
            var go = Child(parent, "Transit", localPosition);
            go.layer = LayerMask.NameToLayer("Triggers");
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(6f, 4f, 6f);
            box.center = new Vector3(0f, 2f, 0f);

            int id = 1;
            var used = new System.Collections.Generic.HashSet<int>();
            foreach (var other in Object.FindObjectsOfType<TransitPoint>(true))
                if (other.parameters != null)
                    used.Add(other.parameters.id);
            while (used.Contains(id))
                id++;

            var mapId = LocaleKeyPrefix();
            var transit = go.AddComponent<TransitPoint>();
            transit.parameters = new JsonType.LocationSettings.Location.TransitParameters
            {
                id = id,
                active = true,
                name = $"{mapId}_TRANSIT_{id}",
                description = $"{mapId}_TRANSIT_{id}_DESC",
                conditions = $"{mapId}_TRANSIT_{id}_COND",
                activateAfterSec = 60,
                time = 20,
                location = "",
                target = "",
            };
            go.name = "Transit_" + id;
            return transit;
        }

        internal static string LocaleKeyPrefix() => EditorSceneManager.GetActiveScene().name.ToUpperInvariant();

        public static EFT.EnvironmentEffect.IndoorTrigger CreateIndoorTrigger(Transform group, Vector3 position)
        {
            var go = new GameObject("IndoorTrigger");
            go.transform.SetParent(group, false);
            go.transform.position = position + new Vector3(0f, 2f, 0f);
            go.transform.localScale = new Vector3(10f, 4f, 10f); // the trigger is this box
            return go.AddComponent<EFT.EnvironmentEffect.IndoorTrigger>();
        }

        // the TriggerGroup EnvironmentManager reads, created (with the manager) when missing
        internal static EFT.EnvironmentEffect.TriggerGroup IndoorTriggerGroup()
        {
            var manager = Object.FindObjectOfType<EFT.EnvironmentEffect.EnvironmentManager>(true);
            if (manager == null)
            {
                manager = new GameObject("EnvironmentManager").AddComponent<EFT.EnvironmentEffect.EnvironmentManager>();
                Undo.RegisterCreatedObjectUndo(manager.gameObject, "Create EnvironmentManager");
            }
            var group = manager.GetComponentInChildren<EFT.EnvironmentEffect.TriggerGroup>(true);
            if (group == null)
            {
                var go = new GameObject("IndoorTriggers");
                go.transform.SetParent(manager.transform, false);
                group = go.AddComponent<EFT.EnvironmentEffect.TriggerGroup>();
                Undo.RegisterCreatedObjectUndo(go, "Create TriggerGroup");
            }
            return group;
        }

        // A box that cuts a hole in the navmesh: bots can't path through it, and the AI cover points
        // and patrols generated from the navmesh avoid it. The legacy bake reads static render meshes,
        // so the box keeps an enabled renderer and no collider; the EditorOnly tag strips it from the
        // build, so it never appears in game. Scale it over the area and rebake the navmesh.
        public static GameObject CreateNavBlocker(Transform parent, Vector3 localPosition)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "NavBlocker";
            if (parent != null)
                go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition + new Vector3(0f, 1f, 0f);
            go.transform.localScale = new Vector3(4f, 2f, 4f);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.tag = "EditorOnly";

            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = NavBlockerMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

#pragma warning disable 618
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.NavigationStatic);
            GameObjectUtility.SetNavMeshArea(go, UnityEngine.AI.NavMesh.GetAreaFromName("Not Walkable"));
#pragma warning restore 618
            return go;
        }

        // translucent red so blockers stand out in the editor; never shipped (EditorOnly)
        private static Material NavBlockerMaterial()
        {
            const string path = "Assets/TarkovSDK/Materials/SDK_NavBlocker.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null)
                return material;
            System.IO.Directory.CreateDirectory("Assets/TarkovSDK/Materials");
            material = new Material(Shader.Find("Legacy Shaders/Transparent/Diffuse")) { name = "SDK_NavBlocker" };
            material.color = new Color(1f, 0.15f, 0.1f, 0.35f);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        public static void MakeStaticGeometry(GameObject go, MaterialType material,
                                              BaseBallistic.ESurfaceSound sound = BaseBallistic.ESurfaceSound.Concrete)
        {
            go.layer = LayerMask.NameToLayer("Terrain");
            // NavigationStatic is deprecated but is still what the legacy scene bake collects
#pragma warning disable 618
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.NavigationStatic | StaticEditorFlags.BatchingStatic
                                                       | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
#pragma warning restore 618
            var ballistic = go.GetComponent<BallisticCollider>() ?? go.AddComponent<BallisticCollider>();
            SetupBallistic(ballistic, material, sound);
        }

        private static void SetupBallistic(BallisticCollider ballistic, MaterialType material,
                                           BaseBallistic.ESurfaceSound sound = BaseBallistic.ESurfaceSound.Concrete)
        {
            ballistic._typeOfMaterial = material;
            ballistic.SurfaceSound = sound;
            // penetration/ricochet values are left at the game's defaults; tune per surface
            EditorUtility.SetDirty(ballistic);
        }
    }
}
