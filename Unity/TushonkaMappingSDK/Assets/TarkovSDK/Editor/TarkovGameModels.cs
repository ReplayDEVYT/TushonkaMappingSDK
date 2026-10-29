using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace TarkovSDK.Editor
{
    // An object's look read from the game's files as plain data (vertex streams, index buffer, small texture mips,
    // matrices), so the reading and decoding run on a background thread and Unity only makes the meshes and textures
    // from the bytes (TarkovGameAssets.CreateMesh / CreateModel). No UnityEngine here.

    public sealed class GameMeshData
    {
        public string Name;
        public int VertexCount;
        // VertexAttribute, VertexAttributeFormat, dimension, stream
        public (int Attribute, int Format, int Dimension, int Stream)[] Attributes;
        public byte[][] Streams;
        public bool Wide;
        public byte[] Indices;
        public GameSubMesh[] SubMeshes;
        public float[] Center, Extent;
    }

    public struct GameSubMesh
    {
        public int IndexStart, IndexCount, Topology, BaseVertex, FirstVertex, VertexCount;
        public float[] Center, Extent;
    }

    public sealed class GameTextureData
    {
        public string Name;
        public int Width, Height, Format, Mips;
        public byte[] Data;
        public int Filter, Aniso, WrapU, WrapV;
        public float MipBias;
    }

    // a material as the previews draw it: its shader's name, main texture and colour
    public sealed class GameMaterialData
    {
        public string Key;
        // a particle system's material (previewed on Unity's particle shaders)
        public bool Particle;
        public string Name;
        public string ShaderName;
        public int Queue;
        public string MainKey;
        public GameTextureData Main;
        public float[] MainScale, MainOffset;
        public float[] Color;
        public float Cutoff = -1f;
    }

    public sealed class GamePartData
    {
        public string MeshKey;
        public GameMeshData Mesh;
        public Matrix4x4 Matrix;
        public GameMaterialData[] Materials;
    }

    public sealed class GameModelData
    {
        public readonly List<GamePartData> Parts = new List<GamePartData>();
        public string Error;
    }

    // Recently read scenes, shared by the importer and the preview thread
    public static class GameSceneCache
    {
        private const int Keep = 8;
        private static readonly Dictionary<string, GameSceneTree> Trees = new Dictionary<string, GameSceneTree>();
        private static readonly List<string> Order = new List<string>();

        public static GameSceneTree Get(GameFiles files, int scene)
        {
            string key = files.DataFolder + "|" + scene;
            lock (Trees)
                if (Trees.TryGetValue(key, out var known))
                {
                    Order.Remove(key);
                    Order.Add(key);
                    return known;
                }
            // read outside the lock: a scene can take a second
            var file = files.Get("level" + scene);
            var tree = file != null ? GameSceneTree.Read(files, file) : null;
            lock (Trees)
            {
                Trees[key] = tree;
                Order.Remove(key);
                Order.Add(key);
                while (Order.Count > Keep)
                {
                    Trees.Remove(Order[0]);
                    Order.RemoveAt(0);
                }
            }
            return tree;
        }

        // an asset bundle's prefab (the Effects)
        public static GameSceneTree GetBundle(GameFiles files, string bundle, long rootGameObject)
        {
            string key = files.DataFolder + "|" + bundle + "|" + rootGameObject;
            lock (Trees)
                if (Trees.TryGetValue(key, out var known))
                    return known;
            var file = bundle == GameIndex.EffectsBundle ? GameIndex.OpenEffects(files) : files.OpenBundle(bundle);
            var tree = file != null ? GameSceneTree.ReadFrom(files, file, rootGameObject) : null;
            lock (Trees)
                Trees[key] = tree;
            return tree;
        }

        // the tree an index entry is in
        public static GameSceneTree For(GameFiles files, GameIndexEntry entry) =>
            string.IsNullOrEmpty(entry.Bundle) ? Get(files, entry.Scene) : GetBundle(files, entry.Bundle, entry.Root);

        public static void Clear()
        {
            lock (Trees)
            {
                Trees.Clear();
                Order.Clear();
            }
        }
    }

    public static class GameModelReader
    {
        private static readonly int[] FormatSizes = { 4, 2, 1, 1, 2, 2, 1, 1, 2, 2, 4, 4 };

        public static int FormatSize(int format) => format >= 0 && format < FormatSizes.Length ? FormatSizes[format] : 4;

        private static float[] Center(GameValue aabb) => aabb?.Floats("m_Center") ?? new float[3];
        private static float[] Extent(GameValue aabb) => aabb?.Floats("m_Extent") ?? new float[3];

        // the mesh's vertices as streams in the layout Unity makes for its attributes (skin weights dropped)
        public static GameMeshData Mesh(GameFiles files, GameSerializedFile from, GamePPtr pptr, out string error)
        {
            error = null;
            if (!files.Resolve(from, pptr, out var file, out var info) || info.ClassId != 43)
            {
                error = "not a mesh";
                return null;
            }
            try
            {
                var m = file.Read(info.PathId);
                if (m.Int("m_MeshCompression") != 0)
                {
                    error = "a compressed mesh";
                    return null;
                }
                var vd = m.Obj("m_VertexData");
                int count = vd.Int("m_VertexCount");
                var data = vd.Bytes("m_DataSize");
                if ((data == null || data.Length == 0) && count > 0)
                    data = files.Stream(file, m.Obj("m_StreamData"));
                if (data == null || count == 0)
                {
                    error = "no vertices";
                    return null;
                }
                var channels = vd.List("m_Channels").OfType<GameValue>()
                    .Select(c => (Stream: c.Int("stream"), Offset: c.Int("offset"), Format: c.Int("format"), Dimension: c.Int("dimension") & 0xF)).ToList();
                // the streams one after another, each 16-byte aligned
                int streams = channels.Where(c => c.Dimension > 0).Select(c => c.Stream).DefaultIfEmpty(-1).Max() + 1;
                if (streams <= 0)
                {
                    error = "no vertex channels";
                    return null;
                }
                var stride = new int[streams];
                var start = new int[streams];
                foreach (var c in channels.Where(c => c.Dimension > 0))
                    stride[c.Stream] += FormatSize(c.Format) * c.Dimension;
                int offset = 0;
                for (int s = 0; s < streams; s++)
                {
                    start[s] = offset;
                    offset += count * stride[s];
                    offset = (offset + 15) & ~15;
                }
                if (data.Length < start[streams - 1] + count * stride[streams - 1])
                {
                    error = "short vertex data";
                    return null;
                }
                // position .. uv7; skin weights are dropped (imports aren't skinned)
                var kept = Enumerable.Range(0, Math.Min(channels.Count, 12)).Where(i => channels[i].Dimension > 0).ToList();
                var used = kept.Select(i => channels[i].Stream).Distinct().OrderBy(s => s).ToList();
                var attributes = new List<(int, int, int, int)>();
                var destStride = new int[used.Count];
                var destOffset = new Dictionary<int, int>();
                foreach (int i in kept)
                {
                    int ds = used.IndexOf(channels[i].Stream);
                    attributes.Add((i, channels[i].Format, channels[i].Dimension, ds));
                    destOffset[i] = destStride[ds];
                    destStride[ds] += FormatSize(channels[i].Format) * channels[i].Dimension;
                }
                var result = new GameMeshData
                {
                    Name = m.Str("m_Name"),
                    VertexCount = count,
                    Attributes = attributes.ToArray(),
                    Streams = new byte[used.Count][],
                };
                for (int ds = 0; ds < used.Count; ds++)
                {
                    int s = used[ds];
                    var bytes = new byte[count * destStride[ds]];
                    // the whole stream at once when its layout is the same (some meshes store channels out of channel order)
                    if (destStride[ds] == stride[s] && kept.Where(i => channels[i].Stream == s).All(i => channels[i].Offset == destOffset[i]))
                        Buffer.BlockCopy(data, start[s], bytes, 0, bytes.Length);
                    else
                        foreach (int i in kept.Where(i => channels[i].Stream == s))
                        {
                            int size = FormatSize(channels[i].Format) * channels[i].Dimension;
                            int src = start[s] + channels[i].Offset, dst = destOffset[i];
                            for (int v = 0; v < count; v++)
                                Buffer.BlockCopy(data, src + v * stride[s], bytes, dst + v * destStride[ds], size);
                        }
                    result.Streams[ds] = bytes;
                }
                result.Wide = m.Int("m_IndexFormat") == 1;
                result.Indices = m.Bytes("m_IndexBuffer") ?? Array.Empty<byte>();
                result.SubMeshes = m.List("m_SubMeshes").OfType<GameValue>().Select(sub => new GameSubMesh
                {
                    IndexStart = (int)(sub.Long("firstByte") / (result.Wide ? 4 : 2)),
                    IndexCount = sub.Int("indexCount"),
                    Topology = sub.Int("topology"),
                    BaseVertex = sub.Int("baseVertex"),
                    FirstVertex = sub.Int("firstVertex"),
                    VertexCount = sub.Int("vertexCount"),
                    Center = Center(sub.Obj("localAABB")),
                    Extent = Extent(sub.Obj("localAABB")),
                }).ToArray();
                result.Center = Center(m.Obj("m_LocalAABB"));
                result.Extent = Extent(m.Obj("m_LocalAABB"));
                return result;
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        // bytes of one mip of the game's usual formats (-1: a format the previews don't decode here)
        public static long MipSize(int format, int w, int h)
        {
            long blocks = (long)((w + 3) / 4) * ((h + 3) / 4);
            switch (format)
            {
                case 10: case 26: return blocks * 8; // DXT1, BC4
                case 12: case 24: case 25: case 27: return blocks * 16; // DXT5, BC6H, BC7, BC5
                case 1: case 63: return (long)w * h; // Alpha8, R8
                case 2: case 7: case 9: case 13: case 15: case 62: return (long)w * h * 2;
                case 3: return (long)w * h * 3;
                case 4: case 5: case 14: case 16: case 18: return (long)w * h * 4;
                case 17: case 19: return (long)w * h * 8;
                case 20: return (long)w * h * 16;
                default: return -1;
            }
        }

        // the texture's mips from the first one no bigger than maxSize
        public static GameTextureData Texture(GameFiles files, GameSerializedFile from, GamePPtr pptr, int maxSize, out string error)
        {
            error = null;
            if (!files.Resolve(from, pptr, out var file, out var info) || info.ClassId != 28)
            {
                error = "not a 2D texture";
                return null;
            }
            try
            {
                var t = file.Read(info.PathId);
                int w = t.Int("m_Width"), h = t.Int("m_Height"), mips = Math.Max(1, t.Int("m_MipCount")), format = t.Int("m_TextureFormat");
                if (w <= 0 || h <= 0 || t.Int("m_ImageCount") > 1)
                {
                    error = "an empty texture";
                    return null;
                }
                int skip = 0;
                while (maxSize > 0 && skip < mips - 1 && (Math.Max(1, w >> skip) > maxSize || Math.Max(1, h >> skip) > maxSize))
                    skip++;
                long skipped = 0, total = 0;
                for (int i = 0; i < mips; i++)
                {
                    long size = MipSize(format, Math.Max(1, w >> i), Math.Max(1, h >> i));
                    if (size < 0)
                    {
                        error = "texture format " + format;
                        return null;
                    }
                    if (i < skip)
                        skipped += size;
                    total += size;
                }
                var inline = t.Bytes("image_data");
                byte[] bytes;
                if (inline != null && inline.Length > 0)
                {
                    if (inline.Length < total)
                    {
                        error = "short texture data";
                        return null;
                    }
                    bytes = new byte[total - skipped];
                    Buffer.BlockCopy(inline, (int)skipped, bytes, 0, bytes.Length);
                }
                else
                    bytes = files.Stream(file, t.Obj("m_StreamData"), skipped, total - skipped);
                if (bytes == null)
                {
                    error = "texture data not found";
                    return null;
                }
                var settings = t.Obj("m_TextureSettings");
                return new GameTextureData
                {
                    Name = t.Str("m_Name"),
                    Width = Math.Max(1, w >> skip),
                    Height = Math.Max(1, h >> skip),
                    Format = format,
                    Mips = mips - skip,
                    Data = bytes,
                    Filter = settings?.Int("m_FilterMode") ?? 1,
                    Aniso = settings?.Int("m_Aniso") ?? 1,
                    MipBias = settings?.Float("m_MipBias") ?? 0f,
                    WrapU = settings?.Int("m_WrapU") ?? 0,
                    WrapV = settings?.Int("m_WrapV") ?? 0,
                };
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        private static readonly Dictionary<string, string> ShaderNames = new Dictionary<string, string>();

        public static string ShaderName(GameFiles files, GameSerializedFile from, GamePPtr shader)
        {
            if (!files.Resolve(from, shader, out var file, out var info))
                return null;
            string key = file.Name + ":" + info.PathId;
            lock (ShaderNames)
                if (ShaderNames.TryGetValue(key, out var known))
                    return known;
            string name = null;
            try
            {
                name = file.Read(info.PathId, "m_Name")?.Str("m_Name");
                if (string.IsNullOrEmpty(name))
                    name = file.Read(info.PathId, "m_Name", "m_ParsedForm")?.Obj("m_ParsedForm")?.Str("m_Name");
            }
            catch (Exception)
            {
                name = null;
            }
            lock (ShaderNames)
                ShaderNames[key] = name;
            return name;
        }

        public static bool IsImpostor(string shaderName) => shaderName != null && shaderName.IndexOf("Amplify Impostor", StringComparison.OrdinalIgnoreCase) >= 0;

        private static readonly string[] MainTextureNames = { "_MainTex", "_BaseMap", "_Albedo", "_MainTex0", "_Diffuse", "_DiffuseMap", "_BaseColorMap" };

        private static string Key(GameFiles files, GameSerializedFile from, GamePPtr pptr) =>
            files.Resolve(from, pptr, out var file, out var info) ? file.Name + ":" + info.PathId : null;

        public static GameMaterialData PreviewMaterial(GameFiles files, GameSerializedFile from, GamePPtr pptr, int textureSize, Dictionary<string, GameTextureData> textures)
        {
            if (!files.Resolve(from, pptr, out var file, out var info) || info.ClassId != 21)
                return null;
            var m = file.Read(info.PathId);
            var data = new GameMaterialData
            {
                Key = file.Name + ":" + info.PathId,
                Name = m.Str("m_Name"),
                ShaderName = ShaderName(files, file, m.Ref("m_Shader")),
                Queue = m.Int("m_CustomRenderQueue"),
            };
            var props = m.Obj("m_SavedProperties");
            if (props == null)
                return data;
            foreach (var pair in props.Map("m_TexEnvs"))
            {
                if (!(pair.Key is string name) || Array.IndexOf(MainTextureNames, name) < 0 || !(pair.Value is GameValue env) || env.Ref("m_Texture").IsNull)
                    continue;
                var key = Key(files, file, env.Ref("m_Texture"));
                if (key == null)
                    continue;
                if (!textures.TryGetValue(key, out var texture))
                    textures[key] = texture = Texture(files, file, env.Ref("m_Texture"), textureSize, out _);
                if (texture == null)
                    continue;
                data.MainKey = key;
                data.Main = texture;
                data.MainScale = env.Floats("m_Scale");
                data.MainOffset = env.Floats("m_Offset");
                break;
            }
            foreach (var pair in props.Map("m_Colors"))
                if (pair.Key as string == "_Color" && pair.Value is float[] c)
                    data.Color = c;
            foreach (var pair in props.Map("m_Floats"))
                if (pair.Key as string == "_Cutoff")
                    data.Cutoff = Convert.ToSingle(pair.Value);
            return data;
        }

        // an index entry's look (a whole effect: its objects together)
        public static GameModelData Model(GameFiles files, GameIndexEntry entry, int textureSize)
        {
            if (entry.Category == GameIndex.Materials)
                return MaterialModel(files, entry, textureSize);
            if (entry.Category == GameIndex.Sounds || entry.Category == GameIndex.Data)
                return new GameModelData { Error = "nothing to show" };
            var tree = GameSceneCache.For(files, entry);
            if (tree == null)
                return new GameModelData { Error = "its file didn't read" };
            if (!entry.IsGroup)
            {
                var node = tree.Get(entry.Transform);
                return node == null ? new GameModelData { Error = "not in its scene (read the map again)" } : Model(files, tree, node, textureSize);
            }
            var model = new GameModelData();
            foreach (var part in entry.Parts)
                if (tree.Get(part) is GameSceneTree.Node node)
                    model.Parts.AddRange(Model(files, tree, node, textureSize).Parts);
            if (model.Parts.Count == 0)
                model.Error = "nothing to show";
            return model;
        }

        // the object's look: LOD0 of each LOD group, renderers that are on, active and not shadow-only, matrices from the
        // root (at the origin, with its world scale)
        public static GameModelData Model(GameFiles files, GameSceneTree tree, GameSceneTree.Node root, int textureSize)
        {
            var model = new GameModelData();
            var file = tree.File;
            var nodes = tree.Subtree(root).ToList();
            var hidden = new HashSet<long>();
            foreach (var node in nodes)
                foreach (var (classId, pathId) in node.Components)
                    if (classId == 205)
                        foreach (var lod in file.Read(pathId).List("m_LODs").OfType<GameValue>().Skip(1))
                            foreach (var r in lod.List("renderers").OfType<GameValue>())
                                hidden.Add(r.Ref("renderer").PathId);
            var meshes = new Dictionary<string, GameMeshData>();
            var materials = new Dictionary<string, GameMaterialData>();
            var textures = new Dictionary<string, GameTextureData>();
            var matrices = new Dictionary<long, Matrix4x4>();
            var active = new Dictionary<long, bool>();
            foreach (var node in nodes)
            {
                var parent = node == root ? null : tree.Get(node.Parent);
                Matrix4x4 local;
                if (node == root)
                {
                    var w = node.World;
                    local = Matrix4x4.CreateScale(new Vector3(new Vector3(w.M11, w.M12, w.M13).Length(), new Vector3(w.M21, w.M22, w.M23).Length(),
                        new Vector3(w.M31, w.M32, w.M33).Length()));
                }
                else
                    local = Matrix4x4.CreateScale(node.Scale) * Matrix4x4.CreateFromQuaternion(node.Rotation) * Matrix4x4.CreateTranslation(node.Position);
                var matrix = parent != null ? local * matrices[parent.Transform] : local;
                matrices[node.Transform] = matrix;
                bool on = node.Active && (parent == null || active[parent.Transform]);
                active[node.Transform] = on;
                if (!on || node.Mesh == 0)
                    continue;
                foreach (var (classId, pathId) in node.Components)
                {
                    if (classId != 23 && classId != 137 || hidden.Contains(pathId))
                        continue;
                    var r = file.Read(pathId, "m_GameObject", "m_Enabled", "m_CastShadows");
                    if (!r.Bool("m_Enabled") || r.Int("m_CastShadows") == 3)
                        continue;
                    var meshPtr = new GamePPtr { FileId = node.MeshFile, PathId = node.Mesh };
                    var meshKey = Key(files, file, meshPtr);
                    if (meshKey == null)
                        continue;
                    if (!meshes.TryGetValue(meshKey, out var mesh))
                        meshes[meshKey] = mesh = Mesh(files, file, meshPtr, out _);
                    if (mesh == null)
                        continue;
                    var list = new List<GameMaterialData>();
                    bool skip = false;
                    foreach (var m in node.Materials ?? new List<GamePPtr>())
                    {
                        var key = Key(files, file, m);
                        GameMaterialData material = null;
                        if (key != null && !materials.TryGetValue(key, out material))
                            materials[key] = material = PreviewMaterial(files, file, m, textureSize, textures);
                        if (material != null && (IsImpostor(material.ShaderName) || material.ShaderName != null && material.ShaderName.StartsWith("Decal/")))
                            skip = true;
                        list.Add(material);
                    }
                    if (skip || list.Count == 0)
                        continue;
                    model.Parts.Add(new GamePartData { MeshKey = meshKey, Mesh = mesh, Matrix = matrix, Materials = list.ToArray() });
                }
            }
            // particle systems: a cross of their material's texture where each one is (there's no mesh to draw)
            foreach (var node in nodes)
            {
                if (!active[node.Transform])
                    continue;
                foreach (var (classId, pathId) in node.Components)
                {
                    if (classId != 199)
                        continue;
                    var r = file.Read(pathId, "m_GameObject", "m_Enabled", "m_Materials");
                    var first = r?.List("m_Materials").OfType<GamePPtr>().FirstOrDefault(m => !m.IsNull) ?? default;
                    if (r == null || !r.Bool("m_Enabled") || first.IsNull)
                        continue;
                    var key = Key(files, file, first);
                    if (key == null)
                        continue;
                    if (!materials.TryGetValue(key, out var material))
                        materials[key] = material = PreviewMaterial(files, file, first, textureSize, textures);
                    if (material == null)
                        continue;
                    material.Particle = true;
                    model.Parts.Add(new GamePartData { MeshKey = "particle cross", Mesh = Cross, Matrix = matrices[node.Transform], Materials = new[] { material } });
                }
            }
            if (model.Parts.Count == 0)
                model.Error = "nothing to show";
            return model;
        }

        // a material on a sphere
        private static GameModelData MaterialModel(GameFiles files, GameIndexEntry entry, int textureSize)
        {
            var file = files.Get(entry.Asset);
            var material = file != null ? PreviewMaterial(files, file, new GamePPtr { PathId = entry.AssetId }, textureSize, new Dictionary<string, GameTextureData>()) : null;
            if (material == null)
                return new GameModelData { Error = "its file didn't read" };
            var model = new GameModelData();
            model.Parts.Add(new GamePartData { MeshKey = "material sphere", Mesh = Sphere, Matrix = Matrix4x4.Identity, Materials = new[] { material } });
            return model;
        }

        // a 1 m sphere (position, normal, uv0), for materials
        public static readonly GameMeshData Sphere = MakeSphere(24, 16);

        private static GameMeshData MakeSphere(int around, int down)
        {
            var verts = new List<float>();
            for (int y = 0; y <= down; y++)
                for (int x = 0; x <= around; x++)
                {
                    float u = x / (float)around, v = y / (float)down;
                    float theta = u * MathF.PI * 2f, phi = v * MathF.PI;
                    var n = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta));
                    verts.AddRange(new[] { n.X * 0.5f, n.Y * 0.5f, n.Z * 0.5f, n.X, n.Y, n.Z, u * 2f, 1f - v });
                }
            var indices = new List<ushort>();
            for (int y = 0; y < down; y++)
                for (int x = 0; x < around; x++)
                {
                    int a = y * (around + 1) + x, b = a + around + 1;
                    indices.AddRange(new[] { (ushort)a, (ushort)(a + 1), (ushort)b, (ushort)(a + 1), (ushort)(b + 1), (ushort)b });
                }
            var stream = new byte[verts.Count * 4];
            Buffer.BlockCopy(verts.ToArray(), 0, stream, 0, stream.Length);
            var ib = new byte[indices.Count * 2];
            Buffer.BlockCopy(indices.ToArray(), 0, ib, 0, ib.Length);
            int count = (around + 1) * (down + 1);
            return new GameMeshData
            {
                Name = "material sphere",
                VertexCount = count,
                Attributes = new[] { (0, 0, 3, 0), (1, 0, 3, 0), (4, 0, 2, 0) },
                Streams = new[] { stream },
                Indices = ib,
                SubMeshes = new[] { new GameSubMesh { IndexStart = 0, IndexCount = indices.Count, Topology = 0, VertexCount = count, Center = new float[3], Extent = new[] { 0.5f, 0.5f, 0.5f } } },
                Center = new float[3],
                Extent = new[] { 0.5f, 0.5f, 0.5f },
            };
        }

        // two crossed 1 m quads (position and uv0), for particle systems
        public static readonly GameMeshData Cross = MakeCross();

        private static GameMeshData MakeCross()
        {
            var verts = new List<float>();
            foreach (var (ax, az) in new[] { (1f, 0f), (0f, 1f) })
                foreach (var (u, v) in new[] { (0f, 0f), (1f, 0f), (1f, 1f), (0f, 1f) })
                    verts.AddRange(new[] { (u - 0.5f) * ax, v - 0.5f, (u - 0.5f) * az, u, v });
            var stream = new byte[verts.Count * 4];
            Buffer.BlockCopy(verts.ToArray(), 0, stream, 0, stream.Length);
            var indices = new ushort[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            var ib = new byte[indices.Length * 2];
            Buffer.BlockCopy(indices, 0, ib, 0, ib.Length);
            return new GameMeshData
            {
                Name = "particle cross",
                VertexCount = 8,
                Attributes = new[] { (0, 0, 3, 0), (4, 0, 2, 0) },
                Streams = new[] { stream },
                Indices = ib,
                SubMeshes = new[] { new GameSubMesh { IndexStart = 0, IndexCount = 12, Topology = 0, VertexCount = 8, Center = new float[3], Extent = new[] { 0.5f, 0.5f, 0.5f } } },
                Center = new float[3],
                Extent = new[] { 0.5f, 0.5f, 0.5f },
            };
        }
    }
}
