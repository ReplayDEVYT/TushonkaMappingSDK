using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommonAssets.Scripts.Game.GameTriggers.Handlers;
using CommonAssets.Scripts.Game.LabyrinthEvent;
using EFT;
using EFT.Ballistics;
using EFT.GameTriggers;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // Labyrinth's traps, from the game's own components with Labyrinth's values:
    //
    //  - Barbed Wire: a trigger box with BarbedWire (a DamageTrigger): inside it a player is slowed to 15 %,
    //    can't sprint, and the body parts in it get cut (BodyPartCollider.ProceedBarb). Its sound is a
    //    SoundBank (retail: rolloff 35, Environment group); the game's own bank isn't loaded on SDK maps.
    //  - Spike Trap: TriggerZone MoveInside (every metre moved) -> HandlerDamage (calves 5, thighs 2) and
    //    HandlerEffect (25 % intoxication); an ObstacleCollider stops prone, jumping and sprinting and slows.
    //  - Flame Trap: TriggerZone Enter / Exit -> HandlerDamage every 0.5 s while inside (10 to calves, pelvis,
    //    chest and upper arms, Labyrinth's "soft" zone); the Effects child holds the fire.
    //  - Trap Door: a hole cover with a TrapSyncable (TrapDoor). With Random Traps on (base.json
    //    EventTrapsData) LabyrinthSyncableTraps removes some covers each raid.
    //  - Random Per Raid: a TrapSyncable (BarbedWire) on a wire or spike trap lets LabyrinthSyncableTraps
    //    pick which of them exist each raid; GameObjectStateSync keeps every player's in step.
    //  - Trap Switch: a Switch whose Triggers Map turns traps off, as Labyrinth's "disable_traps" buttons:
    //    HandlerTriggerState makes the emitter ignore the traps' start ids (TriggersEmitter.SetTriggerState),
    //    HandlerGameObjectState hides their Effects, and their stop ids are sent too so damage already
    //    running ends. Shutting it again turns them back on.
    public static class TarkovTraps
    {
        private const string Menu = "GameObject/Tarkov SDK/Hazards/";
        // a trap's visuals that a trap switch hides
        public const string EffectsName = "Effects";

        [MenuItem(Menu + "Barbed Wire", priority = 33)]
        private static void MenuWire(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateBarbedWire(p).gameObject);

        [MenuItem(Menu + "Spike Trap", priority = 34)]
        private static void MenuSpikes(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateSpikeTrap(p));

        [MenuItem(Menu + "Flame Trap", priority = 35)]
        private static void MenuFlames(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateFlameTrap(p));

        [MenuItem(Menu + "Trap Door", priority = 36)]
        private static void MenuTrapDoor(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateTrapDoor(p).gameObject);

        // no MenuCommand: Unity calls those once per selected object, and this makes one switch for all of them
        [MenuItem(Menu + "Trap Switch (For Selected Traps)", priority = 37)]
        private static void MenuTrapSwitch()
        {
            var traps = Selection.gameObjects.Where(IsSwitchable).ToList();
            var sw = TarkovPrefabTools.CreateSwitch(null, null);
            sw.name = "TrapSwitch";
            sw.ContextMenuTip = "Use";
            sw.transform.position = traps.Count > 0 ? traps[0].transform.position + Vector3.up * 1.2f
                : SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.pivot : Vector3.zero;
            Undo.RegisterCreatedObjectUndo(sw.gameObject, "Create trap switch");
            if (traps.Count > 0)
            {
                ConnectSwitch(sw, traps, canTurnBackOn: true);
                Debug.Log($"[Tarkov SDK] Trap switch turns off {string.Join(", ", traps.Select(t => t.name))} when pressed, and back on when pressed again.");
            }
            else
            {
                Debug.Log("[Tarkov SDK] Select spike or flame traps first to have the switch turn them off; or connect it later in Tarkov SDK > Tools > Interactables > Triggers & Sounds (Turn traps off).");
            }
            Selection.activeGameObject = sw.gameObject;
        }

        private static Scene SceneOf(Transform parent) => parent != null ? parent.gameObject.scene : SceneManager.GetActiveScene();

        private static string NewId(string what, string name) => $"{name.Replace(' ', '_')}_{what}_{TarkovFacilitySystems.ShortId()}";

        // ------------------------------------------------------------ barbed wire

        public static BarbedWire CreateBarbedWire(Transform parent)
        {
            var go = TarkovFacilitySystems.Child(parent, "BarbedWire", Vector3.zero, "Triggers");
            // retail's is 0.38 x 0.29 x 1.87 m around the coil
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.center = new Vector3(0f, 0.25f, 0f);
            box.size = new Vector3(0.5f, 0.5f, 2f);
            var wire = go.AddComponent<BarbedWire>();
            wire._soundBank = WireBank(SceneOf(parent));

            var visual = TarkovFacilitySystems.Child(go.transform, "Wire (replace with your model)", Vector3.zero);
            foreach (float height in new[] { 0.15f, 0.35f })
            {
                var strand = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                strand.name = "Strand";
                Object.DestroyImmediate(strand.GetComponent<Collider>());
                strand.transform.SetParent(visual.transform, false);
                strand.transform.localPosition = new Vector3(0f, height, 0f);
                strand.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                strand.transform.localScale = new Vector3(0.02f, 1f, 0.02f);
                var material = TarkovSceneTools.DefaultSurfaceMaterial();
                if (material != null)
                    strand.GetComponent<MeshRenderer>().sharedMaterial = material;
            }
            return wire;
        }

        // one bank per scene, shared by its wires; its clips are set in any wire's inspector
        public static SoundBank WireBank(Scene scene)
        {
            var folder = Path.Combine(Path.GetDirectoryName(scene.path) ?? "Assets", "SoundBanks").Replace('\\', '/');
            var existing = AssetDatabase.LoadAssetAtPath<SoundBank>($"{folder}/BarbedWire.asset");
            if (existing != null)
                return existing;
            var bank = TarkovRandomSoundEvents.CreateBank(scene, "BarbedWire");
            bank.Rolloff = 35f;
            bank.SourceType = BetterAudio.AudioSourceGroupType.Environment;
            EditorUtility.SetDirty(bank);
            return bank;
        }

        // ------------------------------------------------------------ spike trap

        public static GameObject CreateSpikeTrap(Transform parent)
        {
            var root = TarkovFacilitySystems.Child(parent, "SpikeTrap", Vector3.zero);
            var zone = TarkovFacilitySystems.Zone(root.transform, "TriggerZone", new Vector3(0f, 0.5f, 0f), new Vector3(2f, 1f, 2f));
            string move = NewId("move", root.name), exit = NewId("exit", root.name);
            var moving = TarkovFacilitySystems.AddZone(zone, TriggerZone.ETriggerZoneMode.MoveInside, move);
            moving._moveDistanceToTrigger = 1f;
            moving._triggerTag = TriggerZone.ETriggerTag.Trap;
            TarkovFacilitySystems.AddZone(zone, TriggerZone.ETriggerZoneMode.Exit, exit)._triggerTag = TriggerZone.ETriggerTag.Trap;

            var damage = zone.AddComponent<HandlerDamage>();
            damage._triggerIdStartDamage = move;
            damage._triggerIdFinishDamage = exit;
            damage._damage = Damage(5f, EBodyPartColliderType.LeftCalf, EBodyPartColliderType.RightCalf)
                .Concat(Damage(2f, EBodyPartColliderType.LeftThigh, EBodyPartColliderType.RightThigh)).ToArray();
            damage._damageCount = 1;
            damage._damageSecondsInterval = -1f;
            damage._configurationId = "";

            var effect = zone.AddComponent<HandlerEffect>();
            effect._chance = 0.25f;
            effect._triggerId = move;
            var effects = EmptyEffects();
            effects.Intoxication = new[] { new HandlerEffect.IntoxicationApplier { _ = true } };
            effect._effects = effects;

            var obstacle = zone.AddComponent<ObstacleCollider>();
            obstacle._disablesProne = true;
            obstacle._disablesProneMove = false;
            obstacle._disablesJump = true;
            obstacle._disablesSprint = true;
            obstacle._hasSwampSpeedLimit = true;
            obstacle._botPolitic = ObstacleCollider.EBotPolitic.Ignore;

            // the spikes are what a trap switch retracts
            var visual = TarkovFacilitySystems.Child(root.transform, EffectsName, Vector3.zero);
            var material = TarkovSceneTools.DefaultSurfaceMaterial();
            for (int x = 0; x < 4; x++)
                for (int z = 0; z < 4; z++)
                {
                    var spike = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    spike.name = "Spike (replace with your model)";
                    Object.DestroyImmediate(spike.GetComponent<Collider>());
                    spike.transform.SetParent(visual.transform, false);
                    spike.transform.localPosition = new Vector3(-0.75f + x * 0.5f, 0.1f, -0.75f + z * 0.5f);
                    spike.transform.localScale = new Vector3(0.04f, 0.1f, 0.04f);
                    if (material != null)
                        spike.GetComponent<MeshRenderer>().sharedMaterial = material;
                }
            return root;
        }

        // ------------------------------------------------------------ flame trap

        public static GameObject CreateFlameTrap(Transform parent)
        {
            var root = TarkovFacilitySystems.Child(parent, "FlameTrap", Vector3.zero);
            var zone = TarkovFacilitySystems.Zone(root.transform, "FireZone", new Vector3(0f, 1f, 0f), new Vector3(1.5f, 2f, 3f));
            string enter = NewId("enter", root.name), exit = NewId("exit", root.name);
            // bots walk through, as on Labyrinth
            var entering = TarkovFacilitySystems.AddZone(zone, TriggerZone.ETriggerZoneMode.Enter, enter);
            entering._triggerTag = TriggerZone.ETriggerTag.Trap;
            entering._botPolitic = TriggerZone.EBotPolitic.Ignore;
            var leaving = TarkovFacilitySystems.AddZone(zone, TriggerZone.ETriggerZoneMode.Exit, exit);
            leaving._triggerTag = TriggerZone.ETriggerTag.Trap;
            leaving._botPolitic = TriggerZone.EBotPolitic.Ignore;

            var damage = zone.AddComponent<HandlerDamage>();
            damage._triggerIdStartDamage = enter;
            damage._triggerIdFinishDamage = exit;
            damage._damage = Damage(10f, EBodyPartColliderType.LeftCalf, EBodyPartColliderType.RightCalf, EBodyPartColliderType.Pelvis,
                                    EBodyPartColliderType.RibcageUp, EBodyPartColliderType.RightUpperArm, EBodyPartColliderType.LeftUpperArm).ToArray();
            damage._damageCount = -1;
            damage._damageSecondsInterval = 0.5f;
            damage._configurationId = "";

            var effects = TarkovFacilitySystems.Child(root.transform, EffectsName, Vector3.zero);
            var light = TarkovLights.CreateLight(effects.transform, LightType.Point, "Fire Light (add your fire effect here)");
            light.transform.localPosition = new Vector3(0f, 1f, 0f);
            light.color = new Color(1f, 0.55f, 0.2f);
            light.range = 6f;
            light.intensity = 2f;
            light.shadows = LightShadows.None;
            var culling = light.GetComponent<CullingLightObject>();
            culling._maxLightIntensity = light.intensity;
            culling._initialShadowsMode = light.shadows;
            return root;
        }

        // ------------------------------------------------------------ trap door

        public static TrapSyncable CreateTrapDoor(Transform parent)
        {
            var root = TarkovFacilitySystems.Child(parent, "TrapDoor", Vector3.zero);
            var cover = TarkovFacilitySystems.Panel(root.transform, "Cover", new Vector3(0f, -0.08f, 0f), new Vector3(1.5f, 0.08f, 1.5f), MaterialType.WoodThick);
            var ballistic = cover.GetComponentInChildren<BallisticCollider>();
            ballistic.SurfaceSound = BaseBallistic.ESurfaceSound.Wood;
            var trap = root.AddComponent<TrapSyncable>();
            trap.TrapType = ETrapType.TrapDoor;
            return trap;
        }

        // ------------------------------------------------------------ random per raid

        public static bool IsRandom(GameObject trap) => trap.GetComponent<TrapSyncable>() != null;

        public static void SetRandom(GameObject trap, bool random)
        {
            var existing = trap.GetComponent<TrapSyncable>();
            if (random && existing == null)
                Undo.AddComponent<TrapSyncable>(trap).TrapType = ETrapType.BarbedWire;
            else if (!random && existing != null)
                Undo.DestroyObjectImmediate(existing);
        }

        // ------------------------------------------------------------ switching traps

        // what a trap switch needs from a trap: the ids that start it, the ids that stop damage already
        // running, and its visuals
        public static void Parts(GameObject trap, List<string> start, List<string> stop, List<GameObject> effects)
        {
            foreach (var damage in trap.GetComponentsInChildren<HandlerDamage>(true))
            {
                AddId(start, damage._triggerIdStartDamage);
                AddId(stop, damage._triggerIdFinishDamage);
            }
            foreach (var effect in trap.GetComponentsInChildren<HandlerEffect>(true))
                AddId(start, effect._triggerId);
            foreach (var t in trap.GetComponentsInChildren<Transform>(true))
                if (t.name == EffectsName && !effects.Contains(t.gameObject))
                    effects.Add(t.gameObject);
        }

        private static void AddId(List<string> ids, string id)
        {
            if (!string.IsNullOrEmpty(id) && !ids.Contains(id))
                ids.Add(id);
        }

        public static bool IsSwitchable(GameObject trap) =>
            trap.GetComponentInChildren<HandlerDamage>(true) != null || trap.GetComponentInChildren<HandlerEffect>(true) != null;

        // handlers under parent that, on triggerId, turn the traps off (or on again)
        public static GameObject AddToggle(Transform parent, string triggerId, IEnumerable<GameObject> traps, bool on)
        {
            var start = new List<string>();
            var stop = new List<string>();
            var effects = new List<GameObject>();
            foreach (var trap in traps.Where(t => t != null))
                Parts(trap, start, stop, effects);
            var go = TarkovFacilitySystems.Child(parent, on ? "Traps On" : "Traps Off", Vector3.zero);
            Undo.RegisterCreatedObjectUndo(go, "Add trap toggle");
            var state = go.AddComponent<HandlerTriggerState>();
            state._triggerId = triggerId;
            state._controlledTriggerId = start.ToArray();
            state._targetState = on;
            if (effects.Count > 0)
            {
                var visuals = go.AddComponent<HandlerGameObjectState>();
                visuals._triggerId = triggerId;
                visuals._controlledGameObjects = effects.ToArray();
                visuals._targetState = on;
                visuals._resetStateOnAwake = false;
            }
            return go;
        }

        // the stop ids a switch sends with its "off" trigger, so a trap already hurting someone stops
        public static void SendStops(WorldInteractiveObject wio, EDoorState state, IEnumerable<GameObject> traps)
        {
            var start = new List<string>();
            var stop = new List<string>();
            var effects = new List<GameObject>();
            foreach (var trap in traps.Where(t => t != null))
                Parts(trap, start, stop, effects);
            var map = (wio.TriggersMap ?? new WorldInteractiveObject.TriggerOnStatePair[0]).ToList();
            Undo.RecordObject(wio, "Send trap stops");
            foreach (var id in stop)
                if (!map.Any(p => p.State == state && p.TriggerId == id))
                    map.Add(new WorldInteractiveObject.TriggerOnStatePair { State = state, TriggerId = id });
            wio.TriggersMap = map.ToArray();
            EditorUtility.SetDirty(wio);
        }

        public static void ConnectSwitch(Switch sw, List<GameObject> traps, bool canTurnBackOn)
        {
            AddToggle(sw.transform, TarkovTriggerWiringWindow.StateId(sw, EDoorState.Open), traps, on: false);
            SendStops(sw, EDoorState.Open, traps);
            if (canTurnBackOn)
                AddToggle(sw.transform, TarkovTriggerWiringWindow.StateId(sw, EDoorState.Shut), traps, on: true);
        }

        // ------------------------------------------------------------ values

        private static IEnumerable<DamageData> Damage(float amount, params EBodyPartColliderType[] parts) =>
            parts.Select(p => new DamageData { Amount = amount, BodyPartColliderType = p });

        // every effect list empty: the game walks all of them
        public static HandlerEffect.EffectsSet EmptyEffects()
        {
            object set = new HandlerEffect.EffectsSet();
            foreach (var field in typeof(HandlerEffect.EffectsSet).GetFields())
                if (field.FieldType.IsArray)
                    field.SetValue(set, Array.CreateInstance(field.FieldType.GetElementType(), 0));
            return (HandlerEffect.EffectsSet)set;
        }
    }

    [CustomEditor(typeof(BarbedWire))]
    public class BarbedWireEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var wire = (BarbedWire)target;
            EditorGUILayout.Space();
            if (wire._soundBank == null)
            {
                EditorGUILayout.HelpBox("No sound bank: touching it makes no sound.", MessageType.Info);
                if (GUILayout.Button("Create Sound Bank"))
                    EditorApplication.delayCall += () =>
                    {
                        if (wire == null)
                            return;
                        Undo.RecordObject(wire, "Barbed wire sound bank");
                        wire._soundBank = TarkovTraps.WireBank(wire.gameObject.scene);
                        EditorUtility.SetDirty(wire);
                    };
            }
            else
            {
                var clips = TarkovRandomSoundEvents.GetClips(wire._soundBank).ToList();
                EditorGUILayout.LabelField(new GUIContent($"Sounds ({wire._soundBank.name}, shared by the scene's wires)",
                    "One is picked at random each time a player touches the wire (at most every 0.5 s)."), EditorStyles.boldLabel);
                bool changed = false;
                for (int i = 0; i < clips.Count; i++)
                {
                    var clip = (AudioClip)EditorGUILayout.ObjectField(clips[i], typeof(AudioClip), false);
                    if (clip != clips[i])
                    {
                        clips[i] = clip;
                        changed = true;
                    }
                }
                var added = (AudioClip)EditorGUILayout.ObjectField("Add Sound", null, typeof(AudioClip), false);
                if (added != null)
                {
                    clips.Add(added);
                    changed = true;
                }
                if (changed)
                    TarkovRandomSoundEvents.SetClips(wire._soundBank, clips.Where(c => c != null).ToArray());
            }
            TrapGui.RandomToggle(wire.gameObject);
        }
    }

    internal static class TrapGui
    {
        public static void RandomToggle(GameObject trap)
        {
            bool random = TarkovTraps.IsRandom(trap);
            bool next = EditorGUILayout.ToggleLeft(new GUIContent("Random Per Raid",
                "A TrapSyncable: with Random Traps on in the Server Data Editor (Access tab), each raid keeps only some of these, as Labyrinth's wires. " +
                "Without Random Traps it is always there."), random);
            if (next != random)
                TarkovTraps.SetRandom(trap, next);
        }
    }

    [CustomEditor(typeof(HandlerDamage))]
    public class HandlerDamageEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var damage = (HandlerDamage)target;
            if (!string.IsNullOrEmpty(damage._configurationId))
                EditorGUILayout.HelpBox("Configuration Id: the game replaces Damage with the server's globals entry of this name, if it has one.", MessageType.None);
            // the SDK's traps: the zone is a child of the trap
            var trap = damage.transform.parent;
            if (trap != null && damage.GetComponent<TriggerZone>() != null && damage.GetComponents<TriggerZone>().Any(z => z._mode == TriggerZone.ETriggerZoneMode.MoveInside))
                TrapGui.RandomToggle(trap.gameObject);
        }
    }

    [CustomEditor(typeof(TrapSyncable))]
    public class TrapSyncableEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var trap = (TrapSyncable)target;
            EditorGUILayout.HelpBox(trap.TrapType == ETrapType.TrapDoor
                ? "Trap door: each raid, some of the map's trap doors lose their cover (this object is turned off), as on Labyrinth. Needs Random Traps in the Server Data Editor (Access tab); without it every cover stays."
                : "Random per raid: each raid keeps only some of the map's wires and spike traps marked like this. Needs Random Traps in the Server Data Editor (Access tab); without it all stay.",
                MessageType.None);
        }
    }
}
