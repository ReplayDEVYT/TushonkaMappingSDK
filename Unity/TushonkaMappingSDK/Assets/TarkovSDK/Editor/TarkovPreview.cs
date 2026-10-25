using System;
using System.Collections.Generic;
using System.Linq;
using EFT.GameTriggers;
using EFT.Interactive;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // Edit-mode previews of interactive objects: doors, switches, containers, lamps, elevator doors,
    // automatic doors, PA announcements and sound zones, run by ports of the game's own code
    // (TarkovPreviewSims.cs) so a mapper sees and hears what a raid will do: a door's swing and timing,
    // a lamp's exact flicker, a switch's whole chain.
    //
    // A preview only touches transforms, active states, lights and renderer property blocks, and
    // records each one's value the first time; Stop puts everything back. So does saving a scene,
    // entering play mode, building a map or reloading scripts, so a preview never ends up saved.
    //
    // Sounds play in 2D through hidden AudioSources, optionally faded by the game's rolloff curve for
    // the distance from the scene camera (Attenuate By Camera).
    [InitializeOnLoad]
    public static class TarkovPreview
    {
        private static readonly Dictionary<Component, PreviewSim> Sims = new Dictionary<Component, PreviewSim>();
        private static readonly List<(double Time, Action Action)> Scheduled = new List<(double, Action)>();
        private static readonly Dictionary<Object, Action> Restores = new Dictionary<Object, Action>();
        private static readonly List<Object> RestoreOrder = new List<Object>();
        private static readonly List<AudioSource> Sounds = new List<AudioSource>();
        private static readonly Dictionary<AudioSource, SoundInfo> SoundInfos = new Dictionary<AudioSource, SoundInfo>();
        private static readonly Dictionary<ExfiltrationPoint, EExfiltrationStatus> ExfilStatus = new Dictionary<ExfiltrationPoint, EExfiltrationStatus>();
        private static double _time, _lastRealTime = -1;
        private static int _frame;
        private static double _nextRepaint;

        public static event Action<ExfiltrationPoint, EExfiltrationStatus> ExfilStatusChanged;

        public static bool AttenuateByCamera
        {
            get => EditorPrefs.GetBool("TarkovSDK.Preview.Attenuate", false);
            set => EditorPrefs.SetBool("TarkovSDK.Preview.Attenuate", value);
        }

        // the preview clock in seconds, and the frame count (the game's lamps update every 20th frame)
        public static float Time => (float)_time;
        public static int Frame => _frame;
        public static bool Running => Sims.Count > 0 || Sounds.Count > 0 || Scheduled.Count > 0;

        static TarkovPreview()
        {
            EditorApplication.update += Tick;
            EditorSceneManager.sceneSaving += (scene, path) => StopAll();
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode)
                    StopAll();
            };
            AssemblyReloadEvents.beforeAssemblyReload += StopAll;
            EditorSceneManager.sceneClosing += (scene, removing) => StopAll();
        }

        // ------------------------------------------------------------ sims

        public static PreviewSim Find(Component target) =>
            target != null && Sims.TryGetValue(target, out var sim) ? sim : null;

        public static bool IsPreviewing(Component target) => Find(target) != null;

        // the target's sim, started on first use
        public static T Get<T>(Component target) where T : PreviewSim, new()
        {
            if (target == null)
                return null;
            if (Sims.TryGetValue(target, out var existing))
                return existing as T;
            var sim = new T { Target = target };
            Sims[target] = sim;
            _lastRealTime = EditorApplication.timeSinceStartup;
            sim.Begin();
            return sim;
        }

        // the right sim for any previewable component
        public static PreviewSim For(Component target)
        {
            switch (target)
            {
                case LootableContainer _: return Get<ContainerSim>(target);
                case WorldInteractiveObject _: return Get<InteractiveSim>(target);
                case LampController _: return Get<LampSim>(target);
                case ControlledLampGroup _: return Get<LampGroupSim>(target);
                case ExfiltrationDoor _: return Get<ExfiltrationDoorSim>(target);
                case HandlerAnimator _: return Get<AnimatorHandlerSim>(target);
                case HandlerPlaySoundAdvanced _: return Get<SoundHandlerSim>(target);
                case HandlerFilterOnce _: return Get<FilterOnceSim>(target);
                case TriggerZone _: return Get<ZoneSim>(target);
                case QueuePlayer _: return Get<QueuePlayerSim>(target);
                case InteractiveSubscriber _: return Get<SubscriberSim>(target);
                default: return null;
            }
        }

        public static IEnumerable<PreviewSim> All => Sims.Values.ToList();

        public static void StopAll()
        {
            foreach (var sim in Sims.Values.ToList())
            {
                try
                {
                    sim.End();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
            Sims.Clear();
            Scheduled.Clear();
            ExfilStatus.Clear();
            IgnoredTriggers.Clear();
            foreach (var source in Sounds)
                if (source != null)
                    Object.DestroyImmediate(source.gameObject);
            Sounds.Clear();
            SoundInfos.Clear();
            for (int i = RestoreOrder.Count - 1; i >= 0; i--)
            {
                var key = RestoreOrder[i];
                if (key == null)
                    continue;
                try
                {
                    Restores[key]();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
            Restores.Clear();
            RestoreOrder.Clear();
            _time = 0;
            _frame = 0;
            SceneView.RepaintAll();
        }

        // ------------------------------------------------------------ clock

        public static void After(float seconds, Action action)
        {
            if (seconds <= 0f)
                action();
            else
                Scheduled.Add((_time + seconds, action));
        }

        private static void Tick()
        {
            if (!Running)
            {
                _lastRealTime = -1;
                return;
            }
            double now = EditorApplication.timeSinceStartup;
            float dt = _lastRealTime < 0 ? 0f : (float)Math.Min(now - _lastRealTime, 0.1);
            _lastRealTime = now;
            _time += dt;
            _frame++;

            for (int i = 0; i < Scheduled.Count; i++)
                if (Scheduled[i].Time <= _time)
                {
                    var action = Scheduled[i].Action;
                    Scheduled.RemoveAt(i--);
                    Safe(action);
                }
            foreach (var sim in Sims.Values.ToList())
                if (sim.Target != null)
                    Safe(() => sim.Update(dt));
            for (int i = Sounds.Count - 1; i >= 0; i--)
                if (Sounds[i] == null || (!Sounds[i].isPlaying && !Sounds[i].loop))
                {
                    if (Sounds[i] != null)
                    {
                        SoundInfos.Remove(Sounds[i]);
                        Object.DestroyImmediate(Sounds[i].gameObject);
                    }
                    Sounds.RemoveAt(i);
                }
                else
                {
                    UpdateAttenuation(Sounds[i]);
                }

            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
            if (now > _nextRepaint)
            {
                _nextRepaint = now + 0.15;
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }
        }

        private static void Safe(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Debug.LogError("[Tarkov SDK] Preview: " + e);
            }
        }

        // ------------------------------------------------------------ restore

        // keeps the value a preview is about to change, once per object
        public static void Record(Object key, Action restore)
        {
            if (key == null || Restores.ContainsKey(key))
                return;
            Restores[key] = restore;
            RestoreOrder.Add(key);
        }

        public static void Record(Transform t)
        {
            if (t == null)
                return;
            Vector3 p = t.localPosition, s = t.localScale;
            Quaternion r = t.localRotation;
            Record(t, () =>
            {
                t.localPosition = p;
                t.localRotation = r;
                t.localScale = s;
            });
        }

        public static void RecordHierarchy(Transform root)
        {
            if (root == null)
                return;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                Record(t);
        }

        public static void RecordActive(GameObject go)
        {
            if (go == null)
                return;
            bool active = go.activeSelf;
            Record(go, () => go.SetActive(active));
        }

        public static void RecordEnabled(Behaviour b)
        {
            if (b == null)
                return;
            bool enabled = b.enabled;
            Record(b, () => b.enabled = enabled);
        }

        public static void RecordLight(Light light)
        {
            if (light == null)
                return;
            bool enabled = light.enabled;
            float intensity = light.intensity;
            Record(light, () =>
            {
                light.enabled = enabled;
                light.intensity = intensity;
            });
        }

        public static void RecordBlock(Renderer renderer)
        {
            if (renderer == null)
                return;
            var block = new MaterialPropertyBlock();
            bool had = renderer.HasPropertyBlock();
            if (had)
                renderer.GetPropertyBlock(block);
            Record(renderer, () => renderer.SetPropertyBlock(had ? block : null));
        }

        // ------------------------------------------------------------ sound

        // the game's BetterAudio rolloff (EFTHardSettings.SoundRolloff): volume by distance / max distance
        private static readonly AnimationCurve SoundRolloff = new AnimationCurve(
            new Keyframe(0f, 1f, -3.1399f, -3.1399f), new Keyframe(0.2008f, 0.4026f, -1.8492f, -1.8492f), new Keyframe(1f, 0f, -0.1361f, -0.1361f));

        private class SoundInfo
        {
            public Vector3 Position;
            public float Volume, Rolloff;
            public Transform Follow;
        }

        // plays at a point like BetterAudio.PlayAtPoint; rolloff <= 0 (or a 2D sound) is never faded
        public static AudioSource Play(Vector3 position, AudioClip clip, float volume, float rolloff, bool loop = false, float pitch = 1f, Transform follow = null)
        {
            if (clip == null)
                return null;
            var go = new GameObject("TarkovPreviewSound") { hideFlags = HideFlags.HideAndDontSave };
            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.clip = clip;
            source.loop = loop;
            source.pitch = Mathf.Approximately(pitch, 0f) ? 1f : pitch;
            SoundInfos[source] = new SoundInfo { Position = position, Volume = Mathf.Clamp01(volume), Rolloff = rolloff, Follow = follow };
            UpdateAttenuation(source);
            source.Play();
            Sounds.Add(source);
            _lastRealTime = EditorApplication.timeSinceStartup;
            return source;
        }

        public static void Stop(AudioSource source)
        {
            if (source == null)
                return;
            Sounds.Remove(source);
            SoundInfos.Remove(source);
            Object.DestroyImmediate(source.gameObject);
        }

        public static bool IsPlaying(AudioSource source) => source != null && source.isPlaying;

        private static void UpdateAttenuation(AudioSource source)
        {
            if (!SoundInfos.TryGetValue(source, out var info))
                return;
            float volume = info.Volume;
            var camera = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null;
            if (AttenuateByCamera && info.Rolloff > 0f && camera != null)
            {
                var position = info.Follow != null ? info.Follow.position : info.Position;
                volume *= Mathf.Clamp01(SoundRolloff.Evaluate(Vector3.Distance(camera.transform.position, position) / info.Rolloff));
            }
            source.volume = volume;
        }

        // ------------------------------------------------------------ game triggers

        // TriggersEmitter.SetTriggerState(id, false), from HandlerTriggerState: Emit drops these
        private static readonly HashSet<string> IgnoredTriggers = new HashSet<string>();

        // LocalTriggersEmitter.Emit: unless the id is switched off, every handler in the open scenes that
        // listens to it reacts
        public static void Emit(string triggerId)
        {
            if (string.IsNullOrEmpty(triggerId) || IgnoredTriggers.Contains(triggerId))
                return;
            foreach (var handler in Object.FindObjectsOfType<HandlerTriggerState>())
                if (handler._triggerId == triggerId)
                    foreach (var id in handler._controlledTriggerId ?? new string[0])
                        if (handler._targetState)
                            IgnoredTriggers.Remove(id);
                        else
                            IgnoredTriggers.Add(id);
            foreach (var handler in Object.FindObjectsOfType<CommonAssets.Scripts.Game.GameTriggers.Handlers.HandlerGameObjectState>(true))
                if (handler._triggerId == triggerId)
                    foreach (var go in handler._controlledGameObjects ?? new GameObject[0])
                        if (go != null)
                        {
                            RecordActive(go);
                            go.SetActive(handler._targetState);
                        }
            // damage and effects only happen to players; say what the raid would do
            foreach (var handler in Object.FindObjectsOfType<HandlerDamage>())
                if (handler._triggerIdStartDamage == triggerId && handler._damage != null && handler._damage.Length > 0)
                    Debug.Log($"[Tarkov SDK] Preview: {handler.name} would deal {string.Join(", ", handler._damage.Select(d => $"{d.Amount:0.#} to {d.BodyPartColliderType}"))}" +
                              (handler._damageCount == 1 || handler._damageSecondsInterval < 0.1f ? "" : $", every {handler._damageSecondsInterval:0.##} s until '{handler._triggerIdFinishDamage}'"));
            foreach (var handler in Object.FindObjectsOfType<HandlerAnimator>())
                if (handler._animatorActions != null && handler._animatorActions.Any(a => a.TriggerId == triggerId))
                    Get<AnimatorHandlerSim>(handler).OnTrigger(triggerId);
            foreach (var handler in Object.FindObjectsOfType<HandlerPlaySoundAdvanced>())
            {
                if (handler._playTriggerId == triggerId)
                    SoundHandler.Play(handler);
                if (handler._stopTriggerId == triggerId)
                    SoundHandler.Stop(handler);
            }
            foreach (var handler in Object.FindObjectsOfType<HandlerInteractable>())
                if (handler._triggerId == triggerId && handler._interactable != null)
                    (For(handler._interactable) as InteractiveSim)?.TriggerInteract(handler._interactionType);
            foreach (var handler in Object.FindObjectsOfType<HandlerFilterOnce>())
                Get<FilterOnceSim>(handler).OnTrigger(triggerId);
            foreach (var handler in Object.FindObjectsOfType<HandlerLamp>())
            {
                if (handler._triggerId != triggerId)
                    continue;
                // HandlerLamp.SetLampControllerState: toggle mode toggles, then sets the state anyway
                foreach (var lamp in handler._lampControllers ?? new LampController[0])
                    if (lamp != null)
                    {
                        var sim = Get<LampSim>(lamp);
                        if (handler._isToggleMode)
                            sim.Switch(sim.State == Turnable.EState.On ? Turnable.EState.Off : Turnable.EState.On);
                        sim.Switch(handler._state ? Turnable.EState.On : Turnable.EState.Off);
                    }
                // SetControlledLampGroupState: a group really toggles
                foreach (var group in handler._controlledLampGroups ?? new ControlledLampGroup[0])
                    if (group != null)
                    {
                        var sim = Get<LampGroupSim>(group);
                        if (handler._isToggleMode)
                            sim.ToggleState();
                        else
                            sim.SetLightState(handler._state);
                    }
            }
        }

        // a player entering or leaving a zone, as TriggerZone.OnTriggerEnterAuthority/ExitAuthority sees it
        public static void SimulateZone(GameObject zone, bool enter)
        {
            foreach (var trigger in zone.GetComponents<TriggerZone>())
            {
                var mode = trigger._mode;
                var sim = Get<ZoneSim>(trigger);
                if (enter && (mode == TriggerZone.ETriggerZoneMode.Enter || mode == TriggerZone.ETriggerZoneMode.FirstEnter || mode == TriggerZone.ETriggerZoneMode.MoveInside
                              || mode == TriggerZone.ETriggerZoneMode.OnceEnter && !sim.Triggered))
                {
                    sim.Triggered = true;
                    Emit(trigger._triggerId);
                }
                else if (!enter && (mode == TriggerZone.ETriggerZoneMode.Exit || mode == TriggerZone.ETriggerZoneMode.LastExit))
                {
                    Emit(trigger._triggerId);
                }
            }
        }

        // ------------------------------------------------------------ exfil status

        // where a raid starts an exfil (ExfiltrationPoint.SetInitialStatus): shared timers and world
        // events wait for their requirements, the rest are open
        public static EExfiltrationStatus GetStatus(ExfiltrationPoint exfil)
        {
            if (exfil == null)
                return EExfiltrationStatus.NotPresent;
            if (ExfilStatus.TryGetValue(exfil, out var status))
                return status;
            bool waits = exfil.Settings != null && exfil.Settings.ExfiltrationType == EExfiltrationType.SharedTimer
                         || exfil.Requirements != null && exfil.Requirements.Any(r => r != null && r.Requirement == ERequirementState.WorldEvent);
            return waits ? EExfiltrationStatus.UncompleteRequirements : EExfiltrationStatus.RegularMode;
        }

        public static void SetStatus(ExfiltrationPoint exfil, EExfiltrationStatus status)
        {
            if (exfil == null)
                return;
            var previous = GetStatus(exfil);
            ExfilStatus[exfil] = status;
            _lastRealTime = EditorApplication.timeSinceStartup;
            // elevator doors and other subscribers follow it
            foreach (var door in Object.FindObjectsOfType<ExfiltrationDoor>())
                if (door.Subscribee == exfil)
                    Get<ExfiltrationDoorSim>(door).OnStatusChanged(previous, status);
            ExfilStatusChanged?.Invoke(exfil, status);
        }

        public static bool HasStatus(ExfiltrationPoint exfil) => exfil != null && ExfilStatus.ContainsKey(exfil);
    }

    public abstract class PreviewSim
    {
        public Component Target;

        // what the inspector shows while previewing
        public abstract string Status { get; }

        public virtual void Begin()
        {
        }

        public virtual void Update(float dt)
        {
        }

        public virtual void End()
        {
        }
    }

    // a zone's once-only state while previewing
    public class ZoneSim : PreviewSim
    {
        public bool Triggered;
        public override string Status => Triggered ? "triggered" : "ready";
    }
}
