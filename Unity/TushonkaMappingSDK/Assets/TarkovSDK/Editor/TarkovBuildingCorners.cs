using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Plan = TarkovSDK.TarkovBuilding;

namespace TarkovSDK.Editor
{
    // The Building Builder's bevels (rounded and cut corners) and the free-form geometry they need, next to the boxes:
    //  - a bevel at a room's corner replaces the walls along its two sides, Radius from the corner, with an arc of
    //    Segments chords (one for a chamfer) on the wall's middle line, tangent to the walls it meets. The walls stop
    //    at the tangent points; the arc's wall is offset from the arc with mitres, its outer side starting square to
    //    the wall it carries on from, so it closes against the wall's end without a notch.
    //  - the cells the arc crosses are clipped by its chords: the room keeps what's inside, the room beside it (or
    //    nothing, outside) gets the rest, as convex pieces; floors, roofs, rain cover and indoor volumes use them.
    //  - outlines: a region's edge (one room, or the whole storey) traced round the grid, the bevels' arcs put in at
    //    their corners, each piece carrying its wall's half thickness: the trims run along them.
    // Points in the plane are (x, z) in metres in the building's space.
    public static partial class TarkovBuildingBuilder
    {
        // a bevel checked against the storey's rooms
        internal sealed class Corner
        {
            public Plan.Bevel Source;
            public Vector2Int V;
            public int Dx, Dz;
            public int Room, Outer;
            public float Radius;
            // the corner's square of cells each way (Radius rounded up)
            public int Cells;
            public Vector2 Centre;
            // the arc's points, from A (on the line x = V.x) to B (on the line z = V.z)
            public readonly List<Vector2> Arc = new List<Vector2>();
            // +1 when the arc's centre is on the left going from A to B
            public float Turn;
            public bool Exterior => Outer == 0;
            public bool Smooth => Arc.Count > 2;
            // the way along the wall before A (towards the corner) and after B (away from it)
            public Vector2 TangentA => new Vector2(0f, -Dz);
            public Vector2 TangentB => new Vector2(Dx, 0f);

            public Vector2Int Cell(int i, int j) => new Vector2Int(Dx > 0 ? V.x + i : V.x - 1 - i, Dz > 0 ? V.y + j : V.y - 1 - j);
            public Vector2 Inward(int k) => Left((Arc[k + 1] - Arc[k]).normalized) * Turn;
            public bool OnLine(bool alongZ, int line) => alongZ ? V.x == line : V.y == line;

            // where it takes the wall away on its line, in metres along the line
            public (float From, float To) Span(bool alongZ, float cell)
            {
                float a = alongZ ? V.y * cell : V.x * cell;
                float b = a + (alongZ ? Dz : Dx) * Radius;
                return (Mathf.Min(a, b), Mathf.Max(a, b));
            }

            public bool InSquare(Vector2Int c)
            {
                int i = Dx > 0 ? c.x - V.x : V.x - 1 - c.x, j = Dz > 0 ? c.y - V.y : V.y - 1 - c.y;
                return i >= 0 && j >= 0 && i < Cells && j < Cells;
            }
        }

        internal static Vector2 Left(Vector2 d) => new Vector2(-d.y, d.x);
        internal static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
        private static Vector2 Flat(Vector3 p) => new Vector2(p.x, p.z);
        private static Vector3 At(Vector2 p, float y) => new Vector3(p.x, y, p.y);

        // the storey's bevels that fit its rooms (the rest noted)
        internal static List<Corner> Corners(Plan plan, int s, ICollection<string> notes = null)
        {
            var list = new List<Corner>();
            if (plan.Bevels == null)
                return list;
            float cell = plan.CellSize;
            void Note(string text)
            {
                if (notes != null && !notes.Contains(text))
                    notes.Add(text);
            }
            foreach (var b in plan.Bevels)
            {
                if (b == null || b.Storey != s)
                    continue;
                int q = ((b.Quadrant % 4) + 4) % 4;
                var c = new Corner { Source = b, V = new Vector2Int(b.X, b.Z), Dx = q == 0 || q == 3 ? 1 : -1, Dz = q < 2 ? 1 : -1 };
                float cells = Mathf.Max(0.25f, b.Radius);
                c.Cells = Mathf.CeilToInt(cells - 0.001f);
                c.Radius = cells * cell;
                var first = c.Cell(0, 0);
                c.Room = plan.Get(s, first.x, first.y);
                string at = $"storey {s + 1}, corner {b.X},{b.Z}";
                if (c.Room == 0)
                {
                    Note($"the bevel at {at} isn't in a room");
                    continue;
                }
                if (list.Any(o => o.V == c.V && o.Dx == c.Dx && o.Dz == c.Dz))
                    continue;
                bool whole = true;
                for (int i = 0; i < c.Cells && whole; i++)
                    for (int j = 0; j < c.Cells && whole; j++)
                    {
                        var k = c.Cell(i, j);
                        whole = plan.Get(s, k.x, k.y) == c.Room;
                    }
                if (!whole)
                {
                    Note($"the room at the bevel at {at} isn't {c.Cells} x {c.Cells} cells there (make its radius smaller)");
                    continue;
                }
                // the cells across both walls: one room (or none) all along
                int outer = -1;
                bool same = true;
                for (int i = 0; i < c.Cells; i++)
                    foreach (var k in new[] { new Vector2Int(c.Cell(i, 0).x, c.Dz > 0 ? c.V.y - 1 : c.V.y), new Vector2Int(c.Dx > 0 ? c.V.x - 1 : c.V.x, c.Cell(0, i).y) })
                    {
                        int r = plan.Get(s, k.x, k.y);
                        if (outer < 0)
                            outer = r;
                        else if (r != outer)
                            same = false;
                    }
                if (!same || outer == c.Room)
                {
                    Note($"the walls either side of the bevel at {at} must both face one other room (or the outside) all along its radius");
                    continue;
                }
                c.Outer = outer;
                if (list.Any(o => Overlaps(o, c, cell)))
                {
                    Note($"the bevel at {at} runs into another on the same wall (make one smaller)");
                    continue;
                }
                int segments = b.Kind == Plan.BevelKind.Chamfer ? 1 : Mathf.Clamp(b.Segments, 2, 24);
                c.Centre = new Vector2(c.V.x * cell + c.Dx * c.Radius, c.V.y * cell + c.Dz * c.Radius);
                for (int k = 0; k <= segments; k++)
                {
                    float t = Mathf.PI * 0.5f * k / segments;
                    c.Arc.Add(c.Centre + c.Radius * new Vector2(-c.Dx * Mathf.Cos(t), -c.Dz * Mathf.Sin(t)));
                }
                c.Turn = Cross(c.Arc[1] - c.Arc[0], c.Centre - c.Arc[0]) > 0f ? 1f : -1f;
                list.Add(c);
            }
            return list;
        }

        private static bool Overlaps(Corner a, Corner b, float cell)
        {
            foreach (bool alongZ in new[] { false, true })
            {
                int line = alongZ ? b.V.x : b.V.y;
                if (!a.OnLine(alongZ, line))
                    continue;
                var (a0, a1) = a.Span(alongZ, cell);
                var (b0, b1) = b.Span(alongZ, cell);
                if (a0 < b1 - 1e-4f && b0 < a1 - 1e-4f)
                    return true;
            }
            return false;
        }

        // a run of wall on a line (edges i..j) shortened where a bevel takes over: a run starting (or ending) in a
        // bevel's span starts (ends) at its tangent point, one inside it is gone. Runs end at the corner itself (the
        // rooms either side change there), but paint can split one inside the span.
        private static void ClipRun(List<Corner> corners, bool alongZ, int line, int i, int j, float cell, ref float u0, ref float u1)
        {
            foreach (var c in corners)
            {
                if (!c.OnLine(alongZ, line))
                    continue;
                int at = alongZ ? c.V.y : c.V.x, dir = alongZ ? c.Dz : c.Dx;
                if (dir > 0 && i >= at && i * cell < at * cell + c.Radius - 1e-4f)
                    u0 = Mathf.Max(u0, at * cell + c.Radius);
                if (dir < 0 && j <= at && j * cell > at * cell - c.Radius + 1e-4f)
                    u1 = Mathf.Min(u1, at * cell - c.Radius);
            }
        }

        // whether a point along a wall line (metres) is where a bevel took the wall away
        internal static bool OnCorner(List<Corner> corners, bool alongZ, int line, float u, float cell)
        {
            foreach (var c in corners)
            {
                if (!c.OnLine(alongZ, line))
                    continue;
                var (a, b) = c.Span(alongZ, cell);
                if (u > a + 1e-4f && u < b - 1e-4f)
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------ cut cells

        // the cells bevels cut, each as its pieces: the room's (inside every chord) and the cut-off parts, which go to
        // the room beside it (none outside); convex, wound as the cell (anticlockwise seen from above)
        internal static Dictionary<Vector2Int, List<(int Room, Vector2[] Poly)>> Pieces(Plan plan, List<Corner> corners)
        {
            var result = new Dictionary<Vector2Int, List<(int, Vector2[])>>();
            float cell = plan.CellSize;
            var cells = new HashSet<Vector2Int>();
            foreach (var c in corners)
                for (int i = 0; i < c.Cells; i++)
                    for (int j = 0; j < c.Cells; j++)
                        cells.Add(c.Cell(i, j));
            foreach (var k in cells)
            {
                var poly = new List<Vector2> { new Vector2(k.x * cell, k.y * cell), new Vector2((k.x + 1) * cell, k.y * cell), new Vector2((k.x + 1) * cell, (k.y + 1) * cell), new Vector2(k.x * cell, (k.y + 1) * cell) };
                var pieces = new List<(int, Vector2[])>();
                bool cut = false;
                int room = 0;
                foreach (var c in corners)
                {
                    if (!c.InSquare(k))
                        continue;
                    room = c.Room;
                    for (int i = 0; i + 1 < c.Arc.Count && poly.Count >= 3; i++)
                    {
                        var n = c.Inward(i);
                        var off = Clip(poly, c.Arc[i], -n);
                        if (Area(off) > 1e-5f)
                        {
                            cut = true;
                            if (c.Outer != 0)
                                pieces.Add((c.Outer, off.ToArray()));
                        }
                        poly = Clip(poly, c.Arc[i], n);
                    }
                }
                if (!cut)
                    continue;
                if (poly.Count >= 3 && Area(poly) > 1e-5f)
                    pieces.Add((room, poly.ToArray()));
                result[k] = pieces;
            }
            return result;
        }

        // A convex region as boxes (x, z rects) for the volumes: strips across it, halved until each box covers less
        // than most (10 cm) past its strip on average (one box would cover half a metre past a curve; the regions are already
        // off the wall's face, so this stays in the wall), alike neighbours merged back, across whichever way takes fewer.
        internal static List<Rect> Cover(List<Vector2> poly, float most = 0.1f)
        {
            if (poly.Count < 3 || Area(poly) < 1e-4f)
                return new List<Rect>();
            List<Rect> Strips(bool alongX)
            {
                var axis = alongX ? Vector2.right : Vector2.up;
                var list = new List<Rect>();
                void Strip(float s0, float s1)
                {
                    var strip = Clip(Clip(poly, axis * s0, axis), axis * s1, -axis);
                    if (strip.Count < 3)
                        return;
                    float b0 = strip.Min(q => alongX ? q.y : q.x), b1 = strip.Max(q => alongX ? q.y : q.x);
                    float over = ((b1 - b0) * (s1 - s0) - Mathf.Abs(Area(strip))) / (s1 - s0);
                    if (over > most && s1 - s0 > 0.15f)
                    {
                        Strip(s0, (s0 + s1) * 0.5f);
                        Strip((s0 + s1) * 0.5f, s1);
                        return;
                    }
                    if (list.Count > 0)
                    {
                        var last = list[list.Count - 1];
                        float l0 = alongX ? last.yMin : last.xMin, l1 = alongX ? last.yMax : last.xMax;
                        if (Mathf.Abs(l0 - b0) < 0.02f && Mathf.Abs(l1 - b1) < 0.02f)
                        {
                            list[list.Count - 1] = alongX ? Rect.MinMaxRect(last.xMin, Mathf.Max(l0, b0), s1, Mathf.Min(l1, b1)) : Rect.MinMaxRect(Mathf.Max(l0, b0), last.yMin, Mathf.Min(l1, b1), s1);
                            return;
                        }
                    }
                    list.Add(alongX ? Rect.MinMaxRect(s0, b0, s1, b1) : Rect.MinMaxRect(b0, s0, b1, s1));
                }
                Strip(alongX ? poly.Min(q => q.x) : poly.Min(q => q.y), alongX ? poly.Max(q => q.x) : poly.Max(q => q.y));
                return list;
            }
            var byX = Strips(true);
            var byZ = Strips(false);
            return byX.Count <= byZ.Count ? byX : byZ;
        }

        // the bevel's wall face off (negative: towards its centre) the arc, mitred, from A to B
        internal static List<Vector2> ArcFace(Corner c, float off)
        {
            int n = c.Arc.Count;
            var dirs = new Vector2[n + 1];
            dirs[0] = c.TangentA;
            for (int k = 0; k + 1 < n; k++)
                dirs[k + 1] = (c.Arc[k + 1] - c.Arc[k]).normalized;
            dirs[n] = c.TangentB;
            return Enumerable.Range(0, n).Select(k => Miter(c.Arc[k], dirs[k], off, dirs[k + 1], off, -c.Turn)).ToList();
        }

        // how far points a..b of a line bulge out past the straight line between them (away from the centre)
        internal static float Bulge(List<Vector2> line, int a, int b, Vector2 centre)
        {
            var u = (line[b] - line[a]).normalized;
            var outward = Left(u);
            if (Vector2.Dot(centre - line[a], outward) > 0f)
                outward = -outward;
            float most = 0f;
            for (int i = a + 1; i < b; i++)
                most = Mathf.Max(most, Vector2.Dot(line[i] - line[a], outward));
            return most;
        }

        // the part of a convex polygon on the side of the line through p that n points to
        internal static List<Vector2> Clip(List<Vector2> poly, Vector2 p, Vector2 n)
        {
            var result = new List<Vector2>();
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                float da = Vector2.Dot(a - p, n), db = Vector2.Dot(b - p, n);
                if (da >= 0f)
                    result.Add(a);
                if ((da >= 0f) != (db >= 0f))
                    result.Add(a + (b - a) * (da / (da - db)));
            }
            // drop repeats
            for (int i = result.Count - 1; i >= 0 && result.Count > 0; i--)
                if ((result[i] - result[(i + 1) % result.Count]).sqrMagnitude < 1e-10f)
                    result.RemoveAt(i);
            return result;
        }

        internal static float Area(IList<Vector2> poly)
        {
            float a = 0f;
            for (int i = 0; i < poly.Count; i++)
                a += Cross(poly[i], poly[(i + 1) % poly.Count]);
            return a * 0.5f;
        }

        // the point where the lines offset from two pieces meeting at p cross (off * side along each one's left normal);
        // straight on: just offset
        internal static Vector2 Miter(Vector2 p, Vector2 d1, float o1, Vector2 d2, float o2, float side)
        {
            var a = p + Left(d1) * (o1 * side);
            var b = p + Left(d2) * (o2 * side);
            float den = Cross(d1, d2);
            if (Mathf.Abs(den) < 1e-4f)
                return a;
            var q = a + d1 * (Cross(b - a, d2) / den);
            // a very sharp turn: no spike
            float most = Mathf.Max(Mathf.Abs(o1), Mathf.Abs(o2), 0.01f);
            return (q - p).sqrMagnitude > 16f * most * most ? (a + b) * 0.5f : q;
        }

        // ------------------------------------------------------------ free-form geometry

        // geometry that isn't a box: its faces go into the building's meshes as they are (nothing hides them), its
        // triangles into a mesh collider when it collides; no material = collision only
        private sealed class Shape
        {
            public int Group;
            public Material Material;
            // also in the outside-only LOD
            public bool Outside;
            // a trim: in the trims' own meshes, not the navmesh's
            public bool Trim;
            public Collide Collide;
            public Plan.Surface Surface;
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector3> N = new List<Vector3>();
            public readonly List<Vector2> U = new List<Vector2>();
            public readonly List<int> T = new List<int>();

            // triangles (indices into the points given) each wound to face the front side
            public void Tris(IList<Vector3> p, IList<Vector3> n, IList<Vector2> u, IList<int> tris, Vector3 front)
            {
                int start = V.Count;
                V.AddRange(p);
                N.AddRange(n);
                U.AddRange(u);
                for (int i = 0; i + 2 < tris.Count; i += 3)
                {
                    int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                    if (Vector3.Dot(Vector3.Cross(p[b] - p[a], p[c] - p[a]), front) < 0f)
                        (b, c) = (c, b);
                    T.Add(start + a);
                    T.Add(start + b);
                    T.Add(start + c);
                }
            }

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 na, Vector3 nb, Vector3 nc, Vector3 nd, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
            {
                if ((a - c).sqrMagnitude < 1e-10f && (b - d).sqrMagnitude < 1e-10f)
                    return;
                Tris(new[] { a, b, c, d }, new[] { na, nb, nc, nd }, new[] { ua, ub, uc, ud }, new[] { 0, 1, 2, 0, 2, 3 }, na + nb + nc + nd);
            }

            // a flat polygon (any simple one) facing n, UVs projected from above or the side
            public void Polygon(IList<Vector3> poly, Vector3 n)
            {
                var flat = poly.Select(p => Mathf.Abs(n.y) > 0.5f ? new Vector2(p.x, p.z) : Mathf.Abs(n.x) >= Mathf.Abs(n.z) ? new Vector2(p.z, p.y) : new Vector2(p.x, p.y)).ToList();
                var plane = Mathf.Abs(n.y) > 0.5f ? flat : poly.Select(p => Flat2(p, n)).ToList();
                var tris = Triangulate(plane);
                if (tris.Count == 0)
                    return;
                Tris(poly, Enumerable.Repeat(n, poly.Count).ToList(), flat, tris, n);
            }

            private static Vector2 Flat2(Vector3 p, Vector3 n)
            {
                var u = Vector3.Cross(n, Vector3.up).normalized;
                if (u.sqrMagnitude < 0.5f)
                    u = Vector3.right;
                var v = Vector3.Cross(u, n);
                return new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v));
            }
        }

        private static Shape NewShape(List<Shape> shapes, Material material, bool outside, Collide collide, Plan.Surface surface)
        {
            var s = new Shape { Material = material, Outside = outside, Collide = collide, Surface = surface };
            shapes.Add(s);
            return s;
        }

        // An upright face along a line of points (x, z) from y0 to y1, facing its pieces' left (side +1) or right
        // (side -1); smooth: normals shared across its bends. UVs run along it from uStart (uSign: which way), v = y.
        private static void Strip(Shape shape, List<Vector2> line, float y0, float y1, float side, bool smooth, float uStart, float uSign)
        {
            int n = line.Count;
            if (n < 2)
                return;
            var normals = new Vector3[n - 1];
            for (int i = 0; i + 1 < n; i++)
            {
                var d = line[i + 1] - line[i];
                normals[i] = d.sqrMagnitude < 1e-10f ? Vector3.zero : At(Left(d.normalized) * side, 0f);
            }
            Vector3 Normal(int seg, int vertex)
            {
                if (!smooth)
                    return normals[seg];
                var sum = Vector3.zero;
                if (vertex > 0)
                    sum += normals[vertex - 1];
                if (vertex < n - 1)
                    sum += normals[vertex];
                return sum.sqrMagnitude < 1e-8f ? normals[seg] : sum.normalized;
            }
            float u = uStart;
            for (int i = 0; i + 1 < n; i++)
            {
                float length = (line[i + 1] - line[i]).magnitude;
                if (length < 1e-5f)
                    continue;
                float u1 = u + uSign * length;
                shape.Quad(At(line[i], y0), At(line[i], y1), At(line[i + 1], y1), At(line[i + 1], y0),
                    Normal(i, i), Normal(i, i), Normal(i, i + 1), Normal(i, i + 1),
                    new Vector2(u, y0), new Vector2(u, y1), new Vector2(u1, y1), new Vector2(u1, y0));
                u = u1;
            }
        }

        // the ring between two lines (one forward, one back) as a flat face at y
        private static void Ring(Shape shape, List<Vector2> a, List<Vector2> b, float y, Vector3 n)
        {
            var poly = a.Concat(Enumerable.Reverse(b)).Select(p => At(p, y)).ToList();
            for (int i = poly.Count - 1; i > 0; i--)
                if ((poly[i] - poly[i - 1]).sqrMagnitude < 1e-10f)
                    poly.RemoveAt(i);
            if (poly.Count >= 3)
                shape.Polygon(poly, n);
        }

        // The bevel's wall from y0 to y1, between offsets inner and outer from the arc (negative towards its centre):
        // the inner face towards the room, the outer towards the other side; a flat top when topMaterial is set.
        private static void ArcWall(List<Shape> shapes, Corner c, float y0, float y1, float inner, float outer, Material innerMaterial, Material outerMaterial,
                                    Material topMaterial, bool innerOutside, bool outerOutside, Collide collide, Plan.Surface surface)
        {
            if (y1 - y0 < 0.005f)
                return;
            var pts = c.Arc;
            int n = pts.Count;
            float side = -c.Turn;
            var dirs = new Vector2[n + 1];
            dirs[0] = c.TangentA;
            for (int k = 0; k + 1 < n; k++)
                dirs[k + 1] = (pts[k + 1] - pts[k]).normalized;
            dirs[n] = c.TangentB;
            // mitred all along; the convex (outer) side also starts and ends square to the walls it carries on from
            List<Vector2> Line(float off)
            {
                var l = new List<Vector2>();
                bool ends = off > 1e-4f;
                if (ends)
                    l.Add(pts[0] + Left(dirs[0]) * (off * side));
                for (int k = 0; k < n; k++)
                    l.Add(Miter(pts[k], dirs[k], off, dirs[k + 1], off, side));
                if (ends)
                    l.Add(pts[n - 1] + Left(dirs[n]) * (off * side));
                return l;
            }
            var innerLine = Line(inner);
            var outerLine = Line(outer);
            // UVs carry on from the wall at A (its u is z there)
            float uStart = c.Arc[0].y, uSign = -c.Dz;
            if (innerMaterial != null)
                Strip(NewShape(shapes, innerMaterial, innerOutside, Collide.None, surface), innerLine, y0, y1, -side, c.Smooth, uStart, uSign);
            if (outerMaterial != null)
                Strip(NewShape(shapes, outerMaterial, outerOutside, Collide.None, surface), outerLine, y0, y1, side, c.Smooth, uStart, uSign);
            if (topMaterial != null)
                Ring(NewShape(shapes, topMaterial, true, Collide.None, surface), outerLine, innerLine, y1, Vector3.up);
            if (collide == Collide.None)
                return;
            var hull = NewShape(shapes, null, false, collide, surface);
            Strip(hull, innerLine, y0, y1, -side, false, 0f, 1f);
            Strip(hull, outerLine, y0, y1, side, false, 0f, 1f);
            Ring(hull, outerLine, innerLine, y1, Vector3.up);
            Ring(hull, outerLine, innerLine, y0, Vector3.down);
        }

        // a slab over a convex piece of a cut cell: its top and bottom faces, a closed collider, and a box-like part
        // that hides the faces of boxes it covers
        private static void PieceSlab(List<Shape> shapes, List<Part> parts, Vector2[] poly, float y0, float y1, Material top, Material bottom, bool bottomOutside,
                                      Plan.Surface surface)
        {
            if (top != null)
                NewShape(shapes, top, true, Collide.None, surface).Polygon(poly.Select(p => At(p, y1)).ToList(), Vector3.up);
            if (bottom != null)
                NewShape(shapes, bottom, bottomOutside, Collide.None, surface).Polygon(poly.Select(p => At(p, y0)).ToList(), Vector3.down);
            var hull = NewShape(shapes, null, false, Collide.Both, surface);
            hull.Polygon(poly.Select(p => At(p, y1)).ToList(), Vector3.up);
            hull.Polygon(poly.Select(p => At(p, y0)).ToList(), Vector3.down);
            var centre = Vector2.zero;
            foreach (var p in poly)
                centre += p;
            centre /= poly.Length;
            for (int i = 0; i < poly.Length; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Length];
                var n = At(-Left((b - a).normalized), 0f);
                if (Vector2.Dot(Flat(n), (a + b) * 0.5f - centre) < 0f)
                    n = -n;
                hull.Quad(At(a, y0), At(a, y1), At(b, y1), At(b, y0), n, n, n, n, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            }
            parts.Add(new Part
            {
                Center = At(centre, (y0 + y1) * 0.5f),
                Size = new Vector3(0f, y1 - y0, 0f),
                Poly = poly,
                Surface = surface,
                Collide = Collide.None,
            });
        }

        // ear clipping of a simple polygon (either winding); collinear points are skipped
        internal static List<int> Triangulate(IList<Vector2> poly)
        {
            var tris = new List<int>();
            int count = poly.Count;
            if (count < 3)
                return tris;
            var idx = Enumerable.Range(0, count).ToList();
            bool ccw = Area(poly) > 0f;
            int guard = 0;
            while (idx.Count > 3 && guard++ < 10000)
            {
                bool clipped = false;
                for (int i = 0; i < idx.Count; i++)
                {
                    int a = idx[(i + idx.Count - 1) % idx.Count], b = idx[i], c = idx[(i + 1) % idx.Count];
                    float turn = Cross(poly[b] - poly[a], poly[c] - poly[b]);
                    if (ccw ? turn <= 1e-9f : turn >= -1e-9f)
                        continue;
                    bool inside = false;
                    for (int j = 0; j < idx.Count && !inside; j++)
                    {
                        int p = idx[j];
                        if (p == a || p == b || p == c)
                            continue;
                        inside = InTriangle(poly[p], poly[a], poly[b], poly[c]);
                    }
                    if (inside)
                        continue;
                    tris.Add(a);
                    tris.Add(b);
                    tris.Add(c);
                    idx.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (clipped)
                    continue;
                // nothing to clip: drop the flattest point (a straight run)
                int flattest = 0;
                float least = float.MaxValue;
                for (int i = 0; i < idx.Count; i++)
                {
                    float turn = Mathf.Abs(Cross(poly[idx[i]] - poly[idx[(i + idx.Count - 1) % idx.Count]], poly[idx[(i + 1) % idx.Count]] - poly[idx[i]]));
                    if (turn < least)
                    {
                        least = turn;
                        flattest = i;
                    }
                }
                idx.RemoveAt(flattest);
            }
            if (idx.Count == 3 && Mathf.Abs(Cross(poly[idx[1]] - poly[idx[0]], poly[idx[2]] - poly[idx[0]])) > 1e-9f)
                tris.AddRange(idx);
            return tris;
        }

        private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(b - a, p - a), d2 = Cross(c - b, p - b), d3 = Cross(a - c, p - c);
            bool neg = d1 < -1e-9f || d2 < -1e-9f || d3 < -1e-9f, pos = d1 > 1e-9f || d2 > 1e-9f || d3 > 1e-9f;
            return !(neg && pos);
        }

        // ------------------------------------------------------------ outlines

        internal struct LoopSeg
        {
            // the wall's half thickness
            public float Half;
            public bool Arc;
            public bool Smooth;
            // straight pieces: the grid line they're on
            public bool AlongZ;
            public int Line;
        }

        // a region's edge, anticlockwise seen from above (the region on its left), closed: S[i] runs P[i] -> P[i + 1]
        internal sealed class Loop
        {
            public readonly List<Vector2> P = new List<Vector2>();
            public readonly List<LoopSeg> S = new List<LoopSeg>();
        }

        // the edges round the cells inside, traced into loops (keeping to a cell where two regions touch at a corner),
        // with the storey's bevels put in at their corners
        internal static List<Loop> Outlines(Plan plan, int s, System.Func<int, int, bool> inside, List<Corner> corners)
        {
            var next = new Dictionary<Vector2Int, List<Vector2Int>>();
            void Edge(Vector2Int a, Vector2Int b)
            {
                if (!next.TryGetValue(a, out var l))
                    next[a] = l = new List<Vector2Int>();
                l.Add(b);
            }
            for (int z = 0; z < plan.Depth; z++)
                for (int x = 0; x < plan.Width; x++)
                {
                    if (!inside(x, z))
                        continue;
                    if (z == 0 || !inside(x, z - 1)) Edge(new Vector2Int(x, z), new Vector2Int(x + 1, z));
                    if (x == plan.Width - 1 || !inside(x + 1, z)) Edge(new Vector2Int(x + 1, z), new Vector2Int(x + 1, z + 1));
                    if (z == plan.Depth - 1 || !inside(x, z + 1)) Edge(new Vector2Int(x + 1, z + 1), new Vector2Int(x, z + 1));
                    if (x == 0 || !inside(x - 1, z)) Edge(new Vector2Int(x, z + 1), new Vector2Int(x, z));
                }
            var loops = new List<Loop>();
            while (next.Count > 0)
            {
                var start = next.Keys.First();
                var grid = new List<Vector2Int> { start };
                var at = start;
                var dir = Vector2Int.zero;
                int guard = 0;
                while (guard++ < 100000)
                {
                    if (!next.TryGetValue(at, out var outs) || outs.Count == 0)
                        break;
                    // left, straight, right: keeps to the cell it's going round
                    Vector2Int pick = outs[0];
                    if (dir != Vector2Int.zero)
                    {
                        var leftTurn = new Vector2Int(-dir.y, dir.x);
                        pick = outs.OrderBy(o => o - at == leftTurn ? 0 : o - at == dir ? 1 : 2).First();
                    }
                    outs.Remove(pick);
                    if (outs.Count == 0)
                        next.Remove(at);
                    dir = pick - at;
                    at = pick;
                    if (at == start)
                        break;
                    grid.Add(at);
                }
                if (grid.Count >= 3)
                    loops.Add(Bevelled(plan, s, grid, corners));
            }
            return loops;
        }

        // a traced loop of grid points with the bevels' arcs in place of their corners
        private static Loop Bevelled(Plan plan, int s, List<Vector2Int> grid, List<Corner> corners)
        {
            float cell = plan.CellSize;
            int n = grid.Count;
            var corner = new Corner[n];
            var reversed = new bool[n];
            var removed = new bool[n];
            bool OnA(Corner c, Vector2Int p) => p.x == c.V.x && (p.y - c.V.y) * c.Dz > 0;
            bool OnB(Corner c, Vector2Int p) => p.y == c.V.y && (p.x - c.V.x) * c.Dx > 0;
            for (int k = 0; k < n; k++)
                foreach (var c in corners)
                {
                    if (grid[k] != c.V)
                        continue;
                    var before = grid[(k + n - 1) % n];
                    var after = grid[(k + 1) % n];
                    bool ab = OnA(c, before) && OnB(c, after), ba = OnB(c, before) && OnA(c, after);
                    if (!ab && !ba)
                        continue;
                    corner[k] = c;
                    reversed[k] = ba;
                    // the grid points the arc cuts off either side
                    for (int m = (k + n - 1) % n, guard = 0; guard < n && (ab ? OnA(c, grid[m]) : OnB(c, grid[m])) && (grid[m] - c.V).magnitude * cell < c.Radius - 1e-4f; m = (m + n - 1) % n, guard++)
                        removed[m] = true;
                    for (int m = (k + 1) % n, guard = 0; guard < n && (ab ? OnB(c, grid[m]) : OnA(c, grid[m])) && (grid[m] - c.V).magnitude * cell < c.Radius - 1e-4f; m = (m + 1) % n, guard++)
                        removed[m] = true;
                    break;
                }
            var loop = new Loop();
            LoopSeg Straight(Vector2 a, Vector2 b)
            {
                bool alongZ = Mathf.Abs(b.x - a.x) < 1e-4f;
                int line = Mathf.RoundToInt((alongZ ? a.x : a.y) / cell);
                int index = Mathf.FloorToInt((alongZ ? (a.y + b.y) : (a.x + b.x)) * 0.5f / cell);
                var e = alongZ ? Edge(plan, s, line, index, true) : Edge(plan, s, index, line, false);
                return new LoopSeg { Half = Thickness(plan, e) * 0.5f, AlongZ = alongZ, Line = line };
            }
            void Emit(Vector2 q, LoopSeg? seg)
            {
                if (loop.P.Count > 0 && (q - loop.P[loop.P.Count - 1]).sqrMagnitude < 1e-8f)
                    return;
                if (loop.P.Count > 0)
                    loop.S.Add(seg ?? Straight(loop.P[loop.P.Count - 1], q));
                loop.P.Add(q);
            }
            // start where nothing's cut, so the closing piece is a plain one when there is one
            int start = Enumerable.Range(0, n).FirstOrDefault(k => !removed[k] && corner[k] == null);
            if (removed[start] || corner[start] != null)
                start = Enumerable.Range(0, n).FirstOrDefault(k => corner[k] != null);
            for (int step = 0; step < n; step++)
            {
                int k = (start + step) % n;
                if (removed[k] && corner[k] == null)
                    continue;
                var c = corner[k];
                if (c == null)
                {
                    Emit((Vector2)grid[k] * cell, null);
                    continue;
                }
                var arc = reversed[k] ? Enumerable.Reverse(c.Arc).ToList() : c.Arc;
                var seg = new LoopSeg { Half = (c.Exterior ? plan.WallThickness : plan.InnerWallThickness) * 0.5f, Arc = true, Smooth = c.Smooth };
                for (int i = 0; i < arc.Count; i++)
                    Emit(arc[i], i == 0 ? (LoopSeg?)null : seg);
            }
            if (loop.P.Count >= 2 && (loop.P[loop.P.Count - 1] - loop.P[0]).sqrMagnitude < 1e-8f)
                loop.P.RemoveAt(loop.P.Count - 1);
            else if (loop.P.Count >= 2)
                loop.S.Add(Straight(loop.P[loop.P.Count - 1], loop.P[0]));
            return loop;
        }

        // ------------------------------------------------------------ for the window

        // the room's outward corners on a storey (vertex, quadrant), with how far the walls beside each run against
        // the same room (or the outside), in cells: the longest radius it could take
        internal static List<(int X, int Z, int Quadrant, int Along)> RoomCorners(Plan plan, int s, int room)
        {
            var list = new List<(int, int, int, int)>();
            for (int z = 0; z <= plan.Depth; z++)
                for (int x = 0; x <= plan.Width; x++)
                    for (int q = 0; q < 4; q++)
                    {
                        int dx = q == 0 || q == 3 ? 1 : -1, dz = q < 2 ? 1 : -1;
                        int cx = dx > 0 ? x : x - 1, cz = dz > 0 ? z : z - 1;
                        if (plan.Get(s, cx, cz) != room)
                            continue;
                        int ox = dx > 0 ? x - 1 : x, oz = dz > 0 ? z - 1 : z;
                        int outer = plan.Get(s, ox, cz);
                        if (outer == room || plan.Get(s, cx, oz) != outer)
                            continue;
                        int RunLength(bool alongZ)
                        {
                            int n = 0;
                            for (int k = 0; k < 256; k++)
                            {
                                var inside = alongZ ? new Vector2Int(cx, dz > 0 ? z + k : z - 1 - k) : new Vector2Int(dx > 0 ? x + k : x - 1 - k, cz);
                                var across = alongZ ? new Vector2Int(ox, inside.y) : new Vector2Int(inside.x, oz);
                                if (plan.Get(s, inside.x, inside.y) != room || plan.Get(s, across.x, across.y) != outer)
                                    break;
                                n++;
                            }
                            return n;
                        }
                        list.Add((x, z, q, Mathf.Min(RunLength(true), RunLength(false))));
                    }
            return list;
        }

        // Rounds every outward corner of the room on the storey: radius in cells, or (0) half the shorter wall beside
        // each corner, which makes a square room round. Corners that don't fit are tried at half the radius, then left.
        internal static int RoundRoom(Plan plan, int s, int room, Plan.BevelKind kind, float radius, int segments)
        {
            var corners = RoomCorners(plan, s, room);
            plan.Bevels.RemoveAll(b => b.Storey == s && corners.Any(c => c.X == b.X && c.Z == b.Z && c.Quadrant == ((b.Quadrant % 4) + 4) % 4));
            var added = new List<Plan.Bevel>();
            foreach (var (x, z, q, along) in corners)
            {
                float r = radius > 0f ? radius : along * 0.5f;
                if (r < 0.25f)
                    continue;
                var b = new Plan.Bevel { Storey = s, X = x, Z = z, Quadrant = q, Kind = kind, Radius = r, Segments = segments };
                plan.Bevels.Add(b);
                added.Add(b);
            }
            for (int pass = 0; pass < 2; pass++)
            {
                var ok = new HashSet<Plan.Bevel>(Corners(plan, s).Select(c => c.Source));
                foreach (var b in added.Where(b => !ok.Contains(b)).ToList())
                {
                    if (pass == 0 && b.Radius >= 0.5f)
                        b.Radius *= 0.5f;
                    else
                    {
                        plan.Bevels.Remove(b);
                        added.Remove(b);
                    }
                }
            }
            return added.Count;
        }

        // the SDK's own building materials, as they're saved (null before the first build makes them)
        internal static Material SdkMaterial(string role) => UnityEditor.AssetDatabase.LoadAssetAtPath<Material>($"Assets/TarkovSDK/Materials/SDK_Building_{role}.mat");

        // every material the plan names (and the SDK's for the parts it leaves empty)
        internal static List<Material> UsedMaterials(Plan plan)
        {
            // (Unity's null: a deleted material isn't C# null)
            Material Or(Material m, string sdk) => m != null ? m : SdkMaterial(sdk);
            var list = new List<Material>
            {
                Or(plan.ExteriorMaterial, "Brick"), Or(plan.InteriorMaterial, "Plaster"), Or(plan.FloorMaterial, "Floor"),
                Or(plan.CeilingMaterial, "Ceiling"), Or(plan.RoofMaterial, "Roof"), Or(plan.TrimMaterial, "Trim"),
            };
            list.AddRange(plan.Storeys.Select(s => s.Exterior));
            list.AddRange(plan.Rooms.SelectMany(r => new[] { r.Floor, r.Walls, r.Ceiling }));
            list.AddRange((plan.Paints ?? new List<Plan.WallPaint>()).Select(p => p?.Material));
            list.AddRange((plan.Trims ?? new List<Plan.Trim>()).Select(t => t?.Material));
            list.AddRange((plan.MaterialOptions ?? new List<Plan.MaterialOption>()).Select(o => o?.Material));
            return list.Where(m => m != null).Distinct().ToList();
        }

        // swaps a material everywhere in the plan (an SDK default it leaves empty is named outright); how many places
        internal static int ReplaceMaterial(Plan plan, Material from, Material to)
        {
            int n = 0;
            Material Swap(Material m)
            {
                if (m != from || m == null)
                    return m;
                n++;
                return to;
            }
            Material Role(Material m, string sdk)
            {
                if (m == null && from == SdkMaterial(sdk))
                {
                    n++;
                    return to;
                }
                return Swap(m);
            }
            plan.ExteriorMaterial = Role(plan.ExteriorMaterial, "Brick");
            plan.InteriorMaterial = Role(plan.InteriorMaterial, "Plaster");
            plan.FloorMaterial = Role(plan.FloorMaterial, "Floor");
            plan.CeilingMaterial = Role(plan.CeilingMaterial, "Ceiling");
            plan.RoofMaterial = Role(plan.RoofMaterial, "Roof");
            plan.TrimMaterial = Role(plan.TrimMaterial, "Trim");
            foreach (var s in plan.Storeys)
                s.Exterior = Swap(s.Exterior);
            foreach (var r in plan.Rooms)
            {
                r.Floor = Swap(r.Floor);
                r.Walls = Swap(r.Walls);
                r.Ceiling = Swap(r.Ceiling);
            }
            foreach (var p in plan.Paints ?? new List<Plan.WallPaint>())
                if (p != null)
                    p.Material = Swap(p.Material);
            foreach (var t in plan.Trims ?? new List<Plan.Trim>())
                if (t != null)
                    t.Material = Swap(t.Material);
            // the copy takes the original's options over unless it has its own
            foreach (var o in plan.MaterialOptions ?? new List<Plan.MaterialOption>())
                if (o != null && o.Material == from && from != null && !plan.MaterialOptions.Any(x => x != null && x.Material == to))
                    o.Material = to;
            return n;
        }
    }
}
