using System;
using System.Collections.Generic;
using System.Linq;
using EFT.Game.Spawning;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using Random = System.Random;

namespace TarkovSDK.Editor
{
    // Patrol routes for bot zones, made in the editor so they can be seen and adjusted (Tarkov SDK > Tools > AI >
    // Patrol Editor shows, edits and previews them). A bot with no route stands at its spawn forever.
    //
    // How the game walks them (PatrolPointChooserBasic.FindNextPoint): a way is a set, not a path. The bot goes to a
    // random point of its way that nobody owns and that is over 2 m away, often stopping 40-65% of the way there
    // (CHANCE_TO_CUT_WAY 75) to pick the next; at a stayPoint it stays and looks round (PointWithLookSides, ShallSit:
    // crouched), at a checkPoint it looks and moves on. Every CHANGE_WAY_TIME (scavs 125 s, x0.6-1.4) it may move to
    // another way of the zone, reserved ways (guard posts: it stays at each) included for scavs (CAN_CHOOSE_RESERV).
    // A group's followers go to the leader's point's sub points (GetSubPoint(index): clamped into subPoints, which
    // throws when there are none), which Awake reads from the point's child PatrolPoints; BSG bakes them with the
    // editor-only CreateSubPoints, so the SDK does the same (SubPoints, a port of it).
    //
    // Retail (every scene: 207 ways, 1912 points): 1-10 ways a zone (half have 2+), 114 patrolling, 80 reserved, 13
    // boss; ~7 points a way (1-41) over a ~48 m radius, 12.7 m to the nearest other point (median); 35% stayPoints, 11%
    // ShallSit; nearly every point 7-15 sub points; about half with look directions (mostly one).
    //
    // Generate: reachable navmesh around the zone, each place probed with short rays (cover beside it, a doorway,
    // a ceiling, how far it sees), points picked by farthest-point sampling weighted by that interest, at Spacing;
    // split into ways by area; stayPoints where there's cover and a view (crouched behind low cover), looking down the
    // longest sightlines; a reserved way of guard posts; sub points baked. Zones with a hand-made PatrolWay are left
    // alone; generated ways (named Generated...) are replaced.
    public static class TarkovPatrolGenerator
    {
        public const string Prefix = "Generated";
        public const string WayName = "GeneratedPatrol";
        public const string GuardWayName = "GeneratedGuards";
        public const string LooksName = "Looks";
        private const float SnapRadius = 3f;
        private const float ProbeHeight = 1.5f;
        private const float ProbeRange = 30f;

        [Serializable]
        public sealed class Settings
        {
            [Tooltip("How far round the zone's spawn points the patrol reaches (retail: ~48 m).")]
            public float Radius = 45f;
            [Tooltip("The distance aimed for between points (retail: 12.7 m to the nearest).")]
            public float Spacing = 12f;
            [Tooltip("No two points closer than this.")]
            public float MinSpacing = 5f;
            public int MinPoints = 4;
            public int MaxPoints = 16;
            [Tooltip("0: by the area (one way, two from 10 points, three from 16).")]
            public int Ways;
            [Tooltip("A reserved way of 2-3 guard posts (cover with a view) bots take turns at.")]
            public bool GuardPosts = true;
            [Tooltip("Share of the points that are stayPoints at most (retail 35%).")]
            [Range(0f, 1f)] public float StayShare = 0.35f;
            public int Seed = 1;
        }

        // ------------------------------------------------------------ menu

        [MenuItem("Tarkov SDK/Generation/Essential/Patrol Routes", priority = 103)]
        public static void GenerateMenu()
        {
            // a map's other scenes carry zones and navmesh too
            TarkovMapScenes.OpenMapScenes(SceneManager.GetActiveScene());
            if (!HasNavMesh())
            {
                EditorUtility.DisplayDialog("Patrol Routes", "No navmesh found. Bake one first (Tarkov SDK > Generation > Essential > Bake NavMesh).", "OK");
                return;
            }
            var message = GenerateAll(new Settings(), replaceHandMade: false);
            Debug.Log("[Tarkov SDK] Patrol routes: " + message.Replace("\n\n", " "));
            EditorUtility.DisplayDialog("Patrol Routes", message, "OK");
        }

        public static bool HasNavMesh() => NavMesh.CalculateTriangulation().indices.Length > 0;

        public static IEnumerable<BotZone> Zones()
        {
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded)
                    continue;
                foreach (var zone in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<BotZone>(true)))
                    yield return zone;
            }
        }

        public static bool IsGenerated(PatrolWay way) => way.name.StartsWith(Prefix, StringComparison.Ordinal);

        public static bool HasHandMade(BotZone zone) => zone.GetComponentsInChildren<PatrolWay>(true).Any(w => !IsGenerated(w));

        // every zone (without a hand-made way, unless replaceHandMade); returns what was done
        public static string GenerateAll(Settings settings, bool replaceHandMade)
        {
            int made = 0, kept = 0, points = 0;
            var failed = new List<string>();
            try
            {
                var zones = Zones().ToList();
                for (int i = 0; i < zones.Count; i++)
                {
                    var zone = zones[i];
                    if (EditorUtility.DisplayCancelableProgressBar("Patrol Routes", zone.name, i / (float)zones.Count))
                        break;
                    if (!replaceHandMade && HasHandMade(zone))
                    {
                        kept++;
                        continue;
                    }
                    var ways = Generate(zone, settings, replaceHandMade, out var why);
                    if (ways == null)
                        failed.Add($"{zone.name} ({why})");
                    else
                    {
                        made++;
                        points += ways.Sum(w => w.Points.Count);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            return $"Made patrols for {made} zone(s), {points} point(s)." + (kept > 0 ? $" {kept} zone(s) with a hand-made way were left alone." : "") +
                   (failed.Count > 0 ? $"\n\nNo patrol fits in: {string.Join(", ", failed)}." : "");
        }

        // ------------------------------------------------------------ one zone

        private sealed class Place
        {
            public Vector3 Position;
            public float[] Sight = new float[8];
            public bool Indoor, Cover, LowCover, Doorway;
            public float Interest;
            public float View => Sight.Max();
        }

        // The zone's patrol (its generated ways replaced; hand-made ones too when replaceHandMade). Null if there isn't
        // room for one (why says why).
        public static List<PatrolWay> Generate(BotZone zone, Settings settings, bool replaceHandMade, out string why)
        {
            why = null;
            var random = new Random(settings.Seed * 7919 + Hash(zone.name));
            var anchors = Anchors(zone);
            if (!NavMesh.SamplePosition(anchors.Centre, out var centreHit, 20f, NavMesh.AllAreas))
            {
                why = "no navmesh within 20 m of it";
                return null;
            }
            var centre = centreHit.position;
            float reach = Mathf.Max(settings.Radius, anchors.Spread + 15f);

            var places = Candidates(centre, reach, settings, anchors.Points);
            if (places.Count < settings.MinPoints)
            {
                why = $"only {places.Count} reachable place(s) on the navmesh within {reach:0} m";
                return null;
            }
            foreach (var p in places)
                Probe(p);

            // how many: the reachable area over the spacing, within the limits
            float area = places.Count * CandidateCell * CandidateCell;
            int count = Mathf.Clamp(Mathf.RoundToInt(area / (settings.Spacing * settings.Spacing)), settings.MinPoints, settings.MaxPoints);
            var chosen = Pick(places, centre, count, settings);
            AddIndoor(places, chosen, settings);
            if (chosen.Count < Mathf.Min(settings.MinPoints, 2))
            {
                why = "too little room between obstacles";
                return null;
            }

            // guard posts: cover with a view, away from the patrol points
            var guards = new List<Place>();
            if (settings.GuardPosts)
                foreach (var g in places.Where(p => p.Cover && p.View > 15f && !chosen.Contains(p)).OrderByDescending(p => p.Interest + p.View / 60f))
                {
                    if (guards.Count >= 3)
                        break;
                    if (chosen.Concat(guards).All(c => (c.Position - g.Position).sqrMagnitude > 16f) && guards.All(c => (c.Position - g.Position).sqrMagnitude > settings.Spacing * settings.Spacing))
                        guards.Add(g);
                }
            if (guards.Count < 2)
                guards.Clear();

            // remove the old ways only now that there's a new patrol
            foreach (var old in zone.GetComponentsInChildren<PatrolWay>(true).Where(w => replaceHandMade || IsGenerated(w)).ToList())
                Undo.DestroyObjectImmediate(old.gameObject);

            int wayCount = settings.Ways > 0 ? settings.Ways : chosen.Count >= 16 ? 3 : chosen.Count >= 10 ? 2 : 1;
            var groups = Split(chosen, Mathf.Clamp(wayCount, 1, Mathf.Max(1, chosen.Count / 3)));
            int stayLeft = Mathf.RoundToInt(chosen.Count * settings.StayShare);
            var stayers = new HashSet<Place>(chosen.Where(p => p.Cover && p.View > 12f).OrderByDescending(p => p.View).Take(stayLeft));
            var result = new List<PatrolWay>();
            for (int g = 0; g < groups.Count; g++)
            {
                var way = TarkovSceneTools.CreatePatrolWay(zone.transform, g == 0 ? WayName : $"{WayName}_{g + 1}", Vector3.zero);
                way.transform.position = groups[g][0].Position;
                way.PatrolType = PatrolType.patrolling;
                foreach (var place in groups[g])
                    MakePoint(way, place, stayers.Contains(place), random);
                result.Add(way);
                Undo.RegisterCreatedObjectUndo(way.gameObject, "Generate patrol");
            }
            if (guards.Count > 0)
            {
                var way = TarkovSceneTools.CreatePatrolWay(zone.transform, GuardWayName, Vector3.zero);
                way.transform.position = guards[0].Position;
                way.PatrolType = PatrolType.reserved;
                foreach (var place in guards)
                    MakePoint(way, place, true, random);
                result.Add(way);
                Undo.RegisterCreatedObjectUndo(way.gameObject, "Generate patrol");
            }
            foreach (var way in result)
                Sync(way);
            EditorSceneManager.MarkSceneDirty(zone.gameObject.scene);
            return result;
        }

        private static int Hash(string s)
        {
            unchecked
            {
                int h = 17;
                foreach (var c in s)
                    h = h * 31 + c;
                return h & 0x7fffffff;
            }
        }

        private struct ZoneAnchors
        {
            public Vector3 Centre;
            public float Spread;
            public List<Vector3> Points;
        }

        // the zone's spawn points (its own children and the ones it lists), or the zone itself
        private static ZoneAnchors Anchors(BotZone zone)
        {
            var seeds = zone.GetComponentsInChildren<SpawnPointMarker>(true).Select(m => m.transform.position)
                .Concat(zone.SpawnPointMarkers?.Where(m => m != null).Select(m => m.transform.position) ?? Enumerable.Empty<Vector3>())
                .Distinct().ToList();
            if (seeds.Count == 0)
                seeds.Add(zone.transform.position);
            var centre = seeds.Aggregate(Vector3.zero, (a, b) => a + b) / seeds.Count;
            return new ZoneAnchors { Centre = centre, Spread = seeds.Max(p => Vector3.Distance(p, centre)), Points = seeds };
        }

        private const float CandidateCell = 2.5f;

        // navmesh places reachable from the centre within reach (by path, so nothing on the far side of a wall that
        // takes a long way round), one per 2.5 m cell
        private static List<Place> Candidates(Vector3 centre, float reach, Settings settings, List<Vector3> anchors)
        {
            var cells = new Dictionary<Vector3Int, Vector3>();
            void Add(Vector3 p)
            {
                if ((p - centre).sqrMagnitude > reach * reach)
                    return;
                var key = new Vector3Int(Mathf.FloorToInt(p.x / CandidateCell), Mathf.FloorToInt(p.y / 2f), Mathf.FloorToInt(p.z / CandidateCell));
                if (!cells.ContainsKey(key))
                    cells[key] = p;
            }
            var tri = NavMesh.CalculateTriangulation();
            for (int i = 0; i + 2 < tri.indices.Length; i += 3)
            {
                var a = tri.vertices[tri.indices[i]];
                var b = tri.vertices[tri.indices[i + 1]];
                var c = tri.vertices[tri.indices[i + 2]];
                Add((a + b + c) / 3f);
                // big triangles (open ground) are filled in on the grid
                if (Vector3.Cross(b - a, c - a).magnitude * 0.5f > CandidateCell * CandidateCell * 2f)
                {
                    var min = Vector3.Min(a, Vector3.Min(b, c));
                    var max = Vector3.Max(a, Vector3.Max(b, c));
                    for (float x = min.x; x <= max.x; x += CandidateCell)
                        for (float z = min.z; z <= max.z; z += CandidateCell)
                            if (NavMesh.SamplePosition(new Vector3(x, (min.y + max.y) * 0.5f, z), out var hit, 1f, NavMesh.AllAreas))
                                Add(hit.position);
                }
            }
            foreach (var a in anchors)
                if (NavMesh.SamplePosition(a, out var hit, SnapRadius, NavMesh.AllAreas))
                    Add(hit.position);

            var path = new NavMeshPath();
            var result = new List<Place>();
            foreach (var p in cells.Values.OrderBy(p => (p - centre).sqrMagnitude))
            {
                if (result.Count >= 900)
                    break;
                if (!NavMesh.CalculatePath(centre, p, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                    continue;
                if (PathLength(path) > reach * 2.5f)
                    continue;
                result.Add(new Place { Position = p });
            }
            return result;
        }

        public static float PathLength(NavMeshPath path)
        {
            float length = 0f;
            var corners = path.corners;
            for (int i = 1; i < corners.Length; i++)
                length += Vector3.Distance(corners[i - 1], corners[i]);
            return length;
        }

        private static readonly Vector3[] Eight = Enumerable.Range(0, 8).Select(i => Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward).ToArray();

        // what the place is like, from rays at head height: how far it sees each way, cover right beside it, a doorway
        // (close on two opposite sides, open along), a ceiling
        private static void Probe(Place p)
        {
            int mask = TarkovRainCover.SolidMask | LayerMask.GetMask("Interactive");
            var head = p.Position + Vector3.up * ProbeHeight;
            var knee = p.Position + Vector3.up * 0.7f;
            bool lowOnly = false;
            for (int i = 0; i < 8; i++)
            {
                p.Sight[i] = Physics.Raycast(head, Eight[i], out var hit, ProbeRange, mask, QueryTriggerInteraction.Ignore) ? hit.distance : ProbeRange;
                // something to crouch behind: close at the knee, open at the head
                if (p.Sight[i] > 2f && Physics.Raycast(knee, Eight[i], 1.2f, mask, QueryTriggerInteraction.Ignore))
                    lowOnly = true;
            }
            float near = p.Sight.Min();
            p.Cover = near < 1.6f || lowOnly;
            p.LowCover = lowOnly;
            for (int i = 0; i < 4 && !p.Doorway; i++)
                p.Doorway = p.Sight[i] < 1.3f && p.Sight[i + 4] < 1.3f && Mathf.Max(p.Sight[(i + 2) % 8], p.Sight[(i + 6) % 8]) > 4f;
            p.Indoor = Physics.Raycast(p.Position + Vector3.up * 0.5f, Vector3.up, 8f, mask, QueryTriggerInteraction.Ignore);
            p.Interest = (p.Cover ? 0.3f : 0f) + (p.Doorway ? 0.3f : 0f) + (p.Indoor ? 0.4f : 0f) + 0.15f * Mathf.Clamp01(p.View / ProbeRange);
        }

        // farthest-point sampling weighted by interest, starting from the most interesting place near the centre
        private static List<Place> Pick(List<Place> places, Vector3 centre, int count, Settings settings)
        {
            var chosen = new List<Place>();
            var first = places.Where(p => (p.Position - centre).sqrMagnitude < 15f * 15f).DefaultIfEmpty(places[0]).OrderByDescending(p => p.Interest).First();
            chosen.Add(first);
            var nearest = places.Select(p => (p.Position - first.Position).magnitude).ToArray();
            while (chosen.Count < count)
            {
                int best = -1;
                float bestScore = 0f;
                for (int i = 0; i < places.Count; i++)
                {
                    if (nearest[i] < settings.MinSpacing)
                        continue;
                    // past twice the spacing, further doesn't count for more: the interest decides
                    float score = Mathf.Min(nearest[i], settings.Spacing * 2f) * (0.6f + places[i].Interest);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = i;
                    }
                }
                if (best < 0)
                    break;
                var pick = places[best];
                chosen.Add(pick);
                for (int i = 0; i < places.Count; i++)
                    nearest[i] = Mathf.Min(nearest[i], (places[i].Position - pick.Position).magnitude);
            }
            return chosen;
        }

        // Every indoor area (indoor places within 3.5 m of each other, at least ~20 m²) the sampling left out gets its
        // most enclosed place: bots search buildings on retail, and open ground otherwise wins on spacing.
        private static void AddIndoor(List<Place> places, List<Place> chosen, Settings settings)
        {
            var indoor = places.Where(p => p.Indoor).ToList();
            var seen = new HashSet<Place>();
            foreach (var seed in indoor)
            {
                if (!seen.Add(seed))
                    continue;
                var area = new List<Place> { seed };
                for (int i = 0; i < area.Count; i++)
                    foreach (var other in indoor)
                        if (!seen.Contains(other) && Mathf.Abs(other.Position.y - area[i].Position.y) < 1.5f && (other.Position - area[i].Position).sqrMagnitude < 3.5f * 3.5f)
                        {
                            seen.Add(other);
                            area.Add(other);
                        }
                if (area.Count < 3 || chosen.Count >= settings.MaxPoints + 4 || area.Any(chosen.Contains))
                    continue;
                // the place with the walls closest round it: the middle of the room, not its doorway
                var best = area.Where(p => chosen.All(c => (c.Position - p.Position).sqrMagnitude >= settings.MinSpacing * settings.MinSpacing * 0.5f))
                    .OrderBy(p => p.Sight.Average()).FirstOrDefault();
                if (best != null)
                    chosen.Add(best);
            }
        }

        // k groups by area (k-means from far-apart seeds), each at least 3 points
        private static List<List<Place>> Split(List<Place> points, int k)
        {
            if (k <= 1)
                return new List<List<Place>> { points };
            var seeds = new List<Vector3> { points[0].Position };
            while (seeds.Count < k)
                seeds.Add(points.OrderByDescending(p => seeds.Min(s => (s - p.Position).sqrMagnitude)).First().Position);
            List<List<Place>> groups = null;
            for (int iteration = 0; iteration < 10; iteration++)
            {
                groups = Enumerable.Range(0, k).Select(_ => new List<Place>()).ToList();
                foreach (var p in points)
                    groups[Enumerable.Range(0, k).OrderBy(i => (seeds[i] - p.Position).sqrMagnitude).First()].Add(p);
                for (int i = 0; i < k; i++)
                    if (groups[i].Count > 0)
                        seeds[i] = groups[i].Aggregate(Vector3.zero, (a, p) => a + p.Position) / groups[i].Count;
            }
            // a group too small to be a way joins its nearest neighbour
            var small = groups.Where(g => g.Count < 3).ToList();
            foreach (var g in small)
            {
                groups.Remove(g);
                if (groups.Count == 0)
                {
                    groups.Add(g);
                    break;
                }
                foreach (var p in g)
                    groups.OrderBy(o => o.Min(q => (q.Position - p.Position).sqrMagnitude)).First().Add(p);
            }
            return groups;
        }

        private static void MakePoint(PatrolWay way, Place place, bool stay, Random random)
        {
            var point = TarkovSceneTools.CreatePatrolPoint(way.transform, Vector3.zero);
            point.transform.position = place.Position;
            point.CanUseByBoss = true;
            point.PatrolPointType = stay ? PatrolPointType.stayPoint : PatrolPointType.checkPoint;
            // crouched behind low cover (retail: a third of the stayPoints)
            point.ShallSit = stay && place.LowCover;
            if (stay)
            {
                // the longest sightline, and a second clearly apart and nearly as long
                var order = Enumerable.Range(0, 8).OrderByDescending(i => place.Sight[i]).ToList();
                var directions = new List<Vector3> { Eight[order[0]] };
                int second = order.Skip(1).Where(i => Mathf.Abs(Mathf.DeltaAngle(i * 45f, order[0] * 45f)) >= 90f && place.Sight[i] >= place.Sight[order[0]] * 0.6f)
                    .DefaultIfEmpty(-1).First();
                if (second >= 0)
                    directions.Add(Eight[second]);
                SetLooks(point, directions);
            }
            SubPoints(point, way.CoefSubPoints, random);
        }

        // ------------------------------------------------------------ look directions

        // The game reads a point's look directions from a child PointWithLookSides (PatrolPoint.RefreshData clears the
        // reference, then takes it from the children), as world directions.
        public static PointWithLookSides Looks(PatrolPoint point) =>
            point.transform.Cast<Transform>().Select(c => c.GetComponent<PointWithLookSides>()).FirstOrDefault(l => l != null);

        public static void SetLooks(PatrolPoint point, List<Vector3> directions)
        {
            var looks = Looks(point);
            if (directions == null || directions.Count == 0)
            {
                if (looks != null)
                    Undo.DestroyObjectImmediate(looks.gameObject);
                return;
            }
            if (looks == null)
            {
                var go = new GameObject(LooksName);
                go.transform.SetParent(point.transform, false);
                Undo.RegisterCreatedObjectUndo(go, "Look directions");
                looks = go.AddComponent<PointWithLookSides>();
            }
            else
                Undo.RecordObject(looks, "Look directions");
            looks.Directions = directions.Select(d => Vector3.ProjectOnPlane(d, Vector3.up).normalized).ToList();
        }

        // ------------------------------------------------------------ sub points

        // PatrolPoint.CreateSubPoints (not SubManual), ported: four tries round the point at 1.7, 2.5 and 3.4 m (x the
        // way's CoefSubPoints), each turned 70-110 degrees from the last, kept when on the navmesh and reachable by a
        // path under four times that distance; two more rings allowing a metre of height if that made six or fewer;
        // then filled up to seven within a metre of the others. As child PatrolPoints named SubPoint_n.
        public static int SubPoints(PatrolPoint point, float coef, Random random)
        {
            foreach (var old in point.transform.Cast<Transform>().Where(c => c.GetComponent<PatrolPoint>() != null).ToList())
                Undo.DestroyObjectImmediate(old.gameObject);
            var made = new List<Vector3>();
            var origin = point.transform.position;
            var path = new NavMeshPath();
            float Range(float a, float b) => a + (float)random.NextDouble() * (b - a);

            bool AddAt(Vector3 pos, float offset, float yOffset, float sample, bool checkDistance)
            {
                if (!NavMesh.SamplePosition(pos, out var hit, sample + yOffset, NavMesh.AllAreas))
                    return false;
                if (!NavMesh.CalculatePath(origin, pos, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                    return false;
                if (checkDistance && PathLength(path) >= offset * 4f)
                    return false;
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
                var from = made.Count == 0 ? origin : made[random.Next(made.Count)];
                if (!AddAt(from + new Vector3(Range(-1f, 1f), 0f, Range(-1f, 1f)), 1f, 2f, 1f, false))
                    AddAt(from + new Vector3(Range(-0.1f, 0.1f), 0f, Range(-0.1f, 0.1f)), 0.1f, 2f, 0.1f, false);
            }
            for (int i = 0; i < made.Count; i++)
            {
                var go = new GameObject("SubPoint_" + (i + 1));
                go.transform.SetParent(point.transform, false);
                go.transform.position = made[i];
                go.AddComponent<PatrolPoint>().CanUseByBoss = point.CanUseByBoss;
                Undo.RegisterCreatedObjectUndo(go, "Sub points");
            }
            point.subPoints = point.transform.Cast<Transform>().Select(c => c.GetComponent<PatrolPoint>()).Where(p => p != null).ToList();
            EditorUtility.SetDirty(point);
            return made.Count;
        }

        // a point's own (main) points, not the sub points under them
        public static List<PatrolPoint> MainPoints(PatrolWay way) =>
            way.transform.Cast<Transform>().Select(c => c.GetComponent<PatrolPoint>()).Where(p => p != null).ToList();

        public static List<PatrolPoint> SubPointsOf(PatrolPoint point) =>
            point.transform.Cast<Transform>().Select(c => c.GetComponent<PatrolPoint>()).Where(p => p != null).ToList();

        // the way's Points from its children, as Build Map does
        public static void Sync(PatrolWay way)
        {
            way.Points = MainPoints(way);
            int id = 0;
            foreach (var p in way.Points)
            {
                p.PatrolWay = way;
                p.Id = id++;
                p.subPoints = SubPointsOf(p);
                EditorUtility.SetDirty(p);
            }
            EditorUtility.SetDirty(way);
        }

        // ------------------------------------------------------------ Build Map

        // Points without sub points get them (a follower going to one would throw otherwise), points far off the
        // navmesh and points the rest of their way can't reach are reported.
        public static void Prepare(Scene scene, GameObject[] roots, TarkovScenePreparer.Report report)
        {
            bool navMesh = HasNavMesh();
            int filled = 0, off = 0, unreachable = 0;
            var path = new NavMeshPath();
            foreach (var way in roots.SelectMany(r => r.GetComponentsInChildren<PatrolWay>(true)))
            {
                var points = MainPoints(way);
                if (!navMesh || points.Count == 0)
                    continue;
                var random = new Random(Hash(way.name) ^ points.Count);
                Vector3? anchor = null;
                foreach (var p in points)
                {
                    if (!NavMesh.SamplePosition(p.transform.position, out var hit, SnapRadius, NavMesh.AllAreas))
                    {
                        off++;
                        continue;
                    }
                    if (anchor == null)
                        anchor = hit.position;
                    else if (!NavMesh.CalculatePath(anchor.Value, hit.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                        unreachable++;
                    if (SubPointsOf(p).Count == 0 && SubPoints(p, way.CoefSubPoints > 0f ? way.CoefSubPoints : 1f, random) > 0)
                        filled++;
                }
            }
            if (filled > 0)
                Debug.Log($"[Tarkov SDK] {scene.name}: sub points made for {filled} patrol point(s) (a group's followers spread round them)");
            if (off > 0)
                report.Warnings.Add($"{scene.name}: {off} patrol point(s) are more than {SnapRadius} m from the navmesh; bots stand still trying to reach them " +
                                    "(Tarkov SDK > Tools > AI > Patrol Editor shows them in red)");
            if (unreachable > 0)
                report.Warnings.Add($"{scene.name}: {unreachable} patrol point(s) can't be walked to from the first point of their way (another navmesh island); " +
                                    "bots that pick them stand still (Tarkov SDK > Tools > AI > Patrol Editor)");
        }
    }
}
