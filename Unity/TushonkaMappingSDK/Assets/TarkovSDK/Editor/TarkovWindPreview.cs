using System.Collections.Generic;
using System.Linq;
using EFT.SpeedTree;
using EFT.Weather;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // The raid's wind in the Scene view: trees (TreeWind) and wires (p0/Bumped Specular Wires Wind) sway as the game
    // sways them, for a preview wind strength and direction. In the raid WeatherController hands the weather's wind
    // (WeatherCurve.Wind: the weather node's wind speed 1..5 as 0..1, along one of eight directions) to
    // WindController.SetWind, which sets the trees' globals (ApplyWindToPlants) and the wires' (WiresController.Update),
    // and LevelSettings gives every TreeWind's renderer a property block of its wind values. Those three are ported
    // here; the property blocks aren't saved with the scene. Off, the blocks are cleared and nothing sways.
    [InitializeOnLoad]
    public static class TarkovWindPreview
    {
        private const string EnabledKey = "TarkovSDK.WindPreview";
        private const string StrengthKey = "TarkovSDK.WindPreview.Strength";
        private const string DirectionKey = "TarkovSDK.WindPreview.Direction";

        private static readonly Dictionary<Renderer, int> Applied = new Dictionary<Renderer, int>();
        private static List<TreeWind> _trees;
        private static bool _wires;
        private static double _nextScan, _nextRepaint;
        private static Vector2 _prevWindVector;

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledKey, true);
            set
            {
                EditorPrefs.SetBool(EnabledKey, value);
                if (!value)
                    Clear();
                _trees = null;
                SceneView.RepaintAll();
            }
        }

        // the weather's wind, 0 (calm) .. 1 (the strongest)
        public static float Strength
        {
            get => EditorPrefs.GetFloat(StrengthKey, 0.4f);
            set => EditorPrefs.SetFloat(StrengthKey, Mathf.Clamp01(value));
        }

        // where the wind blows to, in degrees clockwise from the scene's +Z
        public static float Direction
        {
            get => EditorPrefs.GetFloat(DirectionKey, 45f);
            set => EditorPrefs.SetFloat(DirectionKey, Mathf.Repeat(value, 360f));
        }

        static TarkovWindPreview()
        {
            EditorApplication.update += Tick;
            EditorApplication.hierarchyChanged += () => _trees = null;
            Undo.undoRedoPerformed += () => _trees = null;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode)
                    Clear();
            };
        }

        private static void Tick()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || !Enabled)
                return;
            double now = EditorApplication.timeSinceStartup;
            if (_trees == null || now > _nextScan)
            {
                _nextScan = now + 2.0;
                _trees = Object.FindObjectsOfType<TreeWind>().ToList();
                _wires = Object.FindObjectsOfType<TarkovWire>(true).Length > 0;
                foreach (var tree in _trees)
                    Apply(tree);
            }

            float angle = Direction * Mathf.Deg2Rad;
            var wind = new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * Strength;
            ApplyWindToPlants(wind);
            var controller = Object.FindObjectOfType<WindController>(true);
            UpdateWires(controller != null && controller.Wires != null ? controller.Wires : new WiresController(), wind);

            // the sway only moves while the Scene view redraws
            if ((_trees.Count > 0 || _wires) && now > _nextRepaint)
            {
                _nextRepaint = now + 1.0 / 30.0;
                SceneView.RepaintAll();
            }
        }

        // WindController.ApplyWindToPlants
        private static void ApplyWindToPlants(Vector2 wind)
        {
            float magnitude = wind.magnitude;
            var direction = wind.normalized;
            if (magnitude < 0.0001f)
                direction = Vector2.one;
            magnitude = Mathf.Clamp(magnitude, 0.0001f, 0.99999f);
            wind = direction * magnitude;
            Shader.SetGlobalVector("_WindVector", wind);
            Shader.SetGlobalVector("_PrevWindVector", _prevWindVector);
            Shader.SetGlobalVector("_ST_WindVector", new Vector4(direction.x, 0f, direction.y, magnitude));
            // the game's compute path (WindCalculator) isn't run; the shader blends the property block's values itself
            Shader.SetGlobalFloat("_WindCalculated", 0f);
            _prevWindVector = wind;
        }

        // WiresController.Update / SetShaderValues
        private static void UpdateWires(WiresController wires, Vector2 wind)
        {
            var normalized = new Vector3(wind.x, 0f, wind.y).normalized;
            float strength = Mathf.Min(wind.magnitude * wires.StrengthMultiplyer, wires.MaxValue);
            normalized *= strength;
            float freqMult = 1f + strength * wires.FreqDelta;
            float magnitude = wires.MaxValue > 0f ? strength / wires.MaxValue : 0f;
            Shader.SetGlobalVector("_WireWind", normalized);
            Shader.SetGlobalFloat("_WireWindFreq", Mathf.Lerp(wires.WindFreq, wires.MaxWinFreq, magnitude) * freqMult);
            Shader.SetGlobalFloat("_WireWindScale", wires.WindScale);
            Shader.SetGlobalFloat("_WireDetailWindScale", wires.DetailWindScale);
            Shader.SetGlobalFloat("_WireDetailWindFreq", wires.DetailWindFreq * freqMult);
            Shader.SetGlobalFloat("_WireDetailWindAmp", wires.DetailWindAmp);
            Shader.SetGlobalVector("_WindFuncOffsetAndScale",
                new Vector2(wires.WiresMaxWindOffsetMultiplier * magnitude, Mathf.Lerp(1f, wires.WiresMaxWindAmplitudeMultiplier, magnitude)));
        }

        // TreeWind.CreateMaterialPropertyBlock (the values the global sway reads), on the tree's renderer when changed
        private static void Apply(TreeWind tree)
        {
            if (tree == null || !tree.TryGetComponent<Renderer>(out var renderer))
                return;
            int hash;
            unchecked
            {
                hash = tree.BaseMinWindData._ST_WindGlobal.GetHashCode() * 31 + tree.BaseMaxWindData._ST_WindGlobal.GetHashCode();
                hash = hash * 31 + tree.BaseMinWindData._ST_WindBranchAdherences.GetHashCode() + tree.BaseMaxWindData._ST_WindBranchAdherences.GetHashCode() * 7;
                hash = hash * 31 + tree.MinWindData._ST_WindGlobal_B.GetHashCode() + tree.MaxWindData._ST_WindGlobal_B.GetHashCode() * 7;
            }
            if (Applied.TryGetValue(renderer, out var old) && old == hash)
                return;
            Applied[renderer] = hash;
            var block = new MaterialPropertyBlock();
            block.SetFloat("_WindEnabled", tree.BaseMinWindData._ST_WindGlobal.magnitude > 0f ? 1f : 0f);
            block.SetInt("_WindId", 0);
            block.SetVector("_ST_WindGlobal", tree.BaseMinWindData._ST_WindGlobal);
            block.SetVector("_ST_WindBranchAdherences", tree.BaseMinWindData._ST_WindBranchAdherences);
            block.SetVector("_ST_WindGlobal_A", tree.BaseMaxWindData._ST_WindGlobal);
            block.SetVector("_ST_WindBranchAdherences_A", tree.BaseMaxWindData._ST_WindBranchAdherences);
            block.SetVector("_ST_WindGlobal_B", tree.MaxWindData._ST_WindGlobal_B);
            block.SetVector("_ST_WindGlobal_C", tree.MinWindData._ST_WindGlobal_B);
            renderer.SetPropertyBlock(block);
        }

        private static void Clear()
        {
            // every tree's, including blocks set before a script reload emptied Applied
            foreach (var tree in Object.FindObjectsOfType<TreeWind>(true))
                if (tree.TryGetComponent<Renderer>(out var renderer))
                    renderer.SetPropertyBlock(null);
            Applied.Clear();
            Shader.SetGlobalVector("_WireWind", Vector3.zero);
            Shader.SetGlobalVector("_ST_WindVector", new Vector4(1f, 0f, 1f, 0.0001f));
        }

        // the controls, beside the weather preview's (TarkovTimeOfDayEditor.DrawPreviewControls)
        public static void DrawControls()
        {
            bool enabled = EditorGUILayout.Toggle(new GUIContent("Preview Wind", "Trees and wires sway in the Scene view as the raid's wind sways them."), Enabled);
            if (enabled != Enabled)
                Enabled = enabled;
            using (new EditorGUI.DisabledScope(!enabled))
            using (new EditorGUI.IndentLevelScope())
            {
                float strength = EditorGUILayout.Slider(new GUIContent("Strength", "The raid weather's wind: 0 calm, 1 the strongest (the weather's wind speed 1 to 5). " +
                                                                                    "Trees blend from their calm sway to their stormy one; wires swing fully at any wind."), Strength, 0f, 1f);
                if (!Mathf.Approximately(strength, Strength))
                    Strength = strength;
                float direction = EditorGUILayout.Slider(new GUIContent("Direction", "Where the wind blows to, in degrees clockwise from the scene's +Z. In the raid it's one of eight, picked by the weather."),
                    Direction, 0f, 360f);
                if (!Mathf.Approximately(direction, Direction))
                    Direction = direction;
            }
        }
    }
}
