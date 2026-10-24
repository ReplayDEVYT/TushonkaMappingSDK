using System.Collections.Generic;
using System.IO;
using EFT.Ballistics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Turns an imported model (glTF/GLB, FBX, OBJ; e.g. a Source map exported from Blender) into
    // Tarkov map geometry in one step:
    //
    //  - every material is replaced by one on a Tarkov stand-in shader, saved under
    //    <model folder>/Materials/ and reused on later runs; textures, colours and tiling carry over
    //  - every mesh gets a MeshCollider, the Terrain layer, static flags and a BallisticCollider whose
    //    surface (bullet impacts, footsteps) is guessed from the material name
    //  - Source tool textures (toolsskybox, nodraw, clip ...) become invisible collision on the
    //    LevelBorder layer; decals and overlays render but get no collision; water gets no collision
    //  - nodes scaled up for a Source 3D skybox are tagged EditorOnly, so they stay out of the build
    //  - the navmesh is rebaked
    //
    // Running it again on the same objects is safe: converted materials and existing colliders are kept.
    public static class TarkovModelConverter
    {
        private const string MainShader = "Tarkov/p0/Reflective/Bumped Specular SMap";
        private const string CutoutShader = "Tarkov/p0/Cutout/Bumped Diffuse";
        private const string TransparentShader = "Tarkov/Global Fog/Transparent Reflective Specular";

        // a node scaled this much relative to its parent is a Source 3D skybox, not playable space
        private const float SkyboxScale = 4f;

        private enum Kind { Solid, Cutout, Decal, Water, ToolCollision, ToolHidden }

        private static readonly string[] ToolCollisionNames = { "toolsskybox", "toolsclip", "toolsplayerclip", "toolsinvisible", "toolsblack", "toolsblock" };
        private static readonly string[] ToolHiddenNames = { "toolsnodraw", "toolstrigger", "toolshint", "toolsskip", "toolsareaportal", "toolsoccluder", "toolsfog", "toolsnpcclip" };

        [MenuItem("Tarkov SDK/Tools/Geometry/Convert Selected Model For Tarkov", priority = 240)]
        private static void ConvertSelectedMenu()
        {
            var roots = new List<GameObject>();
            foreach (var obj in Selection.objects)
            {
                if (obj is GameObject go && go.scene.IsValid())
                    roots.Add(go);
                else if (obj is GameObject asset && AssetDatabase.Contains(asset))
                    roots.Add(Place(asset));
            }
            if (roots.Count == 0)
            {
                EditorUtility.DisplayDialog("Tarkov SDK",
                    "Select the imported model: either its object in the scene, or the model file (.glb, .fbx ...) in the Project window to place it in the active scene.", "OK");
                return;
            }
            foreach (var root in roots)
                Convert(root);
        }

        private static GameObject Place(GameObject modelAsset)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset, EditorSceneManager.GetActiveScene());
            Undo.RegisterCreatedObjectUndo(instance, "Place model");
            return instance;
        }

        public static void Convert(GameObject root)
        {
            var scene = root.scene;
            var source = ModelAssetPath(root);
            string folder = Path.GetDirectoryName(!string.IsNullOrEmpty(source) ? source : scene.path)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder))
                folder = "Assets";
            folder += "/Materials";
            Directory.CreateDirectory(folder);

            // an imported model is a prefab instance; unpack it so components and materials can change freely
            if (PrefabUtility.IsPartOfPrefabInstance(root))
                PrefabUtility.UnpackPrefabInstance(PrefabUtility.GetOutermostPrefabInstanceRoot(root),
                                                   PrefabUnpackMode.Completely, InteractionMode.UserAction);
            Undo.RegisterFullObjectHierarchyUndo(root, "Convert model for Tarkov");

            TarkovSceneTools.DefaultSurfaceMaterial(); // creates the shared low-gloss texture
            PlayerSettings.bakeCollisionMeshes = true;

            var converted = new Dictionary<Material, Material>();
            int renderers = 0, colliders = 0, hidden = 0, skyboxNodes = 0;

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t != root.transform && t.localScale.x > SkyboxScale && t.gameObject.CompareTag("Untagged"))
                {
                    t.gameObject.tag = "EditorOnly";
                    t.gameObject.SetActive(false);
                    skyboxNodes++;
                }

            foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer.CompareTag("EditorOnly") || IsUnderEditorOnly(renderer.transform, root.transform))
                    continue;
                var filter = renderer.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;
                renderers++;

                var sourceMaterials = renderer.sharedMaterials;
                var newMaterials = new Material[sourceMaterials.Length];
                var kinds = new Kind[sourceMaterials.Length];
                for (int i = 0; i < sourceMaterials.Length; i++)
                {
                    var src = sourceMaterials[i];
                    kinds[i] = Classify(src);
                    newMaterials[i] = kinds[i] == Kind.ToolCollision || kinds[i] == Kind.ToolHidden
                        ? InvisibleMaterial()
                        : ConvertMaterial(src, kinds[i], folder, converted);
                }
                renderer.sharedMaterials = newMaterials;

                bool allTools = System.Array.TrueForAll(kinds, k => k == Kind.ToolCollision || k == Kind.ToolHidden);
                bool anyDecal = System.Array.Exists(kinds, k => k == Kind.Decal);
                renderer.shadowCastingMode = anyDecal || allTools
                    ? UnityEngine.Rendering.ShadowCastingMode.Off
                    : UnityEngine.Rendering.ShadowCastingMode.On;
                if (allTools)
                {
                    renderer.enabled = false;
                    hidden++;
                }

                // collision: the dominant submesh that should collide decides the surface
                int dominant = DominantCollidingSubmesh(mesh, kinds);
                if (mesh == null || dominant < 0)
                {
                    RemoveCollision(renderer.gameObject);
                    continue;
                }
                var collider = renderer.GetComponent<MeshCollider>() ?? renderer.gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh = mesh;
                colliders++;

                if (kinds[dominant] == Kind.ToolCollision)
                {
                    // invisible world borders: collide with players, never walkable or shootable surfaces
                    renderer.gameObject.layer = LayerMask.NameToLayer("LevelBorder");
                    GameObjectUtility.SetStaticEditorFlags(renderer.gameObject, StaticEditorFlags.BatchingStatic);
                    var ballistic = renderer.GetComponent<BallisticCollider>();
                    if (ballistic != null)
                        Object.DestroyImmediate(ballistic);
                    continue;
                }

                var (type, sound) = Surface(sourceMaterials[dominant] != null ? sourceMaterials[dominant].name : "");
                TarkovSceneTools.MakeStaticGeometry(renderer.gameObject, type, sound);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Tarkov SDK] Converted '{root.name}': {renderers} renderers, {converted.Count} materials (in {folder}), " +
                      $"{colliders} colliders, {hidden} hidden tool meshes, {skyboxNodes} 3D skybox node(s) excluded from the build. Rebaking the navmesh...");
            TarkovSceneTools.BakeNavMesh();
        }

        // ------------------------------------------------------------ materials

        private static Kind Classify(Material material)
        {
            if (material == null)
                return Kind.Solid;
            string name = material.name.ToLowerInvariant().Replace(" ", "");
            foreach (var tool in ToolCollisionNames)
                if (name.Contains(tool))
                    return Kind.ToolCollision;
            foreach (var tool in ToolHiddenNames)
                if (name.Contains(tool))
                    return Kind.ToolHidden;
            if (name.StartsWith("decal") || name.StartsWith("overlay") || name.StartsWith("bills") || name.Contains("graffiti"))
                return Kind.Decal;
            if (name.Contains("water"))
                return Kind.Water;
            if (IsAlphaTested(material))
                return Kind.Cutout;
            return Kind.Solid;
        }

        // glTFast and the Standard shader both mark blended/cut-out materials with a render queue
        // above Geometry; Blender's Source exports keep grates, ladders and fences on alpha blend
        private static bool IsAlphaTested(Material material)
        {
            if (material.renderQueue >= 2450)
                return true;
            string tag = material.GetTag("RenderType", false, "");
            return tag == "Transparent" || tag == "TransparentCutout" || material.IsKeywordEnabled("_ALPHATEST_ON")
                   || material.IsKeywordEnabled("_ALPHABLEND_ON");
        }

        private static Material ConvertMaterial(Material src, Kind kind, string folder, Dictionary<Material, Material> converted)
        {
            if (src == null)
                return TarkovSceneTools.DefaultSurfaceMaterial();
            if (src.shader != null && src.shader.name.StartsWith("Tarkov/"))
                return src; // already converted
            if (converted.TryGetValue(src, out var done))
                return done;

            string path = $"{folder}/{Sanitize(src.name)}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            string shaderName = kind == Kind.Water ? TransparentShader
                : kind == Kind.Cutout || kind == Kind.Decal ? CutoutShader
                : MainShader;
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogWarning($"[Tarkov SDK] Stand-in shader '{shaderName}' not found; '{src.name}' keeps its original material.");
                return src;
            }
            if (material == null)
            {
                material = new Material(shader) { name = src.name };
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = shader;
            }

            var albedo = FindTexture(src, "basecolortexture", "_maintex", "_basemap", "_albedo", "diffuse");
            var normal = FindTexture(src, "normaltexture", "_bumpmap", "_normalmap", "normal");
            var color = FindColor(src, "basecolorfactor", "_color", "_basecolor");

            switch (kind)
            {
                case Kind.Water:
                    material.SetColor("_Color", new Color(0.22f, 0.32f, 0.36f, 0.55f));
                    material.SetFloat("_Smoothness", 0.9f);
                    material.SetFloat("_Metallic", 0.2f);
                    break;

                case Kind.Cutout:
                case Kind.Decal:
                    SetTexture(material, "_MainTex", albedo.Texture, albedo.Scale, albedo.Offset, folder, src.name, color);
                    if (normal.Texture != null)
                        SetTexture(material, "_BumpMap", normal.Texture, albedo.Scale, albedo.Offset);
                    material.SetColor("_Color", Color.white);
                    material.SetFloat("_Cutoff", kind == Kind.Decal ? 0.35f : 0.5f);
                    break;

                default:
                    SetTexture(material, "_MainTex", albedo.Texture, albedo.Scale, albedo.Offset, folder, src.name, color);
                    if (normal.Texture != null)
                        SetTexture(material, "_BumpMap", normal.Texture, albedo.Scale, albedo.Offset);
                    // the game shader takes specular strength from _MainTex alpha, which imported albedo
                    // textures leave at 1 (fully specular); damp it so walls and floors don't bloom
                    material.SetColor("_Color", albedo.Texture != null ? Color.white : color);
                    material.SetColor("_SpecColor", new Color(0.18f, 0.18f, 0.18f, 1f));
                    material.SetColor("_ReflectColor", new Color(0.5f, 0.5f, 0.5f, 0.1f));
                    material.SetTexture("_SpecMap", AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/TarkovSDK/Materials/SDK_Default_Gloss.png"));
                    break;
            }

            EditorUtility.SetDirty(material);
            converted[src] = material;
            return material;
        }

        private static void SetTexture(Material material, string property, Texture texture, Vector2 scale, Vector2 offset,
                                       string folder = null, string sourceName = null, Color? fallbackColor = null)
        {
            if (texture == null && folder != null)
            {
                // untextured source material: a small texture of its colour with low specular in alpha
                var c = fallbackColor ?? Color.grey;
                texture = TarkovSceneTools.SolidTexture($"{folder}/{Sanitize(sourceName)}_Color.png", new Color(c.r, c.g, c.b, 0.12f), 0.02f);
                scale = Vector2.one;
                offset = Vector2.zero;
            }
            material.SetTexture(property, texture);
            material.SetTextureScale(property, scale);
            material.SetTextureOffset(property, offset);
        }

        private static (Texture Texture, Vector2 Scale, Vector2 Offset) FindTexture(Material material, params string[] names)
        {
            foreach (var property in material.GetTexturePropertyNames())
            {
                string lower = property.ToLowerInvariant();
                foreach (var name in names)
                    if (lower == name || (name.Length > 6 && lower.Contains(name)))
                    {
                        var texture = material.GetTexture(property);
                        if (texture != null)
                            return (texture, material.GetTextureScale(property), material.GetTextureOffset(property));
                    }
            }
            return (null, Vector2.one, Vector2.zero);
        }

        private static Color FindColor(Material material, params string[] names)
        {
            foreach (var name in names)
                foreach (var candidate in new[] { name, name.TrimStart('_') })
                    foreach (var property in material.shader != null ? PropertyNames(material.shader) : new List<string>())
                        if (property.ToLowerInvariant() == candidate && material.HasProperty(property))
                            return material.GetColor(property);
            return Color.grey;
        }

        private static List<string> PropertyNames(Shader shader)
        {
            var list = new List<string>();
            for (int i = 0; i < shader.GetPropertyCount(); i++)
                if (shader.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Color)
                    list.Add(shader.GetPropertyName(i));
            return list;
        }

        // tool faces inside a mesh that also has visible faces: cut out completely
        private static Material InvisibleMaterial()
        {
            const string path = "Assets/TarkovSDK/Materials/SDK_Invisible.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null)
                return material;
            material = new Material(Shader.Find(CutoutShader)) { name = "SDK_Invisible" };
            material.SetTexture("_MainTex", TarkovSceneTools.SolidTexture("Assets/TarkovSDK/Materials/SDK_Invisible.png", new Color(0f, 0f, 0f, 0f), 0f));
            material.SetColor("_Color", new Color(1f, 1f, 1f, 0f));
            material.SetFloat("_Cutoff", 0.5f);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        // ------------------------------------------------------------ collision

        private static int DominantCollidingSubmesh(Mesh mesh, Kind[] kinds)
        {
            if (mesh == null)
                return -1;
            int best = -1;
            long bestCount = 0;
            for (int i = 0; i < kinds.Length && i < mesh.subMeshCount; i++)
            {
                if (kinds[i] == Kind.Decal || kinds[i] == Kind.Water || kinds[i] == Kind.ToolHidden)
                    continue;
                long count = mesh.GetIndexCount(i);
                // visible surfaces win over invisible borders in a mixed mesh
                if (kinds[i] != Kind.ToolCollision)
                    count += long.MaxValue / 4;
                if (count > bestCount)
                {
                    bestCount = count;
                    best = i;
                }
            }
            return best;
        }

        private static void RemoveCollision(GameObject go)
        {
            var ballistic = go.GetComponent<BallisticCollider>();
            if (ballistic != null)
                Object.DestroyImmediate(ballistic);
            var collider = go.GetComponent<MeshCollider>();
            if (collider != null)
                Object.DestroyImmediate(collider);
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
        }

        internal static (MaterialType, BaseBallistic.ESurfaceSound) Surface(string materialName)
        {
            string n = materialName.ToLowerInvariant();
            if (n.Contains("grate") || n.Contains("truss") || n.Contains("ladder") || n.Contains("fence") || n.Contains("chainlink"))
                return (MaterialType.Grate, BaseBallistic.ESurfaceSound.MetalThin);
            if (n.Contains("metal") || n.Contains("pipe") || n.Contains("citadel") || n.Contains("steel"))
                return (MaterialType.MetalThick, BaseBallistic.ESurfaceSound.Metal);
            if (n.Contains("glass") || n.Contains("window"))
                return (MaterialType.Glass, BaseBallistic.ESurfaceSound.Glass);
            if (n.Contains("wood") || n.Contains("plank"))
                return (MaterialType.WoodThick, BaseBallistic.ESurfaceSound.WoodThick);
            if (n.Contains("sand") || n.Contains("dirt") || n.Contains("mud"))
                return (MaterialType.Soil, BaseBallistic.ESurfaceSound.Soil);
            if (n.Contains("grass"))
                return (MaterialType.GrassLow, BaseBallistic.ESurfaceSound.Grass);
            if (n.Contains("gravel"))
                return (MaterialType.Gravel, BaseBallistic.ESurfaceSound.Gravel);
            if (n.Contains("tile"))
                return (MaterialType.Tile, BaseBallistic.ESurfaceSound.Tile);
            if (n.Contains("asphalt") || n.Contains("road"))
                return (MaterialType.Asphalt, BaseBallistic.ESurfaceSound.Asphalt);
            if (n.Contains("plastic"))
                return (MaterialType.Plastic, BaseBallistic.ESurfaceSound.Concrete);
            if (n.Contains("brick") || n.Contains("stone") || n.Contains("rock"))
                return (MaterialType.Stone, BaseBallistic.ESurfaceSound.Concrete);
            return (MaterialType.Concrete, BaseBallistic.ESurfaceSound.Concrete);
        }

        // ------------------------------------------------------------ helpers

        private static string ModelAssetPath(GameObject root)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(root);
            return source != null ? AssetDatabase.GetAssetPath(source) : null;
        }

        private static bool IsUnderEditorOnly(Transform t, Transform root)
        {
            for (var p = t; p != null && p != root; p = p.parent)
                if (p.CompareTag("EditorOnly"))
                    return true;
            return false;
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
