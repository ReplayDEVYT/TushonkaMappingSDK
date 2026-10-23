using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Editor-only build settings for one map. Lives in an Editor folder and is never bundled.
    [CreateAssetMenu(fileName = "NewMap", menuName = "Tarkov SDK/Map Definition", order = 0)]
    public class TarkovMapDefinition : ScriptableObject
    {
        [Tooltip("Location id. Lowercase letters, digits and underscores. Must match \"Id\" in the server's base.json.")]
        public string MapId = "sdk_example";

        [Tooltip("Scenes that make up the map. The first one is the active scene and holds LocationExportInfo.")]
        public List<SceneAsset> Scenes = new List<SceneAsset>();

        [Header("Environment")]
        [Tooltip("The whole map is indoors, like Labs. Build Map then covers it with one indoor trigger (indoor sound, exposure, " +
                 "muffled rain), gives the simple time of day Labs' indoor lighting, and with the full weather system adds a " +
                 "rain cover over everything so rain never falls on the screen.")]
        public bool EntirelyIndoors;

        [Header("Output")]
        [Tooltip("Folder (relative to the Unity project, or absolute) that receives the bundles and sdk_export.json.")]
        public string OutputFolder = "Build";

        [Tooltip("Also write <Output Folder>/<MapId>.zip: the map's files laid out like an SPT install, as an add-on for the CustomMapLoader mod.")]
        public bool CreatePackage = true;

        [Header("Deploy")]
        [Tooltip("Copy the build into the CustomMapLoader mod of an SPT install after a successful build.")]
        public bool DeployToGame;

        [Tooltip("SPT install root, the folder containing EscapeFromTarkov.exe.")]
        public string SptRoot = "C:/spt41";
    }
}
