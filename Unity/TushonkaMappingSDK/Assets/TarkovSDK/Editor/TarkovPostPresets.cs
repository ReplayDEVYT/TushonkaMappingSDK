using System.IO;
using Prism.Utils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // A map's camera post-processing. The raid's camera (CameraManager.SetCameraFromPrefab) takes two
    // assets from the map's LevelSettings: PrismPresetPrefab, a PrismPreset for the camera's PrismEffects
    // (exposure, tonemap, vignette, bloom, AO, LUT...), and PostProcessProfilePrefab, a post-processing
    // stack v2 profile for its PostProcessVolume. Without them the camera keeps the retail values. This
    // makes a preset with those retail values next to the scene and assigns it, so a mapper can tune it.
    public static class TarkovPostPresets
    {
        [MenuItem("Tarkov SDK/Generation/Misc/Post-Processing Preset", priority = 114)]
        private static void CreateMenu() => EditorApplication.delayCall += Create;

        public static void Create()
        {
            var scene = EditorSceneManager.GetActiveScene();
            var level = Object.FindObjectOfType<LevelSettings>();
            if (level == null)
            {
                EditorUtility.DisplayDialog("Post-Processing Preset", "The active scene has no LevelSettings (Tarkov SDK > Generation > Essential > Map Skeleton In Active Scene).", "OK");
                return;
            }
            var folder = string.IsNullOrEmpty(scene.path) ? "Assets" : Path.GetDirectoryName(scene.path).Replace('\\', '/');
            var path = $"{folder}/{(string.IsNullOrEmpty(scene.name) ? "Map" : scene.name)}_PostProcessing.asset";
            var preset = AssetDatabase.LoadAssetAtPath<PrismPreset>(path);
            if (preset == null)
            {
                preset = ScriptableObject.CreateInstance<PrismPreset>();
                preset.PresetDescription = "The retail camera's post-processing; tune and build the map.";
                TarkovPostPreview.RetailPreset(preset);
                AssetDatabase.CreateAsset(preset, path);
            }
            if (level.PrismPresetPrefab != preset)
            {
                Undo.RecordObject(level, "Assign post-processing preset");
                level.PrismPresetPrefab = preset;
                EditorUtility.SetDirty(level);
                EditorSceneManager.MarkSceneDirty(scene);
            }
            Selection.activeObject = preset;
            EditorGUIUtility.PingObject(preset);
            Debug.Log($"[Tarkov SDK] Post-processing preset {path} assigned to LevelSettings. The Scene view previews its exposure, vignette and tonemap; " +
                      "bloom, AO, LUTs and the rest only show in the raid. A post-processing stack v2 profile can go in LevelSettings > Post Process Profile Prefab.");
        }
    }
}
