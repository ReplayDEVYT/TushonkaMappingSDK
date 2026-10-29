using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Prism.Utils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // The Asset Importer's Data kind: a retail map's lighting and its camera's PRISM post-processing.
    //  - Lighting: the map's LevelSettings, TOD_Sky (full weather) or TODSkySimple (simple rig), WeatherController and
    //    AmbientLight are read from its scene into hidden stub components, then their values (numbers, colours, curves,
    //    gradients; never object references, so materials, lights and cubemaps stay the scene's own) go onto the open
    //    scenes' components of the same class, with Undo. LevelSettings and WeatherController give only their lighting
    //    fields; TOD cycles keep their hour and date (the preview's and the raid's).
    //  - PRISM: retail maps keep their post-processing on the PrismEffects of LevelSettings' CameraPrefab, not in a
    //    PrismPreset (every retail LevelSettings' PrismPresetPrefab is empty), so a PrismPreset asset is made from it by
    //    the reverse of PrismEffects.SetPrismPreset's field copy, with its lens dirt and LUT textures imported, and can
    //    go on the scene's LevelSettings.
    public static class TarkovGamePresets
    {
        // PrismEffects.SetPrismPreset: effects field = preset field
        private static readonly (string Effects, string Preset)[] PrismFields =
        {
            ("useBloom", "useBloom"), ("bloomType", "bloomType"), ("bloomDownsample", "bloomDownsample"), ("bloomBlurPasses", "bloomBlurPasses"),
            ("bloomIntensity", "bloomIntensity"), ("bloomThreshold", "bloomThreshold"), ("useBloomStability", "useBloomStability"),
            ("bloomUseScreenBlend", "bloomUseScreenBlend"), ("useLensDirt", "useBloomLensdirt"), ("dirtIntensity", "bloomLensdirtIntensity"),
            ("lensDirtTexture", "bloomLensdirtTexture"), ("useFullScreenBlur", "useFullScreenBlur"), ("useUIBlur", "useUIBlur"),
            ("uiBlurGrabTextureFromPassNumber", "uiBlurGrabTextureFromPassNumber"), ("useDof", "useDoF"), ("dofRadius", "dofRadius"),
            ("dofSampleAmount", "dofSampleCount"), ("dofBokehFactor", "dofBokehFactor"), ("dofFocusPoint", "dofFocusPoint"),
            ("dofFocusDistance", "dofFocusDistance"), ("useNearDofBlur", "useNearBlur"), ("dofBlurSkybox", "dofBlurSkybox"),
            ("dofNearFocusDistance", "dofNearFocusDistance"), ("dofForceEnableMedian", "dofForceEnableMedian"),
            ("useChromaticAberration", "useChromaticAb"), ("aberrationType", "aberrationType"), ("chromaticIntensity", "chromIntensity"),
            ("chromaticDistanceOne", "chromStart"), ("chromaticDistanceTwo", "chromEnd"), ("useChromaticBlur", "useChromaticBlur"),
            ("chromaticBlurWidth", "chromaticBlurWidth"), ("useVignette", "useVignette"), ("vignetteStrength", "vignetteIntensity"),
            ("vignetteEnd", "vignetteEnd"), ("vignetteStart", "vignetteStart"), ("vignetteColor", "vignetteColor"), ("useNoise", "useNoise"),
            ("noiseIntensity", "noiseIntensity"), ("useTonemap", "useTonemap"), ("tonemapType", "toneType"), ("toneValues", "toneValues"),
            ("secondaryToneValues", "secondaryToneValues"), ("useExposure", "useExposure"), ("exposureMiddleGrey", "exposureMiddleGrey"),
            ("exposureLowerLimit", "exposureLowerLimit"), ("exposureUpperLimit", "exposureUpperLimit"), ("exposureSpeed", "exposureSpeed"),
            ("useGammaCorrection", "useGammaCorrection"), ("gammaValue", "gammaValue"), ("useLut", "useLUT"), ("lutLerpAmount", "lutIntensity"),
            ("basedOnTempTex", "lutPath"), ("twoDLookupTex", "twoDLookupTex"), ("useSecondLut", "useSecondLut"),
            ("secondaryLutLerpAmount", "secondaryLutLerpAmount"), ("secondaryBasedOnTempTex", "secondaryLutPath"),
            ("secondaryTwoDLookupTex", "secondaryTwoDLookupTex"), ("useNightVision", "useNV"), ("m_NVColor", "nvColor"),
            ("m_TargetBleachColor", "nvBleachColor"), ("m_baseLightingContribution", "nvLightingContrib"),
            ("m_LightSensitivityMultiplier", "nvLightSensitivity"), ("useFog", "useFog"), ("fogIntensity", "fogIntensity"),
            ("fogStartPoint", "fogStartPoint"), ("fogDistance", "fogDistance"), ("fogColor", "fogColor"), ("fogEndColor", "fogEndColor"),
            ("fogHeight", "fogHeight"), ("useAmbientObscurance", "useAmbientObscurance"), ("useAODistanceCutoff", "useAODistanceCutoff"),
            ("aoIntensity", "aoIntensity"), ("aoRadius", "aoRadius"), ("aoDistanceCutoffStart", "aoDistanceCutoffStart"),
            ("aoDownsample", "aoDownsample"), ("aoBlurIterations", "aoBlurIterations"), ("aoDistanceCutoffLength", "aoDistanceCutoffLength"),
            ("aoSampleCount", "aoSampleCount"), ("aoBias", "aoBias"), ("aoBlurFilterDistance", "aoBlurFilterDistance"), ("aoBlurType", "aoBlurType"),
            ("aoLightingContribution", "aoLightingContribution"), ("useRays", "useRays"), ("rayWeight", "rayWeight"), ("rayColor", "rayColor"),
            ("rayThreshold", "rayThreshold"),
        };

        // LevelSettings' lighting (not its camera, presets, rain bounds, north or texture budget)
        private static readonly HashSet<string> LevelLighting = new HashSet<string>
        {
            "AmbientMode", "SkyColor", "EquatorColor", "GroundColor", "AmbientIntensity", "NightVisionSkyColor", "NightVisionEquatorColor",
            "NightVisionGroundColor", "NightVisionAmbientIntensity", "_minSmokeAmbientColor", "SSRFactor", "fog", "fogColor", "fogDensity",
            "fogEndDistance", "fogMode", "fogStartDistance", "SunColor", "SunScratchesIntensity", "SunShaftsIntensity", "SunShaftsDensity",
            "SunLensFlares", "SunBackGroung", "DirectionLightShadowForUnshadowedShaders", "HeightFalloff",
        };

        // WeatherController's lighting: the time of day controller and its reflection and fog multipliers
        private static readonly HashSet<string> WeatherLighting = new HashSet<string> { "TimeOfDayController", "CubemapDayMult", "CubemapNightMult", "FogMultyplyer" };

        // the raid's own: its clock (in TOD cycles only: TOD_Sky also has a Day) and Labs' forced indoor light (Entirely
        // Indoors on the Map Definition)
        private static readonly HashSet<string> Clock = new HashSet<string> { "Hour", "Day", "Month", "Year" };
        private static readonly HashSet<string> NeverCopied = new HashSet<string> { "_forceIndoor", "_forceIndoorIntensity" };

        // ---- reading a game component

        private const string HostName = "TarkovSDK_PresetRead";

        // the component's values from the game, on a hidden stub component (destroy its GameObject after)
        private static Component Read(GameFiles files, GameSerializedFile file, long pathId, Type type, Func<GamePPtr, Type, object> resolve, out string error)
        {
            error = null;
            var host = new GameObject(HostName) { hideFlags = HideFlags.HideAndDontSave };
            host.SetActive(false);
            var component = host.AddComponent(type);
            var reader = new GameScriptReader { Resolve = (p, t) => resolve?.Invoke(p, t) };
            if (!file.ReadScript(pathId, out _, (r, size) => reader.Read(r, size, type, component)))
            {
                error = reader.Error ?? "it didn't read";
                Object.DestroyImmediate(host);
                return null;
            }
            return component;
        }

        private static IEnumerable<(long PathId, string Script)> Scripts(GameFiles files, GameSerializedFile file)
        {
            foreach (var info in file.ObjectList)
                if (info.ClassId == 114 && file.ScriptHeader(info.PathId, out _, out _, out var script))
                    yield return (info.PathId, files.Script(file, script).Name);
        }

        // ---- lighting

        public static string ApplyLighting(GameIndexEntry entry, out bool applied)
        {
            applied = false;
            var files = TarkovGameData.Files;
            var file = files?.Get(entry.Asset);
            if (file == null)
                return $"{entry.Asset} didn't read";
            var done = new List<string>();
            var missing = new List<string>();
            var notes = new List<string>();
            var seen = new HashSet<string>();
            foreach (var (pathId, script) in Scripts(files, file).ToList())
            {
                if (!GameIndex.LightingScripts.Contains(script) || !seen.Add(script))
                    continue;
                var type = TarkovGameData.ScriptType(script);
                var label = script.Split('.').Last();
                if (type == null)
                    continue;
                var targets = Object.FindObjectsOfType(type, true).OfType<Component>().Where(c => !EditorUtility.IsPersistent(c) && c.gameObject.hideFlags == HideFlags.None).ToList();
                if (targets.Count == 0)
                {
                    missing.Add(label);
                    continue;
                }
                var source = Read(files, file, pathId, type, null, out var error);
                if (source == null)
                {
                    notes.Add($"{label} didn't read ({error})");
                    continue;
                }
                var only = script == "LevelSettings" ? LevelLighting : script == "EFT.Weather.WeatherController" ? WeatherLighting : null;
                foreach (var target in targets)
                {
                    Undo.RecordObject(target, "Apply retail lighting");
                    Copy(source, target, type, only, 0);
                    EditorUtility.SetDirty(target);
                    EditorSceneManager.MarkSceneDirty(target.gameObject.scene);
                }
                Object.DestroyImmediate(source.gameObject);
                done.Add(label);
            }
            applied = done.Count > 0;
            var text = done.Count > 0 ? $"Applied the lighting of {entry.Name.Replace("Lighting: ", "")} ({string.Join(", ", done)}) to the open scenes (Undo reverts it)." : "Nothing to apply it to.";
            if (missing.Count > 0)
                text += $" The open scenes have no {string.Join(" or ", missing)}" +
                        (missing.Contains("TOD_Sky") || missing.Contains("TODSkySimple") ? " (that map uses the other time of day rig)" : "") + ", so that part was left out.";
            if (notes.Count > 0)
                text += " " + string.Join("; ", notes) + ".";
            return text;
        }

        private static bool IsReference(Type t) =>
            typeof(Object).IsAssignableFrom(t) ||
            t.IsArray && IsReference(t.GetElementType()) ||
            t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) && IsReference(t.GetGenericArguments()[0]);

        // the values of from onto to: plain values and Unity structs as they are, curves and gradients copied, nested
        // serializable classes field by field (so references in them stay to's), object references never
        private static void Copy(object from, object to, Type type, HashSet<string> only, int depth)
        {
            if (from == null || to == null || depth > 10)
                return;
            foreach (var field in GameScriptReader.Fields(type))
            {
                string name = field.Name.StartsWith("<") ? field.Name.Substring(1, field.Name.IndexOf('>') - 1) : field.Name;
                if (only != null && !only.Contains(name) || NeverCopied.Contains(name) || IsReference(field.FieldType) ||
                    type.Name == "TOD_CycleParameters" && Clock.Contains(name))
                    continue;
                var value = field.GetValue(from);
                var t = field.FieldType;
                if (value is AnimationCurve curve)
                    field.SetValue(to, new AnimationCurve(curve.keys) { preWrapMode = curve.preWrapMode, postWrapMode = curve.postWrapMode });
                else if (value is Gradient gradient)
                    field.SetValue(to, new Gradient { colorKeys = gradient.colorKeys, alphaKeys = gradient.alphaKeys, mode = gradient.mode });
                else if (t.IsClass && t != typeof(string) && !t.IsArray && !(value is IList) && value != null)
                {
                    // into the target's own object (a new one if it has none), so what isn't copied stays its own
                    var existing = field.GetValue(to);
                    if (existing == null && t.GetConstructor(Type.EmptyTypes) != null)
                    {
                        existing = Activator.CreateInstance(t);
                        field.SetValue(to, existing);
                    }
                    if (existing != null)
                        Copy(value, existing, t, null, depth + 1);
                }
                else if (value is Array array)
                    field.SetValue(to, array.Clone());
                else
                    field.SetValue(to, value);
            }
        }

        // ---- PRISM

        public static string PresetPathOf(GameIndexEntry entry) =>
            GameAssetStore.PathOf(entry.Asset, "Presets", entry.Name.Replace("PRISM: ", "") + "_PRISM", entry.AssetId, ".asset");

        // the camera's PrismEffects as a PrismPreset asset (made once, then reused)
        public static PrismPreset ImportPrism(GameIndexEntry entry, GameAssetStore store, out string error)
        {
            error = null;
            var path = PresetPathOf(entry);
            var existing = AssetDatabase.LoadAssetAtPath<PrismPreset>(path);
            if (existing != null)
                return existing;
            var files = TarkovGameData.Files;
            var file = files?.Get(entry.Asset);
            var type = TarkovGameData.ScriptType("PrismEffects");
            if (file == null || type == null)
            {
                error = file == null ? $"{entry.Asset} didn't read" : "the SDK has no PrismEffects stub";
                return null;
            }
            var effects = Read(files, file, entry.AssetId, type, (p, t) => typeof(Texture).IsAssignableFrom(t) ? store.Texture(file, p) : null, out error);
            if (effects == null)
                return null;
            var preset = ScriptableObject.CreateInstance<PrismPreset>();
            preset.presetType = PrismPresetType.Full;
            preset.PresetDescription = $"The retail camera post-processing of {entry.Name.Replace("PRISM: ", "")}, from the game.";
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var (from, to) in PrismFields)
            {
                var source = type.GetField(from, any);
                var target = typeof(PrismPreset).GetField(to, any);
                if (source == null || target == null)
                    continue;
                var value = source.GetValue(effects);
                if (value == null || target.FieldType.IsInstanceOfType(value))
                    target.SetValue(preset, value);
            }
            Object.DestroyImmediate(effects.gameObject);
            TarkovGeneratedAssets.EnsureFolder(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'));
            AssetDatabase.CreateAsset(preset, path);
            return preset;
        }

        // the preset on the open scenes' LevelSettings (what the raid camera takes it from)
        public static int Assign(PrismPreset preset)
        {
            int count = 0;
            foreach (var level in Object.FindObjectsOfType<LevelSettings>(true).Where(l => !EditorUtility.IsPersistent(l)))
            {
                Undo.RecordObject(level, "Assign PRISM preset");
                level.PrismPresetPrefab = preset;
                EditorUtility.SetDirty(level);
                EditorSceneManager.MarkSceneDirty(level.gameObject.scene);
                count++;
            }
            return count;
        }

        [InitializeOnLoadMethod]
        private static void Cleanup()
        {
            // hosts left by a script reload mid-read
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                if (go != null && go.name == HostName && go.hideFlags == HideFlags.HideAndDontSave)
                    Object.DestroyImmediate(go);
        }
    }
}
