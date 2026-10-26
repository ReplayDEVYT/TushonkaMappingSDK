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

        [MenuItem("Tarkov SDK/Map Settings/New Map...", priority = 126)]
        private static void Open()
        {
            var window = GetWindow<TarkovNewMapWindow>(true, "New Tarkov Map");
            window.minSize = window.maxSize = new Vector2(400f, 160f);
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox("Creates Assets/Maps/<id>/ with a scene, an example map and a Map Definition. The example has cover, a shed " +
                                    "with a door, switch and loot, a bunker, spawns, a bot zone, every kind of extract, a minefield and a " +
                                    "claymore, airdrop points, a BTR route, spatial audio rooms, the ambient audio system and a baked navmesh.", MessageType.None);
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
            Physics.SyncTransforms();
            TarkovRainCover.Generate(scene);
            TarkovAirdrops.Generate(scene, 20f);
            TarkovAirdrops.EnsurePhysicsSetup(scene, new TarkovScenePreparer.Report());
            // the roads follow the navmesh, so they come after the bake
            var btr = TarkovBtr.Create(null, new Vector3(45f, 0f, -40f));
            Undo.RegisterCreatedObjectUndo(btr.gameObject, "BTR route");
            var failed = TarkovBtr.GenerateRoads(btr, replace: true);
            if (failed.Count > 0)
                Debug.LogWarning("[Tarkov SDK] Example BTR roads not made: " + string.Join("; ", failed));
            // rooms for the shed and the bunker (its trigger has Is Bunker) and the outdoor room; the
            // shed's door and the bunker's doorway become portals
            TarkovAudioWindow.Generate(scene);
            TarkovAudioSources.Fix(scene);
            EditorSceneManager.SaveScene(scene);

            var definition = CreateInstance<TarkovMapDefinition>();
            definition.MapId = mapId;
            definition.Scenes = new List<SceneAsset> { AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) };
            AssetDatabase.CreateAsset(definition, definitionPath);
            AssetDatabase.SaveAssets();

            Selection.activeObject = definition;
            EditorGUIUtility.PingObject(definition);
            Debug.Log($"[Tarkov SDK] Created map '{mapId}' in {folder}. Select {definitionPath} and press Build Map. " +
                      "Add ambience clips in Tarkov SDK > Generation > Ambience > Ambient Sounds..., pick the indoor and bunker room tones in " +
                      "Generation > Audio > Spatial Audio..., and simple or full weather in Generation > Time > Time of Day / Weather....");
        }
    }
}
