using System;
using System.Collections.Generic;
using UnityEngine;

namespace TarkovSDK
{
    // Captions for the player who fires a trigger id (CustomMapLoader's own; Tarkov has no such trigger): lines shown at
    // the bottom of the screen by the game's own caption view (SubtitlesView, which the Lightkeeper's dialogue uses),
    // each at its time from the trigger. Editor-only data on an EditorOnly object: Build Map exports it to
    // sdk_export.json and the client plugin shows it.
    [AddComponentMenu("Tarkov SDK/Caption")]
    public class TarkovCaption : MonoBehaviour
    {
        [Serializable]
        public class Line
        {
            [Tooltip("The text. TextMeshPro rich text works, e.g. <b>Name:</b> or <color=#ffcc00>...</color>.")]
            [TextArea(1, 4)]
            public string Text = "";
            [Tooltip("Seconds from the trigger.")]
            [Min(0f)]
            public float StartSeconds;
            [Tooltip("Seconds it stays up. Lines that overlap show together, one under the other.")]
            [Min(0.1f)]
            public float Duration = 3f;
        }

        [Tooltip("Show the captions to the player who fires this trigger id (a zone or a switch; connect one in Triggers & Sounds). " +
                 "Give a sound the same trigger id to voice them.")]
        public string TriggerId = "";

        [Tooltip("Only show them once per raid to each player.")]
        public bool Once = true;

        public List<Line> Lines = new List<Line> { new Line { Text = "Something happened." } };
    }
}
