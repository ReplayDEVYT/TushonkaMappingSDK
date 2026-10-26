using System.Collections.Generic;
using Prism.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace TarkovSDK.Editor
{
    // Scene view preview of the raid camera's post-processing. In the raid CameraManager puts the map's
    // LevelSettings.PrismPresetPrefab on the camera's PrismEffects (retail Customs: auto exposure to a
    // middle grey of 0.12 capped at 0.9, a black vignette, the filmic tonemap); that is what makes the
    // raid darker and contrastier than the raw frame. This runs the decompiled final pass over every
    // Scene view through a command buffer, with the scene's preset or the retail values.
    [InitializeOnLoad]
    public static class TarkovPostPreview
    {
        private const string EnabledKey = "TarkovSDK.PostPreview";
        private const string ShaderName = "Hidden/Tarkov SDK/Post Preview";
        private const string BufferName = "Tarkov SDK post preview";
        private const string LumName = "SDK post preview luminance";
        private static readonly Dictionary<Camera, CommandBuffer> Buffers = new Dictionary<Camera, CommandBuffer>();
        private static Material _material;
        private static RenderTexture _lum;

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledKey, true);
            set { EditorPrefs.SetBool(EnabledKey, value); SceneView.RepaintAll(); }
        }

        static TarkovPostPreview()
        {
            SceneView.duringSceneGui += view => Apply(view);
            // HideAndDontSave objects and the cameras' command buffers outlive a script reload, which empties the statics
            // that track them: without this every recompile stacked another full-screen pass on each Scene view camera
            // and leaked the textures, until the GPU ran out after hours of work
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
            Cleanup();
        }

        // removes this preview's buffers from every camera and frees its GPU objects, including ones left by earlier reloads
        private static void Cleanup()
        {
            foreach (var camera in Resources.FindObjectsOfTypeAll<Camera>())
                foreach (var buffer in camera.GetCommandBuffers(CameraEvent.AfterImageEffects))
                    if (buffer.name == BufferName)
                    {
                        camera.RemoveCommandBuffer(CameraEvent.AfterImageEffects, buffer);
                        buffer.Release();
                    }
            Buffers.Clear();
            foreach (var texture in Resources.FindObjectsOfTypeAll<RenderTexture>())
                if (texture.name == LumName)
                {
                    texture.Release();
                    Object.DestroyImmediate(texture);
                }
            _lum = null;
            foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
                if (material.shader != null && material.shader.name == ShaderName && !EditorUtility.IsPersistent(material))
                    Object.DestroyImmediate(material);
            _material = null;
            if (_retail != null)
                Object.DestroyImmediate(_retail);
            _retail = null;
        }

        // the retail camera's PrismEffects values (Customs' camera prefab), used when the map has no preset
        public static void RetailPreset(PrismPreset preset)
        {
            preset.presetType = PrismPresetType.Full;
            preset.useTonemap = true;
            preset.toneType = TonemapType.Filmic;
            preset.toneValues = new Vector3(7f, 0.3f, 0.3f);
            preset.secondaryToneValues = new Vector3(-0.05f, 0.25f, 0f);
            preset.useExposure = true;
            preset.exposureMiddleGrey = 0.12f;
            preset.exposureLowerLimit = -10f;
            preset.exposureUpperLimit = 0.9f;
            preset.exposureSpeed = 2f;
            preset.useVignette = true;
            preset.vignetteStart = 0.9f;
            preset.vignetteEnd = 0.4f;
            preset.vignetteIntensity = 0.887f;
            preset.vignetteColor = Color.black;
            preset.useBloom = false;
            preset.bloomType = BloomType.HDR;
            preset.bloomIntensity = 0.05f;
            preset.bloomThreshold = 1.579f;
            preset.bloomDownsample = 2;
            preset.bloomBlurPasses = 4;
            preset.useBloomStability = true;
            preset.useBloomLensdirt = true;
            preset.bloomLensdirtIntensity = 0.759f;
            preset.useAmbientObscurance = false;
            preset.aoIntensity = 1.117f;
            preset.aoRadius = 1.345f;
            preset.aoBias = 0.448f;
            preset.aoLightingContribution = 0.353f;
            preset.useChromaticAb = false;
            preset.useNoise = false;
            preset.useDoF = false;
            preset.useLUT = false;
            preset.useGammaCorrection = false;
            preset.useFog = false;
            preset.useNV = false;
            preset.useRays = false;
        }

        private static PrismPreset _retail;

        private static PrismPreset Retail()
        {
            if (_retail == null)
            {
                _retail = ScriptableObject.CreateInstance<PrismPreset>();
                _retail.hideFlags = HideFlags.HideAndDontSave;
                RetailPreset(_retail);
            }
            return _retail;
        }

        public static void Apply(SceneView view)
        {
            var camera = view != null ? view.camera : null;
            if (camera == null)
                return;
            if (!Enabled || !TarkovLightingPreview.Enabled || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Remove(camera);
                return;
            }
            if (_material == null)
            {
                var shader = Shader.Find(ShaderName);
                if (shader == null)
                    return;
                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            var level = Object.FindObjectOfType<LevelSettings>();
            var preset = level != null && level.PrismPresetPrefab != null ? level.PrismPresetPrefab : Retail();
            bool tonemap = preset.useTonemap && preset.toneType == TonemapType.Filmic;
            _material.SetVector("_TarkovExposure", new Vector4(preset.exposureMiddleGrey, preset.exposureLowerLimit, preset.exposureUpperLimit, preset.useExposure ? 1f : 0f));
            _material.SetVector("_TarkovVignette", new Vector4(preset.vignetteStart, preset.vignetteEnd, preset.vignetteIntensity, preset.useVignette ? 1f : 0f));
            _material.SetColor("_TarkovVignetteColor", preset.vignetteColor);
            _material.SetVector("_TarkovToneParams", new Vector4(preset.toneValues.x, preset.toneValues.y, preset.toneValues.z, preset.toneValues.z));
            _material.SetVector("_TarkovSecondaryTone", new Vector4(preset.secondaryToneValues.x, preset.secondaryToneValues.y, preset.secondaryToneValues.z, preset.secondaryToneValues.z));
            _material.SetFloat("_TarkovTonemap", tonemap ? 1f : 0f);
            _material.SetFloat("_TarkovGamma", preset.useGammaCorrection ? preset.gammaValue : 0f);
            if (Buffers.ContainsKey(camera))
                return;

            if (_lum == null)
            {
                // the frame downsampled with mips: the exposure reads the average from the top mip
                _lum = new RenderTexture(256, 256, 0, RenderTextureFormat.ARGBHalf) { useMipMap = true, autoGenerateMips = true, name = LumName, hideFlags = HideFlags.HideAndDontSave };
                _lum.Create();
            }
            // never two on one camera
            foreach (var stale in camera.GetCommandBuffers(CameraEvent.AfterImageEffects))
                if (stale.name == BufferName)
                {
                    camera.RemoveCommandBuffer(CameraEvent.AfterImageEffects, stale);
                    stale.Release();
                }
            var buffer = new CommandBuffer { name = BufferName };
            int source = Shader.PropertyToID("_TarkovPostSource");
            buffer.GetTemporaryRT(source, -1, -1, 0, FilterMode.Bilinear);
            buffer.Blit(BuiltinRenderTextureType.CameraTarget, source);
            buffer.Blit(source, _lum);
            buffer.SetGlobalTexture("_TarkovLum", _lum);
            buffer.Blit(source, BuiltinRenderTextureType.CameraTarget, _material);
            buffer.ReleaseTemporaryRT(source);
            camera.AddCommandBuffer(CameraEvent.AfterImageEffects, buffer);
            Buffers[camera] = buffer;
        }

        private static void Remove(Camera camera)
        {
            if (!Buffers.TryGetValue(camera, out var buffer))
                return;
            camera.RemoveCommandBuffer(CameraEvent.AfterImageEffects, buffer);
            buffer.Release();
            Buffers.Remove(camera);
        }
    }
}
