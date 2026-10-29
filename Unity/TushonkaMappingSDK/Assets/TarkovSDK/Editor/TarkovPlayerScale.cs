using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Tarkov SDK > Tools > Geometry > Player Scale...: how big the player is, for building at the right size. A figure
    // follows the mouse in the Scene view (or is placed as an EditorOnly TarkovPlayerScale), a measuring tool says what
    // the player can do between two points, and the open scenes' size is put next to the retail maps'.
    // The sizes are the game's: the player prefab's CharacterController (player.bundle: height 1.6, radius 0.366, step
    // 0.2, slope 50), MovementContext.CalculatePoseLevelValues (1.2 m at full crouch, 0.3 m prone), the rig's head bone
    // (1.63 m, so about 1.8 m to the top of the head), globals' VaultingSettings (vault 0.25 to 1.1 m high and up to
    // 0.41 m deep, climb to 1.31 m) and Health.Falling.SafeHeight (3 m).
    public class TarkovPlayerScaleWindow : EditorWindow
    {
        public const float Height = 1.8f;
        public const float StandCapsule = 1.6f;
        public const float CrouchCapsule = 1.2f;
        public const float Radius = 0.366f;
        public const float Step = 0.2f;
        public const float SlopeLimit = 50f;
        public const float VaultMin = 0.25f;
        public const float VaultMax = 1.1f;
        public const float VaultDepth = 0.41f;
        public const float ClimbMax = 1.31f;
        public const float SafeFall = 3f;

        // the retail maps' playable size: the spread of their player spawn points (SPT's base.json), x by z
        private static readonly (string Name, float X, float Z)[] RetailSizes =
        {
            ("Labyrinth", 93f, 107f), ("Factory", 119f, 137f), ("Labs", 171f, 230f), ("Ground Zero", 246f, 441f), ("Reserve", 524f, 432f),
            ("Streets", 535f, 681f), ("Interchange", 773f, 809f), ("Customs", 999f, 441f), ("Woods", 1230f, 1222f), ("Lighthouse", 837f, 1442f),
            ("Shoreline", 1399f, 917f),
        };

        private TarkovPlayerScale.Poses _pose;
        private bool _follow = true;
        private bool _measure;
        private Vector3? _a, _b;
        private Vector3? _mouse;
        private Bounds? _mapBounds;
        private Vector2 _scroll;

        [MenuItem("Tarkov SDK/Tools/Geometry/Player Scale...", priority = 131)]
        public static void Open() => GetWindow<TarkovPlayerScaleWindow>("Player Scale").minSize = new Vector2(340f, 420f);

        private void OnEnable() => SceneView.duringSceneGui += OnSceneGui;

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGui;
            SceneView.RepaintAll();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("The player, at the game's own sizes", EditorStyles.boldLabel);
            Row("Height", $"{Height:0.0} m to the top of the head");
            Row("Standing", $"{StandCapsule:0.0} m collision capsule, {Radius * 2f:0.00} m wide");
            Row("Crouching", $"{CrouchCapsule:0.0} m capsule at full crouch");
            Row("Prone", "0.3 m capsule, about 1.9 m long");
            Row("Step up", $"up to {Step:0.0} m without vaulting");
            Row("Slopes", $"walkable up to {SlopeLimit:0}°");
            Row("Vault", $"{VaultMin:0.00} to {VaultMax:0.0} m high, up to {VaultDepth:0.00} m deep");
            Row("Climb", $"onto ledges up to {ClimbMax:0.00} m");
            Row("Falls", $"no damage up to {SafeFall:0} m");
            EditorGUILayout.HelpBox("Doorways: retail doors are about 2.1 to 2.2 m tall with their frames. Openings narrower than " +
                                    $"{Radius * 2f + 0.1f:0.0} m or lower than {CrouchCapsule + 0.1f:0.0} m (crouched) stop the player.", MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("In the Scene view", EditorStyles.boldLabel);
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                _pose = (TarkovPlayerScale.Poses)EditorGUILayout.EnumPopup("Pose", _pose);
                _follow = EditorGUILayout.ToggleLeft(new GUIContent("Figure at the mouse", "A figure stands where the mouse points in the Scene view."), _follow);
                bool measure = EditorGUILayout.ToggleLeft(new GUIContent("Measure (click two points)",
                    "Click two points in the Scene view: their distance, and whether the player can step, vault, climb or drop between them."), _measure);
                if (measure != _measure)
                {
                    _measure = measure;
                    _a = _b = null;
                }
                if (check.changed)
                    SceneView.RepaintAll();
            }
            if (GUILayout.Button(new GUIContent("Place A Scale Figure", "An EditorOnly figure where the Scene view looks, kept in the scene (it isn't built into the map).")))
                EditorApplication.delayCall += PlaceFigure;
            if (_measure && _a != null && _b != null)
                EditorGUILayout.HelpBox(string.Join("\n", Verdict(_a.Value, _b.Value)), MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Map size", EditorStyles.boldLabel);
            if (GUILayout.Button(new GUIContent("Measure The Open Scenes", "The extent of the open scenes' solid ground and buildings (their colliders).")))
                _mapBounds = MeasureMap();
            DrawSizes();
            EditorGUILayout.EndScrollView();
        }

        private static void Row(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(80f));
                EditorGUILayout.LabelField(value, EditorStyles.wordWrappedLabel);
            }
        }

        private void DrawSizes()
        {
            float area = _mapBounds.HasValue ? _mapBounds.Value.size.x * _mapBounds.Value.size.z : -1f;
            if (_mapBounds.HasValue)
                EditorGUILayout.LabelField($"This map: {_mapBounds.Value.size.x:0} x {_mapBounds.Value.size.z:0} m, {_mapBounds.Value.size.y:0} m high", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Retail maps, where players spawn (x by z):", EditorStyles.miniLabel);
            bool placed = false;
            foreach (var map in RetailSizes.OrderBy(m => m.X * m.Z))
            {
                if (!placed && area >= 0f && area < map.X * map.Z)
                {
                    EditorGUILayout.LabelField("   ▶ this map", EditorStyles.miniBoldLabel);
                    placed = true;
                }
                EditorGUILayout.LabelField($"   {map.Name}: {map.X:0} x {map.Z:0} m", EditorStyles.miniLabel);
            }
            if (!placed && area >= 0f)
                EditorGUILayout.LabelField("   ▶ this map", EditorStyles.miniBoldLabel);
        }

        private static Bounds? MeasureMap()
        {
            Bounds? all = null;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || !TarkovRainCover.SolidBounds(scene, TarkovRainCover.SolidMask, out var bounds))
                    continue;
                if (all == null)
                    all = bounds;
                else
                {
                    var b = all.Value;
                    b.Encapsulate(bounds);
                    all = b;
                }
            }
            if (all == null)
                EditorUtility.DisplayDialog("Player Scale", "The open scenes have no solid colliders to measure.", "OK");
            return all;
        }

        // what the player can do from a to b
        public static List<string> Verdict(Vector3 a, Vector3 b)
        {
            var lines = new List<string>();
            float rise = b.y - a.y;
            float across = new Vector2(b.x - a.x, b.z - a.z).magnitude;
            lines.Add($"{Vector3.Distance(a, b):0.00} m apart: {across:0.00} m across, {Mathf.Abs(rise):0.00} m {(rise >= 0f ? "up" : "down")}");
            if (Mathf.Abs(rise) < 0.01f)
                lines.Add("Level.");
            // as a ledge or wall between them
            else if (rise > 0f)
            {
                if (rise <= Step)
                    lines.Add("As a ledge: a step, walked up without vaulting.");
                else if (rise <= VaultMax)
                    lines.Add($"As a ledge: vaulted over if it's no deeper than {VaultDepth:0.00} m, else climbed onto.");
                else if (rise <= ClimbMax)
                    lines.Add("As a ledge: climbed onto (too high to vault over).");
                else
                    lines.Add($"As a ledge: too high to climb ({ClimbMax:0.00} m at most), it needs stairs, a ramp or a ladder.");
            }
            else if (-rise <= SafeFall)
                lines.Add($"Dropping down: safe (no damage up to {SafeFall:0} m).");
            else
                lines.Add($"Dropping down: a {-rise:0.0} m fall hurts the player (over {SafeFall:0} m).");
            // as a ramp between them
            if (Mathf.Abs(rise) >= 0.01f && across > 0.3f)
            {
                float angle = Mathf.Atan2(Mathf.Abs(rise), across) * Mathf.Rad2Deg;
                lines.Add($"As a slope: {angle:0}°, " + (angle <= SlopeLimit ? "walkable" : $"too steep to walk (the limit is {SlopeLimit:0}°)") + ".");
            }
            if (Mathf.Abs(rise) < 0.3f && across > 0.3f && across < Radius * 2f + 0.1f)
                lines.Add($"As a gap between walls: too narrow for the player ({Radius * 2f + 0.1f:0.0} m needed).");
            return lines;
        }

        private void PlaceFigure()
        {
            var view = SceneView.lastActiveSceneView;
            var point = Vector3.zero;
            if (view != null)
                point = Physics.Raycast(view.camera.transform.position, view.camera.transform.forward, out var hit, 2000f, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore)
                    ? hit.point
                    : view.pivot;
            var go = new GameObject($"Player Scale ({_pose})") { tag = "EditorOnly" };
            go.transform.position = point;
            if (view != null)
                go.transform.rotation = Facing(view.camera);
            go.AddComponent<TarkovPlayerScale>().Pose = _pose;
            Undo.RegisterCreatedObjectUndo(go, "Place player scale figure");
            Selection.activeGameObject = go;
            EditorSceneManager.MarkSceneDirty(go.scene);
        }

        // facing the camera, upright
        private static Quaternion Facing(Camera camera)
        {
            var forward = -camera.transform.forward;
            forward.y = 0f;
            return forward.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(forward.normalized) : Quaternion.identity;
        }

        private static Vector3? Under(Vector2 mouse)
        {
            var ray = HandleUtility.GUIPointToWorldRay(mouse);
            if (Physics.Raycast(ray, out var hit, 5000f, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore))
                return hit.point;
            var ground = new Plane(Vector3.up, Vector3.zero);
            return ground.Raycast(ray, out var at) ? ray.GetPoint(at) : (Vector3?)null;
        }

        private void OnSceneGui(SceneView view)
        {
            var e = Event.current;
            if (_measure)
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
            {
                _mouse = Under(e.mousePosition);
                view.Repaint();
            }
            if (_measure && e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                var point = Under(e.mousePosition);
                if (point != null)
                {
                    if (_a == null || _b != null)
                    {
                        _a = point;
                        _b = null;
                    }
                    else
                        _b = point;
                    Repaint();
                }
                e.Use();
            }
            if (e.type != EventType.Repaint)
                return;
            if (_follow && _mouse != null)
                TarkovPlayerFigure.Draw(Matrix4x4.TRS(_mouse.Value, Facing(view.camera), Vector3.one), _pose, new Color(0.35f, 0.75f, 1f, 0.45f), false);
            if (_measure)
                DrawMeasure();
        }

        private void DrawMeasure()
        {
            Handles.color = new Color(1f, 0.85f, 0.2f);
            if (_a != null)
                Handles.DrawWireDisc(_a.Value, Vector3.up, 0.15f);
            var end = _b ?? _mouse;
            if (_a == null || end == null)
                return;
            Handles.DrawWireDisc(end.Value, Vector3.up, 0.15f);
            Handles.DrawAAPolyLine(3f, _a.Value, end.Value);
            // the rise as a vertical leg
            var corner = new Vector3(end.Value.x, _a.Value.y, end.Value.z);
            Handles.color = new Color(1f, 0.85f, 0.2f, 0.5f);
            Handles.DrawDottedLine(_a.Value, corner, 3f);
            Handles.DrawDottedLine(corner, end.Value, 3f);
            Handles.Label((_a.Value + end.Value) * 0.5f + Vector3.up * 0.2f, string.Join("\n", Verdict(_a.Value, end.Value)), EditorStyles.helpBox);
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
        private static void DrawPlaced(TarkovPlayerScale figure, GizmoType type)
        {
            bool selected = (type & GizmoType.Selected) != 0;
            var t = figure.transform;
            var at = Matrix4x4.TRS(t.position, Quaternion.Euler(0f, t.eulerAngles.y, 0f), Vector3.one);
            TarkovPlayerFigure.Draw(at, figure.Pose, selected ? new Color(1f, 0.7f, 0.25f, 0.9f) : new Color(0.85f, 0.85f, 0.85f, 0.8f), true);
            if (selected && figure.ShowLimits)
                TarkovPlayerFigure.DrawLimits(at, figure.Pose);
        }
    }

    // The figure: simple shapes at the player's size for the pose (feet at the origin, facing +z)
    public static class TarkovPlayerFigure
    {
        private const string MaterialName = "TarkovSDK_PlayerScale";
        private static Material _material;
        private static Mesh _capsule, _sphere;

        [InitializeOnLoadMethod]
        private static void Cleanup()
        {
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                if (m != null && m.name == MaterialName && m != _material)
                    Object.DestroyImmediate(m);
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                if (_material != null)
                    Object.DestroyImmediate(_material);
                _material = null;
            };
        }

        private struct Part
        {
            public bool Head;
            public Vector3 Center, Euler, Scale;

            public Part(bool head, Vector3 center, Vector3 euler, Vector3 scale)
            {
                Head = head;
                Center = center;
                Euler = euler;
                Scale = scale;
            }
        }

        private static IEnumerable<Part> Mirrored(Vector3 center, Vector3 euler, Vector3 scale)
        {
            yield return new Part(false, center, euler, scale);
            yield return new Part(false, new Vector3(-center.x, center.y, center.z), euler, scale);
        }

        // Unity's capsule mesh is 2 m tall and 1 m wide, its sphere 1 m wide
        private static IEnumerable<Part> Parts(TarkovPlayerScale.Poses pose)
        {
            switch (pose)
            {
                case TarkovPlayerScale.Poses.Crouching:
                    yield return new Part(true, new Vector3(0f, 1.13f, 0.12f), Vector3.zero, Vector3.one * 0.23f);
                    yield return new Part(false, new Vector3(0f, 0.78f, 0.02f), new Vector3(20f, 0f, 0f), new Vector3(0.4f, 0.3f, 0.25f));
                    foreach (var p in Mirrored(new Vector3(0.11f, 0.5f, 0.17f), new Vector3(75f, 0f, 0f), new Vector3(0.15f, 0.24f, 0.15f)))
                        yield return p;
                    foreach (var p in Mirrored(new Vector3(0.11f, 0.24f, 0.36f), new Vector3(-10f, 0f, 0f), new Vector3(0.14f, 0.24f, 0.14f)))
                        yield return p;
                    foreach (var p in Mirrored(new Vector3(0.27f, 0.72f, 0.2f), new Vector3(50f, 0f, 0f), new Vector3(0.1f, 0.3f, 0.1f)))
                        yield return p;
                    break;
                case TarkovPlayerScale.Poses.Prone:
                    yield return new Part(true, new Vector3(0f, 0.2f, 0.78f), Vector3.zero, Vector3.one * 0.23f);
                    yield return new Part(false, new Vector3(0f, 0.15f, 0.3f), new Vector3(90f, 0f, 0f), new Vector3(0.4f, 0.33f, 0.25f));
                    foreach (var p in Mirrored(new Vector3(0.1f, 0.09f, -0.45f), new Vector3(90f, 0f, 0f), new Vector3(0.15f, 0.45f, 0.15f)))
                        yield return p;
                    foreach (var p in Mirrored(new Vector3(0.25f, 0.1f, 0.85f), new Vector3(90f, 0f, 0f), new Vector3(0.1f, 0.3f, 0.1f)))
                        yield return p;
                    break;
                default:
                    yield return new Part(true, new Vector3(0f, 1.685f, 0.02f), Vector3.zero, Vector3.one * 0.23f);
                    yield return new Part(false, new Vector3(0f, 1.25f, 0f), Vector3.zero, new Vector3(0.4f, 0.33f, 0.25f));
                    foreach (var p in Mirrored(new Vector3(0.1f, 0.47f, 0f), Vector3.zero, new Vector3(0.15f, 0.47f, 0.15f)))
                        yield return p;
                    foreach (var p in Mirrored(new Vector3(0.27f, 1.17f, 0f), Vector3.zero, new Vector3(0.1f, 0.34f, 0.1f)))
                        yield return p;
                    break;
            }
        }

        // gizmo: inside a DrawGizmo callback (shaded); else in the Scene view's repaint (flat, see-through)
        public static void Draw(Matrix4x4 at, TarkovPlayerScale.Poses pose, Color color, bool gizmo)
        {
            if (_capsule == null)
                _capsule = Resources.GetBuiltinResource<Mesh>("Capsule.fbx");
            if (_sphere == null)
                _sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
            if (!gizmo)
            {
                if (_material == null)
                {
                    _material = new Material(Shader.Find("Hidden/Internal-Colored")) { name = MaterialName, hideFlags = HideFlags.HideAndDontSave };
                    _material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    _material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    _material.SetInt("_ZWrite", 0);
                    _material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Back);
                    _material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
                }
                _material.SetColor("_Color", color);
                _material.SetPass(0);
            }
            else
                Gizmos.color = color;
            foreach (var part in Parts(pose))
            {
                var matrix = at * Matrix4x4.TRS(part.Center, Quaternion.Euler(part.Euler), part.Scale);
                var mesh = part.Head ? _sphere : _capsule;
                if (gizmo)
                {
                    Gizmos.matrix = matrix;
                    Gizmos.DrawMesh(mesh);
                }
                else
                    Graphics.DrawMeshNow(mesh, matrix);
            }
            if (gizmo)
                Gizmos.matrix = Matrix4x4.identity;
            // the CharacterController (none prone: the game lays a 0.3 m one)
            float capsule = pose == TarkovPlayerScale.Poses.Standing ? TarkovPlayerScaleWindow.StandCapsule
                : pose == TarkovPlayerScale.Poses.Crouching ? TarkovPlayerScaleWindow.CrouchCapsule : 0f;
            if (capsule > 0f)
                WireCapsule(at, capsule, TarkovPlayerScaleWindow.Radius, new Color(color.r, color.g, color.b, 0.6f));
        }

        private static void WireCapsule(Matrix4x4 at, float height, float radius, Color color)
        {
            using (new Handles.DrawingScope(color, at))
            {
                var bottom = Vector3.up * radius;
                var top = Vector3.up * (height - radius);
                Handles.DrawWireDisc(bottom, Vector3.up, radius);
                Handles.DrawWireDisc(top, Vector3.up, radius);
                foreach (var side in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
                    Handles.DrawLine(bottom + side * radius, top + side * radius);
                Handles.DrawWireArc(top, Vector3.forward, Vector3.right, 180f, radius);
                Handles.DrawWireArc(top, Vector3.right, Vector3.back, 180f, radius);
                Handles.DrawWireArc(bottom, Vector3.forward, Vector3.left, 180f, radius);
                Handles.DrawWireArc(bottom, Vector3.right, Vector3.forward, 180f, radius);
            }
        }

        // the heights the player can step, vault and climb, as bars in front of the figure
        public static void DrawLimits(Matrix4x4 at, TarkovPlayerScale.Poses pose)
        {
            using (new Handles.DrawingScope(at))
            {
                var front = new Vector3(0f, 0f, 0.7f);
                const float half = 0.35f;
                void Bar(float y, Color color, string label)
                {
                    Handles.color = color;
                    Handles.DrawAAPolyLine(3f, front + new Vector3(-half, y, 0f), front + new Vector3(half, y, 0f));
                    Handles.Label(front + new Vector3(half + 0.05f, y, 0f), label, EditorStyles.miniLabel);
                }
                Handles.DrawSolidRectangleWithOutline(new[]
                {
                    front + new Vector3(-half, TarkovPlayerScaleWindow.VaultMin, 0f), front + new Vector3(half, TarkovPlayerScaleWindow.VaultMin, 0f),
                    front + new Vector3(half, TarkovPlayerScaleWindow.VaultMax, 0f), front + new Vector3(-half, TarkovPlayerScaleWindow.VaultMax, 0f),
                }, new Color(1f, 0.85f, 0.2f, 0.12f), Color.clear);
                Bar(TarkovPlayerScaleWindow.Step, new Color(0.4f, 1f, 0.4f), "step 0.2 m");
                Bar(TarkovPlayerScaleWindow.VaultMin, new Color(1f, 0.85f, 0.2f), "vault from 0.25 m");
                Bar(TarkovPlayerScaleWindow.VaultMax, new Color(1f, 0.85f, 0.2f), "vault to 1.1 m");
                Bar(TarkovPlayerScaleWindow.ClimbMax, new Color(1f, 0.55f, 0.2f), "climb to 1.31 m");
                if (pose != TarkovPlayerScale.Poses.Prone)
                    Bar(pose == TarkovPlayerScale.Poses.Standing ? TarkovPlayerScaleWindow.StandCapsule : TarkovPlayerScaleWindow.CrouchCapsule, new Color(0.5f, 0.8f, 1f),
                        pose == TarkovPlayerScale.Poses.Standing ? "capsule 1.6 m" : "capsule 1.2 m");
                if (pose == TarkovPlayerScale.Poses.Standing)
                    Bar(TarkovPlayerScaleWindow.Height, Color.white, "head 1.8 m");
            }
        }
    }
}
