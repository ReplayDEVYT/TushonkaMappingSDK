using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Builds the full weather system (TOD_Sky, WeatherController, clouds, wind, rain, snow, ambient)
    // into a map scene from the data Tools/WeatherExtract wrote from the author's own game
    // (Assets/TarkovSDK/Weather/Extracted). Every value is the retail map's: the stubs carry the
    // game's field names, so values are written through Unity's own serialization.
    //
    // The root, "SDK_Weather", is saved inactive. The weather components cache shaders and
    // materials in Awake, and in a raid Awake would run before the client could swap the editor
    // stand-in shaders for the game's; CustomMapLoader swaps them first, then activates the root.
    public static class TarkovWeatherBuilder
    {
        public const string RootName = "SDK_Weather";
        public const string ExtractedFolder = "Assets/TarkovSDK/Weather/Extracted";
        private const string GeneratedFolder = "Assets/TarkovSDK/Weather/Generated";
        private const string ShaderPrefix = "Tarkov/";
        private const string ComputePrefix = "Tarkov_";

        // ParticleSystemRenderer is added along with its ParticleSystem
        private static readonly HashSet<string> SkippedTypes = new HashSet<string>
        {
            "UnityStandardAssets.ImageEffects.Tonemapping", // lives outside Assembly-CSharp; only tone-maps the reflection capture
        };
        private static readonly HashSet<string> SkippedFields = new HashSet<string>
        {
            "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "m_PrefabInternal",
        };

        public static bool HasExtractedData => File.Exists(Path.Combine(ExtractedFolder, "weather.json"));

        public static GameObject Find(Scene scene) =>
            scene.GetRootGameObjects().FirstOrDefault(g => g.name == RootName);

        // Customs' sun light ("Light" under "Sky Dome"), for a scene where it was deleted
        public static GameObject CreateSunLight(Transform skyDome)
        {
            var go = new GameObject("Light") { layer = skyDome.gameObject.layer };
            Undo.RegisterCreatedObjectUndo(go, "Restore sun light");
            go.transform.SetParent(skyDome, false);
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(0.933f, 0.813f, 0.509f);
            light.intensity = 0.8f;
            light.shadows = LightShadows.Soft;
            light.shadowBias = 0.02f;
            light.shadowNormalBias = 0.1f;
            light.cullingMask = unchecked((int)4292345855u);
            light.bounceIntensity = 0f;
            light.lightmapBakeType = LightmapBakeType.Realtime;
            go.AddComponent<ShadowMaskExtractor>();
            return go;
        }

        public static GameObject Build(Scene scene) => Build(scene, ExtractedFolder, "weather.json", RootName, true);

        // rebuilds an extracted hierarchy (extract_weather.py output: the json and its Textures and
        // Meshes in folder) under rootName, saved inactive; weather adds the OnRenderObjectManager
        // and the camera effects template
        public static GameObject Build(Scene scene, string folder, string json, string rootName, bool weather)
        {
            var data = JObject.Parse(File.ReadAllText(Path.Combine(folder, json)));
            var b = new Builder(data, folder, rootName, weather);
            try
            {
                AssetDatabase.StartAssetEditing();
                b.PrepareTextureImporters();
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            AssetDatabase.Refresh();
            var root = b.Build(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Tarkov SDK] {rootName} built from {data.Value<string>("source")}: {b.Report}");
            return root;
        }

        private sealed class Builder
        {
            private readonly JObject _data;
            private readonly string _folder;
            private readonly string _rootName;
            private readonly bool _weather;
            private readonly Dictionary<long, UnityEngine.Object> _byPid = new Dictionary<long, UnityEngine.Object>();
            private readonly Dictionary<string, Material> _materials = new Dictionary<string, Material>();
            private readonly Dictionary<string, Mesh> _meshes = new Dictionary<string, Mesh>();
            private readonly Dictionary<string, Texture> _textures = new Dictionary<string, Texture>();
            private readonly Dictionary<string, ScriptableObject> _assets = new Dictionary<string, ScriptableObject>();
            private readonly Dictionary<string, Shader> _shaders = new Dictionary<string, Shader>();
            private readonly Dictionary<string, ComputeShader> _computeShaders = new Dictionary<string, ComputeShader>();
            private readonly List<string> _missingFields = new List<string>();
            private readonly List<string> _skipped = new List<string>();
            private int _components, _values;

            public Builder(JObject data, string folder, string rootName, bool weather)
            {
                _data = data;
                _folder = folder;
                _rootName = rootName;
                _weather = weather;
            }

            public string Report =>
                $"{_byPid.Count} objects/components, {_components} components filled with {_values} values, " +
                $"{_materials.Count} materials, {_meshes.Count} meshes, {_textures.Count} textures, {_assets.Count} assets" +
                (_skipped.Count > 0 ? $"; skipped: {string.Join(", ", _skipped.Distinct())}" : "") +
                (_missingFields.Count > 0 ? $"; {_missingFields.Count} field(s) not in the stubs (e.g. {string.Join(", ", _missingFields.Take(8))})" : "");

            // ------------------------------------------------------------ assets

            public void PrepareTextureImporters()
            {
                foreach (var prop in ((JObject)_data["textures"]).Properties())
                {
                    var file = prop.Value.Value<string>("file");
                    if (file == null)
                        continue;
                    var importer = AssetImporter.GetAtPath($"{_folder}/{file}") as TextureImporter;
                    if (importer == null)
                        continue;
                    bool normal = prop.Value.Value<bool?>("normal") ?? false;
                    var wanted = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                    // a cube import makes the game reject the texture on a 2D property (flares draw as squares)
                    if (importer.textureType != wanted || importer.textureShape != TextureImporterShape.Texture2D)
                    {
                        importer.textureType = wanted;
                        importer.textureShape = TextureImporterShape.Texture2D;
                        importer.SaveAndReimport();
                    }
                }
            }

            private Texture Texture(string key)
            {
                if (key == null)
                    return null;
                if (_textures.TryGetValue(key, out var texture))
                    return texture;
                var file = _data["textures"]?[key]?.Value<string>("file");
                texture = file != null ? AssetDatabase.LoadAssetAtPath<Texture>($"{_folder}/{file}") : null;
                if (texture == null)
                    _skipped.Add($"texture {key}");
                return _textures[key] = texture;
            }

            private Mesh Mesh(string key)
            {
                if (_meshes.TryGetValue(key, out var mesh))
                    return mesh;
                var info = _data["meshes"][key];
                var json = JObject.Parse(File.ReadAllText($"{_folder}/{info.Value<string>("file")}"));
                mesh = new Mesh { name = info.Value<string>("name") };
                var vertices = json["vertices"].Select(v => new Vector3(v.Value<float>(0), v.Value<float>(1), v.Value<float>(2))).ToList();
                if (vertices.Count > 65535)
                    mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(vertices);
                var normals = json["normals"].Select(v => new Vector3(v.Value<float>(0), v.Value<float>(1), v.Value<float>(2))).ToList();
                if (normals.Count == vertices.Count)
                    mesh.SetNormals(normals);
                var uv = json["uv"].Select(v => new Vector2(v.Value<float>(0), v.Value<float>(1))).ToList();
                if (uv.Count == vertices.Count)
                    mesh.SetUVs(0, uv);
                var submeshes = (JArray)json["submeshes"];
                mesh.subMeshCount = submeshes.Count;
                for (int i = 0; i < submeshes.Count; i++)
                    mesh.SetTriangles(submeshes[i].Select(t => t.Value<int>()).ToList(), i);
                if (normals.Count != vertices.Count)
                    mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                return _meshes[key] = Persist(mesh, $"{GeneratedFolder}/Meshes/{key}.asset");
            }

            private Material Material(string key)
            {
                if (_materials.TryGetValue(key, out var material))
                    return material;
                var info = (JObject)_data["materials"][key];
                material = new Material(ShaderFor(info.Value<string>("shader"))) { name = info.Value<string>("name") };
                foreach (var t in ((JObject)info["textures"]).Properties())
                {
                    if (!material.HasProperty(t.Name))
                        continue;
                    material.SetTexture(t.Name, Texture(t.Value.Value<string>("texture")));
                    material.SetTextureScale(t.Name, new Vector2(t.Value["scale"].Value<float>(0), t.Value["scale"].Value<float>(1)));
                    material.SetTextureOffset(t.Name, new Vector2(t.Value["offset"].Value<float>(0), t.Value["offset"].Value<float>(1)));
                }
                foreach (var f in ((JObject)info["floats"]).Properties())
                    if (material.HasProperty(f.Name))
                        material.SetFloat(f.Name, f.Value.Value<float>());
                foreach (var f in ((JObject)info["ints"]).Properties())
                    if (material.HasProperty(f.Name))
                        material.SetInt(f.Name, f.Value.Value<int>());
                foreach (var c in ((JObject)info["colors"]).Properties())
                    if (material.HasProperty(c.Name))
                        material.SetColor(c.Name, new Color(c.Value.Value<float>(0), c.Value.Value<float>(1), c.Value.Value<float>(2), c.Value.Value<float>(3)));
                foreach (var keyword in info["keywords"] ?? new JArray())
                    material.EnableKeyword(keyword.Value<string>());
                material.renderQueue = info.Value<int?>("renderQueue") ?? -1;
                return _materials[key] = Persist(material, $"{GeneratedFolder}/Materials/{Sanitize(material.name)}_{key.GetHashCode() & 0xffff:x4}.mat");
            }

            // the SDK's stand-in when one exists, else a generated placeholder with the same name;
            // either way the client puts the game's shader of that name in its place
            private Shader ShaderFor(string gameName)
            {
                if (string.IsNullOrEmpty(gameName))
                    return Shader.Find("Hidden/InternalErrorShader");
                if (_shaders.TryGetValue(gameName, out var shader))
                    return shader;
                shader = Shader.Find(ShaderPrefix + gameName);
                if (shader == null)
                {
                    var path = $"{GeneratedFolder}/Shaders/{Sanitize(gameName)}.shader";
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path,
                        "// <auto-generated> placeholder for the game shader \"" + gameName + "\"; CustomMapLoader swaps it for the real one in the raid\n" +
                        "Shader \"" + ShaderPrefix + gameName + "\" { SubShader { Pass { ColorMask 0 ZWrite Off } } }\n");
                    AssetDatabase.ImportAsset(path);
                    shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                }
                return _shaders[gameName] = shader;
            }

            // compute shaders cannot be stood in for by name lookup, so the placeholder's asset name
            // carries the game's name after a prefix
            private ComputeShader ComputeShaderFor(string gameName)
            {
                if (_computeShaders.TryGetValue(gameName, out var compute))
                    return compute;
                var path = $"{GeneratedFolder}/Shaders/{ComputePrefix}{Sanitize(gameName)}.compute";
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path,
                        "// <auto-generated> placeholder for the game compute shader \"" + gameName + "\"; CustomMapLoader swaps it in the raid\n" +
                        "#pragma kernel CSMain\n[numthreads(1,1,1)]\nvoid CSMain(uint3 id : SV_DispatchThreadID) {}\n");
                    AssetDatabase.ImportAsset(path);
                }
                return _computeShaders[gameName] = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            }

            // render texture assets carry only their size and format; the game draws into them
            private RenderTexture RenderTextureFor(JObject r)
            {
                var name = r.Value<string>("name") ?? "RenderTexture";
                var path = $"{GeneratedFolder}/Assets/{Sanitize(name)}.renderTexture";
                var existing = AssetDatabase.LoadAssetAtPath<RenderTexture>(path);
                if (existing != null)
                    return existing;
                var format = (UnityEngine.Experimental.Rendering.GraphicsFormat)(r.Value<int?>("format") ?? 0);
                if (format == UnityEngine.Experimental.Rendering.GraphicsFormat.None)
                    format = UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm;
                var texture = new RenderTexture(r.Value<int?>("width") ?? 256, r.Value<int?>("height") ?? 256, 0, format) { name = name };
                return Persist(texture, path);
            }

            private ScriptableObject Asset(string key)
            {
                if (_assets.TryGetValue(key, out var asset))
                    return asset;
                var info = (JObject)_data["assets"][key];
                var type = FindType(info.Value<string>("type"));
                if (type == null || !typeof(ScriptableObject).IsAssignableFrom(type))
                {
                    _skipped.Add(info.Value<string>("type"));
                    return _assets[key] = null;
                }
                var name = info.Value<string>("name") ?? type.Name;
                var path = $"{GeneratedFolder}/Assets/{Sanitize(name)}.asset";
                // the asset already there is filled in place: a new one gets a new GUID, and every scene
                // built with the weather before (other maps, a map's other scenes) loses its reference
                // (in the raid: no cloud layer, so no clouds and an error every frame)
                var existing = AssetDatabase.LoadAssetAtPath(path, type) as ScriptableObject;
                asset = existing != null ? existing : ScriptableObject.CreateInstance(type);
                asset.name = name;
                _assets[key] = asset; // before filling: assets may reference each other
                Fill(asset, (JObject)info["fields"]);
                if (existing != null)
                    EditorUtility.SetDirty(existing); // values written through a SerializedObject stay in memory otherwise
                else
                    CreateAsset(asset, path);
                return asset;
            }

            // fresh's values into the asset already at path (keeping its GUID, so scenes built before keep
            // their references), else fresh saved there; returns the saved one
            private static T Persist<T>(T fresh, string path) where T : UnityEngine.Object
            {
                var existing = AssetDatabase.LoadAssetAtPath<T>(path);
                if (existing == null)
                {
                    CreateAsset(fresh, path);
                    return fresh;
                }
                EditorUtility.CopySerialized(fresh, existing);
                EditorUtility.SetDirty(existing);
                UnityEngine.Object.DestroyImmediate(fresh);
                return existing;
            }

            private static void CreateAsset(UnityEngine.Object asset, string path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) != null)
                    AssetDatabase.DeleteAsset(path);
                AssetDatabase.CreateAsset(asset, path);
            }

            // ------------------------------------------------------------ scene

            public GameObject Build(Scene scene)
            {
                GameObject root = null;
                var objects = (JArray)_data["objects"];
                var goByPid = new Dictionary<long, GameObject>();
                foreach (JObject o in objects)
                {
                    var go = new GameObject(o.Value<string>("name"));
                    SceneManager.MoveGameObjectToScene(go, scene);
                    goByPid[o.Value<long>("pid")] = go;
                    _byPid[o.Value<long>("pid")] = go;
                    _byPid[o.Value<long>("transform")] = go.transform;
                    root ??= go;
                }
                foreach (JObject o in objects)
                {
                    var go = goByPid[o.Value<long>("pid")];
                    var parent = o["parent"]?.Type == JTokenType.Null ? null : o.Value<long?>("parent");
                    if (parent.HasValue && goByPid.TryGetValue(parent.Value, out var p))
                        go.transform.SetParent(p.transform, false);
                    go.transform.localPosition = V3(o["position"]);
                    var r = o["rotation"];
                    go.transform.localRotation = new Quaternion(r.Value<float>(0), r.Value<float>(1), r.Value<float>(2), r.Value<float>(3));
                    go.transform.localScale = V3(o["scale"]);
                    go.layer = o.Value<int>("layer");
                }
                root.name = _rootName;

                // pass 1: every component exists before any reference is resolved
                var filled = new List<(UnityEngine.Object, JObject)>();
                foreach (JObject c in (JArray)_data["components"])
                {
                    var typeName = c.Value<string>("type");
                    if (SkippedTypes.Contains(typeName))
                    {
                        _skipped.Add(typeName);
                        continue;
                    }
                    var type = FindType(typeName);
                    if (type == null || !typeof(Component).IsAssignableFrom(type))
                    {
                        _skipped.Add(typeName);
                        continue;
                    }
                    var go = goByPid[c.Value<long>("go")];
                    var component = go.GetComponent(type) is Component existing && type == typeof(ParticleSystemRenderer)
                        ? existing
                        : go.AddComponent(type);
                    if (component == null)
                    {
                        _skipped.Add(typeName);
                        continue;
                    }
                    _byPid[c.Value<long>("pid")] = component;
                    filled.Add((component, (JObject)c["fields"]));
                }

                // pass 2: values and references
                foreach (var (component, fields) in filled)
                {
                    Fill(component, fields);
                    _components++;
                }

                if (_weather)
                {
                    // feeds the game cameras to AmbientLight (its ambient pass); Customs keeps it outside "Weather"
                    var manager = new GameObject("OnRenderObjectManager");
                    manager.transform.SetParent(root.transform, false);
                    manager.AddComponent<OnRenderObjectManager>();

                    BuildCameraEffects(root.transform);
                }

                foreach (JObject o in objects)
                    goByPid[o.Value<long>("pid")].SetActive(o.Value<bool>("active"));
                root.SetActive(false); // CustomMapLoader activates it once the shaders are swapped
                Undo.RegisterCreatedObjectUndo(root, "Add " + _rootName);
                return root;
            }

            // Fog and rain on the screen are camera components; retail maps get them from their
            // camera prefab, SDK maps from this inactive template, which the client copies onto
            // the player camera when it is created.
            public const string CameraEffectsName = "SDK_CameraEffects";

            private void BuildCameraEffects(Transform root)
            {
                if (!(_data["camera"] is JArray camera) || camera.Count == 0)
                {
                    _skipped.Add("camera effects (re-extract the weather data)");
                    return;
                }
                var template = new GameObject(CameraEffectsName);
                template.transform.SetParent(root, false);
                template.SetActive(false);
                foreach (JObject c in camera)
                {
                    var type = FindType(c.Value<string>("type"));
                    if (type == null)
                    {
                        _skipped.Add(c.Value<string>("type"));
                        continue;
                    }
                    var component = template.AddComponent(type);
                    Fill(component, (JObject)c["fields"]);
                    _components++;
                }
            }

            // ------------------------------------------------------------ values

            private void Fill(UnityEngine.Object target, JObject fields)
            {
                var so = new SerializedObject(target);
                foreach (var field in fields.Properties())
                {
                    if (SkippedFields.Contains(field.Name))
                        continue;
                    var prop = so.FindProperty(field.Name);
                    if (prop == null)
                    {
                        _missingFields.Add($"{target.GetType().Name}.{field.Name}");
                        continue;
                    }
                    try
                    {
                        Apply(prop, field.Value);
                    }
                    catch (Exception e)
                    {
                        _missingFields.Add($"{target.GetType().Name}.{field.Name} ({e.GetType().Name})");
                    }
                }
                so.ApplyModifiedPropertiesWithoutUndo();
                if (EditorUtility.IsPersistent(target))
                    EditorUtility.SetDirty(target);
            }

            private void Apply(SerializedProperty prop, JToken value)
            {
                if (value == null)
                    return;
                if (value.Type == JTokenType.Null)
                {
                    if (prop.propertyType == SerializedPropertyType.ObjectReference)
                        prop.objectReferenceValue = null;
                    return;
                }
                if (value is JObject obj && obj["ref"] != null)
                {
                    if (prop.propertyType == SerializedPropertyType.ObjectReference)
                        prop.objectReferenceValue = Resolve(obj, prop);
                    return;
                }
                _values++;

                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer:
                    case SerializedPropertyType.Enum:
                        prop.longValue = value.Value<long>();
                        return;
                    case SerializedPropertyType.LayerMask:
                        // stored unsigned: "everything" masks exceed int.MaxValue
                        prop.intValue = unchecked((int)(value is JObject mask ? mask.Value<long>("m_Bits") : value.Value<long>()));
                        return;
                    case SerializedPropertyType.Boolean:
                        prop.boolValue = value.Type == JTokenType.Boolean ? value.Value<bool>() : value.Value<long>() != 0;
                        return;
                    case SerializedPropertyType.Float:
                        prop.doubleValue = value.Value<double>();
                        return;
                    case SerializedPropertyType.String:
                        prop.stringValue = value.Value<string>();
                        return;
                    case SerializedPropertyType.Color:
                        prop.colorValue = ColorOf((JObject)value);
                        return;
                    case SerializedPropertyType.Vector2:
                        prop.vector2Value = new Vector2(F(value, "x"), F(value, "y"));
                        return;
                    case SerializedPropertyType.Vector3:
                        prop.vector3Value = new Vector3(F(value, "x"), F(value, "y"), F(value, "z"));
                        return;
                    case SerializedPropertyType.Vector4:
                        prop.vector4Value = new Vector4(F(value, "x"), F(value, "y"), F(value, "z"), F(value, "w"));
                        return;
                    case SerializedPropertyType.Quaternion:
                        prop.quaternionValue = new Quaternion(F(value, "x"), F(value, "y"), F(value, "z"), F(value, "w"));
                        return;
                    case SerializedPropertyType.Rect:
                        prop.rectValue = new Rect(F(value, "x"), F(value, "y"), F(value, "width"), F(value, "height"));
                        return;
                    case SerializedPropertyType.Bounds:
                        prop.boundsValue = new Bounds(V3o(value["m_Center"]), V3o(value["m_Extent"]) * 2f);
                        return;
                    case SerializedPropertyType.AnimationCurve:
                        prop.animationCurveValue = CurveOf((JObject)value);
                        return;
                    case SerializedPropertyType.Gradient:
                        prop.gradientValue = GradientOf((JObject)value);
                        return;
                }

                if (prop.isArray && value is JArray array)
                {
                    prop.arraySize = array.Count;
                    for (int i = 0; i < array.Count; i++)
                    {
                        var element = prop.GetArrayElementAtIndex(i);
                        // maps arrive as [key, value] pairs
                        if (array[i] is JArray pair && pair.Count == 2 && element.FindPropertyRelative("first") != null)
                        {
                            Apply(element.FindPropertyRelative("first"), pair[0]);
                            Apply(element.FindPropertyRelative("second"), pair[1]);
                        }
                        else
                        {
                            Apply(element, array[i]);
                        }
                    }
                    return;
                }

                if (value is JObject children)
                    foreach (var child in children.Properties())
                    {
                        var sub = prop.FindPropertyRelative(child.Name);
                        if (sub != null)
                            Apply(sub, child.Value);
                    }
            }

            private UnityEngine.Object Resolve(JObject r, SerializedProperty prop)
            {
                switch (r.Value<string>("ref"))
                {
                    case "self":
                        _byPid.TryGetValue(r.Value<long>("pid"), out var found);
                        // a field typed GameObject that points at a component, or the reverse
                        if (found is Component c && prop.type == "PPtr<GameObject>")
                            return c.gameObject;
                        return found;
                    case "material": return Material(r.Value<string>("key"));
                    case "texture": return Texture(r.Value<string>("key"));
                    case "mesh": return Mesh(r.Value<string>("key"));
                    case "shader": return ShaderFor(r.Value<string>("name"));
                    case "computeshader": return ComputeShaderFor(r.Value<string>("name"));
                    case "asset": return Asset(r.Value<string>("key"));
                    case "rendertexture": return RenderTextureFor(r);
                    default: return null;
                }
            }

            // ------------------------------------------------------------ conversions

            private static AnimationCurve CurveOf(JObject curve)
            {
                var keys = ((JArray)curve["m_Curve"]).Select(k => new Keyframe(F(k, "time"), F(k, "value"), F(k, "inSlope"), F(k, "outSlope"), F(k, "inWeight"), F(k, "outWeight"))
                {
                    weightedMode = (WeightedMode)(k.Value<int?>("weightedMode") ?? 0),
                }).ToArray();
                return new AnimationCurve(keys)
                {
                    preWrapMode = WrapOf(curve.Value<int?>("m_PreInfinity") ?? 2),
                    postWrapMode = WrapOf(curve.Value<int?>("m_PostInfinity") ?? 2),
                };
            }

            // serialized as Unity's internal wrap mode: 0 ping-pong, 1 repeat, 2 clamp
            private static WrapMode WrapOf(int value) =>
                value == 0 ? WrapMode.PingPong : value == 1 ? WrapMode.Loop : WrapMode.ClampForever;

            private static Gradient GradientOf(JObject g)
            {
                int colorCount = g.Value<int?>("m_NumColorKeys") ?? 2;
                int alphaCount = g.Value<int?>("m_NumAlphaKeys") ?? 2;
                var colors = new GradientColorKey[colorCount];
                for (int i = 0; i < colorCount; i++)
                    colors[i] = new GradientColorKey(ColorOf((JObject)g[$"key{i}"]), (g.Value<int?>($"ctime{i}") ?? 0) / 65535f);
                var alphas = new GradientAlphaKey[alphaCount];
                for (int i = 0; i < alphaCount; i++)
                    alphas[i] = new GradientAlphaKey(F(g[$"key{i}"], "a"), (g.Value<int?>($"atime{i}") ?? 0) / 65535f);
                var gradient = new Gradient { mode = (GradientMode)(g.Value<int?>("m_Mode") ?? 0) };
                gradient.SetKeys(colors, alphas);
                return gradient;
            }

            private static Color ColorOf(JObject c)
            {
                if (c["rgba"] != null)
                {
                    uint rgba = c.Value<uint>("rgba");
                    return new Color32((byte)(rgba & 0xff), (byte)((rgba >> 8) & 0xff), (byte)((rgba >> 16) & 0xff), (byte)(rgba >> 24));
                }
                return new Color(F(c, "r"), F(c, "g"), F(c, "b"), c["a"] != null ? F(c, "a") : 1f);
            }

            private static float F(JToken t, string name) => t?[name]?.Value<float>() ?? 0f;
            private static Vector3 V3(JToken a) => new Vector3(a.Value<float>(0), a.Value<float>(1), a.Value<float>(2));
            private static Vector3 V3o(JToken o) => new Vector3(F(o, "x"), F(o, "y"), F(o, "z"));

            private static Type FindType(string name)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var type = assembly.GetType(name, false) ?? assembly.GetType("UnityEngine." + name, false);
                    if (type != null)
                        return type;
                }
                return null;
            }

            private static string Sanitize(string name)
            {
                foreach (var c in Path.GetInvalidFileNameChars())
                    name = name.Replace(c, '_');
                return name.Replace('/', '_');
            }
        }
    }
}
