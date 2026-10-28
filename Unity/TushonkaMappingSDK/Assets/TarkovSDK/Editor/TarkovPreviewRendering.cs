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

        private static string GameData() => EditorPrefs.GetString("TarkovSDK.GameData", "C:/spt41/EscapeFromTarkov_Data");

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
