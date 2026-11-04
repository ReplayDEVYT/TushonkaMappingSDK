using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace mizmii.CustomMapLoader.AI
{
    // Patrol routes. BotZone.IsValid() requires at least one PatrolWay, and a bot with no route
    // stands at its spawn forever. Authored ways (PatrolWay + child PatrolPoints in the SDK) are
    // kept and snapped to the navmesh; zones without one get a generated way.
    //
    // Generation ported from ManimalIcebreaker's PatrolScanner: a greedy chain of 2-6 reachable
    // points picked from the cover graph near the zone's spawn markers. (The SDK's own generator,
    // Tools > AI > Patrol Editor, makes far fuller patrols; this is the fallback.)
    //
    // Every point gets sub points if it has none: a group's followers go to GetSubPoint(index) of the
    // leader's point, which clamps into subPoints and throws on an empty list. BSG bakes them with the
    // editor-only CreateSubPoints; EnsureSubPoints is the same placement (the SDK bakes them too).
    internal static class PatrolGenerator
    {
        private const int MinPointsPerWay = 2;
        private const int MaxPointsPerWay = 6;
        private const float MinSpacing = 7f;
        private const float MaxSpacing = 45f;
        private const float SnapRadius = 3f;
        private const float ZonePad = 18f;
        private const int MaxCandidates = 70;
        private static readonly float[] RingRadii = { 8f, 16f, 26f };
        private const int RingSamples = 10;

        public static void Generate(AICoversData covers)
        {
            var library = new List<Vector3>();
            if (covers.Points != null)
                foreach (var p in covers.Points)
                    library.Add(p.Position);

            GameObject root = null;
            int kept = 0, generated = 0, failed = 0;
            foreach (var zone in Object.FindObjectsOfType<BotZone>())
            {
                if (zone == null || zone.SpawnPointMarkers == null || zone.SpawnPointMarkers.Count == 0)
                    continue;

                var authored = zone.PatrolWays?.Where(w => w != null && w.Points != null && w.Points.Count >= MinPointsPerWay).ToArray();
                if (authored != null && authored.Length > 0)
                {
                    foreach (var way in authored)
                    {
                        SnapToNavMesh(way);
                        foreach (var point in way.Points)
                            if (point != null)
                                EnsureSubPoints(point, way.CoefSubPoints);
                    }
                    zone.PatrolWays = authored;
                    kept += authored.Length;
                    continue;
                }

                var route = BuildRoute(zone, library);
                if (route.Count < MinPointsPerWay)
                {
                    failed++;
                    Plugin.LogSource.LogWarning($"Bot zone '{zone.name}': only {route.Count} reachable patrol point(s), no patrol generated. " +
                                                "Bots in this zone will not move; add a PatrolWay in the SDK.");
                    continue;
                }

                if (root == null)
                    root = new GameObject("SDK_GeneratedPatrols");
                // the hierarchy is the data: EditorInitPoints builds Points from child transforms
                var wayGo = new GameObject($"Patrol_{zone.name}");
                wayGo.transform.SetParent(root.transform, false);
                wayGo.transform.position = route[0];
                var patrol = wayGo.AddComponent<PatrolWay>();
                patrol.PatrolType = PatrolType.patrolling;
                patrol.CoefSubPoints = 1f;
                foreach (var pos in route)
                {
                    var pointGo = new GameObject("PatrolPoint");
                    pointGo.transform.SetParent(wayGo.transform, false);
                    pointGo.transform.position = pos;
                    pointGo.AddComponent<PatrolPoint>().CanUseByBoss = true;
                }
                try
                {
                    patrol.EditorInitPoints(); // Points from children, ids
                    patrol.InitPoints();       // back references
                    foreach (var point in patrol.Points)
                        EnsureSubPoints(point, patrol.CoefSubPoints);
                }
                catch (System.Exception e)
                {
                    failed++;
                    Plugin.LogSource.LogWarning($"Bot zone '{zone.name}': patrol init failed: {e.Message}");
                    Object.Destroy(wayGo);
                    continue;
                }
                zone.PatrolWays = new[] { patrol };
                generated++;
            }
            Plugin.Verbose($"[AI] patrols: {kept} authored way(s) kept, {generated} generated, {failed} zone(s) without a route");
        }

        // PatrolPoint.CreateSubPoints (not SubManual): four tries round the point at 1.7, 2.5 and 3.4 m (x
        // CoefSubPoints), each turned 70-110 degrees from the last, kept when on the navmesh and reachable
        // within four times that distance; two more rings allowing a metre of height if that made six or
        // fewer; then up to seven within a metre of the others. Child PatrolPoints, as the game's Awake reads.
        private static readonly System.Random SubRandom = new System.Random(4271);

        private static void EnsureSubPoints(PatrolPoint point, float coef)
        {
            if (point.subPoints != null && point.subPoints.Count > 0)
                return;
            if (coef <= 0f)
                coef = 1f;
            var origin = point.transform.position;
            var made = new List<Vector3>();
            var path = new NavMeshPath();
            float Range(float a, float b) => a + (float)SubRandom.NextDouble() * (b - a);

            bool AddAt(Vector3 pos, float offset, float yOffset, float sample, bool checkDistance)
            {
                if (!NavMesh.SamplePosition(pos, out var hit, sample + yOffset, NavMesh.AllAreas))
                    return false;
                if (!NavMesh.CalculatePath(origin, pos, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                    return false;
                if (checkDistance)
                {
                    float length = 0f;
                    for (int i = 1; i < path.corners.Length; i++)
                        length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
                    if (length >= offset * 4f)
                        return false;
                }
                made.Add(hit.position);
                return true;
            }

            void Ring(float offset, float yOffset = 0f)
            {
                var v = new Vector3(Range(-1f, 1f), 0f, Range(-1f, 1f));
                v = (v.sqrMagnitude < 1e-6f ? Vector3.forward : v.normalized) * offset;
                for (int i = 0; i < 4; i++)
                {
                    v = Quaternion.Euler(0f, Range(70f, 110f), 0f) * v;
                    AddAt(origin + v, offset, yOffset, 0.3f, true);
                }
            }

            Ring(1.7f * coef);
            Ring(2.5f * coef);
            Ring(3.4f * coef);
            if (made.Count <= 6)
            {
                Ring(1.7f * coef, 1f);
                Ring(2.5f * coef, 1f);
            }
            for (int tries = 0; made.Count < 7 && tries < 30; tries++)
            {
                var from = made.Count == 0 ? origin : made[SubRandom.Next(made.Count)];
                if (!AddAt(from + new Vector3(Range(-1f, 1f), 0f, Range(-1f, 1f)), 1f, 2f, 1f, false))
                    AddAt(from + new Vector3(Range(-0.1f, 0.1f), 0f, Range(-0.1f, 0.1f)), 0.1f, 2f, 0.1f, false);
            }
            // nowhere at all round it: the point itself, so GetSubPoint has something to return
            if (made.Count == 0)
                made.Add(origin);
            var subs = new List<PatrolPoint>();
            for (int i = 0; i < made.Count; i++)
            {
                var go = new GameObject("SubPoint_" + (i + 1));
                go.transform.SetParent(point.transform, false);
                go.transform.position = made[i];
                var sub = go.AddComponent<PatrolPoint>();
                sub.CanUseByBoss = point.CanUseByBoss;
                subs.Add(sub);
            }
            point.subPoints = subs;
        }

        // PatrollingData ignores the path status when walking to a point, so an unreachable point
        // reads as "walking" while the bot stands still. Pull authored points onto the navmesh.
        private static void SnapToNavMesh(PatrolWay way)
        {
            foreach (var point in way.Points)
            {
                if (point == null)
                    continue;
                if (NavMesh.SamplePosition(point.transform.position, out var hit, SnapRadius, NavMesh.AllAreas))
                    point.transform.position = hit.position;
                else
                    Plugin.LogSource.LogWarning($"Patrol point {way.name}/{point.name} is more than {SnapRadius} m from the navmesh");
            }
        }

        private static List<Vector3> BuildRoute(BotZone zone, List<Vector3> library)
        {
            var seeds = zone.SpawnPointMarkers.Where(m => m != null).Select(m => m.transform.position).ToList();
            if (seeds.Count == 0)
                return new List<Vector3>();

            var centre = Vector3.zero;
            foreach (var s in seeds)
                centre += s;
            centre /= seeds.Count;
            float spread = seeds.Max(s => Vector3.Distance(s, centre));
            float reach = spread + ZonePad;

            var pool = new List<Vector3>(seeds);
            pool.AddRange(library.Where(p => (p - centre).sqrMagnitude <= reach * reach));
            if (pool.Count < 8)
                foreach (var r in RingRadii)
                    for (int i = 0; i < RingSamples; i++)
                    {
                        float a = i * Mathf.PI * 2f / RingSamples;
                        pool.Add(centre + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r);
                    }

            var snapped = new List<Vector3>();
            foreach (var c in pool.OrderBy(p => (p - centre).sqrMagnitude))
            {
                if (snapped.Count >= MaxCandidates)
                    break;
                if (!NavMesh.SamplePosition(c, out var hit, SnapRadius, NavMesh.AllAreas))
                    continue;
                if (snapped.Any(s => (s - hit.position).sqrMagnitude < 1f))
                    continue;
                snapped.Add(hit.position);
            }
            if (snapped.Count == 0)
                return new List<Vector3>();

            // greedy nearest reachable chain; an unreachable candidate is on another navmesh
            // island, so it is discarded for good
            var route = new List<Vector3> { snapped[0] };
            var used = new HashSet<int> { 0 };
            var path = new NavMeshPath();
            while (route.Count < MaxPointsPerWay)
            {
                var from = route[route.Count - 1];
                int best = -1;
                float bestD = float.MaxValue;
                for (int i = 0; i < snapped.Count; i++)
                {
                    if (used.Contains(i))
                        continue;
                    float d = Vector3.Distance(from, snapped[i]);
                    if (d < MinSpacing || d > MaxSpacing || d >= bestD)
                        continue;
                    if (!NavMesh.CalculatePath(from, snapped[i], NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                    {
                        used.Add(i);
                        continue;
                    }
                    best = i;
                    bestD = d;
                }
                if (best < 0)
                    break;
                used.Add(best);
                route.Add(snapped[best]);
            }
            return route;
        }
    }
}
