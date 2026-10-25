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

        // a road from a to b along the navmesh, thinned to points about every 'spacing' metres; null if the
        // navmesh doesn't join them
        public static List<Vector3> NavMeshRoad(Vector3 a, Vector3 b, float spacing = 12f)
        {
            if (!NavMesh.SamplePosition(a, out var ha, 6f, NavMesh.AllAreas) || !NavMesh.SamplePosition(b, out var hb, 6f, NavMesh.AllAreas))
                return null;
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(ha.position, hb.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                return null;
            // resample the corners evenly so the curve doesn't overshoot long straights
            var corners = path.corners;
            var road = new List<Vector3> { a };
            float carried = 0f;
            for (int i = 0; i < corners.Length - 1; i++)
            {
                var from = corners[i];
                var to = corners[i + 1];
                float length = Vector3.Distance(from, to);
                float d = spacing - carried;
                while (d < length)
                {
                    road.Add(Vector3.Lerp(from, to, d / length));
                    d += spacing;
                }
                carried = length - (d - spacing);
            }
            if (Vector3.Distance(road[road.Count - 1], b) < spacing * 0.4f && road.Count > 1)
                road.RemoveAt(road.Count - 1);
            road.Add(b);
            return road;
        }
    }
}
