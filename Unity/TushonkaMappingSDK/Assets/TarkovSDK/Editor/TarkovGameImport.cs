using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using EFT.GameTriggers;
using EFT.Interactive;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // Unity objects from the game's own files (TarkovGameFiles reads them): meshes, textures and materials, and whole
    // objects with their transforms, renderers, colliders, rigidbodies, joints, lights, LOD groups, particle systems,
    // line and trail renderers and the game components the SDK has stubs for, their values read by TarkovGameScripts.
    // Particle systems and line/trail renderers are copied through Unity's own serialized properties: the game's field
    // names are Unity's (the same version), so each value goes onto the property of its path (CopySerialized). Imports go into Assets/TarkovGameAssets/<game file>/
    // (gitignored in the SDK repo: never commit game assets), one asset per game object id, so an asset two imports
    // share is made once. Previews (GameModel) are the same meshes and materials kept in memory, LOD0 only.

    [InitializeOnLoad]
    public static class TarkovGameData
    {
        private const string FolderKey = "TarkovSDK.GameData";
        private static GameFiles _files;
        private static List<string> _scenes;
        private static string _folder;

        static TarkovGameData()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Close;
            EditorApplication.quitting += Close;
            GameScriptValues.Make = MakeValue;
        }

        // the game's EscapeFromTarkov_Data: picked in the Asset Importer, else the Map Definition's SPT root's
        public static string Folder
        {
            get
            {
                if (_folder != null)
                    return _folder;
                var set = EditorPrefs.GetString(FolderKey, "");
                if (string.IsNullOrEmpty(set))
                    set = Path.Combine(TarkovLootSpawnEditor.ItemDatabase.SptRoot() ?? "C:/spt41", "EscapeFromTarkov_Data");
                return _folder = set.Replace('\\', '/').TrimEnd('/');
            }
            set
            {
                EditorPrefs.SetString(FolderKey, value ?? "");
                Close();
            }
        }

        private static bool? _available;

        public static bool Available => _available ?? (_available = File.Exists(Path.Combine(Folder, "globalgamemanagers"))).Value;

        public static GameFiles Files
        {
            get
            {
                if (_files == null)
                    _files = new GameFiles(Folder);
                return _files;
            }
        }

        public static List<string> Scenes => _scenes ?? (_scenes = Available ? Files.Scenes() : new List<string>());

        // closes the game's files (they stay open while used)
        public static void Close()
        {
            // the preview thread reads the files
            TarkovGameModelWorker.Stop();
            GameSceneCache.Clear();
            _files?.Dispose();
            _files = null;
            _scenes = null;
            _folder = null;
            _available = null;
        }

        // ------------------------------------------------------------ game component classes

        private static Dictionary<string, Type> _types;

        // the project's MonoBehaviour class of a game script's full name (the SDK's stubs first)
        public static Type ScriptType(string fullName)
        {
            if (_types == null)
            {
                _types = new Dictionary<string, Type>(StringComparer.Ordinal);
                var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic)
                    .OrderBy(a => a.GetName().Name == "Assembly-CSharp" ? 0 : a.GetName().Name.Contains("Editor") ? 2 : 1);
                foreach (var assembly in assemblies)
                {
                    Type[] types;
                    try
                    {
                        types = assembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException e)
                    {
                        types = e.Types.Where(t => t != null).ToArray();
                    }
                    foreach (var t in types)
                        if (t.FullName != null && !t.IsAbstract && typeof(MonoBehaviour).IsAssignableFrom(t))
                        {
                            var name = t.FullName.Replace('+', '.');
                            if (!_types.ContainsKey(name))
                                _types[name] = t;
                        }
                }
            }
            return fullName != null && _types.TryGetValue(fullName, out var type) ? type : null;
        }

        private static readonly (string Category, Type Type)[] KindTypes =
        {
            ("Doors", typeof(Door)), ("Containers", typeof(LootableContainer)), ("Switches", typeof(Switch)), ("Lamps", typeof(LampController)),
            ("Windows", typeof(WindowBreaker)), ("Mounted Guns", typeof(StationaryWeapon)), ("Explosives", typeof(TriggerBallistic)),
            ("Fires", typeof(FlameDamageTrigger)),
        };

        // a script class -> the Asset Importer category its objects go in (for GameIndex.Scan)
        public static Dictionary<string, string> KindsByScript()
        {
            ScriptType("");
            var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in _types)
                foreach (var (category, type) in KindTypes)
                    if (type.IsAssignableFrom(pair.Value))
                    {
                        kinds[pair.Key] = category;
                        break;
                    }
            return kinds;
        }

        // ------------------------------------------------------------ Unity values for TarkovGameScripts

        private static WrapMode CurveWrap(int infinity) => infinity == 0 ? WrapMode.PingPong : infinity == 1 ? WrapMode.Loop : WrapMode.ClampForever;

        private static object MakeValue(string type, float[] f, int[] i)
        {
            switch (type)
            {
                case "UnityEngine.Vector2": return new Vector2(f[0], f[1]);
                case "UnityEngine.Vector3": return new Vector3(f[0], f[1], f[2]);
                case "UnityEngine.Vector4": return new Vector4(f[0], f[1], f[2], f[3]);
                case "UnityEngine.Quaternion": return new Quaternion(f[0], f[1], f[2], f[3]);
                case "UnityEngine.Color": return new Color(f[0], f[1], f[2], f[3]);
                case "UnityEngine.Rect": return new Rect(f[0], f[1], f[2], f[3]);
                // centre and extent
                case "UnityEngine.Bounds": return new Bounds(new Vector3(f[0], f[1], f[2]), new Vector3(f[3], f[4], f[5]) * 2f);
                case "UnityEngine.Matrix4x4":
                {
                    // e00 e01 e02 e03 e10 ...: row by row
                    var m = new Matrix4x4();
                    for (int k = 0; k < 16; k++)
                        m[k / 4, k % 4] = f[k];
                    return m;
                }
                case "UnityEngine.Vector2Int": return new Vector2Int(i[0], i[1]);
                case "UnityEngine.Vector3Int": return new Vector3Int(i[0], i[1], i[2]);
                case "UnityEngine.RectInt": return new RectInt(i[0], i[1], i[2], i[3]);
                case "UnityEngine.BoundsInt": return new BoundsInt(new Vector3Int(i[0], i[1], i[2]), new Vector3Int(i[3], i[4], i[5]));
                case "UnityEngine.LayerMask": return (LayerMask)i[0];
                case "UnityEngine.Color32":
                {
                    uint v = (uint)i[0];
                    return new Color32((byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24));
                }
                case "UnityEngine.Hash128": return new Hash128((uint)i[0], (uint)i[1], (uint)i[2], (uint)i[3]);
                case "UnityEngine.AnimationCurve":
                {
                    var keys = new Keyframe[f.Length / 7];
                    for (int k = 0; k < keys.Length; k++)
                    {
                        int at = k * 7;
                        keys[k] = new Keyframe(f[at], f[at + 1], f[at + 2], f[at + 3], f[at + 5], f[at + 6]) { weightedMode = (WeightedMode)(int)f[at + 4] };
                    }
                    return new AnimationCurve(keys) { preWrapMode = CurveWrap(i[0]), postWrapMode = CurveWrap(i[1]) };
                }
                case "UnityEngine.Gradient":
                {
                    int colors = Mathf.Clamp(i[18], 0, 8), alphas = Mathf.Clamp(i[19], 0, 8);
                    var ck = new GradientColorKey[colors];
                    var ak = new GradientAlphaKey[alphas];
                    for (int k = 0; k < colors; k++)
                        ck[k] = new GradientColorKey(new Color(f[k * 4], f[k * 4 + 1], f[k * 4 + 2]), i[k] / 65535f);
                    for (int k = 0; k < alphas; k++)
                        ak[k] = new GradientAlphaKey(f[k * 4 + 3], i[8 + k] / 65535f);
                    var gradient = new Gradient();
                    gradient.SetKeys(ck, ak);
                    gradient.mode = (GradientMode)i[16];
                    return gradient;
                }
            }
            return null;
        }
    }

    // Meshes, textures and materials built from the game's objects
    public static class TarkovGameAssets
    {
        public const string Root = "Assets/TarkovGameAssets";
        public const string PreviewPrefix = "SDK game model ";

        public static Mesh BuildMesh(GameFiles files, GameSerializedFile from, GamePPtr pptr, out string error)
        {
            var data = GameModelReader.Mesh(files, from, pptr, out error);
            if (data == null)
                return null;
            try
            {
                return CreateMesh(data);
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        private static Bounds Box(float[] center, float[] extent) =>
            center == null || extent == null ? new Bounds() : new Bounds(new Vector3(center[0], center[1], center[2]), new Vector3(extent[0], extent[1], extent[2]) * 2f);

        // a mesh from data read off the main thread (GameModelReader.Mesh)
        public static Mesh CreateMesh(GameMeshData data)
        {
            var flags = MeshUpdateFlags.DontRecalculateBounds;
            var mesh = new Mesh { name = data.Name };
            mesh.SetVertexBufferParams(data.VertexCount, data.Attributes
                .Select(a => new VertexAttributeDescriptor((VertexAttribute)a.Attribute, (VertexAttributeFormat)a.Format, a.Dimension, a.Stream)).ToArray());
            for (int s = 0; s < data.Streams.Length; s++)
                mesh.SetVertexBufferData(data.Streams[s], 0, 0, data.Streams[s].Length, s, flags);
            int indices = data.Indices.Length / (data.Wide ? 4 : 2);
            mesh.SetIndexBufferParams(indices, data.Wide ? IndexFormat.UInt32 : IndexFormat.UInt16);
            if (data.Wide)
            {
                var u = new uint[indices];
                Buffer.BlockCopy(data.Indices, 0, u, 0, indices * 4);
                mesh.SetIndexBufferData(u, 0, 0, indices, flags);
            }
            else
            {
                var u = new ushort[indices];
                Buffer.BlockCopy(data.Indices, 0, u, 0, indices * 2);
                mesh.SetIndexBufferData(u, 0, 0, indices, flags);
            }
            mesh.subMeshCount = data.SubMeshes.Length;
            for (int i = 0; i < data.SubMeshes.Length; i++)
            {
                var sub = data.SubMeshes[i];
                mesh.SetSubMesh(i, new SubMeshDescriptor(sub.IndexStart, sub.IndexCount, sub.Topology == 1 ? MeshTopology.Triangles : (MeshTopology)sub.Topology)
                {
                    baseVertex = sub.BaseVertex,
                    firstVertex = sub.FirstVertex,
                    vertexCount = sub.VertexCount,
                    bounds = Box(sub.Center, sub.Extent),
                }, flags);
            }
            mesh.bounds = Box(data.Center, data.Extent);
            return mesh;
        }

        // a texture from data read off the main thread (GameModelReader.Texture); null if this GPU can't take its format
        public static Texture2D CreateTexture(GameTextureData data, bool keepReadable)
        {
            var format = (TextureFormat)data.Format;
            if (!SystemInfo.SupportsTextureFormat(format))
                return null;
            var texture = new Texture2D(data.Width, data.Height, format, data.Mips, false)
            {
                name = data.Name,
                filterMode = (FilterMode)Mathf.Clamp(data.Filter, 0, 2),
                anisoLevel = data.Aniso,
                mipMapBias = data.MipBias,
                wrapModeU = (TextureWrapMode)data.WrapU,
                wrapModeV = (TextureWrapMode)data.WrapV,
            };
            try
            {
                texture.LoadRawTextureData(data.Data);
                texture.Apply(false, !keepReadable);
            }
            catch (Exception)
            {
                Object.DestroyImmediate(texture);
                return null;
            }
            return texture;
        }

        // a preview model's Unity objects (owned by the model; Dispose frees them), on simple Unity shaders
        public static GameModel CreateModel(GameModelData data)
        {
            var model = new GameModel { Error = data.Error };
            var meshes = new Dictionary<string, Mesh>();
            var materials = new Dictionary<string, Material>();
            var textures = new Dictionary<string, Texture2D>();
            bool any = false;
            var bounds = new Bounds();
            foreach (var part in data.Parts)
            {
                if (!meshes.TryGetValue(part.MeshKey, out var mesh))
                {
                    try
                    {
                        mesh = CreateMesh(part.Mesh);
                        mesh.name = PreviewPrefix + mesh.name;
                        mesh.hideFlags = HideFlags.HideAndDontSave;
                        mesh.UploadMeshData(true);
                        model.Owned.Add(mesh);
                    }
                    catch (Exception)
                    {
                        mesh = null;
                    }
                    meshes[part.MeshKey] = mesh;
                }
                if (mesh == null)
                    continue;
                var list = new Material[part.Materials.Length];
                for (int i = 0; i < list.Length; i++)
                {
                    var m = part.Materials[i];
                    if (m == null)
                        continue;
                    if (!materials.TryGetValue(m.Key, out var material))
                    {
                        material = PreviewMaterial(m, textures, model);
                        materials[m.Key] = material;
                    }
                    list[i] = material;
                }
                var matrix = Unity(part.Matrix);
                model.Parts.Add(new GameModel.Part { Mesh = mesh, Matrix = matrix, Materials = list });
                var b = TransformBounds(mesh.bounds, matrix);
                if (any)
                    bounds.Encapsulate(b);
                else
                    bounds = b;
                any = true;
            }
            model.Bounds = bounds;
            if (!any && model.Error == null)
                model.Error = "nothing to show";
            return model;
        }

        private static Material PreviewMaterial(GameMaterialData m, Dictionary<string, Texture2D> textures, GameModel model)
        {
            var name = m.ShaderName ?? "";
            bool cutout = name.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Cutoff", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          name.IndexOf("SpeedTree", StringComparison.OrdinalIgnoreCase) >= 0 || m.Queue >= 2450 && m.Queue < 3000;
            bool transparent = m.Queue >= 3000 || name.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0 && !cutout;
            var shader = Shader.Find(transparent ? "Legacy Shaders/Transparent/Diffuse" : cutout ? "Legacy Shaders/Transparent/Cutout/Diffuse" : "Legacy Shaders/Diffuse");
            if (m.Particle || IsParticleShader(name))
                shader = Shader.Find(IsAdditive(name) ? "Legacy Shaders/Particles/Additive" : "Legacy Shaders/Particles/Alpha Blended") ?? shader;
            var material = new Material(shader) { name = PreviewPrefix + m.Name, hideFlags = HideFlags.HideAndDontSave };
            model.Owned.Add(material);
            if (m.Main != null)
            {
                if (!textures.TryGetValue(m.MainKey, out var texture))
                {
                    texture = CreateTexture(m.Main, false);
                    if (texture != null)
                    {
                        texture.name = PreviewPrefix + texture.name;
                        texture.hideFlags = HideFlags.HideAndDontSave;
                        model.Owned.Add(texture);
                    }
                    textures[m.MainKey] = texture;
                }
                material.mainTexture = texture;
                if (m.MainScale != null)
                    material.mainTextureScale = new Vector2(m.MainScale[0], m.MainScale[1]);
                if (m.MainOffset != null)
                    material.mainTextureOffset = new Vector2(m.MainOffset[0], m.MainOffset[1]);
            }
            if (m.Color != null)
                material.color = new Color(m.Color[0], m.Color[1], m.Color[2], m.Color[3]);
            if (m.Cutoff >= 0f && material.HasProperty("_Cutoff"))
                material.SetFloat("_Cutoff", m.Cutoff);
            return material;
        }

        // System.Numerics (row vectors) to Unity (column vectors)
        public static Matrix4x4 Unity(System.Numerics.Matrix4x4 m) => new Matrix4x4(
            new Vector4(m.M11, m.M12, m.M13, m.M14), new Vector4(m.M21, m.M22, m.M23, m.M24),
            new Vector4(m.M31, m.M32, m.M33, m.M34), new Vector4(m.M41, m.M42, m.M43, m.M44));

        public static Bounds TransformBounds(Bounds b, Matrix4x4 m)
        {
            var result = new Bounds(m.MultiplyPoint3x4(b.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
                result.Encapsulate(m.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            return result;
        }

        // maxSize > 0 leaves out the mips above it (previews)
        public static Texture2D BuildTexture(GameFiles files, GameSerializedFile from, GamePPtr pptr, int maxSize, bool keepReadable, out string error)
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
                int w = t.Int("m_Width"), h = t.Int("m_Height"), mips = Math.Max(1, t.Int("m_MipCount"));
                var format = (TextureFormat)t.Int("m_TextureFormat");
                if (w <= 0 || h <= 0 || t.Int("m_ImageCount") > 1 || !SystemInfo.SupportsTextureFormat(format))
                {
                    error = $"texture format {format} isn't supported here";
                    return null;
                }
                var graphics = GraphicsFormatUtility.GetGraphicsFormat(format, false);
                int skip = 0;
                while (maxSize > 0 && skip < mips - 1 && (Math.Max(1, w >> skip) > maxSize || Math.Max(1, h >> skip) > maxSize))
                    skip++;
                long skipped = 0, total = 0;
                for (int i = 0; i < mips; i++)
                {
                    long size = GraphicsFormatUtility.ComputeMipmapSize(Math.Max(1, w >> i), Math.Max(1, h >> i), graphics);
                    if (i < skip)
                        skipped += size;
                    total += size;
                }
                var inline = t.Bytes("image_data");
                byte[] bytes;
                if (inline != null && inline.Length > 0)
                {
                    bytes = new byte[Math.Min(total, inline.Length) - skipped];
                    Buffer.BlockCopy(inline, (int)skipped, bytes, 0, bytes.Length);
                }
                else
                    bytes = files.Stream(file, t.Obj("m_StreamData"), skipped, total - skipped);
                if (bytes == null)
                {
                    error = "texture data not found";
                    return null;
                }
                var texture = new Texture2D(Math.Max(1, w >> skip), Math.Max(1, h >> skip), format, mips - skip, false) { name = t.Str("m_Name") };
                texture.LoadRawTextureData(bytes);
                var settings = t.Obj("m_TextureSettings");
                if (settings != null)
                {
                    texture.filterMode = (FilterMode)Mathf.Clamp(settings.Int("m_FilterMode"), 0, 2);
                    texture.anisoLevel = settings.Int("m_Aniso");
                    texture.mipMapBias = settings.Float("m_MipBias");
                    texture.wrapModeU = (TextureWrapMode)settings.Int("m_WrapU");
                    texture.wrapModeV = (TextureWrapMode)settings.Int("m_WrapV");
                }
                texture.Apply(false, !keepReadable);
                return texture;
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        public static string ShaderName(GameFiles files, GameSerializedFile from, GamePPtr shader) => GameModelReader.ShaderName(files, from, shader);

        public static bool IsImpostor(string shaderName) => GameModelReader.IsImpostor(shaderName);

        // the SDK's stand-in of a game shader; with create, a placeholder Tarkov/<name> is made for one the SDK lacks
        // (the client plugin swaps Tarkov/ shaders for the game's own of the name in the raid)
        public static Shader StandIn(string gameName, bool create)
        {
            if (string.IsNullOrEmpty(gameName))
                return null;
            var shader = Shader.Find("Tarkov/" + gameName);
            if (shader != null)
                return shader;
            // Unity's own shaders (Standard, Legacy Shaders/..., Nature/...) exist in the editor too
            shader = Shader.Find(gameName);
            if (shader != null && !gameName.StartsWith("Hidden/"))
                return shader;
            if (!create)
                return null;
            var folder = Root + "/Shaders";
            TarkovGeneratedAssets.EnsureFolder(folder);
            var safe = string.Concat(gameName.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));
            var path = $"{folder}/{safe}.shader";
            var text = Placeholder(gameName);
            if (!File.Exists(path) || File.ReadAllText(path) != text)
            {
                File.WriteAllText(path, text);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            }
            return AssetDatabase.LoadAssetAtPath<Shader>(path) ?? Shader.Find("Tarkov/" + gameName);
        }

        // a game shader for particles (effects' smoke, fire, sparks), by its name
        public static bool IsParticleShader(string name) => name != null && new[] { "Particle", "Additive", "Smoke", "Fire", "Explosion", "Distortion", "Alpha Blended", "Spark" }
            .Any(w => name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);

        public static bool IsAdditive(string name) => name != null && new[] { "Additive", "Fire", "Spark", "Add" }.Any(w => name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);

        // the placeholder for a game shader the SDK has no stand-in for: the main texture, blended like particles for
        // particle shaders (the client plugin swaps in the game's shader in the raid)
        private static string Placeholder(string gameName)
        {
            var name = gameName.Replace("\"", "");
            var head = "// Made by the Tarkov SDK's Asset Importer for the game's shader \"" + name + "\", which the SDK has no stand-in for.\n" +
                       "// In the raid the client plugin swaps it for the game's own shader of that name; here it only shows the main texture.\n";
            if (IsParticleShader(gameName))
                return head + "Shader \"Tarkov/" + name + "\"\n{\n    Properties\n    {\n" +
                       "        _TintColor (\"Tint Color\", Color) = (0.5,0.5,0.5,0.5)\n        _Color (\"Color\", Color) = (1,1,1,1)\n        _MainTex (\"Particle Texture\", 2D) = \"white\" {}\n" +
                       "    }\n    SubShader\n    {\n        Tags { \"Queue\"=\"Transparent\" \"IgnoreProjector\"=\"True\" \"RenderType\"=\"Transparent\" \"PreviewType\"=\"Plane\" }\n" +
                       "        Blend " + (IsAdditive(gameName) ? "SrcAlpha One" : "SrcAlpha OneMinusSrcAlpha") + "\n        ColorMask RGB\n        Cull Off Lighting Off ZWrite Off\n" +
                       "        Pass\n        {\n            CGPROGRAM\n            #pragma vertex vert\n            #pragma fragment frag\n            #include \"UnityCG.cginc\"\n" +
                       "            sampler2D _MainTex;\n            float4 _MainTex_ST;\n            fixed4 _TintColor;\n            fixed4 _Color;\n" +
                       "            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; };\n" +
                       "            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; };\n" +
                       "            v2f vert (appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.color = v.color; o.uv = TRANSFORM_TEX(v.uv, _MainTex); return o; }\n" +
                       "            fixed4 frag (v2f i) : SV_Target { return 2.0f * i.color * _TintColor * _Color * tex2D(_MainTex, i.uv); }\n" +
                       "            ENDCG\n        }\n    }\n}\n";
            return head + "Shader \"Tarkov/" + name + "\"\n{\n    Properties\n    {\n" +
                   "        _Color (\"Color\", Color) = (1,1,1,1)\n        _MainTex (\"Base\", 2D) = \"white\" {}\n" +
                   "    }\n    SubShader\n    {\n        Tags { \"RenderType\"=\"Opaque\" }\n        CGPROGRAM\n        #pragma surface surf Lambert\n" +
                   "        sampler2D _MainTex;\n        fixed4 _Color;\n        struct Input { float2 uv_MainTex; };\n" +
                   "        void surf (Input IN, inout SurfaceOutput o)\n        {\n            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;\n" +
                   "            o.Albedo = c.rgb;\n            o.Alpha = c.a;\n        }\n        ENDCG\n    }\n    FallBack \"Diffuse\"\n}\n";
        }

        private static readonly string[] MainTextureNames = { "_MainTex", "_BaseMap", "_Albedo", "_MainTex0", "_Diffuse", "_DiffuseMap", "_BaseColorMap" };

        // a material's values onto a new material: as is on its stand-in (texture gives each texture), or for a preview
        // on a simple Unity shader with the main texture and colour only
        public static Material BuildMaterial(GameFiles files, GameSerializedFile from, GamePPtr pptr, bool preview, Func<GameSerializedFile, GamePPtr, Texture> texture,
            Func<string, Shader> shaderFor, out string shaderName)
        {
            shaderName = null;
            if (!files.Resolve(from, pptr, out var file, out var info) || info.ClassId != 21)
                return null;
            var m = file.Read(info.PathId);
            shaderName = ShaderName(files, file, m.Ref("m_Shader"));
            var props = m.Obj("m_SavedProperties");
            var floats = props?.Map("m_Floats").Where(p => p.Key is string).ToDictionary(p => (string)p.Key, p => Convert.ToSingle(p.Value)) ?? new Dictionary<string, float>();
            int queue = m.Int("m_CustomRenderQueue");
            Shader shader;
            if (preview)
            {
                var name = shaderName ?? "";
                bool cutout = name.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Cutoff", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              name.IndexOf("SpeedTree", StringComparison.OrdinalIgnoreCase) >= 0 || queue >= 2450 && queue < 3000;
                bool transparent = queue >= 3000 || name.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0 && !cutout;
                shader = Shader.Find(transparent ? "Legacy Shaders/Transparent/Diffuse" : cutout ? "Legacy Shaders/Transparent/Cutout/Diffuse" : "Legacy Shaders/Diffuse");
            }
            else
                shader = shaderFor(shaderName);
            if (shader == null)
                shader = Shader.Find("Standard");
            var material = new Material(shader) { name = m.Str("m_Name") };
            if (preview)
            {
                foreach (var pair in props?.Map("m_TexEnvs") ?? new List<KeyValuePair<object, object>>())
                {
                    if (!(pair.Key is string name) || Array.IndexOf(MainTextureNames, name) < 0 || !(pair.Value is GameValue env) || env.Ref("m_Texture").IsNull)
                        continue;
                    var tex = texture(file, env.Ref("m_Texture"));
                    if (tex == null)
                        continue;
                    material.mainTexture = tex;
                    var scale = env.Floats("m_Scale");
                    var offset = env.Floats("m_Offset");
                    if (scale != null)
                        material.mainTextureScale = new Vector2(scale[0], scale[1]);
                    if (offset != null)
                        material.mainTextureOffset = new Vector2(offset[0], offset[1]);
                    break;
                }
                foreach (var pair in props?.Map("m_Colors") ?? new List<KeyValuePair<object, object>>())
                    if (pair.Key as string == "_Color" && pair.Value is float[] c)
                        material.color = new Color(c[0], c[1], c[2], c[3]);
                if (floats.TryGetValue("_Cutoff", out var cutoff))
                    material.SetFloat("_Cutoff", cutoff);
                material.name = PreviewPrefix + material.name;
                material.hideFlags = HideFlags.HideAndDontSave;
                return material;
            }
            foreach (var pair in props?.Map("m_TexEnvs") ?? new List<KeyValuePair<object, object>>())
            {
                if (!(pair.Key is string name) || !(pair.Value is GameValue env))
                    continue;
                if (!env.Ref("m_Texture").IsNull)
                    material.SetTexture(name, texture(file, env.Ref("m_Texture")));
                var scale = env.Floats("m_Scale");
                var offset = env.Floats("m_Offset");
                if (scale != null)
                    material.SetTextureScale(name, new Vector2(scale[0], scale[1]));
                if (offset != null)
                    material.SetTextureOffset(name, new Vector2(offset[0], offset[1]));
            }
            foreach (var pair in props?.Map("m_Ints") ?? new List<KeyValuePair<object, object>>())
                if (pair.Key is string name)
                    material.SetInteger(name, Convert.ToInt32(pair.Value));
            foreach (var pair in floats)
                material.SetFloat(pair.Key, pair.Value);
            foreach (var pair in props?.Map("m_Colors") ?? new List<KeyValuePair<object, object>>())
                if (pair.Key is string name && pair.Value is float[] c)
                    material.SetColor(name, new Color(c[0], c[1], c[2], c[3]));
            material.shaderKeywords = m.List("m_ValidKeywords").Concat(m.List("m_InvalidKeywords")).OfType<string>().ToArray();
            if (queue >= 0)
                material.renderQueue = queue;
            foreach (var pair in m.Map("stringTagMap"))
                if (pair.Key is string tag && pair.Value is string value)
                    material.SetOverrideTag(tag, value);
            foreach (var pass in m.List("disabledShaderPasses").OfType<string>())
                material.SetShaderPassEnabled(pass, false);
            material.enableInstancing = m.Bool("m_EnableInstancingVariants");
            material.doubleSidedGI = m.Bool("m_DoubleSidedGI");
            return material;
        }

        // the root's scale in the world (System.Numerics: rows are the scaled axes)
        public static Vector3 LossyScale(System.Numerics.Matrix4x4 world) =>
            new Vector3(new Vector3(world.M11, world.M12, world.M13).magnitude, new Vector3(world.M21, world.M22, world.M23).magnitude,
                new Vector3(world.M31, world.M32, world.M33).magnitude);

        public static Vector3 V(System.Numerics.Vector3 v) => new Vector3(v.X, v.Y, v.Z);
        public static Quaternion Q(System.Numerics.Quaternion q) => new Quaternion(q.X, q.Y, q.Z, q.W);
    }

    // Assets imported into the project, one per game object id: Assets/TarkovGameAssets/<file>/<kind>/<name>_<id>
    public sealed class GameAssetStore
    {
        private readonly GameFiles _files;
        private readonly Dictionary<string, Object> _made = new Dictionary<string, Object>();
        public readonly List<string> Notes = new List<string>();
        public int Created;

        public GameAssetStore(GameFiles files)
        {
            _files = files;
        }

        private static string Safe(string name)
        {
            name = string.IsNullOrWhiteSpace(name) ? "unnamed" : name.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Length > 60 ? name.Substring(0, 60) : name;
        }

        private static string FolderOf(string fileName, string kind) => $"{TarkovGameAssets.Root}/{Safe(fileName).Replace('.', '_')}/{kind}";

        // where the asset of that id in that game file goes
        public static string PathOf(string fileName, string kind, string name, long id, string ext) => $"{FolderOf(fileName, kind)}/{Safe(name)}_{id}{ext}";

        private T Get<T>(GameSerializedFile from, GamePPtr pptr, string kind, string ext, Func<GameSerializedFile, GameObjectInfo, string, T> build) where T : Object
        {
            if (pptr.IsNull || !_files.Resolve(from, pptr, out var file, out var info))
                return null;
            string key = file.Name + ":" + info.PathId;
            if (_made.TryGetValue(key, out var done))
                return done as T;
            string name = file.Read(info.PathId, "m_Name")?.Str("m_Name") ?? "";
            var folder = FolderOf(file.Name, kind);
            var path = PathOf(file.Name, kind, name, info.PathId, ext);
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
            {
                asset = build(file, info, name);
                if (asset != null)
                {
                    TarkovGeneratedAssets.EnsureFolder(folder);
                    AssetDatabase.CreateAsset(asset, path);
                    Created++;
                }
            }
            _made[key] = asset;
            return asset;
        }

        public Mesh Mesh(GameSerializedFile from, GamePPtr pptr) => Get<Mesh>(from, pptr, "Meshes", ".asset", (file, info, name) =>
        {
            var mesh = TarkovGameAssets.BuildMesh(_files, file, new GamePPtr { PathId = info.PathId }, out var error);
            if (mesh == null)
                Notes.Add($"mesh {name}: {error}");
            return mesh;
        });

        public Texture Texture(GameSerializedFile from, GamePPtr pptr)
        {
            if (!_files.Resolve(from, pptr, out var file, out var info))
                return null;
            // reflection cubes: the SDK's (see TarkovReflectionCube)
            if (info.ClassId == 89)
                return TarkovReflectionCube.Get();
            if (info.ClassId != 28)
                return null;
            return Get<Texture2D>(from, pptr, "Textures", ".asset", (f, i, name) =>
            {
                var texture = TarkovGameAssets.BuildTexture(_files, f, new GamePPtr { PathId = i.PathId }, 0, true, out var error);
                if (texture == null)
                    Notes.Add($"texture {name}: {error}");
                return texture;
            });
        }

        public Material Material(GameSerializedFile from, GamePPtr pptr) => Get<Material>(from, pptr, "Materials", ".mat", (file, info, name) =>
            TarkovGameAssets.BuildMaterial(_files, file, new GamePPtr { PathId = info.PathId }, false, Texture, n => TarkovGameAssets.StandIn(n, true), out _));

        public string ShaderOf(GameSerializedFile from, GamePPtr material)
        {
            var m = _files.Read(from, material, "m_Name", "m_Shader");
            if (m == null || !_files.Resolve(from, material, out var file, out _))
                return null;
            return TarkovGameAssets.ShaderName(_files, file, m.Ref("m_Shader"));
        }
    }

    // A game object (a scene's unit, or an asset file's prefab) rebuilt as Unity objects
    public static class TarkovGameObjects
    {
        // builds the object in a preview scene and saves it as a prefab at path; the prefab, or null
        public static GameObject ImportPrefab(GameFiles files, GameSceneTree tree, GameSceneTree.Node root, string path, GameAssetStore store)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var go = Build(files, tree, root, store, scene);
                if (go == null)
                    return null;
                TarkovPrefabLibrary.ClearIds(go);
                return PrefabUtility.SaveAsPrefabAsset(go, path);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        // several objects under one new root (a game effect's pieces), each at the root's origin
        public static GameObject ImportGroup(GameFiles files, GameSceneTree tree, List<GameSceneTree.Node> parts, string name, string path, GameAssetStore store)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject(name);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
                foreach (var part in parts)
                {
                    var go = Build(files, tree, part, store, scene);
                    if (go != null)
                        go.transform.SetParent(root.transform, false);
                }
                TarkovPrefabLibrary.ClearIds(root);
                return root.transform.childCount > 0 ? PrefabUtility.SaveAsPrefabAsset(root, path) : null;
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        public static GameObject Build(GameFiles files, GameSceneTree tree, GameSceneTree.Node root, GameAssetStore store, UnityEngine.SceneManagement.Scene scene)
        {
            var file = tree.File;
            var tags = files.Tags();
            var projectTags = new HashSet<string>(UnityEditorInternal.InternalEditorUtility.tags);
            var created = new Dictionary<long, Object>();
            var nodes = tree.Subtree(root).ToList();
            GameObject rootGo = null;
            foreach (var node in nodes)
            {
                var go = new GameObject(node.Name);
                if (rootGo == null)
                {
                    rootGo = go;
                    if (scene.IsValid())
                        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
                    go.transform.localScale = TarkovGameAssets.LossyScale(node.World);
                }
                else
                {
                    go.transform.SetParent(((GameObject)created[tree.Get(node.Parent).GameObject]).transform, false);
                    go.transform.localPosition = TarkovGameAssets.V(node.Position);
                    go.transform.localRotation = TarkovGameAssets.Q(node.Rotation);
                    go.transform.localScale = TarkovGameAssets.V(node.Scale);
                }
                go.layer = Mathf.Clamp(node.Layer, 0, 31);
                var tag = TagName(node.Tag, tags);
                if (tag != null && projectTags.Contains(tag))
                    go.tag = tag;
                created[node.GameObject] = go;
                created[node.Transform] = go.transform;
            }

            var skipped = new HashSet<string>();
            var lodGroups = new List<(GameObject, long)>();
            var joints = new List<(GameObject, long)>();
            var serialized = new List<(Component, long)>();
            var scripts = new List<(GameObject, long, string)>();
            foreach (var node in nodes)
            {
                var go = (GameObject)created[node.GameObject];
                int script = 0;
                foreach (var (classId, pathId) in node.Components)
                {
                    try
                    {
                        var component = AddComponent(files, file, go, classId, pathId, store, skipped);
                        if (component != null)
                            created[pathId] = component;
                    }
                    catch (Exception e)
                    {
                        store.Notes.Add($"{node.Name}: {ClassName(classId)} not made ({e.Message})");
                    }
                    if (classId == 205)
                        lodGroups.Add((go, pathId));
                    else if (classId == 144)
                        joints.Add((go, pathId));
                    if (created.TryGetValue(pathId, out var made) && (classId == 198 || classId == 199 || classId == 120 || classId == 96))
                        serialized.Add(((Component)made, pathId));
                    else if (classId == 114)
                        scripts.Add((go, pathId, script < node.Scripts.Count ? node.Scripts[script++] : null));
                }
            }

            foreach (var (go, pathId) in lodGroups)
            {
                var g = file.Read(pathId);
                var lods = new List<LOD>();
                foreach (var lod in g.List("m_LODs").OfType<GameValue>())
                {
                    var renderers = lod.List("renderers").OfType<GameValue>()
                        .Select(r => created.TryGetValue(r.Ref("renderer").PathId, out var o) ? o as Renderer : null).Where(r => r != null).ToArray();
                    lods.Add(new LOD(lod.Float("screenRelativeHeight"), renderers) { fadeTransitionWidth = lod.Float("fadeTransitionWidth") });
                }
                if (lods.All(l => l.renderers.Length == 0))
                    continue;
                var group = go.AddComponent<LODGroup>();
                group.SetLODs(lods.ToArray());
                var reference = g.Floats("m_LocalReferencePoint");
                if (reference != null)
                    group.localReferencePoint = new Vector3(reference[0], reference[1], reference[2]);
                group.size = g.Float("m_Size");
                group.fadeMode = (LODFadeMode)g.Int("m_FadeMode");
                group.animateCrossFading = g.Bool("m_AnimateCrossFading");
                group.enabled = g.Bool("m_Enabled");
                created[pathId] = group;
            }

            // joints after every rigidbody exists (they connect to others)
            foreach (var (go, pathId) in joints)
            {
                if (go.GetComponent<Rigidbody>() == null)
                    continue;
                var j = file.Read(pathId);
                var joint = go.AddComponent<CharacterJoint>();
                var connected = j.Ref("m_ConnectedBody");
                joint.connectedBody = connected.FileId == 0 && created.TryGetValue(connected.PathId, out var body) ? body as Rigidbody : null;
                joint.autoConfigureConnectedAnchor = j.Bool("m_AutoConfigureConnectedAnchor");
                joint.anchor = V3(j.Floats("m_Anchor"));
                joint.axis = V3(j.Floats("m_Axis"));
                joint.connectedAnchor = V3(j.Floats("m_ConnectedAnchor"));
                joint.swingAxis = V3(j.Floats("m_SwingAxis"));
                joint.twistLimitSpring = Spring(j.Obj("m_TwistLimitSpring"));
                joint.swingLimitSpring = Spring(j.Obj("m_SwingLimitSpring"));
                joint.lowTwistLimit = Limit(j.Obj("m_LowTwistLimit"));
                joint.highTwistLimit = Limit(j.Obj("m_HighTwistLimit"));
                joint.swing1Limit = Limit(j.Obj("m_Swing1Limit"));
                joint.swing2Limit = Limit(j.Obj("m_Swing2Limit"));
                joint.enableProjection = j.Bool("m_EnableProjection");
                joint.projectionDistance = j.Float("m_ProjectionDistance");
                joint.projectionAngle = j.Float("m_ProjectionAngle");
                joint.breakForce = j.Float("m_BreakForce");
                joint.breakTorque = j.Float("m_BreakTorque");
                joint.enableCollision = j.Bool("m_EnableCollision");
                joint.enablePreprocessing = j.Bool("m_EnablePreprocessing");
                joint.massScale = j.Float("m_MassScale");
                joint.connectedMassScale = j.Float("m_ConnectedMassScale");
                created[pathId] = joint;
            }

            Object Fit(Object o, Type type)
            {
                if (o == null)
                    return null;
                if (type.IsInstanceOfType(o))
                    return o;
                if (o is GameObject g && typeof(Component).IsAssignableFrom(type))
                    return g.GetComponent(type);
                if (o is Component c)
                {
                    if (type == typeof(GameObject))
                        return c.gameObject;
                    if (typeof(Component).IsAssignableFrom(type))
                        return c.GetComponent(type);
                }
                return null;
            }
            Object Resolve(GamePPtr pptr, Type type)
            {
                if (pptr.FileId == 0 && created.TryGetValue(pptr.PathId, out var made))
                    return Fit(made, type);
                if (!files.Resolve(file, pptr, out var target, out var info))
                    return null;
                switch (info.ClassId)
                {
                    case 43: return Fit(store.Mesh(file, pptr), type);
                    case 21: return Fit(store.Material(file, pptr), type);
                    case 28:
                    case 89: return Fit(store.Texture(file, pptr), type);
                }
                return null;
            }
            // particle systems (sub-emitters, shapes and collisions point at other objects) once everything exists
            foreach (var (component, pathId) in serialized)
            {
                try
                {
                    CopySerialized(component, file.Read(pathId), Resolve);
                }
                catch (Exception e)
                {
                    store.Notes.Add($"{component.name}: {component.GetType().Name}'s values not all copied ({e.Message})");
                }
            }
            var reader = new GameScriptReader { Resolve = (pptr, type) => Resolve(pptr, type) };
            foreach (var (go, pathId, scriptName) in scripts)
            {
                var type = TarkovGameData.ScriptType(scriptName);
                if (type == null)
                {
                    skipped.Add(scriptName ?? "an unknown script");
                    continue;
                }
                var component = go.GetComponent(type) != null && Attribute.IsDefined(type, typeof(DisallowMultipleComponent)) ? null : go.AddComponent(type);
                if (component == null)
                    continue;
                created[pathId] = component;
                GameValue header = null;
                bool read = file.ReadScript(pathId, out header, (r, size) => reader.Read(r, size, type, component));
                if (component is Behaviour behaviour && header != null)
                    behaviour.enabled = header.Long("m_Enabled") != 0;
                if (!read)
                    store.Notes.Add($"{go.name}: {type.Name}'s values not read ({reader.Error}); it has its defaults");
            }

            foreach (var node in nodes)
                if (!node.Active)
                    ((GameObject)created[node.GameObject]).SetActive(false);
            if (skipped.Count > 0)
                store.Notes.Add("left out (no SDK class): " + string.Join(", ", skipped.OrderBy(s => s)));
            return rootGo;
        }

        // Unity's built-in tags, then the TagManager's from 20000
        private static string TagName(int tag, List<string> tags)
        {
            switch (tag)
            {
                case 0: return null;
                case 1: return "Respawn";
                case 2: return "Finish";
                case 3: return "EditorOnly";
                case 5: return "MainCamera";
                case 6: return "Player";
                case 7: return "GameController";
            }
            int i = tag - 20000;
            return i >= 0 && i < tags.Count ? tags[i] : null;
        }

        private static string ClassName(int classId)
        {
            switch (classId)
            {
                case 23: return "MeshRenderer";
                case 33: return "MeshFilter";
                case 54: return "Rigidbody";
                case 64: return "MeshCollider";
                case 65: return "BoxCollider";
                case 82: return "AudioSource";
                case 95: return "Animator";
                case 108: return "Light";
                case 114: return "MonoBehaviour";
                case 135: return "SphereCollider";
                case 136: return "CapsuleCollider";
                case 137: return "SkinnedMeshRenderer";
                case 144: return "CharacterJoint";
                case 198: return "ParticleSystem";
                case 199: return "ParticleSystemRenderer";
                case 120: return "LineRenderer";
                case 96: return "TrailRenderer";
                case 205: return "LODGroup";
                case 208: return "NavMeshObstacle";
                default: return "class " + classId;
            }
        }

        private static Vector3 V3(float[] f) => f != null && f.Length >= 3 ? new Vector3(f[0], f[1], f[2]) : Vector3.zero;

        // a built-in component's read values onto its serialized properties of the same names
        public static void CopySerialized(Component component, GameValue value, Func<GamePPtr, Type, Object> resolve)
        {
            if (component == null || value == null)
                return;
            var so = new SerializedObject(component);
            foreach (var pair in value)
            {
                if (pair.Key == "m_GameObject")
                    continue;
                var prop = so.FindProperty(pair.Key);
                if (prop != null)
                    Assign(prop, pair.Value, resolve);
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static readonly Dictionary<string, Type> ObjectTypes = new Dictionary<string, Type>();

        // "PPtr<$Material>" -> Material
        private static Type ReferenceType(SerializedProperty prop)
        {
            var name = prop.type;
            if (name.StartsWith("PPtr<"))
                name = name.Substring(5).TrimEnd('>').TrimStart('$');
            lock (ObjectTypes)
                if (ObjectTypes.TryGetValue(name, out var known))
                    return known;
            Type found = typeof(Object);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.GetName().Name.StartsWith("UnityEngine"))
                    continue;
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray();
                }
                var t = types.FirstOrDefault(x => x.Name == name && typeof(Object).IsAssignableFrom(x));
                if (t != null)
                {
                    found = t;
                    break;
                }
            }
            lock (ObjectTypes)
                ObjectTypes[name] = found;
            return found;
        }

        private static Vector4 V4(float[] f) => new Vector4(f.Length > 0 ? f[0] : 0f, f.Length > 1 ? f[1] : 0f, f.Length > 2 ? f[2] : 0f, f.Length > 3 ? f[3] : 0f);

        private static void Assign(SerializedProperty prop, object v, Func<GamePPtr, Type, Object> resolve)
        {
            if (v == null)
                return;
            switch (prop.propertyType)
            {
                case SerializedPropertyType.ObjectReference:
                    if (v is GamePPtr p)
                        prop.objectReferenceValue = p.IsNull ? null : resolve(p, ReferenceType(prop));
                    return;
                case SerializedPropertyType.AnimationCurve:
                    if (v is GameValue curve)
                        prop.animationCurveValue = Curve(curve);
                    return;
                case SerializedPropertyType.Gradient:
                    if (v is GameValue gradient)
                        prop.gradientValue = Gradient(gradient);
                    return;
                case SerializedPropertyType.LayerMask:
                    prop.intValue = (int)(v is GameValue bits ? bits.Long("m_Bits") : Convert.ToInt64(v));
                    return;
                case SerializedPropertyType.Boolean:
                    if (!(v is GameValue))
                        prop.boolValue = Convert.ToBoolean(v);
                    return;
                case SerializedPropertyType.Enum:
                    if (!(v is GameValue))
                        prop.intValue = Convert.ToInt32(v);
                    return;
                case SerializedPropertyType.Integer:
                    if (v is ulong big)
                        prop.ulongValue = big;
                    else if (!(v is GameValue))
                        prop.longValue = Convert.ToInt64(v);
                    return;
                case SerializedPropertyType.Float:
                    if (v is GameValue)
                        return;
                    if (prop.type == "double")
                        prop.doubleValue = Convert.ToDouble(v);
                    else
                        prop.floatValue = Convert.ToSingle(v);
                    return;
                case SerializedPropertyType.String:
                    prop.stringValue = v as string ?? "";
                    return;
                case SerializedPropertyType.Vector2:
                    if (v is float[] v2)
                        prop.vector2Value = V4(v2);
                    return;
                case SerializedPropertyType.Vector3:
                    if (v is float[] v3)
                        prop.vector3Value = V4(v3);
                    return;
                case SerializedPropertyType.Vector4:
                    if (v is float[] v4)
                        prop.vector4Value = V4(v4);
                    return;
                case SerializedPropertyType.Quaternion:
                    if (v is float[] q && q.Length >= 4)
                        prop.quaternionValue = new Quaternion(q[0], q[1], q[2], q[3]);
                    return;
                case SerializedPropertyType.Color:
                    if (v is float[] c && c.Length >= 4)
                        prop.colorValue = new Color(c[0], c[1], c[2], c[3]);
                    else if (v is uint packed)
                        prop.colorValue = new Color32((byte)packed, (byte)(packed >> 8), (byte)(packed >> 16), (byte)(packed >> 24));
                    return;
                case SerializedPropertyType.Bounds:
                    if (v is GameValue b && b.Floats("m_Center") is float[] center && b.Floats("m_Extent") is float[] extent)
                        prop.boundsValue = new Bounds(V4(center), (Vector3)V4(extent) * 2f);
                    return;
                case SerializedPropertyType.ManagedReference:
                case SerializedPropertyType.Hash128:
                    return;
            }
            if (prop.isArray)
            {
                if (v is List<object> list)
                {
                    prop.arraySize = list.Count;
                    for (int i = 0; i < list.Count; i++)
                        Assign(prop.GetArrayElementAtIndex(i), list[i], resolve);
                }
                else if (v is float[] floats)
                {
                    prop.arraySize = floats.Length;
                    for (int i = 0; i < floats.Length; i++)
                        Assign(prop.GetArrayElementAtIndex(i), floats[i], resolve);
                }
                else if (v is byte[] bytes)
                {
                    prop.arraySize = bytes.Length;
                    for (int i = 0; i < bytes.Length; i++)
                        prop.GetArrayElementAtIndex(i).intValue = bytes[i];
                }
                return;
            }
            if (v is GameValue value)
                foreach (var pair in value)
                {
                    var child = prop.FindPropertyRelative(pair.Key);
                    if (child != null)
                        Assign(child, pair.Value, resolve);
                }
        }

        private static AnimationCurve Curve(GameValue v)
        {
            var keys = v.List("m_Curve").OfType<GameValue>().Select(k => new Keyframe(k.Float("time"), k.Float("value"), k.Float("inSlope"), k.Float("outSlope"),
                k.Float("inWeight"), k.Float("outWeight")) { weightedMode = (WeightedMode)k.Int("weightedMode") }).ToArray();
            WrapMode Wrap(int infinity) => infinity == 0 ? WrapMode.PingPong : infinity == 1 ? WrapMode.Loop : WrapMode.ClampForever;
            return new AnimationCurve(keys) { preWrapMode = Wrap(v.Int("m_PreInfinity")), postWrapMode = Wrap(v.Int("m_PostInfinity")) };
        }

        private static Gradient Gradient(GameValue v)
        {
            int colors = Mathf.Clamp(v.Int("m_NumColorKeys"), 0, 8), alphas = Mathf.Clamp(v.Int("m_NumAlphaKeys"), 0, 8);
            var ck = new GradientColorKey[colors];
            var ak = new GradientAlphaKey[alphas];
            for (int i = 0; i < 8; i++)
            {
                var key = v.Floats("key" + i) ?? new float[4];
                if (i < colors)
                    ck[i] = new GradientColorKey(new Color(key[0], key[1], key[2]), v.Int("ctime" + i) / 65535f);
                if (i < alphas)
                    ak[i] = new GradientAlphaKey(key[3], v.Int("atime" + i) / 65535f);
            }
            var gradient = new Gradient();
            gradient.SetKeys(ck, ak);
            gradient.mode = (GradientMode)v.Int("m_Mode");
            return gradient;
        }

        private static SoftJointLimitSpring Spring(GameValue v) => new SoftJointLimitSpring { spring = v?.Float("spring") ?? 0f, damper = v?.Float("damper") ?? 0f };

        private static SoftJointLimit Limit(GameValue v) =>
            new SoftJointLimit { limit = v?.Float("limit") ?? 0f, bounciness = v?.Float("bounciness") ?? 0f, contactDistance = v?.Float("contactDistance") ?? 0f };

        private static T Ensure<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }

        // the Unity components the importer copies; LOD groups and scripts are added after (they reference the others)
        private static Component AddComponent(GameFiles files, GameSerializedFile file, GameObject go, int classId, long pathId, GameAssetStore store, HashSet<string> skipped)
        {
            switch (classId)
            {
                case 4:
                case 114:
                case 144:
                case 205:
                    return null;
                case 33:
                {
                    var mesh = store.Mesh(file, file.Read(pathId).Ref("m_Mesh"));
                    var filter = Ensure<MeshFilter>(go);
                    filter.sharedMesh = mesh;
                    return filter;
                }
                case 23:
                case 137:
                {
                    var r = file.Read(pathId);
                    var materials = r.List("m_Materials").OfType<GamePPtr>().ToList();
                    // AmplifyImpostor billboards: the game's impostor system isn't in the SDK
                    if (materials.Any(m => TarkovGameAssets.IsImpostor(store.ShaderOf(file, m))))
                    {
                        var impostor = go.GetComponent<MeshFilter>();
                        if (impostor != null)
                            Object.DestroyImmediate(impostor);
                        skipped.Add("impostor renderers");
                        return null;
                    }
                    if (classId == 137)
                    {
                        // a skinned mesh comes in as a plain one in its bind pose
                        Ensure<MeshFilter>(go).sharedMesh = store.Mesh(file, r.Ref("m_Mesh"));
                    }
                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.enabled = r.Bool("m_Enabled");
                    renderer.shadowCastingMode = (ShadowCastingMode)r.Int("m_CastShadows");
                    renderer.receiveShadows = r.Int("m_ReceiveShadows") != 0;
                    renderer.allowOcclusionWhenDynamic = r.Int("m_DynamicOccludee") != 0;
                    renderer.staticShadowCaster = r.Int("m_StaticShadowCaster") != 0;
                    renderer.motionVectorGenerationMode = (MotionVectorGenerationMode)r.Int("m_MotionVectors");
                    renderer.lightProbeUsage = (LightProbeUsage)r.Int("m_LightProbeUsage");
                    renderer.reflectionProbeUsage = (ReflectionProbeUsage)r.Int("m_ReflectionProbeUsage");
                    renderer.renderingLayerMask = (uint)r.Long("m_RenderingLayerMask");
                    renderer.rendererPriority = r.Int("m_RendererPriority");
                    renderer.sharedMaterials = materials.Select(m => store.Material(file, m)).ToArray();
                    return renderer;
                }
                case 65:
                case 135:
                case 136:
                case 64:
                {
                    var c = file.Read(pathId);
                    Collider collider;
                    if (classId == 65)
                    {
                        var box = go.AddComponent<BoxCollider>();
                        box.size = V3(c.Floats("m_Size"));
                        box.center = V3(c.Floats("m_Center"));
                        collider = box;
                    }
                    else if (classId == 135)
                    {
                        var sphere = go.AddComponent<SphereCollider>();
                        sphere.radius = c.Float("m_Radius");
                        sphere.center = V3(c.Floats("m_Center"));
                        collider = sphere;
                    }
                    else if (classId == 136)
                    {
                        var capsule = go.AddComponent<CapsuleCollider>();
                        capsule.radius = c.Float("m_Radius");
                        capsule.height = c.Float("m_Height");
                        capsule.direction = c.Int("m_Direction");
                        capsule.center = V3(c.Floats("m_Center"));
                        collider = capsule;
                    }
                    else
                    {
                        var mesh = go.AddComponent<MeshCollider>();
                        mesh.sharedMesh = store.Mesh(file, c.Ref("m_Mesh"));
                        mesh.convex = c.Bool("m_Convex");
                        mesh.cookingOptions = (MeshColliderCookingOptions)c.Int("m_CookingOptions");
                        collider = mesh;
                    }
                    collider.isTrigger = c.Bool("m_IsTrigger");
                    collider.enabled = c.Bool("m_Enabled");
                    collider.includeLayers = (int)(c.Obj("m_IncludeLayers")?.Long("m_Bits") ?? 0);
                    collider.excludeLayers = (int)(c.Obj("m_ExcludeLayers")?.Long("m_Bits") ?? 0);
                    collider.layerOverridePriority = c.Int("m_LayerOverridePriority");
                    return collider;
                }
                case 54:
                {
                    var b = file.Read(pathId);
                    var body = go.AddComponent<Rigidbody>();
                    body.mass = b.Float("m_Mass");
                    body.drag = b.Float("m_Drag");
                    body.angularDrag = b.Float("m_AngularDrag");
                    body.useGravity = b.Bool("m_UseGravity");
                    body.isKinematic = b.Bool("m_IsKinematic");
                    body.interpolation = (RigidbodyInterpolation)b.Int("m_Interpolate");
                    body.constraints = (RigidbodyConstraints)b.Int("m_Constraints");
                    body.collisionDetectionMode = (CollisionDetectionMode)b.Int("m_CollisionDetection");
                    if (!b.Bool("m_ImplicitCom"))
                        body.centerOfMass = V3(b.Floats("m_CenterOfMass"));
                    return body;
                }
                case 198:
                    // its renderer comes with it
                    return Ensure<ParticleSystem>(go);
                case 199:
                    return Ensure<ParticleSystemRenderer>(go);
                case 120:
                    return Ensure<LineRenderer>(go);
                case 96:
                    return Ensure<TrailRenderer>(go);
                case 108:
                {
                    var l = file.Read(pathId);
                    var light = go.AddComponent<Light>();
                    light.type = (LightType)l.Int("m_Type");
                    var color = l.Floats("m_Color");
                    if (color != null)
                        light.color = new Color(color[0], color[1], color[2], color[3]);
                    light.intensity = l.Float("m_Intensity");
                    light.range = l.Float("m_Range");
                    light.spotAngle = l.Float("m_SpotAngle");
                    light.innerSpotAngle = l.Float("m_InnerSpotAngle");
                    light.cookieSize = l.Float("m_CookieSize");
                    var shadows = l.Obj("m_Shadows");
                    if (shadows != null)
                    {
                        light.shadows = (LightShadows)shadows.Int("m_Type");
                        light.shadowStrength = shadows.Float("m_Strength");
                        light.shadowBias = shadows.Float("m_Bias");
                        light.shadowNormalBias = shadows.Float("m_NormalBias");
                        light.shadowNearPlane = shadows.Float("m_NearPlane");
                    }
                    light.cookie = store.Texture(file, l.Ref("m_Cookie"));
                    light.renderMode = (LightRenderMode)l.Int("m_RenderMode");
                    light.cullingMask = (int)(l.Obj("m_CullingMask")?.Long("m_Bits") ?? -1);
                    light.lightmapBakeType = (LightmapBakeType)l.Int("m_Lightmapping");
                    light.bounceIntensity = l.Float("m_BounceIntensity");
                    light.colorTemperature = l.Float("m_ColorTemperature");
                    light.useColorTemperature = l.Bool("m_UseColorTemperature");
                    light.enabled = l.Long("m_Enabled") != 0;
                    return light;
                }
                default:
                    skipped.Add(ClassName(classId));
                    return null;
            }
        }
    }

    // An object's look in memory (LOD0, no shadow-only or disabled renderers), drawn by previews
    public sealed class GameModel
    {
        public struct Part
        {
            public Mesh Mesh;
            public Matrix4x4 Matrix;
            public Material[] Materials;
        }

        public readonly List<Part> Parts = new List<Part>();
        public Bounds Bounds;
        public string Error;
        // the meshes, materials and textures made for this model alone (CreateModel)
        public readonly List<Object> Owned = new List<Object>();

        public void Dispose()
        {
            foreach (var o in Owned)
                if (o != null)
                    Object.DestroyImmediate(o);
            Owned.Clear();
            Parts.Clear();
        }

        public void Draw(Matrix4x4 at, Camera camera)
        {
            foreach (var part in Parts)
                if (part.Mesh != null)
                    for (int i = 0; i < part.Mesh.subMeshCount; i++)
                    {
                        var material = part.Materials[Mathf.Min(i, part.Materials.Length - 1)];
                        if (material != null)
                            Graphics.DrawMesh(part.Mesh, at * part.Matrix, material, 0, camera, i);
                    }
        }
    }

    // Meshes, materials and textures for previews, kept by game object id and freed together (named, so they're also
    // freed after a script reload)
    public sealed class GamePreviewCache
    {
        private readonly Dictionary<string, Mesh> _meshes = new Dictionary<string, Mesh>();
        private readonly Dictionary<string, Material> _materials = new Dictionary<string, Material>();
        private readonly Dictionary<string, Texture> _textures = new Dictionary<string, Texture>();
        public readonly int TextureSize;
        public readonly bool StandIns;

        // standIns: the materials on the SDK's stand-in shaders (as in a map), else simple ones with their main texture
        public GamePreviewCache(int textureSize, bool standIns)
        {
            TextureSize = textureSize;
            StandIns = standIns;
        }

        public int Count => _meshes.Count + _textures.Count;

        public void Clear()
        {
            foreach (var o in _meshes.Values.Cast<Object>().Concat(_materials.Values).Concat(_textures.Values.Where(t => !EditorUtility.IsPersistent(t))))
                if (o != null)
                    Object.DestroyImmediate(o);
            _meshes.Clear();
            _materials.Clear();
            _textures.Clear();
        }

        // what's left from before a script reload
        public static void FreeAll()
        {
            foreach (var o in Resources.FindObjectsOfTypeAll<Mesh>().Cast<Object>().Concat(Resources.FindObjectsOfTypeAll<Material>())
                         .Concat(Resources.FindObjectsOfTypeAll<Texture2D>()))
                if (o != null && o.name.StartsWith(TarkovGameAssets.PreviewPrefix) && !EditorUtility.IsPersistent(o))
                    Object.DestroyImmediate(o);
        }

        private static string Key(GameFiles files, GameSerializedFile from, GamePPtr pptr) =>
            files.Resolve(from, pptr, out var file, out var info) ? file.Name + ":" + info.PathId : null;

        public Mesh Mesh(GameFiles files, GameSerializedFile from, GamePPtr pptr)
        {
            var key = Key(files, from, pptr);
            if (key == null)
                return null;
            if (!_meshes.TryGetValue(key, out var mesh))
            {
                mesh = TarkovGameAssets.BuildMesh(files, from, pptr, out _);
                if (mesh != null)
                {
                    mesh.name = TarkovGameAssets.PreviewPrefix + mesh.name;
                    mesh.hideFlags = HideFlags.HideAndDontSave;
                    mesh.UploadMeshData(true);
                }
                _meshes[key] = mesh;
            }
            return mesh;
        }

        public Texture Texture(GameFiles files, GameSerializedFile from, GamePPtr pptr)
        {
            if (!files.Resolve(from, pptr, out var file, out var info))
                return null;
            if (info.ClassId == 89)
                return TarkovReflectionCube.Get();
            var key = file.Name + ":" + info.PathId;
            if (!_textures.TryGetValue(key, out var texture))
            {
                var t = TarkovGameAssets.BuildTexture(files, from, pptr, TextureSize, false, out _);
                if (t != null)
                {
                    t.name = TarkovGameAssets.PreviewPrefix + t.name;
                    t.hideFlags = HideFlags.HideAndDontSave;
                }
                _textures[key] = texture = t;
            }
            return texture;
        }

        public Material Material(GameFiles files, GameSerializedFile from, GamePPtr pptr, out string shaderName)
        {
            shaderName = null;
            var key = Key(files, from, pptr);
            if (key == null)
                return null;
            if (_materials.TryGetValue(key, out var material))
            {
                shaderName = material != null ? material.GetTag("SDKGameShader", false, "") : null;
                return material;
            }
            material = TarkovGameAssets.BuildMaterial(files, from, pptr, !StandIns, (f, p) => Texture(files, f, p), n => TarkovGameAssets.StandIn(n, false), out shaderName);
            if (material != null && StandIns)
            {
                if (TarkovGameAssets.StandIn(shaderName, false) == null)
                {
                    // no stand-in: the simple look
                    Object.DestroyImmediate(material);
                    material = TarkovGameAssets.BuildMaterial(files, from, pptr, true, (f, p) => Texture(files, f, p), null, out shaderName);
                }
                else
                {
                    material.name = TarkovGameAssets.PreviewPrefix + material.name;
                    material.hideFlags = HideFlags.HideAndDontSave;
                }
            }
            if (material != null)
                material.SetOverrideTag("SDKGameShader", shaderName ?? "");
            _materials[key] = material;
            return material;
        }

        // the object's look: LOD0 of each LOD group, renderers that are on, active and not shadow-only
        public GameModel Model(GameFiles files, GameSceneTree tree, GameSceneTree.Node root)
        {
            var model = new GameModel();
            var file = tree.File;
            var nodes = tree.Subtree(root).ToList();
            var hidden = new HashSet<long>();
            foreach (var node in nodes)
                foreach (var (classId, pathId) in node.Components)
                    if (classId == 205)
                    {
                        var lods = file.Read(pathId).List("m_LODs").OfType<GameValue>().ToList();
                        foreach (var lod in lods.Skip(1))
                            foreach (var r in lod.List("renderers").OfType<GameValue>())
                                hidden.Add(r.Ref("renderer").PathId);
                    }
            var matrices = new Dictionary<long, Matrix4x4>();
            var active = new Dictionary<long, bool>();
            bool any = false;
            var bounds = new Bounds();
            foreach (var node in nodes)
            {
                var parent = node == root ? null : tree.Get(node.Parent);
                var local = node == root ? Matrix4x4.Scale(TarkovGameAssets.LossyScale(node.World))
                    : Matrix4x4.TRS(TarkovGameAssets.V(node.Position), TarkovGameAssets.Q(node.Rotation), TarkovGameAssets.V(node.Scale));
                var matrix = parent != null ? matrices[parent.Transform] * local : local;
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
                    var mesh = Mesh(files, file, new GamePPtr { FileId = node.MeshFile, PathId = node.Mesh });
                    if (mesh == null)
                        continue;
                    var materials = new List<Material>();
                    bool skip = false;
                    foreach (var m in node.Materials ?? new List<GamePPtr>())
                    {
                        var material = Material(files, file, m, out var shaderName);
                        if (TarkovGameAssets.IsImpostor(shaderName) || shaderName != null && shaderName.StartsWith("Decal/"))
                            skip = true;
                        materials.Add(material);
                    }
                    if (skip || materials.Count == 0)
                        continue;
                    model.Parts.Add(new GameModel.Part { Mesh = mesh, Matrix = matrix, Materials = materials.ToArray() });
                    var b = Transform(mesh.bounds, matrix);
                    if (any)
                        bounds.Encapsulate(b);
                    else
                        bounds = b;
                    any = true;
                }
            }
            model.Bounds = bounds;
            if (!any)
                model.Error = "nothing to show";
            return model;
        }

        private static Bounds Transform(Bounds b, Matrix4x4 m)
        {
            var result = new Bounds(m.MultiplyPoint3x4(b.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
                result.Encapsulate(m.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            return result;
        }
    }
}
