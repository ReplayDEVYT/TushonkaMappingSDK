using System;
using System.Collections.Generic;
using UnityEngine;

namespace TarkovSDK
{
    // A scripted world event (CustomMapLoader's own; Tarkov's Runddans-style events need BSG's server): starts at a raid
    // time and/or when a trigger id fires, then runs its steps one after another, each after its wait. Steps fire
    // trigger ids (so an event can set off explosions, mortar strikes, bot and loot spawns, doors, lamps, sounds,
    // anything Triggers & Sounds connects), show or hide objects, play or stop a scene sound and show a notification.
    // Editor-only data on an EditorOnly object: Build Map exports it to sdk_export.json and the client plugin runs it on
    // every machine in co-op, rolling chances and times from a seed every player shares, so all see the same event.
    [AddComponentMenu("Tarkov SDK/World Event")]
    public class TarkovWorldEvent : MonoBehaviour
    {
        public enum StartMode
        {
            [Tooltip("At a random time in the raid between Earliest and Latest.")]
            RaidTime,
            [Tooltip("When a player fires the Trigger Id.")]
            Trigger,
            [Tooltip("At the raid time, or earlier if the trigger fires first.")]
            RaidTimeOrTrigger,
        }

        public enum StepKind
        {
            [Tooltip("Emit a trigger id: whatever listens to it reacts (explosions, mortar zones, spawns, lamps, doors, sounds...).")]
            FireTrigger,
            [Tooltip("Switch a scene object on.")]
            Show,
            [Tooltip("Switch a scene object off.")]
            Hide,
            [Tooltip("Play an AudioSource in the scene (leave its Play On Awake off).")]
            PlaySound,
            [Tooltip("Stop an AudioSource in the scene.")]
            StopSound,
            [Tooltip("Show every player the game's notification popup.")]
            Notification,
        }

        [Serializable]
        public class Step
        {
            [Tooltip("Seconds after the previous step (or the event's start) before this one runs.")]
            [Min(0f)]
            public float Wait;
            public StepKind Kind;
            [Tooltip("The trigger id this step emits.")]
            public string TriggerId = "";
            [Tooltip("The scene object this step switches on or off.")]
            public GameObject Target;
            [Tooltip("The scene AudioSource this step plays or stops.")]
            public AudioSource Sound;
            public TarkovNotification.Settings Notification = new TarkovNotification.Settings { Message = "Something is happening." };
        }

        [Tooltip("Shown in logs and the World Events window.")]
        public string EventName = "";

        public StartMode Start = StartMode.RaidTime;

        [Tooltip("Earliest raid time it starts at, in minutes from the raid's start.")]
        [Min(0f)]
        public float EarliestMinute = 5f;

        [Tooltip("Latest raid time it starts at, in minutes; the time is picked at random between the two.")]
        [Min(0f)]
        public float LatestMinute = 15f;

        [Tooltip("Start it when a player fires this trigger id (a zone or a switch; connect one in Triggers & Sounds).")]
        public string TriggerId = "";

        [Tooltip("The chance it happens at all this raid (rolled once per raid, the same for every player).")]
        [Range(0f, 1f)]
        public float Chance = 1f;

        [Tooltip("Run again after it finishes, after a random pause between the two times.")]
        public bool Repeat;

        [Tooltip("Shortest pause before it runs again, in minutes.")]
        [Min(0f)]
        public float RepeatAfterMinMinutes = 5f;

        [Tooltip("Longest pause before it runs again, in minutes.")]
        [Min(0f)]
        public float RepeatAfterMaxMinutes = 10f;

        public List<Step> Steps = new List<Step>();
    }
}
