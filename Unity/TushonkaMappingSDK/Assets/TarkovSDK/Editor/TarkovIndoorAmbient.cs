using System.Collections.Generic;
using System.Linq;
using EFT.EnvironmentEffect;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Indoor ambient: the game's StencilShadow and AnalyticSource, which AmbientLight draws into its stencil pass before
    // the deferred lighting (DrawStencil, every camera, every frame; shaders all in globalgamemanagers.assets, filled on
    // SDK maps by the client's AmbientLightShaderPatch).
    //
    //  - StencilShadow: a closed mesh around an interior (its MeshRenderer disabled: 675 of retail's 954 are like that;
    //    AmbientLight draws it with its own material through DrawRenderer). Surfaces inside it get Ambient instead of
    //    the sky's ambient: retail black at alpha 0.78-0.83, culled past 40 m (100 for big halls) with a 5 m fade.
    //    Without one an interior is lit by the open sky's ambient. Awake skips it when it has no Renderer.
    //  - AnalyticSource ("ambient_portal" on retail, 5,747 of them): soft light let in through an opening. Its origin
    //    is the middle of the opening, its forward points into the room, DoorSize is the opening's half width and half
    //    height, and the box it lights is CubeControlHelper's (a child: half its scale = CubeScale, its position =
    //    CubeShift; UpdateSettings copies them at Start). Retail medians: a ~5.5 x 4.8 x 5.4 m box centred on the
    //    opening, from 1.3 m outside to 3.8 m in; LightIntensity 6 x multiplier 2.6 (doors) or 2 (windows), FadeNormals
    //    0.25 / 0.18, FadeOut ~ (0.23 x width, 1.2, 0.57 x depth), culled past 30 m with a 15 m fade. Static ones are
    //    only drawn from the list AmbientLight.RuntimeOptimizePrepare makes at raid load (TarkovApplication), so they
    //    must be in the scene from the start; DynamicAnalyticSource follows a moving parent (Reserve's train).
    //
    // Generate (Tarkov SDK > Generation > Geometry > Indoor Ambient): the rooms the indoor trigger scan finds, each
    // box fitted to its walls by rays (just past the inner face, so the room's own walls are inside and their outside
    // isn't), one StencilShadow per box; then each wall face is scanned for openings to the outside (doors and glass
    // count as open: light passes them) and each gets an AnalyticSource. Rooms that are turned get one box along their
    // walls. Everything goes under GeneratedIndoorAmbient in the map's first scene; hand-made ones elsewhere are kept
    // and rooms they cover skipped.
    public static class TarkovIndoorAmbient
    {
        public const string RootName = "GeneratedIndoorAmbient";
        private const string Menu = "GameObject/Tarkov SDK/Lights/";
        private const string BoxMeshPath = TarkovGeneratedAssets.PropsFolder + "/SDK_StencilBox.asset";
        public static readonly Color DarkAmbient = new Color(0f, 0f, 0f, 0.8f);
        // past the inner wall face, into the wall
        private const float Inset = 0.05f;
        // how far a face may move to meet its wall
        private const float WallReach = 1.5f;
        // openings: the grid across a wall face, and the thickest wall
        private const float OpeningStep = 0.2f;
        private const float WallThickness = 1f;

        public struct Box
        {
            public Vector3 Center;
            public Quaternion Rotation;
            public Vector3 Size;

            public Vector3 Right => Rotation * Vector3.right;
            public Vector3 Forward => Rotation * Vector3.forward;
            public float Floor => Center.y - Size.y * 0.5f;
            public float Ceiling => Center.y + Size.y * 0.5f;

            public bool Contains(Vector3 p)
            {
                var local = Quaternion.Inverse(Rotation) * (p - Center);
                return Mathf.Abs(local.x) <= Size.x * 0.5f && Mathf.Abs(local.y) <= Size.y * 0.5f && Mathf.Abs(local.z) <= Size.z * 0.5f;
            }
        }

        public struct Opening
        {
            public Vector3 Center;     // on the inner wall face
            public Vector3 Inward;     // into the room
            public float Width, Height;
            public bool Door;          // reaches the floor
        }

        // ------------------------------------------------------------ menus

        [MenuItem("Tarkov SDK/Generation/Geometry/Indoor Ambient", priority = 111)]
        public static void GenerateMenu()
        {
            var scene = SceneManager.GetActiveScene();
            TarkovMapScenes.OpenMapScenes(scene);
            var root = Generate(scene);
            if (root != null)
            {
                Selection.activeGameObject = root;
                EditorSceneManager.MarkSceneDirty(root.scene);
            }
        }

        [MenuItem(Menu + "Indoor Darkness Volume (Stencil Shadow)", priority = 58)]
        private static void MenuStencil(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p =>
        {
            var stencil = CreateStencil(p, new Box { Center = p != null ? p.position : Vector3.zero, Rotation = p != null ? p.rotation : Quaternion.identity, Size = new Vector3(4f, 3f, 4f) });
            return stencil.gameObject;
        });

        [MenuItem(Menu + "Ambient Portal (Analytic Source)", priority = 59)]
        private static void MenuPortal(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p =>
        {
            var source = CreatePortal(p, new Opening { Center = p != null ? p.position : Vector3.zero, Inward = p != null ? p.forward : Vector3.forward, Width = 1f, Height = 2f, Door = true }, 4f, 4f, 3f, 1f);
            return source.gameObject;
        });

        // ------------------------------------------------------------ generate

        public static GameObject Generate(Scene scene)
        {
            int mask = TarkovRainCover.SolidMask;
            int wallMask = mask | LayerMask.GetMask("Interactive");
            if (!TarkovRainCover.MapSolidBounds(scene, mask, out var bounds, TarkovIndoorTriggers.Counts))
            {
                EditorUtility.DisplayDialog("Indoor Ambient", "The map has no solid mesh colliders to scan.", "OK");
                return null;
            }
            var maps = TarkovMapScenes.MapScenes(scene).ToList();
            var oldRoots = maps.SelectMany(s => s.GetRootGameObjects()).Where(g => g.name == RootName).ToList();
            // rooms with a hand-made stencil are left to it
            var manual = maps.SelectMany(s => s.GetRootGameObjects()).Where(g => g.name != RootName)
                .SelectMany(r => r.GetComponentsInChildren<StencilShadow>(true)).Select(s => s.GetComponent<Renderer>()).Where(r => r != null)
                .Select(r => r.bounds).ToList();
            foreach (var old in oldRoots)
                old.SetActive(false);
            List<List<Bounds>> rooms;
            try
            {
                rooms = TarkovIndoorTriggers.ScanRooms(bounds, mask, wallMask, manual, "Indoor Ambient");
            }
            finally
            {
                foreach (var old in oldRoots)
                    old.SetActive(true);
            }
            if (rooms == null)
                return null;

            var fitted = new List<List<Box>>();
            bool backfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = false;
            try
            {
                for (int i = 0; i < rooms.Count; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Indoor Ambient", "Fitting rooms to their walls", i / (float)rooms.Count))
                        return null;
                    fitted.Add(FitRoom(rooms[i]));
                }
                var all = fitted.SelectMany(b => b).ToList();
                var openings = new List<List<(Opening Opening, Box Box)>>();
                for (int i = 0; i < fitted.Count; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Indoor Ambient", "Finding openings", i / (float)fitted.Count))
                        return null;
                    openings.Add(fitted[i].SelectMany(b => Openings(b, all).Select(o => (o, b))).ToList());
                }

                foreach (var old in oldRoots)
                    Undo.DestroyObjectImmediate(old);
                if (fitted.Count == 0)
                {
                    Debug.Log("[Tarkov SDK] Indoor ambient: found no rooms (a ceiling with walls around it).");
                    return null;
                }
                var root = new GameObject(RootName);
                SceneManager.MoveGameObjectToScene(root, TarkovMapScenes.FirstScene(scene));
                int stencils = 0, doors = 0, windows = 0;
                for (int i = 0; i < fitted.Count; i++)
                {
                    var room = new GameObject($"Room_{i + 1}");
                    room.transform.SetParent(root.transform, false);
                    foreach (var box in fitted[i])
                    {
                        CreateStencil(room.transform, box);
                        stencils++;
                    }
                    foreach (var (opening, box) in openings[i])
                    {
                        Measure(box, opening, out var depth, out var width, out var height, out var floor);
                        CreatePortal(room.transform, opening, depth, width, height, floor);
                        if (opening.Door)
                            doors++;
                        else
                            windows++;
                    }
                }
                Undo.RegisterCreatedObjectUndo(root, "Generate indoor ambient");
                Debug.Log($"[Tarkov SDK] Indoor ambient: {fitted.Count} room(s), {stencils} darkness volume(s), {doors} door and {windows} window portal(s)" +
                          (manual.Count > 0 ? $"; rooms in {manual.Count} hand-made volume(s) left alone" : "") + ". Check them in the Scene view (select one to see it).");
                return root;
            }
            finally
            {
                Physics.queriesHitBackfaces = backfaces;
                EditorUtility.ClearProgressBar();
                TarkovIndoorTriggers.ResetCache();
            }
        }

        // ------------------------------------------------------------ fitting

        // The room's boxes fitted to its walls; a room turned off the grid becomes one box along its walls.
        private static List<Box> FitRoom(List<Bounds> boxes)
        {
            float yaw = DominantYaw(boxes);
            if (Mathf.Abs(yaw) > 2f)
            {
                // the room's extent seen in its own frame, then fitted
                var rotation = Quaternion.Euler(0f, yaw, 0f);
                var inverse = Quaternion.Inverse(rotation);
                Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
                var centroid = Vector3.zero;
                float area = 0f;
                foreach (var b in boxes)
                {
                    float a = b.size.x * b.size.z;
                    centroid += b.center * a;
                    area += a;
                    for (int i = 0; i < 8; i++)
                    {
                        var p = inverse * new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                    }
                }
                centroid /= Mathf.Max(area, 1e-3f);
                var local = inverse * centroid;
                var box = new Box
                {
                    Center = rotation * new Vector3((min.x + max.x) * 0.5f, local.y, (min.z + max.z) * 0.5f),
                    Rotation = rotation,
                    Size = max - min,
                };
                return new List<Box> { Fit(box) };
            }
            return boxes.Select(b => Fit(new Box { Center = b.center, Rotation = Quaternion.identity, Size = b.size })).ToList();
        }

        // the walls' direction, as a turn from the grid (-45..45 degrees; 0 when there's no clear one)
        private static float DominantYaw(List<Bounds> boxes)
        {
            float c = 0f, s = 0f;
            int n = 0;
            foreach (var b in boxes)
            {
                var origin = new Vector3(b.center.x, b.min.y + 0.2f + 1.2f, b.center.z);
                for (int i = 0; i < 16; i++)
                {
                    var d = Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward;
                    if (!TarkovIndoorTriggers.Cast(origin, d, TarkovIndoorTriggers.MaxRoomSize, TarkovRainCover.SolidMask, out var hit))
                        continue;
                    var normal = Vector3.ProjectOnPlane(hit.normal, Vector3.up);
                    if (normal.sqrMagnitude < 0.25f)
                        continue;
                    // walls a quarter turn apart agree: four times the angle
                    float a = Mathf.Atan2(normal.x, normal.z) * 4f;
                    c += Mathf.Cos(a);
                    s += Mathf.Sin(a);
                    n++;
                }
            }
            if (n < 4 || Mathf.Sqrt(c * c + s * s) / n < 0.6f)
                return 0f;
            return Mathf.Atan2(s, c) * Mathf.Rad2Deg / 4f;
        }

        // Each side moved to the wall in front of it (the median of rays across it, only hits from WallReach inside where
        // it was to reach outside it: furniture near the middle doesn't count), then Inset into the wall; floor and
        // ceiling the same way. By hand a box may start far smaller than its room, so reach is larger there.
        public static Box Fit(Box box, float reach = WallReach)
        {
            int mask = TarkovRainCover.SolidMask;
            var right = box.Right;
            var forward = box.Forward;
            float[] half = { box.Size.x * 0.5f, box.Size.x * 0.5f, box.Size.z * 0.5f, box.Size.z * 0.5f };
            var dirs = new[] { right, -right, forward, -forward };
            var tangents = new[] { forward, forward, right, right };
            var spans = new[] { box.Size.z, box.Size.z, box.Size.x, box.Size.x };
            float floor = box.Floor + 0.2f, height = box.Size.y;
            var heights = new[] { floor + 0.6f, floor + 1.4f, floor + Mathf.Max(1.6f, height * 0.6f) };
            var fittedHalf = (float[])half.Clone();
            for (int f = 0; f < 4; f++)
            {
                var distances = new List<float>();
                foreach (var u in new[] { -0.35f, -0.15f, 0f, 0.15f, 0.35f })
                    foreach (var h in heights)
                    {
                        var origin = box.Center + tangents[f] * (u * spans[f]);
                        origin.y = Mathf.Min(h, box.Ceiling - 0.1f);
                        if (TarkovIndoorTriggers.Cast(origin, dirs[f], half[f] + reach, mask, out var hit) && hit.distance > half[f] - WallReach)
                            distances.Add(hit.distance);
                    }
                if (distances.Count >= 3)
                    fittedHalf[f] = Median(distances) + Inset;
            }
            // floor and ceiling, from points across the room
            var floors = new List<float>();
            var ceilings = new List<float>();
            foreach (var (u, v) in new[] { (0f, 0f), (-0.3f, -0.3f), (0.3f, -0.3f), (-0.3f, 0.3f), (0.3f, 0.3f) })
            {
                var origin = box.Center + right * (u * box.Size.x) + forward * (v * box.Size.z);
                origin.y = floor + 0.5f;
                if (TarkovIndoorTriggers.Cast(origin, Vector3.down, 2f, mask, out var down))
                    floors.Add(down.point.y);
                if (TarkovIndoorTriggers.Cast(origin, Vector3.up, height + reach, mask, out var up))
                    ceilings.Add(up.point.y);
            }
            float bottom = floors.Count >= 2 ? Median(floors) - Inset : box.Floor;
            float top = ceilings.Count >= 2 ? Median(ceilings) + Inset : box.Ceiling;
            float sx = fittedHalf[0] + fittedHalf[1], sz = fittedHalf[2] + fittedHalf[3];
            var centre = box.Center + right * ((fittedHalf[0] - fittedHalf[1]) * 0.5f) + forward * ((fittedHalf[2] - fittedHalf[3]) * 0.5f);
            centre.y = (bottom + top) * 0.5f;
            return new Box { Center = centre, Rotation = box.Rotation, Size = new Vector3(Mathf.Max(0.5f, sx), Mathf.Max(1f, top - bottom), Mathf.Max(0.5f, sz)) };
        }

        private static float Median(List<float> values)
        {
            values.Sort();
            int n = values.Count;
            return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) * 0.5f;
        }

        // ------------------------------------------------------------ openings

        // The holes in the box's wall faces that lead outside (a point 1.5 m beyond isn't in any room). Doors (the
        // Interactive layer) and glass (TransparentCollider) aren't in the mask, so a doorway or window counts as open.
        public static List<Opening> Openings(Box box, List<Box> rooms)
        {
            var result = new List<Opening>();
            int mask = TarkovRainCover.SolidMask;
            var right = box.Right;
            var forward = box.Forward;
            var dirs = new[] { right, -right, forward, -forward };
            var tangents = new[] { forward, forward, right, right };
            var halves = new[] { box.Size.x * 0.5f, box.Size.x * 0.5f, box.Size.z * 0.5f, box.Size.z * 0.5f };
            var spans = new[] { box.Size.z, box.Size.z, box.Size.x, box.Size.x };
            float floor = box.Floor + Inset, ceiling = box.Ceiling - Inset;
            int nv = Mathf.Max(1, Mathf.FloorToInt((ceiling - floor - 0.1f) / OpeningStep));
            for (int f = 0; f < 4; f++)
            {
                int nu = Mathf.Max(1, Mathf.FloorToInt((spans[f] - 0.2f) / OpeningStep));
                var open = new bool[nu, nv];
                int holes = 0;
                float u0 = -(nu - 1) * OpeningStep * 0.5f;
                for (int iu = 0; iu < nu; iu++)
                    for (int iv = 0; iv < nv; iv++)
                    {
                        var p = box.Center + tangents[f] * (u0 + iu * OpeningStep) + dirs[f] * (halves[f] - Inset - 0.15f);
                        p.y = floor + 0.1f + (iv + 0.5f) * OpeningStep;
                        if (!TarkovIndoorTriggers.Cast(p, dirs[f], WallThickness + 0.15f + Inset, mask, out _))
                        {
                            open[iu, iv] = true;
                            holes++;
                        }
                    }
                // mostly open: not a wall (a side shared with another box of the room, an open-sided shed)
                if (holes == 0 || holes > nu * nv * 0.6f)
                    continue;
                var seen = new bool[nu, nv];
                var queue = new Queue<Vector2Int>();
                for (int iu = 0; iu < nu; iu++)
                    for (int iv = 0; iv < nv; iv++)
                    {
                        if (!open[iu, iv] || seen[iu, iv])
                            continue;
                        int minU = iu, maxU = iu, minV = iv, maxV = iv;
                        seen[iu, iv] = true;
                        queue.Enqueue(new Vector2Int(iu, iv));
                        while (queue.Count > 0)
                        {
                            var c = queue.Dequeue();
                            minU = Mathf.Min(minU, c.x); maxU = Mathf.Max(maxU, c.x);
                            minV = Mathf.Min(minV, c.y); maxV = Mathf.Max(maxV, c.y);
                            foreach (var d in new[] { Vector2Int.right, Vector2Int.left, Vector2Int.up, Vector2Int.down })
                            {
                                var n = c + d;
                                if (n.x < 0 || n.y < 0 || n.x >= nu || n.y >= nv || !open[n.x, n.y] || seen[n.x, n.y])
                                    continue;
                                seen[n.x, n.y] = true;
                                queue.Enqueue(n);
                            }
                        }
                        // a gap of at least 40 cm each way
                        if (maxU - minU < 1 || maxV - minV < 1)
                            continue;
                        float width = (maxU - minU + 1) * OpeningStep, height = (maxV - minV + 1) * OpeningStep;
                        var centre = box.Center + tangents[f] * (u0 + (minU + maxU) * 0.5f * OpeningStep) + dirs[f] * (halves[f] - Inset);
                        centre.y = floor + 0.1f + (minV + maxV + 1) * 0.5f * OpeningStep;
                        // leads outside, not into the next room
                        var beyond = centre + dirs[f] * 1.5f;
                        if (rooms.Any(r => r.Contains(beyond)))
                            continue;
                        result.Add(new Opening { Center = centre, Inward = -dirs[f], Width = width, Height = height, Door = minV == 0 });
                    }
            }
            return result;
        }

        // the room behind an opening, from the opening: depth inward, width across, height, and the floor below its middle
        private static void Measure(Box box, Opening opening, out float depth, out float width, out float height, out float floorBelow)
        {
            bool alongX = Mathf.Abs(Vector3.Dot(opening.Inward, box.Right)) > 0.5f;
            depth = alongX ? box.Size.x : box.Size.z;
            width = alongX ? box.Size.z : box.Size.x;
            height = box.Size.y;
            floorBelow = opening.Center.y - box.Floor;
        }

        // ------------------------------------------------------------ creating

        public static Mesh BoxMesh() => TarkovGeneratedAssets.Mesh(BoxMeshPath, () =>
        {
            // a closed unit cube, faces outward (the stencil pass counts front and back faces)
            var mesh = new Mesh { name = "SDK_StencilBox" };
            var v = new List<Vector3>();
            for (int i = 0; i < 8; i++)
                v.Add(new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f));
            mesh.SetVertices(v);
            mesh.SetTriangles(new[]
            {
                0, 2, 3, 0, 3, 1, // -z
                4, 5, 7, 4, 7, 6, // +z
                0, 4, 6, 0, 6, 2, // -x
                1, 3, 7, 1, 7, 5, // +x
                0, 1, 5, 0, 5, 4, // -y
                2, 6, 7, 2, 7, 3, // +y
            }, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        });

        public static StencilShadow CreateStencil(Transform parent, Box box)
        {
            var go = new GameObject("DarknessVolume") { layer = LayerMask.NameToLayer("Triggers") };
            if (parent != null)
                go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = BoxMesh();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = TarkovSceneTools.DefaultSurfaceMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            // AmbientLight draws it itself; shown, it would be a grey box
            renderer.enabled = false;
            var stencil = go.AddComponent<StencilShadow>();
            stencil.Ambient = DarkAmbient;
            stencil.Culling = new AmbientLight.CullingSettings { Distance = 40f, FadeLength = 5f };
            Apply(stencil, box);
            return stencil;
        }

        // the stencil's box (a unit cube scaled) where box is, in world space
        public static void Apply(StencilShadow stencil, Box box)
        {
            var t = stencil.transform;
            t.SetPositionAndRotation(box.Center, box.Rotation);
            var parentScale = t.parent != null ? t.parent.lossyScale : Vector3.one;
            t.localScale = new Vector3(box.Size.x / parentScale.x, box.Size.y / parentScale.y, box.Size.z / parentScale.z);
            // a big hall is seen from further away (retail: 100 m)
            if (stencil.Culling != null)
                stencil.Culling.Distance = new Vector2(box.Size.x, box.Size.z).magnitude > 17f ? 100f : Mathf.Max(stencil.Culling.Distance, 40f);
        }

        public static Box BoxOf(StencilShadow stencil)
        {
            var t = stencil.transform;
            var mesh = stencil.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
            var b = mesh != null ? mesh.bounds : new Bounds(Vector3.zero, Vector3.one);
            var s = t.lossyScale;
            return new Box { Center = t.TransformPoint(b.center), Rotation = t.rotation, Size = Vector3.Scale(b.size, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z))) };
        }

        public static AnalyticSource CreatePortal(Transform parent, Opening opening, float depth, float width, float height, float floorBelow)
        {
            var go = new GameObject(opening.Door ? "AmbientPortal_Door" : "AmbientPortal_Window");
            if (parent != null)
                go.transform.SetParent(parent, false);
            var helper = new GameObject("Cube");
            helper.transform.SetParent(go.transform, false);
            var source = go.AddComponent<AnalyticSource>();
            source.CubeControlHelper = helper.transform;
            source.Culling = new AmbientLight.CullingSettings { Distance = 30f, FadeLength = 15f };
            SetUp(source, opening, depth, width, height, floorBelow);
            return source;
        }

        // the retail layout around an opening (see the top)
        public static void SetUp(AnalyticSource source, Opening opening, float depth, float width, float height, float floorBelow)
        {
            var t = source.transform;
            var inward = Vector3.ProjectOnPlane(opening.Inward, Vector3.up);
            t.SetPositionAndRotation(opening.Center, Quaternion.LookRotation(inward.sqrMagnitude > 1e-4f ? inward.normalized : Vector3.forward, Vector3.up));
            source.DoorSize = new Vector2(opening.Width * 0.5f, opening.Height * 0.5f);
            source.LightIntensity = 6f;
            source.LightIntensityMultiplicator = opening.Door ? 2.5f : 2f;
            source.FadeNormals = opening.Door ? 0.25f : 0.18f;
            source.AddAmbient = 0f;
            source.IsAdditional = false;
            float inside = Mathf.Clamp(depth, 1f, 4f);
            var size = new Vector3(Mathf.Min(opening.Width + 3.4f, width + 1f), Mathf.Min(opening.Height + 2.2f, height + 1f), inside + 1.3f);
            // centred on the opening across, on the room's middle up and down, from 1.3 m outside
            float roomMiddle = height * 0.5f - floorBelow;
            var shift = new Vector3(0f, Mathf.Clamp(roomMiddle, -size.y * 0.25f, size.y * 0.25f), (inside - 1.3f) * 0.5f);
            if (source.CubeControlHelper != null)
            {
                source.CubeControlHelper.localPosition = shift;
                source.CubeControlHelper.localRotation = Quaternion.identity;
                source.CubeControlHelper.localScale = size;
            }
            source.CubeScale = size * 0.5f;
            source.CubeShift = shift;
            source.FadeOut = new Vector3(Mathf.Max(0.3f, size.x * 0.23f), 1.2f, Mathf.Max(0.5f, size.z * 0.57f));
        }

        // ------------------------------------------------------------ fitting by hand

        public static bool FitToRoom(StencilShadow stencil)
        {
            Physics.SyncTransforms();
            bool backfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = false;
            try
            {
                var renderer = stencil.GetComponent<Renderer>();
                bool was = renderer != null && renderer.enabled;
                var box = BoxOf(stencil);
                var fitted = Fit(box, TarkovIndoorTriggers.MaxRoomSize);
                Undo.RecordObject(stencil.transform, "Fit to room");
                Undo.RecordObject(stencil, "Fit to room");
                Apply(stencil, fitted);
                if (renderer != null)
                    renderer.enabled = was;
                return (fitted.Center - box.Center).sqrMagnitude > 1e-6f || (fitted.Size - box.Size).sqrMagnitude > 1e-6f;
            }
            finally
            {
                Physics.queriesHitBackfaces = backfaces;
                TarkovIndoorTriggers.ResetCache();
            }
        }

        // portals for the stencil's openings to the outside (next to it, under its parent)
        public static int AddPortals(StencilShadow stencil)
        {
            var box = BoxOf(stencil);
            var rooms = Object.FindObjectsOfType<StencilShadow>(true).Select(BoxOf).ToList();
            int made = 0;
            try
            {
                foreach (var opening in Openings(box, rooms))
                {
                    Measure(box, opening, out var depth, out var width, out var height, out var floor);
                    var portal = CreatePortal(stencil.transform.parent, opening, depth, width, height, floor);
                    Undo.RegisterCreatedObjectUndo(portal.gameObject, "Add ambient portals");
                    made++;
                }
            }
            finally
            {
                TarkovIndoorTriggers.ResetCache();
            }
            return made;
        }

        // The portal placed roughly in an opening, facing roughly into the room: the opening's edges found by rays
        // across it, the portal moved to its middle on the inner wall face, the room behind it measured.
        public static bool FitToOpening(AnalyticSource source, out string message)
        {
            int mask = TarkovRainCover.SolidMask;
            Physics.SyncTransforms();
            var t = source.transform;
            var forward = Vector3.ProjectOnPlane(t.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f)
            {
                message = "it faces straight up or down; turn its blue arrow into the room.";
                return false;
            }
            forward.Normalize();
            var right = Vector3.Cross(Vector3.up, forward);
            var start = t.position;
            var o = start;
            try
            {
                float Reach(Vector3 from, Vector3 dir, float max) =>
                    TarkovIndoorTriggers.Cast(from, dir, max, mask, out var hit) ? hit.distance : float.NaN;
                // the place along its arrow, up to a metre either way, that is inside the opening: edges on all four sides,
                // the smallest such gap (in a doorway's thickness)
                float left = 0f, rightD = 0f, up = 0f, down = 0f, best = float.MaxValue;
                for (float d = -1f; d <= 1.001f; d += 0.1f)
                {
                    var at = start + forward * d;
                    float l = Reach(at, -right, 3f), r = Reach(at, right, 3f), u = Reach(at, Vector3.up, 3f), dn = Reach(at, Vector3.down, 3f);
                    if (float.IsNaN(l) || float.IsNaN(r) || float.IsNaN(u) || float.IsNaN(dn) || (l + r) * (u + dn) >= best)
                        continue;
                    best = (l + r) * (u + dn);
                    left = l; rightD = r; up = u; down = dn;
                    o = at;
                }
                if (best == float.MaxValue)
                {
                    message = "no opening around it: put it in a door or window (its edges within 3 m), facing into the room.";
                    return false;
                }
                var centre = o + right * ((rightD - left) * 0.5f) + Vector3.up * ((up - down) * 0.5f);
                float w = left + rightD, h = up + down;
                // the inner wall face: from inside the room, back towards the wall beside the opening; the portal turns to
                // face straight out of that wall
                var beside = centre + right * (w * 0.5f + 0.25f) + forward * 1f;
                if (TarkovIndoorTriggers.Cast(beside, -forward, 2f, mask, out var wall))
                {
                    var normal = Vector3.ProjectOnPlane(wall.normal, Vector3.up);
                    if (normal.sqrMagnitude > 0.5f && Vector3.Dot(normal, forward) > 0.7f)
                    {
                        forward = normal.normalized;
                        right = Vector3.Cross(Vector3.up, forward);
                    }
                    centre += forward * Vector3.Dot(wall.point - centre, forward);
                }
                // the room behind it
                var inside = centre + forward * 0.5f;
                float depth = Reach(inside, forward, 30f);
                var middle = centre + forward * Mathf.Min(1.5f, float.IsNaN(depth) ? 1.5f : depth * 0.5f);
                float across = Reach(middle, -right, 30f) + Reach(middle, right, 30f);
                // the room's floor a metre in (below a window is its sill); a door's bottom is that floor
                float floorBelow = Reach(centre + forward * 1f, Vector3.down, 5f);
                bool door = !float.IsNaN(floorBelow) && Mathf.Abs(floorBelow - h * 0.5f) < 0.3f;
                float roomHeight = floorBelow + Reach(centre + forward * 1f, Vector3.up, 10f);
                Undo.RecordObject(t, "Fit to opening");
                Undo.RecordObject(source, "Fit to opening");
                if (source.CubeControlHelper != null)
                    Undo.RecordObject(source.CubeControlHelper, "Fit to opening");
                var opening = new Opening { Center = centre, Inward = forward, Width = w, Height = h, Door = door };
                SetUp(source, opening, float.IsNaN(depth) ? 4f : depth + 0.5f, float.IsNaN(across) ? w + 3f : across, float.IsNaN(roomHeight) ? h + 1f : roomHeight,
                      float.IsNaN(floorBelow) ? h * 0.5f : floorBelow);
                source.gameObject.name = opening.Door ? "AmbientPortal_Door" : "AmbientPortal_Window";
                message = $"{(opening.Door ? "door" : "window")} {w:0.00} x {h:0.00} m, room {(float.IsNaN(depth) ? "?" : depth.ToString("0.0"))} m deep";
                return true;
            }
            finally
            {
                TarkovIndoorTriggers.ResetCache();
            }
        }

        // ------------------------------------------------------------ Build Map

        public static void Check(Scene scene, GameObject[] roots, TarkovScenePreparer.Report report)
        {
            var stencils = roots.SelectMany(r => r.GetComponentsInChildren<StencilShadow>(true)).ToList();
            var sources = roots.SelectMany(r => r.GetComponentsInChildren<AnalyticSource>(true)).ToList();
            int noRenderer = stencils.Count(s => !s.TryGetComponent<Renderer>(out _));
            if (noRenderer > 0)
                report.Warnings.Add($"{scene.name}: {noRenderer} StencilShadow(s) have no MeshRenderer; the game skips a darkness volume without one " +
                                    "(add a MeshRenderer and switch it off, or make it with GameObject > Tarkov SDK > Lights > Indoor Darkness Volume)");
            int noMesh = stencils.Count(s => !s.TryGetComponent<MeshFilter>(out var f) || f.sharedMesh == null);
            if (noMesh > 0)
                report.Warnings.Add($"{scene.name}: {noMesh} StencilShadow(s) have no mesh; they darken nothing");
            if ((stencils.Count > 0 || sources.Count > 0) && !TarkovMapScenes.MapScenes(scene).Any(s => s.GetRootGameObjects().Any(r => r.GetComponentInChildren<AmbientLight>(true) != null)))
                report.Warnings.Add($"{scene.name}: has darkness volumes or ambient portals but the map has no AmbientLight (the time of day / weather rig), which is what draws them " +
                                    "(Tarkov SDK > Generation > Time > Time of Day / Weather...)");
        }

        // ------------------------------------------------------------ gizmos

        [DrawGizmo(GizmoType.Selected | GizmoType.InSelectionHierarchy)]
        private static void DrawStencil(StencilShadow stencil, GizmoType type)
        {
            var box = BoxOf(stencil);
            Gizmos.matrix = Matrix4x4.TRS(box.Center, box.Rotation, box.Size);
            Gizmos.color = new Color(0.35f, 0.2f, 0.6f, 0.12f);
            Gizmos.DrawCube(Vector3.zero, Vector3.one);
            Gizmos.color = new Color(0.55f, 0.35f, 0.95f, 0.9f);
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
            Gizmos.matrix = Matrix4x4.identity;
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.InSelectionHierarchy | GizmoType.NonSelected | GizmoType.Pickable)]
        private static void DrawSource(AnalyticSource source, GizmoType type)
        {
            bool selected = (type & (GizmoType.Selected | GizmoType.InSelectionHierarchy)) != 0;
            var t = source.transform;
            Gizmos.matrix = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
            // the opening
            Gizmos.color = new Color(1f, 0.85f, 0.4f, selected ? 0.5f : 0.25f);
            Gizmos.DrawCube(Vector3.zero, new Vector3(source.DoorSize.x * 2f, source.DoorSize.y * 2f, 0.01f));
            if (selected)
            {
                // the lit box and the way the light goes
                var shift = source.CubeControlHelper != null ? source.CubeControlHelper.localPosition : source.CubeShift;
                var scale = source.CubeControlHelper != null ? source.CubeControlHelper.localScale : source.CubeScale * 2f;
                Gizmos.color = new Color(1f, 0.85f, 0.4f, 0.8f);
                Gizmos.DrawWireCube(shift, scale);
                Gizmos.DrawLine(Vector3.zero, Vector3.forward * 1.5f);
                Gizmos.DrawLine(Vector3.forward * 1.5f, new Vector3(0.15f, 0f, 1.3f));
                Gizmos.DrawLine(Vector3.forward * 1.5f, new Vector3(-0.15f, 0f, 1.3f));
            }
            Gizmos.matrix = Matrix4x4.identity;
        }
    }

    [CustomEditor(typeof(StencilShadow)), CanEditMultipleObjects]
    public class TarkovStencilShadowEditor : UnityEditor.Editor
    {
        private string _note;

        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("Darkness volume: surfaces inside this closed mesh get Ambient instead of the open sky's ambient light " +
                                    "(retail: black, alpha about 0.8). The game draws it itself; keep its MeshRenderer switched off.", MessageType.None);
            DrawDefaultInspector();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Fit To Room", "Moves each side onto the wall, floor and ceiling in front of it (a box volume).")))
                    EditorApplication.delayCall += () =>
                    {
                        int moved = targets.OfType<StencilShadow>().Count(TarkovIndoorAmbient.FitToRoom);
                        _note = $"{moved} of {targets.Length} fitted";
                        Repaint();
                    };
                if (GUILayout.Button(new GUIContent("Add Ambient Portals", "An ambient portal in every opening of its walls that leads outside.")))
                    EditorApplication.delayCall += () =>
                    {
                        int made = targets.OfType<StencilShadow>().Sum(TarkovIndoorAmbient.AddPortals);
                        _note = $"{made} portal(s) added";
                        Repaint();
                    };
            }
            if (_note != null)
                EditorGUILayout.LabelField(_note, EditorStyles.miniLabel);
        }
    }

    [CustomEditor(typeof(AnalyticSource), true), CanEditMultipleObjects]
    public class TarkovAnalyticSourceEditor : UnityEditor.Editor
    {
        private string _note;

        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("Ambient portal: soft light let in through an opening. Its middle is the opening's middle, its blue arrow points " +
                                    "into the room, Door Size is half the opening's width and height, and the Cube child is the box it lights.", MessageType.None);
            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            if (EditorGUI.EndChangeCheck())
                foreach (var source in targets.OfType<AnalyticSource>())
                    Sync(source);
            if (GUILayout.Button(new GUIContent("Fit To Opening", "Put it roughly in a door or window, facing into the room; this finds the opening's edges and the room behind it.")))
                EditorApplication.delayCall += () =>
                {
                    var notes = new List<string>();
                    foreach (var source in targets.OfType<AnalyticSource>())
                    {
                        TarkovIndoorAmbient.FitToOpening(source, out var message);
                        notes.Add(source.name + ": " + message);
                    }
                    _note = string.Join("\n", notes);
                    Repaint();
                };
            if (_note != null)
                EditorGUILayout.LabelField(_note, EditorStyles.wordWrappedMiniLabel);
        }

        // what UpdateSettings does at Start, so the saved values match the Cube child
        private static void Sync(AnalyticSource source)
        {
            var helper = source.CubeControlHelper;
            if (helper == null)
                return;
            source.CubeScale = helper.localScale * 0.5f;
            source.CubeShift = helper.localPosition;
        }
    }
}
