using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.Weather;
using HarmonyLib;
using mizmii.CustomMapLoader.Loading;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace mizmii.CustomMapLoader.Diagnostics
{
    // Logs the lighting, camera, material and bot state of a raid on a custom map, 8 s and 30 s in,
    // to BepInEx/LogOutput.log. Read-only; enabled by the "Debug/Raid diagnostics" setting.
    internal class RaidDiagnostics : MonoBehaviour
    {
        private static RaidDiagnostics _running;

        public static void Install()
        {
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (_running != null || Plugin.RaidDiagnostics == null || !Plugin.RaidDiagnostics.Value || !MapRegistry.IsCustomScene(scene))
                    return;
                var go = new GameObject("MappingSDK_RaidDiagnostics");
                DontDestroyOnLoad(go);
                _running = go.AddComponent<RaidDiagnostics>();
            };
        }

        private IEnumerator Start()
        {
            float timeout = Time.realtimeSinceStartup + 600f;
            while (!Singleton<GameWorld>.Instantiated || !CameraManager.Exist || CameraManager.Instance.Camera == null)
            {
                if (Time.realtimeSinceStartup > timeout)
                {
                    Destroy(gameObject);
                    yield break;
                }
                yield return new WaitForSeconds(1f);
            }

            yield return new WaitForSeconds(8f);
            Report(full: true);
            yield return new WaitForSeconds(22f);
            Report(full: false);
            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            if (_running == this)
                _running = null;
        }

        private static void Report(bool full)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"===== MappingSDK raid diagnostics ({(full ? "8 s" : "30 s")}) =====");
            if (full)
            {
                Section(sb, "sky", Sky);
                Section(sb, "ambient light", Ambient);
                Section(sb, "shader globals", Globals);
                Section(sb, "lights", Lights);
                Section(sb, "camera", MainCamera);
                Section(sb, "materials", Materials);
            }
            Section(sb, "ambient audio", AudioReport);
            Section(sb, "bots", Bots);
            Plugin.LogSource.LogInfo(sb.ToString());
        }

        private static void Section(StringBuilder sb, string name, Action<StringBuilder> body)
        {
            sb.AppendLine($"--- {name}");
            try
            {
                body(sb);
            }
            catch (Exception e)
            {
                sb.AppendLine($"  (failed: {e.GetType().Name}: {e.Message})");
            }
        }

        private static void Sky(StringBuilder sb)
        {
            sb.AppendLine($"  TODSkyProvider.IsAvailable={TODSkyProvider.IsAvailable} Instance={TODSkyProvider.Instance?.GetType().Name ?? "null"}");
            var simple = MonoBehaviourSingleton<TODSkySimple>.Instance;
            sb.AppendLine($"  TOD_Sky={(MonoBehaviourSingleton<TOD_Sky>.Instantiated ? "yes" : "no")} WeatherController={(WeatherController.Instance != null ? "yes" : "no")}");
            if (simple == null)
            {
                sb.AppendLine("  TODSkySimple: none");
                return;
            }
            sb.AppendLine($"  TODSkySimple: active={simple.isActiveAndEnabled} LightSource={Name(simple.LightSource)} AmbientLight={Name(simple.AmbientLight)} SunDirection={simple.SunDirection} SunVisibility={simple.SunVisibility:0.###} IsDay={simple.IsDay} Hour={simple.Cycle?.Hour:0.##}");
            var sh = simple.SH;
            sb.AppendLine($"  SH L0 rgb=({sh[0, 0]:0.####}, {sh[1, 0]:0.####}, {sh[2, 0]:0.####})");
        }

        private static void Ambient(StringBuilder sb)
        {
            var all = Resources.FindObjectsOfTypeAll<AmbientLight>().Where(a => a != null && a.gameObject.scene.IsValid()).ToList();
            if (all.Count == 0)
                sb.AppendLine("  no AmbientLight in any scene");
            var camerasField = AccessTools.Field(typeof(AmbientLight), "_cameras");
            foreach (var a in all)
            {
                var cams = camerasField?.GetValue(a) as IDictionary;
                sb.AppendLine($"  {Path(a.transform)} scene={a.gameObject.scene.name} active={a.isActiveAndEnabled} initialised={a.IsInitialized} cameras={(cams != null ? cams.Count.ToString() : "?")} " +
                              $"shaders=[{Name(a.DepthWriteShader)}, {Name(a.ClearStencilShader)}, {Name(a.WriteStencilShader)}, {Name(a.ScreenAmbientShader)}, {Name(a.AnalyticSourceShader)}] " +
                              $"supported={a.ScreenAmbientShader != null && a.ScreenAmbientShader.isSupported}");
            }
            var manager = UnityEngine.Object.FindObjectOfType<OnRenderObjectManager>();
            sb.AppendLine($"  OnRenderObjectManager={(manager != null ? Path(manager.transform) + " active=" + manager.isActiveAndEnabled : "none")}");
            sb.AppendLine($"  RenderSettings: mode={RenderSettings.ambientMode} sky={RenderSettings.ambientSkyColor} intensity={RenderSettings.ambientIntensity} skybox={Name(RenderSettings.skybox)}");
            sb.AppendLine($"  reflection: mode={RenderSettings.defaultReflectionMode} intensity={RenderSettings.reflectionIntensity} custom={Name(RenderSettings.customReflectionTexture)} active scene={SceneManager.GetActiveScene().name}");
            sb.AppendLine($"  fog: on={RenderSettings.fog} mode={RenderSettings.fogMode} ({(int)RenderSettings.fogMode}) density={RenderSettings.fogDensity} start={RenderSettings.fogStartDistance} end={RenderSettings.fogEndDistance} color={RenderSettings.fogColor}");
            // the weather's fog: MBOIT_Scattering on the camera, driven by WeatherController
            var camera = Camera.main;
            var tod = camera != null ? camera.GetComponent<TOD_Scattering>() : null;
            var mboit = camera != null ? camera.GetComponent<MBOIT_Scattering>() : null;
            var weather = WeatherController.Instance;
            sb.AppendLine($"  weather fog: value={(weather != null ? weather.WeatherCurve?.Fog.ToString("0.###") ?? "?" : "no WeatherController")} remapV2={(weather != null && weather.MBOITFogRemapDataV2 != null)} " +
                          $"TOD_Scattering={(tod != null ? $"on={tod.enabled} mboit={tod.MBOIT} density={tod.GlobalDensity:0.#####}" : "none")} " +
                          $"MBOIT_Scattering={(mboit != null ? $"on={mboit.enabled} density={mboit.GlobalDensity:0.#####}" : "none")}");
        }

        private static void AudioReport(StringBuilder sb)
        {
            if (!MonoBehaviourSingleton<BetterAudio>.Instantiated)
            {
                sb.AppendLine("  BetterAudio not instantiated");
                return;
            }
            var mixer = MonoBehaviourSingleton<BetterAudio>.Instance.Master;
            var parameters = new[] { "MainVolume", "InGame", "AmbientVolume", "AmbientOutVolume", "AmbientInVolume", "AmbientOutDayVolume", "AmbientOutNightVolume", "AmbientInDayVolume", "AmbientInNightVolume", "WorldVolume" };
            sb.AppendLine("  mixer: " + string.Join(" ", parameters.Select(p => mixer.GetFloat(p, out var v) ? $"{p}={v:0.#}" : $"{p}=(not exposed)")));
            sb.AppendLine($"  AmbientAudioSystem={MonoBehaviourSingleton<Audio.AmbientSubsystem.AmbientAudioSystem>.Instantiated} driver={UnityEngine.Object.FindObjectOfType<AmbientMixerDriver>() != null} " +
                          $"environment={EFT.EnvironmentEffect.EnvironmentManager.Instance?.Environment.ToString() ?? "no EnvironmentManager"} listeners={UnityEngine.Object.FindObjectsOfType<AudioListener>().Length} AudioListener.volume={AudioListener.volume} pause={AudioListener.pause}");
            if (MonoBehaviourSingleton<Audio.AmbientSubsystem.AmbientAudioSystem>.Instantiated)
            {
                var system = MonoBehaviourSingleton<Audio.AmbientSubsystem.AmbientAudioSystem>.Instance;
                sb.AppendLine($"  ambient system: {Path(system.transform)} initialised={system.Initialized} season={system.CurrentSeasonStatus} " +
                              $"data={Name(system.AmbientSoundData)} effects={Name(system.EffectsData)} spatial audio initialised={Audio.SpatialSystem.SpatialAudioSystem.Initialized}");
                foreach (var group in system.GetComponentsInChildren<Audio.AmbientSubsystem.AmbientSoundPlayerGroup>(true))
                    sb.AppendLine($"  group {group.name} active={group.gameObject.activeInHierarchy} mixer={group._outputMixerGroup?.name ?? "none"} players={group._soundPlayers.Count} playing={group.IsPlaying}");
            }
            foreach (var source in UnityEngine.Object.FindObjectsOfType<AudioSource>())
            {
                if (!MapRegistry.IsCustomScene(source.gameObject.scene))
                    continue;
                var clip = source.clip;
                sb.AppendLine($"  {Path(source.transform)} enabled={source.isActiveAndEnabled} playing={source.isPlaying} volume={source.volume:0.###} mute={source.mute} " +
                              $"clip={(clip != null ? clip.name + " " + clip.loadState + " " + clip.length.ToString("0.#") + "s" : "none")} " +
                              $"mixer={source.outputAudioMixerGroup?.name ?? "none"} spatial={source.spatialBlend} fader={source.GetComponent<AmbientSoundFader>() != null}");
            }
        }

        private static void Globals(StringBuilder sb)
        {
            foreach (var name in new[] { "_SHAr", "_SHAg", "_SHAb", "_SHC", "_EFT_Ambient", "TOD_LightDirection" })
                sb.AppendLine($"  {name}={Shader.GetGlobalVector(name)}");
            foreach (var name in new[] { "_SunVisibility", "_ClearSky", "_ForceIndoor", "_ForceIndoorIntensity", "_DirectionLightShadow" })
                sb.AppendLine($"  {name}={Shader.GetGlobalFloat(name)}");
        }

        private static void Lights(StringBuilder sb)
        {
            foreach (var light in UnityEngine.Object.FindObjectsOfType<Light>().Where(l => l.enabled))
                sb.AppendLine($"  {Path(light.transform)} scene={light.gameObject.scene.name} type={light.type} enabled={light.enabled} intensity={light.intensity} color={light.color} " +
                              $"shadows={light.shadows} mask=0x{light.cullingMask:X8} renderMode={light.renderMode} forward={light.transform.forward}");
        }

        private static void MainCamera(StringBuilder sb)
        {
            var cam = CameraManager.Instance.Camera;
            sb.AppendLine($"  {Path(cam.transform)} path={cam.actualRenderingPath} hdr={cam.allowHDR} clear={cam.clearFlags} bg={cam.backgroundColor} mask=0x{cam.cullingMask:X8} near={cam.nearClipPlane} far={cam.farClipPlane}");
            foreach (CameraEvent evt in Enum.GetValues(typeof(CameraEvent)))
            {
                var buffers = cam.GetCommandBuffers(evt);
                if (buffers.Length > 0)
                    sb.AppendLine($"  cb {evt}: {string.Join(", ", buffers.Select(b => b.name))}");
            }
            sb.AppendLine("  components: " + string.Join(", ", cam.GetComponents<Behaviour>().Select(b => b.GetType().Name + (b.enabled ? "" : "(off)"))));
            sb.AppendLine($"  QualitySettings: colorSpace={QualitySettings.activeColorSpace} pixelLights={QualitySettings.pixelLightCount} shadows={QualitySettings.shadows}");
        }

        private static void Materials(StringBuilder sb)
        {
            var counts = new Dictionary<string, int>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!MapRegistry.IsCustomScene(scene))
                    continue;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                        foreach (var m in r.sharedMaterials)
                        {
                            var key = m == null ? "(null material)" : $"{m.name} -> {(m.shader != null ? m.shader.name + (m.shader.isSupported ? "" : " (unsupported)") : "(null shader)")} queue={m.renderQueue} layer={LayerMask.LayerToName(r.gameObject.layer)} on={r.enabled && r.gameObject.activeInHierarchy}";
                            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
                        }
            }
            foreach (var kv in counts)
                sb.AppendLine($"  {kv.Value}x {kv.Key}");
        }

        private static void Bots(StringBuilder sb)
        {
            if (!Singleton<IBotGame>.Instantiated)
            {
                sb.AppendLine("  no IBotGame");
                return;
            }
            var controller = Singleton<IBotGame>.Instance.BotsController;
            var bots = controller?.Bots?.BotOwners?.ToList() ?? new List<BotOwner>();
            sb.AppendLine($"  {bots.Count} bot(s)");
            foreach (var bot in bots)
            {
                if (bot == null)
                    continue;
                string layer = "?", decision = "?";
                try { layer = bot.Brain?.ActiveLayerName() ?? "null"; } catch { }
                try { decision = bot.Brain?.LastDecision?.ToString() ?? "null"; } catch { }
                var patrol = bot.PatrollingData;
                var mover = bot.Mover;
                sb.AppendLine($"  {bot.Profile?.Info?.Settings?.Role} {bot.name} state={bot.BotState} pos={bot.Position} layer={layer} decision={decision} " +
                              $"patrol={(patrol != null ? patrol.Status + (patrol.IsActivated ? "" : "(inactive)") : "null")} " +
                              $"moving={(mover != null && mover.IsMoving)} path={(mover != null && mover.HasPathAndNoComplete)} target={mover?.TargetPoint} " +
                              $"enemy={(bot.Memory?.GoalEnemy != null ? bot.Memory.GoalEnemy.Person?.Profile?.Nickname : "none")} zone={bot.BotsGroup?.BotZone?.name}");
            }
        }

        private static string Name(UnityEngine.Object o) => o == null ? "null" : o.name;

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent)
                parts.Insert(0, t.name);
            return string.Join("/", parts);
        }
    }
}
