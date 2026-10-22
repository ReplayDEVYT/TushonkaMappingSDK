using System;
using System.IO;
using System.Linq;
using Audio.AmbientSubsystem;
using EFT;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Random sound events with the game's own ambient player, SeasonAmbientSoundPlayer: after a random
    // delay it plays a random clip from a SoundBank, waits for the clip and another delay, and repeats.
    // Its bank is chosen per season; the SDK gives every season the same generated SoundBank. In the
    // raid CustomMapLoader routes it into a game mixer and tells it the season, which starts it (on
    // retail maps the game's AmbientAudioSystem does both).
    public static class TarkovRandomSoundEvents
    {
        [MenuItem("GameObject/Tarkov SDK/Random Sound Events (3D)", priority = 91)]
        private static void Menu3D(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => Create(p, true).gameObject);

        [MenuItem("GameObject/Tarkov SDK/Random Sound Events (2D)", priority = 92)]
        private static void Menu2D(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => Create(p, false).gameObject);

        public static SeasonAmbientSoundPlayer Create(Transform parent, bool spatial)
        {
            var go = new GameObject(spatial ? "[Nature] Random Sound Events" : "[Outdoor] Random Sound Events (2D)");
            if (parent != null)
                go.transform.SetParent(parent, false);
            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            var player = go.AddComponent<SeasonAmbientSoundPlayer>();

            var so = new SerializedObject(player);
            so.FindProperty("_volume").floatValue = 0.7f;
            so.FindProperty("_deltaVolume").floatValue = 0.1f;
            so.FindProperty("_randomPitchRange").vector2Value = new Vector2(0.95f, 1.05f);
            // inside one of the ambient audio system's groups the system starts and stops it (day/night)
            so.FindProperty("_playOnAwake").boolValue = parent == null || parent.GetComponentInParent<AmbientSoundPlayerGroup>(true) == null;
            so.FindProperty("_spread").floatValue = spatial ? 0.2f : 1f;
            so.FindProperty("_spatialBlend").floatValue = spatial ? 1f : 0f;
            so.FindProperty("_minDistance").floatValue = 5f;
            so.FindProperty("_maxDistance").floatValue = 80f;
            // custom rolloff over distance / max distance
            so.FindProperty("_rolloffCurve").animationCurveValue = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.3f, 0.35f), new Keyframe(1f, 0f));
            so.FindProperty("_spreadCurve").animationCurveValue = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            so.FindProperty("_startPlayingDelay").vector2Value = new Vector2(2f, 20f);
            so.FindProperty("_randomTimeRange").vector2Value = new Vector2(20f, 60f);
            so.ApplyModifiedPropertiesWithoutUndo();

            SetBank(player, CreateBank(go.scene, go.name));
            return player;
        }

        // ------------------------------------------------------------ sound bank

        public static SoundBank CreateBank(UnityEngine.SceneManagement.Scene scene, string name)
        {
            var folder = Path.Combine(Path.GetDirectoryName(scene.path) ?? "Assets", "SoundBanks").Replace('\\', '/');
            Directory.CreateDirectory(folder);
            var bank = ScriptableObject.CreateInstance<SoundBank>();
            bank.name = name;
            bank.BaseVolume = 1f;
            bank.BasePitch = 1f;
            bank.HasEnvironment = false;
            // [environment][distance] clip lists; the player picks from environment 0, distance 0
            bank.Environments = new EnvironmentVariety[2];
            for (int e = 0; e < bank.Environments.Length; e++)
            {
                bank.Environments[e] = new EnvironmentVariety { Clips = new DistanceVarity[3] };
                for (int d = 0; d < 3; d++)
                    bank.Environments[e].Clips[d] = new DistanceVarity { Clips = new AudioClip[0] };
            }
            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{Sanitize(name)}.asset");
            AssetDatabase.CreateAsset(bank, path);
            return bank;
        }

        // the game's ESeasonStatus names; the dictionary stores its keys as these strings
        private static readonly string[] Seasons = { "Summer", "Autumn", "Winter", "Spring", "AutumnLate", "SpringEarly", "Storm" };

        // every season gets the same bank
        public static void SetBank(SeasonAmbientSoundPlayer player, SoundBank bank)
        {
            var so = new SerializedObject(player);
            var keys = so.FindProperty("_seasonAmbientBanks._keys");
            var values = so.FindProperty("_seasonAmbientBanks._values");
            var seasons = Seasons;
            keys.arraySize = seasons.Length;
            values.arraySize = seasons.Length;
            for (int i = 0; i < seasons.Length; i++)
            {
                keys.GetArrayElementAtIndex(i).stringValue = seasons[i];
                values.GetArrayElementAtIndex(i).objectReferenceValue = bank;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(player);
        }

        public static SoundBank GetBank(SeasonAmbientSoundPlayer player)
        {
            var values = new SerializedObject(player).FindProperty("_seasonAmbientBanks._values");
            return values != null && values.arraySize > 0 ? values.GetArrayElementAtIndex(0).objectReferenceValue as SoundBank : null;
        }

        // the clips the player picks from, kept the same in every environment and distance slot
        public static AudioClip[] GetClips(SoundBank bank) =>
            bank?.Environments != null && bank.Environments.Length > 0 && bank.Environments[0]?.Clips != null && bank.Environments[0].Clips.Length > 0
                ? bank.Environments[0].Clips[0]?.Clips ?? new AudioClip[0]
                : new AudioClip[0];

        public static void SetClips(SoundBank bank, AudioClip[] clips)
        {
            Undo.RecordObject(bank, "Sound bank clips");
            foreach (var environment in bank.Environments)
                foreach (var distance in environment.Clips)
                    distance.Clips = clips.ToArray();
            EditorUtility.SetDirty(bank);
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Replace('[', '(').Replace(']', ')');
        }
    }

    [CustomEditor(typeof(SeasonAmbientSoundPlayer))]
    public class TarkovRandomSoundEventsEditor : UnityEditor.Editor
    {
        private bool _advanced;

        public override void OnInspectorGUI()
        {
            var player = (SeasonAmbientSoundPlayer)target;
            var bank = TarkovRandomSoundEvents.GetBank(player);

            EditorGUILayout.LabelField("Random Sound Events", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Plays a random clip after a random delay, over and over. Name tags pick the game mixer: " +
                                       "[Nature], [Technical], [Outdoor], [Indoor].", EditorStyles.wordWrappedMiniLabel);
            if (bank == null)
            {
                EditorGUILayout.HelpBox("No sound bank.", MessageType.Warning);
                if (GUILayout.Button("Create Sound Bank"))
                    TarkovRandomSoundEvents.SetBank(player, TarkovRandomSoundEvents.CreateBank(player.gameObject.scene, player.name));
                return;
            }

            // clips
            var clips = TarkovRandomSoundEvents.GetClips(bank).ToList();
            EditorGUILayout.LabelField($"Clips ({clips.Count})");
            int remove = -1;
            for (int i = 0; i < clips.Count; i++)
                using (new EditorGUILayout.HorizontalScope())
                {
                    var clip = (AudioClip)EditorGUILayout.ObjectField(clips[i], typeof(AudioClip), false);
                    if (clip != clips[i])
                    {
                        clips[i] = clip;
                        TarkovRandomSoundEvents.SetClips(bank, clips.Where(c => c != null).ToArray());
                    }
                    if (GUILayout.Button("-", GUILayout.Width(22f)))
                        remove = i;
                }
            if (remove >= 0)
            {
                clips.RemoveAt(remove);
                TarkovRandomSoundEvents.SetClips(bank, clips.ToArray());
            }
            var added = (AudioClip)EditorGUILayout.ObjectField("Add Clip", null, typeof(AudioClip), false);
            if (added != null)
            {
                clips.Add(added);
                TarkovRandomSoundEvents.SetClips(bank, clips.ToArray());
            }
            var selected = Selection.objects.OfType<AudioClip>().Where(c => !clips.Contains(c)).ToArray();
            using (new EditorGUI.DisabledScope(selected.Length == 0))
                if (GUILayout.Button($"Add {selected.Length} Clip(s) Selected In The Project Window"))
                    TarkovRandomSoundEvents.SetClips(bank, clips.Concat(selected).ToArray());
            if (clips.Count == 0)
                EditorGUILayout.HelpBox("Add at least one clip; with none the player throws in the raid.", MessageType.Warning);

            // the settings that matter
            serializedObject.Update();
            EditorGUILayout.Space();
            var delay = serializedObject.FindProperty("_randomTimeRange");
            var range = delay.vector2Value;
            EditorGUILayout.LabelField("Delay Between Sounds (s)");
            using (new EditorGUILayout.HorizontalScope())
            {
                range.x = EditorGUILayout.FloatField("Min", range.x);
                range.y = EditorGUILayout.FloatField("Max", range.y);
            }
            delay.vector2Value = new Vector2(Mathf.Max(0f, range.x), Mathf.Max(range.x, range.y));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_volume"), new GUIContent("Volume"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_deltaVolume"), new GUIContent("Volume Variation"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_randomPitchRange"), new GUIContent("Pitch Range"));
            var blend = serializedObject.FindProperty("_spatialBlend");
            bool spatial = EditorGUILayout.Toggle(new GUIContent("3D", "Off: heard everywhere. On: heard around this object."), blend.floatValue > 0.5f);
            blend.floatValue = spatial ? 1f : 0f;
            if (spatial)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_minDistance"), new GUIContent("Full Volume Within"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_maxDistance"), new GUIContent("Silent Beyond"));
            }
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            _advanced = EditorGUILayout.Foldout(_advanced, "All Fields", true);
            if (_advanced)
            {
                EditorGUILayout.ObjectField("Sound Bank", bank, typeof(SoundBank), false);
                DrawDefaultInspector();
            }
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
        private static void DrawGizmo(SeasonAmbientSoundPlayer player, GizmoType type)
        {
            var source = player.GetComponent<AudioSource>();
            bool spatial = new SerializedObject(player).FindProperty("_spatialBlend").floatValue > 0.5f;
            Gizmos.color = new Color(0.4f, 0.9f, 1f);
            Gizmos.DrawWireSphere(player.transform.position, 0.4f);
            if (spatial && (type & GizmoType.Selected) != 0)
            {
                var max = new SerializedObject(player).FindProperty("_maxDistance").floatValue;
                Gizmos.color = new Color(0.4f, 0.9f, 1f, 0.25f);
                Gizmos.DrawWireSphere(player.transform.position, max);
            }
        }
    }
}
