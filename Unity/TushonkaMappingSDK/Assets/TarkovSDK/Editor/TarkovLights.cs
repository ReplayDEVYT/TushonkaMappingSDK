using System.IO;
using System.Linq;
using EFT.Ballistics;
using EFT.GameTriggers;
using EFT.Interactive;
using MultiFlare;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Tarkov's own light types, laid out like retail ones (defaults taken from the game's maps):
    //
    //  Lamp / Spot Lamp  a Unity Light with a CullingLightObject (fades it out with distance and drops
    //          its shadows close up) under a LampController, which turns it on and off, flickers it and
    //          breaks it when its glass bulb is shot. Switches and the time of day drive lamps.
    //  Area Light  the game's AreaLight: soft light from a rectangle (strip lights, light panels,
    //          windows), with a CullingAdvancedLightObject. It has no editor preview; its shader is
    //          the game's, filled in by CustomMapLoader in the raid.
    //  Lens Flare  a MultiFlare FlareLight: the glow sprite around a bulb. Flares draw only through the
    //          scene's FlareSceneSettings (Lens Flare Settings), built from the flare atlas in Lights/Flares.
    //  Advanced Light  the game's AdvancedLight: a shadowless point light drawn in one pass. Retail
    //          maps hardly use it; prefer a Lamp.
    //  Controlled Lamp Group  the game's ControlledLampGroup: switches many lights, glowing materials
    //          and lamps at once, with one on and one off sound, and keeps every player's state in step.
    //          Only trigger handlers (HandlerLamp) switch it; a light switch is made from its inspector.
    //
    // Right-clicking a lamp adds the new light to it, so the lamp switches it too.
    public static class TarkovLights
    {
        private const string Menu = "GameObject/Tarkov SDK/Lights/";
        public const string FlaresRootName = "SDK_Flares";
        private const string FlareDataFolder = "Assets/TarkovSDK/Lights/Flares";
        private const string AreaSourceMaterialPath = "Assets/TarkovSDK/Materials/AreaLightSource.mat";
        private static readonly Color Warm = new Color(1f, 0.86f, 0.68f);

        [MenuItem(Menu + "Lamp", priority = 44)]
        private static void MenuLamp(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateLamp(p, LightType.Point).gameObject);

        [MenuItem(Menu + "Spot Lamp", priority = 45)]
        private static void MenuSpotLamp(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateLamp(p, LightType.Spot).gameObject);

        [MenuItem(Menu + "Area Light", priority = 46)]
        private static void MenuAreaLight(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateAreaLight(p, LampOf(cmd)).gameObject);

        [MenuItem(Menu + "Lens Flare", priority = 48)]
        private static void MenuFlare(MenuCommand cmd)
        {
            TarkovSceneTools.Place(cmd, p => CreateFlare(p, LampOf(cmd)).gameObject);
            if (FindFlareSettings() == null && EditorUtility.DisplayDialog("Lens Flare",
                    "Lens flares only show when the map has flare settings (the game's flare atlas). Add them now?", "Add", "Later"))
                EditorApplication.delayCall += AddFlareSettingsMenu;
        }

        [MenuItem(Menu + "Advanced Light", priority = 47)]
        private static void MenuAdvancedLight(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateAdvancedLight(p, LampOf(cmd)).gameObject);

        // right-clicking an object that holds lights makes it the group, as retail's INTERACTIVE_Light
        [MenuItem(Menu + "Controlled Lamp Group", priority = 49)]
        private static void MenuLampGroup(MenuCommand cmd)
        {
            var target = cmd.context as GameObject;
            if (target != null && target.GetComponent<ControlledLampGroup>() == null
                && (target.GetComponentInChildren<Light>(true) != null || target.GetComponentInChildren<BaseLight>(true) != null))
            {
                var group = Undo.AddComponent<ControlledLampGroup>(target);
                FillLampGroup(group, target);
                Selection.activeGameObject = target;
                Debug.Log($"[Tarkov SDK] {target.name} is now a lamp group: {LampGroupSummary(group)}.");
                return;
            }
            TarkovSceneTools.Place(cmd, p => CreateLampGroup(p).gameObject);
        }

        [MenuItem("Tarkov SDK/Generation/Misc/Lens Flare Settings", priority = 113)]
        public static void AddFlareSettingsMenu()
        {
            var scene = SceneManager.GetActiveScene();
            var root = AddFlareSettings(scene);
            if (root != null)
            {
                Selection.activeGameObject = root;
                EditorSceneManager.MarkSceneDirty(scene);
            }
        }

        private static LampController LampOf(MenuCommand cmd) => (cmd.context as GameObject)?.GetComponentInParent<LampController>();

        // ------------------------------------------------------------ lamp

        public static LampController CreateLamp(Transform parent, LightType type)
        {
            var root = new GameObject(type == LightType.Spot ? "SpotLamp" : "Lamp");
            if (parent != null)
                root.transform.SetParent(parent, false);

            var light = CreateLight(root.transform, type);

            // the bulb is what gets shot: glass on the bullets' layer; it casts no shadow over its own light
            var bulb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            bulb.name = "Bulb (replace with your model)";
            bulb.layer = LayerMask.NameToLayer("HighPolyCollider");
            bulb.transform.SetParent(root.transform, false);
            bulb.transform.localScale = Vector3.one * 0.15f;
            var renderer = bulb.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var material = TarkovSceneTools.DefaultSurfaceMaterial();
            if (material != null)
                renderer.sharedMaterial = material;
            var ballistic = bulb.AddComponent<BallisticCollider>();
            ballistic._typeOfMaterial = MaterialType.Glass;
            ballistic.SurfaceSound = BaseBallistic.ESurfaceSound.Glass;

            var lamp = root.AddComponent<LampController>();
            lamp.Lights = new[] { light };
            lamp.LampState = Turnable.EState.On;
            lamp.BallisticCollider = ballistic;
            lamp._animationCurve = new AnimationCurve();
            lamp._materialsWithEmission = new MaterialEmission[0];
            lamp._legacyMaterialsWithEmission = new MaterialColor[0];
            lamp.OnObjects = new GameObject[0];
            lamp.OffObjects = new GameObject[0];
            lamp.DestroyedObjects = new GameObject[0];
            lamp.TurnOnClips = new AudioClip[0];
            // Id is assigned when the map is built
            return lamp;
        }

        // a Unity Light with the CullingLightObject retail lights carry
        public static Light CreateLight(Transform parent, LightType type, string name = "Light")
        {
            var lightObject = new GameObject(name);
            lightObject.transform.SetParent(parent, false);
            if (type == LightType.Spot)
                lightObject.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); // pointing down
            var light = lightObject.AddComponent<Light>();
            light.type = type;
            light.range = 10f;
            light.spotAngle = 80f;
            light.intensity = 1.4f;
            light.color = Warm;
            light.shadows = LightShadows.Soft;
            light.lightmapBakeType = LightmapBakeType.Realtime;

            var culling = lightObject.AddComponent<CullingLightObject>();
            culling._light = light;
            culling._transform = lightObject.transform;
            culling.CullDistance = 50f;
            culling._fadeStartDistance = 40f;
            culling._fadeEndDistance = 50f;
            culling._fadeCurve = new AnimationCurve();
            culling._useLightIntensityFromEditor = true;
            culling._maxLightIntensity = light.intensity;
            culling._cullShadowsByDistance = true;
            culling._shadowsFadeStartDistance = 10f;
            culling._shadowsFadeEndDistance = 16f;
            culling._initialShadowsMode = light.shadows;
            return light;
        }

        // ------------------------------------------------------------ controlled lamp group

        // two plain lights (Light + CullingLightObject, like retail's group) to replace or add to
        public static ControlledLampGroup CreateLampGroup(Transform parent)
        {
            var root = new GameObject("LampGroup");
            if (parent != null)
                root.transform.SetParent(parent, false);
            CreateLight(root.transform, LightType.Point, "Light 1").transform.localPosition = new Vector3(-1.5f, 2.5f, 0f);
            CreateLight(root.transform, LightType.Point, "Light 2").transform.localPosition = new Vector3(1.5f, 2.5f, 0f);
            var group = root.AddComponent<ControlledLampGroup>();
            group._sfxRootPosition = root.transform;
            group._soundRolloff = 20;
            FillLampGroup(group, root);
            return group;
        }

        // everything under root the group can switch (what its "Drag root light object here" field is for)
        public static void FillLampGroup(ControlledLampGroup group, GameObject root)
        {
            Undo.RecordObject(group, "Fill lamp group");
            group._lampControllers = root.GetComponentsInChildren<LampController>(true);
            group._lights = root.GetComponentsInChildren<Light>(true);
            group._cullingLightObjects = root.GetComponentsInChildren<CullingLightObject>(true);
            group._cullingAdvancedLightObjects = root.GetComponentsInChildren<CullingAdvancedLightObject>(true);
            group._baseLights = root.GetComponentsInChildren<BaseLight>(true);
            group._volumetricLights = root.GetComponentsInChildren<VolumetricLight>(true);
            group._multiFlareLights = root.GetComponentsInChildren<FlareLight>(true);
            group._meshRenderers = root.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.sharedMaterials.Any(Glows)).ToArray();
            if (group._sfxRootPosition == null)
                group._sfxRootPosition = group.transform;
            EditorUtility.SetDirty(group);
        }

        // the game dims _EmissionVisibility, or else _EmissionColor; only materials that glow are worth listing
        private static bool Glows(Material m) =>
            m != null && (m.HasProperty("_EmissionVisibility")
                          || m.HasProperty("_EmissionColor") && m.IsKeywordEnabled("_EMISSION") && m.GetColor("_EmissionColor").maxColorComponent > 0.01f);

        public static string LampGroupSummary(ControlledLampGroup g)
        {
            int Count<T>(T[] a) => a?.Count(x => x != null) ?? 0;
            int culled = Count(g._cullingLightObjects) + Count(g._cullingAdvancedLightObjects);
            return $"{Count(g._lights)} lights, {Count(g._baseLights)} area/tube lights, {Count(g._meshRenderers)} glowing renderers, " +
                   $"{Count(g._lampControllers)} lamps, {Count(g._multiFlareLights)} flares, {culled} culling";
        }

        public static bool LampGroupIsEmpty(ControlledLampGroup g) =>
            (g._lights?.All(x => x == null) ?? true) && (g._baseLights?.All(x => x == null) ?? true) && (g._meshRenderers?.All(x => x == null) ?? true)
            && (g._cullingLightObjects?.All(x => x == null) ?? true) && (g._cullingAdvancedLightObjects?.All(x => x == null) ?? true)
            && (g._multiFlareLights?.All(x => x == null) ?? true) && (g._volumetricLights?.All(x => x == null) ?? true);

        // a switch beside the group: pressed (open) turns it on, pressed again (shut) off; the other way
        // round for a group that starts on
        public static Switch CreateLightSwitch(ControlledLampGroup group)
        {
            var sw = TarkovPrefabTools.CreateSwitch(null, null);
            sw.name = "LightSwitch_" + group.name;
            sw.transform.position = group.transform.position + Vector3.up * 1.2f;
            Undo.RegisterCreatedObjectUndo(sw.gameObject, "Create light switch");
            bool startsOn = group._initialLightState;
            AddLampHandler(sw.transform, TarkovTriggerWiringWindow.StateId(sw, EDoorState.Open), group, !startsOn);
            AddLampHandler(sw.transform, TarkovTriggerWiringWindow.StateId(sw, EDoorState.Shut), group, startsOn);
            return sw;
        }

        private static void AddLampHandler(Transform parent, string triggerId, ControlledLampGroup group, bool on)
        {
            var go = new GameObject(on ? "Lights On" : "Lights Off");
            go.transform.SetParent(parent, false);
            var handler = go.AddComponent<HandlerLamp>();
            handler._triggerId = triggerId;
            handler._state = on;
            handler._controlledLampGroups = new[] { group };
            handler._lampControllers = new LampController[0];
            handler._lampControllerIDs = new string[0];
            handler._controlledLampGroupIDs = new ulong[0];
            Undo.RegisterCreatedObjectUndo(go, "Create lamp handler");
        }

        // ------------------------------------------------------------ area light

        public static AreaLight CreateAreaLight(Transform parent, LampController lamp)
        {
            var go = new GameObject("AreaLight");
            if (parent != null)
                go.transform.SetParent(parent, false);
            // it shines along +Z: pointing down, like a ceiling panel
            go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

            var area = go.AddComponent<AreaLight>();
            area.m_Intensity = 0.8f;
            area.length = 1.7f; // the glowing rectangle, length x size
            area.size = 1f;
            area.depth = 2f; // how far it reaches
            area.m_ClipBoxSize = new Vector3(3.1f, 2f, 2f);
            area.m_Angle = 100f;
            area.m_Hardness = 0f;
            area.m_SpecularScale = 1f;
            area.m_Color = Warm;
            area.m_Specular = true;
            area.m_SourceColor = Color.white;
            area.SourceMaterial = AreaSourceMaterial();
            area.m_Quad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            area.m_Cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            // m_ProxyShader stays empty: CustomMapLoader fills in the game's Hidden/AreaLight

            var culling = go.AddComponent<CullingAdvancedLightObject>();
            culling._light = area;
            culling._transform = go.transform;
            culling.CullDistance = 20f;
            culling._fadeStartDistance = 15f;
            culling._fadeEndDistance = 20f;
            culling._fadeCurve = new AnimationCurve();
            culling._maxLightIntensity = 1f;
            culling._isControlledByLampController = lamp != null;

            if (lamp != null)
                AddToLamp(lamp, l => l.AreaAndTubeLights = Append(l.AreaAndTubeLights, area));
            return area;
        }

        // the glowing surface material retail area lights carry (plain Standard)
        private static Material AreaSourceMaterial()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(AreaSourceMaterialPath);
            if (material == null)
            {
                material = new Material(Shader.Find("Standard")) { name = "AreaLightSource" };
                AssetDatabase.CreateAsset(material, AreaSourceMaterialPath);
            }
            return material;
        }

        // ------------------------------------------------------------ lens flare

        public static FlareLight CreateFlare(Transform parent, LampController lamp)
        {
            var go = new GameObject("LensFlare");
            if (parent != null)
                go.transform.SetParent(parent, false);
            var flare = go.AddComponent<FlareLight>();
            flare._totalScale = 1f;
            flare._totalAlpha = 0.5f;
            flare._flares = new[]
            {
                // Factory's ladder light: shrinks and fades towards 33 m
                new MultiFlare.Flare
                {
                    _scale = new Vector2(0.3f, 0.3f), _alpha = 0.25f, _color = Warm, _flareType = FlareType.Normal,
                    _minDist = 0.1f, _maxDist = 33f, _minScale = 1f, _maxScale = 0.01f, _minAlpha = 1f, _maxAlpha = 0.5f,
                    _texId = 8,
                },
            };
            if (lamp != null)
                AddToLamp(lamp, l => l.MultiFlareLights = Append(l.MultiFlareLights, flare));
            return flare;
        }

        public static FlareSceneSettings FindFlareSettings() =>
            Object.FindObjectsOfType<FlareSceneSettings>(true).FirstOrDefault();

        // the game's FlareSceneSettings (flare atlas and batch materials), as Factory's
        // TechnicalManagers/lightManager has it; the SDK ships its data in Lights/Flares (every retail
        // map shares the one set). Saved inactive under SDK_Flares; CustomMapLoader swaps its
        // shaders and wakes it.
        public static GameObject AddFlareSettings(Scene scene)
        {
            if (!File.Exists(Path.Combine(FlareDataFolder, "flares.json")))
            {
                EditorUtility.DisplayDialog("Lens Flare Settings", $"The SDK's flare data is missing from {FlareDataFolder}.", "OK");
                return null;
            }
            foreach (var old in scene.GetRootGameObjects().Where(g => g.name == FlaresRootName).ToList())
                Undo.DestroyObjectImmediate(old);
            if (FindFlareSettings() != null)
            {
                EditorUtility.DisplayDialog("Lens Flare Settings", "Another open scene of the map already has flare settings.", "OK");
                return null;
            }
            return TarkovWeatherBuilder.Build(scene, FlareDataFolder, "flares.json", FlaresRootName, false);
        }

        // ------------------------------------------------------------ advanced light

        public static AdvancedLight CreateAdvancedLight(Transform parent, LampController lamp)
        {
            var go = new GameObject("AdvancedLight");
            if (parent != null)
                go.transform.SetParent(parent, false);
            var light = go.AddComponent<AdvancedLight>();
            light.LightType = AdvancedLight.LightTypeEnum.Point;
            light.ShadingType = AdvancedLight.ShadingTypeEnum.Diffuse;
            light.Mesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            light.Color = Warm * 0.5f;
            light.Radius = 5f;
            light.CubeScale = Vector3.one;
            // Shader stays empty: CustomMapLoader fills in the game's CustomLights/Light
            if (lamp != null)
                AddToLamp(lamp, l => l.CustomLights = Append(l.CustomLights, light));
            return light;
        }

        // ------------------------------------------------------------ helpers

        private static void AddToLamp(LampController lamp, System.Action<LampController> add)
        {
            Undo.RecordObject(lamp, "Add light to lamp");
            add(lamp);
            EditorUtility.SetDirty(lamp);
        }

        private static T[] Append<T>(T[] array, T item) => (array ?? new T[0]).Append(item).ToArray();

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
        private static void DrawAreaLight(AreaLight area, GizmoType type)
        {
            Gizmos.matrix = area.transform.localToWorldMatrix;
            Gizmos.color = new Color(area.m_Color.r, area.m_Color.g, area.m_Color.b, 0.6f);
            Gizmos.DrawCube(Vector3.zero, new Vector3(area.length, area.size, 0.01f));
            if ((type & GizmoType.Selected) != 0)
            {
                Gizmos.color = new Color(area.m_Color.r, area.m_Color.g, area.m_Color.b, 0.9f);
                Gizmos.DrawWireCube(new Vector3(0f, 0f, area.m_ClipBoxSize.z * 0.5f), area.m_ClipBoxSize);
                Gizmos.DrawLine(Vector3.zero, Vector3.forward * area.depth);
            }
            Gizmos.matrix = Matrix4x4.identity;
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
        private static void DrawAdvancedLight(AdvancedLight light, GizmoType type)
        {
            Gizmos.color = new Color(light.Color.r, light.Color.g, light.Color.b, 1f);
            Gizmos.DrawSphere(light.transform.position, 0.1f);
            if ((type & GizmoType.Selected) != 0)
                Gizmos.DrawWireSphere(light.transform.position, light.Radius);
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
        private static void DrawFlare(FlareLight flare, GizmoType type)
        {
            var color = flare._flares != null && flare._flares.Length > 0 ? flare._flares[0]._color : Color.white;
            Gizmos.color = new Color(color.r, color.g, color.b, 0.8f);
            var p = flare.transform.position;
            float s = 0.15f;
            Gizmos.DrawLine(p - Vector3.right * s, p + Vector3.right * s);
            Gizmos.DrawLine(p - Vector3.up * s, p + Vector3.up * s);
            Gizmos.DrawLine(p - Vector3.forward * s, p + Vector3.forward * s);
        }
    }
}
