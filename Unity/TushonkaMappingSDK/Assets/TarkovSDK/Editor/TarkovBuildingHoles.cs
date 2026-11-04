using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using Plan = TarkovSDK.TarkovBuilding;

namespace TarkovSDK.Editor
{
    // Holes (Plan.Hole): boxes in the building's space cut out of its boxes, as ProBuilder's cuts but kept in the plan,
    // so they last through rebuilds. Rough holes are a smaller middle plus boxes round its rim (some only part of the
    // way through: gouges), all inside the hole's size. Each box splits every square box it overlaps into up to six
    // that leave it out; their new faces (the hole's sides) take the face material of their side. The curved walls'
    // and trims' shapes aren't cut: their triangles in the hole are dropped.
    //
    // What the cut took out of walls and slabs can stay as the game's physics props (SmallPhysicsObject, which also
    // makes the physics props): chunks (boxes split down to the chunk size) with a Rigidbody, a Loot-layer box to land
    // on things with and a HighPolyCollider box with a BallisticCollider that bullets push.
    //  - Rubble: the chunks lie on whatever is under the hole (found by raycasts, stacked), as if blown out.
    //  - Breakable: one prop holding the hole's piece (a mesh and its colliders, so the wall looks and blocks as if
    //    whole) as its SolidObject and the chunks (inactive) as its FragmentedObject. SmallPhysicsObject.PlayCollision
    //    swaps them and throws the chunks; it's called by OnBlowUp for explosions within BlowUpThreshold (the client's
    //    PhysicsPropExplosionPatch passes every explosion on: Tarkov never calls it) and by OnHit when bullets may
    //    break it. Touch is off (OnTouchForceMultiplier -1: walking into the wall would break it). Each chunk is a
    //    prop of its own once loose.
    public static partial class TarkovBuildingBuilder
    {
        // at most this many chunks per hole (bigger chunks for a big hole)
        private const int MaxChunks = 120;

        // The hole's boxes: itself, or with Rough a smaller middle and ragged boxes round its rim, all within its size.
        // Its thinnest side is the way through.
        internal static List<Bounds> HoleBoxes(Plan.Hole h)
        {
            var size = new Vector3(Mathf.Max(0.05f, Mathf.Abs(h.Size.x)), Mathf.Max(0.05f, Mathf.Abs(h.Size.y)), Mathf.Max(0.05f, Mathf.Abs(h.Size.z)));
            var boxes = new List<Bounds>();
            if (h.Rough <= 0.01f)
            {
                boxes.Add(new Bounds(h.Center, size));
                return boxes;
            }
            int through = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
            int a = (through + 1) % 3, b = (through + 2) % 3;
            var rng = new System.Random(h.Seed * 7919 + 17);
            float R() => (float)rng.NextDouble();
            var min = h.Center - size * 0.5f;
            var max = h.Center + size * 0.5f;
            var core = size;
            core[a] *= 1f - 0.35f * h.Rough;
            core[b] *= 1f - 0.35f * h.Rough;
            boxes.Add(new Bounds(h.Center, core));
            int n = 4 + Mathf.RoundToInt(10f * h.Rough);
            float face = Mathf.Min(size[a], size[b]);
            for (int k = 0; k < n; k++)
            {
                float angle = Mathf.PI * 2f * (k + R() * 0.8f) / n;
                var c = h.Center;
                c[a] += Mathf.Cos(angle) * core[a] * 0.5f * (0.85f + 0.3f * R());
                c[b] += Mathf.Sin(angle) * core[b] * 0.5f * (0.85f + 0.3f * R());
                var s = Vector3.zero;
                s[a] = face * (0.15f + 0.3f * R()) * (0.6f + h.Rough);
                s[b] = face * (0.15f + 0.3f * R()) * (0.6f + h.Rough);
                s[through] = size[through];
                // a gouge from one face
                if (R() < 0.4f)
                {
                    float depth = size[through] * (0.3f + 0.4f * R());
                    s[through] = depth;
                    c[through] = R() < 0.5f ? min[through] + depth * 0.5f : max[through] - depth * 0.5f;
                }
                var lo = Vector3.Max(c - s * 0.5f, min);
                var hi = Vector3.Min(c + s * 0.5f, max);
                if (hi.x - lo.x > 0.02f && hi.y - lo.y > 0.02f && hi.z - lo.z > 0.02f)
                    boxes.Add(new Bounds((lo + hi) * 0.5f, hi - lo));
            }
            return boxes;
        }

        // the storey a hole's debris is built with: the one its middle is in
        internal static int HoleStorey(Plan plan, Plan.Hole h)
        {
            int s = 0;
            while (s + 1 < plan.Storeys.Count && h.Center.y >= plan.Base(s + 1))
                s++;
            return s;
        }

        private static bool Cuttable(Part p) => p.Poly == null && Quaternion.Angle(p.Rotation, Quaternion.identity) < 0.01f;

        private static bool Overlap(Part p, Bounds box, out Vector3 min, out Vector3 max)
        {
            var h = p.Size * 0.5f;
            min = Vector3.Max(p.Center - h, box.min);
            max = Vector3.Min(p.Center + h, box.max);
            return max.x - min.x > 0.001f && max.y - min.y > 0.001f && max.z - min.z > 0.001f;
        }

        // a box of p between min and max, keeping its faces; faces inside p (the cut's) show from outside
        private static Part Piece(Part p, Vector3 min, Vector3 max)
        {
            var q = new Part
            {
                Center = (min + max) * 0.5f,
                Size = max - min,
                Surface = p.Surface,
                Collide = p.Collide,
                Solid = p.Solid,
                Group = p.Group,
            };
            var pmin = p.Center - p.Size * 0.5f;
            var pmax = p.Center + p.Size * 0.5f;
            for (int f = 0; f < 6; f++)
            {
                q.Faces[f] = p.Faces[f];
                int axis = f / 2;
                bool plus = f % 2 == 0;
                float at = plus ? max[axis] : min[axis];
                bool onOriginal = Mathf.Abs(at - (plus ? pmax[axis] : pmin[axis])) < 1e-4f;
                q.Outside[f] = onOriginal ? p.Outside[f] : true;
            }
            return q;
        }

        // p without the box min..max: slabs either side of it in x, then in y over its x, then in z over both
        private static IEnumerable<Part> Subtract(Part p, Vector3 min, Vector3 max)
        {
            var pmin = p.Center - p.Size * 0.5f;
            var pmax = p.Center + p.Size * 0.5f;
            const float e = 0.001f;
            if (min.x - pmin.x > e) yield return Piece(p, pmin, new Vector3(min.x, pmax.y, pmax.z));
            if (pmax.x - max.x > e) yield return Piece(p, new Vector3(max.x, pmin.y, pmin.z), pmax);
            if (min.y - pmin.y > e) yield return Piece(p, new Vector3(min.x, pmin.y, pmin.z), new Vector3(max.x, min.y, pmax.z));
            if (pmax.y - max.y > e) yield return Piece(p, new Vector3(min.x, max.y, pmin.z), new Vector3(max.x, pmax.y, pmax.z));
            if (min.z - pmin.z > e) yield return Piece(p, new Vector3(min.x, min.y, pmin.z), new Vector3(max.x, max.y, min.z));
            if (pmax.z - max.z > e) yield return Piece(p, new Vector3(min.x, min.y, max.z), new Vector3(max.x, max.y, pmax.z));
        }

        // Cuts every hole out of the boxes and drops the shapes' triangles in them. Returns, per hole, what was taken
        // out of solid, colliding boxes (walls, floors), as boxes keeping their faces.
        private static List<List<Part>> CutHoles(Plan plan, List<Part> parts, List<Shape> shapes, Result result)
        {
            var taken = new List<List<Part>>();
            foreach (var h in plan.Holes ?? new List<Plan.Hole>())
            {
                var mine = new List<Part>();
                taken.Add(mine);
                if (h == null)
                    continue;
                bool cut = false;
                var boxes = HoleBoxes(h);
                foreach (var box in boxes)
                    for (int i = parts.Count - 1; i >= 0; i--)
                    {
                        var p = parts[i];
                        if (!Cuttable(p) || !Overlap(p, box, out var min, out var max))
                            continue;
                        parts.RemoveAt(i);
                        parts.AddRange(Subtract(p, min, max));
                        if (p.Solid && p.Collide != Collide.None)
                            mine.Add(Piece(p, min, max));
                        cut = true;
                    }
                // one with the wall (or floor) still there a centimetre past both its ends, along its way through,
                // cuts out a slice nobody sees
                var hs = new Vector3(Mathf.Abs(h.Size.x), Mathf.Abs(h.Size.y), Mathf.Abs(h.Size.z));
                int through = hs.x <= hs.y && hs.x <= hs.z ? 0 : hs.y <= hs.z ? 1 : 2;
                var end = Vector3.zero;
                end[through] = hs[through] * 0.5f + 0.01f;
                bool buried = parts.Any(q => q.Solid && q.Contains(h.Center - end)) && parts.Any(q => q.Solid && q.Contains(h.Center + end));
                int index = plan.Holes.IndexOf(h) + 1;
                if (cut && buried)
                    result.Notes.Add($"hole {index} is inside a wall or floor without reaching either face, so it shows nothing (Holes tool: select it, Go Right Through)");
                else if (!cut)
                    result.Notes.Add($"hole {index} doesn't touch the building");
                foreach (var s in shapes)
                {
                    var kept = new List<int>();
                    for (int i = 0; i + 2 < s.T.Count; i += 3)
                    {
                        var c = (s.V[s.T[i]] + s.V[s.T[i + 1]] + s.V[s.T[i + 2]]) / 3f;
                        if (!boxes.Any(b => b.Contains(c)))
                            kept.AddRange(new[] { s.T[i], s.T[i + 1], s.T[i + 2] });
                    }
                    if (kept.Count != s.T.Count)
                    {
                        s.T.Clear();
                        s.T.AddRange(kept);
                    }
                }
            }
            return taken;
        }

        // whether a point (building space) is inside one of the holes
        private static bool InHole(List<List<Bounds>> holes, Vector3 p) => holes.Any(h => h.Any(b => b.Contains(p)));

        // a box split along its longest side (35-65% of the way) until no side is over the size
        private static void Fracture(Part p, float size, System.Random rng, List<Part> into)
        {
            int axis = p.Size.x >= p.Size.y && p.Size.x >= p.Size.z ? 0 : p.Size.y >= p.Size.z ? 1 : 2;
            if (p.Size[axis] <= size * 1.3f)
            {
                into.Add(p);
                return;
            }
            float t = 0.35f + 0.3f * (float)rng.NextDouble();
            var min = p.Center - p.Size * 0.5f;
            var max = p.Center + p.Size * 0.5f;
            float cut = Mathf.Lerp(min[axis], max[axis], t);
            var aMax = max;
            aMax[axis] = cut;
            var bMin = min;
            bMin[axis] = cut;
            Fracture(Piece(p, min, aMax), size, rng, into);
            Fracture(Piece(p, bMin, max), size, rng, into);
        }

        // the faces of boxes (those not hidden by the others or by `hiding`) as a mesh round origin, a submesh per
        // material, UVs laid as on the building (from the building-space corners) so a piece matches its wall
        private static Mesh BoxesMesh(string name, List<Part> boxes, List<Part> hiding, Vector3 origin, out Material[] materials)
        {
            var lists = new Dictionary<Material, (List<Vector3> V, List<Vector3> N, List<Vector2> U, List<int> T)>();
            var corners = new Vector3[4];
            var covers = hiding != null ? boxes.Concat(hiding).ToList() : boxes;
            foreach (var p in boxes)
                for (int f = 0; f < 6; f++)
                {
                    if (p.Faces[f] == null)
                        continue;
                    FaceCorners(p, f, corners);
                    if (CullHidden && Covered(p, f, corners, covers))
                        continue;
                    Add(lists, p.Faces[f], corners, Normals[f]);
                }
            var mesh = new Mesh { name = name };
            materials = lists.Keys.ToArray();
            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var u = new List<Vector2>();
            foreach (var m in materials)
            {
                v.AddRange(lists[m].V.Select(x => x - origin));
                n.AddRange(lists[m].N);
                u.AddRange(lists[m].U);
            }
            mesh.SetVertices(v);
            mesh.SetNormals(n);
            mesh.SetUVs(0, u);
            mesh.subMeshCount = materials.Length;
            int offset = 0;
            for (int i = 0; i < materials.Length; i++)
            {
                mesh.SetTriangles(lists[materials[i]].T.Select(t => t + offset).ToList(), i);
                offset += lists[materials[i]].V.Count;
            }
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        // what most of the pieces are, by volume
        private static Plan.Surface MainSurface(List<Part> parts) =>
            parts.GroupBy(p => p.Surface).OrderByDescending(g => g.Sum(p => p.Size.x * p.Size.y * p.Size.z)).Select(g => g.Key).FirstOrDefault();

        // The storey's holes' debris, under Holes in its group: rubble or a breakable piece per hole that has some.
        // Their meshes are one asset per hole (the piece and its chunks as sub-assets).
        private static void Debris(Plan plan, int storey, List<List<Part>> taken, List<Part> parts, Transform group, string prefix, Result result)
        {
            var holes = plan.Holes ?? new List<Plan.Hole>();
            var folder = Path.GetDirectoryName(prefix).Replace('\\', '/');
            string stem = Path.GetFileName(prefix);
            // this group's hole meshes from before
            if (AssetDatabase.IsValidFolder(folder))
                foreach (var guid in AssetDatabase.FindAssets("t:Mesh", new[] { folder }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (Path.GetFileName(path).StartsWith(stem + "Hole") && Path.GetDirectoryName(path).Replace('\\', '/') == folder)
                        AssetDatabase.DeleteAsset(path);
                }
            Transform holder = null;
            var building = Building(plan);
            for (int i = 0; i < holes.Count && i < taken.Count; i++)
            {
                var h = holes[i];
                if (h == null || h.Debris == Plan.DebrisKind.None || taken[i].Count == 0 || HoleStorey(plan, h) != storey)
                    continue;
                var rng = new System.Random(h.Seed * 104729 + 3);
                float volume = taken[i].Sum(p => p.Size.x * p.Size.y * p.Size.z);
                // no more than MaxChunks of them
                float size = Mathf.Max(h.ChunkSize, Mathf.Pow(volume / MaxChunks, 1f / 3f));
                var chunks = new List<Part>();
                foreach (var p in taken[i])
                    Fracture(p, size, rng, chunks);
                holder ??= Child(group, "Holes");
                var root = new GameObject($"Hole_{i + 1}").transform;
                root.SetParent(holder, false);
                var centre = taken[i].Aggregate(Vector3.zero, (s, p) => s + p.Center * (p.Size.x * p.Size.y * p.Size.z)) / Mathf.Max(1e-6f, volume);
                root.localPosition = centre;
                var surface = MainSurface(taken[i]);
                var meshes = new List<Mesh>();
                var chunkParent = root;
                Transform solid = null;
                if (h.Debris == Plan.DebrisKind.Breakable)
                {
                    // the piece as it stood: its faces against the wall hidden, as the wall's own are
                    solid = new GameObject("Solid").transform;
                    solid.SetParent(root, false);
                    var mesh = BoxesMesh($"Hole{i + 1}_Solid", taken[i], parts, centre, out var mats);
                    meshes.Add(mesh);
                    solid.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                    solid.gameObject.AddComponent<MeshRenderer>().sharedMaterials = mats;
                    // it moves out of the way when broken: no static batching, but bots path round it while it stands
#pragma warning disable 618
                    GameObjectUtility.SetStaticEditorFlags(solid.gameObject, StaticEditorFlags.NavigationStatic | StaticEditorFlags.OccludeeStatic);
#pragma warning restore 618
                    var collision = BoxesMesh($"Hole{i + 1}_Collider", taken[i], null, centre, out _);
                    meshes.Add(collision);
                    foreach (var layer in new[] { "LowPolyCollider", "HighPolyCollider" })
                    {
                        var go = new GameObject(layer == "LowPolyCollider" ? "Solid_COLLIDER" : "Solid_BALLISTIC") { layer = LayerMask.NameToLayer(layer) };
                        go.transform.SetParent(solid, false);
                        go.AddComponent<MeshCollider>().sharedMesh = collision;
                        if (layer == "HighPolyCollider")
                            Ballistic(go, surface);
                    }
                    chunkParent = new GameObject("Chunks").transform;
                    chunkParent.SetParent(root, false);
                }
                // rubble: dropped on what's under the hole, lowest first, stacked on each other
                var physics = building.gameObject.scene.GetPhysicsScene();
                int mask = LayerMask.GetMask("LowPolyCollider", "HighPolyCollider", "Terrain", "Default");
                if (h.Debris == Plan.DebrisKind.Rubble)
                    Physics.SyncTransforms();
                var piles = new List<Bounds>();
                int through = h.Size.x <= h.Size.y && h.Size.x <= h.Size.z ? 0 : h.Size.y <= h.Size.z ? 1 : 2;
                var props = new List<SmallPhysicsObject>();
                foreach (var c in h.Debris == Plan.DebrisKind.Rubble ? chunks.OrderBy(c => c.Center.y).ToList() : chunks)
                {
                    var at = c.Center;
                    float yaw = 0f;
                    if (h.Debris == Plan.DebrisKind.Rubble)
                    {
                        // out of a wall to either side, or down through a floor
                        var spread = new Vector3((float)rng.NextDouble() - 0.5f, 0f, (float)rng.NextDouble() - 0.5f) * 0.8f;
                        if (through != 1)
                        {
                            var side = through == 0 ? Vector3.right : Vector3.forward;
                            spread += side * ((rng.NextDouble() < 0.5 ? -1f : 1f) * (0.35f + 0.9f * (float)rng.NextDouble()));
                        }
                        var from = new Vector3(at.x + spread.x, at.y, at.z + spread.z);
                        float rest = HoleStoreyFloor(plan, h);
                        if (physics.Raycast(building.TransformPoint(from), -building.up, out var hit, 60f, mask, QueryTriggerInteraction.Ignore))
                            rest = building.InverseTransformPoint(hit.point).y;
                        var half = c.Size * 0.5f;
                        var placed = new Bounds(new Vector3(from.x, rest + half.y, from.z), c.Size);
                        // on top of what's already there
                        for (int guard = 0; guard < 40; guard++)
                        {
                            var under = piles.Where(q => q.Intersects(placed)).ToList();
                            if (under.Count == 0)
                                break;
                            placed.center = new Vector3(placed.center.x, under.Max(q => q.max.y) + half.y + 0.005f, placed.center.z);
                        }
                        piles.Add(placed);
                        at = placed.center;
                        yaw = (float)rng.NextDouble() * 360f;
                    }
                    var mesh = BoxesMesh($"Hole{i + 1}_Chunk{meshes.Count}", new List<Part> { c }, null, c.Center, out var mats);
                    meshes.Add(mesh);
                    props.Add(Chunk(chunkParent, at - centre, yaw, c, mesh, mats));
                }
                if (h.Debris == Plan.DebrisKind.Breakable)
                {
                    chunkParent.gameObject.SetActive(false);
                    var prop = root.gameObject.AddComponent<SmallPhysicsObject>();
                    prop.SoundType = surface == Plan.Surface.Metal ? SmallPhysicsObject.EPhysicsObjectType.MetalCandlestick : SmallPhysicsObject.EPhysicsObjectType.MediumGeneric;
                    prop.SolidObject = solid.gameObject;
                    prop.FragmentedObject = chunkParent.gameObject;
                    var ballistic = solid.GetComponentInChildren<EFT.Ballistics.BallisticCollider>(true);
                    prop.BallisticColliders.Add(ballistic);
                    prop.Collider = ballistic.GetComponent<Collider>();
                    prop.Rigidbodies.AddRange(props.SelectMany(x => x.Rigidbodies));
                    float mass = props.Count > 0 ? props.Average(x => x.Rigidbodies[0].mass) : 10f;
                    // the whole piece goes at once: each chunk gets the blast's push
                    prop.OnBlowUpForceMultiplier = TarkovPhysicsProps.ExplosionSpeed * mass;
                    prop.OnHitForceMultiplier = h.BreaksWhenShot ? TarkovPhysicsProps.ShotSpeed * mass : -1f;
                    prop.OnTouchForceMultiplier = -1f;
                    prop.BlowUpThreshold = h.BlastRadius;
                }
                // one asset per hole
                var assetPath = $"{prefix}Hole{i + 1}.asset";
                TarkovGeneratedAssets.EnsureFolder(folder);
                AssetDatabase.CreateAsset(meshes[0], assetPath);
                foreach (var m in meshes.Skip(1))
                    AssetDatabase.AddObjectToAsset(m, meshes[0]);
                AssetDatabase.ImportAsset(assetPath);
                result.Chunks += chunks.Count;
            }
        }

        // the floor under a hole when nothing is hit: its storey's floor inside the building, the ground outside
        private static float HoleStoreyFloor(Plan plan, Plan.Hole h)
        {
            int s = HoleStorey(plan, h);
            int x = Mathf.FloorToInt(h.Center.x / plan.CellSize), z = Mathf.FloorToInt(h.Center.z / plan.CellSize);
            return plan.Get(s, x, z) != 0 ? plan.Base(s) : 0f;
        }

        // a chunk: a physics prop of its own (a Rigidbody, a Loot box to land with, a HighPolyCollider box that bullets
        // push), at `at` (its parent's space) turned by yaw
        private static SmallPhysicsObject Chunk(Transform parent, Vector3 at, float yaw, Part c, Mesh mesh, Material[] mats)
        {
            var go = new GameObject("Chunk");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = mats;
            var body = go.AddComponent<Rigidbody>();
            // about concrete's weight, kept within what the props' pushes move
            body.mass = Mathf.Clamp(2000f * c.Size.x * c.Size.y * c.Size.z, 1f, 60f);
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            foreach (var layer in new[] { "Loot", "HighPolyCollider" })
            {
                var shape = new GameObject(layer == "Loot" ? "Collision" : "Hitbox") { layer = LayerMask.NameToLayer(layer) };
                shape.transform.SetParent(go.transform, false);
                var box = shape.AddComponent<BoxCollider>();
                box.size = Vector3.Max(c.Size, Vector3.one * 0.02f);
                if (layer == "HighPolyCollider")
                    Ballistic(shape, c.Surface);
            }
            var prop = go.AddComponent<SmallPhysicsObject>();
            prop.SoundType = c.Surface == Plan.Surface.Metal ? SmallPhysicsObject.EPhysicsObjectType.MetalCandlestick : SmallPhysicsObject.EPhysicsObjectType.MediumGeneric;
            prop.BallisticColliders.Add(go.GetComponentInChildren<EFT.Ballistics.BallisticCollider>(true));
            prop.Rigidbodies.Add(body);
            prop.Collider = prop.BallisticColliders[0].GetComponent<Collider>();
            TarkovPhysicsProps.SetPushSpeeds(prop, TarkovPhysicsProps.ShotSpeed, TarkovPhysicsProps.TouchSpeed, TarkovPhysicsProps.ExplosionSpeed);
            return prop;
        }
    }
}
