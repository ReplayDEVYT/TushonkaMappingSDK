using System.IO;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    [CustomEditor(typeof(TarkovMapDefinition))]
    public class TarkovMapDefinitionEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var def = (TarkovMapDefinition)target;
            DrawScenes(def);

            EditorGUILayout.Space();
            if (!string.IsNullOrEmpty(def.SptRoot) && File.Exists(Path.Combine(def.SptRoot, "EscapeFromTarkov.exe"))
                && !TarkovMapBuilder.LoaderInstalled(def.SptRoot, out var missing))
                EditorGUILayout.HelpBox($"{TarkovMapBuilder.InstallNote}\n\nNot installed in {def.SptRoot}: {missing}.", MessageType.Warning);
            else
                EditorGUILayout.HelpBox(TarkovMapBuilder.InstallNote, MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "base.json Scene must be:\n" +
                $"  \"path\": \"{TarkovMapBuilder.PresetBundleKey(def.MapId)}\"\n" +
                $"  \"rcid\": \"{TarkovMapBuilder.PresetRcid(def.MapId)}\"",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Prepare Scenes"))
                    TarkovMapBuilder.PrepareOnly(def);
                if (GUILayout.Button("Build Map", GUILayout.Height(24)))
                    TarkovMapBuilder.Build(def);
            }
            if (GUILayout.Button("Edit Server Data (name, description, icon)"))
                TarkovServerDataWindow.Open(def);
            if (GUILayout.Button("Open Output Folder"))
            {
                var dir = TarkovMapBuilder.OutputDirectory(def);
                Directory.CreateDirectory(dir);
                EditorUtility.RevealInFinder(dir);
            }
        }

        private string _newScene;

        // several scenes: the first holds what a map has once, the others any part of it (TarkovMapScenes)
        private void DrawScenes(TarkovMapDefinition def)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scenes", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "A map can be split into several scenes (an area each, a building, the sound), loaded together in the raid. The first scene holds what a map has once: " +
                "LevelSettings, the time of day, the EnvironmentManager and every indoor trigger. Build Map moves those there from the other scenes and keeps ids unique across all of them.\n" +
                "Work and bake (navmesh, rain cover, indoor triggers, spatial audio) with all the scenes open, so each sees the whole map.", MessageType.None);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Open All Scenes", "Opens the map's scenes together, the first one active (new objects go into the active scene).")))
                    EditorApplication.delayCall += () => TarkovMapScenes.OpenAll(def);
                if (GUILayout.Button(new GUIContent("Fix Scene Setup", "Moves what a map has once and the indoor triggers from the other scenes into the first, as Build Map does.")))
                    EditorApplication.delayCall += () => TarkovMapScenes.FixSetup(def);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                _newScene ??= $"{def.MapId}_part{def.Scenes.Count + 1}";
                _newScene = EditorGUILayout.TextField(new GUIContent("New Scene", "A new scene next to the first, with its LocationScene, added to Scenes."), _newScene);
                if (GUILayout.Button("Add", GUILayout.Width(60)))
                {
                    var name = _newScene;
                    _newScene = null;
                    EditorApplication.delayCall += () => TarkovMapScenes.AddScene(def, name);
                }
            }
            foreach (var stray in TarkovMapScenes.NotInMap(def))
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox($"{stray.name} is open but not in Scenes, so it isn't built.", MessageType.Warning);
                    if (GUILayout.Button("Add To Map", GUILayout.Width(90), GUILayout.Height(38)))
                    {
                        var scene = stray;
                        EditorApplication.delayCall += () => TarkovMapScenes.AddToMap(def, scene);
                    }
                }
        }
    }
}
