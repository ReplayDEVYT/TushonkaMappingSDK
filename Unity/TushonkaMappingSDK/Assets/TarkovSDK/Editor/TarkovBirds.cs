using System;
using System.Collections.Generic;
using System.Linq;
using BezierSplineTools;
using EFT.Animals;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // Birds, on the game's own components (EFT.Animals; the retail scenes checked place none, their crows are ambience
    // sounds, and the game has no bird model outside its scenes, so the SDK makes its own crow and gull):
    //  - BirdsSpawner: Awake spawns _count copies of _birdsPrefabs under itself, one per path in turn, then waits
    //    _spawnDelay seconds and goes round again. Each gets a BirdCurveBrain that flies its BezierSpline by length at
    //    |direction * 6 + gravity * 0.1| m/s and wraps from the end back to the start, so paths are closed loops. The game
    //    registers BirdCurveBrainSystem at startup (CreateTechnicalSystems, "[Technical Systems]"), so nothing more is needed.
    //  - BirdBoidsSpawner: a flock round the spawner (BirdBoidBrain: cohesion/separation/alignment once a second among
    //    birds within CohesionRadius on the Water layer; a bird with fewer than MinBoidsForCohesion near it, itself
    //    included, stops), pulled back inside sqrt(SwarmSqrRadius).
    //  - Bird: every 0.1 s faces Carina along the flight, banks it, sets the Animator trigger "flight" (level or
    //    climbing, y > _switchToSoarPoint) or "soar" (diving), and the float speedMult = (1 + climb or dive) * speed *
    //    _speedToFlap. Triggers stay set until a transition uses them, so the SDK's controller has a second layer
    //    (weight 0) whose self-transitions use up the one the bird keeps setting in its own state; without it a stale
    //    "flight" pulls a gliding bird straight back to flapping.
    public static class TarkovBirds
    {
        public const string Folder = "Assets/TarkovSDK/Birds";
        public enum Kind { Crow, Gull }

        // BirdCurveBrain's speed on a level path (|forward * 6 + (0, -9.8, 0) * 0.1|)
        public static readonly float PathSpeed = new Vector3(0f, -0.98f, 6f).magnitude;

        // ------------------------------------------------------------ the bird prefabs

        public static GameObject Prefab(Kind kind)
        {
            string path = $"{Folder}/SDK_{kind}.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null)
                return existing;
            TarkovGeneratedAssets.EnsureFolder(Folder);
            var body = TarkovGeneratedAssets.Mesh($"{Folder}/SDK_Bird_Body.asset", BodyMesh);
            var wingL = TarkovGeneratedAssets.Mesh($"{Folder}/SDK_Bird_WingL.asset", () => WingMesh(-1f));
            var wingR = TarkovGeneratedAssets.Mesh($"{Folder}/SDK_Bird_WingR.asset", () => WingMesh(1f));
            var material = kind == Kind.Crow
                ? TarkovGeneratedAssets.SurfaceMaterial("SDK_Crow", new Color(0.045f, 0.045f, 0.055f, 0.06f), 0.05f)
                : TarkovGeneratedAssets.SurfaceMaterial("SDK_Gull", new Color(0.8f, 0.81f, 0.83f, 0.04f), 0.04f);

            var root = new GameObject($"SDK_{kind}");
            var animator = root.AddComponent<Animator>();
            animator.runtimeAnimatorController = Controller();
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            var carina = new GameObject("Carina").transform;
            carina.SetParent(root.transform, false);
            // a crow is 0.45 m long with a 1 m span; a herring gull about a third bigger
            carina.localScale = Vector3.one * (kind == Kind.Gull ? 1.35f : 1f);
            Part(carina, "Body", body, material, Vector3.zero);
            Part(carina, "WingL", wingL, material, new Vector3(-0.045f, 0.03f, 0.03f));
            Part(carina, "WingR", wingR, material, new Vector3(0.045f, 0.03f, 0.03f));
            var bird = root.AddComponent<Bird>();
            bird._animator = animator;
            bird.Carina = carina;
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab;
        }

        private static void Part(Transform parent, string name, Mesh mesh, Material material, Vector3 position)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        // a lofted body from tail to beak, flat shaded, with a fanned tail; +Z forward
        private static Mesh BodyMesh()
        {
            var b = new TarkovGeneratedAssets.MeshBuilder();
            // rings: (z, half width, half height, centre height)
            var rings = new[]
            {
                new Vector4(-0.17f, 0.03f, 0.025f, 0f), new Vector4(-0.09f, 0.055f, 0.05f, 0f), new Vector4(0.01f, 0.062f, 0.058f, 0.004f),
                new Vector4(0.09f, 0.045f, 0.045f, 0.018f), new Vector4(0.125f, 0.038f, 0.04f, 0.032f), new Vector4(0.17f, 0.034f, 0.033f, 0.034f),
                new Vector4(0.195f, 0.014f, 0.012f, 0.03f),
            };
            const int sides = 8;
            Vector3 P(Vector4 r, int i)
            {
                float a = i * Mathf.PI * 2f / sides;
                return new Vector3(Mathf.Cos(a) * r.y, r.w + Mathf.Sin(a) * r.z, r.x);
            }
            for (int k = 0; k + 1 < rings.Length; k++)
                for (int i = 0; i < sides; i++)
                {
                    Vector3 a = P(rings[k], i), c = P(rings[k + 1], i + 1);
                    b.Facet(a, P(rings[k], i + 1), c);
                    b.Facet(a, c, P(rings[k + 1], i));
                }
            Vector3 back = new Vector3(0f, 0f, -0.19f), beak = new Vector3(0f, 0.026f, 0.25f);
            var last = rings[rings.Length - 1];
            for (int i = 0; i < sides; i++)
            {
                b.Facet(back, P(rings[0], i + 1), P(rings[0], i));
                b.Facet(beak, P(last, i), P(last, i + 1));
            }
            // the tail, both faces
            Vector3 t0 = new Vector3(-0.025f, 0.005f, -0.15f), t1 = new Vector3(0.025f, 0.005f, -0.15f);
            Vector3 t2 = new Vector3(0.075f, 0f, -0.31f), t3 = new Vector3(-0.075f, 0f, -0.31f);
            b.Facet(t0, t1, t2);
            b.Facet(t0, t2, t3);
            b.Facet(t0, t2, t1);
            b.Facet(t0, t3, t2);
            return b.Build("SDK_Bird_Body");
        }

        // a wing from its shoulder (the origin) out along +X (side 1) or -X (-1), both faces
        private static Mesh WingMesh(float side)
        {
            var b = new TarkovGeneratedAssets.MeshBuilder();
            Vector3 S(float x, float y, float z) => new Vector3(x * side, y, z);
            var lead = new[] { S(0f, 0f, 0.035f), S(0.2f, 0.005f, 0.045f), S(0.47f, -0.005f, -0.03f) };
            var trail = new[] { S(0f, 0f, -0.07f), S(0.18f, 0f, -0.12f), S(0.4f, 0f, -0.11f) };
            void Both(Vector3 a, Vector3 c, Vector3 d)
            {
                b.Facet(a, c, d);
                b.Facet(a, d, c);
            }
            Both(lead[0], lead[1], trail[1]);
            Both(lead[0], trail[1], trail[0]);
            Both(lead[1], lead[2], trail[2]);
            Both(lead[1], trail[2], trail[1]);
            return b.Build(side > 0f ? "SDK_Bird_WingR" : "SDK_Bird_WingL");
        }

        // Flight (a wing beat, down faster than up) and Soar (held out, a slow wobble), both at the state speed speedMult
        private static AnimatorController Controller()
        {
            string path = $"{Folder}/SDK_Bird.controller";
            var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (existing != null)
                return existing;
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            controller.AddParameter("flight", AnimatorControllerParameterType.Trigger);
            controller.AddParameter("soar", AnimatorControllerParameterType.Trigger);
            controller.AddParameter(new AnimatorControllerParameter { name = "speedMult", type = AnimatorControllerParameterType.Float, defaultFloat = 1f });

            var flap = WingClip("Flight", 0.3f, u =>
            {
                // the downstroke takes 45% of the beat
                float w = u < 0.45f ? u / 0.45f * 0.5f : 0.5f + (u - 0.45f) / 0.55f * 0.5f;
                return 7.5f + 42.5f * Mathf.Cos(w * Mathf.PI * 2f);
            });
            var glide = WingClip("Soar", 2f, u => 6f + 3f * Mathf.Sin(u * Mathf.PI * 2f));
            AssetDatabase.AddObjectToAsset(flap, controller);
            AssetDatabase.AddObjectToAsset(glide, controller);

            var machine = controller.layers[0].stateMachine;
            var flight = machine.AddState("Flight");
            var soar = machine.AddState("Soar");
            foreach (var (state, clip) in new[] { (flight, flap), (soar, glide) })
            {
                state.motion = clip;
                state.speedParameterActive = true;
                state.speedParameter = "speedMult";
            }
            machine.defaultState = flight;
            foreach (var (from, to, trigger) in new[] { (flight, soar, "soar"), (soar, flight, "flight") })
            {
                var t = from.AddTransition(to);
                t.hasExitTime = false;
                t.duration = 0.25f;
                t.AddCondition(AnimatorConditionMode.If, 0f, trigger);
            }

            // uses up the trigger set for the state the bird is already in (see the header)
            controller.AddLayer("Triggers");
            var layers = controller.layers;
            var sink = layers[1].stateMachine;
            var idle = sink.AddState("Idle");
            sink.defaultState = idle;
            foreach (var trigger in new[] { "flight", "soar" })
            {
                var t = idle.AddTransition(idle);
                t.hasExitTime = false;
                t.duration = 0f;
                t.AddCondition(AnimatorConditionMode.If, 0f, trigger);
            }
            layers[1].defaultWeight = 0f;
            controller.layers = layers;
            AssetDatabase.SaveAssets();
            return controller;
        }

        // both wings rolled up by angle(phase) degrees, sampled as quaternions
        private static AnimationClip WingClip(string name, float length, Func<float, float> angle)
        {
            var clip = new AnimationClip { name = name };
            foreach (var (path, side) in new[] { ("Carina/WingR", 1f), ("Carina/WingL", -1f) })
            {
                var x = new AnimationCurve();
                var y = new AnimationCurve();
                var z = new AnimationCurve();
                var w = new AnimationCurve();
                const int steps = 16;
                for (int i = 0; i <= steps; i++)
                {
                    float u = i / (float)steps;
                    var q = Quaternion.Euler(0f, 0f, side * angle(u));
                    x.AddKey(u * length, q.x);
                    y.AddKey(u * length, q.y);
                    z.AddKey(u * length, q.z);
                    w.AddKey(u * length, q.w);
                }
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.x", x);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.y", y);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.z", z);
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.w", w);
            }
            clip.EnsureQuaternionContinuity();
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            return clip;
        }

        // ------------------------------------------------------------ spawners

        [MenuItem("GameObject/Tarkov SDK/Environment/Birds (Flight Paths)", priority = 73)]
        private static void MenuPaths(MenuCommand cmd) => EditorApplication.delayCall += () => TarkovSceneTools.Place(cmd, p => CreatePaths(p).gameObject);

        [MenuItem("GameObject/Tarkov SDK/Environment/Bird Flock", priority = 74)]
        private static void MenuFlock(MenuCommand cmd) => EditorApplication.delayCall += () => TarkovSceneTools.Place(cmd, p => CreateFlock(p).transform.parent.gameObject);

        // birds round two loops over the spawner, 25 and 32 m up, flown opposite ways
        public static BirdsSpawner CreatePaths(Transform parent, Kind kind = Kind.Crow)
        {
            var go = new GameObject("Birds");
            if (parent != null)
                go.transform.SetParent(parent, false);
            var spawner = go.AddComponent<BirdsSpawner>();
            spawner._birdsPrefabs = new[] { Prefab(kind) };
            spawner._count = 6;
            spawner._spawnDelay = 5f;
            spawner._paths = new BezierSpline[0];
            AddPath(spawner, Circle(go.transform.position, 35f, 25f, 7, 4f, 1, followGround: false));
            AddPath(spawner, Circle(go.transform.position, 50f, 32f, 8, 6f, -1, followGround: false));
            return spawner;
        }

        // a flock circling a point 35 m above the object
        public static BirdBoidsSpawner CreateFlock(Transform parent, Kind kind = Kind.Crow)
        {
            var root = new GameObject("Bird Flock");
            if (parent != null)
                root.transform.SetParent(parent, false);
            var centre = new GameObject("Flock Centre");
            centre.transform.SetParent(root.transform, false);
            centre.transform.localPosition = Vector3.up * 35f;
            var flock = centre.AddComponent<BirdBoidsSpawner>();
            flock._birdsPrefabs = new[] { Prefab(kind) };
            flock._count = 9;
            flock._spawnDelay = 0.3f;
            flock._spawnRadius = 8f;
            return flock;
        }

        // a new "Path n" under the spawner, a loop through the world points, added to its paths
        public static BezierSpline AddPath(BirdsSpawner spawner, IList<Vector3> world)
        {
            int n = (spawner._paths ?? new BezierSpline[0]).Length + 1;
            var go = new GameObject($"Path {n}");
            go.transform.SetParent(spawner.transform, false);
            var spline = go.AddComponent<BezierSpline>();
            SetLoop(spline, world);
            Undo.RecordObject(spawner, "Add bird path");
            spawner._paths = (spawner._paths ?? new BezierSpline[0]).Where(p => p != null).Append(spline).ToArray();
            EditorUtility.SetDirty(spawner);
            return spline;
        }

        // a closed Catmull-Rom curve through the points: the last anchor is the first, with matching tangents, so the
        // bird's wrap from the end to the start is seamless
        public static void SetLoop(BezierSpline spline, IList<Vector3> world)
        {
            var p = world.Select(w => spline.transform.InverseTransformPoint(w)).ToList();
            int n = p.Count;
            var points = new List<Vector3>();
            for (int i = 0; i < n; i++)
            {
                Vector3 prev = p[(i - 1 + n) % n], a = p[i], b = p[(i + 1) % n], next = p[(i + 2) % n];
                points.Add(a);
                points.Add(a + (b - prev) / 6f);
                points.Add(b - (next - a) / 6f);
            }
            points.Add(p[0]);
            spline.points = points.ToArray();
            spline.modes = Enumerable.Repeat(BezierControlPointMode.Free, n + 1).ToArray();
            spline.loop = true;
            EditorUtility.SetDirty(spline);
        }

        // the loop's waypoints (its anchors without the closing one)
        public static List<Vector3> Waypoints(BezierSpline spline)
        {
            var anchors = TarkovSplines.Anchors(spline);
            if (anchors.Count > 1 && Vector3.Distance(anchors[0], anchors[anchors.Count - 1]) < 0.01f)
                anchors.RemoveAt(anchors.Count - 1);
            return anchors;
        }

        // points round a centre, `height` above the ground under each (or the centre), rising and falling by `wave`
        public static List<Vector3> Circle(Vector3 centre, float radius, float height, int count, float wave, int direction, bool followGround = true)
        {
            var result = new List<Vector3>();
            var random = new System.Random(Mathf.RoundToInt(radius * 31f + height * 7f));
            for (int i = 0; i < count; i++)
            {
                float a = direction * i * Mathf.PI * 2f / count;
                float r = radius * (0.85f + 0.3f * (float)random.NextDouble());
                var p = centre + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                float ground = followGround && Ground(p, out var y) ? y : centre.y;
                p.y = ground + height + wave * Mathf.Sin(a * 2f);
                result.Add(p);
            }
            return result;
        }

        public static bool Ground(Vector3 p, out float y)
        {
            if (Physics.Raycast(new Vector3(p.x, p.y + 2000f, p.z), Vector3.down, out var hit, 4000f, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore))
            {
                y = hit.point.y;
                return true;
            }
            y = p.y;
            return false;
        }

        // places along the path where it goes through something solid (every 2 m)
        public static List<Vector3> Blocked(BezierSpline spline)
        {
            var result = new List<Vector3>();
            var samples = TarkovSplines.Sample(spline, 24);
            for (int i = 0; i + 1 < samples.Count; i++)
            {
                var a = samples[i];
                var b = samples[i + 1];
                float d = Vector3.Distance(a, b);
                if (d > 0.001f && (Physics.Linecast(a, b, out var hit, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore)
                                   || Physics.CheckSphere(a, 1f, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore)))
                {
                    var at = hit.collider != null ? hit.point : a;
                    if (result.Count == 0 || Vector3.Distance(result[result.Count - 1], at) > 4f)
                        result.Add(at);
                }
            }
            return result;
        }

        public static float Length(BezierSpline spline)
        {
            var s = TarkovSplines.Sample(spline, 24);
            float length = 0f;
            for (int i = 0; i + 1 < s.Count; i++)
                length += Vector3.Distance(s[i], s[i + 1]);
            return length;
        }

        // ------------------------------------------------------------ Build Map

        public static void Prepare(UnityEngine.SceneManagement.Scene scene, GameObject[] roots, TarkovScenePreparer.Report report)
        {
            foreach (var spawner in roots.SelectMany(r => r.GetComponentsInChildren<BirdsSpawner>(true)))
            {
                string where = $"{scene.name}/{spawner.name}";
                var paths = (spawner._paths ?? new BezierSpline[0]).ToList();
                // Spawn logs "bad initialization data" and does nothing without both
                if (paths.Count == 0)
                    report.Errors.Add($"{where}: birds have no flight paths (Tarkov SDK > Tools > Geometry > Bird Path Tool...)");
                CheckPrefabs(spawner._birdsPrefabs, where, report);
                if (paths.Any(p => p == null || p.points == null || p.points.Length < 4))
                    report.Errors.Add($"{where}: a bird flight path is missing or has no curve; the game throws when a bird flies it");
                foreach (var path in paths.Where(p => p != null && p.points != null && p.points.Length >= 4))
                {
                    var anchors = TarkovSplines.Anchors(path);
                    if (Vector3.Distance(anchors[0], anchors[anchors.Count - 1]) > 0.5f)
                        report.Warnings.Add($"{where}/{path.name}: bird flight path isn't a loop, so its birds jump from its end back to its start (Bird Path Tool, Close Loop)");
                    var blocked = Blocked(path);
                    if (blocked.Count > 0)
                        report.Warnings.Add($"{where}/{path.name}: bird flight path goes through something solid in {blocked.Count} place(s); birds fly straight through it (Bird Path Tool shows where)");
                }
                if ((spawner.transform.lossyScale - Vector3.one).sqrMagnitude > 0.0001f)
                    report.Warnings.Add($"{where}: birds are spawned under their spawner and take its scale; keep it at 1");
                if (spawner._count <= 0)
                    report.Warnings.Add($"{where}: bird Count is 0, so none fly");
            }
            foreach (var flock in roots.SelectMany(r => r.GetComponentsInChildren<BirdBoidsSpawner>(true)))
            {
                string where = $"{scene.name}/{flock.name}";
                CheckPrefabs(flock._birdsPrefabs, where, report);
                // a bird moves only with MinBoidsForCohesion birds (itself included) within CohesionRadius
                if (flock._count < Mathf.Max(3, flock.MinBoidsForCohesion + 1))
                    report.Warnings.Add($"{where}: a flock of {flock._count} bird(s) hardly moves; a bird stops with fewer than {flock.MinBoidsForCohesion} near it. Use 5 or more");
                if (flock._spawnRadius > flock.CohesionRadius)
                    report.Warnings.Add($"{where}: flock Spawn Radius is wider than its Cohesion Radius, so birds spawned apart stay still");
                if (Physics.CheckSphere(flock.transform.position, Mathf.Sqrt(Mathf.Max(flock.SwarmSqrRadius, 1f)) * 0.5f, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore))
                    report.Warnings.Add($"{where}: the flock's centre is close to something solid; birds fly through it (raise the Flock Centre)");
            }
        }

        private static void CheckPrefabs(GameObject[] prefabs, string where, TarkovScenePreparer.Report report)
        {
            if (prefabs == null || prefabs.Length == 0)
            {
                report.Errors.Add($"{where}: birds have no bird prefab (Birds Prefabs: SDK_Crow or SDK_Gull in {Folder})");
                return;
            }
            foreach (var prefab in prefabs)
            {
                var bird = prefab != null ? prefab.GetComponent<Bird>() : null;
                if (bird == null)
                    report.Errors.Add($"{where}: a bird prefab is empty or has no Bird component; the game throws when it spawns one");
                else if (bird._animator == null || bird.Carina == null)
                    report.Errors.Add($"{where}: bird prefab '{prefab.name}' needs its Animator and Carina set; the game turns and animates them unchecked");
            }
        }

        // ------------------------------------------------------------ gizmos

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.InSelectionHierarchy)]
        private static void DrawPaths(BirdsSpawner spawner, GizmoType type)
        {
            bool selected = (type & (GizmoType.Selected | GizmoType.InSelectionHierarchy)) != 0;
            foreach (var path in spawner._paths ?? new BezierSpline[0])
            {
                if (path == null || path.points == null || path.points.Length < 4)
                    continue;
                var s = TarkovSplines.Sample(path, 16);
                Handles.color = new Color(0.55f, 0.8f, 1f, selected ? 0.9f : 0.35f);
                Handles.DrawAAPolyLine(selected ? 3f : 1.5f, s.ToArray());
                if (!selected)
                    continue;
                // which way they fly
                for (int i = 8; i + 1 < s.Count; i += 16)
                    Handles.ConeHandleCap(0, s[i], Quaternion.LookRotation(s[i + 1] - s[i]), 1.2f, EventType.Repaint);
            }
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        private static void DrawFlock(BirdBoidsSpawner flock, GizmoType type)
        {
            Gizmos.color = new Color(0.55f, 0.8f, 1f, (type & GizmoType.Selected) != 0 ? 0.6f : 0.2f);
            Gizmos.DrawWireSphere(flock.transform.position, Mathf.Sqrt(Mathf.Max(flock.SwarmSqrRadius, 0f)));
            Gizmos.DrawWireSphere(flock.transform.position, flock._spawnRadius);
        }
    }

    // ------------------------------------------------------------ flight preview

    // BirdsSpawner.SpawnBirds + BirdCurveBrain.ManualUpdate + Bird.SetDirection in the Scene view, on HideAndDontSave
    // copies of the prefab; the Animator's two states are sampled from their clips (no cross-fade)
    public static class TarkovBirdsPreview
    {
        private const float Seconds = 120f;

        public static void Play(BirdsSpawner spawner)
        {
            var prefabs = (spawner._birdsPrefabs ?? new GameObject[0]).Where(p => p != null && p.GetComponent<Bird>() != null).ToArray();
            var paths = (spawner._paths ?? new BezierSpline[0]).Where(p => p != null && p.points != null && p.points.Length >= 4).ToList();
            if (prefabs.Length == 0 || paths.Count == 0)
                return;
            int spawned = 0;
            for (int round = 0; spawned < spawner._count; round++)
                foreach (var path in paths)
                {
                    if (spawned++ >= spawner._count)
                        break;
                    var prefab = prefabs[UnityEngine.Random.Range(0, prefabs.Length)];
                    float at = round * Mathf.Max(spawner._spawnDelay, 0f);
                    if (at <= 0f)
                        Fly(prefab, path, Seconds);
                    else
                        TarkovPreview.After(at, () => Fly(prefab, path, Seconds - at));
                }
        }

        public static void PlayFlock(BirdBoidsSpawner flock)
        {
            var prefabs = (flock._birdsPrefabs ?? new GameObject[0]).Where(p => p != null && p.GetComponent<Bird>() != null).ToArray();
            if (prefabs.Length == 0)
                return;
            var boids = new List<Boid>();
            for (int i = 0; i < flock._count; i++)
            {
                var prefab = prefabs[UnityEngine.Random.Range(0, prefabs.Length)];
                float at = i * Mathf.Max(flock._spawnDelay, 0f);
                void Spawn()
                {
                    var bird = Copy(prefab, flock.transform.position + UnityEngine.Random.insideUnitSphere * flock._spawnRadius, Seconds - at);
                    boids.Add(new Boid { Bird = new BirdPose(bird), Velocity = UnityEngine.Random.onUnitSphere * flock.BaseSpeed });
                }
                if (at <= 0f)
                    Spawn();
                else
                    TarkovPreview.After(at, Spawn);
            }
            float calc = 0f, direction = 0f;
            TarkovPreview.Animate(dt =>
            {
                if (flock == null)
                    return false;
                boids.RemoveAll(b => b.Bird.Root == null);
                var centre = flock.transform.position;
                // BirdBoidBrain.CalculateVelocity, every CalculationVelocityInterval (the boids' own Water-layer spheres)
                if ((calc -= dt) <= 0f)
                {
                    calc = flock.CalculationVelocityInterval;
                    foreach (var b in boids)
                    {
                        var near = boids.Where(o => Vector3.Distance(o.Position, b.Position) <= flock.CohesionRadius + 0.5f).Take(flock.MaxBoidsForCohesion).ToList();
                        b.Velocity = Vector3.zero;
                        if (near.Count < flock.MinBoidsForCohesion)
                            continue;
                        Vector3 cohesion = Vector3.zero, alignment = Vector3.zero, separation = Vector3.zero;
                        int apart = 0;
                        foreach (var o in near)
                        {
                            cohesion += o.Position;
                            alignment += o.Velocity;
                            var away = b.Position - o.Position;
                            if (o != b && away.sqrMagnitude < flock.SeparationMaxSqrDistance)
                            {
                                separation += away / away.sqrMagnitude;
                                apart++;
                            }
                        }
                        cohesion = (cohesion / near.Count - b.Position) / near.Count;
                        if (apart > 0)
                            separation /= apart;
                        separation *= flock.SeparationMult;
                        b.Velocity = Vector3.ClampMagnitude(cohesion * flock.CohesionMult + separation * flock.SeparationMult + alignment * flock.AlignmentMult, flock.BaseSpeed);
                    }
                }
                // BirdBoidBrain.Update
                foreach (var b in boids)
                {
                    var off = b.Position - centre;
                    if (off.sqrMagnitude > flock.SwarmSqrRadius)
                        b.Velocity -= off / (flock.SwarmSqrRadius / 2f);
                    b.Bird.Root.transform.position += b.Velocity * dt * flock.VelocityMult;
                }
                bool turn = (direction -= dt) <= 0f;
                if (turn)
                    direction = flock.UpdateDirectionInterval;
                foreach (var b in boids)
                {
                    if (turn)
                        b.Bird.SetDirection(b.Velocity.normalized, b.Velocity.magnitude);
                    b.Bird.Animate(dt);
                }
                return true;
            });
        }

        private sealed class Boid
        {
            public BirdPose Bird;
            public Vector3 Velocity;
            public Vector3 Position => Bird.Root.transform.position;
        }

        private static GameObject Copy(GameObject prefab, Vector3 position, float lifetime)
        {
            var holder = TarkovPreview.Spawn("Bird preview", position, lifetime);
            var bird = Object.Instantiate(prefab, holder.transform);
            bird.transform.localPosition = Vector3.zero;
            foreach (var t in bird.GetComponentsInChildren<Transform>(true))
                t.gameObject.hideFlags = HideFlags.HideAndDontSave;
            return bird;
        }

        private static void Fly(GameObject prefab, BezierSpline spline, float lifetime)
        {
            if (spline == null)
                return;
            var bird = Copy(prefab, spline.transform.TransformPoint(spline.points[0]), lifetime);
            var pose = new BirdPose(bird);
            var cache = new LengthCache(spline);
            var brain = bird.GetComponent<Bird>();
            float t = 0f, speed = 0f, duration = 1f;
            void Recalculate(Vector3 direction)
            {
                speed = (direction * 6f + new Vector3(0f, -9.8f, 0f) * 0.1f).magnitude;
                duration = cache.Length / speed;
            }
            Recalculate(Vector3.forward);
            TarkovPreview.Animate(dt =>
            {
                if (bird == null || spline == null)
                    return false;
                // BirdCurveBrain.ManualUpdate
                t = Mathf.Min(t + dt / duration, 1f);
                var (point, direction) = cache.At(t);
                bird.transform.position = point;
                pose.SetDirection(direction, speed);
                if (t >= 1f)
                    t = 0f;
                Recalculate(direction);
                pose.Animate(dt);
                return true;
            });
        }

        // Bird.SetDirection and the Animator it drives
        private sealed class BirdPose
        {
            public readonly GameObject Root;
            private readonly Bird _bird;
            private readonly AnimationClip _flight, _soar;
            private Vector3 _direction;
            private float _shaftAngle, _shaftVelocity, _speed = 1f, _time;
            private bool _soaring;

            public BirdPose(GameObject root)
            {
                Root = root;
                _bird = root.GetComponent<Bird>();
                var clips = _bird._animator != null && _bird._animator.runtimeAnimatorController != null
                    ? _bird._animator.runtimeAnimatorController.animationClips : new AnimationClip[0];
                _flight = clips.FirstOrDefault(c => c.name == "Flight") ?? clips.FirstOrDefault();
                _soar = clips.FirstOrDefault(c => c.name == "Soar") ?? _flight;
            }

            public void SetDirection(Vector3 direction, float commonSpeedMult)
            {
                if (_bird.Carina == null || commonSpeedMult == 0f || direction == Vector3.zero)
                    return;
                _bird.Carina.forward = direction;
                float target = (_direction.x - direction.x) * _bird._directionChangeSensitivity;
                _shaftAngle = Mathf.SmoothDampAngle(_shaftAngle, target, ref _shaftVelocity, _bird._shaftAngleSmooth);
                _bird.Carina.Rotate(direction, _shaftAngle);
                float flap;
                if (direction.y > _bird._switchToSoarPoint)
                {
                    _soaring = false;
                    flap = 1f + Mathf.InverseLerp(_bird._switchToSoarPoint, 1f, direction.y) * _bird._directionToFlap;
                }
                else
                {
                    _soaring = true;
                    flap = 1f + Mathf.InverseLerp(_bird._switchToSoarPoint, -1f, direction.y) * _bird._directionToFlap;
                }
                _speed = flap * commonSpeedMult * _bird._speedToFlap;
                _direction = direction;
            }

            public void Animate(float dt)
            {
                var clip = _soaring ? _soar : _flight;
                if (clip == null)
                    return;
                _time += dt * _speed;
                clip.SampleAnimation(Root, Mathf.Repeat(_time, clip.length));
            }
        }

        // BezierSpline.CreateCurvesLengthCache(0.1) + GetCurveIndexAndTime: t runs over the length, curve by curve
        private sealed class LengthCache
        {
            private readonly BezierSpline _spline;
            private readonly List<float> _lengths = new List<float>();
            public readonly float Length;

            public LengthCache(BezierSpline spline)
            {
                _spline = spline;
                const float sensitivity = 0.1f;
                float delta = 0.1f;
                for (int c = 0; c < Curves; c++)
                {
                    float length = 0f, u = 0f;
                    var a = Point(c, 0f);
                    do
                    {
                        u = Mathf.Min(u + delta, 1f);
                        var b = Point(c, u);
                        float d = Vector3.Distance(a, b);
                        if (d > sensitivity)
                        {
                            u -= delta;
                            delta *= sensitivity / d;
                        }
                        else
                        {
                            length += d;
                            a = b;
                        }
                    }
                    while (u < 1f);
                    _lengths.Add(length);
                    Length += length;
                }
            }

            private int Curves => (_spline.points.Length - 1) / 3;

            private Vector3 Point(int curve, float u)
            {
                var p = _spline.points;
                int i = curve * 3;
                return _spline.transform.TransformPoint(TarkovSplines.Bezier(p[i], p[i + 1], p[i + 2], p[i + 3], u));
            }

            public (Vector3 Point, Vector3 Direction) At(float t)
            {
                float target = Length * Mathf.Clamp01(t), sum = 0f, u = 0f;
                int index = Curves - 1;
                for (int c = 0; c < Curves; c++)
                {
                    sum += _lengths[c];
                    if (sum >= target)
                    {
                        index = c;
                        u = _lengths[c] > 0f ? 1f - (sum - target) / _lengths[c] : 0f;
                        break;
                    }
                }
                var p = _spline.points;
                int i = index * 3;
                float v = 1f - u;
                var derivative = 3f * v * v * (p[i + 1] - p[i]) + 6f * v * u * (p[i + 2] - p[i + 1]) + 3f * u * u * (p[i + 3] - p[i + 2]);
                var direction = (_spline.transform.TransformPoint(derivative) - _spline.transform.position).normalized;
                return (Point(index, u), direction);
            }
        }
    }

    [CustomEditor(typeof(BirdsSpawner))]
    internal class TarkovBirdsSpawnerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var spawner = (BirdsSpawner)target;
            EditorGUILayout.HelpBox($"Count birds fly the paths: one on each path, then Spawn Delay seconds, then the next round. They fly about {TarkovBirds.PathSpeed:0.#} m/s, " +
                                    "flapping when level or climbing and gliding on the way down. Draw and edit paths with the Bird Path Tool.", MessageType.None);
            DrawDefaultInspector();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Bird Path Tool..."))
                    TarkovBirdsWindow.Open(spawner);
                if (GUILayout.Button(new GUIContent("Preview Flight", "Flies the birds in the Scene view for two minutes, as the game does (not saved).")))
                    TarkovBirdsPreview.Play(spawner);
                using (new EditorGUI.DisabledScope(!TarkovPreview.Running))
                    if (GUILayout.Button("Stop"))
                        TarkovPreview.StopAll();
            }
        }
    }

    [CustomEditor(typeof(BirdBoidsSpawner))]
    internal class TarkovBirdFlockEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("Count birds flock round this point (the outer sphere is how far they stray, the inner where they appear). " +
                                    "Each steers towards its neighbours within Cohesion Radius, away from those too close and along with them; " +
                                    "a bird with fewer than Min Boids For Cohesion near it, itself included, stops in the air, so keep the flock together.", MessageType.None);
            DrawDefaultInspector();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Preview Flock", "Flies the flock in the Scene view for two minutes, as the game does (not saved).")))
                    TarkovBirdsPreview.PlayFlock((BirdBoidsSpawner)target);
                using (new EditorGUI.DisabledScope(!TarkovPreview.Running))
                    if (GUILayout.Button("Stop"))
                        TarkovPreview.StopAll();
            }
        }
    }

    // ------------------------------------------------------------ the path tool

    public class TarkovBirdsWindow : EditorWindow
    {
        private BirdsSpawner _spawner;
        private float _height = 25f;
        private float _radius = 40f;
        private int _circlePoints = 8;
        private bool _drawing;
        private bool _editPoints = true;
        private readonly List<Vector3> _drawn = new List<Vector3>();
        private Vector2 _scroll;
        private double _nextCheck;
        private readonly Dictionary<BezierSpline, List<Vector3>> _blocked = new Dictionary<BezierSpline, List<Vector3>>();

        [MenuItem("Tarkov SDK/Tools/Geometry/Bird Path Tool...", priority = 130)]
        public static void OpenMenu() => Open(Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<BirdsSpawner>() : null);

        public static void Open(BirdsSpawner spawner)
        {
            var window = GetWindow<TarkovBirdsWindow>("Bird Paths");
            window.minSize = new Vector2(340f, 380f);
            if (spawner != null)
                window._spawner = spawner;
        }

        private void OnEnable() => SceneView.duringSceneGui += OnSceneGUI;
        private void OnDisable() => SceneView.duringSceneGui -= OnSceneGUI;

        private void OnSelectionChange()
        {
            var picked = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<BirdsSpawner>() : null;
            if (picked != null && !_drawing)
            {
                _spawner = picked;
                Repaint();
            }
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.HelpBox("Birds fly closed loops: draw one by clicking its points in the Scene view (each Height above the ground you click), " +
                                    "or add a circle round the birds. Drag a path's points to change it; red marks show where it goes through something.", MessageType.None);
            using (new EditorGUILayout.HorizontalScope())
            {
                _spawner = (BirdsSpawner)EditorGUILayout.ObjectField("Birds", _spawner, typeof(BirdsSpawner), true);
                if (GUILayout.Button("New", GUILayout.Width(50f)))
                    EditorApplication.delayCall += () =>
                    {
                        // its paths are its children, so they move with it
                        var spawner = TarkovBirds.CreatePaths(null);
                        if (SceneView.lastActiveSceneView != null)
                            spawner.transform.position = SceneView.lastActiveSceneView.pivot;
                        Undo.RegisterCreatedObjectUndo(spawner.gameObject, "Create birds");
                        Selection.activeGameObject = spawner.gameObject;
                        _spawner = spawner;
                    };
            }
            _height = EditorGUILayout.Slider(new GUIContent("Height (m)", "How far above the ground new points are."), _height, 3f, 120f);
            if (_spawner == null)
            {
                EditorGUILayout.HelpBox("Pick birds (a BirdsSpawner), or press New, or GameObject > Tarkov SDK > Environment > Birds (Flight Paths).", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUILayout.Space();
            if (!_drawing)
            {
                if (GUILayout.Button("Draw A Path (click in the Scene view)", GUILayout.Height(26f)))
                {
                    _drawing = true;
                    _drawn.Clear();
                    SceneView.lastActiveSceneView?.Focus();
                }
            }
            else
            {
                EditorGUILayout.HelpBox($"{_drawn.Count} point(s). Click to add; Enter or Close Loop to finish (3 or more); Backspace removes the last; Esc cancels.", MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(_drawn.Count < 3))
                        if (GUILayout.Button("Close Loop", GUILayout.Height(24f)))
                            FinishDrawing();
                    if (GUILayout.Button("Cancel", GUILayout.Height(24f)))
                        _drawing = false;
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                _radius = EditorGUILayout.Slider("Circle Radius (m)", _radius, 5f, 300f);
                _circlePoints = EditorGUILayout.IntSlider(_circlePoints, 4, 16, GUILayout.Width(110f));
            }
            if (GUILayout.Button("Add A Circle Round The Birds"))
            {
                var spawner = _spawner;
                int direction = (spawner._paths?.Length ?? 0) % 2 == 0 ? 1 : -1;
                Undo.RegisterFullObjectHierarchyUndo(spawner.gameObject, "Add bird path");
                var spline = TarkovBirds.AddPath(spawner, TarkovBirds.Circle(spawner.transform.position, _radius, _height, _circlePoints, _height * 0.15f, direction));
                Undo.RegisterCreatedObjectUndo(spline.gameObject, "Add bird path");
                _blocked.Clear();
            }
            _editPoints = EditorGUILayout.ToggleLeft("Show handles on the path points", _editPoints);

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Paths", EditorStyles.boldLabel);
            var paths = _spawner._paths ?? new BezierSpline[0];
            if (paths.Length == 0)
                EditorGUILayout.LabelField("None yet.", EditorStyles.miniLabel);
            foreach (var path in paths.ToList())
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    if (path == null || path.points == null || path.points.Length < 4)
                    {
                        EditorGUILayout.LabelField("(missing)", EditorStyles.miniLabel);
                        if (GUILayout.Button("Remove", GUILayout.Width(62f)))
                            RemovePath(path);
                        continue;
                    }
                    float length = TarkovBirds.Length(path);
                    int blocked = _blocked.TryGetValue(path, out var b) ? b.Count : 0;
                    EditorGUILayout.LabelField($"{path.name}: {length:0} m, a lap in {length / TarkovBirds.PathSpeed:0} s" + (blocked > 0 ? $", hits something {blocked}x" : ""), EditorStyles.miniLabel);
                    if (GUILayout.Button("Select", GUILayout.Width(50f)))
                        Selection.activeObject = path.gameObject;
                    if (GUILayout.Button(new GUIContent("Ground", "Puts every point Height above the ground under it."), GUILayout.Width(56f)))
                    {
                        Undo.RecordObject(path, "Bird path to ground");
                        TarkovBirds.SetLoop(path, TarkovBirds.Waypoints(path).Select(p => TarkovBirds.Ground(p, out var y) ? new Vector3(p.x, y + _height, p.z) : p).ToList());
                        _blocked.Clear();
                    }
                    if (GUILayout.Button(new GUIContent("Reverse", "Birds fly it the other way."), GUILayout.Width(58f)))
                    {
                        Undo.RecordObject(path, "Reverse bird path");
                        var points = TarkovBirds.Waypoints(path);
                        points.Reverse();
                        TarkovBirds.SetLoop(path, points);
                    }
                    if (GUILayout.Button("X", GUILayout.Width(22f)))
                        RemovePath(path);
                }
            EditorGUILayout.Space(8f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Preview Flight"))
                    TarkovBirdsPreview.Play(_spawner);
                using (new EditorGUI.DisabledScope(!TarkovPreview.Running))
                    if (GUILayout.Button("Stop"))
                        TarkovPreview.StopAll();
            }
            EditorGUILayout.EndScrollView();
        }

        private void RemovePath(BezierSpline path)
        {
            var spawner = _spawner;
            EditorApplication.delayCall += () =>
            {
                Undo.RecordObject(spawner, "Remove bird path");
                spawner._paths = (spawner._paths ?? new BezierSpline[0]).Where(p => p != null && p != path).ToArray();
                EditorUtility.SetDirty(spawner);
                if (path != null)
                    Undo.DestroyObjectImmediate(path.gameObject);
            };
        }

        private void FinishDrawing()
        {
            if (_drawn.Count >= 3 && _spawner != null)
            {
                var spline = TarkovBirds.AddPath(_spawner, _drawn.ToList());
                Undo.RegisterCreatedObjectUndo(spline.gameObject, "Draw bird path");
                _blocked.Clear();
            }
            _drawing = false;
            _drawn.Clear();
            Repaint();
        }

        private void OnSceneGUI(SceneView view)
        {
            if (_spawner == null)
                return;
            // where the paths go through something, rechecked every second
            if (EditorApplication.timeSinceStartup > _nextCheck)
            {
                _nextCheck = EditorApplication.timeSinceStartup + 1.0;
                _blocked.Clear();
                foreach (var path in (_spawner._paths ?? new BezierSpline[0]).Where(p => p != null && p.points != null && p.points.Length >= 4))
                    _blocked[path] = TarkovBirds.Blocked(path);
                Repaint();
            }
            Handles.color = new Color(1f, 0.3f, 0.25f);
            foreach (var spot in _blocked.Values.SelectMany(v => v))
                Handles.DrawWireDisc(spot, view.camera.transform.forward, 1.5f);

            if (_drawing)
            {
                Draw(view);
                return;
            }
            if (!_editPoints)
                return;
            foreach (var path in (_spawner._paths ?? new BezierSpline[0]).Where(p => p != null && p.points != null && p.points.Length >= 4))
            {
                var points = TarkovBirds.Waypoints(path);
                for (int i = 0; i < points.Count; i++)
                {
                    float size = HandleUtility.GetHandleSize(points[i]) * 0.12f;
                    EditorGUI.BeginChangeCheck();
                    // the sphere moves the point across, keeping its height; the arrow up and down
                    Handles.color = new Color(0.55f, 0.8f, 1f);
                    var across = Handles.FreeMoveHandle(points[i], size, Vector3.zero, Handles.SphereHandleCap);
                    Handles.color = Handles.yAxisColor;
                    var up = Handles.Slider(points[i], Vector3.up, size * 6f, Handles.ArrowHandleCap, 0f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(path, "Move bird path point");
                        points[i] = new Vector3(across.x, up.y, across.z);
                        TarkovBirds.SetLoop(path, points);
                    }
                }
                Handles.Label(points[0] + Vector3.up * 2f, path.name);
            }
        }

        private void Draw(SceneView view)
        {
            var e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Escape)
                {
                    _drawing = false;
                    _drawn.Clear();
                    e.Use();
                    Repaint();
                    return;
                }
                if ((e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && _drawn.Count >= 3)
                {
                    FinishDrawing();
                    e.Use();
                    return;
                }
                if (e.keyCode == KeyCode.Backspace && _drawn.Count > 0)
                {
                    _drawn.RemoveAt(_drawn.Count - 1);
                    e.Use();
                    Repaint();
                    return;
                }
            }
            int id = GUIUtility.GetControlID(FocusType.Passive);
            if (e.type == EventType.Layout)
                HandleUtility.AddDefaultControl(id);
            Handles.color = new Color(0.55f, 0.8f, 1f);
            for (int i = 0; i < _drawn.Count; i++)
            {
                Handles.DrawWireDisc(_drawn[i], Vector3.up, 0.8f);
                if (i > 0)
                    Handles.DrawLine(_drawn[i - 1], _drawn[i]);
            }
            var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            if (!Physics.Raycast(ray, out var hit, 5000f, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore))
                return;
            var point = hit.point + Vector3.up * _height;
            Handles.DrawDottedLine(hit.point, point, 3f);
            Handles.DrawWireDisc(point, Vector3.up, 1f);
            if (_drawn.Count > 0)
            {
                Handles.DrawDottedLine(_drawn[_drawn.Count - 1], point, 4f);
                if (_drawn.Count >= 2)
                    Handles.DrawDottedLine(point, _drawn[0], 4f);
            }
            Handles.Label(point + Vector3.up * 1.5f, _drawn.Count < 3 ? "Click: add a point" : "Click: add a point (Enter: close the loop)");
            if (e.type == EventType.MouseMove)
                SceneView.RepaintAll();
            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                _drawn.Add(point);
                e.Use();
                Repaint();
            }
        }
    }
}
