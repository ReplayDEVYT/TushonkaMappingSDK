using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EFT.Ballistics;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // Decompilation > Adjust Prefabs...: makes ripped (or any) prefabs work in a map without setting each up by hand.
    //
    // Repair: what the prefabs already have is completed the way the game needs it (what Fix Broken Interactables, Clean
    // Up Ripped Scene and Build Map do in a scene, done to the prefab assets): missing scripts and AmplifyImpostor
    // renderers removed, LODGroups tidied, mesh-less renderers and colliders removed, materials onto the SDK's stand-ins,
    // retail collider names given their layers (*_COLLIDER LowPoly, *_BALLISTIC_<material> HighPoly with that material),
    // BallisticColliders on HighPoly/Terrain colliders, Low + High colliders for prefabs with no collision at all, doors'
    // and switches' curves, containers' lists, positions and interaction boxes, lamps' lights and bulbs, light culling,
    // windows' glass, card readers, audio sources for Meta, trees' wind, and ids cleared so every copy gets its own.
    //
    // Rebuild by type: a prefab with no interactive object that its name and shape say is one gets the SDK's own, as the
    // Interactive Object Builder makes it, fitted to the model:
    //  - a door (named door, door-sized and thin): the SDK's door with the model as its leaf, hinged on its left edge
    //  - a lootable container (named after a retail type: crate, drawer, safe, toolbox...): that type's container
    //  - a lamp (has Lights, or named lamp/lantern/...): a LampController over its lights, shot out at its glass or bulb
    // Preview lists what each would get without saving anything.
    public class TarkovPrefabAdjuster : EditorWindow
    {
        private string _folder = "Assets/Ripped";
        private bool _repair = true, _doors = true, _containers = true, _lamps = true, _collision = true;
        private Vector2 _scroll;
        private readonly List<string> _report = new List<string>();

        [MenuItem("Tarkov SDK/Decompilation/Adjust Prefabs...", priority = 150)]
        private static void Open()
        {
            var window = GetWindow<TarkovPrefabAdjuster>("Adjust Prefabs");
            window.minSize = new Vector2(420f, 360f);
            var picked = Selection.activeObject != null ? AssetDatabase.GetAssetPath(Selection.activeObject) : null;
            if (!string.IsNullOrEmpty(picked) && AssetDatabase.IsValidFolder(picked))
                window._folder = picked;
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox("Makes the prefabs in a folder work in a map: completes the components they have the way the game needs them, " +
                                    "and turns doors, lootable containers and lamps that have lost (or never had) their game components into the SDK's working ones. " +
                                    "Preview first; Adjust saves the prefabs (Undo can't put them back, so keep a copy or commit first).", MessageType.None);
            using (new EditorGUILayout.HorizontalScope())
            {
                _folder = EditorGUILayout.TextField("Folder", _folder);
                if (GUILayout.Button("Selected", GUILayout.Width(70f)))
                {
                    var picked = Selection.activeObject != null ? AssetDatabase.GetAssetPath(Selection.activeObject) : null;
                    if (!string.IsNullOrEmpty(picked))
                        _folder = AssetDatabase.IsValidFolder(picked) ? picked : System.IO.Path.GetDirectoryName(picked).Replace('\\', '/');
                }
            }
            _repair = EditorGUILayout.ToggleLeft(new GUIContent("Repair what they have", "Missing scripts, LODs, materials, colliders, ballistics, interactable settings, lamps, windows, ids."), _repair);
            _collision = EditorGUILayout.ToggleLeft(new GUIContent("Give collision to prefabs with none", "Low poly box + high poly mesh per mesh (its nearest LOD), with a bullet material from its material's name."), _collision);
            EditorGUILayout.LabelField("Rebuild by type", EditorStyles.boldLabel);
            _doors = EditorGUILayout.ToggleLeft("Doors (named door, door-sized and thin)", _doors);
            _containers = EditorGUILayout.ToggleLeft("Lootable containers (named crate, drawer, safe, toolbox, bag, ...)", _containers);
            _lamps = EditorGUILayout.ToggleLeft("Lamps (prefabs with lights, or named lamp, lantern, ...)", _lamps);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Preview", GUILayout.Height(26f)))
                    EditorApplication.delayCall += () => Run(save: false);
                if (GUILayout.Button("Adjust", GUILayout.Height(26f)))
                    EditorApplication.delayCall += () =>
                    {
                        if (EditorUtility.DisplayDialog("Adjust Prefabs", $"Adjust and save every prefab in {_folder}? Undo can't put them back.", "Adjust", "Cancel"))
                            Run(save: true);
                    };
            }
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var line in _report.Take(500))
                EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);
            if (_report.Count > 500)
                EditorGUILayout.LabelField($"... and {_report.Count - 500} more (the Console has them all)", EditorStyles.miniLabel);
            EditorGUILayout.EndScrollView();
        }

        private void Run(bool save)
        {
            _report.Clear();
            if (!AssetDatabase.IsValidFolder(_folder))
            {
                EditorUtility.DisplayDialog("Adjust Prefabs", $"{_folder} isn't a folder in the project.", "OK");
                return;
            }
            var options = new Options { Repair = _repair, Collision = _collision, Doors = _doors, Containers = _containers, Lamps = _lamps };
            var paths = AssetDatabase.FindAssets("t:Prefab", new[] { _folder }).Select(AssetDatabase.GUIDToAssetPath).Distinct().ToList();
            int changed = 0;
            var materials = new HashSet<Material>();
            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    if (i % 10 == 0 && EditorUtility.DisplayCancelableProgressBar("Adjust Prefabs", paths[i], i / (float)Math.Max(1, paths.Count)))
                        break;
                    var notes = Adjust(paths[i], options, save, materials);
                    if (notes.Count == 0)
                        continue;
                    changed++;
                    _report.Add($"{System.IO.Path.GetFileNameWithoutExtension(paths[i])}: {string.Join("; ", notes)}");
                }
                // shared assets: once, after the prefabs
                if (options.Repair && save)
                {
                    int fixedMaterials = TarkovRipImport.FixMaterials(materials);
                    if (fixedMaterials > 0)
                        _report.Insert(0, $"{fixedMaterials} material(s) moved onto the SDK's shader stand-ins");
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            if (save)
                AssetDatabase.SaveAssets();
            _report.Insert(0, $"{(save ? "Adjusted" : "Would adjust")} {changed} of {paths.Count} prefab(s) in {_folder}.");
            Debug.Log("[Tarkov SDK] Adjust Prefabs:\n" + string.Join("\n", _report));
            Repaint();
        }

        public sealed class Options
        {
            public bool Repair = true, Collision = true, Doors = true, Containers = true, Lamps = true;
        }

        // ------------------------------------------------------------ one prefab

        // what the prefab got; saved only with `save` (else its contents are thrown away)
        public static List<string> Adjust(string path, Options options, bool save, HashSet<Material> materials)
        {
            var notes = new List<string>();
            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(path);
                if (options.Repair)
                    Repair(root, notes, materials);
                if (root.GetComponentInChildren<WorldInteractiveObject>(true) == null && root.GetComponentInChildren<LampController>(true) == null)
                {
                    string name = root.name;
                    if (options.Containers && ContainerType(name) is var type && type != null)
                        MakeContainer(root, type.Value, notes);
                    else if (options.Doors && LooksLikeDoor(root, name))
                        MakeDoor(root, notes);
                    else if (options.Lamps && (root.GetComponentInChildren<Light>(true) != null || IsLampName(name)))
                        MakeLamp(root, notes);
                }
                if (options.Collision && root.GetComponentsInChildren<Collider>(true).Length == 0)
                    GiveCollision(root, notes);
                if (save && notes.Count > 0)
                    PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            catch (Exception e)
            {
                notes.Add("failed: " + e.Message);
                Debug.LogWarning($"[Tarkov SDK] Adjust Prefabs: {path}: {e}");
            }
            finally
            {
                if (root != null)
                    PrefabUtility.UnloadPrefabContents(root);
            }
            return notes;
        }

        // ------------------------------------------------------------ repair

        private static readonly Regex BallisticName = new Regex(@"_BALLISTIC(?:_([A-Za-z]+))?", RegexOptions.IgnoreCase);

        private static void Repair(GameObject root, List<string> notes, HashSet<Material> materials)
        {
            int count;
            // missing scripts
            count = root.GetComponentsInChildren<Transform>(true).Sum(t => GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject));
            Note(notes, count, "missing script(s) removed");

            // AmplifyImpostor renderers (their shader isn't in the game) and LODs that point at nothing
            var impostors = root.GetComponentsInChildren<Renderer>(true).Where(IsImpostor).ToList();
            foreach (var r in impostors)
                Object.DestroyImmediate(r.gameObject == root ? (Object)r : r.gameObject);
            Note(notes, impostors.Count, "impostor renderer(s) removed");
            count = 0;
            foreach (var group in root.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = group.GetLODs();
                if (lods.All(l => l.renderers.All(r => r != null)))
                    continue;
                group.SetLODs(lods.Select(l => new LOD(l.screenRelativeTransitionHeight, l.renderers.Where(r => r != null).ToArray())).Where(l => l.renderers.Length > 0).ToArray());
                count++;
            }
            Note(notes, count, "LOD group(s) tidied");

            // renderers and mesh colliders without a mesh
            count = 0;
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh == null).ToList())
            {
                if (filter.TryGetComponent<MeshRenderer>(out var r))
                    Object.DestroyImmediate(r);
                Object.DestroyImmediate(filter);
                count++;
            }
            foreach (var mc in root.GetComponentsInChildren<MeshCollider>(true).Where(c => c.sharedMesh == null).ToList())
            {
                Object.DestroyImmediate(mc);
                count++;
            }
            Note(notes, count, "mesh-less renderer(s)/collider(s) removed");

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m != null)
                        materials.Add(m);

            // retail collider names: *_COLLIDER moves players, *_BALLISTIC_<material> takes bullets
            int lowPoly = LayerMask.NameToLayer("LowPolyCollider"), highPoly = LayerMask.NameToLayer("HighPolyCollider"), terrain = LayerMask.NameToLayer("Terrain");
            int layered = 0, ballistics = 0;
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider.isTrigger || collider.CompareTag("EditorOnly"))
                    continue;
                var go = collider.gameObject;
                var ballisticName = BallisticName.Match(go.name);
                if (go.layer == 0)
                {
                    if (ballisticName.Success)
                        go.layer = highPoly;
                    else if (go.name.IndexOf("_COLLIDER", StringComparison.OrdinalIgnoreCase) >= 0)
                        go.layer = lowPoly;
                    else
                        go.layer = terrain;
                    layered++;
                }
                if ((go.layer == highPoly || go.layer == terrain) && go.GetComponent<BaseBallistic>() == null)
                {
                    var renderer = go.GetComponentInParent<Renderer>();
                    var hint = ballisticName.Success && ballisticName.Groups[1].Success ? ballisticName.Groups[1].Value
                        : renderer != null && renderer.sharedMaterial != null ? renderer.sharedMaterial.name : go.name;
                    var (type, sound) = TarkovModelConverter.Surface(hint);
                    var ballistic = go.AddComponent<BallisticCollider>();
                    ballistic._typeOfMaterial = type;
                    ballistic.SurfaceSound = sound;
                    ballistics++;
                }
            }
            Note(notes, layered, "collider(s) on Default given their layer");
            Note(notes, ballistics, "BallisticCollider(s) added");

            RepairInteractables(root, notes);
            // a sync object's scene id is its path hash with this; Build Map gives each copy a new one
            foreach (var sync in root.GetComponentsInChildren<ISyncAble>(true))
                sync._random = 0;
            RepairLamps(root, notes);

            count = 0;
            foreach (var window in root.GetComponentsInChildren<WindowBreaker>(true))
            {
                if (!string.IsNullOrEmpty(window.Id))
                {
                    window.Id = "";
                    count++;
                }
                if (TarkovWindows.Setup(window, out var problem))
                    count++;
                else
                    notes.Add($"window '{window.name}' can't break ({problem})");
                window.gameObject.layer = LayerMask.NameToLayer("TransparentCollider");
            }
            Note(notes, count, "window(s) set up");
            count = root.GetComponentsInChildren<InteractiveProxy>(true).Count(TarkovKeycardDoors.RefreshProxy);
            Note(notes, count, "card reader(s) refreshed");
            count = root.GetComponentsInChildren<AudioSource>(true).Count(s => TarkovAudioSources.SetUp(s, undo: false));
            Note(notes, count, "audio source(s) set up for Meta XR Audio");
            if (root.GetComponentInChildren<LODGroup>(true) != null && (root.GetComponentInChildren<EFT.SpeedTree.TreeWind>(true) != null || IsTreeName(root.name))
                && TarkovTrees.EnsureWind(root))
                notes.Add("tree wind added");
        }

        private static void RepairInteractables(GameObject root, List<string> notes)
        {
            int interactive = LayerMask.NameToLayer("Interactive");
            foreach (var wio in root.GetComponentsInChildren<WorldInteractiveObject>(true))
            {
                var fixes = new List<string>();
                if (wio.gameObject.layer != interactive)
                {
                    wio.gameObject.layer = interactive;
                    fixes.Add("Interactive layer");
                }
                // Build Map gives every copy its own
                if (!string.IsNullOrEmpty(wio.Id))
                {
                    wio.Id = "";
                    fixes.Add("id cleared");
                }
                if (wio.GetComponent<Collider>() == null)
                {
                    // what the player's interaction ray finds; a door's is a trigger in front of its leaf
                    var box = wio.gameObject.AddComponent<BoxCollider>();
                    var b = LocalBounds(wio.transform);
                    box.center = b.center;
                    box.size = Vector3.Max(b.size, Vector3.one * 0.1f);
                    box.isTrigger = wio is Door;
                    fixes.Add("interaction box");
                }
                switch (wio)
                {
                    case Switch sw:
                        if (sw.CustomProgressCurve == null || sw.CustomProgressCurve.length == 0)
                        {
                            sw.CustomProgressCurve = TarkovPrefabTools.SwitchCurve();
                            fixes.Add("switch curve");
                        }
                        sw.NextSwitches ??= new Switch.SwitchAndOperation[0];
                        sw.ConditionStatus ??= new EExfiltrationStatus[0];
                        sw.Lamps = (sw.Lamps ?? new LampController[0]).Where(l => l != null).ToArray();
                        break;
                    case Door door:
                        if (door.KickCurve == null || door.KickCurve.length == 0)
                        {
                            door.KickCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
                            fixes.Add("kick curve");
                        }
                        if (door._handle == null && door.GetComponentInChildren<DoorHandle>(true) is DoorHandle handle)
                        {
                            door._handle = handle;
                            fixes.Add("handle linked");
                        }
                        break;
                    case LootableContainer container:
                        if (string.IsNullOrEmpty(container.Template))
                        {
                            var type = ContainerType(container.name) ?? ContainerType(root.name);
                            if (type != null)
                            {
                                container.Template = type.Value.Template;
                                fixes.Add($"type {type.Value.Name}");
                            }
                            else
                                notes.Add($"container '{container.name}' has no type (set its Template)");
                        }
                        container.FilterExtended ??= new List<LootableContainerParameters>();
                        container.GameObjectsToDestroy ??= new List<GameObject>();
                        if (container.interactPosition1 == Vector3.zero && container.viewTarget1 == Vector3.zero)
                        {
                            var b = LocalBounds(container.transform);
                            container.interactPosition1 = new Vector3(b.center.x, 0f, b.min.z - 0.6f);
                            container.viewTarget1 = b.center;
                            fixes.Add("search position");
                        }
                        if (TarkovPrefabTools.KeepContainerInPlace(container))
                            fixes.Add("stays in place when opened");
                        if (container.GetComponent<Collider>() is BoxCollider box2 && TarkovPrefabTools.EncloseHighPoly(box2))
                            fixes.Add("interaction box clear of its HighPoly");
                        break;
                }
                if (fixes.Count > 0)
                    notes.Add($"{wio.GetType().Name} '{wio.name}': {string.Join(", ", fixes)}");
            }
        }

        private static void RepairLamps(GameObject root, List<string> notes)
        {
            foreach (var lamp in root.GetComponentsInChildren<LampController>(true))
            {
                var fixes = new List<string>();
                if (!string.IsNullOrEmpty(lamp.Id))
                {
                    lamp.Id = "";
                    fixes.Add("id cleared");
                }
                lamp.Lights = (lamp.Lights ?? new Light[0]).Where(l => l != null).ToArray();
                if (lamp.Lights.Length == 0 && (lamp.OnObjects == null || lamp.OnObjects.Length == 0))
                {
                    lamp.Lights = lamp.GetComponentsInChildren<Light>(true);
                    if (lamp.Lights.Length > 0)
                        fixes.Add($"{lamp.Lights.Length} light(s) linked");
                }
                lamp.OnObjects = (lamp.OnObjects ?? new GameObject[0]).Where(o => o != null).ToArray();
                lamp.OffObjects = (lamp.OffObjects ?? new GameObject[0]).Where(o => o != null).ToArray();
                lamp.DestroyedObjects = (lamp.DestroyedObjects ?? new GameObject[0]).Where(o => o != null).ToArray();
                lamp.TurnOnClips = (lamp.TurnOnClips ?? new AudioClip[0]).Where(c => c != null).ToArray();
                lamp._materialsWithEmission ??= new MaterialEmission[0];
                lamp._legacyMaterialsWithEmission ??= new MaterialColor[0];
                lamp._animationCurve ??= new AnimationCurve();
                if (lamp.BallisticCollider == null)
                {
                    lamp.BallisticCollider = Bulb(lamp.transform, lamp.Lights.FirstOrDefault());
                    fixes.Add("bulb to shoot out");
                }
                foreach (var light in lamp.Lights)
                    if (EnsureCulling(light))
                        fixes.Add($"culling on '{light.name}'");
                if (fixes.Count > 0)
                    notes.Add($"lamp '{lamp.name}': {string.Join(", ", fixes)}");
            }
            foreach (var culling in root.GetComponentsInChildren<CullingLightObject>(true))
                if (culling._light == null && culling.TryGetComponent<Light>(out var light))
                {
                    culling._light = light;
                    culling._transform = culling.transform;
                    notes.Add($"light culling on '{culling.name}' relinked");
                }
        }

        // ------------------------------------------------------------ rebuild by type

        // the retail container types whose loot pools the server has, by name; checked in order (a "weapon crate" is a weapon box)
        private static readonly (Regex Name, string Type, string Template)[] ContainerNames =
        {
            (Word("weapon_?(box|crate|case)|weaponbox|gun_?(box|case)"), "Weapon box (6x3)", "5909d76c86f77471e53d2adf"),
            (Word("grenade_?(box|crate|case)"), "Grenade box", "5909d36d86f774660f0bb900"),
            (Word("med_?(bag|box|case|kit)|medbag|medical_?(bag|box)"), "Medbag SMU06", "5909d24f86f77466f56e6855"),
            (Word("tool_?box|toolbox|tool_?case"), "Toolbox", "5909d50c86f774659e6aaebe"),
            (Word("safe|strongbox|money_?box"), "Safe", "578f8782245977354405a1e3"),
            (Word("technical_?(supply|crate)|tech_?crate|supply_?crate"), "Technical supply crate", "5d6fd45b86f774317075ed43"),
            (Word("drawer|drawers|nightstand|bedside"), "Drawer", "578f87b7245977356274f2cd"),
            (Word("jacket|coat"), "Jacket", "578f8778245977358849a9b5"),
            (Word("duffle|duffel|sport_?bag|bag"), "Duffle bag", "578f87a3245977356274f2cb"),
            (Word("crate|wooden_?box|ammo_?box|box_?wood"), "Wooden crate", TarkovPrefabTools.WoodenCrateTemplate),
        };

        // a whole word in an object name: between start/end, separators or digits (Box_01, wooden_crate, CrateA)
        private static Regex Word(string pattern) =>
            new Regex($@"(^|[_\-\s\d.(])({pattern})([_\-\s\d.)]|[A-Z]|$)", RegexOptions.IgnoreCase);

        private static (string Name, string Template)? ContainerType(string name)
        {
            foreach (var (regex, type, template) in ContainerNames)
                if (regex.IsMatch(name))
                    return (type, template);
            return null;
        }

        private static readonly Regex DoorName = Word("door|doors");
        private static readonly Regex NotDoorName = new Regex("frame|jamb|handle|knob|lock|hinge|sign|decal|plate|garage|gate|trim|portal|shadow|collider|ballistic|broken|_static", RegexOptions.IgnoreCase);
        private static readonly Regex LampName = Word("lamp|lantern|chandelier|bulb|light_?fixture|ceiling_?light|wall_?light|street_?light|spotlight|projector|sconce");
        private static readonly Regex TreeName = Word("tree|pine|fir|spruce|birch|oak|maple|willow|larch|cedar|bush");

        private static bool IsLampName(string name) => LampName.IsMatch(name);
        private static bool IsTreeName(string name) => TreeName.IsMatch(name);

        private static bool LooksLikeDoor(GameObject root, string name)
        {
            if (!DoorName.IsMatch(name) || NotDoorName.IsMatch(name))
                return false;
            var b = LocalBounds(root.transform);
            float width = Mathf.Max(b.size.x, b.size.z), thickness = Mathf.Min(b.size.x, b.size.z);
            return width >= 0.5f && width <= 2.6f && b.size.y >= 1.6f && b.size.y <= 3.3f && thickness <= 0.35f;
        }

        // the SDK's door (TarkovPrefabTools.CreateDoor) at the model's bottom middle, turned so its leaf lies along the
        // model's wide side, with the model as the leaf (hinged on its left edge); the model's colliders go, the door's boxes
        // take bullets with a material from the model's
        private static void MakeDoor(GameObject root, List<string> notes)
        {
            var b = LocalBounds(root.transform);
            bool alongZ = b.size.z > b.size.x;
            float width = alongZ ? b.size.z : b.size.x, thickness = Mathf.Max(alongZ ? b.size.x : b.size.z, 0.03f);
            var model = DetachModel(root);
            var door = TarkovPrefabTools.CreateDoor(root.transform, width, b.size.y, thickness);
            var doorRoot = door.transform.parent;
            doorRoot.localPosition = new Vector3(b.center.x, b.min.y, b.center.z);
            doorRoot.localRotation = alongZ ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
            Object.DestroyImmediate(door.transform.Find("Panel (replace with your model)").gameObject);
            RemoveColliders(model);
            model.transform.SetParent(door.transform, true);
            SetSurface(door.transform.Find("HighPoly").gameObject, model);
            notes.Add($"made a door ({width:0.00} x {b.size.y:0.00} m, hinged on its left; check the hinge side and swing)");
        }

        // a LootableContainer of the type, on its own child (the root keeps its layer), boxed round the model
        private static void MakeContainer(GameObject root, (string Name, string Template) type, List<string> notes)
        {
            var b = LocalBounds(root.transform);
            var go = new GameObject("LootableContainer") { layer = LayerMask.NameToLayer("Interactive") };
            go.transform.SetParent(root.transform, false);
            var box = go.AddComponent<BoxCollider>();
            box.center = b.center;
            box.size = b.size + Vector3.one * (2f * TarkovPrefabTools.InteractMargin);
            // what bullets hit, when the model has nothing on HighPoly
            if (!root.GetComponentsInChildren<Collider>(true).Any(c => c.gameObject.layer == LayerMask.NameToLayer("HighPolyCollider") && c.GetComponent<BaseBallistic>() != null))
            {
                var high = new GameObject("HighPoly") { layer = LayerMask.NameToLayer("HighPolyCollider") };
                high.transform.SetParent(go.transform, false);
                var hb = high.AddComponent<BoxCollider>();
                hb.center = b.center;
                hb.size = b.size;
                SetSurface(high, root);
            }
            var container = go.AddComponent<LootableContainer>();
            container.Template = type.Template;
            container.SpawnType = ELootableContainerSpawnType.CanBeEmpty;
            container.SpawnChance = 100;
            container._openInteraction = EInteraction.ContainerOpenDefault;
            container._closeInteraction = EInteraction.ContainerCloseDefault;
            container._doorState = EDoorState.Shut;
            container.Snap = EDoorState.Shut | EDoorState.Open | EDoorState.Interacting;
            container.Operatable = true;
            container.interactPosition1 = new Vector3(b.center.x, 0f, b.min.z - 0.6f);
            container.viewTarget1 = b.center;
            container.FilterExtended = new List<LootableContainerParameters>();
            container.GameObjectsToDestroy = new List<GameObject>();
            TarkovPrefabTools.EncloseHighPoly(box);
            notes.Add($"made a lootable container ({type.Name}, searched from its -Z side)");
        }

        // a LampController over the prefab's lights (or a new one at the model's middle), shot out at its glass or a bulb
        private static void MakeLamp(GameObject root, List<string> notes)
        {
            var lights = root.GetComponentsInChildren<Light>(true).Where(l => l.type != LightType.Directional).ToArray();
            bool added = false;
            if (lights.Length == 0)
            {
                var b = LocalBounds(root.transform);
                var light = TarkovLights.CreateLight(root.transform, LightType.Point);
                light.transform.localPosition = b.center;
                lights = new[] { light };
                added = true;
            }
            var lamp = root.AddComponent<LampController>();
            lamp.Lights = lights;
            lamp.LampState = Turnable.EState.On;
            lamp._animationCurve = new AnimationCurve();
            lamp._materialsWithEmission = new MaterialEmission[0];
            lamp._legacyMaterialsWithEmission = new MaterialColor[0];
            lamp.OnObjects = new GameObject[0];
            lamp.OffObjects = new GameObject[0];
            lamp.DestroyedObjects = new GameObject[0];
            lamp.TurnOnClips = new AudioClip[0];
            var glass = root.GetComponentsInChildren<BallisticCollider>(true).FirstOrDefault(c => c._typeOfMaterial == MaterialType.Glass);
            lamp.BallisticCollider = glass != null ? glass : Bulb(root.transform, lights[0]);
            foreach (var light in lights)
                EnsureCulling(light);
            notes.Add(added ? "made a lamp with a new light at its middle (set its colour, range and position)" : $"made a lamp over its {lights.Length} light(s)");
        }

        // ------------------------------------------------------------ helpers

        private static void Note(List<string> notes, int count, string what)
        {
            if (count > 0)
                notes.Add($"{count} {what}");
        }

        private static bool IsImpostor(Renderer r) =>
            r.gameObject.name.IndexOf("impostor", StringComparison.OrdinalIgnoreCase) >= 0
            || r.sharedMaterials.Any(m => m != null && m.shader != null && m.shader.name.IndexOf("Impostor", StringComparison.OrdinalIgnoreCase) >= 0);

        // the renderers' bounds in t's space
        private static Bounds LocalBounds(Transform t)
        {
            var renderers = t.GetComponentsInChildren<Renderer>(true).Where(r => !(r is ParticleSystemRenderer)).ToList();
            if (renderers.Count == 0)
                return new Bounds(Vector3.zero, Vector3.one * 0.3f);
            bool first = true;
            var result = new Bounds();
            foreach (var r in renderers)
            {
                var wb = r.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = wb.center + Vector3.Scale(wb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var local = t.InverseTransformPoint(corner);
                    if (first)
                    {
                        result = new Bounds(local, Vector3.zero);
                        first = false;
                    }
                    else
                        result.Encapsulate(local);
                }
            }
            return result;
        }

        // everything the root shows, gathered under a "Model" child (the root's own mesh copied onto it), so it can move
        private static GameObject DetachModel(GameObject root)
        {
            var model = new GameObject("Model");
            model.transform.SetParent(root.transform, false);
            foreach (var child in root.transform.Cast<Transform>().Where(c => c != model.transform).ToList())
                child.SetParent(model.transform, true);
            if (root.TryGetComponent<MeshFilter>(out var filter) && root.TryGetComponent<MeshRenderer>(out var renderer))
            {
                var own = new GameObject(root.name + "_Mesh");
                own.transform.SetParent(model.transform, false);
                own.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var copy = own.AddComponent<MeshRenderer>();
                copy.sharedMaterials = renderer.sharedMaterials;
                copy.shadowCastingMode = renderer.shadowCastingMode;
                var lod = root.GetComponent<LODGroup>();
                if (lod != null)
                    lod.SetLODs(lod.GetLODs().Select(l => new LOD(l.screenRelativeTransitionHeight, l.renderers.Select(r => r == renderer ? copy : r).ToArray())).ToArray());
                Object.DestroyImmediate(renderer);
                Object.DestroyImmediate(filter);
            }
            foreach (var c in root.GetComponents<Collider>())
                Object.DestroyImmediate(c);
            return model;
        }

        private static void RemoveColliders(GameObject go)
        {
            foreach (var b in go.GetComponentsInChildren<BaseBallistic>(true))
                Object.DestroyImmediate(b);
            foreach (var c in go.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(c);
        }

        // the bullet material and impact sound of the model's main material
        private static void SetSurface(GameObject collider, GameObject model)
        {
            var material = model.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).FirstOrDefault(m => m != null);
            var (type, sound) = TarkovModelConverter.Surface(material != null ? material.name : model.name);
            var ballistic = collider.GetComponent<BallisticCollider>();
            if (ballistic == null) // Unity's ==: a missing component can be a "fake null"
                ballistic = collider.AddComponent<BallisticCollider>();
            ballistic._typeOfMaterial = type;
            ballistic.SurfaceSound = sound;
        }

        // a small glass sphere at the light, on the bullets' layer, for the lamp to be shot out at
        private static BallisticCollider Bulb(Transform parent, Light light)
        {
            var bulb = new GameObject("Bulb") { layer = LayerMask.NameToLayer("HighPolyCollider") };
            bulb.transform.SetParent(parent, false);
            if (light != null)
                bulb.transform.position = light.transform.position;
            bulb.AddComponent<SphereCollider>().radius = 0.08f;
            var ballistic = bulb.AddComponent<BallisticCollider>();
            ballistic._typeOfMaterial = MaterialType.Glass;
            ballistic.SurfaceSound = BaseBallistic.ESurfaceSound.Glass;
            return ballistic;
        }

        // the CullingLightObject retail lights carry (TarkovLights.CreateLight's values)
        private static bool EnsureCulling(Light light)
        {
            if (light == null || light.GetComponent<CullingLightObject>() != null)
                return false;
            TarkovLights.ConfigureCulling(light.gameObject.AddComponent<CullingLightObject>(), light);
            return true;
        }

        // Low + High colliders for each mesh of the nearest LOD (or every mesh without LODs), box low poly
        private static void GiveCollision(GameObject root, List<string> notes)
        {
            var group = root.GetComponentInChildren<LODGroup>(true);
            var renderers = group != null && group.GetLODs().Length > 0
                ? group.GetLODs()[0].renderers.Where(r => r != null).ToList()
                : root.GetComponentsInChildren<MeshRenderer>(true).Cast<Renderer>().ToList();
            var b = LocalBounds(root.transform);
            // skip tiny things (decals, debris) that don't need collision
            if (b.size.magnitude < 0.3f)
                return;
            int count = renderers.Select(r => r.GetComponent<MeshFilter>()).Count(f => f != null && TarkovPrefabTools.AddLowHigh(f, boxLowPoly: true));
            Note(notes, count, "mesh(es) given low + high poly colliders");
        }
    }
}
