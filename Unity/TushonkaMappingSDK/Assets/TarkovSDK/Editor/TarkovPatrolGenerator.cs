using System.Collections.Generic;
using System.Linq;
using EFT.Game.Spawning;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Patrol routes for bot zones, made in the editor so they can be seen and adjusted. A bot with no
    // route stands at its spawn forever; the client generates one at raid start for zones without any,
    // and this makes the same kind of route ahead of time: a PatrolWay under the zone whose child
    // PatrolPoints are a greedy chain of 2-6 points (retail's range), 7-45 m apart, each reachable on
    // the navmesh from the last, picked around the zone's spawn points.
    //
    // Zones with a hand-made PatrolWay are left alone. Generated ways are named GeneratedPatrol and
    // are replaced when generating again. Needs a baked navmesh.
    public static class TarkovPatrolGenerator
    {
        public const string WayName = "GeneratedPatrol";
        private const int MinPoints = 2;
        private const int MaxPoints = 6;
        private const float MinSpacing = 7f;
        private const float MaxSpacing = 45f;
        private const float SnapRadius = 3f;
        private const float ZonePad = 18f;
        private const int MaxCandidates = 120;
        private static readonly float[] RingRadii = { 8f, 16f, 26f, 36f };
        private const int RingSamples = 12;

        [MenuItem("Tarkov SDK/Tools/Generate Patrol Routes", priority = 223)]
        public static void GenerateMenu()
        {
            var triangulation = NavMesh.CalculateTriangulation();
            if (triangulation.indices.Length == 0)
            {
                EditorUtility.DisplayDialog("Patrol Routes", "No navmesh found. Bake one first (Tarkov SDK > Tools > Bake NavMesh For Active Scene).", "OK");
                return;
            }
            // navmesh triangle centres: places bots can stand, spread over the walkable area
            var library = new List<Vector3>();
            for (int i = 0; i + 2 < triangulation.indices.Length; i += 3)
                library.Add((triangulation.vertices[triangulation.indices[i]] + triangulation.vertices[triangulation.indices[i + 1]] +
                             triangulation.vertices[triangulation.indices[i + 2]]) / 3f);

            int made = 0, kept = 0;
            var failed = new List<string>();
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded)
                    continue;
                foreach (var zone in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<BotZone>(true)))
                {
                    var ways = zone.GetComponentsInChildren<PatrolWay>(true);
                    if (ways.Any(w => w.name != WayName))
                    {
                        kept++;
                        continue;
                    }
                    foreach (var old in ways)
                        Undo.DestroyObjectImmediate(old.gameObject);

                    var route = BuildRoute(zone, library);
                    if (route.Count < MinPoints)
                    {
                        failed.Add(zone.name);
                        continue;
                    }
                    var way = TarkovSceneTools.CreatePatrolWay(zone.transform, WayName, Vector3.zero);
                    way.transform.position = route[0];
                    foreach (var position in route)
                        TarkovSceneTools.CreatePatrolPoint(way.transform, Vector3.zero).transform.position = position;
                    Undo.RegisterCreatedObjectUndo(way.gameObject, "Generate patrol routes");
                    EditorSceneManager.MarkSceneDirty(scene);
                    made++;
                }
            }

            var message = $"Made {made} patrol route(s)." + (kept > 0 ? $" {kept} zone(s) with a hand-made route were left alone." : "") +
                          (failed.Count > 0 ? $"\n\nNo route fits in: {string.Join(", ", failed)} (too little reachable navmesh around their spawn points)." : "");
            Debug.Log("[Tarkov SDK] Patrol routes: " + message.Replace("\n\n", " "));
            EditorUtility.DisplayDialog("Patrol Routes", message, "OK");
        }

        private static List<Vector3> BuildRoute(BotZone zone, List<Vector3> library)
        {
            // the zone's spawn points, or the zone itself
            var seeds = zone.GetComponentsInChildren<SpawnPointMarker>(true).Select(m => m.transform.position)
                .Concat(zone.SpawnPointMarkers?.Where(m => m != null).Select(m => m.transform.position) ?? Enumerable.Empty<Vector3>())
                .Distinct().ToList();
            if (seeds.Count == 0)
                seeds.Add(zone.transform.position);
            var centre = seeds.Aggregate(Vector3.zero, (a, b) => a + b) / seeds.Count;
            float reach = seeds.Max(p => Vector3.Distance(p, centre)) + ZonePad;

            var pool = new List<Vector3>(seeds);
            pool.AddRange(library.Where(p => (p - centre).sqrMagnitude <= reach * reach));
            foreach (var r in RingRadii)
                for (int i = 0; i < RingSamples; i++)
                {
                    float a = i * Mathf.PI * 2f / RingSamples;
                    pool.Add(centre + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r);
                }

            // candidates on the navmesh, nearest the centre first, no two within a metre
            var snapped = new List<Vector3>();
            foreach (var c in pool.OrderBy(p => (p - centre).sqrMagnitude))
            {
                if (snapped.Count >= MaxCandidates)
                    break;
                if (!NavMesh.SamplePosition(c, out var hit, SnapRadius, NavMesh.AllAreas))
                    continue;
                if (snapped.Any(p => (p - hit.position).sqrMagnitude < 1f))
                    continue;
                snapped.Add(hit.position);
            }
            if (snapped.Count == 0)
                return new List<Vector3>();

            // greedy chain: the nearest reachable candidate in range; an unreachable one is on another
            // navmesh island and is dropped for good
            var route = new List<Vector3> { snapped[0] };
            var used = new HashSet<int> { 0 };
            var path = new NavMeshPath();
            while (route.Count < MaxPoints)
            {
                var from = route[route.Count - 1];
                int best = -1;
                float bestDistance = float.MaxValue;
                for (int i = 0; i < snapped.Count; i++)
                {
                    if (used.Contains(i))
                        continue;
                    float d = Vector3.Distance(from, snapped[i]);
                    if (d < MinSpacing || d > MaxSpacing || d >= bestDistance)
                        continue;
                    // not too close to the points already on the route either, so it doesn't fold back on itself
                    if (route.Any(p => Vector3.Distance(p, snapped[i]) < MinSpacing))
                        continue;
                    if (!NavMesh.CalculatePath(from, snapped[i], NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                    {
                        used.Add(i);
                        continue;
                    }
                    best = i;
                    bestDistance = d;
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
