using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Plan = TarkovSDK.TarkovBuilding;

namespace TarkovSDK.Editor
{
    // The Building Builder's trims: a profile (x out from the wall, y up) swept along outlines (each room's for
    // inside trims, the storey's whole outline for outside ones), offset from the wall's middle line by its half
    // thickness, mitred at corners, round the bevels' arcs; cut where an opening crosses it (each cut end closed by
    // the profile's shape) and where a wall's face steps (a thicker wall meeting a thinner one in line).
    public static partial class TarkovBuildingBuilder
    {
        public static readonly (string Name, Func<Plan, Plan.Trim> Make)[] TrimPresets =
        {
            ("Baseboard", p => new Plan.Trim
            {
                Name = "Baseboard", Side = Plan.TrimSide.Inside, Anchor = Plan.TrimAnchor.Floor,
                Profile = new[] { V(0f, 0f), V(0.018f, 0f), V(0.018f, 0.075f), V(0.012f, 0.09f), V(0.006f, 0.1f), V(0f, 0.1f) },
            }),
            ("Crown Moulding", p => new Plan.Trim
            {
                Name = "Crown Moulding", Side = Plan.TrimSide.Inside, Anchor = Plan.TrimAnchor.Ceiling,
                Profile = new[] { V(0f, 0f), V(0.008f, 0f), V(0.008f, 0.01f), V(0.02f, 0.025f), V(0.04f, 0.045f), V(0.065f, 0.07f), V(0.09f, 0.09f), V(0.105f, 0.1f), V(0.105f, 0.12f) },
            }),
            ("Chair Rail", p => new Plan.Trim
            {
                Name = "Chair Rail", Side = Plan.TrimSide.Inside, Anchor = Plan.TrimAnchor.Floor, Height = 0.85f,
                Profile = new[] { V(0f, 0f), V(0.01f, 0f), V(0.02f, 0.015f), V(0.02f, 0.045f), V(0.01f, 0.06f), V(0f, 0.06f) },
            }),
            ("Plinth Band", p => new Plan.Trim
            {
                // from the ground (the building's pivot) to a little over the ground floor
                Name = "Plinth Band", Side = Plan.TrimSide.Outside, Anchor = Plan.TrimAnchor.Floor, Height = -Mathf.Max(0f, p.Plinth), Storeys = Plan.TrimStoreys.Ground,
                Profile = new[] { V(0f, 0f), V(0.05f, 0f), V(0.05f, Mathf.Max(0f, p.Plinth) + 0.18f), V(0.03f, Mathf.Max(0f, p.Plinth) + 0.2f), V(0f, Mathf.Max(0f, p.Plinth) + 0.2f) },
            }),
            ("String Course", p => new Plan.Trim
            {
                // round the outside at each upper floor's level
                Name = "String Course", Side = Plan.TrimSide.Outside, Anchor = Plan.TrimAnchor.Floor, Height = -0.15f, Storeys = Plan.TrimStoreys.Upper,
                Profile = new[] { V(0f, 0f), V(0.06f, 0f), V(0.06f, 0.16f), V(0.04f, 0.2f), V(0f, 0.2f) },
            }),
            ("Cornice", p => new Plan.Trim
            {
                // its top at the roof's
                Name = "Cornice", Side = Plan.TrimSide.Outside, Anchor = Plan.TrimAnchor.Top, Height = -0.35f, Storeys = Plan.TrimStoreys.Top,
                Profile = new[] { V(0f, 0f), V(0.04f, 0f), V(0.04f, 0.08f), V(0.1f, 0.12f), V(0.1f, 0.2f), V(0.18f, 0.26f), V(0.22f, 0.3f), V(0.22f, 0.35f), V(0f, 0.35f) },
            }),
        };

        private static Vector2 V(float x, float y) => new Vector2(x, y);

        internal static bool TrimOn(Plan plan, Plan.Trim t, int s)
        {
            int top = plan.Storeys.Count - 1;
            return t.Storeys switch
            {
                Plan.TrimStoreys.Ground => s == 0,
                Plan.TrimStoreys.Upper => s > 0,
                Plan.TrimStoreys.Top => s == top,
                Plan.TrimStoreys.NotTop => s < top,
                _ => true,
            };
        }

        // the profile anticlockwise with its ends closed along the wall (so its right-hand normals point out of it)
        internal static List<Vector2> TrimProfile(Plan.Trim t)
        {
            var p = (t.Profile ?? Array.Empty<Vector2>()).Select(q => new Vector2(Mathf.Max(0f, q.x), q.y)).ToList();
            for (int i = p.Count - 1; i > 0; i--)
                if ((p[i] - p[i - 1]).sqrMagnitude < 1e-10f)
                    p.RemoveAt(i);
            if (p.Count < 2)
                return p;
            var closed = new List<Vector2>(p);
            if (closed[closed.Count - 1].x > 1e-5f)
                closed.Add(new Vector2(0f, closed[closed.Count - 1].y));
            if (closed[0].x > 1e-5f)
                closed.Add(new Vector2(0f, closed[0].y));
            if (Area(closed) < 0f)
                p.Reverse();
            return p;
        }

        // a stretch of outline the trim runs along, open (square ends) or closed
        private sealed class TrimRun
        {
            public readonly List<Vector2> P = new List<Vector2>();
            public readonly List<LoopSeg> S = new List<LoopSeg>();
            public bool Closed;
            public float UStart;
        }

        private static void TrimShapes(Plan plan, int s, List<Corner> corners, Materials mats, List<Shape> shapes)
        {
            var trims = (plan.Trims ?? new List<Plan.Trim>()).Where(t => t != null && t.Enabled && t.Profile != null && t.Profile.Length >= 2 && TrimOn(plan, t, s)).ToList();
            if (trims.Count == 0)
                return;
            List<Loop> outside = null;
            var rooms = new Dictionary<int, List<Loop>>();
            float floor = plan.Base(s), ceiling = plan.Base(s + 1) - plan.SlabThickness, top = plan.Base(s + 1);
            foreach (var t in trims)
            {
                var profile = TrimProfile(t);
                if (profile.Count < 2)
                    continue;
                float minY = profile.Min(p => p.y), maxY = profile.Max(p => p.y);
                float yb = t.Anchor == Plan.TrimAnchor.Ceiling ? ceiling - maxY + t.Height : (t.Anchor == Plan.TrimAnchor.Top ? top : floor) + t.Height;
                var loops = new List<Loop>();
                if (t.Side == Plan.TrimSide.Outside)
                    loops = outside ??= Outlines(plan, s, (x, z) => plan.Get(s, x, z) != 0, corners);
                else
                    for (int r = 1; r <= plan.Rooms.Count; r++)
                    {
                        if (t.Room > 0 && t.Room != r)
                            continue;
                        int room = r;
                        if (!rooms.TryGetValue(room, out var l))
                            rooms[room] = l = Outlines(plan, s, (x, z) => plan.Get(s, x, z) == room, corners);
                        loops.AddRange(l);
                    }
                var shape = NewShape(shapes, t.Material != null ? t.Material : mats.Trim, t.Side == Plan.TrimSide.Outside, Collide.None, plan.InteriorSurface);
                shape.Trim = true;
                float side = t.Side == Plan.TrimSide.Inside ? 1f : -1f;
                foreach (var loop in loops)
                    foreach (var run in CutTrim(plan, loop, yb + minY, yb + maxY))
                        Sweep(shape, run, profile, yb, side);
            }
        }

        // the outline in the stretches the trim runs along: cut round openings it would cross (y0..y1, of any storey
        // on that line) and at steps in the wall's face
        private static List<TrimRun> CutTrim(Plan plan, Loop loop, float y0, float y1)
        {
            float cell = plan.CellSize;
            int n = loop.P.Count;
            var runs = new List<TrimRun>();
            if (n < 2)
                return runs;
            var length = new float[n];
            var dir = new Vector2[n];
            var kept = new List<List<(float A, float B)>>();
            for (int i = 0; i < n; i++)
            {
                var a = loop.P[i];
                var b = loop.P[(i + 1) % n];
                length[i] = (b - a).magnitude;
                dir[i] = length[i] > 1e-6f ? (b - a) / length[i] : Vector2.right;
                var keep = new List<(float, float)> { (0f, length[i]) };
                var seg = loop.S[i];
                if (!seg.Arc)
                    foreach (var o in plan.Openings)
                    {
                        if (o.AlongZ != seg.AlongZ || (o.AlongZ ? o.X : o.Z) != seg.Line || o.Storey < 0 || o.Storey >= plan.Storeys.Count)
                            continue;
                        var (width, height, _) = Size(plan, o);
                        float sill = o.Kind == Plan.OpeningKind.Window ? o.Sill : 0f;
                        float bottom = plan.Base(o.Storey) + sill;
                        float head = Mathf.Min(bottom + height, plan.Base(o.Storey + 1) - plan.SlabThickness);
                        if (head <= y0 + 1e-3f || bottom >= y1 - 1e-3f)
                            continue;
                        float centre = ((o.AlongZ ? o.Z : o.X) + Mathf.Max(1, o.Span) * 0.5f) * cell + o.Offset;
                        // along the piece from its start, in metres
                        float ua = seg.AlongZ ? a.y : a.x, sign = seg.AlongZ ? dir[i].y : dir[i].x;
                        float t0 = (centre - width * 0.5f - ua) * sign, t1 = (centre + width * 0.5f - ua) * sign;
                        Subtract(keep, Mathf.Min(t0, t1), Mathf.Max(t0, t1));
                    }
                kept.Add(keep);
            }
            // a step in the face (pieces in line, one wall thicker): the thicker face carries on past the corner by its
            // half thickness (as its wall does, over the wall across), the thinner starts there
            var breaks = new bool[n];
            for (int i = 0; i < n; i++)
            {
                int prev = (i + n - 1) % n;
                if (Vector2.Dot(dir[prev], dir[i]) < 0.999f || Mathf.Abs(loop.S[prev].Half - loop.S[i].Half) < 1e-3f)
                    continue;
                breaks[i] = true;
                float h = Mathf.Max(loop.S[prev].Half, loop.S[i].Half);
                bool thickerFirst = loop.S[prev].Half > loop.S[i].Half;
                var before = kept[prev];
                var after = kept[i];
                if (before.Count > 0 && before[before.Count - 1].B >= length[prev] - 1e-4f)
                    before[before.Count - 1] = (before[before.Count - 1].A, length[prev] + (thickerFirst ? h : -h));
                if (after.Count > 0 && after[0].A <= 1e-4f)
                    after[0] = (thickerFirst ? h : -h, after[0].B);
                before.RemoveAll(k => k.B - k.A < 1e-3f);
                after.RemoveAll(k => k.B - k.A < 1e-3f);
            }
            // the kept pieces in order round the loop; one carries on from the one before it when that one reached
            // the corner, it starts there and the corner isn't a break
            var pieces = new List<(int Seg, float A, float B)>();
            for (int i = 0; i < n; i++)
                foreach (var (a, b) in kept[i])
                    pieces.Add((i, a, b));
            if (pieces.Count == 0)
                return runs;
            bool Continues(int p)
            {
                var cur = pieces[p];
                var before = pieces[(p + pieces.Count - 1) % pieces.Count];
                return before.Seg == (cur.Seg + n - 1) % n && before.Seg != cur.Seg && before.B >= length[before.Seg] - 1e-4f && cur.A <= 1e-4f && !breaks[cur.Seg];
            }
            float[] along = new float[n + 1];
            for (int i = 0; i < n; i++)
                along[i + 1] = along[i] + length[i];
            int first = Enumerable.Range(0, pieces.Count).FirstOrDefault(p => !Continues(p));
            if (Continues(first))
            {
                // all the way round
                var closed = new TrimRun { Closed = true };
                closed.P.AddRange(loop.P);
                closed.S.AddRange(loop.S);
                runs.Add(closed);
                return runs;
            }
            TrimRun run = null;
            for (int step = 0; step < pieces.Count; step++)
            {
                int p = (first + step) % pieces.Count;
                var (seg, a, b) = pieces[p];
                var start = loop.P[seg] + dir[seg] * a;
                var end = loop.P[seg] + dir[seg] * b;
                if (run == null || !Continues(p))
                {
                    run = new TrimRun { UStart = along[seg] + a };
                    run.P.Add(start);
                    runs.Add(run);
                }
                run.P.Add(end);
                run.S.Add(loop.S[seg]);
            }
            return runs;
        }

        private static void Subtract(List<(float A, float B)> keep, float a, float b)
        {
            for (int i = keep.Count - 1; i >= 0; i--)
            {
                var (ka, kb) = keep[i];
                if (b <= ka || a >= kb)
                    continue;
                keep.RemoveAt(i);
                if (b < kb - 1e-3f)
                    keep.Insert(i, (b, kb));
                if (a > ka + 1e-3f)
                    keep.Insert(i, (ka, a));
            }
        }

        // the run's line offset from the wall's middle by its half thickness + off, towards side (+1 left)
        private static List<Vector2> TrimLine(TrimRun run, float off, float side)
        {
            int n = run.P.Count;
            int segs = run.S.Count;
            var line = new List<Vector2>(n);
            Vector2 Dir(int i) => (run.P[(i + 1) % n] - run.P[i]).normalized;
            for (int k = 0; k < n; k++)
            {
                if (!run.Closed && k == 0)
                    line.Add(run.P[0] + Left(Dir(0)) * ((run.S[0].Half + off) * side));
                else if (!run.Closed && k == n - 1)
                    line.Add(run.P[k] + Left(Dir(k - 1)) * ((run.S[segs - 1].Half + off) * side));
                else
                {
                    int prev = (k + segs - 1) % segs;
                    line.Add(Miter(run.P[k], Dir(prev), run.S[prev].Half + off, Dir(k), run.S[k].Half + off, side));
                }
            }
            if (run.Closed)
                line.Add(line[0]);
            return line;
        }

        private static void Sweep(Shape shape, TrimRun run, List<Vector2> profile, float yb, float side)
        {
            int m = profile.Count;
            var lines = profile.Select(q => TrimLine(run, q.x, side)).ToList();
            var wall = TrimLine(run, 0f, side);
            int count = lines[0].Count;
            if (count < 2)
                return;
            // horizontal normals per piece, and at each point (shared round arcs and gentle bends)
            var segNormal = new Vector3[count - 1];
            for (int i = 0; i + 1 < count; i++)
            {
                var d = wall[i + 1] - wall[i];
                segNormal[i] = d.sqrMagnitude < 1e-10f ? Vector3.zero : At(Left(d.normalized) * side, 0f);
            }
            Vector3 Horizontal(int seg, int vertex)
            {
                int other = vertex == seg ? seg - 1 : seg + 1;
                if (run.Closed)
                    other = (other + count - 1) % (count - 1);
                if (other < 0 || other >= count - 1 || segNormal[other] == Vector3.zero)
                    return segNormal[seg];
                return Vector3.Angle(segNormal[seg], segNormal[other]) < 30f ? (segNormal[seg] + segNormal[other]).normalized : segNormal[seg];
            }
            // the profile's outward normals per piece, and its length so far (v)
            var profNormal = new Vector2[m - 1];
            var v = new float[m];
            for (int j = 0; j + 1 < m; j++)
            {
                var d = profile[j + 1] - profile[j];
                profNormal[j] = new Vector2(d.y, -d.x).normalized;
                v[j + 1] = v[j] + d.magnitude;
            }
            Vector2 Profile(int seg, int vertex)
            {
                int other = vertex == seg ? seg - 1 : seg + 1;
                if (other < 0 || other >= m - 1)
                    return profNormal[seg];
                return Vector2.Angle(profNormal[seg], profNormal[other]) < 40f ? (profNormal[seg] + profNormal[other]).normalized : profNormal[seg];
            }
            var u = new float[count];
            u[0] = run.UStart;
            for (int i = 1; i < count; i++)
                u[i] = u[i - 1] + (wall[i] - wall[i - 1]).magnitude;
            Vector3 N(int seg, int vertex, int pseg, int pvertex)
            {
                var h = Horizontal(seg, vertex);
                var p = Profile(pseg, pvertex);
                return (h * p.x + Vector3.up * p.y).normalized;
            }
            for (int j = 0; j + 1 < m; j++)
                for (int i = 0; i + 1 < count; i++)
                {
                    if ((wall[i + 1] - wall[i]).sqrMagnitude < 1e-10f)
                        continue;
                    shape.Quad(At(lines[j][i], yb + profile[j].y), At(lines[j + 1][i], yb + profile[j + 1].y), At(lines[j + 1][i + 1], yb + profile[j + 1].y), At(lines[j][i + 1], yb + profile[j].y),
                        N(i, i, j, j), N(i, i, j, j + 1), N(i, i + 1, j, j + 1), N(i, i + 1, j, j),
                        new Vector2(u[i], v[j]), new Vector2(u[i], v[j + 1]), new Vector2(u[i + 1], v[j + 1]), new Vector2(u[i + 1], v[j]));
                }
            if (run.Closed)
                return;
            // the open ends closed with the profile's shape (back to the wall)
            for (int end = 0; end < 2; end++)
            {
                int k = end == 0 ? 0 : count - 1;
                var d = end == 0 ? -(wall[1] - wall[0]).normalized : (wall[count - 1] - wall[count - 2]).normalized;
                var poly = new List<Vector3>();
                for (int j = 0; j < m; j++)
                    poly.Add(At(lines[j][k], yb + profile[j].y));
                if (profile[m - 1].x > 1e-5f)
                    poly.Add(At(wall[k], yb + profile[m - 1].y));
                if (profile[0].x > 1e-5f)
                    poly.Add(At(wall[k], yb + profile[0].y));
                shape.Polygon(poly, At(d, 0f));
            }
        }
    }
}
