using System.Linq;
using Audio.AmbientSubsystem;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Slot = TarkovSDK.Editor.TarkovAmbientAudioSystem.Slot;

namespace TarkovSDK.Editor
{
    // Ambient sound for a map, in one of two setups:
    //
    //  Simple beds   each slot is a looping 2D AudioSource under "SDK_Ambience", named with tags
    //                CustomMapLoader reads in the raid: it routes the source into the game's
    //                outdoor/indoor ambience mixer and fades [Day]/[Night] beds with daylight and
    //                [Rain] beds with the rain.
    //  Game system   the game's own AmbientAudioSystem (TarkovAmbientAudioSystem): day/night loops on
    //                the game's schedule, wind and rain by weather and season, random sound event
    //                groups, per-room reverb. Plain beds (room tone) still go under it.
    public class TarkovAmbienceWindow : EditorWindow
    {
        public const string RootName = "SDK_Ambience";

        private static readonly (string Label, string Tags, string Help)[] Beds =
        {
            ("Outdoor Day", "[Outdoor][Day]", "Birds, distant city: fades out at night."),
            ("Outdoor Night", "[Outdoor][Night]", "Crickets, night wind: fades in at night."),
            ("Outdoor Wind", "[Outdoor]", "Always on outdoors."),
            ("Light Rain (outdoors)", "[Outdoor][RainLight]", "Plays while the rain is light."),
            ("Rain (outdoors)", "[Outdoor][RainMedium]", "Plays while the rain is medium."),
            ("Heavy Rain (outdoors)", "[Outdoor][RainHeavy]", "Plays while the rain is heavy."),
            ("Indoor", "[Indoor]", "Room tone inside buildings (needs indoor triggers)."),
            ("Light Rain (indoors)", "[Indoor][RainLight]", "Rain on the roof while the rain is light."),
            ("Rain (indoors)", "[Indoor][RainMedium]", "Rain on the roof while the rain is medium."),
            ("Heavy Rain (indoors)", "[Indoor][RainHeavy]", "Rain on the roof while the rain is heavy."),
        };

        // simple bed -> the game system's slot holding the same sound
        private static readonly (string Bed, Slot Slot)[] BedSlots =
        {
            ("Outdoor Day", Slot.Day), ("Outdoor Night", Slot.Night), ("Outdoor Wind", Slot.WindOutdoor),
            ("Light Rain (outdoors)", Slot.RainOutdoorLight), ("Rain (outdoors)", Slot.RainOutdoorMedium), ("Heavy Rain (outdoors)", Slot.RainOutdoorHeavy),
            ("Light Rain (indoors)", Slot.RainIndoorLight), ("Rain (indoors)", Slot.RainIndoorMedium), ("Heavy Rain (indoors)", Slot.RainIndoorHeavy),
        };

        // beds made before rain had intensities: one clip for every intensity
        private static readonly (string Name, Slot[] Slots)[] LegacyBeds =
        {
            ("[Outdoor][Rain] Outdoor Rain", new[] { Slot.RainOutdoorLight, Slot.RainOutdoorMedium, Slot.RainOutdoorHeavy }),
            ("[Indoor][Rain] Indoor Rain", new[] { Slot.RainIndoorLight, Slot.RainIndoorMedium, Slot.RainIndoorHeavy }),
        };

        // bed label -> file name fragments, most specific first, and a default volume
        private static readonly (string Label, string[] Names, float Volume)[] AutoFill =
        {
            ("Outdoor Day", new[] { "openspace_day", "outdoor_day", "_day" }, 0.6f),
            ("Outdoor Night", new[] { "openspace_night", "outdoor_night", "_night" }, 0.6f),
            ("Outdoor Wind", new[] { "wind_weak_outdoor", "wind" }, 0.5f),
            ("Light Rain (outdoors)", new[] { "rain_outdoor_weak", "rain_outdoor_light" }, 0.6f),
            ("Rain (outdoors)", new[] { "rain_outdoor_medium", "rain_outdoor" }, 0.7f),
            ("Heavy Rain (outdoors)", new[] { "rain_outdoor_strong", "rain_outdoor_heavy" }, 0.8f),
            ("Indoor", new[] { "residential", "room", "indoor_tone" }, 0.4f),
            ("Light Rain (indoors)", new[] { "rain_indoor_weak", "rain_indoor_light" }, 0.5f),
            ("Rain (indoors)", new[] { "rain_indoor_medium", "rain_indoor" }, 0.6f),
            ("Heavy Rain (indoors)", new[] { "rain_indoor_strong", "rain_indoor_heavy" }, 0.7f),
        };

        private Vector2 _scroll;

        [MenuItem("Tarkov SDK/Map/Ambient Sounds...", priority = 121)]
        private static void Open()
        {
            var window = GetWindow<TarkovAmbienceWindow>(true, "Ambient Sounds");
            window.minSize = new Vector2(440f, 360f);
        }

        private void OnGUI()
        {
            var scene = SceneManager.GetActiveScene();
            var system = TarkovAmbientAudioSystem.Find(scene);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Scene: " + scene.name, EditorStyles.boldLabel);

            var folder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(scene.path) ?? "Assets", "Sounds").Replace('\\', '/');
            using (new EditorGUI.DisabledScope(!AssetDatabase.IsValidFolder(folder)))
                if (GUILayout.Button(new GUIContent($"Auto-Fill From {folder}", "Picks clips by name: openspace_day, openspace_night, wind, rain_outdoor_weak/medium/strong, residential, rain_indoor_weak/medium/strong.")))
                    EditorApplication.delayCall += () => AutoFillFrom(scene, folder);
            EditorGUILayout.Space();

            int indoor = UnityEngine.Object.FindObjectsOfType<EFT.EnvironmentEffect.IndoorTrigger>(true).Length;
            EditorGUILayout.LabelField(indoor > 0
                ? $"Indoor triggers: {indoor}. Regenerate after changing buildings; hand-placed triggers are kept."
                : "Indoor triggers: none, so the whole map counts as outdoors (no indoor muffling or room tone).", EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button(new GUIContent(TarkovIndoorTriggers.Find() != null ? "Regenerate Indoor Triggers" : "Generate Indoor Triggers",
                    "Scans the map for rooms (a ceiling with walls around it) and covers each with indoor trigger boxes.")))
                EditorApplication.delayCall += TarkovIndoorTriggers.GenerateMenu;
            EditorGUILayout.Space();

            if (system != null)
                DrawSystem(scene, system);
            else
                DrawBeds(scene);

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("For a sound at one place (a generator, birds in a tree), use GameObject > Tarkov SDK > Ambient Sound Point.", MessageType.None);
            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------ simple beds

        private void DrawBeds(Scene scene)
        {
            EditorGUILayout.LabelField("Simple beds", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Looping clips that play everywhere (2D) through the game's ambience mixers, so they follow its volume " +
                                       "and get muffled indoors.", EditorStyles.wordWrappedMiniLabel);
            foreach (var (label, tags, help) in Beds)
                DrawBed(scene, label, tags, help);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("The game's Ambient Audio System", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("What retail maps use: day and night loops crossfaded on the game's schedule, wind and rain chosen by " +
                                       "the weather and season, random sound events grouped by day and night, and per-room reverb. " +
                                       "The clips above move into it.", EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button("Use The Game's Ambient Audio System"))
                EditorApplication.delayCall += () => Convert(scene);
        }

        private static void DrawBed(Scene scene, string label, string tags, string help)
        {
            var name = $"{tags} {label}";
            var root = BedRoot(scene, false);
            var source = root != null ? root.GetComponentsInChildren<AudioSource>(true).FirstOrDefault(s => s.name == name) : null;
            EditorGUILayout.LabelField(new GUIContent(label, help), EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var clip = (AudioClip)EditorGUILayout.ObjectField(source != null ? source.clip : null, typeof(AudioClip), false);
                float volume = EditorGUILayout.Slider(source != null ? source.volume : 0.5f, 0f, 1f, GUILayout.Width(160f));
                if (EditorGUI.EndChangeCheck())
                    SetBed(scene, name, clip, volume);
            }
            EditorGUILayout.LabelField(help, EditorStyles.miniLabel);
        }

        // ------------------------------------------------------------ game system

        private void DrawSystem(Scene scene, GameObject system)
        {
            EditorGUILayout.LabelField("The game's Ambient Audio System", EditorStyles.boldLabel);
            var data = TarkovAmbientAudioSystem.SoundData(system);
            if (data == null)
            {
                EditorGUILayout.HelpBox("Its sound data asset is missing. Remove the system and add it again.", MessageType.Error);
            }
            else
            {
                EditorGUILayout.LabelField("Every season uses these clips. Loops: import them with Load Type Compressed In Memory.", EditorStyles.wordWrappedMiniLabel);
                foreach (var (slot, label, help) in TarkovAmbientAudioSystem.Slots)
                {
                    EditorGUI.BeginChangeCheck();
                    var clip = (AudioClip)EditorGUILayout.ObjectField(new GUIContent(label, help), TarkovAmbientAudioSystem.GetClip(data, slot), typeof(AudioClip), false);
                    if (EditorGUI.EndChangeCheck())
                        TarkovAmbientAudioSystem.SetClip(data, slot, clip);
                }
            }

            EditorGUILayout.Space();
            var (bedLabel, bedTags, bedHelp) = Beds.First(b => b.Label == "Indoor");
            DrawBed(scene, bedLabel, bedTags, bedHelp);

            // reverb
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Reverb", EditorStyles.boldLabel);
            var effects = TarkovAmbientAudioSystem.MixerEffects(system);
            if (TarkovAmbientAudioSystem.HasMixerEffects(effects))
            {
                EditorGUILayout.LabelField("Customs' indoor/outdoor ambient reverb and delay (extracted from your game).", EditorStyles.wordWrappedMiniLabel);
            }
            else if (System.IO.File.Exists(TarkovAmbientAudioSystem.MixerEffectsJson))
            {
                EditorGUILayout.HelpBox("Reverb data is extracted but not applied to this map.", MessageType.Info);
                if (GUILayout.Button("Apply Extracted Reverb Data"))
                    EditorApplication.delayCall += () => TarkovAmbientAudioSystem.FillMixerEffects(effects);
            }
            else
            {
                EditorGUILayout.HelpBox("No reverb data: the ambience plays dry. Extract Customs' settings from your own game " +
                                        "(uses the game folder and Python set in Time of Day / Weather; needs pip install UnityPy).", MessageType.Info);
                if (GUILayout.Button("Extract Reverb Data From Game"))
                    EditorApplication.delayCall += () =>
                    {
                        if (TarkovTimeOfDayWindow.RunExtractor("Extracting the ambient mixer settings...", "--mixer-only"))
                            TarkovAmbientAudioSystem.FillMixerEffects(effects);
                    };
            }

            // random sound event groups
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Random sound events", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Day plays only by day, Night only at night, Always all the time, Indoor through the indoor mixer.",
                EditorStyles.wordWrappedMiniLabel);
            foreach (var group in system.GetComponentsInChildren<AmbientSoundPlayerGroup>(true))
                using (new EditorGUILayout.HorizontalScope())
                {
                    int count = group.GetComponentsInChildren<SeasonAmbientSoundPlayer>(true).Length;
                    EditorGUILayout.LabelField($"{TarkovAmbientAudioSystem.GroupLabel(group)} ({count})", GUILayout.Width(120f));
                    if (GUILayout.Button("Select", GUILayout.Width(60f)))
                        Selection.activeGameObject = group.gameObject;
                    if (GUILayout.Button("Add Random Sound Event"))
                    {
                        var parent = group.transform;
                        EditorApplication.delayCall += () =>
                        {
                            var player = TarkovRandomSoundEvents.Create(parent, true);
                            Undo.RegisterCreatedObjectUndo(player.gameObject, "Random sound event");
                            player.name = "Random Sound Event";
                            Selection.activeGameObject = player.gameObject;
                            EditorSceneManager.MarkSceneDirty(scene);
                        };
                    }
                }

            EditorGUILayout.Space();
            if (GUILayout.Button("Remove The Game's Ambient Audio System"))
                EditorApplication.delayCall += () =>
                {
                    if (!EditorUtility.DisplayDialog("Ambient Sounds", "Remove the ambient audio system, its random sound events and beds? " +
                                                                       "Its data assets stay in the AmbientAudio folder.", "Remove", "Cancel"))
                        return;
                    Undo.DestroyObjectImmediate(system);
                    EditorSceneManager.MarkSceneDirty(scene);
                };
        }

        // moves the simple beds' clips into a new game system
        private static void Convert(Scene scene)
        {
            var system = TarkovAmbientAudioSystem.Create(scene);
            var data = TarkovAmbientAudioSystem.SoundData(system);
            var beds = FindSimpleRoot(scene);
            if (beds != null)
            {
                foreach (var (bed, slot) in BedSlots)
                {
                    var tags = Beds.First(b => b.Label == bed).Tags;
                    var source = beds.GetComponentsInChildren<AudioSource>(true).FirstOrDefault(s => s.name == $"{tags} {bed}");
                    if (source == null)
                        continue;
                    if (source.clip != null)
                        TarkovAmbientAudioSystem.SetClip(data, slot, source.clip);
                    Undo.DestroyObjectImmediate(source.gameObject);
                }
                foreach (var (legacy, slots) in LegacyBeds)
                {
                    var source = beds.GetComponentsInChildren<AudioSource>(true).FirstOrDefault(s => s.name == legacy);
                    if (source == null)
                        continue;
                    if (source.clip != null)
                        foreach (var slot in slots)
                            if (TarkovAmbientAudioSystem.GetClip(data, slot) == null)
                                TarkovAmbientAudioSystem.SetClip(data, slot, source.clip);
                    Undo.DestroyObjectImmediate(source.gameObject);
                }
                var bedRoot = system.transform.Find("Beds");
                foreach (Transform child in beds.transform.Cast<Transform>().ToList())
                    Undo.SetTransformParent(child, bedRoot, "Ambient beds");
                Undo.DestroyObjectImmediate(beds);
            }
            Selection.activeGameObject = system;
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log("[Tarkov SDK] Ambient sounds: the game's Ambient Audio System added" + (TarkovAmbientAudioSystem.HasMixerEffects(TarkovAmbientAudioSystem.MixerEffects(system))
                ? " with Customs' reverb data" : "; extract the reverb data in Ambient Sounds for indoor/outdoor reverb"));
        }

        // ------------------------------------------------------------ shared

        private static void AutoFillFrom(Scene scene, string folder)
        {
            var clips = AssetDatabase.FindAssets("t:AudioClip", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath).ToList();
            var system = TarkovAmbientAudioSystem.Find(scene);
            var data = TarkovAmbientAudioSystem.SoundData(system);
            int filled = 0;
            foreach (var (label, names, volume) in AutoFill)
            {
                string path = null;
                foreach (var fragment in names)
                {
                    path = clips.FirstOrDefault(p => System.IO.Path.GetFileNameWithoutExtension(p).ToLowerInvariant().Contains(fragment));
                    if (path != null)
                        break;
                }
                if (path == null)
                    continue;
                ConfigureImport(path);
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                var mapped = BedSlots.Where(b => b.Bed == label).Select(b => (Slot?)b.Slot).FirstOrDefault();
                if (data != null && mapped.HasValue)
                    TarkovAmbientAudioSystem.SetClip(data, mapped.Value, clip);
                else if (system == null || label == "Indoor")
                    SetBed(scene, $"{Beds.First(s => s.Label == label).Tags} {label}", clip, volume);
                else
                    continue;
                filled++;
            }
            Debug.Log($"[Tarkov SDK] Ambient sounds: filled {filled} slot(s) from {folder}");
        }

        // long loops: compressed in memory keeps them small in the bundle without streaming hitches
        private static void ConfigureImport(string path)
        {
            if (!(AssetImporter.GetAtPath(path) is AudioImporter importer))
                return;
            var settings = importer.defaultSampleSettings;
            if (settings.loadType == AudioClipLoadType.CompressedInMemory && settings.compressionFormat == AudioCompressionFormat.Vorbis)
                return;
            settings.loadType = AudioClipLoadType.CompressedInMemory;
            settings.compressionFormat = AudioCompressionFormat.Vorbis;
            settings.quality = 0.6f;
            importer.defaultSampleSettings = settings;
            importer.loadInBackground = true;
            importer.SaveAndReimport();
        }

        private static GameObject FindSimpleRoot(Scene scene) => scene.GetRootGameObjects().FirstOrDefault(g => g.name == RootName);

        // where plain beds live: the game system's "Beds", or SDK_Ambience
        private static Transform BedRoot(Scene scene, bool create)
        {
            var system = TarkovAmbientAudioSystem.Find(scene);
            if (system != null)
            {
                var beds = system.transform.Find("Beds");
                if (beds == null && create)
                {
                    beds = new GameObject("Beds").transform;
                    beds.SetParent(system.transform, false);
                    Undo.RegisterCreatedObjectUndo(beds.gameObject, "Ambient sounds");
                }
                return beds;
            }
            var root = FindSimpleRoot(scene);
            if (root == null && create)
            {
                root = new GameObject(RootName);
                SceneManager.MoveGameObjectToScene(root, scene);
                Undo.RegisterCreatedObjectUndo(root, "Ambient sounds");
            }
            return root != null ? root.transform : null;
        }

        private static void SetBed(Scene scene, string name, AudioClip clip, float volume)
        {
            var root = BedRoot(scene, clip != null);
            var source = root != null ? root.GetComponentsInChildren<AudioSource>(true).FirstOrDefault(s => s.name == name) : null;
            if (clip == null)
            {
                if (source != null)
                    Undo.DestroyObjectImmediate(source.gameObject);
                EditorSceneManager.MarkSceneDirty(scene);
                return;
            }
            if (source == null)
            {
                var go = new GameObject(name);
                go.transform.SetParent(root, false);
                source = go.AddComponent<AudioSource>();
                Undo.RegisterCreatedObjectUndo(go, "Ambient sound");
            }
            Undo.RecordObject(source, "Ambient sound");
            source.clip = clip;
            source.volume = volume;
            source.loop = true;
            source.playOnAwake = true;
            source.spatialBlend = 0f;
            source.priority = 32;
            source.dopplerLevel = 0f;
            EditorUtility.SetDirty(source);
            EditorSceneManager.MarkSceneDirty(scene);
        }

        [MenuItem("GameObject/Tarkov SDK/Ambient Sound Point", priority = 90)]
        private static void MenuSoundPoint(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p =>
        {
            var go = new GameObject("[Nature] Ambient Sound Point");
            if (p != null)
                go.transform.SetParent(p, false);
            var source = go.AddComponent<AudioSource>();
            source.loop = true;
            source.playOnAwake = true;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = 2f;
            source.maxDistance = 50f;
            source.dopplerLevel = 0f;
            source.volume = 0.6f;
            return go;
        });
    }
}
