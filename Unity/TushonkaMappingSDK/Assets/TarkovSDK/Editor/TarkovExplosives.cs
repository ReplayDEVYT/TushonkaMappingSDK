using System;
using System.Linq;
using EFT;
using EFT.Ballistics;
using EFT.GameTriggers;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Shootable triggers and scripted explosions, from the game's own components as retail Interchange's red gas
    // cylinders have them (level8, 49 of them): on the cylinder a TriggerBallistic (emits its trigger id when its
    // BallisticCollider is hit, or when an explosion's ray reaches it: ExplosionSharedMethods.OnExplosion, so blasts
    // chain), a HandlerExplosion on the same id (MineDirectional.MineSettings: the blast, fragments and the effect,
    // EDamageType.HotGases) and a HandlerGOState hiding the cylinder (GameObjectStateSync, so late joiners see it gone).
    //
    //  Explosive Barrel    the whole cylinder, generated
    //  Shootable Trigger   a TriggerBallistic on any object (its BallisticCollider, or a new hit box): shooting it fires
    //                      a trigger id, for whatever Triggers & Sounds connects to it
    //  Explosion           a HandlerExplosion at a point: a trigger id sets it off once (a demolition, a booby trap)
    // TriggerBallistic only emits on the shooter's machine (IsClientAuthority), as in retail.
    public static class TarkovExplosives
    {
        private const string HazardsMenu = "GameObject/Tarkov SDK/Hazards/";
        private const string TriggersMenu = "GameObject/Tarkov SDK/Triggers/";

        // effect names Effects.EmitGrenade knows (its EffectsArray in effects.bundle, always loaded)
        public static readonly string[] EffectNames =
            { "Gas_explosion", "Grenade_new", "smallgrenade_expl", "Artillery_mine", "landmine", "big_explosion", "big_smoky_explosion", "spg_explosion", "" };
        public static readonly string[] EffectLabels =
            { "Gas cylinder (Interchange)", "Grenade", "Small grenade", "Mortar shell", "Landmine", "Big explosion", "Big smoky explosion", "Rocket (SPG)", "None" };

        [MenuItem(HazardsMenu + "Explosive Barrel", priority = 50)]
        private static void MenuBarrel(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateExplosiveCylinder(p));

        [MenuItem(TriggersMenu + "Shootable Trigger", priority = 82)]
        private static void MenuShootable(MenuCommand cmd)
        {
            var target = cmd.context as GameObject ?? Selection.activeGameObject;
            if (target != null && target.scene.IsValid())
            {
                var trigger = AddShootable(target, true);
                Selection.activeGameObject = trigger.gameObject;
                return;
            }
            TarkovSceneTools.Place(cmd, p =>
            {
                var go = new GameObject("ShootableTarget");
                if (p != null)
                    go.transform.SetParent(p, false);
                AddShootable(go, true);
                return go;
            });
        }

        [MenuItem(TriggersMenu + "Explosion", priority = 83)]
        private static void MenuExplosion(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateExplosion(p, NewId("Explosion")).gameObject);

        public static string NewId(string what) => $"{what}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";

        // ------------------------------------------------------------ explosive cylinder

        public static GameObject CreateExplosiveCylinder(Transform parent)
        {
            const float radius = 0.16f, height = 1.35f;
            var root = new GameObject("ExplosiveCylinder");
            if (parent != null)
                root.transform.SetParent(parent, false);

            // the part the explosion hides; the handlers stay on the root
            var model = new GameObject("Cylinder");
            model.transform.SetParent(root.transform, false);
            var visual = new GameObject("Model");
            visual.transform.SetParent(model.transform, false);
            visual.AddComponent<MeshFilter>().sharedMesh = TarkovGeneratedAssets.Mesh(TarkovGeneratedAssets.PropsFolder + "/SDK_GasCylinder.asset", GasCylinderMesh);
            visual.AddComponent<MeshRenderer>().sharedMaterial = TarkovGeneratedAssets.SurfaceMaterial("SDK_RedPaint", new Color(0.55f, 0.07f, 0.05f, 0.25f), 0.05f);
            model.AddComponent<GameObjectStateSync>();

            var low = new GameObject("LowPoly") { layer = LayerMask.NameToLayer("LowPolyCollider") };
            low.transform.SetParent(model.transform, false);
            var lowCapsule = low.AddComponent<CapsuleCollider>();
            lowCapsule.radius = radius;
            lowCapsule.height = height;
            lowCapsule.center = new Vector3(0f, height * 0.5f, 0f);
            var high = new GameObject("HighPoly") { layer = LayerMask.NameToLayer("HighPolyCollider") };
            high.transform.SetParent(model.transform, false);
            var highCapsule = high.AddComponent<CapsuleCollider>();
            highCapsule.radius = radius;
            highCapsule.height = height;
            highCapsule.center = lowCapsule.center;
            // retail: metalthin preset, penetration level 4
            var ballistic = high.AddComponent<BallisticCollider>();
            ballistic._typeOfMaterial = MaterialType.MetalThin;
            ballistic.SurfaceSound = BaseBallistic.ESurfaceSound.MetalThin;

            string id = NewId("GasCylinder");
            var shot = root.AddComponent<TriggerBallistic>();
            shot._triggerId = id;
            shot._targetCollider = ballistic;
            shot._once = true;
            var explosion = root.AddComponent<HandlerExplosion>();
            explosion._triggerId = id;
            explosion._mineSettings = GasCylinder();
            explosion._damageType = EDamageType.HotGases;
            var hide = root.AddComponent<HandlerGOState>();
            hide._triggerIdEnable = "";
            hide._triggerIdDisable = id;
            hide._target = model;
            return root;
        }

        // retail Interchange's gas cylinder (level8 HandlerExplosion)
        public static MineDirectional.MineSettings GasCylinder() => new MineDirectional.MineSettings
        {
            _blindness = new Vector3(10f, 10f, 10f),
            _contusion = new Vector3(10f, 10f, 10f),
            _armorDistanceDistanceDamage = new Vector3(10f, 10f, 10f),
            _minExplosionDistance = 0f,
            _maxExplosionDistance = 10f,
            _fragmentsCount = 3,
            _strength = 6f,
            _tag = "default",
            _armorDamage = 0.7f,
            _staminaBurnRate = 5f,
            _penetrationPower = 32f,
            // the fragment item: claymore balls, as retail's
            _fragmentType = "5996f6d686f77467977ba6cc",
            _fxName = "Gas_explosion",
            _directionalDamageAngle = 60f,
            _directionalDamageMultiplier = 7f,
        };

        private static Mesh GasCylinderMesh()
        {
            // a body with a domed shoulder and a valve stub
            const float radius = 0.16f, body = 1.15f;
            const int sides = 16;
            var b = new TarkovGeneratedAssets.MeshBuilder();
            // the profile from the foot's centre up: (height, radius)
            var rings = new System.Collections.Generic.List<(float y, float r)> { (0f, 0f), (0f, radius), (body, radius) };
            for (int i = 1; i <= 4; i++)
            {
                float a = i / 4f * Mathf.PI * 0.5f;
                rings.Add((body + Mathf.Sin(a) * 0.13f, Mathf.Max(radius * Mathf.Cos(a), 0.03f)));
            }
            rings.Add((body + 0.2f, 0.03f));
            rings.Add((body + 0.2f, 0f));
            void Facet(Vector3 a, Vector3 c, Vector3 d)
            {
                // skips the zero-area halves of the caps' quads
                if (Vector3.Cross(c - a, d - a).sqrMagnitude > 1e-12f)
                    b.Facet(a, c, d);
            }
            for (int r = 0; r < rings.Count - 1; r++)
            {
                var (y0, r0) = rings[r];
                var (y1, r1) = rings[r + 1];
                for (int i = 0; i < sides; i++)
                {
                    float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
                    var p00 = new Vector3(Mathf.Cos(a0) * r0, y0, Mathf.Sin(a0) * r0);
                    var p01 = new Vector3(Mathf.Cos(a1) * r0, y0, Mathf.Sin(a1) * r0);
                    var p10 = new Vector3(Mathf.Cos(a0) * r1, y1, Mathf.Sin(a0) * r1);
                    var p11 = new Vector3(Mathf.Cos(a1) * r1, y1, Mathf.Sin(a1) * r1);
                    // front faces outward (clockwise seen from outside)
                    Facet(p00, p10, p11);
                    Facet(p00, p11, p01);
                }
            }
            return b.Build("SDK_GasCylinder");
        }

        // ------------------------------------------------------------ shootable trigger

        // a TriggerBallistic on the object for its (first) BallisticCollider, or a new hit box around its renderers
        public static TriggerBallistic AddShootable(GameObject target, bool once)
        {
            var existing = target.GetComponent<TriggerBallistic>();
            if (existing != null)
                return existing;
            var ballistic = target.GetComponentInChildren<BallisticCollider>(true);
            if (ballistic == null)
            {
                var hit = new GameObject("ShootBox") { layer = LayerMask.NameToLayer("HighPolyCollider") };
                Undo.RegisterCreatedObjectUndo(hit, "Add shoot box");
                hit.transform.SetParent(target.transform, false);
                var box = hit.AddComponent<BoxCollider>();
                var renderers = target.GetComponentsInChildren<Renderer>();
                if (renderers.Length > 0)
                {
                    var bounds = renderers[0].bounds;
                    foreach (var r in renderers)
                        bounds.Encapsulate(r.bounds);
                    box.center = hit.transform.InverseTransformPoint(bounds.center);
                    box.size = Vector3.Scale(bounds.size, new Vector3(1f / Mathf.Max(1e-4f, hit.transform.lossyScale.x), 1f / Mathf.Max(1e-4f, hit.transform.lossyScale.y), 1f / Mathf.Max(1e-4f, hit.transform.lossyScale.z)));
                }
                else
                {
                    box.center = new Vector3(0f, 0.5f, 0f);
                    box.size = new Vector3(0.6f, 1f, 0.6f);
                }
                ballistic = hit.AddComponent<BallisticCollider>();
                ballistic._typeOfMaterial = MaterialType.MetalThin;
                ballistic.SurfaceSound = BaseBallistic.ESurfaceSound.MetalThin;
            }
            var trigger = Undo.AddComponent<TriggerBallistic>(target);
            trigger._triggerId = NewId(target.name.Replace(' ', '_') + "_Shot");
            trigger._targetCollider = ballistic;
            trigger._once = once;
            return trigger;
        }

        // ------------------------------------------------------------ explosion

        public static HandlerExplosion CreateExplosion(Transform parent, string triggerId)
        {
            var go = new GameObject("Explosion");
            if (parent != null)
                go.transform.SetParent(parent, false);
            var explosion = go.AddComponent<HandlerExplosion>();
            explosion._triggerId = triggerId;
            explosion._mineSettings = GasCylinder();
            explosion._damageType = EDamageType.Explosion;
            return explosion;
        }

        // ------------------------------------------------------------ Build Map

        public static void Prepare(UnityEngine.SceneManagement.Scene scene, GameObject[] roots, TarkovScenePreparer.Report report)
        {
            foreach (var shot in roots.SelectMany(r => r.GetComponentsInChildren<TriggerBallistic>(true)))
            {
                if (string.IsNullOrEmpty(shot._triggerId))
                    report.Warnings.Add($"{scene.name}: shootable trigger '{shot.name}' has no trigger id, so shooting it does nothing");
                if (shot._targetCollider == null)
                    report.Errors.Add($"{scene.name}: shootable trigger '{shot.name}' has no Target Collider (a BallisticCollider), so it can't be hit");
                else if (shot._targetCollider.gameObject.layer != LayerMask.NameToLayer("HighPolyCollider") && shot._targetCollider.gameObject.layer != LayerMask.NameToLayer("Terrain"))
                    report.Warnings.Add($"{scene.name}: shootable trigger '{shot.name}': its target '{shot._targetCollider.name}' isn't on HighPolyCollider, so bullets and blasts don't reach it");
            }
            foreach (var explosion in roots.SelectMany(r => r.GetComponentsInChildren<HandlerExplosion>(true)))
            {
                if (string.IsNullOrEmpty(explosion._triggerId))
                    report.Warnings.Add($"{scene.name}: explosion '{explosion.name}' has no trigger id, so it never goes off");
                if (explosion._mineSettings._maxExplosionDistance <= 0f)
                    report.Warnings.Add($"{scene.name}: explosion '{explosion.name}' has a Max Explosion Distance of 0, so it hurts nobody");
                // MineSettings.Explosion makes its fragments from this item
                if (explosion._mineSettings._fragmentsCount > 0 && !System.Text.RegularExpressions.Regex.IsMatch(explosion._mineSettings._fragmentType ?? "", "^[0-9a-fA-F]{24}$"))
                    report.Errors.Add($"{scene.name}: explosion '{explosion.name}' has fragments but no Fragment Type item id");
            }
            foreach (var hide in roots.SelectMany(r => r.GetComponentsInChildren<HandlerGOState>(true)))
                if (hide._target != null && hide._target.GetComponent<GameObjectStateSync>() == null)
                {
                    hide._target.AddComponent<GameObjectStateSync>();
                    report.Warnings.Add($"{scene.name}: '{hide.name}' switches '{hide._target.name}', which had no GameObjectStateSync (the game needs one); one was added");
                }
        }

        // ------------------------------------------------------------ gizmos

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        private static void DrawExplosion(HandlerExplosion explosion, GizmoType type)
        {
            bool selected = (type & GizmoType.Selected) != 0;
            Gizmos.color = new Color(1f, 0.3f, 0.1f, selected ? 0.8f : 0.3f);
            Gizmos.DrawWireSphere(explosion.transform.position, selected ? explosion._mineSettings._maxExplosionDistance : 0.3f);
        }

        [DrawGizmo(GizmoType.Selected)]
        private static void DrawShootable(TriggerBallistic trigger, GizmoType type)
        {
            if (trigger._targetCollider == null)
                return;
            var collider = trigger._targetCollider.GetComponent<Collider>();
            if (collider == null)
                return;
            Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.8f);
            Gizmos.DrawWireCube(collider.bounds.center, collider.bounds.size);
        }
    }

    // the explosion's main settings up front, the rest below
    [CustomEditor(typeof(HandlerExplosion))]
    internal class TarkovExplosionEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var explosion = (HandlerExplosion)target;
            EditorGUILayout.HelpBox("Goes off once when its trigger id fires (a shootable trigger, a zone, a switch, a world event). " +
                                    "The blast hurts and knocks out players and bots in range and sets off shootable triggers its rays reach.", MessageType.None);
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_triggerId"), new GUIContent("Trigger Id"));
            var settings = serializedObject.FindProperty("_mineSettings");
            var fx = settings.FindPropertyRelative("_fxName");
            int index = Array.IndexOf(TarkovExplosives.EffectNames, fx.stringValue);
            int picked = EditorGUILayout.Popup(new GUIContent("Effect", "The game's explosion effect (Effects.EmitGrenade)."), index < 0 ? TarkovExplosives.EffectNames.Length : index,
                TarkovExplosives.EffectLabels.Concat(new[] { "Other: " + fx.stringValue }).ToArray());
            if (picked < TarkovExplosives.EffectNames.Length)
                fx.stringValue = TarkovExplosives.EffectNames[picked];
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("_maxExplosionDistance"), new GUIContent("Radius (m)"));
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("_strength"), new GUIContent("Strength"));
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("_fragmentsCount"), new GUIContent("Fragments"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_damageType"), new GUIContent("Damage Type", "What the death screen says killed the player."));
            if (GUILayout.Button("Reset To Gas Cylinder (Interchange)"))
            {
                Undo.RecordObject(explosion, "Reset explosion");
                serializedObject.ApplyModifiedProperties();
                explosion._mineSettings = TarkovExplosives.GasCylinder();
                EditorUtility.SetDirty(explosion);
                serializedObject.Update();
            }
            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(settings, new GUIContent("All Settings"), true);
            serializedObject.ApplyModifiedProperties();

            if (!PreviewGui.Begin("explosion"))
                return;
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(explosion._triggerId)))
                if (PreviewGui.Button($"Fire '{explosion._triggerId}'", "Its trigger fires: a stand-in for the game's effect, the blast's reach, and every shootable trigger the blast reaches goes off too (chains)."))
                    TarkovPreview.Emit(explosion._triggerId);
            PreviewGui.End(explosion);
        }
    }
}
