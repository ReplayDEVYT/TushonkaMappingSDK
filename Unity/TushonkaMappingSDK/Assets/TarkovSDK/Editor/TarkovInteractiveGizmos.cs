using System.Collections.Generic;
using System.Linq;
using EFT.GameTriggers;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Which components react to a trigger id, and which switches operate an object: for gizmos and
    // inspectors. Rebuilt when the hierarchy changes.
    [InitializeOnLoad]
    public static class TarkovTriggerLinks
    {
        private static Switch[] _switches;
        private static List<Component> _handlers;

        static TarkovTriggerLinks()
        {
            EditorApplication.hierarchyChanged += () =>
            {
                _switches = null;
                _handlers = null;
            };
        }

        public static IEnumerable<Switch> Switches => _switches ??= Object.FindObjectsOfType<Switch>(true);

        public static List<Component> Handlers =>
            _handlers ??= Object.FindObjectsOfType<HandlerAnimator>(true).Cast<Component>()
                .Concat(Object.FindObjectsOfType<HandlerPlaySoundAdvanced>(true))
                .Concat(Object.FindObjectsOfType<BaseTriggerHandler>(true))
                .Concat(Object.FindObjectsOfType<HandlerFilterOnce>(true))
                .Concat(Object.FindObjectsOfType<HandlerDamage>(true))
                .Concat(Object.FindObjectsOfType<HandlerEffect>(true))
                .Concat(Object.FindObjectsOfType<HandlerTriggerState>(true))
                // CustomMapLoader's own listeners (forced transits, gear strips, notifications...), run by the client plugin
                .Concat(Object.FindObjectsOfType<TarkovSDK.TarkovForceTransit>(true))
                .Concat(Object.FindObjectsOfType<TarkovSDK.TarkovGearStrip>(true))
                .Concat(Object.FindObjectsOfType<TarkovSDK.TarkovNotification>(true))
                .Concat(Object.FindObjectsOfType<TarkovSDK.TarkovCaption>(true))
                .Concat(Object.FindObjectsOfType<TarkovSDK.TarkovHealthChange>(true))
                .Concat(Object.FindObjectsOfType<TarkovSDK.TarkovEffect>(true)).ToList();

        public static IEnumerable<Component> Listeners(string triggerId)
        {
            if (string.IsNullOrEmpty(triggerId))
                yield break;
            foreach (var h in Handlers)
            {
                if (h == null)
                    continue;
                bool listens = h switch
                {
                    HandlerAnimator a => a._animatorActions != null && a._animatorActions.Any(x => x.TriggerId == triggerId),
                    HandlerPlaySoundAdvanced s => s._playTriggerId == triggerId || s._stopTriggerId == triggerId,
                    BaseTriggerHandler b => b._triggerId == triggerId,
                    HandlerFilterOnce f => f._inputTriggerIds != null && f._inputTriggerIds.Contains(triggerId) || f._resetTriggerId == triggerId,
                    HandlerDamage d => d._triggerIdStartDamage == triggerId || d._triggerIdFinishDamage == triggerId,
                    HandlerEffect e => e._triggerId == triggerId,
                    HandlerTriggerState s => s._triggerId == triggerId,
                    TarkovSDK.TarkovForceTransit f => f.TriggerId == triggerId,
                    TarkovSDK.TarkovGearStrip g => g.TriggerId == triggerId,
                    TarkovSDK.TarkovNotification n => n.TriggerId == triggerId,
                    TarkovSDK.TarkovCaption c => c.TriggerId == triggerId,
                    TarkovSDK.TarkovHealthChange hc => hc.TriggerId == triggerId || hc.StopTriggerId == triggerId,
                    TarkovSDK.TarkovEffect fx => fx.TriggerId == triggerId,
                    _ => false,
                };
                if (listens)
                    yield return h;
            }
        }

        // every trigger id something sends, with its senders: zones, objects' Triggers Maps, filters
        public static Dictionary<string, List<Component>> Senders()
        {
            var senders = new Dictionary<string, List<Component>>();
            void Add(string id, Component c)
            {
                if (string.IsNullOrEmpty(id))
                    return;
                if (!senders.TryGetValue(id, out var list))
                    senders[id] = list = new List<Component>();
                list.Add(c);
            }
            foreach (var zone in Object.FindObjectsOfType<TriggerZone>(true))
                Add(zone._triggerId, zone);
            foreach (var wio in Object.FindObjectsOfType<WorldInteractiveObject>(true))
                if (wio.TriggersMap != null)
                    foreach (var pair in wio.TriggersMap)
                        Add(pair.TriggerId, wio);
            foreach (var filter in Object.FindObjectsOfType<HandlerFilterOnce>(true))
                Add(filter._outputTriggerId, filter);
            return senders;
        }

        // the ids a listener reacts to
        public static IEnumerable<string> ListensTo(Component listener) => listener switch
        {
            HandlerAnimator a => (a._animatorActions ?? new HandlerAnimator.AnimatorAction[0]).Select(x => x.TriggerId),
            HandlerPlaySoundAdvanced s => new[] { s._playTriggerId, s._stopTriggerId },
            BaseTriggerHandler b => new[] { b._triggerId },
            HandlerFilterOnce f => (f._inputTriggerIds ?? new string[0]).Concat(new[] { f._resetTriggerId }),
            TarkovSDK.TarkovForceTransit f => new[] { f.TriggerId },
            TarkovSDK.TarkovGearStrip g => new[] { g.TriggerId },
            TarkovSDK.TarkovNotification n => new[] { n.TriggerId },
            TarkovSDK.TarkovCaption c => new[] { c.TriggerId },
            TarkovSDK.TarkovHealthChange hc => new[] { hc.TriggerId, hc.StopTriggerId },
            TarkovSDK.TarkovEffect fx => new[] { fx.TriggerId },
            _ => Enumerable.Empty<string>(),
        };

        // trigger ids that something listens to but nothing sends, and the other way round
        public static List<(Component Where, string Problem)> Problems()
        {
            var problems = new List<(Component, string)>();
            var senders = Senders();
            var heard = new HashSet<string>();
            var objectIds = Object.FindObjectsOfType<WorldInteractiveObject>(true).Where(w => !string.IsNullOrEmpty(w.Id))
                .GroupBy(w => w.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (var listener in Handlers.Where(h => h != null))
                foreach (var id in ListensTo(listener).Where(i => !string.IsNullOrEmpty(i)).Distinct())
                {
                    heard.Add(id);
                    if (senders.ContainsKey(id))
                        continue;
                    string hint = objectIds.TryGetValue(id, out var owner)
                        ? $" '{id}' is the Id of '{owner.name}', but an object's Id isn't a trigger id: add it to that object's Triggers Map (or connect it with Tarkov SDK > Tools > Interactables > Triggers & Sounds)."
                        : " Connect a zone or a switch to it with Tarkov SDK > Tools > Interactables > Triggers & Sounds.";
                    problems.Add((listener, $"{listener.GetType().Name} listens to '{id}', but nothing sends it.{hint}"));
                }
            foreach (var pair in senders)
                if (!heard.Contains(pair.Key))
                    foreach (var sender in pair.Value)
                        problems.Add((sender, $"{sender.GetType().Name} sends '{pair.Key}', but nothing listens to it."));
            return problems;
        }

        // where a listener acts: the animator it drives, the object it interacts with, where it plays
        public static Vector3 ActsAt(Component listener) => listener switch
        {
            HandlerAnimator a when a._target != null => a._target.transform.position,
            HandlerInteractable i when i._interactable != null => i._interactable.transform.position,
            HandlerPlaySoundAdvanced s when s._overrideSoundPosition != null => s._overrideSoundPosition.position,
            HandlerLamp l when l._controlledLampGroups != null && l._controlledLampGroups.Any(g => g != null) => l._controlledLampGroups.First(g => g != null).transform.position,
            HandlerLamp l when l._lampControllers != null && l._lampControllers.Any(g => g != null) => l._lampControllers.First(g => g != null).transform.position,
            TarkovSDK.TarkovForceTransit f when f.Transit != null => f.Transit.transform.position,
            TarkovSDK.TarkovGearStrip g when g.Container != null => g.Container.transform.position,
            _ => listener.transform.position,
        };
    }

    public static class TarkovInteractiveGizmos
    {
        private static readonly Color Shut = new Color(1f, 0.25f, 0.2f);
        private static readonly Color Open = new Color(0.25f, 1f, 0.3f);
        private static readonly Color Kick = new Color(1f, 0.6f, 0.1f);
        private static readonly Color DoorLink = new Color(0.3f, 0.9f, 1f);
        private static readonly Color LampLink = new Color(1f, 0.9f, 0.3f);
        private static readonly Color SwitchLink = new Color(1f, 0.55f, 0.15f);
        private static readonly Color ExfilLink = new Color(0.3f, 1f, 0.45f);
        private static readonly Color TriggerColor = new Color(0.75f, 0.35f, 1f);
        private static readonly Color SoundColor = new Color(1f, 0.35f, 0.75f);

        // ------------------------------------------------------------ doors, switches, containers

        [DrawGizmo(GizmoType.Selected | GizmoType.Active)]
        private static void DrawInteractive(WorldInteractiveObject wio, GizmoType type)
        {
            var t = wio.transform;
            var parent = t.parent;
            var axis = InteractiveSim.GetRotationAxis(wio.DoorAxis, parent);
            bool rotates = !(wio is Switch sw0 && sw0.DontAnimateRotation) && axis != Vector3.zero && !wio._keepRotation;

            if (rotates)
                DrawSwing(wio, t, axis);

            // where a player stands to use it, and what they look at (the game's own gizmo)
            if (parent != null)
            {
                Handles.color = new Color(0.3f, 0.5f, 1f);
                foreach (var (p, name) in new[] { (wio.interactPosition1, "stand 1"), (wio.interactPosition2, "stand 2") })
                {
                    if (p == Vector3.zero)
                        continue;
                    var world = parent.rotation * p + t.position;
                    Handles.DrawWireCube(world, Vector3.one * 0.1f);
                    Handles.Label(world, name, EditorStyles.miniLabel);
                }
            }
            if (wio.viewTarget1 != Vector3.zero)
            {
                Gizmos.color = Shut;
                Gizmos.DrawWireSphere(t.TransformPoint(wio.viewTarget1), 0.05f);
            }

            // a switch that slides instead of turning
            if (wio is Switch slide && (slide.ShutPosition.sqrMagnitude > 0f || slide.OpenPosition.sqrMagnitude > 0f))
            {
                var a = parent != null ? parent.TransformPoint(slide.ShutPosition) : slide.ShutPosition;
                var b = parent != null ? parent.TransformPoint(slide.OpenPosition) : slide.OpenPosition;
                Handles.color = Open;
                Handles.DrawDottedLine(a, b, 3f);
                Handles.Label(a, "shut", EditorStyles.miniLabel);
                Handles.Label(b, "open", EditorStyles.miniLabel);
            }
            // a drawer: its body slides between these
            if (wio is LootableContainer drawer && drawer.OpenPosition != drawer.ClosedPosition && parent != null)
            {
                Handles.color = Open;
                Handles.DrawDottedLine(parent.TransformPoint(drawer.ClosedPosition), parent.TransformPoint(drawer.OpenPosition), 3f);
                Handles.Label(parent.TransformPoint(drawer.OpenPosition), "open", EditorStyles.miniLabel);
            }

            if (wio is Switch sw)
                DrawSwitchLinks(sw);
            DrawOperatedBy(wio);
        }

        // the leaf's sweep from shut to open (and to the kick angle), around the hinge axis
        private static void DrawSwing(WorldInteractiveObject wio, Transform t, Vector3 axis)
        {
            var sim = TarkovPreview.Find(wio) as InteractiveSim;
            float current = sim != null ? sim.Angle : wio._doorState == EDoorState.Open ? wio.OpenAngle : wio.CloseAngle;
            // the leaf's direction and size from its renderers, taken back to the shut angle
            var renderers = wio.GetComponentsInChildren<Renderer>();
            Vector3 leaf;
            float radius;
            if (renderers.Length > 0)
            {
                var bounds = renderers[0].bounds;
                foreach (var r in renderers)
                    bounds.Encapsulate(r.bounds);
                leaf = Vector3.ProjectOnPlane(bounds.center - t.position, axis);
                radius = Mathf.Max(0.2f, leaf.magnitude * 2f);
                if (leaf.sqrMagnitude < 0.0001f)
                    leaf = Vector3.ProjectOnPlane(InteractiveSim.GetRotationAxis(wio.DoorForward, t), axis);
            }
            else
            {
                leaf = Vector3.ProjectOnPlane(InteractiveSim.GetRotationAxis(wio.DoorForward, t), axis);
                radius = 0.6f;
            }
            if (leaf.sqrMagnitude < 0.0001f)
                return;
            var shutDir = Quaternion.AngleAxis(wio.CloseAngle - current, axis) * leaf.normalized;
            float sweep = Mathf.DeltaAngle(wio.CloseAngle, wio.OpenAngle);
            var openDir = Quaternion.AngleAxis(sweep, axis) * shutDir;
            var hinge = t.position;

            Handles.color = new Color(Open.r, Open.g, Open.b, 0.08f);
            Handles.DrawSolidArc(hinge, axis, shutDir, sweep, radius);
            Handles.color = new Color(Open.r, Open.g, Open.b, 0.6f);
            Handles.DrawWireArc(hinge, axis, shutDir, sweep, radius);
            Handles.color = Color.white;
            Handles.DrawLine(hinge - axis * 0.3f, hinge + axis * 0.3f);
            Handles.color = Shut;
            Handles.DrawLine(hinge, hinge + shutDir * radius);
            Handles.Label(hinge + shutDir * radius, $"shut {wio.CloseAngle:0.#}°", EditorStyles.miniLabel);
            Handles.color = Open;
            Handles.DrawLine(hinge, hinge + openDir * radius);
            Handles.Label(hinge + openDir * radius, $"open {wio.OpenAngle:0.#}°", EditorStyles.miniLabel);

            if (wio is Door door && door.CanBeBreached && door.KickCurve != null && door.KickCurve.length > 0)
            {
                float kick = door.KickCurve[door.KickCurve.length - 1].value * door.OpenAngle;
                if (!Mathf.Approximately(kick, door.OpenAngle))
                {
                    var kickDir = Quaternion.AngleAxis(Mathf.DeltaAngle(door.CloseAngle, kick), axis) * shutDir;
                    Handles.color = Kick;
                    Handles.DrawDottedLine(hinge, hinge + kickDir * radius, 3f);
                    Handles.Label(hinge + kickDir * radius, $"kick {kick:0.#}°", EditorStyles.miniLabel);
                }
            }
        }

        private static void Link(Vector3 from, Vector3 to, Color color, string label, bool dotted = false)
        {
            Handles.color = color;
            if (dotted)
                Handles.DrawDottedLine(from, to, 4f);
            else
                Handles.DrawAAPolyLine(2.5f, from, to);
            if (!string.IsNullOrEmpty(label))
                Handles.Label(Vector3.Lerp(from, to, 0.5f), label, EditorStyles.miniBoldLabel);
        }

        // everything a selected switch operates
        private static void DrawSwitchLinks(Switch sw)
        {
            var from = sw.transform.position;
            if (sw.Door != null)
                Link(from, sw.Door.transform.position, DoorLink, $"{sw.Interaction} door");
            if (sw.Lamps != null)
                foreach (var lamp in sw.Lamps.Where(l => l != null))
                    Link(from, lamp.transform.position, LampLink, "lamp on");
            if (sw.NextSwitches != null)
                foreach (var next in sw.NextSwitches.Where(n => n?.Switch != null))
                    Link(from, next.Switch.transform.position, SwitchLink, $"then {next.Operation}");
            if (sw.PreviousSwitch != null)
                Link(from, sw.PreviousSwitch.transform.position, Color.gray, "needs", dotted: true);
            if (sw.ExfiltrationPoint != null)
            {
                string condition = sw.ConditionStatus != null && sw.ConditionStatus.Length > 0 ? $" if {string.Join("/", sw.ConditionStatus)}" : "";
                Link(from, sw.ExfiltrationPoint.transform.position, ExfilLink, $"exfil → {sw.TargetStatus}{condition}");
            }
            if (sw.TriggersMap != null)
                foreach (var pair in sw.TriggersMap)
                    foreach (var listener in TarkovTriggerLinks.Listeners(pair.TriggerId))
                        Link(from, TarkovTriggerLinks.ActsAt(listener), TriggerColor, $"{pair.State}: {pair.TriggerId}", dotted: true);
        }

        // the switches that operate a selected object
        private static void DrawOperatedBy(Component target)
        {
            foreach (var sw in TarkovTriggerLinks.Switches)
            {
                if (sw == null || sw == target)
                    continue;
                bool operates = sw.Door == target || sw.Lamps != null && sw.Lamps.Contains(target as LampController)
                                || sw.NextSwitches != null && sw.NextSwitches.Any(n => n?.Switch == target);
                if (operates)
                    Link(sw.transform.position, target.transform.position, Color.gray, "switch " + sw.name, dotted: true);
            }
        }

        // ------------------------------------------------------------ lamps

        [DrawGizmo(GizmoType.Selected | GizmoType.Active)]
        private static void DrawLamp(LampController lamp, GizmoType type)
        {
            var from = lamp.transform.position;
            foreach (var light in lamp.Lights ?? new Light[0])
                if (light != null && light.transform != lamp.transform)
                    Link(from, light.transform.position, LampLink, null);
            foreach (var area in lamp.AreaAndTubeLights ?? new BaseLight[0])
                if (area != null)
                    Link(from, area.transform.position, LampLink, "area light");
            foreach (var flare in lamp.MultiFlareLights ?? new MultiFlare.FlareLight[0])
                if (flare != null)
                    Link(from, flare.transform.position, LampLink, "flare");
            DrawOperatedBy(lamp);
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.Active)]
        private static void DrawLampGroup(ControlledLampGroup group, GizmoType type)
        {
            var from = group.transform.position;
            foreach (var light in group._lights ?? new Light[0])
                if (light != null && light.transform != group.transform)
                    Link(from, light.transform.position, LampLink, null);
            foreach (var area in group._baseLights ?? new BaseLight[0])
                if (area != null)
                    Link(from, area.transform.position, LampLink, "area light");
            foreach (var lamp in group._lampControllers ?? new LampController[0])
                if (lamp != null)
                    Link(from, lamp.transform.position, LampLink, "lamp");
            foreach (var r in group._meshRenderers ?? new MeshRenderer[0])
                if (r != null)
                {
                    Gizmos.color = new Color(LampLink.r, LampLink.g, LampLink.b, 0.5f);
                    Gizmos.DrawWireCube(r.bounds.center, r.bounds.size);
                }
            var lines = new List<string> { "lamp group, starts " + (group._initialLightState ? "on" : "off") };
            foreach (var handler in TarkovTriggerLinks.Handlers.OfType<HandlerLamp>())
                if (handler._controlledLampGroups != null && handler._controlledLampGroups.Contains(group))
                {
                    string what = handler._isToggleMode ? "toggle" : handler._state ? "on" : "off";
                    lines.Add($"{what} on '{handler._triggerId}'");
                    foreach (var sender in TarkovTriggerLinks.Senders().TryGetValue(handler._triggerId ?? "", out var list) ? list : new List<Component>())
                        if (sender != null)
                            Link(sender.transform.position, from, TriggerColor, what, dotted: true);
                }
            Handles.Label(from + Vector3.up * 0.4f, string.Join("\n", lines), EditorStyles.miniBoldLabel);
        }

        // ------------------------------------------------------------ game triggers

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
        private static void DrawTriggerZone(TriggerZone zone, GizmoType type)
        {
            // several TriggerZones share one collider; draw it once
            if (zone.GetComponents<TriggerZone>()[0] != zone)
                return;
            bool selected = (type & GizmoType.Selected) != 0;
            var box = zone.GetComponent<BoxCollider>();
            if (box != null)
            {
                Gizmos.matrix = zone.transform.localToWorldMatrix;
                Gizmos.color = new Color(TriggerColor.r, TriggerColor.g, TriggerColor.b, selected ? 0.18f : 0.06f);
                Gizmos.DrawCube(box.center, box.size);
                Gizmos.color = new Color(TriggerColor.r, TriggerColor.g, TriggerColor.b, selected ? 0.9f : 0.4f);
                Gizmos.DrawWireCube(box.center, box.size);
                Gizmos.matrix = Matrix4x4.identity;
            }
            if (!selected)
                return;
            var center = box != null ? zone.transform.TransformPoint(box.center) : zone.transform.position;
            var lines = new List<string>();
            foreach (var trigger in zone.GetComponents<TriggerZone>())
            {
                lines.Add($"{trigger._mode}: {trigger._triggerId}");
                foreach (var listener in TarkovTriggerLinks.Listeners(trigger._triggerId))
                {
                    Link(center, TarkovTriggerLinks.ActsAt(listener), TriggerColor, listener.GetType().Name);
                    if (listener is HandlerInteractable i && i._interactable != null && i._interactable.transform.position != listener.transform.position)
                        Link(listener.transform.position, i._interactable.transform.position, TriggerColor, i._interactionType.ToString(), dotted: true);
                }
            }
            Handles.Label(center + Vector3.up * 0.5f, string.Join("\n", lines), EditorStyles.miniBoldLabel);
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.Active)]
        private static void DrawSoundHandler(HandlerPlaySoundAdvanced handler, GizmoType type)
        {
            var position = handler._overrideSoundPosition != null ? handler._overrideSoundPosition.position : handler.transform.position;
            var first = handler._soundsPool != null && handler._soundsPool.Length > 0 ? handler._soundsPool[0] : default;
            Gizmos.color = new Color(SoundColor.r, SoundColor.g, SoundColor.b, 0.5f);
            Gizmos.DrawWireSphere(position, Mathf.Max(0.5f, first.RollOff));
            Handles.Label(position, $"sound: play '{handler._playTriggerId}'" + (first.IsForceStereo ? " (2D)" : "") + $"\nheard to {first.RollOff} m", EditorStyles.miniBoldLabel);
        }

        // ------------------------------------------------------------ traps, keycard readers, quest zones

        private static readonly Color TrapColor = new Color(1f, 0.3f, 0.15f);
        private static readonly Color QuestColor = new Color(0.3f, 0.8f, 1f);

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
        private static void DrawBarbedWire(BarbedWire wire, GizmoType type)
        {
            var box = wire.GetComponent<BoxCollider>();
            if (box == null)
                return;
            bool selected = (type & GizmoType.Selected) != 0;
            Gizmos.matrix = wire.transform.localToWorldMatrix;
            Gizmos.color = new Color(TrapColor.r, TrapColor.g, TrapColor.b, selected ? 0.9f : 0.35f);
            Gizmos.DrawWireCube(box.center, box.size);
            Gizmos.matrix = Matrix4x4.identity;
            if (selected)
                Handles.Label(wire.transform.TransformPoint(box.center) + Vector3.up * 0.4f,
                              "barbed wire" + (wire.GetComponent<CommonAssets.Scripts.Game.LabyrinthEvent.TrapSyncable>() != null ? " (random per raid)" : ""), EditorStyles.miniBoldLabel);
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.Active)]
        private static void DrawTrapSyncable(CommonAssets.Scripts.Game.LabyrinthEvent.TrapSyncable trap, GizmoType type)
        {
            Handles.Label(trap.transform.position + Vector3.up * 0.6f,
                          trap.TrapType == CommonAssets.Scripts.Game.LabyrinthEvent.ETrapType.TrapDoor ? "trap door (cover removed in some raids)" : "random per raid",
                          EditorStyles.miniBoldLabel);
        }

        // a trap switch's handlers: the zones whose triggers it turns off or on
        [DrawGizmo(GizmoType.Selected | GizmoType.Active | GizmoType.InSelectionHierarchy)]
        private static void DrawTriggerState(HandlerTriggerState handler, GizmoType type)
        {
            var senders = TarkovTriggerLinks.Senders();
            foreach (var id in handler._controlledTriggerId ?? new string[0])
                if (!string.IsNullOrEmpty(id) && senders.TryGetValue(id, out var from))
                    foreach (var zone in from.Where(z => z != null))
                        Link(handler.transform.position, zone.transform.position, TrapColor, handler._targetState ? "turns on" : "turns off", dotted: true);
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Active)]
        private static void DrawCardReader(InteractiveProxy proxy, GizmoType type)
        {
            bool selected = (type & GizmoType.Selected) != 0;
            Gizmos.color = new Color(DoorLink.r, DoorLink.g, DoorLink.b, selected ? 0.9f : 0.4f);
            Gizmos.DrawWireSphere(proxy.transform.TransformPoint(proxy._interactionPosition), 0.08f);
            if (!selected || proxy.Link == null)
                return;
            Link(proxy.transform.position, proxy.Link.transform.position, DoorLink, "card reader for");
            Handles.Label(proxy.transform.TransformPoint(proxy._interactionPosition), "player stands here", EditorStyles.miniLabel);
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
        private static void DrawQuestZone(TriggerWithId zone, GizmoType type)
        {
            var box = zone.GetComponent<BoxCollider>();
            if (box == null)
                return;
            bool selected = (type & GizmoType.Selected) != 0;
            Gizmos.matrix = zone.transform.localToWorldMatrix;
            Gizmos.color = new Color(QuestColor.r, QuestColor.g, QuestColor.b, selected ? 0.15f : 0.05f);
            Gizmos.DrawCube(box.center, box.size);
            Gizmos.color = new Color(QuestColor.r, QuestColor.g, QuestColor.b, selected ? 0.9f : 0.4f);
            Gizmos.DrawWireCube(box.center, box.size);
            Gizmos.matrix = Matrix4x4.identity;
            if (selected)
                Handles.Label(zone.transform.TransformPoint(box.center) + Vector3.up * 0.5f,
                              $"{(zone is PlaceItemTrigger ? "place item" : "visit")}: {zone._id}", EditorStyles.miniBoldLabel);
        }

        // ------------------------------------------------------------ elevator doors

        [DrawGizmo(GizmoType.Selected | GizmoType.Active)]
        private static void DrawExfiltrationDoor(ExfiltrationDoor door, GizmoType type)
        {
            foreach (var t in door._doorTransforms ?? new ExfiltrationDoor.DoorTransform[0])
            {
                if (t?.Transform == null)
                    continue;
                var parent = t.Transform.parent;
                var shut = parent != null ? parent.TransformPoint(t.ClosePosition) : t.ClosePosition;
                var open = parent != null ? parent.TransformPoint(t.OpenPosition) : t.OpenPosition;
                Handles.color = Open;
                Handles.DrawDottedLine(shut, open, 3f);
                Handles.DrawWireCube(open, Vector3.one * 0.08f);
                Handles.color = Shut;
                Handles.DrawWireCube(shut, Vector3.one * 0.08f);
            }
            if (door.Subscribee != null)
                Link(door.transform.position, door.Subscribee.transform.position, ExfilLink,
                     door.OpenStatus != null && door.OpenStatus.Length > 0 ? $"open while {string.Join("/", door.OpenStatus)}" : "never opens (no Open Status)", dotted: true);
        }

        // ------------------------------------------------------------ PA

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected)]
        private static void DrawQueuePlayer(QueuePlayer player, GizmoType type)
        {
            bool selected = (type & GizmoType.Selected) != 0;
            foreach (var speaker in player.GetComponentsInChildren<AudioSource>())
            {
                Handles.color = new Color(SoundColor.r, SoundColor.g, SoundColor.b, selected ? 0.8f : 0.25f);
                Handles.DrawDottedLine(player.transform.position, speaker.transform.position, 4f);
                if (selected)
                {
                    Gizmos.color = new Color(SoundColor.r, SoundColor.g, SoundColor.b, 0.35f);
                    Gizmos.DrawWireSphere(speaker.transform.position, speaker.maxDistance);
                    Handles.Label(speaker.transform.position, "speaker", EditorStyles.miniLabel);
                }
            }
            if (selected)
                foreach (var subscriber in Object.FindObjectsOfType<InteractiveSubscriber>().Where(s => s.QueuePlayer == player && s.Subscribee != null))
                    Link(player.transform.position, subscriber.Subscribee.transform.position, SoundColor, "announces on " + string.Join("/", (subscriber.Sounds ?? new InteractiveSubscriber.PlaySettings[0]).Select(s => s.State).Distinct()), dotted: true);
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.Active)]
        private static void DrawSubscriber(InteractiveSubscriber subscriber, GizmoType type)
        {
            if (subscriber.Subscribee != null)
                Link(subscriber.transform.position, subscriber.Subscribee.transform.position, SoundColor, "follows");
            if (subscriber.QueuePlayer != null && subscriber.QueuePlayer.transform != subscriber.transform)
                Link(subscriber.transform.position, subscriber.QueuePlayer.transform.position, SoundColor, "PA");
        }
    }
}
