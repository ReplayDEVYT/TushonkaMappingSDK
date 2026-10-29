using System;
using System.Collections.Generic;
using System.Linq;
using BezierSplineTools;
using EFT.Vehicle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // The BTR, as on Woods and Streets. The game's BtrController loads a MapPathConfig for the map (retail: a
    // Resources prefab, "MapPathsConfigurations/<map>/<map>MapPathsConfiguration"; on SDK maps the client takes
    // the scene's instead) and drives along it:
    //
    //   stops   PathDestination "p1", "p2", ...: where it pauses, and where the taxi can take a player; facing
    //           matters at the route's enter point, where it appears
    //   roads   PathSpline "s_pA_pB" (BezierSpline) between every two stops: it drives a stop's road to any
    //           other stop of its route (the taxi picks any), so every pair needs one. The SDK makes them along
    //           the navmesh.
    //   routes  PathConfig (from the server, globals BTRSettings.MapsConfigs[map]): the stops in order, the
    //           enter / exit points it comes from and leaves to, and how many laps (circle) or one pass (once)
    //
    // Woods has 12 stops (p9-p12 are off-road enter/exit points), 28 roads between p1-p8 and three-point-turn
    // splines (useReverse); without useReverse the BTR just turns along the next road. Build Map exports the
    // routes and the TarkovBtrSettings (chance, timing, stop names) and CustomMapLoader registers the BTR.
    public static class TarkovBtr
    {
        public const string RootName = "BTR_MapPathsConfiguration";
        public const string StopsName = "Stops", RoadsName = "Roads", SettingsName = "SDK_BtrSettings";

        [MenuItem("GameObject/Tarkov SDK/World Events/BTR Route", priority = 78)]
        private static void MenuRoute(MenuCommand cmd)
        {
            var existing = Find(SceneManager.GetActiveScene());
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                Debug.Log("[Tarkov SDK] The map already has a BTR route; add stops and routes in its inspector.");
                return;
            }
            TarkovSceneTools.Place(cmd, p => Create(p).gameObject);
        }

        [MenuItem("Tarkov SDK/Generation/Misc/BTR Roads And Turns", priority = 112)]
        private static void RoadsMenu() => EditorApplication.delayCall += () =>
        {
            var config = Find(SceneManager.GetActiveScene());
            if (config == null)
            {
                EditorUtility.DisplayDialog("BTR", "The active scene has no BTR route. Add one with GameObject > Tarkov SDK > World Events > BTR Route.", "OK");
                return;
            }
            MapPathConfigEditor.Report(GenerateRoads(config, replace: true));
        };

        public static MapPathConfig Find(Scene scene) =>
            scene.GetRootGameObjects().Select(g => g.GetComponentInChildren<MapPathConfig>(true)).FirstOrDefault(c => c != null);

        public static TarkovBtrSettings Settings(MapPathConfig config, bool create)
        {
            var settings = config.GetComponentInChildren<TarkovBtrSettings>(true);
            if (settings == null && create)
            {
                var go = TarkovFacilitySystems.Child(config.transform, SettingsName, Vector3.zero);
                go.tag = "EditorOnly";
                settings = go.AddComponent<TarkovBtrSettings>();
            }
            return settings;
        }

        // The starter route, laid out around origin (default: the Scene view's centre): it comes in at p1, drives
        // laps of p2 > p3 > p4 and leaves to p5, with the stops facing the way it drives. A route of only two stops
        // drives the same road back and forth, so it would turn round at every stop.
        public static MapPathConfig Create(Transform parent, Vector3? origin = null)
        {
            var go = TarkovFacilitySystems.Child(parent, RootName, Vector3.zero);
            var config = go.AddComponent<MapPathConfig>();
            // where it waits off the map before it comes (retail: 1000, -1000, 1000)
            config.DepotPosition = new Vector3(1000f, -1000f, 1000f);
            config.PathDestinations = new List<PathDestination>();
            config.PathSplines = new List<PathSpline>();
            TarkovFacilitySystems.Child(go.transform, StopsName, Vector3.zero);
            TarkovFacilitySystems.Child(go.transform, RoadsName, Vector3.zero);
            Settings(config, true);
            var center = origin ?? (SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.pivot : Vector3.zero);
            AddStop(config, center + new Vector3(-40f, 0f, -30f), "Comes In");
            AddStop(config, center + new Vector3(15f, 0f, -30f), "Stop 2");
            AddStop(config, center + new Vector3(40f, 0f, 20f), "Stop 3");
            AddStop(config, center + new Vector3(-5f, 0f, 30f), "Stop 4");
            AddStop(config, center + new Vector3(52f, 0f, -12f), "Leaves");
            config.PathsConfiguration = new BTRMapPath
            {
                pathsConfigurations = new[] { NewRoute("RouteOne", "p1", new List<string> { "p2", "p3", "p4" }, "p5") },
            };
            Sync(config);
            FaceAlongRoutes(config);
            return config;
        }

        public static PathConfig NewRoute(string id, string enter, List<string> points, string exit = null) => new PathConfig
        {
            id = id, active = true, enterPoint = enter, exitPoint = exit ?? enter, pathPoints = points, circle = true, circleCount = 5, once = false,
        };

        public static PathDestination AddStop(MapPathConfig config, Vector3 position, string name = null)
        {
            var stops = config.transform.Find(StopsName) ?? TarkovFacilitySystems.Child(config.transform, StopsName, Vector3.zero).transform;
            int n = 1;
            var ids = new HashSet<string>(Stops(config).Select(s => s.id));
            while (ids.Contains("p" + n))
                n++;
            var go = TarkovFacilitySystems.Child(stops, "Stop p" + n, Vector3.zero);
            go.transform.position = SnapToGround(position);
            var stop = go.AddComponent<PathDestination>();
            stop.id = "p" + n;
            stop.connectedPaths = new List<PathSpline>();
            stop.frontReverseData = new PathReverseData { reverseParts = new List<PathReversePart>() };
            stop.backReverseData = new PathReverseData { reverseParts = new List<PathReversePart>() };
            stop.useReverse = false;
            var settings = Settings(config, true);
            settings.Stops.Add(new TarkovBtrSettings.Stop { Id = stop.id, Name = name ?? "Stop " + n });
            return stop;
        }

        private static Vector3 SnapToGround(Vector3 p) =>
            Physics.Raycast(p + Vector3.up * 50f, Vector3.down, out var hit, 200f, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore) ? hit.point : p;

        public static List<PathDestination> Stops(MapPathConfig config) => config.GetComponentsInChildren<PathDestination>(true).ToList();
        public static List<PathSpline> Roads(MapPathConfig config) => config.GetComponentsInChildren<PathSpline>(true).ToList();

        private static int Number(string id) => int.TryParse(id.TrimStart('p'), out var n) ? n : int.MaxValue;
        public static string RoadId(string a, string b) => Number(a) <= Number(b) ? $"s_{a}_{b}" : $"s_{b}_{a}";

        // the pairs of stops the BTR may drive between: every two stops of each route, its enter and exit included
        public static List<(string A, string B)> NeededPairs(MapPathConfig config)
        {
            var pairs = new HashSet<(string, string)>();
            foreach (var route in config.PathsConfiguration.pathsConfigurations ?? new PathConfig[0])
            {
                var all = (route.pathPoints ?? new List<string>()).Concat(new[] { route.enterPoint, route.exitPoint }).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
                for (int i = 0; i < all.Count; i++)
                    for (int j = i + 1; j < all.Count; j++)
                        pairs.Add(Number(all[i]) <= Number(all[j]) ? (all[i], all[j]) : (all[j], all[i]));
            }
            return pairs.Select(p => (p.Item1, p.Item2)).ToList();
        }

        // how far roads keep off the navmesh's edges: half the hull (1.45 m) and a metre
        public const float RoadClearance = 2.5f;
        // straight run out of and into a stop, along its heading
        public const float RoadLead = 8f;

        // The legs a route drives, in order, as the game picks them (VehicleBase.ToNextDestination): enter to the
        // first stop, stop to stop, the last stop back to the first for another lap (circle), and on to the exit
        // (from the first stop after the last lap, from the last stop when once through).
        public static List<(string From, string To)> RouteLegs(PathConfig route)
        {
            var legs = new List<(string, string)>();
            var points = route.pathPoints ?? new List<string>();
            if (points.Count == 0)
                return legs;
            if (!string.IsNullOrEmpty(route.enterPoint))
                legs.Add((route.enterPoint, points[0]));
            for (int i = 0; i + 1 < points.Count; i++)
                legs.Add((points[i], points[i + 1]));
            if (route.circle && points.Count > 1)
                legs.Add((points[points.Count - 1], points[0]));
            if (!string.IsNullOrEmpty(route.exitPoint))
                legs.Add((route.circle ? points[0] : points[points.Count - 1], route.exitPoint));
            return legs.Where(l => l.Item1 != l.Item2).ToList();
        }

        private static IEnumerable<PathConfig> RoutesOf(MapPathConfig config) =>
            (config.PathsConfiguration.pathsConfigurations ?? new PathConfig[0]).OrderByDescending(r => r.active);

        // Turns every stop of a route to face the way the BTR drives through it (half way between coming in and
        // going out). Drive-through roads then leave and arrive along those headings, so it drives on without the
        // three-point turn. Returns the stops it turned.
        public static List<PathDestination> FaceAlongRoutes(MapPathConfig config)
        {
            var stops = Stops(config).ToDictionary(s => s.id);
            var headings = new Dictionary<string, Vector3>();
            foreach (var route in RoutesOf(config))
                foreach (var (from, to) in RouteLegs(route))
                {
                    if (!stops.TryGetValue(from, out var a) || !stops.TryGetValue(to, out var b))
                        continue;
                    var dir = Vector3.ProjectOnPlane(b.transform.position - a.transform.position, Vector3.up).normalized;
                    headings[from] = (headings.TryGetValue(from, out var h) ? h : Vector3.zero) + dir;
                    headings[to] = (headings.TryGetValue(to, out h) ? h : Vector3.zero) + dir;
                }
            var turned = new List<PathDestination>();
            foreach (var pair in headings)
            {
                if (!stops.TryGetValue(pair.Key, out var stop) || pair.Value.sqrMagnitude < 0.01f)
                    continue;
                Undo.RecordObject(stop.transform, "Face BTR stops");
                stop.transform.rotation = Quaternion.LookRotation(pair.Value.normalized, Vector3.up);
                turned.Add(stop);
            }
            return turned;
        }

        // the way a road between a and b should leave a and come into b: along the legs the routes drive (the
        // first one found), else along whichever way each stop faces that points more towards the other
        private static (Vector3 Leave, Vector3 Arrive, bool Flip) RoadHeadings(MapPathConfig config, PathDestination a, PathDestination b)
        {
            Vector3 Flat(Vector3 v) => Vector3.ProjectOnPlane(v, Vector3.up).normalized;
            foreach (var route in RoutesOf(config))
                foreach (var (from, to) in RouteLegs(route))
                {
                    if (from == a.id && to == b.id)
                        return (Flat(a.transform.forward), Flat(b.transform.forward), false);
                    if (from == b.id && to == a.id)
                        return (Flat(b.transform.forward), Flat(a.transform.forward), true);
                }
            var ab = Flat(b.transform.position - a.transform.position);
            var leave = Flat(a.transform.forward) * (Vector3.Dot(a.transform.forward, ab) >= 0f ? 1f : -1f);
            var arrive = Flat(b.transform.forward) * (Vector3.Dot(b.transform.forward, ab) >= 0f ? 1f : -1f);
            return (leave, arrive, false);
        }

        // Builds one road between two stops: drive-through (along their headings, see RoadHeadings) or the plain
        // shortest navmesh path; either way kept off the navmesh's edges. Null if the navmesh doesn't join them.
        public static List<Vector3> RoadWaypoints(MapPathConfig config, PathDestination a, PathDestination b, bool driveThrough)
        {
            if (!driveThrough)
                return TarkovSplines.NavMeshRoad(a.transform.position, b.transform.position, 12f, RoadClearance);
            var (leave, arrive, flip) = RoadHeadings(config, a, b);
            var from = flip ? b : a;
            var to = flip ? a : b;
            var road = TarkovSplines.DirectedRoad(from.transform.position, leave, to.transform.position, arrive, RoadLead, 12f, RoadClearance);
            if (road != null && flip)
                road.Reverse();
            return road;
        }

        public static PathSpline CreateRoad(MapPathConfig config, string id, List<Vector3> waypoints)
        {
            var roadsRoot = config.transform.Find(RoadsName) ?? TarkovFacilitySystems.Child(config.transform, RoadsName, Vector3.zero).transform;
            var go = TarkovFacilitySystems.Child(roadsRoot, "Road " + id, Vector3.zero);
            go.transform.position = Vector3.zero;
            var spline = go.AddComponent<BezierSpline>();
            TarkovSplines.FromWaypoints(spline, waypoints);
            var part = go.AddComponent<PathSpline>();
            part.id = id;
            part.spline = spline;
            Undo.RegisterCreatedObjectUndo(go, "BTR road");
            return part;
        }

        // Rebuilds one road from its stops (after moving them, or to undo hand edits); false if the navmesh
        // doesn't join them.
        public static bool RegenerateRoad(MapPathConfig config, PathSpline road, bool driveThrough)
        {
            var ids = road.id.Split('_');
            var stops = Stops(config).ToDictionary(s => s.id);
            if (ids.Length < 3 || !stops.TryGetValue(ids[1], out var a) || !stops.TryGetValue(ids[2], out var b))
                return false;
            var waypoints = RoadWaypoints(config, a, b, driveThrough);
            if (waypoints == null)
                return false;
            Undo.RecordObject(road.spline, "Regenerate BTR road");
            TarkovSplines.FromWaypoints(road.spline, waypoints);
            EditorUtility.SetDirty(road.spline);
            SetEdited(config, road.id, false);
            return true;
        }

        // roads changed by hand in the path editor; Generate keeps them unless told otherwise
        public static bool IsEdited(MapPathConfig config, string id) => Settings(config, false)?.EditedRoads.Contains(id) ?? false;

        public static void SetEdited(MapPathConfig config, string id, bool edited)
        {
            var settings = Settings(config, edited);
            if (settings == null || settings.EditedRoads.Contains(id) == edited)
                return;
            Undo.RecordObject(settings, "BTR road edited");
            if (edited)
                settings.EditedRoads.Add(id);
            else
                settings.EditedRoads.Remove(id);
            EditorUtility.SetDirty(settings);
        }

        // A road for every needed pair that has none (replace: every road, except hand-edited ones unless
        // replaceEdited). Drive-through turns the route stops along their routes first and shapes the roads to
        // their headings. Returns what it couldn't make.
        public static List<string> GenerateRoads(MapPathConfig config, bool replace, bool driveThrough = true, bool replaceEdited = false)
        {
            if (driveThrough && replace)
                FaceAlongRoutes(config);
            var stops = Stops(config).ToDictionary(s => s.id);
            var existing = Roads(config).GroupBy(r => r.id).ToDictionary(g => g.Key, g => g.First());
            var failed = new List<string>();
            foreach (var (a, b) in NeededPairs(config))
            {
                var id = RoadId(a, b);
                if (!stops.TryGetValue(a, out var sa) || !stops.TryGetValue(b, out var sb))
                {
                    failed.Add($"{id} (no stop {(stops.ContainsKey(a) ? b : a)})");
                    continue;
                }
                if (existing.TryGetValue(id, out var old))
                {
                    if (!replace || (IsEdited(config, id) && !replaceEdited))
                        continue;
                    Undo.DestroyObjectImmediate(old.gameObject);
                    SetEdited(config, id, false);
                }
                var road = RoadWaypoints(config, sa, sb, driveThrough);
                if (road == null)
                {
                    failed.Add($"{id} (the navmesh doesn't join {a} and {b})");
                    continue;
                }
                CreateRoad(config, id, road);
            }
            Sync(config);
            // the turns are built in each stop's frame, so the enter points are turned to their road first
            var turned = OrientEnterPoints(config);
            foreach (var stop in Stops(config))
                if (!GenerateTurns(config, stop, replace || turned.Contains(stop)))
                    failed.Add($"turn at {stop.id} (no size or side keeps the BTR clear of walls; the least bad one is used. Give the stop more open space)");
            Sync(config);
            return failed;
        }

        // ------------------------------------------------------------ turns

        // Woods p1's three-point turn, in the stop's frame facing the way the BTR does (x right, z forward,
        // metres): forward curving left, back up, forward to the stop again, now facing the other way. The
        // game drives it (useReverse) after a pause when the next road starts behind the BTR: front when it
        // faces the stop's forward, back when it faces the other way.
        private static readonly Vector2[][] TurnShape =
        {
            new[] { new Vector2(0f, 0f), new Vector2(-2.1f, 12.9f), new Vector2(-5.5f, 14.7f), new Vector2(-10.6f, 15.1f) },
            new[] { new Vector2(-10.6f, 15.1f), new Vector2(-6.4f, 14.7f), new Vector2(-2.4f, 16.3f), new Vector2(-1.6f, 22.2f) },
            new[] { new Vector2(-1.6f, 22.2f), new Vector2(-2.4f, 15.9f), new Vector2(-1.8f, 10.7f), new Vector2(0f, 0f) },
        };
        private static readonly ReverseMoveType[] TurnMoves = { ReverseMoveType.forward, ReverseMoveType.backward, ReverseMoveType.forward };

        private static string TurnId(string stop, string side, int part) => $"r_{stop}_{side}_{part}";

        // false when neither side has navmesh room (the left one is made anyway)
        public static bool GenerateTurns(MapPathConfig config, PathDestination stop, bool replace)
        {
            var mine = Roads(config).Where(r => r.id.StartsWith($"r_{stop.id}_", StringComparison.Ordinal)).ToList();
            if (mine.Count == 6 && !replace && stop.useReverse)
                return true;
            foreach (var old in mine)
                Undo.DestroyObjectImmediate(old.gameObject);
            var roads = config.transform.Find(RoadsName) ?? TarkovFacilitySystems.Child(config.transform, RoadsName, Vector3.zero).transform;
            var turns = roads.Find(TurnsName) ?? TarkovFacilitySystems.Child(roads, TurnsName, Vector3.zero).transform;
            var forward = Vector3.ProjectOnPlane(stop.transform.forward, Vector3.up).normalized;
            if (forward == Vector3.zero)
                forward = Vector3.forward;
            bool clear = true;
            Undo.RecordObject(stop, "BTR turns");
            stop.frontReverseData = new PathReverseData { reverseParts = Turn(turns, stop, "f", forward, ref clear) };
            stop.backReverseData = new PathReverseData { reverseParts = Turn(turns, stop, "b", -forward, ref clear) };
            stop.useReverse = true;
            EditorUtility.SetDirty(stop);
            return clear;
        }

        public const string TurnsName = "Turns";

        // the BTR's footprint (retail hull about 2.9 x 2.8 x 7.5 m) with a margin, from 0.5 m above the ground so
        // kerbs and slopes don't count
        private static readonly Vector3 HullHalfExtents = new Vector3(1.45f + 0.6f, 1.15f, 3.75f + 0.6f);
        private const float HullClearance = 0.5f;
        // turn sizes tried (1 = Woods'), then the other side, smallest clash wins
        private static readonly float[] TurnScales = { 1f, 1.2f, 0.85f, 1.45f, 0.7f, 1.7f };

        private static List<PathReversePart> Turn(Transform parent, PathDestination stop, string tag, Vector3 facing, ref bool clear)
        {
            var origin = stop.transform.position;
            var right = Vector3.Cross(Vector3.up, facing);
            Vector3 Ground(Vector3 flat) =>
                Physics.Raycast(flat + Vector3.up * 5f, Vector3.down, out var hit, 20f, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore) ? hit.point : flat;
            Vector3 Point(Vector2 p, float side, float scale) => Ground(origin + right * (p.x * side * scale) + facing * (p.y * scale));

            // how much of the turn the hull hits: the hull is swept along each part, turned along its path
            float Clash(float side, float scale)
            {
                float score = 0f;
                foreach (var shape in TurnShape)
                {
                    var pts = shape.Select(p => Point(p, side, scale)).ToArray();
                    var samples = Enumerable.Range(0, 17).Select(i => BezierPoint(pts, i / 16f)).ToArray();
                    for (int i = 0; i < samples.Length; i++)
                    {
                        var along = samples[Math.Min(i + 1, samples.Length - 1)] - samples[Math.Max(i - 1, 0)];
                        along = Vector3.ProjectOnPlane(along, Vector3.up);
                        if (along.sqrMagnitude < 0.0001f)
                            along = facing;
                        var ground = Ground(samples[i]);
                        var center = ground + Vector3.up * (HullClearance + HullHalfExtents.y);
                        if (Physics.CheckBox(center, HullHalfExtents, Quaternion.LookRotation(along, Vector3.up), TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore))
                            score += 1f;
                        if (!UnityEngine.AI.NavMesh.SamplePosition(ground, out _, 1.5f, UnityEngine.AI.NavMesh.AllAreas))
                            score += 0.25f;
                    }
                }
                return score;
            }
            float bestSide = 1f, bestScale = 1f, best = float.MaxValue;
            foreach (var scale in TurnScales)
            {
                foreach (var s in new[] { 1f, -1f })
                {
                    float score = Clash(s, scale);
                    if (score < best)
                    {
                        best = score;
                        bestSide = s;
                        bestScale = scale;
                    }
                    if (best == 0f)
                        break;
                }
                if (best == 0f)
                    break;
            }
            if (best > 0f)
                clear = false;
            var parts = new List<PathReversePart>();
            for (int i = 0; i < TurnShape.Length; i++)
            {
                var id = TurnId(stop.id, tag, i + 1);
                var go = TarkovFacilitySystems.Child(parent, "Turn " + id, Vector3.zero);
                go.transform.position = Vector3.zero;
                var spline = go.AddComponent<BezierSpline>();
                spline.points = TurnShape[i].Select(p => Point(p, bestSide, bestScale)).ToArray();
                spline.modes = new[] { BezierControlPointMode.Free, BezierControlPointMode.Free };
                spline.loop = false;
                var part = go.AddComponent<PathSpline>();
                part.id = id;
                part.spline = spline;
                Undo.RegisterCreatedObjectUndo(go, "BTR turn");
                parts.Add(new PathReversePart { moveType = TurnMoves[i], spline = part });
            }
            return parts;
        }

        private static Vector3 BezierPoint(Vector3[] p, float t)
        {
            float u = 1f - t;
            return u * u * u * p[0] + 3f * u * u * t * p[1] + 3f * u * t * t * p[2] + t * t * t * p[3];
        }

        // the way a road leaves a stop, flat
        private static Vector3 RoadDirection(PathSpline road, Vector3 stop)
        {
            var pts = road.spline.points;
            var t = road.spline.transform;
            var first = t.TransformPoint(pts[0]);
            var last = t.TransformPoint(pts[pts.Length - 1]);
            var dir = Vector3.Distance(first, stop) <= Vector3.Distance(last, stop) ? t.TransformPoint(pts[1]) - first : t.TransformPoint(pts[pts.Length - 2]) - last;
            return Vector3.ProjectOnPlane(dir, Vector3.up).normalized;
        }

        // the BTR appears at a route's enter point facing the stop's forward and drives off without checking for a
        // turn (VehicleBase.MoveByPathSpline keeps its heading when the road is 90 degrees or more off it, so it
        // slides sideways): each enter point faces along its road to the route's first stop
        public static HashSet<PathDestination> OrientEnterPoints(MapPathConfig config)
        {
            var turned = new HashSet<PathDestination>();
            var stops = Stops(config).ToDictionary(s => s.id);
            var roads = Roads(config).ToDictionary(r => r.id);
            foreach (var route in (config.PathsConfiguration.pathsConfigurations ?? new PathConfig[0]).OrderByDescending(r => r.active))
            {
                var first = route.pathPoints?.FirstOrDefault();
                if (first == null || !stops.TryGetValue(route.enterPoint ?? "", out var enter) || turned.Contains(enter)
                    || !roads.TryGetValue(RoadId(route.enterPoint, first), out var road) || road.spline == null)
                    continue;
                var dir = RoadDirection(road, enter.transform.position);
                if (dir == Vector3.zero)
                    continue;
                Undo.RecordObject(enter.transform, "Face BTR road");
                enter.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
                turned.Add(enter);
            }
            return turned;
        }

        // the lists the game reads, filled as BSG's editor tools do (MapPathConfig.Add*, PathDestination.FindPaths)
        public static void Sync(MapPathConfig config)
        {
            var roads = Roads(config);
            config.PathSplines = roads;
            config.PathDestinations = Stops(config);
            foreach (var stop in config.PathDestinations)
            {
                stop.connectedPaths = roads.Where(r => !string.IsNullOrEmpty(r.id) && r.id.Split('_').Length >= 3
                                                       && (r.id.Split('_')[1] == stop.id || r.id.Split('_')[2] == stop.id)).ToList();
                stop.frontReverseData.reverseParts ??= new List<PathReversePart>();
                stop.backReverseData.reverseParts ??= new List<PathReversePart>();
                EditorUtility.SetDirty(stop);
            }
            foreach (var road in roads)
                if (road.spline == null)
                    road.spline = road.GetComponent<BezierSpline>();
            EditorUtility.SetDirty(config);
        }

        // Build Map: the lists, and every road the routes need
        public static void Prepare(GameObject[] roots, string sceneName, TarkovScenePreparer.Report report)
        {
            var configs = roots.SelectMany(r => r.GetComponentsInChildren<MapPathConfig>(true)).ToList();
            if (configs.Count > 1)
                report.Errors.Add($"{sceneName}: {configs.Count} BTR routes (MapPathConfig); the game uses one per map");
            report.Btr ??= configs.FirstOrDefault();
            foreach (var config in configs)
            {
                Sync(config);
                var routes = config.PathsConfiguration.pathsConfigurations ?? new PathConfig[0];
                if (!routes.Any(r => r.active))
                    report.Errors.Add($"{sceneName}/{config.name}: the BTR has no active route");
                var ids = new HashSet<string>(config.PathDestinations.Select(s => s.id));
                foreach (var route in routes)
                    foreach (var point in (route.pathPoints ?? new List<string>()).Concat(new[] { route.enterPoint, route.exitPoint }))
                        if (!ids.Contains(point))
                            report.Errors.Add($"{sceneName}/{config.name}: route '{route.id}' uses stop '{point}', which doesn't exist");
                // the game keys a route's parts by id (MapPathConfig.GetPathParts over enter + stops + exit), so a stop
                // listed twice is driven once; the taxi offers every listed stop, enter and exit included
                foreach (var route in routes.Where(r => r.pathPoints != null))
                {
                    var listed = route.pathPoints.Where(p => p == route.enterPoint || p == route.exitPoint).Distinct().ToList();
                    if (listed.Count > 0)
                        report.Warnings.Add($"{sceneName}/{config.name}: route '{route.id}' lists {string.Join(" and ", listed)} among its stops too, so the taxi offers it; " +
                                            "keep Comes From / Leaves To out of Stops In Order");
                    var twice = route.pathPoints.GroupBy(p => p).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                    if (twice.Count > 0)
                        report.Warnings.Add($"{sceneName}/{config.name}: route '{route.id}' lists {string.Join(", ", twice)} more than once; the game drives each stop once per pass");
                }
                var roads = new HashSet<string>(config.PathSplines.Select(r => r.id));
                var missing = NeededPairs(config).Select(p => RoadId(p.A, p.B)).Where(id => !roads.Contains(id)).ToList();
                if (missing.Count > 0)
                    report.Errors.Add($"{sceneName}/{config.name}: no BTR road for {string.Join(", ", missing)} (it would stop dead there); " +
                                      "Generate Roads in the BTR route's inspector");
                var noTurns = config.PathDestinations.Where(s => !s.useReverse || (s.frontReverseData.reverseParts?.Count ?? 0) != 3 || (s.backReverseData.reverseParts?.Count ?? 0) != 3
                                                                 || s.frontReverseData.reverseParts.Concat(s.backReverseData.reverseParts).Any(p => p.spline == null)).Select(s => s.id).ToList();
                if (noTurns.Count > 0)
                    report.Errors.Add($"{sceneName}/{config.name}: stops {string.Join(", ", noTurns)} have no three-point turn, so the BTR slides sideways when its next road " +
                                      "starts behind it; Generate Missing Roads in the BTR route's inspector");
                var stopsById = config.PathDestinations.ToDictionary(s => s.id);
                var roadsById = config.PathSplines.Where(r => !string.IsNullOrEmpty(r.id)).GroupBy(r => r.id).ToDictionary(g => g.Key, g => g.First());
                foreach (var route in routes.Where(r => r.active && r.pathPoints != null && r.pathPoints.Count > 0))
                    if (stopsById.TryGetValue(route.enterPoint ?? "", out var enter) && roadsById.TryGetValue(RoadId(route.enterPoint, route.pathPoints[0]), out var road) && road.spline != null
                        && Vector3.Angle(Vector3.ProjectOnPlane(enter.transform.forward, Vector3.up), RoadDirection(road, enter.transform.position)) > 45f)
                        report.Errors.Add($"{sceneName}/{config.name}: route '{route.id}' starts at {enter.id}, which doesn't face its road, so the BTR slides off sideways; " +
                                          "Regenerate All Roads (it turns enter points to their road)");
                if (Settings(config, false) == null)
                    report.Warnings.Add($"{sceneName}/{config.name}: no {SettingsName} (BTR chance and timing); defaults are used");
                foreach (var route in routes.Where(r => r.active && r.once && !string.IsNullOrEmpty(r.exitPoint) && r.exitPoint == r.enterPoint))
                    report.Warnings.Add($"{sceneName}/{config.name}: route '{route.id}' drives once through but leaves to its own enter point, which the game never drives back to " +
                                        "(its parts are keyed by stop), so the BTR stays at its last stop for good; give it its own Leaves To stop");

                // drive every active route as the game would and report where it turns round or slides
                if (missing.Count > 0)
                    continue;
                float speed = Settings(config, false)?.MoveSpeed ?? 8f;
                foreach (var route in routes.Where(r => r.active && r.pathPoints != null && r.pathPoints.Count > 0))
                {
                    var events = TarkovBtrSim.Check(config, route, speed);
                    var turns = events.Where(e => e.Kind == TarkovBtrSim.EventKind.Turn).Select(e => e.Text.Split(':')[0].Replace("three-point turn at ", "")).Distinct().ToList();
                    if (turns.Count > 0)
                        report.Warnings.Add($"{sceneName}/{config.name}: route '{route.id}' makes a three-point turn at {string.Join(", ", turns)} (the next road starts behind it); " +
                                            "Tools > World Events > BTR Path Editor shows it. Drive-through roads (Regenerate All Roads) leave each stop the way the BTR came in");
                    foreach (var slide in events.Where(e => e.Kind == TarkovBtrSim.EventKind.Slide).Select(e => e.Text).Distinct().Take(3))
                        report.Warnings.Add($"{sceneName}/{config.name}: route '{route.id}': the BTR {slide}; smooth that road in the BTR Path Editor");
                    foreach (var problem in events.Where(e => e.Kind == TarkovBtrSim.EventKind.Problem))
                        report.Warnings.Add($"{sceneName}/{config.name}: route '{route.id}': {problem.Text}");
                }
            }
        }
    }

    [CustomEditor(typeof(MapPathConfig))]
    public class MapPathConfigEditor : UnityEditor.Editor
    {
        private bool _showRaw;

        public override void OnInspectorGUI()
        {
            var config = (MapPathConfig)target;
            var settings = TarkovBtr.Settings(config, false);
            EditorGUILayout.LabelField("BTR route", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Stops (blue in the Scene view) are where the BTR pauses and where the taxi goes; roads join every two stops of a route. " +
                                       "Bake the navmesh first: roads follow it. The BTR is as wide as a car lane, so check them.", EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button(new GUIContent("Open BTR Path Editor", "Edit roads point by point and watch the BTR drive its routes, with its turns and slides marked.")))
                EditorApplication.delayCall += () => TarkovBtrEditorWindow.Open(config);

            if (settings != null)
            {
                var so = new SerializedObject(settings);
                EditorGUILayout.PropertyField(so.FindProperty("ChanceSpawn"), new GUIContent("Chance (%)"));
                EditorGUILayout.PropertyField(so.FindProperty("SpawnPeriod"), new GUIContent("Arrives After (s)"));
                EditorGUILayout.PropertyField(so.FindProperty("MoveSpeed"));
                EditorGUILayout.PropertyField(so.FindProperty("PauseDuration"), new GUIContent("Waits At Stops (s)"));
                EditorGUILayout.PropertyField(so.FindProperty("Skin"));
                so.ApplyModifiedProperties();
            }

            EditorGUILayout.Space();
            var stops = TarkovBtr.Stops(config);
            EditorGUILayout.LabelField($"Stops ({stops.Count})", EditorStyles.boldLabel);
            foreach (var stop in stops)
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(stop.id, GUILayout.Width(36f));
                    var entry = settings?.Stops.FirstOrDefault(s => s.Id == stop.id);
                    if (settings != null && entry == null)
                        settings.Stops.Add(entry = new TarkovBtrSettings.Stop { Id = stop.id, Name = stop.id });
                    if (entry != null)
                    {
                        EditorGUI.BeginChangeCheck();
                        var name = EditorGUILayout.TextField(entry.Name);
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(settings, "Stop name");
                            entry.Name = name;
                            EditorUtility.SetDirty(settings);
                        }
                    }
                    if (GUILayout.Button("Select", GUILayout.Width(52f)))
                        Selection.activeGameObject = stop.gameObject;
                    if (GUILayout.Button("-", GUILayout.Width(22f)))
                    {
                        var s = stop;
                        EditorApplication.delayCall += () =>
                        {
                            if (settings != null)
                            {
                                Undo.RecordObject(settings, "Remove stop");
                                settings.Stops.RemoveAll(x => x.Id == s.id);
                            }
                            foreach (var road in TarkovBtr.Roads(config).Where(r => r.id.Split('_').Skip(1).Contains(s.id)))
                                Undo.DestroyObjectImmediate(road.gameObject);
                            Undo.DestroyObjectImmediate(s.gameObject);
                            TarkovBtr.Sync(config);
                        };
                    }
                }
            if (GUILayout.Button("Add Stop At Scene View Centre"))
                EditorApplication.delayCall += () =>
                {
                    var stop = TarkovBtr.AddStop(config, SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.pivot : config.transform.position);
                    Undo.RegisterCreatedObjectUndo(stop.gameObject, "Add BTR stop");
                    TarkovBtr.Sync(config);
                    Selection.activeGameObject = stop.gameObject;
                };

            EditorGUILayout.Space();
            DrawRoutes(config, stops.Select(s => s.id).ToArray());

            EditorGUILayout.Space();
            var needed = TarkovBtr.NeededPairs(config).Select(p => TarkovBtr.RoadId(p.A, p.B)).ToList();
            var have = new HashSet<string>(TarkovBtr.Roads(config).Select(r => r.id));
            int missing = needed.Count(id => !have.Contains(id));
            EditorGUILayout.LabelField($"Roads: {needed.Count - missing} of {needed.Count} needed", missing > 0 ? EditorStyles.boldLabel : EditorStyles.label);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Generate Missing Roads", "Along the navmesh, for every pair of stops a route needs.")))
                    EditorApplication.delayCall += () => Report(TarkovBtr.GenerateRoads(config, replace: false));
                if (GUILayout.Button(new GUIContent("Regenerate All Roads", "Turns the stops along their routes and replaces every road (after moving stops) with one that " +
                                                                            "leaves and enters each stop the way the BTR drives, so it never turns round there. Roads edited by hand are kept.")))
                    EditorApplication.delayCall += () => Report(TarkovBtr.GenerateRoads(config, replace: true));
            }
            _showRaw = EditorGUILayout.Foldout(_showRaw, "All fields");
            if (_showRaw)
                DrawDefaultInspector();
        }

        internal static void Report(List<string> failed)
        {
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            if (failed.Count == 0)
                Debug.Log("[Tarkov SDK] BTR roads: every needed road is there.");
            else
                Debug.LogWarning("[Tarkov SDK] BTR roads not made: " + string.Join("; ", failed));
        }

        internal static bool DrawRoutes(MapPathConfig config, string[] stopIds)
        {
            var routes = (config.PathsConfiguration.pathsConfigurations ?? new PathConfig[0]).ToList();
            EditorGUILayout.LabelField($"Routes ({routes.Count}); one active route is picked each raid", EditorStyles.boldLabel);
            bool changed = false;
            int remove = -1;
            for (int r = 0; r < routes.Count; r++)
            {
                var route = routes[r];
                route.pathPoints ??= new List<string>();
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUI.BeginChangeCheck();
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        route.id = EditorGUILayout.TextField(route.id);
                        route.active = EditorGUILayout.ToggleLeft("Active", route.active, GUILayout.Width(60f));
                        if (GUILayout.Button("-", GUILayout.Width(22f)))
                            remove = r;
                    }
                    route.enterPoint = StopPopup("Comes From", route.enterPoint, stopIds, "Where it appears (facing the stop's forward) before driving to the first stop.");
                    route.exitPoint = StopPopup("Leaves To", route.exitPoint, stopIds, "Where it drives to and disappears at the end.");
                    int mode = route.once ? 1 : 0;
                    mode = EditorGUILayout.Popup("Drives", mode, new[] { "Laps (circle)", "Once through" });
                    route.once = mode == 1;
                    route.circle = mode == 0;
                    if (route.circle)
                        route.circleCount = Mathf.Max(1, EditorGUILayout.IntField("Laps", route.circleCount));
                    EditorGUILayout.LabelField("Stops in order", EditorStyles.miniBoldLabel);
                    int removePoint = -1;
                    for (int i = 0; i < route.pathPoints.Count; i++)
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            route.pathPoints[i] = StopPopup((i + 1).ToString(), route.pathPoints[i], stopIds, null);
                            if (GUILayout.Button("-", GUILayout.Width(22f)))
                                removePoint = i;
                        }
                    if (removePoint >= 0)
                        route.pathPoints.RemoveAt(removePoint);
                    if (GUILayout.Button("Add Stop To Route") && stopIds.Length > 0)
                        route.pathPoints.Add(stopIds.FirstOrDefault(s => !route.pathPoints.Contains(s)) ?? stopIds[0]);
                    changed |= EditorGUI.EndChangeCheck() || removePoint >= 0;
                }
            }
            if (remove >= 0)
            {
                routes.RemoveAt(remove);
                changed = true;
            }
            if (GUILayout.Button("Add Route"))
            {
                routes.Add(TarkovBtr.NewRoute("Route" + (routes.Count + 1), stopIds.FirstOrDefault() ?? "p1", stopIds.Skip(1).ToList()));
                changed = true;
            }
            if (changed)
            {
                Undo.RecordObject(config, "BTR routes");
                var path = config.PathsConfiguration;
                path.pathsConfigurations = routes.ToArray();
                config.PathsConfiguration = path;
                EditorUtility.SetDirty(config);
            }
            return changed;
        }

        internal static string StopPopup(string label, string value, string[] ids, string tooltip)
        {
            int index = Array.IndexOf(ids, value);
            int next = EditorGUILayout.Popup(new GUIContent(label, tooltip), index, ids);
            return next >= 0 ? ids[next] : value;
        }
    }

    public static class TarkovBtrGizmos
    {
        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
        private static void DrawStop(PathDestination stop, GizmoType type)
        {
            var t = stop.transform;
            Gizmos.color = new Color(0.3f, 0.6f, 1f, (type & GizmoType.Selected) != 0 ? 1f : 0.6f);
            // the BTR's footprint, about 3 x 7.5 m, facing the stop's forward
            Gizmos.matrix = Matrix4x4.TRS(t.position + Vector3.up * 1.4f, t.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(2.9f, 2.8f, 7.5f));
            Gizmos.DrawLine(new Vector3(0f, 0f, 3.75f), new Vector3(0f, 0f, 5.5f));
            Gizmos.matrix = Matrix4x4.identity;
            Handles.Label(t.position + Vector3.up * 3.5f, "BTR " + stop.id);
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.InSelectionHierarchy)]
        private static void DrawRoad(PathSpline road, GizmoType type)
        {
            if (road.spline == null)
                return;
            // three-point turns: only driven when the next road starts behind the BTR, so shown with their stop only
            if (road.id != null && road.id.StartsWith("r_", StringComparison.Ordinal))
            {
                var parts = road.id.Split('_');
                var selectedStop = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponent<PathDestination>() : null;
                if ((type & GizmoType.Selected) == 0 && (selectedStop == null || parts.Length < 2 || selectedStop.id != parts[1]))
                    return;
                Handles.color = new Color(1f, 0.6f, 0.2f, 0.8f);
                Handles.DrawAAPolyLine(2f, TarkovSplines.Sample(road.spline).ToArray());
                return;
            }
            var config = road.GetComponentInParent<MapPathConfig>();
            bool selected = (type & (GizmoType.Selected | GizmoType.InSelectionHierarchy)) != 0
                            || (config != null && Selection.activeGameObject != null && Selection.activeGameObject.GetComponentInParent<MapPathConfig>() == config);
            Handles.color = new Color(0.3f, 0.6f, 1f, selected ? 0.9f : 0.25f);
            Handles.DrawAAPolyLine(selected ? 4f : 2f, TarkovSplines.Sample(road.spline).ToArray());
        }
    }
}
