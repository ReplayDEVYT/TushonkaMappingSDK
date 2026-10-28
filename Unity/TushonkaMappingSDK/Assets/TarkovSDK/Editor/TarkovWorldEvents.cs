using System.Collections.Generic;
using System.Linq;
using EFT.GameTriggers;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Scripted world events (CustomMapLoader's own; TarkovWorldEvent): a timeline of steps started at a raid time or by a
    // trigger id. Steps fire trigger ids, show or hide scene objects, play or stop scene sounds and notify every player.
    // A step's trigger id reaches the same listeners a zone or switch would (explosions, mortar zones, spawns, lamps,
    // doors, sounds), so an event strings the map's other features together: sirens, then a mortar barrage, then a
    // squad of bots, a burning building revealed. The client plugin runs each event on every machine in co-op from a
    // seed every player shares (the raid's loot ids), so they all see the same rolls and times; bots, loot and shelling
    // still happen on the host only, as when a player fires their triggers.
    public static class TarkovWorldEvents
    {
        private const string Menu = "GameObject/Tarkov SDK/World Events/";

        [MenuItem(Menu + "Scripted Event", priority = 80)]
        private static void MenuEvent(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => Create(p).gameObject);

        // an event with a notification and an explosion to start from
        public static TarkovWorldEvent Create(Transform parent)
        {
            var evt = TarkovMapEvents.EditorOnlyChild(parent, "SDK_WorldEvent").AddComponent<TarkovWorldEvent>();
            evt.EventName = "World Event";
            evt.Steps.Add(new TarkovWorldEvent.Step
            {
                Kind = TarkovWorldEvent.StepKind.Notification,
                Notification = new TarkovNotification.Settings { Message = "You hear something in the distance...", Icon = TarkovNotification.Icon.Alert },
            });
            return evt;
        }

        // ------------------------------------------------------------ Build Map

        public static void Prepare(Scene scene, GameObject[] roots, TarkovScenePreparer.Report report)
        {
            foreach (var evt in roots.SelectMany(r => r.GetComponentsInChildren<TarkovWorldEvent>(true)))
            {
                string name = string.IsNullOrWhiteSpace(evt.EventName) ? evt.name : evt.EventName;
                string where = $"{scene.name}: world event '{name}'";
                bool byTrigger = evt.Start != TarkovWorldEvent.StartMode.RaidTime;
                bool byTime = evt.Start != TarkovWorldEvent.StartMode.Trigger;
                if (byTrigger && string.IsNullOrEmpty(evt.TriggerId))
                {
                    if (!byTime)
                    {
                        report.Warnings.Add($"{where} starts on a trigger but has no trigger id, so it never runs (connect one in Triggers & Sounds)");
                        continue;
                    }
                    report.Warnings.Add($"{where} can start on a trigger but has no trigger id; only its raid time starts it");
                }
                if (byTime && evt.LatestMinute < evt.EarliestMinute)
                {
                    report.Warnings.Add($"{where}: Latest Minute was before Earliest Minute; it is now the same");
                    evt.LatestMinute = evt.EarliestMinute;
                    EditorUtility.SetDirty(evt);
                }
                if (evt.Steps == null || evt.Steps.Count == 0)
                {
                    report.Warnings.Add($"{where} has no steps, so it does nothing");
                    continue;
                }
                if (evt.Repeat && evt.RepeatAfterMaxMinutes < evt.RepeatAfterMinMinutes)
                {
                    evt.RepeatAfterMaxMinutes = evt.RepeatAfterMinMinutes;
                    EditorUtility.SetDirty(evt);
                }

                var steps = new List<(TarkovWorldEvent.Step Step, string Scene, string Path)>();
                for (int i = 0; i < evt.Steps.Count; i++)
                {
                    var step = evt.Steps[i];
                    string at = $"{where}, step {i + 1} ({step.Kind})";
                    switch (step.Kind)
                    {
                        case TarkovWorldEvent.StepKind.FireTrigger:
                            if (string.IsNullOrEmpty(step.TriggerId))
                            {
                                report.Warnings.Add($"{at} has no trigger id; skipped");
                                continue;
                            }
                            if (!TarkovTriggerLinks.Listeners(step.TriggerId).Any())
                                report.Warnings.Add($"{at}: nothing listens to '{step.TriggerId}'");
                            steps.Add((step, null, null));
                            break;
                        case TarkovWorldEvent.StepKind.Notification:
                            if (string.IsNullOrWhiteSpace(step.Notification?.Message))
                            {
                                report.Warnings.Add($"{at} has no message; skipped");
                                continue;
                            }
                            steps.Add((step, null, null));
                            break;
                        default:
                        {
                            var target = step.Kind == TarkovWorldEvent.StepKind.PlaySound || step.Kind == TarkovWorldEvent.StepKind.StopSound
                                ? step.Sound != null ? step.Sound.gameObject : null
                                : step.Target;
                            if (target == null)
                            {
                                report.Warnings.Add($"{at} has no {(step.Kind == TarkovWorldEvent.StepKind.PlaySound || step.Kind == TarkovWorldEvent.StepKind.StopSound ? "Sound" : "Target")}; skipped");
                                continue;
                            }
                            if (target.scene != scene)
                            {
                                report.Errors.Add($"{at}: '{target.name}' is in another scene; keep it in the event's scene");
                                continue;
                            }
                            if (target.GetComponentsInParent<Transform>(true).Any(t => t.CompareTag("EditorOnly")))
                            {
                                report.Errors.Add($"{at}: '{target.name}' is (under) an EditorOnly object, so it isn't in the build; move it out");
                                continue;
                            }
                            if (step.Kind == TarkovWorldEvent.StepKind.PlaySound && step.Sound.playOnAwake)
                                report.Warnings.Add($"{at}: '{target.name}' plays on awake, so it plays at raid start too; turn Play On Awake off");
                            var path = TarkovTriggeredSpawns.PathOf(target.transform);
                            if (TarkovTriggeredSpawns.Resolve(scene, path) != target)
                            {
                                report.Errors.Add($"{at}: another object has the same place and name as '{path}', so the game can't tell them apart; rename it");
                                continue;
                            }
                            steps.Add((step, scene.name, path));
                            break;
                        }
                    }
                }
                report.WorldEvents.Add((evt, name, steps));
            }
        }

        // ------------------------------------------------------------ timeline

        // "0:05  Fire trigger X (reaches: ...)" per step, from the event's start
        public static IEnumerable<string> Timeline(TarkovWorldEvent evt)
        {
            float t = 0f;
            foreach (var step in evt.Steps ?? new List<TarkovWorldEvent.Step>())
            {
                t += Mathf.Max(0f, step.Wait);
                yield return $"{(int)(t / 60f)}:{t % 60f:00.#}  {Describe(step)}";
            }
        }

        public static string Describe(TarkovWorldEvent.Step step)
        {
            switch (step.Kind)
            {
                case TarkovWorldEvent.StepKind.FireTrigger:
                {
                    if (string.IsNullOrEmpty(step.TriggerId))
                        return "fire a trigger (no id)";
                    var reaches = TarkovTriggerLinks.Listeners(step.TriggerId).Select(l => $"{l.name} ({l.GetType().Name})").ToList();
                    return $"fire '{step.TriggerId}' -> " + (reaches.Count == 0 ? "nothing listens" : string.Join(", ", reaches.Take(3)) + (reaches.Count > 3 ? $" +{reaches.Count - 3}" : ""));
                }
                case TarkovWorldEvent.StepKind.Show: return $"show {(step.Target != null ? step.Target.name : "(nothing)")}";
                case TarkovWorldEvent.StepKind.Hide: return $"hide {(step.Target != null ? step.Target.name : "(nothing)")}";
                case TarkovWorldEvent.StepKind.PlaySound: return $"play {(step.Sound != null ? step.Sound.name : "(no sound)")}";
                case TarkovWorldEvent.StepKind.StopSound: return $"stop {(step.Sound != null ? step.Sound.name : "(no sound)")}";
                default: return $"notify \"{step.Notification?.Message}\"";
            }
        }

        // the trigger id a new listener gets for a step, and the step that fires it
        public static string AddFireStep(TarkovWorldEvent evt, string what, float wait)
        {
            string id = $"{(string.IsNullOrWhiteSpace(evt.EventName) ? evt.name : evt.EventName).Replace(' ', '_')}_{what}_{System.Guid.NewGuid().ToString("N").Substring(0, 6)}";
            Undo.RecordObject(evt, "Add event step");
            evt.Steps.Add(new TarkovWorldEvent.Step { Kind = TarkovWorldEvent.StepKind.FireTrigger, TriggerId = id, Wait = wait });
            EditorUtility.SetDirty(evt);
            return id;
        }

        // ------------------------------------------------------------ gizmo

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        private static void DrawEvent(TarkovWorldEvent evt, GizmoType type)
        {
            bool selected = (type & GizmoType.Selected) != 0;
            Gizmos.color = new Color(0.9f, 0.5f, 1f, selected ? 1f : 0.5f);
            Gizmos.DrawWireSphere(evt.transform.position, 0.5f);
            if (!selected)
                return;
            Handles.Label(evt.transform.position + Vector3.up, string.IsNullOrWhiteSpace(evt.EventName) ? evt.name : evt.EventName);
            foreach (var step in evt.Steps)
            {
                var target = step.Kind == TarkovWorldEvent.StepKind.PlaySound || step.Kind == TarkovWorldEvent.StepKind.StopSound
                    ? step.Sound != null ? step.Sound.transform : null
                    : step.Target != null ? step.Target.transform : null;
                if (target != null)
                    Gizmos.DrawLine(evt.transform.position, target.position);
                if (step.Kind == TarkovWorldEvent.StepKind.FireTrigger)
                    foreach (var listener in TarkovTriggerLinks.Listeners(step.TriggerId))
                        Gizmos.DrawLine(evt.transform.position, listener.transform.position);
            }
        }
    }

    [CustomEditor(typeof(TarkovWorldEvent))]
    internal class TarkovWorldEventEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var evt = (TarkovWorldEvent)target;
            DrawDefaultInspector();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Timeline (from the event's start)", EditorStyles.boldLabel);
            foreach (var line in TarkovWorldEvents.Timeline(evt))
                EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button("Open World Events Window"))
                TarkovWorldEventsWindow.Open(evt);
            WorldEventPreviewGui.Draw(evt);
        }
    }

    // Tarkov SDK > Tools > World Events > Scripted Events...: the open scenes' events, a timeline for the selected one, and
    // quick steps that make what they fire (an explosion, a mortar zone, a bot or loot spawn, a prefab to show)
    public class TarkovWorldEventsWindow : EditorWindow
    {
        private TarkovWorldEvent _selected;
        private Vector2 _scroll;
        private float _wait = 5f;

        [MenuItem("Tarkov SDK/Tools/World Events/Scripted Events...", priority = 121)]
        public static void OpenMenu() => Open(null);

        public static void Open(TarkovWorldEvent evt)
        {
            var window = GetWindow<TarkovWorldEventsWindow>("World Events");
            window.minSize = new Vector2(420f, 420f);
            if (evt != null)
                window._selected = evt;
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Scripted world events in the open scenes", EditorStyles.boldLabel);
            var events = FindObjectsOfType<TarkovWorldEvent>(true);
            if (events.Length == 0)
                EditorGUILayout.LabelField("None yet.", EditorStyles.miniLabel);
            foreach (var evt in events)
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    string start = evt.Start == TarkovWorldEvent.StartMode.Trigger ? $"on '{evt.TriggerId}'"
                        : $"{evt.EarliestMinute:0.#}-{evt.LatestMinute:0.#} min" + (evt.Start == TarkovWorldEvent.StartMode.RaidTimeOrTrigger ? $" or on '{evt.TriggerId}'" : "");
                    EditorGUILayout.LabelField($"{(string.IsNullOrWhiteSpace(evt.EventName) ? evt.name : evt.EventName)}: {start}, {evt.Steps.Count} step(s), {evt.Chance:P0}",
                        evt == _selected ? EditorStyles.miniBoldLabel : EditorStyles.miniLabel);
                    if (GUILayout.Button("Edit", GUILayout.Width(44f)))
                    {
                        _selected = evt;
                        Selection.activeObject = evt.gameObject;
                    }
                }
            if (GUILayout.Button("New Scripted Event"))
                EditorApplication.delayCall += () =>
                {
                    var evt = TarkovWorldEvents.Create(null);
                    if (SceneView.lastActiveSceneView != null)
                        evt.transform.position = SceneView.lastActiveSceneView.pivot;
                    Undo.RegisterCreatedObjectUndo(evt.gameObject, "Create world event");
                    _selected = evt;
                    Selection.activeObject = evt.gameObject;
                    Repaint();
                };

            if (_selected != null)
            {
                EditorGUILayout.Space(10f);
                EditorGUILayout.LabelField(string.IsNullOrWhiteSpace(_selected.EventName) ? _selected.name : _selected.EventName, EditorStyles.boldLabel);
                foreach (var line in TarkovWorldEvents.Timeline(_selected))
                    EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Add a step that fires a new...", EditorStyles.miniBoldLabel);
                _wait = EditorGUILayout.FloatField(new GUIContent("Wait Before It (s)"), Mathf.Max(0f, _wait));
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Explosion"))
                        Later(evt =>
                        {
                            var explosion = TarkovExplosives.CreateExplosion(null, TarkovWorldEvents.AddFireStep(evt, "Explosion", _wait));
                            Place(explosion.gameObject, evt, "Create explosion");
                        });
                    if (GUILayout.Button("Mortar Strike"))
                        Later(evt =>
                        {
                            var root = TarkovMortar.EnsureRoot(evt.gameObject.scene);
                            var zone = TarkovMortar.CreateZone(root.transform);
                            zone.Planned = false;
                            zone.TriggerId = TarkovWorldEvents.AddFireStep(evt, "Mortar", _wait);
                            Place(zone.gameObject, evt, "Create mortar zone");
                            if (!root.GetComponentsInChildren<TarkovMortarBattery>(true).Any())
                                Undo.RegisterCreatedObjectUndo(TarkovMortar.CreateBattery(root.transform, zone.transform.position + new Vector3(350f, 0f, 0f)).gameObject, "Create mortar battery");
                        });
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Bot Spawn"))
                        Later(evt =>
                        {
                            var bots = TarkovTriggeredSpawns.CreateBots(null);
                            bots.TriggerId = TarkovWorldEvents.AddFireStep(evt, "Bots", _wait);
                            Place(bots.gameObject, evt, "Create bot spawn");
                        });
                    if (GUILayout.Button("Loot Spawn"))
                        Later(evt =>
                        {
                            var loot = TarkovTriggeredSpawns.CreateLoot(null);
                            loot.TriggerId = TarkovWorldEvents.AddFireStep(evt, "Loot", _wait);
                            Place(loot.gameObject, evt, "Create loot spawn");
                        });
                    if (GUILayout.Button("Prefab Spawn"))
                        Later(evt =>
                        {
                            var prefab = TarkovTriggeredSpawns.CreatePrefab(null);
                            prefab.TriggerId = TarkovWorldEvents.AddFireStep(evt, "Prefab", _wait);
                            Place(prefab.gameObject, evt, "Create prefab spawn");
                        });
                }
                EditorGUILayout.LabelField("Each is placed at the event; move it where it should happen. Show/hide, sound and notification steps are added in the event's inspector.",
                    EditorStyles.wordWrappedMiniLabel);
            }
            EditorGUILayout.EndScrollView();
        }

        private void Later(System.Action<TarkovWorldEvent> action)
        {
            var evt = _selected;
            EditorApplication.delayCall += () =>
            {
                if (evt == null)
                    return;
                Undo.SetCurrentGroupName("Add world event step");
                action(evt);
                Repaint();
            };
        }

        private static void Place(GameObject go, TarkovWorldEvent evt, string undo)
        {
            if (go.transform.parent == null)
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, evt.gameObject.scene);
            go.transform.position = evt.transform.position + Vector3.right * 2f;
            Undo.RegisterCreatedObjectUndo(go, undo);
            EditorGUIUtility.PingObject(go);
        }

        private void OnHierarchyChange() => Repaint();
    }
}
