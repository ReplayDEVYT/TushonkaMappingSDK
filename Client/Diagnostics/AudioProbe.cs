using System.Collections.Generic;
using System.Text;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using mizmii.CustomMapLoader.Loading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace mizmii.CustomMapLoader.Diagnostics
{
    // While a raid on a custom map runs: once a second, every spatial sound playing within 80 m of the
    // audio listener, with what decides how loud it is (distance, rolloff, spatializer, mixer), and
    // where the listener is against the camera. For tracking down positional audio that sounds wrong.
    // Read-only; enabled by the "Debug/Audio probe" setting.
    internal class AudioProbe : MonoBehaviour
    {
        private const float Range = 80f;
        private const int MaxPerReport = 12;
        private static AudioProbe _running;
        private float _next;
        private readonly Dictionary<int, string> _last = new Dictionary<int, string>();

        public static void Install()
        {
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (_running != null || Plugin.AudioProbe == null || !Plugin.AudioProbe.Value || !MapRegistry.IsCustomScene(scene))
                    return;
                var go = new GameObject("MappingSDK_AudioProbe");
                DontDestroyOnLoad(go);
                _running = go.AddComponent<AudioProbe>();
            };
        }

        private void Update()
        {
            if (!MapRegistry.IsCustomMapActive())
            {
                if (Singleton<GameWorld>.Instantiated)
                    return;
                Destroy(gameObject);
                return;
            }
            if (Time.time < _next)
                return;
            _next = Time.time + 1f;

            var listener = FindObjectOfType<AudioListener>();
            if (listener == null)
                return;
            var lp = listener.transform.position;
            var camera = CameraManager.Exist ? CameraManager.Instance.Camera : null;
            var sb = new StringBuilder();
            int count = 0;
            foreach (var source in FindObjectsOfType<AudioSource>())
            {
                if (!source.isPlaying || source.spatialBlend <= 0f || source.clip == null)
                    continue;
                float distance = Vector3.Distance(lp, source.transform.position);
                if (distance > Range || ++count > MaxPerReport)
                    continue;
                string line = $"{Path(source.transform)} clip={source.clip.name} ({source.clip.channels}ch) dist={distance:0.0} vol={source.volume:0.00} " +
                              $"spatialBlend={source.spatialBlend:0.00} spatialize={source.spatialize} rolloff={source.rolloffMode} min={source.minDistance:0.#} max={source.maxDistance:0.#} " +
                              $"spread={source.spread:0} mixer={(source.outputAudioMixerGroup != null ? source.outputAudioMixerGroup.name : "none")} " +
                              $"curve@dist={(source.rolloffMode == AudioRolloffMode.Custom ? source.GetCustomCurve(AudioSourceCurveType.CustomRolloff).Evaluate(distance / Mathf.Max(0.01f, source.maxDistance)).ToString("0.00") : "-")}";
                // only what changed, so a steady sound doesn't fill the log
                int id = source.GetInstanceID();
                string key = $"{Mathf.Round(distance)}|{source.volume:0.00}|{source.spatialize}";
                if (_last.TryGetValue(id, out var previous) && previous == key)
                    continue;
                _last[id] = key;
                sb.AppendLine("  " + line);
            }
            if (sb.Length == 0)
                return;
            var cp = camera != null ? camera.transform.position : Vector3.zero;
            Plugin.LogSource.LogInfo($"[Audio probe] listener {Path(listener.transform)} at {lp} ({Vector3.Distance(lp, cp):0.00} m from the camera), " +
                                     $"AudioListener.volume={AudioListener.volume:0.00}\n" + sb.ToString().TrimEnd());
        }

        private static string Path(Transform t)
        {
            var path = t.name;
            for (var p = t.parent; p != null && path.Length < 120; p = p.parent)
                path = p.name + "/" + path;
            return path;
        }

        private void OnDestroy()
        {
            if (_running == this)
                _running = null;
        }
    }
}
