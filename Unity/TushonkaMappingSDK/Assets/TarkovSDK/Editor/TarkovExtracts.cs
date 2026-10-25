using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BezierSplineTools;
using EFT.Ballistics;
using EFT.Game.Spawning;
using EFT.Interactive;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // Extracts of every kind the game has, as retail sets them up (the exit's rules live in the server's exits,
    // which Build Map writes from the scene; the game matches them by Name):
    //
    //  - Regular: walk in, wait the exfil time.
    //  - Scav: a ScavExfiltrationPoint, for scav raids.
    //  - Co-op: a SharedExfiltrationPoint with ScavCooperation (PMC and scav extract together, 8 s shared timer).
    //  - Car (V-Ex): SharedTimer, pay 5000 roubles (TransferItem), up to 4 players, 60 s; on 50 % of raids. A
    //    CarExtraction drives the car off along its path when the extract closes (and removes it on raids
    //    without it). The car is a physics vehicle: WheelDrive, EasySuspension, a RigidbodySpawner and four
    //    WheelColliders whose first child is the wheel model, as Customs' Toyota.
    //  - Flare: an extract behind a sniper zone; firing a green flare in the flare zone in front of it makes the
    //    sniper hold fire for that player and their group (Customs' customs_sniper_exit).
    //  - Elevator: TarkovFacilitySystems' Labs elevators.
    //
    // Any of them can be a transit instead (TransitPoint): the same zone takes the player to another map.
    public static class TarkovExtracts
    {
        private const string Menu = "GameObject/Tarkov SDK/Exfils/";
        public const string Roubles = "5449016a4bdc2d6f028b456f";
        public const string DrivePathName = "Drive Path";

        public enum Kind { Regular, Scav, Coop, Car, Flare, Elevator, ElevatorPower, ElevatorApproach, Transit }

        public static readonly (Kind Kind, string Label, string Help)[] Kinds =
        {
            (Kind.Regular, "Regular", "Walk in and wait the exfil time."),
            (Kind.Scav, "Scav", "For scav raids only."),
            (Kind.Coop, "Co-op (PMC + Scav)", "A PMC and a scav extract together; 8 s shared timer."),
            (Kind.Car, "Car (V-Ex)", "Pay 5000 roubles; up to 4 players leave together after 60 s, the car drives off. On half the raids."),
            (Kind.Flare, "Flare", "Behind a sniper zone: fire a green flare in the flare zone in front of it and the sniper holds fire."),
            (Kind.Elevator, "Elevator", "Labs-style: call it with a button, ride out."),
            (Kind.ElevatorPower, "Elevator (needs power)", "As Elevator, once a power switch is on."),
            (Kind.ElevatorApproach, "Elevator (opens on approach)", "As Elevator, called by walking up to it."),
        };

        [MenuItem(Menu + "Car Extract", priority = 16)]
        private static void MenuCar(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => Create(Kind.Car, p));

        [MenuItem(Menu + "Flare Extract", priority = 17)]
        private static void MenuFlare(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => Create(Kind.Flare, p));

        [MenuItem(Menu + "Scav Extract", priority = 14)]
        private static void MenuScav(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => Create(Kind.Scav, p));

        [MenuItem(Menu + "Co-op Extract (PMC + Scav)", priority = 15)]
        private static void MenuCoop(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => Create(Kind.Coop, p));

        public static string Label(Kind kind) => kind == Kind.Transit ? "Transit" : Kinds.First(k => k.Kind == kind).Label;

        public static Kind KindOf(Component c)
        {
            if (c is TransitPoint)
                return Kind.Transit;
            var exfil = c as ExfiltrationPoint;
            if (exfil == null)
                return Kind.Regular;
            if (exfil is SharedExfiltrationPoint)
                return Kind.Coop;
            if (exfil is ScavExfiltrationPoint)
                return Kind.Scav;
            if (Object.FindObjectsOfType<CarExtraction>(true).Any(car => car.Subscribee == exfil))
                return Kind.Car;
            if (exfil.Settings?.ExfiltrationType == EExfiltrationType.Manual)
                return Kind.Elevator;
            if (exfil.transform.parent != null && exfil.transform.parent.GetComponentInChildren<PlayersWithImmuneToSniperFireCollector>(true) != null)
                return Kind.Flare;
            return Kind.Regular;
        }

        // the entry points of the scene's PMC spawns, so a new extract is usable from all of them
        public static List<string> EntryPoints(Scene scene) =>
            scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<SpawnPointMarker>(true))
                 .Select(m => m._spawnPoint?.Infiltration).Where(i => !string.IsNullOrWhiteSpace(i))
                 .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(i => i).ToList();

        private static string UniqueName(Scene scene, string name)
        {
            var used = new HashSet<string>(scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<ExfiltrationPoint>(true))
                                                .Select(e => e.Settings?.Name ?? e.name), StringComparer.OrdinalIgnoreCase);
            var candidate = name;
            for (int i = 2; used.Contains(candidate); i++)
                candidate = $"{name}_{i}";
            return candidate;
        }

        private static Scene SceneOf(Transform parent) => parent != null ? parent.gameObject.scene : SceneManager.GetActiveScene();

        public static GameObject Create(Kind kind, Transform parent)
        {
            var scene = SceneOf(parent);
            switch (kind)
            {
                case Kind.Scav: return AddExfil<ScavExfiltrationPoint>(Zone(parent, "ScavExtract"), UniqueName(scene, "Scav_Extract"), scene).gameObject;
                case Kind.Coop: return CreateCoop(parent, scene).gameObject;
                case Kind.Car: return CreateCar(parent, null, null, null);
                case Kind.Flare: return CreateFlare(parent, scene);
                case Kind.Elevator: return TarkovFacilitySystems.CreateElevator(parent, withPower: false);
                case Kind.ElevatorPower: return TarkovFacilitySystems.CreateElevator(parent, withPower: true);
                case Kind.ElevatorApproach: return TarkovFacilitySystems.CreateElevator(parent, withPower: false, onApproach: true);
                default: return AddExfil<ExfiltrationPoint>(Zone(parent, "Extract"), UniqueName(scene, "Extract"), scene).gameObject;
            }
        }

        private static GameObject Zone(Transform parent, string name, Vector3 size = default)
        {
            var go = TarkovFacilitySystems.Child(parent, name, Vector3.zero, "Triggers");
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = size == default ? new Vector3(6f, 4f, 6f) : size;
            box.center = new Vector3(0f, box.size.y * 0.5f, 0f);
            return go;
        }

        private static T AddExfil<T>(GameObject go, string name, Scene scene) where T : ExfiltrationPoint
        {
            var entry = EntryPoints(scene);
            var exfil = go.AddComponent<T>();
            exfil.Settings = new ExitTriggerSettings
            {
                Name = name,
                EntryPoints = entry.Count > 0 ? string.Join(",", entry) : "Main",
                ExfiltrationType = EExfiltrationType.Individual,
                ExfiltrationTime = 8f,
                Chance = 100f,
            };
            exfil.EligibleEntryPoints = exfil.Settings.EntryPoints.ToLowerInvariant().Split(',');
            exfil.Requirements = new ExfiltrationRequirement[0];
            go.name = name;
            return exfil;
        }

        private static SharedExfiltrationPoint CreateCoop(Transform parent, Scene scene)
        {
            var exfil = AddExfil<SharedExfiltrationPoint>(Zone(parent, "CoopExtract"), UniqueName(scene, "Coop_Extract"), scene);
            exfil.Settings.ExfiltrationType = EExfiltrationType.SharedTimer;
            exfil.Settings.ExfiltrationTime = 8f;
            exfil.Requirements = new[] { new ExfiltrationRequirement { Requirement = ERequirementState.ScavCooperation, RequirementTip = "EXFIL_Cooperate" } };
            exfil.IsMandatoryForScavs = true;
            exfil.Reusable = true;
            return exfil;
        }

        // ------------------------------------------------------------ car

        // the car is the given model (its wheels found by name) or a stand-in box; it drives along the
        // Drive Path's points once the extract closes
        public static GameObject CreateCar(Transform parent, GameObject model, AudioClip idle, AudioClip start)
        {
            var scene = SceneOf(parent);
            var root = TarkovFacilitySystems.Child(parent, "CarExtract", Vector3.zero);
            var exfil = AddExfil<ExfiltrationPoint>(Zone(root.transform, "Exit", new Vector3(10f, 4f, 10f)), UniqueName(scene, "Car_Extract"), scene);
            exfil.Settings.ExfiltrationType = EExfiltrationType.SharedTimer;
            exfil.Settings.ExfiltrationTime = 60f;
            exfil.Settings.PlayersCount = 4;
            exfil.Settings.Chance = 50f;
            exfil.Requirements = new[] { new ExfiltrationRequirement { Requirement = ERequirementState.TransferItem, Id = Roubles, Count = 5000, RequirementTip = "EXFIL_Item" } };

            // everything the extraction removes when the car isn't there this raid, or after it drove off
            var holder = TarkovFacilitySystems.Child(root.transform, "CarExtraction", Vector3.zero);
            var extraction = holder.AddComponent<CarExtraction>();
            extraction.Subscribee = exfil;
            extraction.Step = 0.1f;
            extraction.Driver = BuildCar(holder.transform, model, idle, start);

            var path = TarkovFacilitySystems.Child(holder.transform, DrivePathName, Vector3.zero);
            path.tag = "EditorOnly";
            foreach (var z in new[] { 8f, 25f, 45f, 70f })
                TarkovFacilitySystems.Child(path.transform, "Point", new Vector3(0f, 0f, z));
            extraction.Spline = TarkovFacilitySystems.Child(holder.transform, "Spline", Vector3.zero).AddComponent<BezierSpline>();
            UpdateDrivePath(extraction);
            return root;
        }

        // the spline through the car and the Drive Path's points (also at Build Map)
        public static void UpdateDrivePath(CarExtraction extraction)
        {
            var path = extraction.transform.Find(DrivePathName);
            if (path == null || extraction.Spline == null)
                return;
            var points = new List<Vector3>();
            if (extraction.Driver != null)
                points.Add(extraction.Driver.transform.position);
            points.AddRange(path.Cast<Transform>().Select(t => t.position));
            TarkovSplines.FromWaypoints(extraction.Spline, points);
            EditorUtility.SetDirty(extraction.Spline);
        }

        private static WheelDrive BuildCar(Transform parent, GameObject model, AudioClip idle, AudioClip start)
        {
            var car = TarkovFacilitySystems.Child(parent, "Car", Vector3.zero, "LowPolyCollider");
            var wheels = new List<Transform>();
            if (model != null)
            {
                var body = (GameObject)PrefabUtility.InstantiatePrefab(model) ?? Object.Instantiate(model);
                if (PrefabUtility.IsPartOfPrefabInstance(body))
                    PrefabUtility.UnpackPrefabInstance(body, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                body.name = "Body";
                body.transform.SetParent(car.transform, false);
                body.transform.localPosition = Vector3.zero;
                foreach (var c in body.GetComponentsInChildren<Collider>(true))
                    Object.DestroyImmediate(c);
                wheels = body.GetComponentsInChildren<Transform>(true)
                             .Where(t => t.name.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0 && t.GetComponentInChildren<Renderer>() != null)
                             .Where((t, _) => t.parent == null || t.parent.name.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) < 0).ToList();
                if (wheels.Count != 4)
                {
                    Debug.LogWarning($"[Tarkov SDK] Car extract: found {wheels.Count} objects named like 'wheel' in {model.name}, not 4; using stand-in wheels. Name the four wheel objects '...Wheel...'.");
                    wheels.Clear();
                }
            }
            else
            {
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.name = "Body (replace with your car model)";
                Object.DestroyImmediate(box.GetComponent<Collider>());
                box.transform.SetParent(car.transform, false);
                box.transform.localPosition = new Vector3(0f, 1.05f, 0f);
                box.transform.localScale = new Vector3(1.9f, 1.1f, 4.6f);
                var material = TarkovSceneTools.DefaultSurfaceMaterial();
                if (material != null)
                    box.GetComponent<MeshRenderer>().sharedMaterial = material;
            }
            var bounds = new Bounds(car.transform.position + Vector3.up, new Vector3(1.9f, 1.5f, 4.6f));
            var renderers = car.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length > 0)
            {
                bounds = renderers[0].bounds;
                foreach (var r in renderers.Skip(1))
                    bounds.Encapsulate(r.bounds);
            }
            float radius = 0.42f;
            if (wheels.Count == 4)
            {
                var wb = wheels[0].GetComponentInChildren<Renderer>().bounds;
                radius = Mathf.Max(0.2f, wb.size.y * 0.5f);
            }
            else
            {
                var size = bounds.size;
                foreach (var (x, z) in new[] { (-1f, 1f), (1f, 1f), (-1f, -1f), (1f, -1f) })
                {
                    var wheel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    wheel.name = "WheelModel";
                    Object.DestroyImmediate(wheel.GetComponent<Collider>());
                    wheel.transform.SetParent(car.transform, false);
                    wheel.transform.localPosition = new Vector3(x * (size.x * 0.5f - 0.2f), radius, z * (size.z * 0.5f - 0.8f));
                    wheel.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
                    wheel.transform.localScale = new Vector3(radius * 2f, 0.12f, radius * 2f);
                    wheels.Add(wheel.transform);
                }
            }

            // chassis from the wheel centres up, so the wheels carry it
            var localBounds = new Bounds(car.transform.InverseTransformPoint(bounds.center), bounds.size);
            var chassis = TarkovFacilitySystems.Child(car.transform, "Chassis", Vector3.zero, "LowPolyCollider");
            var chassisBox = chassis.AddComponent<BoxCollider>();
            float bottom = radius + 0.15f;
            chassisBox.center = new Vector3(localBounds.center.x, (bottom + localBounds.max.y) * 0.5f, localBounds.center.z);
            chassisBox.size = new Vector3(localBounds.size.x, Mathf.Max(0.5f, localBounds.max.y - bottom), localBounds.size.z);

            // WheelDrive takes each WheelCollider's first child as its wheel model
            var colliders = new List<WheelCollider>();
            foreach (var wheel in wheels.OrderByDescending(w => car.transform.InverseTransformPoint(w.position).z).ThenBy(w => car.transform.InverseTransformPoint(w.position).x))
            {
                var local = car.transform.InverseTransformPoint(wheel.position);
                var go = TarkovFacilitySystems.Child(car.transform, (local.z > 0 ? "Wheel_Front" : "Wheel_Back") + (local.x < 0 ? "_L" : "_R"), local, "LowPolyCollider");
                var wc = go.AddComponent<WheelCollider>();
                wc.radius = radius;
                wc.mass = 20f;
                wc.suspensionDistance = 0.2f;
                wc.forceAppPointDistance = 0.74f;
                var spring = wc.suspensionSpring;
                spring.targetPosition = 0.5f;
                wc.suspensionSpring = spring;
                wheel.SetParent(go.transform, true);
                wheel.SetAsFirstSibling();
                colliders.Add(wc);
            }

            var spawner = car.AddComponent<RigidbodySpawner>();
            spawner.mass = 1500f;
            spawner.angularDrag = 0.05f;
            spawner.useGravity = true;
            var suspension = car.AddComponent<EasySuspension>();
            var audio = car.AddComponent<AudioSource>();
            audio.clip = idle;
            audio.loop = true;
            audio.playOnAwake = idle != null;
            audio.volume = 0.54f;
            audio.spatialBlend = 1f;
            audio.rolloffMode = AudioRolloffMode.Linear;
            audio.minDistance = 2f;
            audio.maxDistance = 20f;
            TarkovAudioSources.SetUp(audio, undo: false);
            var drive = car.AddComponent<WheelDrive>();
            drive._rigidSpawner = spawner;
            drive.ES = suspension;
            drive.frontWheels = colliders.Take(2).ToArray();
            drive.AudioSource = audio;
            drive.OnStartSound = start;
            // Customs' Toyota
            drive.MinSpeedAtDistance = new Vector2(1650f, 1f);
            drive.MaxpeedAtDistance = new Vector2(4200f, 10f);
            drive.BrakeTorque = 400000f;
            drive.SteerSpeed = 1000f;
            drive.SpeedByTurnAngel = new Vector2(1f, 1f);
            drive.criticalSpeed = 5f;
            drive.stepsBelow = 5;
            drive.stepsAbove = 1;

            // the bullet hull: WheelDrive moves it with the car every frame
            var ballistics = TarkovFacilitySystems.Child(parent, "Ballistics", Vector3.zero);
            var hull = TarkovFacilitySystems.Child(ballistics.transform, "Hull", Vector3.zero, "HighPolyCollider");
            var hullBox = hull.AddComponent<BoxCollider>();
            hullBox.center = chassisBox.center;
            hullBox.size = chassisBox.size;
            var ballistic = hull.AddComponent<BallisticCollider>();
            ballistic._typeOfMaterial = MaterialType.MetalThin;
            ballistic.SurfaceSound = BaseBallistic.ESurfaceSound.MetalThin;
            drive.Ballistics = ballistics.transform;
            return drive;
        }

        // Build Map: the car's path from its points, and what the game reads unchecked
        public static void Prepare(GameObject[] roots, string sceneName, TarkovScenePreparer.Report report)
        {
            foreach (var car in roots.SelectMany(r => r.GetComponentsInChildren<CarExtraction>(true)))
            {
                UpdateDrivePath(car);
                var drive = car.Driver;
                if (drive == null || car.Spline == null)
                {
                    report.Errors.Add($"{sceneName}/{car.name}: car extraction needs its car (WheelDrive) and spline");
                    continue;
                }
                if (drive.transform.parent != car.transform && !drive.transform.IsChildOf(car.transform))
                    report.Warnings.Add($"{sceneName}/{car.name}: the car isn't under its CarExtraction, so it stays when the extract is off");
                var wheels = drive.GetComponentsInChildren<WheelCollider>(true);
                if (drive.frontWheels == null || drive.frontWheels.Length < 2 || drive.frontWheels.Any(w => w == null))
                    report.Errors.Add($"{sceneName}/{drive.name}: WheelDrive needs its two front wheels");
                if (wheels.Any(w => w.transform.childCount == 0))
                    report.Errors.Add($"{sceneName}/{drive.name}: every WheelCollider needs its wheel model as its first child (WheelDrive spins it)");
                if (drive.Ballistics == null)
                    report.Errors.Add($"{sceneName}/{drive.name}: WheelDrive needs its Ballistics object (it moves it every frame)");
                if (drive._rigidSpawner == null)
                    report.Errors.Add($"{sceneName}/{drive.name}: WheelDrive needs its RigidbodySpawner");
                if (drive.AudioSource == null)
                    report.Errors.Add($"{sceneName}/{drive.name}: WheelDrive needs an AudioSource (the car extraction plays it)");
                else if (drive.OnStartSound == null && drive.AudioSource.clip == null)
                    drive.AudioSource.enabled = false; // nothing to play; disabled, the drive-off sound is skipped
                if (car.Subscribee == null && !(car.transform.parent ?? car.transform).GetComponentsInChildren<TransitPoint>(true).Any())
                    report.Warnings.Add($"{sceneName}/{car.name}: car extraction follows no extract, so the car just stays parked");
                EditorUtility.SetDirty(drive);
            }
        }

        // ------------------------------------------------------------ flare

        // flare zone, sniper zone, extract, one behind the other along +X
        private static GameObject CreateFlare(Transform parent, Scene scene)
        {
            var root = TarkovFacilitySystems.Child(parent, "FlareExtract", Vector3.zero);
            var flare = TarkovHazards.CreateFlareZone(root.transform);
            flare.transform.localPosition = Vector3.zero;
            var zone = TarkovHazards.CreateSniperZone(root.transform);
            zone.transform.localPosition = new Vector3(40f, 8f, 0f);
            // the buffer faces the flare zone, where the player comes from
            zone._triggerZoneSettings = new Vector4(13f, 0f, 0f, 0f);
            zone._playersWithImmuneToFire = flare;
            var exfil = AddExfil<ExfiltrationPoint>(Zone(root.transform, "Exit", new Vector3(10f, 4f, 10f)), UniqueName(scene, "Flare_Extract"), scene);
            exfil.transform.localPosition = new Vector3(60f, 0f, 0f);
            return root;
        }

        // ------------------------------------------------------------ extract <-> transit

        // same zone, takes the player to another map instead; the extract's own rules are dropped
        public static TransitPoint ToTransit(ExfiltrationPoint exfil)
        {
            var go = exfil.gameObject;
            var name = exfil.Settings?.Name ?? go.name;
            foreach (var car in Object.FindObjectsOfType<CarExtraction>(true).Where(c => c.Subscribee == exfil))
            {
                Undo.RecordObject(car, "Make transit");
                car.Subscribee = null;
            }
            Undo.DestroyObjectImmediate(exfil);
            var transit = Undo.AddComponent<TransitPoint>(go);
            int id = 1;
            var used = new HashSet<int>(Object.FindObjectsOfType<TransitPoint>(true).Where(t => t != transit && t.parameters != null).Select(t => t.parameters.id));
            while (used.Contains(id))
                id++;
            var prefix = TarkovSceneTools.LocaleKeyPrefix();
            transit.parameters = new JsonType.LocationSettings.Location.TransitParameters
            {
                id = id,
                active = true,
                name = $"{prefix}_TRANSIT_{id}",
                description = $"{prefix}_TRANSIT_{id}_DESC",
                conditions = $"{prefix}_TRANSIT_{id}_COND",
                activateAfterSec = 60,
                time = 20,
                location = "",
                target = "",
            };
            Debug.Log($"[Tarkov SDK] '{name}' is now transit {id}; pick its destination map in Tarkov SDK > Tools > Extracts > Extracts...");
            return transit;
        }

        public static ExfiltrationPoint ToExtract(TransitPoint transit)
        {
            var go = transit.gameObject;
            Undo.DestroyObjectImmediate(transit);
            Undo.RecordObject(go, "Make extract");
            var exfil = AddExfil<ExfiltrationPoint>(go, UniqueName(go.scene, go.name.StartsWith("Transit") ? "Extract" : go.name), go.scene);
            Undo.RegisterCreatedObjectUndo(exfil, "Make extract");
            return exfil;
        }

        // the maps a transit can lead to: retail ids, and the custom maps installed in SPT
        public static List<string> Destinations()
        {
            var maps = new List<string> { "bigmap", "factory4_day", "factory4_night", "Woods", "Shoreline", "Interchange", "RezervBase", "laboratory",
                                          "Lighthouse", "TarkovStreets", "Sandbox", "Sandbox_high", "Labyrinth" };
            var root = TarkovLootSpawnEditor.ItemDatabase.SptRoot();
            var dir = root != null ? Path.Combine(root, "SPT_Runtime", "user", "mods", "CustomMapLoader", "maps") : null;
            if (dir != null && Directory.Exists(dir))
                maps.AddRange(Directory.GetDirectories(dir).Select(Path.GetFileName).Where(m => !maps.Contains(m)));
            return maps;
        }
    }

    // Tarkov SDK > Tools > Extracts > Extracts...: every extract and transit of the active scene, new ones of any kind, and
    // the selected one's rules
    public class TarkovExtractsWindow : EditorWindow
    {
        private Vector2 _scroll;
        private TarkovExtracts.Kind _newKind = TarkovExtracts.Kind.Regular;
        private bool _newAsTransit;
        private GameObject _carModel;
        private AudioClip _carIdle, _carStart;
        private readonly TarkovItemPicker.State _picker = new TarkovItemPicker.State();

        private static readonly (ERequirementState State, string Label, string Tip)[] Requirements =
        {
            (ERequirementState.None, "None", ""),
            (ERequirementState.TransferItem, "Pay an item (e.g. roubles)", "EXFIL_Item"),
            (ERequirementState.HasItem, "Carry an item", "EXFIL_Item"),
            (ERequirementState.Empty, "Empty slot (e.g. no backpack)", "EXFIL_tip_backpack"),
            (ERequirementState.WorldEvent, "World event (switch, power)", ""),
            (ERequirementState.ScavCooperation, "Scav cooperation", "EXFIL_Cooperate"),
            (ERequirementState.Reference, "Climbing gear (Alpinist)", ""),
        };

        [MenuItem("Tarkov SDK/Tools/Extracts/Extracts...", priority = 114)]
        private static void Open() => GetWindow<TarkovExtractsWindow>("Extracts").minSize = new Vector2(440f, 420f);

        private void OnSelectionChange() => Repaint();

        private void OnGUI()
        {
            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();
            var exfils = roots.SelectMany(r => r.GetComponentsInChildren<ExfiltrationPoint>(true)).ToList();
            var transits = roots.SelectMany(r => r.GetComponentsInChildren<TransitPoint>(true)).ToList();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("New", EditorStyles.boldLabel);
            int kindIndex = EditorGUILayout.Popup("Kind", Math.Max(0, Array.FindIndex(TarkovExtracts.Kinds, k => k.Kind == _newKind)),
                                                  TarkovExtracts.Kinds.Select(k => k.Label).ToArray());
            _newKind = TarkovExtracts.Kinds[kindIndex].Kind;
            EditorGUILayout.LabelField(TarkovExtracts.Kinds.First(k => k.Kind == _newKind).Help, EditorStyles.wordWrappedMiniLabel);
            if (_newKind == TarkovExtracts.Kind.Car)
            {
                _carModel = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Car Model", "Optional; its four wheels are the objects named like 'wheel'. Empty: a stand-in box."), _carModel, typeof(GameObject), false);
                _carIdle = (AudioClip)EditorGUILayout.ObjectField("Engine Idle Loop", _carIdle, typeof(AudioClip), false);
                _carStart = (AudioClip)EditorGUILayout.ObjectField("Drive Off Sound", _carStart, typeof(AudioClip), false);
            }
            bool elevator = _newKind >= TarkovExtracts.Kind.Elevator;
            using (new EditorGUI.DisabledScope(elevator))
                _newAsTransit = EditorGUILayout.Toggle(new GUIContent("As Transit", "The same zone takes the player to another map instead of ending the raid."), _newAsTransit && !elevator);
            if (GUILayout.Button("Create At Scene View Centre", GUILayout.Height(24f)))
            {
                var kind = _newKind;
                bool transit = _newAsTransit && !elevator;
                EditorApplication.delayCall += () => CreateNew(kind, transit);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"In {scene.name}: {exfils.Count} extract(s), {transits.Count} transit(s)", EditorStyles.boldLabel);
            foreach (var c in exfils.Cast<Component>().Concat(transits))
                using (new EditorGUILayout.HorizontalScope())
                {
                    var kind = TarkovExtracts.KindOf(c);
                    var name = c is ExfiltrationPoint e ? e.Settings?.Name ?? c.name : $"{c.name} -> {((TransitPoint)c).parameters?.location}";
                    bool selected = Selection.activeGameObject == c.gameObject;
                    EditorGUILayout.LabelField(name, selected ? EditorStyles.boldLabel : EditorStyles.label, GUILayout.MinWidth(120f));
                    EditorGUILayout.LabelField(TarkovExtracts.Label(kind), EditorStyles.miniLabel, GUILayout.Width(120f));
                    if (GUILayout.Button("Select", GUILayout.Width(52f)))
                    {
                        Selection.activeGameObject = c.gameObject;
                        SceneView.lastActiveSceneView?.FrameSelected();
                    }
                }

            EditorGUILayout.Space();
            var go = Selection.activeGameObject;
            var exfil = go != null ? go.GetComponent<ExfiltrationPoint>() : null;
            var transitPoint = go != null ? go.GetComponent<TransitPoint>() : null;
            if (exfil != null)
                DrawExtract(scene, exfil);
            else if (transitPoint != null)
                DrawTransit(transitPoint);
            else
                EditorGUILayout.HelpBox("Select an extract or transit to edit it here.", MessageType.None);
            EditorGUILayout.EndScrollView();
        }

        private void CreateNew(TarkovExtracts.Kind kind, bool transit)
        {
            var root = kind == TarkovExtracts.Kind.Car ? TarkovExtracts.CreateCar(null, _carModel, _carIdle, _carStart) : TarkovExtracts.Create(kind, null);
            if (SceneView.lastActiveSceneView != null)
                root.transform.position = SceneView.lastActiveSceneView.pivot;
            Undo.RegisterCreatedObjectUndo(root, "Create extract");
            var exfil = root.GetComponentInChildren<ExfiltrationPoint>();
            if (kind == TarkovExtracts.Kind.Car)
                TarkovExtracts.UpdateDrivePath(root.GetComponentInChildren<CarExtraction>());
            if (transit && exfil != null)
                Selection.activeGameObject = TarkovExtracts.ToTransit(exfil).gameObject;
            else
                Selection.activeGameObject = exfil != null ? exfil.gameObject : root;
        }

        private void DrawExtract(Scene scene, ExfiltrationPoint exfil)
        {
            var kind = TarkovExtracts.KindOf(exfil);
            EditorGUILayout.LabelField($"{TarkovExtracts.Label(kind)} extract", EditorStyles.boldLabel);
            exfil.Settings ??= new ExitTriggerSettings();
            var s = exfil.Settings;
            EditorGUI.BeginChangeCheck();
            Undo.RecordObject(exfil, "Edit extract");
            s.Name = EditorGUILayout.TextField(new GUIContent("Name", "Also the locale key of the name shown in the raid."), s.Name);
            s.Chance = EditorGUILayout.Slider(new GUIContent("Chance (%)", "On how many raids it's open."), s.Chance, 0f, 100f);
            s.ExfiltrationTime = Mathf.Max(0f, EditorGUILayout.FloatField("Exfil Time (s)", s.ExfiltrationTime));
            s.ExfiltrationType = (EExfiltrationType)EditorGUILayout.EnumPopup(new GUIContent("Timer", "Individual: each player's own. SharedTimer: starts for everyone once its requirement is met (car, co-op). Manual: started by a switch (elevator)."), s.ExfiltrationType);
            if (s.ExfiltrationType == EExfiltrationType.SharedTimer)
                s.PlayersCount = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent("Max Players", "0: any number."), s.PlayersCount));

            // entry points: the PMC spawns' infiltrations this extract serves
            var all = TarkovExtracts.EntryPoints(scene);
            var current = new HashSet<string>((s.EntryPoints ?? "").Split(',').Select(e => e.Trim()).Where(e => e.Length > 0), StringComparer.OrdinalIgnoreCase);
            EditorGUILayout.LabelField("Entry Points", EditorStyles.miniBoldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                foreach (var entry in all.Union(current, StringComparer.OrdinalIgnoreCase))
                {
                    bool on = EditorGUILayout.ToggleLeft(entry + (all.Contains(entry, StringComparer.OrdinalIgnoreCase) ? "" : " (no spawn uses it)"), current.Contains(entry));
                    if (on) current.Add(entry); else current.Remove(entry);
                }
                if (all.Count == 0)
                    EditorGUILayout.LabelField("No PMC spawn has an entry point (Infiltration) yet.", EditorStyles.miniLabel);
            }
            s.EntryPoints = string.Join(",", current);
            exfil.EligibleEntryPoints = current.Select(e => e.ToLowerInvariant()).ToArray();

            var req = exfil.Requirements != null && exfil.Requirements.Length > 0 ? exfil.Requirements[0] : null;
            int index = Array.FindIndex(Requirements, r => r.State == (req?.Requirement ?? ERequirementState.None));
            int next = EditorGUILayout.Popup("Requirement", Math.Max(0, index), Requirements.Select(r => r.Label).ToArray());
            if (next != index)
            {
                var r = Requirements[next];
                req = r.State == ERequirementState.None ? null : new ExfiltrationRequirement { Requirement = r.State, RequirementTip = r.Tip };
                if (r.State == ERequirementState.TransferItem) { req.Id = TarkovExtracts.Roubles; req.Count = 5000; }
                if (r.State == ERequirementState.Empty) req.RequiredSlot = EFT.InventoryLogic.EquipmentSlot.Backpack;
                if (r.State == ERequirementState.Reference) req.Id = "Alpinist";
                exfil.Requirements = req == null ? new ExfiltrationRequirement[0] : new[] { req };
            }
            if (EditorGUI.EndChangeCheck())
                EditorUtility.SetDirty(exfil);
            if (req != null)
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUI.BeginChangeCheck();
                    if (req.Requirement == ERequirementState.TransferItem || req.Requirement == ERequirementState.HasItem)
                    {
                        EditorGUILayout.LabelField("Item", TarkovItemPicker.Name(req.Id));
                        req.Count = Mathf.Max(1, EditorGUILayout.IntField("Count", req.Count));
                        var picked = TarkovItemPicker.Draw(_picker);
                        if (picked != null)
                        {
                            req.Id = picked;
                            GUI.changed = true;
                        }
                    }
                    if (req.Requirement == ERequirementState.Empty)
                        req.RequiredSlot = (EFT.InventoryLogic.EquipmentSlot)EditorGUILayout.EnumPopup("Slot", req.RequiredSlot);
                    req.RequirementTip = EditorGUILayout.TextField(new GUIContent("Tip (locale key)", "Shown while the requirement isn't met."), req.RequirementTip);
                    if (EditorGUI.EndChangeCheck())
                        EditorUtility.SetDirty(exfil);
                }

            if (kind == TarkovExtracts.Kind.Car)
            {
                var car = Object.FindObjectsOfType<CarExtraction>(true).First(c => c.Subscribee == exfil);
                EditorGUILayout.LabelField("Car", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField("Move the car and the Drive Path's points (under CarExtraction) in the Scene view; the car drives through them when the extract closes.",
                                           EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Select Drive Path") && car.transform.Find(TarkovExtracts.DrivePathName) != null)
                        Selection.activeTransform = car.transform.Find(TarkovExtracts.DrivePathName);
                    if (GUILayout.Button("Select Car") && car.Driver != null)
                        Selection.activeGameObject = car.Driver.gameObject;
                }
            }
            if (kind == TarkovExtracts.Kind.Flare)
                EditorGUILayout.HelpBox("A green flare fired in the flare zone (green box) makes the sniper zone hold fire for that player and their group.", MessageType.None);

            if (kind < TarkovExtracts.Kind.Elevator && GUILayout.Button("Make It A Transit"))
                EditorApplication.delayCall += () => Selection.activeGameObject = TarkovExtracts.ToTransit(exfil).gameObject;
        }

        private void DrawTransit(TransitPoint transit)
        {
            EditorGUILayout.LabelField("Transit", EditorStyles.boldLabel);
            var p = transit.parameters ??= new JsonType.LocationSettings.Location.TransitParameters();
            EditorGUI.BeginChangeCheck();
            Undo.RecordObject(transit, "Edit transit");
            var maps = TarkovExtracts.Destinations();
            int index = maps.FindIndex(m => string.Equals(m, p.location, StringComparison.OrdinalIgnoreCase));
            int next = EditorGUILayout.Popup(new GUIContent("Destination", "The map it leads to (custom maps installed in SPT are listed too)."), index, maps.ToArray());
            if (next != index && next >= 0)
                p.location = maps[next];
            p.location = EditorGUILayout.TextField("Destination Id", p.location);
            p.activateAfterSec = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent("Opens After (s)", "Seconds into the raid before it can be used."), p.activateAfterSec));
            p.time = (ushort)Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Transit Time (s)", "How long the player waits in the zone."), p.time), 1, ushort.MaxValue);
            p.active = EditorGUILayout.Toggle("Active", p.active);
            EditorGUILayout.LabelField($"Id {p.id}; name, description and conditions are locale keys ({p.name}); Server Data > Add Missing Locale Keys adds them.",
                                       EditorStyles.wordWrappedMiniLabel);
            if (EditorGUI.EndChangeCheck())
                EditorUtility.SetDirty(transit);
            if (GUILayout.Button("Make It An Extract"))
                EditorApplication.delayCall += () => Selection.activeGameObject = TarkovExtracts.ToExtract(transit).gameObject;
        }
    }

    public static class TarkovExtractGizmos
    {
        [DrawGizmo(GizmoType.Selected | GizmoType.InSelectionHierarchy | GizmoType.NonSelected)]
        private static void DrawCar(CarExtraction car, GizmoType type)
        {
            var path = car.transform.Find(TarkovExtracts.DrivePathName);
            if (path == null)
                return;
            var points = new List<Vector3>();
            if (car.Driver != null)
                points.Add(car.Driver.transform.position);
            points.AddRange(path.Cast<Transform>().Select(t => t.position));
            Handles.color = new Color(0.3f, 0.8f, 1f, (type & GizmoType.NonSelected) != 0 ? 0.4f : 1f);
            Handles.DrawAAPolyLine(3f, points.ToArray());
            foreach (var p in points.Skip(1))
                Handles.DrawWireDisc(p, Vector3.up, 0.6f);
        }
    }
}
