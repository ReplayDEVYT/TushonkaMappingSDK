using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using Random = System.Random;

namespace TarkovSDK.Editor
{
    // Tarkov SDK > Tools > AI > Patrol Editor: a bot zone's patrol ways in the Scene view (each way its colour, stay
    // points filled, look directions as arrows, sub points as dots, the navmesh paths between neighbouring points,
    // problems in red), editing them (click to add points on the navmesh, drag them, set look directions by clicking
    // where to look), generating them (TarkovPatrolGenerator) and previewing bots walking them (TarkovPatrolSim).
    public class TarkovPatrolEditorWindow : EditorWindow
    {
        private static readonly Color[] Palette =
        {
            new Color(0.3f, 0.75f, 1f), new Color(1f, 0.6f, 0.2f), new Color(0.5f, 1f, 0.4f), new Color(1f, 0.4f, 0.8f),
            new Color(0.9f, 0.9f, 0.3f), new Color(0.6f, 0.5f, 1f),
        };
        private static readonly Color Problem = new Color(1f, 0.2f, 0.15f);

        private enum Mode { None, AddPoints, Look }

        private BotZone _zone;
        private PatrolWay _way;
        private PatrolPoint _point;
        private Mode _mode;
        private Vector2 _scroll;
        private bool _showSettings;
        private readonly TarkovPatrolGenerator.Settings _settings = new TarkovPatrolGenerator.Settings();
        private string _note;

        // per way: the paths drawn between neighbouring points, and each point's problem (null: fine); remade when
        // anything in the zone moves
        private readonly Dictionary<PatrolWay, List<Vector3[]>> _links = new Dictionary<PatrolWay, List<Vector3[]>>();
        private readonly Dictionary<PatrolPoint, string> _problems = new Dictionary<PatrolPoint, string>();
        private int _hash;

        private TarkovPatrolSim _sim;
        private int _simBots = 3;
        private float _simSpeed = 8f;
        private double _lastStep;

        [MenuItem("Tarkov SDK/Tools/AI/Patrol Editor...", priority = 120)]
        private static void Menu()
        {
            var window = GetWindow<TarkovPatrolEditorWindow>("Patrol Editor");
            window.minSize = new Vector2(380f, 420f);
            var selected = Selection.activeGameObject;
            if (selected != null)
                window.SelectFrom(selected);
            window.Show();
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += Step;
            Undo.undoRedoPerformed += Invalidate;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= Step;
            Undo.undoRedoPerformed -= Invalidate;
            _sim = null;
        }

        private void Invalidate()
        {
            _hash = 0;
            Repaint();
            SceneView.RepaintAll();
        }

        private void OnSelectionChange()
        {
            if (Selection.activeGameObject != null && _mode == Mode.None)
                SelectFrom(Selection.activeGameObject);
            Repaint();
        }

        // the zone, way and point the object is or is under
        private void SelectFrom(GameObject go)
        {
            var point = go.GetComponentInParent<PatrolPoint>();
            // a sub point selects its main point
            while (point != null && point.transform.parent != null && point.transform.parent.GetComponent<PatrolPoint>() != null)
                point = point.transform.parent.GetComponent<PatrolPoint>();
            var way = go.GetComponentInParent<PatrolWay>();
            var zone = go.GetComponentInParent<BotZone>();
            if (zone == null)
                return;
            if (zone != _zone)
                _sim = null;
            _zone = zone;
            _way = way;
            _point = point;
            Invalidate();
        }

        // ------------------------------------------------------------ window

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Patrol Editor", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Bots walk to random points of their way (not in order), stay at stay points looking where the arrows point, " +
                                       "and every few minutes may move to another way of the zone. Reserved ways are guard posts.", EditorStyles.wordWrappedMiniLabel);
            var zones = TarkovPatrolGenerator.Zones().ToList();
            if (zones.Count == 0)
            {
                EditorGUILayout.HelpBox("No bot zones in the open scenes (GameObject > Tarkov SDK > AI > Bot Zone).", MessageType.Info);
                return;
            }
            if (!TarkovPatrolGenerator.HasNavMesh())
                EditorGUILayout.HelpBox("No navmesh: bake one first (Generation > Essential > Bake NavMesh). Patrols need it to be checked, generated and previewed.", MessageType.Warning);

            using (new EditorGUILayout.HorizontalScope())
            {
                int index = Mathf.Max(0, zones.IndexOf(_zone));
                int picked = EditorGUILayout.Popup("Zone", index, zones.Select(z => $"{z.name} ({z.GetComponentsInChildren<PatrolWay>(true).Length} way(s))").ToArray());
                if (picked != index || _zone == null)
                {
                    _zone = zones[picked];
                    _way = null;
                    _point = null;
                    _sim = null;
                    Invalidate();
                }
                if (GUILayout.Button("Frame", GUILayout.Width(55f)))
                    Frame(_zone.transform.position, 60f);
            }
            if (_zone == null)
                return;
            Refresh();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawGenerate();
            EditorGUILayout.Space();
            DrawWays();
            if (_way != null)
            {
                EditorGUILayout.Space();
                DrawWay();
            }
            if (_point != null)
            {
                EditorGUILayout.Space();
                DrawPoint();
            }
            EditorGUILayout.Space();
            DrawPreview();
            EditorGUILayout.EndScrollView();
            if (_note != null)
                EditorGUILayout.HelpBox(_note, MessageType.None);
        }

        private void DrawGenerate()
        {
            EditorGUILayout.LabelField("Generate", EditorStyles.boldLabel);
            _showSettings = EditorGUILayout.Foldout(_showSettings, "Settings", true);
            if (_showSettings)
                using (new EditorGUI.IndentLevelScope())
                {
                    _settings.Radius = EditorGUILayout.Slider(new GUIContent("Radius", "How far round the zone's spawn points the patrol reaches (retail ~48 m)."), _settings.Radius, 10f, 150f);
                    _settings.Spacing = EditorGUILayout.Slider(new GUIContent("Spacing", "The distance aimed for between points (retail 12.7 m)."), _settings.Spacing, 4f, 40f);
                    _settings.MinSpacing = EditorGUILayout.Slider(new GUIContent("Min Spacing", "No two points closer than this."), _settings.MinSpacing, 2f, _settings.Spacing);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.PrefixLabel("Points");
                        _settings.MinPoints = EditorGUILayout.IntField(_settings.MinPoints, GUILayout.Width(40f));
                        EditorGUILayout.LabelField("to", GUILayout.Width(18f));
                        _settings.MaxPoints = EditorGUILayout.IntField(_settings.MaxPoints, GUILayout.Width(40f));
                    }
                    _settings.MinPoints = Mathf.Clamp(_settings.MinPoints, 2, 40);
                    _settings.MaxPoints = Mathf.Clamp(_settings.MaxPoints, _settings.MinPoints, 60);
                    _settings.Ways = EditorGUILayout.IntSlider(new GUIContent("Ways", "0: by the area (one, two from 10 points, three from 16)."), _settings.Ways, 0, 6);
                    _settings.GuardPosts = EditorGUILayout.Toggle(new GUIContent("Guard Posts", "A reserved way of 2-3 places with cover and a view, where bots stand guard."), _settings.GuardPosts);
                    _settings.StayShare = EditorGUILayout.Slider(new GUIContent("Stay Points", "Share of the points where bots stay and look round, at most (retail 35%)."), _settings.StayShare, 0f, 1f);
                    _settings.Seed = EditorGUILayout.IntField(new GUIContent("Seed", "Change it for another layout."), _settings.Seed);
                }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Generate This Zone", "Replaces its generated ways (and, if you say so, its hand-made ones).")))
                    EditorApplication.delayCall += () =>
                    {
                        bool replace = false;
                        if (TarkovPatrolGenerator.HasHandMade(_zone))
                        {
                            int choice = EditorUtility.DisplayDialogComplex("Patrol Editor", $"{_zone.name} has hand-made ways.", "Replace Them", "Cancel", "Keep Them, Add Generated");
                            if (choice == 1)
                                return;
                            replace = choice == 0;
                        }
                        var ways = TarkovPatrolGenerator.Generate(_zone, _settings, replace, out var why);
                        _note = ways == null ? $"No patrol fits: {why}." : $"{ways.Count} way(s), {ways.Sum(w => w.Points.Count)} point(s).";
                        _way = ways?.FirstOrDefault();
                        _point = null;
                        _sim = null;
                        Invalidate();
                    };
                if (GUILayout.Button(new GUIContent("Generate All Zones", "Every zone without a hand-made way.")))
                    EditorApplication.delayCall += () =>
                    {
                        _note = TarkovPatrolGenerator.GenerateAll(_settings, replaceHandMade: false);
                        _sim = null;
                        Invalidate();
                    };
            }
        }

        private void DrawWays()
        {
            var ways = _zone.GetComponentsInChildren<PatrolWay>(true).ToList();
            EditorGUILayout.LabelField($"Ways of {_zone.name}", EditorStyles.boldLabel);
            if (ways.Count == 0)
                EditorGUILayout.HelpBox("No ways: bots of this zone would stand still (the client makes a simple one at raid start). Generate one, or add one below.", MessageType.Warning);
            for (int i = 0; i < ways.Count; i++)
            {
                var way = ways[i];
                var points = TarkovPatrolGenerator.MainPoints(way);
                int problems = points.Count(p => _problems.ContainsKey(p) && _problems[p] != null);
                using (new EditorGUILayout.HorizontalScope())
                {
                    var old = GUI.color;
                    GUI.color = Palette[i % Palette.Length];
                    GUILayout.Label("■", GUILayout.Width(14f));
                    GUI.color = old;
                    bool selected = way == _way;
                    if (GUILayout.Toggle(selected, $"{way.name}  ({way.PatrolType}, {points.Count} point(s){(problems > 0 ? $", {problems} problem(s)" : "")})", EditorStyles.radioButton) && !selected)
                    {
                        _way = way;
                        _point = null;
                        Selection.activeGameObject = way.gameObject;
                    }
                }
            }
            if (GUILayout.Button("New Way"))
                EditorApplication.delayCall += () =>
                {
                    var way = TarkovSceneTools.CreatePatrolWay(_zone.transform, "PatrolWay_" + (_zone.GetComponentsInChildren<PatrolWay>(true).Length + 1), Vector3.zero);
                    Undo.RegisterCreatedObjectUndo(way.gameObject, "New patrol way");
                    _way = way;
                    _point = null;
                    _mode = Mode.AddPoints;
                    _note = "Click on the navmesh in the Scene view to add points (Esc when done).";
                    Invalidate();
                };
        }

        private void DrawWay()
        {
            EditorGUILayout.LabelField(_way.name, EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            var type = (PatrolType)EditorGUILayout.EnumPopup(new GUIContent("Type", "patrolling: walked; reserved: guard posts, a bot stays at each (72 s for scavs); boss: a boss's way."), _way.PatrolType);
            int max = EditorGUILayout.IntField(new GUIContent("Max Bots", "How many bots may use it at once (-1: any)."), _way._maxPersons);
            float coef = EditorGUILayout.Slider(new GUIContent("Sub Point Spread", "CoefSubPoints: how far round each point a group's followers spread (1 = 1.7-3.4 m)."), _way.CoefSubPoints, 0.3f, 5f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_way, "Patrol way");
                _way.PatrolType = type;
                _way._maxPersons = max;
                _way.CoefSubPoints = coef;
                EditorUtility.SetDirty(_way);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                bool adding = GUILayout.Toggle(_mode == Mode.AddPoints, "Add Points", "Button");
                if (adding != (_mode == Mode.AddPoints))
                {
                    _mode = adding ? Mode.AddPoints : Mode.None;
                    _note = adding ? "Click on the navmesh in the Scene view to add points (Esc when done)." : null;
                    SceneView.RepaintAll();
                }
                if (GUILayout.Button(new GUIContent("Rebuild Sub Points", "Where a group's followers spread round each point.")))
                    EditorApplication.delayCall += () =>
                    {
                        var random = new Random(_settings.Seed);
                        int n = TarkovPatrolGenerator.MainPoints(_way).Sum(p => TarkovPatrolGenerator.SubPoints(p, _way.CoefSubPoints, random));
                        _note = $"{n} sub point(s).";
                        Invalidate();
                    };
                if (GUILayout.Button(new GUIContent("Snap To NavMesh", "Moves every point onto the navmesh below it.")))
                    EditorApplication.delayCall += () =>
                    {
                        int moved = 0;
                        foreach (var p in TarkovPatrolGenerator.MainPoints(_way))
                            if (Snap(p))
                                moved++;
                        _note = $"{moved} point(s) moved.";
                        Invalidate();
                    };
            }
            if (GUILayout.Button("Delete Way"))
                EditorApplication.delayCall += () =>
                {
                    if (!EditorUtility.DisplayDialog("Patrol Editor", $"Delete {_way.name} and its points?", "Delete", "Cancel"))
                        return;
                    Undo.DestroyObjectImmediate(_way.gameObject);
                    _way = null;
                    _point = null;
                    _sim = null;
                    Invalidate();
                };

            var points = TarkovPatrolGenerator.MainPoints(_way);
            foreach (var p in points)
                using (new EditorGUILayout.HorizontalScope())
                {
                    _problems.TryGetValue(p, out var problem);
                    var looks = TarkovPatrolGenerator.Looks(p);
                    string label = $"{p.name}  {(p.PatrolPointType == PatrolPointType.stayPoint ? "stay" : "check")}{(p.ShallSit ? ", sit" : "")}" +
                                   $"{(looks != null ? $", {looks.Directions.Count} look(s)" : "")}, {TarkovPatrolGenerator.SubPointsOf(p).Count} sub" + (problem != null ? $"  ({problem})" : "");
                    var style = new GUIStyle(EditorStyles.radioButton);
                    if (problem != null)
                        style.normal.textColor = Problem;
                    if (GUILayout.Toggle(p == _point, label, style) && p != _point)
                    {
                        _point = p;
                        Selection.activeGameObject = p.gameObject;
                        if (SceneView.lastActiveSceneView != null)
                            SceneView.lastActiveSceneView.LookAt(p.transform.position);
                    }
                }
        }

        private void DrawPoint()
        {
            EditorGUILayout.LabelField(_point.name, EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            var type = (PatrolPointType)EditorGUILayout.EnumPopup(new GUIContent("Type", "checkPoint: looks round and moves on; stayPoint: stays a while, looking where its arrows point."), _point.PatrolPointType);
            bool sit = EditorGUILayout.Toggle(new GUIContent("Crouch", "ShallSit: crouches there (behind low cover)."), _point.ShallSit);
            bool boss = EditorGUILayout.Toggle(new GUIContent("Boss May Use", "CanUseByBoss."), _point.CanUseByBoss);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_point, "Patrol point");
                _point.PatrolPointType = type;
                _point.ShallSit = sit;
                _point.CanUseByBoss = boss;
                EditorUtility.SetDirty(_point);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                bool looking = GUILayout.Toggle(_mode == Mode.Look, new GUIContent("Add Look Direction", "Click in the Scene view where the bot should look from this point."), "Button");
                if (looking != (_mode == Mode.Look))
                {
                    _mode = looking ? Mode.Look : Mode.None;
                    _note = looking ? "Click where the bot should look (Esc when done)." : null;
                    SceneView.RepaintAll();
                }
                if (GUILayout.Button("Clear Looks"))
                    EditorApplication.delayCall += () =>
                    {
                        TarkovPatrolGenerator.SetLooks(_point, null);
                        Invalidate();
                    };
                if (GUILayout.Button("Delete Point"))
                    EditorApplication.delayCall += () =>
                    {
                        Undo.DestroyObjectImmediate(_point.gameObject);
                        _point = null;
                        TarkovPatrolGenerator.Sync(_way);
                        Invalidate();
                    };
            }
        }

        private void DrawPreview()
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _simBots = EditorGUILayout.IntSlider("Bots", _simBots, 1, 8);
            }
            _simSpeed = EditorGUILayout.Slider("Speed", _simSpeed, 1f, 30f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(_sim == null ? "Play" : "Restart"))
                {
                    _sim = new TarkovPatrolSim(_zone, _simBots, _settings.Seed + (int)EditorApplication.timeSinceStartup);
                    _lastStep = EditorApplication.timeSinceStartup;
                    if (_sim.Bots.Count == 0)
                    {
                        _note = "No way with points to walk.";
                        _sim = null;
                    }
                }
                using (new EditorGUI.DisabledScope(_sim == null))
                    if (GUILayout.Button("Stop"))
                    {
                        _sim = null;
                        SceneView.RepaintAll();
                    }
            }
            if (_sim == null)
                return;
            int minutes = (int)(_sim.Time / 60f);
            EditorGUILayout.LabelField($"{minutes}:{(int)_sim.Time % 60:00} in the raid, {_sim.WayChanges} way change(s)", EditorStyles.miniLabel);
            foreach (var bot in _sim.Bots)
                EditorGUILayout.LabelField($"Bot {bot.Index + 1} ({bot.Way.name}): {bot.State}", EditorStyles.miniLabel);
            var all = _zone.GetComponentsInChildren<PatrolWay>(true).SelectMany(TarkovPatrolGenerator.MainPoints).ToList();
            int never = all.Count(p => !_sim.Visits.ContainsKey(p));
            if (_sim.Time > 300f && never > 0)
                EditorGUILayout.LabelField($"{never} point(s) not visited yet (grey in the Scene view)", EditorStyles.miniLabel);
        }

        // ------------------------------------------------------------ checks

        private void Refresh()
        {
            if (_zone == null)
                return;
            int hash = 17;
            foreach (var way in _zone.GetComponentsInChildren<PatrolWay>(true))
                foreach (var p in TarkovPatrolGenerator.MainPoints(way))
                    hash = hash * 31 + p.transform.position.GetHashCode() + way.GetInstanceID();
            if (hash == _hash)
                return;
            _hash = hash;
            _links.Clear();
            _problems.Clear();
            bool navMesh = TarkovPatrolGenerator.HasNavMesh();
            var path = new NavMeshPath();
            foreach (var way in _zone.GetComponentsInChildren<PatrolWay>(true))
            {
                var points = TarkovPatrolGenerator.MainPoints(way);
                var links = new List<Vector3[]>();
                _links[way] = links;
                if (!navMesh)
                    continue;
                var snapped = new Dictionary<PatrolPoint, Vector3>();
                foreach (var p in points)
                {
                    if (NavMesh.SamplePosition(p.transform.position, out var hit, 3f, NavMesh.AllAreas))
                        snapped[p] = hit.position;
                    else
                        _problems[p] = "off the navmesh";
                }
                var ok = points.Where(snapped.ContainsKey).ToList();
                if (ok.Count > 0)
                    foreach (var p in ok.Skip(1))
                        if (!NavMesh.CalculatePath(snapped[ok[0]], snapped[p], NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                            _problems[p] = "can't be walked to";
                // each point to its two nearest: shows how the way hangs together on the navmesh
                var done = new HashSet<(PatrolPoint, PatrolPoint)>();
                foreach (var p in ok)
                    foreach (var q in ok.Where(q => q != p).OrderBy(q => (q.transform.position - p.transform.position).sqrMagnitude).Take(2))
                    {
                        if (done.Contains((q, p)) || !done.Add((p, q)))
                            continue;
                        if (NavMesh.CalculatePath(snapped[p], snapped[q], NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete)
                            links.Add(path.corners.ToArray());
                    }
            }
        }

        private static bool Snap(PatrolPoint p)
        {
            if (!NavMesh.SamplePosition(p.transform.position, out var hit, 5f, NavMesh.AllAreas) || (hit.position - p.transform.position).sqrMagnitude < 1e-4f)
                return false;
            Undo.RecordObject(p.transform, "Snap patrol point");
            // its sub points move with it
            p.transform.position = hit.position;
            return true;
        }

        // ------------------------------------------------------------ scene view

        private void OnSceneGUI(SceneView view)
        {
            if (_zone == null)
                return;
            Refresh();
            var ways = _zone.GetComponentsInChildren<PatrolWay>(true).ToList();
            for (int i = 0; i < ways.Count; i++)
                DrawWay(ways[i], Palette[i % Palette.Length], ways[i] == _way);
            if (_sim != null)
                DrawSim();
            switch (_mode)
            {
                case Mode.AddPoints:
                    AddPoints();
                    break;
                case Mode.Look:
                    AddLook();
                    break;
            }
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape && _mode != Mode.None)
            {
                _mode = Mode.None;
                _note = null;
                Event.current.Use();
                Repaint();
            }
        }

        private void DrawWay(PatrolWay way, Color color, bool selected)
        {
            if (_links.TryGetValue(way, out var links))
            {
                Handles.color = new Color(color.r, color.g, color.b, selected ? 0.9f : 0.45f);
                foreach (var link in links)
                    Handles.DrawAAPolyLine(selected ? 3f : 2f, link.Select(c => c + Vector3.up * 0.15f).ToArray());
            }
            foreach (var p in TarkovPatrolGenerator.MainPoints(way))
            {
                var pos = p.transform.position;
                float size = HandleUtility.GetHandleSize(pos) * 0.12f;
                bool problem = _problems.TryGetValue(p, out var why) && why != null;
                bool visited = _sim == null || _sim.Visits.ContainsKey(p) || _sim.Time < 300f;
                var c = problem ? Problem : visited ? color : Color.grey;
                Handles.color = c;
                if (p.PatrolPointType == PatrolPointType.stayPoint)
                    Handles.DrawSolidDisc(pos + Vector3.up * 0.05f, Vector3.up, 0.45f);
                Handles.DrawWireDisc(pos + Vector3.up * 0.05f, Vector3.up, 0.6f, 2f);
                Handles.DrawLine(pos, pos + Vector3.up * 1.8f);
                if (selected || p == _point)
                {
                    var looks = TarkovPatrolGenerator.Looks(p);
                    if (looks != null)
                        foreach (var d in looks.Directions)
                            if (d.sqrMagnitude > 1e-4f)
                                Handles.ArrowHandleCap(0, pos + Vector3.up * 1.5f, Quaternion.LookRotation(d), 2f, EventType.Repaint);
                    Handles.color = new Color(c.r, c.g, c.b, 0.7f);
                    foreach (var s in TarkovPatrolGenerator.SubPointsOf(p))
                        Handles.DotHandleCap(0, s.transform.position + Vector3.up * 0.05f, Quaternion.identity, HandleUtility.GetHandleSize(s.transform.position) * 0.025f, EventType.Repaint);
                    Handles.Label(pos + Vector3.up * 2.1f, p.name + (p.ShallSit ? " (crouch)" : "") + (problem ? $"\n{why}" : ""));
                }
                Handles.color = c;
                if (_mode == Mode.None && Handles.Button(pos + Vector3.up * 1.8f, Quaternion.identity, size, size * 1.3f, Handles.SphereHandleCap))
                {
                    _way = way;
                    _point = p;
                    Selection.activeGameObject = p.gameObject;
                    Repaint();
                }
                if (p == _point && _mode == Mode.None)
                {
                    EditorGUI.BeginChangeCheck();
                    var moved = Handles.PositionHandle(pos, Quaternion.identity);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(p.transform, "Move patrol point");
                        // kept on the navmesh as it's dragged; its sub points move with it
                        p.transform.position = NavMesh.SamplePosition(moved, out var hit, 2f, NavMesh.AllAreas) ? hit.position : moved;
                        Repaint();
                    }
                }
            }
        }

        private bool MouseOnGround(out Vector3 point)
        {
            point = default;
            var ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
            if (!Physics.Raycast(ray, out var hit, 2000f, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore))
                return false;
            point = hit.point;
            return true;
        }

        private void AddPoints()
        {
            if (_way == null)
                return;
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            if (!MouseOnGround(out var ground))
                return;
            bool onNav = NavMesh.SamplePosition(ground, out var hit, 2f, NavMesh.AllAreas);
            Handles.color = onNav ? Color.white : Problem;
            var at = onNav ? hit.position : ground;
            Handles.DrawWireDisc(at, Vector3.up, 0.6f, 2f);
            Handles.Label(at + Vector3.up * 1.5f, onNav ? "Click: new point (Esc: done)" : "Not on the navmesh");
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && onNav)
            {
                var point = TarkovSceneTools.CreatePatrolPoint(_way.transform, Vector3.zero);
                point.transform.position = at;
                point.CanUseByBoss = true;
                Undo.RegisterCreatedObjectUndo(point.gameObject, "Add patrol point");
                TarkovPatrolGenerator.SubPoints(point, _way.CoefSubPoints > 0f ? _way.CoefSubPoints : 1f, new Random(point.GetInstanceID()));
                TarkovPatrolGenerator.Sync(_way);
                _point = point;
                Event.current.Use();
                Invalidate();
            }
            SceneView.RepaintAll();
        }

        private void AddLook()
        {
            if (_point == null)
                return;
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            if (!MouseOnGround(out var ground))
                return;
            var from = _point.transform.position + Vector3.up * 1.5f;
            var dir = Vector3.ProjectOnPlane(ground - _point.transform.position, Vector3.up);
            Handles.color = Color.white;
            Handles.DrawDottedLine(from, ground + Vector3.up * 1.5f, 4f);
            Handles.Label(ground + Vector3.up * 1.8f, "Click: look this way (Esc: done)");
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && dir.sqrMagnitude > 0.25f)
            {
                var looks = TarkovPatrolGenerator.Looks(_point);
                var list = looks != null ? new List<Vector3>(looks.Directions) : new List<Vector3>();
                list.Add(dir.normalized);
                TarkovPatrolGenerator.SetLooks(_point, list);
                // looking somewhere is what a stay point is for
                if (_point.PatrolPointType != PatrolPointType.stayPoint)
                {
                    Undo.RecordObject(_point, "Look direction");
                    _point.PatrolPointType = PatrolPointType.stayPoint;
                }
                Event.current.Use();
                Repaint();
            }
            SceneView.RepaintAll();
        }

        private void DrawSim()
        {
            foreach (var bot in _sim.Bots)
            {
                var color = Color.Lerp(Color.white, Color.red, 0.35f);
                Handles.color = new Color(1f, 1f, 1f, 0.5f);
                if (bot.Trail.Count > 1)
                    Handles.DrawAAPolyLine(2f, bot.Trail.Select(p => p + Vector3.up * 0.3f).Append(bot.Position + Vector3.up * 0.3f).ToArray());
                Handles.color = color;
                Handles.DrawSolidDisc(bot.Position + Vector3.up * 0.1f, Vector3.up, 0.35f);
                Handles.DrawLine(bot.Position, bot.Position + Vector3.up * 1.8f);
                if (bot.Target != null)
                {
                    Handles.color = new Color(color.r, color.g, color.b, 0.4f);
                    Handles.DrawDottedLine(bot.Position + Vector3.up * 1.8f, bot.Target.transform.position + Vector3.up * 1.8f, 3f);
                }
                Handles.Label(bot.Position + Vector3.up * 2.2f, $"Bot {bot.Index + 1}");
            }
        }

        private void Step()
        {
            if (_sim == null)
                return;
            double now = EditorApplication.timeSinceStartup;
            float dt = Mathf.Min(0.1f, (float)(now - _lastStep));
            _lastStep = now;
            // in fixed steps so a fast preview walks the same way
            float total = dt * _simSpeed;
            while (total > 0f)
            {
                float step = Mathf.Min(total, 0.1f);
                _sim.Step(step);
                total -= step;
            }
            SceneView.RepaintAll();
            Repaint();
        }

        private static void Frame(Vector3 position, float size)
        {
            var view = SceneView.lastActiveSceneView;
            if (view != null)
                view.LookAt(position, view.rotation, size);
        }
    }
}
