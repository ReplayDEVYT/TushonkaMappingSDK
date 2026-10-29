using System;
using System.Collections.Generic;
using System.Linq;
using BezierSplineTools;
using EFT.Vehicle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Tarkov SDK > Tools > World Events > BTR Path Editor: the map's BTR route in one place.
    //  - Stops & Routes: the BTR's timing, the stops (placed by clicking in the Scene view), the routes
    //  - Roads: every road a route needs, made drive-through or shortest, and edited point by point in the Scene view
    //    (move points and their handles, insert, delete, smooth, snap to the ground, keep off walls)
    //  - Preview: the BTR driving a route as the game does (TarkovBtrSim), with its three-point turns, sideways slides
    //    and dead ends marked; Check Routes drives them all at once and lists what happens where
    public class TarkovBtrEditorWindow : EditorWindow
    {
        private enum Tab { StopsAndRoutes, Roads, Preview }

        private static readonly string[] TabNames = { "Stops & Routes", "Roads", "Preview" };
        private static readonly Color StopColor = new Color(0.3f, 0.6f, 1f);
        private static readonly Color RouteColor = new Color(0.35f, 0.9f, 0.45f);
        private static readonly Color TurnColor = new Color(1f, 0.6f, 0.2f);
        private static readonly Color SlideColor = new Color(1f, 0.25f, 0.25f);
        private static readonly Color ProblemColor = new Color(1f, 0.2f, 0.9f);

        private MapPathConfig _config;
        private Tab _tab;
        private Vector2 _scroll;
        private bool _placing;
        private bool _driveThrough = true;
        private bool _replaceEdited;
        private bool _snapToGround = true;

        // the road being edited and its selected anchor (point index / 3)
        private PathSpline _road;
        private int _anchor = -1;

        // preview
        private int _routeIndex;
        private TarkovBtrSim _sim;
        private bool _playing;
        private float _speed = 4f;
        private float _pause = 2f;
        private double _lastTime;
        private float _carry;
        private Dictionary<string, List<TarkovBtrSim.Event>> _checks;

        [MenuItem("Tarkov SDK/Tools/World Events/BTR Path Editor...", priority = 119)]
        private static void Menu() => Open(TarkovBtr.FindInMap(SceneManager.GetActiveScene()));

        public static void Open(MapPathConfig config)
        {
            var window = GetWindow<TarkovBtrEditorWindow>("BTR Path Editor");
            window.minSize = new Vector2(380f, 420f);
            if (config != null)
                window._config = config;
            window.Show();
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += Step;
            Undo.undoRedoPerformed += Repaint;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= Step;
            Undo.undoRedoPerformed -= Repaint;
            _playing = false;
            SceneView.RepaintAll();
        }

        private void OnSelectionChange()
        {
            // a road or stop picked in the hierarchy is edited here
            var go = Selection.activeGameObject;
            if (go == null)
                return;
            var config = go.GetComponentInParent<MapPathConfig>();
            if (config == null)
                return;
            _config = config;
            var road = go.GetComponent<PathSpline>();
            if (road != null && road != _road)
            {
                _road = road;
                _anchor = -1;
                _tab = Tab.Roads;
            }
            Repaint();
        }

        private void OnGUI()
        {
            if (_config == null)
                _config = TarkovBtr.FindInMap(SceneManager.GetActiveScene());
            if (_config == null)
            {
                EditorGUILayout.HelpBox("The active scene has no BTR route.", MessageType.Info);
                if (GUILayout.Button("Add BTR Route At Scene View Centre"))
                    EditorApplication.delayCall += () =>
                    {
                        var config = TarkovBtr.Create(null);
                        Undo.RegisterCreatedObjectUndo(config.gameObject, "BTR route");
                        _config = config;
                        EditorSceneManager.MarkSceneDirty(config.gameObject.scene);
                    };
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.ObjectField(_config, typeof(MapPathConfig), true);
                if (GUILayout.Button("Select", GUILayout.Width(52f)))
                    Selection.activeGameObject = _config.gameObject;
            }
            _tab = (Tab)GUILayout.Toolbar((int)_tab, TabNames);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            switch (_tab)
            {
                case Tab.StopsAndRoutes:
                    DrawStops();
                    break;
                case Tab.Roads:
                    DrawRoads();
                    break;
                case Tab.Preview:
                    DrawPreview();
                    break;
            }
            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------ stops and routes

        private void DrawStops()
        {
            var settings = TarkovBtr.Settings(_config, false);
            if (settings != null)
            {
                var so = new SerializedObject(settings);
                EditorGUILayout.PropertyField(so.FindProperty("ChanceSpawn"), new GUIContent("Chance (%)"));
                EditorGUILayout.PropertyField(so.FindProperty("SpawnPeriod"), new GUIContent("Arrives After (s)"));
                EditorGUILayout.PropertyField(so.FindProperty("MoveSpeed"));
                EditorGUILayout.PropertyField(so.FindProperty("PauseDuration"), new GUIContent("Waits At Stops (s)"));
                EditorGUILayout.PropertyField(so.FindProperty("Skin"));
                so.ApplyModifiedProperties();
            }

            EditorGUILayout.Space();
            var stops = TarkovBtr.Stops(_config);
            EditorGUILayout.LabelField($"Stops ({stops.Count})", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Where it pauses (and where the taxi goes). A stop's arrow is the way the BTR faces there: it appears at a route's " +
                                       "enter point facing it, and drive-through roads leave and enter each stop along it.", EditorStyles.wordWrappedMiniLabel);
            foreach (var stop in stops)
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(stop.id, GUILayout.Width(36f));
                    var entry = settings?.Stops.FirstOrDefault(s => s.Id == stop.id);
                    if (entry != null)
                    {
                        EditorGUI.BeginChangeCheck();
                        var name = EditorGUILayout.TextField(entry.Name);
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(settings, "Stop name");
                            entry.Name = name;
                            EditorUtility.SetDirty(settings);
                        }
                    }
                    else
                    {
                        GUILayout.FlexibleSpace();
                    }
                    if (GUILayout.Button("Show", GUILayout.Width(44f)))
                        Frame(stop.transform.position, select: stop.gameObject);
                }
            using (new EditorGUILayout.HorizontalScope())
            {
                var placing = GUILayout.Toggle(_placing, new GUIContent("Place Stops By Clicking", "Click the ground in the Scene view to add a stop there; Esc or this button stops."), "Button");
                if (placing != _placing)
                {
                    _placing = placing;
                    SceneView.RepaintAll();
                }
                if (GUILayout.Button(new GUIContent("Face Stops Along Routes", "Turns every route stop to face the way the BTR drives through it.")))
                    EditorApplication.delayCall += () =>
                    {
                        var turned = TarkovBtr.FaceAlongRoutes(_config);
                        Debug.Log($"[Tarkov SDK] BTR: {turned.Count} stop(s) turned along their routes; regenerate the roads to follow them.");
                        MarkDirty();
                    };
            }

            EditorGUILayout.Space();
            if (MapPathConfigEditor.DrawRoutes(_config, stops.Select(s => s.id).ToArray()))
            {
                _checks = null;
                _sim = null;
            }
            EditorGUILayout.LabelField("A route needs at least three stops to drive laps without turning round: with two, it drives the same road back and forth.",
                EditorStyles.wordWrappedMiniLabel);
        }

        // ------------------------------------------------------------ roads

        private void DrawRoads()
        {
            var needed = TarkovBtr.NeededPairs(_config).Select(p => TarkovBtr.RoadId(p.A, p.B)).ToList();
            var roads = TarkovBtr.Roads(_config).Where(r => !string.IsNullOrEmpty(r.id) && r.id.StartsWith("s_", StringComparison.Ordinal))
                .GroupBy(r => r.id).ToDictionary(g => g.Key, g => g.First());
            int missing = needed.Count(id => !roads.ContainsKey(id));

            EditorGUILayout.LabelField($"Roads: {needed.Count - missing} of {needed.Count} needed", EditorStyles.boldLabel);
            _driveThrough = EditorGUILayout.ToggleLeft(new GUIContent("Drive-through roads",
                "Roads leave and enter each stop along its arrow, the way the routes drive, so the BTR drives on instead of making a three-point turn. " +
                "Off: the shortest navmesh path (it turns round wherever the next road starts behind it)."), _driveThrough);
            _replaceEdited = EditorGUILayout.ToggleLeft(new GUIContent("Also replace roads edited by hand"), _replaceEdited);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Generate Missing"))
                    EditorApplication.delayCall += () => Regenerate(replace: false);
                if (GUILayout.Button(new GUIContent("Regenerate All", _driveThrough ? "Turns the stops along their routes first." : null)))
                    EditorApplication.delayCall += () => Regenerate(replace: true);
            }

            EditorGUILayout.Space();
            foreach (var id in needed)
                using (new EditorGUILayout.HorizontalScope())
                {
                    roads.TryGetValue(id, out var road);
                    bool selected = road != null && road == _road;
                    var label = road == null ? id + "  (missing)" : $"{id}  {Length(road):0} m{(TarkovBtr.IsEdited(_config, id) ? "  (edited)" : "")}";
                    var style = selected ? EditorStyles.boldLabel : road == null ? EditorStyles.miniBoldLabel : EditorStyles.label;
                    EditorGUILayout.LabelField(label, style);
                    if (road != null && GUILayout.Button(selected ? "Editing" : "Edit", GUILayout.Width(60f)))
                    {
                        _road = road;
                        _anchor = -1;
                        Frame(Bounds(road).center, Bounds(road).size.magnitude);
                    }
                }

            EditorGUILayout.Space();
            if (_road != null && _road.spline != null)
                DrawRoadEditor();
            else
                EditorGUILayout.HelpBox("Edit a road to move its points in the Scene view: click a white point to pick it, drag it (and its yellow handles). " +
                                        "Roads you change are kept when the others are regenerated.", MessageType.None);

            EditorGUILayout.Space();
            DrawTurns();
        }

        private void DrawRoadEditor()
        {
            var spline = _road.spline;
            int anchors = (spline.points.Length + 2) / 3;
            EditorGUILayout.LabelField($"Editing {_road.id}: {anchors} points", EditorStyles.boldLabel);
            _snapToGround = EditorGUILayout.ToggleLeft("Snap moved points to the ground", _snapToGround);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_anchor < 0 || _anchor >= anchors - 1))
                    if (GUILayout.Button(new GUIContent("Insert Point", "Adds a point half way to the next one.")))
                        EditRoad("Insert BTR road point", () => InsertAfter(spline, _anchor));
                using (new EditorGUI.DisabledScope(_anchor <= 0 || _anchor >= anchors - 1 || anchors <= 2))
                    if (GUILayout.Button(new GUIContent("Delete Point", "The ends stay: they're at the stops.")))
                        EditRoad("Delete BTR road point", () =>
                        {
                            Delete(spline, _anchor);
                            _anchor = Mathf.Min(_anchor, (spline.points.Length + 2) / 3 - 1);
                        });
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Smooth", "Recomputes the handles from the points (a smooth curve through them).")))
                    EditRoad("Smooth BTR road", () => TarkovSplines.FromWaypoints(spline, TarkovSplines.Anchors(spline)));
                if (GUILayout.Button(new GUIContent("Snap To Ground", "Puts every point on the surface below it.")))
                    EditRoad("Snap BTR road", () => TarkovSplines.FromWaypoints(spline, TarkovSplines.Anchors(spline).Select(Ground).ToList()));
                if (GUILayout.Button(new GUIContent("Keep Off Walls", $"Moves the inner points {TarkovBtr.RoadClearance} m clear of the navmesh's edges.")))
                    EditRoad("BTR road off walls", () =>
                    {
                        var pts = TarkovSplines.Anchors(spline);
                        TarkovSplines.KeepClear(pts, TarkovBtr.RoadClearance, 1, pts.Count - 2);
                        TarkovSplines.FromWaypoints(spline, pts);
                    });
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent(_driveThrough ? "Rebuild (Drive-Through)" : "Rebuild (Shortest)", "Makes this road again from its stops, dropping your edits.")))
                    EditorApplication.delayCall += () =>
                    {
                        if (!TarkovBtr.RegenerateRoad(_config, _road, _driveThrough))
                            Debug.LogWarning($"[Tarkov SDK] BTR: the navmesh doesn't join the stops of {_road.id}.");
                        _anchor = -1;
                        Changed();
                    };
                if (GUILayout.Button("Done", GUILayout.Width(60f)))
                {
                    _road = null;
                    _anchor = -1;
                    SceneView.RepaintAll();
                }
            }
        }

        private void DrawTurns()
        {
            var stop = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponent<PathDestination>() : null;
            EditorGUILayout.LabelField("Three-point turns", EditorStyles.boldLabel);
            if (stop == null || stop.GetComponentInParent<MapPathConfig>() != _config)
            {
                EditorGUILayout.LabelField("Select a stop to see and remake its turns (orange in the Scene view). The BTR only uses one when the road it " +
                                           "leaves on starts behind it.", EditorStyles.wordWrappedMiniLabel);
                return;
            }
            EditorGUILayout.LabelField($"{stop.id}: {(stop.useReverse ? "has its turns" : "no turns (it slides sideways if a road starts behind it)")}", EditorStyles.wordWrappedLabel);
            if (GUILayout.Button(new GUIContent("Remake Turns", "Fits the turn into the open space around the stop, facing its arrow.")))
                EditorApplication.delayCall += () =>
                {
                    if (!TarkovBtr.GenerateTurns(_config, stop, replace: true))
                        Debug.LogWarning($"[Tarkov SDK] BTR: no turn at {stop.id} keeps the BTR clear of walls; the least bad one is used. Give the stop more open space.");
                    TarkovBtr.Sync(_config);
                    Changed();
                };
        }

        private void Regenerate(bool replace)
        {
            MapPathConfigEditor.Report(TarkovBtr.GenerateRoads(_config, replace, _driveThrough, _replaceEdited));
            if (_road == null)
                _anchor = -1;
            Changed();
        }

        private void EditRoad(string undo, Action edit)
        {
            Undo.RecordObject(_road.spline, undo);
            edit();
            EditorUtility.SetDirty(_road.spline);
            TarkovBtr.SetEdited(_config, _road.id, true);
            Changed();
        }

        private void Changed()
        {
            _checks = null;
            _sim = null;
            _playing = false;
            MarkDirty();
            Repaint();
            SceneView.RepaintAll();
        }

        private void MarkDirty()
        {
            if (_config != null)
                EditorSceneManager.MarkSceneDirty(_config.gameObject.scene);
        }

        // splits the curve after an anchor in two at its middle (de Casteljau), so the road's shape stays
        private static void InsertAfter(BezierSpline spline, int anchor)
        {
            var p = spline.points.ToList();
            int i = anchor * 3;
            Vector3 p0 = p[i], p1 = p[i + 1], p2 = p[i + 2], p3 = p[i + 3];
            var a = Vector3.Lerp(p0, p1, 0.5f);
            var b = Vector3.Lerp(p1, p2, 0.5f);
            var c = Vector3.Lerp(p2, p3, 0.5f);
            var d = Vector3.Lerp(a, b, 0.5f);
            var e = Vector3.Lerp(b, c, 0.5f);
            var m = Vector3.Lerp(d, e, 0.5f);
            p.RemoveRange(i + 1, 2);
            p.InsertRange(i + 1, new[] { a, d, m, e, c });
            spline.points = p.ToArray();
            var modes = (spline.modes ?? new BezierControlPointMode[0]).ToList();
            modes.Insert(Mathf.Min(anchor + 1, modes.Count), BezierControlPointMode.Free);
            spline.modes = modes.ToArray();
        }

        // joins the curves on both sides of an anchor, keeping the outer handles
        private static void Delete(BezierSpline spline, int anchor)
        {
            var p = spline.points.ToList();
            p.RemoveRange(anchor * 3 - 1, 3);
            spline.points = p.ToArray();
            var modes = (spline.modes ?? new BezierControlPointMode[0]).ToList();
            if (anchor < modes.Count)
                modes.RemoveAt(anchor);
            spline.modes = modes.ToArray();
        }

        // ------------------------------------------------------------ preview

        private void DrawPreview()
        {
            var routes = (_config.PathsConfiguration.pathsConfigurations ?? new PathConfig[0]).ToList();
            if (routes.Count == 0)
            {
                EditorGUILayout.HelpBox("The BTR has no routes (Stops & Routes tab).", MessageType.Info);
                return;
            }
            _routeIndex = Mathf.Clamp(_routeIndex, 0, routes.Count - 1);
            var route = routes[_routeIndex];
            EditorGUI.BeginChangeCheck();
            _routeIndex = EditorGUILayout.Popup("Route", _routeIndex, routes.Select(r => (r.id ?? "?") + (r.active ? "" : " (inactive)")).ToArray());
            if (EditorGUI.EndChangeCheck())
                StopSim();
            _speed = EditorGUILayout.Slider(new GUIContent("Speed", "Times real time."), _speed, 0.25f, 20f);
            _pause = EditorGUILayout.Slider(new GUIContent("Waits At Stops (s)", "In the preview only; the raid waits as long as the BTR's Waits At Stops."), _pause, 0f, 10f);
            TarkovGameModelPreview.Enabled = EditorGUILayout.ToggleLeft(new GUIContent("Show The BTR's Model",
                "Draws the game's BTR (from the game install) at each stop and where the preview drives it."), TarkovGameModelPreview.Enabled);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(_playing ? "Pause" : _sim == null || _sim.Finished ? "Play" : "Resume"))
                {
                    if (_sim == null || _sim.Finished)
                        StartSim(route);
                    else
                        _playing = !_playing;
                    _lastTime = EditorApplication.timeSinceStartup;
                }
                using (new EditorGUI.DisabledScope(_sim == null))
                    if (GUILayout.Button("Stop"))
                        StopSim();
            }
            if (_sim != null)
            {
                string state = _sim.Finished ? "done" : _sim.Paused ? "waiting at a stop" : _sim.Backward ? "backing up (turn)" : _sim.Reversing ? "turning round" :
                               _sim.SlideAngle > 0f ? $"sliding sideways ({_sim.SlideAngle:0} degrees off)" : "driving";
                EditorGUILayout.LabelField($"{_sim.Time / 60f:0.0} min, {_sim.Distance:0} m, {state}, on {_sim.Where}");
                DrawEvents(_sim.Events, 12);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Check", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Drives every active route at once, as Build Map does, and lists where the BTR turns round, slides sideways or gets stuck.",
                EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button("Check Routes"))
            {
                TarkovBtr.Sync(_config);
                float speed = TarkovBtr.Settings(_config, false)?.MoveSpeed ?? 8f;
                _checks = routes.Where(r => r.active).ToDictionary(r => r.id ?? "?", r => TarkovBtrSim.Check(_config, r, speed));
                SceneView.RepaintAll();
            }
            if (_checks != null)
                foreach (var pair in _checks)
                {
                    int turns = pair.Value.Count(e => e.Kind == TarkovBtrSim.EventKind.Turn);
                    int slides = pair.Value.Count(e => e.Kind == TarkovBtrSim.EventKind.Slide);
                    int problems = pair.Value.Count(e => e.Kind == TarkovBtrSim.EventKind.Problem);
                    EditorGUILayout.LabelField($"{pair.Key}: {turns} turn(s), {slides} slide(s), {problems} problem(s)", EditorStyles.boldLabel);
                    DrawEvents(pair.Value.Where(e => e.Kind != TarkovBtrSim.EventKind.Info).ToList(), 30);
                }
        }

        private void DrawEvents(List<TarkovBtrSim.Event> events, int max)
        {
            foreach (var e in events.Skip(Math.Max(0, events.Count - max)))
                using (new EditorGUILayout.HorizontalScope())
                {
                    var color = GUI.color;
                    GUI.color = EventColor(e.Kind, Color.white);
                    EditorGUILayout.LabelField($"{e.Time / 60f:0.0}m  {e.Text}", EditorStyles.wordWrappedMiniLabel);
                    GUI.color = color;
                    if (GUILayout.Button("Show", GUILayout.Width(44f)))
                        Frame(e.Position, 40f);
                }
        }

        private void StartSim(PathConfig route)
        {
            // the lists the game (and the sim) read, after any roads or stops made since
            TarkovBtr.Sync(_config);
            _sim = new TarkovBtrSim(_config, route)
            {
                MoveSpeed = TarkovBtr.Settings(_config, false)?.MoveSpeed ?? 8f,
                PauseSeconds = _pause,
            };
            _playing = _sim.Start();
            _carry = 0f;
            _lastTime = EditorApplication.timeSinceStartup;
            SceneView.RepaintAll();
        }

        private void StopSim()
        {
            _sim = null;
            _playing = false;
            SceneView.RepaintAll();
        }

        private void Step()
        {
            if (!_playing || _sim == null)
                return;
            double now = EditorApplication.timeSinceStartup;
            _carry += (float)(now - _lastTime) * _speed;
            _lastTime = now;
            _sim.PauseSeconds = _pause;
            // at most 20 s of raid time per editor frame, so a slow frame doesn't stall the editor
            int ticks = 0;
            while (_carry >= TarkovBtrSim.Step && ticks < 1000 && !_sim.Finished)
            {
                _sim.Tick();
                _carry -= TarkovBtrSim.Step;
                ticks++;
            }
            if (ticks == 1000)
                _carry = 0f;
            if (_sim.Finished)
                _playing = false;
            SceneView.RepaintAll();
            Repaint();
        }

        // ------------------------------------------------------------ Scene view

        private void OnSceneGUI(SceneView view)
        {
            if (_config == null)
                return;
            DrawRouteLegs();
            if (_placing)
                PlaceStops();
            if (_tab == Tab.Roads && _road != null && _road.spline != null)
                EditHandles();
            if (_sim != null)
                DrawSim(_sim);
            if (_checks != null)
                foreach (var e in _checks.Values.SelectMany(v => v).Where(e => e.Kind != TarkovBtrSim.EventKind.Info && e.Kind != TarkovBtrSim.EventKind.Finished))
                    DrawMarker(e);
        }

        // the legs of the routes, with arrows the way they're driven, over the roads' own gizmos
        private void DrawRouteLegs()
        {
            var roads = TarkovBtr.Roads(_config).Where(r => r.spline != null && !string.IsNullOrEmpty(r.id)).GroupBy(r => r.id).ToDictionary(g => g.Key, g => g.First());
            var stops = TarkovBtr.Stops(_config).ToDictionary(s => s.id);
            var drawn = new HashSet<string>();
            foreach (var route in (_config.PathsConfiguration.pathsConfigurations ?? new PathConfig[0]).Where(r => r.active))
                foreach (var (from, to) in TarkovBtr.RouteLegs(route))
                {
                    if (!roads.TryGetValue(TarkovBtr.RoadId(from, to), out var road) || !stops.TryGetValue(from, out var start) || !drawn.Add(from + ">" + to))
                        continue;
                    var pts = TarkovSplines.Sample(road.spline);
                    if (pts.Count < 2)
                        continue;
                    if (Vector3.Distance(pts[0], start.transform.position) > Vector3.Distance(pts[pts.Count - 1], start.transform.position))
                        pts.Reverse();
                    Handles.color = road == _road ? Color.white : RouteColor;
                    Handles.DrawAAPolyLine(road == _road ? 5f : 3.5f, pts.Select(p => p + Vector3.up * 0.3f).ToArray());
                    // an arrow every 25 m
                    float along = 0f;
                    for (int i = 1; i < pts.Count; i++)
                    {
                        along += Vector3.Distance(pts[i - 1], pts[i]);
                        if (along < 25f)
                            continue;
                        along = 0f;
                        var dir = pts[i] - pts[i - 1];
                        if (dir.sqrMagnitude > 1e-4f)
                            Handles.ArrowHandleCap(0, pts[i] + Vector3.up * 0.3f, Quaternion.LookRotation(dir), 2.5f, EventType.Repaint);
                    }
                }
            // each stop's heading
            Handles.color = StopColor;
            foreach (var stop in stops.Values)
            {
                var fwd = Vector3.ProjectOnPlane(stop.transform.forward, Vector3.up);
                if (fwd.sqrMagnitude > 1e-4f)
                    Handles.ArrowHandleCap(0, stop.transform.position + Vector3.up * 0.5f, Quaternion.LookRotation(fwd), 6f, EventType.Repaint);
            }
        }

        private void PlaceStops()
        {
            var e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                _placing = false;
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
            Handles.color = StopColor;
            Handles.DrawWireDisc(hit.point, Vector3.up, 3.75f);
            Handles.Label(hit.point + Vector3.up * 3f, "Click: new BTR stop (Esc: done)");
            if (e.type == EventType.MouseMove)
                SceneView.RepaintAll();
            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                var stop = TarkovBtr.AddStop(_config, hit.point);
                Undo.RegisterCreatedObjectUndo(stop.gameObject, "Add BTR stop");
                TarkovBtr.Sync(_config);
                Changed();
                e.Use();
            }
        }

        private void EditHandles()
        {
            var spline = _road.spline;
            var t = spline.transform;
            var pts = spline.points;
            int anchors = (pts.Length + 2) / 3;
            for (int a = 0; a < anchors; a++)
            {
                var world = t.TransformPoint(pts[a * 3]);
                float size = HandleUtility.GetHandleSize(world) * 0.08f;
                Handles.color = a == _anchor ? Color.yellow : Color.white;
                if (Handles.Button(world, Quaternion.identity, size, size * 1.4f, Handles.SphereHandleCap))
                {
                    _anchor = a;
                    Repaint();
                }
            }
            if (_anchor < 0 || _anchor >= anchors)
                return;

            int i = _anchor * 3;
            // the anchor: moves its handles with it (the ends are at the stops, so they stay put)
            if (_anchor > 0 && _anchor < anchors - 1)
            {
                EditorGUI.BeginChangeCheck();
                var moved = Handles.PositionHandle(t.TransformPoint(pts[i]), Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                    EditRoad("Move BTR road point", () =>
                    {
                        if (_snapToGround)
                            moved = Ground(moved);
                        var delta = t.InverseTransformPoint(moved) - pts[i];
                        pts[i] += delta;
                        pts[i - 1] += delta;
                        pts[i + 1] += delta;
                    });
            }
            // its handles: yellow dots joined to it
            foreach (int h in new[] { i - 1, i + 1 })
            {
                if (h < 0 || h >= pts.Length)
                    continue;
                var world = t.TransformPoint(pts[h]);
                Handles.color = Color.yellow;
                Handles.DrawLine(t.TransformPoint(pts[i]), world);
                EditorGUI.BeginChangeCheck();
                var moved = Handles.FreeMoveHandle(world, HandleUtility.GetHandleSize(world) * 0.06f, Vector3.zero, Handles.DotHandleCap);
                if (EditorGUI.EndChangeCheck())
                    EditRoad("Move BTR road handle", () => pts[h] = t.InverseTransformPoint(moved));
            }
        }

        private static void DrawSim(TarkovBtrSim sim)
        {
            if (sim.Trail.Count > 1)
            {
                Handles.color = new Color(1f, 1f, 1f, 0.6f);
                Handles.DrawAAPolyLine(2f, sim.Trail.Select(p => p + Vector3.up * 0.4f).Append(sim.Position + Vector3.up * 0.4f).ToArray());
            }
            TarkovGameScenePreviews.ShowBtr(sim.Position, sim.Rotation);
            var color = sim.SlideAngle > 0f ? SlideColor : sim.Reversing ? TurnColor : RouteColor;
            // the BTR's hull, 2.9 x 2.8 x 7.5 m, and its nose
            var matrix = Matrix4x4.TRS(sim.Position + Vector3.up * 1.4f, sim.Rotation, Vector3.one);
            using (new Handles.DrawingScope(color, matrix))
            {
                Handles.DrawWireCube(Vector3.zero, new Vector3(2.9f, 2.8f, 7.5f));
                Handles.DrawWireCube(new Vector3(0f, 0.3f, 2.6f), new Vector3(2.6f, 1.8f, 2.2f));
                Handles.DrawAAPolyLine(4f, new Vector3(0f, 0f, 3.75f), new Vector3(0f, 0f, 6f));
            }
            Handles.Label(sim.Position + Vector3.up * 4f, sim.Paused ? "BTR (waiting)" : sim.Backward ? "BTR (backing up)" : sim.SlideAngle > 0f ? "BTR (sliding)" : "BTR");
            foreach (var e in sim.Events.Where(e => e.Kind != TarkovBtrSim.EventKind.Info && e.Kind != TarkovBtrSim.EventKind.Finished))
                DrawMarker(e);
        }

        private static void DrawMarker(TarkovBtrSim.Event e)
        {
            Handles.color = EventColor(e.Kind, Color.white);
            Handles.DrawWireDisc(e.Position + Vector3.up * 0.2f, Vector3.up, 5f, 3f);
            Handles.Label(e.Position + Vector3.up * 6f, e.Kind == TarkovBtrSim.EventKind.Turn ? "turns round" : e.Kind == TarkovBtrSim.EventKind.Slide ? "slides sideways" : "stuck");
        }

        private static Color EventColor(TarkovBtrSim.EventKind kind, Color fallback) => kind switch
        {
            TarkovBtrSim.EventKind.Turn => TurnColor,
            TarkovBtrSim.EventKind.Slide => SlideColor,
            TarkovBtrSim.EventKind.Problem => ProblemColor,
            _ => fallback,
        };

        // ------------------------------------------------------------ helpers

        private static Vector3 Ground(Vector3 p) =>
            Physics.Raycast(p + Vector3.up * 20f, Vector3.down, out var hit, 60f, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore) ? hit.point : p;

        private static float Length(PathSpline road)
        {
            var pts = TarkovSplines.Sample(road.spline, 6);
            float length = 0f;
            for (int i = 1; i < pts.Count; i++)
                length += Vector3.Distance(pts[i - 1], pts[i]);
            return length;
        }

        private static Bounds Bounds(PathSpline road)
        {
            var pts = TarkovSplines.Anchors(road.spline);
            var b = new Bounds(pts.Count > 0 ? pts[0] : road.transform.position, Vector3.one);
            foreach (var p in pts)
                b.Encapsulate(p);
            return b;
        }

        private static void Frame(Vector3 position, float size = 25f, GameObject select = null)
        {
            if (select != null)
                Selection.activeGameObject = select;
            var view = SceneView.lastActiveSceneView;
            if (view != null)
                view.Frame(new Bounds(position, Vector3.one * Mathf.Max(size, 10f)), false);
        }
    }
}
