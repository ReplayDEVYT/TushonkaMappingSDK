using System;
using System.IO;
using System.Linq;
using Audio.AmbientSubsystem;
using Audio.AmbientSubsystem.Data;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // The game's own ambient audio system (Audio.AmbientSubsystem.AmbientAudioSystem), laid out as on
    // Customs' sound scene:
    //
    //   AmbientAudioSystem          season sound data (day/night loops, wind, rain) and mixer effects
    //     Subsystems
    //       DayTimeSoundBlender     crossfades the day and night loops at the game's hours (5:15, 21:15)
    //       WindAmbientBlender      wind loop, by wind speed and indoor/outdoor
    //       PrecipitationBlenders   rain loops outdoors and indoors, by rain intensity
    //     AmbientPlayers            random sound event groups: Day, Night, Always, Indoor
    //     Beds                      plain looping sources (room tone), as in the simple setup
    //
    // The game finds and starts it when the raid loads. What the SDK can't reference, the game's audio
    // mixer, is named instead: " @GroupName" at the end of an object's name, which CustomMapLoader
    // resolves to the game's mixer group of that name.
    public static class TarkovAmbientAudioSystem
    {
        public const string RootName = "AmbientAudioSystem";
        private const string MixerTag = " @";

        // the game's ESeasonStatus, EWindSpeed and RainController.ERainIntensity names, the keys of its dictionaries
        private static readonly string[] Seasons = { "Summer", "Autumn", "Winter", "Spring", "AutumnLate", "SpringEarly", "Storm" };
        private static readonly string[] WindSpeeds = { "Light", "Moderate", "Strong", "VeryStrong", "Hurricane" };
        private static readonly string[] RainIntensities = { "Low", "Med", "High" };

        // rain slots are one per RainController.ERainIntensity (Low, Med, High), in that order
        public enum Slot { Day, Night, WindOutdoor, WindIndoor, RainOutdoorLight, RainOutdoorMedium, RainOutdoorHeavy, RainIndoorLight, RainIndoorMedium, RainIndoorHeavy }

        public static readonly (Slot Slot, string Label, string Help)[] Slots =
        {
            (Slot.Day, "Outdoor Day", "Daytime loop (birds, distant city). Crossfades to night at 21:15."),
            (Slot.Night, "Outdoor Night", "Night loop (crickets). Crossfades to day at 5:15."),
            (Slot.WindOutdoor, "Wind (outdoors)", "Wind loop outdoors."),
            (Slot.WindIndoor, "Wind (indoors)", "Wind heard inside buildings (optional)."),
            (Slot.RainOutdoorLight, "Light Rain (outdoors)", "Outdoor rain loop while the rain is light."),
            (Slot.RainOutdoorMedium, "Rain (outdoors)", "Outdoor rain loop while the rain is medium."),
            (Slot.RainOutdoorHeavy, "Heavy Rain (outdoors)", "Outdoor rain loop while the rain is heavy."),
            (Slot.RainIndoorLight, "Light Rain (indoors)", "Rain on the roof, heard inside, while the rain is light."),
            (Slot.RainIndoorMedium, "Rain (indoors)", "Rain on the roof, heard inside, while the rain is medium."),
            (Slot.RainIndoorHeavy, "Heavy Rain (indoors)", "Rain on the roof, heard inside, while the rain is heavy."),
        };

        public static bool IsRain(Slot slot) => slot >= Slot.RainOutdoorLight;
        private static int RainIndex(Slot slot) => ((int)slot - (int)Slot.RainOutdoorLight) % 3;

        public static GameObject Find(Scene scene) =>
            scene.GetRootGameObjects().FirstOrDefault(g => g.GetComponent<AmbientAudioSystem>() != null);

        public static string MixerEffectsJson => Path.Combine(TarkovWeatherBuilder.ExtractedFolder, "mixer_effects.json");

        // ------------------------------------------------------------ build

        public static GameObject Create(Scene scene)
        {
            var root = new GameObject(RootName);
            SceneManager.MoveGameObjectToScene(root, scene);
            var system = root.AddComponent<AmbientAudioSystem>();
            var so = new SerializedObject(system);
            so.FindProperty("_ambientData").objectReferenceValue = CreateSoundData(scene);
            so.FindProperty("EffectsData").objectReferenceValue = CreateMixerEffects(scene);
            so.ApplyModifiedPropertiesWithoutUndo();

            var subsystems = Child(root.transform, "Subsystems");

            var dayTime = Child(subsystems, "DayTimeSoundBlender").gameObject.AddComponent<DayTimeAmbientBlender>();
            var blender = new SerializedObject(dayTime);
            blender.FindProperty("_outdoorAmbientDaySource").objectReferenceValue = LoopSource(dayTime.transform, "DaySource" + MixerTag + "AmbientOutDayBypass");
            blender.FindProperty("_outdoorAmbientNightSource").objectReferenceValue = LoopSource(dayTime.transform, "NightSource" + MixerTag + "AmbientOutNight");
            blender.FindProperty("_dayTimeRange").vector2Value = new Vector2(5.25f, 21.25f);
            blender.FindProperty("_dayToNightBlendDuration").floatValue = 1.5f;
            blender.FindProperty("_nightToDayBlendDuration").floatValue = 0.75f;
            blender.ApplyModifiedPropertiesWithoutUndo();

            // CustomMapLoader assigns the tagged mixer group to the blender, which passes it to its sources;
            // indoor and outdoor groups so the rain and wind follow the indoor/outdoor crossfade
            var wind = Child(subsystems, "WindAmbientBlender" + MixerTag + "PrecipitationAmbientOut").gameObject.AddComponent<WindAmbientBlender>();
            var windSo = new SerializedObject(wind);
            windSo.FindProperty("_windSource").objectReferenceValue = LoopSource(wind.transform, "WindSource");
            windSo.FindProperty("_windMixSource").objectReferenceValue = LoopSource(wind.transform, "WindMixSource");
            windSo.ApplyModifiedPropertiesWithoutUndo();

            var precipitation = Child(subsystems, "PrecipitationBlenders");
            Precipitation(precipitation, "OutdoorPrecipitationBlender" + MixerTag + "PrecipitationAmbientOut", EnvironmentType.Outdoor);
            Precipitation(precipitation, "IndoorPrecipitationBlender" + MixerTag + "PrecipitationAmbientIn", EnvironmentType.Indoor);

            var players = Child(root.transform, "AmbientPlayers");
            var controller = players.gameObject.AddComponent<AmbientSoundPlayerGroupController>();
            var day = Group(players, "Day" + MixerTag + "AmbientOutDayEffects");
            var night = Group(players, "Night" + MixerTag + "AmbientOutNightEffects");
            Group(players, "Always" + MixerTag + "CommonAmbOutEffects");
            Group(players, "Indoor" + MixerTag + "CommonAmbInEffects");
            var controllerSo = new SerializedObject(controller);
            SetList(controllerSo.FindProperty("_dayGroup"), day);
            SetList(controllerSo.FindProperty("_nightGroup"), night);
            controllerSo.ApplyModifiedPropertiesWithoutUndo();

            Child(root.transform, "Beds");
            Undo.RegisterCreatedObjectUndo(root, "Add ambient audio system");
            EditorSceneManager.MarkSceneDirty(scene);
            return root;
        }

        private static Transform Child(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        private static AudioSource LoopSource(Transform parent, string name)
        {
            var source = Child(parent, name).gameObject.AddComponent<AudioSource>();
            source.loop = true;
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.priority = 32;
            source.dopplerLevel = 0f;
            return source;
        }

        private static void Precipitation(Transform parent, string name, EnvironmentType environment)
        {
            var blender = Child(parent, name).gameObject.AddComponent<PrecipitationAmbientBlender>();
            var so = new SerializedObject(blender);
            so.FindProperty("_precipitationSource").objectReferenceValue = LoopSource(blender.transform, "PrecipitationSource");
            so.FindProperty("_precipitationMixSource").objectReferenceValue = LoopSource(blender.transform, "PrecipitationMixSource");
            so.FindProperty("_environment").intValue = (int)environment;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static AmbientSoundPlayerGroup Group(Transform parent, string name) =>
            Child(parent, name).gameObject.AddComponent<AmbientSoundPlayerGroup>();

        private static void SetList(SerializedProperty list, params UnityEngine.Object[] items)
        {
            list.arraySize = items.Length;
            for (int i = 0; i < items.Length; i++)
                list.GetArrayElementAtIndex(i).objectReferenceValue = items[i];
        }

        public static string GroupLabel(AmbientSoundPlayerGroup group)
        {
            int tag = group.name.IndexOf(MixerTag, StringComparison.Ordinal);
            return tag >= 0 ? group.name.Substring(0, tag) : group.name;
        }

        // ------------------------------------------------------------ season sound data

        private static string DataFolder(Scene scene)
        {
            var folder = Path.Combine(Path.GetDirectoryName(scene.path) ?? "Assets", "AmbientAudio").Replace('\\', '/');
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static SeasonAmbientSoundDataSO CreateSoundData(Scene scene)
        {
            var path = AssetDatabase.GenerateUniqueAssetPath($"{DataFolder(scene)}/{scene.name}_AmbientSoundData.asset");
            var data = ScriptableObject.CreateInstance<SeasonAmbientSoundDataSO>();
            AssetDatabase.CreateAsset(data, path);
            var precipitation = ScriptableObject.CreateInstance<PrecipitationSoundStorageSO>();
            precipitation.name = "Precipitation";
            AssetDatabase.AddObjectToAsset(precipitation, data);
            var so = new SerializedObject(data);
            so.FindProperty("_precipitationSoundStorageSo").objectReferenceValue = precipitation;
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            return data;
        }

        public static SeasonAmbientSoundDataSO SoundData(GameObject root) =>
            root != null ? new SerializedObject(root.GetComponent<AmbientAudioSystem>()).FindProperty("_ambientData").objectReferenceValue as SeasonAmbientSoundDataSO : null;

        // every season, wind speed and rain intensity gets the same clip: the game picks by season and weather
        public static AudioClip GetClip(SeasonAmbientSoundDataSO data, Slot slot)
        {
            if (data == null)
                return null;
            var value = FirstValue(Dictionary(data, slot, out _, create: false));
            if (value == null)
                return null;
            switch (slot)
            {
                case Slot.Day: return value.FindPropertyRelative("DayAmbientClip").objectReferenceValue as AudioClip;
                case Slot.Night: return value.FindPropertyRelative("NightAmbientClip").objectReferenceValue as AudioClip;
                default:
                    var inner = value.FindPropertyRelative("_values");
                    int index = IsRain(slot) ? RainIndex(slot) : 0;
                    return inner != null && inner.arraySize > index
                        ? inner.GetArrayElementAtIndex(index).FindPropertyRelative(IsIndoor(slot) ? "_indoorClip" : "_outdoorClip").objectReferenceValue as AudioClip
                        : null;
            }
        }

        public static void SetClip(SeasonAmbientSoundDataSO data, Slot slot, AudioClip clip)
        {
            var seasons = Dictionary(data, slot, out var owner, create: true);
            Undo.RecordObject(owner, "Ambient clip");
            EnsureKeys(seasons, Seasons);
            var values = seasons.FindPropertyRelative("_values");
            for (int s = 0; s < Seasons.Length; s++)
            {
                var value = values.GetArrayElementAtIndex(s);
                switch (slot)
                {
                    case Slot.Day:
                        value.FindPropertyRelative("DayAmbientClip").objectReferenceValue = clip;
                        break;
                    case Slot.Night:
                        value.FindPropertyRelative("NightAmbientClip").objectReferenceValue = clip;
                        break;
                    default:
                        var keys = IsRain(slot) ? RainIntensities : WindSpeeds;
                        EnsureKeys(value, keys);
                        var inner = value.FindPropertyRelative("_values");
                        // wind: the same clip at every speed; rain: one clip per intensity
                        for (int i = 0; i < keys.Length; i++)
                            if (!IsRain(slot) || i == RainIndex(slot))
                                inner.GetArrayElementAtIndex(i).FindPropertyRelative(IsIndoor(slot) ? "_indoorClip" : "_outdoorClip").objectReferenceValue = clip;
                        break;
                }
            }
            seasons.serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(owner);
        }

        private static bool IsIndoor(Slot slot) => slot == Slot.WindIndoor || slot >= Slot.RainIndoorLight;

        // the season dictionary a slot lives in, and the asset that holds it
        private static SerializedProperty Dictionary(SeasonAmbientSoundDataSO data, Slot slot, out UnityEngine.Object owner, bool create)
        {
            var so = new SerializedObject(data);
            owner = data;
            switch (slot)
            {
                case Slot.Day:
                case Slot.Night:
                    return so.FindProperty("_dayTimeAmbientSeasonClips");
                case Slot.WindOutdoor:
                case Slot.WindIndoor:
                    return so.FindProperty("_windSoundStorage._seasonPrecipitations");
                default:
                    var storage = so.FindProperty("_precipitationSoundStorageSo").objectReferenceValue as PrecipitationSoundStorageSO;
                    if (storage == null && !create)
                        return null;
                    if (storage == null)
                    {
                        storage = ScriptableObject.CreateInstance<PrecipitationSoundStorageSO>();
                        storage.name = "Precipitation";
                        AssetDatabase.AddObjectToAsset(storage, data);
                        so.FindProperty("_precipitationSoundStorageSo").objectReferenceValue = storage;
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                    owner = storage;
                    return new SerializedObject(storage).FindProperty("_storage._seasonPrecipitations");
            }
        }

        private static void EnsureKeys(SerializedProperty dictionary, string[] keys)
        {
            var keyList = dictionary.FindPropertyRelative("_keys");
            var values = dictionary.FindPropertyRelative("_values");
            bool same = keyList.arraySize == keys.Length && values.arraySize == keys.Length;
            for (int i = 0; same && i < keys.Length; i++)
                same = keyList.GetArrayElementAtIndex(i).stringValue == keys[i];
            if (same)
                return;
            keyList.arraySize = keys.Length;
            values.arraySize = keys.Length;
            for (int i = 0; i < keys.Length; i++)
                keyList.GetArrayElementAtIndex(i).stringValue = keys[i];
        }

        private static SerializedProperty FirstValue(SerializedProperty dictionary)
        {
            var values = dictionary?.FindPropertyRelative("_values");
            return values != null && values.arraySize > 0 ? values.GetArrayElementAtIndex(0) : null;
        }

        // ------------------------------------------------------------ mixer effects

        // per-room ambient reverb, delay and effect sends, from the author's own game (Customs) when
        // extracted; without, the game's mixer keeps its defaults
        private static MixerEffectsDataSO CreateMixerEffects(Scene scene)
        {
            var path = AssetDatabase.GenerateUniqueAssetPath($"{DataFolder(scene)}/{scene.name}_MixerEffects.asset");
            var effects = ScriptableObject.CreateInstance<MixerEffectsDataSO>();
            AssetDatabase.CreateAsset(effects, path);
            var reverb = SubAsset<ReverbMixerDataSO>(effects, "Reverb");
            var delay = SubAsset<DelayMixerDataSO>(effects, "Delay");
            var sends = SubAsset<GenericVolumeParamsMixerDataSO>(effects, "EffectsSends");
            var volumes = SubAsset<GenericVolumeParamsMixerDataSO>(effects, "EffectsVolumes");
            var so = new SerializedObject(effects);
            so.FindProperty("reverbMixerData").objectReferenceValue = reverb;
            so.FindProperty("delayMixerData").objectReferenceValue = delay;
            so.FindProperty("effectsSendsMixerData").objectReferenceValue = sends;
            so.FindProperty("effectsVolumesMixerData").objectReferenceValue = volumes;
            so.ApplyModifiedPropertiesWithoutUndo();
            FillMixerEffects(effects);
            return effects;
        }

        private static T SubAsset<T>(UnityEngine.Object main, string name) where T : ScriptableObject
        {
            var asset = ScriptableObject.CreateInstance<T>();
            asset.name = name;
            AssetDatabase.AddObjectToAsset(asset, main);
            return asset;
        }

        public static bool FillMixerEffects(MixerEffectsDataSO effects)
        {
            if (effects == null || !File.Exists(MixerEffectsJson))
                return false;
            var json = JObject.Parse(File.ReadAllText(MixerEffectsJson));
            var so = new SerializedObject(effects);
            FillFixed(so.FindProperty("reverbMixerData").objectReferenceValue, json["reverbMixerData"]?["params"] as JArray);
            FillFixed(so.FindProperty("delayMixerData").objectReferenceValue, json["delayMixerData"]?["params"] as JArray);
            FillArray(so.FindProperty("effectsSendsMixerData").objectReferenceValue, json["effectsSendsMixerData"]?["params"] as JArray);
            FillArray(so.FindProperty("effectsVolumesMixerData").objectReferenceValue, json["effectsVolumesMixerData"]?["params"] as JArray);
            AssetDatabase.SaveAssets();
            return true;
        }

        // ReverbMixerDataSO / DelayMixerDataSO: one MixerEnvironmentParameter field each, in the game's order
        private static void FillFixed(UnityEngine.Object asset, JArray parameters)
        {
            if (asset == null || parameters == null)
                return;
            var so = new SerializedObject(asset);
            var it = so.GetIterator();
            int index = 0;
            for (bool enter = true; it.NextVisible(enter) && index < parameters.Count; enter = false)
                if (it.propertyType == SerializedPropertyType.Generic && it.FindPropertyRelative("paramName") != null)
                    Fill(it, (JObject)parameters[index++]);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }

        private static void FillArray(UnityEngine.Object asset, JArray parameters)
        {
            if (asset == null || parameters == null)
                return;
            var so = new SerializedObject(asset);
            var list = so.FindProperty("mixerEnvironmentParameters");
            list.arraySize = parameters.Count;
            for (int i = 0; i < parameters.Count; i++)
                Fill(list.GetArrayElementAtIndex(i), (JObject)parameters[i]);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }

        private static void Fill(SerializedProperty parameter, JObject json)
        {
            parameter.FindPropertyRelative("paramName").stringValue = json.Value<string>("paramName");
            var keys = (JArray)json["keys"];
            var values = (JArray)json["values"];
            var keyList = parameter.FindPropertyRelative("parametersByRoom._keys");
            var valueList = parameter.FindPropertyRelative("parametersByRoom._values");
            keyList.arraySize = keys.Count;
            valueList.arraySize = values.Count;
            for (int i = 0; i < keys.Count; i++)
                keyList.GetArrayElementAtIndex(i).stringValue = keys[i].Value<string>();
            for (int i = 0; i < values.Count; i++)
                valueList.GetArrayElementAtIndex(i).floatValue = values[i].Value<float>();
        }

        public static MixerEffectsDataSO MixerEffects(GameObject root) =>
            root != null ? new SerializedObject(root.GetComponent<AmbientAudioSystem>()).FindProperty("EffectsData").objectReferenceValue as MixerEffectsDataSO : null;

        public static bool HasMixerEffects(MixerEffectsDataSO effects)
        {
            var reverb = effects != null ? new SerializedObject(effects).FindProperty("reverbMixerData").objectReferenceValue : null;
            return reverb != null && !string.IsNullOrEmpty(new SerializedObject(reverb).FindProperty("reverbVolume.paramName").stringValue);
        }
    }
}
