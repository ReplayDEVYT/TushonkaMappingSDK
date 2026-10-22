using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // One-click starting point: a scene with the map skeleton and a baked navmesh, plus a Map
    // Definition that builds it.
    public class TarkovNewMapWindow : EditorWindow
    {
        private string _mapId = "sdk_example";

        [MenuItem("Tarkov SDK/Map/New Map...", priority = 100)]
        private static void Open()
        {
            var window = GetWindow<TarkovNewMapWindow>(true, "New Tarkov Map");
            window.minSize = window.maxSize = new Vector2(380f, 140f);
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox("Creates Assets/Maps/<id>/ with a scene, an example map (cover, a shed with a door, switch, indoor trigger and loot, bot zone, spawns, exfil, ambient audio system), a baked navmesh and a Map Definition.", MessageType.None);
            _mapId = EditorGUILayout.TextField("Map Id", _mapId).Trim().ToLowerInvariant();
            bool valid = Regex.IsMatch(_mapId, "^[a-z0-9_]+$");
            if (!valid)
                EditorGUILayout.HelpBox("Lowercase letters, digits and underscores only.", MessageType.Warning);
            using (new EditorGUI.DisabledScope(!valid))
                if (GUILayout.Button("Create"))
                {
                    var id = _mapId;
                    Close();
                    EditorApplication.delayCall += () => Create(id);
                }
        }

        private static void Create(string mapId)
        {
            var folder = $"Assets/Maps/{mapId}";
            var scenePath = $"{folder}/{mapId}.unity";
            var definitionPath = $"{folder}/{mapId}.asset";
            if (File.Exists(scenePath) || File.Exists(definitionPath))
            {
                EditorUtility.DisplayDialog("Tarkov SDK", $"{folder} already contains a map named '{mapId}'.", "OK");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            Directory.CreateDirectory(folder);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            TarkovSceneTools.CreateSkeleton();
            EditorSceneManager.SaveScene(scene, scenePath);
            // these keep their data next to the scene, so they come once it has a path
            TarkovAmbientAudioSystem.Create(scene);
            TarkovLightingPreview.ApplyRetailReflection();
            TarkovSceneTools.BakeNavMesh();
            EditorSceneManager.SaveScene(scene);

            var definition = CreateInstance<TarkovMapDefinition>();
            definition.MapId = mapId;
            definition.Scenes = new List<SceneAsset> { AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) };
            AssetDatabase.CreateAsset(definition, definitionPath);
            AssetDatabase.SaveAssets();

            Selection.activeObject = definition;
            EditorGUIUtility.PingObject(definition);
            Debug.Log($"[Tarkov SDK] Created map '{mapId}' in {folder}. Select {definitionPath} and press Build Map. " +
                      "Add ambience clips in Tarkov SDK > Map > Ambient Sounds..., and pick simple or full weather in Time of Day / Weather....");
        }
    }
}
