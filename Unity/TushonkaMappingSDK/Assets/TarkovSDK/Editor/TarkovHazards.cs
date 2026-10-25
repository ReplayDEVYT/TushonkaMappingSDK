using System;
using System.Collections.Generic;
using System.Linq;
using EFT.Interactive;
using EFT.InventoryLogic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // The map's border hazards, from the game's own components with retail values (Customs' custom_DesignMain,
    // Lighthouse's Lighthouse_DesignMain):
    //
    //  - Sniper Zone: a SniperFiringZone (a BorderZone: a trigger box the game checks once a second). A player
    //    in it is shot every 3 s: the first shot hits half the time, then in the buffer strip (the first metres
    //    in from the zone's +X side) 66 % in the limbs, deeper in always in the chest, and after 3 shots in a
    //    row deep in, in the head. Its SniperImitator plays the distant shot and muzzle flash from one of its
    //    sniper positions; the game's own sound and flash (the SVD's) aren't loaded on SDK maps, so the client
    //    fills in a loaded sniper sound and skips the flash.
    //  - Flare Zone: a FlareShootDetectorZone with a PlayersWithImmuneToSniperFireCollector, as Customs'
    //    FlareFireCollector_zone: firing a green flare (ExitActivate) while standing in it makes the player and
    //    their group immune to the sniper zones that use it. A flare extract is an extract behind such a zone.
    //  - Immune items: the zone also spares players carrying one of these items, which Tarkov itself doesn't
    //    do; CustomMapLoader's client does it from the zone's "SDK_ImmuneItem <template>" children.
    //  - Minefield: a Minefield (a BorderZone) with MinefieldView for the blasts: a step deep into it explodes
    //    (60 to the legs and stomach, 250 on the next blast 4-7 m on), with contusion and damage around.
    //  - Claymore: a MineDirectional (MON-50) with three 12 m tripwire triggers fanned out in front; it goes
    //    off once, fragments in a 60 degree cone.
    public static class TarkovHazards
    {
        private const string Menu = "GameObject/Tarkov SDK/Hazards/";
        public const string ImmuneItemPrefix = "SDK_ImmuneItem ";
        // the 7.62x54R the game's snipers fire (SniperFiringZone default)
        public const string SniperAmmo = "560d61e84bdc2da74d8b4571";

        [MenuItem(Menu + "Sniper Zone", priority = 38)]
        private static void MenuSniper(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateSniperZone(p).gameObject);

        [MenuItem(Menu + "Flare Zone (Sniper Immunity)", priority = 39)]
        private static void MenuFlare(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateFlareZone(p).gameObject);

        [MenuItem(Menu + "Minefield", priority = 40)]
        private static void MenuMinefield(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateMinefield(p).gameObject);

        [MenuItem(Menu + "Claymore", priority = 41)]
        private static void MenuClaymore(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateClaymore(p).gameObject);

        // a BorderZone's box: centred on its transform (the game measures from the transform), on Triggers
        private static BoxCollider ZoneBox(GameObject go, Vector3 size)
        {
            go.layer = LayerMask.NameToLayer("Triggers");
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.center = Vector3.zero;
            box.size = size;
            return box;
        }

        // ------------------------------------------------------------ sniper zone

        public static SniperFiringZone CreateSniperZone(Transform parent)
        {
            var go = TarkovFacilitySystems.Child(parent, "SniperZone", Vector3.zero);
            var box = ZoneBox(go, new Vector3(56f, 20f, 25f));
            var zone = go.AddComponent<SniperFiringZone>();
            zone.Collider = box;
            zone._extents = box.size * 0.5f;
            // retail: a 13 m buffer strip on the +X side, the rest is the deep zone
            zone._triggerZoneSettings = new Vector4(0f, 13f, 0f, 0f);
            zone._shotFrequency = 3f;
            zone._triggerZoneHitProbability = 1f;
            zone._bufferZoneHitProbability = 0.66f;
            zone._firstShotHitProbability = 0.5f;
            zone._becomeLethalIfShotsCountExceed = 3;
            zone._ammoTemplate = SniperAmmo;
            zone._triggerZoneTarget = new List<EBodyPart> { EBodyPart.Chest };
            zone._bufferZoneTarget = new List<EBodyPart> { EBodyPart.Stomach, EBodyPart.LeftArm, EBodyPart.RightArm, EBodyPart.LeftLeg, EBodyPart.RightLeg };

            var sniper = TarkovFacilitySystems.Child(go.transform, "Sniper", Vector3.zero);
            var imitator = sniper.AddComponent<SniperImitator>();
            imitator.SniperFiringZone = zone;
            imitator.SniperPositions = new List<Transform>();
            for (int i = 0; i < 3; i++)
                imitator.SniperPositions.Add(TarkovFacilitySystems.Child(sniper.transform, $"Sniper Position {i + 1}", new Vector3(-90f, 25f, (i - 1) * 30f)).transform);
            imitator._effectsTransform = TarkovFacilitySystems.Child(sniper.transform, "SniperEffects", Vector3.zero).transform;
            return zone;
        }

        public static List<string> ImmuneItems(SniperFiringZone zone) =>
            zone.transform.Cast<Transform>().Where(t => t.name.StartsWith(ImmuneItemPrefix, StringComparison.Ordinal))
                .Select(t => t.name.Substring(ImmuneItemPrefix.Length).Trim().Split(' ')[0]).ToList();

        public static void AddImmuneItem(SniperFiringZone zone, string template)
        {
            if (ImmuneItems(zone).Contains(template, StringComparer.OrdinalIgnoreCase))
                return;
            var go = TarkovFacilitySystems.Child(zone.transform, ImmuneItemPrefix + template, Vector3.zero);
            Undo.RegisterCreatedObjectUndo(go, "Add immune item");
        }

        public static void RemoveImmuneItem(SniperFiringZone zone, string template)
        {
            foreach (var t in zone.transform.Cast<Transform>().ToList())
                if (t.name.StartsWith(ImmuneItemPrefix + template, StringComparison.OrdinalIgnoreCase))
                    Undo.DestroyObjectImmediate(t.gameObject);
        }

        // ------------------------------------------------------------ flare zone

        public static PlayersWithImmuneToSniperFireCollector CreateFlareZone(Transform parent)
        {
            var go = TarkovFacilitySystems.Child(parent, "FlareZone", Vector3.zero);
            // retail: 30 x 2 x 10 m, where the player stands to fire
            var box = ZoneBox(go, new Vector3(30f, 2f, 10f));
            box.center = new Vector3(0f, 1f, 0f);
            var handler = go.AddComponent<PhysicsTriggerHandler>();
            handler.trigger = box;
            var detector = go.AddComponent<FlareShootDetectorZone>();
            detector.zoneID = "";
            detector.flareTypeForHandle = FlareEventType.ExitActivate;
            detector._triggerHandlers = new List<PhysicsTriggerHandler> { handler };
            var collector = go.AddComponent<PlayersWithImmuneToSniperFireCollector>();
            collector.flareTypeForImmune = FlareEventType.ExitActivate;
            return collector;
        }

        // ------------------------------------------------------------ minefield

        public static Minefield CreateMinefield(Transform parent)
        {
            var go = TarkovFacilitySystems.Child(parent, "Minefield", Vector3.zero);
            var box = ZoneBox(go, new Vector3(40f, 12f, 40f));
            var field = go.AddComponent<Minefield>();
            field.Collider = box;
            field._extents = box.size * 0.5f;
            // Customs' Minefield_rightroad, scaled to this smaller box: the edges wobble (distortion; -11.3 there)
            // and a strip on the +X side is safe (25 m of 112 there)
            field._triggerZoneSettings = new Vector4(0f, 8f, 0f, 0f);
            field._zoneDistortionPeriod = 0.06f;
            field._zoneDistortionPower = -4f;
            field._contusionStrength = 1f;
            field._contusionDuration = 15f;
            field._collateralContusionRange = 15f;
            field._collateralDamageRange = 6f;
            field._explosionTarget = new List<EBodyPart> { EBodyPart.LeftLeg, EBodyPart.RightLeg, EBodyPart.Stomach };
            field._collateralDamageTarget = new List<EBodyPart> { EBodyPart.Chest, EBodyPart.Stomach, EBodyPart.LeftLeg, EBodyPart.RightLeg, EBodyPart.Head, EBodyPart.LeftArm, EBodyPart.RightArm };
            field._firstExplosionDamage = 60f;
            field._minRangeToNextExplosion = 4f;
            field._maxRangeToNextExplosion = 7f;
            field._secondExplosionDamage = 250f;

            var view = go.AddComponent<MinefieldView>();
            view._minefield = field;
            view._effectName = "landmine";
            view._cameraShakeRange = 30f;
            view._explosionLocalPosition = new Vector3(0f, 0f, 0.7f);
            view._raycastToTheGround = false;
            view._raycastMask = 264192;
            return field;
        }

        // ------------------------------------------------------------ claymore

        public static MineDirectional CreateClaymore(Transform parent)
        {
            var go = TarkovFacilitySystems.Child(parent, "Claymore", Vector3.zero, "Triggers");
            var mine = go.AddComponent<MineDirectional>();
            // Lighthouse's MON-50s
            mine._mineData = new MineDirectional.MineSettings
            {
                _blindness = new Vector3(10f, 10f, 10f),
                _contusion = new Vector3(10f, 10f, 10f),
                _armorDistanceDistanceDamage = new Vector3(10f, 10f, 10f),
                _minExplosionDistance = 0f,
                _maxExplosionDistance = 13f,
                _fragmentsCount = 100,
                _strength = 1000f,
                _tag = "default",
                _armorDamage = 0.7f,
                _staminaBurnRate = 5f,
                _penetrationPower = 32f,
                _fragmentType = "5996f6d686f77467977ba6cc",
                _fxName = "Grenade_new",
                _directionalDamageAngle = 60f,
                _directionalDamageMultiplier = 7f,
            };
            // three tripwires fanned out 12 m in front (+Z), as retail
            foreach (var (x, yaw) in new[] { (-0.71f, -7.5f), (0f, 0f), (0.71f, 7.5f) })
            {
                var wire = TarkovFacilitySystems.Child(go.transform, "MineTrigger", new Vector3(x, 0.32f, 5.7f), "Triggers");
                wire.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
                wire.transform.localScale = new Vector3(1.5f, 1f, 12f);
                var box = wire.AddComponent<BoxCollider>();
                box.isTrigger = true;
                wire.AddComponent<MineDirectionalColliders>();
            }
            var body = TarkovFacilitySystems.Child(go.transform, "Collider", new Vector3(0f, -0.05f, -0.03f), "LowPolyCollider");
            body.AddComponent<BoxCollider>().size = new Vector3(0.22f, 0.3f, 0.08f);

            var model = GameObject.CreatePrimitive(PrimitiveType.Cube);
            model.name = "Model (replace with your claymore model)";
            Object.DestroyImmediate(model.GetComponent<Collider>());
            model.transform.SetParent(go.transform, false);
            model.transform.localPosition = new Vector3(0f, 0.08f, 0f);
            model.transform.localScale = new Vector3(0.22f, 0.15f, 0.05f);
            var material = TarkovSceneTools.DefaultSurfaceMaterial();
            if (material != null)
                model.GetComponent<MeshRenderer>().sharedMaterial = material;
            return mine;
        }

        // ------------------------------------------------------------ build

        // BorderZone.OnValidate (the extents from the box) and PhysicsTriggerHandler set-up only run in BSG's
        // editor; Build Map does them
        public static void Prepare(GameObject[] roots, string sceneName, TarkovScenePreparer.Report report)
        {
            foreach (var zone in roots.SelectMany(r => r.GetComponentsInChildren<BorderZone>(true)))
            {
                if (zone.Collider == null)
                    zone.Collider = zone.GetComponent<BoxCollider>();
                if (zone.Collider == null)
                {
                    report.Errors.Add($"{sceneName}/{zone.name}: {zone.GetType().Name} needs a BoxCollider on its object");
                    continue;
                }
                zone.Collider.isTrigger = true;
                zone.gameObject.layer = LayerMask.NameToLayer("Triggers");
                zone._extents = zone.Collider.size * 0.5f;
                if (zone.Collider.center != Vector3.zero)
                    report.Warnings.Add($"{sceneName}/{zone.name}: the game measures the zone from its transform, but its box is off-centre; move the object instead of the box centre");
                if (zone is SniperFiringZone sniper && !roots.SelectMany(r => r.GetComponentsInChildren<SniperImitator>(true)).Any(i => i.SniperFiringZone == sniper))
                    report.Warnings.Add($"{sceneName}/{zone.name}: sniper zone has no SniperImitator, so its shots are silent");
                EditorUtility.SetDirty(zone);
            }
            foreach (var imitator in roots.SelectMany(r => r.GetComponentsInChildren<SniperImitator>(true)))
            {
                imitator.SniperPositions?.RemoveAll(t => t == null);
                if (imitator.SniperFiringZone == null || imitator.SniperPositions == null || imitator.SniperPositions.Count == 0)
                    report.Errors.Add($"{sceneName}/{imitator.name}: SniperImitator needs its sniper zone and at least one sniper position (it throws on the first shot otherwise)");
                if (imitator._effectsTransform == null)
                    imitator._effectsTransform = imitator.transform;
                EditorUtility.SetDirty(imitator);
            }
            foreach (var detector in roots.SelectMany(r => r.GetComponentsInChildren<FlareShootDetectorZone>(true)))
            {
                detector._triggerHandlers ??= new List<PhysicsTriggerHandler>();
                detector._triggerHandlers.RemoveAll(h => h == null);
                if (detector._triggerHandlers.Count == 0)
                    detector._triggerHandlers.AddRange(detector.GetComponentsInChildren<PhysicsTriggerHandler>(true));
                foreach (var handler in detector._triggerHandlers)
                {
                    handler.trigger ??= handler.GetComponent<Collider>();
                    if (handler.trigger == null)
                        report.Errors.Add($"{sceneName}/{handler.name}: flare zone trigger handler has no collider");
                    else
                    {
                        handler.trigger.isTrigger = true;
                        handler.gameObject.layer = LayerMask.NameToLayer("Triggers");
                    }
                    EditorUtility.SetDirty(handler);
                }
                if (detector._triggerHandlers.Count == 0)
                    report.Errors.Add($"{sceneName}/{detector.name}: flare zone has no PhysicsTriggerHandler with a trigger box");
                EditorUtility.SetDirty(detector);
            }
            foreach (var mine in roots.SelectMany(r => r.GetComponentsInChildren<MineDirectional>(true)))
            {
                var wires = mine.GetComponentsInChildren<MineDirectionalColliders>(true);
                if (wires.Length == 0)
                    report.Warnings.Add($"{sceneName}/{mine.name}: claymore has no tripwires (MineDirectionalColliders with trigger boxes), so nothing sets it off");
                foreach (var wire in wires)
                {
                    wire.gameObject.layer = LayerMask.NameToLayer("Triggers");
                    foreach (var c in wire.GetComponents<Collider>())
                        c.isTrigger = true;
                }
            }
        }
    }

    [CustomEditor(typeof(SniperFiringZone))]
    public class SniperFiringZoneEditor : UnityEditor.Editor
    {
        private readonly TarkovItemPicker.State _picker = new TarkovItemPicker.State();

        public override void OnInspectorGUI()
        {
            var zone = (SniperFiringZone)target;
            EditorGUILayout.LabelField("Sniper zone", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("A player inside is shot every few seconds. The strip along the +X side (Buffer Zone, red in the Scene view) " +
                                       "gets limb hits two times in three; deeper in, every shot hits the chest, then the head after a few in a row.",
                                       EditorStyles.wordWrappedMiniLabel);
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();
            var settings = zone._triggerZoneSettings;
            float buffer = EditorGUILayout.FloatField(new GUIContent("Buffer Zone (m)", "How far in from the +X side the buffer strip reaches."), settings.y);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(zone, "Sniper buffer");
                zone._triggerZoneSettings = new Vector4(settings.x, Mathf.Max(0f, buffer), settings.z, settings.w);
                EditorUtility.SetDirty(zone);
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_shotFrequency"), new GUIContent("Seconds Between Shots"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_firstShotHitProbability"), new GUIContent("First Shot Hit Chance"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_bufferZoneHitProbability"), new GUIContent("Buffer Hit Chance"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_becomeLethalIfShotsCountExceed"), new GUIContent("Headshot After Shots"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_playersWithImmuneToFire"), new GUIContent("Flare Zone", "Players who fire a green flare in this zone are spared (a flare extract)."));
            serializedObject.ApplyModifiedProperties();
            if (zone._playersWithImmuneToFire == null && GUILayout.Button("Add Flare Zone (Flare Extract)"))
                EditorApplication.delayCall += () =>
                {
                    var collector = TarkovHazards.CreateFlareZone(zone.transform.parent);
                    collector.transform.position = zone.transform.position + zone.transform.right * (zone.GetComponent<BoxCollider>().size.x * 0.5f + 10f);
                    Undo.RegisterCreatedObjectUndo(collector.gameObject, "Add flare zone");
                    Undo.RecordObject(zone, "Add flare zone");
                    zone._playersWithImmuneToFire = collector;
                    EditorUtility.SetDirty(zone);
                };

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Immune items", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Players carrying any of these aren't shot. Tarkov has no such option; CustomMapLoader's client adds it.",
                                       EditorStyles.wordWrappedMiniLabel);
            foreach (var template in TarkovHazards.ImmuneItems(zone))
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(TarkovItemPicker.Name(template));
                    if (GUILayout.Button("Remove", GUILayout.Width(64f)))
                    {
                        var t = template;
                        EditorApplication.delayCall += () => TarkovHazards.RemoveImmuneItem(zone, t);
                    }
                }
            var picked = TarkovItemPicker.Draw(_picker, "Add");
            if (picked != null)
                EditorApplication.delayCall += () => TarkovHazards.AddImmuneItem(zone, picked);
        }
    }

    public static class TarkovHazardGizmos
    {
        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
        private static void DrawBorderZone(BorderZone zone, GizmoType type)
        {
            var box = zone.GetComponent<BoxCollider>();
            if (box == null)
                return;
            bool selected = (type & GizmoType.Selected) != 0;
            Gizmos.matrix = zone.transform.localToWorldMatrix;
            var deep = zone is Minefield ? new Color(1f, 0.5f, 0f) : new Color(1f, 0.15f, 0.1f);
            Gizmos.color = new Color(deep.r, deep.g, deep.b, selected ? 0.18f : 0.07f);
            Gizmos.DrawCube(box.center, box.size);
            Gizmos.color = deep;
            Gizmos.DrawWireCube(box.center, box.size);
            // the buffer strips: where the zone's own trigger settings keep a player outside the deep part
            var s = zone._triggerZoneSettings;
            var half = box.size * 0.5f;
            void Strip(Vector3 center, Vector3 size)
            {
                if (size.x <= 0f || size.z <= 0f)
                    return;
                Gizmos.color = new Color(1f, 0.9f, 0.2f, selected ? 0.25f : 0.1f);
                Gizmos.DrawCube(center, size);
            }
            Strip(new Vector3(-half.x + s.x * 0.5f, 0f, 0f), new Vector3(s.x, box.size.y, box.size.z));
            Strip(new Vector3(half.x - s.y * 0.5f, 0f, 0f), new Vector3(s.y, box.size.y, box.size.z));
            Strip(new Vector3(0f, 0f, -half.z + s.z * 0.5f), new Vector3(box.size.x, box.size.y, s.z));
            Strip(new Vector3(0f, 0f, half.z - s.w * 0.5f), new Vector3(box.size.x, box.size.y, s.w));
            Gizmos.matrix = Matrix4x4.identity;
            if (selected)
                Handles.Label(zone.transform.position + Vector3.up * (half.y + 1f), zone is Minefield ? "Minefield (yellow: safe strip)" : "Sniper zone (yellow: buffer strip)");
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.InSelectionHierarchy)]
        private static void DrawSniper(SniperImitator imitator, GizmoType type)
        {
            if (imitator.SniperPositions == null || imitator.SniperFiringZone == null)
                return;
            Gizmos.color = new Color(1f, 0.3f, 0.2f);
            foreach (var p in imitator.SniperPositions.Where(p => p != null))
            {
                Gizmos.DrawWireSphere(p.position, 1f);
                Gizmos.DrawLine(p.position, imitator.SniperFiringZone.transform.position);
            }
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
        private static void DrawFlareZone(FlareShootDetectorZone zone, GizmoType type)
        {
            var box = zone.GetComponent<BoxCollider>();
            if (box == null)
                return;
            Gizmos.matrix = zone.transform.localToWorldMatrix;
            Gizmos.color = new Color(0.2f, 1f, 0.3f, (type & GizmoType.Selected) != 0 ? 0.25f : 0.1f);
            Gizmos.DrawCube(box.center, box.size);
            Gizmos.color = new Color(0.2f, 1f, 0.3f);
            Gizmos.DrawWireCube(box.center, box.size);
            Gizmos.matrix = Matrix4x4.identity;
            var collector = zone.GetComponent<PlayersWithImmuneToSniperFireCollector>();
            if (collector != null)
                foreach (var sniper in Object.FindObjectsOfType<SniperFiringZone>().Where(s => s._playersWithImmuneToFire == collector))
                    Gizmos.DrawLine(zone.transform.position, sniper.transform.position);
            if ((type & GizmoType.Selected) != 0)
                Handles.Label(zone.transform.position + Vector3.up * 2f, "Flare zone: fire a green flare here");
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
        private static void DrawClaymore(MineDirectional mine, GizmoType type)
        {
            // the fragment cone: 60 degrees, 13 m
            var t = mine.transform;
            float range = Mathf.Max(1f, mine._mineData._maxExplosionDistance), angle = mine._mineData._directionalDamageAngle * 0.5f;
            Handles.color = new Color(1f, 0.3f, 0.1f, (type & GizmoType.Selected) != 0 ? 0.15f : 0.05f);
            Handles.DrawSolidArc(t.position, t.up, Quaternion.AngleAxis(-angle, t.up) * t.forward, angle * 2f, range);
            Gizmos.color = new Color(1f, 0.3f, 0.1f);
            foreach (var wire in mine.GetComponentsInChildren<MineDirectionalColliders>(true))
            {
                Gizmos.matrix = wire.transform.localToWorldMatrix;
                var box = wire.GetComponent<BoxCollider>();
                if (box != null)
                    Gizmos.DrawWireCube(box.center, box.size);
            }
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
