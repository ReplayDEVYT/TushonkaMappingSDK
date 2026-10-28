using System.Collections.Generic;
using System.Linq;
using BezierSplineTools;
using UnityEngine;
using UnityEngine.AI;

namespace TarkovSDK.Editor
{
    // The game's BezierSpline (BezierSplineTools): cubic segments, points [anchor, control, control, anchor, ...]
    // local to the spline's transform, one mode per anchor. The car extract's drive-away path and the BTR's
    // roads are made from waypoints here, smoothed as a Catmull-Rom curve through them.
    public static class TarkovSplines
    {
        public static void FromWaypoints(BezierSpline spline, IList<Vector3> world)
        {
            var p = world.Select(w => spline.transform.InverseTransformPoint(w)).ToList();
            if (p.Count < 2)
                p.Add(p.Count == 1 ? p[0] + Vector3.forward : Vector3.forward);
            var points = new List<Vector3> { p[0] };
            for (int i = 0; i < p.Count - 1; i++)
            {
                var before = i > 0 ? p[i - 1] : p[i];
                var after = i + 2 < p.Count ? p[i + 2] : p[i + 1];
                points.Add(p[i] + (p[i + 1] - before) / 6f);
                points.Add(p[i + 1] - (after - p[i]) / 6f);
                points.Add(p[i + 1]);
            }
            spline.points = points.ToArray();
            spline.modes = Enumerable.Repeat(BezierControlPointMode.Free, p.Count).ToArray();
            spline.loop = false;
        }

        // world positions along the spline, for gizmos
        public static List<Vector3> Sample(BezierSpline spline, int perCurve = 12)
        {
            var result = new List<Vector3>();
            var pts = spline.points;
            if (pts == null || pts.Length < 4)
                return result;
            for (int c = 0; c + 3 < pts.Length; c += 3)
                for (int i = c == 0 ? 0 : 1; i <= perCurve; i++)
                {
                    float t = i / (float)perCurve, u = 1f - t;
                    var local = u * u * u * pts[c] + 3f * u * u * t * pts[c + 1] + 3f * u * t * t * pts[c + 2] + t * t * t * pts[c + 3];
                    result.Add(spline.transform.TransformPoint(local));
                }
            return result;
        }

        // a road from a to b along the navmesh, thinned to points about every 'spacing' metres and kept
        // 'clearance' metres off the navmesh's edges where it can be; null if the navmesh doesn't join them
        public static List<Vector3> NavMeshRoad(Vector3 a, Vector3 b, float spacing = 12f, float clearance = 0f)
        {
            var middle = NavMeshPoints(a, b, spacing);
            if (middle == null)
                return null;
            var road = new List<Vector3> { a };
            road.AddRange(middle.Skip(1));
            if (Vector3.Distance(road[road.Count - 1], b) < spacing * 0.4f && road.Count > 1)
                road.RemoveAt(road.Count - 1);
            road.Add(b);
            KeepClear(road, clearance, 1, road.Count - 2);
            return road;
        }

        // A road that leaves a along 'leave' and comes into b along 'arrive' (flat directions): straight lead
        // points 'lead' metres out of a and before b, the navmesh path between them. A vehicle that stops at a
        // stop facing its road in and out never needs to turn round there.
        public static List<Vector3> DirectedRoad(Vector3 a, Vector3 leave, Vector3 b, Vector3 arrive, float lead = 8f, float spacing = 12f, float clearance = 0f)
        {
            // stops close together: shorter leads, or the two would cross
            lead = Mathf.Min(lead, Vector3.Distance(a, b) / 3f);
            if (lead < 1f || leave == Vector3.zero || arrive == Vector3.zero)
                return NavMeshRoad(a, b, spacing, clearance);
            var leadOut = OnNavMesh(a + leave.normalized * lead);
            var leadIn = OnNavMesh(b - arrive.normalized * lead);
            if (leadOut == null || leadIn == null)
                return NavMeshRoad(a, b, spacing, clearance);
            var middle = NavMeshPoints(leadOut.Value, leadIn.Value, spacing);
            if (middle == null)
                return NavMeshRoad(a, b, spacing, clearance);
            var road = new List<Vector3> { a };
            road.AddRange(middle);
            // the path's own first and last points are the lead points; a resampled point right next to the
            // lead-in point would kink the curve
            if (road.Count > 3 && Vector3.Distance(road[road.Count - 2], road[road.Count - 1]) < spacing * 0.4f)
                road.RemoveAt(road.Count - 2);
            road.Add(b);
            // the lead points stay where they are: they hold the stops' headings
            KeepClear(road, clearance, 2, road.Count - 3);
            return road;
        }

        public static Vector3? OnNavMesh(Vector3 p, float radius = 3f) =>
            NavMesh.SamplePosition(p, out var hit, radius, NavMesh.AllAreas) ? hit.position : (Vector3?)null;

        // the navmesh path from a to b resampled about every 'spacing' metres, a and b included (their navmesh
        // positions); null if the navmesh doesn't join them
        private static List<Vector3> NavMeshPoints(Vector3 a, Vector3 b, float spacing)
        {
            if (!NavMesh.SamplePosition(a, out var ha, 6f, NavMesh.AllAreas) || !NavMesh.SamplePosition(b, out var hb, 6f, NavMesh.AllAreas))
                return null;
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(ha.position, hb.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                return null;
            // resample the corners evenly so the curve doesn't overshoot long straights
            var corners = path.corners;
            var points = new List<Vector3> { corners[0] };
            float carried = 0f;
            for (int i = 0; i < corners.Length - 1; i++)
            {
                var from = corners[i];
                var to = corners[i + 1];
                float length = Vector3.Distance(from, to);
                float d = spacing - carried;
                while (d < length)
                {
                    points.Add(Vector3.Lerp(from, to, d / length));
                    d += spacing;
                }
                carried = length - (d - spacing);
            }
            if (points.Count > 1 && Vector3.Distance(points[points.Count - 1], corners[corners.Length - 1]) < spacing * 0.4f)
                points.RemoveAt(points.Count - 1);
            points.Add(corners[corners.Length - 1]);
            return points;
        }

        // pushes points [from..to] away from the navmesh's nearest edge until they're 'clearance' metres off it
        // (or as far as the walkable area lets them): navmesh paths hug corners at the agent's radius, a vehicle
        // as wide as a lane then scrapes the walls
        public static void KeepClear(List<Vector3> points, float clearance, int from, int to)
        {
            if (clearance <= 0f)
                return;
            for (int pass = 0; pass < 3; pass++)
                for (int i = Mathf.Max(from, 0); i <= Mathf.Min(to, points.Count - 1); i++)
                {
                    if (!NavMesh.FindClosestEdge(points[i], out var edge, NavMesh.AllAreas) || edge.distance >= clearance)
                        continue;
                    var normal = Vector3.ProjectOnPlane(edge.normal, Vector3.up);
                    if (normal.sqrMagnitude < 0.0001f)
                        continue;
                    var moved = OnNavMesh(points[i] + normal.normalized * (clearance - edge.distance), 1.5f);
                    if (moved != null)
                        points[i] = moved.Value;
                }
        }

        // the anchors (every third point) of a spline, in world space
        public static List<Vector3> Anchors(BezierSpline spline)
        {
            var result = new List<Vector3>();
            if (spline.points == null)
                return result;
            for (int i = 0; i < spline.points.Length; i += 3)
                result.Add(spline.transform.TransformPoint(spline.points[i]));
            return result;
        }

        // the game's BezierSpline.GetPoint: t runs over the curves by index, not by length
        public static Vector3 GetPoint(BezierSpline spline, float t)
        {
            var p = spline.points;
            int curves = (p.Length - 1) / 3;
            int i;
            if (t >= 1f)
            {
                t = 1f;
                i = p.Length - 4;
            }
            else
            {
                t = Mathf.Clamp01(t) * curves;
                i = (int)t;
                t -= i;
                i *= 3;
            }
            return spline.transform.TransformPoint(Bezier(p[i], p[i + 1], p[i + 2], p[i + 3], t));
        }

        public static Vector3 Bezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float u = 1f - t;
            return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
        }
    }
}
