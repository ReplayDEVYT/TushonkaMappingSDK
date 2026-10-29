using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EFT.Ballistics;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // Hand-drawn roads and junctions for the City Builder's Roads tab.
    //   Road (built meshes: Surface, Sidewalks, Curbs, Markings; Roadside = what Line This Road placed)
    //     SDK_Road (EditorOnly, TarkovCityRoad settings)
    //       Point 1..n (EditorOnly: the curve goes through them, in order)
    //   Junction (built meshes as above, flat at its height)
    //     SDK_Junction (EditorOnly, TarkovCityJunction settings)
    // A road is a Hermite curve through its points (Catmull-Rom tangents; an end joined to a junction leaves along the
    // junction's arm), cut into cross-sections Step metres apart, each lying on the ground under it (smoothed) when
    // FollowGround is on. A junction is a 3 x 3 grid of cells (sidewalk, road, sidewalk each way): the centre is road,
    // the corners sidewalk, each arm cell road when the arm is open (a road joins there, at the arm's outer edge) or
    // sidewalk when closed. Kerbs go between road and sidewalk, a skirt 0.5 m down round the outside hides the edge
    // against the ground. Everything is rebuilt when its settings, points or joined junction change (also after Undo):
    // a hash is checked a few times a second over the open scenes' roads and junctions.
    [InitializeOnLoad]
    public static class TarkovCityRoads
    {
        private const float Skirt = 0.5f;
        private static double _next;
        private static List<TarkovCityRoad> _roads;
        private static List<TarkovCityJunction> _junctions;
        // the hash each road or junction was last built with, by instance id (falls back to the saved BuiltHash)
        private static readonly Dictionary<int, int> Built = new Dictionary<int, int>();
        private static readonly Dictionary<int, (int Hash, List<Frame> Frames)> FrameCache = new Dictionary<int, (int, List<Frame>)>();

        static TarkovCityRoads()
        {
            EditorApplication.hierarchyChanged += Forget;
            EditorApplication.update += Update;
            SceneView.duringSceneGui += OnSceneGui;
        }

        public static void Forget()
        {
            _roads = null;
            _junctions = null;
        }

        public static List<TarkovCityRoad> Roads => (_roads ?? (_roads = Find<TarkovCityRoad>())).Where(r => r != null && r.transform.parent != null).ToList();
        public static List<TarkovCityJunction> Junctions => (_junctions ?? (_junctions = Find<TarkovCityJunction>())).Where(j => j != null && j.transform.parent != null).ToList();

        private static List<T> Find<T>() where T : Component
        {
            var found = new List<T>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded)
                    foreach (var root in scene.GetRootGameObjects())
                        found.AddRange(root.GetComponentsInChildren<T>(true));
            }
            return found;
        }

        // ------------------------------------------------------------ rebuild on change

        private static void Update()
        {
            if (EditorApplication.timeSinceStartup < _next || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                return;
            _next = EditorApplication.timeSinceStartup + 0.25;
            foreach (var junction in Junctions)
                if (NeedsBuild(junction, junction.BuiltHash, Hash(junction)))
                    Rebuild(junction);
            foreach (var road in Roads)
            {
                Pin(road);
                if (NeedsBuild(road, road.BuiltHash, Hash(road)))
                    Rebuild(road);
            }
        }

        private static bool NeedsBuild(Object o, int saved, int hash) =>
            Built.TryGetValue(o.GetInstanceID(), out var last) ? last != hash : saved != hash;

        private static int KitHash(TarkovCityKit kit) => kit != null ? JsonUtility.ToJson(kit).GetHashCode() : 0;

        public static int Hash(TarkovCityJunction j)
        {
            int saved = j.BuiltHash;
            j.BuiltHash = 0;
            int h = JsonUtility.ToJson(j).GetHashCode() * 31 + KitHash(j.Kit);
            j.BuiltHash = saved;
            return h;
        }

        public static int Hash(TarkovCityRoad road)
        {
            unchecked
            {
                int saved = road.BuiltHash;
                road.BuiltHash = 0;
                int h = JsonUtility.ToJson(road).GetHashCode() * 31 + KitHash(road.Kit);
                road.BuiltHash = saved;
                foreach (Transform point in road.transform)
                {
                    h = h * 31 + point.position.GetHashCode();
                    var data = point.GetComponent<TarkovCityRoadPoint>();
                    if (data != null)
                        h = h * 31 + JsonUtility.ToJson(data).GetHashCode();
                }
                h = h * 31 + road.transform.parent.localToWorldMatrix.GetHashCode();
                foreach (var j in new[] { road.StartJunction, road.EndJunction })
                    if (j != null && j.transform.parent != null)
                        h = h * 31 + j.transform.parent.localToWorldMatrix.GetHashCode() + Hash(j);
                return h;
            }
        }

        // a joined end sits on its junction's arm, and the road takes the junction's widths
        private static void Pin(TarkovCityRoad road)
        {
            int count = road.transform.childCount;
            if (count == 0)
                return;
            void Join(TarkovCityJunction j, int arm, Transform point)
            {
                if (j == null || j.transform.parent == null)
                    return;
                var anchor = Anchor(j, arm);
                if ((point.position - anchor).sqrMagnitude > 1e-6f)
                    point.position = anchor;
                if (road.Width != j.Width || road.SidewalkLeft != j.SidewalkWidth || road.SidewalkRight != j.SidewalkWidth || road.CurbHeight != j.CurbHeight || road.CurbWidth != j.CurbWidth)
                {
                    road.Width = j.Width;
                    road.SidewalkLeft = road.SidewalkRight = j.SidewalkWidth;
                    road.CurbHeight = j.CurbHeight;
                    road.CurbWidth = j.CurbWidth;
                    EditorUtility.SetDirty(road);
                }
            }
            Join(road.StartJunction, road.StartArm, road.transform.GetChild(0));
            Join(road.EndJunction, road.EndArm, road.transform.GetChild(count - 1));
        }

        // ------------------------------------------------------------ junction geometry

        public static Vector3 ArmLocal(int arm) => arm == 0 ? Vector3.forward : arm == 1 ? Vector3.right : arm == 2 ? Vector3.back : Vector3.left;

        public static float Reach(TarkovCityJunction j) => j.Width / 2f + j.SidewalkWidth;

        // where a road joins the arm (the middle of its outer edge), and which way it leaves
        public static Vector3 Anchor(TarkovCityJunction j, int arm) => j.transform.parent.TransformPoint(ArmLocal(arm) * Reach(j));

        public static Vector3 ArmDirection(TarkovCityJunction j, int arm)
        {
            var d = j.transform.parent.TransformDirection(ArmLocal(arm));
            d.y = 0f;
            return d.normalized;
        }

        // the road joined to the arm, if any
        public static TarkovCityRoad RoadAt(TarkovCityJunction j, int arm) =>
            Roads.FirstOrDefault(r => r.StartJunction == j && r.StartArm == arm || r.EndJunction == j && r.EndArm == arm);

        // the nearest arm with no road joined, within `within` metres (or the road's half width) of its anchor
        public static bool NearestFreeArm(Vector3 p, float within, out TarkovCityJunction junction, out int arm)
        {
            junction = null;
            arm = 0;
            float best = float.MaxValue;
            foreach (var j in Junctions)
                for (int a = 0; a < 4; a++)
                {
                    var anchor = Anchor(j, a);
                    float d = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(anchor.x, anchor.z));
                    if (d < Mathf.Max(within, j.Width / 2f) && d < best && RoadAt(j, a) == null)
                    {
                        best = d;
                        junction = j;
                        arm = a;
                    }
                }
            return junction != null;
        }

        public static void Attach(TarkovCityRoad road, bool atStart, TarkovCityJunction j, int arm)
        {
            Undo.RecordObject(road, "Join road");
            Undo.RecordObject(j, "Join road");
            if (atStart)
            {
                road.StartJunction = j;
                road.StartArm = arm;
            }
            else
            {
                road.EndJunction = j;
                road.EndArm = arm;
            }
            j.SetOpen(arm, true);
            EditorUtility.SetDirty(road);
            EditorUtility.SetDirty(j);
            Pin(road);
        }

        public static void Rebuild(TarkovCityJunction j)
        {
            var root = j.transform.parent;
            if (root == null)
                return;
            if (string.IsNullOrEmpty(j.Id))
                j.Id = NewId();
            float w = j.Width / 2f, s = j.SidewalkWidth, h = j.CurbHeight, tex = Tex(j.Kit);
            float[] edges = { -w - s, -w, w, w + s };
            var surface = new TarkovGeneratedAssets.MeshBuilder();
            var sidewalks = new TarkovGeneratedAssets.MeshBuilder();
            var curbs = new TarkovGeneratedAssets.MeshBuilder();
            var markings = new TarkovGeneratedAssets.MeshBuilder();
            // cell (i, k): i along x, k along z; the arm cells are N (1, 2), E (2, 1), S (1, 0), W (0, 1)
            int ArmOf(int i, int k) => i == 1 && k == 2 ? 0 : i == 2 && k == 1 ? 1 : i == 1 && k == 0 ? 2 : i == 0 && k == 1 ? 3 : -1;
            bool Road(int i, int k) => i == 1 && k == 1 || ArmOf(i, k) >= 0 && j.Open(ArmOf(i, k));
            Rect Cell(int i, int k) => Rect.MinMaxRect(edges[i], edges[k], edges[i + 1], edges[k + 1]);
            float hw = WalkHeight(h), kw = Mathf.Clamp(j.CurbWidth, 0.02f, Mathf.Max(0.02f, s - 0.05f));
            var profile = KerbProfile(h, kw);
            // -1 outside the grid, 1 road, 0 sidewalk
            int Kind(int i, int k) => i < 0 || i > 2 || k < 0 || k > 2 ? -1 : Road(i, k) ? 1 : 0;
            for (int i = 0; i < 3; i++)
                for (int k = 0; k < 3; k++)
                {
                    var c = Cell(i, k);
                    if (Road(i, k))
                    {
                        TopRect(surface, c, 0f, tex);
                        continue;
                    }
                    float l = c.xMin, r = c.xMax, bottom = c.yMin, top = c.yMax;
                    foreach (var (di, dk) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int ni = i + di, nk = k + dk;
                        var n = new Vector3(di, 0f, dk);
                        if (Kind(ni, nk) == 1)
                        {
                            // a kerb along the edge facing the road, the sidewalk cell giving up its width
                            Vector3 a, b;
                            if (di != 0)
                            {
                                float x = di > 0 ? c.xMax : c.xMin;
                                a = new Vector3(x, 0f, c.yMin);
                                b = new Vector3(x, 0f, c.yMax);
                            }
                            else
                            {
                                float z = dk > 0 ? c.yMax : c.yMin;
                                a = new Vector3(c.xMin, 0f, z);
                                b = new Vector3(c.xMax, 0f, z);
                            }
                            // each end: square where the kerb carries on, cut back at the sidewalk's outer corner, run on
                            // round the road's corner
                            float Mitre(int ei, int ek)
                            {
                                int beyond = Kind(i + ei, k + ek);
                                if (beyond == -1)
                                    return 0f;
                                if (beyond == 1)
                                    return -1f;
                                return Kind(ni + ei, nk + ek) == 1 ? 0f : 1f;
                            }
                            float mitreA = di != 0 ? Mitre(0, -1) : Mitre(-1, 0), mitreB = di != 0 ? Mitre(0, 1) : Mitre(1, 0);
                            float va = (di != 0 ? a.z : a.x) / tex, vb = (di != 0 ? b.z : b.x) / tex;
                            KerbSection(curbs, a, b, -n, -n, Vector3.up, profile, mitreA, mitreB, va, vb, tex);
                            if (di > 0) r -= kw;
                            if (di < 0) l += kw;
                            if (dk > 0) top -= kw;
                            if (dk < 0) bottom += kw;
                            continue;
                        }
                        if (Kind(ni, nk) == 0)
                            continue;
                        // the outside: a skirt down into the ground, except where a corner carries on into an open
                        // arm's road sidewalk
                        int arm = dk > 0 ? 0 : di > 0 ? 1 : dk < 0 ? 2 : 3;
                        if (i != 1 && k != 1 && j.Open(arm))
                            continue;
                        var mid = new Vector3(di > 0 ? c.xMax : di < 0 ? c.xMin : c.center.x, 0f, dk > 0 ? c.yMax : dk < 0 ? c.yMin : c.center.y);
                        var half = new Vector3(dk != 0 ? 1f : 0f, 0f, di != 0 ? 1f : 0f) * ((di != 0 ? c.height : c.width) / 2f);
                        Wall(sidewalks, mid - half, mid + half, -Skirt, hw, n, tex);
                    }
                    TopRect(sidewalks, Rect.MinMaxRect(l, bottom, r, top), hw, tex);
                }
            if (j.Crossings)
                for (int arm = 0; arm < 4; arm++)
                {
                    if (!j.Open(arm))
                        continue;
                    var c = Cell(arm == 1 ? 2 : arm == 3 ? 0 : 1, arm == 0 ? 2 : arm == 2 ? 0 : 1);
                    bool alongZ = arm == 0 || arm == 2;
                    float from = alongZ ? c.xMin : c.yMin, to = alongZ ? c.xMax : c.yMax;
                    for (float x = from + 0.5f; x + 0.5f <= to - 0.4f; x += 1f)
                        TopRect(markings, alongZ ? Rect.MinMaxRect(x, c.yMin + 0.25f, x + 0.5f, c.yMax - 0.25f) : Rect.MinMaxRect(c.xMin + 0.25f, x, c.xMax - 0.25f, x + 0.5f), 0.01f, 1f);
                }
            BuildParts(root, j.Kit, "Junction_" + j.Id, surface, sidewalks, curbs, markings);
            int hash = Hash(j);
            Built[j.GetInstanceID()] = hash;
            j.BuiltHash = hash;
            EditorUtility.SetDirty(j);
        }

        // ------------------------------------------------------------ road geometry

        public struct Frame
        {
            public Vector3 Position;
            public Vector3 Forward;
            public Vector3 Right;
            // the cross-section's direction: Right, banked near a joined junction to match its tilt
            public Vector3 Across;
            public float Distance;
            // how far the road is raised here, and the point it passes (else -1)
            public float Raise;
            public int Point;
        }

        public static List<Vector3> Points(TarkovCityRoad road) => road.transform.Cast<Transform>().Select(t => t.position).ToList();

        // the road's cross-sections in world space, cached until it changes
        public static List<Frame> Frames(TarkovCityRoad road)
        {
            int hash = Hash(road);
            if (FrameCache.TryGetValue(road.GetInstanceID(), out var cached) && cached.Hash == hash)
                return cached.Frames;
            var frames = BuildFrames(road);
            FrameCache[road.GetInstanceID()] = (hash, frames);
            return frames;
        }

        private static bool IsLoop(TarkovCityRoad road, int count) => road.Loop && count > 2 && road.StartJunction == null && road.EndJunction == null;

        public static TarkovCityRoadPoint.Modes ModeOf(Transform point)
        {
            var data = point.GetComponent<TarkovCityRoadPoint>();
            return data != null ? data.Mode : TarkovCityRoadPoint.Modes.Smooth;
        }

        private static float RaiseOf(Transform point)
        {
            var data = point.GetComponent<TarkovCityRoadPoint>();
            return data != null ? data.Raise : 0f;
        }

        // each point's Bezier handles, as world offsets from it: the point's own (Aligned, Free), worked out from the
        // neighbours (Smooth: Catmull-Rom; Corner: a third of the way to each neighbour, so both sides run straight),
        // and along the arm at an end joined to a junction
        public static List<(Vector3 In, Vector3 Out)> BezierHandles(TarkovCityRoad road)
        {
            var pts = Points(road);
            int n = pts.Count;
            var handles = new List<(Vector3, Vector3)>();
            if (n < 2)
                return handles;
            bool loop = IsLoop(road, n);
            Vector3 P(int i) => loop ? pts[(i % n + n) % n] : pts[Mathf.Clamp(i, 0, n - 1)];
            var root = road.transform.parent;
            for (int i = 0; i < n; i++)
            {
                var point = road.transform.GetChild(i);
                var data = point.GetComponent<TarkovCityRoadPoint>();
                bool first = !loop && i == 0, last = !loop && i == n - 1;
                Vector3 inHandle, outHandle;
                switch (data != null ? data.Mode : TarkovCityRoadPoint.Modes.Smooth)
                {
                    case TarkovCityRoadPoint.Modes.Aligned:
                    case TarkovCityRoadPoint.Modes.Free:
                        inHandle = root.TransformVector(data.In);
                        outHandle = root.TransformVector(data.Out);
                        break;
                    case TarkovCityRoadPoint.Modes.Corner:
                        inHandle = first ? Vector3.zero : (P(i - 1) - P(i)) / 3f;
                        outHandle = last ? Vector3.zero : (P(i + 1) - P(i)) / 3f;
                        break;
                    default:
                        var m = first ? P(1) - P(0) : last ? P(n - 1) - P(n - 2) : (P(i + 1) - P(i - 1)) * 0.5f;
                        inHandle = -m / 3f;
                        outHandle = m / 3f;
                        break;
                }
                if (first && road.StartJunction != null)
                    outHandle = ArmDirection(road.StartJunction, road.StartArm) * (Vector3.Distance(pts[0], pts[1]) / 3f);
                if (last && road.EndJunction != null)
                    inHandle = ArmDirection(road.EndJunction, road.EndArm) * (Vector3.Distance(pts[n - 1], pts[n - 2]) / 3f);
                handles.Add((inHandle, outHandle));
            }
            return handles;
        }

        private static List<Frame> BuildFrames(TarkovCityRoad road)
        {
            var pts = Points(road);
            var frames = new List<Frame>();
            if (pts.Count < 2)
                return frames;
            int n = pts.Count;
            bool loop = IsLoop(road, n);
            var handles = BezierHandles(road);
            var raises = road.transform.Cast<Transform>().Select(RaiseOf).ToList();
            var line = new List<Vector3>();
            var raise = new List<float>();
            var pointAt = new List<int>();
            int segments = loop ? n : n - 1;
            for (int s = 0; s < segments; s++)
            {
                int e = (s + 1) % n;
                Vector3 a = pts[s], b = pts[e], c1 = a + handles[s].Out, c2 = b + handles[e].In;
                float span = Vector3.Distance(a, c1) + Vector3.Distance(c1, c2) + Vector3.Distance(c2, b);
                int steps = Mathf.Max(1, Mathf.CeilToInt(span / Mathf.Max(0.25f, road.Step)));
                for (int k = 0; k < steps; k++)
                {
                    float t = k / (float)steps, u = 1f - t;
                    line.Add(u * u * u * a + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * b);
                    raise.Add(Mathf.Lerp(raises[s], raises[e], Mathf.SmoothStep(0f, 1f, t)));
                    pointAt.Add(k == 0 ? s : -1);
                }
            }
            line.Add(loop ? pts[0] : pts[n - 1]);
            raise.Add(loop ? raises[0] : raises[n - 1]);
            pointAt.Add(loop ? -1 : n - 1);

            // heights from the ground under each cross-section (smoothed) or the curve, plus the points' raise, eased
            // into joined junctions
            var heights = line.Select(p => p.y).ToList();
            if (road.FollowGround)
            {
                Physics.SyncTransforms();
                heights = line.Select(p => Ground(new Ray(p + Vector3.up * 60f, Vector3.down), 200f, false, out var hit) ? hit.point.y + road.GroundOffset : p.y).ToList();
                // a loop's last sample is its first again: smoothed round the ring of the others, then closed
                int ring = loop ? heights.Count - 1 : heights.Count;
                for (int pass = 0; pass < 2; pass++)
                {
                    var smooth = new List<float>(heights);
                    for (int i = 0; i < ring; i++)
                    {
                        float sum = 0f;
                        for (int k = -2; k <= 2; k++)
                            sum += heights[loop ? ((i + k) % ring + ring) % ring : Mathf.Clamp(i + k, 0, ring - 1)];
                        smooth[i] = sum / 5f;
                    }
                    if (loop)
                        smooth[ring] = smooth[0];
                    heights = smooth;
                }
            }
            for (int i = 0; i < line.Count; i++)
                line[i] = new Vector3(line[i].x, heights[i] + raise[i], line[i].z);
            float total = 0f;
            var along = new List<float> { 0f };
            for (int i = 1; i < line.Count; i++)
                along.Add(total += Vector3.Distance(line[i - 1], line[i]));
            for (int i = 0; i < line.Count; i++)
            {
                var p = line[i];
                if (road.StartJunction != null)
                    p.y = Mathf.Lerp(pts[0].y, p.y, Mathf.SmoothStep(0f, 1f, along[i] / 8f));
                if (road.EndJunction != null)
                    p.y = Mathf.Lerp(pts[n - 1].y, p.y, Mathf.SmoothStep(0f, 1f, (total - along[i]) / 8f));
                line[i] = p;
            }

            float distance = 0f;
            for (int i = 0; i < line.Count; i++)
            {
                if (i > 0)
                    distance += Vector3.Distance(line[i - 1], line[i]);
                var forward = (i == 0 && loop ? line[1] - line[line.Count - 2] : i == line.Count - 1 && loop ? line[1] - line[i - 1] : line[Mathf.Min(i + 1, line.Count - 1)] - line[Mathf.Max(i - 1, 0)]);
                // joined ends leave exactly along the arm, so they meet the junction square
                if (i == 0 && road.StartJunction != null)
                    forward = ArmDirection(road.StartJunction, road.StartArm);
                if (i == line.Count - 1 && road.EndJunction != null)
                    forward = -ArmDirection(road.EndJunction, road.EndArm);
                forward.y = 0f;
                if (forward.sqrMagnitude < 1e-8f)
                    forward = frames.Count > 0 ? frames[frames.Count - 1].Forward : Vector3.forward;
                forward.Normalize();
                var right = Vector3.Cross(Vector3.up, forward);
                frames.Add(new Frame { Position = line[i], Forward = forward, Right = right, Across = right, Distance = distance, Raise = raise[i], Point = pointAt[i] });
            }
            // banked into a tilted junction, so the cross-section meets its arm edge, level again 8 m on
            float length = distance;
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frames[i];
                if (road.StartJunction != null)
                    f.Across = Vector3.Slerp(JunctionAcross(road.StartJunction, road.StartArm), f.Across, Mathf.SmoothStep(0f, 1f, f.Distance / 8f));
                if (road.EndJunction != null)
                    f.Across = Vector3.Slerp(-JunctionAcross(road.EndJunction, road.EndArm), f.Across, Mathf.SmoothStep(0f, 1f, (length - f.Distance) / 8f));
                f.Across.Normalize();
                frames[i] = f;
            }
            return frames;
        }

        // the arm's outer edge direction (to the right of a road leaving along the arm), tilted with the junction
        private static Vector3 JunctionAcross(TarkovCityJunction j, int arm) =>
            j.transform.parent.TransformDirection(Vector3.Cross(Vector3.up, ArmLocal(arm))).normalized;

        public static float Length(List<Frame> frames) => frames.Count > 0 ? frames[frames.Count - 1].Distance : 0f;

        // the cross-section at a distance along the road (between two samples)
        public static Frame At(List<Frame> frames, float distance)
        {
            if (distance <= 0f)
                return frames[0];
            if (distance >= Length(frames))
                return frames[frames.Count - 1];
            int lo = 0, hi = frames.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (frames[mid].Distance <= distance)
                    lo = mid;
                else
                    hi = mid;
            }
            var a = frames[lo];
            var b = frames[hi];
            float t = Mathf.InverseLerp(a.Distance, b.Distance, distance);
            var forward = Vector3.Lerp(a.Forward, b.Forward, t).normalized;
            return new Frame
            {
                Position = Vector3.Lerp(a.Position, b.Position, t), Forward = forward, Right = Vector3.Cross(Vector3.up, forward),
                Across = Vector3.Lerp(a.Across, b.Across, t).normalized, Distance = distance, Raise = Mathf.Lerp(a.Raise, b.Raise, t), Point = -1,
            };
        }

        // the cross-section nearest a point, and how far it is (flat)
        public static Frame Nearest(List<Frame> frames, Vector3 p, out float distance)
        {
            var flat = new Vector2(p.x, p.z);
            float best = float.MaxValue;
            Frame found = default;
            for (int i = 0; i + 1 < frames.Count; i++)
            {
                var a = new Vector2(frames[i].Position.x, frames[i].Position.z);
                var b = new Vector2(frames[i + 1].Position.x, frames[i + 1].Position.z);
                var ab = b - a;
                float t = ab.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector2.Dot(flat - a, ab) / ab.sqrMagnitude) : 0f;
                float d = (a + ab * t - flat).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    found = At(frames, Mathf.Lerp(frames[i].Distance, frames[i + 1].Distance, t));
                }
            }
            distance = Mathf.Sqrt(best);
            return found;
        }

        public static void Rebuild(TarkovCityRoad road)
        {
            var root = road.transform.parent;
            if (root == null)
                return;
            if (string.IsNullOrEmpty(road.Id))
                road.Id = NewId();
            Pin(road);
            var frames = Frames(road);
            var surface = new TarkovGeneratedAssets.MeshBuilder();
            var sidewalks = new TarkovGeneratedAssets.MeshBuilder();
            var curbs = new TarkovGeneratedAssets.MeshBuilder();
            var markings = new TarkovGeneratedAssets.MeshBuilder();
            if (frames.Count >= 2)
            {
                float w = road.Width / 2f, h = road.CurbHeight, hw = WalkHeight(h), tex = Tex(road.Kit);
                var toLocal = root.worldToLocalMatrix;
                Vector3 L(Vector3 world) => toLocal.MultiplyPoint3x4(world);
                Vector3 D(Vector3 world) => toLocal.MultiplyVector(world);
                var up = D(Vector3.up).normalized;
                for (int i = 0; i + 1 < frames.Count; i++)
                {
                    var a = frames[i];
                    var b = frames[i + 1];
                    Vector3 pa = L(a.Position), pb = L(b.Position), ra = D(a.Across), rb = D(b.Across);
                    float va = a.Distance / tex, vb = b.Distance / tex;
                    Quad(surface, pa - ra * w, pb - rb * w, pb + rb * w, pa + ra * w, up,
                        new Vector2(0f, va), new Vector2(0f, vb), new Vector2(road.Width / tex, vb), new Vector2(road.Width / tex, va));
                    foreach (var side in new[] { -1f, 1f })
                    {
                        float walk = side > 0f ? road.SidewalkRight : road.SidewalkLeft;
                        // skirts reach further down where the road is raised; a side without a sidewalk gets one from the road's edge
                        float da = Skirt + Mathf.Max(0f, a.Raise), db = Skirt + Mathf.Max(0f, b.Raise);
                        var outward = ((ra + rb) * (0.5f * side)).normalized;
                        Vector3 ea = pa + ra * (side * w), eb = pb + rb * (side * w);
                        if (walk <= 0f)
                        {
                            Quad(surface, ea - up * da, ea, eb, eb - up * db, outward,
                                new Vector2(va, 0f), new Vector2(va, da / tex), new Vector2(vb, db / tex), new Vector2(vb, 0f));
                            continue;
                        }
                        float kw = Mathf.Clamp(road.CurbWidth, 0.02f, Mathf.Max(0.02f, walk - 0.05f));
                        KerbSection(curbs, ea, eb, ra * side, rb * side, up, KerbProfile(h, kw), 0f, 0f, va, vb, tex);
                        Vector3 ia = pa + ra * (side * (w + kw)), ib = pb + rb * (side * (w + kw));
                        Vector3 oa = pa + ra * (side * (w + walk)), ob = pb + rb * (side * (w + walk));
                        Quad(sidewalks, ia + up * hw, ib + up * hw, ob + up * hw, oa + up * hw, up,
                            new Vector2(0f, va), new Vector2(0f, vb), new Vector2((walk - kw) / tex, vb), new Vector2((walk - kw) / tex, va));
                        Quad(sidewalks, oa - up * da, oa + up * hw, ob + up * hw, ob - up * db, outward,
                            new Vector2(va, 0f), new Vector2(va, (hw + da) / tex), new Vector2(vb, (hw + db) / tex), new Vector2(vb, 0f));
                    }
                }
                // a free end with sidewalks gets their end faces
                foreach (var (frame, end, joined) in new[] { (frames[0], -1f, road.StartJunction != null), (frames[frames.Count - 1], 1f, road.EndJunction != null) })
                {
                    if (joined || road.Loop && frames.Count > 2 && road.StartJunction == null && road.EndJunction == null)
                        continue;
                    Vector3 p = L(frame.Position), r = D(frame.Across), f = D(frame.Forward) * end;
                    foreach (var side in new[] { -1f, 1f })
                    {
                        float walk = side > 0f ? road.SidewalkRight : road.SidewalkLeft;
                        if (walk <= 0f)
                            continue;
                        float kw = Mathf.Clamp(road.CurbWidth, 0.02f, Mathf.Max(0.02f, walk - 0.05f));
                        KerbCap(curbs, p + r * (side * w), r * side, up, KerbProfile(h, kw), f, tex);
                        Vector3 i0 = p + r * (side * (w + kw)), o0 = p + r * (side * (w + walk));
                        Quad(sidewalks, i0 - up * Skirt, i0 + up * hw, o0 + up * hw, o0 - up * Skirt, f,
                            Vector2.zero, new Vector2(0f, (hw + Skirt) / tex), new Vector2((walk - kw) / tex, (hw + Skirt) / tex), new Vector2((walk - kw) / tex, 0f));
                    }
                }
                if (road.LaneLines)
                {
                    float length = Length(frames), from = 1f, to = length - 1f;
                    if (road.Avenue)
                    {
                        Line(markings, frames, toLocal, from, to, -0.25f, -0.1f);
                        Line(markings, frames, toLocal, from, to, 0.1f, 0.25f);
                    }
                    else
                    {
                        for (float d = from; d < to; d += 8f)
                            Line(markings, frames, toLocal, d, Mathf.Min(d + 3f, to), -0.075f, 0.075f);
                    }
                }
            }
            BuildParts(root, road.Kit, "Road_" + road.Id, surface, sidewalks, curbs, markings);
            int hash = Hash(road);
            Built[road.GetInstanceID()] = hash;
            road.BuiltHash = hash;
            EditorUtility.SetDirty(road);
        }

        // a painted strip from one distance to another, `fromOffset`..`toOffset` metres right of the centre
        private static void Line(TarkovGeneratedAssets.MeshBuilder b, List<Frame> frames, Matrix4x4 toLocal, float from, float to, float fromOffset, float toOffset)
        {
            if (to <= from)
                return;
            var cuts = new List<Frame> { At(frames, from) };
            cuts.AddRange(frames.Where(f => f.Distance > from && f.Distance < to));
            cuts.Add(At(frames, to));
            var up = toLocal.MultiplyVector(Vector3.up).normalized;
            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                Vector3 pa = toLocal.MultiplyPoint3x4(cuts[i].Position) + up * 0.01f, pb = toLocal.MultiplyPoint3x4(cuts[i + 1].Position) + up * 0.01f;
                Vector3 ra = toLocal.MultiplyVector(cuts[i].Across), rb = toLocal.MultiplyVector(cuts[i + 1].Across);
                Quad(b, pa + ra * fromOffset, pb + rb * fromOffset, pb + rb * toOffset, pa + ra * toOffset, up, Vector2.zero, Vector2.up, Vector2.one, Vector2.right);
            }
        }

        // ------------------------------------------------------------ meshes

        private static float Tex(TarkovCityKit kit) => kit != null ? Mathf.Max(0.25f, kit.TextureMetres) : 4f;

        private static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 8);

        // kerbstones: a strip CurbWidth wide between the road and the sidewalk, its road edge bevelled, its top at the
        // kerb height and the sidewalk Lip below it, so it reads as its own stone
        public const float Lip = 0.02f;
        private const float Bevel = 0.03f;

        // the height of sidewalks (and what stands on them) for a kerb height
        public static float WalkHeight(float curb) => Mathf.Max(0f, curb - Lip);

        // the kerb's cross-section, from the road edge (x across towards the sidewalk, y up)
        internal static Vector2[] KerbProfile(float height, float width)
        {
            float bevel = Mathf.Min(Bevel, height * 0.4f, width * 0.4f), lip = Mathf.Min(Lip, height);
            var profile = new List<Vector2> { Vector2.zero };
            if (bevel > 0.001f)
            {
                profile.Add(new Vector2(0f, height - bevel));
                profile.Add(new Vector2(bevel, height));
            }
            else
                profile.Add(new Vector2(0f, height));
            profile.Add(new Vector2(width, height));
            if (lip > 0.001f)
                profile.Add(new Vector2(width, height - lip));
            return profile.ToArray();
        }

        // a stretch of kerb from road-edge point a to b (`outA`/`outB` across towards the sidewalk at each). At an end a
        // mitre of -1 cuts the kerb back by its depth across (the outside of a sidewalk corner), +1 runs it on (round a
        // road corner), 0 ends it square (it carries on in the next stretch).
        internal static void KerbSection(TarkovGeneratedAssets.MeshBuilder b, Vector3 a, Vector3 bEnd, Vector3 outA, Vector3 outB, Vector3 up,
            Vector2[] profile, float mitreA, float mitreB, float va, float vb, float tex)
        {
            var along = (bEnd - a).normalized;
            float arc = 0f;
            for (int k = 0; k + 1 < profile.Length; k++)
            {
                Vector2 p0 = profile[k], p1 = profile[k + 1];
                float length = (p1 - p0).magnitude;
                Vector3 A(Vector2 p) => a + outA * p.x + up * p.y - along * (mitreA * p.x);
                Vector3 B(Vector2 p) => bEnd + outB * p.x + up * p.y + along * (mitreB * p.x);
                var d = p1 - p0;
                var n = new Vector2(-d.y, d.x).normalized;
                var normal = ((outA + outB) * 0.5f).normalized * n.x + up * n.y;
                Quad(b, A(p0), A(p1), B(p1), B(p0), normal,
                    new Vector2(va, arc / tex), new Vector2(va, (arc + length) / tex), new Vector2(vb, (arc + length) / tex), new Vector2(vb, arc / tex));
                arc += length;
            }
        }

        // the kerb's end face at a free end, down to the road
        internal static void KerbCap(TarkovGeneratedAssets.MeshBuilder b, Vector3 at, Vector3 outward, Vector3 up, Vector2[] profile, Vector3 normal, float tex)
        {
            var points = profile.ToList();
            if (points[points.Count - 1].y > 0.001f)
                points.Add(new Vector2(points[points.Count - 1].x, 0f));
            Vector3 P(Vector2 p) => at + outward * p.x + up * p.y;
            for (int k = 1; k + 1 < points.Count; k++)
            {
                Vector3 p0 = P(points[0]), p1 = P(points[k]), p2 = P(points[k + 1]);
                int i0 = b.Add(p0, normal, points[0] / tex), i1 = b.Add(p1, normal, points[k] / tex), i2 = b.Add(p2, normal, points[k + 1] / tex);
                if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), normal) >= 0f)
                    b.Triangle(i0, i1, i2);
                else
                    b.Triangle(i0, i2, i1);
            }
        }

        // a quad over the four corners in order, facing `normal` whichever way round they were given
        internal static void Quad(TarkovGeneratedAssets.MeshBuilder b, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 normal,
            Vector2 u0, Vector2 u1, Vector2 u2, Vector2 u3)
        {
            int a = b.Add(p0, normal, u0), c1 = b.Add(p1, normal, u1), c2 = b.Add(p2, normal, u2), c3 = b.Add(p3, normal, u3);
            if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), normal) >= 0f)
            {
                b.Triangle(a, c1, c2);
                b.Triangle(a, c2, c3);
            }
            else
            {
                b.Triangle(a, c2, c1);
                b.Triangle(a, c3, c2);
            }
        }

        internal static void TopRect(TarkovGeneratedAssets.MeshBuilder b, Rect r, float y, float tex)
        {
            if (r.width <= 0.001f || r.height <= 0.001f)
                return;
            Vector3 V(float x, float z) => new Vector3(x, y, z);
            Vector2 U(float x, float z) => new Vector2(x / tex, z / tex);
            Quad(b, V(r.xMin, r.yMin), V(r.xMin, r.yMax), V(r.xMax, r.yMax), V(r.xMax, r.yMin), Vector3.up,
                U(r.xMin, r.yMin), U(r.xMin, r.yMax), U(r.xMax, r.yMax), U(r.xMax, r.yMin));
        }

        private static void Wall(TarkovGeneratedAssets.MeshBuilder b, Vector3 p, Vector3 q, float bottom, float top, Vector3 normal, float tex)
        {
            float length = Vector3.Distance(p, q) / tex;
            Quad(b, p + Vector3.up * bottom, p + Vector3.up * top, q + Vector3.up * top, q + Vector3.up * bottom, normal,
                new Vector2(0f, bottom / tex), new Vector2(0f, top / tex), new Vector2(length, top / tex), new Vector2(length, bottom / tex));
        }

        private static void BuildParts(Transform root, TarkovCityKit kit, string prefix, TarkovGeneratedAssets.MeshBuilder surface,
            TarkovGeneratedAssets.MeshBuilder sidewalks, TarkovGeneratedAssets.MeshBuilder curbs, TarkovGeneratedAssets.MeshBuilder markings)
        {
            string folder = MeshFolder(root.gameObject.scene);
            string Path(string part) => folder != null ? $"{folder}/{prefix}_{part}.asset" : null;
            Part(root, "Surface", surface, TarkovCityBuilder.KitMaterial(kit, "Road"), (MaterialType.Asphalt, BaseBallistic.ESurfaceSound.Asphalt), Path("Surface"));
            Part(root, "Sidewalks", sidewalks, TarkovCityBuilder.KitMaterial(kit, "Sidewalk"), (MaterialType.Concrete, BaseBallistic.ESurfaceSound.Concrete), Path("Sidewalks"));
            Part(root, "Curbs", curbs, TarkovCityBuilder.KitMaterial(kit, "Curb"), (MaterialType.Concrete, BaseBallistic.ESurfaceSound.Concrete), Path("Curbs"));
            Part(root, "Markings", markings, TarkovCityBuilder.KitMaterial(kit, "Markings"), null, Path("Markings"));
        }

        // `<scene folder>/<scene>_City`, or null for a scene not saved yet (its meshes then live in the scene)
        public static string MeshFolder(Scene scene) =>
            string.IsNullOrEmpty(scene.path) ? null : System.IO.Path.GetDirectoryName(scene.path).Replace('\\', '/') + "/" + scene.name + "_City";

        // the named child with the mesh, its material and (with a surface) a collider on the Terrain layer; an empty
        // mesh removes the child. The mesh asset is rewritten in place, so its GUID stays.
        internal static void Part(Transform parent, string name, TarkovGeneratedAssets.MeshBuilder b, Material material,
            (MaterialType Type, BaseBallistic.ESurfaceSound Sound)? surface, string meshPath)
        {
            var go = parent.Find(name)?.gameObject;
            if (b.Vertices.Count == 0)
            {
                if (go != null)
                    Object.DestroyImmediate(go);
                return;
            }
            var mesh = b.Build(meshPath != null ? System.IO.Path.GetFileNameWithoutExtension(meshPath) : name);
            if (meshPath != null)
                mesh = TarkovGeneratedAssets.SaveMesh(meshPath, mesh);
            if (go == null)
            {
                go = new GameObject(name);
                go.transform.SetParent(parent, false);
            }
            var filter = Ensure<MeshFilter>(go);
            var old = filter.sharedMesh;
            filter.sharedMesh = mesh;
            if (old != null && old != mesh && !EditorUtility.IsPersistent(old))
                Object.DestroyImmediate(old);
            var renderer = Ensure<MeshRenderer>(go);
            renderer.sharedMaterial = material;
            if (surface == null)
            {
                // markings: drawn only, a centimetre over the road
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
#pragma warning disable 618
                GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);
#pragma warning restore 618
                return;
            }
            var collider = Ensure<MeshCollider>(go);
            // the same mesh asset with new contents: the collider only sees them when set again
            collider.sharedMesh = null;
            collider.sharedMesh = mesh;
            TarkovSceneTools.MakeStaticGeometry(go, surface.Value.Type, surface.Value.Sound);
        }

        // a missing component is Unity's fake null from GetComponent, which the ?? operator doesn't see
        private static T Ensure<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }

        // ------------------------------------------------------------ ground

        // the first solid surface along the ray; with `roads` off, hand-made roads and junctions are looked through
        public static bool Ground(Ray ray, float max, bool roads, out RaycastHit hit)
        {
            foreach (var h in Physics.RaycastAll(ray, max, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore).OrderBy(x => x.distance))
            {
                if (!roads && IsRoadPart(h.collider.transform))
                    continue;
                if (h.collider.CompareTag("EditorOnly"))
                    continue;
                hit = h;
                return true;
            }
            hit = default;
            return false;
        }

        public static bool IsRoadPart(Transform t)
        {
            for (var p = t; p != null; p = p.parent)
            {
                var settings = p.Find("SDK_Road") ?? p.Find("SDK_Junction");
                if (settings != null && (settings.GetComponent<TarkovCityRoad>() != null || settings.GetComponent<TarkovCityJunction>() != null))
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------ creating

        public static TarkovCityRoad CreateRoad(Vector3 first, TarkovCityRoad like)
        {
            var root = new GameObject("Road");
            root.transform.position = first;
            var road = TarkovMapEvents.EditorOnlyChild(root.transform, "SDK_Road").AddComponent<TarkovCityRoad>();
            if (like != null)
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(like), road);
            road.StartJunction = road.EndJunction = null;
            road.Id = NewId();
            road.BuiltHash = 0;
            Undo.RegisterCreatedObjectUndo(root, "Create road");
            AddPoint(road, first, -1, false);
            Forget();
            return road;
        }

        public static Transform AddPoint(TarkovCityRoad road, Vector3 world, int index, bool undo = true)
        {
            var point = TarkovMapEvents.EditorOnlyChild(road.transform, "Point");
            point.transform.position = world;
            point.AddComponent<TarkovCityRoadPoint>();
            if (index >= 0)
                point.transform.SetSiblingIndex(index);
            Rename(road);
            if (undo)
                Undo.RegisterCreatedObjectUndo(point, "Add road point");
            return point.transform;
        }

        public static TarkovCityJunction CreateJunction(Vector3 position, float yaw, TarkovCityJunction like)
        {
            var root = new GameObject("Junction");
            root.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            var j = TarkovMapEvents.EditorOnlyChild(root.transform, "SDK_Junction").AddComponent<TarkovCityJunction>();
            if (like != null)
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(like), j);
            j.Id = NewId();
            j.BuiltHash = 0;
            Undo.RegisterCreatedObjectUndo(root, "Create junction");
            Rebuild(j);
            Forget();
            return j;
        }

        // tilts and raises the junction to the ground under it (a plane through its corners, Newell's normal), keeping
        // its heading; the roads joined to it follow. False when the ground isn't found under three corners.
        public static bool FitToGround(TarkovCityJunction j)
        {
            var root = j.transform.parent;
            float r = Reach(j);
            var forward = root.forward;
            forward.y = 0f;
            var heading = Quaternion.LookRotation(forward.sqrMagnitude > 1e-6f ? forward : Vector3.forward);
            Physics.SyncTransforms();
            var corners = new List<Vector3>();
            foreach (var c in new[] { new Vector3(-r, 0f, -r), new Vector3(-r, 0f, r), new Vector3(r, 0f, r), new Vector3(r, 0f, -r) })
                if (Ground(new Ray(root.position + heading * c + Vector3.up * 60f, Vector3.down), 200f, false, out var hit))
                    corners.Add(hit.point);
            if (corners.Count < 3)
                return false;
            var normal = Vector3.zero;
            for (int i = 0; i < corners.Count; i++)
            {
                var a = corners[i];
                var b = corners[(i + 1) % corners.Count];
                normal += new Vector3((a.y - b.y) * (a.z + b.z), (a.z - b.z) * (a.x + b.x), (a.x - b.x) * (a.y + b.y));
            }
            if (normal.y < 0f)
                normal = -normal;
            normal.Normalize();
            var centroid = corners.Aggregate(Vector3.zero, (sum, c) => sum + c) / corners.Count;
            // the height of that plane under the junction's own centre
            float y = centroid.y - (normal.x * (root.position.x - centroid.x) + normal.z * (root.position.z - centroid.z)) / Mathf.Max(0.1f, normal.y);
            Undo.RecordObject(root, "Fit junction to ground");
            root.SetPositionAndRotation(new Vector3(root.position.x, y + 0.03f, root.position.z), Quaternion.FromToRotation(Vector3.up, normal) * heading);
            foreach (var road in Roads.Where(x => x.StartJunction == j || x.EndJunction == j))
                Pin(road);
            return true;
        }

        // the road (or junction) an object belongs to: its root, its settings, a point or a built part
        public static TarkovCityRoad RoadOf(Transform t)
        {
            for (var p = t; p != null; p = p.parent)
            {
                var road = p.GetComponent<TarkovCityRoad>();
                if (road != null)
                    return road;
                var settings = p.Find("SDK_Road");
                road = settings != null ? settings.GetComponent<TarkovCityRoad>() : null;
                if (road != null)
                    return road;
            }
            return null;
        }

        public static TarkovCityJunction JunctionOf(Transform t)
        {
            for (var p = t; p != null; p = p.parent)
            {
                var j = p.GetComponent<TarkovCityJunction>();
                if (j != null)
                    return j;
                var settings = p.Find("SDK_Junction");
                j = settings != null ? settings.GetComponent<TarkovCityJunction>() : null;
                if (j != null)
                    return j;
            }
            return null;
        }

        // ------------------------------------------------------------ roadside

        // what a kit list's pieces are, for where they go
        public enum Kind
        {
            Building,
            Lamp,
            Tree,
            Prop,
            Vehicle,
        }

        public static Kind KindOf(string list) =>
            list == "Buildings" ? Kind.Building : list == "Lamps" ? Kind.Lamp : list == "Trees" ? Kind.Tree : list == "Vehicles" ? Kind.Vehicle : Kind.Prop;

        public struct Pose
        {
            public Vector3 Position;
            public float Yaw;
            // the footprint's corners (x, z), front edge first
            public Vector2[] Corners;
        }

        private static float Walk(TarkovCityRoad road, int side) => side > 0 ? road.SidewalkRight : road.SidewalkLeft;

        // where a piece goes beside the road at a distance along it, on a side (+1 right, -1 left, looking along it):
        // a building behind the sidewalk with its front on the sidewalk's edge, a lamp or tree at the kerb, a prop near
        // the buildings, a car parked at the kerb driving on the right; centred on `along`
        public static Pose RoadsidePose(TarkovCityRoad road, List<Frame> frames, float along, int side, TarkovCityKit.Piece piece, Kind kind, float setback = 0f)
        {
            var f = At(frames, along);
            float w = road.Width / 2f, walk = Walk(road, side);
            var toRoad = -f.Right * side;
            var local = TarkovCityBuilder.LocalBounds(piece.Prefab);
            // the footprint with the piece's front turned to +z
            var rect = TarkovCityBuilder.FootprintOf(local, -(int)piece.Front * 90f);
            Vector3 facing = kind == Kind.Vehicle ? f.Forward * side : toRoad;
            var x = Vector3.Cross(Vector3.up, facing);
            float yaw = Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg - (int)piece.Front * 90f;
            Vector3 anchor;
            switch (kind)
            {
                case Kind.Building:
                    // the front edge on the sidewalk's outer edge (plus the setback), the middle at `along`
                    anchor = f.Position + f.Right * (side * (w + walk + setback)) + Vector3.up * WalkHeight(road.CurbHeight);
                    anchor -= x * rect.center.x + facing * rect.yMax;
                    break;
                case Kind.Vehicle:
                    anchor = f.Position + f.Right * (side * (w - 0.3f - rect.width / 2f));
                    anchor -= x * rect.center.x + facing * rect.center.y;
                    break;
                default:
                    float inset = kind == Kind.Lamp ? Mathf.Min(0.6f, walk / 2f) : kind == Kind.Tree ? Mathf.Min(1.2f, walk / 2f) : Mathf.Max(0.6f, walk - 0.7f);
                    anchor = f.Position + f.Right * (side * (w + inset)) + Vector3.up * WalkHeight(road.CurbHeight);
                    break;
            }
            anchor += Vector3.up * piece.Height;
            var corners = Corners(anchor, yaw, local);
            if (kind == Kind.Building)
            {
                // on the inside of a bend a straight front cuts into the sidewalk: step back until it clears
                float need = w + walk + setback;
                for (int pass = 0; pass < 3; pass++)
                {
                    float nearest = float.MaxValue;
                    for (int i = 0; i < corners.Length; i++)
                        foreach (var p in new[] { corners[i], (corners[i] + corners[(i + 1) % corners.Length]) / 2f })
                        {
                            Nearest(frames, new Vector3(p.x, 0f, p.y), out float d);
                            nearest = Mathf.Min(nearest, d);
                        }
                    if (nearest >= need - 0.01f)
                        break;
                    anchor -= facing * (need - nearest + 0.02f);
                    corners = Corners(anchor, yaw, local);
                }
            }
            return new Pose { Position = anchor, Yaw = yaw, Corners = corners };
        }

        // a piece's footprint corners at a pose
        public static Vector2[] Corners(Vector3 position, float yaw, Bounds local)
        {
            var rotation = Quaternion.Euler(0f, yaw, 0f);
            return new[]
            {
                new Vector3(local.min.x, 0f, local.max.z), new Vector3(local.max.x, 0f, local.max.z),
                new Vector3(local.max.x, 0f, local.min.z), new Vector3(local.min.x, 0f, local.min.z),
            }.Select(c => position + rotation * c).Select(c => new Vector2(c.x, c.z)).ToArray();
        }

        // whether two convex quads overlap (separating axes), with a small tolerance
        public static bool Overlap(Vector2[] a, Vector2[] b)
        {
            foreach (var quad in new[] { a, b })
                for (int i = 0; i < quad.Length; i++)
                {
                    var edge = quad[(i + 1) % quad.Length] - quad[i];
                    var axis = new Vector2(-edge.y, edge.x).normalized;
                    float aMin = a.Min(p => Vector2.Dot(p, axis)), aMax = a.Max(p => Vector2.Dot(p, axis));
                    float bMin = b.Min(p => Vector2.Dot(p, axis)), bMax = b.Max(p => Vector2.Dot(p, axis));
                    if (aMax <= bMin + 0.02f || bMax <= aMin + 0.02f)
                        return false;
                }
            return true;
        }

        // whether a footprint keeps off every hand-made road and junction (their road and sidewalks)
        public static bool OffRoads(Vector2[] corners)
        {
            var probes = new List<Vector2>(corners);
            for (int i = 0; i < corners.Length; i++)
                probes.Add((corners[i] + corners[(i + 1) % corners.Length]) / 2f);
            probes.Add(corners.Aggregate(Vector2.zero, (s, c) => s + c) / corners.Length);
            foreach (var road in Roads)
            {
                var frames = Frames(road);
                if (frames.Count < 2)
                    continue;
                foreach (var p in probes)
                {
                    var f = Nearest(frames, new Vector3(p.x, 0f, p.y), out float d);
                    int side = Vector2.Dot(p - new Vector2(f.Position.x, f.Position.z), new Vector2(f.Right.x, f.Right.z)) >= 0f ? 1 : -1;
                    if (d < road.Width / 2f + Walk(road, side) - 0.05f)
                        return false;
                }
            }
            foreach (var j in Junctions)
            {
                var root = j.transform.parent;
                float reach = Reach(j) - 0.05f;
                foreach (var p in probes)
                {
                    var l = root.InverseTransformPoint(new Vector3(p.x, root.position.y, p.y));
                    if (Mathf.Abs(l.x) < reach && Mathf.Abs(l.z) < reach)
                        return false;
                }
            }
            return true;
        }

        // footprints of buildings already standing: other roads' roadside, hand-placed and generated ones
        public static List<Vector2[]> ExistingBuildings(Transform skip)
        {
            var groups = new List<Transform>();
            foreach (var road in Roads)
                groups.Add(road.transform.parent.Find("Roadside/Buildings"));
            foreach (var city in TarkovCityBuilder.Cities())
                if (city.transform.parent != null)
                    groups.Add(city.transform.parent.Find("Buildings"));
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded)
                    foreach (var root in scene.GetRootGameObjects())
                        if (root.name == PiecesRoot)
                            groups.Add(root.transform.Find("Buildings"));
            }
            var found = new List<Vector2[]>();
            foreach (var group in groups.Where(g => g != null && (skip == null || !g.IsChildOf(skip))))
                foreach (Transform child in group)
                {
                    var renderers = child.GetComponentsInChildren<Renderer>();
                    if (renderers.Length == 0)
                        continue;
                    var b = renderers[0].bounds;
                    foreach (var r in renderers)
                        b.Encapsulate(r.bounds);
                    found.Add(new[] { new Vector2(b.min.x, b.max.z), new Vector2(b.max.x, b.max.z), new Vector2(b.max.x, b.min.z), new Vector2(b.min.x, b.min.z) });
                }
            return found;
        }

        // the scene root hand-placed pieces go under, a group per kit list
        public const string PiecesRoot = "City Pieces";

        public static GameObject Instantiate(TarkovCityKit.Piece piece, Pose pose, Transform parent)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(piece.Prefab, parent);
            instance.transform.SetPositionAndRotation(pose.Position, Quaternion.Euler(0f, pose.Yaw, 0f));
            return instance;
        }

        // Line This Road: the kit's buildings, lamps, trees, props and cars along both sides, replacing what it placed
        // before (Undo puts the old back); a summary
        public static string Line(TarkovCityRoad road)
        {
            var root = road.transform.parent;
            var frames = Frames(road);
            float length = Length(frames);
            if (frames.Count < 2 || length < 4f)
                return "The road is too short.";
            var kit = road.Kit;
            if (kit == null)
                return "The road has no kit to take its buildings and props from (set its Kit).";
            var old = root.Find("Roadside");
            if (old != null)
                Undo.DestroyObjectImmediate(old.gameObject);
            var group = new GameObject("Roadside").transform;
            group.SetParent(root, false);
            Transform Group(string name)
            {
                var g = group.Find(name);
                if (g == null)
                {
                    g = new GameObject(name).transform;
                    g.SetParent(group, false);
                }
                return g;
            }
            var random = new System.Random(road.Seed * 7919 + 3);
            var taken = ExistingBuildings(root);
            Physics.SyncTransforms();
            int highMask = LayerMask.GetMask("HighPolyCollider", "LowPolyCollider");
            int placed = 0;
            float startClear = road.StartJunction != null ? 0f : 1f, endClear = road.EndJunction != null ? 0f : 1f;

            bool Free(Pose pose, Bounds local)
            {
                if (taken.Any(t => Overlap(t, pose.Corners)) || !OffRoads(pose.Corners))
                    return false;
                // anything solid standing there already (hand-placed buildings), above kerb height
                var centre = pose.Position + Quaternion.Euler(0f, pose.Yaw, 0f) * new Vector3(local.center.x, 0f, local.center.z) + Vector3.up * 2f;
                var half = new Vector3(Mathf.Max(0.05f, local.extents.x - 0.2f), 1.4f, Mathf.Max(0.05f, local.extents.z - 0.2f));
                return !Physics.CheckBox(centre, half, Quaternion.Euler(0f, pose.Yaw, 0f), highMask, QueryTriggerInteraction.Ignore);
            }

            foreach (int side in new[] { 1, -1 })
            {
                if (side > 0 ? !road.FillRight : !road.FillLeft)
                    continue;
                var buildings = Usable(kit.Buildings);
                float s = startClear;
                while (buildings.Count > 0 && s < length - endClear - 2f)
                {
                    if (random.NextDouble() > road.BuildingChance)
                    {
                        s += 6f + 6f * (float)random.NextDouble();
                        continue;
                    }
                    bool done = false;
                    foreach (var piece in Shuffled(buildings, random).Take(8))
                    {
                        var local = TarkovCityBuilder.LocalBounds(piece.Prefab);
                        float width = TarkovCityBuilder.FootprintOf(local, -(int)piece.Front * 90f).width;
                        if (s + width > length - endClear)
                            continue;
                        var pose = RoadsidePose(road, frames, s + width / 2f, side, piece, Kind.Building);
                        if (!Free(pose, local))
                            continue;
                        Instantiate(piece, pose, Group("Buildings"));
                        taken.Add(pose.Corners);
                        placed++;
                        s += width + road.BuildingGap;
                        done = true;
                        break;
                    }
                    if (!done)
                        s += 2f;
                }
                if (Walk(road, side) <= 0f)
                    continue;
                var along = new List<float>();
                var lamps = Usable(kit.Lamps);
                if (lamps.Count > 0 && road.LampSpacing > 0f)
                    for (float d = Mathf.Max(4f, road.LampSpacing / 2f); d < length - 4f; d += road.LampSpacing)
                    {
                        var piece = Pick(lamps, random);
                        Instantiate(piece, RoadsidePose(road, frames, d, side, piece, Kind.Lamp), Group("Street Lamps"));
                        along.Add(d);
                        placed++;
                    }
                var trees = Usable(kit.Trees);
                if (trees.Count > 0 && road.TreeSpacing > 0f && Walk(road, side) >= 2.5f)
                    for (float d = 4f + road.TreeSpacing / 2f; d < length - 4f; d += road.TreeSpacing)
                    {
                        if (along.Any(a => Mathf.Abs(a - d) < 2f))
                            continue;
                        var piece = Pick(trees, random);
                        var pose = RoadsidePose(road, frames, d, side, piece, Kind.Tree);
                        pose.Yaw = (float)random.NextDouble() * 360f;
                        Instantiate(piece, pose, Group("Trees"));
                        along.Add(d);
                        placed++;
                    }
                var props = Usable(kit.StreetProps);
                if (props.Count > 0 && road.PropsPer100m > 0f && length > 8f)
                {
                    float wanted = length / 100f * road.PropsPer100m;
                    int count = Mathf.FloorToInt(wanted) + (random.NextDouble() < wanted - Mathf.Floor(wanted) ? 1 : 0);
                    for (int k = 0, tries = 0; k < count && tries < count * 6; tries++)
                    {
                        float d = 3f + (float)random.NextDouble() * (length - 6f);
                        if (along.Any(a => Mathf.Abs(a - d) < 2f))
                            continue;
                        var piece = Pick(props, random);
                        Instantiate(piece, RoadsidePose(road, frames, d, side, piece, Kind.Prop), Group("Street Props"));
                        along.Add(d);
                        placed++;
                        k++;
                    }
                }
            }
            var cars = Usable(kit.Vehicles);
            if (cars.Count > 0 && road.ParkedCarChance > 0f)
                foreach (int side in new[] { 1, -1 })
                {
                    if (Walk(road, side) <= 0f)
                        continue;
                    float s = 7f;
                    while (s < length - 7f)
                    {
                        var piece = Pick(cars, random);
                        var rect = TarkovCityBuilder.FootprintOf(TarkovCityBuilder.LocalBounds(piece.Prefab), -(int)piece.Front * 90f);
                        float carLength = rect.height;
                        if (s + carLength > length - 7f)
                            break;
                        if (0.3f + rect.width <= road.Width / 2f && random.NextDouble() < road.ParkedCarChance)
                        {
                            Instantiate(piece, RoadsidePose(road, frames, s + carLength / 2f, side, piece, Kind.Vehicle), Group("Vehicles"));
                            placed++;
                        }
                        s += carLength + 1.5f;
                    }
                }
            Undo.RegisterCreatedObjectUndo(group.gameObject, "Line road");
            return $"{placed} objects placed along {root.name} ({length:0} m).";
        }

        public static List<TarkovCityKit.Piece> Usable(List<TarkovCityKit.Piece> pieces) =>
            pieces?.Where(p => p != null && p.Prefab != null && p.Weight > 0f).ToList() ?? new List<TarkovCityKit.Piece>();

        public static IEnumerable<TarkovCityKit.Piece> Shuffled(List<TarkovCityKit.Piece> pieces, System.Random random) =>
            pieces.Select(p => (p, Key: Math.Pow(random.NextDouble(), 1.0 / p.Weight))).OrderByDescending(x => x.Key).Select(x => x.p);

        public static TarkovCityKit.Piece Pick(List<TarkovCityKit.Piece> pieces, System.Random random) => Shuffled(pieces, random).FirstOrDefault();

        // ------------------------------------------------------------ Scene view

        // the selected road's curve and points (drag a point over the ground; Ctrl-click the road to add one), and the
        // selected junction's arms
        private static void OnSceneGui(SceneView view)
        {
            var active = Selection.activeTransform;
            if (active == null)
                return;
            var road = RoadOf(active);
            if (road != null && road.transform.parent != null)
                DrawRoad(road);
            var junction = road == null ? JunctionOf(active) : null;
            if (junction != null && junction.transform.parent != null)
                for (int arm = 0; arm < 4; arm++)
                {
                    var anchor = Anchor(junction, arm);
                    Handles.color = junction.Open(arm) ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 1f, 1f, 0.5f);
                    Handles.DrawWireDisc(anchor, Vector3.up, junction.Width / 2f);
                    Handles.Label(anchor + Vector3.up, new[] { "N", "E", "S", "W" }[arm] + (RoadAt(junction, arm) != null ? " (joined)" : junction.Open(arm) ? " (open)" : ""));
                }
        }

        // the point the curve editor has picked (Scene view and window share it)
        public static int PickedPoint = -1;
        public static event Action Changed;

        private static void DrawRoad(TarkovCityRoad road)
        {
            var frames = Frames(road);
            var e = Event.current;
            if (frames.Count >= 2)
            {
                Handles.color = new Color(1f, 0.85f, 0.2f, 0.9f);
                Handles.DrawAAPolyLine(3f, frames.Select(f => f.Position + Vector3.up * 0.3f).ToArray());
                Handles.color = new Color(1f, 0.85f, 0.2f, 0.4f);
                Handles.DrawAAPolyLine(2f, frames.Select(f => f.Position + f.Right * (road.Width / 2f + road.SidewalkRight) + Vector3.up * 0.3f).ToArray());
                Handles.DrawAAPolyLine(2f, frames.Select(f => f.Position - f.Right * (road.Width / 2f + road.SidewalkLeft) + Vector3.up * 0.3f).ToArray());
            }
            int count = road.transform.childCount;
            var handles = BezierHandles(road);
            for (int i = 0; i < count; i++)
            {
                var point = road.transform.GetChild(i);
                bool pinned = i == 0 && road.StartJunction != null || i == count - 1 && road.EndJunction != null;
                var mode = ModeOf(point);
                float size = HandleUtility.GetHandleSize(point.position) * 0.09f;

                // right-click: the point's mode, and more
                if (e.type == EventType.MouseDown && e.button == 1 && !e.alt &&
                    Vector2.Distance(HandleUtility.WorldToGUIPoint(point.position), e.mousePosition) < 12f)
                {
                    PointMenu(road, i, frames);
                    e.Use();
                }

                // the curve's handles: drag to shape it (a Smooth point becomes Aligned); Corner points have none
                if (!pinned && mode != TarkovCityRoadPoint.Modes.Corner && i < handles.Count)
                {
                    bool own = mode != TarkovCityRoadPoint.Modes.Smooth;
                    foreach (bool outHandle in new[] { false, true })
                    {
                        if (!IsLoop(road, count) && (outHandle ? i == count - 1 : i == 0))
                            continue;
                        var offset = outHandle ? handles[i].Out : handles[i].In;
                        var tip = point.position + offset;
                        Handles.color = own ? new Color(0.4f, 0.8f, 1f) : new Color(0.4f, 0.8f, 1f, 0.45f);
                        Handles.DrawLine(point.position, tip);
                        EditorGUI.BeginChangeCheck();
                        var moved = Handles.FreeMoveHandle(tip, size * 0.75f, Vector3.zero, Handles.CircleHandleCap);
                        if (EditorGUI.EndChangeCheck())
                        {
                            // over the flat plane through the handle's tip, so dragging doesn't tip it up or down
                            var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                            if (new Plane(Vector3.up, tip).Raycast(ray, out float d))
                                moved = ray.GetPoint(d);
                            SetHandle(road, i, outHandle, moved - point.position);
                            PickedPoint = i;
                        }
                    }
                }

                Handles.color = pinned ? new Color(0.3f, 1f, 0.4f) : i == PickedPoint ? Color.white : new Color(1f, 0.85f, 0.2f);
                if (pinned)
                {
                    Handles.SphereHandleCap(0, point.position, Quaternion.identity, size, EventType.Repaint);
                    continue;
                }
                EditorGUI.BeginChangeCheck();
                var at = Handles.FreeMoveHandle(point.position, mode == TarkovCityRoadPoint.Modes.Corner ? size * 1.2f : size, Vector3.zero,
                    mode == TarkovCityRoadPoint.Modes.Corner ? (Handles.CapFunction)Handles.CubeHandleCap : Handles.SphereHandleCap);
                if (EditorGUI.EndChangeCheck())
                {
                    // dragged over the ground under the mouse
                    if (Ground(HandleUtility.GUIPointToWorldRay(e.mousePosition), 5000f, false, out var hit))
                        at = hit.point;
                    Undo.RecordObject(point, "Move road point");
                    point.position = at;
                    PickedPoint = i;
                    Changed?.Invoke();
                }
            }
            // Ctrl-click on the road: a new point there
            if (e.type == EventType.MouseDown && e.button == 0 && (e.control || e.command) && !e.alt && frames.Count >= 2 &&
                Ground(HandleUtility.GUIPointToWorldRay(e.mousePosition), 5000f, true, out var on))
            {
                var nearest = Nearest(frames, on.point, out float d);
                if (d <= road.Width / 2f + 1f)
                {
                    AddPoint(road, new Vector3(nearest.Position.x, on.point.y, nearest.Position.z), PointAfter(frames, nearest.Distance) + 1);
                    e.Use();
                    Changed?.Invoke();
                }
            }
        }

        // the index of the last point the road passes before a distance along it
        public static int PointAfter(List<Frame> frames, float distance)
        {
            int index = 0;
            foreach (var f in frames)
                if (f.Point >= 0 && f.Distance <= distance)
                    index = Mathf.Max(index, f.Point);
            return index;
        }

        private static void PointMenu(TarkovCityRoad road, int index, List<Frame> frames)
        {
            var menu = new GenericMenu();
            var current = ModeOf(road.transform.GetChild(index));
            foreach (TarkovCityRoadPoint.Modes mode in Enum.GetValues(typeof(TarkovCityRoadPoint.Modes)))
            {
                var m = mode;
                menu.AddItem(new GUIContent(mode.ToString()), current == mode, () =>
                {
                    SetMode(road, index, m);
                    Changed?.Invoke();
                });
            }
            menu.AddSeparator("");
            var at = frames.FirstOrDefault(f => f.Point == index);
            menu.AddItem(new GUIContent("Add Intersection Here"), false, () =>
            {
                if (AddIntersection(road, at.Distance, out string error) == null)
                    EditorUtility.DisplayDialog("City Builder", error, "OK");
            });
            if (road.transform.childCount > 2)
                menu.AddItem(new GUIContent("Delete Point"), false, () =>
                {
                    Undo.DestroyObjectImmediate(road.transform.GetChild(index).gameObject);
                    Rename(road);
                    PickedPoint = -1;
                    Changed?.Invoke();
                });
            menu.ShowAsContext();
        }

        // ------------------------------------------------------------ curve editing

        private static Vector3 ToRoad(TarkovCityRoad road, Vector3 world) => road.transform.parent.InverseTransformVector(world);

        private static TarkovCityRoadPoint PointData(Transform point)
        {
            var data = point.GetComponent<TarkovCityRoadPoint>();
            return data != null ? data : Undo.AddComponent<TarkovCityRoadPoint>(point.gameObject);
        }

        // a point's mode; Aligned and Free start from the handles the curve has there now, so it doesn't jump
        public static void SetMode(TarkovCityRoad road, int index, TarkovCityRoadPoint.Modes mode)
        {
            var handles = BezierHandles(road);
            var data = PointData(road.transform.GetChild(index));
            Undo.RecordObject(data, "Road point mode");
            if (mode == TarkovCityRoadPoint.Modes.Aligned || mode == TarkovCityRoadPoint.Modes.Free)
            {
                var (inHandle, outHandle) = index < handles.Count ? handles[index] : (Vector3.back * 5f, Vector3.forward * 5f);
                // an open end has only one handle that counts: the other mirrors it
                if (inHandle.sqrMagnitude < 1e-6f)
                    inHandle = -outHandle;
                if (outHandle.sqrMagnitude < 1e-6f)
                    outHandle = -inHandle;
                data.In = ToRoad(road, inHandle);
                data.Out = ToRoad(road, outHandle);
            }
            data.Mode = mode;
            EditorUtility.SetDirty(data);
        }

        // moves one handle (a world offset from the point); an Aligned point turns its other handle to stay in line
        public static void SetHandle(TarkovCityRoad road, int index, bool outHandle, Vector3 offset)
        {
            var data = PointData(road.transform.GetChild(index));
            if (data.Mode == TarkovCityRoadPoint.Modes.Smooth || data.Mode == TarkovCityRoadPoint.Modes.Corner)
                SetMode(road, index, TarkovCityRoadPoint.Modes.Aligned);
            Undo.RecordObject(data, "Road point handle");
            var local = ToRoad(road, offset);
            if (local.sqrMagnitude < 0.01f)
                local = local.normalized * 0.1f;
            if (outHandle)
                data.Out = local;
            else
                data.In = local;
            if (data.Mode == TarkovCityRoadPoint.Modes.Aligned)
            {
                if (outHandle)
                    data.In = -local.normalized * data.In.magnitude;
                else
                    data.Out = -local.normalized * data.Out.magnitude;
            }
            EditorUtility.SetDirty(data);
        }

        public static void SetRaise(TarkovCityRoad road, int index, float raise)
        {
            var data = PointData(road.transform.GetChild(index));
            Undo.RecordObject(data, "Road point raise");
            data.Raise = raise;
            EditorUtility.SetDirty(data);
        }

        public static void Rename(TarkovCityRoad road)
        {
            for (int i = 0; i < road.transform.childCount; i++)
                road.transform.GetChild(i).name = "Point " + (i + 1);
        }

        // ------------------------------------------------------------ intersections

        public static float JunctionReach(TarkovCityRoad road) => road.Width / 2f + Mathf.Max(0.5f, Mathf.Max(road.SidewalkLeft, road.SidewalkRight));

        // why the road can't be split at the distance (keeping `margin` metres clear each side), or null
        public static string Splittable(TarkovCityRoad road, float distance, float margin)
        {
            if (IsLoop(road, road.transform.childCount))
                return $"{road.transform.parent.name} is a loop: turn its Loop off to split it.";
            float length = Length(Frames(road));
            if (distance - margin < 2f || length - distance - margin < 2f)
                return $"Too near an end of {road.transform.parent.name} for an intersection: it needs about {margin + 2f:0} m of road each side.";
            return null;
        }

        // a crossroads across the road at a distance along it: the road is cut there, its first part joins the
        // junction's south arm and the rest, a new road, its north arm (east and west are left free); null and why
        public static TarkovCityJunction AddIntersection(TarkovCityRoad road, float distance, out string error)
        {
            float margin = JunctionReach(road) + 2f;
            error = Splittable(road, distance, margin);
            if (error != null)
                return null;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            var f = At(Frames(road), distance);
            var junction = NewJunction(road, f.Position, Mathf.Atan2(f.Forward.x, f.Forward.z) * Mathf.Rad2Deg);
            Split(road, distance, junction, 2, 0, margin);
            Undo.SetCurrentGroupName("Add intersection");
            Undo.CollapseUndoOperations(group);
            return junction;
        }

        private static TarkovCityJunction NewJunction(TarkovCityRoad road, Vector3 position, float yaw)
        {
            var like = new GameObject("TarkovSDK_JunctionLike") { hideFlags = HideFlags.HideAndDontSave }.AddComponent<TarkovCityJunction>();
            like.North = like.East = like.South = like.West = false;
            like.Kit = road.Kit;
            like.Width = road.Width;
            like.SidewalkWidth = Mathf.Max(0.5f, Mathf.Max(road.SidewalkLeft, road.SidewalkRight));
            like.CurbHeight = road.CurbHeight;
            like.CurbWidth = road.CurbWidth;
            var junction = CreateJunction(position, yaw, like);
            Object.DestroyImmediate(like.gameObject);
            if (road.FollowGround)
                FitToGround(junction);
            return junction;
        }

        // cuts the road at the distance: its points within `margin` of it go, its first part ends on `firstArm`, the
        // rest becomes a new road from `secondArm` (keeping the old end's junction); the new road
        private static TarkovCityRoad Split(TarkovCityRoad road, float distance, TarkovCityJunction junction, int firstArm, int secondArm, float margin)
        {
            var frames = Frames(road);
            var points = road.transform.Cast<Transform>().ToList();
            var along = new float[points.Count];
            foreach (var f in frames)
                if (f.Point >= 0 && f.Point < along.Length)
                    along[f.Point] = f.Distance;
            var second = CreateRoad(Anchor(junction, secondArm), road);
            second.transform.parent.name = road.transform.parent.name;
            Undo.RecordObject(second, "Split road");
            second.EndJunction = road.EndJunction;
            second.EndArm = road.EndArm;
            Undo.RecordObject(road, "Split road");
            road.EndJunction = null;
            // the points after the cut are copied onto the new road and the originals removed (moving them onto a road
            // made in the same Undo step doesn't undo reliably)
            for (int i = 0; i < points.Count; i++)
            {
                if (along[i] > distance + margin)
                {
                    var copy = AddPoint(second, points[i].position, -1);
                    var data = points[i].GetComponent<TarkovCityRoadPoint>();
                    if (data != null)
                        EditorUtility.CopySerialized(data, copy.GetComponent<TarkovCityRoadPoint>());
                }
                if (along[i] >= distance - margin)
                    Undo.DestroyObjectImmediate(points[i].gameObject);
            }
            AddPoint(road, Anchor(junction, firstArm), -1);
            Attach(road, false, junction, firstArm);
            Attach(second, true, junction, secondArm);
            Rename(road);
            Rename(second);
            ClearRoadside(road, junction);
            Forget();
            Rebuild(junction);
            Rebuild(road);
            Rebuild(second);
            return second;
        }

        // what Line This Road placed where the junction now is
        private static void ClearRoadside(TarkovCityRoad road, TarkovCityJunction junction)
        {
            var side = road.transform.parent.Find("Roadside");
            if (side == null)
                return;
            var root = junction.transform.parent;
            float reach = Reach(junction) + 1f;
            foreach (var t in side.GetComponentsInChildren<Transform>().Where(t => t.parent != null && t.parent.parent == side).ToList())
            {
                var renderers = t.GetComponentsInChildren<Renderer>();
                var centre = renderers.Length > 0 ? renderers.Aggregate(renderers[0].bounds, (b, r) => { b.Encapsulate(r.bounds); return b; }).center : t.position;
                var local = root.InverseTransformPoint(centre);
                if (Mathf.Abs(local.x) < reach && Mathf.Abs(local.z) < reach)
                    Undo.DestroyObjectImmediate(t.gameObject);
            }
        }

        public struct Crossing
        {
            public TarkovCityRoad A, B;
            public float AlongA, AlongB;
            public Vector3 Point;
        }

        // where two roads' centre lines cross (at about the same height: more than 3 m apart is a bridge)
        public static List<Crossing> Crossings()
        {
            var found = new List<Crossing>();
            var roads = Roads;
            for (int i = 0; i < roads.Count; i++)
                for (int k = i + 1; k < roads.Count; k++)
                {
                    var fa = Frames(roads[i]);
                    var fb = Frames(roads[k]);
                    for (int a = 0; a + 1 < fa.Count; a++)
                        for (int b = 0; b + 1 < fb.Count; b++)
                        {
                            Vector2 p = Flat(fa[a].Position), p2 = Flat(fa[a + 1].Position), q = Flat(fb[b].Position), q2 = Flat(fb[b + 1].Position);
                            Vector2 r = p2 - p, s = q2 - q;
                            float cross = r.x * s.y - r.y * s.x;
                            if (Mathf.Abs(cross) < 1e-6f)
                                continue;
                            float t = ((q.x - p.x) * s.y - (q.y - p.y) * s.x) / cross;
                            float u = ((q.x - p.x) * r.y - (q.y - p.y) * r.x) / cross;
                            if (t < 0f || t >= 1f || u < 0f || u >= 1f)
                                continue;
                            var pa = Vector3.Lerp(fa[a].Position, fa[a + 1].Position, t);
                            var pb = Vector3.Lerp(fb[b].Position, fb[b + 1].Position, u);
                            if (Mathf.Abs(pa.y - pb.y) > 3f)
                                continue;
                            found.Add(new Crossing
                            {
                                A = roads[i], B = roads[k], Point = pa,
                                AlongA = Mathf.Lerp(fa[a].Distance, fa[a + 1].Distance, t), AlongB = Mathf.Lerp(fb[b].Distance, fb[b + 1].Distance, u),
                            });
                        }
                }
            return found;
        }

        private static Vector2 Flat(Vector3 v) => new Vector2(v.x, v.z);

        // a crossroads at every place two roads cross: the first road runs north-south through it, the second joins
        // east and west (cut back further the more slanted it crosses, so it has room to turn into the arms). How
        // many were made, and why any weren't.
        public static int IntersectCrossings(List<string> notes)
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            int made = 0;
            var skipped = new List<Vector3>();
            for (int guard = 0; guard < 100; guard++)
            {
                var crossing = Crossings().FirstOrDefault(c => !skipped.Any(p => (p - c.Point).sqrMagnitude < 1f));
                if (crossing.A == null)
                    break;
                var fa = At(Frames(crossing.A), crossing.AlongA);
                var fb = At(Frames(crossing.B), crossing.AlongB);
                float marginA = JunctionReach(crossing.A) + 2f;
                float slant = Mathf.Abs(Vector3.Dot(fb.Forward, fa.Right));
                float marginB = JunctionReach(crossing.A) + 2f + 10f * (1f - slant);
                var error = Splittable(crossing.A, crossing.AlongA, marginA) ?? Splittable(crossing.B, crossing.AlongB, marginB);
                if (error != null)
                {
                    notes.Add(error);
                    skipped.Add(crossing.Point);
                    continue;
                }
                var junction = NewJunction(crossing.A, new Vector3(crossing.Point.x, fa.Position.y, crossing.Point.z), Mathf.Atan2(fa.Forward.x, fa.Forward.z) * Mathf.Rad2Deg);
                Split(crossing.A, crossing.AlongA, junction, 2, 0, marginA);
                bool eastward = Vector3.Dot(fb.Forward, fa.Right) > 0f;
                Split(crossing.B, crossing.AlongB, junction, eastward ? 3 : 1, eastward ? 1 : 3, marginB);
                made++;
            }
            Undo.SetCurrentGroupName("Intersect crossing roads");
            Undo.CollapseUndoOperations(group);
            return made;
        }
    }
}
