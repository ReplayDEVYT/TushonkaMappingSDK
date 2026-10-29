using System;
using System.IO;
using System.Linq;
using Audio.AmbientSubsystem.Data;
using Audio.SpatialSystem;
using Audio.SpatialSystem.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // Tarkov SDK > Generation > Audio > Spatial Audio...: the game's spatial audio for the active scene in one place.
    public class TarkovAudioWindow : EditorWindow
    {
        private Vector2 _scroll;

        [MenuItem("Tarkov SDK/Generation/Audio/Spatial Audio...", priority = 105)]
        private static void Open() => GetWindow<TarkovAudioWindow>("Spatial Audio").minSize = new Vector2(420f, 420f);

        [MenuItem("Tarkov SDK/Generation/Audio/Generate Rooms And Portals", priority = 106)]
        private static void GenerateMenu() => EditorApplication.delayCall += () => Generate(SceneManager.GetActiveScene());

        [MenuItem("Tarkov SDK/Generation/Audio/Bake Sound Routes", priority = 107)]
        private static void BakeRoutesMenu() => EditorApplication.delayCall += () => BakeRoutes(SceneManager.GetActiveScene());

        private void OnGUI()
        {
            // the map's one system, whichever of its scenes is active
            var scene = TarkovSpatialAudio.AudioScene(SceneManager.GetActiveScene());
            var system = TarkovSpatialAudio.Find(scene);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Spatial audio", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Rooms and the openings between them (doors, windows, doorways): the game muffles a sound by the way it " +
                                       "travels from room to room, blends the outdoor and indoor ambience as the player goes in and out, and gives " +
                                       "each room the reverb and echo of its type.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space();

            if (system == null)
            {
                EditorGUILayout.HelpBox($"{scene.name} has no spatial audio system: sounds play unoccluded and the ambience doesn't follow indoors and outdoors.", MessageType.Info);
                if (GUILayout.Button("Create And Generate Rooms", GUILayout.Height(26f)))
                    EditorApplication.delayCall += () => Generate(scene);
                EditorGUILayout.EndScrollView();
                return;
            }

            var rooms = system.GetComponentsInChildren<SpatialAudioRoom>(true);
            var portals = system.GetComponentsInChildren<BaseSpatialAudioPortal>(true);
            EditorGUILayout.LabelField($"{rooms.Length} room(s) ({rooms.Count(TarkovSpatialAudio.IsOutdoor)} outdoor), {portals.Length} portal(s)");
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Generate Rooms And Portals", "Replaces the generated rooms and portals; rooms made by hand are kept.")))
                    EditorApplication.delayCall += () => Generate(scene);
                if (GUILayout.Button("Select System"))
                    Selection.activeGameObject = system.gameObject;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Sound routes", EditorStyles.boldLabel);
            var bake = TarkovSpatialAudio.BakeFilePath(scene);
            EditorGUILayout.LabelField(File.Exists(bake) ? $"Baked {File.GetLastWriteTime(bake):g}: {Path.GetFileName(bake)}" : "Not baked yet. Build Map bakes them too.",
                                       EditorStyles.miniLabel);
            var info = system._locationInfo;
            if (info != null)
            {
                info.bakeSettings ??= new LocationBakeSettings();
                var s = info.bakeSettings;
                EditorGUI.BeginChangeCheck();
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.LabelField("How many portals, and how far, a sound may travel", EditorStyles.miniLabel);
                    s.maxDepthIndoorToIndoor = Mathf.Max(1, EditorGUILayout.IntField("Indoor To Indoor Portals", s.maxDepthIndoorToIndoor));
                    s.maxDistanceIndoorToIndoor = Mathf.Max(1, EditorGUILayout.IntField("Indoor To Indoor Distance (m)", s.maxDistanceIndoorToIndoor));
                    s.maxDepthIndoorToOutdoor = Mathf.Max(1, EditorGUILayout.IntField("Indoor To Outdoor Portals", s.maxDepthIndoorToOutdoor));
                    s.maxDistanceIndoorToOutdoor = Mathf.Max(1, EditorGUILayout.IntField("Indoor To Outdoor Distance (m)", s.maxDistanceIndoorToOutdoor));
                }
                if (EditorGUI.EndChangeCheck())
                    EditorUtility.SetDirty(info);
            }
            var state = TarkovAudioBakeState.Load(scene);
            EditorGUI.BeginChangeCheck();
            var mode = (TarkovAudioBakeState.RouteMode)EditorGUILayout.Popup(new GUIContent("Rebake On Build",
                "Auto: only when rooms, portals or these settings changed. Always: every build. Never: keep the baked file (bake it here when the rooms change)."),
                (int)state.RoutesOnBuild, new[] { "Auto (when rooms change)", "Always", "Never" });
            if (EditorGUI.EndChangeCheck())
            {
                state.RoutesOnBuild = mode;
                state.Save(scene);
            }
            if (GUILayout.Button("Bake Sound Routes"))
                EditorApplication.delayCall += () => BakeRoutes(scene);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Ambience", EditorStyles.boldLabel);
            var ambient = TarkovMapScenes.MapScenes(scene).Select(TarkovAmbientAudioSystem.Find).FirstOrDefault(a => a != null);
            if (ambient == null)
                EditorGUILayout.HelpBox("No game ambient audio system: add it in Tarkov SDK > Generation > Ambience > Ambient Sounds... (Game System) for wind, rain, day and night " +
                                        "loops and the rooms' reverb and echo.", MessageType.Info);
            else if (ambient.GetComponentInChildren<Audio.AmbientSubsystem.EnvironmentSoundBlendSystem>(true) == null)
            {
                EditorGUILayout.HelpBox("The ambient audio system has no indoor/outdoor blend, so the outdoor ambience doesn't fade inside rooms.", MessageType.Warning);
                if (GUILayout.Button("Add Indoor/Outdoor Blend"))
                    EditorApplication.delayCall += () =>
                    {
                        TarkovAmbientAudioSystem.AddEnvironmentBlend(ambient);
                        EditorSceneManager.MarkSceneDirty(ambient.scene);
                    };
            }
            else
            {
                EditorGUILayout.LabelField("The game blends the outdoor and indoor ambience through the rooms' portals.", EditorStyles.wordWrappedMiniLabel);
                foreach (var blender in ambient.GetComponentsInChildren<Audio.AmbientSubsystem.AmbientSoundBlender>(true))
                    DrawBlender(blender);
            }

            DrawRoomTones(scene, system);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Room acoustics (Meta XR Audio)", EditorStyles.boldLabel);
            if (!TarkovAcoustics.Available)
            {
                EditorGUILayout.HelpBox(TarkovAcoustics.InstallNote, MessageType.Info);
                if (GUILayout.Button($"Install Meta XR Audio SDK {TarkovMetaXrPackage.Version}"))
                    EditorApplication.delayCall += TarkovMetaXrPackage.Install;
            }
            else
            {
                var map = TarkovAcoustics.FindMap(scene);
                var geometries = TarkovAcoustics.Geometries(scene);
                EditorGUILayout.LabelField($"{geometries.Length} geometry file(s), acoustic map {(map != null ? "present" : "missing")}, " +
                                           $"{TarkovAcoustics.Files(scene).Count} baked file(s)", EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Reverb and reflections from the real walls, floors and their materials. Only static, visible meshes that never " +
                                           "move count (mark the map's geometry Static); each gets an acoustic material from its bullet material, or keeps " +
                                           "one you put on it. They're copied under SpatialAudioSystem/AcousticGeometry as editor-only proxies.",
                                           EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Set Up Acoustics"))
                        EditorApplication.delayCall += () =>
                        {
                            var result = TarkovAcoustics.Setup(scene, system);
                            EditorSceneManager.MarkSceneDirty(scene);
                            Debug.Log($"[Tarkov SDK] Acoustics: {result}");
                        };
                }
                if (_acousticStatus == null || EditorApplication.timeSinceStartup > _acousticStatusTime + 5)
                {
                    // hashing every mesh is too slow for every repaint
                    _acousticStatus = TarkovAcoustics.Check(scene);
                    _acousticStatusTime = EditorApplication.timeSinceStartup;
                }
                EditorGUILayout.LabelField("Baked: " + _acousticStatus.Selection, EditorStyles.wordWrappedMiniLabel);
                if (_acousticStatus.OldSetup)
                    EditorGUILayout.HelpBox("Acoustic geometry from an older SDK is on your scene roots and bakes every mesh under them (props, invisible borders). " +
                                            "Set Up Acoustics or the next bake replaces it with copies of the static, visible meshes only.", MessageType.Warning);
                if (map != null)
                {
                    EditorGUI.BeginChangeCheck();
                    int quality = EditorGUILayout.Popup(new GUIContent("Map Quality", "Spacing of the map's points and reflections per point; the bake time is mostly " +
                                                        "the number of points times the reflections. Balanced is Meta's default; Retail is Tarkov's own setting."),
                                                        TarkovAcoustics.Quality(map), TarkovAcoustics.Qualities.Select(q => q.Label).ToArray());
                    if (EditorGUI.EndChangeCheck() && quality >= 0)
                    {
                        TarkovAcoustics.SetQuality(map, quality);
                        _acousticStatus = null;
                    }
                }
                EditorGUILayout.LabelField(_acousticStatus?.ToString() ?? "", EditorStyles.miniBoldLabel);
                using (new EditorGUI.DisabledScope(geometries.Length == 0))
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("Bake Changes", "Rewrites only geometry that changed since the last bake, and computes the acoustic map only when anything did (or the last one was a quick bake).")))
                        BakeAcoustics(scene, TarkovAcoustics.BakeMode.Changes);
                    if (GUILayout.Button(new GUIContent("Quick Bake", "As Bake Changes, with a coarse map (points every 2-16 m, 4 reflections) that computes much faster. For listening tests; Bake Changes makes the full one.")))
                        BakeAcoustics(scene, TarkovAcoustics.BakeMode.Quick);
                    if (GUILayout.Button(new GUIContent("Full Rebake", "Everything, whether it changed or not.")))
                        BakeAcoustics(scene, TarkovAcoustics.BakeMode.Full);
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Audio sources", EditorStyles.boldLabel);
            if (_brokenSources == null || EditorApplication.timeSinceStartup > _brokenSourcesTime + 5)
            {
                _brokenSources = TarkovMapScenes.MapScenes(scene).SelectMany(TarkovAudioSources.Broken).ToList();
                _brokenSourcesTime = EditorApplication.timeSinceStartup;
                // bunker rooms follow the indoor triggers' Is Bunker as it's ticked
                EditorApplication.delayCall += () =>
                {
                    if (scene.IsValid() && scene.isLoaded && TarkovRoomTones.SyncBunkers(scene, TarkovSpatialAudio.Find(scene)) > 0)
                    {
                        EditorSceneManager.MarkSceneDirty(scene);
                        Repaint();
                    }
                };
            }
            _brokenSources.RemoveAll(s => s == null);
            EditorGUILayout.LabelField("The map's own sounds go through Meta's spatializer like the game's: 3D sources need Spatialize and a Meta XR Audio " +
                                       "Source (placement and room acoustics), 2D ones (ambience beds) need Spatialize off.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(_brokenSources.Count == 0 ? "Every audio source is set up." : $"{_brokenSources.Count} audio source(s) to fix, e.g. " +
                                       $"'{_brokenSources[0].name}': {TarkovAudioSources.Problem(_brokenSources[0])}", EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUI.DisabledScope(_brokenSources.Count == 0))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Fix Audio Sources"))
                    EditorApplication.delayCall += () =>
                    {
                        int count = 0;
                        foreach (var s in TarkovMapScenes.MapScenes(scene))
                        {
                            count += TarkovAudioSources.Fix(s);
                            EditorSceneManager.MarkSceneDirty(s);
                        }
                        Debug.Log($"[Tarkov SDK] {count} audio source(s) set up for Meta XR Audio.");
                        _brokenSources = null;
                        Repaint();
                    };
                if (GUILayout.Button("Select Them", GUILayout.Width(100f)))
                    Selection.objects = _brokenSources.Select(s => (Object)s.gameObject).ToArray();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Rooms", EditorStyles.boldLabel);
            foreach (var room in rooms.OrderBy(r => TarkovSpatialAudio.IsOutdoor(r) ? 0 : 1).ThenBy(r => r.name))
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(room.name, GUILayout.MinWidth(120f));
                    EditorGUI.BeginChangeCheck();
                    var type = TarkovAudioGui.TypePopup(room._type, GUILayout.Width(160f));
                    if (EditorGUI.EndChangeCheck())
                        TarkovRoomTones.SetType(room, type);
                    if (GUILayout.Button("Select", GUILayout.Width(52f)))
                        Selection.activeGameObject = room.gameObject;
                }
            EditorGUILayout.EndScrollView();
        }

        private TarkovAcoustics.Status _acousticStatus;
        private double _acousticStatusTime;
        private System.Collections.Generic.List<AudioSource> _brokenSources;
        private double _brokenSourcesTime;

        private static void DrawRoomTones(Scene scene, SpatialAudioSystem system)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Indoor ambience (room tones)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("A loop each room plays while the player is in it, as on retail maps (Customs: a quiet residential hum at 0.4, " +
                                       "its own in bunkers). Changing a tone changes the rooms that have it or none; rooms given their own tone keep it.",
                                       EditorStyles.wordWrappedMiniLabel);
            var bed = TarkovRoomTones.IndoorBed(scene);
            if (bed != null)
            {
                EditorGUILayout.HelpBox($"'{bed.name}' (Ambient Sounds' indoor bed) plays in every room through the same mixer as the room tones. Make it the rooms' " +
                                        "tone instead so each room (and bunker) can have its own.", MessageType.Warning);
                if (GUILayout.Button("Use Indoor Bed As Room Tone"))
                    EditorApplication.delayCall += () =>
                    {
                        TarkovRoomTones.UseBedAsTone(scene, TarkovSpatialAudio.Find(scene));
                        EditorSceneManager.MarkSceneDirty(scene);
                    };
            }
            using (new EditorGUI.IndentLevelScope())
            {
                foreach (var (bunker, label, help) in new[] { (false, "Indoor Tone", "Most rooms."),
                                                              (true, "Bunker Tone", "Rooms of the Bunker type (underground rooms become bunkers when generated).") })
                {
                    var rooms = TarkovRoomTones.Rooms(system, bunker);
                    var tone = TarkovRoomTones.Current(system, bunker);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        var clip = (AudioClip)EditorGUILayout.ObjectField(new GUIContent($"{label} ({rooms.Count})", help), tone.Clip, typeof(AudioClip), false);
                        var volume = EditorGUILayout.Slider(tone.Volume, 0f, 1f, GUILayout.Width(150f));
                        if (EditorGUI.EndChangeCheck())
                            TarkovRoomTones.SetTone(system, bunker, clip, volume);
                    }
                    if (bunker && rooms.Count == 0)
                        EditorGUILayout.LabelField("No bunker rooms yet: tick Is Bunker on the IndoorTriggers over your bunkers; the rooms inside them become " +
                                                   "bunkers with this tone.", EditorStyles.wordWrappedMiniLabel);
                }
                EditorGUI.BeginChangeCheck();
                var level = EditorGUILayout.Slider(new GUIContent("Outdoor Ambience Indoors", "How loud the outdoor ambience is inside rooms, away from their doors and " +
                                                                  "windows (near them it comes in by distance). Retail: 0."), TarkovRoomTones.OutdoorLevel(system), 0f, 1f);
                if (EditorGUI.EndChangeCheck())
                    TarkovRoomTones.SetOutdoorLevel(system, level);
            }
        }

        // the blender's fades; one shorter than its _blendDurationMult is applied at once instead of faded
        private static void DrawBlender(Audio.AmbientSubsystem.AmbientSoundBlender blender)
        {
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUI.BeginChangeCheck();
                var up = EditorGUILayout.FloatField(new GUIContent("Fade Up Outdoors (s)", "How long the outdoor ambience takes to come back to full on leaving a room."),
                                                    blender._fadeOutDuration);
                var down = EditorGUILayout.FloatField(new GUIContent("Fade Down Indoors (s)", "How long the outdoor ambience takes to fade to the room's level away from its openings."),
                                                      blender._fadeInDuration);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(blender, "Ambience fades");
                    blender._fadeOutDuration = Mathf.Max(0f, up);
                    blender._fadeInDuration = Mathf.Max(0f, down);
                    EditorUtility.SetDirty(blender);
                }
                if (blender._fadeOutDuration < blender._blendDurationMult || blender._fadeInDuration < blender._blendDurationMult)
                {
                    EditorGUILayout.HelpBox($"The game doesn't fade at all under {blender._blendDurationMult:0.##} s: the outdoor ambience jumps " +
                                            "(to full volume on leaving a room).", MessageType.Warning);
                    if (GUILayout.Button("Fade Smoothly"))
                    {
                        Undo.RecordObject(blender, "Ambience fades");
                        blender._fadeOutDuration = Mathf.Max(blender._fadeOutDuration, TarkovAmbientAudioSystem.SmoothFadeOut, blender._blendDurationMult);
                        blender._fadeInDuration = Mathf.Max(blender._fadeInDuration, blender._blendDurationMult);
                        EditorUtility.SetDirty(blender);
                        GUI.changed = true;
                    }
                }
            }
        }

        private void BakeAcoustics(Scene scene, TarkovAcoustics.BakeMode mode)
        {
            EditorApplication.delayCall += () => TarkovAcoustics.Bake(scene, mode, (ok, message) =>
            {
                if (ok)
                    Debug.Log($"[Tarkov SDK] Acoustics: {message}");
                else
                    Debug.LogWarning($"[Tarkov SDK] Acoustics: {message}");
                _acousticStatus = null;
                EditorSceneManager.MarkSceneDirty(scene);
                Repaint();
            });
        }

        // rooms over the whole map (its closed scenes are opened), in its one spatial audio system
        internal static void Generate(Scene scene)
        {
            TarkovMapScenes.OpenMapScenes(scene);
            scene = TarkovSpatialAudio.AudioScene(scene);
            var tones = TarkovRoomTones.Capture(TarkovSpatialAudio.Find(scene));
            var result = TarkovAudioRooms.Generate(scene);
            if (result == null)
                return;
            TarkovRoomTones.SyncBunkers(scene, TarkovSpatialAudio.Find(scene));
            TarkovRoomTones.Apply(TarkovSpatialAudio.Find(scene), tones);
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log($"[Tarkov SDK] Spatial audio: {result}. Check them in the Scene view (select the system); fix room types for their reverb, " +
                      "then Bake Sound Routes or build the map.");
        }

        private static void BakeRoutes(Scene scene)
        {
            scene = TarkovSpatialAudio.AudioScene(scene);
            var report = new TarkovScenePreparer.Report();
            var path = TarkovSpatialAudio.Prepare(scene, report, forceRoutes: true);
            foreach (var w in report.Warnings)
                Debug.LogWarning("[Tarkov SDK] " + w);
            foreach (var e in report.Errors)
                Debug.LogError("[Tarkov SDK] " + e);
            if (path != null)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                var info = TarkovSpatialAudio.Find(scene)?._locationInfo;
                Debug.Log($"[Tarkov SDK] Spatial audio: routes baked to {path} ({info?.roomsCount} rooms, {info?.portalsCount} portals, " +
                          $"{info?.roomPairsCount} room pairs, {info?.routesCount} routes).");
            }
        }
    }

    internal static class TarkovAudioGui
    {
        // the room types a mapper picks from; each has its own reverb and echo in the mixer effects
        public static readonly (EAudioRoomTypeMask Type, string Label)[] Types =
        {
            (EAudioRoomTypeMask.OutdoorGeneric, "Outdoor"),
            (EAudioRoomTypeMask.OutdoorCommon, "Outdoor (common)"),
            (EAudioRoomTypeMask.OutdoorVehicle, "Outdoor vehicle"),
            (EAudioRoomTypeMask.IndoorGeneric, "Indoor"),
            (EAudioRoomTypeMask.IndoorConcrete, "Indoor concrete"),
            (EAudioRoomTypeMask.IndoorBrick, "Indoor brick"),
            (EAudioRoomTypeMask.IndoorPlasterBoard, "Indoor plasterboard"),
            (EAudioRoomTypeMask.IndoorWoodThin, "Indoor thin wood"),
            (EAudioRoomTypeMask.IndoorWoodThick, "Indoor thick wood"),
            (EAudioRoomTypeMask.IndoorMetalThin, "Indoor thin metal"),
            (EAudioRoomTypeMask.IndoorMetalThick, "Indoor thick metal"),
            (EAudioRoomTypeMask.IndoorFabric, "Indoor fabric (soft)"),
            (EAudioRoomTypeMask.IndoorTransparent, "Indoor glass"),
            (EAudioRoomTypeMask.IndoorStairs, "Stairs"),
            (EAudioRoomTypeMask.IndoorBasement, "Basement"),
            (EAudioRoomTypeMask.IndoorIsolated, "Bunker (Is Bunker indoor trigger)"),
            (EAudioRoomTypeMask.IndoorVehicle, "Inside a vehicle"),
        };

        public static EAudioRoomTypeMask TypePopup(EAudioRoomTypeMask current, params GUILayoutOption[] options)
        {
            int index = Array.FindIndex(Types, t => t.Type == current);
            var labels = Types.Select(t => t.Label).ToArray();
            if (index < 0)
            {
                labels = labels.Concat(new[] { current.ToString() }).ToArray();
                index = labels.Length - 1;
            }
            int next = EditorGUILayout.Popup(index, labels, options);
            return next < Types.Length ? Types[next].Type : current;
        }

        public static EAudioRoomTypeMask TypePopup(GUIContent label, EAudioRoomTypeMask current)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(label);
                return TypePopup(current);
            }
        }

        // the reverb and delay the game sets for a room type (MixerEffectsDataSO, keyed by type name),
        // editable; every room of the type shares them
        public static void DrawTypeEffects(MixerEffectsDataSO effects, EAudioRoomTypeMask type)
        {
            if (effects == null)
            {
                EditorGUILayout.HelpBox("No ambient audio system with mixer effects: rooms use the mixer's defaults. Add the game system in Map > Ambient Sounds...", MessageType.None);
                return;
            }
            var so = new SerializedObject(effects);
            foreach (var (field, title) in new[] { ("reverbMixerData", "Reverb"), ("delayMixerData", "Echo (delay)") })
            {
                var asset = so.FindProperty(field).objectReferenceValue;
                if (asset == null)
                    continue;
                EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
                var data = new SerializedObject(asset);
                var it = data.GetIterator();
                bool any = false;
                using (new EditorGUI.IndentLevelScope())
                    for (bool enter = true; it.NextVisible(enter); enter = false)
                    {
                        var name = it.FindPropertyRelative("paramName");
                        if (it.propertyType != SerializedPropertyType.Generic || name == null)
                            continue;
                        var keys = it.FindPropertyRelative("parametersByRoom._keys");
                        var values = it.FindPropertyRelative("parametersByRoom._values");
                        int index = -1;
                        for (int i = 0; i < keys.arraySize; i++)
                            if (keys.GetArrayElementAtIndex(i).stringValue == type.ToString())
                                index = i;
                        if (index < 0 || index >= values.arraySize)
                            continue;
                        any = true;
                        EditorGUILayout.PropertyField(values.GetArrayElementAtIndex(index), new GUIContent(ObjectNames.NicifyVariableName(it.name), name.stringValue));
                    }
                if (!any)
                    EditorGUILayout.LabelField("   none set for this type (the mixer keeps its last values)", EditorStyles.miniLabel);
                data.ApplyModifiedProperties();
            }
        }
    }

    [CustomEditor(typeof(SpatialAudioRoom))]
    public class SpatialAudioRoomEditor : UnityEditor.Editor
    {
        private static bool _showRaw;

        public override void OnInspectorGUI()
        {
            var room = (SpatialAudioRoom)target;
            serializedObject.Update();
            EditorGUILayout.LabelField(TarkovSpatialAudio.IsOutdoor(room) ? "Outdoor audio room" : "Indoor audio room", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            var type = TarkovAudioGui.TypePopup(new GUIContent("Type", "Picks the room's reverb and echo (below), and whether it is indoors. Bunker also makes it isolated, " +
                                                                "with thick walls, no outdoor ambience and the scene's bunker tone."), room._type);
            if (EditorGUI.EndChangeCheck())
            {
                TarkovRoomTones.SetType(room, type);
                serializedObject.Update();
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("WallOcclusion"), new GUIContent("Wall Occlusion", "How much its walls muffle sound going through them (0-1)."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("Isolated"), new GUIContent("Isolated", "Sound from rooms not directly connected to it isn't heard."));
            var ambient = serializedObject.FindProperty("AmbientData");
            if (ambient != null)
            {
                EditorGUILayout.LabelField("Ambience", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(ambient.FindPropertyRelative("OutdoorAmbientVolume"), new GUIContent("Outdoor Ambience Volume", "How loud the outdoor ambience is in here (0-1)."));
                EditorGUILayout.PropertyField(ambient.FindPropertyRelative("PrecipitationVolume"), new GUIContent("Rain Volume", "How loud the rain is in here (0-1)."));
                EditorGUILayout.PropertyField(ambient.FindPropertyRelative("RoomTone"), new GUIContent("Room Tone", "A loop played while the player is in the room (hum, machinery)."));
                EditorGUILayout.PropertyField(ambient.FindPropertyRelative("RoomToneVolume"));
            }
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"{room._type} reverb and echo (shared by every room of this type)", EditorStyles.boldLabel);
            var system = TarkovAmbientAudioSystem.Find(room.gameObject.scene);
            TarkovAudioGui.DrawTypeEffects(system != null ? TarkovAmbientAudioSystem.MixerEffects(system) : null, room._type);

            EditorGUILayout.Space();
            int areas = room.Areas?.Count(a => a != null) ?? 0;
            int connections = room.roomConnections?.Count ?? 0;
            EditorGUILayout.LabelField($"{areas} trigger area(s), {connections} connection(s) through portals", EditorStyles.miniLabel);
            if (GUILayout.Button(new GUIContent("Add Area", "Another box of the room, at its centre; move and scale it.")))
                EditorApplication.delayCall += () =>
                {
                    if (room == null)
                        return;
                    Undo.RecordObject(room, "Add audio area");
                    var area = TarkovSpatialAudio.AddArea(room, new Bounds(TarkovSpatialAudio.RoomBounds(room).center, new Vector3(4f, 3f, 4f)));
                    Undo.RegisterCreatedObjectUndo(area.gameObject, "Add audio area");
                    Selection.activeGameObject = area.gameObject;
                };
            _showRaw = EditorGUILayout.Foldout(_showRaw, "All fields");
            if (_showRaw)
                DrawDefaultInspector();
        }
    }

    [CustomEditor(typeof(SpatialAudioPortal))]
    public class SpatialAudioPortalEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var portal = (SpatialAudioPortal)target;
            var rooms = portal._connectedRooms ?? new System.Collections.Generic.List<SpatialAudioRoom>();
            EditorGUILayout.LabelField(rooms.Count == 2 ? $"{rooms[0]?.name} ↔ {rooms[1]?.name}" : "Not between two rooms", EditorStyles.boldLabel);
            string what = portal.portalType == BaseSpatialAudioPortal.PortalType.Opening ? "a door: open and closed with the door at it"
                : portal.portalType == BaseSpatialAudioPortal.PortalType.Window ? "a window: closed until the breakable window at it breaks"
                : "an opening, always open";
            EditorGUILayout.LabelField($"This portal is {what}. Build Map links it to the nearest door or window within 2 m.", EditorStyles.wordWrappedMiniLabel);
            DrawDefaultInspector();
        }
    }

    // rooms, areas and portals in the scene view
    public static class TarkovAudioGizmos
    {
        private static readonly Color Indoor = new Color(0.3f, 0.75f, 1f);
        private static readonly Color Outdoor = new Color(0.4f, 1f, 0.5f);
        private static readonly Color DoorColor = new Color(1f, 0.6f, 0.2f);
        private static readonly Color WindowColor = new Color(0.6f, 0.9f, 1f);
        private static readonly Color OpeningColor = new Color(0.5f, 1f, 0.6f);

        private static bool SystemSelected(Component c)
        {
            var selected = Selection.activeTransform;
            if (selected == null)
                return false;
            var system = c.GetComponentInParent<SpatialAudioSystem>(true);
            return system != null && (selected == system.transform || selected.IsChildOf(system.transform)) && !(selected.IsChildOf(c.transform) || c.transform == selected);
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.Pickable)]
        private static void DrawArea(AudioTriggerArea area, GizmoType type)
        {
            var room = area.GetComponentInParent<SpatialAudioRoom>(true);
            bool selected = (type & GizmoType.Selected) != 0 || (room != null && Selection.activeGameObject == room.gameObject);
            if (!selected && !SystemSelected(area))
                return;
            bool outdoor = room != null && TarkovSpatialAudio.IsOutdoor(room);
            if (outdoor && !selected)
                return;
            var color = outdoor ? Outdoor : Indoor;
            Gizmos.matrix = area.transform.localToWorldMatrix;
            Gizmos.color = new Color(color.r, color.g, color.b, selected ? 0.12f : 0.04f);
            Gizmos.DrawCube(Vector3.zero, Vector3.one);
            Gizmos.color = new Color(color.r, color.g, color.b, selected ? 0.9f : 0.35f);
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
            Gizmos.matrix = Matrix4x4.identity;
            if (selected && room != null)
                Handles.Label(area.transform.position, $"{room.name}\n{room._type}", EditorStyles.miniBoldLabel);
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.Pickable)]
        private static void DrawPortal(BaseSpatialAudioPortal portal, GizmoType type)
        {
            bool selected = (type & GizmoType.Selected) != 0;
            if (!selected && !SystemSelected(portal))
                return;
            var color = portal.portalType == BaseSpatialAudioPortal.PortalType.Opening ? DoorColor
                : portal.portalType == BaseSpatialAudioPortal.PortalType.Window ? WindowColor : OpeningColor;
            var t = portal.transform;
            var right = t.right * t.localScale.x * 0.5f;
            var up = t.up * t.localScale.y * 0.5f;
            var c = t.position;
            var corners = new[] { c - right - up, c + right - up, c + right + up, c - right + up };
            Handles.color = new Color(color.r, color.g, color.b, 0.25f);
            Handles.DrawAAConvexPolygon(corners);
            Handles.color = color;
            Handles.DrawAAPolyLine(3f, corners[0], corners[1], corners[2], corners[3], corners[0]);
            Handles.DrawLine(c, c + t.forward * 0.4f);
            if (!selected)
                return;
            foreach (var room in portal._connectedRooms ?? new System.Collections.Generic.List<SpatialAudioRoom>())
                if (room != null && !TarkovSpatialAudio.IsOutdoor(room))
                    Handles.DrawDottedLine(c, TarkovSpatialAudio.RoomBounds(room).center, 3f);
            Handles.Label(c + up, portal.portalName + (portal is SpatialAudioPortal p && !string.IsNullOrEmpty(p.DoorID) ? $"\n{p.DoorID}" : ""), EditorStyles.miniBoldLabel);
        }
    }
}
