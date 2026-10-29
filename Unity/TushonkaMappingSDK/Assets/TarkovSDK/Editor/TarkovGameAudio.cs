using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TarkovSDK.Editor
{
    // The game's sounds, without Unity. An AudioClip's samples are an FMOD sound bank (FSB5: PCM16, IMA ADPCM or Vorbis)
    // in a .resource file next to its serialized file (or a .resource node of its bundle). FMOD's Vorbis has no
    // headers to rebuild an .ogg from, so the sound importer has Unity decode it: GameAudio.Bundle wraps the clip and
    // its bank in a one-clip asset bundle (UnityFS, uncompressed, with type trees) that the editor loads like any game
    // bundle, decompressing on load so the samples can be read and written out as a .wav (TarkovGameSounds).

    public sealed class GameSound
    {
        public string Name;
        public int Channels;
        public int Frequency;
        public float Length;
        public int LoadType;
        public int Compression;
        public bool Legacy3D;
        public byte[] Bank;

        // FMOD's codec, from the bank's header
        public string Codec
        {
            get
            {
                if (Bank == null || Bank.Length < 28 || Bank[0] != 'F' || Bank[1] != 'S' || Bank[2] != 'B' || Bank[3] != '5')
                    return "unknown";
                switch (BitConverter.ToUInt32(Bank, 24))
                {
                    case 1: return "PCM8";
                    case 2: return "PCM16";
                    case 3: return "PCM24";
                    case 4: return "PCM32";
                    case 5: return "PCM float";
                    case 7: return "IMA ADPCM";
                    case 11: return "MP3";
                    case 15: return "Vorbis";
                    default: return "codec " + BitConverter.ToUInt32(Bank, 24);
                }
            }
        }
    }

    public static class GameAudio
    {
        // an AudioClip and its sound bank
        public static GameSound Read(GameFiles files, GameSerializedFile file, long pathId)
        {
            var clip = file?.Read(pathId);
            if (clip == null)
                return null;
            var resource = clip.Obj("m_Resource");
            var sound = new GameSound
            {
                Name = clip.Str("m_Name"),
                Channels = clip.Int("m_Channels"),
                Frequency = clip.Int("m_Frequency"),
                Length = clip.Float("m_Length"),
                LoadType = clip.Int("m_LoadType"),
                Compression = clip.Int("m_CompressionFormat"),
                Legacy3D = clip.Bool("m_Legacy3D"),
            };
            if (resource != null && resource.Long("m_Size") > 0)
                sound.Bank = files.Stream(file, new GameValue { ["path"] = resource.Str("m_Source"), ["offset"] = resource["m_Offset"], ["size"] = resource["m_Size"] });
            return sound;
        }

        // A UnityFS bundle holding the clip (decompressed on load, preloaded) and its bank, under the name cab
        // (bundles loaded at once need different names). Its asset is "<name>.wav".
        public static byte[] Bundle(GameSound sound, string cab)
        {
            var clipTree = GameTypeTrees.Get(83);
            var bundleTree = GameTypeTrees.Get(142);
            if (clipTree == null || bundleTree == null || sound.Bank == null)
                return null;
            string resource = cab + ".resource";
            var clip = new GameValue
            {
                ["m_Name"] = sound.Name,
                ["m_LoadType"] = 0,
                ["m_Channels"] = sound.Channels,
                ["m_Frequency"] = sound.Frequency,
                ["m_BitsPerSample"] = 16,
                ["m_Length"] = sound.Length,
                ["m_IsTrackerFormat"] = false,
                ["m_Ambisonic"] = false,
                ["m_SubsoundIndex"] = 0,
                ["m_PreloadAudioData"] = true,
                ["m_LoadInBackground"] = false,
                ["m_Legacy3D"] = sound.Legacy3D,
                ["m_Resource"] = new GameValue { ["m_Source"] = $"archive:/{cab}/{resource}", ["m_Offset"] = 0UL, ["m_Size"] = (ulong)sound.Bank.Length },
                ["m_CompressionFormat"] = sound.Compression,
            };
            var asset = new GamePPtr { FileId = 0, PathId = 2 };
            var none = new GameValue { ["preloadIndex"] = 0, ["preloadSize"] = 0, ["asset"] = new GamePPtr() };
            var manifest = new GameValue
            {
                ["m_Name"] = cab,
                ["m_PreloadTable"] = new List<object> { asset },
                ["m_Container"] = new List<KeyValuePair<object, object>>
                {
                    new KeyValuePair<object, object>(AssetPath(sound), new GameValue { ["preloadIndex"] = 0, ["preloadSize"] = 1, ["asset"] = asset }),
                },
                ["m_MainAsset"] = none,
                ["m_RuntimeCompatibility"] = 1u,
                ["m_AssetBundleName"] = cab,
                ["m_Dependencies"] = new List<object>(),
                ["m_IsStreamedSceneAssetBundle"] = false,
                ["m_ExplicitDataLayout"] = 1,
                ["m_PathFlags"] = 0,
                ["m_SceneHashes"] = new List<KeyValuePair<object, object>>(),
            };
            var serialized = SerializedFile(new[] { (142, bundleTree), (83, clipTree) },
                new[] { (1L, 0, GameWriter.Value(bundleTree, manifest)), (2L, 1, GameWriter.Value(clipTree, clip)) });
            return UnityFs(new[] { (cab, serialized, 4u), (resource, sound.Bank, 0u) });
        }

        public static string AssetPath(GameSound sound) => "assets/" + sound.Name.ToLowerInvariant() + ".wav";

        // ---- writing

        // a format 22 serialized file with type trees, for the Windows player
        // the types' hashes in the game's bundles (Unity 2022.3.43f1)
        private static readonly Dictionary<int, string> TypeHashes = new Dictionary<int, string>
        {
            [83] = "57a6370a001d5449abd37c2184ec5138",
            [142] = "97da5f4688e45a57c8b42d4f42497297",
        };

        private static byte[] SerializedFile((int ClassId, GameTypeNode Tree)[] types, (long PathId, int Type, byte[] Data)[] objects)
        {
            var meta = new GameWriter();
            meta.CString("2022.3.43f1");
            meta.I32(19);
            meta.U8(1);
            meta.I32(types.Length);
            foreach (var (classId, tree) in types)
            {
                meta.I32(classId);
                meta.U8(0);
                meta.I16(-1);
                var hash = new byte[16];
                if (TypeHashes.TryGetValue(classId, out var hex))
                    for (int i = 0; i < 16; i++)
                        hash[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                meta.Bytes(hash);
                GameWriter.TypeBlob(meta, tree);
                meta.I32(0);
            }
            meta.I32(objects.Length);
            long offset = 0;
            var starts = new long[objects.Length];
            for (int i = 0; i < objects.Length; i++)
            {
                starts[i] = offset;
                offset = (offset + objects[i].Data.Length + 7) & ~7L;
            }
            for (int i = 0; i < objects.Length; i++)
            {
                // object entries are aligned to 4 from the file's start (the header is 48 bytes)
                meta.Align();
                meta.I64(objects[i].PathId);
                meta.I64(starts[i]);
                meta.U32((uint)objects[i].Data.Length);
                meta.I32(objects[i].Type);
            }
            meta.I32(0); // scripts
            meta.I32(0); // externals
            meta.I32(0); // referenced types
            meta.CString("");
            var metadata = meta.ToArray();
            long dataOffset = (48 + metadata.Length + 15) & ~15L;
            long size = dataOffset + offset;
            var file = new byte[size];
            void Big32(int at, uint v)
            {
                file[at] = (byte)(v >> 24);
                file[at + 1] = (byte)(v >> 16);
                file[at + 2] = (byte)(v >> 8);
                file[at + 3] = (byte)v;
            }
            void Big64(int at, long v)
            {
                Big32(at, (uint)((ulong)v >> 32));
                Big32(at + 4, (uint)v);
            }
            Big32(8, 22);
            Big32(20, (uint)metadata.Length);
            Big64(24, size);
            Big64(32, dataOffset);
            Buffer.BlockCopy(metadata, 0, file, 48, metadata.Length);
            for (int i = 0; i < objects.Length; i++)
                Buffer.BlockCopy(objects[i].Data, 0, file, (int)(dataOffset + starts[i]), objects[i].Data.Length);
            return file;
        }

        // UnityFS (format 8) with its files in one uncompressed block, block info right after the header
        private static byte[] UnityFs((string Name, byte[] Data, uint Flags)[] nodes)
        {
            var info = new BigWriter();
            info.Bytes(new byte[16]);
            long total = 0;
            foreach (var node in nodes)
                total += node.Data.Length;
            // blocks of at most 128 KiB, as Unity writes them
            const int block = 1 << 17;
            int blocks = (int)((total + block - 1) / block);
            info.I32(blocks);
            for (int i = 0; i < blocks; i++)
            {
                uint length = (uint)Math.Min(block, total - (long)i * block);
                info.U32(length);
                info.U32(length);
                info.U16(0);
            }
            info.I32(nodes.Length);
            long offset = 0;
            foreach (var node in nodes)
            {
                info.I64(offset);
                info.I64(node.Data.Length);
                info.U32(node.Flags);
                info.CString(node.Name);
                offset += node.Data.Length;
            }
            var blockInfo = info.ToArray();

            var head = new BigWriter();
            head.CString("UnityFS");
            head.U32(8);
            head.CString("5.x.x");
            head.CString("2022.3.43f1");
            int headerSize = head.Length + 8 + 4 + 4 + 4;
            // block info and directory together (0x40), padded to 16 after the header (0x200), uncompressed
            int start = (headerSize + 15) & ~15;
            // ... and the data after the block info too
            long dataStart = (start + blockInfo.Length + 15) & ~15L;
            long size = dataStart + total;
            head.I64(size);
            head.U32((uint)blockInfo.Length);
            head.U32((uint)blockInfo.Length);
            head.U32(0x40 | 0x200);
            var bundle = new byte[size];
            var header = head.ToArray();
            Buffer.BlockCopy(header, 0, bundle, 0, header.Length);
            Buffer.BlockCopy(blockInfo, 0, bundle, start, blockInfo.Length);
            long at = dataStart;
            foreach (var node in nodes)
            {
                Buffer.BlockCopy(node.Data, 0, bundle, (int)at, node.Data.Length);
                at += node.Data.Length;
            }
            return bundle;
        }

        private sealed class BigWriter
        {
            private readonly MemoryStream _stream = new MemoryStream();
            public int Length => (int)_stream.Length;
            public byte[] ToArray() => _stream.ToArray();
            public void Bytes(byte[] b) => _stream.Write(b, 0, b.Length);
            public void U16(ushort v)
            {
                _stream.WriteByte((byte)(v >> 8));
                _stream.WriteByte((byte)v);
            }
            public void U32(uint v)
            {
                U16((ushort)(v >> 16));
                U16((ushort)v);
            }
            public void I32(int v) => U32((uint)v);
            public void I64(long v)
            {
                U32((uint)((ulong)v >> 32));
                U32((uint)v);
            }
            public void CString(string s)
            {
                Bytes(Encoding.UTF8.GetBytes(s));
                _stream.WriteByte(0);
            }
        }
    }

    // Little-endian writes, and values written by a type tree: the mirror of GameReader.Read
    public sealed class GameWriter
    {
        private readonly MemoryStream _stream = new MemoryStream();
        // where alignment counts from (a serialized file's metadata starts 48 bytes in)
        private readonly long _origin;

        public GameWriter(long origin = 48) => _origin = origin;

        public byte[] ToArray() => _stream.ToArray();
        public long Position => _stream.Position;

        public void Bytes(byte[] b) => _stream.Write(b, 0, b.Length);
        public void U8(byte v) => _stream.WriteByte(v);
        public void I16(short v) => Bytes(BitConverter.GetBytes(v));
        public void U16(ushort v) => Bytes(BitConverter.GetBytes(v));
        public void I32(int v) => Bytes(BitConverter.GetBytes(v));
        public void U32(uint v) => Bytes(BitConverter.GetBytes(v));
        public void I64(long v) => Bytes(BitConverter.GetBytes(v));
        public void U64(ulong v) => Bytes(BitConverter.GetBytes(v));
        public void F32(float v) => Bytes(BitConverter.GetBytes(v));
        public void F64(double v) => Bytes(BitConverter.GetBytes(v));

        public void Align()
        {
            while ((_stream.Position + _origin) % 4 != 0)
                _stream.WriteByte(0);
        }

        public void CString(string s)
        {
            Bytes(Encoding.UTF8.GetBytes(s));
            U8(0);
        }

        // an object's bytes (objects start 8-aligned, so alignment inside counts from 0)
        public static byte[] Value(GameTypeNode tree, GameValue value)
        {
            var w = new GameWriter(0);
            foreach (var child in tree.Children)
                w.Write(child, value.TryGetValue(child.Name, out var v) ? v : null);
            if (tree.Align)
                w.Align();
            return w.ToArray();
        }

        public void Write(GameTypeNode node, object value)
        {
            switch (node.Type)
            {
                case "bool": U8(Convert.ToBoolean(value ?? false) ? (byte)1 : (byte)0); break;
                case "SInt8": U8(unchecked((byte)Convert.ToSByte(value ?? 0))); break;
                case "UInt8":
                case "char": U8(Convert.ToByte(value ?? 0)); break;
                case "SInt16":
                case "short": I16(Convert.ToInt16(value ?? 0)); break;
                case "UInt16":
                case "unsigned short": U16(Convert.ToUInt16(value ?? 0)); break;
                case "SInt32":
                case "int": I32(Convert.ToInt32(value ?? 0)); break;
                case "UInt32":
                case "unsigned int":
                case "Type*": U32(Convert.ToUInt32(value ?? 0u)); break;
                case "SInt64":
                case "long long": I64(Convert.ToInt64(value ?? 0L)); break;
                case "UInt64":
                case "unsigned long long":
                case "FileSize": U64(Convert.ToUInt64(value ?? 0UL)); break;
                case "float": F32(Convert.ToSingle(value ?? 0f)); break;
                case "double": F64(Convert.ToDouble(value ?? 0.0)); break;
                case "string":
                {
                    var bytes = Encoding.UTF8.GetBytes(value as string ?? "");
                    I32(bytes.Length);
                    Bytes(bytes);
                    if (node.Children.Length > 0 && node.Children[0].Align)
                        Align();
                    break;
                }
                default:
                    if (node.Type.StartsWith("PPtr<"))
                    {
                        var p = value is GamePPtr pptr ? pptr : default;
                        I32(p.FileId);
                        I64(p.PathId);
                        break;
                    }
                    if (node.Children.Length > 0 && node.Children[0].Type == "Array")
                    {
                        WriteArray(node.Children[0], value);
                        if (node.Children[0].Align)
                            Align();
                        break;
                    }
                    if (node.Children.Length == 0)
                        throw new InvalidDataException($"can't write {node.Type} {node.Name}");
                    var obj = value as GameValue;
                    foreach (var child in node.Children)
                        Write(child, obj != null && obj.TryGetValue(child.Name, out var v) ? v : null);
                    break;
            }
            if (node.Align)
                Align();
        }

        private void WriteArray(GameTypeNode array, object value)
        {
            var element = array.Children[1];
            switch (value)
            {
                case byte[] bytes:
                    I32(bytes.Length);
                    Bytes(bytes);
                    break;
                case List<KeyValuePair<object, object>> pairs:
                    I32(pairs.Count);
                    foreach (var pair in pairs)
                    {
                        Write(element.Children[0], pair.Key);
                        Write(element.Children[1], pair.Value);
                        if (element.Align)
                            Align();
                    }
                    break;
                case System.Collections.IList list:
                    I32(list.Count);
                    foreach (var item in list)
                        Write(element, item);
                    break;
                default:
                    I32(0);
                    break;
            }
        }

        // a type tree as a format 22 blob: 32-byte nodes, then their strings (all in its own string table)
        public static void TypeBlob(GameWriter w, GameTypeNode tree)
        {
            var nodes = new List<(GameTypeNode Node, int Level)>();
            void Walk(GameTypeNode n, int level)
            {
                nodes.Add((n, level));
                foreach (var c in n.Children)
                    Walk(c, level + 1);
            }
            Walk(tree, 0);
            var strings = new MemoryStream();
            var offsets = new Dictionary<string, uint>();
            uint Offset(string s)
            {
                if (offsets.TryGetValue(s, out var known))
                    return known;
                var at = (uint)strings.Length;
                var bytes = Encoding.UTF8.GetBytes(s);
                strings.Write(bytes, 0, bytes.Length);
                strings.WriteByte(0);
                offsets[s] = at;
                return at;
            }
            var raw = new GameWriter(0);
            for (int i = 0; i < nodes.Count; i++)
            {
                var (n, level) = nodes[i];
                raw.U16((ushort)n.Version);
                raw.U8((byte)level);
                raw.U8(n.Type == "Array" ? (byte)1 : (byte)0);
                raw.U32(Offset(n.Type));
                // the type tree data keeps spaces in names as _ (none of the written classes has either)
                raw.U32(Offset(n.Name));
                raw.I32(n.Size);
                raw.I32(i);
                raw.I32(n.Meta);
                raw.U64(0);
            }
            var nodeBytes = raw.ToArray();
            var stringBytes = strings.ToArray();
            w.I32(nodes.Count);
            w.I32(stringBytes.Length);
            w.Bytes(nodeBytes);
            w.Bytes(stringBytes);
        }
    }
}
