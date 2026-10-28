using System;
using System.Linq;
using EFT;
using EFT.GameTriggers;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // The Preview panels under the inspectors of previewable components (TarkovPreview): buttons that do
    // what a player or the game would, and the object's live state.
    internal static class PreviewGui
    {
        public static bool Begin(string title)
        {
            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(new GUIContent("Preview: " + title, "Runs the game's own logic in the editor, sounds included. Stop puts the scene back."),
                                       EditorStyles.boldLabel);
            if (Application.isPlaying)
            {
                EditorGUILayout.LabelField("Previews run in edit mode only.", EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
                return false;
            }
            return true;
        }

        public static void End(Component target)
        {
            var sim = TarkovPreview.Find(target);
            EditorGUILayout.LabelField(sim != null ? "State: " + sim.Status : "Not previewing.", EditorStyles.miniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                TarkovPreview.AttenuateByCamera = EditorGUILayout.ToggleLeft(new GUIContent("Fade By Camera Distance",
                    "Sounds fade with the scene camera's distance, by the game's rolloff curve, as a player would hear them."), TarkovPreview.AttenuateByCamera);
                using (new EditorGUI.DisabledScope(!TarkovPreview.Running))
                    if (GUILayout.Button("Stop & Restore", GUILayout.Width(110f)))
                        TarkovPreview.StopAll();
            }
            EditorGUILayout.EndVertical();
        }

        public static bool Button(string label, string tooltip = null) => GUILayout.Button(new GUIContent(label, tooltip));
    }

    [CustomEditor(typeof(WorldInteractiveObject), true)]
    [CanEditMultipleObjects]
    public class InteractivePreviewEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            if (targets.Length != 1)
                return;
            var wio = (WorldInteractiveObject)target;
            string kind = wio is Door ? "door" : wio is Switch ? "switch" : wio is LootableContainer ? "container" : "interactive object";
            if (!PreviewGui.Begin(kind))
                return;
            var sim = (InteractiveSim)TarkovPreview.For(TarkovPreview.IsPreviewing(wio) ? wio : null);
            InteractiveSim Sim() => (InteractiveSim)TarkovPreview.For(wio);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (wio is Switch)
                {
                    if (PreviewGui.Button("Press", "A player switches it on: it moves, then operates its door, lamps, next switches and exfil."))
                        Sim().PlayerInteract(EInteractionType.Open);
                    if (PreviewGui.Button("Release", "Switches it off."))
                        Sim().PlayerInteract(EInteractionType.Close);
                }
                else
                {
                    if (PreviewGui.Button("Open", "A player opens it, with the game's timing, curve and sounds."))
                        Sim().PlayerInteract(EInteractionType.Open);
                    if (PreviewGui.Button("Close"))
                        Sim().PlayerInteract(EInteractionType.Close);
                    if (wio is Door door && door.CanBeBreached && PreviewGui.Button("Kick", "A player breaches it: KickCurve and BreachSound."))
                        Sim().PlayerInteract(EInteractionType.Breach);
                }
                if (PreviewGui.Button("Lock"))
                    Sim().Lock();
                if (PreviewGui.Button("Unlock", "A key unlocks it: the lock handle turns, then it is shut."))
                    Sim().Unlock();
            }
            if (sim == null && wio is Switch sw)
                EditorGUILayout.LabelField(SwitchSummary(sw), EditorStyles.wordWrappedMiniLabel);
            PreviewGui.End(wio);
        }

        private static string SwitchSummary(Switch sw)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (sw.Door != null)
                parts.Add($"{sw.Interaction} door '{sw.Door.name}'");
            if (sw.Lamps != null && sw.Lamps.Any(l => l != null))
                parts.Add($"{sw.Lamps.Count(l => l != null)} lamp(s) on");
            if (sw.NextSwitches != null)
                foreach (var next in sw.NextSwitches.Where(n => n?.Switch != null))
                    parts.Add($"{next.Operation} switch '{next.Switch.name}'");
            if (sw.ExfiltrationPoint != null)
                parts.Add($"exfil '{sw.ExfiltrationPoint.name}' to {sw.TargetStatus}");
            return parts.Count == 0 ? "Operates nothing." : "Pressing it: " + string.Join(", ", parts) + (sw.Delay > 0f ? $" (after {sw.Delay:0.##} s)" : "") + ".";
        }
    }

    [CustomEditor(typeof(LampController))]
    public class LampPreviewEditor : UnityEditor.Editor
    {
        private static float _blast = 0.5f;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var lamp = (LampController)target;
            if (!PreviewGui.Begin("lamp"))
                return;
            LampSim Sim() => (LampSim)TarkovPreview.For(lamp);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (PreviewGui.Button("Turn On", "As a switch or the time of day turns it on: TurnedOnLength, fade-in, blinking and its sounds."))
                    Sim().Switch(Turnable.EState.On);
                if (PreviewGui.Button("Turn Off"))
                    Sim().Switch(Turnable.EState.Off);
                if (PreviewGui.Button("Smooth Off"))
                    Sim().Switch(Turnable.EState.SmoothOff);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (PreviewGui.Button("Flicker", "ConstantFlickering: Perlin noise (or the lamp's curve) at FlickeringFreq."))
                    Sim().Switch(Turnable.EState.ConstantFlickering);
                if (PreviewGui.Button("Break", "Shot out: dark for good."))
                    Sim().Switch(Turnable.EState.Destroyed);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                _blast = EditorGUILayout.Slider(new GUIContent("Blast Strength", "How close a grenade went off: 1 is right next to it."), _blast, 0f, 1f);
                if (GUILayout.Button(new GUIContent("Blast Nearby", "StartFlickering: a strong flicker that fades back over IntactTime."), GUILayout.Width(100f)))
                    Sim().StartFlickering(_blast);
            }
            PreviewGui.End(lamp);
        }
    }

    [CustomEditor(typeof(ControlledLampGroup))]
    public class LampGroupPreviewEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var group = (ControlledLampGroup)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(TarkovLights.LampGroupSummary(group), EditorStyles.wordWrappedMiniLabel);
            if (TarkovLights.LampGroupIsEmpty(group))
                EditorGUILayout.HelpBox("The group switches nothing yet. Put lights and glowing models under it (or in Fill Light From) and press Fill.", MessageType.Warning);
            if (!TarkovTriggerLinks.Handlers.OfType<HandlerLamp>().Any(h => h._controlledLampGroups != null && h._controlledLampGroups.Contains(group)))
                EditorGUILayout.HelpBox($"Nothing switches it, so it stays {(group._initialLightState ? "on" : "off")}. Add a light switch, or connect a zone or switch to it in Tarkov SDK > Tools > Triggers > Triggers & Sounds.", MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                var from = group._fillLightFrom != null ? group._fillLightFrom : group.gameObject;
                if (GUILayout.Button(new GUIContent("Fill From " + (group._fillLightFrom != null ? group._fillLightFrom.name : "Children"),
                        "Lists every light, area light, glowing renderer, lamp and flare under it.")))
                    TarkovLights.FillLampGroup(group, from);
                if (GUILayout.Button(new GUIContent("Add Light Switch", "A switch that turns the group on when pressed and off when pressed again.")))
                    EditorApplication.delayCall += () =>
                    {
                        if (group == null)
                            return;
                        var sw = TarkovLights.CreateLightSwitch(group);
                        Selection.activeGameObject = sw.gameObject;
                    };
            }
            if (!PreviewGui.Begin("lamp group"))
                return;
            LampGroupSim Sim() => (LampGroupSim)TarkovPreview.For(group);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (PreviewGui.Button("Turn On", "SetLightState(true): every light and glow on, with the on sound."))
                    Sim().SetLightState(true);
                if (PreviewGui.Button("Turn Off"))
                    Sim().SetLightState(false);
                if (PreviewGui.Button("Toggle"))
                    Sim().ToggleState();
            }
            PreviewGui.End(group);
        }
    }

    [CustomEditor(typeof(ExfiltrationDoor))]
    public class ExfiltrationDoorPreviewEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var door = (ExfiltrationDoor)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Poses", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Move the door transforms to a pose, then store it (the game's own Set open / Set close).", EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Store As Open Pose"))
                    StorePose(door, open: true);
                if (GUILayout.Button("Store As Closed Pose"))
                    StorePose(door, open: false);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Go To Open Pose"))
                    ApplyPose(door, open: true);
                if (GUILayout.Button("Go To Closed Pose"))
                    ApplyPose(door, open: false);
            }

            if (!PreviewGui.Begin("elevator door"))
                return;
            if (door.Subscribee == null)
            {
                EditorGUILayout.HelpBox("Set Subscribee: the exfil whose status opens and closes this door.", MessageType.Warning);
            }
            else
            {
                var open = door.OpenStatus != null && door.OpenStatus.Length > 0 ? door.OpenStatus[0] : EExfiltrationStatus.AwaitsManualActivation;
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (PreviewGui.Button($"Open (exfil {open})"))
                    {
                        TarkovPreview.For(door);
                        TarkovPreview.SetStatus(door.Subscribee, open);
                    }
                    if (PreviewGui.Button("Close (exfil UncompleteRequirements)"))
                    {
                        TarkovPreview.For(door);
                        TarkovPreview.SetStatus(door.Subscribee, EExfiltrationStatus.UncompleteRequirements);
                    }
                }
            }
            PreviewGui.End(door);
        }

        private static ExfiltrationDoor.DoorTransform[] Transforms(ExfiltrationDoor door)
        {
            if (door._doorTransforms == null || door._doorTransforms.Length == 0)
                door._doorTransforms = new[] { new ExfiltrationDoor.DoorTransform { Transform = door.transform, OpenScale = Vector3.one, CloseScale = Vector3.one } };
            return door._doorTransforms;
        }

        private static void StorePose(ExfiltrationDoor door, bool open)
        {
            Undo.RecordObject(door, open ? "Store open pose" : "Store closed pose");
            foreach (var t in Transforms(door).Where(t => t?.Transform != null))
                if (open)
                {
                    t.OpenPosition = t.Transform.localPosition;
                    t.OpenRotation = t.Transform.localRotation;
                    t.OpenScale = t.Transform.localScale;
                }
                else
                {
                    t.ClosePosition = t.Transform.localPosition;
                    t.CloseRotation = t.Transform.localRotation;
                    t.CloseScale = t.Transform.localScale;
                }
            EditorUtility.SetDirty(door);
        }

        private static void ApplyPose(ExfiltrationDoor door, bool open)
        {
            foreach (var t in Transforms(door).Where(t => t?.Transform != null))
            {
                Undo.RecordObject(t.Transform, "Go to pose");
                t.Transform.localPosition = open ? t.OpenPosition : t.ClosePosition;
                var q = open ? t.OpenRotation : t.CloseRotation;
                t.Transform.localRotation = q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f ? Quaternion.identity : q;
                t.Transform.localScale = open ? t.OpenScale : t.CloseScale;
            }
        }
    }

    [CustomEditor(typeof(ExfiltrationPoint), true)]
    public class ExfilPreviewEditor : UnityEditor.Editor
    {
        private static EExfiltrationStatus _status = EExfiltrationStatus.AwaitsManualActivation;

        public override void OnInspectorGUI()
        {
            if (GUILayout.Button(new GUIContent("Open Extracts Window", "Kind, entry points, requirement and transit conversion in one place.")))
                EditorApplication.ExecuteMenuItem("Tarkov SDK/Tools/Extracts/Extracts...");
            DrawDefaultInspector();
            var exfil = (ExfiltrationPoint)target;
            bool doors = FindObjectsOfType<ExfiltrationDoor>().Any(d => d.Subscribee == exfil);
            if (!doors || !PreviewGui.Begin("exfil status"))
                return;
            EditorGUILayout.LabelField($"Status: {TarkovPreview.GetStatus(exfil)}" + (TarkovPreview.HasStatus(exfil) ? " (preview)" : " (at raid start)"), EditorStyles.miniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _status = (EExfiltrationStatus)EditorGUILayout.EnumPopup(_status);
                if (GUILayout.Button("Set", GUILayout.Width(60f)))
                    TarkovPreview.SetStatus(exfil, _status);
            }
            EditorGUILayout.LabelField("Elevator doors that follow this exfil move as the status changes.", EditorStyles.miniLabel);
            PreviewGui.End(exfil);
        }
    }

    [CustomEditor(typeof(TriggerZone))]
    public class TriggerZonePreviewEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var zone = (TriggerZone)target;
            var listeners = TarkovTriggerLinks.Listeners(zone._triggerId).ToList();
            EditorGUILayout.LabelField(listeners.Count == 0 ? $"Nothing listens to '{zone._triggerId}'."
                                           : $"'{zone._triggerId}' reaches: " + string.Join(", ", listeners.Select(l => $"{l.name} ({l.GetType().Name})")),
                                       EditorStyles.wordWrappedMiniLabel);
            if (!PreviewGui.Begin("trigger zone"))
                return;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (PreviewGui.Button("Player Enters", "Every TriggerZone on this object reacts as when a player walks in."))
                    TarkovPreview.SimulateZone(zone.gameObject, enter: true);
                if (PreviewGui.Button("Player Leaves"))
                    TarkovPreview.SimulateZone(zone.gameObject, enter: false);
            }
            PreviewGui.End(zone);
        }
    }

    [CustomEditor(typeof(HandlerAnimator))]
    public class HandlerAnimatorPreviewEditor : UnityEditor.Editor
    {
        private static float _slide = 0.95f, _openTime = 1f;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var handler = (HandlerAnimator)target;
            // an automatic sliding door made by the SDK: its animation can be rebuilt for new panels
            var prefab = handler.GetComponent<TriggersPrefab>();
            if (prefab != null && handler.GetComponent<AutoDoorSound>() != null && handler._target != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Automatic Sliding Door", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Panels are the children named Panel_L / Panel_R; their current positions are the closed ones.", EditorStyles.wordWrappedMiniLabel);
                _slide = EditorGUILayout.FloatField(new GUIContent("Slide Distance (m)", "How far each panel moves to open."), _slide);
                _openTime = Mathf.Max(0.1f, EditorGUILayout.FloatField(new GUIContent("Open Time (s)"), _openTime));
                if (GUILayout.Button("Rebuild Sliding Animation"))
                {
                    var root = handler._target.gameObject;
                    string id = string.IsNullOrEmpty(prefab._postfix) ? Guid.NewGuid().ToString("N").Substring(0, 8) : prefab._postfix;
                    EditorApplication.delayCall += () =>
                    {
                        TarkovPreview.StopAll();
                        var controller = TarkovFacilitySystems.BuildSlidingAnimation(root, id, _slide, _openTime);
                        Undo.RecordObject(handler._target, "Rebuild sliding animation");
                        handler._target.runtimeAnimatorController = controller;
                        EditorUtility.SetDirty(handler._target);
                    };
                }
            }
            if (!PreviewGui.Begin("animation"))
                return;
            if (handler._target == null)
                EditorGUILayout.HelpBox("Set Target: the Animator these actions drive.", MessageType.Warning);
            else
                foreach (var action in handler._animatorActions ?? new HandlerAnimator.AnimatorAction[0])
                    if (!string.IsNullOrEmpty(action.TriggerId) && PreviewGui.Button($"Fire '{action.TriggerId}'"))
                    {
                        TarkovPreview.For(handler);
                        TarkovPreview.Emit(action.TriggerId);
                    }
            PreviewGui.End(handler);
        }
    }

    [CustomEditor(typeof(HandlerPlaySoundAdvanced))]
    public class SoundHandlerPreviewEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var handler = (HandlerPlaySoundAdvanced)target;
            if (handler._soundsPool != null && handler._soundsPool.Any(c => Mathf.Approximately(c.Pitch, 0f)))
                EditorGUILayout.HelpBox("A sound has Pitch 0, which the game plays silent. Use 1 for normal pitch.", MessageType.Warning);
            if (!PreviewGui.Begin("sound"))
                return;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (PreviewGui.Button("Play", "A random sound from the pool, as its play trigger does."))
                    SoundHandler.Play(handler);
                if (PreviewGui.Button("Stop"))
                    SoundHandler.Stop(handler);
            }
            PreviewGui.End(handler);
        }
    }

    [CustomEditor(typeof(InteractiveSubscriber))]
    public class SubscriberPreviewEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var subscriber = (InteractiveSubscriber)target;
            if (subscriber.Subscribee == null)
            {
                EditorGUILayout.HelpBox("Set Subscribee: the switch or door whose state plays these sounds. Without one the game throws at raid start.", MessageType.Error);
                return;
            }
            if (!PreviewGui.Begin("announcement"))
                return;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (PreviewGui.Button($"Open '{subscriber.Subscribee.name}'"))
                    ((InteractiveSim)TarkovPreview.For(subscriber.Subscribee)).PlayerInteract(EInteractionType.Open);
                if (PreviewGui.Button($"Close '{subscriber.Subscribee.name}'"))
                    ((InteractiveSim)TarkovPreview.For(subscriber.Subscribee)).PlayerInteract(EInteractionType.Close);
            }
            PreviewGui.End(subscriber);
        }
    }

    [CustomEditor(typeof(QueuePlayer))]
    public class QueuePlayerPreviewEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var player = (QueuePlayer)target;
            int speakers = player.GetComponentsInChildren<AudioSource>().Length;
            EditorGUILayout.LabelField(speakers == 0 ? "No speakers: add AudioSources as children (GameObject > Tarkov SDK > Sound > PA > Speaker)."
                                           : $"{speakers} speaker(s). Announcements play on all of them at once, one after another.", EditorStyles.wordWrappedMiniLabel);
            var subscribers = FindObjectsOfType<InteractiveSubscriber>().Where(s => s.QueuePlayer == player).ToList();
            EditorGUILayout.LabelField($"{subscribers.Count} announcement(s) queue here.", EditorStyles.miniLabel);
            if (!PreviewGui.Begin("PA system"))
                return;
            foreach (var subscriber in subscribers)
                foreach (var sound in subscriber.Sounds ?? new InteractiveSubscriber.PlaySettings[0])
                    if (sound?.Clip != null && PreviewGui.Button($"Queue '{sound.Clip.name}'"))
                        ((QueuePlayerSim)TarkovPreview.For(player)).Play(sound.Clip);
            PreviewGui.End(player);
        }
    }
}
