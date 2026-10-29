using System.Collections.Generic;
using System.IO;
using System.Linq;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace TarkovSDK.Editor
{
    // Scene view drawing for what the editor can't otherwise show, through Graphics.DrawMesh on Scene view cameras only
    // (nothing is added to the scene, saved or built). The meshes, materials and loaded bundles outlive a script reload,
    // which empties the statics tracking them, so they are named and freed by name on load and before a reload.

    // The mounted guns' model: the Utes world prefab read from the game's own bundles in the game install (its LOD0
    // renderers, on the SDK's p0 stand-in with the game's textures), drawn where the client plugin places it in the raid
    [InitializeOnLoad]
    public static class TarkovGameModelPreview
    {
        private const string EnabledKey = "TarkovSDK.GunPreview";
        private const string Prefix = "SDK game model preview ";
        private const string WorldPrefab = "weapon_ckib_nsv_utes_127x108_world";
        // the world prefab's bundle with what its meshes, materials and textures are in (Windows.json); its other
        // dependencies (sounds, animations, effects, the game's shaders) aren't needed to draw it
        private static readonly string[] UtesBundles =
        {
            "assets/content/weapons/utes/textures/client_assets.bundle",
            "assets/content/weapons/utes/client_assets.bundle",
            "assets/content/weapons/utes/weapon_ckib_nsv_utes_127x108_container.bundle",
        };

        private struct Part
        {
            public Mesh Mesh;
            public Matrix4x4 Local;
            public Material[] Materials;
        }

        private static List<Part> _utes;
        private static bool _loading;
        private static List<TarkovMountedGun> _guns;

        // why the model isn't shown, when it isn't
        public static string UtesError { get; private set; }

        // also the switch for the other game models (TarkovGameScenePreviews)
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledKey, true);
            set
            {
                if (value == Enabled)
                    return;
                EditorPrefs.SetBool(EnabledKey, value);
                SceneView.RepaintAll();
            }
        }

        static TarkovGameModelPreview()
        {
            Camera.onPreCull += Draw;
            EditorApplication.hierarchyChanged += () => _guns = null;
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
            Cleanup();
        }

        private static void Cleanup()
        {
            foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles().ToList())
                if (UtesBundles.Contains(bundle.name))
                    bundle.Unload(true);
            foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
                if (material.name.StartsWith(Prefix) && !EditorUtility.IsPersistent(material))
                    Object.DestroyImmediate(material);
            _utes = null;
            _loading = false;
        }

        private static string GameData() => TarkovGameData.Folder;

        private static void Load()
        {
            _loading = false;
            _utes = new List<Part>();
            string folder = Path.Combine(GameData(), "StreamingAssets/Windows");
            var missing = UtesBundles.FirstOrDefault(b => !File.Exists(Path.Combine(folder, b)));
            if (missing != null)
            {
                UtesError = $"not found in the game install ({Path.Combine(folder, missing)})";
                return;
            }
            Cleanup();
            _utes = new List<Part>();
            // the prefab's scripts the SDK has no stub for would each warn as missing
            bool log = Debug.unityLogger.logEnabled;
            Debug.unityLogger.logEnabled = false;
            try
            {
                var bundles = UtesBundles.Select(b => AssetBundle.LoadFromFile(Path.Combine(folder, b))).ToList();
                if (bundles.Any(b => b == null))
                {
                    UtesError = "the game's bundles didn't load (is the editor's build target Windows?)";
                    return;
                }
                var root = bundles[2].LoadAllAssets<GameObject>()
                    .Where(g => g.transform.parent == null && g.name == WorldPrefab)
                    .OrderByDescending(g => g.GetComponent<StationaryWeapon>() != null).FirstOrDefault();
                if (root == null)
                {
                    UtesError = "the world prefab isn't in its bundle";
                    return;
                }
                var shader = Shader.Find("Tarkov/p0/Bumped Specular") ?? Shader.Find("Standard");
                var copies = new Dictionary<Material, Material>();
                Material Copy(Material source)
                {
                    if (source == null)
                        return TarkovSceneTools.DefaultSurfaceMaterial();
                    if (!copies.TryGetValue(source, out var copy))
                    {
                        // the game's shaders aren't loaded; a shader change keeps the properties both have
                        copy = new Material(source) { name = Prefix + source.name, hideFlags = HideFlags.HideAndDontSave, shader = shader };
                        copies[source] = copy;
                    }
                    return copy;
                }
                var lods = root.GetComponent<LODGroup>();
                var renderers = lods != null && lods.lodCount > 0 ? lods.GetLODs()[0].renderers.Where(r => r != null)
                    : root.GetComponentsInChildren<MeshRenderer>(true).Where(r => !r.name.Contains("SHADOW") && !r.name.Contains("LOD1") && !r.name.Contains("LOD2"));
                foreach (var renderer in renderers)
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    var mesh = filter != null ? filter.sharedMesh : null;
                    if (mesh == null || !renderer.enabled || renderer.name.Contains("SHADOW"))
                        continue;
                    _utes.Add(new Part
                    {
                        Mesh = mesh,
                        Local = root.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix,
                        Materials = renderer.sharedMaterials.Select(Copy).ToArray(),
                    });
                }
                UtesError = _utes.Count == 0 ? "the prefab's meshes didn't load" : null;
            }
            finally
            {
                Debug.unityLogger.logEnabled = log;
            }
            SceneView.RepaintAll();
        }

        private static void Draw(Camera camera)
        {
            if (camera.cameraType != CameraType.SceneView || !Enabled)
                return;
            if (_guns == null)
                _guns = Object.FindObjectsOfType<TarkovMountedGun>().ToList();
            if (_guns.Count == 0)
                return;
            if (_utes == null)
            {
                // not inside a camera's render
                if (!_loading)
                {
                    _loading = true;
                    EditorApplication.delayCall += Load;
                }
                return;
            }
            foreach (var gun in _guns)
            {
                if (gun == null || !gun.isActiveAndEnabled)
                    continue;
                var t = gun.transform;
                var matrix = Matrix4x4.TRS(t.position + Vector3.up * TarkovMountedGuns.UtesRootHeight, Quaternion.Euler(0f, t.eulerAngles.y, 0f), Vector3.one);
                foreach (var part in _utes)
                    if (part.Mesh != null)
                        for (int i = 0; i < part.Mesh.subMeshCount; i++)
                            Graphics.DrawMesh(part.Mesh, matrix * part.Local, part.Materials[Mathf.Min(i, part.Materials.Length - 1)], 0, camera, i);
            }
        }
    }

    // The game's own models over the SDK's markers in Scene views (TarkovGameModelPreview.Enabled switches them with the
    // gun's): loot spawns show their first item (its bundle from SPT's items.json Prefab path), airdrop points the
    // airdrop crate (scontainer_crate.bundle's default view), BTR stops and the BTR Path Editor's playing BTR the BTR
    // (Resources btrtemplates/client/btrview/btrviewtemplate in resources.assets, read by TarkovGameFiles). Birds keep
    // the SDK's own: the game ships no bird model. Bundles are loaded with the bundles their meshes and textures are in
    // (Windows.json), not the game's shaders, so their materials go onto the p0 stand-in, as the gun's do.
    [InitializeOnLoad]
    public static class TarkovGameScenePreviews
    {
        private const string CrateBundle = "assets/content/location_objects/lootable/prefab/scontainer_crate.bundle";
        private const string BtrResource = "btrtemplates/client/btrview/btrviewtemplate";

        private static readonly Dictionary<string, GameModel> Models = new Dictionary<string, GameModel>();
        private static readonly HashSet<string> Loading = new HashSet<string>();
        private static GamePreviewCache _cache;
        private static Newtonsoft.Json.Linq.JObject _bundles;
        private static Dictionary<string, string> _itemPrefabs;
        private static List<TarkovLootSpawn> _loot;
        private static List<TarkovSpawnLoot> _spawnLoot;
        private static List<EFT.Airdrop.AirdropPoint> _airdrops;
        private static List<EFT.Vehicle.PathDestination> _stops;

        // the BTR Path Editor's playing BTR, set each time it draws
        private static (Vector3 Position, Quaternion Rotation, double Time) _btr;

        public static void ShowBtr(Vector3 position, Quaternion rotation) => _btr = (position, rotation, EditorApplication.timeSinceStartup);

        static TarkovGameScenePreviews()
        {
            Camera.onPreCull += Draw;
            EditorApplication.hierarchyChanged += () => _loot = null;
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
            Cleanup();
        }

        private static void Cleanup()
        {
            _cache?.Clear();
            _cache = null;
            // bundles stay loaded (unloading them would destroy the meshes); a reload finds them by name
            GamePreviewCache.FreeAll();
            Models.Clear();
            Loading.Clear();
            _bundles = null;
            _itemPrefabs = null;
        }

        private static string BundleFolder => Path.Combine(TarkovGameData.Folder, "StreamingAssets/Windows");

        // a model, made outside the camera's render the first time it's asked for
        private static GameModel Model(string key, System.Func<GameModel> build)
        {
            if (Models.TryGetValue(key, out var model))
                return model;
            if (Loading.Add(key))
                EditorApplication.delayCall += () =>
                {
                    GameModel made;
                    bool log = Debug.unityLogger.logEnabled;
                    // the bundles' scripts the SDK has no stub for would each warn as missing
                    Debug.unityLogger.logEnabled = false;
                    try
                    {
                        made = build() ?? new GameModel { Error = "not found" };
                    }
                    catch (System.Exception e)
                    {
                        made = new GameModel { Error = e.Message };
                    }
                    finally
                    {
                        Debug.unityLogger.logEnabled = log;
                    }
                    Models[key] = made;
                    Loading.Remove(key);
                    SceneView.RepaintAll();
                };
            return null;
        }

        // a bundle and the bundles its meshes and textures are in (not the game's shaders, sounds, animations or effects)
        private static AssetBundle LoadBundle(string bundle)
        {
            if (_bundles == null)
            {
                var json = Path.Combine(BundleFolder, "Windows.json");
                _bundles = File.Exists(json) ? Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(json)) : new Newtonsoft.Json.Linq.JObject();
            }
            var wanted = new List<string>();
            void Add(string name)
            {
                if (wanted.Contains(name) || name == "shaders" || name == "cubemaps" || name.Contains("/audio/") || name.Contains("animations") || name.Contains("/systems/"))
                    return;
                wanted.Add(name);
                foreach (var dep in _bundles[name]?["Dependencies"] ?? new Newtonsoft.Json.Linq.JArray())
                    Add((string)dep);
            }
            Add(bundle);
            var loaded = AssetBundle.GetAllLoadedAssetBundles().ToDictionary(b => b.name, b => b);
            AssetBundle main = null;
            foreach (var name in wanted.AsEnumerable().Reverse())
            {
                if (!loaded.TryGetValue(name, out var b))
                {
                    var path = Path.Combine(BundleFolder, name);
                    b = File.Exists(path) ? AssetBundle.LoadFromFile(path) : null;
                }
                if (name == bundle)
                    main = b;
            }
            return main;
        }

        // a bundle prefab's look: LOD0, on the p0 stand-in
        private static GameModel FromPrefab(GameObject root)
        {
            if (root == null)
                return null;
            var model = new GameModel();
            var shader = Shader.Find("Tarkov/p0/Bumped Specular") ?? Shader.Find("Standard");
            var hidden = new HashSet<Renderer>();
            foreach (var group in root.GetComponentsInChildren<LODGroup>(true))
                foreach (var lod in group.GetLODs().Skip(1))
                    foreach (var r in lod.renderers)
                        if (r != null)
                            hidden.Add(r);
            var copies = new Dictionary<Material, Material>();
            bool any = false;
            var bounds = new Bounds();
            foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var filter = renderer.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null || hidden.Contains(renderer) || !renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                    renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly || renderer.name.Contains("SHADOW"))
                    continue;
                var materials = renderer.sharedMaterials.Select(m =>
                {
                    if (m == null)
                        return TarkovSceneTools.DefaultSurfaceMaterial();
                    if (!copies.TryGetValue(m, out var copy))
                        copies[m] = copy = new Material(m) { name = TarkovGameAssets.PreviewPrefix + m.name, hideFlags = HideFlags.HideAndDontSave, shader = shader };
                    return copy;
                }).ToArray();
                var matrix = root.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                model.Parts.Add(new GameModel.Part { Mesh = mesh, Matrix = matrix, Materials = materials });
                var b = new Bounds(matrix.MultiplyPoint3x4(mesh.bounds.center), Vector3.zero);
                if (any)
                    bounds.Encapsulate(b);
                else
                    bounds = b;
                any = true;
            }
            model.Bounds = bounds;
            return model;
        }

        private static GameModel Crate() => Model("crate", () =>
        {
            var bundle = LoadBundle(CrateBundle);
            var view = bundle != null ? bundle.LoadAllAssets<GameObject>().FirstOrDefault(g => g.name == "default_airdrop_view") : null;
            return FromPrefab(view);
        });

        private static GameModel Btr() => Model("btr", () =>
        {
            var files = TarkovGameData.Files;
            if (!files.ResourcePaths(out var ggm).TryGetValue(BtrResource, out var pptr) || !files.Resolve(ggm, pptr, out var file, out var info))
                return null;
            var tree = GameSceneTree.ReadFrom(files, file, info.PathId);
            if (tree == null)
                return null;
            if (_cache == null)
                _cache = new GamePreviewCache(1024, true);
            var model = _cache.Model(files, tree, tree.Roots[0]);
            // parts the template keeps out of sight far below
            model.Parts.RemoveAll(p => p.Matrix.GetColumn(3).y < -50f);
            return model;
        });

        private static GameModel Item(string template)
        {
            if (string.IsNullOrEmpty(template))
                return null;
            // the item database is read once, outside the camera's render
            if (_itemPrefabs == null)
            {
                if (Loading.Add("items"))
                    EditorApplication.delayCall += () =>
                    {
                        var prefabs = new Dictionary<string, string>();
                        var root = TarkovLootSpawnEditor.ItemDatabase.SptRoot() ?? "C:/spt41";
                        var path = Path.Combine(root, "SPT_Runtime", "SPT_Data", "database", "templates", "items.json");
                        try
                        {
                            if (File.Exists(path))
                                foreach (var p in Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path)).Properties())
                                {
                                    var prefab = (string)p.Value["_props"]?["Prefab"]?["path"];
                                    if (!string.IsNullOrEmpty(prefab))
                                        prefabs[p.Name] = prefab;
                                }
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogWarning("[Tarkov SDK] Loot previews: the item database didn't read: " + e.Message);
                        }
                        _itemPrefabs = prefabs;
                        Loading.Remove("items");
                        SceneView.RepaintAll();
                    };
                return null;
            }
            if (!_itemPrefabs.TryGetValue(template, out var bundle))
                return null;
            return Model("bundle:" + bundle, () =>
            {
                var loaded = LoadBundle(bundle);
                if (loaded == null)
                    return null;
                var roots = loaded.LoadAllAssets<GameObject>().Where(g => g.transform.parent == null).ToList();
                return FromPrefab(roots.OrderByDescending(g => g.GetComponentsInChildren<MeshRenderer>(true).Length).FirstOrDefault());
            });
        }

        private static void Draw(Camera camera)
        {
            if (camera.cameraType != CameraType.SceneView || !TarkovGameModelPreview.Enabled || !TarkovGameData.Available)
                return;
            if (_loot == null)
            {
                _loot = Object.FindObjectsOfType<TarkovLootSpawn>().ToList();
                _spawnLoot = Object.FindObjectsOfType<TarkovSpawnLoot>().ToList();
                _airdrops = Object.FindObjectsOfType<EFT.Airdrop.AirdropPoint>().ToList();
                _stops = Object.FindObjectsOfType<EFT.Vehicle.PathDestination>().ToList();
            }
            foreach (var spawn in _loot)
                if (spawn != null && spawn.isActiveAndEnabled)
                    DrawItem(spawn.Items.FirstOrDefault(i => !string.IsNullOrEmpty(i.Template))?.Template, spawn.transform, camera);
            foreach (var spawn in _spawnLoot)
            {
                if (spawn == null || !spawn.isActiveAndEnabled)
                    continue;
                var items = spawn.Items.Where(i => !string.IsNullOrEmpty(i.Template)).ToList();
                if (items.Count == 0)
                    continue;
                if (spawn.AtThisObject || spawn.transform.childCount == 0)
                    DrawItem(items[0].Template, spawn.transform, camera);
                else
                    for (int i = 0; i < spawn.transform.childCount; i++)
                        DrawItem(items[spawn.Pick == TarkovSpawnLoot.PickMode.All ? i % items.Count : 0].Template, spawn.transform.GetChild(i), camera);
            }
            if (_airdrops.Count > 0 && Crate() is GameModel crate)
                foreach (var point in _airdrops)
                    if (point != null && point.isActiveAndEnabled)
                        crate.Draw(Matrix4x4.TRS(point.transform.position, Quaternion.Euler(0f, point.transform.eulerAngles.y, 0f), Vector3.one), camera);
            bool playing = EditorApplication.timeSinceStartup - _btr.Time < 1.0;
            if ((_stops.Count > 0 || playing) && Btr() is GameModel btr)
            {
                if (playing)
                    btr.Draw(Matrix4x4.TRS(_btr.Position, _btr.Rotation, Vector3.one), camera);
                else
                    foreach (var stop in _stops)
                        if (stop != null && stop.isActiveAndEnabled)
                            btr.Draw(Matrix4x4.TRS(stop.transform.position, stop.transform.rotation, Vector3.one), camera);
            }
        }

        private static void DrawItem(string template, Transform at, Camera camera)
        {
            var model = Item(template);
            if (model != null)
                model.Draw(Matrix4x4.TRS(at.position, at.rotation, Vector3.one), camera);
        }
    }

    // The game's volumetric light in Scene views: a ray march through each light's reach with the component's
    // scattering, extinction and Mie G, as the game's VolumetricLightRenderer does (its shadow-map test left out)
    [InitializeOnLoad]
    public static class TarkovVolumetricPreview
    {
        private const string EnabledKey = "TarkovSDK.VolumetricPreview";
        private const string ShaderName = "Hidden/Tarkov SDK/Volumetric Preview";
        private const string MeshName = "SDK volumetric preview box";
        private static Material _material;
        private static Mesh _box;
        private static List<VolumetricLight> _lights;
        private static MaterialPropertyBlock _block;

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledKey, true);
            set
            {
                if (value == Enabled)
                    return;
                EditorPrefs.SetBool(EnabledKey, value);
                SceneView.RepaintAll();
            }
        }

        static TarkovVolumetricPreview()
        {
            Camera.onPreCull += Draw;
            EditorApplication.hierarchyChanged += () => _lights = null;
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
            Cleanup();
        }

        private static void Cleanup()
        {
            foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
                if (material.shader != null && material.shader.name == ShaderName && !EditorUtility.IsPersistent(material))
                    Object.DestroyImmediate(material);
            foreach (var mesh in Resources.FindObjectsOfTypeAll<Mesh>())
                if (mesh.name == MeshName && !EditorUtility.IsPersistent(mesh))
                    Object.DestroyImmediate(mesh);
            _material = null;
            _box = null;
        }

        private static void Draw(Camera camera)
        {
            if (camera.cameraType != CameraType.SceneView || !Enabled)
                return;
            if (_lights == null)
                _lights = Object.FindObjectsOfType<VolumetricLight>().ToList();
            if (_lights.Count == 0)
                return;
            if (_material == null)
            {
                var shader = Shader.Find(ShaderName);
                if (shader == null)
                    return;
                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (_box == null)
                _box = Box();
            if (_block == null)
                _block = new MaterialPropertyBlock();
            // the march stops at the scene's surfaces
            camera.depthTextureMode |= DepthTextureMode.Depth;
            foreach (var volumetric in _lights)
            {
                if (volumetric == null || !volumetric.isActiveAndEnabled)
                    continue;
                var light = volumetric.GetComponent<Light>();
                if (light == null || !light.isActiveAndEnabled || light.intensity < 0.001f || light.type != LightType.Spot && light.type != LightType.Point)
                    continue;
                var t = light.transform;
                _block.Clear();
                _block.SetVector("_LightPos", new Vector4(t.position.x, t.position.y, t.position.z, light.range));
                var forward = t.forward;
                _block.SetVector("_SpotDir", new Vector4(forward.x, forward.y, forward.z,
                    light.type == LightType.Spot ? Mathf.Cos(light.spotAngle * 0.5f * Mathf.Deg2Rad) : -2f));
                _block.SetColor("_LightColor", light.color * light.intensity);
                _block.SetVector("_VolumetricLight", new Vector4(volumetric.ScatteringCoef, volumetric.ExtinctionCoef, Mathf.Clamp(volumetric.MieG, -0.99f, 0.99f),
                    Mathf.Clamp(volumetric.SampleCount, 4, 64)));
                Graphics.DrawMesh(_box, Matrix4x4.TRS(t.position, Quaternion.identity, Vector3.one * light.range * 2f), _material, 0, camera, 0, _block);
            }
        }

        // a unit cube round the origin; the shader draws its far faces and marches the light's sphere inside
        private static Mesh Box()
        {
            var mesh = new Mesh { name = MeshName, hideFlags = HideFlags.HideAndDontSave };
            var v = new List<Vector3>();
            for (int i = 0; i < 8; i++)
                v.Add(new Vector3((i & 1) - 0.5f, ((i >> 1) & 1) - 0.5f, ((i >> 2) & 1) - 0.5f));
            mesh.SetVertices(v);
            mesh.SetTriangles(new[]
            {
                0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4,
                2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5,
            }, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one);
            return mesh;
        }
    }
}
