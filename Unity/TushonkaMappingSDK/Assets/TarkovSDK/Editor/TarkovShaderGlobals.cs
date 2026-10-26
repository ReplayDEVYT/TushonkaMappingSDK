using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // The shader globals the game sets at runtime, set in the editor so the decompiled stand-ins
    // (Assets/TarkovSDK/Shaders/Game, Tools/ShaderStandins/DECOMPILE.md) light the Scene view as the raid
    // does: AmbientLight.SetSH's harmonics, its sky reflection cubemap and reflection settings,
    // TOD_Scattering's fog density and TOD_Sky.UpdateShaderProperties' sky constants. The maths is the
    // game's (TOD_Sky.UpdateCelestials / UpdateScattering / ShaderScatteringColor), ported line by line.
    [InitializeOnLoad]
    public static class TarkovShaderGlobals
    {
        private const int CubeSize = 128;  // AmbientLight.QubemapResolution on retail maps
        private const string CubeName = "SDK sky reflection";
        private const string CubeCameraName = "SDK sky reflection camera";
        private static RenderTexture _skyCube;
        private static Camera _cubeCamera;

        // the cubemap and its camera are HideAndDontSave, so they outlive a script reload that forgets them; each
        // recompile used to leak another pair
        static TarkovShaderGlobals()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
            Cleanup();
        }

        private static void Cleanup()
        {
            foreach (var texture in Resources.FindObjectsOfTypeAll<RenderTexture>())
                if (texture.name == CubeName)
                {
                    texture.Release();
                    Object.DestroyImmediate(texture);
                }
            _skyCube = null;
            foreach (var camera in Resources.FindObjectsOfTypeAll<Camera>())
                if (camera != null && camera.name == CubeCameraName && !EditorUtility.IsPersistent(camera))
                    Object.DestroyImmediate(camera.gameObject);
            _cubeCamera = null;
        }

        // ------------------------------------------------------------ TOD_Sky.UpdateCelestials

        public struct Celestials
        {
            public Vector3 Sun, Moon;        // world directions towards them
            public float SunZenith, MoonZenith; // degrees
        }

        // the sun and moon for a date and place, as TOD_Sky computes them (north = LevelSettings.NorthDirection)
        public static Celestials Compute(TOD_CycleParameters cycle, TOD_WorldParameters world, float north, TOD_MoonPositionType moonPosition)
        {
            float lat = Mathf.Deg2Rad * world.Latitude;
            float sinLat = Mathf.Sin(lat), cosLat = Mathf.Cos(lat);
            float hours = cycle.Hour - world.UTC;
            int year = cycle.Year, month = cycle.Month, day = cycle.Day;
            float d = (367 * year - 7 * (year + (month + 9) / 12) / 4 + 275 * month / 9 + day - 730530) + hours / 24f;
            float ecl = Mathf.Deg2Rad * (23.4393f - 3.563E-07f * d);
            float sinEcl = Mathf.Sin(ecl), cosEcl = Mathf.Cos(ecl);

            // sun
            float w = 282.9404f + 4.70935E-05f * d;
            float e = 0.016709f - 1.151E-09f * d;
            float M = Mathf.Deg2Rad * (356.047f + 0.98560023f * d);
            float sinM = Mathf.Sin(M), cosM = Mathf.Cos(M);
            float E = M + e * sinM * (1f + e * cosM);
            float xv = Mathf.Cos(E) - e, yv = Mathf.Sqrt(1f - e * e) * Mathf.Sin(E);
            float v = Mathf.Rad2Deg * Mathf.Atan2(yv, xv);
            float r = Mathf.Sqrt(xv * xv + yv * yv);
            float lon = Mathf.Deg2Rad * (v + w);
            float xs = r * Mathf.Cos(lon), ys = r * Mathf.Sin(lon);
            float xe = xs, ye = ys * cosEcl, ze = ys * sinEcl;
            float ra = Mathf.Atan2(ye, xe);
            float dec = Mathf.Atan2(ze, Mathf.Sqrt(xe * xe + ye * ye));
            float sinDec = Mathf.Sin(dec), cosDec = Mathf.Cos(dec);
            float gmst0 = v + w + 180f + 15f * hours;
            float lst = Mathf.Deg2Rad * (gmst0 + world.Longitude);
            float ha = lst - ra;
            float x = Mathf.Cos(ha) * cosDec, y = Mathf.Sin(ha) * cosDec, z = sinDec;
            float xhor = x * sinLat - z * cosLat, yhor = y, zhor = x * cosLat + z * sinLat;
            float azimuth = Mathf.Atan2(yhor, xhor) + Mathf.Deg2Rad * north;
            float altitude = Mathf.Atan2(zhor, Mathf.Sqrt(xhor * xhor + yhor * yhor));
            float sunTheta = Mathf.PI / 2f - altitude, sunPhi = -azimuth;

            float moonTheta, moonPhi;
            if (moonPosition == TOD_MoonPositionType.Realistic)
            {
                float N = Mathf.Deg2Rad * (125.1228f - 0.05295381f * d);
                float wm = 318.0634f + 0.16435732f * d;
                const float em = 0.0549f;
                float Mm = Mathf.Deg2Rad * (115.3654f + 13.064993f * d);
                float sinN = Mathf.Sin(N), cosN = Mathf.Cos(N);
                float sinI = Mathf.Sin(0.08980417f), cosI = Mathf.Cos(0.08980417f);
                float sinMm = Mathf.Sin(Mm), cosMm = Mathf.Cos(Mm);
                float Em = Mm + em * sinMm * (1f + em * cosMm);
                float xvm = 60.2666f * (Mathf.Cos(Em) - em), yvm = 60.2666f * (Mathf.Sqrt(0.996986f) * Mathf.Sin(Em));
                float vm = Mathf.Rad2Deg * Mathf.Atan2(yvm, xvm);
                float rm = Mathf.Sqrt(xvm * xvm + yvm * yvm);
                float lm = Mathf.Deg2Rad * (vm + wm);
                float sinL = Mathf.Sin(lm), cosL = Mathf.Cos(lm);
                float xg = rm * (cosN * cosL - sinN * sinL * cosI);
                float yg = rm * (sinN * cosL + cosN * sinL * cosI);
                float zg = rm * (sinL * sinI);
                float xem = xg, yem = yg * cosEcl - zg * sinEcl, zem = yg * sinEcl + zg * cosEcl;
                float ram = Mathf.Atan2(yem, xem);
                float decm = Mathf.Atan2(zem, Mathf.Sqrt(xem * xem + yem * yem));
                float sinDecm = Mathf.Sin(decm), cosDecm = Mathf.Cos(decm);
                float ham = lst - ram;
                float xm = Mathf.Cos(ham) * cosDecm, ym = Mathf.Sin(ham) * cosDecm, zm = sinDecm;
                float xhm = xm * sinLat - zm * cosLat, yhm = ym, zhm = xm * cosLat + zm * sinLat;
                float azm = Mathf.Atan2(yhm, xhm) + Mathf.Deg2Rad * north;
                float altm = Mathf.Atan2(zhm, Mathf.Sqrt(xhm * xhm + yhm * yhm));
                moonTheta = Mathf.PI / 2f - altm;
                moonPhi = -azm;
            }
            else
            {
                moonTheta = sunTheta - Mathf.PI;
                moonPhi = sunPhi;
            }
            return new Celestials
            {
                Sun = OrbitalToLocal(sunTheta, sunPhi),
                Moon = OrbitalToLocal(moonTheta, moonPhi),
                SunZenith = Mathf.Rad2Deg * sunTheta,
                MoonZenith = Mathf.Rad2Deg * moonTheta,
            };
        }

        private static Vector3 OrbitalToLocal(float theta, float phi)
        {
            float sinTheta = Mathf.Sin(theta), cosTheta = Mathf.Cos(theta);
            return new Vector3(sinTheta * Mathf.Sin(phi), cosTheta, sinTheta * Mathf.Cos(phi));
        }

        // ------------------------------------------------------------ the weather's atmosphere

        // WeatherController.method_4 + ToDController.Update: what the weather makes of the sky's atmosphere for
        // a sun height (-1..1) and the raid's cloudiness (-1 clear .. 1 overcast); the Night colour multiplier
        // follows the moon's phase against the sun
        public static TOD_AtmosphereParameters WeatherAtmosphere(EFT.Weather.ToDController tod, float sunY, float cloudiness, Vector3 sunDir, Vector3 moonDir, out float nightMultiplier)
        {
            var a = new TOD_AtmosphereParameters();
            float fogginess = Mathf.InverseLerp(0f, 0.4f, cloudiness) * 0.5f + Mathf.InverseLerp(0.1f, 0.4f, cloudiness) * 0.5f;
            a.Fogginess = Mathf.Clamp01(fogginess);
            a.RayleighMultiplier = Mathf.LerpUnclamped(tod.RayleighMultiplierMinMax.x, tod.RayleighMultiplierMinMax.y, Curve(tod.RayleighMultiplier, sunY));
            a.MieMultiplier = Mathf.LerpUnclamped(tod.MieMultiplierMinMax.x, tod.MieMultiplierMinMax.y, Curve(tod.MieMultiplier, sunY));
            a.Directionality = 1f - Curve(tod.Directionality, sunY) * tod.DirectionalityMult;
            a.Brightness = Mathf.LerpUnclamped(tod.BrightnessMinMax.x, tod.BrightnessMinMax.y, Curve(tod.Brightness, sunY));
            a.Brightness *= 1f - a.Fogginess * tod.BrightnessOvercast;
            a.ScatteringBrightness = Curve(tod.ScatteringBrightness, sunY) * tod.ScatteringBrightnessMultiplier * a.Brightness;
            // method_4 rewrites the controller's ContrastMinMax.y from the cloudiness before Update reads it
            float contrastMax = 1.5f - Mathf.Clamp(cloudiness, -1f, 0.4f) * 0.5f;
            a.Contrast = Mathf.LerpUnclamped(tod.ContrastMinMax.x, contrastMax, Curve(tod.Contrast, sunY));
            nightMultiplier = Mathf.LerpUnclamped(tod.MoonLightMinMax.x, tod.MoonLightMinMax.y, 0.5f - 0.5f * Vector3.Dot(moonDir, sunDir));
            return a;
        }

        public static float Curve(AnimationCurve curve, float t) => curve != null && curve.length > 0 ? curve.Evaluate(t) : 0f;

        // ------------------------------------------------------------ TOD_Sky's shader state

        // what TOD_Sky.UpdateShaderProperties sets, from a scene's TOD_Sky and the celestials
        public sealed class SkyState
        {
            public Vector3 SunDirection, MoonDirection;
            public Color SunSkyColor, MoonSkyColor, GroundColor, AmbientColor, MoonHaloColor, SunMeshColor, MoonMeshColor;
            public float Contrast, Brightness, ScatteringBrightness, Fogginess, MoonHaloPower;
            public float SpaceTiling, SpaceBrightness, SunMeshContrast, SunMeshBrightness, MoonMeshContrast, MoonMeshBrightness;
            public Vector4 KBetaMie, KSun, K4PI, KRadius, KScale;
            public float LerpValue, LightIntensity;
            public bool IsDay;

            // atmosphere: the sky's own values, or the weather's (WeatherAtmosphere); nightMultiplier: Night.ColorMultiplier
            public static SkyState From(TOD_Sky sky, Celestials c, TOD_AtmosphereParameters atmosphere = null, float? nightMultiplier = null)
            {
                atmosphere = atmosphere ?? sky.Atmosphere ?? new TOD_AtmosphereParameters();
                var day = sky.Day ?? new TOD_DayParameters();
                var night = sky.Night ?? new TOD_NightParameters();
                float nightMult = nightMultiplier ?? night.ColorMultiplier;
                var sun = sky.Sun ?? new TOD_SunParameters();
                var moon = sky.Moon ?? new TOD_MoonParameters();
                var stars = sky.Stars ?? new TOD_StarParameters();
                var s = new SkyState();

                // UpdateScattering
                float g = -atmosphere.Directionality, g2 = g * g;
                s.KBetaMie = new Vector4(1.5f * ((1f - g2) / (2f + g2)), 1f + g2, 2f * g, 0f);
                float mie = 0.002f * atmosphere.MieMultiplier, ray = 0.002f * atmosphere.RayleighMultiplier;
                s.KSun = new Vector4(ray * 40f * 5.2701645f, ray * 40f * 9.473284f, ray * 40f * 19.643803f, mie * 40f);
                s.K4PI = new Vector4(ray * 4f * Mathf.PI * 5.2701645f, ray * 4f * Mathf.PI * 9.473284f, ray * 4f * Mathf.PI * 19.643803f, mie * 4f * Mathf.PI);
                s.KRadius = new Vector4(1f, 1f, 1.025f, 1.050625f);
                s.KScale = new Vector4(40.00004f, 0.25f, 160.00015f, 0.0001f);

                // UpdateCelestials: colours
                float lerp = Mathf.InverseLerp(110f, 80f, c.SunZenith);
                float time = 1f - lerp;
                float clear = 1f - atmosphere.Fogginess;
                float moonVisible = moon.Position == TOD_MoonPositionType.Realistic
                    ? Mathf.Clamp01((90f - c.MoonZenith) / 5f) : Mathf.Clamp01((90f + c.MoonZenith) / 5f);
                s.LerpValue = lerp;
                s.SunSkyColor = MulRGB(Eval(day.SkyColor, time), day.ColorMultiplier);
                s.MoonSkyColor = MulRGB(Eval(night.SkyColor, time), 0.25f * nightMult);
                s.SunMeshColor = MulRGB(Eval(sun.MeshColor, time), day.ColorMultiplier);
                s.MoonMeshColor = MulRGB(Eval(moon.MeshColor, time), nightMult);
                s.GroundColor = Color.Lerp(MulRGB(Eval(night.AmbientColor, time), 0.25f * nightMult), MulRGB(Eval(day.AmbientColor, time), day.ColorMultiplier), lerp);
                s.AmbientColor = Color.Lerp(MulRGB(Eval(night.AmbientColor, time), 0.5f * nightMult), MulRGB(Eval(day.AmbientColor, time), day.ColorMultiplier), lerp);
                s.MoonHaloColor = MulRGB(Eval(moon.HaloColor, time), 0.25f * nightMult * moonVisible);
                // UpdateCelestials: the light's intensity, and which body it is
                s.IsDay = lerp > 0.1f;
                float sunShown = Mathf.Clamp01(clear * (lerp - 0.1f) / 0.9f);
                float moonShown = Mathf.Clamp01(clear * moonVisible);
                s.LightIntensity = s.IsDay ? Mathf.Lerp(0f, day.LightIntensity, sunShown) : Mathf.Lerp(0f, night.LightIntensity, moonShown);
                s.SunDirection = c.Sun;
                s.MoonDirection = c.Moon;

                // UpdateShaderProperties
                s.Contrast = atmosphere.Contrast;
                s.Brightness = atmosphere.Brightness;
                s.ScatteringBrightness = atmosphere.ScatteringBrightness;
                s.Fogginess = atmosphere.Fogginess;
                s.MoonHaloPower = 1f / Mathf.Max(0.0001f, moon.HaloSize);
                s.SpaceTiling = stars.Tiling;
                s.SpaceBrightness = stars.Brightness * clear * (1f - lerp);
                s.SunMeshContrast = 1f / Mathf.Max(0.001f, sun.MeshContrast);
                s.SunMeshBrightness = sun.MeshBrightness * clear;
                s.MoonMeshContrast = 1f / Mathf.Max(0.001f, moon.MeshContrast);
                s.MoonMeshBrightness = moon.MeshBrightness * clear;
                return s;
            }

            public void SetGlobals()
            {
                Shader.SetGlobalColor("TOD_SunSkyColor", SunSkyColor);
                Shader.SetGlobalColor("TOD_MoonSkyColor", MoonSkyColor);
                Shader.SetGlobalColor("TOD_SunMeshColor", SunMeshColor);
                Shader.SetGlobalColor("TOD_MoonMeshColor", MoonMeshColor);
                Shader.SetGlobalColor("TOD_GroundColor", GroundColor);
                Shader.SetGlobalColor("TOD_AmbientColor", AmbientColor);
                Shader.SetGlobalColor("TOD_MoonHaloColor", MoonHaloColor);
                Shader.SetGlobalVector("TOD_SunDirection", SunDirection);
                Shader.SetGlobalVector("TOD_MoonDirection", MoonDirection);
                Shader.SetGlobalVector("TOD_LightDirection", SunDirection);
                // the dome is not rotated in the editor, so local = world
                Shader.SetGlobalVector("TOD_LocalSunDirection", SunDirection);
                Shader.SetGlobalVector("TOD_LocalMoonDirection", MoonDirection);
                Shader.SetGlobalVector("TOD_LocalLightDirection", SunDirection);
                Shader.SetGlobalFloat("TOD_Contrast", Contrast);
                Shader.SetGlobalFloat("TOD_Brightness", Brightness);
                Shader.SetGlobalFloat("TOD_ScatteringBrightness", ScatteringBrightness);
                Shader.SetGlobalFloat("TOD_Fogginess", Fogginess);
                Shader.SetGlobalFloat("TOD_MoonHaloPower", MoonHaloPower);
                Shader.SetGlobalFloat("TOD_SpaceTiling", SpaceTiling);
                Shader.SetGlobalFloat("TOD_SpaceBrightness", SpaceBrightness);
                Shader.SetGlobalFloat("TOD_SunMeshContrast", SunMeshContrast);
                Shader.SetGlobalFloat("TOD_SunMeshBrightness", SunMeshBrightness);
                Shader.SetGlobalFloat("TOD_MoonMeshContrast", MoonMeshContrast);
                Shader.SetGlobalFloat("TOD_MoonMeshBrightness", MoonMeshBrightness);
                Shader.SetGlobalVector("TOD_kBetaMie", KBetaMie);
                Shader.SetGlobalVector("TOD_kSun", KSun);
                Shader.SetGlobalVector("TOD_k4PI", K4PI);
                Shader.SetGlobalVector("TOD_kRadius", KRadius);
                Shader.SetGlobalVector("TOD_kScale", KScale);
                Shader.SetGlobalMatrix("TOD_World2Sky", Matrix4x4.identity);
                Shader.SetGlobalMatrix("TOD_Sky2World", Matrix4x4.identity);
            }

            // TOD_Sky.ShaderScatteringColor, HDR2LDR and LINEAR2GAMMA: the sky colour in a direction, as
            // SampleAtmosphere returns it (used for the fog colour and the ambient samples)
            public Color SampleAtmosphere(Vector3 dir, bool directLight = true)
            {
                dir.y = Mathf.Clamp01(dir.y);
                float kr = KRadius.x, kr2 = KRadius.y, kR2 = KRadius.w;
                float kscale = KScale.x, scaleOverDepth = KScale.z, cameraHeight = KScale.w;
                var cameraPos = new Vector3(0f, kr + cameraHeight, 0f);
                float far = Mathf.Sqrt(kR2 + kr2 * dir.y * dir.y - kr2) - kr * dir.y;
                float startDepth = Mathf.Exp(scaleOverDepth * -cameraHeight);
                float startOffset = startDepth * Scale(Vector3.Dot(dir, cameraPos) / (kr + cameraHeight));
                float sampleLength = far / 2f;
                float scaledLength = sampleLength * kscale;
                var sampleRay = dir * sampleLength;
                var samplePoint = cameraPos + sampleRay * 0.5f;
                float fr = 0f, fg = 0f, fb = 0f;
                for (int i = 0; i < 2; i++)
                {
                    float height = samplePoint.magnitude, inv = 1f / height;
                    float depth = Mathf.Exp(scaleOverDepth * (kr - height));
                    float cameraAngle = Vector3.Dot(dir, samplePoint) * inv;
                    float lightAngle = Vector3.Dot(SunDirection, samplePoint) * inv;
                    float scatter = startOffset + depth * (Scale(lightAngle) - Scale(cameraAngle));
                    fr += Mathf.Exp(-scatter * (K4PI.x + K4PI.w)) * depth * scaledLength;
                    fg += Mathf.Exp(-scatter * (K4PI.y + K4PI.w)) * depth * scaledLength;
                    fb += Mathf.Exp(-scatter * (K4PI.z + K4PI.w)) * depth * scaledLength;
                    samplePoint += sampleRay;
                }
                float rr = SunSkyColor.r * fr * KSun.x, rg = SunSkyColor.g * fg * KSun.y, rb = SunSkyColor.b * fb * KSun.z;
                float mr = SunSkyColor.r * fr * KSun.w, mg = SunSkyColor.g * fg * KSun.w, mb = SunSkyColor.b * fb * KSun.w;
                float cosSun = Vector3.Dot(SunDirection, dir), cos2 = cosSun * cosSun;
                float rayleighPhase = 0.75f + 0.75f * cos2;
                float r = rayleighPhase * rr, g = rayleighPhase * rg, b = rayleighPhase * rb;
                if (directLight)
                {
                    float miePhase = KBetaMie.x * (1f + cos2) / Mathf.Pow(KBetaMie.y + KBetaMie.z * cosSun, 1.5f);
                    r += miePhase * mr; g += miePhase * mg; b += miePhase * mb;
                }
                var nightSky = Color.Lerp(MoonSkyColor, Color.black, dir.y);
                r += nightSky.r; g += nightSky.g; b += nightSky.b;
                if (directLight)
                {
                    var halo = MoonHaloColor * Mathf.Pow(Mathf.Max(0f, Vector3.Dot(dir, MoonDirection)), MoonHaloPower);
                    r += halo.r; g += halo.g; b += halo.b;
                }
                r = Mathf.Lerp(r, AmbientColor.r, 0.5f); g = Mathf.Lerp(g, AmbientColor.g, 0.5f); b = Mathf.Lerp(b, AmbientColor.b, 0.5f);
                r = Mathf.Pow(r * Brightness, Contrast); g = Mathf.Pow(g * Brightness, Contrast); b = Mathf.Pow(b * Brightness, Contrast);
                // TOD_HDR2LDR, then TOD_LINEAR2GAMMA
                r = 1f - Mathf.Pow(2f, -Brightness * r); g = 1f - Mathf.Pow(2f, -Brightness * g); b = 1f - Mathf.Pow(2f, -Brightness * b);
                return new Color(Mathf.Sqrt(Mathf.Max(0f, r)), Mathf.Sqrt(Mathf.Max(0f, g)), Mathf.Sqrt(Mathf.Max(0f, b)), 1f);
            }

            // TOD_Sky.SampleFogColor for a camera yaw (Fog.Mode Directional samples with the sun's light)
            public Color FogColor(Vector3 cameraForward, float heightBias, bool directLight)
            {
                var flat = Quaternion.Euler(0f, Quaternion.LookRotation(cameraForward == Vector3.zero ? Vector3.forward : cameraForward).eulerAngles.y, 0f) * Vector3.forward;
                var c = SampleAtmosphere(Vector3.Lerp(flat, Vector3.up, heightBias).normalized, directLight);
                return new Color(c.r, c.g, c.b, 1f);
            }

            private static float Scale(float cos)
            {
                float x = 1f - cos;
                return 0.25f * Mathf.Exp(-0.00287f + x * (0.459f + x * (3.83f + x * (-6.8f + x * 5.25f))));
            }

            private static Color Eval(Gradient g, float t) => g != null ? g.Evaluate(t) : Color.white;

            private static Color MulRGB(Color c, float m) => new Color(c.r * m, c.g * m, c.b * m, c.a);
        }

        // ------------------------------------------------------------ the globals

        // AmbientLight.SetSH
        public static void SetHarmonics(SphericalHarmonicsL2 sh)
        {
            Shader.SetGlobalVector("_SHC", new Vector4(sh[0, 8], sh[1, 8], sh[2, 8], 1f));
            string[] a = { "_SHAr", "_SHAg", "_SHAb" }, b = { "_SHBr", "_SHBg", "_SHBb" };
            for (int i = 0; i < 3; i++)
            {
                Shader.SetGlobalVector(a[i], new Vector4(sh[i, 3], sh[i, 1], sh[i, 2], sh[i, 0] - sh[i, 6]));
                Shader.SetGlobalVector(b[i], new Vector4(sh[i, 4], sh[i, 5], sh[i, 6] * 3f, sh[i, 7]));
            }
            var top = new Color[1];
            sh.Evaluate(new[] { Vector3.up }, top);
            Shader.SetGlobalVector("_EFT_Ambient", new Vector4(top[0].r, top[0].g, top[0].b, 0f));
        }

        // everything but the harmonics (TarkovLightingPreview sets those with the sun)
        public static void Apply(Vector3 toSun, SkyState sky, LevelSettings level, AmbientLight ambient, Camera sceneCamera, float scatteringDensity)
        {
            float blur = 1.54f, reflection = 0.2f, bottom = 0.857f;
            if (ambient != null)
            {
                blur = ambient.AmbientBlur;
                reflection = ambient.ReflectionIntensity;
                bottom = ambient.ReflectionBottomShade;
            }
            Shader.SetGlobalFloat("_AmbientBlur", blur);
            Shader.SetGlobalFloat("_ReflectionIntensity", reflection);
            Shader.SetGlobalFloat("_ReflectionBottomShade", 2f - bottom);
            Shader.SetGlobalVector("_SunDirection", toSun);
            Shader.SetGlobalVector("TOD_LightDirection", toSun);
            Shader.SetGlobalFloat("_NightVisionOn", 0f);
            Shader.SetGlobalFloat("_ThermalVisionOn", 0f);
            Shader.SetGlobalFloat("_HeatVisibleFactor", 0f);
            Shader.SetGlobalFloat("_HeatThermalFactor", 0f);
            if (level != null)
            {
                Shader.SetGlobalFloat("_DirectionLightShadow", level.DirectionLightShadowForUnshadowedShaders);
                Shader.SetGlobalFloat("_WaterLevel", level.LyingWater);
            }

            // TOD_Scattering: only maps with the full weather have it in the raid
            if (sky != null)
                sky.SetGlobals();
            else
                Shader.SetGlobalFloat("TOD_Fogginess", 0f);
            float cameraY = sceneCamera != null ? sceneCamera.transform.position.y : 0f;
            float heightFalloff = level != null ? level.HeightFalloff : 0.029f;
            float zeroLevel = level != null ? level.ZeroLevel : 0f;
            Shader.SetGlobalVector("_Density", new Vector4(heightFalloff, cameraY - zeroLevel, scatteringDensity, 0f));
            Shader.SetGlobalFloat("_TarkovScattering", sky != null ? 1f : 0f);

            Shader.SetGlobalTexture("_MyGlobalReflectionProbe", SkyCubemap());
        }

        // AmbientLight's cubemap of the sky: what RenderSettings.skybox draws, with mips for the roughness lookup
        private static RenderTexture SkyCubemap()
        {
            if (_skyCube == null)
            {
                _skyCube = new RenderTexture(CubeSize, CubeSize, 0, RenderTextureFormat.ARGBHalf)
                {
                    dimension = TextureDimension.Cube, useMipMap = true, autoGenerateMips = true, name = CubeName, hideFlags = HideFlags.HideAndDontSave,
                };
                _skyCube.Create();
            }
            if (_cubeCamera == null)
            {
                var go = EditorUtility.CreateGameObjectWithHideFlags(CubeCameraName, HideFlags.HideAndDontSave, typeof(Camera));
                _cubeCamera = go.GetComponent<Camera>();
                _cubeCamera.enabled = false;
                _cubeCamera.clearFlags = CameraClearFlags.Skybox;
                _cubeCamera.cullingMask = 0;
                _cubeCamera.allowHDR = true;
                _cubeCamera.nearClipPlane = 0.1f;
                _cubeCamera.farClipPlane = 10f;
            }
            try
            {
                _cubeCamera.RenderToCubemap(_skyCube);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Tarkov SDK] Sky reflection cubemap: " + e.Message);
            }
            return _skyCube;
        }
    }
}
