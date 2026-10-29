using System.Collections.Generic;
using System.Linq;
using EFT.Ballistics;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Overhead wires, as retail maps' wires (BSG's editor-only WireGenerator, about 2,700 of them on the five big maps:
    // spans A -> B with a Gravity sag, ShapeSize 0.02, ShapeDetails 3 to 5 sides, baked into a plain mesh on
    // p0/Reflective/Bumped Specular with a LODGroup). Here a wire line is:
    //   Wires (MeshFilter + MeshRenderer + LODGroup: what the game draws)
    //     SDK_Wire (EditorOnly, TarkovWire settings)
    //       Point 1..n (EditorOnly: where the wires hang from, in order)
    //     Poles (optional wooden poles with crossarms and colliders, one under each point)
    // The mesh and poles are rebuilt when the settings or points change (moving a point with the Move tool too). The
    // Wire Tool window (Tarkov SDK > Tools > Geometry > Wire Tool...) places points by clicking in the Scene view.
    [InitializeOnLoad]
    public static class TarkovWires
    {
        private const string Menu = "GameObject/Tarkov SDK/Environment/";
        private static double _next;
        private static readonly Dictionary<int, int> Hashes = new Dictionary<int, int>();

        static TarkovWires()
        {
            // rebuild the selected line when its points or settings change
            EditorApplication.update += () =>
            {
                if (EditorApplication.timeSinceStartup < _next || EditorApplication.isPlayingOrWillChangePlaymode)
                    return;
                _next = EditorApplication.timeSinceStartup + 0.2;
                foreach (var wire in Selection.transforms.Select(t => t.GetComponentInParent<TarkovWire>()).Where(w => w != null).Distinct())
                {
                    int hash = Hash(wire);
                    if (Hashes.TryGetValue(wire.GetInstanceID(), out var old) && old == hash)
                        continue;
                    bool known = Hashes.ContainsKey(wire.GetInstanceID());
                    Hashes[wire.GetInstanceID()] = hash;
                    if (known || !HasSway(wire))
                        Rebuild(wire);
                }
            };
        }

        [MenuItem(Menu + "Wire", priority = 72)]
        private static void MenuWire(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p =>
        {
            var wire = Create(p, new[] { new Vector3(0f, 7f, 0f), new Vector3(25f, 7f, 0f) }, null);
            return wire.transform.parent.gameObject;
        });

        // a line built before wires swayed (no sway weights in TEXCOORD1) is rebuilt when selected, and by Build Map
        private static bool HasSway(TarkovWire wire)
        {
            var filter = wire.transform.parent != null ? wire.transform.parent.GetComponent<MeshFilter>() : null;
            return filter == null || filter.sharedMesh == null || filter.sharedMesh.uv2 != null && filter.sharedMesh.uv2.Length > 0;
        }

        private static int Hash(TarkovWire wire)
        {
            unchecked
            {
                int h = JsonUtility.ToJson(wire).GetHashCode();
                foreach (Transform point in wire.transform)
                    h = h * 31 + point.position.GetHashCode();
                return h * 31 + wire.transform.parent.position.GetHashCode() + wire.transform.parent.rotation.GetHashCode();
            }
        }

        // a wire line through world points; settings copied from `like` if given
        public static TarkovWire Create(Transform parent, IList<Vector3> worldPoints, TarkovWire like)
        {
            var root = new GameObject("Wires");
            if (parent != null)
                root.transform.SetParent(parent, false);
            if (worldPoints.Count > 0 && parent == null)
                root.transform.position = new Vector3(worldPoints[0].x, 0f, worldPoints[0].z);
            root.AddComponent<MeshFilter>();
            root.AddComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
#pragma warning disable 618
            GameObjectUtility.SetStaticEditorFlags(root, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);
#pragma warning restore 618
            var wire = TarkovMapEvents.EditorOnlyChild(root.transform, "SDK_Wire").AddComponent<TarkovWire>();
            if (like != null)
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(like), wire);
            wire.WireMaterial ??= DefaultWireMaterial();
            wire.PoleMaterial ??= TarkovGeneratedAssets.SurfaceMaterial("SDK_WoodPole", new Color(0.28f, 0.21f, 0.15f, 0.08f), 0.12f);
            foreach (var p in worldPoints)
                AddPoint(wire, p);
            Rebuild(wire);
            return wire;
        }

        public static Transform AddPoint(TarkovWire wire, Vector3 world)
        {
            var point = TarkovMapEvents.EditorOnlyChild(wire.transform, "Point " + (wire.transform.childCount + 1));
            point.transform.position = world;
            return point.transform;
        }

        // ------------------------------------------------------------ rebuild

        public static void Rebuild(TarkovWire wire)
        {
            var root = wire.transform.parent;
            if (root == null)
                return;
            var points = wire.transform.Cast<Transform>().Select(t => t.position).ToList();
            var filter = root.GetComponent<MeshFilter>() ?? root.gameObject.AddComponent<MeshFilter>();
            var renderer = root.GetComponent<MeshRenderer>() ?? root.gameObject.AddComponent<MeshRenderer>();
            wire.WireMaterial = WindMaterial(wire.WireMaterial);
            if (wire.WireMaterial != null)
                renderer.sharedMaterial = wire.WireMaterial;

            // poles first go, so the ground below each point isn't found on the old pole
            var oldPoles = root.Find("Poles");
            if (oldPoles != null)
                Object.DestroyImmediate(oldPoles.gameObject);

            var spans = new List<(Vector3 A, Vector3 B)>();
            for (int i = 0; i + 1 < points.Count; i++)
                spans.Add((points[i], points[i + 1]));
            if (wire.Loop && points.Count > 2)
                spans.Add((points[points.Count - 1], points[0]));

            var mesh = new TarkovGeneratedAssets.MeshBuilder();
            var random = new System.Random(points.Count * 7919 + wire.WireCount);
            var toLocal = root.worldToLocalMatrix;
            foreach (var (a, b) in spans)
            {
                var span = b - a;
                float length = span.magnitude;
                if (length < 0.05f)
                    continue;
                var across = Across(span);
                int segments = Mathf.Max(2, Mathf.CeilToInt(length * wire.SegmentsPerMeter));
                // how far the middle of the span swings in the wind: longer spans more
                float swing = Mathf.Clamp(length / 40f, 0.15f, 0.75f) * wire.Sway;
                for (int w = 0; w < wire.WireCount; w++)
                {
                    var offset = across * (w - (wire.WireCount - 1) * 0.5f) * wire.Spacing;
                    float sag = wire.SagPer10m * length / 10f * (1f + wire.SagVariation * ((float)random.NextDouble() * 2f - 1f));
                    // each wire at its own pace, so they don't swing in lockstep
                    float speed = 0.85f + (float)random.NextDouble() * 0.3f;
                    var line = new List<Vector3>(segments + 1);
                    var weights = new List<Vector2>(segments + 1);
                    for (int s = 0; s <= segments; s++)
                    {
                        float t = s / (float)segments;
                        // a parabola is as close to a hanging wire's catenary as the eye can tell at these sags
                        line.Add(toLocal.MultiplyPoint3x4(Vector3.Lerp(a, b, t) + offset + Vector3.down * (4f * sag * t * (1f - t))));
                        // the wire wind shader's TEXCOORD1: sway weight (still at the poles), speed
                        weights.Add(new Vector2(4f * t * (1f - t) * swing, speed));
                    }
                    Tube(mesh, line, weights, wire.Radius, wire.Sides);
                }
            }

            var built = mesh.Build(root.name);
            filter.sharedMesh = SaveMesh(root, filter.sharedMesh, built);

            // cull thin wires far away, as retail's LODGroup does
            var lod = root.GetComponent<LODGroup>() ?? root.gameObject.AddComponent<LODGroup>();
            lod.SetLODs(new[] { new LOD(0.015f, new Renderer[] { renderer }) });
            lod.RecalculateBounds();

            if (wire.Poles && points.Count > 0)
                BuildPoles(wire, root, points, spans);
            EditorUtility.SetDirty(root.gameObject);
            Hashes[wire.GetInstanceID()] = Hash(wire);
        }

        // the game's swaying wire shader (p0/Bumped Specular Wires Wind, always loaded); the SDK's own wire material is
        // moved onto it (it was on the reflective p0 stand-in before), other materials are left as chosen
        public const string WindShader = "Tarkov/p0/Bumped Specular Wires Wind";

        public static Material DefaultWireMaterial() =>
            WindMaterial(TarkovGeneratedAssets.SurfaceMaterial("SDK_Wire", new Color(0.05f, 0.05f, 0.05f, 0.3f), 0f));

        private static Material WindMaterial(Material material)
        {
            if (material == null)
                material = TarkovGeneratedAssets.SurfaceMaterial("SDK_Wire", new Color(0.05f, 0.05f, 0.05f, 0.3f), 0f);
            var shader = Shader.Find(WindShader);
            if (material != null && shader != null && material.name == "SDK_Wire" && material.shader != shader && EditorUtility.IsPersistent(material))
            {
                material.shader = shader;
                EditorUtility.SetDirty(material);
            }
            return material;
        }

        private static Vector3 Across(Vector3 span)
        {
            var flat = new Vector3(span.x, 0f, span.z);
            if (flat.sqrMagnitude < 1e-6f)
                return Vector3.right;
            return Vector3.Cross(Vector3.up, flat.normalized).normalized;
        }

        // a tube along the polyline (local points)
        private static void Tube(TarkovGeneratedAssets.MeshBuilder mesh, List<Vector3> line, List<Vector2> weights, float radius, int sides)
        {
            int start = mesh.Vertices.Count;
            float along = 0f;
            for (int i = 0; i < line.Count; i++)
            {
                var tangent = (line[Mathf.Min(i + 1, line.Count - 1)] - line[Mathf.Max(i - 1, 0)]).normalized;
                var side = Vector3.Cross(tangent, Vector3.up);
                if (side.sqrMagnitude < 1e-6f)
                    side = Vector3.right;
                side.Normalize();
                var up = Vector3.Cross(side, tangent).normalized;
                if (i > 0)
                    along += Vector3.Distance(line[i], line[i - 1]);
                for (int s = 0; s < sides; s++)
                {
                    float angle = s * Mathf.PI * 2f / sides;
                    var normal = side * Mathf.Cos(angle) + up * Mathf.Sin(angle);
                    mesh.Add(line[i] + normal * radius, normal, new Vector2(s / (float)sides, along), weights[i]);
                }
            }
            for (int i = 0; i + 1 < line.Count; i++)
                for (int s = 0; s < sides; s++)
                {
                    int a = start + i * sides + s, b = start + i * sides + (s + 1) % sides;
                    int c = start + (i + 1) * sides + s, d = start + (i + 1) * sides + (s + 1) % sides;
                    mesh.Triangle(a, d, b);
                    mesh.Triangle(a, c, d);
                }
        }

        // the mesh asset next to the scene, reused (same GUID) on every rebuild
        private static Mesh SaveMesh(Transform root, Mesh current, Mesh built)
        {
            var scene = root.gameObject.scene;
            string path = current != null ? AssetDatabase.GetAssetPath(current) : null;
            if (string.IsNullOrEmpty(path))
            {
                if (string.IsNullOrEmpty(scene.path))
                    return built;
                string folder = System.IO.Path.GetDirectoryName(scene.path).Replace('\\', '/') + "/" + scene.name + "_Wires";
                TarkovGeneratedAssets.EnsureFolder(folder);
                path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{root.name}.asset");
            }
            return TarkovGeneratedAssets.SaveMesh(path, built);
        }

        // ------------------------------------------------------------ poles

        private static void BuildPoles(TarkovWire wire, Transform root, List<Vector3> points, List<(Vector3 A, Vector3 B)> spans)
        {
            var holder = new GameObject("Poles");
            holder.transform.SetParent(root, false);
            var unit = TarkovGeneratedAssets.Mesh(TarkovGeneratedAssets.PropsFolder + "/SDK_PoleUnit.asset",
                () => TarkovGeneratedAssets.Cylinder("SDK_PoleUnit", 1f, 1f, 10, open: false));
            var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            for (int i = 0; i < points.Count; i++)
            {
                var top = points[i];
                // the ground under the point (the map's colliders, not triggers)
                float ground = Physics.Raycast(top + Vector3.up * 0.2f, Vector3.down, out var hit, 60f, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore)
                    ? hit.point.y : top.y - 8f;
                float height = top.y - ground + 0.35f;
                if (height < 0.5f)
                    continue;
                // the crossarm across the wires of the spans meeting here
                var dir = Vector3.zero;
                foreach (var (a, b) in spans)
                    if (a == top || b == top)
                        dir += new Vector3((b - a).x, 0f, (b - a).z).normalized;
                var across = Across(dir.sqrMagnitude > 1e-6f ? dir : Vector3.forward);

                var pole = new GameObject("Pole " + (i + 1));
                pole.transform.SetParent(holder.transform, false);
                pole.transform.position = new Vector3(top.x, ground - 0.3f, top.z);
                pole.transform.rotation = Quaternion.LookRotation(Vector3.Cross(across, Vector3.up), Vector3.up);
#pragma warning disable 618
                GameObjectUtility.SetStaticEditorFlags(pole, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);
#pragma warning restore 618
                float h = height + 0.3f;
                var model = Part(pole.transform, "Model", unit, wire.PoleMaterial, new Vector3(0f, 0f, 0f), new Vector3(wire.PoleRadius, h, wire.PoleRadius));
                float width = Mathf.Max(0.8f, (wire.WireCount - 1) * wire.Spacing + 0.4f);
                if (wire.WireCount > 1)
                    Part(pole.transform, "Crossarm", cube, wire.PoleMaterial, new Vector3(0f, h - 0.35f - wire.Radius - 0.06f, 0f), new Vector3(width, 0.1f, 0.1f));
#pragma warning disable 618
                GameObjectUtility.SetStaticEditorFlags(model, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);
#pragma warning restore 618

                // movement and bullets, as the map's own props
                foreach (var (layer, name) in new[] { ("LowPolyCollider", "LowPoly"), ("HighPolyCollider", "HighPoly") })
                {
                    var go = new GameObject(name) { layer = LayerMask.NameToLayer(layer) };
                    go.transform.SetParent(pole.transform, false);
                    var capsule = go.AddComponent<CapsuleCollider>();
                    capsule.radius = wire.PoleRadius;
                    capsule.height = h;
                    capsule.center = new Vector3(0f, h * 0.5f, 0f);
                    if (name == "HighPoly")
                    {
                        var ballistic = go.AddComponent<BallisticCollider>();
                        ballistic._typeOfMaterial = MaterialType.WoodThick;
                        ballistic.SurfaceSound = BaseBallistic.ESurfaceSound.WoodThick;
                    }
#pragma warning disable 618
                    else
                        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.NavigationStatic);
#pragma warning restore 618
                }
            }
        }

        private static GameObject Part(Transform parent, string name, Mesh mesh, Material material, Vector3 local, Vector3 scale)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = local;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        // ------------------------------------------------------------ Build Map

        public static void Prepare(UnityEngine.SceneManagement.Scene scene, GameObject[] roots, TarkovScenePreparer.Report report)
        {
            foreach (var wire in roots.SelectMany(r => r.GetComponentsInChildren<TarkovWire>(true)))
            {
                if (wire.transform.parent == null)
                {
                    report.Warnings.Add($"{scene.name}: wire settings '{wire.name}' have no Wires object above them, so nothing is drawn");
                    continue;
                }
                if (wire.transform.childCount < 2)
                    report.Warnings.Add($"{scene.name}: wire line '{wire.transform.parent.name}' has fewer than two points, so it has no wires");
                // the mesh must be an asset or saved in the scene; a rebuild makes sure it matches the points
                Rebuild(wire);
            }
        }

        // ------------------------------------------------------------ gizmos

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.InSelectionHierarchy)]
        private static void DrawWire(TarkovWire wire, GizmoType type)
        {
            bool selected = (type & (GizmoType.Selected | GizmoType.InSelectionHierarchy)) != 0;
            if (!selected)
                return;
            Gizmos.color = new Color(1f, 0.9f, 0.3f, 0.9f);
            Transform previous = null;
            foreach (Transform point in wire.transform)
            {
                Gizmos.DrawWireSphere(point.position, 0.25f);
                if (previous != null)
                    Gizmos.DrawLine(previous.position, point.position);
                previous = point;
            }
        }
    }

    [CustomEditor(typeof(TarkovWire))]
    internal class TarkovWireEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var wire = (TarkovWire)target;
            EditorGUILayout.HelpBox("The wires hang between this object's children, in order. Move them with the Move tool; the wires follow. " +
                                    "Add points by clicking in the Scene view with Tarkov SDK > Tools > Geometry > Wire Tool...", MessageType.None);
            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            bool changed = EditorGUI.EndChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Point"))
                {
                    var last = wire.transform.childCount > 0 ? wire.transform.GetChild(wire.transform.childCount - 1).position : wire.transform.position;
                    var previous = wire.transform.childCount > 1 ? wire.transform.GetChild(wire.transform.childCount - 2).position : last - Vector3.right * 25f;
                    var point = TarkovWires.AddPoint(wire, last + (last - previous));
                    Undo.RegisterCreatedObjectUndo(point.gameObject, "Add wire point");
                    Selection.activeTransform = point;
                    changed = true;
                }
                if (GUILayout.Button("Rebuild"))
                    changed = true;
                if (GUILayout.Button("Wire Tool..."))
                    TarkovWiresWindow.Open();
            }
            if (changed)
                EditorApplication.delayCall += () =>
                {
                    if (wire != null)
                        TarkovWires.Rebuild(wire);
                };
        }
    }

    // Tarkov SDK > Tools > Geometry > Wire Tool...: click in the Scene view to place a new line's points (pole tops, or
    // points on walls and roofs), with the line's settings
    public class TarkovWiresWindow : EditorWindow
    {
        private TarkovWire _settings;
        private UnityEditor.Editor _settingsEditor;
        private TarkovWire _placing;
        private bool _placingStarted;
        private bool _onSurface;
        private float _poleHeight = 7f;
        private Vector2 _scroll;

        [MenuItem("Tarkov SDK/Tools/Geometry/Wire Tool...", priority = 129)]
        public static void Open() => GetWindow<TarkovWiresWindow>("Wire Tool").minSize = new Vector2(340f, 360f);

        private void OnEnable() => SceneView.duringSceneGui += OnSceneGUI;

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            if (_settingsEditor != null)
                DestroyImmediate(_settingsEditor);
            if (_settings != null)
                DestroyImmediate(_settings.gameObject);
        }

        // the settings new lines get, on a hidden object
        private TarkovWire Settings()
        {
            if (_settings == null)
            {
                var go = new GameObject("WireToolSettings") { hideFlags = HideFlags.HideAndDontSave };
                _settings = go.AddComponent<TarkovWire>();
                _settings.WireMaterial = TarkovWires.DefaultWireMaterial();
                _settings.PoleMaterial = TarkovGeneratedAssets.SurfaceMaterial("SDK_WoodPole", new Color(0.28f, 0.21f, 0.15f, 0.08f), 0.12f);
            }
            return _settings;
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("New wire line", EditorStyles.boldLabel);
            if (_settingsEditor == null || _settingsEditor.target != Settings())
                _settingsEditor = UnityEditor.Editor.CreateEditor(Settings());
            _settingsEditor.DrawDefaultInspector();
            _onSurface = EditorGUILayout.Toggle(new GUIContent("Points On Surfaces", "Each click is a point where you click (a wall, a roof). Off: a pole top, Pole Height above the ground you click."), _onSurface);
            using (new EditorGUI.DisabledScope(_onSurface))
                _poleHeight = EditorGUILayout.Slider("Pole Height (m)", _poleHeight, 2f, 15f);

            EditorGUILayout.Space();
            if (_placing == null)
            {
                if (GUILayout.Button("Place A New Line (click in the Scene view)", GUILayout.Height(26f)))
                {
                    _placing = null;
                    _placingStarted = true;
                    SceneView.lastActiveSceneView?.Focus();
                }
            }
            else
            {
                EditorGUILayout.HelpBox($"Placing '{_placing.transform.parent.name}': click to add points, Esc or Done to finish.", MessageType.Info);
                if (GUILayout.Button("Done", GUILayout.Height(26f)))
                    _placing = null;
            }
            if (_placingStarted)
                EditorGUILayout.HelpBox("Click in the Scene view for the first point (Esc to cancel).", MessageType.Info);

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Wire lines in the open scenes", EditorStyles.boldLabel);
            var wires = FindObjectsOfType<TarkovWire>(true);
            if (wires.Length == 0)
                EditorGUILayout.LabelField("None yet.", EditorStyles.miniLabel);
            foreach (var wire in wires)
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField($"{(wire.transform.parent != null ? wire.transform.parent.name : wire.name)}: {wire.transform.childCount} points, {wire.WireCount} wire(s)", EditorStyles.miniLabel);
                    if (GUILayout.Button("Select", GUILayout.Width(52f)))
                        Selection.activeObject = wire.gameObject;
                    if (GUILayout.Button("Continue", GUILayout.Width(66f)))
                        _placing = wire;
                }
            if (wires.Length > 0 && GUILayout.Button("Rebuild All"))
                EditorApplication.delayCall += () =>
                {
                    foreach (var wire in FindObjectsOfType<TarkovWire>(true))
                        TarkovWires.Rebuild(wire);
                };
            EditorGUILayout.EndScrollView();
        }

        private void OnSceneGUI(SceneView view)
        {
            if (_placing == null && !_placingStarted)
                return;
            var e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                _placing = null;
                _placingStarted = false;
                e.Use();
                Repaint();
                return;
            }
            int id = GUIUtility.GetControlID(FocusType.Passive);
            if (e.type == EventType.Layout)
                HandleUtility.AddDefaultControl(id);
            var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            if (!Physics.Raycast(ray, out var hit, 5000f, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore))
                return;
            var point = _onSurface ? hit.point : hit.point + Vector3.up * _poleHeight;
            Handles.color = new Color(1f, 0.9f, 0.3f);
            Handles.DrawLine(hit.point, point);
            Handles.DrawWireDisc(point, Vector3.up, 0.4f);
            if (_placing != null && _placing.transform.childCount > 0)
                Handles.DrawDottedLine(_placing.transform.GetChild(_placing.transform.childCount - 1).position, point, 4f);
            Handles.Label(point + Vector3.up * 0.6f, "Click: add a point (Esc: done)");
            if (e.type == EventType.MouseMove)
                SceneView.RepaintAll();
            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                if (_placing == null)
                {
                    _placing = TarkovWires.Create(null, new[] { point }, Settings());
                    _placing.transform.parent.name = "Wires";
                    Undo.RegisterCreatedObjectUndo(_placing.transform.parent.gameObject, "Create wire line");
                    _placingStarted = false;
                }
                else
                {
                    var added = TarkovWires.AddPoint(_placing, point);
                    Undo.RegisterCreatedObjectUndo(added.gameObject, "Add wire point");
                    TarkovWires.Rebuild(_placing);
                }
                e.Use();
                Repaint();
            }
        }
    }
}
