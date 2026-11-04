using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Plan = TarkovSDK.TarkovBuilding;

namespace TarkovSDK.Editor
{
    // The Building Builder window's bevel and paint tools, its Trims tab (trims and a profile editor: drag points,
    // double-click a line to add one, right-click one to remove it) and its Materials tab (what each part uses,
    // each storey's outside, how a material's texture is laid, its shader's properties to edit, new materials and
    // building-only copies).
    public partial class TarkovBuildingBuilderWindow
    {
        private string _bevelNote;
        private int _trim = -1;
        private int _dragPoint = -1;
        // the profile editor's view in metres (x out from the wall, y up), held while a point is dragged
        private Rect _profileView;
        private bool _showPoints;
        private Material _editing;
        private string _newMaterial = "Wall";
        private Material _showOptions;

        // ------------------------------------------------------------ bevels

        private void DrawBevelOptions()
        {
            _bevelKind = (Plan.BevelKind)EditorGUILayout.EnumPopup(new GUIContent("Kind", "Round: an arc. Chamfer: one straight cut across the corner."), _bevelKind);
            _bevelRadius = Mathf.Round(EditorGUILayout.Slider(new GUIContent("Radius", "In cells along each wall from the corner."), _bevelRadius, 0.5f, 12f) * 4f) / 4f;
            if (_bevelKind == Plan.BevelKind.Round)
                _bevelSegments = EditorGUILayout.IntSlider(new GUIContent("Segments", "Straight pieces in the arc (more: smoother, more triangles)."), _bevelSegments, 2, 24);
            if (!string.IsNullOrEmpty(_bevelNote))
                EditorGUILayout.HelpBox(_bevelNote, MessageType.Warning);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                _room = RoomPopup("Room", _room, false);
                _bevelFit = GUILayout.Toggle(_bevelFit, new GUIContent("Fit", "Round Room uses half the shorter wall at each corner (a square room becomes round, a long one gets rounded ends) instead of the radius."), "Button", GUILayout.Width(36f));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_room <= 0))
                {
                    if (GUILayout.Button(new GUIContent("Round Room", "Bevels every outward corner of the room on this storey.")))
                    {
                        Undo.RecordObject(_plan, "Round room");
                        int n = TarkovBuildingBuilder.RoundRoom(_plan, _storey, _room, _bevelKind, _bevelFit ? 0f : _bevelRadius, _bevelSegments);
                        _bevelNote = n == 0 ? "None of the room's corners has room for a bevel." : null;
                        Changed();
                    }
                    if (GUILayout.Button(new GUIContent("Square Room", "Removes the room's bevels on this storey.")))
                    {
                        Undo.RecordObject(_plan, "Square room");
                        var corners = TarkovBuildingBuilder.RoomCorners(_plan, _storey, _room);
                        _plan.Bevels.RemoveAll(b => b.Storey == _storey && corners.Any(c => c.X == b.X && c.Z == b.Z && c.Quadrant == ((b.Quadrant % 4) + 4) % 4));
                        Changed();
                    }
                }
            }
            if (GUILayout.Button(new GUIContent("Copy To Every Storey", "Every storey gets this storey's bevels (where its rooms allow them).")))
            {
                Undo.RecordObject(_plan, "Copy bevels");
                var mine = _plan.Bevels.Where(b => b.Storey == _storey).ToList();
                _plan.Bevels.RemoveAll(b => b.Storey != _storey);
                for (int s = 0; s < _plan.Storeys.Count; s++)
                    if (s != _storey)
                        foreach (var b in mine)
                            _plan.Bevels.Add(new Plan.Bevel { Storey = s, X = b.X, Z = b.Z, Quadrant = b.Quadrant, Kind = b.Kind, Radius = b.Radius, Segments = b.Segments });
                Changed();
            }

            // this storey's, each editable
            var notes = new List<string>();
            var fitting = TarkovBuildingBuilder.Corners(_plan, _storey, notes);
            var list = _plan.Bevels.Where(b => b.Storey == _storey).ToList();
            if (list.Count > 0)
                EditorGUILayout.LabelField("This storey's bevels", EditorStyles.miniBoldLabel);
            foreach (var b in list)
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool ok = fitting.Any(c => c.Source == b);
                    GUILayout.Label(new GUIContent($"{b.X},{b.Z}", ok ? "" : "Doesn't fit its room: see the note below."), ok ? EditorStyles.label : EditorStyles.boldLabel, GUILayout.Width(44f));
                    EditorGUI.BeginChangeCheck();
                    var kind = (Plan.BevelKind)EditorGUILayout.EnumPopup(b.Kind, GUILayout.Width(70f));
                    float radius = EditorGUILayout.FloatField(b.Radius, GUILayout.Width(40f));
                    int segments = b.Kind == Plan.BevelKind.Round ? EditorGUILayout.IntField(b.Segments, GUILayout.Width(30f)) : b.Segments;
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(_plan, "Bevel");
                        b.Kind = kind;
                        b.Radius = Mathf.Max(0.25f, radius);
                        b.Segments = Mathf.Clamp(segments, 2, 24);
                        Changed();
                    }
                    if (!ok)
                        GUILayout.Label(EditorGUIUtility.IconContent("console.warnicon.sml"), GUILayout.Width(18f));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("X", GUILayout.Width(22f)))
                    {
                        Undo.RecordObject(_plan, "Remove bevel");
                        _plan.Bevels.Remove(b);
                        Changed();
                        GUIUtility.ExitGUI();
                    }
                }
            foreach (var note in notes)
                EditorGUILayout.HelpBox(note, MessageType.Warning);
        }

        // a click in a cell near one of its corners: that corner of the cell's room, rounded or squared again
        private void ToggleBevel(int cx, int cz, float fx, float fz)
        {
            int vx = fx - cx < 0.5f ? cx : cx + 1, vz = fz - cz < 0.5f ? cz : cz + 1;
            int q = cx >= vx ? (cz >= vz ? 0 : 3) : (cz >= vz ? 1 : 2);
            Undo.RecordObject(_plan, "Bevel");
            int i = _plan.Bevels.FindIndex(b => b.Storey == _storey && b.X == vx && b.Z == vz && ((b.Quadrant % 4) + 4) % 4 == q);
            _bevelNote = null;
            if (i >= 0)
                _plan.Bevels.RemoveAt(i);
            else
            {
                var b = new Plan.Bevel { Storey = _storey, X = vx, Z = vz, Quadrant = q, Kind = _bevelKind, Radius = _bevelRadius, Segments = _bevelSegments };
                _plan.Bevels.Add(b);
                var notes = new List<string>();
                if (!TarkovBuildingBuilder.Corners(_plan, _storey, notes).Any(c => c.Source == b))
                    _bevelNote = notes.LastOrDefault() ?? "That corner can't take a bevel.";
            }
            Changed();
        }

        // ------------------------------------------------------------ paint

        private void DrawPaintOptions()
        {
            _brush = (Material)EditorGUILayout.ObjectField(new GUIContent("Material", "What a click paints on the wall's side."), _brush, typeof(Material), false);
            Palette(m => _brush = m, _brush);
            using (new EditorGUILayout.HorizontalScope())
            {
                _room = RoomPopup("Room", _room, false);
                using (new EditorGUI.DisabledScope(_brush == null || _room <= 0))
                    if (GUILayout.Button(new GUIContent("Paint Its Walls", "Every wall's side facing the room on this storey.")))
                    {
                        Undo.RecordObject(_plan, "Paint room");
                        PaintRoom(_storey, _room, _brush);
                        Changed();
                    }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var outside = (Material)EditorGUILayout.ObjectField(new GUIContent("This Storey's Outside", "The outside walls of this storey (empty: the building's)."), _plan.Storeys[_storey].Exterior, typeof(Material), false);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(_plan, "Storey outside");
                    _plan.Storeys[_storey].Exterior = outside;
                    Changed();
                }
            }
            int painted = _plan.Paints.Count(p => p.Storey == _storey);
            using (new EditorGUI.DisabledScope(painted == 0))
                if (GUILayout.Button($"Clear This Storey's Paint ({painted})"))
                {
                    Undo.RecordObject(_plan, "Clear paint");
                    _plan.Paints.RemoveAll(p => p.Storey == _storey);
                    Changed();
                }
        }

        private void PaintAt(float fx, float fz, bool erase)
        {
            var edge = NearestEdge(fx, fz);
            if (edge == null)
                return;
            var (ex, ez, alongZ) = edge.Value;
            bool plus = alongZ ? fx > ex : fz > ez;
            int i = _plan.Paints.FindIndex(p => p.Storey == _storey && p.X == ex && p.Z == ez && p.AlongZ == alongZ && p.Plus == plus);
            if (erase)
            {
                if (i < 0)
                    return;
                Undo.RecordObject(_plan, "Paint wall");
                _plan.Paints.RemoveAt(i);
                Changed();
                return;
            }
            if (_brush == null || (i >= 0 && _plan.Paints[i].Material == _brush))
                return;
            Undo.RecordObject(_plan, "Paint wall");
            if (i >= 0)
                _plan.Paints[i].Material = _brush;
            else
                _plan.Paints.Add(new Plan.WallPaint { Storey = _storey, X = ex, Z = ez, AlongZ = alongZ, Plus = plus, Material = _brush });
            Changed();
        }

        // every wall's side facing the room on the storey
        private void PaintRoom(int s, int room, Material material)
        {
            void Set(int x, int z, bool alongZ, bool plus)
            {
                _plan.Paints.RemoveAll(p => p.Storey == s && p.X == x && p.Z == z && p.AlongZ == alongZ && p.Plus == plus);
                _plan.Paints.Add(new Plan.WallPaint { Storey = s, X = x, Z = z, AlongZ = alongZ, Plus = plus, Material = material });
            }
            for (int z = 0; z <= _plan.Depth; z++)
                for (int x = 0; x < _plan.Width; x++)
                {
                    int a = _plan.Get(s, x, z - 1), b = _plan.Get(s, x, z);
                    if (a != b && b == room) Set(x, z, false, true);
                    if (a != b && a == room) Set(x, z, false, false);
                }
            for (int x = 0; x <= _plan.Width; x++)
                for (int z = 0; z < _plan.Depth; z++)
                {
                    int a = _plan.Get(s, x - 1, z), b = _plan.Get(s, x, z);
                    if (a != b && b == room) Set(x, z, true, true);
                    if (a != b && a == room) Set(x, z, true, false);
                }
        }

        // the building's materials as buttons
        private void Palette(Action<Material> pick, Material current)
        {
            var used = TarkovBuildingBuilder.UsedMaterials(_plan);
            if (used.Count == 0)
                return;
            int columns = Mathf.Max(1, Mathf.FloorToInt((position.width - 40f) / 44f));
            for (int row = 0; row * columns < used.Count; row++)
                using (new EditorGUILayout.HorizontalScope())
                {
                    for (int c = 0; c < columns && row * columns + c < used.Count; c++)
                    {
                        var m = used[row * columns + c];
                        var preview = AssetPreview.GetAssetPreview(m);
                        var content = preview != null ? new GUIContent(preview, m.name) : new GUIContent(m.name.Length > 5 ? m.name.Substring(0, 5) : m.name, m.name);
                        var r = GUILayoutUtility.GetRect(40f, 40f, GUILayout.Width(40f), GUILayout.Height(40f));
                        if (GUI.Button(r, content))
                            pick(m);
                        if (m == current && Event.current.type == EventType.Repaint)
                        {
                            Handles.color = Color.white;
                            Handles.DrawSolidRectangleWithOutline(r, Color.clear, Color.white);
                        }
                    }
                    GUILayout.FlexibleSpace();
                }
            // previews load a frame or two later
            if (AssetPreview.IsLoadingAssetPreviews())
                Repaint();
        }

        private static Color Swatch(Material m)
        {
            if (m == null)
                return new Color(0.6f, 0.6f, 0.6f);
            var c = m.HasProperty("_Color") ? m.color : Color.white;
            c.a = 1f;
            return c;
        }

        // bevels' arcs (and the corners of ones that don't fit, crossed out) and wall paint, through map (metres in
        // the plan to drawing space); pixels: how big a cell is drawn, for the paint's offset in the window
        private void DrawExtras(Func<Vector2, Vector3> map, float pixels, bool gui)
        {
            float cell = _plan.CellSize;
            float off = gui ? 3f / Mathf.Max(1f, pixels) * cell : cell * 0.07f;
            foreach (var p in _plan.Paints)
            {
                if (p.Storey != _storey || p.Material == null)
                    continue;
                var a = new Vector2(p.X * cell, p.Z * cell);
                var along = p.AlongZ ? new Vector2(0f, cell) : new Vector2(cell, 0f);
                var n = (p.AlongZ ? new Vector2(1f, 0f) : new Vector2(0f, 1f)) * (p.Plus ? off : -off);
                Handles.color = Swatch(p.Material);
                Handles.DrawAAPolyLine(gui ? 3f : 5f, map(a + n + along * 0.08f), map(a + n + along * 0.92f));
            }
            var fitting = TarkovBuildingBuilder.Corners(_plan, _storey);
            Handles.color = new Color(1f, 0.85f, 0.3f);
            foreach (var c in fitting)
                Handles.DrawAAPolyLine(gui ? 3f : 6f, c.Arc.Select(map).ToArray());
            Handles.color = new Color(1f, 0.3f, 0.25f);
            foreach (var b in _plan.Bevels)
            {
                if (b.Storey != _storey || fitting.Any(c => c.Source == b))
                    continue;
                var v = new Vector2(b.X * cell, b.Z * cell);
                float d = cell * 0.2f;
                Handles.DrawAAPolyLine(gui ? 2f : 4f, map(v + new Vector2(-d, -d)), map(v + new Vector2(d, d)));
                Handles.DrawAAPolyLine(gui ? 2f : 4f, map(v + new Vector2(-d, d)), map(v + new Vector2(d, -d)));
            }
        }

        // a room by name (0: "every room" when allowed)
        private int RoomPopup(string label, int room, bool every)
        {
            var names = new List<string>();
            if (every)
                names.Add("Every Room");
            names.AddRange(_plan.Rooms.Select((r, i) => $"{i + 1}  {r.Name}"));
            if (names.Count == 0)
                return 0;
            int index = every ? Mathf.Clamp(room, 0, _plan.Rooms.Count) : Mathf.Clamp(room - 1, 0, _plan.Rooms.Count - 1);
            index = EditorGUILayout.Popup(label, index, names.ToArray());
            return every ? index : index + 1;
        }

        // ------------------------------------------------------------ trims

        private void DrawTrims()
        {
            EditorGUILayout.LabelField("Mouldings swept along the walls: inside every room (or one), or round the building's outside, mitred at corners, following bevels and " +
                                       "stopping at doors and windows they'd cross. Start from a preset and change its profile below.", EditorStyles.wordWrappedMiniLabel);
            var presets = TarkovBuildingBuilder.TrimPresets;
            int pick = GUILayout.SelectionGrid(-1, presets.Select(p => p.Name).Concat(new[] { "Empty" }).ToArray(), 3);
            if (pick >= 0)
            {
                Undo.RecordObject(_plan, "Add trim");
                var t = pick < presets.Length ? presets[pick].Make(_plan) : new Plan.Trim { Name = "Trim", Profile = new[] { new Vector2(0f, 0f), new Vector2(0.03f, 0f), new Vector2(0.03f, 0.05f), new Vector2(0f, 0.05f) } };
                _plan.Trims.Add(t);
                _trim = _plan.Trims.Count - 1;
                Changed();
            }
            EditorGUILayout.Space();
            for (int i = 0; i < _plan.Trims.Count; i++)
            {
                var t = _plan.Trims[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    bool on = EditorGUILayout.Toggle(t.Enabled, GUILayout.Width(16f));
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(_plan, "Trim");
                        t.Enabled = on;
                        Changed();
                    }
                    if (GUILayout.Toggle(_trim == i, $"{t.Name}  ({t.Side.ToString().ToLowerInvariant()}, {t.Storeys.ToString().ToLowerInvariant()})", "Button"))
                        _trim = i;
                    if (GUILayout.Button(new GUIContent("Copy", "Another one like it."), GUILayout.Width(42f)))
                    {
                        Undo.RecordObject(_plan, "Copy trim");
                        var copy = JsonUtility.FromJson<Plan.Trim>(JsonUtility.ToJson(t));
                        copy.Material = t.Material;
                        copy.Name += " copy";
                        _plan.Trims.Insert(i + 1, copy);
                        _trim = i + 1;
                        Changed();
                        GUIUtility.ExitGUI();
                    }
                    if (GUILayout.Button("X", GUILayout.Width(22f)))
                    {
                        Undo.RecordObject(_plan, "Remove trim");
                        _plan.Trims.RemoveAt(i);
                        _trim = Mathf.Min(_trim, _plan.Trims.Count - 1);
                        Changed();
                        GUIUtility.ExitGUI();
                    }
                }
            }
            if (_trim >= 0 && _trim < _plan.Trims.Count)
                DrawTrim(_plan.Trims[_trim]);
        }

        private void DrawTrim(Plan.Trim t)
        {
            EditorGUILayout.Space();
            var so = new SerializedObject(_plan);
            var prop = so.FindProperty("Trims").GetArrayElementAtIndex(_trim);
            EditorGUI.BeginChangeCheck();
            foreach (var field in new[] { "Name", "Side", "Anchor", "Height", "Storeys", "Material" })
                EditorGUILayout.PropertyField(prop.FindPropertyRelative(field));
            if (EditorGUI.EndChangeCheck())
            {
                so.ApplyModifiedProperties();
                Changed();
            }
            if (t.Side == Plan.TrimSide.Inside)
            {
                EditorGUI.BeginChangeCheck();
                int room = RoomPopup("Room", t.Room, true);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(_plan, "Trim room");
                    t.Room = room;
                    Changed();
                }
            }

            EditorGUILayout.LabelField("Profile (out from the wall, up; drag points, double-click a line to add one, right-click one to remove it, Ctrl: no snapping)", EditorStyles.wordWrappedMiniLabel);
            DrawProfile(t);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Smooth", "Rounds its corners (each line cut into three).")))
                    EditProfile(t, "Smooth profile", Smooth);
                if (GUILayout.Button(new GUIContent("Flip", "Upside down (a baseboard into a crown, say).")))
                    EditProfile(t, "Flip profile", p =>
                    {
                        float lo = p.Min(q => q.y), hi = p.Max(q => q.y);
                        return p.Select(q => new Vector2(q.x, lo + hi - q.y)).Reverse().ToArray();
                    });
                if (GUILayout.Button(new GUIContent("Shape Of...", "Takes a preset's profile.")))
                {
                    var menu = new GenericMenu();
                    foreach (var (name, make) in TarkovBuildingBuilder.TrimPresets)
                        menu.AddItem(new GUIContent(name), false, () => EditProfile(t, "Profile preset", _ => make(_plan).Profile));
                    menu.ShowAsContext();
                }
            }
            if (t.Profile != null && t.Profile.Length >= 2)
            {
                float width = t.Profile.Max(p => p.x), height = t.Profile.Max(p => p.y) - t.Profile.Min(p => p.y);
                EditorGUI.BeginChangeCheck();
                float w = EditorGUILayout.FloatField(new GUIContent("Depth (m)", "How far it stands out of the wall."), width);
                float h = EditorGUILayout.FloatField(new GUIContent("Tall (m)"), height);
                if (EditorGUI.EndChangeCheck() && w > 0.001f && h > 0.001f)
                {
                    float sx = width > 1e-4f ? w / width : 1f, sy = height > 1e-4f ? h / height : 1f;
                    float lo = t.Profile.Min(p => p.y);
                    EditProfile(t, "Profile size", p => p.Select(q => new Vector2(q.x * sx, lo + (q.y - lo) * sy)).ToArray());
                }
            }
            _showPoints = EditorGUILayout.Foldout(_showPoints, "Points", true);
            if (_showPoints)
            {
                var p = so.FindProperty("Trims").GetArrayElementAtIndex(_trim).FindPropertyRelative("Profile");
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(p, true);
                if (EditorGUI.EndChangeCheck())
                {
                    so.ApplyModifiedProperties();
                    Changed();
                }
            }
        }

        private void EditProfile(Plan.Trim t, string undo, Func<Vector2[], Vector2[]> change)
        {
            Undo.RecordObject(_plan, undo);
            t.Profile = change(t.Profile ?? Array.Empty<Vector2>());
            Changed();
        }

        // each line cut at a quarter and three quarters (Chaikin), the ends kept where they meet the wall
        private static Vector2[] Smooth(Vector2[] p)
        {
            if (p.Length < 3)
                return p;
            var r = new List<Vector2> { p[0] };
            for (int i = 0; i + 1 < p.Length; i++)
            {
                if (i > 0)
                    r.Add(Vector2.Lerp(p[i], p[i + 1], 0.25f));
                if (i + 2 < p.Length)
                    r.Add(Vector2.Lerp(p[i], p[i + 1], 0.75f));
            }
            r.Add(p[p.Length - 1]);
            return r.ToArray();
        }

        private void DrawProfile(Plan.Trim t)
        {
            var rect = GUILayoutUtility.GetRect(200f, 230f, GUILayout.ExpandWidth(true));
            var pts = t.Profile ?? Array.Empty<Vector2>();
            if (_dragPoint < 0 || _profileView.width <= 0f)
            {
                float x0 = Mathf.Min(-0.03f, pts.Length > 0 ? pts.Min(p => p.x) : 0f), x1 = Mathf.Max(0.05f, pts.Length > 0 ? pts.Max(p => p.x) : 0f) + 0.03f;
                float y0 = (pts.Length > 0 ? pts.Min(p => p.y) : 0f) - 0.03f, y1 = (pts.Length > 0 ? pts.Max(p => p.y) : 0.05f) + 0.03f;
                _profileView = Rect.MinMaxRect(x0, y0, x1, y1);
            }
            const float pad = 12f;
            float scale = Mathf.Min((rect.width - pad * 2f) / _profileView.width, (rect.height - pad * 2f) / _profileView.height);
            Vector2 Screen(Vector2 p) => new Vector2(rect.x + pad + (p.x - _profileView.xMin) * scale, rect.yMax - pad - (p.y - _profileView.yMin) * scale);
            Vector2 Profile(Vector2 s) => new Vector2((s.x - rect.x - pad) / scale + _profileView.xMin, (rect.yMax - pad - s.y) / scale + _profileView.yMin);
            var e = Event.current;
            if (e.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(rect, new Color(0.13f, 0.13f, 0.13f));
                // the wall (x < 0)
                var wallLeft = Screen(new Vector2(_profileView.xMin, 0f)).x;
                var wallRight = Screen(Vector2.zero).x;
                EditorGUI.DrawRect(new Rect(Mathf.Max(rect.x, wallLeft), rect.y, Mathf.Max(0f, wallRight - Mathf.Max(rect.x, wallLeft)), rect.height), new Color(0.3f, 0.27f, 0.24f));
                // a centimetre grid (5 cm when it's crowded), every fifth line brighter
                float step = scale * 0.01f >= 7f ? 0.01f : 0.05f;
                for (int k = Mathf.CeilToInt(_profileView.xMin / step); k * step <= _profileView.xMax; k++)
                {
                    float x = Screen(new Vector2(k * step, 0f)).x;
                    EditorGUI.DrawRect(new Rect(x, rect.y, 1f, rect.height), new Color(1f, 1f, 1f, k % 5 == 0 ? 0.14f : 0.05f));
                }
                for (int k = Mathf.CeilToInt(_profileView.yMin / step); k * step <= _profileView.yMax; k++)
                {
                    float y = Screen(new Vector2(0f, k * step)).y;
                    EditorGUI.DrawRect(new Rect(rect.x, y, rect.width, 1f), new Color(1f, 1f, 1f, k % 5 == 0 ? 0.14f : 0.05f));
                }
                if (pts.Length >= 2)
                {
                    // the shape closed back along the wall
                    var outline = pts.Select(p => (Vector3)Screen(new Vector2(Mathf.Max(0f, p.x), p.y))).ToList();
                    outline.Add(Screen(new Vector2(0f, pts[pts.Length - 1].y)));
                    outline.Add(Screen(new Vector2(0f, pts[0].y)));
                    outline.Add(outline[0]);
                    Handles.color = new Color(1f, 1f, 1f, 0.35f);
                    Handles.DrawAAPolyLine(1.5f, outline.Skip(pts.Length - 1).ToArray());
                    Handles.color = new Color(1f, 0.85f, 0.3f);
                    Handles.DrawAAPolyLine(2.5f, outline.Take(pts.Length).ToArray());
                }
                for (int i = 0; i < pts.Length; i++)
                {
                    var s = Screen(pts[i]);
                    EditorGUI.DrawRect(new Rect(s.x - 3.5f, s.y - 3.5f, 7f, 7f), i == _dragPoint ? Color.white : i == 0 || i == pts.Length - 1 ? new Color(0.4f, 0.8f, 1f) : new Color(1f, 0.85f, 0.3f));
                }
                if (pts.Length >= 2)
                {
                    float w = pts.Max(p => p.x) * 100f, h = (pts.Max(p => p.y) - pts.Min(p => p.y)) * 100f;
                    GUI.Label(new Rect(rect.x + 4f, rect.y + 2f, rect.width, 16f), $"{w:0.#} cm out, {h:0.#} cm tall (grid {step * 100f:0} cm)", EditorStyles.miniLabel);
                }
            }
            if (!rect.Contains(e.mousePosition) && _dragPoint < 0)
                return;
            int Nearest()
            {
                int best = -1;
                float bestDistance = 8f;
                for (int i = 0; i < pts.Length; i++)
                {
                    float d = (Screen(pts[i]) - e.mousePosition).magnitude;
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = i;
                    }
                }
                return best;
            }
            switch (e.type)
            {
                case EventType.MouseDown when e.button == 0:
                {
                    int near = Nearest();
                    if (near >= 0)
                    {
                        Undo.IncrementCurrentGroup();
                        _dragPoint = near;
                        e.Use();
                        break;
                    }
                    if (e.clickCount == 2 && pts.Length >= 2)
                    {
                        // a new point on the nearest line
                        var at = Profile(e.mousePosition);
                        int seg = 0;
                        float best = float.MaxValue;
                        for (int i = 0; i + 1 < pts.Length; i++)
                        {
                            var a = pts[i];
                            var b = pts[i + 1];
                            float u = Mathf.Clamp01(Vector2.Dot(at - a, b - a) / Mathf.Max(1e-8f, (b - a).sqrMagnitude));
                            float d = (a + (b - a) * u - at).sqrMagnitude;
                            if (d < best)
                            {
                                best = d;
                                seg = i;
                            }
                        }
                        EditProfile(t, "Add profile point", p => p.Take(seg + 1).Concat(new[] { Snap(at, e.control) }).Concat(p.Skip(seg + 1)).ToArray());
                        e.Use();
                    }
                    break;
                }
                case EventType.MouseDown when e.button == 1:
                {
                    int near = Nearest();
                    if (near >= 0 && pts.Length > 2)
                    {
                        EditProfile(t, "Remove profile point", p => p.Where((_, i) => i != near).ToArray());
                        e.Use();
                    }
                    break;
                }
                case EventType.MouseDrag when _dragPoint >= 0 && _dragPoint < pts.Length:
                {
                    var p = Snap(Profile(e.mousePosition), e.control);
                    if (p != pts[_dragPoint])
                    {
                        Undo.RecordObject(_plan, "Move profile point");
                        t.Profile[_dragPoint] = p;
                        Changed();
                    }
                    e.Use();
                    break;
                }
                case EventType.MouseUp when _dragPoint >= 0:
                    _dragPoint = -1;
                    Undo.CollapseUndoOperations(Undo.GetCurrentGroup());
                    e.Use();
                    Repaint();
                    break;
            }
        }

        // to the half centimetre (Ctrl: not), never into the wall
        private static Vector2 Snap(Vector2 p, bool free)
        {
            if (!free)
                p = new Vector2(Mathf.Round(p.x * 200f) / 200f, Mathf.Round(p.y * 200f) / 200f);
            return new Vector2(Mathf.Max(0f, p.x), p.y);
        }

        // ------------------------------------------------------------ materials

        private void DrawMaterials()
        {
            EditorGUILayout.LabelField("What each part of the building is made of. Empty fields use the building's (or the SDK's) material; the Plan tab's Paint tool " +
                                       "puts materials on single walls. Pick any material below to lay out its texture and edit it.", EditorStyles.wordWrappedMiniLabel);
            var so = new SerializedObject(_plan);
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.LabelField("Building", EditorStyles.boldLabel);
            Role(so, "ExteriorMaterial", "Outside Walls", "Brick");
            Role(so, "InteriorMaterial", "Inside Walls", "Plaster");
            Role(so, "FloorMaterial", "Floors", "Floor");
            Role(so, "CeilingMaterial", "Ceilings", "Ceiling");
            Role(so, "RoofMaterial", "Roof", "Roof");
            Role(so, "TrimMaterial", new GUIContent("Trim", "Frames, parapet and wall tops, and trims without their own."), "Trim");
            EditorGUILayout.LabelField("What bullets hit (and sound like)", EditorStyles.miniBoldLabel);
            foreach (var name in new[] { "ExteriorSurface", "InteriorSurface", "FloorSurface" })
                EditorGUILayout.PropertyField(so.FindProperty(name));
            if (EditorGUI.EndChangeCheck())
            {
                so.ApplyModifiedProperties();
                Changed();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Each Storey's Outside", EditorStyles.boldLabel);
            for (int s = 0; s < _plan.Storeys.Count; s++)
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    var m = (Material)EditorGUILayout.ObjectField($"Storey {s + 1}", _plan.Storeys[s].Exterior, typeof(Material), false);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(_plan, "Storey outside");
                        _plan.Storeys[s].Exterior = m;
                        Changed();
                    }
                    EditButton(m);
                }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Rooms", EditorStyles.boldLabel);
            var rooms = so.FindProperty("Rooms");
            EditorGUI.BeginChangeCheck();
            for (int i = 0; i < _plan.Rooms.Count; i++)
            {
                var room = rooms.GetArrayElementAtIndex(i);
                EditorGUILayout.LabelField($"{i + 1}  {_plan.Rooms[i].Name}", EditorStyles.miniBoldLabel);
                using (new EditorGUI.IndentLevelScope())
                    foreach (var field in new[] { "Floor", "FloorSurface", "Walls", "WallSurface", "Ceiling", "LampPrefab", "LampRotation", "LampTint", "LampColor", "LampIntensity", "LampRange" })
                        EditorGUILayout.PropertyField(room.FindPropertyRelative(field));
            }
            if (EditorGUI.EndChangeCheck())
            {
                so.ApplyModifiedProperties();
                Changed();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Every Material On It", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Pick one to edit it; Layout sets how big its texture is on the walls (metres per repeat), its offset and turn, and what bullets hit where it's used.",
                EditorStyles.wordWrappedMiniLabel);
            Palette(m => _editing = m, _editing);
            foreach (var m in TarkovBuildingBuilder.UsedMaterials(_plan))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(m.name, m == _editing ? EditorStyles.boldLabel : EditorStyles.label);
                    GUILayout.FlexibleSpace();
                    bool hasOptions = _plan.MaterialOptions.Any(o => o != null && o.Material == m);
                    if (GUILayout.Toggle(_showOptions == m, hasOptions ? "Layout *" : "Layout", "Button", GUILayout.Width(64f)) != (_showOptions == m))
                        _showOptions = _showOptions == m ? null : m;
                    EditButton(m);
                }
                if (_showOptions == m)
                    DrawOptions(m);
            }

            EditorGUILayout.Space();
            DrawMaterialEditor();
        }

        private void Role(SerializedObject so, string field, string label, string sdk) => Role(so, field, new GUIContent(label), sdk);

        private void Role(SerializedObject so, string field, GUIContent label, string sdk)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var prop = so.FindProperty(field);
                EditorGUILayout.PropertyField(prop, label);
                var m = prop.objectReferenceValue as Material;
                EditButton(m != null ? m : TarkovBuildingBuilder.SdkMaterial(sdk));
            }
        }

        private void EditButton(Material m)
        {
            using (new EditorGUI.DisabledScope(m == null))
                if (GUILayout.Toggle(m != null && _editing == m, "Edit", "Button", GUILayout.Width(40f)) && m != null && _editing != m)
                    _editing = m;
        }

        private void DrawOptions(Material m)
        {
            var o = _plan.MaterialOptions.FirstOrDefault(x => x != null && x.Material == m);
            var shown = o ?? new Plan.MaterialOption { Material = m };
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUI.BeginChangeCheck();
                var tile = EditorGUILayout.Vector2Field(new GUIContent("Metres Per Repeat", "How big one repeat of its texture is on the building (1, 1: a metre each way)."), shown.Tile);
                var offset = EditorGUILayout.Vector2Field(new GUIContent("Offset", "Moves the texture, in repeats."), shown.Offset);
                float rotation = EditorGUILayout.Slider("Turn", shown.Rotation, -180f, 180f);
                bool overrideSurface = EditorGUILayout.Toggle(new GUIContent("Own Bullet Surface", "Walls and floors with this material are hit as this, not as their part's surface."), shown.OverrideSurface);
                var surface = shown.Surface;
                using (new EditorGUI.DisabledScope(!overrideSurface))
                    surface = (Plan.Surface)EditorGUILayout.EnumPopup("Surface", surface);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(_plan, "Material layout");
                    if (o == null)
                    {
                        o = shown;
                        _plan.MaterialOptions.Add(o);
                    }
                    o.Tile = new Vector2(Mathf.Max(0.01f, tile.x), Mathf.Max(0.01f, tile.y));
                    o.Offset = offset;
                    o.Rotation = rotation;
                    o.OverrideSurface = overrideSurface;
                    o.Surface = surface;
                    Changed();
                }
                if (o != null && GUILayout.Button("Reset Layout"))
                {
                    Undo.RecordObject(_plan, "Reset material layout");
                    _plan.MaterialOptions.Remove(o);
                    Changed();
                }
            }
        }

        private void DrawMaterialEditor()
        {
            EditorGUILayout.LabelField("Material", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _newMaterial = EditorGUILayout.TextField(_newMaterial);
                if (GUILayout.Button(new GUIContent("New Material", "A new material on the game's surface shader (a copy of the SDK's default), saved with the building's meshes."), GUILayout.Width(100f)))
                    EditorApplication.delayCall += NewMaterial;
            }
            if (_editing == null)
            {
                EditorGUILayout.LabelField("Pick a material above (Edit) to change it here.", EditorStyles.miniLabel);
                return;
            }
            var path = AssetDatabase.GetAssetPath(_editing);
            bool sdk = path.StartsWith("Assets/TarkovSDK/");
            if (sdk)
                EditorGUILayout.HelpBox("This is one of the SDK's own materials: every building that leaves this part empty uses it. Make a copy to change it for this building only.", MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Select", "Shows it in the Project window.")))
                    EditorGUIUtility.PingObject(_editing);
                if (GUILayout.Button(new GUIContent("Copy For This Building", "A copy of it next to the building's meshes, used everywhere this building used the original.")))
                    EditorApplication.delayCall += UniqueCopy;
                if (GUILayout.Button(new GUIContent("Use For...", "Puts it on a part of the building.")))
                    UseForMenu(_editing).ShowAsContext();
                if (GUILayout.Button(new GUIContent("Paint With", "Makes it the Paint tool's material.")))
                {
                    _brush = _editing;
                    _tab = Tab.Plan;
                    _tool = Tool.Paint;
                }
            }
            // its properties drawn here with plain fields: Unity's own material inspector, put inside this window's
            // layout, broke it (the whole window shrank to its top)
            using (new EditorGUILayout.HorizontalScope())
            {
                var preview = AssetPreview.GetAssetPreview(_editing);
                var r = GUILayoutUtility.GetRect(64f, 64f, GUILayout.Width(64f), GUILayout.Height(64f));
                if (preview != null)
                    GUI.DrawTexture(r, preview, ScaleMode.ScaleToFit);
                else
                    EditorGUI.DrawRect(r, Swatch(_editing));
                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField(_editing.name, EditorStyles.boldLabel);
                    EditorGUI.BeginChangeCheck();
                    var shader = (Shader)EditorGUILayout.ObjectField("Shader", _editing.shader, typeof(Shader), false);
                    if (EditorGUI.EndChangeCheck() && shader != null)
                    {
                        Undo.RecordObject(_editing, "Material shader");
                        _editing.shader = shader;
                        EditorUtility.SetDirty(_editing);
                    }
                }
            }
            DrawMaterialProperties(_editing);
            if (AssetPreview.IsLoadingAssetPreview(_editing.GetInstanceID()))
                Repaint();
        }

        // a material's shader properties, as the material inspector shows them: textures (with tiling and offset),
        // colours, numbers and sliders, and the keyword toggles and popups the stand-in shaders declare
        private static void DrawMaterialProperties(Material m)
        {
            var shader = m.shader;
            if (shader == null)
                return;
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                var flags = shader.GetPropertyFlags(i);
                if ((flags & UnityEngine.Rendering.ShaderPropertyFlags.HideInInspector) != 0)
                    continue;
                string name = shader.GetPropertyName(i);
                var label = new GUIContent(shader.GetPropertyDescription(i), name);
                var attributes = shader.GetPropertyAttributes(i);
                string toggle = attributes.FirstOrDefault(a => a.StartsWith("Toggle") || a.StartsWith("MaterialToggle"));
                string keywordEnum = attributes.FirstOrDefault(a => a.StartsWith("KeywordEnum("));
                void Changed(string undo, Action set)
                {
                    Undo.RecordObject(m, undo);
                    set();
                    EditorUtility.SetDirty(m);
                }
                switch (shader.GetPropertyType(i))
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                    {
                        EditorGUI.BeginChangeCheck();
                        var type = shader.GetPropertyTextureDimension(i) == UnityEngine.Rendering.TextureDimension.Cube ? typeof(Cubemap) : typeof(Texture);
                        var tex = (Texture)EditorGUILayout.ObjectField(label, m.GetTexture(name), type, false, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                        if (EditorGUI.EndChangeCheck())
                            Changed("Material texture", () => m.SetTexture(name, tex));
                        if ((flags & UnityEngine.Rendering.ShaderPropertyFlags.NoScaleOffset) == 0)
                            using (new EditorGUI.IndentLevelScope())
                            {
                                EditorGUI.BeginChangeCheck();
                                var scale = EditorGUILayout.Vector2Field("Tiling", m.GetTextureScale(name));
                                var offset = EditorGUILayout.Vector2Field("Offset", m.GetTextureOffset(name));
                                if (EditorGUI.EndChangeCheck())
                                    Changed("Material tiling", () =>
                                    {
                                        m.SetTextureScale(name, scale);
                                        m.SetTextureOffset(name, offset);
                                    });
                            }
                        break;
                    }
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                    {
                        EditorGUI.BeginChangeCheck();
                        bool hdr = (flags & UnityEngine.Rendering.ShaderPropertyFlags.HDR) != 0;
                        var c = EditorGUILayout.ColorField(label, m.GetColor(name), true, true, hdr);
                        if (EditorGUI.EndChangeCheck())
                            Changed("Material colour", () => m.SetColor(name, c));
                        break;
                    }
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                    {
                        EditorGUI.BeginChangeCheck();
                        var v = EditorGUILayout.Vector4Field(label, m.GetVector(name));
                        if (EditorGUI.EndChangeCheck())
                            Changed("Material value", () => m.SetVector(name, v));
                        break;
                    }
                    default:
                    {
                        float value = m.GetFloat(name);
                        EditorGUI.BeginChangeCheck();
                        if (toggle != null)
                        {
                            // [Toggle(KEY)] / [Toggle] (NAME_ON) / [ToggleOff] (NAME_OFF): the float and its keyword
                            bool on = EditorGUILayout.Toggle(label, value > 0.5f);
                            if (EditorGUI.EndChangeCheck())
                            {
                                bool off = toggle.StartsWith("ToggleOff");
                                string key = toggle.Contains("(") ? toggle.Substring(toggle.IndexOf('(') + 1).TrimEnd(')').Trim() : name.ToUpperInvariant() + (off ? "_OFF" : "_ON");
                                Changed("Material toggle", () =>
                                {
                                    m.SetFloat(name, on ? 1f : 0f);
                                    if (on != off)
                                        m.EnableKeyword(key);
                                    else
                                        m.DisableKeyword(key);
                                });
                            }
                        }
                        else if (keywordEnum != null)
                        {
                            // [KeywordEnum(A, B)]: NAME_A, NAME_B
                            var names = keywordEnum.Substring(12).TrimEnd(')').Split(',').Select(n => n.Trim()).ToArray();
                            int pick = EditorGUILayout.Popup(label, Mathf.Clamp(Mathf.RoundToInt(value), 0, names.Length - 1), names);
                            if (EditorGUI.EndChangeCheck())
                                Changed("Material option", () =>
                                {
                                    m.SetFloat(name, pick);
                                    for (int k = 0; k < names.Length; k++)
                                    {
                                        string key = (name + "_" + names[k]).ToUpperInvariant().Replace(' ', '_');
                                        if (k == pick)
                                            m.EnableKeyword(key);
                                        else
                                            m.DisableKeyword(key);
                                    }
                                });
                        }
                        else if (shader.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Range)
                        {
                            var limits = shader.GetPropertyRangeLimits(i);
                            float f = EditorGUILayout.Slider(label, value, limits.x, limits.y);
                            if (EditorGUI.EndChangeCheck())
                                Changed("Material value", () => m.SetFloat(name, f));
                        }
                        else
                        {
                            float f = EditorGUILayout.FloatField(label, value);
                            if (EditorGUI.EndChangeCheck())
                                Changed("Material value", () => m.SetFloat(name, f));
                        }
                        break;
                    }
                }
            }
        }

        private string MaterialsFolder()
        {
            var folder = TarkovBuildingBuilder.PartsFolder(_plan) + "/Materials";
            TarkovGeneratedAssets.EnsureFolder(folder);
            return folder;
        }

        private void NewMaterial()
        {
            if (_plan == null)
                return;
            var source = TarkovSceneTools.DefaultSurfaceMaterial();
            if (source == null)
                return;
            string name = string.IsNullOrWhiteSpace(_newMaterial) ? "Material" : _newMaterial.Trim();
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            var material = new Material(source) { name = name };
            var path = AssetDatabase.GenerateUniqueAssetPath($"{MaterialsFolder()}/{name}.mat");
            AssetDatabase.CreateAsset(material, path);
            _editing = AssetDatabase.LoadAssetAtPath<Material>(path);
            _brush = _editing;
            _report = $"Made {path}: put it on a part with Use For..., or paint walls with it.";
            Repaint();
        }

        private void UniqueCopy()
        {
            if (_plan == null || _editing == null)
                return;
            var source = _editing;
            var path = AssetDatabase.GenerateUniqueAssetPath($"{MaterialsFolder()}/{source.name}.mat");
            if (!AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(source), path))
                return;
            var copy = AssetDatabase.LoadAssetAtPath<Material>(path);
            Undo.RecordObject(_plan, "Copy material");
            int n = TarkovBuildingBuilder.ReplaceMaterial(_plan, source, copy);
            if (_brush == source)
                _brush = copy;
            _editing = copy;
            _report = $"Copied {source.name} to {path}, used in its place in {n} part(s) of this building.";
            Changed();
        }

        private GenericMenu UseForMenu(Material m)
        {
            var menu = new GenericMenu();
            void Item(string label, Action set) => menu.AddItem(new GUIContent(label), false, () =>
            {
                Undo.RecordObject(_plan, "Use material");
                set();
                Changed();
            });
            Item("Building/Outside Walls", () => _plan.ExteriorMaterial = m);
            Item("Building/Inside Walls", () => _plan.InteriorMaterial = m);
            Item("Building/Floors", () => _plan.FloorMaterial = m);
            Item("Building/Ceilings", () => _plan.CeilingMaterial = m);
            Item("Building/Roof", () => _plan.RoofMaterial = m);
            Item("Building/Trim", () => _plan.TrimMaterial = m);
            for (int s = 0; s < _plan.Storeys.Count; s++)
            {
                var storey = _plan.Storeys[s];
                Item($"Storey's Outside/Storey {s + 1}", () => storey.Exterior = m);
            }
            for (int i = 0; i < _plan.Rooms.Count; i++)
            {
                var room = _plan.Rooms[i];
                string name = $"Rooms/{i + 1} {room.Name}";
                Item(name + "/Walls", () => room.Walls = m);
                Item(name + "/Floor", () => room.Floor = m);
                Item(name + "/Ceiling", () => room.Ceiling = m);
            }
            foreach (var t in _plan.Trims)
            {
                var trim = t;
                Item($"Trims/{trim.Name}", () => trim.Material = m);
            }
            return menu;
        }
    }
}
