using System.Linq;
using EFT.Weather;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace TarkovSDK.Editor
{
    // Tools > Lighting > Lighting Editor: the values the raid lights the map with, in one place. Everything here
    // is a field of the scene's own components (TOD_Sky Day/Night, WeatherController's ToDController,
    // TODSkySimple, AmbientLight, LevelSettings), so the raid uses exactly what is set; the Scene view preview
    // (TarkovLightingPreview) shows it at the preview hour.
    //
    // The game's time-of-day curves are sampled by the sun's height: t = -1 (midnight) .. 1 (noon), or
    // t01 = t * 0.5 + 0.5 for some of them (the game's choice; the harmonics job reads Horizon/Forward and the
    // gradients by t01). The night and day sliders show a curve's value in the middle of that phase and scale
    // the keys of that phase only, so the shape of the curve (and the dusk and dawn) is kept.
    public class TarkovLightingEditorWindow : EditorWindow
    {
        private Vector2 _scroll;
        private bool _advanced;

        [MenuItem("Tarkov SDK/Tools/Lighting/Lighting Editor...", priority = 131)]
        public static void Open() => GetWindow<TarkovLightingEditorWindow>("Lighting Editor");

        // the readout follows the preview
        private void OnInspectorUpdate() => Repaint();

        private void OnGUI()
        {
            var scene = SceneManager.GetActiveScene();
            var full = TarkovWeatherBuilder.Find(scene);
            var sky = full != null ? full.GetComponentInChildren<TOD_Sky>(true) : null;
            var weather = full != null ? full.GetComponentInChildren<WeatherController>(true) : null;
            var simple = full == null ? TarkovLightingRig.Find(scene) : null;
            var ambient = full != null ? full.GetComponentInChildren<AmbientLight>(true) : simple != null ? simple.AmbientLight : null;
            var level = scene.GetRootGameObjects().Select(r => r.GetComponentInChildren<LevelSettings>(true)).FirstOrDefault(l => l != null);

            using var scroll = new EditorGUILayout.ScrollViewScope(_scroll);
            _scroll = scroll.scrollPosition;

            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
            TarkovTimeOfDayEditor.DrawPreviewControls();
            DrawReadout();

            if (sky == null && simple == null)
            {
                EditorGUILayout.HelpBox("The active scene has no lighting rig. Add one with Tarkov SDK > Generation > Time.", MessageType.Info);
                return;
            }

            EditorGUILayout.Space();
            if (sky != null)
                DrawFullWeather(sky, weather);
            else
                DrawSimpleRig(simple);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Reflections", EditorStyles.boldLabel);
            if (ambient != null)
            {
                Float(ambient, "Sky Reflection Intensity", "AmbientLight.ReflectionIntensity: how much of the sky every glossy surface reflects (Customs 0.2). " +
                      "With the game's SSR setting on it is always 1.", ref ambient.ReflectionIntensity, 0f, 1f);
                Float(ambient, "Reflection Bottom Shade", "AmbientLight.ReflectionBottomShade: how dark reflections pointing down get (Customs 0.857).", ref ambient.ReflectionBottomShade, 0f, 1f);
            }
            else
                EditorGUILayout.HelpBox("No AmbientLight on the rig: the raid has no ambient light or sky reflections.", MessageType.Warning);

            if (level != null)
                DrawUnityAmbient(level, sky != null);

            EditorGUILayout.Space();
            _advanced = EditorGUILayout.Foldout(_advanced, "All values", true);
            if (_advanced)
            {
                if (sky != null)
                {
                    DrawProperties(sky, "Day", "Night", "Sun", "Moon", "Light");
                    if (weather != null)
                        DrawProperties(weather, "TimeOfDayController");
                }
                if (simple != null)
                    DrawProperties(simple, "LightColorGradient", "AddTopAmbient", "AddAmbient", "CloudnessLightColor", "BounceHarmonicIntensity",
                                   "AmbientBrightness", "AmbientSaturation", "AmbientContrast", "HarmonicsDaylight");
                if (ambient != null)
                    DrawProperties(ambient, "AmbientBlur", "ReflectionIntensity", "ReflectionIntensitySSR", "ReflectionBottomShade");
            }
        }

        // ------------------------------------------------------------ sections

        private static void DrawReadout()
        {
            if (!TarkovLightingPreview.Enabled || !TarkovLightingPreview.HasHarmonics)
                return;
            var dirs = new[] { Vector3.up, Vector3.forward, Vector3.down };
            var colors = new Color[3];
            TarkovLightingPreview.LastHarmonics.Evaluate(dirs, colors);
            float t = TarkovLightingPreview.LastSunHeight;
            EditorGUILayout.LabelField("Sun height", $"{t:0.00} ({(t >= 0f ? "day" : "night")}; curves read t = {t:0.00}, t01 = {t * 0.5f + 0.5f:0.00})");
            Swatch("Ambient from above", colors[0]);
            Swatch("Ambient from the side", colors[1]);
            Swatch("Ambient from below", colors[2]);
            Swatch("Sun / moon light", TarkovLightingPreview.LastLight, linear: false);
        }

        // the game converts the harmonics to gamma before using them (TarkovGame.cginc), so that's what a surface gets
        private static void Swatch(string label, Color c, bool linear = true)
        {
            var shown = linear ? new Color(Mathf.LinearToGammaSpace(Mathf.Max(0f, c.r)), Mathf.LinearToGammaSpace(Mathf.Max(0f, c.g)), Mathf.LinearToGammaSpace(Mathf.Max(0f, c.b))) : c;
            var rect = EditorGUILayout.GetControlRect();
            var labelRect = new Rect(rect.x, rect.y, EditorGUIUtility.labelWidth, rect.height);
            EditorGUI.LabelField(labelRect, label);
            var swatch = new Rect(labelRect.xMax, rect.y + 1f, 40f, rect.height - 2f);
            EditorGUI.DrawRect(swatch, new Color(shown.r, shown.g, shown.b, 1f));
            EditorGUI.LabelField(new Rect(swatch.xMax + 6f, rect.y, rect.width - swatch.xMax, rect.height),
                $"{shown.maxColorComponent * 100f:0}% of white on a white surface");
        }

        private static void DrawFullWeather(TOD_Sky sky, WeatherController weather)
        {
            var tod = weather != null ? weather.TimeOfDayController : null;

            EditorGUILayout.LabelField("Sun and moon", EditorStyles.boldLabel);
            if (sky.Day != null)
            {
                Float(sky, "Sunlight Intensity", "TOD_Sky Day.LightIntensity (Customs 1).", ref sky.Day.LightIntensity, 0f, 3f);
                Float(sky, "Sun Shadow Strength", "TOD_Sky Day.ShadowStrength.", ref sky.Day.ShadowStrength, 0f, 1f);
            }
            if (sky.Night != null)
            {
                Float(sky, "Moonlight Intensity", "TOD_Sky Night.LightIntensity: the moon's directional light. It also lights glossy surfaces (their highlights).", ref sky.Night.LightIntensity, 0f, 1f);
                Float(sky, "Moon Shadow Strength", "TOD_Sky Night.ShadowStrength.", ref sky.Night.ShadowStrength, 0f, 1f);
            }
            if (tod != null)
            {
                EditorGUI.BeginChangeCheck();
                var moon = EditorGUILayout.Vector2Field(new GUIContent("Night Sky by Moon Phase", "ToDController.MoonLightMinMax: the night sky's brightness at new moon (x) and full moon (y). " +
                    "The night sky also lights the map (ambient from above and the side). The game overwrites Night.ColorMultiplier with it."), tod.MoonLightMinMax);
                if (EditorGUI.EndChangeCheck())
                {
                    Record(weather);
                    tod.MoonLightMinMax = new Vector2(Mathf.Max(0f, moon.x), Mathf.Max(0f, moon.y));
                    Changed(weather);
                }
            }

            if (tod == null)
            {
                EditorGUILayout.HelpBox("The weather has no ToDController (WeatherController.TimeOfDayController), so the raid computes no time-of-day ambient.", MessageType.Warning);
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Ambient light at night", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Ambient light reaches every surface, in shadow too. Ambient Brightness is the main control; the rest set where it comes from.", MessageType.None);
            Phase(weather, tod.AmbientBrightness, "Brightness", "ToDController.AmbientBrightness at night (Customs about 0.11).", true, false, 0f, 0.5f);
            Phase(weather, tod.AmbientSaturation, "Saturation", "ToDController.AmbientSaturation at night: 0 grey, 1 the sky's colour (Customs about 0.5).", true, false, 0f, 2f);
            Phase(weather, tod.AmbientContrast, "Contrast", "ToDController.AmbientContrast at night. Below 1 lifts dark colours towards grey (Customs 1.15).", true, false, 0f, 2f);
            Phase(weather, tod.TopHarmonicIntensity, "From the Sky Above", "ToDController.TopHarmonicIntensity at night: the sky straight up.", true, false, 0f, 4f);
            Phase(weather, tod.HorizontHarmonicIntensity, "From the Horizon", "ToDController.HorizontHarmonicIntensity at night: the sky around the horizon.", true, true, 0f, 4f);
            PhaseColor(weather, tod.AddTopAmbient, "Added From Above", "ToDController.AddTopAmbient at night: a colour added from straight up (before Brightness).", true);
            PhaseColor(weather, tod.AddAmbient, "Added Everywhere", "ToDController.AddAmbient at night: a colour added from every direction (before Brightness).", true);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Ambient light by day", EditorStyles.boldLabel);
            Phase(weather, tod.AmbientBrightness, "Brightness", "ToDController.AmbientBrightness by day (Customs about 0.1).", false, false, 0f, 0.5f);
            Phase(weather, tod.AmbientSaturation, "Saturation", "ToDController.AmbientSaturation by day.", false, false, 0f, 2f);
            Phase(weather, tod.TopHarmonicIntensity, "From the Sky Above", "ToDController.TopHarmonicIntensity by day.", false, false, 0f, 4f);
            Phase(weather, tod.HorizontHarmonicIntensity, "From the Horizon", "ToDController.HorizontHarmonicIntensity by day.", false, true, 0f, 4f);
            Phase(weather, tod.BounceHarmonicIntensity, "Sunlight Bounce", "ToDController.BounceHarmonicIntensity by day: sunlight bounced up from the ground.", false, false, 0f, 1f);
        }

        private static void DrawSimpleRig(TODSkySimple simple)
        {
            EditorGUILayout.LabelField("Ambient light at night", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("The simple rig has no moon: at night only ambient light (and the map's lamps) light the map.", MessageType.None);
            Phase(simple, simple.AmbientBrightness, "Brightness", "TODSkySimple.AmbientBrightness at night.", true, false, 0f, 2f);
            Phase(simple, simple.AmbientSaturation, "Saturation", "TODSkySimple.AmbientSaturation at night.", true, false, 0f, 2f);
            Phase(simple, simple.AmbientContrast, "Contrast", "TODSkySimple.AmbientContrast at night. Below 1 lifts dark colours towards grey.", true, false, 0f, 2f);
            PhaseColor(simple, simple.AddTopAmbient, "Added From Above", "TODSkySimple.AddTopAmbient at night.", true);
            PhaseColor(simple, simple.AddAmbient, "Added Everywhere", "TODSkySimple.AddAmbient at night.", true);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("By day", EditorStyles.boldLabel);
            Phase(simple, simple.AmbientBrightness, "Brightness", "TODSkySimple.AmbientBrightness by day.", false, false, 0f, 2f);
            Phase(simple, simple.AmbientSaturation, "Saturation", "TODSkySimple.AmbientSaturation by day.", false, false, 0f, 2f);
            Phase(simple, simple.BounceHarmonicIntensity, "Sunlight Bounce", "TODSkySimple.BounceHarmonicIntensity by day.", false, false, 0f, 1f);
            PhaseColor(simple, simple.LightColorGradient, "Sunlight", "TODSkySimple.LightColorGradient by day: the sun's colour and brightness.", false);
            PhaseColor(simple, simple.AddTopAmbient, "Added From Above", "TODSkySimple.AddTopAmbient by day.", false);
        }

        private static void DrawUnityAmbient(LevelSettings level, bool fullWeather)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Unity ambient (LevelSettings)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Added to every surface day and night, on top of the time of day's ambient. " +
                                    (fullWeather ? "Customs keeps it Flat, black, intensity 0." : "Factory: Flat, Sky Color 0.03 at intensity 0.6."), MessageType.None);
            EditorGUI.BeginChangeCheck();
            var mode = (AmbientMode)EditorGUILayout.EnumPopup(new GUIContent("Mode", "Flat is the only mode retail maps use."), level.AmbientMode);
            var color = EditorGUILayout.ColorField(new GUIContent("Sky Color"), level.SkyColor, true, false, false);
            float intensity = EditorGUILayout.Slider(new GUIContent("Intensity"), level.AmbientIntensity, 0f, 2f);
            float unshadowed = EditorGUILayout.Slider(new GUIContent("Sun On Unshadowed Shaders", "LevelSettings.DirectionLightShadowForUnshadowedShaders (Customs 0, Factory 0.832)."),
                level.DirectionLightShadowForUnshadowedShaders, 0f, 1f);
            if (EditorGUI.EndChangeCheck())
            {
                Record(level);
                level.AmbientMode = mode;
                level.SkyColor = color;
                level.AmbientIntensity = intensity;
                level.DirectionLightShadowForUnshadowedShaders = unshadowed;
                Changed(level);
            }
            if (level.AmbientMode != AmbientMode.Flat || level.SkyColor.maxColorComponent * level.AmbientIntensity > (fullWeather ? 0f : TarkovQuickFixes.SimpleRigAmbientLimit))
                if (GUILayout.Button(fullWeather ? "Use Customs' values" : "Use Factory's values"))
                {
                    Record(level);
                    level.AmbientMode = AmbientMode.Flat;
                    level.SkyColor = fullWeather ? Color.black : new Color(0.03f, 0.03f, 0.03f);
                    level.AmbientIntensity = fullWeather ? 0f : 0.6f;
                    Changed(level);
                }
        }

        private static void DrawProperties(Object target, params string[] names)
        {
            var so = new SerializedObject(target);
            so.Update();
            EditorGUILayout.LabelField(target.GetType().Name, EditorStyles.miniBoldLabel);
            foreach (var name in names)
            {
                var property = so.FindProperty(name);
                if (property != null)
                    EditorGUILayout.PropertyField(property, true);
            }
            if (so.ApplyModifiedProperties())
                TarkovLightingPreview.Refresh();
        }

        // ------------------------------------------------------------ editing helpers

        private static void Float(Object owner, string label, string tip, ref float value, float min, float max)
        {
            EditorGUI.BeginChangeCheck();
            float v = EditorGUILayout.Slider(new GUIContent(label, tip), value, min, max);
            if (!EditorGUI.EndChangeCheck())
                return;
            Record(owner);
            value = v;
            Changed(owner);
        }

        // a curve over one phase (sun below or above the horizon): shows its value in the middle of the phase
        // and scales that phase's keys (value and tangents) to the new value
        private static void Phase(Object owner, AnimationCurve curve, string label, string tip, bool night, bool byT01, float min, float max)
        {
            if (curve == null)
                return;
            float middle = night ? -0.5f : 0.5f;
            if (byT01)
                middle = middle * 0.5f + 0.5f;
            float split = byT01 ? 0.5f : 0f;
            float current = curve.length > 0 ? curve.Evaluate(middle) : 0f;
            EditorGUI.BeginChangeCheck();
            float v = EditorGUILayout.Slider(new GUIContent(label, tip), current, min, Mathf.Max(max, current));
            if (!EditorGUI.EndChangeCheck())
                return;
            Record(owner);
            var keys = curve.keys;
            bool any = false;
            for (int i = 0; i < keys.Length; i++)
            {
                if (night ? keys[i].time >= split : keys[i].time < split)
                    continue;
                any = true;
                if (Mathf.Abs(current) > 1e-5f)
                {
                    float k = v / current;
                    keys[i].value *= k;
                    keys[i].inTangent *= k;
                    keys[i].outTangent *= k;
                }
                else
                    keys[i].value = v;
            }
            if (any)
                curve.keys = keys;
            else
                curve.AddKey(middle, v);
            Changed(owner);
        }

        // a gradient read by t01 over one phase: shows its colour in the middle of the phase and scales that
        // phase's colour keys per channel to the new colour
        private static void PhaseColor(Object owner, Gradient gradient, string label, string tip, bool night)
        {
            if (gradient == null)
                return;
            float middle = night ? 0.25f : 0.75f;
            var current = gradient.Evaluate(middle);
            EditorGUI.BeginChangeCheck();
            var picked = EditorGUILayout.ColorField(new GUIContent(label, tip), current, true, false, false);
            if (!EditorGUI.EndChangeCheck())
                return;
            Record(owner);
            var keys = gradient.colorKeys;
            bool any = false;
            for (int i = 0; i < keys.Length; i++)
            {
                if (night ? keys[i].time >= 0.5f : keys[i].time < 0.5f)
                    continue;
                any = true;
                var c = keys[i].color;
                keys[i].color = new Color(Scale(c.r, current.r, picked.r), Scale(c.g, current.g, picked.g), Scale(c.b, current.b, picked.b), c.a);
            }
            if (!any)
            {
                keys = keys.Append(new GradientColorKey(picked, middle)).OrderBy(k => k.time).ToArray();
                if (keys.Length > 8)
                    return; // Unity's limit
            }
            gradient.SetKeys(keys, gradient.alphaKeys);
            Changed(owner);
        }

        private static float Scale(float value, float from, float to) => from > 1e-4f ? value * to / from : to;

        private static void Record(Object owner) => Undo.RecordObject(owner, "Lighting editor");

        private static void Changed(Object owner)
        {
            EditorUtility.SetDirty(owner);
            TarkovLightingPreview.Refresh();
        }
    }
}
