using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // The City Builder's hand tools (TarkovCityBuilder.cs has the Generate tab):
    //   Roads: draw roads by clicking points (a click on a junction's free arm joins it; that ends the road), extend the
    //          selected road, place junctions (one near a free road end lines up with it and joins it), edit the selected
    //          road or junction, Line This Road
    //   Place: a brush of the kit's prefabs; with Snap To Roads a building stands behind the nearest road's sidewalk
    //          facing it, a lamp, tree or prop goes on the sidewalk, a car parks at the kerb; without, the prefab stands
    //          where you click, turned with R / Shift+wheel. Placed pieces go under "City Pieces/<list>" in the active
    //          scene, with Undo.
    public partial class TarkovCityBuilderWindow
    {
        private enum Tab
        {
            Generate,
            Roads,
            Place,
        }

        private enum Mode
        {
            None,
            DrawRoad,
            Junction,
            Place,
        }

        private static readonly string[] TabNames = { "Generate", "Roads", "Place" };
        private static readonly string[] ListNames = { "Buildings", "Lamps", "Props", "Cars", "Trees", "Park" };

        private Tab _tab;
        private Mode _mode;
        private TarkovCityKit _kit;
        private bool _kitLooked;
        private bool _showToolKit;
        private UnityEditor.Editor _toolKitEditor;
        private UnityEditor.Editor _selectedEditor;
        private TarkovCityRoad _roadTemplate;
        private TarkovCityJunction _junctionTemplate;
        private SerializedObject _roadTemplateObject;
        private SerializedObject _junctionTemplateObject;
        private TarkovCityRoad _drawing;
        private float _junctionYaw;
        private Vector2 _toolScroll;
        private string _toolStatus;

        private int _list;
        private int _piece;
        private GameObject _anyPrefab;
        private TarkovCityKit.Facing _anyFront;
        private bool _snap = true;
        private bool _randomNext;
        private float _rotation;
        private float _setback;
        private List<Vector2[]> _existing;
        private double _existingTime;
        private bool _showCurve = true;
        private int _dragProfile = -1;
        private Vector2 _pointScroll;

        private void OnGUI()
        {
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                _tab = (Tab)GUILayout.Toolbar((int)_tab, TabNames, GUILayout.Height(24f));
                if (check.changed)
                {
                    EditorApplication.delayCall += StopTool;
                    SceneView.RepaintAll();
                }
            }
            if (_tab == Tab.Generate)
                DrawGenerate();
            else if (_tab == Tab.Roads)
                DrawRoads();
            else
                DrawPlace();
        }

        private void DisableTools()
        {
            TarkovCityRoads.Changed -= Repaint;
            StopTool();
            DestroyImmediate(_toolKitEditor);
            DestroyImmediate(_selectedEditor);
            if (_roadTemplate != null)
                DestroyImmediate(_roadTemplate.gameObject);
            if (_junctionTemplate != null)
                DestroyImmediate(_junctionTemplate.gameObject);
        }

        // ------------------------------------------------------------ shared

        private void KitField()
        {
            if (_kit == null && !_kitLooked)
            {
                _kit = TarkovCityBuilder.CurrentKit;
                _kitLooked = true;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (var check = new EditorGUI.ChangeCheckScope())
                {
                    _kit = (TarkovCityKit)EditorGUILayout.ObjectField(new GUIContent("Kit", "The materials and prefabs new roads, junctions and placed pieces take."), _kit, typeof(TarkovCityKit), false);
                    if (check.changed && _kit != null)
                        TarkovCityBuilder.CurrentKit = _kit;
                }
                if (_kit == null && GUILayout.Button("New Kit", GUILayout.Width(80f)))
                    EditorApplication.delayCall += () =>
                    {
                        _kit = TarkovCityBuilder.CreateKit();
                        Repaint();
                    };
            }
            if (_kit != null)
            {
                _showToolKit = EditorGUILayout.Foldout(_showToolKit, "Edit the kit", true);
                if (_showToolKit)
                {
                    UnityEditor.Editor.CreateCachedEditor(_kit, typeof(TarkovCityKitEditor), ref _toolKitEditor);
                    using (new EditorGUI.IndentLevelScope())
                        _toolKitEditor.OnInspectorGUI();
                    if (AssetPreview.IsLoadingAssetPreviews())
                        Repaint();
                }
            }
        }

        private void StopTool()
        {
            if (_mode == Mode.DrawRoad && _drawing != null)
            {
                if (_drawing.transform.childCount < 2)
                    Undo.DestroyObjectImmediate(_drawing.transform.parent.gameObject);
                else
                    Selection.activeGameObject = _drawing.transform.parent.gameObject;
                TarkovCityRoads.Forget();
            }
            _drawing = null;
            _mode = Mode.None;
            Repaint();
            SceneView.RepaintAll();
        }

        private void StartTool(Mode mode)
        {
            StopTool();
            _mode = mode;
            SceneView.lastActiveSceneView?.Focus();
        }

        private void SceneTools(SceneView view)
        {
            if (_mode == Mode.None)
                return;
            var e = Event.current;
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Escape || e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
            {
                StopTool();
                e.Use();
                return;
            }
            int id = GUIUtility.GetControlID(FocusType.Passive);
            if (e.type == EventType.Layout)
                HandleUtility.AddDefaultControl(id);
            if (e.type == EventType.MouseMove)
                SceneView.RepaintAll();
            // R turns, Shift + wheel turns finely
            float turn = 0f;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.R && !e.control && !e.alt)
                turn = e.shift ? -90f : 90f;
            if (e.type == EventType.ScrollWheel && e.shift)
                turn = Mathf.Sign(Mathf.Abs(e.delta.y) > Mathf.Abs(e.delta.x) ? e.delta.y : e.delta.x) * 15f;
            if (turn != 0f)
            {
                if (_mode == Mode.Junction)
                    _junctionYaw = Mathf.Repeat(_junctionYaw + turn, 360f);
                else
                    _rotation = Mathf.Repeat(_rotation + turn, 360f);
                e.Use();
                Repaint();
            }
            var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            if (_mode == Mode.DrawRoad)
                RoadTool(e, ray);
            else if (_mode == Mode.Junction)
                JunctionTool(e, ray);
            else if (_mode == Mode.Place)
                PlaceTool(e, ray);
        }

        private static bool Click(Event e) => e.type == EventType.MouseDown && e.button == 0 && !e.alt;

        // ------------------------------------------------------------ Roads tab

        private TarkovCityRoad RoadTemplate()
        {
            if (_roadTemplate == null)
            {
                var go = new GameObject("TarkovSDK_CityRoadTemplate") { hideFlags = HideFlags.HideAndDontSave };
                _roadTemplate = go.AddComponent<TarkovCityRoad>();
                _roadTemplateObject = null;
            }
            _roadTemplate.Kit = _kit;
            return _roadTemplate;
        }

        private TarkovCityJunction JunctionTemplate()
        {
            if (_junctionTemplate == null)
            {
                var go = new GameObject("TarkovSDK_CityJunctionTemplate") { hideFlags = HideFlags.HideAndDontSave };
                _junctionTemplate = go.AddComponent<TarkovCityJunction>();
                // arms open as roads join
                for (int arm = 0; arm < 4; arm++)
                    _junctionTemplate.SetOpen(arm, false);
                _junctionTemplateObject = null;
            }
            var road = RoadTemplate();
            _junctionTemplate.Kit = _kit;
            _junctionTemplate.Width = road.Width;
            _junctionTemplate.SidewalkWidth = Mathf.Max(0.5f, Mathf.Max(road.SidewalkLeft, road.SidewalkRight));
            _junctionTemplate.CurbHeight = road.CurbHeight;
            _junctionTemplate.CurbWidth = road.CurbWidth;
            return _junctionTemplate;
        }

        private void DrawRoads()
        {
            _toolScroll = EditorGUILayout.BeginScrollView(_toolScroll);
            EditorGUILayout.LabelField("Roads drawn by hand: click points in the Scene view; the road follows a smooth curve through them, with sidewalks, kerbs and lane lines. " +
                                       "Junctions join roads: a road that ends on a junction's arm meets its square and sidewalks.", EditorStyles.wordWrappedMiniLabel);
            KitField();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("New roads", EditorStyles.boldLabel);
            var template = _roadTemplateObject ?? (_roadTemplateObject = new SerializedObject(RoadTemplate()));
            template.Update();
            foreach (var field in new[] { "Width", "SidewalkLeft", "SidewalkRight", "CurbHeight", "CurbWidth", "LaneLines", "Avenue", "FollowGround", "Step" })
                EditorGUILayout.PropertyField(template.FindProperty(field));
            template.ApplyModifiedProperties();
            var junctionTemplate = _junctionTemplateObject ?? (_junctionTemplateObject = new SerializedObject(JunctionTemplate()));
            junctionTemplate.Update();
            EditorGUILayout.PropertyField(junctionTemplate.FindProperty("Crossings"), new GUIContent("Junction Crossings", "Zebra crossings on new junctions' open arms."));
            junctionTemplate.ApplyModifiedProperties();

            EditorGUILayout.Space();
            var selectedRoad = Selection.activeTransform != null ? TarkovCityRoads.RoadOf(Selection.activeTransform) : null;
            var selectedJunction = selectedRoad == null && Selection.activeTransform != null ? TarkovCityRoads.JunctionOf(Selection.activeTransform) : null;
            if (_mode == Mode.None)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("Draw A Road", "Click points in the Scene view; Esc or Enter ends the road. A click on a junction's free arm joins the road there."), GUILayout.Height(26f)))
                        StartTool(Mode.DrawRoad);
                    if (GUILayout.Button(new GUIContent("Place Junctions", "Click to place a junction (R turns it). Near a free road end it lines up with the road and joins it."), GUILayout.Height(26f)))
                        StartTool(Mode.Junction);
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(selectedRoad == null || selectedRoad.EndJunction != null || selectedRoad.Loop))
                        if (GUILayout.Button(new GUIContent("Extend The Selected Road", "More points after its last one.")))
                        {
                            StartTool(Mode.DrawRoad);
                            _drawing = selectedRoad;
                        }
                    if (GUILayout.Button(new GUIContent("Intersect Crossing Roads", "A crossroads wherever two roads cross at about the same height (more than 3 m apart is a bridge): " +
                                                                                    "both are cut there and joined to it.")))
                        EditorApplication.delayCall += () =>
                        {
                            var notes = new List<string>();
                            int made = TarkovCityRoads.IntersectCrossings(notes);
                            _toolStatus = (made == 0 && notes.Count == 0 ? "No roads cross." : $"{made} intersection(s) made.") +
                                          (notes.Count > 0 ? " Not made: " + string.Join(" ", notes.Distinct()) : "");
                            Repaint();
                        };
                }
            }
            else
            {
                string what = _mode == Mode.DrawRoad
                    ? (_drawing == null ? "Click the road's first point." : $"Click the next point ({_drawing.transform.childCount} so far). A free junction arm (green) ends the road there.")
                    : _mode == Mode.Junction ? $"Click to place a junction, turned {_junctionYaw:0}° (R / Shift+wheel). Near a free road end it joins it; on a road it cuts it into an intersection."
                    : "";
                EditorGUILayout.HelpBox(what + " Esc or Enter: done. Alt-drag still moves the view.", MessageType.Info);
                if (GUILayout.Button("Done", GUILayout.Height(26f)))
                    StopTool();
            }

            EditorGUILayout.Space();
            if (selectedRoad != null)
            {
                EditorGUILayout.LabelField($"Selected road: {selectedRoad.transform.parent.name} ({TarkovCityRoads.Length(TarkovCityRoads.Frames(selectedRoad)):0} m)", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Drag its points in the Scene view (they follow the ground); Ctrl-click on the road adds a point; delete a point object to remove it.",
                    EditorStyles.wordWrappedMiniLabel);
                TarkovCityRoads.Changed -= Repaint;
                TarkovCityRoads.Changed += Repaint;
                _showCurve = EditorGUILayout.Foldout(_showCurve, "Curve editor", true, EditorStyles.foldoutHeader);
                if (_showCurve)
                    DrawCurveEditor(selectedRoad);
                UnityEditor.Editor.CreateCachedEditor(selectedRoad, null, ref _selectedEditor);
                _selectedEditor.OnInspectorGUI();
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("Line This Road", "Buildings, lamps, trees, props and parked cars from the road's kit along its sides, by its Roadside settings. " +
                                                                          "Replaces what it placed before (Undo puts it back).")))
                    {
                        var road = selectedRoad;
                        EditorApplication.delayCall += () =>
                        {
                            _toolStatus = TarkovCityRoads.Line(road);
                            Repaint();
                        };
                    }
                    if (GUILayout.Button(new GUIContent("Clear Roadside", "Removes what Line This Road placed.")))
                    {
                        var group = selectedRoad.transform.parent.Find("Roadside");
                        if (group != null)
                            EditorApplication.delayCall += () => Undo.DestroyObjectImmediate(group.gameObject);
                    }
                    if (GUILayout.Button("Rebuild", GUILayout.Width(70f)))
                    {
                        var road = selectedRoad;
                        EditorApplication.delayCall += () => TarkovCityRoads.Rebuild(road);
                    }
                }
            }
            else if (selectedJunction != null)
            {
                EditorGUILayout.LabelField($"Selected junction: {selectedJunction.transform.parent.name}", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Move and turn its object to move it; roads joined to it follow. Arms: N is the object's forward (+Z).", EditorStyles.wordWrappedMiniLabel);
                UnityEditor.Editor.CreateCachedEditor(selectedJunction, null, ref _selectedEditor);
                _selectedEditor.OnInspectorGUI();
                if (GUILayout.Button(new GUIContent("Fit To Ground", "Tilts and raises the junction to the ground under its corners, so a slope doesn't bury its uphill side. Junctions placed with Follow Ground on are fitted already.")))
                {
                    var junction = selectedJunction;
                    EditorApplication.delayCall += () =>
                    {
                        _toolStatus = TarkovCityRoads.FitToGround(junction) ? "Fitted to the ground." : "No ground found under its corners.";
                        Repaint();
                    };
                }
            }
            else
                EditorGUILayout.LabelField("Select a road or junction to edit it.", EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(_toolStatus))
                EditorGUILayout.HelpBox(_toolStatus, MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        private void RoadTool(Event e, Ray ray)
        {
            Vector3 point;
            float refY = _drawing != null && _drawing.transform.childCount > 0 ? _drawing.transform.GetChild(_drawing.transform.childCount - 1).position.y : 0f;
            if (TarkovCityRoads.Ground(ray, 5000f, false, out var hit))
                point = hit.point;
            else if (new Plane(Vector3.up, new Vector3(0f, refY, 0f)).Raycast(ray, out float d))
                point = ray.GetPoint(d);
            else
                return;
            bool snapped = TarkovCityRoads.NearestFreeArm(point, 4f, out var junction, out int arm);
            if (snapped && _drawing != null && (_drawing.StartJunction == junction && _drawing.StartArm == arm))
                snapped = false;
            if (snapped)
                point = TarkovCityRoads.Anchor(junction, arm);
            // on another road's middle: a T intersection there
            var cross = RoadUnder(point, _drawing, out float crossAt);
            if (snapped)
                cross = null;
            if (cross != null)
            {
                DrawIntersectionPreview(cross, crossAt);
                Handles.Label(point + Vector3.up * 1.5f, "Click: a T intersection on this road, joined on this side");
                if (!Click(e))
                    return;
                var made = TarkovCityRoads.AddIntersection(cross, crossAt, out string error);
                if (made == null)
                {
                    _toolStatus = error;
                    e.Use();
                    Repaint();
                    return;
                }
                // the arm on the side the new road comes from (its last point, or the side of the road clicked)
                var reference = _drawing != null && _drawing.transform.childCount > 0 ? _drawing.transform.GetChild(_drawing.transform.childCount - 1).position : point;
                int side = Vector3.Dot(reference - made.transform.parent.position, TarkovCityRoads.ArmDirection(made, 1)) >= 0f ? 1 : 3;
                var anchor = TarkovCityRoads.Anchor(made, side);
                if (_drawing == null)
                {
                    _drawing = TarkovCityRoads.CreateRoad(anchor, RoadTemplate());
                    TarkovCityRoads.Attach(_drawing, true, made, side);
                }
                else
                {
                    TarkovCityRoads.AddPoint(_drawing, anchor, -1);
                    TarkovCityRoads.Attach(_drawing, false, made, side);
                    TarkovCityRoads.Rebuild(_drawing);
                    StopTool();
                }
                e.Use();
                Repaint();
                return;
            }
            var width = _drawing != null ? _drawing.Width : RoadTemplate().Width;
            Handles.color = snapped ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.85f, 0.2f);
            Handles.DrawWireDisc(point, Vector3.up, width / 2f);
            if (_drawing != null && _drawing.transform.childCount > 0)
                Handles.DrawDottedLine(_drawing.transform.GetChild(_drawing.transform.childCount - 1).position, point, 4f);
            Handles.Label(point + Vector3.up * 1.5f, snapped ? "Click: join the junction here (ends the road)" : _drawing == null ? "Click: the road's first point" : "Click: next point (Esc: done)");
            if (!Click(e))
                return;
            if (_drawing == null)
            {
                _drawing = TarkovCityRoads.CreateRoad(point, RoadTemplate());
                if (snapped)
                    TarkovCityRoads.Attach(_drawing, true, junction, arm);
            }
            else
            {
                TarkovCityRoads.AddPoint(_drawing, point, -1);
                if (snapped)
                {
                    TarkovCityRoads.Attach(_drawing, false, junction, arm);
                    TarkovCityRoads.Rebuild(_drawing);
                    StopTool();
                    e.Use();
                    return;
                }
                TarkovCityRoads.Rebuild(_drawing);
            }
            e.Use();
            Repaint();
        }

        private void JunctionTool(Event e, Ray ray)
        {
            if (!TarkovCityRoads.Ground(ray, 5000f, false, out var hit))
                return;
            // on a road's middle: an intersection cutting it
            var onRoad = RoadUnder(hit.point, null, out float roadAt);
            if (onRoad != null)
            {
                DrawIntersectionPreview(onRoad, roadAt);
                Handles.Label(hit.point + Vector3.up * 2f, "Click: an intersection on this road");
                if (Click(e))
                {
                    if (TarkovCityRoads.AddIntersection(onRoad, roadAt, out string error) == null)
                        _toolStatus = error;
                    e.Use();
                    Repaint();
                }
                return;
            }
            var template = JunctionTemplate();
            var centre = hit.point;
            float yaw = _junctionYaw;
            // near a free road end: lined up with it, its south arm on the end
            TarkovCityRoad joinRoad = null;
            bool joinStart = false;
            float best = 6f;
            foreach (var road in TarkovCityRoads.Roads)
            {
                var frames = TarkovCityRoads.Frames(road);
                if (frames.Count < 2 || road.Loop)
                    continue;
                float reach = road.Width / 2f + Mathf.Max(0.5f, Mathf.Max(road.SidewalkLeft, road.SidewalkRight));
                foreach (bool start in new[] { true, false })
                {
                    if (start ? road.StartJunction != null : road.EndJunction != null)
                        continue;
                    var end = start ? frames[0] : frames[frames.Count - 1];
                    var outward = start ? -end.Forward : end.Forward;
                    var c = end.Position + outward * reach;
                    float d = Vector2.Distance(new Vector2(c.x, c.z), new Vector2(hit.point.x, hit.point.z));
                    if (d < best)
                    {
                        best = d;
                        joinRoad = road;
                        joinStart = start;
                        centre = c;
                        yaw = Mathf.Atan2(outward.x, outward.z) * Mathf.Rad2Deg;
                    }
                }
            }
            float w = joinRoad != null ? joinRoad.Width : template.Width;
            float s = joinRoad != null ? Mathf.Max(0.5f, Mathf.Max(joinRoad.SidewalkLeft, joinRoad.SidewalkRight)) : template.SidewalkWidth;
            float r = w / 2f + s;
            var rotation = Quaternion.Euler(0f, yaw, 0f);
            Handles.color = joinRoad != null ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.85f, 0.2f);
            var corners = new[] { new Vector3(-r, 0, -r), new Vector3(-r, 0, r), new Vector3(r, 0, r), new Vector3(r, 0, -r), new Vector3(-r, 0, -r) }
                .Select(c => centre + rotation * c + Vector3.up * 0.2f).ToArray();
            Handles.DrawAAPolyLine(3f, corners);
            Handles.DrawAAPolyLine(2f, centre + Vector3.up * 0.2f, centre + rotation * Vector3.forward * r + Vector3.up * 0.2f);
            Handles.Label(centre + rotation * Vector3.forward * r + Vector3.up, "N");
            Handles.Label(centre + Vector3.up * 2f, joinRoad != null ? "Click: a junction joined to this road end" : "Click: place a junction (R: turn)");
            if (!Click(e))
                return;
            var like = Object.Instantiate(template);
            like.Width = w;
            like.SidewalkWidth = s;
            if (joinRoad != null)
            {
                like.CurbHeight = joinRoad.CurbHeight;
                like.CurbWidth = joinRoad.CurbWidth;
            }
            var junction = TarkovCityRoads.CreateJunction(centre, yaw, like);
            DestroyImmediate(like.gameObject);
            // flat on a slope, its uphill side would sink into the ground
            if (RoadTemplate().FollowGround)
                TarkovCityRoads.FitToGround(junction);
            if (joinRoad != null)
            {
                TarkovCityRoads.Attach(joinRoad, joinStart, junction, 2);
                TarkovCityRoads.Rebuild(junction);
                TarkovCityRoads.Rebuild(joinRoad);
            }
            e.Use();
        }

        // ------------------------------------------------------------ Place tab

        private List<TarkovCityKit.Piece> ListPieces()
        {
            if (_kit == null)
                return new List<TarkovCityKit.Piece>();
            var field = typeof(TarkovCityKit).GetField(TarkovCityBuilder.PieceSlots[_list].Field);
            return TarkovCityRoads.Usable(field.GetValue(_kit) as List<TarkovCityKit.Piece>);
        }

        private TarkovCityKit.Piece CurrentPiece()
        {
            if (_anyPrefab != null)
                return new TarkovCityKit.Piece { Prefab = _anyPrefab, Front = _anyFront };
            var pieces = ListPieces();
            return pieces.Count > 0 ? pieces[Mathf.Clamp(_piece, 0, pieces.Count - 1)] : null;
        }

        private void DrawPlace()
        {
            _toolScroll = EditorGUILayout.BeginScrollView(_toolScroll);
            EditorGUILayout.LabelField("Place the kit's prefabs by hand. With Snap To Roads they line up with the nearest hand-drawn road: buildings behind its sidewalk facing it, " +
                                       "lamps, trees and props on the sidewalk, cars parked at the kerb.", EditorStyles.wordWrappedMiniLabel);
            KitField();
            EditorGUILayout.Space();
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                _list = GUILayout.Toolbar(_list, ListNames);
                if (check.changed)
                    _piece = 0;
            }
            var pieces = ListPieces();
            if (pieces.Count == 0)
                EditorGUILayout.HelpBox(_kit == null ? "Pick or make a kit." : $"The kit has no {TarkovCityBuilder.PieceSlots[_list].Label.ToLowerInvariant()}: add some under Edit the kit, " +
                                                                               "or with the Asset Importer's Add To City Kit.", MessageType.None);
            else
            {
                const float cell = 76f;
                int columns = Mathf.Max(1, Mathf.FloorToInt((position.width - 24f) / (cell + 4f)));
                for (int i = 0; i < pieces.Count; i += columns)
                    using (new EditorGUILayout.HorizontalScope())
                        for (int k = i; k < Mathf.Min(i + columns, pieces.Count); k++)
                        {
                            var prefab = pieces[k].Prefab;
                            var image = AssetPreview.GetAssetPreview(prefab) ?? AssetPreview.GetMiniThumbnail(prefab);
                            var size = TarkovCityBuilder.LocalBounds(prefab).size;
                            var color = GUI.backgroundColor;
                            if (k == _piece && _anyPrefab == null)
                                GUI.backgroundColor = new Color(0.4f, 0.8f, 1f);
                            if (GUILayout.Button(new GUIContent(image, $"{prefab.name}\n{size.x:0.#} x {size.z:0.#} m, {size.y:0.#} m high"), GUILayout.Width(cell), GUILayout.Height(cell)))
                            {
                                _piece = k;
                                _anyPrefab = null;
                                if (_mode != Mode.Place)
                                    StartTool(Mode.Place);
                            }
                            GUI.backgroundColor = color;
                        }
                if (AssetPreview.IsLoadingAssetPreviews())
                    Repaint();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                _anyPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Or Any Prefab", "Place a prefab that isn't in the kit."), _anyPrefab, typeof(GameObject), false);
                using (new EditorGUI.DisabledScope(_anyPrefab == null))
                    _anyFront = (TarkovCityKit.Facing)EditorGUILayout.EnumPopup(_anyFront, GUILayout.Width(100f));
            }
            EditorGUILayout.Space();
            _snap = EditorGUILayout.ToggleLeft(new GUIContent("Snap To Roads", "Line up with the nearest hand-drawn road (within 30 m of its sidewalk). Hold Ctrl to place freely."), _snap);
            using (new EditorGUI.DisabledScope(!_snap || _list != 0))
                _setback = EditorGUILayout.Slider(new GUIContent("Building Setback", "Metres between the sidewalk and a snapped building's front."), _setback, 0f, 10f);
            _randomNext = EditorGUILayout.ToggleLeft(new GUIContent("Random Piece After Each", "After each placement, the next piece is drawn from the list by weight."), _randomNext);
            EditorGUILayout.LabelField($"Turned {_rotation:0}° when placed freely (R: 90°, Shift+wheel: 15°)", EditorStyles.miniLabel);
            EditorGUILayout.Space();
            if (_mode != Mode.Place)
            {
                using (new EditorGUI.DisabledScope(CurrentPiece() == null))
                    if (GUILayout.Button("Place (click in the Scene view)", GUILayout.Height(26f)))
                        StartTool(Mode.Place);
            }
            else
            {
                EditorGUILayout.HelpBox("Click to place. Red: it would overlap a building or a road. Ctrl: don't snap. Esc: done.", MessageType.Info);
                if (GUILayout.Button("Done", GUILayout.Height(26f)))
                    StopTool();
            }
            if (!string.IsNullOrEmpty(_toolStatus))
                EditorGUILayout.HelpBox(_toolStatus, MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        private void PlaceTool(Event e, Ray ray)
        {
            var piece = CurrentPiece();
            if (piece == null || !TarkovCityRoads.Ground(ray, 5000f, true, out var hit))
                return;
            var local = TarkovCityBuilder.LocalBounds(piece.Prefab);
            var kind = _anyPrefab != null ? TarkovCityRoads.Kind.Building : TarkovCityRoads.KindOf(TarkovCityBuilder.PieceSlots[_list].Field);
            TarkovCityRoads.Pose pose = default;
            bool snapped = false;
            if (_snap && !(e.control || e.command))
            {
                float best = float.MaxValue;
                foreach (var road in TarkovCityRoads.Roads)
                {
                    var frames = TarkovCityRoads.Frames(road);
                    if (frames.Count < 2)
                        continue;
                    var f = TarkovCityRoads.Nearest(frames, hit.point, out float d);
                    if (d > road.Width / 2f + Mathf.Max(road.SidewalkLeft, road.SidewalkRight) + 30f || d >= best)
                        continue;
                    best = d;
                    int side = Vector3.Dot(hit.point - f.Position, f.Right) >= 0f ? 1 : -1;
                    pose = TarkovCityRoads.RoadsidePose(road, frames, f.Distance, side, piece, kind, _setback);
                    snapped = true;
                }
            }
            if (!snapped)
            {
                var position = hit.point + Vector3.up * piece.Height;
                pose = new TarkovCityRoads.Pose { Position = position, Yaw = _rotation, Corners = TarkovCityRoads.Corners(position, _rotation, local) };
            }
            bool clear = true;
            if (kind == TarkovCityRoads.Kind.Building)
            {
                if (_existing == null || EditorApplication.timeSinceStartup > _existingTime + 1.0)
                {
                    _existing = TarkovCityRoads.ExistingBuildings(null);
                    _existingTime = EditorApplication.timeSinceStartup;
                }
                clear = !_existing.Any(t => TarkovCityRoads.Overlap(t, pose.Corners)) && TarkovCityRoads.OffRoads(pose.Corners);
            }
            var rotation = Quaternion.Euler(0f, pose.Yaw, 0f);
            using (new Handles.DrawingScope(clear ? new Color(0.3f, 0.9f, 1f) : new Color(1f, 0.3f, 0.3f), Matrix4x4.TRS(pose.Position, rotation, Vector3.one)))
            {
                Handles.DrawWireCube(local.center, local.size);
                var front = Quaternion.Euler(0f, (int)piece.Front * 90f, 0f) * Vector3.forward;
                var from = new Vector3(local.center.x, local.min.y + 0.2f, local.center.z);
                float reach = Mathf.Abs(Vector3.Dot(local.extents, new Vector3(Mathf.Abs(front.x), 0f, Mathf.Abs(front.z))));
                Handles.DrawAAPolyLine(4f, from, from + front * (reach + 2f));
                Handles.ConeHandleCap(0, from + front * (reach + 2f), Quaternion.LookRotation(front), 0.8f, EventType.Repaint);
            }
            Handles.Label(pose.Position + Vector3.up * (local.max.y + 1f), $"{piece.Prefab.name}{(snapped ? " (on the road)" : "")}");
            if (!Click(e))
                return;
            var instance = TarkovCityRoads.Instantiate(piece, pose, PiecesGroup(TarkovCityBuilder.PieceSlots[_anyPrefab != null ? 0 : _list].Label));
            Undo.RegisterCreatedObjectUndo(instance, "Place " + piece.Prefab.name);
            _existing = null;
            if (_randomNext && _anyPrefab == null)
            {
                var pieces = ListPieces();
                var next = TarkovCityRoads.Pick(pieces, new System.Random());
                _piece = Mathf.Max(0, pieces.IndexOf(next));
                Repaint();
            }
            e.Use();
        }

        // City Pieces/<group> in the active scene, made when missing
        private static Transform PiecesGroup(string name)
        {
            var scene = SceneManager.GetActiveScene();
            var root = scene.GetRootGameObjects().FirstOrDefault(g => g.name == TarkovCityRoads.PiecesRoot);
            if (root == null)
            {
                root = new GameObject(TarkovCityRoads.PiecesRoot);
                Undo.RegisterCreatedObjectUndo(root, "Create " + TarkovCityRoads.PiecesRoot);
            }
            var group = root.transform.Find(name);
            if (group == null)
            {
                group = new GameObject(name).transform;
                group.SetParent(root.transform, false);
                Undo.RegisterCreatedObjectUndo(group.gameObject, "Create " + name);
            }
            return group;
        }

        // ------------------------------------------------------------ intersections and the curve editor

        // the road whose surface is under the point (not `except`), and how far along it
        private static TarkovCityRoad RoadUnder(Vector3 point, TarkovCityRoad except, out float along)
        {
            along = 0f;
            foreach (var road in TarkovCityRoads.Roads)
            {
                if (road == except)
                    continue;
                var frames = TarkovCityRoads.Frames(road);
                if (frames.Count < 2)
                    continue;
                var f = TarkovCityRoads.Nearest(frames, point, out float d);
                if (d < road.Width / 2f)
                {
                    along = f.Distance;
                    return road;
                }
            }
            return null;
        }

        // the junction square an intersection would make there: green, or red when the road is too short there
        private static void DrawIntersectionPreview(TarkovCityRoad road, float along)
        {
            var f = TarkovCityRoads.At(TarkovCityRoads.Frames(road), along);
            float r = TarkovCityRoads.JunctionReach(road);
            bool ok = TarkovCityRoads.Splittable(road, along, r + 2f) == null;
            Handles.color = ok ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.3f, 0.3f);
            var rotation = Quaternion.LookRotation(f.Forward);
            var corners = new[] { new Vector3(-r, 0, -r), new Vector3(-r, 0, r), new Vector3(r, 0, r), new Vector3(r, 0, -r), new Vector3(-r, 0, -r) }
                .Select(c => f.Position + rotation * c + Vector3.up * 0.3f).ToArray();
            Handles.DrawAAPolyLine(3f, corners);
        }

        // the road's height along it (yellow; grey without the points' raise) with its points to drag up and down, and
        // each point's mode and raise
        private void DrawCurveEditor(TarkovCityRoad road)
        {
            var frames = TarkovCityRoads.Frames(road);
            int count = road.transform.childCount;
            EditorGUILayout.LabelField("In the Scene view drag a point's blue handles to shape the curve, right-click a point for its mode. " +
                                       "Below: drag a point up or down to raise or lower the road there.", EditorStyles.wordWrappedMiniLabel);
            if (frames.Count >= 2)
                DrawProfile(road, frames);
            using (new EditorGUILayout.HorizontalScope())
            {
                foreach (TarkovCityRoadPoint.Modes mode in System.Enum.GetValues(typeof(TarkovCityRoadPoint.Modes)))
                {
                    if (mode == TarkovCityRoadPoint.Modes.Free)
                        continue;
                    if (GUILayout.Button(new GUIContent("All " + mode, $"Every point {mode}."), EditorStyles.miniButton))
                    {
                        var m = mode;
                        EditorApplication.delayCall += () =>
                        {
                            for (int i = 0; i < road.transform.childCount; i++)
                                TarkovCityRoads.SetMode(road, i, m);
                        };
                    }
                }
            }
            _pointScroll = EditorGUILayout.BeginScrollView(_pointScroll, GUILayout.MaxHeight(Mathf.Min(count, 6) * 22f + 6f));
            for (int i = 0; i < count; i++)
            {
                var point = road.transform.GetChild(i);
                bool pinned = i == 0 && road.StartJunction != null || i == count - 1 && road.EndJunction != null;
                using (new EditorGUILayout.HorizontalScope())
                {
                    var style = i == TarkovCityRoads.PickedPoint ? EditorStyles.miniButtonMid : EditorStyles.miniButton;
                    if (GUILayout.Button(pinned ? $"{i + 1} (junction)" : (i + 1).ToString(), style, GUILayout.Width(84f)))
                    {
                        TarkovCityRoads.PickedPoint = i;
                        SceneView.lastActiveSceneView?.LookAt(point.position);
                        SceneView.RepaintAll();
                    }
                    using (new EditorGUI.DisabledScope(pinned))
                    {
                        var mode = TarkovCityRoads.ModeOf(point);
                        var picked = (TarkovCityRoadPoint.Modes)EditorGUILayout.EnumPopup(mode, GUILayout.Width(80f));
                        if (picked != mode)
                        {
                            int index = i;
                            EditorApplication.delayCall += () => TarkovCityRoads.SetMode(road, index, picked);
                        }
                    }
                    var data = point.GetComponent<TarkovCityRoadPoint>();
                    float raise = data != null ? data.Raise : 0f;
                    float labelWidth = EditorGUIUtility.labelWidth;
                    EditorGUIUtility.labelWidth = 40f;
                    float changed = EditorGUILayout.FloatField(new GUIContent("Raise", "Metres up (or down) from where the road would lie here."), raise);
                    EditorGUIUtility.labelWidth = labelWidth;
                    if (!Mathf.Approximately(changed, raise))
                        TarkovCityRoads.SetRaise(road, i, changed);
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawProfile(TarkovCityRoad road, List<TarkovCityRoads.Frame> frames)
        {
            var rect = GUILayoutUtility.GetRect(10f, 130f, GUILayout.ExpandWidth(true));
            var e = Event.current;
            float length = TarkovCityRoads.Length(frames);
            float min = frames.Min(f => Mathf.Min(f.Position.y, f.Position.y - f.Raise)), max = frames.Max(f => Mathf.Max(f.Position.y, f.Position.y - f.Raise));
            float pad = Mathf.Max(1f, (max - min) * 0.15f);
            min -= pad;
            max += pad;
            var inner = new Rect(rect.x + 34f, rect.y + 8f, rect.width - 42f, rect.height - 22f);
            Vector3 G(float d, float y) => new Vector3(inner.x + inner.width * d / Mathf.Max(0.01f, length), inner.yMax - inner.height * (y - min) / (max - min), 0f);
            int id = GUIUtility.GetControlID(FocusType.Passive);
            if (e.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(rect, new Color(0.13f, 0.13f, 0.13f));
                Handles.color = new Color(1f, 1f, 1f, 0.08f);
                for (int k = 0; k <= 4; k++)
                {
                    float y = inner.y + inner.height * k / 4f;
                    Handles.DrawLine(new Vector3(inner.x, y), new Vector3(inner.xMax, y));
                }
                Handles.color = new Color(0.6f, 0.6f, 0.6f, 0.7f);
                Handles.DrawAAPolyLine(2f, frames.Select(f => G(f.Distance, f.Position.y - f.Raise)).ToArray());
                Handles.color = new Color(1f, 0.85f, 0.2f);
                Handles.DrawAAPolyLine(3f, frames.Select(f => G(f.Distance, f.Position.y)).ToArray());
                GUI.Label(new Rect(rect.x + 2f, inner.y - 7f, 32f, 14f), $"{max:0.#}", EditorStyles.miniLabel);
                GUI.Label(new Rect(rect.x + 2f, inner.yMax - 7f, 32f, 14f), $"{min:0.#}", EditorStyles.miniLabel);
                GUI.Label(new Rect(inner.xMax - 60f, inner.yMax + 1f, 60f, 14f), $"{length:0} m", EditorStyles.miniLabel);
            }
            foreach (var f in frames.Where(f => f.Point >= 0))
            {
                var at = G(f.Distance, f.Position.y);
                var marker = new Rect(at.x - 5f, at.y - 5f, 10f, 10f);
                bool pinned = f.Point == 0 && road.StartJunction != null || f.Point == road.transform.childCount - 1 && road.EndJunction != null;
                if (e.type == EventType.Repaint)
                    EditorGUI.DrawRect(marker, pinned ? new Color(0.3f, 1f, 0.4f) : f.Point == TarkovCityRoads.PickedPoint ? Color.white : new Color(1f, 0.85f, 0.2f));
                if (!pinned && e.type == EventType.MouseDown && e.button == 0 && marker.Contains(e.mousePosition))
                {
                    _dragProfile = f.Point;
                    TarkovCityRoads.PickedPoint = f.Point;
                    GUIUtility.hotControl = id;
                    e.Use();
                    SceneView.RepaintAll();
                }
            }
            if (_dragProfile >= 0 && GUIUtility.hotControl == id)
            {
                if (e.type == EventType.MouseDrag && _dragProfile < road.transform.childCount)
                {
                    var data = road.transform.GetChild(_dragProfile).GetComponent<TarkovCityRoadPoint>();
                    float raise = data != null ? data.Raise : 0f;
                    TarkovCityRoads.SetRaise(road, _dragProfile, raise - e.delta.y * (max - min) / inner.height);
                    e.Use();
                    Repaint();
                }
                else if (e.type == EventType.MouseUp)
                {
                    _dragProfile = -1;
                    GUIUtility.hotControl = 0;
                    e.Use();
                }
            }
        }
    }
}
