using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace TarkovSDK.Editor
{
    // The retail maps' scenes and the objects in them worth importing, for the Asset Importer. Maps are the build's
    // scenes grouped by their folder under Assets/Content/Locations (as DrakiaXYZ's list of map scenes,
    // gist.github.com/DrakiaXYZ/0c392b4ad6781f287e3f281bdc79e70e). Objects are found as the Prefab Library's Extract
    // finds them in an open scene: doors, containers, switches, lamps, windows, mounted guns, explosives and fires by
    // their component, their unit the largest group above them that stays small and holds no second one; then the
    // top-level LOD groups as trees (with the game's TreeWind), buildings (over 12 m) or props. Identical objects
    // (same components, meshes and materials in the same order) are one entry with a count. Particle effects are the
    // largest groups of particle systems with no meshes or other kinds round them; the game's own effects (explosions,
    // impacts: Effects.EmitGrenade's names) come from the effects bundle, one entry per effect object under its Effects
    // prefab, named with the effects that use it. Each scene's materials (what its renderers use) and sounds (what its
    // AudioSources play, and every clip in its own sharedassets file) are entries too, imported as assets rather than
    // prefabs. Data entries are a map's lighting (the scene with its LevelSettings, time of day and ambient light) and
    // its camera's PRISM post-processing (the PrismEffects on LevelSettings' CameraPrefab). Plain data, no UnityEngine.

    public sealed class GameMap
    {
        public string Key;
        public string Name;
        public readonly List<int> Scenes = new List<int>();
    }

    [Serializable]
    public sealed class GameIndexEntry
    {
        public string Category;
        public string Name;
        public int Scene;
        public long Transform;
        public float SizeX, SizeY, SizeZ;
        public int Triangles;
        public int Count;
        public string Signature;
        // an object in an asset bundle (Effects): the bundle and the root GameObject its tree is read from; Scene is -1
        public string Bundle;
        public long Root;
        // a whole game effect: the effect objects it's made of (Transform unused), imported together under one root
        public long[] Parts;
        // a material or sound: the data file it's in and its id there; Detail = its shader, or its format; Seconds = a
        // sound's length
        public string Asset;
        public long AssetId;
        public string Detail;
        public float Seconds;

        // JsonUtility reads a missing array back empty, not null
        public bool IsGroup => Parts != null && Parts.Length > 0;
    }

    // one scene's hierarchy
    public sealed class GameSceneTree
    {
        public sealed class Node
        {
            public long Transform;
            public long GameObject;
            public string Name = "";
            public int Layer;
            public bool Active;
            public int Tag;
            public long Parent;
            public readonly List<long> Children = new List<long>();
            public Vector3 Position;
            public Quaternion Rotation = Quaternion.Identity;
            public Vector3 Scale = Vector3.One;
            public readonly List<(int ClassId, long PathId)> Components = new List<(int, long)>();
            public readonly List<string> Scripts = new List<string>();
            public long Mesh;
            public int MeshFile;
            public List<GamePPtr> Materials;
            public Matrix4x4 World;
        }

        public GameSerializedFile File;
        public readonly Dictionary<long, Node> Nodes = new Dictionary<long, Node>();
        public readonly List<Node> Roots = new List<Node>();

        public static GameSceneTree Read(GameFiles files, GameSerializedFile file, Func<bool> cancelled = null)
        {
            var tree = new GameSceneTree { File = file };
            var byGameObject = new Dictionary<long, Node>();
            var objects = file.ObjectList.OrderBy(o => o.Start).ToList();
            var gameObjects = new Dictionary<long, GameValue>();
            foreach (var info in objects)
            {
                if (cancelled != null && cancelled())
                    return null;
                if (info.ClassId == 1)
                    gameObjects[info.PathId] = file.Read(info.PathId);
                else if (info.ClassId == 4 || info.ClassId == 224)
                {
                    if (info.ClassId == 224)
                        continue; // RectTransform: UI, not wanted
                    var t = file.Read(info.PathId);
                    var node = new Node
                    {
                        Transform = info.PathId,
                        GameObject = t.Ref("m_GameObject").PathId,
                        Parent = t.Ref("m_Father").PathId,
                        Position = V3(t.Floats("m_LocalPosition")),
                        Rotation = Q(t.Floats("m_LocalRotation")),
                        Scale = V3(t.Floats("m_LocalScale")),
                    };
                    foreach (var c in t.List("m_Children"))
                        if (c is GamePPtr p)
                            node.Children.Add(p.PathId);
                    tree.Nodes[info.PathId] = node;
                    byGameObject[node.GameObject] = node;
                }
            }
            foreach (var pair in gameObjects)
                if (byGameObject.TryGetValue(pair.Key, out var node))
                    tree.Fill(files, node, pair.Value);
            foreach (var node in tree.Nodes.Values)
                if (node.Parent == 0 || !tree.Nodes.ContainsKey(node.Parent))
                    tree.Roots.Add(node);
            foreach (var root in tree.Roots)
                tree.SetWorld(root, Matrix4x4.Identity);
            return tree;
        }

        // one prefab-like object and what's under it (an asset file's prefab, e.g. a Resources one)
        public static GameSceneTree ReadFrom(GameFiles files, GameSerializedFile file, long gameObject)
        {
            var tree = new GameSceneTree { File = file };
            var go = file.Read(gameObject, "m_Component");
            long transform = 0;
            foreach (var c in go?.List("m_Component") ?? new List<object>())
            {
                var p = (c as GameValue)?.Ref("component") ?? default;
                var info = file.Info(p.PathId);
                if (info != null && info.ClassId == 4)
                    transform = p.PathId;
            }
            var root = transform != 0 ? tree.AddSubtree(files, transform, 0) : null;
            if (root == null)
                return null;
            root.Parent = 0;
            tree.Roots.Add(root);
            tree.SetWorld(root, Matrix4x4.Identity);
            return tree;
        }

        private Node AddSubtree(GameFiles files, long transform, int depth)
        {
            var t = File.Read(transform);
            if (t == null || depth > 64)
                return null;
            var node = new Node
            {
                Transform = transform,
                GameObject = t.Ref("m_GameObject").PathId,
                Parent = t.Ref("m_Father").PathId,
                Position = V3(t.Floats("m_LocalPosition")),
                Rotation = Q(t.Floats("m_LocalRotation")),
                Scale = V3(t.Floats("m_LocalScale")),
            };
            Nodes[transform] = node;
            var go = File.Read(node.GameObject);
            if (go != null)
                Fill(files, node, go);
            foreach (var c in t.List("m_Children"))
                if (c is GamePPtr p && p.FileId == 0)
                {
                    node.Children.Add(p.PathId);
                    AddSubtree(files, p.PathId, depth + 1);
                }
            return node;
        }

        // the GameObject's name, layer, tag and components
        private void Fill(GameFiles files, Node node, GameValue go)
        {
            var file = File;
            node.Name = go.Str("m_Name");
            node.Layer = go.Int("m_Layer");
            node.Active = go.Bool("m_IsActive");
            node.Tag = go.Int("m_Tag");
            foreach (var c in go.List("m_Component"))
            {
                var p = (c as GameValue)?.Ref("component") ?? default;
                var info = file.Info(p.PathId);
                if (info == null)
                    continue;
                node.Components.Add((info.ClassId, p.PathId));
                if (info.ClassId == 114 && file.ScriptHeader(p.PathId, out _, out _, out var script))
                    node.Scripts.Add(files.Script(file, script).Name ?? "?");
                else if (info.ClassId == 33)
                {
                    var filter = file.Read(p.PathId);
                    var mesh = filter.Ref("m_Mesh");
                    node.Mesh = mesh.PathId;
                    node.MeshFile = mesh.FileId;
                }
                else if (info.ClassId == 23)
                {
                    var renderer = file.Read(p.PathId, "m_Materials");
                    node.Materials = renderer.List("m_Materials").OfType<GamePPtr>().ToList();
                }
                else if (info.ClassId == 137)
                {
                    var skinned = file.Read(p.PathId, "m_Materials", "m_Mesh");
                    node.Materials = skinned.List("m_Materials").OfType<GamePPtr>().ToList();
                    node.Mesh = skinned.Ref("m_Mesh").PathId;
                    node.MeshFile = skinned.Ref("m_Mesh").FileId;
                }
            }
        }

        private void SetWorld(Node node, Matrix4x4 parent)
        {
            node.World = Matrix4x4.CreateScale(node.Scale) * Matrix4x4.CreateFromQuaternion(node.Rotation) * Matrix4x4.CreateTranslation(node.Position) * parent;
            foreach (var child in node.Children)
                if (Nodes.TryGetValue(child, out var c))
                    SetWorld(c, node.World);
        }

        public Node Get(long transform) => Nodes.TryGetValue(transform, out var node) ? node : null;

        // the node and everything under it, parents first
        public IEnumerable<Node> Subtree(Node node)
        {
            var stack = new Stack<Node>();
            stack.Push(node);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                yield return n;
                for (int i = n.Children.Count - 1; i >= 0; i--)
                    if (Nodes.TryGetValue(n.Children[i], out var c))
                        stack.Push(c);
            }
        }

        private static Vector3 V3(float[] f) => f != null && f.Length >= 3 ? new Vector3(f[0], f[1], f[2]) : Vector3.Zero;
        private static Quaternion Q(float[] f) => f != null && f.Length >= 4 ? new Quaternion(f[0], f[1], f[2], f[3]) : Quaternion.Identity;
    }

    public static class GameIndex
    {
        public const float UnitSize = 8f;
        public const float BuildingSize = 12f;

        public static readonly string[] Categories =
            { "Doors", "Containers", "Switches", "Lamps", "Windows", "Mounted Guns", "Explosives", "Fires", "Particles", "Trees", "Props", "Buildings", "Materials", "Sounds", "Data" };

        // the kinds imported as assets (or applied), not prefabs
        public const string Materials = "Materials";
        public const string Sounds = "Sounds";
        public const string Data = "Data";
        public static bool IsAsset(GameIndexEntry e) => e.Category == Materials || e.Category == Sounds || e.Category == Data;
        public static bool IsLighting(GameIndexEntry e) => e.Category == Data && e.Signature.StartsWith("Data|lighting");

        // the components a map's lighting is made of
        public static readonly string[] LightingScripts = { "LevelSettings", "TOD_Sky", "EFT.Weather.TODSkySimple", "EFT.Weather.WeatherController", "AmbientLight" };

        // the game's effects (explosions, impacts, flashes) and the bundles its materials' shaders and textures are in
        public const string EffectsBundle = "assets/systems/effects/particlesystems/effects.bundle";
        public static readonly string[] EffectsDependencies = { "shaders", "defaultmaterial", "assets/content/textures/holemanager/glow_particle_bright.bundle" };
        public const string EffectsKey = "Effects";

        public static GameSerializedFile OpenEffects(GameFiles files)
        {
            var file = files.OpenBundle(EffectsBundle);
            foreach (var dependency in EffectsDependencies)
                files.OpenBundle(dependency);
            return file;
        }

        // the Effects prefab's root GameObject
        public static long EffectsRoot(GameFiles files, GameSerializedFile file)
        {
            foreach (var info in file.ObjectList)
                if (info.ClassId == 1 && file.Read(info.PathId, "m_Component", "m_Layer", "m_Name")?.Str("m_Name") == "Effects")
                {
                    var go = file.Read(info.PathId, "m_Component");
                    foreach (var c in go.List("m_Component"))
                    {
                        var p = (c as GameValue)?.Ref("component") ?? default;
                        var t = file.Info(p.PathId);
                        if (t != null && t.ClassId == 4 && file.Read(p.PathId).Ref("m_Father").IsNull)
                            return info.PathId;
                    }
                }
            return 0;
        }

        // one entry per effect object under the Effects prefab, named with the game effects that use it
        public static List<GameIndexEntry> ScanEffects(GameFiles files)
        {
            var entries = new List<GameIndexEntry>();
            var file = OpenEffects(files);
            if (file == null)
                return entries;
            long rootGo = EffectsRoot(files, file);
            var tree = rootGo != 0 ? GameSceneTree.ReadFrom(files, file, rootGo) : null;
            if (tree == null)
                return entries;
            var root = tree.Roots[0];
            var top = new Dictionary<long, GameSceneTree.Node>();
            foreach (var child in root.Children)
                if (tree.Get(child) is GameSceneTree.Node c)
                    foreach (var n in tree.Subtree(c))
                        top[n.GameObject] = c;
            var uses = new Dictionary<long, SortedSet<string>>();
            // each game effect's objects, in the order it lists them
            var effectParts = new List<(string Name, List<long> Parts)>();
            foreach (var (classId, pathId) in root.Components)
            {
                if (classId != 114)
                    continue;
                var effects = file.Read(pathId);
                foreach (var effect in effects?.List("EffectsArray").OfType<GameValue>() ?? Enumerable.Empty<GameValue>())
                {
                    var parts = new List<long>();
                    effectParts.Add((effect.Str("Name"), parts));
                    // explosions keep their particle systems under a BasicParticleSystemMediator, the rest list them
                    var refs = effect.List("Particles").OfType<GameValue>().Select(particle => particle.Ref("Particle")).ToList();
                    refs.Insert(0, effect.Ref("BasicParticleSystemMediator"));
                    foreach (var p in refs)
                    {
                        var info = file.Info(p.PathId);
                        if (info == null)
                            continue;
                        long go = info.ClassId == 1 ? p.PathId : file.Read(p.PathId, "m_GameObject")?.Ref("m_GameObject").PathId ?? 0;
                        if (top.TryGetValue(go, out var owner))
                        {
                            if (!uses.TryGetValue(owner.Transform, out var names))
                                uses[owner.Transform] = names = new SortedSet<string>();
                            names.Add(effect.Str("Name"));
                            if (!parts.Contains(owner.Transform))
                                parts.Add(owner.Transform);
                        }
                    }
                }
            }
            // the game's effects whole (Gas_explosion, Grenade_new ...), then the objects they're made of
            foreach (var (name, parts) in effectParts.Where(e => e.Parts.Count > 0).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                entries.Add(new GameIndexEntry
                {
                    Category = "Particles",
                    Name = Clean(name),
                    Scene = -1,
                    Bundle = EffectsBundle,
                    Root = rootGo,
                    Parts = parts.ToArray(),
                    Count = parts.Count,
                    Signature = "Particles|effect_" + Clean(name),
                });
            foreach (var child in root.Children)
            {
                var node = tree.Get(child);
                if (node == null || !tree.Subtree(node).Any(n => n.Components.Any(c => c.ClassId == 198)))
                    continue;
                string name = Clean(node.Name);
                if (uses.TryGetValue(node.Transform, out var names))
                    name += " (" + string.Join(", ", names.Take(3)) + (names.Count > 3 ? ", ..." : "") + ")";
                entries.Add(new GameIndexEntry
                {
                    Category = "Particles",
                    Name = name,
                    Scene = -1,
                    Bundle = EffectsBundle,
                    Root = rootGo,
                    Transform = node.Transform,
                    Count = names?.Count ?? 0,
                    Signature = "Particles|fx" + node.Transform.ToString("x"),
                });
            }
            return entries;
        }

        // the kinds found by component (the Prefab Library's): category, how big a unit may get
        public static readonly (string Category, float MaxSize)[] Kinds =
        {
            ("Doors", UnitSize), ("Containers", UnitSize), ("Switches", 3f), ("Lamps", UnitSize), ("Windows", UnitSize),
            ("Mounted Guns", UnitSize), ("Explosives", 4f), ("Fires", 6f),
        };

        private static readonly (string Folder, string Name)[] MapFolders =
        {
            ("Custom", "Customs"), ("Factory_Rework", "Factory"), ("Factory", "Factory"), ("Shopping_Mall", "Interchange"),
            ("Laboratory", "Labs"), ("Labyrinth", "Labyrinth"), ("Lighthouse", "Lighthouse"), ("Reserve_Base", "Reserve"),
            ("shorline", "Shoreline"), ("City", "Streets"), ("Woods", "Woods"), ("Sandbox", "Ground Zero"), ("bunker", "Hideout"),
        };

        // Arena: a defunct debug map
        private static readonly string[] SkippedFolders = { "Arena" };

        // the retail maps, in a fixed order, from the build's scene list
        public static List<GameMap> Maps(List<string> scenes)
        {
            var maps = new Dictionary<string, GameMap>();
            for (int i = 0; i < scenes.Count; i++)
            {
                var m = Regex.Match(scenes[i], @"^Assets/Content/Locations/([^/]+)/", RegexOptions.IgnoreCase);
                if (!m.Success)
                    continue;
                var folder = m.Groups[1].Value;
                if (SkippedFolders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var known = MapFolders.FirstOrDefault(f => string.Equals(f.Folder, folder, StringComparison.OrdinalIgnoreCase));
                string name = known.Name ?? folder;
                if (!maps.TryGetValue(name, out var map))
                    maps[name] = map = new GameMap { Key = name.Replace(" ", ""), Name = name };
                map.Scenes.Add(i);
            }
            var order = MapFolders.Select(f => f.Name).Distinct().ToList();
            return maps.Values.OrderBy(m => order.IndexOf(m.Name) < 0 ? 99 : order.IndexOf(m.Name)).ThenBy(m => m.Name).ToList();
        }

        public static string SceneName(List<string> scenes, int index)
        {
            if (index < 0 || index >= scenes.Count)
                return "level" + index;
            var path = scenes[index];
            int slash = path.LastIndexOf('/');
            return path.Substring(slash + 1).Replace(".unity", "");
        }

        private struct Box
        {
            public Vector3 Min, Max;
            public bool Valid;

            public void Add(Box other)
            {
                if (!other.Valid)
                    return;
                if (!Valid)
                {
                    this = other;
                    return;
                }
                Min = Vector3.Min(Min, other.Min);
                Max = Vector3.Max(Max, other.Max);
            }

            public float Largest => Valid ? Math.Max(Max.X - Min.X, Math.Max(Max.Y - Min.Y, Max.Z - Min.Z)) : 0f;
        }

        private sealed class MeshInfo
        {
            public string Name;
            public Vector3 Center, Extent;
            public int Triangles;
        }

        // the scene's entries; kindOf maps a script class to its category (Doors ...) or null
        public static List<GameIndexEntry> Scan(GameFiles files, int sceneIndex, Func<string, string> kindOf, Func<bool> cancelled = null)
        {
            var entries = new List<GameIndexEntry>();
            var file = files.Get("level" + sceneIndex);
            if (file == null)
                return entries;
            var tree = GameSceneTree.Read(files, file, cancelled);
            if (tree == null)
                return entries;

            var meshes = new Dictionary<string, MeshInfo>();
            var materialNames = new Dictionary<string, string>();
            MeshInfo Mesh(GameSceneTree.Node node)
            {
                if (node.Mesh == 0)
                    return null;
                var pptr = new GamePPtr { FileId = node.MeshFile, PathId = node.Mesh };
                string key = file.ExternalName(pptr.FileId) + ":" + pptr.PathId;
                if (meshes.TryGetValue(key, out var info))
                    return info;
                info = null;
                try
                {
                    var mesh = files.Read(file, pptr, "m_Name", "m_SubMeshes", "m_LocalAABB");
                    if (mesh != null)
                    {
                        var aabb = mesh.Obj("m_LocalAABB");
                        long indices = 0;
                        foreach (var s in mesh.List("m_SubMeshes"))
                            if (s is GameValue sub)
                                indices += sub.Long("indexCount");
                        info = new MeshInfo
                        {
                            Name = mesh.Str("m_Name"),
                            Center = V3(aabb?.Floats("m_Center")),
                            Extent = V3(aabb?.Floats("m_Extent")),
                            Triangles = (int)(indices / 3),
                        };
                    }
                }
                catch (Exception)
                {
                    info = null;
                }
                meshes[key] = info;
                return info;
            }
            string MaterialName(GamePPtr pptr)
            {
                string key = file.ExternalName(pptr.FileId) + ":" + pptr.PathId;
                if (!materialNames.TryGetValue(key, out var name))
                {
                    try
                    {
                        name = pptr.IsNull ? "-" : files.Read(file, pptr, "m_Name")?.Str("m_Name") ?? "?";
                    }
                    catch (Exception)
                    {
                        name = "?";
                    }
                    materialNames[key] = name;
                }
                return name;
            }

            // per node: its renderers' world box and how many of each kind sit under it
            var boxes = new Dictionary<long, Box>();
            var kinds = new Dictionary<long, Dictionary<string, int>>();
            var nodeKind = new Dictionary<long, string>();
            foreach (var node in tree.Nodes.Values)
                foreach (var script in node.Scripts)
                {
                    var kind = kindOf(script);
                    if (kind != null)
                    {
                        nodeKind[node.Transform] = kind;
                        break;
                    }
                }
            void Gather(GameSceneTree.Node node)
            {
                var box = new Box();
                var counts = new Dictionary<string, int>();
                if (node.Components.Any(c => c.ClassId == 23) && Mesh(node) is MeshInfo mesh)
                    box = WorldBox(mesh, node.World);
                if (nodeKind.TryGetValue(node.Transform, out var k))
                    counts[k] = 1;
                foreach (var child in node.Children)
                {
                    var c = tree.Get(child);
                    if (c == null)
                        continue;
                    Gather(c);
                    box.Add(boxes[c.Transform]);
                    foreach (var pair in kinds[c.Transform])
                        counts[pair.Key] = counts.TryGetValue(pair.Key, out var n) ? n + pair.Value : pair.Value;
                }
                boxes[node.Transform] = box;
                kinds[node.Transform] = counts;
            }
            foreach (var root in tree.Roots)
                Gather(root);

            var taken = new HashSet<long>();
            bool InTaken(GameSceneTree.Node node)
            {
                for (var n = node; n != null; n = tree.Get(n.Parent))
                    if (taken.Contains(n.Transform))
                        return true;
                return false;
            }
            var units = new List<(string Category, GameSceneTree.Node Root)>();
            foreach (var (category, maxSize) in Kinds)
                foreach (var node in tree.Nodes.Values.Where(n => nodeKind.TryGetValue(n.Transform, out var k) && k == category).OrderBy(n => n.Transform))
                {
                    var unit = node;
                    for (var parent = tree.Get(node.Parent); parent != null; parent = tree.Get(parent.Parent))
                    {
                        if (tree.Get(parent.Parent) == null && parent.Children.Count > 8)
                            break;
                        if (kinds[parent.Transform].TryGetValue(category, out var n) && n > 1)
                            break;
                        if (boxes[parent.Transform].Largest > maxSize)
                            break;
                        unit = parent;
                    }
                    if (InTaken(unit))
                        continue;
                    taken.Add(unit.Transform);
                    units.Add((category, unit));
                }
            // particle effects: the largest group above particle systems with no meshes, LOD groups or other kinds in it
            foreach (var node in tree.Nodes.Values.Where(n => n.Components.Any(c => c.ClassId == 198)).OrderBy(n => n.Transform))
            {
                if (InTaken(node))
                    continue;
                var unit = node;
                for (var parent = tree.Get(node.Parent); parent != null; parent = tree.Get(parent.Parent))
                {
                    if (tree.Get(parent.Parent) == null && parent.Children.Count > 8)
                        break;
                    if (boxes[parent.Transform].Valid || kinds[parent.Transform].Count > 0 || parent.Components.Any(c => c.ClassId == 205))
                        break;
                    unit = parent;
                }
                if (boxes[unit.Transform].Valid || InTaken(unit))
                    continue;
                taken.Add(unit.Transform);
                units.Add(("Particles", unit));
            }
            foreach (var node in tree.Nodes.Values.OrderBy(n => n.Transform))
            {
                if (!node.Components.Any(c => c.ClassId == 205) || InTaken(node))
                    continue;
                bool nested = false;
                for (var p = tree.Get(node.Parent); p != null && !nested; p = tree.Get(p.Parent))
                    nested = p.Components.Any(c => c.ClassId == 205);
                if (nested)
                    continue;
                bool isTree = tree.Subtree(node).Any(n => n.Scripts.Contains("EFT.SpeedTree.TreeWind"));
                string category = isTree ? "Trees" : boxes[node.Transform].Largest > BuildingSize ? "Buildings" : "Props";
                taken.Add(node.Transform);
                units.Add((category, node));
            }

            var bySignature = new Dictionary<string, GameIndexEntry>();
            foreach (var (category, root) in units)
            {
                var parts = new StringBuilder();
                int triangles = 0;
                foreach (var n in tree.Subtree(root))
                {
                    foreach (var c in n.Components)
                        parts.Append(c.ClassId).Append(',');
                    foreach (var s in n.Scripts)
                        parts.Append(s).Append(',');
                    var mesh = n.Mesh != 0 ? Mesh(n) : null;
                    if (mesh != null)
                    {
                        parts.Append(':').Append(mesh.Name).Append(mesh.Triangles);
                        if (!n.Name.Contains("LOD") || n.Name.Contains("LOD0"))
                            triangles += mesh.Triangles;
                    }
                    if (n.Materials != null)
                        foreach (var m in n.Materials)
                            parts.Append('+').Append(MaterialName(m));
                    parts.Append('/');
                }
                // nothing to see (a controller without a model, a bare trigger, a car light's flare quad)
                if (category != "Particles" && (triangles == 0 || boxes[root.Transform].Largest < 0.05f))
                    continue;
                string signature = category + "|" + Hash(parts.ToString());
                if (bySignature.TryGetValue(signature, out var known))
                {
                    known.Count++;
                    continue;
                }
                var box = boxes[root.Transform];
                var size = box.Valid ? box.Max - box.Min : Vector3.Zero;
                var entry = new GameIndexEntry
                {
                    Category = category,
                    Name = Clean(root.Name),
                    Scene = sceneIndex,
                    Transform = root.Transform,
                    SizeX = size.X,
                    SizeY = size.Y,
                    SizeZ = size.Z,
                    Triangles = triangles,
                    Count = 1,
                    Signature = signature,
                };
                bySignature[signature] = entry;
                entries.Add(entry);
            }
            AddAssets(files, file, tree, sceneIndex, entries);
            AddData(files, file, tree, sceneIndex, entries);
            return entries;
        }

        // the scene's lighting, if it has the map's LevelSettings, and the PRISM preset of LevelSettings' camera
        private static void AddData(GameFiles files, GameSerializedFile file, GameSceneTree tree, int sceneIndex, List<GameIndexEntry> entries)
        {
            var found = new HashSet<string>();
            long level = 0;
            foreach (var node in tree.Nodes.Values)
                foreach (var script in node.Scripts)
                    if (LightingScripts.Contains(script))
                    {
                        found.Add(script);
                        if (script == "LevelSettings")
                            foreach (var (classId, pathId) in node.Components)
                                if (classId == 114 && file.ScriptHeader(pathId, out _, out _, out var sc) && files.Script(file, sc).Name == "LevelSettings")
                                    level = pathId;
                    }
            if (level == 0)
                return;
            string sceneName = SceneName(files.Scenes(), sceneIndex);
            entries.Add(new GameIndexEntry
            {
                Category = Data,
                Name = "Lighting: " + sceneName,
                Scene = sceneIndex,
                Asset = file.Name,
                Detail = found.Contains("TOD_Sky") ? "full weather: TOD sky, weather, ambient light, LevelSettings"
                    : found.Contains("EFT.Weather.TODSkySimple") ? "simple rig: TODSkySimple, ambient light, LevelSettings" : "LevelSettings only",
                Count = 1,
                Signature = $"Data|lighting|{file.Name}",
            });
            try
            {
                // LevelSettings' first field is its CameraPrefab; a Camera's first its GameObject
                var camera = file.ReadScript(level, out _, (r, size) => r.PPtr());
                if (camera.IsNull || !files.Resolve(file, camera, out var cf, out var ci))
                    return;
                var raw = cf.RawBytes(ci.Start, 12);
                long goId = BitConverter.ToInt64(raw, 4);
                var go = cf.Read(goId, "m_Component", "m_Layer", "m_Name");
                foreach (var c in go?.List("m_Component").OfType<GameValue>() ?? Enumerable.Empty<GameValue>())
                {
                    var p = c.Ref("component");
                    var info = cf.Info(p.PathId);
                    if (info == null || info.ClassId != 114 || !cf.ScriptHeader(p.PathId, out _, out _, out var sc) || files.Script(cf, sc).Name != "PrismEffects")
                        continue;
                    entries.Add(new GameIndexEntry
                    {
                        Category = Data,
                        Name = "PRISM: " + sceneName,
                        Scene = sceneIndex,
                        Asset = cf.Name,
                        AssetId = p.PathId,
                        Detail = cf.Name == "resources.assets" ? "the game's default camera's post-processing" : "the map camera's post-processing",
                        Count = 1,
                        Signature = $"Data|prism|{cf.Name}:{p.PathId}",
                    });
                }
            }
            catch (Exception)
            {
            }
        }

        private static readonly string[] AudioFormats = { "PCM", "Vorbis", "ADPCM", "MP3" };

        // the scene's materials (by how many renderers use each) and sounds (its AudioSources' clips, then every clip in
        // the scene's own sharedassets file: ambience, sound banks' clips)
        private static void AddAssets(GameFiles files, GameSerializedFile file, GameSceneTree tree, int sceneIndex, List<GameIndexEntry> entries)
        {
            var used = new Dictionary<string, (GameSerializedFile File, long PathId, int Count)>();
            foreach (var node in tree.Nodes.Values)
                if (node.Materials != null)
                    foreach (var m in node.Materials)
                    {
                        if (m.IsNull || !files.Resolve(file, m, out var f, out var info) || info.ClassId != 21)
                            continue;
                        string key = f.Name + ":" + info.PathId;
                        used[key] = used.TryGetValue(key, out var known) ? (f, info.PathId, known.Count + 1) : (f, info.PathId, 1);
                    }
            foreach (var (f, pathId, count) in used.Values)
            {
                GameValue m;
                string shader;
                try
                {
                    m = f.Read(pathId, "m_Name", "m_Shader");
                    shader = GameModelReader.ShaderName(files, f, m.Ref("m_Shader")) ?? "";
                }
                catch (Exception)
                {
                    continue;
                }
                // impostors and the game's hidden helper materials aren't worth a cell
                if (m == null || GameModelReader.IsImpostor(shader) || shader.StartsWith("Hidden/") || shader == "shadow")
                    continue;
                entries.Add(new GameIndexEntry
                {
                    Category = Materials,
                    Name = Clean(m.Str("m_Name")),
                    Scene = sceneIndex,
                    Asset = f.Name,
                    AssetId = pathId,
                    Detail = shader,
                    Count = count,
                    Signature = $"{Materials}|{f.Name}:{pathId}",
                });
            }

            var clips = new Dictionary<string, (GameSerializedFile File, long PathId, int Count)>();
            foreach (var node in tree.Nodes.Values)
                foreach (var (classId, pathId) in node.Components)
                {
                    if (classId != 82)
                        continue;
                    var clip = file.Read(pathId, "m_GameObject", "m_Enabled", "OutputAudioMixerGroup", "m_audioClip")?.Ref("m_audioClip") ?? default;
                    if (clip.IsNull || !files.Resolve(file, clip, out var f, out var info) || info.ClassId != 83)
                        continue;
                    string key = f.Name + ":" + info.PathId;
                    clips[key] = clips.TryGetValue(key, out var known) ? (f, info.PathId, known.Count + 1) : (f, info.PathId, 1);
                }
            var own = files.Get($"sharedassets{sceneIndex}.assets");
            if (own != null)
                foreach (var info in own.ObjectList)
                    if (info.ClassId == 83 && !clips.ContainsKey(own.Name + ":" + info.PathId))
                        clips[own.Name + ":" + info.PathId] = (own, info.PathId, 0);
            foreach (var (f, pathId, count) in clips.Values)
            {
                GameValue c;
                try
                {
                    c = f.Read(pathId);
                }
                catch (Exception)
                {
                    continue;
                }
                if (c == null || (c.Obj("m_Resource")?.Long("m_Size") ?? 0) <= 0)
                    continue;
                int format = c.Int("m_CompressionFormat");
                entries.Add(new GameIndexEntry
                {
                    Category = Sounds,
                    Name = Clean(c.Str("m_Name")),
                    Scene = sceneIndex,
                    Asset = f.Name,
                    AssetId = pathId,
                    Detail = $"{(format >= 0 && format < AudioFormats.Length ? AudioFormats[format] : "format " + format)}, {(c.Int("m_Channels") == 1 ? "mono" : c.Int("m_Channels") == 2 ? "stereo" : c.Int("m_Channels") + " channels")}, {c.Int("m_Frequency")} Hz",
                    Seconds = c.Float("m_Length"),
                    Count = count,
                    Signature = $"{Sounds}|{f.Name}:{pathId}",
                });
            }
        }

        private static Box WorldBox(MeshInfo mesh, Matrix4x4 world)
        {
            var box = new Box();
            for (int i = 0; i < 8; i++)
            {
                var corner = mesh.Center + new Vector3((i & 1) == 0 ? -mesh.Extent.X : mesh.Extent.X, (i & 2) == 0 ? -mesh.Extent.Y : mesh.Extent.Y,
                    (i & 4) == 0 ? -mesh.Extent.Z : mesh.Extent.Z);
                var p = Vector3.Transform(corner, world);
                box.Add(new Box { Min = p, Max = p, Valid = true });
            }
            return box;
        }

        private static Vector3 V3(float[] f) => f != null && f.Length >= 3 ? new Vector3(f[0], f[1], f[2]) : Vector3.Zero;

        // "Crate_02 (17)" -> "Crate_02"
        public static string Clean(string name)
        {
            name = Regex.Replace(name ?? "", @"\s*\(\d+\)$", "");
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "Object" : name.Trim();
        }

        private static string Hash(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in s)
            {
                h ^= c;
                h *= 1099511628211UL;
            }
            return h.ToString("x16");
        }
    }
}
