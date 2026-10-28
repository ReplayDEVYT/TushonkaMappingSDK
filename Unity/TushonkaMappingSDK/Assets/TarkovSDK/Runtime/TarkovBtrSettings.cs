using System;
using System.Collections.Generic;
using UnityEngine;

namespace TarkovSDK
{
    // The BTR's settings the server holds (globals BTRSettings and locationServices ServerMapBTRSettings) and
    // its stops' names. Editor-only data: the object is tagged EditorOnly and never ships; Build Map exports it
    // to sdk_export.json and CustomMapLoader registers the BTR from it.
    [AddComponentMenu("")]
    public class TarkovBtrSettings : MonoBehaviour
    {
        [Tooltip("Chance (0-100) that the BTR comes on a raid.")]
        [Range(0f, 100f)]
        public float ChanceSpawn = 100f;

        [Tooltip("Seconds into the raid it arrives: between these two.")]
        public Vector2 SpawnPeriod = new Vector2(300f, 600f);

        [Tooltip("Driving speed (retail 8).")]
        public float MoveSpeed = 8f;

        [Tooltip("Seconds it waits at a stop: between these two (retail 90-150).")]
        public Vector2 PauseDuration = new Vector2(90f, 150f);

        [Tooltip("The paint: Dirt (Woods) or HeavyDirt.")]
        public string Skin = "Dirt";

        public List<Stop> Stops = new List<Stop>();

        [Tooltip("Roads changed by hand in the BTR Path Editor; Regenerate All Roads leaves them alone.")]
        public List<string> EditedRoads = new List<string>();

        [Serializable]
        public class Stop
        {
            public string Id = "";
            [Tooltip("Shown in the taxi menu.")]
            public string Name = "";
            public string Description = "";
        }
    }
}
