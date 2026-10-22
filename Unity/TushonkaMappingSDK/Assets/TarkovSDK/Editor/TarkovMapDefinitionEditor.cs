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
    }
}
