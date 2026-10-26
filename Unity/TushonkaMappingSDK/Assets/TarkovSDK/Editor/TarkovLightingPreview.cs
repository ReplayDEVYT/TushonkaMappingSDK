using System.Collections.Generic;
using System.Linq;
using EFT.Weather;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Scene view preview of the in-raid lighting, driven by the scene's environment object (the simple
    // "Weather" rig or the full "SDK_Weather"). In the raid BSG's shaders take ambient light from
    // spherical harmonics the time of day system computes from the sun's height and AmbientLight hands
    // to the shaders as the _SHAr.. globals; the editor never runs that, so this computes the same
    // harmonics (TODSkySimple's formula exactly; ToDController's job for the full weather, with the
    // preview's cloudiness and fog), sets the game's globals through TarkovShaderGlobals for the
    // decompiled stand-ins, places the sun (the game's own celestial maths for the full weather) and
    // applies LevelSettings to RenderSettings as LevelSettings.ApplySettings does in the raid.
    //
    // Only values the game overrides at runtime are touched (light rotation, colour and intensity,
    // RenderSettings), so the preview never changes what ships.
    [InitializeOnLoad]
    public static class TarkovLightingPreview
    {
        private const string EnabledKey = "TarkovSDK.LightingPreview";
        private const string HourKey = "TarkovSDK.LightingPreviewHour";
        private const string CloudinessKey = "TarkovSDK.LightingPreviewCloudiness";
        private const string FogKey = "TarkovSDK.LightingPreviewFog";
        // TOD_Scattering.GlobalDensity on Customs' camera: the fog of a clear raid
        private const float ClearScatteringDensity = 0.004076f;
        private const string SkyboxMaterialPath = "Assets/TarkovSDK/Materials/SDK_TodSkybox.mat";
        private static double _next;

        // the last preview's ambient harmonics, sun height and light (colour x intensity)
        public static SphericalHarmonicsL2 LastHarmonics;
        public static bool HasHarmonics;
        public static float LastSunHeight;
        public static Color LastLight;

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledKey, true);
            set { EditorPrefs.SetBool(EnabledKey, value); Refresh(); }
        }

        // below 0: the map's own hour (the rig's Cycle)
        public static float Hour
        {
            get => EditorPrefs.GetFloat(HourKey, -1f);
            set { EditorPrefs.SetFloat(HourKey, value); Refresh(); }
        }

        // the raid weather's Cloudiness, -1 clear .. 1 overcast (full weather maps only)
        public static float Cloudiness
        {
            get => EditorPrefs.GetFloat(CloudinessKey, -1f);
            set { EditorPrefs.SetFloat(CloudinessKey, Mathf.Clamp(value, -1f, 1f)); Refresh(); }
        }

        // the raid weather's Fog, 0 .. 1 (full weather maps only)
        public static float Fog
        {
            get => EditorPrefs.GetFloat(FogKey, 0f);
            set { EditorPrefs.SetFloat(FogKey, Mathf.Clamp01(value)); Refresh(); }
        }

        static TarkovLightingPreview()
        {
            SceneView.duringSceneGui += view =>
            {
                if (!Enabled || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.timeSinceStartup < _next)
                    return;
                _next = EditorApplication.timeSinceStartup + 0.25;
                Apply(SceneManager.GetActiveScene(), view != null ? view.camera : null);
            };
        }

        // What lights the preview: the full weather's Sky Dome (TOD_Sky) and the sun under it, or the simple rig and
        // its sun. Turned off, the preview has nothing to light the scene with, so it looks unlit.
        private static IEnumerable<Component> SkyParts(Scene scene)
        {
            var full = TarkovWeatherBuilder.Find(scene);
            if (full != null)
            {
                var sky = full.GetComponentInChildren<TOD_Sky>(true);
                if (sky == null)
                    yield break;
                yield return sky;
                foreach (var light in sky.GetComponentsInChildren<Light>(true))
                    if (light.type == LightType.Directional)
                        yield return light;
                yield break;
            }
            var simple = TarkovLightingRig.Find(scene);
            if (simple == null)
                yield break;
            yield return simple;
            if (simple.LightSource != null)
                yield return simple.LightSource;
        }

        private static bool IsOn(Component c) => c.gameObject.activeInHierarchy && (!(c is Behaviour b) || b.enabled);

        // the turned-off parts' names, or null when the sky is on (or the scene has none)
        public static string SkyOffReason(Scene scene)
        {
            var off = SkyParts(scene).Where(c => c != null && !IsOn(c)).Select(c => c.name).Distinct().ToList();
            return off.Count == 0 ? null : string.Join(", ", off);
        }

        // turns the sky's objects (and their parents) and components back on
        public static void EnableSky(Scene scene)
        {
            foreach (var part in SkyParts(scene).Where(c => c != null && !IsOn(c)).ToList())
            {
                for (var t = part.transform; t != null; t = t.parent)
                    if (!t.gameObject.activeSelf)
                    {
                        Undo.RecordObject(t.gameObject, "Enable sky");
                        t.gameObject.SetActive(true);
                    }
                if (part is Behaviour behaviour && !behaviour.enabled)
                {
                    Undo.RecordObject(behaviour, "Enable sky");
                    behaviour.enabled = true;
                }
            }
            Refresh();
        }

        public static void Refresh()
        {
            _next = 0;
            SceneView.RepaintAll();
        }

        public static void Apply(Scene scene, Camera sceneCamera = null)
        {
            Light sun = null;
            TOD_CycleParameters cycle = null;
            float latitude = 46f;
            TOD_Sky sky = null;
            ToDController tod = null;
            WeatherController weather = null;
            TODSkySimple simple = null;
            AmbientLight ambientLight = null;

            var full = TarkovWeatherBuilder.Find(scene);
            if (full != null)
            {
                sky = full.GetComponentInChildren<TOD_Sky>(true);
                weather = full.GetComponentInChildren<WeatherController>(true);
                tod = weather != null ? weather.TimeOfDayController : null;
                ambientLight = full.GetComponentInChildren<AmbientLight>(true);
                if (sky != null)
                {
                    cycle = sky.Cycle;
                    if (sky.World != null)
                        latitude = sky.World.Latitude;
                    foreach (var light in sky.GetComponentsInChildren<Light>(true))
                        if (light.type == LightType.Directional)
                            sun = light;
                }
            }
            else
            {
                simple = TarkovLightingRig.Find(scene);
                if (simple != null)
                {
                    sun = simple.LightSource;
                    cycle = simple.Cycle;
                    latitude = simple.Latitude;
                    ambientLight = simple.AmbientLight;
                }
            }
            if (sun == null || cycle == null)
                return;

            var level = Object.FindObjectOfType<LevelSettings>();
            float north = level != null ? level._northDirection : 0f;
            var preview = new TOD_CycleParameters { Hour = Hour >= 0f ? Hour : cycle.Hour, Day = cycle.Day, Month = cycle.Month, Year = cycle.Year };
            Vector3 toSun;
            TarkovShaderGlobals.SkyState skyState = null;
            SphericalHarmonicsL2 sh = default;
            bool haveSh = false;
            float scatteringDensity = 0f;

            if (sky != null)
            {
                // the game's TOD_Sky.UpdateCelestials, so the sun and moon sit where the raid puts them
                var world = sky.World ?? new TOD_WorldParameters { Latitude = latitude, Longitude = 84f, UTC = 7.24f };
                var moonPosition = sky.Moon != null ? sky.Moon.Position : TOD_MoonPositionType.Realistic;
                var celestials = TarkovShaderGlobals.Compute(preview, world, north, moonPosition);
                toSun = celestials.Sun;
                float cloudiness = Cloudiness;
                if (tod != null)
                {
                    // ToDController.Update: the weather's atmosphere, light colour and harmonics
                    var atmosphere = TarkovShaderGlobals.WeatherAtmosphere(tod, toSun.y, cloudiness, celestials.Sun, celestials.Moon, out float nightMultiplier);
                    skyState = TarkovShaderGlobals.SkyState.From(sky, celestials, atmosphere, nightMultiplier);
                    float t01 = toSun.y * 0.5f + 0.5f;
                    var lightColor = SaturateColor(tod.LightColor != null ? tod.LightColor.Evaluate(t01) : Color.white, 1f - skyState.Fogginess);
                    sun.color = lightColor;
                    sun.intensity = skyState.LightIntensity;
                    sun.transform.rotation = Quaternion.LookRotation(-(skyState.IsDay ? celestials.Sun : celestials.Moon));
                    sh = FullHarmonics(tod, skyState, toSun, lightColor, -cloudiness);
                    haveSh = true;
                }
                else
                {
                    skyState = TarkovShaderGlobals.SkyState.From(sky, celestials);
                    sun.transform.rotation = Quaternion.LookRotation(-toSun);
                }
                // WeatherController.method_4 -> ControlScattering, as the client's WeatherFogDensityPatch applies it
                float multiplier = weather != null ? TarkovShaderGlobals.Curve(weather.FogMultyplyer, toSun.y) : 1f;
                float fog = Mathf.Max(Mathf.Clamp01(Fog * multiplier - 0.001f), 0.001f);
                scatteringDensity = Mathf.Max(ClearScatteringDensity, fog);
            }
            else
            {
                toSun = TarkovTimeOfDayEditor.SunDirection(preview, latitude, north);
                sun.transform.rotation = Quaternion.LookRotation(-toSun);
                float t01 = toSun.y * 0.5f + 0.5f;
                float daylight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.035f, 0.17f, toSun.y));
                sun.color = (simple.LightColorGradient != null ? simple.LightColorGradient.Evaluate(t01) : Color.white) * Mathf.Max(daylight, 0.001f);
                sh = SimpleHarmonics(simple, toSun);
                haveSh = true;
            }
            RenderSettings.sun = sun;
            ApplyRetailReflection();
            if (haveSh)
                TarkovShaderGlobals.SetHarmonics(sh);
            // for the Lighting Editor's readout
            LastHarmonics = haveSh ? sh : default;
            HasHarmonics = haveSh;
            LastSunHeight = toSun.y;
            LastLight = sun.color * sun.intensity;

            // RenderSettings exactly as LevelSettings.ApplySettings sets them in the raid; the stand-ins take
            // the real ambient from the harmonics and only use these for the small GBuffer ambient
            if (level != null)
            {
                RenderSettings.ambientMode = level.AmbientMode;
                RenderSettings.ambientSkyColor = level.SkyColor;
                RenderSettings.ambientEquatorColor = level.EquatorColor;
                RenderSettings.ambientGroundColor = level.GroundColor;
                RenderSettings.ambientLight = level.SkyColor;
                RenderSettings.ambientIntensity = level.AmbientIntensity;
                RenderSettings.fog = level.fog;
                int mode = (int)level.fogMode;
                RenderSettings.fogMode = mode >= 1 && mode <= 3 ? level.fogMode : FogMode.Exponential;
                RenderSettings.fogDensity = level.fogDensity;
                RenderSettings.fogStartDistance = level.fogStartDistance;
                RenderSettings.fogEndDistance = level.fogEndDistance;
                RenderSettings.fogColor = level.fogColor;
                RenderSettings.skybox = level.skybox;
            }
            if (skyState != null)
            {
                // the raid draws the TOD dome; the editor draws the same sky as the skybox
                var skybox = TodSkyboxMaterial();
                if (skybox != null)
                    RenderSettings.skybox = skybox;
                // TOD_Sky.UpdateFog: the fog colour follows the sky in the camera's direction
                var fogSettings = sky.Fog;
                if (fogSettings != null && fogSettings.Mode != TOD_FogType.None)
                {
                    var forward = sceneCamera != null ? sceneCamera.transform.forward : Vector3.forward;
                    RenderSettings.fogColor = skyState.FogColor(forward, fogSettings.HeightBias, fogSettings.Mode == TOD_FogType.Directional);
                }
            }

            TarkovShaderGlobals.Apply(toSun, skyState, level, ambientLight, sceneCamera, scatteringDensity);
        }

        // as retail scenes: default reflection Custom, no cubemap, intensity 0 (the game also forces this)
        public static void ApplyRetailReflection()
        {
            if (RenderSettings.defaultReflectionMode == DefaultReflectionMode.Custom && RenderSettings.customReflectionTexture == null && RenderSettings.reflectionIntensity == 0f)
                return;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = null;
            RenderSettings.reflectionIntensity = 0f;
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        }

        // the decompiled "Time of Day/Skybox" as a skybox material (editor only; never referenced by a scene)
        private static Material TodSkyboxMaterial()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(SkyboxMaterialPath);
            if (material != null)
                return material;
            var shader = Shader.Find("Tarkov/Time of Day/Skybox");
            if (shader == null)
                return null;
            System.IO.Directory.CreateDirectory("Assets/TarkovSDK/Materials");
            material = new Material(shader) { name = "SDK_TodSkybox" };
            AssetDatabase.CreateAsset(material, SkyboxMaterialPath);
            return material;
        }

        // ------------------------------------------------------------ the game's formulas

        // TODSkySimple.CalculateSphericalHarmonics
        private static SphericalHarmonicsL2 SimpleHarmonics(TODSkySimple sky, Vector3 toSun)
        {
            float t = toSun.y, t01 = t * 0.5f + 0.5f;
            float contrast = Eval(sky.AmbientContrast, t, 1f), saturation = Eval(sky.AmbientSaturation, t, 1f), brightness = Eval(sky.AmbientBrightness, t, 1f);
            float light = Mathf.Lerp(0.003f, 0.25f, Mathf.InverseLerp(0.07f, 1f, t));
            var sh = new SphericalHarmonicsL2();
            var lightColor = Grade(sky.LightColorGradient != null ? sky.LightColorGradient.Evaluate(t01) : Color.white, contrast, light, 1f, saturation, brightness);
            sh.AddDirectionalLight((-toSun + Vector3.down).normalized, lightColor, Eval(sky.BounceHarmonicIntensity, t, 0f));
            var add = (sky.AddAmbient != null ? sky.AddAmbient.Evaluate(t01) : Color.black) + sky.CloudnessLightColor * Mathf.Clamp01(t) * 0.333f;
            sh.AddAmbientLight(Grade(add, contrast, light, 1f, saturation, brightness));
            var top = Grade(sky.AddTopAmbient != null ? sky.AddTopAmbient.Evaluate(t01) : Color.black, contrast, light, 1f, saturation, brightness);
            sh.AddDirectionalLight(Vector3.up, top, 1f);
            var environment = new SphericalHarmonicsL2();
            environment.AddAmbientLight(new Color(0.013f, 0.013f, 0.013f));
            return sh * (1f - sky.HarmonicsDaylight) + environment * sky.HarmonicsDaylight;
        }

        // ToDController.ComputeSphericalHarmonicsContainerJob.CalculateSphericalHarmonics (FullSH): the sun's
        // bounce, the flat ambient, five atmosphere samples (up and the four horizon directions around the
        // sun) and the top ambient. cloudiness is the job's value (minus the weather's Cloudiness).
        private static SphericalHarmonicsL2 FullHarmonics(ToDController tod, TarkovShaderGlobals.SkyState sky, Vector3 toSun, Color lightSourceColor, float cloudiness)
        {
            float t = toSun.y, t01 = t * 0.5f + 0.5f;
            var forward = Eval(tod.ForwarHarmonicMultiplyer, t01, new Color(0.5f, 0.5f, 0.5f)) * 2f * Eval(tod.ForwarHarmonicIntensity, t01, 0f);
            var backward = Eval(tod.BackwardHarmonicMultiplyer, t01, new Color(0.5f, 0.5f, 0.5f)) * 2f;
            float top = Eval(tod.TopHarmonicIntensity, t, 0f);
            float horizon = Eval(tod.HorizontHarmonicIntensity, t01, 0f);
            float fogginess = sky.Fogginess;
            var flat = new Vector3(toSun.x, 0f, toSun.z).normalized;
            var dirs = new[] { Vector3.up, flat, -flat, new Vector3(flat.z, 0f, -flat.x), new Vector3(-flat.z, 0f, flat.x) };
            float contrast = Eval(tod.AmbientContrast, t, 1f), saturation = Eval(tod.AmbientSaturation, t, 1f) * (1f - fogginess), brightness = Eval(tod.AmbientBrightness, t, 1f);
            float light = Mathf.Lerp(0.003f, 0.25f, Mathf.InverseLerp(0.07f, 1f, t));
            float lightContrast = Mathf.Lerp(1f, 0.3f, fogginess);
            float cloudBoost = 1f + Mathf.InverseLerp(-0.4f, 0.2f, cloudiness);
            top *= cloudBoost;
            float bounce = Mathf.Max(1f - (cloudiness + fogginess) * 1.3f, 0f);

            var sh = new SphericalHarmonicsL2();
            var lightColor = Grade(lightSourceColor, contrast, light, lightContrast, saturation, brightness);
            sh.AddDirectionalLight((-toSun + Vector3.down).normalized, lightColor, Eval(tod.BounceHarmonicIntensity, t, 0f) * bounce);
            var add = (tod.AddAmbient != null ? tod.AddAmbient.Evaluate(t01) : Color.black) + tod.CloudnessLightColor * Mathf.Clamp01(t) * cloudBoost;
            sh.AddAmbientLight(Grade(add, contrast, light, lightContrast, saturation, brightness));
            for (int i = 0; i < dirs.Length; i++)
            {
                var c = sky.SampleAtmosphere(dirs[i], directLight: false);
                if (i == 1) c *= forward;
                if (i == 2) c *= backward;
                c = Grade(c, contrast, light, lightContrast, saturation, brightness);
                sh.AddDirectionalLight(dirs[i], c, i == 0 ? top : horizon);
            }
            var topAmbient = Grade(tod.AddTopAmbient != null ? tod.AddTopAmbient.Evaluate(t01) : Color.black, contrast, light, lightContrast, saturation, brightness);
            sh.AddDirectionalLight(Vector3.up, topAmbient, 1f);
            // HarmonicsDaylight is 0 outside the game's day/night transition, so the environment term drops out
            return sh;
        }

        // ContrastColor, ContrastColor2(light), SaturateColor, brightness, LimitColor, in the game's order
        private static Color Grade(Color c, float contrast, float light, float lightContrast, float saturation, float brightness)
        {
            c = new Color(Mathf.LerpUnclamped(0.5f, c.r, contrast), Mathf.LerpUnclamped(0.5f, c.g, contrast), Mathf.LerpUnclamped(0.5f, c.b, contrast));
            c = new Color(Mathf.LerpUnclamped(light, c.r, lightContrast), Mathf.LerpUnclamped(light, c.g, lightContrast), Mathf.LerpUnclamped(light, c.b, lightContrast));
            c = SaturateColor(c, saturation);
            c *= brightness;
            return new Color(Mathf.Max(0f, c.r), Mathf.Max(0f, c.g), Mathf.Max(0f, c.b));
        }

        private static Color SaturateColor(Color c, float t)
        {
            float grey = (c.r + c.g + c.b) / 3f;
            return new Color(Mathf.LerpUnclamped(grey, c.r, t), Mathf.LerpUnclamped(grey, c.g, t), Mathf.LerpUnclamped(grey, c.b, t), c.a);
        }

        private static float Eval(AnimationCurve curve, float t, float fallback) =>
            curve != null && curve.length > 0 ? curve.Evaluate(t) : fallback;

        private static Color Eval(Gradient gradient, float t, Color fallback) => gradient != null ? gradient.Evaluate(t) : fallback;
    }
}
