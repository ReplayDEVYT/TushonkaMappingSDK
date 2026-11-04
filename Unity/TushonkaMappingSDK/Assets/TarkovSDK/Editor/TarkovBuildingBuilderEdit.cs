using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using Plan = TarkovSDK.TarkovBuilding;

namespace TarkovSDK.Editor
{
    // The Building Builder's editing tools (a partial of TarkovBuildingBuilderWindow):
    //  - Select: click (Shift/Ctrl adds or takes away) or drag a box over cells, doors, windows, gaps, stairs, furniture
    //    and holes; what's selected is edited together (each field shows "-" where they differ). Copy (Ctrl+C), Cut,
    //    Duplicate (Ctrl+D), Paste (Ctrl+V: it follows the mouse, R turns it a quarter, a click puts it down, on any
    //    storey or building), Delete, Copy To Storey. Copying cells takes what's on and next to them: the openings on
    //    their edges, stairs, furniture, holes, bevels, paint and open ceilings. Pasted doors keep facing the way they
    //    did (their swing and hinge are flipped when the rooms either side swap).
    //  - Furniture: a palette of prefabs (kept per project), placed by clicking on the plan or the storey's floor,
    //    snapped to the grid and (optionally) backed onto the nearest wall; R turns it, Shift+click removes one.
    //  - Holes: click on the building in the Scene view to cut a hole there (through the wall or floor clicked);
    //    the selected hole has a box handle to size it and a move handle.
    public partial class TarkovBuildingBuilderWindow
    {
        // ------------------------------------------------------------ selection

        private readonly HashSet<int> _selOpenings = new HashSet<int>();
        private readonly HashSet<int> _selStairs = new HashSet<int>();
        private readonly HashSet<int> _selProps = new HashSet<int>();
        private readonly HashSet<int> _selHoles = new HashSet<int>();
        private readonly HashSet<Vector2Int> _selCells = new HashSet<Vector2Int>();
        private int _selStorey;
        // a box being dragged (grid coordinates)
        private Vector2? _marqueeFrom;
        private Vector2 _marqueeTo;
        // the mouse on the plan (grid coordinates), for the paste and furniture previews
        private Vector2 _hover;
        private bool _hovering;
        private bool _pasting;
        private int _pasteTurns;
        private static Clip _clip;

        private int SelectionCount => _selOpenings.Count + _selStairs.Count + _selProps.Count + _selHoles.Count + _selCells.Count;

        private void ClearSelection()
        {
            _selOpenings.Clear();
            _selStairs.Clear();
            _selProps.Clear();
            _selHoles.Clear();
            _selCells.Clear();
        }

        private void SelectOnly(HashSet<int> set, int index)
        {
            ClearSelection();
            if (index >= 0)
                set.Add(index);
        }

        // the one thing selected, when it's an opening (its prefab's handles), else -1
        private int SingleOpening => _selOpenings.Count == 1 && SelectionCount == 1 ? _selOpenings.First() : -1;

        // indices that an undo (or a removal elsewhere) left pointing past the lists; a storey change empties it
        private void ValidateSelection()
        {
            if (_plan == null)
                return;
            if (_selStorey != _storey)
            {
                ClearSelection();
                _selStorey = _storey;
                _pasting = _pasting && _clip != null;
            }
            _selOpenings.RemoveWhere(i => i < 0 || i >= _plan.Openings.Count);
            _selStairs.RemoveWhere(i => i < 0 || i >= _plan.Stairs.Count);
            _selProps.RemoveWhere(i => i < 0 || i >= (_plan.Props?.Count ?? 0));
            _selHoles.RemoveWhere(i => i < 0 || i >= (_plan.Holes?.Count ?? 0));
            _selCells.RemoveWhere(c => c.x < 0 || c.y < 0 || c.x >= _plan.Width || c.y >= _plan.Depth);
        }

        private static void Toggle<T>(HashSet<T> set, T item, bool toggle)
        {
            if (toggle && set.Contains(item))
                set.Remove(item);
            else
                set.Add(item);
        }

        // ------------------------------------------------------------ what's where (grid coordinates)

        private int OpeningAt(float fx, float fz)
        {
            var edge = NearestEdge(fx, fz);
            if (edge == null)
                return -1;
            var (ex, ez, alongZ) = edge.Value;
            return _plan.Openings.FindIndex(o => o.Storey == _storey && o.AlongZ == alongZ &&
                (alongZ ? o.X == ex && ez >= o.Z && ez < o.Z + Mathf.Max(1, o.Span) : o.Z == ez && ex >= o.X && ex < o.X + Mathf.Max(1, o.Span)));
        }

        private int StairAt(int cx, int cz) => _plan.Stairs.FindIndex(st => st.Storey == _storey && TarkovBuildingBuilder.StairFootprint(st).Contains(new Vector2Int(cx, cz)));

        // a prop's footprint corners on the plan (metres)
        private static Vector2[] Footprint(Plan.Prop p, Vector3 position, float yaw)
        {
            var b = TarkovBuildingBuilder.PrefabBounds(p.Prefab);
            float k = p.Scale > 0f ? p.Scale : 1f;
            var q = Quaternion.Euler(0f, yaw, 0f);
            return new[] { new Vector3(b.min.x, 0f, b.min.z), new Vector3(b.max.x, 0f, b.min.z), new Vector3(b.max.x, 0f, b.max.z), new Vector3(b.min.x, 0f, b.max.z) }
                .Select(c => q * (c * k) + position).Select(c => new Vector2(c.x, c.z)).ToArray();
        }

        private static bool InPolygon(Vector2[] poly, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if ((poly[i].y > p.y) != (poly[j].y > p.y) && p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            return inside;
        }

        private int PropAt(float fx, float fz)
        {
            var p = new Vector2(fx, fz) * _plan.CellSize;
            var props = _plan.Props ?? new List<Plan.Prop>();
            for (int i = props.Count - 1; i >= 0; i--)
                if (props[i] != null && props[i].Storey == _storey && props[i].Prefab != null && InPolygon(Footprint(props[i], props[i].Position, props[i].Yaw), p))
                    return i;
            return -1;
        }

        // a hole reaching into the storey (its walls, or its floor's slab)
        private bool OnStorey(Plan.Hole h, int s) =>
            h != null && h.Center.y + Mathf.Abs(h.Size.y) * 0.5f > _plan.Base(s) - _plan.SlabThickness && h.Center.y - Mathf.Abs(h.Size.y) * 0.5f < _plan.Base(s + 1);

        private static Vector2[] HoleFootprint(Plan.Hole h)
        {
            var a = new Vector2(h.Center.x - Mathf.Abs(h.Size.x) * 0.5f, h.Center.z - Mathf.Abs(h.Size.z) * 0.5f);
            var b = new Vector2(h.Center.x + Mathf.Abs(h.Size.x) * 0.5f, h.Center.z + Mathf.Abs(h.Size.z) * 0.5f);
            return new[] { a, new Vector2(b.x, a.y), b, new Vector2(a.x, b.y) };
        }

        private int HoleAt(float fx, float fz)
        {
            var p = new Vector2(fx, fz) * _plan.CellSize;
            var holes = _plan.Holes ?? new List<Plan.Hole>();
            for (int i = holes.Count - 1; i >= 0; i--)
                if (OnStorey(holes[i], _storey) && InPolygon(HoleFootprint(holes[i]), p))
                    return i;
            return -1;
        }

        private static Vector2 Middle(Plan.Opening o) =>
            o.AlongZ ? new Vector2(o.X, o.Z + Mathf.Max(1, o.Span) * 0.5f) : new Vector2(o.X + Mathf.Max(1, o.Span) * 0.5f, o.Z);

        // ------------------------------------------------------------ the Select tool

        private bool SelectInput(float fx, float fz, Event e)
        {
            if (_pasting)
            {
                if (e.type != EventType.MouseDown)
                    return false;
                Paste(Mathf.FloorToInt(fx), Mathf.FloorToInt(fz), _storey);
                // Shift keeps pasting
                _pasting = e.shift;
                e.Use();
                return true;
            }
            if (e.type == EventType.MouseDown)
            {
                _marqueeFrom = new Vector2(fx, fz);
                _marqueeTo = _marqueeFrom.Value;
                e.Use();
                return true;
            }
            if (e.type == EventType.MouseDrag && _marqueeFrom.HasValue)
            {
                _marqueeTo = new Vector2(fx, fz);
                e.Use();
                Repaint();
                SceneView.RepaintAll();
                return true;
            }
            if (e.type == EventType.MouseUp && _marqueeFrom.HasValue)
            {
                var a = _marqueeFrom.Value;
                _marqueeFrom = null;
                bool add = e.shift || e.control || e.command;
                if ((a - new Vector2(fx, fz)).magnitude < 0.2f)
                    ClickSelect(fx, fz, add);
                else
                {
                    if (!add)
                        ClearSelection();
                    BoxSelect(Rect.MinMaxRect(Mathf.Min(a.x, fx), Mathf.Min(a.y, fz), Mathf.Max(a.x, fx), Mathf.Max(a.y, fz)));
                }
                e.Use();
                Repaint();
                SceneView.RepaintAll();
                return true;
            }
            return false;
        }

        // what's under the mouse, most specific first: furniture, a hole, an opening, stairs, else the cell
        private void ClickSelect(float fx, float fz, bool add)
        {
            if (!add)
                ClearSelection();
            int i;
            if ((i = PropAt(fx, fz)) >= 0)
                Toggle(_selProps, i, add);
            else if ((i = HoleAt(fx, fz)) >= 0)
                Toggle(_selHoles, i, add);
            else if ((i = OpeningAt(fx, fz)) >= 0)
                Toggle(_selOpenings, i, add);
            else if ((i = StairAt(Mathf.FloorToInt(fx), Mathf.FloorToInt(fz))) >= 0)
                Toggle(_selStairs, i, add);
            else
            {
                var c = new Vector2Int(Mathf.FloorToInt(fx), Mathf.FloorToInt(fz));
                if (c.x >= 0 && c.y >= 0 && c.x < _plan.Width && c.y < _plan.Depth)
                    Toggle(_selCells, c, add);
            }
        }

        private void BoxSelect(Rect r)
        {
            for (int z = 0; z < _plan.Depth; z++)
                for (int x = 0; x < _plan.Width; x++)
                    if (r.Contains(new Vector2(x + 0.5f, z + 0.5f)))
                        _selCells.Add(new Vector2Int(x, z));
            for (int i = 0; i < _plan.Openings.Count; i++)
                if (_plan.Openings[i].Storey == _storey && r.Contains(Middle(_plan.Openings[i])))
                    _selOpenings.Add(i);
            for (int i = 0; i < _plan.Stairs.Count; i++)
                if (_plan.Stairs[i].Storey == _storey && TarkovBuildingBuilder.StairFootprint(_plan.Stairs[i]).All(c => r.Contains(new Vector2(c.x + 0.5f, c.y + 0.5f))))
                    _selStairs.Add(i);
            var props = _plan.Props ?? new List<Plan.Prop>();
            for (int i = 0; i < props.Count; i++)
                if (props[i] != null && props[i].Storey == _storey && r.Contains(new Vector2(props[i].Position.x, props[i].Position.z) / _plan.CellSize))
                    _selProps.Add(i);
            var holes = _plan.Holes ?? new List<Plan.Hole>();
            for (int i = 0; i < holes.Count; i++)
                if (OnStorey(holes[i], _storey) && r.Contains(new Vector2(holes[i].Center.x, holes[i].Center.z) / _plan.CellSize))
                    _selHoles.Add(i);
        }

        private void SelectAll()
        {
            ClearSelection();
            BoxSelect(new Rect(-1f, -1f, _plan.Width + 2f, _plan.Depth + 2f));
            _selCells.RemoveWhere(c => _plan.Get(_storey, c.x, c.y) == 0);
        }

        // Copy, Cut, Paste, Duplicate, Delete, Select All (the editor's commands, so the Scene view doesn't copy the
        // building's GameObjects instead), Escape, and R to turn what's being pasted
        private bool EditCommands(Event e)
        {
            if (_plan == null || _tool != Tool.Select)
                return false;
            if (e.type == EventType.ValidateCommand && e.commandName is "Copy" or "Cut" or "Paste" or "Duplicate" or "Delete" or "SoftDelete" or "SelectAll")
            {
                e.Use();
                return true;
            }
            if (e.type == EventType.ExecuteCommand)
            {
                switch (e.commandName)
                {
                    case "Copy":
                        Copy();
                        break;
                    case "Cut":
                        Copy();
                        DeleteSelection();
                        break;
                    case "Paste":
                        StartPaste();
                        break;
                    case "Duplicate":
                        Copy();
                        StartPaste();
                        break;
                    case "Delete":
                    case "SoftDelete":
                        DeleteSelection();
                        break;
                    case "SelectAll":
                        SelectAll();
                        break;
                    default:
                        return false;
                }
                e.Use();
                Repaint();
                SceneView.RepaintAll();
                return true;
            }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape && (_pasting || SelectionCount > 0))
            {
                if (_pasting)
                    _pasting = false;
                else
                    ClearSelection();
                e.Use();
                Repaint();
                SceneView.RepaintAll();
                return true;
            }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.R && _pasting)
            {
                _pasteTurns = (_pasteTurns + (e.shift ? 3 : 1)) % 4;
                e.Use();
                SceneView.RepaintAll();
                Repaint();
                return true;
            }
            return false;
        }

        private void StartPaste()
        {
            if (_clip == null)
                return;
            _pasting = true;
            _pasteTurns = 0;
        }

        private void DeleteSelection()
        {
            if (SelectionCount == 0)
                return;
            Undo.RecordObject(_plan, "Delete selection");
            foreach (var i in _selOpenings.OrderByDescending(i => i))
                _plan.Openings.RemoveAt(i);
            foreach (var i in _selStairs.OrderByDescending(i => i))
                _plan.Stairs.RemoveAt(i);
            foreach (var i in _selProps.OrderByDescending(i => i))
                _plan.Props.RemoveAt(i);
            foreach (var i in _selHoles.OrderByDescending(i => i))
                _plan.Holes.RemoveAt(i);
            foreach (var c in _selCells)
            {
                _plan.Set(_storey, c.x, c.y, 0);
                _plan.Storeys[_storey].OpenCeiling?.Remove(c);
            }
            ClearSelection();
            Changed();
        }

        // ------------------------------------------------------------ clipboard

        // What was copied, relative to its corner (grid coordinates), turnable a quarter at a time. Static: it pastes
        // into any building.
        private sealed class Clip
        {
            public Plan Source;
            public float Cell = 1f;
            public int W, D;
            public readonly List<(Vector2Int Cell, int Room, bool Open)> Cells = new List<(Vector2Int, int, bool)>();
            // each opening with the way it faced and ran (to keep it so where the rooms either side swap)
            public readonly List<(Plan.Opening O, Vector3 Out, Vector3 Along)> Openings = new List<(Plan.Opening, Vector3, Vector3)>();
            public readonly List<Plan.Stair> Stairs = new List<Plan.Stair>();
            public readonly List<Plan.Prop> Props = new List<Plan.Prop>();
            // centres relative to the corner and the storey's floor
            public readonly List<Plan.Hole> Holes = new List<Plan.Hole>();
            public readonly List<Plan.Bevel> Bevels = new List<Plan.Bevel>();
            public readonly List<Plan.WallPaint> Paints = new List<Plan.WallPaint>();
            public readonly Dictionary<int, Plan.Room> Rooms = new Dictionary<int, Plan.Room>();

            // a quarter turn anticlockwise (seen from above): (x, z) -> (D - z, x)
            public void Turn()
            {
                int w = W, d = D;
                Vector3 V(Vector3 v) => new Vector3(-v.z, v.y, v.x);
                for (int i = 0; i < Cells.Count; i++)
                    Cells[i] = (new Vector2Int(d - 1 - Cells[i].Cell.y, Cells[i].Cell.x), Cells[i].Room, Cells[i].Open);
                for (int i = 0; i < Openings.Count; i++)
                {
                    var (o, n, a) = Openings[i];
                    int span = Mathf.Max(1, o.Span);
                    int x = o.X, z = o.Z;
                    if (!o.AlongZ)
                    {
                        o.X = d - z;
                        o.Z = x;
                        o.AlongZ = true;
                    }
                    else
                    {
                        o.X = d - z - span;
                        o.Z = x;
                        o.AlongZ = false;
                    }
                    Openings[i] = (o, V(n), V(a));
                }
                foreach (var st in Stairs)
                {
                    (st.X, st.Z) = (d - 1 - st.Z, st.X);
                    st.Direction = (((st.Direction % 4) + 4) % 4 + 3) % 4;
                }
                foreach (var p in Props)
                {
                    p.Position = new Vector3(d * Cell - p.Position.z, p.Position.y, p.Position.x);
                    p.Yaw -= 90f;
                }
                foreach (var h in Holes)
                {
                    h.Center = new Vector3(d * Cell - h.Center.z, h.Center.y, h.Center.x);
                    h.Size = new Vector3(h.Size.z, h.Size.y, h.Size.x);
                }
                foreach (var b in Bevels)
                {
                    (b.X, b.Z) = (d - b.Z, b.X);
                    b.Quadrant = (b.Quadrant + 1) % 4;
                }
                foreach (var p in Paints)
                {
                    int x = p.X, z = p.Z;
                    if (!p.AlongZ)
                    {
                        p.X = d - z;
                        p.Z = x;
                        p.AlongZ = true;
                        // the + side of an edge along x (+z) turns to -x
                        p.Plus = !p.Plus;
                    }
                    else
                    {
                        p.X = d - z - 1;
                        p.Z = x;
                        p.AlongZ = false;
                    }
                }
                W = d;
                D = w;
            }

            public Clip Copy()
            {
                var c = new Clip { Source = Source, Cell = Cell, W = W, D = D };
                c.Cells.AddRange(Cells);
                c.Openings.AddRange(Openings.Select(o => (Clone(o.O), o.Out, o.Along)));
                c.Stairs.AddRange(Stairs.Select(Clone));
                c.Props.AddRange(Props.Select(Clone));
                c.Holes.AddRange(Holes.Select(Clone));
                c.Bevels.AddRange(Bevels.Select(Clone));
                c.Paints.AddRange(Paints.Select(Clone));
                foreach (var kv in Rooms)
                    c.Rooms[kv.Key] = kv.Value;
                return c;
            }
        }

        private static T Clone<T>(T item) where T : new()
        {
            var copy = new T();
            EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(item), copy);
            return copy;
        }

        // the selection, with what's on and next to its cells
        private void Copy()
        {
            if (_plan == null || SelectionCount == 0)
                return;
            int s = _storey;
            float cell = _plan.CellSize;
            var cells = new HashSet<Vector2Int>(_selCells);
            bool Near(int x, int z) => cells.Contains(new Vector2Int(x, z));
            var openings = new HashSet<int>(_selOpenings);
            for (int i = 0; i < _plan.Openings.Count; i++)
            {
                var o = _plan.Openings[i];
                if (o.Storey != s)
                    continue;
                for (int k = 0; k < Mathf.Max(1, o.Span); k++)
                    if (o.AlongZ ? Near(o.X - 1, o.Z + k) || Near(o.X, o.Z + k) : Near(o.X + k, o.Z - 1) || Near(o.X + k, o.Z))
                        openings.Add(i);
            }
            var stairs = new HashSet<int>(_selStairs);
            for (int i = 0; i < _plan.Stairs.Count; i++)
                if (_plan.Stairs[i].Storey == s && TarkovBuildingBuilder.StairFootprint(_plan.Stairs[i]).Any(c => cells.Contains(c)))
                    stairs.Add(i);
            var props = new HashSet<int>(_selProps);
            for (int i = 0; i < (_plan.Props?.Count ?? 0); i++)
                if (_plan.Props[i] != null && _plan.Props[i].Storey == s && Near(Mathf.FloorToInt(_plan.Props[i].Position.x / cell), Mathf.FloorToInt(_plan.Props[i].Position.z / cell)))
                    props.Add(i);
            var holes = new HashSet<int>(_selHoles);
            for (int i = 0; i < (_plan.Holes?.Count ?? 0); i++)
                if (OnStorey(_plan.Holes[i], s) && Near(Mathf.FloorToInt(_plan.Holes[i].Center.x / cell), Mathf.FloorToInt(_plan.Holes[i].Center.z / cell)))
                    holes.Add(i);

            // the corner and size of all of it
            var points = new List<Vector2>();
            foreach (var c in cells)
            {
                points.Add(c);
                points.Add(c + Vector2.one);
            }
            foreach (var i in openings)
            {
                var o = _plan.Openings[i];
                points.Add(new Vector2(o.X, o.Z));
                points.Add(o.AlongZ ? new Vector2(o.X, o.Z + Mathf.Max(1, o.Span)) : new Vector2(o.X + Mathf.Max(1, o.Span), o.Z));
            }
            foreach (var i in stairs)
                foreach (var c in TarkovBuildingBuilder.StairFootprint(_plan.Stairs[i]))
                {
                    points.Add(c);
                    points.Add(c + Vector2.one);
                }
            foreach (var i in props)
                points.AddRange(Footprint(_plan.Props[i], _plan.Props[i].Position, _plan.Props[i].Yaw).Select(p => p / cell));
            foreach (var i in holes)
                points.AddRange(HoleFootprint(_plan.Holes[i]).Select(p => p / cell));
            if (points.Count == 0)
                return;
            int x0 = Mathf.FloorToInt(points.Min(p => p.x) + 1e-3f), z0 = Mathf.FloorToInt(points.Min(p => p.y) + 1e-3f);
            int x1 = Mathf.CeilToInt(points.Max(p => p.x) - 1e-3f), z1 = Mathf.CeilToInt(points.Max(p => p.y) - 1e-3f);
            var clip = new Clip { Source = _plan, Cell = cell, W = Mathf.Max(1, x1 - x0), D = Mathf.Max(1, z1 - z0) };
            var open = _plan.OpenCeilings(s);
            foreach (var c in cells)
            {
                int room = _plan.Get(s, c.x, c.y);
                clip.Cells.Add((c - new Vector2Int(x0, z0), room, open.Contains(c)));
                if (room > 0 && room <= _plan.Rooms.Count)
                    clip.Rooms[room] = _plan.Rooms[room - 1];
            }
            foreach (var i in openings)
            {
                var o = Clone(_plan.Openings[i]);
                TarkovBuildingBuilder.Locate(_plan, _plan.Openings[i], out _, out var outward, out var along, out var edge, out _);
                o.X -= x0;
                o.Z -= z0;
                clip.Openings.Add((o, edge.Exists ? outward : Vector3.zero, edge.Exists ? along : Vector3.zero));
            }
            foreach (var i in stairs)
            {
                var st = Clone(_plan.Stairs[i]);
                st.X -= x0;
                st.Z -= z0;
                clip.Stairs.Add(st);
            }
            foreach (var i in props)
            {
                var p = Clone(_plan.Props[i]);
                p.Position -= new Vector3(x0 * cell, 0f, z0 * cell);
                clip.Props.Add(p);
            }
            foreach (var i in holes)
            {
                var h = Clone(_plan.Holes[i]);
                h.Center -= new Vector3(x0 * cell, _plan.Base(s), z0 * cell);
                clip.Holes.Add(h);
            }
            foreach (var b in _plan.Bevels.Where(b => b.Storey == s))
            {
                // the bevel's room cell
                var c = new Vector2Int(b.Quadrant == 0 || b.Quadrant == 3 ? b.X : b.X - 1, b.Quadrant <= 1 ? b.Z : b.Z - 1);
                if (!cells.Contains(c))
                    continue;
                var copy = Clone(b);
                copy.X -= x0;
                copy.Z -= z0;
                clip.Bevels.Add(copy);
            }
            foreach (var p in _plan.Paints.Where(p => p.Storey == s))
            {
                if (!(p.AlongZ ? Near(p.X - 1, p.Z) || Near(p.X, p.Z) : Near(p.X, p.Z - 1) || Near(p.X, p.Z)))
                    continue;
                var copy = Clone(p);
                copy.X -= x0;
                copy.Z -= z0;
                clip.Paints.Add(copy);
            }
            _clip = clip;
            _clipOrigin = new Vector2Int(x0, z0);
            _report = $"Copied {cells.Count} cell(s), {openings.Count} opening(s), {stairs.Count} stair(s), {props.Count} furniture, {holes.Count} hole(s). Paste: Ctrl+V (R turns it).";
        }

        // where the copy came from, for Copy To Storey
        private Vector2Int _clipOrigin;

        // the clipboard (turned as being pasted) with its corner at the cell, on the storey
        private void Paste(int ox, int oz, int s)
        {
            if (_clip == null || _plan == null || s < 0 || s >= _plan.Storeys.Count)
                return;
            var clip = _clip.Copy();
            for (int t = 0; t < _pasteTurns; t++)
                clip.Turn();
            Undo.RecordObject(_plan, "Paste");
            float cell = _plan.CellSize;
            bool Inside(int x, int z) => x >= 0 && z >= 0 && x < _plan.Width && z < _plan.Depth;
            int dropped = 0;

            // rooms from another building: the one of the same name, else a copy
            var rooms = new Dictionary<int, int>();
            foreach (var kv in clip.Rooms)
            {
                if (clip.Source == _plan)
                {
                    rooms[kv.Key] = kv.Key;
                    continue;
                }
                int found = _plan.Rooms.FindIndex(r => r.Name == kv.Value.Name);
                if (found < 0)
                {
                    _plan.Rooms.Add(Clone(kv.Value));
                    found = _plan.Rooms.Count - 1;
                }
                rooms[kv.Key] = found + 1;
            }
            ClearSelection();
            var storey = _plan.Storeys[s];
            storey.OpenCeiling ??= new List<Vector2Int>();
            foreach (var (c, room, open) in clip.Cells)
            {
                int x = ox + c.x, z = oz + c.y;
                if (!Inside(x, z))
                {
                    dropped++;
                    continue;
                }
                _plan.Set(s, x, z, room > 0 && rooms.TryGetValue(room, out int r) ? r : room);
                var at = new Vector2Int(x, z);
                storey.OpenCeiling.Remove(at);
                if (open)
                    storey.OpenCeiling.Add(at);
                _selCells.Add(at);
            }
            var pasted = new List<(Plan.Opening O, Vector3 Out, Vector3 Along)>();
            foreach (var (src, outward, along) in clip.Openings)
            {
                var o = Clone(src);
                o.Storey = s;
                o.X += ox;
                o.Z += oz;
                int span = Mathf.Max(1, o.Span);
                if (!(o.AlongZ ? o.X >= 0 && o.X <= _plan.Width && o.Z >= 0 && o.Z + span <= _plan.Depth : o.Z >= 0 && o.Z <= _plan.Depth && o.X >= 0 && o.X + span <= _plan.Width))
                {
                    dropped++;
                    continue;
                }
                // whatever is on those edges goes
                _plan.Openings.RemoveAll(e => e.Storey == s && e.AlongZ == o.AlongZ && (e.AlongZ ? e.X == o.X : e.Z == o.Z) &&
                    (e.AlongZ ? e.Z : e.X) < (o.AlongZ ? o.Z : o.X) + span && (o.AlongZ ? o.Z : o.X) < (e.AlongZ ? e.Z : e.X) + Mathf.Max(1, e.Span));
                _plan.Openings.Add(o);
                pasted.Add((o, outward, along));
            }
            foreach (var src in clip.Stairs)
            {
                var st = Clone(src);
                st.Storey = s;
                st.X += ox;
                st.Z += oz;
                var foot = TarkovBuildingBuilder.StairFootprint(st).ToList();
                if (!foot.All(c => Inside(c.x, c.y)))
                {
                    dropped++;
                    continue;
                }
                _plan.Stairs.RemoveAll(e => e.Storey == s && TarkovBuildingBuilder.StairFootprint(e).Any(foot.Contains));
                _plan.Stairs.Add(st);
            }
            _plan.Props ??= new List<Plan.Prop>();
            foreach (var src in clip.Props)
            {
                var p = Clone(src);
                p.Storey = s;
                p.Position += new Vector3(ox * cell, 0f, oz * cell);
                if (!Inside(Mathf.FloorToInt(p.Position.x / cell), Mathf.FloorToInt(p.Position.z / cell)))
                {
                    dropped++;
                    continue;
                }
                _plan.Props.Add(p);
                _selProps.Add(_plan.Props.Count - 1);
            }
            _plan.Holes ??= new List<Plan.Hole>();
            foreach (var src in clip.Holes)
            {
                var h = Clone(src);
                h.Center += new Vector3(ox * cell, _plan.Base(s), oz * cell);
                _plan.Holes.Add(h);
                _selHoles.Add(_plan.Holes.Count - 1);
            }
            foreach (var src in clip.Bevels)
            {
                var b = Clone(src);
                b.Storey = s;
                b.X += ox;
                b.Z += oz;
                _plan.Bevels.RemoveAll(e => e.Storey == s && e.X == b.X && e.Z == b.Z && e.Quadrant == b.Quadrant);
                _plan.Bevels.Add(b);
            }
            foreach (var src in clip.Paints)
            {
                var p = Clone(src);
                p.Storey = s;
                p.X += ox;
                p.Z += oz;
                _plan.Paints.RemoveAll(e => e.Storey == s && e.X == p.X && e.Z == p.Z && e.AlongZ == p.AlongZ && e.Plus == p.Plus);
                _plan.Paints.Add(p);
            }
            // doors still facing (and opening) the way they did: where the rooms either side came out the other way
            // round, it's turned back and its hinge and swing flipped; and offsets along the wall kept running the same way
            foreach (var (o, outward, along) in pasted)
            {
                if (outward == Vector3.zero || !TarkovBuildingBuilder.Locate(_plan, o, out _, out var now, out var runs, out _, out _))
                    continue;
                if (Vector3.Dot(now, outward) < 0f)
                {
                    o.ReverseSwing = !o.ReverseSwing;
                    o.HingeRight = !o.HingeRight;
                    o.PrefabOffset.z = -o.PrefabOffset.z;
                }
                if (Vector3.Dot(runs, along) < 0f)
                {
                    o.Offset = -o.Offset;
                    o.PrefabOffset.x = -o.PrefabOffset.x;
                }
            }
            foreach (var (o, _, _) in pasted)
                _selOpenings.Add(_plan.Openings.IndexOf(o));
            for (int i = 0; i < _plan.Stairs.Count; i++)
                if (clip.Stairs.Count > 0 && _plan.Stairs[i].Storey == s && _selCells.Overlaps(TarkovBuildingBuilder.StairFootprint(_plan.Stairs[i])))
                    _selStairs.Add(i);
            if (s != _storey)
            {
                _storey = s;
                _selStorey = s;
            }
            _report = dropped > 0 ? $"Pasted; {dropped} piece(s) fell outside the grid and were left out (Building tab: make the grid bigger)." : "Pasted.";
            Changed();
        }

        // ------------------------------------------------------------ the selection's panel

        private void DrawSelected()
        {
            ValidateSelection();
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(SelectionCount == 0))
                {
                    if (GUILayout.Button(new GUIContent("Copy", "Ctrl+C")))
                        Copy();
                    if (GUILayout.Button(new GUIContent("Duplicate", "Ctrl+D: a copy to put down with the mouse.")))
                    {
                        Copy();
                        StartPaste();
                    }
                }
                using (new EditorGUI.DisabledScope(_clip == null))
                    if (GUILayout.Toggle(_pasting, new GUIContent("Paste", "Ctrl+V: the copy follows the mouse, R turns it, a click puts it down (Shift: and keeps pasting), Esc stops."), "Button") != _pasting)
                    {
                        if (_pasting)
                            _pasting = false;
                        else
                            StartPaste();
                    }
                using (new EditorGUI.DisabledScope(SelectionCount == 0))
                {
                    if (GUILayout.Button(new GUIContent("To Storey...", "A copy of it in the same place on another storey.")))
                    {
                        var menu = new GenericMenu();
                        for (int s = 0; s < _plan.Storeys.Count; s++)
                        {
                            int to = s;
                            if (to == _storey)
                                continue;
                            menu.AddItem(new GUIContent($"Storey {to + 1}"), false, () =>
                            {
                                Copy();
                                int turns = _pasteTurns;
                                _pasteTurns = 0;
                                Paste(_clipOrigin.x, _clipOrigin.y, to);
                                _pasteTurns = turns;
                            });
                        }
                        menu.ShowAsContext();
                    }
                    if (GUILayout.Button(new GUIContent("Delete", "Del: removes the selected openings, stairs, furniture and holes, and empties the selected cells.")))
                        DeleteSelection();
                }
            }
            if (_pasting)
                EditorGUILayout.HelpBox($"Pasting{(_pasteTurns > 0 ? $" (turned {_pasteTurns * 90}°)" : "")}: click where its corner goes. R turns it, Shift+click keeps pasting, Esc stops.", MessageType.None);
            if (SelectionCount == 0)
            {
                EditorGUILayout.LabelField("Nothing selected. Click something, or drag a box (Shift/Ctrl: add to it). Ctrl+A selects the storey.", EditorStyles.wordWrappedMiniLabel);
                return;
            }
            EditorGUILayout.LabelField(string.Join(", ", new[]
            {
                _selCells.Count > 0 ? $"{_selCells.Count} cell(s)" : null, _selOpenings.Count > 0 ? $"{_selOpenings.Count} opening(s)" : null,
                _selStairs.Count > 0 ? $"{_selStairs.Count} stair(s)" : null, _selProps.Count > 0 ? $"{_selProps.Count} furniture" : null,
                _selHoles.Count > 0 ? $"{_selHoles.Count} hole(s)" : null,
            }.Where(t => t != null)) + " selected", EditorStyles.miniBoldLabel);

            var so = new SerializedObject(_plan);
            bool changed = false;
            if (_selCells.Count > 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    int room = _plan.Get(_storey, _selCells.First().x, _selCells.First().y);
                    EditorGUI.showMixedValue = _selCells.Any(c => _plan.Get(_storey, c.x, c.y) != room);
                    var names = new[] { "0  (no room)" }.Concat(_plan.Rooms.Select((r, i) => $"{i + 1}  {r.Name}")).ToArray();
                    EditorGUI.BeginChangeCheck();
                    int pick = EditorGUILayout.Popup("Cells' Room", Mathf.Clamp(room, 0, _plan.Rooms.Count), names);
                    EditorGUI.showMixedValue = false;
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(_plan, "Cells' room");
                        foreach (var c in _selCells)
                            _plan.Set(_storey, c.x, c.y, pick);
                        Changed();
                    }
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Open Their Ceilings"))
                        SetOpen(true);
                    if (GUILayout.Button("Close Their Ceilings"))
                        SetOpen(false);
                }
            }
            if (_selOpenings.Count > 0)
            {
                EditorGUILayout.LabelField("Doors, windows, gaps", EditorStyles.boldLabel);
                var list = so.FindProperty("Openings");
                var idx = _selOpenings.ToList();
                foreach (var field in new[] { "Kind", "Span", "Width", "Height", "Sill", "Offset", "Prefab", "HingeRight", "ReverseSwing" })
                    changed |= Multi(list, idx, field);
                if (idx.Any(i => TarkovBuildingBuilder.PrefabOf(_plan, _plan.Openings[i]) != null))
                {
                    EditorGUILayout.LabelField(idx.Count == 1 ? "Its prefab (drag the handles in the Scene view, or type)" : "Their prefabs", EditorStyles.miniBoldLabel);
                    foreach (var field in new[] { "AutoFit", "PrefabOffset", "PrefabTurn", "PrefabScale" })
                        changed |= Multi(list, idx, field);
                    if (GUILayout.Button("Reset Prefab Adjustments"))
                    {
                        Undo.RecordObject(_plan, "Reset prefab adjustments");
                        foreach (var i in idx)
                        {
                            _plan.Openings[i].PrefabOffset = Vector3.zero;
                            _plan.Openings[i].PrefabTurn = 0f;
                            _plan.Openings[i].PrefabScale = 1f;
                        }
                        Changed();
                    }
                }
                if (idx.Any(i => _plan.Openings[i].Kind == Plan.OpeningKind.Door))
                {
                    EditorGUILayout.LabelField("Door settings", EditorStyles.miniBoldLabel);
                    changed |= Multi(list, idx, "Door.Override", "Change The Door");
                    if (idx.Any(i => _plan.Openings[i].Door != null && _plan.Openings[i].Door.Override))
                        using (new EditorGUI.IndentLevelScope())
                            foreach (var field in new[] { "State", "KeyId", "CanBeBreached", "OpenAngle", "Interactable" })
                                changed |= Multi(list, idx, "Door." + field);
                }
            }
            if (_selStairs.Count > 0)
            {
                EditorGUILayout.LabelField("Stairs", EditorStyles.boldLabel);
                var list = so.FindProperty("Stairs");
                foreach (var field in new[] { "Direction", "Length", "Width", "Prefab", "FitToFlight", "PrefabTurn" })
                    changed |= Multi(list, _selStairs.ToList(), field);
            }
            if (_selProps.Count > 0)
            {
                EditorGUILayout.LabelField("Furniture", EditorStyles.boldLabel);
                var list = so.FindProperty("Props");
                foreach (var field in _selProps.Count == 1 ? new[] { "Prefab", "Position", "Yaw", "Scale" } : new[] { "Prefab", "Yaw", "Scale" })
                    changed |= Multi(list, _selProps.ToList(), field);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Turn 90° Left"))
                        TurnProps(-90f);
                    if (GUILayout.Button("Turn 90° Right"))
                        TurnProps(90f);
                }
            }
            if (_selHoles.Count > 0)
            {
                EditorGUILayout.LabelField("Holes", EditorStyles.boldLabel);
                changed |= DrawHoleFields(so, _selHoles.ToList());
            }
            if (changed)
            {
                so.ApplyModifiedProperties();
                Changed();
            }
        }

        private void SetOpen(bool open)
        {
            Undo.RecordObject(_plan, open ? "Open ceilings" : "Close ceilings");
            var storey = _plan.Storeys[_storey];
            storey.OpenCeiling ??= new List<Vector2Int>();
            foreach (var c in _selCells)
            {
                storey.OpenCeiling.Remove(c);
                if (open && _plan.Get(_storey, c.x, c.y) != 0)
                    storey.OpenCeiling.Add(c);
            }
            Changed();
        }

        private void TurnProps(float by)
        {
            Undo.RecordObject(_plan, "Turn furniture");
            foreach (var i in _selProps)
                _plan.Props[i].Yaw = Mathf.Repeat(_plan.Props[i].Yaw + by, 360f);
            Changed();
        }

        // One field of several list elements: shown as the first's ("-" where they differ), written to all of them.
        // True when changed (the caller applies the SerializedObject).
        private static bool Multi(SerializedProperty list, List<int> indices, string path, string label = null)
        {
            var props = indices.Where(i => i < list.arraySize).Select(i => list.GetArrayElementAtIndex(i).FindPropertyRelative(path)).Where(p => p != null).ToList();
            if (props.Count == 0)
                return false;
            EditorGUI.showMixedValue = props.Skip(1).Any(p => !SerializedProperty.DataEquals(p, props[0]));
            EditorGUI.BeginChangeCheck();
            if (label != null)
                EditorGUILayout.PropertyField(props[0], new GUIContent(label, props[0].tooltip), true);
            else
                EditorGUILayout.PropertyField(props[0], true);
            EditorGUI.showMixedValue = false;
            if (!EditorGUI.EndChangeCheck())
                return false;
            foreach (var p in props.Skip(1))
                CopyValue(props[0], p);
            return true;
        }

        private static void CopyValue(SerializedProperty from, SerializedProperty to)
        {
            switch (from.propertyType)
            {
                case SerializedPropertyType.Integer: to.intValue = from.intValue; break;
                case SerializedPropertyType.Boolean: to.boolValue = from.boolValue; break;
                case SerializedPropertyType.Float: to.floatValue = from.floatValue; break;
                case SerializedPropertyType.String: to.stringValue = from.stringValue; break;
                case SerializedPropertyType.Color: to.colorValue = from.colorValue; break;
                case SerializedPropertyType.ObjectReference: to.objectReferenceValue = from.objectReferenceValue; break;
                case SerializedPropertyType.Enum: to.enumValueIndex = from.enumValueIndex; break;
                case SerializedPropertyType.Vector2: to.vector2Value = from.vector2Value; break;
                case SerializedPropertyType.Vector3: to.vector3Value = from.vector3Value; break;
                case SerializedPropertyType.Vector2Int: to.vector2IntValue = from.vector2IntValue; break;
                default:
                    to.boxedValue = from.boxedValue;
                    break;
            }
        }

        // ------------------------------------------------------------ furniture

        private const string PaletteKey = "TarkovSDK.BuildingFurniture";
        private GameObject _furniture;
        private float _furnitureYaw;
        // snap to: whole cells (their middles), half, quarter cells, or not at all
        private int _furnitureSnap = 1;
        private bool _againstWall = true;
        private float _furnitureLift;
        private List<GameObject> _palette;
        private Vector2 _paletteScroll;

        private List<GameObject> Palette()
        {
            if (_palette != null)
                return _palette;
            _palette = new List<GameObject>();
            foreach (var guid in EditorPrefs.GetString(PaletteKey, "").Split(';'))
            {
                var go = string.IsNullOrEmpty(guid) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (go != null && !_palette.Contains(go))
                    _palette.Add(go);
            }
            return _palette;
        }

        private void SavePalette() =>
            EditorPrefs.SetString(PaletteKey, string.Join(";", Palette().Select(p => AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(p))).Where(g => !string.IsNullOrEmpty(g))));

        private void AddToPalette(IEnumerable<GameObject> prefabs)
        {
            var list = Palette();
            foreach (var p in prefabs)
                if (p != null && PrefabUtility.IsPartOfPrefabAsset(p) && !list.Contains(p))
                    list.Add(p);
            SavePalette();
        }

        private void DrawFurnitureOptions()
        {
            var list = Palette();
            EditorGUILayout.LabelField("Furniture prefabs (drag prefabs or folders onto the box; right-click one to take it off)", EditorStyles.wordWrappedMiniLabel);
            const float size = 64f;
            int columns = Mathf.Max(1, Mathf.FloorToInt((position.width - 40f) / (size + 4f)));
            int rows = Mathf.Max(1, Mathf.CeilToInt(list.Count / (float)columns));
            var area = GUILayoutUtility.GetRect(columns * (size + 4f), Mathf.Min(rows, 3) * (size + 18f) + 4f, GUILayout.ExpandWidth(true));
            GUI.Box(area, list.Count == 0 ? "Drop furniture prefabs here" : "");
            var view = new Rect(0f, 0f, area.width - 16f, rows * (size + 18f));
            _paletteScroll = GUI.BeginScrollView(area, _paletteScroll, view);
            var e = Event.current;
            for (int i = 0; i < list.Count; i++)
            {
                var r = new Rect(4f + (i % columns) * (size + 4f), 2f + (i / columns) * (size + 18f), size, size);
                var p = list[i];
                var thumb = AssetPreview.GetAssetPreview(p) ?? AssetPreview.GetMiniThumbnail(p);
                if (p == _furniture)
                    EditorGUI.DrawRect(new Rect(r.x - 2f, r.y - 2f, r.width + 4f, r.height + 18f), new Color(0.3f, 0.55f, 1f, 0.6f));
                if (thumb != null)
                    GUI.DrawTexture(r, thumb, ScaleMode.ScaleToFit);
                GUI.Label(new Rect(r.x, r.yMax, r.width, 16f), p.name, EditorStyles.miniLabel);
                if (e.type == EventType.MouseDown && new Rect(r.x, r.y, r.width, r.height + 16f).Contains(e.mousePosition))
                {
                    if (e.button == 1)
                    {
                        int at = i;
                        var menu = new GenericMenu();
                        menu.AddItem(new GUIContent("Take Off The Palette"), false, () => { Palette().RemoveAt(at); SavePalette(); Repaint(); });
                        menu.AddItem(new GUIContent("Show In Project"), false, () => EditorGUIUtility.PingObject(p));
                        menu.ShowAsContext();
                    }
                    else
                        _furniture = p;
                    e.Use();
                }
            }
            GUI.EndScrollView();
            if ((e.type == EventType.DragUpdated || e.type == EventType.DragPerform) && area.Contains(e.mousePosition))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (e.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    var dropped = new List<GameObject>();
                    foreach (var path in DragAndDrop.paths)
                        if (AssetDatabase.IsValidFolder(path))
                            dropped.AddRange(AssetDatabase.FindAssets("t:Prefab", new[] { path }).Take(300).Select(g => AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(g))));
                    dropped.AddRange(DragAndDrop.objectReferences.OfType<GameObject>());
                    AddToPalette(dropped);
                    if (_furniture == null)
                        _furniture = Palette().FirstOrDefault();
                }
                e.Use();
            }
            EditorGUI.BeginChangeCheck();
            var picked = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Place", "The prefab a click places (it joins the palette)."), _furniture, typeof(GameObject), false);
            if (EditorGUI.EndChangeCheck())
            {
                _furniture = picked;
                AddToPalette(new[] { picked });
            }
            _furnitureSnap = EditorGUILayout.Popup(new GUIContent("Snap", "Where its pivot goes: the middle of a cell, every half or quarter cell, or anywhere."), _furnitureSnap,
                new[] { "Cell Middles", "Half Cells", "Quarter Cells", "Free" });
            _againstWall = EditorGUILayout.Toggle(new GUIContent("Back To Walls", "Near a wall, it turns its back (-Z) to the wall and stands against it; the turn below adds to that."), _againstWall);
            using (new EditorGUILayout.HorizontalScope())
            {
                _furnitureYaw = EditorGUILayout.Slider(new GUIContent("Turn", "R: 90° (Shift+R the other way)."), _furnitureYaw, 0f, 345f);
                _furnitureYaw = Mathf.Round(_furnitureYaw / 15f) * 15f;
            }
            _furnitureLift = EditorGUILayout.FloatField(new GUIContent("Lift", "Metres above the floor (shelves, things on tables)."), _furnitureLift);
            int here = (_plan.Props ?? new List<Plan.Prop>()).Count(p => p != null && p.Storey == _storey);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"{here} on this storey", EditorStyles.miniLabel);
                using (new EditorGUI.DisabledScope(here == 0))
                    if (GUILayout.Button("Remove All On This Storey", GUILayout.Width(170f)))
                    {
                        Undo.RecordObject(_plan, "Remove furniture");
                        _plan.Props.RemoveAll(p => p == null || p.Storey == _storey);
                        ClearSelection();
                        Changed();
                    }
            }
        }

        // where a click would put the furniture: snapped, and backed onto a wall when one is near
        private bool FurniturePose(float fx, float fz, out Vector3 position, out float yaw)
        {
            float cell = _plan.CellSize;
            var p = new Vector2(fx, fz) * cell;
            float step = cell * (_furnitureSnap == 0 ? 1f : _furnitureSnap == 1 ? 0.5f : 0.25f);
            if (_furnitureSnap == 0)
                p = new Vector2((Mathf.Floor(p.x / step) + 0.5f) * step, (Mathf.Floor(p.y / step) + 0.5f) * step);
            else if (_furnitureSnap < 3)
                p = new Vector2(Mathf.Round(p.x / step) * step, Mathf.Round(p.y / step) * step);
            yaw = _furnitureYaw;
            position = new Vector3(p.x, _furnitureLift, p.y);
            if (_furniture == null)
                return false;
            if (_againstWall && NearestWall(new Vector2(fx, fz) * cell, cell * 0.9f, out var face, out var inward))
            {
                yaw = Mathf.Repeat(Mathf.Atan2(inward.x, inward.y) * Mathf.Rad2Deg + _furnitureYaw, 360f);
                // the footprint's reach behind its pivot, towards the wall
                var b = TarkovBuildingBuilder.PrefabBounds(_furniture);
                var q = Quaternion.Euler(0f, yaw, 0f);
                var back = new Vector3(-inward.x, 0f, -inward.y);
                float reach = 0f;
                for (int i = 0; i < 4; i++)
                {
                    var c = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, 0f, (i & 2) == 0 ? b.min.z : b.max.z);
                    reach = Mathf.Max(reach, Vector3.Dot(q * c, back));
                }
                // along the wall where the mouse is (snapped), out from its face by that reach
                var along = new Vector2(inward.y, -inward.x);
                float t = Vector2.Dot(p - face, along);
                var at = face + along * t + inward * (reach + 0.01f);
                position = new Vector3(at.x, _furnitureLift, at.y);
            }
            return true;
        }

        // the nearest wall face within reach of p (metres): a point on it and the way into the room p is in
        private bool NearestWall(Vector2 p, float reach, out Vector2 face, out Vector2 inward)
        {
            float cell = _plan.CellSize;
            face = inward = Vector2.zero;
            float best = reach;
            bool found = false;
            int cx = Mathf.FloorToInt(p.x / cell), cz = Mathf.FloorToInt(p.y / cell);
            for (int x = cx - 1; x <= cx + 2; x++)
                for (int z = cz - 1; z <= cz + 2; z++)
                    foreach (bool alongZ in new[] { false, true })
                    {
                        if (alongZ ? x < 0 || x > _plan.Width || z < 0 || z >= _plan.Depth : x < 0 || x >= _plan.Width || z < 0 || z > _plan.Depth)
                            continue;
                        float t = TarkovBuildingBuilder.WallOn(_plan, _storey, x, z, alongZ);
                        if (t <= 0f)
                            continue;
                        var a = new Vector2(x * cell, z * cell);
                        var b = alongZ ? a + new Vector2(0f, cell) : a + new Vector2(cell, 0f);
                        var u = (b - a).normalized;
                        float s = Mathf.Clamp(Vector2.Dot(p - a, u), 0f, cell);
                        var closest = a + u * s;
                        var n = new Vector2(-u.y, u.x);
                        float side = Vector2.Dot(p - closest, n);
                        if (Mathf.Abs(side) < 1e-4f)
                            continue;
                        if (side < 0f)
                            n = -n;
                        float d = Mathf.Abs(side) - t * 0.5f;
                        if (d < best && Mathf.Abs(Vector2.Dot(p - a, u) - s) < 0.01f)
                        {
                            best = d;
                            face = closest + n * (t * 0.5f);
                            inward = n;
                            found = true;
                        }
                    }
            return found;
        }

        private bool FurnitureInput(float fx, float fz, Event e)
        {
            if (e.type != EventType.MouseDown)
                return false;
            if (e.shift)
            {
                int hit = PropAt(fx, fz);
                if (hit >= 0)
                {
                    Undo.RecordObject(_plan, "Remove furniture");
                    _plan.Props.RemoveAt(hit);
                    ClearSelection();
                    Changed();
                }
                e.Use();
                return true;
            }
            if (!FurniturePose(fx, fz, out var at, out float yaw))
            {
                _report = "Pick a furniture prefab first (drag prefabs onto the palette).";
                Repaint();
                e.Use();
                return true;
            }
            int cx = Mathf.FloorToInt(at.x / _plan.CellSize), cz = Mathf.FloorToInt(at.z / _plan.CellSize);
            if (cx < 0 || cz < 0 || cx >= _plan.Width || cz >= _plan.Depth)
                return false;
            Undo.RecordObject(_plan, "Place furniture");
            _plan.Props ??= new List<Plan.Prop>();
            _plan.Props.Add(new Plan.Prop { Prefab = _furniture, Storey = _storey, Position = at, Yaw = yaw, Scale = 1f });
            SelectOnly(_selProps, _plan.Props.Count - 1);
            e.Use();
            Changed();
            return true;
        }

        // ------------------------------------------------------------ holes

        private Vector3 _holeSize = new Vector3(1.4f, 1.4f, 0.8f);
        private float _holeRough = 0.5f, _holeChunk = 0.35f, _holeBlast = 6f;
        private Plan.DebrisKind _holeDebris = Plan.DebrisKind.None;
        private bool _holeShot;
        // a hole through a wall or floor clicked goes right through it, whatever its thickness
        private bool _holeThrough = true;
        private readonly BoxBoundsHandle _holeHandle = new BoxBoundsHandle();

        private void DrawHoleOptions()
        {
            EditorGUILayout.LabelField("New holes", EditorStyles.miniBoldLabel);
            _holeSize.x = EditorGUILayout.Slider(new GUIContent("Width", "Across the face clicked."), _holeSize.x, 0.2f, 8f);
            _holeSize.y = EditorGUILayout.Slider(new GUIContent("Height", "Up the face clicked (on a floor: its other way across)."), _holeSize.y, 0.2f, 8f);
            _holeThrough = EditorGUILayout.Toggle(new GUIContent("Right Through", "The hole goes through the whole wall (or floor) clicked, however thick. Off: Depth into it from the face clicked (a dent)."), _holeThrough);
            using (new EditorGUI.DisabledScope(_holeThrough))
                _holeSize.z = EditorGUILayout.Slider(new GUIContent("Depth", "Into what was clicked, from its face."), _holeSize.z, 0.05f, 3f);
            _holeRough = EditorGUILayout.Slider(new GUIContent("Rough", "0: a clean box. Higher: ragged edges and gouges."), _holeRough, 0f, 1f);
            _holeDebris = (Plan.DebrisKind)EditorGUILayout.EnumPopup(new GUIContent("Debris", "None: an open hole. Rubble: its material lies in chunks on the floor (physics props). Breakable: the wall stays whole until an explosion (or a bullet) blows the chunks out."), _holeDebris);
            if (_holeDebris != Plan.DebrisKind.None)
            {
                _holeChunk = EditorGUILayout.Slider("Chunk Size", _holeChunk, 0.15f, 1f);
                if (_holeDebris == Plan.DebrisKind.Breakable)
                {
                    _holeBlast = EditorGUILayout.Slider(new GUIContent("Blast Radius", "Explosions this close break it."), _holeBlast, 1f, 20f);
                    _holeShot = EditorGUILayout.Toggle("Breaks When Shot", _holeShot);
                }
            }
            var holes = _plan.Holes ?? new List<Plan.Hole>();
            var mine = Enumerable.Range(0, holes.Count).Where(i => OnStorey(holes[i], _storey)).ToList();
            EditorGUILayout.LabelField($"{mine.Count} hole(s) on this storey", EditorStyles.miniLabel);
            if (_selHoles.Count > 0)
            {
                var so = new SerializedObject(_plan);
                if (DrawHoleFields(so, _selHoles.ToList()))
                {
                    so.ApplyModifiedProperties();
                    Changed();
                }
                if (GUILayout.Button("Delete"))
                    DeleteSelection();
            }
        }

        private bool DrawHoleFields(SerializedObject so, List<int> indices)
        {
            var list = so.FindProperty("Holes");
            bool changed = false;
            foreach (var field in indices.Count == 1 ? new[] { "Center", "Size", "Rough", "Debris" } : new[] { "Size", "Rough", "Debris" })
                changed |= Multi(list, indices, field);
            if (indices.Any(i => _plan.Holes[i].Debris != Plan.DebrisKind.None))
                changed |= Multi(list, indices, "ChunkSize");
            if (indices.Any(i => _plan.Holes[i].Debris == Plan.DebrisKind.Breakable))
            {
                changed |= Multi(list, indices, "BlastRadius");
                changed |= Multi(list, indices, "BreaksWhenShot");
            }
            if (GUILayout.Button(new GUIContent("Go Right Through", "Sizes them through the whole wall (or floor) they're in: from one face to the other, 10 cm past each.")))
            {
                Undo.RecordObject(_plan, "Hole through");
                int missed = 0;
                foreach (var i in indices)
                    if (!FitThrough(_plan.Holes[i]))
                        missed++;
                _report = missed > 0 ? $"{missed} hole(s) aren't in a wall or floor of this building (nothing found either side of them)." : null;
                Changed();
            }
            if (GUILayout.Button(new GUIContent("New Shape", "Another random rim (and chunks).")))
            {
                Undo.RecordObject(_plan, "New hole shape");
                foreach (var i in indices)
                    _plan.Holes[i].Seed = Random.Range(1, 100000);
                Changed();
            }
            return changed;
        }

        // the building's surface (or one of its holes) under the mouse, in the building's space
        private bool HoleUnderMouse(Transform root, Event e, out Vector3 point, out Vector3 normal, out int hole)
        {
            point = normal = Vector3.zero;
            hole = -1;
            var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            var local = new Ray(root.InverseTransformPoint(ray.origin), root.InverseTransformDirection(ray.direction));
            float nearest = float.MaxValue;
            var holes = _plan.Holes ?? new List<Plan.Hole>();
            for (int i = 0; i < holes.Count; i++)
                if (holes[i] != null && new Bounds(holes[i].Center, new Vector3(Mathf.Abs(holes[i].Size.x), Mathf.Abs(holes[i].Size.y), Mathf.Abs(holes[i].Size.z))).IntersectRay(local, out float d) && d < nearest)
                {
                    nearest = d;
                    hole = i;
                }
            var physics = root.gameObject.scene.GetPhysicsScene();
            int mask = LayerMask.GetMask("LowPolyCollider", "HighPolyCollider");
            if (physics.Raycast(ray.origin, ray.direction, out var hit, 2000f, mask, QueryTriggerInteraction.Ignore) && hit.transform.IsChildOf(root))
            {
                float d = root.InverseTransformVector(hit.point - ray.origin).magnitude;
                if (d < nearest)
                {
                    hole = -1;
                    point = root.InverseTransformPoint(hit.point);
                    normal = root.InverseTransformDirection(hit.normal);
                    return true;
                }
            }
            return hole >= 0;
        }

        // a new hole at a point on a face: the size given across the face, the depth into it from just outside
        private Plan.Hole NewHole(Vector3 point, Vector3 normal)
        {
            var a = new Vector3(Mathf.Abs(normal.x), Mathf.Abs(normal.y), Mathf.Abs(normal.z));
            Vector3 n, size;
            if (a.x >= a.y && a.x >= a.z)
            {
                n = new Vector3(Mathf.Sign(normal.x), 0f, 0f);
                size = new Vector3(_holeSize.z, _holeSize.y, _holeSize.x);
            }
            else if (a.z >= a.y)
            {
                n = new Vector3(0f, 0f, Mathf.Sign(normal.z));
                size = new Vector3(_holeSize.x, _holeSize.y, _holeSize.z);
            }
            else
            {
                n = new Vector3(0f, Mathf.Sign(normal.y), 0f);
                size = new Vector3(_holeSize.x, _holeSize.z, _holeSize.y);
            }
            float depth = _holeSize.z;
            var centre = point - n * (depth * 0.5f - 0.1f);
            int axis = Mathf.Abs(n.x) > 0.5f ? 0 : Mathf.Abs(n.y) > 0.5f ? 1 : 2;
            // through the wall clicked: from 10 cm out of its face to 10 cm out of its far face
            var root = TarkovBuildingBuilder.Building(_plan);
            if (_holeThrough && WallSpan(root, point - n * 0.01f, axis, out float from, out float to))
            {
                centre[axis] = (from + to) * 0.5f;
                size[axis] = to - from + 0.2f;
            }
            return new Plan.Hole
            {
                Center = centre,
                Size = size,
                Rough = _holeRough,
                Seed = Random.Range(1, 100000),
                Debris = _holeDebris,
                ChunkSize = _holeChunk,
                BlastRadius = _holeBlast,
                BreaksWhenShot = _holeShot,
            };
        }

        // The building's solid (wall, floor) at q (building space, inside it or on its face) along an axis: from the face
        // where it starts to the one where it ends. Rays come in from each side up to q; the faces they enter by
        // nearest q are its two. The holes' own pieces don't count.
        private static bool WallSpan(Transform root, Vector3 q, int axis, out float a, out float b)
        {
            a = b = q[axis];
            var physics = root.gameObject.scene.GetPhysicsScene();
            int mask = LayerMask.GetMask("LowPolyCollider", "HighPolyCollider");
            var dir = Vector3.zero;
            dir[axis] = 1f;
            var hits = new RaycastHit[64];
            const float reach = 3f;
            bool found = false;
            foreach (float side in new[] { -1f, 1f })
            {
                // from beyond the side towards q: the entering face nearest q
                var from = q + dir * (side * reach);
                int n = physics.Raycast(root.TransformPoint(from), root.TransformDirection(-dir * side), hits, reach + 0.02f, mask, QueryTriggerInteraction.Ignore);
                float best = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    var h = hits[i];
                    if (!h.collider.transform.IsChildOf(root) || InHoles(h.collider.transform, root))
                        continue;
                    var at = root.InverseTransformPoint(h.point);
                    float d = Mathf.Abs(at[axis] - q[axis]);
                    if (d < best && Vector3.Dot(root.InverseTransformDirection(h.normal), dir * side) > 0.7f)
                    {
                        best = d;
                        if (side < 0f)
                            a = at[axis];
                        else
                            b = at[axis];
                    }
                }
                if (best == float.MaxValue)
                    return false;
                found = true;
            }
            return found && b - a > 0.005f && b - a < 2.5f;
        }

        private static bool InHoles(Transform t, Transform root)
        {
            for (var x = t; x != null && x != root; x = x.parent)
                if (x.name == "Holes")
                    return true;
            return false;
        }

        // the hole sized through the wall (or floor) it's in, found beside it (it's cut where it is)
        private bool FitThrough(Plan.Hole h)
        {
            var root = TarkovBuildingBuilder.Building(_plan);
            Physics.SyncTransforms();
            var size = new Vector3(Mathf.Abs(h.Size.x), Mathf.Abs(h.Size.y), Mathf.Abs(h.Size.z));
            int axis = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
            int u = (axis + 1) % 3, v = (axis + 2) % 3;
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var (du, dv) in new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) })
            {
                var q = h.Center;
                q[u] += du * (size[u] * 0.5f + 0.15f);
                q[v] += dv * (size[v] * 0.5f + 0.15f);
                if (WallSpan(root, q, axis, out float a, out float b))
                {
                    lo = Mathf.Min(lo, a);
                    hi = Mathf.Max(hi, b);
                }
            }
            if (lo > hi)
                return false;
            var c = h.Center;
            c[axis] = (lo + hi) * 0.5f;
            size[axis] = hi - lo + 0.2f;
            h.Center = c;
            h.Size = size;
            return true;
        }

        // the Holes tool in the Scene view: a click on the building cuts a hole there, on a hole selects it (Shift:
        // removes it); the hover shows where it would go
        private void HoleScene(Transform root, Event e)
        {
            if (e.type == EventType.Repaint || e.type == EventType.MouseMove)
            {
                if (e.type == EventType.MouseMove)
                    SceneView.RepaintAll();
                else if (HoleUnderMouse(root, e, out var point, out var normal, out int over) && over < 0)
                {
                    var h = NewHole(point, normal);
                    using (new Handles.DrawingScope(new Color(1f, 0.35f, 0.25f), root.localToWorldMatrix))
                        Handles.DrawWireCube(h.Center, h.Size);
                }
                return;
            }
            if (e.type != EventType.MouseDown || e.button != 0)
                return;
            if (!HoleUnderMouse(root, e, out var at, out var n, out int hole))
                return;
            if (hole >= 0)
            {
                if (e.shift)
                {
                    Undo.RecordObject(_plan, "Remove hole");
                    _plan.Holes.RemoveAt(hole);
                    ClearSelection();
                    Changed();
                }
                else
                {
                    if (!(e.control || e.command))
                        ClearSelection();
                    Toggle(_selHoles, hole, e.control || e.command);
                    Repaint();
                }
                e.Use();
                return;
            }
            Undo.RecordObject(_plan, "Cut hole");
            _plan.Holes ??= new List<Plan.Hole>();
            _plan.Holes.Add(NewHole(at, n));
            SelectOnly(_selHoles, _plan.Holes.Count - 1);
            e.Use();
            Changed();
        }

        // the one selected hole's box and move handles; furniture's move and turn handles: true while dragged
        private bool EditHandles(Transform root)
        {
            if (_selHoles.Count == 1 && (_tool == Tool.Holes || _tool == Tool.Select))
            {
                var h = _plan.Holes[_selHoles.First()];
                using (new Handles.DrawingScope(new Color(1f, 0.45f, 0.3f), root.localToWorldMatrix))
                {
                    EditorGUI.BeginChangeCheck();
                    _holeHandle.center = h.Center;
                    _holeHandle.size = new Vector3(Mathf.Abs(h.Size.x), Mathf.Abs(h.Size.y), Mathf.Abs(h.Size.z));
                    _holeHandle.DrawHandle();
                    var moved = Handles.PositionHandle(_holeHandle.center, Quaternion.identity);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(_plan, "Edit hole");
                        h.Size = Vector3.Max(_holeHandle.size, Vector3.one * 0.05f);
                        h.Center = moved != _holeHandle.center ? moved : _holeHandle.center;
                        Changed();
                        return true;
                    }
                }
            }
            if (_selProps.Count == 1 && _tool == Tool.Select)
            {
                var p = _plan.Props[_selProps.First()];
                var at = root.TransformPoint(p.Position + Vector3.up * _plan.Base(p.Storey));
                var frame = root.rotation * Quaternion.Euler(0f, p.Yaw, 0f);
                EditorGUI.BeginChangeCheck();
                var moved = Handles.PositionHandle(at, frame);
                Handles.color = new Color(1f, 0.85f, 0.3f);
                var turned = Handles.Disc(frame, at, root.up, HandleUtility.GetHandleSize(at) * 0.9f, false, 15f);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(_plan, "Move furniture");
                    p.Position = root.InverseTransformPoint(moved) - Vector3.up * _plan.Base(p.Storey);
                    var forward = Quaternion.Inverse(root.rotation) * (turned * Vector3.forward);
                    p.Yaw = Mathf.Repeat(Mathf.Round(Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg), 360f);
                    Changed();
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------ drawing

        // furniture, holes, the selection, the box being dragged and what's being pasted or placed, through map
        // (metres on the plan to drawing space)
        private void DrawEditing(System.Func<Vector2, Vector3> map, bool gui)
        {
            float cell = _plan.CellSize;
            float thin = gui ? 1.5f : 2.5f, thick = gui ? 3f : 5f;
            var props = _plan.Props ?? new List<Plan.Prop>();
            for (int i = 0; i < props.Count; i++)
            {
                var p = props[i];
                if (p == null || p.Storey != _storey || p.Prefab == null)
                    continue;
                bool selected = _selProps.Contains(i);
                Outline(map, Footprint(p, p.Position, p.Yaw), selected ? Color.white : new Color(0.85f, 0.6f, 0.35f), selected ? thick : thin, gui ? 0.25f : 0.12f);
            }
            var holes = _plan.Holes ?? new List<Plan.Hole>();
            for (int i = 0; i < holes.Count; i++)
                if (OnStorey(holes[i], _storey))
                    Outline(map, HoleFootprint(holes[i]), _selHoles.Contains(i) ? Color.white : new Color(1f, 0.3f, 0.25f), _selHoles.Contains(i) ? thick : thin, 0f);
            // selected cells and stairs
            Handles.color = new Color(1f, 1f, 1f, 0.9f);
            foreach (var c in _selCells)
                Outline(map, new[] { new Vector2(c.x, c.y) * cell, new Vector2(c.x + 1, c.y) * cell, new Vector2(c.x + 1, c.y + 1) * cell, new Vector2(c.x, c.y + 1) * cell }.Select(v => v + (Vector2.one * cell * 0.5f - v) * 0.12f).ToArray(),
                    Color.white, thin, gui ? 0.15f : 0.08f);
            foreach (var i in _selStairs)
                foreach (var c in TarkovBuildingBuilder.StairFootprint(_plan.Stairs[i]))
                    Outline(map, new[] { new Vector2(c.x, c.y) * cell, new Vector2(c.x + 1, c.y) * cell, new Vector2(c.x + 1, c.y + 1) * cell, new Vector2(c.x, c.y + 1) * cell }, Color.white, thick, 0f);
            if (_marqueeFrom.HasValue)
            {
                var a = _marqueeFrom.Value * cell;
                var b = _marqueeTo * cell;
                Outline(map, new[] { a, new Vector2(b.x, a.y), b, new Vector2(a.x, b.y) }, new Color(0.5f, 0.8f, 1f), thin, gui ? 0.12f : 0.06f);
            }
            if (!_hovering)
                return;
            if (_pasting && _clip != null && _tool == Tool.Select)
            {
                var clip = _clip.Copy();
                for (int t = 0; t < _pasteTurns; t++)
                    clip.Turn();
                var o = new Vector2(Mathf.Floor(_hover.x), Mathf.Floor(_hover.y)) * cell;
                foreach (var (c, room, _) in clip.Cells)
                {
                    var col = room > 0 && room <= _plan.Rooms.Count ? _plan.Rooms[room - 1].Color : new Color(0.1f, 0.1f, 0.1f);
                    Outline(map, new[] { new Vector2(c.x, c.y), new Vector2(c.x + 1, c.y), new Vector2(c.x + 1, c.y + 1), new Vector2(c.x, c.y + 1) }.Select(v => o + v * cell).ToArray(), col, thin, 0.45f);
                }
                foreach (var (op, _, _) in clip.Openings)
                {
                    var from = o + new Vector2(op.X, op.Z) * cell;
                    var to = from + (op.AlongZ ? new Vector2(0f, Mathf.Max(1, op.Span)) : new Vector2(Mathf.Max(1, op.Span), 0f)) * cell;
                    Handles.color = KindColor(op.Kind);
                    Handles.DrawAAPolyLine(thick, map(from), map(to));
                }
                foreach (var st in clip.Stairs)
                    foreach (var c in TarkovBuildingBuilder.StairFootprint(st))
                        Outline(map, new[] { new Vector2(c.x, c.y), new Vector2(c.x + 1, c.y), new Vector2(c.x + 1, c.y + 1), new Vector2(c.x, c.y + 1) }.Select(v => o + v * cell).ToArray(), Color.white, thin, 0f);
                foreach (var p in clip.Props)
                    if (p.Prefab != null)
                        Outline(map, Footprint(p, p.Position + new Vector3(o.x, 0f, o.y), p.Yaw), new Color(0.85f, 0.6f, 0.35f), thin, 0.3f);
                Outline(map, new[] { o, o + new Vector2(clip.W, 0f) * cell, o + new Vector2(clip.W, clip.D) * cell, o + new Vector2(0f, clip.D) * cell }, new Color(0.5f, 0.8f, 1f), thick, 0f);
            }
            if (_tool == Tool.Furniture && _furniture != null && FurniturePose(_hover.x, _hover.y, out var at, out float yaw))
            {
                var ghost = new Plan.Prop { Prefab = _furniture, Scale = 1f };
                var foot = Footprint(ghost, at, yaw);
                Outline(map, foot, new Color(0.4f, 1f, 0.5f), thick, 0.3f);
                // its front
                var mid = (foot[2] + foot[3]) * 0.5f;
                var centre = (foot[0] + foot[2]) * 0.5f;
                Handles.color = new Color(0.4f, 1f, 0.5f);
                Handles.DrawAAPolyLine(thick, map(centre), map(mid));
            }
        }

        private static void Outline(System.Func<Vector2, Vector3> map, Vector2[] poly, Color color, float width, float fill)
        {
            var points = poly.Select(map).ToArray();
            if (fill > 0f)
            {
                Handles.color = new Color(color.r, color.g, color.b, fill);
                Handles.DrawAAConvexPolygon(points);
            }
            Handles.color = color;
            Handles.DrawAAPolyLine(width, points.Concat(new[] { points[0] }).ToArray());
        }
    }
}
