using EFT.Weather;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Scene view preview of the in-raid lighting, driven by the scene's environment object (the simple
    // "Weather" rig or the full "SDK_Weather"). In the raid, BSG's shaders take ambient light from
    // spherical harmonics the time of day system computes from the sun's height; the editor never runs
    // that, so the stand-in shaders were lit by Unity's default ambient instead. This computes the same
    // harmonics (TODSkySimple's formula exactly; the full system's ToDController without its live
    // atmosphere samples) and gives them to Unity as the scene ambient, places the sun for the preview
    // hour, and applies LevelSettings' fog and skybox as the game does.
    //
    // Only values the game overrides at runtime are touched (light rotation and colour, RenderSettings),
    // so the preview never changes what ships.
    [InitializeOnLoad]
    public static class TarkovLightingPreview
    {
        private const string EnabledKey = "TarkovSDK.LightingPreview";
        private const string HourKey = "TarkovSDK.LightingPreviewHour";
        private static double _next;

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

        static TarkovLightingPreview()
        {
            SceneView.duringSceneGui += _ =>
            {
                if (!Enabled || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.timeSinceStartup < _next)
                    return;
                _next = EditorApplication.timeSinceStartup + 0.25;
                Apply(SceneManager.GetActiveScene());
            };
        }

        public static void Refresh()
        {
            _next = 0;
            SceneView.RepaintAll();
        }

        public static void Apply(Scene scene)
        {
            Light sun = null;
            TOD_CycleParameters cycle = null;
            float latitude = 46f;
            System.Func<Vector3, SphericalHarmonicsL2> ambient = null;
            System.Func<float, Color> sunColor = null;

            var full = TarkovWeatherBuilder.Find(scene);
            var simple = TarkovLightingRig.Find(scene);
            if (full != null)
            {
                var sky = full.GetComponentInChildren<TOD_Sky>(true);
                var weather = full.GetComponentInChildren<WeatherController>(true);
                var tod = weather != null ? weather.TimeOfDayController : null;
                if (sky != null)
                {
                    cycle = sky.Cycle;
                    if (sky.World != null)
                        latitude = sky.World.Latitude;
                    foreach (var light in sky.GetComponentsInChildren<Light>(true))
                        if (light.type == LightType.Directional)
                            sun = light;
                }
                if (tod != null)
                {
                    ambient = dir => FullHarmonics(tod, dir);
                    sunColor = t01 => tod.LightColor != null ? tod.LightColor.Evaluate(t01) : Color.white;
                }
            }
            else if (simple != null)
            {
                sun = simple.LightSource;
                cycle = simple.Cycle;
                latitude = simple.Latitude;
                ambient = dir => SimpleHarmonics(simple, dir);
                sunColor = t01 => simple.LightColorGradient != null ? simple.LightColorGradient.Evaluate(t01) : Color.white;
            }
            if (sun == null || cycle == null)
                return;

            var level = Object.FindObjectOfType<LevelSettings>();
            float north = level != null ? level._northDirection : 0f;
            var preview = new TOD_CycleParameters { Hour = Hour >= 0f ? Hour : cycle.Hour, Day = cycle.Day, Month = cycle.Month, Year = cycle.Year };
            var toSun = TarkovTimeOfDayEditor.SunDirection(preview, latitude, north);
            sun.transform.rotation = Quaternion.LookRotation(-toSun);

            float t01 = toSun.y * 0.5f + 0.5f;
            float daylight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.035f, 0.17f, toSun.y));
            if (sunColor != null)
                sun.color = sunColor(t01) * Mathf.Max(daylight, 0.001f);
            RenderSettings.sun = sun;
            ApplyRetailReflection();

            if (ambient != null)
            {
                var sh = ambient(toSun);
                var results = new Color[3];
                sh.Evaluate(new[] { Vector3.up, Vector3.ProjectOnPlane(toSun, Vector3.up).normalized, Vector3.down }, results);
                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = Clamp(results[0]);
                RenderSettings.ambientEquatorColor = Clamp(results[1]);
                RenderSettings.ambientGroundColor = Clamp(results[2]);
                RenderSettings.ambientIntensity = 1f;
            }

            if (level != null)
            {
                RenderSettings.fog = level.fog;
                int mode = (int)level.fogMode;
                RenderSettings.fogMode = mode >= 1 && mode <= 3 ? level.fogMode : FogMode.Exponential;
                RenderSettings.fogDensity = level.fogDensity;
                RenderSettings.fogStartDistance = level.fogStartDistance;
                RenderSettings.fogEndDistance = level.fogEndDistance;
                RenderSettings.fogColor = level.fogColor * Mathf.Lerp(0.08f, 1f, daylight);
                RenderSettings.skybox = level.skybox;
            }
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

        private static Color Clamp(Color c) => new Color(Mathf.Max(0f, c.r), Mathf.Max(0f, c.g), Mathf.Max(0f, c.b), 1f);

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

        // ToDController's job for a clear sky, without the atmosphere samples it adds from the live TOD sky
        private static SphericalHarmonicsL2 FullHarmonics(ToDController tod, Vector3 toSun)
        {
            float t = toSun.y, t01 = t * 0.5f + 0.5f;
            float contrast = Eval(tod.AmbientContrast, t, 1f), saturation = Eval(tod.AmbientSaturation, t, 1f), brightness = Eval(tod.AmbientBrightness, t, 1f);
            float light = Mathf.Lerp(0.003f, 0.25f, Mathf.InverseLerp(0.07f, 1f, t));
            const float cloudBoost = 1.667f; // 1 + InverseLerp(-0.4, 0.2, 0): a clear sky
            var sh = new SphericalHarmonicsL2();
            var lightColor = Grade(tod.LightColor != null ? tod.LightColor.Evaluate(t01) : Color.white, contrast, light, 1f, saturation, brightness);
            sh.AddDirectionalLight((-toSun + Vector3.down).normalized, lightColor, Eval(tod.BounceHarmonicIntensity, t, 0f));
            var add = (tod.AddAmbient != null ? tod.AddAmbient.Evaluate(t01) : Color.black) + tod.CloudnessLightColor * Mathf.Clamp01(t) * cloudBoost;
            sh.AddAmbientLight(Grade(add, contrast, light, 1f, saturation, brightness));
            var top = Grade(tod.AddTopAmbient != null ? tod.AddTopAmbient.Evaluate(t01) : Color.black, contrast, light, 1f, saturation, brightness);
            sh.AddDirectionalLight(Vector3.up, top, 1f);
            return sh;
        }

        // ContrastColor, ContrastColor(light), SaturateColor, brightness, LimitColor, in the game's order
        private static Color Grade(Color c, float contrast, float light, float lightContrast, float saturation, float brightness)
        {
            c = new Color(Mathf.LerpUnclamped(0.5f, c.r, contrast), Mathf.LerpUnclamped(0.5f, c.g, contrast), Mathf.LerpUnclamped(0.5f, c.b, contrast));
            c = new Color(Mathf.LerpUnclamped(light, c.r, lightContrast), Mathf.LerpUnclamped(light, c.g, lightContrast), Mathf.LerpUnclamped(light, c.b, lightContrast));
            float grey = (c.r + c.g + c.b) / 3f;
            c = new Color(Mathf.LerpUnclamped(grey, c.r, saturation), Mathf.LerpUnclamped(grey, c.g, saturation), Mathf.LerpUnclamped(grey, c.b, saturation));
            c *= brightness;
            return new Color(Mathf.Max(0f, c.r), Mathf.Max(0f, c.g), Mathf.Max(0f, c.b));
        }

        private static float Eval(AnimationCurve curve, float t, float fallback) =>
            curve != null && curve.length > 0 ? curve.Evaluate(t) : fallback;
    }
}
