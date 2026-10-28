using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.GameTriggers;
using UnityEngine;

namespace mizmii.CustomMapLoader.Features
{
    // Scripted world events (the SDK's TarkovWorldEvent): each starts at a raid time and/or on a trigger id, then runs its
    // steps one after another, each after its wait: emit a trigger id (as the main player, so the map's own listeners and
    // CustomMapLoader's features react as if the player had fired it), switch a scene object on or off, play or stop a
    // scene AudioSource, show the game's notification. Every machine runs them in co-op; chances and times come from a
    // seed every player shares (the raid's loot ids, which the host sends everyone), so all see the same event at about
    // the same time. Bots, loot and shelling still only happen on the host (SdkSpawns).
    internal static class SdkWorldEvents
    {
        private sealed class Runner : MonoBehaviour
        {
        }

        private static Runner _runner;
        private static readonly HashSet<int> Running = new HashSet<int>();
        private static readonly HashSet<int> Started = new HashSet<int>();

        public static void Reset()
        {
            Running.Clear();
            Started.Clear();
            if (_runner != null)
                UnityEngine.Object.Destroy(_runner.gameObject);
            _runner = null;
        }

        public static void Start(GameWorld world, List<FeatureConfig.WorldEvent> events)
        {
            if (events == null || events.Count == 0)
                return;
            // under the GameWorld, so it goes with the raid
            _runner = new GameObject("CustomMapLoader_WorldEvents").AddComponent<Runner>();
            _runner.transform.SetParent(world.transform, false);
            int seed = SharedSeed(world);
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (evt?.Steps == null || evt.Steps.Count == 0)
                    continue;
                int index = i;
                var random = new System.Random(seed ^ Fnv(evt.Name ?? "") ^ (index * 7919));
                if (random.NextDouble() >= evt.Chance)
                {
                    Plugin.Verbose($"World events: '{evt.Name}' doesn't happen this raid");
                    continue;
                }
                bool byTime = evt.Start != "Trigger";
                bool byTrigger = evt.Start != "RaidTime" && !string.IsNullOrEmpty(evt.TriggerId);
                float at = evt.Earliest + (float)random.NextDouble() * Mathf.Max(0f, evt.Latest - evt.Earliest);
                if (byTime)
                    _runner.StartCoroutine(After(at, () => Begin(world, evt, index, random)));
                if (byTrigger && world.TriggersEmitter != null)
                    world.TriggersEmitter.Subscribe(evt.TriggerId, (TriggerEvent e) => Begin(world, evt, index, random));
                Plugin.Verbose($"World events: '{evt.Name}'" + (byTime ? $" at {at / 60f:0.0} min" : "") + (byTrigger ? $" on '{evt.TriggerId}'" : ""));
            }
        }

        // the smallest loot id: the same on every machine of a co-op raid, new each raid
        private static int SharedSeed(GameWorld world)
        {
            var first = world.AllLoot?.Where(l => !string.IsNullOrEmpty(l?.Id)).Select(l => l.Id).OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
            return first != null ? Fnv(first) : Environment.TickCount;
        }

        private static int Fnv(string text)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in text)
                    hash = (hash ^ c) * 16777619;
                return (int)hash;
            }
        }

        private static IEnumerator After(float seconds, Action action)
        {
            yield return new WaitForSeconds(seconds);
            action();
        }

        // a trigger and the raid time both start it: the first one does; a repeating event starts again on its own
        private static void Begin(GameWorld world, FeatureConfig.WorldEvent evt, int index, System.Random random)
        {
            if (_runner == null || Running.Contains(index) || (!evt.Repeat && !Started.Add(index)))
                return;
            Running.Add(index);
            _runner.StartCoroutine(Run(world, evt, index, random));
        }

        private static IEnumerator Run(GameWorld world, FeatureConfig.WorldEvent evt, int index, System.Random random)
        {
            Plugin.Verbose($"World events: '{evt.Name}' starts");
            foreach (var step in evt.Steps)
            {
                if (step == null)
                    continue;
                if (step.Wait > 0f)
                    yield return new WaitForSeconds(step.Wait);
                try
                {
                    Do(world, evt, step);
                }
                catch (Exception e)
                {
                    Plugin.LogSource.LogError($"World events: '{evt.Name}' step {step.Kind} failed: {e}");
                }
            }
            Running.Remove(index);
            if (evt.Repeat && _runner != null)
            {
                float pause = evt.RepeatMin + (float)random.NextDouble() * Mathf.Max(0f, evt.RepeatMax - evt.RepeatMin);
                _runner.StartCoroutine(After(pause, () => Begin(world, evt, index, random)));
            }
        }

        private static void Do(GameWorld world, FeatureConfig.WorldEvent evt, FeatureConfig.WorldEventStep step)
        {
            switch (step.Kind)
            {
                case "FireTrigger":
                    if (!string.IsNullOrEmpty(step.TriggerId) && world.TriggersEmitter != null)
                        world.TriggersEmitter.Emit(step.TriggerId, world.MainPlayer != null ? world.MainPlayer.ProfileId : "");
                    break;
                case "Show":
                case "Hide":
                {
                    var target = SdkSpawns.Find(step.Scene, step.Path);
                    if (target == null)
                        Plugin.LogSource.LogWarning($"World events: '{evt.Name}': '{step.Path}' isn't in scene '{step.Scene}'");
                    else
                        target.SetActive(step.Kind == "Show");
                    break;
                }
                case "PlaySound":
                case "StopSound":
                {
                    var source = SdkSpawns.Find(step.Scene, step.Path)?.GetComponent<AudioSource>();
                    if (source == null)
                        Plugin.LogSource.LogWarning($"World events: '{evt.Name}': no AudioSource at '{step.Path}' in scene '{step.Scene}'");
                    else if (step.Kind == "PlaySound")
                    {
                        source.gameObject.SetActive(true);
                        source.Play();
                    }
                    else
                        source.Stop();
                    break;
                }
                case "Notification":
                    RaidFeatures.Notify(step.Notification);
                    break;
            }
        }
    }
}
