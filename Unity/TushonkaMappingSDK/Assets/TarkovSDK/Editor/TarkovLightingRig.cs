using EFT.Weather;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // The sky and lighting setup Factory uses (Factory_Rework_Day_Scripts, "Weather_Factory"):
    //
    //   Weather                  TODSkySimple, TOD_Time, TodLightSetter
    //     Sun                    Light (directional; TODSkySimple reads the sun height from its rotation)
    //     AmbientManager         AmbientLight
    //     OnRenderObjectManager  OnRenderObjectManager (Factory keeps it under TechnicalManagers)
    //
    // BSG's shaders take ambient light only from the _SHAr/_SHBr/_SHC/_EFT_Ambient globals that
    // AmbientLight.SetSH writes, and TODSkySimple calls SetSH every frame. Without this rig every
    // BSG-shaded surface (player arms, gear, game props) renders black whatever LevelSettings says.
    // OnRenderObjectManager hands each game camera to AmbientLight so it can add its ambient pass.
    //
    // AmbientLight's five shader fields stay empty: the shaders are BSG's (Hidden/WriteDepth etc.),
    // so the client plugin fills them in by name when the scene loads.
    //
    // Values are Factory's, read from the game; Factory is lit dimly, so raise AmbientBrightness or
    // the sun intensity for brighter maps.
    public static class TarkovLightingRig
    {
        // added from Tarkov SDK > Generation > Time > Time of Day / Weather... (simple mode)
        public static TODSkySimple Find(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                var sky = root.GetComponentInChildren<TODSkySimple>(true);
                if (sky != null)
                    return sky;
            }
            return null;
        }

        // Adds the rig to the scene and returns its root. An existing directional light becomes the sun.
        public static GameObject Create(Scene scene)
        {
            var weather = new GameObject("Weather");
            SceneManager.MoveGameObjectToScene(weather, scene);

            var sun = ExistingSun(scene);
            if (sun == null)
            {
                var go = new GameObject("Sun");
                go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                sun = go.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.color = new Color(1f, 0.796f, 0.6f);
                sun.intensity = 1.2f;
                sun.shadows = LightShadows.Soft;
                sun.shadowStrength = 0.97f;
                sun.shadowBias = 0.02f;
                sun.shadowNormalBias = 0f;
            }
            sun.transform.SetParent(weather.transform, true);

            var ambientGo = new GameObject("AmbientManager");
            ambientGo.transform.SetParent(weather.transform, false);
            var ambient = ambientGo.AddComponent<AmbientLight>();
            ambient.AmbientBlur = 1.54f;
            ambient.ReflectionIntensity = 0.2f;
            ambient.ReflectionIntensitySSR = 1f;
            ambient.RenderDelay = 1f;
            ambient.QubemapResolution = 128;
            ambient.CullingMask = LayerMaskOf("Sky");
            ambient.ReflectionBottomShade = 0.857f;
            ambient._reflectionWettingFunc = new AnimationCurve(new Keyframe(0f, 1f, 0f, 0f), new Keyframe(0.998f, 0.606f, 0f, 0f));

            var time = weather.AddComponent<TOD_Time>();
            time.DayLengthInMinutes = 3000f;
            time.TimeCurve = AnimationCurve.Linear(0f, 0f, 24f, 24f);

            var setter = weather.AddComponent<TodLightSetter>();
            setter._forceIndoorIntensity = 1f;
            setter._sunVisibilityCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

            var sky = weather.AddComponent<TODSkySimple>();
            sky.LightSource = sun;
            sky.AmbientLight = ambient;
            sky.LightColorGradient = Gradient(
                (0f, 0.809f, 0.881f, 1f), (0.5115f, 0f, 0f, 0f), (0.5267f, 1f, 0.457f, 0.322f),
                (0.5536f, 0.859f, 0.631f, 0.392f), (0.6972f, 1f, 0.867f, 0.537f), (0.9993f, 0.585f, 0.530f, 0.361f));
            sky.AddTopAmbient = Gradient(
                (0f, 0.185f, 0.305f, 0.404f), (0.2696f, 0.075f, 0.082f, 0.090f), (0.4249f, 0.078f, 0.064f, 0.059f),
                (0.4919f, 0.184f, 0.123f, 0.112f), (0.5608f, 0.185f, 0.305f, 0.404f), (0.642f, 0.228f, 0.372f, 0.468f),
                (0.8195f, 0.191f, 0.440f, 0.638f), (1f, 0.234f, 0.455f, 0.629f));
            sky.AddAmbient = Gradient(
                (0.0007f, 0f, 0f, 0f), (0.3824f, 0.066f, 0.074f, 0.078f), (0.4673f, 0.097f, 0.065f, 0.065f),
                (0.5456f, 0.043f, 0.055f, 0.064f), (0.716f, 0f, 0f, 0f), (0.9026f, 0.042f, 0.042f, 0.042f), (1f, 0f, 0f, 0f));
            sky.CloudnessLightColor = new Color(0.090f, 0.095f, 0.104f, 0f);
            // curves are sampled by the sun height, -1 (below the horizon) to 1 (overhead)
            sky.BounceHarmonicIntensity = new AnimationCurve(
                new Keyframe(-1.01f, 0.0005f, 0.009f, 0.009f), new Keyframe(-0.464f, 0.0032f, 0.034f, 0.034f),
                new Keyframe(0.024f, 0.020f, 0.055f, 0.055f), new Keyframe(0.975f, 0.171f, 0.019f, 0.019f));
            sky.AmbientBrightness = new AnimationCurve(
                new Keyframe(-1.016f, 0.111f, 0f, 0f), new Keyframe(-0.242f, 0.115f, 0.033f, 0.033f),
                new Keyframe(0.249f, 0.136f, 0.003f, 0.003f), new Keyframe(1.009f, 0.065f, -0.203f, -0.203f));
            sky.AmbientSaturation = new AnimationCurve(
                new Keyframe(-0.942f, 0.497f, 0f, 0f), new Keyframe(0.026f, 0.561f, 0.004f, 0.004f), new Keyframe(1.001f, 0.502f, 0f, 0f));
            sky.AmbientContrast = new AnimationCurve(
                new Keyframe(-1.003f, 1.15f, 0f, 0f), new Keyframe(1.009f, 1.15f, 0f, 0f));
            sky.HarmonicsDaylight = 0f;
            sky.Latitude = 46f;
            sky.Longitude = 84f;
            sky.UTC = 7.24f;
            sky.Cycle = new TOD_CycleParameters { Hour = 9.4f, Day = 1, Month = 8, Year = 2018 };

            var managers = new GameObject("OnRenderObjectManager");
            managers.transform.SetParent(weather.transform, false);
            managers.AddComponent<OnRenderObjectManager>();

            return weather;
        }

        // Unity's procedural sky, bundled with the map (retail maps use their own cubemap skyboxes,
        // which the SDK cannot reference); LevelSettings.skybox sets it in game
        public static Material DefaultSkybox()
        {
            const string path = "Assets/TarkovSDK/Materials/SDK_Skybox.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null)
                return material;
            var shader = Shader.Find("Skybox/Procedural");
            if (shader == null)
                return null;
            System.IO.Directory.CreateDirectory("Assets/TarkovSDK/Materials");
            material = new Material(shader) { name = "SDK_Skybox" };
            material.SetFloat("_SunSize", 0.03f);
            material.SetFloat("_AtmosphereThickness", 1f);
            material.SetColor("_SkyTint", new Color(0.5f, 0.5f, 0.5f));
            material.SetColor("_GroundColor", new Color(0.37f, 0.35f, 0.34f));
            material.SetFloat("_Exposure", 1f);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        private static Light ExistingSun(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var light in root.GetComponentsInChildren<Light>(true))
                    if (light.type == LightType.Directional)
                        return light;
            return null;
        }

        private static LayerMask LayerMaskOf(string layer)
        {
            int index = LayerMask.NameToLayer(layer);
            return index < 0 ? (LayerMask)0 : (LayerMask)(1 << index);
        }

        private static Gradient Gradient(params (float Time, float R, float G, float B)[] keys)
        {
            var colors = new GradientColorKey[keys.Length];
            for (int i = 0; i < keys.Length; i++)
                colors[i] = new GradientColorKey(new Color(keys[i].R, keys[i].G, keys[i].B), keys[i].Time);
            var gradient = new Gradient();
            gradient.SetKeys(colors, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return gradient;
        }
    }
}
