using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TarkovSDK.Editor
{
    // Reads the game's own data files (levelN scenes, sharedassetsN.assets, resources.assets, globalgamemanagers ...) and
    // asset bundles (StreamingAssets) in the game install, without Unity: the Asset Importer and the game model previews
    // build their meshes, materials, textures and objects from what this returns. The data files are Unity 2022.3
    // serialized files (format 22) built without type trees, so objects are read with the type trees in
    // TarkovGameTypeTrees (UnityPy's database); bundles carry their own type trees and are LZ4 compressed in blocks,
    // decompressed only where read (nothing is unpacked to disk). MonoBehaviours' own fields are read by
    // TarkovGameScripts. Nothing here touches UnityEngine, so it also runs off the main thread.

    public struct GamePPtr
    {
        public int FileId;
        public long PathId;
        public bool IsNull => PathId == 0;
        public override string ToString() => $"{FileId}:{PathId}";
    }

    // one node of a type tree
    public sealed class GameTypeNode
    {
        public string Type;
        public string Name;
        public int Size;
        public bool Align;
        // Unity's meta flags (0x4000 = Align); kept so a tree can be written back (GameWriter)
        public int Meta;
        public int Version = 1;
        public GameTypeNode[] Children = Array.Empty<GameTypeNode>();
        // bytes when every instance has the same size (no arrays, strings or alignment inside), else -1
        public int Fixed = -2;

        public int FixedSize()
        {
            if (Fixed != -2)
                return Fixed;
            int size;
            if (Align || Type == "string" || Type == "TypelessData" || Children.Length > 0 && Children[0].Type == "Array")
                size = -1;
            else if (Children.Length == 0)
                size = Size;
            else
            {
                size = 0;
                foreach (var child in Children)
                {
                    int s = child.FixedSize();
                    if (s < 0)
                    {
                        size = -1;
                        break;
                    }
                    size += s;
                }
            }
            Fixed = size;
            return size;
        }

        public GameTypeNode Child(string name)
        {
            foreach (var child in Children)
                if (child.Name == name)
                    return child;
            return null;
        }
    }

    public static class GameTypeTrees
    {
        private static Dictionary<int, GameTypeNode> _trees;
        private static Dictionary<uint, string> _common;

        // a type tree as stored in a serialized file (format 22 blob: 32-byte nodes, then their strings; offsets with the
        // top bit set are Unity's common strings)
        public static GameTypeNode ReadBlob(GameReader r)
        {
            if (_common == null)
            {
                var common = new Dictionary<uint, string>();
                foreach (var line in TarkovGameTypeTrees.CommonStrings.Split('\n'))
                {
                    var t = line.Trim();
                    int space = t.IndexOf(' ');
                    if (space > 0)
                        common[uint.Parse(t.Substring(0, space))] = t.Substring(space + 1);
                }
                _common = common;
            }
            int count = r.I32(), stringSize = r.I32();
            if (count <= 0 || count > 1 << 20 || stringSize < 0)
                throw new InvalidDataException("bad type tree");
            var raw = r.Bytes(count * 32);
            var strings = r.Bytes(stringSize);
            string Str(uint offset)
            {
                if ((offset & 0x80000000) != 0)
                    return _common.TryGetValue(offset & 0x7FFFFFFF, out var c) ? c : "?";
                int end = (int)offset;
                while (end < strings.Length && strings[end] != 0)
                    end++;
                return Encoding.UTF8.GetString(strings, (int)offset, end - (int)offset);
            }
            var stack = new List<GameTypeNode>();
            var children = new Dictionary<GameTypeNode, List<GameTypeNode>>();
            GameTypeNode root = null;
            for (int i = 0; i < count; i++)
            {
                int at = i * 32;
                int level = raw[at + 2];
                var node = new GameTypeNode
                {
                    Type = Str(BitConverter.ToUInt32(raw, at + 4)),
                    Name = Str(BitConverter.ToUInt32(raw, at + 8)).Replace(' ', '_'),
                    Size = BitConverter.ToInt32(raw, at + 12),
                    Meta = BitConverter.ToInt32(raw, at + 20),
                    Version = BitConverter.ToUInt16(raw, at),
                    Align = (BitConverter.ToInt32(raw, at + 20) & 0x4000) != 0,
                };
                children[node] = new List<GameTypeNode>();
                if (level == 0)
                    root = node;
                else if (level - 1 < stack.Count)
                    children[stack[level - 1]].Add(node);
                if (stack.Count > level)
                    stack.RemoveRange(level, stack.Count - level);
                stack.Add(node);
            }
            foreach (var pair in children)
                pair.Key.Children = pair.Value.ToArray();
            return root;
        }

        public static GameTypeNode Get(int classId)
        {
            if (_trees == null)
                _trees = Parse(TarkovGameTypeTrees.Data);
            return _trees.TryGetValue(classId, out var tree) ? tree : null;
        }

        private static Dictionary<int, GameTypeNode> Parse(string data)
        {
            var trees = new Dictionary<int, GameTypeNode>();
            var stack = new List<GameTypeNode>();
            var children = new Dictionary<GameTypeNode, List<GameTypeNode>>();
            int classId = 0;
            foreach (var raw in data.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                    continue;
                if (line[0] == '#')
                {
                    classId = int.Parse(line.Substring(1).Trim());
                    stack.Clear();
                    continue;
                }
                var parts = line.Split(' ');
                int depth = int.Parse(parts[0]);
                var node = new GameTypeNode
                {
                    Type = parts[1].Replace('_', ' '),
                    Name = parts[2],
                    Size = int.Parse(parts[3]),
                    Meta = int.Parse(parts[4]),
                    Version = int.Parse(parts[5]),
                    Align = (int.Parse(parts[4]) & 0x4000) != 0,
                };
                children[node] = new List<GameTypeNode>();
                if (depth == 0)
                    trees[classId] = node;
                else
                    children[stack[depth - 1]].Add(node);
                if (stack.Count > depth)
                    stack.RemoveRange(depth, stack.Count - depth);
                stack.Add(node);
            }
            foreach (var pair in children)
                pair.Key.Children = pair.Value.ToArray();
            return trees;
        }
    }

    // A read object: field name -> value. Values: bool, byte, sbyte, short, ushort, int, uint, long, ulong, float,
    // double, string, byte[] (byte/char arrays, TypelessData), float[] (float arrays and the float vector types),
    // GamePPtr, GameValue (classes), List<object> (arrays), List<KeyValuePair<object, object>> (maps).
    public sealed class GameValue : Dictionary<string, object>
    {
        public GameValue() : base(StringComparer.Ordinal) { }

        public GameValue Obj(string name) => TryGetValue(name, out var v) ? v as GameValue : null;
        public List<object> List(string name) => TryGetValue(name, out var v) && v is List<object> l ? l : new List<object>();
        public List<KeyValuePair<object, object>> Map(string name) =>
            TryGetValue(name, out var v) && v is List<KeyValuePair<object, object>> m ? m : new List<KeyValuePair<object, object>>();
        public string Str(string name) => TryGetValue(name, out var v) ? v as string ?? "" : "";
        public GamePPtr Ref(string name) => TryGetValue(name, out var v) && v is GamePPtr p ? p : default;
        public byte[] Bytes(string name) => TryGetValue(name, out var v) ? v as byte[] : null;
        public float[] Floats(string name) => TryGetValue(name, out var v) ? v as float[] : null;
        public bool Bool(string name) => TryGetValue(name, out var v) && Convert.ToBoolean(v);

        public long Long(string name) => TryGetValue(name, out var v) && v != null && !(v is GameValue) ? Convert.ToInt64(v) : 0;
        public int Int(string name) => (int)Long(name);

        public float Float(string name) => TryGetValue(name, out var v) && v != null && !(v is GameValue) ? Convert.ToSingle(v) : 0f;
    }

    public sealed class GameObjectInfo
    {
        public long PathId;
        public long Start;
        public uint Size;
        public int ClassId;
        public int TypeIndex;
    }

    // Little-endian reads over a stream, with skips as seeks
    public sealed class GameReader
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[16];
        private readonly long _origin;

        public GameReader(Stream stream, long origin)
        {
            _stream = stream;
            _origin = origin;
        }

        public long Position
        {
            get => _stream.Position - _origin;
            set => _stream.Position = _origin + value;
        }

        private byte[] Fill(int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = _stream.Read(_buffer, read, count - read);
                if (n <= 0)
                    throw new EndOfStreamException();
                read += n;
            }
            return _buffer;
        }

        public byte[] Bytes(int count)
        {
            if (count < 0 || count > 1 << 30)
                throw new InvalidDataException("bad length " + count);
            var bytes = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = _stream.Read(bytes, read, count - read);
                if (n <= 0)
                    throw new EndOfStreamException();
                read += n;
            }
            return bytes;
        }

        public void Skip(long count) => _stream.Seek(count, SeekOrigin.Current);
        public void Align() => Position = (Position + 3) & ~3L;

        public byte U8() => Fill(1)[0];
        public short I16() => BitConverter.ToInt16(Fill(2), 0);
        public ushort U16() => BitConverter.ToUInt16(Fill(2), 0);
        public int I32() => BitConverter.ToInt32(Fill(4), 0);
        public uint U32() => BitConverter.ToUInt32(Fill(4), 0);
        public long I64() => BitConverter.ToInt64(Fill(8), 0);
        public ulong U64() => BitConverter.ToUInt64(Fill(8), 0);
        public float F32() => BitConverter.ToSingle(Fill(4), 0);
        public double F64() => BitConverter.ToDouble(Fill(8), 0);

        public string AlignedString()
        {
            int length = I32();
            var s = Encoding.UTF8.GetString(Bytes(length));
            Align();
            return s;
        }

        public GamePPtr PPtr() => new GamePPtr { FileId = I32(), PathId = I64() };

        // ---- type tree driven

        public object Read(GameTypeNode node) => Read(node, false);

        // the value of one node; with skip nothing is kept and fixed-size arrays are seeked over
        public object Read(GameTypeNode node, bool skip)
        {
            object value;
            switch (node.Type)
            {
                case "bool": value = U8() != 0; break;
                case "SInt8": value = (sbyte)U8(); break;
                case "UInt8":
                case "char": value = U8(); break;
                case "SInt16":
                case "short": value = I16(); break;
                case "UInt16":
                case "unsigned short": value = U16(); break;
                case "SInt32":
                case "int": value = I32(); break;
                case "UInt32":
                case "unsigned int":
                case "Type*": value = U32(); break;
                case "SInt64":
                case "long long": value = I64(); break;
                case "UInt64":
                case "unsigned long long":
                case "FileSize": value = U64(); break;
                case "float": value = F32(); break;
                case "double": value = F64(); break;
                case "string":
                {
                    int length = I32();
                    if (skip)
                    {
                        Skip(length);
                        value = null;
                    }
                    else
                        value = Encoding.UTF8.GetString(Bytes(length));
                    if (node.Children.Length > 0 && node.Children[0].Align)
                        Align();
                    break;
                }
                case "TypelessData":
                {
                    int length = I32();
                    if (skip)
                    {
                        Skip(length);
                        value = null;
                    }
                    else
                        value = Bytes(length);
                    break;
                }
                default:
                    if (node.Type.StartsWith("PPtr<"))
                    {
                        value = PPtr();
                        break;
                    }
                    if (node.Children.Length > 0 && node.Children[0].Type == "Array")
                    {
                        value = ReadArray(node.Children[0], node.Type == "map", skip);
                        if (node.Children[0].Align)
                            Align();
                        break;
                    }
                    if (node.Children.Length == 0)
                    {
                        // unknown primitive: its size
                        Skip(Math.Max(0, node.Size));
                        value = null;
                        break;
                    }
                    if (skip && node.FixedSize() >= 0)
                    {
                        Skip(node.FixedSize());
                        value = null;
                        break;
                    }
                    if (!skip && IsFloatVector(node))
                    {
                        var floats = new float[node.Children.Length];
                        for (int i = 0; i < floats.Length; i++)
                            floats[i] = F32();
                        value = floats;
                        break;
                    }
                    GameValue obj = skip ? null : new GameValue();
                    foreach (var child in node.Children)
                    {
                        var v = Read(child, skip);
                        if (obj != null)
                            obj[child.Name] = v;
                    }
                    value = obj;
                    break;
            }
            if (node.Align)
                Align();
            return value;
        }

        private static bool IsFloatVector(GameTypeNode node) =>
            node.Children.Length >= 2 && node.Children.Length <= 4 && (node.Type.StartsWith("Vector") || node.Type == "Quaternionf" || node.Type == "ColorRGBA") &&
            Array.TrueForAll(node.Children, c => c.Type == "float");

        private object ReadArray(GameTypeNode array, bool map, bool skip)
        {
            int count = I32();
            if (count < 0 || count > 1 << 28)
                throw new InvalidDataException($"bad array size {count} in {array.Name}");
            var element = array.Children[1];
            if (element.Children.Length == 0 && (element.Type == "UInt8" || element.Type == "char" || element.Type == "SInt8"))
            {
                if (skip)
                {
                    Skip(count);
                    return null;
                }
                return Bytes(count);
            }
            if (skip)
            {
                int size = element.FixedSize();
                if (size >= 0)
                {
                    Skip((long)size * count);
                    return null;
                }
                for (int i = 0; i < count; i++)
                    Read(element, true);
                return null;
            }
            if (element.Type == "float" && element.Children.Length == 0)
            {
                var floats = new float[count];
                for (int i = 0; i < count; i++)
                    floats[i] = F32();
                return floats;
            }
            if (map)
            {
                var pairs = new List<KeyValuePair<object, object>>(count);
                for (int i = 0; i < count; i++)
                {
                    var key = Read(element.Children[0], false);
                    var val = Read(element.Children[1], false);
                    pairs.Add(new KeyValuePair<object, object>(key, val));
                    if (element.Align)
                        Align();
                }
                return pairs;
            }
            var list = new List<object>(count);
            for (int i = 0; i < count; i++)
                list.Add(Read(element, false));
            return list;
        }
    }

    // One serialized file: its types, objects and the files its references point into
    public sealed class GameSerializedFile : IDisposable
    {
        public string Name;
        public string Path;
        public uint Version;
        public string UnityVersion;
        public long DataOffset;
        public readonly List<int> TypeClassIds = new List<int>();
        // the file's own type trees (asset bundles), by type index
        public readonly List<GameTypeNode> TypeTrees = new List<GameTypeNode>();
        public readonly Dictionary<long, GameObjectInfo> Objects = new Dictionary<long, GameObjectInfo>();
        public readonly List<GameObjectInfo> ObjectList = new List<GameObjectInfo>();
        public readonly List<string> Externals = new List<string>();
        private Stream _stream;
        private readonly object _lock = new object();

        public static GameSerializedFile Open(string path) =>
            Open(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16), System.IO.Path.GetFileName(path), path);

        // a serialized file in any seekable stream (a bundle's node)
        public static GameSerializedFile Open(Stream stream, string name, string path)
        {
            var file = new GameSerializedFile { Path = path, Name = name };
            file._stream = stream;
            try
            {
                file.ReadHeader();
            }
            catch
            {
                file.Dispose();
                throw;
            }
            return file;
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
        }

        private static uint BigU32(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
        private static long BigI64(byte[] b, int at) => (long)((ulong)BigU32(b, at) << 32 | BigU32(b, at + 4));

        private void ReadHeader()
        {
            var head = new byte[48];
            if (_stream.Read(head, 0, 48) < 48)
                throw new InvalidDataException("not a serialized file");
            Version = BigU32(head, 8);
            if (Version < 22)
                throw new InvalidDataException($"serialized file version {Version} (expected 22, Unity 2022)");
            bool bigEndian = head[16] != 0;
            if (bigEndian)
                throw new InvalidDataException("big-endian serialized file");
            uint metadataSize = BigU32(head, 20);
            DataOffset = BigI64(head, 32);
            var r = new GameReader(_stream, 0) { Position = 48 };
            UnityVersion = NullString(r);
            r.I32(); // platform
            bool typeTrees = r.U8() != 0;
            int typeCount = r.I32();
            for (int i = 0; i < typeCount; i++)
            {
                int classId = r.I32();
                r.U8(); // stripped
                r.I16(); // script type index
                if (classId == 114)
                    r.Skip(16);
                r.Skip(16);
                TypeClassIds.Add(classId);
                GameTypeNode tree = null;
                if (typeTrees)
                {
                    tree = GameTypeTrees.ReadBlob(r);
                    // the type's dependencies
                    int dependencies = r.I32();
                    r.Skip(dependencies * 4L);
                }
                TypeTrees.Add(tree);
            }
            int objectCount = r.I32();
            for (int i = 0; i < objectCount; i++)
            {
                r.Align();
                var info = new GameObjectInfo { PathId = r.I64(), Start = r.I64() + DataOffset, Size = r.U32(), TypeIndex = r.I32() };
                info.ClassId = info.TypeIndex >= 0 && info.TypeIndex < TypeClassIds.Count ? TypeClassIds[info.TypeIndex] : -1;
                Objects[info.PathId] = info;
                ObjectList.Add(info);
            }
            int scriptCount = r.I32();
            for (int i = 0; i < scriptCount; i++)
            {
                r.I32();
                r.Align();
                r.I64();
            }
            int externalCount = r.I32();
            for (int i = 0; i < externalCount; i++)
            {
                NullString(r);
                r.Skip(16);
                r.I32();
                Externals.Add(NullString(r));
            }
            _ = metadataSize;
        }

        private static string NullString(GameReader r)
        {
            var bytes = new List<byte>();
            for (byte b; (b = r.U8()) != 0;)
                bytes.Add(b);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        // the external file a reference's file id points into (null = this one)
        public string ExternalName(int fileId)
        {
            if (fileId == 0)
                return Name;
            if (fileId < 1 || fileId > Externals.Count)
                return null;
            var path = Externals[fileId - 1].Replace('\\', '/');
            return path.Substring(path.LastIndexOf('/') + 1);
        }

        public GameObjectInfo Info(long pathId) => Objects.TryGetValue(pathId, out var info) ? info : null;

        // the object's type tree: the file's own (bundles), else Unity's for its class
        public GameTypeNode TreeOf(GameObjectInfo info) =>
            (info.TypeIndex >= 0 && info.TypeIndex < TypeTrees.Count ? TypeTrees[info.TypeIndex] : null) ?? GameTypeTrees.Get(info.ClassId);

        // the object read with its class's type tree; with fields, only up to the last of them (the rest skipped)
        public GameValue Read(long pathId, params string[] fields)
        {
            var info = Info(pathId);
            if (info == null)
                return null;
            var tree = TreeOf(info);
            if (tree == null)
                return null;
            lock (_lock)
            {
                var r = new GameReader(_stream, info.Start) { Position = 0 };
                return ReadFields(r, tree, fields);
            }
        }

        private static GameValue ReadFields(GameReader r, GameTypeNode tree, string[] fields)
        {
            var value = new GameValue();
            int wanted = fields.Length;
            foreach (var child in tree.Children)
            {
                bool keep = fields.Length == 0 || Array.IndexOf(fields, child.Name) >= 0;
                var v = r.Read(child, !keep);
                if (keep)
                {
                    value[child.Name] = v;
                    if (fields.Length > 0 && --wanted == 0)
                        break;
                }
            }
            return value;
        }

        // whole-object check for the reader: every field read, and where it ended (should be the object's size)
        public long ReadAllCheck(long pathId)
        {
            var info = Info(pathId);
            var tree = TreeOf(info);
            lock (_lock)
            {
                var r = new GameReader(_stream, info.Start) { Position = 0 };
                r.Read(tree);
                return r.Position;
            }
        }

        // a MonoBehaviour's base fields, then its script's own fields through read (given a reader at their start and
        // the bytes left)
        public T ReadScript<T>(long pathId, out GameValue header, Func<GameReader, long, T> read)
        {
            header = null;
            var info = Info(pathId);
            if (info == null || info.ClassId != 114)
                return default;
            lock (_lock)
            {
                var r = new GameReader(_stream, info.Start) { Position = 0 };
                header = ReadFields(r, GameTypeTrees.Get(114), Array.Empty<string>());
                return read == null ? default : read(r, info.Size - r.Position);
            }
        }

        // the start of a MonoBehaviour: its GameObject, enabled and script, without reading its name
        public bool ScriptHeader(long pathId, out GamePPtr gameObject, out bool enabled, out GamePPtr script)
        {
            gameObject = script = default;
            enabled = false;
            var info = Info(pathId);
            if (info == null || info.ClassId != 114)
                return false;
            lock (_lock)
            {
                var r = new GameReader(_stream, info.Start) { Position = 0 };
                gameObject = r.PPtr();
                enabled = r.U8() != 0;
                r.Align();
                script = r.PPtr();
            }
            return true;
        }

        public byte[] RawBytes(long start, int size)
        {
            lock (_lock)
            {
                _stream.Position = start;
                var bytes = new byte[size];
                int read = 0;
                while (read < size)
                {
                    int n = _stream.Read(bytes, read, size - read);
                    if (n <= 0)
                        throw new EndOfStreamException();
                    read += n;
                }
                return bytes;
            }
        }
    }

    // The game's Data folder: files opened on first use, references resolved across them
    public sealed class GameFiles : IDisposable
    {
        public readonly string DataFolder;
        private readonly Dictionary<string, GameSerializedFile> _files = new Dictionary<string, GameSerializedFile>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        // opened bundles by path under StreamingAssets/Windows, and their files (CAB-...) by name
        private readonly Dictionary<string, GameBundle> _bundles = new Dictionary<string, GameBundle>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (GameBundle Bundle, GameBundle.Node Node)> _bundleFiles =
            new Dictionary<string, (GameBundle, GameBundle.Node)>(StringComparer.OrdinalIgnoreCase);

        public string BundleFolder => DataFolder + "/StreamingAssets/Windows";

        // a bundle (its path as Windows.json names it): its files become available by name; its main serialized file
        public GameSerializedFile OpenBundle(string bundle)
        {
            GameBundle opened;
            lock (_lock)
            {
                if (!_bundles.TryGetValue(bundle, out opened))
                {
                    var path = BundleFolder + "/" + bundle;
                    try
                    {
                        opened = File.Exists(path) ? GameBundle.Open(path) : null;
                    }
                    catch (Exception)
                    {
                        opened = null;
                    }
                    _bundles[bundle] = opened;
                    if (opened != null)
                        foreach (var node in opened.Nodes)
                            _bundleFiles[node.Name] = (opened, node);
                }
            }
            var main = opened?.Nodes.FirstOrDefault(n => (n.Flags & 4) != 0) ?? opened?.Nodes.FirstOrDefault();
            return main != null ? Get(main.Name) : null;
        }

        public GameFiles(string dataFolder)
        {
            DataFolder = dataFolder.Replace('\\', '/').TrimEnd('/');
        }

        public bool Exists => File.Exists(DataFolder + "/globalgamemanagers");

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var file in _files.Values)
                    file?.Dispose();
                _files.Clear();
                foreach (var bundle in _bundles.Values)
                    bundle?.Dispose();
                _bundles.Clear();
                _bundleFiles.Clear();
            }
        }

        // "sharedassets5.assets", "level5", "unity default resources" ...
        public GameSerializedFile Get(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            lock (_lock)
            {
                if (_files.TryGetValue(name, out var file))
                    return file;
                if (_bundleFiles.TryGetValue(name, out var inBundle))
                {
                    try
                    {
                        file = GameSerializedFile.Open(inBundle.Bundle.OpenNode(inBundle.Node), inBundle.Node.Name, inBundle.Bundle.Path + "/" + inBundle.Node.Name);
                    }
                    catch (Exception)
                    {
                        file = null;
                    }
                    _files[name] = file;
                    return file;
                }
                string path = DataFolder + "/" + name;
                if (!File.Exists(path))
                    path = DataFolder + "/Resources/" + name;
                try
                {
                    file = File.Exists(path) ? GameSerializedFile.Open(path) : null;
                }
                catch (Exception)
                {
                    file = null;
                }
                _files[name] = file;
                return file;
            }
        }

        // the file and object a reference in from points at
        public bool Resolve(GameSerializedFile from, GamePPtr pptr, out GameSerializedFile file, out GameObjectInfo info)
        {
            file = null;
            info = null;
            if (from == null || pptr.IsNull)
                return false;
            file = pptr.FileId == 0 ? from : Get(from.ExternalName(pptr.FileId));
            info = file?.Info(pptr.PathId);
            return info != null;
        }

        public GameValue Read(GameSerializedFile from, GamePPtr pptr, params string[] fields) =>
            Resolve(from, pptr, out var file, out var info) ? file.Read(info.PathId, fields) : null;

        // the bytes of a texture's or mesh's streamed data (StreamingInfo: offset, size, path of the .resS)
        public byte[] Stream(GameSerializedFile from, GameValue streamInfo, long skip = 0, long take = -1)
        {
            if (streamInfo == null)
                return null;
            long size = streamInfo.Long("size");
            var path = streamInfo.Str("path").Replace('\\', '/');
            if (size <= 0 || path.Length == 0)
                return null;
            path = path.Substring(path.LastIndexOf('/') + 1);
            long length = take < 0 ? size - skip : Math.Min(take, size - skip);
            Stream Open()
            {
                lock (_lock)
                    if (_bundleFiles.TryGetValue(path, out var inBundle))
                        return inBundle.Bundle.OpenNode(inBundle.Node);
                var full = DataFolder + "/" + path;
                return File.Exists(full) ? new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16) : null;
            }
            var opened = Open();
            if (opened == null)
                return null;
            using (var stream = opened)
            {
                stream.Position = (long)Convert.ToUInt64(streamInfo["offset"]) + skip;
                var bytes = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int n = stream.Read(bytes, read, (int)Math.Min(length - read, 1 << 20));
                    if (n <= 0)
                        throw new EndOfStreamException();
                    read += n;
                }
                return bytes;
            }
        }

        // the scene files of the build, by index (levelN = Scenes[N])
        public List<string> Scenes()
        {
            var ggm = Get("globalgamemanagers");
            if (ggm == null)
                return new List<string>();
            foreach (var info in ggm.ObjectList)
                if (info.ClassId == 141)
                {
                    var settings = ggm.Read(info.PathId, "scenes");
                    var scenes = new List<string>();
                    foreach (var s in settings.List("scenes"))
                        scenes.Add(s as string ?? "");
                    return scenes;
                }
            return new List<string>();
        }

        // Resources paths (lower case, as the game loads them) -> the asset
        public Dictionary<string, GamePPtr> ResourcePaths(out GameSerializedFile from)
        {
            var paths = new Dictionary<string, GamePPtr>(StringComparer.OrdinalIgnoreCase);
            from = Get("globalgamemanagers");
            if (from == null)
                return paths;
            foreach (var info in from.ObjectList)
                if (info.ClassId == 147)
                {
                    foreach (var pair in from.Read(info.PathId, "m_Container").Map("m_Container"))
                        if (pair.Key is string key && pair.Value is GamePPtr p && !paths.ContainsKey(key))
                            paths[key] = p;
                    break;
                }
            return paths;
        }

        // the tag names by index (TagManager), past Unity's built-in ones
        public List<string> Tags()
        {
            var tags = new List<string>();
            var ggm = Get("globalgamemanagers");
            if (ggm == null)
                return tags;
            foreach (var info in ggm.ObjectList)
                if (info.ClassId == 78)
                {
                    foreach (var t in ggm.Read(info.PathId, "tags").List("tags"))
                        tags.Add(t as string ?? "");
                    break;
                }
            return tags;
        }

        // a MonoScript's class: "Namespace.Class" (and its assembly)
        private readonly Dictionary<string, (string Name, string Assembly)> _scripts = new Dictionary<string, (string, string)>();

        public (string Name, string Assembly) Script(GameSerializedFile from, GamePPtr script)
        {
            if (!Resolve(from, script, out var file, out var info))
                return (null, null);
            string key = file.Name + ":" + info.PathId;
            lock (_scripts)
                if (_scripts.TryGetValue(key, out var known))
                    return known;
            var value = file.Read(info.PathId, "m_ClassName", "m_Namespace", "m_AssemblyName");
            var ns = value?.Str("m_Namespace") ?? "";
            var result = value == null ? (null, null) : ((ns.Length > 0 ? ns + "." : "") + value.Str("m_ClassName"), value.Str("m_AssemblyName"));
            lock (_scripts)
                _scripts[key] = result;
            return result;
        }
    }

    // A UnityFS asset bundle: its files (nodes) read through a stream that decompresses the LZ4 blocks it touches, so a
    // bundle (the game's shaders bundle is 378 MB) is never unpacked whole
    public sealed class GameBundle : IDisposable
    {
        public sealed class Node
        {
            public long Offset, Size;
            public uint Flags;
            public string Name;
        }

        private struct Block
        {
            public long Start, Uncompressed, Compressed, FileOffset;
            public int Flags;
        }

        public string Path;
        public readonly List<Node> Nodes = new List<Node>();
        private readonly List<Block> _blocks = new List<Block>();
        private FileStream _file;
        private readonly object _lock = new object();
        // the last few blocks decompressed
        private readonly Dictionary<int, byte[]> _cache = new Dictionary<int, byte[]>();
        private readonly LinkedList<int> _order = new LinkedList<int>();

        private static uint U32(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
        private static long I64(byte[] b, int at) => (long)((ulong)U32(b, at) << 32 | U32(b, at + 4));

        public static GameBundle Open(string path)
        {
            var bundle = new GameBundle { Path = path, _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16) };
            try
            {
                bundle.ReadHeader();
            }
            catch
            {
                bundle.Dispose();
                throw;
            }
            return bundle;
        }

        public void Dispose()
        {
            _file?.Dispose();
            _file = null;
        }

        private string CString(Stream s)
        {
            var bytes = new List<byte>();
            for (int b; (b = s.ReadByte()) > 0;)
                bytes.Add((byte)b);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private byte[] ReadExact(int count)
        {
            var bytes = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = _file.Read(bytes, read, count - read);
                if (n <= 0)
                    throw new EndOfStreamException();
                read += n;
            }
            return bytes;
        }

        private void ReadHeader()
        {
            if (CString(_file) != "UnityFS")
                throw new InvalidDataException("not a UnityFS bundle");
            var b = ReadExact(4);
            uint version = U32(b, 0);
            CString(_file);
            CString(_file);
            b = ReadExact(20);
            int infoCompressed = (int)U32(b, 8), infoSize = (int)U32(b, 12);
            uint flags = U32(b, 16);
            if (version >= 7)
                _file.Position = (_file.Position + 15) & ~15L;
            long infoAt = (flags & 0x80) != 0 ? _file.Length - infoCompressed : _file.Position;
            _file.Position = infoAt;
            var info = Decompress(ReadExact(infoCompressed), infoSize, (int)(flags & 0x3F));
            long dataAt = (flags & 0x80) != 0 ? infoAt - 0 : infoAt + infoCompressed;
            if ((flags & 0x80) != 0)
                dataAt = 0;
            if ((flags & 0x80) == 0)
            {
                _file.Position = dataAt;
                if ((flags & 0x200) != 0)
                    dataAt = (dataAt + 15) & ~15L;
            }
            int at = 16;
            int blocks = (int)U32(info, at);
            at += 4;
            long start = 0, fileOffset = dataAt;
            for (int i = 0; i < blocks; i++)
            {
                var block = new Block
                {
                    Uncompressed = U32(info, at),
                    Compressed = U32(info, at + 4),
                    Flags = (info[at + 8] << 8) | info[at + 9],
                    Start = start,
                    FileOffset = fileOffset,
                };
                at += 10;
                _blocks.Add(block);
                start += block.Uncompressed;
                fileOffset += block.Compressed;
            }
            int nodes = (int)U32(info, at);
            at += 4;
            for (int i = 0; i < nodes; i++)
            {
                var node = new Node { Offset = I64(info, at), Size = I64(info, at + 8), Flags = U32(info, at + 16) };
                at += 20;
                int end = at;
                while (info[end] != 0)
                    end++;
                node.Name = Encoding.UTF8.GetString(info, at, end - at);
                at = end + 1;
                Nodes.Add(node);
            }
        }

        private static byte[] Decompress(byte[] data, int size, int compression)
        {
            switch (compression)
            {
                case 0: return data;
                case 2:
                case 3: return Lz4.Decode(data, size);
                default: throw new NotSupportedException("bundle compression " + compression + " (LZMA bundles aren't read)");
            }
        }

        private byte[] BlockData(int index)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(index, out var cached))
                {
                    _order.Remove(index);
                    _order.AddLast(index);
                    return cached;
                }
                var block = _blocks[index];
                _file.Position = block.FileOffset;
                var data = Decompress(ReadExact((int)block.Compressed), (int)block.Uncompressed, block.Flags & 0x3F);
                _cache[index] = data;
                _order.AddLast(index);
                while (_order.Count > 16)
                {
                    _cache.Remove(_order.First.Value);
                    _order.RemoveFirst();
                }
                return data;
            }
        }

        // reads uncompressed bytes at position into buffer
        internal int ReadAt(long position, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (count > 0)
            {
                int index = FindBlock(position);
                if (index < 0)
                    break;
                var block = _blocks[index];
                var data = BlockData(index);
                int within = (int)(position - block.Start);
                int n = Math.Min(count, data.Length - within);
                if (n <= 0)
                    break;
                Buffer.BlockCopy(data, within, buffer, offset, n);
                position += n;
                offset += n;
                count -= n;
                total += n;
            }
            return total;
        }

        private int FindBlock(long position)
        {
            int lo = 0, hi = _blocks.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var b = _blocks[mid];
                if (position < b.Start)
                    hi = mid - 1;
                else if (position >= b.Start + b.Uncompressed)
                    lo = mid + 1;
                else
                    return mid;
            }
            return -1;
        }

        public Stream OpenNode(Node node) => new NodeStream(this, node);

        private sealed class NodeStream : Stream
        {
            private readonly GameBundle _bundle;
            private readonly Node _node;
            private long _position;

            public NodeStream(GameBundle bundle, Node node)
            {
                _bundle = bundle;
                _node = node;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _node.Size;

            public override long Position
            {
                get => _position;
                set => _position = value;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                count = (int)Math.Max(0, Math.Min(count, _node.Size - _position));
                if (count == 0)
                    return 0;
                int n = _bundle.ReadAt(_node.Offset + _position, buffer, offset, count);
                _position += n;
                return n;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                _position = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _position + offset : _node.Size + offset;
                return _position;
            }

            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    // LZ4 block decompression (the format Unity's LZ4 and LZ4HC bundles use)
    public static class Lz4
    {
        public static byte[] Decode(byte[] src, int size)
        {
            var dst = new byte[size];
            int ip = 0, op = 0;
            while (ip < src.Length)
            {
                int token = src[ip++];
                int literals = token >> 4;
                if (literals == 15)
                {
                    int b;
                    do
                    {
                        b = src[ip++];
                        literals += b;
                    } while (b == 255);
                }
                Buffer.BlockCopy(src, ip, dst, op, literals);
                ip += literals;
                op += literals;
                if (ip >= src.Length || op >= size)
                    break;
                int distance = src[ip] | src[ip + 1] << 8;
                ip += 2;
                int match = token & 15;
                if (match == 15)
                {
                    int b;
                    do
                    {
                        b = src[ip++];
                        match += b;
                    } while (b == 255);
                }
                match += 4;
                int from = op - distance;
                if (distance <= 0 || from < 0)
                    throw new InvalidDataException("bad LZ4 data");
                if (distance >= match)
                {
                    Buffer.BlockCopy(dst, from, dst, op, match);
                    op += match;
                }
                else
                    for (int i = 0; i < match; i++)
                        dst[op++] = dst[from + i];
            }
            if (op != size)
                throw new InvalidDataException($"LZ4: {op} of {size} bytes");
            return dst;
        }
    }
}
