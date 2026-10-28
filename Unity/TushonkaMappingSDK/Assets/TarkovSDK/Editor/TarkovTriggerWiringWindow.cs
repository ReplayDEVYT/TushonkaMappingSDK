using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.GameTriggers;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // Tarkov SDK > Tools > Triggers > Triggers & Sounds: connects "when" to "do" with the game's trigger system,
    // so nobody has to match trigger ids by hand.
    //
    // A trigger id is a name a sender emits and listeners react to:
    //  - senders: a TriggerZone (a player enters or leaves its box), a switch or door's Triggers Map
    //    (it reaches a state), a TriggerBallistic (the object is shot, or a blast reaches it), a world event's step
    //  - listeners: HandlerPlaySoundAdvanced (play / stop), HandlerLamp, HandlerInteractable (open or
    //    close an object), HandlerAnimator
    // PA announcements follow an object's state directly (InteractiveSubscriber); a zone announces by
    // pressing a hidden switch, as Labs does. The list below shows every connection in the open
    // scenes, and the ones that can never fire.
    public class TarkovTriggerWiringWindow : EditorWindow
    {
        private enum When
        {
            PlayerEntersZone,
            PlayerLeavesZone,
            LastPlayerLeavesZone,
            ObjectOpens,
            ObjectCloses,
            ObjectShot,
        }

        private enum Action
        {
            PlaySound,
            StopSound,
            Announce,
            LampsOn,
            LampsOff,
            LampsToggle,
            TrapsOff,
            TrapsOn,
            OpenObject,
            CloseObject,
            AnimateOpen,
            AnimateClose,
            PlayClip,
            ForceTransit,
            StripGear,
            ShowNotification,
            ShowCaption,
            ChangeHealth,
            ApplyEffect,
            SpawnBots,
            SpawnLoot,
            SpawnPrefab,
            MortarStrike,
            Explode,
            StartWorldEvent,
        }

        private static readonly string[] WhenNames =
        {
            "A player enters a zone", "A player leaves a zone", "The last player leaves a zone", "A switch or door opens", "A switch or door closes",
            "An object is shot (or caught in a blast)",
        };

        private static readonly string[] ActionNames =
        {
            "Play a sound", "Stop a sound", "Announce on the PA", "Turn lamps on", "Turn lamps off", "Toggle lamp groups", "Turn traps off", "Turn traps on",
            "Open a door or switch", "Close a door or switch", "Open an animated object", "Close an animated object", "Play an animation",
            "Force a transit (no confirmation)", "Strip the player's gear", "Show a notification",
            "Show captions", "Change the player's health", "Give the player effects",
            "Spawn bots", "Spawn loot", "Spawn, show or hide a prefab", "Shell a mortar zone",
            "Set off an explosion", "Start a scripted world event",
        };

        private When _when;
        private Action _action;
        private GameObject _source;
        private List<AudioClip> _clips = new List<AudioClip> { null };
        private Transform _soundAt;
        private bool _loop, _stereo, _playOnce, _stopOnLastLeave = true;
        private float _volume = 1f;
        private int _heardTo = 45;
        private HandlerPlaySoundAdvanced _stopTarget;
        private List<LampController> _lamps = new List<LampController> { null };
        private List<ControlledLampGroup> _groups = new List<ControlledLampGroup> { null };
        private WorldInteractiveObject _objectTarget;
        private Vector2 _scroll;
        private List<GameObject> _traps = new List<GameObject> { null };
        private TarkovAnimatedObject _animated;
        private Animator _animator;
        private AnimationClip _animationClip;
        private TransitPoint _transit;
        private TarkovGearStrip _strip;
        private TarkovNotification _notification;
        private TarkovCaption _caption;
        private TarkovHealthChange _health;
        private TarkovEffect _effect;
        private TarkovSpawnBots _bots;
        private TarkovSpawnLoot _loot;
        private TarkovSpawnPrefab _prefab;
        private TarkovMortarZone _mortarZone;
        private bool _stopHealthOnLeave = true;
        private bool _shotOnce = true;
        private Transform _explosionAt;
        private TarkovWorldEvent _worldEvent;

        [MenuItem("Tarkov SDK/Tools/Triggers/Triggers & Sounds...", priority = 118)]
        public static void Open()
        {
            var window = GetWindow<TarkovTriggerWiringWindow>("Triggers & Sounds");
            window.minSize = new Vector2(420f, 420f);
            if (Selection.activeGameObject != null)
                window._source = Selection.activeGameObject;
        }

        // set up to force a transit (from the Extracts window); the mapper picks the zone or switch
        public static void OpenForceTransit(TransitPoint transit)
        {
            var window = GetWindow<TarkovTriggerWiringWindow>("Triggers & Sounds");
            window.minSize = new Vector2(420f, 420f);
            window._action = Action.ForceTransit;
            window._transit = transit;
            window._source = null;
            window.Repaint();
        }

        private bool ZoneSource => _when <= When.LastPlayerLeavesZone;
        private bool ObjectSource => _when == When.ObjectOpens || _when == When.ObjectCloses;
        private bool ShotSource => _when == When.ObjectShot;

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Connect", EditorStyles.boldLabel);
            _when = (When)EditorGUILayout.Popup("When", (int)_when, WhenNames);
            _source = (GameObject)EditorGUILayout.ObjectField(ZoneSource ? "Zone" : ShotSource ? "Object" : "Switch Or Door", _source, typeof(GameObject), true);
            if (ZoneSource)
                EditorGUILayout.LabelField(_source == null ? "Any object: it becomes (or gets) a trigger zone. Size its box to the area."
                                               : ZoneNote(_source), EditorStyles.wordWrappedMiniLabel);
            else if (ShotSource)
            {
                EditorGUILayout.LabelField(_source != null && _source.GetComponentInChildren<EFT.Ballistics.BallisticCollider>(true) == null
                    ? "It has no BallisticCollider, so a hit box is added round it (on HighPolyCollider)."
                    : "Its BallisticCollider is what gets shot (the game's TriggerBallistic; blasts that reach it count too).", EditorStyles.wordWrappedMiniLabel);
                _shotOnce = EditorGUILayout.Toggle(new GUIContent("Only The First Hit", "Fire only once per raid; off: on every hit."), _shotOnce);
            }
            else if (_source != null && _source.GetComponent<WorldInteractiveObject>() == null)
                EditorGUILayout.HelpBox("Pick an object with a Door, Switch, container or other interactive object.", MessageType.Warning);

            _action = (Action)EditorGUILayout.Popup("Do", (int)_action, ActionNames);
            using (new EditorGUI.IndentLevelScope())
                DrawActionFields();

            EditorGUILayout.Space();
            string problem = Problem();
            if (problem != null)
                EditorGUILayout.HelpBox(problem, MessageType.Info);
            using (new EditorGUI.DisabledScope(problem != null))
                if (GUILayout.Button("Connect", GUILayout.Height(26f)))
                    EditorApplication.delayCall += () =>
                    {
                        if (this == null)
                            return;
                        Connect();
                        Repaint();
                    };

            EditorGUILayout.Space(12f);
            DrawConnections();
            EditorGUILayout.EndScrollView();
        }

        private static string ZoneNote(GameObject go)
        {
            var collider = go.GetComponent<Collider>();
            if (go.GetComponent<TriggerZone>() != null || collider != null && collider.isTrigger)
                return "Its trigger box is the zone.";
            return collider != null ? "It has a solid collider, so a trigger box is added as a child." : "A 4 x 3 x 4 m trigger box is added to it.";
        }

        private void DrawActionFields()
        {
            switch (_action)
            {
                case Action.PlaySound:
                    ClipList("Sounds (one picked at random)");
                    _soundAt = (Transform)EditorGUILayout.ObjectField(new GUIContent("Play At", "Empty: at the zone or object."), _soundAt, typeof(Transform), true);
                    _volume = EditorGUILayout.Slider("Volume", _volume, 0f, 1f);
                    _heardTo = EditorGUILayout.IntField(new GUIContent("Heard To (m)", "Rolloff: the distance it fades out over."), _heardTo);
                    _loop = EditorGUILayout.Toggle("Loop", _loop);
                    _stereo = EditorGUILayout.Toggle(new GUIContent("2D (Not Spatial)", "Is Force Stereo: the same in both ears, for ambience heard everywhere around."), _stereo);
                    _playOnce = EditorGUILayout.Toggle(new GUIContent("Only Once Per Raid"), _playOnce);
                    if (_loop && _when == When.PlayerEntersZone)
                        _stopOnLastLeave = EditorGUILayout.Toggle("Stop When Last Player Leaves", _stopOnLastLeave);
                    break;
                case Action.StopSound:
                    _stopTarget = (HandlerPlaySoundAdvanced)EditorGUILayout.ObjectField("Sound", _stopTarget, typeof(HandlerPlaySoundAdvanced), true);
                    break;
                case Action.Announce:
                    ClipList("Announcement");
                    EditorGUILayout.LabelField(Object.FindObjectOfType<QueuePlayer>() == null ? "A PA system with two speakers is added; move the speakers around the map."
                                                   : "Plays on the scene's PA system, on all its speakers.", EditorStyles.wordWrappedMiniLabel);
                    break;
                case Action.LampsOn:
                case Action.LampsOff:
                    for (int i = 0; i < _lamps.Count; i++)
                        _lamps[i] = (LampController)EditorGUILayout.ObjectField($"Lamp {i + 1}", _lamps[i], typeof(LampController), true);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(EditorGUI.indentLevel * 15f);
                        if (GUILayout.Button("Add Lamp"))
                            _lamps.Add(null);
                        if (_lamps.Count > 1 && GUILayout.Button("Remove"))
                            _lamps.RemoveAt(_lamps.Count - 1);
                    }
                    GroupList();
                    break;
                case Action.LampsToggle:
                    GroupList();
                    // HandlerLamp's toggle mode on a single lamp toggles it and then sets it off anyway
                    EditorGUILayout.LabelField("Only lamp groups toggle; for single lamps use on and off.", EditorStyles.miniLabel);
                    break;
                case Action.TrapsOff:
                case Action.TrapsOn:
                    for (int i = 0; i < _traps.Count; i++)
                    {
                        _traps[i] = (GameObject)EditorGUILayout.ObjectField($"Trap {i + 1}", _traps[i], typeof(GameObject), true);
                        if (_traps[i] != null && !TarkovTraps.IsSwitchable(_traps[i]))
                            EditorGUILayout.HelpBox("Not a spike or flame trap (no HandlerDamage or HandlerEffect under it). Barbed wire can't be switched off.", MessageType.Warning);
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(EditorGUI.indentLevel * 15f);
                        if (GUILayout.Button("Add Trap"))
                            _traps.Add(null);
                        if (_traps.Count > 1 && GUILayout.Button("Remove"))
                            _traps.RemoveAt(_traps.Count - 1);
                    }
                    EditorGUILayout.LabelField(_action == Action.TrapsOff
                        ? "Their triggers are ignored and their Effects hidden; from a switch, damage already running stops too."
                        : "Undoes Turn traps off.", EditorStyles.wordWrappedMiniLabel);
                    break;
                case Action.OpenObject:
                case Action.CloseObject:
                    _objectTarget = (WorldInteractiveObject)EditorGUILayout.ObjectField("Door Or Switch", _objectTarget, typeof(WorldInteractiveObject), true);
                    EditorGUILayout.LabelField("A locked one is unlocked first, as the game does for triggers.", EditorStyles.miniLabel);
                    break;
                case Action.AnimateOpen:
                case Action.AnimateClose:
                    _animated = (TarkovAnimatedObject)EditorGUILayout.ObjectField(new GUIContent("Animated Object", "Its SDK_Animation settings (GameObject > Tarkov SDK > Interactables > Animated Object)."),
                        _animated, typeof(TarkovAnimatedObject), true);
                    break;
                case Action.PlayClip:
                    _animator = (Animator)EditorGUILayout.ObjectField("Animator", _animator, typeof(Animator), true);
                    _animationClip = (AnimationClip)EditorGUILayout.ObjectField("Animation", _animationClip, typeof(AnimationClip), false);
                    EditorGUILayout.LabelField("The animation gets its own state on the Animator (a controller is made if it has none) and stays on its last frame.", EditorStyles.wordWrappedMiniLabel);
                    break;
                case Action.ForceTransit:
                    _transit = (TransitPoint)EditorGUILayout.ObjectField("Transit", _transit, typeof(TransitPoint), true);
                    EditorGUILayout.LabelField("Sends the player who fires it on at once: no transit menu, no confirmation, no countdown (CustomMapLoader's own; Tarkov has none).", EditorStyles.wordWrappedMiniLabel);
                    break;
                case Action.StripGear:
                    _strip = (TarkovGearStrip)EditorGUILayout.ObjectField(new GUIContent("Gear Strip", "Empty: one is made (GameObject > Tarkov SDK > Triggers > Gear Strip), with its stash."), _strip, typeof(TarkovGearStrip), true);
                    EditorGUILayout.LabelField("Takes the player's gear into its stash or for good (CustomMapLoader's own; Tarkov has none).", EditorStyles.wordWrappedMiniLabel);
                    break;
                case Action.ShowNotification:
                    _notification = (TarkovNotification)EditorGUILayout.ObjectField(new GUIContent("Notification", "Empty: one is made (GameObject > Tarkov SDK > Triggers > Notification); set its message, icon, sound and duration."), _notification, typeof(TarkovNotification), true);
                    EditorGUILayout.LabelField("Shows the game's notification popup to the player who fires it (CustomMapLoader's own).", EditorStyles.wordWrappedMiniLabel);
                    break;
                case Action.ShowCaption:
                    _caption = (TarkovCaption)EditorGUILayout.ObjectField(new GUIContent("Caption", "Empty: one is made (GameObject > Tarkov SDK > Triggers > Caption); write its lines and times."), _caption, typeof(TarkovCaption), true);
                    EditorGUILayout.LabelField("Shows lines in the game's caption view to the player who fires it, each at its time (CustomMapLoader's own). Connect a sound to the same zone to voice them.", EditorStyles.wordWrappedMiniLabel);
                    break;
                case Action.ChangeHealth:
                    _health = (TarkovHealthChange)EditorGUILayout.ObjectField(new GUIContent("Health Change", "Empty: one is made (GameObject > Tarkov SDK > Triggers > Health Change); set what it does."), _health, typeof(TarkovHealthChange), true);
                    if (_when == When.PlayerEntersZone)
                        _stopHealthOnLeave = EditorGUILayout.Toggle(new GUIContent("Stop When They Leave", "Repeats (a gas zone) stop when the player leaves the zone (its Stop Trigger Id)."), _stopHealthOnLeave);
                    EditorGUILayout.LabelField("Damages, heals, restores or kills the player who fires it, or changes their energy or hydration (CustomMapLoader's own).", EditorStyles.wordWrappedMiniLabel);
                    break;
                case Action.ApplyEffect:
                    _effect = (TarkovEffect)EditorGUILayout.ObjectField(new GUIContent("Effect", "Empty: one is made (GameObject > Tarkov SDK > Triggers > Effect); pick its effects."), _effect, typeof(TarkovEffect), true);
                    EditorGUILayout.LabelField("Gives the player who fires it health effects: bleeding, fractures, pain, contusion, stun, poison, stimulator buffs (CustomMapLoader's own).", EditorStyles.wordWrappedMiniLabel);
                    break;
                case Action.SpawnBots:
                    _bots = (TarkovSpawnBots)EditorGUILayout.ObjectField(new GUIContent("Spawn Bots", "Empty: one is made (GameObject > Tarkov SDK > Triggers > Spawn Bots) at the source; move its spawn points."), _bots, typeof(TarkovSpawnBots), true);
                    EditorGUILayout.LabelField("Spawns bots of a role and difficulty at its points (CustomMapLoader's own; on the host in co-op).", EditorStyles.wordWrappedMiniLabel);
                    break;
                case Action.SpawnLoot:
                    _loot = (TarkovSpawnLoot)EditorGUILayout.ObjectField(new GUIContent("Spawn Loot", "Empty: one is made (GameObject > Tarkov SDK > Triggers > Spawn Loot) at the source; pick its items."), _loot, typeof(TarkovSpawnLoot), true);
                    EditorGUILayout.LabelField("Drops items at its points (CustomMapLoader's own; in co-op only the host sees them).", EditorStyles.wordWrappedMiniLabel);
                    break;
                case Action.SpawnPrefab:
                    _prefab = (TarkovSpawnPrefab)EditorGUILayout.ObjectField(new GUIContent("Spawn Prefab", "Empty: one is made (GameObject > Tarkov SDK > Triggers > Spawn Prefab) at the source; pick its prefab."), _prefab, typeof(TarkovSpawnPrefab), true);
                    EditorGUILayout.LabelField("Puts a copy of a prefab at its points, or switches a scene object on or off (CustomMapLoader's own).", EditorStyles.wordWrappedMiniLabel);
                    break;
                case Action.MortarStrike:
                    _mortarZone = (TarkovMortarZone)EditorGUILayout.ObjectField(new GUIContent("Mortar Zone", "Empty: one is made at the source (with the map's mortar setup if it has none)."), _mortarZone, typeof(TarkovMortarZone), true);
                    EditorGUILayout.LabelField("Shells the zone after its Trigger Delay, with the game's warning (CustomMapLoader's own trigger; the shelling is the game's).", EditorStyles.wordWrappedMiniLabel);
                    break;
                case Action.Explode:
                    _explosionAt = (Transform)EditorGUILayout.ObjectField(new GUIContent("Explode At", "Empty: at the source."), _explosionAt, typeof(Transform), true);
                    EditorGUILayout.LabelField("The game's explosion (HandlerExplosion, retail's gas cylinder blast): goes off once, hurts and stuns those in range and sets off shootable triggers it reaches.", EditorStyles.wordWrappedMiniLabel);
                    break;
                case Action.StartWorldEvent:
                    _worldEvent = (TarkovWorldEvent)EditorGUILayout.ObjectField(new GUIContent("World Event", "Empty: one is made (GameObject > Tarkov SDK > World Events > Scripted Event)."), _worldEvent, typeof(TarkovWorldEvent), true);
                    EditorGUILayout.LabelField("Starts the event's steps (CustomMapLoader's own); one set to start at a raid time can still start earlier on this.", EditorStyles.wordWrappedMiniLabel);
                    break;
            }
        }

        private void GroupList()
        {
            for (int i = 0; i < _groups.Count; i++)
                _groups[i] = (ControlledLampGroup)EditorGUILayout.ObjectField($"Lamp Group {i + 1}", _groups[i], typeof(ControlledLampGroup), true);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(EditorGUI.indentLevel * 15f);
                if (GUILayout.Button("Add Lamp Group"))
                    _groups.Add(null);
                if (_groups.Count > 1 && GUILayout.Button("Remove"))
                    _groups.RemoveAt(_groups.Count - 1);
            }
        }

        private void ClipList(string label)
        {
            for (int i = 0; i < _clips.Count; i++)
                _clips[i] = (AudioClip)EditorGUILayout.ObjectField(i == 0 ? label : " ", _clips[i], typeof(AudioClip), false);
            if (_action == Action.Announce)
                return;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(EditorGUI.indentLevel * 15f);
                if (GUILayout.Button("Add Sound"))
                    _clips.Add(null);
                if (_clips.Count > 1 && GUILayout.Button("Remove"))
                    _clips.RemoveAt(_clips.Count - 1);
            }
        }

        private string Problem()
        {
            if (_source == null)
                return ZoneSource ? "Pick the zone (any object in the scene)." : ShotSource ? "Pick the object that gets shot." : "Pick the switch or door.";
            if (ShotSource && _action == Action.Announce)
                return "PA announcements follow a zone or a switch; use Play a sound for a shot.";
            if (ObjectSource && _source.GetComponent<WorldInteractiveObject>() == null)
                return "The source has no interactive object.";
            switch (_action)
            {
                case Action.PlaySound:
                case Action.Announce:
                    return _clips.Any(c => c != null) ? null : "Pick a sound.";
                case Action.StopSound:
                    return _stopTarget == null ? "Pick the sound to stop (a Sound Zone or a sound made here)." : null;
                case Action.LampsOn:
                case Action.LampsOff:
                    return _lamps.Any(l => l != null) || _groups.Any(g => g != null) ? null : "Pick a lamp or lamp group.";
                case Action.LampsToggle:
                    return _groups.Any(g => g != null) ? null : "Pick a lamp group.";
                case Action.TrapsOff:
                case Action.TrapsOn:
                    return _traps.Any(t => t != null && TarkovTraps.IsSwitchable(t)) ? null : "Pick a spike or flame trap.";
                case Action.AnimateOpen:
                case Action.AnimateClose:
                    return _animated == null || _animated.Animator == null ? "Pick the animated object (its SDK_Animation child)." : null;
                case Action.PlayClip:
                    return _animator == null || _animationClip == null ? "Pick the Animator and the animation." : null;
                case Action.ForceTransit:
                    return _transit == null ? "Pick the transit." : null;
                case Action.StripGear:
                case Action.ShowNotification:
                case Action.ShowCaption:
                case Action.ChangeHealth:
                case Action.ApplyEffect:
                case Action.SpawnBots:
                case Action.SpawnLoot:
                case Action.SpawnPrefab:
                case Action.MortarStrike:
                case Action.Explode:
                case Action.StartWorldEvent:
                    return null;
                default:
                    return _objectTarget == null ? "Pick the door or switch." : null;
            }
        }

        // ------------------------------------------------------------ wiring

        private static string NewId(string what, GameObject go) =>
            $"{go.name.Replace(' ', '_')}_{what}_{Guid.NewGuid().ToString("N").Substring(0, 6)}";

        // the zone's TriggerZone for a mode, made if needed, on a trigger box on the Triggers layer
        private static string ZoneId(GameObject source, TriggerZone.ETriggerZoneMode mode, string what)
        {
            var zoneObject = source;
            var collider = source.GetComponent<Collider>();
            if (source.GetComponent<TriggerZone>() == null && (collider == null || !collider.isTrigger))
            {
                if (collider != null)
                {
                    zoneObject = new GameObject("TriggerZone");
                    Undo.RegisterCreatedObjectUndo(zoneObject, "Add trigger zone");
                    zoneObject.transform.SetParent(source.transform, false);
                }
                var box = Undo.AddComponent<BoxCollider>(zoneObject);
                box.isTrigger = true;
                box.center = new Vector3(0f, 1.5f, 0f);
                box.size = new Vector3(4f, 3f, 4f);
            }
            if (zoneObject.layer != LayerMask.NameToLayer("Triggers"))
            {
                Undo.RecordObject(zoneObject, "Triggers layer");
                zoneObject.layer = LayerMask.NameToLayer("Triggers");
            }
            var zone = zoneObject.GetComponents<TriggerZone>().FirstOrDefault(z => z._mode == mode && !string.IsNullOrEmpty(z._triggerId));
            if (zone != null)
                return zone._triggerId;
            zone = Undo.AddComponent<TriggerZone>(zoneObject);
            zone._mode = mode;
            zone._triggerId = NewId(what, source);
            return zone._triggerId;
        }

        // the object's Triggers Map entry for a state, made if needed
        public static string StateId(WorldInteractiveObject wio, EDoorState state)
        {
            var existing = wio.TriggersMap?.FirstOrDefault(p => p.State == state && !string.IsNullOrEmpty(p.TriggerId)) ?? default;
            if (!string.IsNullOrEmpty(existing.TriggerId))
                return existing.TriggerId;
            Undo.RecordObject(wio, "Add trigger");
            var id = NewId(state == EDoorState.Open ? "Opened" : "Closed", wio.gameObject);
            wio.TriggersMap = (wio.TriggersMap ?? new WorldInteractiveObject.TriggerOnStatePair[0])
                .Concat(new[] { new WorldInteractiveObject.TriggerOnStatePair { State = state, TriggerId = id } }).ToArray();
            EditorUtility.SetDirty(wio);
            return id;
        }

        private string SourceId()
        {
            switch (_when)
            {
                // a loop starts for the first player in only: each Enter would start another copy
                case When.PlayerEntersZone:
                    return _action == Action.PlaySound && _loop
                        ? ZoneId(_source, TriggerZone.ETriggerZoneMode.FirstEnter, "FirstEnter")
                        : ZoneId(_source, TriggerZone.ETriggerZoneMode.Enter, "Enter");
                case When.PlayerLeavesZone: return ZoneId(_source, TriggerZone.ETriggerZoneMode.Exit, "Leave");
                case When.LastPlayerLeavesZone: return ZoneId(_source, TriggerZone.ETriggerZoneMode.LastExit, "LastLeave");
                case When.ObjectOpens: return StateId(_source.GetComponent<WorldInteractiveObject>(), EDoorState.Open);
                case When.ObjectShot:
                {
                    var shot = TarkovExplosives.AddShootable(_source, _shotOnce);
                    Undo.RecordObject(shot, "Shootable");
                    shot._once = _shotOnce;
                    return shot._triggerId;
                }
                default: return StateId(_source.GetComponent<WorldInteractiveObject>(), EDoorState.Shut);
            }
        }

        private GameObject HandlerObject(string name)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Add " + name);
            go.transform.SetParent(_source.transform, false);
            return go;
        }

        private void Connect()
        {
            if (_source == null)
                return;
            Undo.SetCurrentGroupName("Connect trigger");
            int group = Undo.GetCurrentGroup();
            GameObject result;
            if (_action == Action.Announce)
                result = ConnectAnnouncement();
            else
                result = ConnectHandler(SourceId());
            Undo.CollapseUndoOperations(group);
            if (result != null)
            {
                Selection.activeGameObject = result;
                EditorGUIUtility.PingObject(result);
            }
            Debug.Log($"[Tarkov SDK] Connected: {WhenNames[(int)_when]} '{_source.name}' -> {ActionNames[(int)_action]}");
        }

        private GameObject ConnectHandler(string id)
        {
            switch (_action)
            {
                case Action.PlaySound:
                {
                    var clips = _clips.Where(c => c != null).ToList();
                    var go = HandlerObject("Sound (" + clips[0].name + ")");
                    if (_soundAt != null)
                        go.transform.position = _soundAt.position;
                    else if (ZoneSource && _source.GetComponentInChildren<BoxCollider>() is BoxCollider box)
                        go.transform.position = box.transform.TransformPoint(box.center);
                    var sound = go.AddComponent<HandlerPlaySoundAdvanced>();
                    sound._playTriggerId = id;
                    sound._playOnce = _playOnce;
                    sound._soundsPool = clips.Select(c => new HandlerPlaySoundAdvanced.PlaySoundConfig
                    {
                        AudioClip = c, Volume = _volume, GroupType = BetterAudio.AudioSourceGroupType.Environment, RollOff = Mathf.Max(1, _heardTo),
                        Pitch = 1f, IsForceStereo = _stereo, Loop = _loop, OcclusionTest = EOcclusionTest.None, SpreadCurve = new AnimationCurve(),
                    }).ToArray();
                    if (_loop && _when == When.PlayerEntersZone && _stopOnLastLeave)
                        sound._stopTriggerId = ZoneId(_source, TriggerZone.ETriggerZoneMode.LastExit, "LastLeave");
                    return go;
                }
                case Action.StopSound:
                {
                    Undo.RecordObject(_stopTarget, "Stop sound");
                    if (!string.IsNullOrEmpty(_stopTarget._stopTriggerId) && _stopTarget._stopTriggerId != id)
                        Debug.Log($"[Tarkov SDK] '{_stopTarget.name}' stopped on '{_stopTarget._stopTriggerId}' before; it now stops on '{id}'.");
                    _stopTarget._stopTriggerId = id;
                    EditorUtility.SetDirty(_stopTarget);
                    return _stopTarget.gameObject;
                }
                case Action.LampsOn:
                case Action.LampsOff:
                case Action.LampsToggle:
                {
                    var go = HandlerObject(_action == Action.LampsOn ? "Lamps On" : _action == Action.LampsOff ? "Lamps Off" : "Lamps Toggle");
                    var lamps = go.AddComponent<HandlerLamp>();
                    lamps._triggerId = id;
                    lamps._lampControllers = _action == Action.LampsToggle ? new LampController[0] : _lamps.Where(l => l != null).ToArray();
                    lamps._state = _action == Action.LampsOn;
                    lamps._isToggleMode = _action == Action.LampsToggle;
                    lamps._controlledLampGroups = _groups.Where(g => g != null).ToArray();
                    lamps._lampControllerIDs = new string[0];
                    lamps._controlledLampGroupIDs = new ulong[0];
                    return go;
                }
                case Action.TrapsOff:
                case Action.TrapsOn:
                {
                    var traps = _traps.Where(t => t != null && TarkovTraps.IsSwitchable(t)).ToList();
                    bool on = _action == Action.TrapsOn;
                    // a switch also sends the traps' stop ids, so a flame already burning someone goes out
                    if (!on && ObjectSource)
                        TarkovTraps.SendStops(_source.GetComponent<WorldInteractiveObject>(), _when == When.ObjectOpens ? EDoorState.Open : EDoorState.Shut, traps);
                    return TarkovTraps.AddToggle(_source.transform, id, traps, on);
                }
                case Action.AnimateOpen:
                case Action.AnimateClose:
                    TarkovAnimatedObjects.Connect(_animated, id, _action == Action.AnimateOpen);
                    return _animated.Animator.gameObject;
                case Action.PlayClip:
                    TarkovAnimatedObjects.ConnectClip(_animator, _animationClip, id);
                    return _animator.gameObject;
                case Action.ForceTransit:
                {
                    var force = TarkovMapEvents.AddForce(_transit);
                    Undo.RecordObject(force, "Force transit");
                    Replaced(force.TriggerId, id, force.name);
                    force.TriggerId = id;
                    EditorUtility.SetDirty(force);
                    return force.gameObject;
                }
                case Action.StripGear:
                {
                    var strip = _strip;
                    if (strip == null)
                    {
                        strip = TarkovMapEvents.CreateGearStrip(null);
                        strip.transform.parent.position = _source.transform.position;
                        Undo.RegisterCreatedObjectUndo(strip.transform.parent.gameObject, "Create gear strip");
                    }
                    Undo.RecordObject(strip, "Strip gear");
                    Replaced(strip.TriggerId, id, strip.name);
                    strip.TriggerId = id;
                    EditorUtility.SetDirty(strip);
                    return strip.gameObject;
                }
                case Action.ShowNotification:
                {
                    var notification = _notification;
                    if (notification == null)
                    {
                        notification = TarkovMapEvents.CreateNotification(null);
                        notification.transform.position = _source.transform.position;
                        Undo.RegisterCreatedObjectUndo(notification.gameObject, "Create notification");
                    }
                    Undo.RecordObject(notification, "Show notification");
                    Replaced(notification.TriggerId, id, notification.name);
                    notification.TriggerId = id;
                    EditorUtility.SetDirty(notification);
                    return notification.gameObject;
                }
                case Action.ShowCaption:
                {
                    var caption = _caption;
                    if (caption == null)
                    {
                        caption = TarkovMapEvents.CreateCaption(null);
                        caption.transform.position = _source.transform.position;
                        Undo.RegisterCreatedObjectUndo(caption.gameObject, "Create caption");
                    }
                    Undo.RecordObject(caption, "Show caption");
                    Replaced(caption.TriggerId, id, caption.name);
                    caption.TriggerId = id;
                    EditorUtility.SetDirty(caption);
                    return caption.gameObject;
                }
                case Action.ChangeHealth:
                {
                    var health = _health;
                    if (health == null)
                    {
                        health = TarkovMapEvents.CreateHealthChange(null);
                        health.transform.position = _source.transform.position;
                        Undo.RegisterCreatedObjectUndo(health.gameObject, "Create health change");
                    }
                    Undo.RecordObject(health, "Change health");
                    Replaced(health.TriggerId, id, health.name);
                    health.TriggerId = id;
                    if (_when == When.PlayerEntersZone && _stopHealthOnLeave)
                        health.StopTriggerId = ZoneId(_source, TriggerZone.ETriggerZoneMode.Exit, "Leave");
                    EditorUtility.SetDirty(health);
                    return health.gameObject;
                }
                case Action.ApplyEffect:
                {
                    var effect = _effect;
                    if (effect == null)
                    {
                        effect = TarkovMapEvents.CreateEffect(null);
                        effect.transform.position = _source.transform.position;
                        Undo.RegisterCreatedObjectUndo(effect.gameObject, "Create effect");
                    }
                    Undo.RecordObject(effect, "Apply effect");
                    Replaced(effect.TriggerId, id, effect.name);
                    effect.TriggerId = id;
                    EditorUtility.SetDirty(effect);
                    return effect.gameObject;
                }
                case Action.SpawnBots:
                    return Listen(_bots != null ? _bots : Made(TarkovTriggeredSpawns.CreateBots(null), "Create bot spawn"), id, "Spawn bots");
                case Action.SpawnLoot:
                    return Listen(_loot != null ? _loot : Made(TarkovTriggeredSpawns.CreateLoot(null), "Create loot spawn"), id, "Spawn loot");
                case Action.SpawnPrefab:
                    return Listen(_prefab != null ? _prefab : Made(TarkovTriggeredSpawns.CreatePrefab(null), "Create prefab spawn"), id, "Spawn prefab");
                case Action.MortarStrike:
                {
                    var zone = _mortarZone;
                    if (zone == null)
                    {
                        var root = TarkovMortar.EnsureRoot(_source.scene);
                        zone = TarkovMortar.CreateZone(root.transform);
                        zone.transform.position = _source.transform.position;
                        zone.Planned = false;
                        Undo.RegisterCreatedObjectUndo(zone.gameObject, "Create mortar zone");
                        if (!root.GetComponentsInChildren<TarkovMortarBattery>(true).Any())
                            Undo.RegisterCreatedObjectUndo(TarkovMortar.CreateBattery(root.transform, zone.transform.position + new Vector3(350f, 0f, 0f)).gameObject, "Create mortar battery");
                    }
                    return Listen(zone, id, "Mortar strike");
                }
                case Action.Explode:
                {
                    var explosion = TarkovExplosives.CreateExplosion(null, id);
                    if (explosion.gameObject.scene != _source.scene)
                        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(explosion.gameObject, _source.scene);
                    explosion.transform.position = _explosionAt != null ? _explosionAt.position : _source.transform.position;
                    Undo.RegisterCreatedObjectUndo(explosion.gameObject, "Create explosion");
                    return explosion.gameObject;
                }
                case Action.StartWorldEvent:
                {
                    var evt = _worldEvent != null ? _worldEvent : Made(TarkovWorldEvents.Create(null), "Create world event");
                    Undo.RecordObject(evt, "Start world event");
                    Replaced(evt.TriggerId, id, evt.name);
                    evt.TriggerId = id;
                    if (evt.Start == TarkovWorldEvent.StartMode.RaidTime)
                        evt.Start = TarkovWorldEvent.StartMode.Trigger;
                    EditorUtility.SetDirty(evt);
                    return evt.gameObject;
                }
                default:
                {
                    var go = HandlerObject((_action == Action.OpenObject ? "Open " : "Close ") + _objectTarget.name);
                    var press = go.AddComponent<HandlerInteractable>();
                    press._triggerId = id;
                    press._interactable = _objectTarget;
                    press._interactableId = _objectTarget.Id;
                    press._interactionType = _action == Action.OpenObject ? EInteractionType.Open : EInteractionType.Close;
                    return go;
                }
            }
        }

        // a new SDK listener placed at the source
        private T Made<T>(T listener, string undo) where T : Component
        {
            listener.transform.position = _source.transform.position;
            Undo.RegisterCreatedObjectUndo(listener.gameObject, undo);
            return listener;
        }

        // points an SDK listener with a TriggerId field (spawns, mortar zones) at the id
        private static GameObject Listen(Component listener, string id, string undo)
        {
            var field = listener.GetType().GetField("TriggerId");
            Undo.RecordObject(listener, undo);
            Replaced(field.GetValue(listener) as string, id, listener.name);
            field.SetValue(listener, id);
            EditorUtility.SetDirty(listener);
            return listener.gameObject;
        }

        // the SDK's own listeners take one trigger id
        private static void Replaced(string old, string id, string name)
        {
            if (!string.IsNullOrEmpty(old) && old != id)
                Debug.Log($"[Tarkov SDK] '{name}' listened to '{old}' before; it now listens to '{id}'.");
        }

        private GameObject ConnectAnnouncement()
        {
            var pa = Object.FindObjectOfType<QueuePlayer>();
            if (pa == null)
            {
                pa = TarkovFacilitySystems.CreatePa(null);
                pa.transform.position = _source.transform.position + Vector3.up * 3f;
                Undo.RegisterCreatedObjectUndo(pa.gameObject, "Create PA system");
            }
            var clip = _clips.FirstOrDefault(c => c != null);
            if (ObjectSource)
            {
                // an announcement follows the object's state itself
                var subscriber = TarkovFacilitySystems.CreateAnnouncement(pa, _source.GetComponent<WorldInteractiveObject>(), "Announcement_" + _source.name);
                subscriber.Sounds[0].Clip = clip;
                subscriber.Sounds[0].State = _when == When.ObjectOpens ? EDoorState.Open : EDoorState.Shut;
                Undo.RegisterCreatedObjectUndo(subscriber.gameObject, "Create announcement");
                return subscriber.gameObject;
            }
            // a zone presses a hidden switch that the announcement follows (Labs' announcer switches)
            var mode = _when == When.PlayerEntersZone ? TriggerZone.ETriggerZoneMode.FirstEnter
                : _when == When.PlayerLeavesZone ? TriggerZone.ETriggerZoneMode.Exit : TriggerZone.ETriggerZoneMode.LastExit;
            string id = ZoneId(_source, mode, "Announce");
            var hidden = HandlerObject("AnnouncerSwitch (hidden)");
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
            var press = hidden.AddComponent<HandlerInteractable>();
            press._triggerId = id;
            press._interactable = sw;
            press._interactionType = EInteractionType.Open;
            var announcement = TarkovFacilitySystems.CreateAnnouncement(pa, sw, "Announcement_" + _source.name);
            announcement.Sounds[0].Clip = clip;
            Undo.RegisterCreatedObjectUndo(announcement.gameObject, "Create announcement");
            return announcement.gameObject;
        }

        // ------------------------------------------------------------ connections

        private void DrawConnections()
        {
            EditorGUILayout.LabelField("Connections in the open scenes", EditorStyles.boldLabel);
            var senders = TarkovTriggerLinks.Senders();
            var ids = new SortedSet<string>(senders.Keys);
            foreach (var listener in TarkovTriggerLinks.Handlers.Where(h => h != null))
                foreach (var id in TarkovTriggerLinks.ListensTo(listener))
                    if (!string.IsNullOrEmpty(id))
                        ids.Add(id);
            if (ids.Count == 0)
                EditorGUILayout.LabelField("None yet.", EditorStyles.miniLabel);
            foreach (var id in ids)
            {
                senders.TryGetValue(id, out var from);
                var to = TarkovTriggerLinks.Listeners(id).ToList();
                bool broken = from == null || to.Count == 0;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    var style = new GUIStyle(EditorStyles.miniBoldLabel);
                    if (broken)
                        style.normal.textColor = new Color(1f, 0.45f, 0.4f);
                    EditorGUILayout.LabelField(id + (from == null ? "   (nothing sends it)" : to.Count == 0 ? "   (nothing listens)" : ""), style);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        Column(from, "sent by");
                        GUILayout.Label("→", GUILayout.Width(16f));
                        Column(to, "reaches");
                    }
                }
            }

            var announcements = Object.FindObjectsOfType<InteractiveSubscriber>();
            if (announcements.Length > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("PA announcements", EditorStyles.boldLabel);
                foreach (var a in announcements)
                    using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                    {
                        string clips = string.Join(", ", (a.Sounds ?? new InteractiveSubscriber.PlaySettings[0]).Select(s => s?.Clip != null ? s.Clip.name : "(no clip)"));
                        string states = string.Join("/", (a.Sounds ?? new InteractiveSubscriber.PlaySettings[0]).Select(s => s?.State.ToString()).Distinct());
                        EditorGUILayout.LabelField(a.Subscribee == null ? $"{a.name}: follows nothing (set Subscribee)" : $"{a.Subscribee.name} {states} → {clips}", EditorStyles.miniLabel);
                        if (GUILayout.Button("Select", GUILayout.Width(52f)))
                            Selection.activeObject = a.gameObject;
                    }
            }
        }

        private static void Column(IEnumerable<Component> items, string empty)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.MinWidth(150f)))
            {
                var list = items?.ToList() ?? new List<Component>();
                if (list.Count == 0)
                    EditorGUILayout.LabelField("nothing", EditorStyles.miniLabel);
                foreach (var c in list)
                    if (GUILayout.Button($"{c.name} ({c.GetType().Name})", EditorStyles.miniButton))
                        Selection.activeObject = c.gameObject;
            }
        }

        private void OnHierarchyChange() => Repaint();
    }
}
