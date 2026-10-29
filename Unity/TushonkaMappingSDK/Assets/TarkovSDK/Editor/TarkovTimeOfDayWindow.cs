using System.Diagnostics;
using System.IO;
using EFT.Weather;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace TarkovSDK.Editor
{
    // Chooses the map's sky and time of day:
    //
    //  Simple  Factory's static rig (TODSkySimple). The sun follows the raid's clock (or stays at the
    //          raid's start time when frozen); no clouds, rain or wind. Light and safe.
    //  Full    Customs' complete weather system (TOD_Sky, WeatherController, clouds, wind, rain,
    //          snow), driven by the server's weather and season. Built from data extracted from the
    //          author's own game.
    public class TarkovTimeOfDayWindow : EditorWindow
    {
        private string _gameData = "C:/spt41/EscapeFromTarkov_Data";
        private string _python = "python";

        [MenuItem("Tarkov SDK/Generation/Time/Time of Day \u2215 Weather...", priority = 109)]
        private static void Open()
        {
            var window = GetWindow<TarkovTimeOfDayWindow>(true, "Time of Day / Weather");
            window.minSize = new Vector2(440f, 420f);
        }

        private void OnEnable()
        {
            _gameData = EditorPrefs.GetString("TarkovSDK.GameData", _gameData);
            _python = EditorPrefs.GetString("TarkovSDK.Python", _python);
        }

        private void OnGUI()
        {
            var scene = SceneManager.GetActiveScene();
            var simple = TarkovLightingRig.Find(scene);
            var full = TarkovWeatherBuilder.Find(scene);

            EditorGUILayout.LabelField("Scene: " + scene.name, EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(full != null && simple != null ? "Both systems are in the scene. Keep only one."
                : full != null ? "Using the full weather system."
                : simple != null ? "Using the simple time of day."
                : "No time of day system yet. Build Map adds the simple one if you don't pick.", full != null && simple != null ? MessageType.Warning : MessageType.Info);

            // ------------------------------------------------ simple
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Simple", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Static sky and Factory's lighting. The sun follows the raid's time of day (unless frozen). " +
                                       "No clouds, rain, wind or seasons.", EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button(simple != null ? "Simple Time of Day (in use)" : "Use Simple Time of Day"))
                UseSimple(scene);

            // ------------------------------------------------ full
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Full weather system", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Customs' TOD sky with day and night, clouds, fog, wind, rain and snow, driven by the server's " +
                                       "weather and season. Heavier, and built from your own game's data.", EditorStyles.wordWrappedMiniLabel);
            if (!TarkovWeatherBuilder.HasExtractedData)
            {
                EditorGUILayout.HelpBox("Extract the weather data from your game first. It goes to Assets/TarkovSDK/Weather/Extracted " +
                                        "and is never committed. Needs Python with: pip install UnityPy TypeTreeGeneratorAPI", MessageType.Info);
                _gameData = EditorGUILayout.TextField("EscapeFromTarkov_Data", _gameData);
                _python = EditorGUILayout.TextField("Python", _python);
                if (GUILayout.Button("Extract From Game"))
                    Extract();
            }
            using (new EditorGUI.DisabledScope(!TarkovWeatherBuilder.HasExtractedData))
                if (GUILayout.Button(full != null ? "Rebuild Full Weather System" : "Use Full Weather System"))
                    UseFull(scene);
            if (full != null)
            {
                var cover = TarkovRainCover.FindInMap(scene);
                EditorGUILayout.LabelField(cover != null
                    ? "Rain cover: generated. Regenerate after changing roofs or buildings."
                    : "Rain cover: none, so the whole map counts as outdoors (rain on the screen everywhere).", EditorStyles.wordWrappedMiniLabel);
                if (GUILayout.Button(cover != null ? "Regenerate Rain Cover" : "Generate Rain Cover"))
                    TarkovRainCover.GenerateMenu();
            }

            // ------------------------------------------------ preview
            if (simple != null || full != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Scene view preview", EditorStyles.boldLabel);
                TarkovTimeOfDayEditor.DrawPreviewControls();
                if (full != null)
                    EditorGUILayout.LabelField("Full weather: ambient light is approximated for a clear sky; the sky dome itself shows only in the raid.",
                        EditorStyles.wordWrappedMiniLabel);
            }

            // ------------------------------------------------ freeze
            var time = full != null ? full.GetComponentInChildren<TOD_Time>(true)
                : simple != null ? simple.GetComponent<TOD_Time>() : null;
            if (time != null)
            {
                EditorGUILayout.Space();
                EditorGUI.BeginChangeCheck();
                bool freeze = EditorGUILayout.ToggleLeft(new GUIContent("Freeze Time",
                    "Keep the time of day at the raid's start time for the whole raid."), time.LockCurrentTime);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(time, "Freeze time");
                    time.LockCurrentTime = freeze;
                    EditorUtility.SetDirty(time);
                    EditorSceneManager.MarkSceneDirty(scene);
                }
            }
        }

        private static void UseSimple(Scene scene)
        {
            var full = TarkovWeatherBuilder.Find(scene);
            if (full != null)
            {
                if (!EditorUtility.DisplayDialog("Time of Day", "Remove the full weather system from the scene?", "Remove", "Cancel"))
                    return;
                Undo.DestroyObjectImmediate(full);
            }
            if (TarkovLightingRig.Find(scene) == null)
                Undo.RegisterCreatedObjectUndo(TarkovLightingRig.Create(scene), "Use simple time of day");
            EditorSceneManager.MarkSceneDirty(scene);
        }

        private static void UseFull(Scene scene)
        {
            var simple = TarkovLightingRig.Find(scene);
            if (simple != null)
            {
                if (!EditorUtility.DisplayDialog("Time of Day", "Replace the simple time of day rig (the 'Weather' object and its sun) with the full weather system?", "Replace", "Cancel"))
                    return;
                Undo.DestroyObjectImmediate(simple.transform.root.gameObject);
            }
            var old = TarkovWeatherBuilder.Find(scene);
            if (old != null)
                Undo.DestroyObjectImmediate(old);

            var root = TarkovWeatherBuilder.Build(scene);
            ApplyCustomsLevelSettings(scene);
            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(scene);
        }

        // the TOD sky draws the sky dome itself and supplies the ambient light, as on Customs
        private static void ApplyCustomsLevelSettings(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                var settings = root.GetComponentInChildren<LevelSettings>(true);
                if (settings == null)
                    continue;
                Undo.RecordObject(settings, "Full weather level settings");
                settings.skybox = null;
                // Flat with a black sky: no Unity ambient at all (Trilight would add the equator and
                // ground colours day and night)
                settings.AmbientMode = UnityEngine.Rendering.AmbientMode.Flat;
                settings.SkyColor = Color.black;
                settings.EquatorColor = Color.black;
                settings.GroundColor = Color.black;
                settings.AmbientIntensity = 0f;
                // Customs' values; the simple rig's Factory values light shadowed surfaces and look bright at night
                settings.DirectionLightShadowForUnshadowedShaders = 0f;
                settings.SSRFactor = 1f;
                settings._minSmokeAmbientColor = Color.clear;
                EditorUtility.SetDirty(settings);
                return;
            }
        }

        private void Extract()
        {
            EditorPrefs.SetString("TarkovSDK.GameData", _gameData);
            EditorPrefs.SetString("TarkovSDK.Python", _python);
            RunExtractor("Extracting the weather system from the game (about a minute)...");
        }

        // runs Tools/WeatherExtract/extract_weather.py into Assets/TarkovSDK/Weather/Extracted with the
        // game folder and Python saved by this window; extraArgs e.g. "--mixer-only"
        public static bool RunExtractor(string progress, string extraArgs = "")
        {
            var gameData = EditorPrefs.GetString("TarkovSDK.GameData", "C:/spt41/EscapeFromTarkov_Data");
            var python = EditorPrefs.GetString("TarkovSDK.Python", "python");
            var script = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "Tools", "WeatherExtract", "extract_weather.py"));
            if (!File.Exists(script))
            {
                EditorUtility.DisplayDialog("Tarkov SDK", $"Extractor not found at {script}.", "OK");
                return false;
            }
            var output = Path.GetFullPath(TarkovWeatherBuilder.ExtractedFolder);
            Directory.CreateDirectory(output);
            var start = new ProcessStartInfo(python, $"\"{script}\" \"{gameData}\" \"{output}\" {extraArgs}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            try
            {
                EditorUtility.DisplayProgressBar("Tarkov SDK", progress, 0.5f);
                using var process = Process.Start(start);
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    Debug.LogError($"[Tarkov SDK] Extraction failed:\n{stderr}\n{stdout}");
                    return false;
                }
                Debug.Log($"[Tarkov SDK] {stdout}");
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Tarkov SDK] Could not run '{python}': {e.Message}. Install Python 3 and run: pip install UnityPy TypeTreeGeneratorAPI " +
                               "(set the game folder and Python in Tarkov SDK > Generation > Time > Time of Day / Weather...)");
                return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }
        }
    }
}
