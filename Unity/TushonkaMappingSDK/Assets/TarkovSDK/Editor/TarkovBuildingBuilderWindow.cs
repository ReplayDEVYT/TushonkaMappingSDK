using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Plan = TarkovSDK.TarkovBuilding;

namespace TarkovSDK.Editor
{
    // Tarkov SDK > Tools > Geometry > Building Builder: paints a building's plan (TarkovBuilding) storey by storey, in
    // the Scene view on the storey's floor or on the plan drawn in the window, and rebuilds it as it changes
    // (TarkovBuildingBuilder). Rooms are painted on cells (drag a rectangle); doors, windows and gaps are clicked onto
    // the edges between cells; stairs are clicked onto a cell and run in the chosen direction; bevels round a room's
    // corner (clicked in the corner of the cell), paint puts a material on one side of a wall. Furniture, holes and
    // the Select tool (copy, paste, editing many things at once) are in TarkovBuildingBuilderEdit.cs. The Trims tab
    // makes mouldings and draws their profiles, the Materials tab sets and edits every material the building uses
    // (TarkovBuildingBuilderTabs.cs). A building that is a prefab is edited in Prefab Mode (the window opens it), so
    // every copy changes with it.
    public partial class TarkovBuildingBuilderWindow : EditorWindow
    {
        private enum Tool { Rooms, Doors, Windows, Gaps, Stairs, Ceiling, Bevel, Paint, Furniture, Holes, Select }
        private enum Tab { Plan, Trims, Materials, Building }

        private Plan _plan;
        private int _storey;
        private Tool _tool;
        private Tab _tab;
        private int _room = 1;
        private bool _auto = true;
        private string _name = "Building";
        private string _report;
        private Vector2 _scroll;
        private double _lastEdit;
        // an edit not yet rebuilt
        private bool _pending;

        // tool defaults
        // retail door units are about 2.1 x 1.1 m (Inside_Door_Wood_26_R_210-110): narrower and bots can't path through
        private float _doorWidth = 1.1f, _doorHeight = 2.1f;
        private float _windowWidth = 1.2f, _windowHeight = 1.2f, _windowSill = 0.9f;
        private int _span = 1;
        // what new doors, windows and stairs get (prefabs: empty = the building's default, else the SDK's)
        private GameObject _doorPrefab, _windowPrefab, _stairPrefab;
        private bool _doorHinge, _doorReverse, _stairFit = true;
        private Plan.DoorSettings _doorSettings = new Plan.DoorSettings();
        private bool _showLamps;
        // two cells: a one-cell flight between the floor's edges is too tight for bots (0.3 m radius) at its top
        private int _stairDirection, _stairLength = 4, _stairWidth = 2;
        private Plan.BevelKind _bevelKind = Plan.BevelKind.Round;
        private float _bevelRadius = 2f;
        private int _bevelSegments = 8;
        // Round Room: half the shorter wall at each corner (round rooms, rounded ends) instead of the radius
        private bool _bevelFit = true;
        private Material _brush;
        // a paint stroke in progress (one undo step)
        private bool _painting;

        // a drag of rooms in progress (cells)
        private Vector2Int? _dragFrom;
        private Vector2Int _dragTo;

        [MenuItem("Tarkov SDK/Tools/Geometry/Building Builder...", priority = 136)]
        private static void Menu()
        {
            var window = GetWindow<TarkovBuildingBuilderWindow>("Building Builder");
            window.minSize = new Vector2(380f, 460f);
            window.Pick();
            window.Show();
        }

        private void OnEnable()
        {
            wantsMouseMove = true;
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += Tick;
            Undo.undoRedoPerformed += Undone;
            Pick();
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= Tick;
            Undo.undoRedoPerformed -= Undone;
        }

        private void Undone()
        {
            ValidateSelection();
            TarkovBuildingBuilder.ForgetPrefabSizes();
            _pending = true;
            Repaint();
        }

        private void OnSelectionChange()
        {
            Pick();
            Repaint();
        }

        private void Pick()
        {
            var plan = TarkovBuildingBuilder.PlanOf(Selection.activeGameObject);
            if (plan != null && plan != _plan)
            {
                _plan = plan;
                _storey = Mathf.Clamp(_storey, 0, Mathf.Max(0, plan.Storeys.Count - 1));
                ClearSelection();
            }
        }

        // a prefab copy can't have its children rebuilt: it's edited in Prefab Mode
        private bool Editable => _plan != null && !PrefabUtility.IsPartOfPrefabInstance(TarkovBuildingBuilder.Building(_plan).gameObject);

        // a rebuild a moment after the last change, of the storeys it touched; the plan's hash is only looked at after
        // an edit (or an undo), not every frame
        private void Tick()
        {
            if (!_auto || !_pending || _plan == null || !Editable || EditorApplication.timeSinceStartup - _lastEdit < 0.3)
                return;
            _pending = false;
            if (TarkovBuildingBuilder.Hash(_plan) != _plan.BuiltHash)
                Rebuild(false);
        }

        private void Rebuild(bool force)
        {
            if (_plan == null || !Editable)
                return;
            var result = TarkovBuildingBuilder.Build(_plan, force);
            _report = result.ToString();
            EditorSceneManager.MarkSceneDirty(_plan.gameObject.scene);
            Repaint();
            SceneView.RepaintAll();
        }

        private void Changed()
        {
            _lastEdit = EditorApplication.timeSinceStartup;
            _pending = true;
            EditorUtility.SetDirty(_plan);
            Repaint();
            SceneView.RepaintAll();
        }

        // ------------------------------------------------------------ window

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Building Builder", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Paint rooms on the grid, click doors, windows and gaps onto the edges between cells, add stairs between storeys. " +
                                       "The building rebuilds as you go: merged meshes, collision, doors, windows, lamps, and its darkness volumes, ambient portals, " +
                                       "rain cover and indoor triggers. Put props inside it anywhere but under Generated.", EditorStyles.wordWrappedMiniLabel);
            if (_plan == null)
            {
                DrawNew();
                return;
            }
            var root = TarkovBuildingBuilder.Building(_plan);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(root.name, EditorStyles.boldLabel);
                if (GUILayout.Button("Select", GUILayout.Width(55f)))
                    Selection.activeGameObject = root.gameObject;
                if (GUILayout.Button("New", GUILayout.Width(45f)))
                    _plan = null;
            }
            if (_plan == null)
                return;
            if (!Editable)
            {
                EditorGUILayout.HelpBox("This building is a prefab. Open it in Prefab Mode to edit it; every copy changes with it.", MessageType.Info);
                if (GUILayout.Button("Open Prefab"))
                    EditorApplication.delayCall += () =>
                    {
                        var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root.gameObject);
                        var stage = PrefabStageUtility.OpenPrefab(path);
                        var plan = stage != null ? stage.prefabContentsRoot.GetComponentInChildren<Plan>(true) : null;
                        if (plan != null)
                        {
                            _plan = plan;
                            Selection.activeGameObject = TarkovBuildingBuilder.Building(plan).gameObject;
                        }
                    };
                return;
            }

            ValidateSelection();
            if (_tab == Tab.Plan && EditCommands(Event.current))
                return;
            _tab = (Tab)GUILayout.Toolbar((int)_tab, new[] { "Plan", "Trims", "Materials", "Building" });
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            switch (_tab)
            {
                case Tab.Plan:
                    DrawStoreys();
                    DrawTools();
                    DrawPlanGrid();
                    DrawToolOptions();
                    break;
                case Tab.Trims:
                    DrawTrims();
                    break;
                case Tab.Materials:
                    DrawMaterials();
                    break;
                default:
                    DrawSettings();
                    break;
            }
            EditorGUILayout.EndScrollView();

            using (new EditorGUILayout.HorizontalScope())
            {
                _auto = GUILayout.Toggle(_auto, new GUIContent("Rebuild As I Go", "Rebuilds a moment after each change."), "Button");
                if (GUILayout.Button(new GUIContent("Rebuild", "Everything, also what hasn't changed (after editing a door or window prefab, say).")))
                    EditorApplication.delayCall += () => Rebuild(true);
                bool isPrefab = PrefabStageUtility.GetCurrentPrefabStage() != null && PrefabStageUtility.GetCurrentPrefabStage().scene == root.gameObject.scene;
                using (new EditorGUI.DisabledScope(isPrefab))
                    if (GUILayout.Button(new GUIContent("Save As Prefab", "Saves it as a prefab and turns this one into a copy of it.")))
                        EditorApplication.delayCall += SavePrefab;
            }
            if (_report != null)
                EditorGUILayout.HelpBox(_report, MessageType.None);
        }

        private void DrawNew()
        {
            EditorGUILayout.HelpBox("Select a building made with this tool, or make a new one at the Scene view's centre.", MessageType.Info);
            _name = EditorGUILayout.TextField("Name", _name);
            if (GUILayout.Button("New Building"))
                EditorApplication.delayCall += () =>
                {
                    var view = SceneView.lastActiveSceneView;
                    var at = view != null ? view.pivot : Vector3.zero;
                    if (Physics.Raycast(at + Vector3.up * 200f, Vector3.down, out var hit, 500f, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore))
                        at = hit.point;
                    var stage = PrefabStageUtility.GetCurrentPrefabStage();
                    var scene = stage != null ? stage.scene : SceneManager.GetActiveScene();
                    _plan = TarkovBuildingBuilder.Create(scene, at, string.IsNullOrWhiteSpace(_name) ? "Building" : _name.Trim());
                    _storey = 0;
                    Selection.activeGameObject = TarkovBuildingBuilder.Building(_plan).gameObject;
                    Rebuild(true);
                };
        }

        private void DrawStoreys()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("Storey");
                var names = Enumerable.Range(1, _plan.Storeys.Count).Select(i => i.ToString()).ToArray();
                _storey = GUILayout.Toolbar(Mathf.Clamp(_storey, 0, names.Length - 1), names);
                if (GUILayout.Button(new GUIContent("+", "Adds a storey on top, a copy of the top one's rooms, bevels and wall paint."), GUILayout.Width(24f)))
                {
                    Undo.RecordObject(_plan, "Add storey");
                    var last = _plan.Storeys.LastOrDefault();
                    var storey = new Plan.Storey { Height = last?.Height ?? 3f, Cells = last != null ? (int[])last.Cells.Clone() : new int[0], Exterior = last?.Exterior };
                    _plan.Storeys.Add(storey);
                    _plan.Fit(storey);
                    int from = _plan.Storeys.Count - 2, to = _plan.Storeys.Count - 1;
                    foreach (var b in _plan.Bevels.Where(b => b.Storey == from).ToList())
                        _plan.Bevels.Add(new Plan.Bevel { Storey = to, X = b.X, Z = b.Z, Quadrant = b.Quadrant, Kind = b.Kind, Radius = b.Radius, Segments = b.Segments });
                    foreach (var p in _plan.Paints.Where(p => p.Storey == from).ToList())
                        _plan.Paints.Add(new Plan.WallPaint { Storey = to, X = p.X, Z = p.Z, AlongZ = p.AlongZ, Plus = p.Plus, Material = p.Material });
                    _storey = to;
                    Changed();
                }
                using (new EditorGUI.DisabledScope(_plan.Storeys.Count <= 1))
                    if (GUILayout.Button(new GUIContent("-", "Removes this storey, its openings, stairs, bevels, paint, furniture and holes."), GUILayout.Width(24f)))
                    {
                        Undo.RecordObject(_plan, "Remove storey");
                        int s = _storey;
                        // holes are where they are in the building: the ones in it go, the ones above come down with it
                        float gone = _plan.Storeys[s].Height;
                        float bottom = _plan.Base(s + 1);
                        _plan.Holes?.RemoveAll(hole => hole == null || TarkovBuildingBuilder.HoleStorey(_plan, hole) == s);
                        foreach (var hole in _plan.Holes?.Where(hole => hole.Center.y >= bottom) ?? Enumerable.Empty<Plan.Hole>())
                            hole.Center.y -= gone;
                        _plan.Storeys.RemoveAt(s);
                        _plan.Openings.RemoveAll(o => o.Storey == s);
                        _plan.Stairs.RemoveAll(st => st.Storey == s);
                        _plan.Bevels.RemoveAll(b => b.Storey == s);
                        _plan.Paints.RemoveAll(p => p.Storey == s);
                        _plan.Props?.RemoveAll(p => p == null || p.Storey == s);
                        foreach (var p in _plan.Props?.Where(p => p.Storey > s) ?? Enumerable.Empty<Plan.Prop>())
                            p.Storey--;
                        foreach (var o in _plan.Openings.Where(o => o.Storey > s))
                            o.Storey--;
                        foreach (var st in _plan.Stairs.Where(st => st.Storey > s))
                            st.Storey--;
                        foreach (var b in _plan.Bevels.Where(b => b.Storey > s))
                            b.Storey--;
                        foreach (var p in _plan.Paints.Where(p => p.Storey > s))
                            p.Storey--;
                        _storey = Mathf.Clamp(s - 1, 0, _plan.Storeys.Count - 1);
                        Changed();
                    }
            }
            if (_plan.Storeys.Count == 0)
                return;
            EditorGUI.BeginChangeCheck();
            float h = EditorGUILayout.Slider(new GUIContent("Storey Height", "Floor to floor, the slab included (retail rooms: about 2.7-3.3 m)."), _plan.Storeys[_storey].Height, 2.2f, 8f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_plan, "Storey height");
                _plan.Storeys[_storey].Height = h;
                Changed();
            }
        }

        private void DrawTools()
        {
            var tool = (Tool)GUILayout.SelectionGrid((int)_tool, new[] { "Rooms", "Doors", "Windows", "Gaps", "Stairs", "Ceiling", "Bevel", "Paint", "Furniture", "Holes", "Select" }, 4);
            if (tool != _tool)
            {
                _tool = tool;
                _pasting = false;
                SceneView.RepaintAll();
            }
            EditorGUILayout.LabelField(_tool switch
            {
                Tool.Rooms => "Drag over cells to paint the room (Shift: erase). Room 0 is no room.",
                Tool.Doors => "Click an edge between cells (on a wall) to add a door; click a door to remove it.",
                Tool.Windows => "Click a wall's edge to add a window; click a window to remove it.",
                Tool.Gaps => "Click a wall's edge for an opening with nothing in it; click one to remove it.",
                Tool.Stairs => "Click a cell to start a flight there going the chosen way (to the storey above); click a flight to remove it.",
                Tool.Ceiling => "Drag over cells to take their ceiling away (Shift: put it back): the floor above opens there, or the roof when nothing is above. No lamps go under them.",
                Tool.Bevel => "Click inside a room's cell near the corner to round (or cut) it; click it again to square it. The walls either side must face one other room or the outside.",
                Tool.Paint => "Click or drag along walls to paint the side of the wall the mouse is on (Shift: back to the room's own).",
                Tool.Furniture => "Pick a prefab below and click on the plan or the storey's floor to place it (R: turn it 90°, Shift+click: remove one). Move and turn placed ones with Select.",
                Tool.Holes => "Click on the building in the Scene view to cut a hole into what you clicked (Shift+click a hole: remove it). Drag the selected hole's box handles to size it.",
                _ => "Click anything (Shift/Ctrl: add to the selection) or drag a box. Ctrl+C / Ctrl+V copies and pastes it (R turns the copy, on any storey or building), Ctrl+D duplicates, Del deletes.",
            }, EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawToolOptions()
        {
            switch (_tool)
            {
                case Tool.Rooms:
                    DrawRooms();
                    break;
                case Tool.Doors:
                    _doorWidth = EditorGUILayout.Slider("Width", _doorWidth, 0.6f, 4f);
                    _doorHeight = EditorGUILayout.Slider("Height", _doorHeight, 1.8f, 3.5f);
                    _span = EditorGUILayout.IntSlider(new GUIContent("Cells", "How many cells of wall it spans (wide doors, garage openings)."), _span, 1, 6);
                    _doorPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Prefab", "New doors get this (empty: the building's Default Door, else the SDK's)."), _doorPrefab, typeof(GameObject), false);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        _doorHinge = GUILayout.Toggle(_doorHinge, new GUIContent("Hinge Right", "The SDK's door: hinged on the other side."), "Button");
                        _doorReverse = GUILayout.Toggle(_doorReverse, new GUIContent("Reverse Swing", "It opens the other way (a prefab is turned round)."), "Button");
                    }
                    DoorSettingsGUI(_doorSettings);
                    DrawInBuilding(Plan.OpeningKind.Door);
                    break;
                case Tool.Windows:
                    _windowWidth = EditorGUILayout.Slider("Width", _windowWidth, 0.3f, 4f);
                    _windowHeight = EditorGUILayout.Slider("Height", _windowHeight, 0.3f, 2.5f);
                    _windowSill = EditorGUILayout.Slider("Sill", _windowSill, 0f, 2f);
                    _span = EditorGUILayout.IntSlider("Cells", _span, 1, 6);
                    _windowPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Prefab", "New windows get this (empty: the building's Default Window, else the SDK's glass)."), _windowPrefab, typeof(GameObject), false);
                    DrawInBuilding(Plan.OpeningKind.Window);
                    break;
                case Tool.Gaps:
                    _doorWidth = EditorGUILayout.Slider("Width", _doorWidth, 0.6f, 6f);
                    _doorHeight = EditorGUILayout.Slider("Height", _doorHeight, 1.8f, 4f);
                    _span = EditorGUILayout.IntSlider("Cells", _span, 1, 8);
                    break;
                case Tool.Stairs:
                    _stairDirection = GUILayout.Toolbar(_stairDirection, new[] { "↑ +Z", "→ +X", "↓ -Z", "← -X" });
                    _stairLength = EditorGUILayout.IntSlider(new GUIContent("Length", "Cells; about 4 m of run per 3 m storey is comfortable."), _stairLength, 2, 10);
                    _stairWidth = EditorGUILayout.IntSlider("Width", _stairWidth, 1, 4);
                    _stairPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Prefab", "New stairs use this model instead of the SDK's steps (empty: the building's Default Stair). The ramp stays for walking on."), _stairPrefab, typeof(GameObject), false);
                    if (_stairPrefab != null)
                        _stairFit = EditorGUILayout.Toggle(new GUIContent("Fit To Flight", "Stretch it to the flight's run, rise and width."), _stairFit);
                    DrawStairsInBuilding();
                    break;
                case Tool.Ceiling:
                {
                    var open = _plan.Storeys[_storey].OpenCeiling;
                    EditorGUILayout.LabelField($"{open?.Count ?? 0} cell(s) without a ceiling on this storey.", EditorStyles.miniLabel);
                    using (new EditorGUI.DisabledScope(open == null || open.Count == 0))
                        if (GUILayout.Button("Put Back This Storey's Ceilings"))
                        {
                            Undo.RecordObject(_plan, "Put back ceilings");
                            open.Clear();
                            Changed();
                        }
                    break;
                }
                case Tool.Bevel:
                    DrawBevelOptions();
                    break;
                case Tool.Paint:
                    DrawPaintOptions();
                    break;
                case Tool.Furniture:
                    DrawFurnitureOptions();
                    break;
                case Tool.Holes:
                    DrawHoleOptions();
                    break;
                case Tool.Select:
                    DrawSelected();
                    break;
            }
        }

        private void DrawRooms()
        {
            for (int i = 0; i <= _plan.Rooms.Count; i++)
                using (new EditorGUILayout.HorizontalScope())
                {
                    var color = i == 0 ? new Color(0.2f, 0.2f, 0.2f) : _plan.Rooms[i - 1].Color;
                    var rect = GUILayoutUtility.GetRect(16f, 16f, GUILayout.Width(16f));
                    EditorGUI.DrawRect(rect, color);
                    if (GUILayout.Toggle(_room == i, i == 0 ? "0  (no room)" : $"{i}  {_plan.Rooms[i - 1].Name}", EditorStyles.radioButton, GUILayout.Width(150f)))
                        _room = i;
                    if (i > 0)
                    {
                        var room = _plan.Rooms[i - 1];
                        EditorGUI.BeginChangeCheck();
                        string name = EditorGUILayout.TextField(room.Name);
                        var c = EditorGUILayout.ColorField(GUIContent.none, room.Color, false, false, false, GUILayout.Width(36f));
                        bool lamp = GUILayout.Toggle(room.Lamp, new GUIContent("Lamp", "Ceiling lamps in this room."), "Button", GUILayout.Width(44f));
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(_plan, "Room");
                            room.Name = name;
                            room.Color = c;
                            room.Lamp = lamp;
                            Changed();
                        }
                    }
                }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Room"))
                {
                    Undo.RecordObject(_plan, "Add room");
                    float hue = (_plan.Rooms.Count * 0.17f) % 1f;
                    _plan.Rooms.Add(new Plan.Room { Name = "Room " + (_plan.Rooms.Count + 1), Color = Color.HSVToRGB(hue, 0.55f, 1f) });
                    _room = _plan.Rooms.Count;
                    Changed();
                }
                using (new EditorGUI.DisabledScope(_room == 0))
                    if (GUILayout.Button(new GUIContent("Fill Storey", "Every cell of this storey becomes the selected room.")))
                    {
                        Undo.RecordObject(_plan, "Fill storey");
                        for (int z = 0; z < _plan.Depth; z++)
                            for (int x = 0; x < _plan.Width; x++)
                                _plan.Set(_storey, x, z, _room);
                        Changed();
                    }
            }
            if (_room > 0 && _room <= _plan.Rooms.Count)
            {
                var so = new SerializedObject(_plan);
                var prop = so.FindProperty("Rooms").GetArrayElementAtIndex(_room - 1);
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(prop.FindPropertyRelative("Floor"));
                EditorGUILayout.PropertyField(prop.FindPropertyRelative("FloorSurface"));
                EditorGUILayout.PropertyField(prop.FindPropertyRelative("Walls"));
                EditorGUILayout.PropertyField(prop.FindPropertyRelative("WallSurface"));
                EditorGUILayout.PropertyField(prop.FindPropertyRelative("Ceiling"));
                _showLamps = EditorGUILayout.Foldout(_showLamps, "Lamps", true);
                if (_showLamps)
                    using (new EditorGUI.IndentLevelScope())
                        foreach (var field in new[] { "LampPrefab", "LampRotation", "LampTint", "LampColor", "LampIntensity", "LampRange" })
                            EditorGUILayout.PropertyField(prop.FindPropertyRelative(field));
                if (EditorGUI.EndChangeCheck())
                {
                    so.ApplyModifiedProperties();
                    Changed();
                }
            }
        }

        // the door settings new doors get
        private static void DoorSettingsGUI(Plan.DoorSettings d)
        {
            d.Override = EditorGUILayout.Toggle(new GUIContent("Change The Door", "Off: new doors as they come (the SDK's start shut, unlocked, breachable; a prefab as it was made)."), d.Override);
            if (!d.Override)
                return;
            using (new EditorGUI.IndentLevelScope())
            {
                d.State = (Plan.DoorState)EditorGUILayout.EnumPopup("State", d.State);
                if (d.State == Plan.DoorState.Locked)
                    d.KeyId = EditorGUILayout.TextField(new GUIContent("Key Id", "The key's item id (empty: only breaching opens it)."), d.KeyId);
                d.CanBeBreached = EditorGUILayout.Toggle("Can Be Breached", d.CanBeBreached);
                d.OpenAngle = EditorGUILayout.Slider("Open Angle", d.OpenAngle, 30f, 180f);
                d.Interactable = EditorGUILayout.Toggle(new GUIContent("Interactable", "Off: players can't open or close it."), d.Interactable);
            }
        }

        // the building's doors (or windows) by prefab, each swappable for another at once, and the tool's settings
        // for all of them
        private void DrawInBuilding(Plan.OpeningKind kind)
        {
            var all = _plan.Openings.Where(o => o.Kind == kind).ToList();
            if (all.Count == 0)
                return;
            EditorGUILayout.LabelField($"In this building ({all.Count})", EditorStyles.miniBoldLabel);
            foreach (var group in all.GroupBy(o => TarkovBuildingBuilder.PrefabOf(_plan, o)).ToList())
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"{(group.Key != null ? group.Key.name : kind == Plan.OpeningKind.Door ? "SDK door" : "SDK glass")} x{group.Count()}", GUILayout.Width(170f));
                    EditorGUI.BeginChangeCheck();
                    var swap = (GameObject)EditorGUILayout.ObjectField(GUIContent.none, group.Key, typeof(GameObject), false);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(_plan, "Swap prefab");
                        foreach (var o in group)
                            o.Prefab = swap;
                        Changed();
                    }
                }
            using (new EditorGUILayout.HorizontalScope())
            {
                var mine = kind == Plan.OpeningKind.Door ? _doorPrefab : _windowPrefab;
                if (GUILayout.Button(new GUIContent($"Use {(mine != null ? mine.name : "The Default")} On All", "Every one of them gets the tool's prefab.")))
                {
                    Undo.RecordObject(_plan, "Swap prefab");
                    foreach (var o in all)
                        o.Prefab = mine;
                    Changed();
                }
                if (kind == Plan.OpeningKind.Door && GUILayout.Button(new GUIContent("Use These Settings On All", "Every door gets the settings above (and the hinge and swing).")))
                {
                    Undo.RecordObject(_plan, "Door settings");
                    foreach (var o in all)
                    {
                        o.Door = JsonUtility.FromJson<Plan.DoorSettings>(JsonUtility.ToJson(_doorSettings));
                        o.HingeRight = _doorHinge;
                        o.ReverseSwing = _doorReverse;
                    }
                    Changed();
                }
            }
        }

        private void DrawStairsInBuilding()
        {
            if (_plan.Stairs.Count == 0)
                return;
            EditorGUILayout.LabelField($"In this building ({_plan.Stairs.Count})", EditorStyles.miniBoldLabel);
            foreach (var group in _plan.Stairs.GroupBy(st => TarkovBuildingBuilder.StairPrefabOf(_plan, st)).ToList())
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"{(group.Key != null ? group.Key.name : "SDK steps")} x{group.Count()}", GUILayout.Width(170f));
                    EditorGUI.BeginChangeCheck();
                    var swap = (GameObject)EditorGUILayout.ObjectField(GUIContent.none, group.Key, typeof(GameObject), false);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(_plan, "Swap stairs");
                        foreach (var st in group)
                            st.Prefab = swap;
                        Changed();
                    }
                }
            if (GUILayout.Button($"Use {(_stairPrefab != null ? _stairPrefab.name : "The Default")} On All"))
            {
                Undo.RecordObject(_plan, "Swap stairs");
                foreach (var st in _plan.Stairs)
                {
                    st.Prefab = _stairPrefab;
                    st.FitToFlight = _stairFit;
                }
                Changed();
            }
        }

        private void DrawSettings()
        {
            var so = new SerializedObject(_plan);
            int oldWidth = _plan.Width;
            EditorGUI.BeginChangeCheck();
            foreach (var name in new[]
                     {
                         "Width", "Depth", "CellSize", "WallThickness", "InnerWallThickness", "SlabThickness", "Plinth", "Roof", "RoofPitch", "Overhang", "Parapet",
                         "DefaultDoor", "DefaultWindow", "DefaultStair", "Glass", "LampPrefab", "LampRange", "Frames", "DarknessVolumes", "AmbientPortals", "RainCover", "IndoorTriggers", "Lods",
                     })
                EditorGUILayout.PropertyField(so.FindProperty(name));
            if (EditorGUI.EndChangeCheck())
            {
                so.ApplyModifiedProperties();
                // the grid's cells stay where they were when it changes size
                foreach (var storey in _plan.Storeys)
                    _plan.Fit(storey, oldWidth);
                Changed();
            }
        }

        // ------------------------------------------------------------ the plan in the window

        private void DrawPlanGrid()
        {
            if (_plan.Storeys.Count == 0)
                return;
            float size = Mathf.Clamp((position.width - 30f) / Mathf.Max(_plan.Width, 1), 6f, 28f);
            var area = GUILayoutUtility.GetRect(_plan.Width * size, _plan.Depth * size, GUILayout.ExpandWidth(false));
            area.x += 8f;
            // +z up the screen
            Rect CellRect(int x, int z) => new Rect(area.x + x * size, area.y + (_plan.Depth - 1 - z) * size, size - 1f, size - 1f);
            if (Event.current.type == EventType.Repaint)
            {
                for (int z = 0; z < _plan.Depth; z++)
                    for (int x = 0; x < _plan.Width; x++)
                    {
                        int room = _plan.Get(_storey, x, z);
                        var below = _storey > 0 ? _plan.Get(_storey - 1, x, z) : 0;
                        var c = room > 0 && room <= _plan.Rooms.Count ? _plan.Rooms[room - 1].Color : below > 0 ? new Color(0.3f, 0.3f, 0.3f) : new Color(0.17f, 0.17f, 0.17f);
                        if (_dragFrom.HasValue && InDrag(x, z) && _tool == Tool.Rooms)
                            c = Color.Lerp(c, Event.current.shift || _room == 0 ? Color.black : RoomColor(_room), 0.6f);
                        EditorGUI.DrawRect(CellRect(x, z), c);
                    }
                // open ceilings: a dark hole in the cell (as they'd be after the drag being made)
                var open = _plan.OpenCeilings(_storey);
                for (int z = 0; z < _plan.Depth; z++)
                    for (int x = 0; x < _plan.Width; x++)
                        if (_dragFrom.HasValue && InDrag(x, z) && _tool == Tool.Ceiling ? !Event.current.shift && _plan.Get(_storey, x, z) != 0 : open.Contains(new Vector2Int(x, z)))
                            EditorGUI.DrawRect(CellRect(x, z).Inset(size * 0.15f), new Color(0.05f, 0.06f, 0.09f, 0.85f));
                foreach (var st in _plan.Stairs.Where(st => st.Storey == _storey))
                    foreach (var at in TarkovBuildingBuilder.StairFootprint(st))
                        if (at.x >= 0 && at.y >= 0 && at.x < _plan.Width && at.y < _plan.Depth)
                            EditorGUI.DrawRect(CellRect(at.x, at.y).Inset(size * 0.3f), Color.white);
                for (int i = 0; i < _plan.Openings.Count; i++)
                {
                    var o = _plan.Openings[i];
                    if (o.Storey != _storey)
                        continue;
                    int span = Mathf.Max(1, o.Span);
                    var r = o.AlongZ
                        ? new Rect(area.x + o.X * size - 2f, area.y + (_plan.Depth - o.Z - span) * size, 4f, span * size)
                        : new Rect(area.x + o.X * size, area.y + (_plan.Depth - o.Z) * size - 2f, span * size, 4f);
                    EditorGUI.DrawRect(r, _selOpenings.Contains(i) ? Color.white : KindColor(o.Kind));
                }
                // bevels' arcs and paint, in the window's space (metres to pixels, +z up the screen)
                float cell = _plan.CellSize;
                DrawExtras(p => new Vector3(area.x + p.x / cell * size, area.y + (_plan.Depth - p.y / cell) * size, 0f), size, true);
                DrawEditing(p => new Vector3(area.x + p.x / cell * size, area.y + (_plan.Depth - p.y / cell) * size, 0f), true);
            }
            var e = Event.current;
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
            {
                bool over = area.Contains(e.mousePosition);
                if (over)
                    _hover = new Vector2((e.mousePosition.x - area.x) / size, _plan.Depth - (e.mousePosition.y - area.y) / size);
                if (over || _hovering)
                    Repaint();
                _hovering = over;
            }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.R && _tool == Tool.Furniture)
            {
                _furnitureYaw = Mathf.Repeat(_furnitureYaw + (e.shift ? -90f : 90f), 360f);
                e.Use();
                Repaint();
                SceneView.RepaintAll();
            }
            // a drag carries on (and ends) outside the plan
            if (e.button != 0 || (!area.Contains(e.mousePosition) && !_dragFrom.HasValue))
                return;
            float fx = (e.mousePosition.x - area.x) / size, fz = _plan.Depth - (e.mousePosition.y - area.y) / size;
            Input(fx, fz, e);
        }

        private Color RoomColor(int room) => room > 0 && room <= _plan.Rooms.Count ? _plan.Rooms[room - 1].Color : Color.black;

        private static Color KindColor(Plan.OpeningKind kind) =>
            kind == Plan.OpeningKind.Door ? new Color(1f, 0.6f, 0.15f) : kind == Plan.OpeningKind.Window ? new Color(0.3f, 0.85f, 1f) : new Color(0.75f, 0.75f, 0.75f);

        private bool InDrag(int x, int z) =>
            _dragFrom.HasValue && x >= Mathf.Min(_dragFrom.Value.x, _dragTo.x) && x <= Mathf.Max(_dragFrom.Value.x, _dragTo.x) &&
            z >= Mathf.Min(_dragFrom.Value.y, _dragTo.y) && z <= Mathf.Max(_dragFrom.Value.y, _dragTo.y);

        // a mouse event at grid coordinates (cells, may be fractional): the same for the window's plan and the Scene view
        private bool Input(float fx, float fz, Event e)
        {
            int cx = Mathf.FloorToInt(fx), cz = Mathf.FloorToInt(fz);
            bool inside = cx >= 0 && cz >= 0 && cx < _plan.Width && cz < _plan.Depth;
            switch (_tool)
            {
                case Tool.Rooms:
                case Tool.Ceiling:
                    if (e.type == EventType.MouseDown && inside)
                    {
                        _dragFrom = new Vector2Int(cx, cz);
                        _dragTo = _dragFrom.Value;
                        e.Use();
                        Repaint();
                        return true;
                    }
                    if (e.type == EventType.MouseDrag && _dragFrom.HasValue)
                    {
                        _dragTo = new Vector2Int(Mathf.Clamp(cx, 0, _plan.Width - 1), Mathf.Clamp(cz, 0, _plan.Depth - 1));
                        e.Use();
                        Repaint();
                        SceneView.RepaintAll();
                        return true;
                    }
                    if (e.type == EventType.MouseUp && _dragFrom.HasValue && _tool == Tool.Ceiling)
                    {
                        Undo.RecordObject(_plan, "Open ceilings");
                        var storey = _plan.Storeys[_storey];
                        storey.OpenCeiling ??= new List<Vector2Int>();
                        for (int z = 0; z < _plan.Depth; z++)
                            for (int x = 0; x < _plan.Width; x++)
                            {
                                var at = new Vector2Int(x, z);
                                if (!InDrag(x, z))
                                    continue;
                                storey.OpenCeiling.Remove(at);
                                if (!e.shift && _plan.Get(_storey, x, z) != 0)
                                    storey.OpenCeiling.Add(at);
                            }
                        _dragFrom = null;
                        e.Use();
                        Changed();
                        return true;
                    }
                    if (e.type == EventType.MouseUp && _dragFrom.HasValue)
                    {
                        Undo.RecordObject(_plan, "Paint rooms");
                        int room = e.shift ? 0 : _room;
                        for (int z = 0; z < _plan.Depth; z++)
                            for (int x = 0; x < _plan.Width; x++)
                                if (InDrag(x, z))
                                    _plan.Set(_storey, x, z, room);
                        _dragFrom = null;
                        e.Use();
                        Changed();
                        return true;
                    }
                    break;
                case Tool.Select:
                    return SelectInput(fx, fz, e);
                case Tool.Furniture:
                    return FurnitureInput(fx, fz, e);
                case Tool.Holes:
                    // holes are cut in the Scene view; on the plan a click selects one
                    if (e.type != EventType.MouseDown)
                        break;
                    ClearSelection();
                    int over = HoleAt(fx, fz);
                    if (over >= 0)
                        _selHoles.Add(over);
                    e.Use();
                    Repaint();
                    SceneView.RepaintAll();
                    return true;
                case Tool.Doors:
                case Tool.Windows:
                case Tool.Gaps:
                    if (e.type != EventType.MouseDown)
                        break;
                    var edge = NearestEdge(fx, fz);
                    if (edge == null)
                        break;
                    var (ex, ez, alongZ) = edge.Value;
                    int hit = OpeningAt(fx, fz);
                    Undo.RecordObject(_plan, "Opening");
                    var kind = _tool == Tool.Doors ? Plan.OpeningKind.Door : _tool == Tool.Windows ? Plan.OpeningKind.Window : Plan.OpeningKind.Gap;
                    if (hit >= 0 && _plan.Openings[hit].Kind == kind)
                        _plan.Openings.RemoveAt(hit);
                    else
                    {
                        if (hit >= 0)
                            _plan.Openings.RemoveAt(hit);
                        bool window = kind == Plan.OpeningKind.Window;
                        bool door = kind == Plan.OpeningKind.Door;
                        _plan.Openings.Add(new Plan.Opening
                        {
                            Storey = _storey, X = ex, Z = ez, AlongZ = alongZ, Kind = kind, Span = _span,
                            Width = Mathf.Min(window ? _windowWidth : _doorWidth, _span * _plan.CellSize - 0.1f),
                            Height = window ? _windowHeight : _doorHeight,
                            Sill = window ? _windowSill : 0f,
                            Prefab = door ? _doorPrefab : window ? _windowPrefab : null,
                            HingeRight = door && _doorHinge,
                            ReverseSwing = door && _doorReverse,
                            Door = door ? JsonUtility.FromJson<Plan.DoorSettings>(JsonUtility.ToJson(_doorSettings)) : new Plan.DoorSettings(),
                        });
                        SelectOnly(_selOpenings, _plan.Openings.Count - 1);
                    }
                    e.Use();
                    Changed();
                    return true;
                case Tool.Bevel:
                    if (e.type != EventType.MouseDown || !inside)
                        break;
                    ToggleBevel(cx, cz, fx, fz);
                    e.Use();
                    return true;
                case Tool.Paint:
                    if (e.type == EventType.MouseUp && _painting)
                    {
                        _painting = false;
                        Undo.CollapseUndoOperations(Undo.GetCurrentGroup());
                        e.Use();
                        return true;
                    }
                    if (e.type != EventType.MouseDown && !(e.type == EventType.MouseDrag && _painting))
                        break;
                    if (e.type == EventType.MouseDown)
                    {
                        _painting = true;
                        Undo.IncrementCurrentGroup();
                    }
                    PaintAt(fx, fz, e.shift);
                    e.Use();
                    return true;
                case Tool.Stairs:
                    if (e.type != EventType.MouseDown || !inside)
                        break;
                    Undo.RecordObject(_plan, "Stairs");
                    int existing = _plan.Stairs.FindIndex(st => st.Storey == _storey && TarkovBuildingBuilder.StairFootprint(st).Contains(new Vector2Int(cx, cz)));
                    if (existing >= 0)
                        _plan.Stairs.RemoveAt(existing);
                    else
                        _plan.Stairs.Add(new Plan.Stair { Storey = _storey, X = cx, Z = cz, Direction = _stairDirection, Length = _stairLength, Width = _stairWidth, Prefab = _stairPrefab, FitToFlight = _stairFit });
                    e.Use();
                    Changed();
                    return true;
            }
            return false;
        }

        // the edge nearest the point: a line of x (along z) or of z (along x), and which cell along it
        private (int X, int Z, bool AlongZ)? NearestEdge(float fx, float fz)
        {
            float dx = Mathf.Abs(fx - Mathf.Round(fx)), dz = Mathf.Abs(fz - Mathf.Round(fz));
            if (dx > 0.35f && dz > 0.35f)
                return null;
            if (dx < dz)
            {
                int x = Mathf.RoundToInt(fx), z = Mathf.FloorToInt(fz);
                return x >= 0 && x <= _plan.Width && z >= 0 && z < _plan.Depth ? (x, z, true) : ((int, int, bool)?)null;
            }
            else
            {
                int x = Mathf.FloorToInt(fx), z = Mathf.RoundToInt(fz);
                return x >= 0 && x < _plan.Width && z >= 0 && z <= _plan.Depth ? (x, z, false) : ((int, int, bool)?)null;
            }
        }

        // ------------------------------------------------------------ scene view

        private void OnSceneGUI(SceneView view)
        {
            // the plan's overlay and tools only while planning (not over the materials being looked at)
            if (_plan == null || !Editable || _plan.Storeys.Count == 0 || _tab != Tab.Plan)
                return;
            var root = TarkovBuildingBuilder.Building(_plan);
            if (root.gameObject.scene != (PrefabStageUtility.GetCurrentPrefabStage()?.scene ?? root.gameObject.scene))
                return;
            ValidateSelection();
            if (EditCommands(Event.current))
                return;
            float cell = _plan.CellSize;
            float y = _plan.Base(_storey) + 0.03f;
            using (new Handles.DrawingScope(root.localToWorldMatrix))
            {
                // the storey's rooms, the grid, its openings and stairs
                for (int z = 0; z < _plan.Depth; z++)
                    for (int x = 0; x < _plan.Width; x++)
                    {
                        int room = _plan.Get(_storey, x, z);
                        bool drag = _dragFrom.HasValue && InDrag(x, z) && _tool == Tool.Rooms;
                        if (room == 0 && !drag)
                            continue;
                        var c = drag ? (Event.current.shift || _room == 0 ? new Color(0f, 0f, 0f, 0.5f) : RoomColor(_room)) : RoomColor(room);
                        c.a = drag ? 0.55f : 0.25f;
                        var quad = new[] { new Vector3(x * cell, y, z * cell), new Vector3(x * cell, y, (z + 1) * cell), new Vector3((x + 1) * cell, y, (z + 1) * cell), new Vector3((x + 1) * cell, y, z * cell) };
                        Handles.DrawSolidRectangleWithOutline(quad, c, new Color(c.r, c.g, c.b, 0.6f));
                    }
                // open ceilings (and the drag being made with the Ceiling tool): a cross on the cell
                var open = _plan.OpenCeilings(_storey);
                Handles.color = new Color(0.4f, 0.85f, 1f, 0.9f);
                for (int z = 0; z < _plan.Depth; z++)
                    for (int x = 0; x < _plan.Width; x++)
                        if (_dragFrom.HasValue && InDrag(x, z) && _tool == Tool.Ceiling ? !Event.current.shift && _plan.Get(_storey, x, z) != 0 : open.Contains(new Vector2Int(x, z)))
                        {
                            float i = cell * 0.15f;
                            Handles.DrawAAPolyLine(3f, new Vector3(x * cell + i, y, z * cell + i), new Vector3((x + 1) * cell - i, y, (z + 1) * cell - i));
                            Handles.DrawAAPolyLine(3f, new Vector3(x * cell + i, y, (z + 1) * cell - i), new Vector3((x + 1) * cell - i, y, z * cell + i));
                        }
                Handles.color = new Color(1f, 1f, 1f, 0.15f);
                for (int x = 0; x <= _plan.Width; x++)
                    Handles.DrawLine(new Vector3(x * cell, y, 0f), new Vector3(x * cell, y, _plan.Depth * cell));
                for (int z = 0; z <= _plan.Depth; z++)
                    Handles.DrawLine(new Vector3(0f, y, z * cell), new Vector3(_plan.Width * cell, y, z * cell));
                for (int i = 0; i < _plan.Openings.Count; i++)
                {
                    var o = _plan.Openings[i];
                    if (o.Storey != _storey)
                        continue;
                    int span = Mathf.Max(1, o.Span);
                    var a = o.AlongZ ? new Vector3(o.X * cell, y, o.Z * cell) : new Vector3(o.X * cell, y, o.Z * cell);
                    var b = o.AlongZ ? new Vector3(o.X * cell, y, (o.Z + span) * cell) : new Vector3((o.X + span) * cell, y, o.Z * cell);
                    Handles.color = _selOpenings.Contains(i) ? Color.white : KindColor(o.Kind);
                    Handles.DrawAAPolyLine(8f, a, b);
                }
                foreach (var st in _plan.Stairs.Where(st => st.Storey == _storey))
                {
                    var d = new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left }[((st.Direction % 4) + 4) % 4];
                    var from = new Vector3((st.X + 0.5f) * cell, y, (st.Z + 0.5f) * cell);
                    Handles.color = Color.white;
                    Handles.DrawAAPolyLine(4f, from, from + d * (Mathf.Max(1, st.Length) - 1) * cell);
                    Handles.ConeHandleCap(0, from + d * (Mathf.Max(1, st.Length) - 1) * cell, Quaternion.LookRotation(d), cell * 0.4f, EventType.Repaint);
                }
                if (Event.current.type == EventType.Repaint)
                {
                    DrawExtras(p => new Vector3(p.x, y + 0.02f, p.y), 30f, false);
                    DrawEditing(p => new Vector3(p.x, y + 0.04f, p.y), false);
                }
            }

            if (_tool == Tool.Select && PrefabHandles(root))
                return;
            if (EditHandles(root))
                return;

            // the mouse on the storey's floor
            var e = Event.current;
            // the camera keeps Alt, the right and the middle button
            if (e.alt)
                return;
            bool mouse = e.type == EventType.MouseDown || e.type == EventType.MouseDrag || e.type == EventType.MouseUp;
            if (mouse && e.button != 0)
                return;
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            if (_tool == Tool.Holes)
            {
                HoleScene(root, e);
                return;
            }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.R && _tool == Tool.Furniture)
            {
                _furnitureYaw = Mathf.Repeat(_furnitureYaw + (e.shift ? -90f : 90f), 360f);
                e.Use();
                Repaint();
                SceneView.RepaintAll();
                return;
            }
            var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            var plane = new Plane(root.up, root.TransformPoint(new Vector3(0f, y, 0f)));
            if (!plane.Raycast(ray, out float distance))
                return;
            var local = root.InverseTransformPoint(ray.GetPoint(distance));
            float fx = local.x / cell, fz = local.z / cell;
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
            {
                _hover = new Vector2(fx, fz);
                _hovering = true;
                if (_tool == Tool.Furniture || _pasting)
                    Repaint();
            }
            // a hint of what a click does here
            if (e.type == EventType.Repaint)
                using (new Handles.DrawingScope(root.localToWorldMatrix))
                {
                    if (_tool == Tool.Bevel)
                    {
                        // the corner a click would round
                        int cx = Mathf.FloorToInt(fx), cz = Mathf.FloorToInt(fz);
                        if (cx >= 0 && cz >= 0 && cx < _plan.Width && cz < _plan.Depth)
                        {
                            int vx = fx - cx < 0.5f ? cx : cx + 1, vz = fz - cz < 0.5f ? cz : cz + 1;
                            Handles.color = new Color(1f, 0.85f, 0.3f);
                            var v = new Vector3(vx * cell, y, vz * cell);
                            var c = new Vector3((cx + 0.5f) * cell, y, (cz + 0.5f) * cell);
                            Handles.DrawAAPolyLine(4f, v, Vector3.Lerp(v, c, 0.7f));
                            Handles.DrawWireDisc(v, Vector3.up, cell * 0.15f);
                        }
                    }
                    else if (_tool == Tool.Select || _tool == Tool.Furniture)
                    {
                        // their own previews (DrawEditing)
                    }
                    else if (_tool != Tool.Rooms && _tool != Tool.Stairs && _tool != Tool.Ceiling)
                    {
                        var edge = NearestEdge(fx, fz);
                        if (edge != null)
                        {
                            var (ex, ez, alongZ) = edge.Value;
                            int span = _tool == Tool.Paint ? 1 : _span;
                            Handles.color = _tool == Tool.Paint ? Swatch(_brush) :
                                KindColor(_tool == Tool.Doors ? Plan.OpeningKind.Door : _tool == Tool.Windows ? Plan.OpeningKind.Window : Plan.OpeningKind.Gap);
                            var a = new Vector3(ex * cell, y, ez * cell);
                            var b = alongZ ? a + Vector3.forward * cell * span : a + Vector3.right * cell * span;
                            Handles.DrawAAPolyLine(4f, a, b);
                        }
                    }
                    else
                    {
                        int cx = Mathf.FloorToInt(fx), cz = Mathf.FloorToInt(fz);
                        if (cx >= 0 && cz >= 0 && cx < _plan.Width && cz < _plan.Depth)
                        {
                            Handles.color = Color.white;
                            var c = new Vector3((cx + 0.5f) * cell, y, (cz + 0.5f) * cell);
                            Handles.DrawWireCube(c, new Vector3(cell, 0.02f, cell));
                        }
                    }
                }
            if (mouse)
                Input(fx, fz, e);
            if (e.type == EventType.MouseMove)
                SceneView.RepaintAll();
        }

        // The selected opening's prefab: a move handle and a turn disc at its place in the wall, writing the plan's
        // offset (along the wall, up, out of it) and turn, so they last through rebuilds. True while one is being used.
        private bool PrefabHandles(Transform root)
        {
            int selected = SingleOpening;
            if (selected < 0)
                return false;
            var o = _plan.Openings[selected];
            if (TarkovBuildingBuilder.PrefabOf(_plan, o) == null || !TarkovBuildingBuilder.Locate(_plan, o, out var bottom, out var outward, out var along, out _, out _))
                return false;
            var basePoint = root.TransformPoint(bottom);
            var at = root.TransformPoint(bottom + along * o.PrefabOffset.x + Vector3.up * o.PrefabOffset.y + outward * o.PrefabOffset.z);
            var frame = root.rotation * Quaternion.LookRotation(outward, Vector3.up) * Quaternion.AngleAxis(o.PrefabTurn, Vector3.up);
            EditorGUI.BeginChangeCheck();
            var moved = Handles.PositionHandle(at, frame);
            Handles.color = new Color(1f, 0.85f, 0.3f);
            var turned = Handles.Disc(frame, at, root.up, HandleUtility.GetHandleSize(at) * 0.9f, false, 5f);
            if (!EditorGUI.EndChangeCheck())
                return false;
            Undo.RecordObject(_plan, "Adjust prefab");
            var local = root.InverseTransformVector(moved - basePoint);
            o.PrefabOffset = new Vector3(Vector3.Dot(local, along), local.y, Vector3.Dot(local, outward));
            var forward = Quaternion.Inverse(root.rotation) * (turned * Vector3.forward);
            o.PrefabTurn = Mathf.Round(Vector3.SignedAngle(outward, Vector3.ProjectOnPlane(forward, Vector3.up), Vector3.up));
            Changed();
            return true;
        }

        // ------------------------------------------------------------ prefab

        private void SavePrefab()
        {
            if (_plan == null)
                return;
            var root = TarkovBuildingBuilder.Building(_plan).gameObject;
            var folder = string.IsNullOrEmpty(root.scene.path) ? "Assets" : System.IO.Path.GetDirectoryName(root.scene.path).Replace('\\', '/');
            var path = EditorUtility.SaveFilePanelInProject("Save Building Prefab", root.name, "prefab", "Where to save the building", folder);
            if (string.IsNullOrEmpty(path))
                return;
            if (TarkovBuildingBuilder.Hash(_plan) != _plan.BuiltHash)
                TarkovBuildingBuilder.Build(_plan);
            var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(root, path, InteractionMode.UserAction);
            _report = prefab != null ? $"Saved {path}. Place copies of it anywhere; to change it, select a copy and Open Prefab." : "The prefab wasn't saved (see the Console).";
            Repaint();
        }
    }

    internal static class RectInset
    {
        public static Rect Inset(this Rect r, float by) => new Rect(r.x + by, r.y + by, Mathf.Max(1f, r.width - by * 2f), Mathf.Max(1f, r.height - by * 2f));
    }
}
