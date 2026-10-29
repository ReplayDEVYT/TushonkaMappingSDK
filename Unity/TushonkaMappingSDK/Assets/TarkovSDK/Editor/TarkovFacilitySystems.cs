using System;
using System.IO;
using System.Linq;
using EFT;
using EFT.Ballistics;
using EFT.GameTriggers;
using EFT.Interactive;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // Labs-style facility systems, built from the game's own components exactly as Labs uses them:
    //
    //  - Automatic Sliding Door: TriggerZone (FirstEnter / LastExit) -> HandlerAnimator -> an Animator with
    //    Close/Open states on an IsOpened bool; the clips' events call AutoDoorSound. AnimatorSync syncs it.
    //  - Elevator Exfil: a Manual exfil with a WorldEvent requirement. Optional power switch -> call button
    //    (exfil to AwaitsManualActivation, doors open) -> floor button inside (Countdown, doors close).
    //    ExfiltrationDoors slide the doors by the exfil's status.
    //  - PA System: a QueuePlayer whose child AudioSources (speakers) all play each queued announcement.
    //    Announcements are InteractiveSubscribers: on a switch or door's state, or, for a zone, on a hidden
    //    switch the zone presses (TriggerZone -> HandlerInteractable), as Labs' "announcer" switches.
    //  - Sound Zone: TriggerZone -> HandlerPlaySoundAdvanced, a random clip from a pool when a player enters.
    //
    // Mappers add their own models and sounds; the placeholders show where they go.
    public static class TarkovFacilitySystems
    {
        private const string Menu = "GameObject/Tarkov SDK/";

        private static uint RandomUInt()
        {
            var bytes = Guid.NewGuid().ToByteArray();
            return BitConverter.ToUInt32(bytes, 0) | 1u;
        }

        internal static string ShortId() => Guid.NewGuid().ToString("N").Substring(0, 8);

        internal static GameObject Child(Transform parent, string name, Vector3 localPosition, string layer = null)
        {
            var go = new GameObject(name);
            if (layer != null)
                go.layer = LayerMask.NameToLayer(layer);
            if (parent != null)
                go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go;
        }

        // a box panel: placeholder visual, movement collider and bullet collider
        internal static GameObject Panel(Transform parent, string name, Vector3 localPosition, Vector3 size, MaterialType material)
        {
            var panel = Child(parent, name, localPosition);
            var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "Visual (replace with your model)";
            Object.DestroyImmediate(visual.GetComponent<Collider>());
            visual.transform.SetParent(panel.transform, false);
            visual.transform.localPosition = new Vector3(0f, size.y * 0.5f, 0f);
            visual.transform.localScale = size;
            var surface = TarkovSceneTools.DefaultSurfaceMaterial();
            if (surface != null)
                visual.GetComponent<MeshRenderer>().sharedMaterial = surface;

            var low = Child(panel.transform, "LowPoly", Vector3.zero, "LowPolyCollider");
            var lowBox = low.AddComponent<BoxCollider>();
            lowBox.center = new Vector3(0f, size.y * 0.5f, 0f);
            lowBox.size = size;
            var high = Child(panel.transform, "HighPoly", Vector3.zero, "HighPolyCollider");
            var highBox = high.AddComponent<BoxCollider>();
            highBox.center = lowBox.center;
            highBox.size = size;
            var ballistic = high.AddComponent<BallisticCollider>();
            ballistic._typeOfMaterial = material;
            ballistic.SurfaceSound = material == MaterialType.Glass ? BaseBallistic.ESurfaceSound.Glass : BaseBallistic.ESurfaceSound.MetalThin;
            return panel;
        }

        internal static GameObject Zone(Transform parent, string name, Vector3 center, Vector3 size)
        {
            var go = Child(parent, name, Vector3.zero, "Triggers");
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.center = center;
            box.size = size;
            return go;
        }

        internal static TriggerZone AddZone(GameObject go, TriggerZone.ETriggerZoneMode mode, string id)
        {
            var zone = go.AddComponent<TriggerZone>();
            zone._mode = mode;
            zone._triggerId = id;
            return zone;
        }

        // ------------------------------------------------------------ automatic sliding door

        [MenuItem(Menu + "Interactables/Automatic Sliding Door", priority = 33)]
        private static void MenuSlidingDoor(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateSlidingDoor(p, 2f, 2.2f, true));

        public static GameObject CreateSlidingDoor(Transform parent, float width, float height, bool twoPanels)
        {
            string id = ShortId();
            var root = Child(parent, "AutoSlidingDoor", Vector3.zero);
            float panelWidth = twoPanels ? width / 2f : width;
            var size = new Vector3(panelWidth, height, 0.05f);
            Panel(root.transform, "Panel_L", new Vector3(twoPanels ? -panelWidth / 2f : 0f, 0f, 0f), size, MaterialType.Glass);
            if (twoPanels)
                Panel(root.transform, "Panel_R", new Vector3(panelWidth / 2f, 0f, 0f), size, MaterialType.Glass);

            var animator = root.AddComponent<Animator>();
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var handler = root.AddComponent<HandlerAnimator>();
            handler._target = animator;
            handler._animatorActions = new[]
            {
                new HandlerAnimator.AnimatorAction { TriggerId = "SlidingOpen_" + id, BoolAction = { Name = "IsOpened", Value = true } },
                new HandlerAnimator.AnimatorAction { TriggerId = "SlidingClose_" + id, BoolAction = { Name = "IsOpened", Value = false } },
            };
            var sync = root.AddComponent<AnimatorSync>();
            sync._animator = animator;
            sync._random = RandomUInt();
            var prefab = root.AddComponent<TriggersPrefab>();
            prefab._processed = true;
            prefab._postfix = id;
            var sound = root.AddComponent<AutoDoorSound>();
            sound._volume = 0.5f;

            // opens for the first player in, closes after the last one out (Labs' 5 x 4 x 4 m)
            var zone = Zone(root.transform, "TriggerZone", new Vector3(0f, 2f, 0f), new Vector3(Mathf.Max(5f, width + 2f), 4f, 4f));
            AddZone(zone, TriggerZone.ETriggerZoneMode.FirstEnter, "SlidingOpen_" + id);
            AddZone(zone, TriggerZone.ETriggerZoneMode.LastExit, "SlidingClose_" + id);

            animator.runtimeAnimatorController = BuildSlidingAnimation(root, id, panelWidth * 0.95f, 1f);
            return root;
        }

        // Labs' Lab_Plastic_Door_1: Close (default) and Open, switched by IsOpened in 0.25 s; each clip
        // slides the panels over openTime and starts with the AutoDoorSound event. Rebuilt from the
        // panels' current (closed) positions.
        public static AnimatorController BuildSlidingAnimation(GameObject root, string id, float slide, float openTime)
        {
            var scenePath = root.scene.path;
            var folder = string.IsNullOrEmpty(scenePath) ? "Assets/AutoDoors" : $"{Path.GetDirectoryName(scenePath).Replace('\\', '/')}/{Path.GetFileNameWithoutExtension(scenePath)}_AutoDoors";
            if (!AssetDatabase.IsValidFolder(folder))
            {
                var parentFolder = Path.GetDirectoryName(folder).Replace('\\', '/');
                AssetDatabase.CreateFolder(parentFolder, Path.GetFileName(folder));
            }
            var path = $"{folder}/AutoDoor_{id}.controller";
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(path) != null)
                AssetDatabase.DeleteAsset(path);

            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            controller.AddParameter("IsOpened", AnimatorControllerParameterType.Bool);
            var machine = controller.layers[0].stateMachine;
            var open = MakeClip(root, "Open", slide, openTime, opening: true);
            var close = MakeClip(root, "Close", slide, openTime, opening: false);
            AssetDatabase.AddObjectToAsset(open, controller);
            AssetDatabase.AddObjectToAsset(close, controller);
            var closeState = machine.AddState("Close");
            closeState.motion = close;
            var openState = machine.AddState("Open");
            openState.motion = open;
            machine.defaultState = closeState;
            var toOpen = closeState.AddTransition(openState);
            toOpen.hasExitTime = false;
            toOpen.duration = 0.25f;
            toOpen.AddCondition(AnimatorConditionMode.If, 0f, "IsOpened");
            var toClose = openState.AddTransition(closeState);
            toClose.hasExitTime = false;
            toClose.duration = 0.25f;
            toClose.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsOpened");
            AssetDatabase.SaveAssets();
            return controller;
        }

        private static AnimationClip MakeClip(GameObject root, string name, float slide, float time, bool opening)
        {
            var clip = new AnimationClip { name = name };
            foreach (Transform panel in root.transform)
            {
                if (!panel.name.StartsWith("Panel", StringComparison.Ordinal))
                    continue;
                // the left panel slides left, the right one right
                float direction = panel.name.EndsWith("_R", StringComparison.Ordinal) ? 1f : -1f;
                var shut = panel.localPosition;
                var wide = shut + new Vector3(direction * slide, 0f, 0f);
                var from = opening ? shut : wide;
                var to = opening ? wide : shut;
                string path = AnimationUtility.CalculateTransformPath(panel, root.transform);
                clip.SetCurve(path, typeof(Transform), "m_LocalPosition.x", AnimationCurve.EaseInOut(0f, from.x, time, to.x));
                clip.SetCurve(path, typeof(Transform), "m_LocalPosition.y", AnimationCurve.Constant(0f, time, from.y));
                clip.SetCurve(path, typeof(Transform), "m_LocalPosition.z", AnimationCurve.Constant(0f, time, from.z));
            }
            AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { time = 0f, functionName = opening ? "OnStartOpen" : "OnStartClose" } });
            return clip;
        }

        // ------------------------------------------------------------ elevator exfil

        [MenuItem(Menu + "Exfils/Elevator Exfil", priority = 18)]
        private static void MenuElevator(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateElevator(p, withPower: false));

        [MenuItem(Menu + "Exfils/Elevator Exfil (Needs Power)", priority = 19)]
        private static void MenuElevatorPower(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateElevator(p, withPower: true));

        [MenuItem(Menu + "Exfils/Elevator Exfil (Opens On Approach)", priority = 20)]
        private static void MenuElevatorApproach(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateElevator(p, withPower: false, onApproach: true));

        // the car's floor is at the root, its doors face -Z, the call button is outside them.
        // onApproach: a zone in front of the doors presses the call button for the first player near
        public static GameObject CreateElevator(Transform parent, bool withPower, bool onApproach = false)
        {
            var root = Child(parent, "ElevatorExfil", Vector3.zero);

            var exfil = TarkovSceneTools.CreateExfil(root.transform, "Elevator_" + ShortId(), Vector3.zero);
            var box = exfil.GetComponent<BoxCollider>();
            box.center = new Vector3(0f, 1.2f, 0.9f);
            box.size = new Vector3(1.8f, 2.4f, 1.6f);
            exfil.Settings.ExfiltrationType = EExfiltrationType.Manual;
            exfil.Settings.ExfiltrationTime = 5f;
            exfil.Requirements = new[] { new ExfiltrationRequirement { Requirement = ERequirementState.WorldEvent, RequirementTip = "" } };

            // Labs' tips and buttons (the game's locale has these keys)
            var call = TarkovPrefabTools.CreateSwitch(root.transform, null);
            call.name = "CallButton";
            call.transform.localPosition = new Vector3(1.2f, 1.2f, -0.15f);
            call.ContextMenuTip = "EXFIL_button_call";
            call.ExtractionZoneTip = "EXFIL_elevator_call";
            call.ExfiltrationPoint = exfil;
            call.TargetStatus = EExfiltrationStatus.AwaitsManualActivation;
            call.ConditionStatus = new[] { EExfiltrationStatus.UncompleteRequirements };

            var floor = TarkovPrefabTools.CreateSwitch(root.transform, null);
            floor.name = "FloorButton";
            floor.transform.localPosition = new Vector3(0.8f, 1.2f, 1.6f);
            floor.ContextMenuTip = "EXFIL_button_floor";
            floor.ExtractionZoneTip = "EXFIL_elevator_floor";
            floor.ExfiltrationPoint = exfil;
            floor.TargetStatus = EExfiltrationStatus.Countdown;
            floor.ConditionStatus = new[] { EExfiltrationStatus.AwaitsManualActivation };
            floor.PreviousSwitch = call;
            // the call button can be pressed again for the next ride
            floor.NextSwitches = new[] { new Switch.SwitchAndOperation { Switch = call, Operation = EInteractionType.Close } };
            floor.AutoTurnOff = true;
            floor.AutoTurnOffSecondsDelay = 10;
            // the exfil reads its tips back along this chain
            exfil.Switch = floor;

            if (withPower)
            {
                var power = TarkovPrefabTools.CreateSwitch(root.transform, null);
                power.name = "PowerSwitch (move it anywhere)";
                power.transform.localPosition = new Vector3(2.5f, 1.2f, -0.5f);
                power.ContextMenuTip = "EXFIL_button_elevator";
                power.ExtractionZoneTip = "EXFIL_elevator_power";
                power.NextSwitches = new[] { new Switch.SwitchAndOperation { Switch = call, Operation = EInteractionType.Unlock } };
                call._doorState = EDoorState.Locked;
                call.PreviousSwitch = power;
            }

            if (onApproach)
            {
                string id = "ElevatorCall_" + ShortId();
                var approach = Zone(root.transform, "ApproachZone", new Vector3(0f, 1.2f, -1.5f), new Vector3(3f, 2.4f, 2.5f));
                AddZone(approach, TriggerZone.ETriggerZoneMode.FirstEnter, id);
                var press = approach.AddComponent<HandlerInteractable>();
                press._triggerId = id;
                press._interactable = call;
                press._interactionType = EInteractionType.Open;
            }

            // two leaves that slide apart while the exfil awaits activation (Labs: 1.5 s delay, 3 s slide)
            var doors = Child(root.transform, "Doors", Vector3.zero);
            foreach (var (name, side) in new[] { ("Door_L", -1f), ("Door_R", 1f) })
            {
                var leaf = Panel(doors.transform, name, new Vector3(side * 0.5f, 0f, 0f), new Vector3(1f, 2.3f, 0.06f), MaterialType.MetalThick);
                var door = leaf.AddComponent<ExfiltrationDoor>();
                door.Subscribee = exfil;
                door.Delay = 1.5f;
                door.OpenStatus = new[] { EExfiltrationStatus.AwaitsManualActivation };
                door.Curve = AnimationCurve.EaseInOut(0f, 0f, 2.5f, 1f);
                door.ForceStateOnStart = true;
                door.Volume = 1f;
                door.Rolloff = 50;
                door.LerpSpeed = 2f;
                var shut = leaf.transform.localPosition;
                door._doorTransforms = new[]
                {
                    new ExfiltrationDoor.DoorTransform
                    {
                        Transform = leaf.transform, ClosePosition = shut, OpenPosition = shut + new Vector3(side * 0.95f, 0f, 0f),
                        CloseRotation = Quaternion.identity, OpenRotation = Quaternion.identity, CloseScale = Vector3.one, OpenScale = Vector3.one,
                        PlaySoundAt = side < 0f,
                    },
                };
            }
            return root;
        }

        // ------------------------------------------------------------ PA system

        [MenuItem(Menu + "Sound/PA/PA System", priority = 62)]
        private static void MenuPa(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreatePa(p).gameObject);

        [MenuItem(Menu + "Sound/PA/Speaker", priority = 63)]
        private static void MenuSpeaker(MenuCommand cmd)
        {
            var pa = (cmd.context as GameObject)?.GetComponentInParent<QueuePlayer>() ?? Object.FindObjectOfType<QueuePlayer>();
            if (pa == null)
            {
                EditorUtility.DisplayDialog("Tarkov SDK", "Add a PA System first (GameObject > Tarkov SDK > Sound > PA > PA System).", "OK");
                return;
            }
            var speaker = CreateSpeaker(pa.transform, Vector3.zero);
            if (SceneView.lastActiveSceneView != null)
                speaker.transform.position = SceneView.lastActiveSceneView.pivot;
            Undo.RegisterCreatedObjectUndo(speaker, "Create speaker");
            Selection.activeGameObject = speaker;
        }

        [MenuItem(Menu + "Sound/PA/Announcement On Interaction", priority = 64)]
        private static void MenuAnnounceInteraction(MenuCommand cmd)
        {
            var wio = (cmd.context as GameObject)?.GetComponent<WorldInteractiveObject>() ?? Selection.activeGameObject?.GetComponent<WorldInteractiveObject>();
            if (wio == null)
            {
                EditorUtility.DisplayDialog("Tarkov SDK", "Right-click the switch or door whose use plays the announcement, then choose this.", "OK");
                return;
            }
            var pa = PaFor(wio.transform);
            var announcement = CreateAnnouncement(pa, wio, "Announcement_" + wio.name);
            Undo.RegisterCreatedObjectUndo(announcement.gameObject, "Create announcement");
            Selection.activeGameObject = announcement.gameObject;
        }

        [MenuItem(Menu + "Sound/PA/Announcement Zone", priority = 65)]
        private static void MenuAnnounceZone(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateAnnouncementZone(p, PaFor(p)));

        // the scene's PA, or a new one
        private static QueuePlayer PaFor(Transform near)
        {
            var pa = Object.FindObjectOfType<QueuePlayer>();
            if (pa != null)
                return pa;
            pa = CreatePa(null);
            if (near != null)
                pa.transform.position = near.position + Vector3.up * 3f;
            Undo.RegisterCreatedObjectUndo(pa.gameObject, "Create PA system");
            return pa;
        }

        public static QueuePlayer CreatePa(Transform parent)
        {
            var root = Child(parent, "PA_System", Vector3.zero);
            var player = root.AddComponent<QueuePlayer>();
            player.QueueRefreshTime = 0.5f;
            CreateSpeaker(root.transform, new Vector3(-5f, 2.5f, 0f));
            CreateSpeaker(root.transform, new Vector3(5f, 2.5f, 0f));
            return player;
        }

        // Labs' facility speakers: 3D, heard to 70 m, a little quieter than full. Labs puts them on
        // CommonAmbInEffects, an indoor ambience group the game silences while the player is outdoors
        // (fine on an all-indoor map, backwards on any other); CommonSounds, the world's own sound
        // group, works everywhere. The client plugin assigns "@Group" names.
        public const string SpeakerMixerTag = "@CommonSounds";

        // mixer groups under the game's Ambient > AmbientIn / AmbientOut, which follow indoors / outdoors
        public static bool AmbientOnlyGroup(string group) =>
            group.StartsWith("AmbientIn", StringComparison.OrdinalIgnoreCase) || group.StartsWith("AmbientOut", StringComparison.OrdinalIgnoreCase)
            || group.StartsWith("CommonAmbIn", StringComparison.OrdinalIgnoreCase) || group.StartsWith("CommonAmbOut", StringComparison.OrdinalIgnoreCase)
            || group.StartsWith("OccludedIn", StringComparison.OrdinalIgnoreCase) || group.StartsWith("RadioIn", StringComparison.OrdinalIgnoreCase)
            || group.StartsWith("RadioOut", StringComparison.OrdinalIgnoreCase) || group.StartsWith("OutEnvironment", StringComparison.OrdinalIgnoreCase);

        public static GameObject CreateSpeaker(Transform pa, Vector3 localPosition)
        {
            var go = Child(pa, "Speaker " + SpeakerMixerTag, localPosition);
            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.volume = 0.58f;
            source.spatialBlend = 1f;
            source.dopplerLevel = 0f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = 6f;
            source.maxDistance = 70f;
            TarkovAudioSources.SetUp(source, undo: false);
            return go;
        }

        // an InteractiveSubscriber that queues its clip on the PA when the object opens
        public static InteractiveSubscriber CreateAnnouncement(QueuePlayer pa, WorldInteractiveObject subscribee, string name)
        {
            var go = Child(pa.transform, name, Vector3.zero);
            var subscriber = go.AddComponent<InteractiveSubscriber>();
            subscriber.Subscribee = subscribee;
            subscriber.QueuePlayer = pa;
            subscriber.Views = new InteractiveSubscriber.ViewPerState[0];
            subscriber.Sounds = new[]
            {
                new InteractiveSubscriber.PlaySettings
                {
                    State = EDoorState.Open, Volume = 0.58f, MaxDistance = 50, SpatialBlend = 1f, Spatialize = false,
                    CustomCurve = new AnimationCurve(), SpreadCurve = new AnimationCurve(), AdditionalPositions = new Transform[0],
                },
            };
            return subscriber;
        }

        // a zone that presses a hidden switch, whose opening queues the announcement; the switch turns
        // itself off again so the next player in can hear it too
        public static GameObject CreateAnnouncementZone(Transform parent, QueuePlayer pa)
        {
            string id = "Announce_" + ShortId();
            var root = Child(parent, "AnnouncementZone", Vector3.zero);
            var zone = Zone(root.transform, "TriggerZone", new Vector3(0f, 1.5f, 0f), new Vector3(4f, 3f, 4f));
            AddZone(zone, TriggerZone.ETriggerZoneMode.FirstEnter, id);

            var hidden = new GameObject("AnnouncerSwitch (hidden)");
            hidden.transform.SetParent(root.transform, false);
            var sw = hidden.AddComponent<Switch>();
            sw.interactWithoutAnimation = true;
            sw.DontAnimateRotation = true;
            sw._doorState = EDoorState.Shut;
            sw.Operatable = true;
            sw.CustomProgressCurve = TarkovPrefabTools.SwitchCurve();
            sw.NextSwitches = new Switch.SwitchAndOperation[0];
            sw.ConditionStatus = new EExfiltrationStatus[0];
            sw.Lamps = new LampController[0];
            sw.AutoTurnOff = true;
            sw.AutoTurnOffSecondsDelay = 2;

            var press = zone.AddComponent<HandlerInteractable>();
            press._triggerId = id;
            press._interactable = sw;
            press._interactionType = EInteractionType.Open;
            CreateAnnouncement(pa, sw, "Announcement_" + root.name);
            return root;
        }

        // ------------------------------------------------------------ sound zone

        [MenuItem(Menu + "Sound/Sound Zone", priority = 61)]
        private static void MenuSoundZone(MenuCommand cmd) => TarkovSceneTools.Place(cmd, CreateSoundZone);

        // plays a random clip from its pool when a player enters; a looping one stops when the last leaves
        public static GameObject CreateSoundZone(Transform parent)
        {
            string id = ShortId();
            var root = Child(parent, "SoundZone", Vector3.zero);
            var zone = Zone(root.transform, "TriggerZone", new Vector3(0f, 1.5f, 0f), new Vector3(6f, 3f, 6f));
            AddZone(zone, TriggerZone.ETriggerZoneMode.Enter, "SoundPlay_" + id);
            AddZone(zone, TriggerZone.ETriggerZoneMode.LastExit, "SoundStop_" + id);
            var sound = root.AddComponent<HandlerPlaySoundAdvanced>();
            sound._playTriggerId = "SoundPlay_" + id;
            sound._stopTriggerId = "SoundStop_" + id;
            sound._soundsPool = new[]
            {
                new HandlerPlaySoundAdvanced.PlaySoundConfig
                {
                    Volume = 1f, GroupType = BetterAudio.AudioSourceGroupType.Environment, RollOff = 45, Pitch = 1f,
                    IsForceStereo = false, Loop = false, OcclusionTest = EOcclusionTest.None, SpreadCurve = new AnimationCurve(),
                },
            };
            return root;
        }
    }
}
