using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.GameTriggers;
using EFT.Interactive;
using UnityEditor.Animations;
using UnityEngine;
using Random = UnityEngine.Random;

namespace TarkovSDK.Editor
{
    // The preview simulations: ports of the game's own code for each previewable component, written to
    // match the original line by line where it decides timing, motion and sound. Coroutines are kept as
    // coroutines, stepped by the preview clock. Each method names the game method it follows.
    //
    // Left out on purpose: network confirmation and sync, player animations and grips, bots, occlusion,
    // and anything only a raid has (quest conditions, items, damage).

    public abstract class RoutineSim : PreviewSim
    {
        protected sealed class Wait
        {
            public readonly float Seconds;
            public Wait(float seconds) => Seconds = seconds;
        }

        private class Routine
        {
            public readonly Stack<IEnumerator> Stack = new Stack<IEnumerator>();
            public float WaitUntil;
        }

        private readonly List<Routine> _routines = new List<Routine>();
        protected static float DeltaTime { get; private set; }

        protected object StartRoutine(IEnumerator routine)
        {
            var r = new Routine();
            r.Stack.Push(routine);
            _routines.Add(r);
            Step(r);
            return r;
        }

        protected void StopRoutine(object routine)
        {
            if (routine is Routine r)
                _routines.Remove(r);
        }

        public override void Update(float dt)
        {
            DeltaTime = dt;
            foreach (var r in _routines.ToList())
                if (r.WaitUntil <= TarkovPreview.Time)
                    Step(r);
        }

        // runs a routine until it waits or yields a frame, like Unity's coroutine scheduler
        private void Step(Routine r)
        {
            while (r.Stack.Count > 0)
            {
                var top = r.Stack.Peek();
                if (!top.MoveNext())
                {
                    r.Stack.Pop();
                    continue;
                }
                switch (top.Current)
                {
                    case Wait w:
                        r.WaitUntil = TarkovPreview.Time + w.Seconds;
                        return;
                    case IEnumerator nested:
                        r.Stack.Push(nested);
                        continue;
                    default:
                        return; // null: next frame
                }
            }
            _routines.Remove(r);
        }

        protected static float RandomIn(Vector2 range) => Random.Range(range.x, range.y);
    }

    // ------------------------------------------------------------ doors, switches and other interactive objects

    // WorldInteractiveObject with Door's and Switch's overrides
    public class InteractiveSim : RoutineSim
    {
        // EFTHardSettings.DoorCurve: the curve every door and container opens by
        public static readonly AnimationCurve DoorCurve = new AnimationCurve(
            new Keyframe(0f, 0f, 1.1147f, 1.1147f), new Keyframe(0.5681f, 0.5459f, 0.6058f, 0.6058f),
            new Keyframe(1.718f, 0.9762f, 0.0966f, 0.0966f), new Keyframe(2.5f, 1f, 0.0094f, 0.0094f));
        // EFTHardSettings.DelayToOpenContainer, before a container or plain interactive object reacts
        public const float DelayToOpenContainer = 0.55f;

        // WorldInteractiveObject.InteractionState
        protected class InteractionState
        {
            public EDoorState ResultState;
            public bool Break;
            public float ResultAngle, ProgressTime, Duration, Speed = 1f, StartTime, EndTime;
            private float _initAngle, _deltaAngle;
            private AnimationCurve _curve;

            public AnimationCurve ProgressCurve => _curve ?? DoorCurve;
            public bool IsInProgress => ProgressTime < Duration && !Break;
            public float CurrentAngle => _initAngle + ProgressCurve.Evaluate(ProgressTime) * _deltaAngle;

            public void UpdateTime(float deltaTime) => ProgressTime += deltaTime * Speed;

            public void InitSmoothOpen(EDoorState state, float initAngle, float resultAngle, AnimationCurve progressCurve)
            {
                _curve = progressCurve ?? DoorCurve;
                Duration = CurveDuration(_curve);
                Break = false;
                ResultState = state;
                ResultAngle = resultAngle * _curve[_curve.length - 1].value;
                _initAngle = initAngle;
                _deltaAngle = Mathf.DeltaAngle(initAngle, resultAngle);
                ProgressTime = 0f;
            }

            public void SetEndData(InteractionState other)
            {
                ResultState = other.ResultState;
                _curve = other.ProgressCurve;
                Duration = CurveDuration(_curve);
                EndTime = TarkovPreview.Time;
            }

            private static float CurveDuration(AnimationCurve curve) => curve.length == 0 ? 0f : curve[curve.length - 1].time;
        }

        protected readonly InteractionState Interaction = new InteractionState();
        protected readonly InteractionState Previous = new InteractionState();
        private bool _shutPlayed;
        private float _angle;
        private EDoorState _state;

        public WorldInteractiveObject Wio => (WorldInteractiveObject)Target;
        public EDoorState State => _state;
        public EDoorState FallbackState { get; private set; }
        public float Angle => _angle;
        public event Action<EDoorState, EDoorState> StateChanged;

        public override string Status => $"{_state}  {_angle:0.#}°" + (Interaction.IsInProgress ? $"  (to {Interaction.ResultState})" : "");

        public override void Begin()
        {
            TarkovPreview.RecordHierarchy(Wio.transform);
            if (Wio._handle != null)
                TarkovPreview.RecordHierarchy(Wio._handle.transform);
            if (Wio.LockHandle != null)
                TarkovPreview.RecordHierarchy(Wio.LockHandle.transform);
            _state = Wio._doorState;
            // as the game's OnValidate leaves it: open, or shut
            _angle = GetAngle(_state == EDoorState.Open ? EDoorState.Open : EDoorState.Shut);
            // InteractiveSubscriber.Start: announcements and views that follow this object
            foreach (var subscriber in UnityEngine.Object.FindObjectsOfType<InteractiveSubscriber>())
                if (subscriber.Subscribee == Wio)
                    TarkovPreview.Get<SubscriberSim>(subscriber);
        }

        public void SetState(EDoorState value)
        {
            var previous = _state;
            _state = value;
            if (previous != value)
                StateChanged?.Invoke(previous, value);
        }

        // ------------------------------------------------------------ angle

        public static Vector3 GetRotationAxis(WorldInteractiveObject.EDoorAxis axis, Transform t)
        {
            if (t == null)
                return Vector3.zero;
            switch (axis)
            {
                case WorldInteractiveObject.EDoorAxis.X: return t.right;
                case WorldInteractiveObject.EDoorAxis.Y: return t.up;
                case WorldInteractiveObject.EDoorAxis.Z: return t.forward;
                case WorldInteractiveObject.EDoorAxis.XNegative: return -t.right;
                case WorldInteractiveObject.EDoorAxis.YNegative: return -t.up;
                case WorldInteractiveObject.EDoorAxis.ZNegative: return -t.forward;
                default: return Vector3.zero;
            }
        }

        public static Quaternion GetDoorRotation(WorldInteractiveObject wio, float angle) =>
            Quaternion.AngleAxis(angle, GetRotationAxis(wio.DoorAxis, wio.transform.parent));

        // WorldInteractiveObject.CurrentAngle's setter, with Switch's override
        protected virtual void SetAngle(float value)
        {
            var t = Wio.transform;
            if (Wio is Switch sw)
            {
                if (sw.DontAnimateRotation)
                    _angle = value;
                else
                    ApplyRotation(value);
                if (sw.ShutPosition.sqrMagnitude > 0f || sw.OpenPosition.sqrMagnitude > 0f)
                    t.localPosition = Vector3.Lerp(sw.ShutPosition, sw.OpenPosition, value / sw.OpenAngle);
                return;
            }
            ApplyRotation(value);
        }

        private void ApplyRotation(float value)
        {
            _angle = value;
            var t = Wio.transform;
            if (t.parent != null && !Wio._keepRotation)
                t.rotation = GetDoorRotation(Wio, value) * t.parent.rotation;
        }

        // WorldInteractiveObject.GetAngle, with Door's breach angle
        public float GetAngle(EDoorState state)
        {
            if (state == EDoorState.Breaching && Wio is Door door && door.KickCurve != null && door.KickCurve.length > 0)
                return door.KickCurve[door.KickCurve.length - 1].value * door.OpenAngle;
            switch (state)
            {
                case EDoorState.Open: return Wio.OpenAngle;
                case EDoorState.Locked:
                case EDoorState.Shut: return Wio.CloseAngle;
                default: return _angle;
            }
        }

        protected virtual AnimationCurve ProgressCurve =>
            Wio is Switch sw && sw.CustomProgressCurve != null && sw.CustomProgressCurve.length > 0 ? sw.CustomProgressCurve : DoorCurve;

        protected virtual Vector2 OpenSoundVolumeRange => new Vector2(0.6f, 0.75f);
        private static readonly Vector2 ShutSoundVolumeRange = new Vector2(0.2f, 0.3f);

        // ------------------------------------------------------------ interaction

        // what a player's interaction or a switch does: Player hands the result to Interact
        public void PlayerInteract(EInteractionType type)
        {
            LockForInteraction();
            Interact(type);
        }

        // WorldInteractiveObject.Interact(EInteractionType), what a trigger's HandlerInteractable calls:
        // a locked object is first forced shut (HasFlag(Open) is always true, Open being 0)
        public void TriggerInteract(EInteractionType type)
        {
            if ((_state & EDoorState.Locked) != 0)
                SetDoorState(EDoorState.Shut, force: true);
            Interact(type);
        }

        public void LockForInteraction()
        {
            if (_state != EDoorState.Interacting)
                FallbackState = _state;
            if (_state != EDoorState.Interacting)
                SetState(EDoorState.Interacting);
        }

        // WorldInteractiveObject.Interact and its Door, Switch and LootableContainer overrides
        public virtual void Interact(EInteractionType type)
        {
            if (Wio is Switch)
            {
                InteractNow(type);
                return;
            }
            if (Wio is Door)
            {
                switch (type)
                {
                    case EInteractionType.Open:
                        if (_state != EDoorState.Locked)
                            Open();
                        break;
                    case EInteractionType.Close: Close(); break;
                    case EInteractionType.Unlock: Unlock(); break;
                    case EInteractionType.Breach: KickOpen(); break;
                }
                return;
            }
            TarkovPreview.After(DelayToOpenContainer, () => InteractNow(type));
        }

        protected void InteractNow(EInteractionType type)
        {
            switch (type)
            {
                case EInteractionType.Open:
                    if (!(Wio is LootableContainer) || _state != EDoorState.Locked)
                        Open();
                    break;
                case EInteractionType.Close: Close(); break;
                case EInteractionType.Unlock: Unlock(); break;
                case EInteractionType.Lock: Lock(); break;
            }
        }

        public void Open()
        {
            SetDoorState(EDoorState.Open);
            if (Wio is Switch sw)
            {
                SetLampsState(sw, Turnable.EState.On);
                StartRoutine(HandleTurnOff(sw));
            }
        }

        public void Close() => SetDoorState(EDoorState.Shut);

        public void Lock()
        {
            Interaction.ResultState = EDoorState.Locked;
            SetState(EDoorState.Locked);
            SetAngle(GetAngle(EDoorState.Locked));
        }

        public void Unlock() => StartRoutine(UnlockCoroutine());

        private IEnumerator UnlockCoroutine()
        {
            if (Wio.LockHandle != null)
                yield return HandleOpen(Wio.LockHandle);
            SetState(EDoorState.Shut);
            SetAngle(GetAngle(EDoorState.Shut));
            PushTriggers(EDoorState.Shut);
        }

        // Door.KickOpen
        public void KickOpen()
        {
            if (!(Wio is Door door) || Interaction.ResultState == EDoorState.Breaching && !Interaction.Break)
                return;
            Interaction.InitSmoothOpen(EDoorState.Breaching, door.CloseAngle, door.OpenAngle, door.KickCurve);
            StartRoutine(SmoothDoorOpen(EDoorState.Open, 1f));
            // the BrokenDoor Door.Start found, breached from the door's own position (KickOpen(bool)'s default)
            var broken = BrokenDoorSim.Of(door);
            if (broken != null)
                TarkovPreview.Get<BrokenDoorSim>(broken).Breach(door.transform.position);
            if (door.BreachSound != null)
                TarkovPreview.Play(GetSoundPos(), door.BreachSound, 1f, door._breachSoundRolloff);
        }

        private bool CanStartInteraction(EDoorState state) =>
            _state != state && (Interaction.ResultState != state || Interaction.Break)
                            && (Previous.ResultState != state || Previous.EndTime + Interaction.Duration < TarkovPreview.Time);

        // WorldInteractiveObject.SetDoorState: a player's interaction plays the curve 1.5x faster
        public void SetDoorState(EDoorState state, bool force = false)
        {
            if (!CanStartInteraction(state))
            {
                if (!Interaction.IsInProgress && _state == EDoorState.Interacting)
                    SetState(FallbackState);
                return;
            }
            Interaction.ResultState = state;
            if (force)
            {
                SetAngle(GetAngle(state));
                SetState(state);
            }
            else
            {
                Interaction.InitSmoothOpen(state, _angle, GetAngle(state), ProgressCurve);
                StartRoutine(SmoothDoorOpen(state, 1.5f));
            }
            PushTriggers(state);
        }

        private void PushTriggers(EDoorState state)
        {
            if (Wio.TriggersMap == null)
                return;
            foreach (var pair in Wio.TriggersMap)
                if (pair.State == state)
                    TarkovPreview.Emit(pair.TriggerId);
        }

        // Door.SmoothDoorOpenCoroutine: the player's hand reaches the door first, and Door plays the
        // base curve at normal speed
        protected virtual IEnumerator SmoothDoorOpen(EDoorState state, float speed)
        {
            if (Wio is Door)
            {
                if (Interaction.ResultState != EDoorState.Breaching)
                    yield return new Wait(0.5f);
                yield return BaseSmoothDoorOpen(state, 1f);
                yield break;
            }
            yield return BaseSmoothDoorOpen(state, speed);
            if (Wio is Switch sw)
                yield return SwitchAfterOpen(sw, state);
        }

        // WorldInteractiveObject.SmoothDoorOpenCoroutine
        private IEnumerator BaseSmoothDoorOpen(EDoorState state, float speed)
        {
            if (Interaction.Break)
                yield break;
            Interaction.StartTime = TarkovPreview.Time;
            if (Wio._handle != null && Interaction.ResultState == EDoorState.Open)
            {
                StartRoutine(HandleOpen(Wio._handle));
                yield return new Wait(HandleWait(Wio._handle));
            }
            PlaySound(Interaction.ResultState, RandomIn(OpenSoundVolumeRange));
            _shutPlayed = false;
            Interaction.Speed = speed;
            int steps = (int)(Interaction.Duration * 1000f);
            while (Interaction.IsInProgress && steps-- > 0)
            {
                SetAngle(Interaction.CurrentAngle);
                Interaction.UpdateTime(DeltaTime);
                if (Interaction.ResultState == EDoorState.Shut && FallbackState != EDoorState.Locked && !_shutPlayed
                    && Interaction.ProgressTime > Interaction.Duration - Wio.ShutShift)
                    PlayShut();
                yield return null;
            }
            if (!Interaction.Break)
            {
                if (!_shutPlayed && Interaction.ResultState == EDoorState.Shut && FallbackState != EDoorState.Locked)
                    PlayShut();
                SetState(Interaction.ResultState == EDoorState.Breaching ? EDoorState.Open : Interaction.ResultState);
                SetAngle(Interaction.ResultAngle);
                Previous.SetEndData(Interaction);
            }
        }

        // Switch.SmoothDoorOpenCoroutine after the base: its delay, then everything it operates
        private IEnumerator SwitchAfterOpen(Switch sw, EDoorState state)
        {
            if (sw.Delay > 0f)
                yield return new Wait(sw.Delay);
            if (state != EDoorState.Open)
                yield break;
            if (sw.NextSwitches != null)
                foreach (var next in sw.NextSwitches)
                {
                    if (next?.Switch == null)
                        continue;
                    var sim = (InteractiveSim)TarkovPreview.For(next.Switch);
                    if (sim.State != EDoorState.Interacting && (next.Operation != EInteractionType.Open || sim.State != EDoorState.Locked))
                        sim.PlayerInteract(next.Operation);
                }
            if (sw.Door != null)
            {
                var door = (InteractiveSim)TarkovPreview.For(sw.Door);
                if (door.State != EDoorState.Interacting)
                {
                    if (door.State == EDoorState.Locked)
                        door.SetState(EDoorState.Shut);
                    door.PlayerInteract(sw.Interaction);
                }
            }
            var exfil = sw.ExfiltrationPoint;
            if (exfil != null && (sw.ConditionStatus == null || sw.ConditionStatus.Length < 1 || sw.ConditionStatus.Contains(TarkovPreview.GetStatus(exfil))))
                TarkovPreview.SetStatus(exfil, sw.TargetStatus);
            SetLampsState(sw, Turnable.EState.On);
        }

        private IEnumerator HandleTurnOff(Switch sw)
        {
            if (!sw.AutoTurnOff)
                yield break;
            yield return new Wait(sw.AutoTurnOffSecondsDelay);
            Close();
        }

        private static void SetLampsState(Switch sw, Turnable.EState state)
        {
            if (sw.Lamps == null)
                return;
            foreach (var lamp in sw.Lamps)
            {
                if (lamp == null)
                    continue;
                var sim = TarkovPreview.Get<LampSim>(lamp);
                if (sim.State != Turnable.EState.Destroyed)
                    sim.Switch(state);
            }
        }

        // ------------------------------------------------------------ handle

        // the time a door waits for its handle: the first key that reaches the open pose
        protected static float HandleWait(DoorHandle handle)
        {
            if (handle.OpenAnimation == null)
                return 0f;
            foreach (var key in handle.OpenAnimation.keys)
                if (key.value >= 1f)
                    return key.time;
            return 0f;
        }

        // DoorHandle.OpenCoroutine
        protected IEnumerator HandleOpen(DoorHandle handle)
        {
            var t = handle.transform;
            if (handle.DownSound != null && handle.DownSound.Length != 0)
                TarkovPreview.Play(t.position, handle.DownSound[Random.Range(0, handle.DownSound.Length)], 0.8f, 14);
            var curve = handle.OpenAnimation;
            if (curve == null || curve.length == 0)
                yield break;
            float time = 0f;
            bool upPlayed = false;
            float end = curve[curve.length - 1].time;
            do
            {
                time += DeltaTime;
                float v = curve.Evaluate(time);
                t.localRotation = Quaternion.Slerp(handle.DefaultRotation, handle.OpenRotation, v);
                if (handle.pos)
                    t.localPosition = Vector3.Lerp(handle.DefaultPosition, handle.OpenPosition, v);
                if (handle.UpSound != null && handle.UpSound.Length != 0 && !upPlayed && time > end / 2f)
                {
                    TarkovPreview.Play(t.position, handle.UpSound[Random.Range(0, handle.UpSound.Length)], 0.8f, 14);
                    upPlayed = true;
                }
                yield return null;
            }
            while (time < end);
        }

        // ------------------------------------------------------------ sound

        // WorldInteractiveObject.GetSoundPos; a Door sounds at its view target
        protected Vector3 GetSoundPos()
        {
            var view = Wio.transform.TransformPoint(Wio.viewTarget1);
            if (Wio is Door)
                return view;
            var p = Wio.transform.position;
            return new Vector3(p.x, view.y, p.z);
        }

        // WorldInteractiveObject.PlaySound
        protected void PlaySound(EDoorState state, float volume)
        {
            if (Wio.OpenSound != null && Wio.OpenSound.Length != 0 && state == EDoorState.Open)
                TarkovPreview.Play(GetSoundPos(), Wio.OpenSound[Random.Range(0, Wio.OpenSound.Length)], volume, Wio._openSoundRolloff);
            if (Wio.SqueakSound != null && Wio.SqueakSound.Length != 0 && FallbackState != EDoorState.Locked)
                TarkovPreview.Play(GetSoundPos(), Wio.SqueakSound[Random.Range(0, Wio.SqueakSound.Length)], volume, Wio._squeakSoundRolloff);
        }

        // WorldInteractiveObject.PlayShut
        protected void PlayShut()
        {
            _shutPlayed = true;
            if (Wio.ShutSound != null && Wio.ShutSound.Length != 0)
                TarkovPreview.Play(GetSoundPos(), Wio.ShutSound[Random.Range(0, Wio.ShutSound.Length)], RandomIn(ShutSoundVolumeRange), Wio._shutSoundRolloff);
        }
    }

    // LootableContainer: delayed interaction, and its own opening that also slides between
    // ClosedPosition and OpenPosition (drawers)
    public class ContainerSim : InteractiveSim
    {
        private LootableContainer Container => (LootableContainer)Target;
        protected override Vector2 OpenSoundVolumeRange => new Vector2(0.5f, 0.55f);
        protected override AnimationCurve ProgressCurve => DoorCurve;

        // LootableContainer.SmoothDoorOpenCoroutine
        protected override IEnumerator SmoothDoorOpen(EDoorState state, float speed)
        {
            if (Container._handle != null && state == EDoorState.Open)
            {
                StartRoutine(HandleOpen(Container._handle));
                yield return new Wait(HandleWait(Container._handle));
            }
            PlaySound(state, RandomIn(OpenSoundVolumeRange));
            float angle = GetAngle(state);
            float startAngle = Angle;
            float time = 0f;
            float delta = Mathf.DeltaAngle(startAngle, angle);
            var curve = ProgressCurve;
            float duration = curve[curve.length - 1].time;
            bool shutPlayed = false;
            var t = Container.transform;
            while (time < duration)
            {
                float v = curve.Evaluate(time);
                SetAngle(startAngle + v * delta);
                t.localPosition = state == EDoorState.Open
                    ? Vector3.Lerp(Container.ClosedPosition, Container.OpenPosition, v)
                    : Vector3.Lerp(Container.ClosedPosition, Container.OpenPosition, 1f - v);
                time += DeltaTime * speed;
                if (state == EDoorState.Shut && !shutPlayed && time > duration - Container.ShutShift)
                {
                    PlayShut();
                    shutPlayed = true;
                }
                yield return null;
            }
            if (!shutPlayed && state == EDoorState.Shut)
                PlayShut();
            t.localPosition = state == EDoorState.Open ? Container.OpenPosition : Container.ClosedPosition;
            SetState(state);
            SetAngle(angle);
        }
    }

    // ------------------------------------------------------------ lamps

    // ControlledLampGroup: Start (its lamps forced on, then the initial state), RecordInitial and
    // SetLightState with its sounds. The game also plays the on sound at raid start for a group that
    // starts on; the preview applies the initial state silently. CullingLightObject/
    // CullingAdvancedLightObject only scale by IntensityMultiplier in the raid, so the preview writes
    // what their next update would (CullingLightObject.UpdateCullingObject, at full distance multiplier).
    public class LampGroupSim : PreviewSim
    {
        private static readonly int EmissionVisibilityId = Shader.PropertyToID("_EmissionVisibility");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        private struct MaterialInfo
        {
            public bool Visibility;
            public float Emission;
            public Color Color;
            public LampController Parent;
        }

        private readonly Dictionary<MeshRenderer, List<MaterialInfo>> _materials = new Dictionary<MeshRenderer, List<MaterialInfo>>();
        private readonly Dictionary<UnityEngine.Object, float> _initial = new Dictionary<UnityEngine.Object, float>();
        private bool _state;

        private ControlledLampGroup Group => (ControlledLampGroup)Target;

        public override string Status => _state ? "lights on" : "lights off";

        public override void Begin()
        {
            var g = Group;
            foreach (var light in Items(g._lights))
                TarkovPreview.RecordLight(light);
            foreach (var culling in Items(g._cullingLightObjects))
                TarkovPreview.RecordLight(culling._light);
            foreach (var area in Items(g._baseLights).Concat(Items(g._cullingAdvancedLightObjects).Select(c => c._light)).Where(a => a != null))
            {
                float intensity = area.m_Intensity;
                TarkovPreview.Record(area, () => area.m_Intensity = intensity);
            }
            foreach (var v in Items(g._volumetricLights))
                TarkovPreview.RecordEnabled(v);
            foreach (var flare in Items(g._multiFlareLights))
                TarkovPreview.RecordEnabled(flare);
            foreach (var r in Items(g._meshRenderers))
                TarkovPreview.RecordBlock(r);

            // RecordInitial
            foreach (var r in Items(g._meshRenderers))
            {
                if (_materials.ContainsKey(r))
                    continue;
                var parent = r.GetComponentInParent<LampController>();
                var list = new List<MaterialInfo>();
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null)
                        continue;
                    bool visibility = m.HasProperty(EmissionVisibilityId);
                    if (visibility)
                        list.Add(new MaterialInfo { Visibility = true, Emission = m.GetFloat(EmissionVisibilityId), Parent = parent });
                    else if (m.HasProperty(EmissionColorId))
                        list.Add(new MaterialInfo { Color = m.GetColor(EmissionColorId), Parent = parent });
                }
                _materials[r] = list;
            }
            foreach (var light in Items(g._lights))
                _initial[light] = light.intensity;
            foreach (var area in Items(g._baseLights))
                _initial[area] = area.m_Intensity;
            foreach (var c in Items(g._cullingLightObjects).Where(c => c._light != null))
                _initial[c] = c._useLightIntensityFromEditor ? c._maxLightIntensity : c._light.intensity;
            foreach (var c in Items(g._cullingAdvancedLightObjects).Where(c => c._light != null))
                _initial[c] = c._maxLightIntensity >= 0f ? c._maxLightIntensity : c._light.m_Intensity;

            // Start
            foreach (var lamp in Items(g._lampControllers))
            {
                var sim = TarkovPreview.Get<LampSim>(lamp);
                if (sim.State != Turnable.EState.On)
                    sim.Switch(Turnable.EState.On);
            }
            _state = g._initialLightState;
            Apply(_state);
        }

        public void ToggleState() => SetLightState(!_state);

        public void SetLightState(bool state)
        {
            if (_state != state)
                PlaySfx(state ? Group._turnOnSFX : Group._turnOffSFX);
            _state = state;
            Apply(state);
        }

        // ControlledLampGroup.PlaySfx: forced stereo, at _sfxRootPosition, rolloff _soundRolloff, volume 1
        private void PlaySfx(AudioClip clip)
        {
            if (clip == null)
                return;
            var g = Group;
            var position = g._sfxRootPosition != null ? g._sfxRootPosition.position : g.transform.position;
            TarkovPreview.Play(position, clip, 1f, g._soundRolloff);
        }

        private void Apply(bool state)
        {
            var g = Group;
            float multiplier = state ? 1f : 0f;
            foreach (var flare in Items(g._multiFlareLights))
                flare.enabled = state;
            foreach (var light in Items(g._lights))
            {
                light.enabled = state;
                light.intensity = state && _initial.TryGetValue(light, out var v) ? v : 0f;
            }
            foreach (var area in Items(g._baseLights))
                area.m_Intensity = state && _initial.TryGetValue(area, out var v) ? v : 0f;
            foreach (var v in Items(g._volumetricLights))
                v.enabled = state;
            foreach (var c in Items(g._cullingLightObjects).Where(c => c._light != null))
                c._light.intensity = (_initial.TryGetValue(c, out var v) ? v : c._light.intensity) * multiplier;
            foreach (var c in Items(g._cullingAdvancedLightObjects).Where(c => c._light != null))
                c._light.m_Intensity = (_initial.TryGetValue(c, out var v) ? v : c._light.m_Intensity) * multiplier;
            foreach (var r in Items(g._meshRenderers))
            {
                if (!_materials.TryGetValue(r, out var infos))
                    continue;
                int infoIndex = 0;
                var materials = r.sharedMaterials;
                for (int j = 0; j < materials.Length && infoIndex < infos.Count; j++)
                {
                    var m = materials[j];
                    var info = infos[infoIndex];
                    bool on = state && !(info.Parent != null && TarkovPreview.Find(info.Parent) is LampSim lamp && lamp.State == Turnable.EState.Destroyed);
                    var block = new MaterialPropertyBlock();
                    if (info.Visibility)
                    {
                        if (m == null || !m.HasProperty(EmissionVisibilityId))
                            continue;
                        block.SetFloat(EmissionVisibilityId, on ? info.Emission : 0f);
                    }
                    else
                    {
                        if (m == null || !m.HasProperty(EmissionColorId))
                            continue;
                        block.SetColor(EmissionColorId, on ? info.Color : new Color(0f, 0f, 0f, 0f));
                    }
                    r.SetPropertyBlock(block, j);
                    infoIndex++;
                }
            }
        }

        private static IEnumerable<T> Items<T>(T[] array) where T : UnityEngine.Object => (array ?? new T[0]).Where(x => x != null);
    }

    // LampController: ManualUpdate, Switch, TurnLights and VisualUpdate, run at 60 frames a second
    // (the game updates a steady lamp every 20th frame, and only flickering states every frame)
    public class LampSim : PreviewSim
    {
        private const float FrameTime = 1f / 60f;

        // LampController.Blinker
        private class Blinker
        {
            private readonly int _seed;
            private int _band = -1;
            private float _threshold;

            public Blinker(int seed) => _seed = seed;

            public bool IsBlink(float time, float bandWidth, float randomness)
            {
                time *= bandWidth;
                int band = (int)time;
                float fraction = time - band;
                if (_band != band)
                {
                    _band = band;
                    _threshold = (RandomizeInt(band ^ _seed) & 0xFF) * (1f / 255f);
                    _threshold = 0.5f + (_threshold - 0.5f) * randomness;
                }
                if (fraction <= _threshold)
                    return false;
                _threshold = float.MaxValue;
                return true;
            }
        }

        // FastRndom.RandomizeInt
        private static int RandomizeInt(int input)
        {
            input++;
            input ^= input << 7;
            input ^= input >> 3;
            return input >= 0 ? input : -input;
        }

        private LampController Lamp => (LampController)Target;
        public Turnable.EState State { get; private set; }

        private int _seed, _frame;
        private float _accumulator, _time;
        private Blinker _blinker;
        private bool _lightsOn, _flickArmed = true;
        private float _switchEnd, _onLength, _offLength, _blinkEnd = float.MaxValue, _lastIntensity = -1f;
        // LampController's float_1/3/4/5: StartFlickering's strong flicker fading back. They start at 0,
        // so in the game a lamp's own TurnedOnFlickering only shows after a blast has flickered it
        private float _turnedOnFlickering, _flickerFrom, _flickerTo, _flickerLength, _flickerProgress;
        private float[] _lightIntensity, _areaIntensity;
        private Color[] _customColor;
        private AudioSource _loop;

        public override string Status => State + (_lightsOn ? $"  intensity {Mathf.Max(0f, _lastIntensity):0.00}" : "  dark");

        public override void Begin()
        {
            var lamp = Lamp;
            var p = lamp.transform.position;
            _seed = (int)Mathf.Repeat(p.x + p.y * 10f + p.z * 100f, 1000f);
            _blinker = new Blinker(_seed);
            _turnedOnFlickering = lamp.TurnedOnFlickering;

            var lights = lamp.Lights ?? new Light[0];
            _lightIntensity = new float[lights.Length];
            for (int i = 0; i < lights.Length; i++)
                if (lights[i] != null)
                {
                    TarkovPreview.RecordLight(lights[i]);
                    _lightIntensity[i] = lights[i].intensity;
                }
            var area = lamp.AreaAndTubeLights ?? new BaseLight[0];
            _areaIntensity = new float[area.Length];
            for (int i = 0; i < area.Length; i++)
                if (area[i] != null)
                {
                    TarkovPreview.RecordEnabled(area[i]);
                    _areaIntensity[i] = area[i].m_Intensity;
                }
            var custom = lamp.CustomLights ?? new AdvancedLight[0];
            _customColor = custom.Select(c => c != null ? c.Color : Color.black).ToArray();
            foreach (var c in custom)
                TarkovPreview.RecordEnabled(c);
            foreach (var flare in lamp.MultiFlareLights ?? new MultiFlare.FlareLight[0])
                TarkovPreview.RecordEnabled(flare);
            foreach (var go in Objects())
                TarkovPreview.RecordActive(go);
            foreach (var m in Emission())
                TarkovPreview.RecordBlock(m.Renderer);

            // the authored state, without the raid's start-up sequence
            State = lamp.LampState == Turnable.EState.TurningOn ? Turnable.EState.On
                : lamp.LampState == Turnable.EState.TurningOff || lamp.LampState == Turnable.EState.SmoothOff ? Turnable.EState.Off : lamp.LampState;
            TurnLights(State == Turnable.EState.On || State == Turnable.EState.ConstantFlickering, true);
            if (_lightsOn)
                VisualUpdate(1f);
        }

        private IEnumerable<GameObject> Objects() =>
            (Lamp.OnObjects ?? new GameObject[0]).Concat(Lamp.OffObjects ?? new GameObject[0]).Concat(Lamp.DestroyedObjects ?? new GameObject[0]).Where(go => go != null);

        private IEnumerable<MaterialData> Emission() =>
            (Lamp._materialsWithEmission ?? new MaterialEmission[0]).Cast<MaterialData>()
            .Concat(Lamp._legacyMaterialsWithEmission ?? new MaterialColor[0]).Where(m => m != null && m.Renderer != null);

        public override void End() => StopLoop();

        public override void Update(float dt)
        {
            _accumulator += dt;
            while (_accumulator >= FrameTime)
            {
                _accumulator -= FrameTime;
                _time += FrameTime;
                ManualUpdate();
            }
        }

        // LampController.Switch
        public void Switch(Turnable.EState switchTo)
        {
            var lamp = Lamp;
            switch (switchTo)
            {
                default:
                    _offLength = 0f;
                    _switchEnd = _time;
                    break;
                case Turnable.EState.On:
                    if (!lamp.gameObject.activeSelf)
                    {
                        TarkovPreview.RecordActive(lamp.gameObject);
                        lamp.gameObject.SetActive(true);
                    }
                    _onLength = Mathf.Max(0.1f, Random.Range(lamp.TurnedOnLengthMinMax.x, lamp.TurnedOnLengthMinMax.y));
                    _switchEnd = _time + _onLength;
                    State = Turnable.EState.TurningOn;
                    PlayClip(lamp.TurnedOnClipStart);
                    break;
                case Turnable.EState.Off:
                    TurnLights(false, true);
                    _offLength = Mathf.Max(0.1f, Random.Range(lamp.TurnedOffLengthMinMax.x, lamp.TurnedOffLengthMinMax.y));
                    _switchEnd = _time + _offLength;
                    State = Turnable.EState.TurningOff;
                    break;
                case Turnable.EState.Destroyed:
                    State = Turnable.EState.Destroyed;
                    TurnLights(false, true);
                    StopLoop();
                    break;
                case Turnable.EState.ConstantFlickering:
                    TurnLights(true);
                    State = Turnable.EState.ConstantFlickering;
                    if (lamp.FlickeringClipLoop != null)
                        PlayClip(lamp.FlickeringClipLoop, true);
                    break;
                case Turnable.EState.TurningOn:
                case Turnable.EState.TurningOff:
                    break;
                case Turnable.EState.SmoothOff:
                    _offLength = Mathf.Max(0.1f, Random.Range(lamp.TurnedOffLengthMinMax.x, lamp.TurnedOffLengthMinMax.y));
                    _switchEnd = _time + _offLength;
                    State = Turnable.EState.TurningOff;
                    break;
            }
        }

        // LampController.StartFlickering: a nearby blast (strength 0..1)
        public void StartFlickering(float strength)
        {
            _flickerTo = _turnedOnFlickering;
            _flickerLength = strength * Lamp.IntactTime / 2f + Lamp.IntactTime / 2f;
            _flickerProgress = 0f;
            _flickerFrom = _turnedOnFlickering = 8f * (0.5f + strength);
        }

        // LampController.TryToInit: the states that update every frame
        private bool UpdatesEveryFrame()
        {
            switch (State)
            {
                case Turnable.EState.TurningOn: return Lamp.TurningOnFlickering > 0f;
                case Turnable.EState.TurningOff: return Lamp.TurningOffFlickering > 0f;
                case Turnable.EState.ConstantFlickering:
                case Turnable.EState.SmoothOff: return true;
                default: return false;
            }
        }

        // LampController.ManualUpdate
        private void ManualUpdate()
        {
            _frame = (_frame + 1) % 20;
            if (_frame != 0 && !UpdatesEveryFrame())
                return;
            var lamp = Lamp;
            float flicker = 0f;
            float intensity = 1f;
            bool on = _lightsOn;
            float time = _time;
            switch (State)
            {
                case Turnable.EState.TurningOn:
                {
                    if (lamp.Blinking)
                    {
                        if (_blinker.IsBlink(time, lamp.BlinkingFreq, lamp.Randomness))
                        {
                            on = true;
                            _blinkEnd = time + lamp.SingleBlinkLength;
                        }
                        else if (time > _blinkEnd)
                        {
                            on = false;
                            _blinkEnd = float.MaxValue;
                        }
                    }
                    else
                    {
                        on = true;
                    }
                    flicker = lamp.TurningOnFlickering;
                    float left = _onLength > 0f ? Mathf.Clamp01((_switchEnd - time) / _onLength) : 0f;
                    intensity = Mathf.Pow(1f - left, lamp.FadeInPower);
                    if (_switchEnd < time)
                    {
                        State = Turnable.EState.On;
                        _switchEnd = float.MaxValue;
                        PlayClip(lamp.TurnedOnClipLoop, true);
                        on = true;
                    }
                    break;
                }
                case Turnable.EState.TurningOff:
                    flicker = lamp.TurningOffFlickering;
                    intensity = Mathf.Pow((_switchEnd - time) / _offLength, lamp.FadeOutPower);
                    if (_switchEnd < time)
                    {
                        State = Turnable.EState.Off;
                        _switchEnd = float.MaxValue;
                        if (lamp.TurnOffClip != null)
                            PlayClip(lamp.TurnOffClip);
                        else
                            StopLoop();
                        on = false;
                    }
                    break;
                case Turnable.EState.On:
                    if (_flickerProgress < 1f)
                    {
                        _turnedOnFlickering = Mathf.Lerp(_flickerFrom, _flickerTo, _flickerProgress);
                        _flickerProgress += FrameTime / _flickerLength;
                    }
                    else
                    {
                        _turnedOnFlickering = _flickerTo;
                    }
                    if (lamp.AudioSource != null && lamp.AudioSource.loop && !TarkovPreview.IsPlaying(_loop))
                        PlayLoop();
                    flicker = _turnedOnFlickering;
                    break;
                case Turnable.EState.Destroyed:
                    return;
            }
            if (State == Turnable.EState.ConstantFlickering)
            {
                intensity = lamp._useCurve && lamp._animationCurve != null ? lamp._animationCurve.Evaluate(time * lamp.FlickeringFreq) : Mathf.PerlinNoise(time * lamp.FlickeringFreq, _seed);
                intensity /= 1.5f;
            }
            else if (flicker > 0f)
            {
                intensity += (Mathf.PerlinNoise(time * lamp.FlickeringFreq, _seed) - 0.5f) * flicker;
            }
            TurnLights(on);
            if (on)
                VisualUpdate(intensity);
        }

        // LampController.TurnLights
        private void TurnLights(bool on, bool force = false)
        {
            if (on == _lightsOn && !force)
                return;
            _lightsOn = on;
            var lamp = Lamp;
            foreach (var flare in lamp.MultiFlareLights ?? new MultiFlare.FlareLight[0])
                if (flare != null)
                    flare.enabled = on;
            foreach (var light in lamp.Lights ?? new Light[0])
                if (light != null)
                    light.enabled = on;
            foreach (var area in lamp.AreaAndTubeLights ?? new BaseLight[0])
                if (area != null)
                    area.enabled = on;
            foreach (var custom in lamp.CustomLights ?? new AdvancedLight[0])
                if (custom != null)
                    custom.enabled = on;
            foreach (var m in Emission())
                SetEmission(m, on ? 1f : 0f, true);
            foreach (var go in Objects())
                go.SetActive(false);
            var shown = State == Turnable.EState.Destroyed ? lamp.DestroyedObjects : on ? lamp.OnObjects : lamp.OffObjects;
            foreach (var go in shown ?? new GameObject[0])
                if (go != null)
                    go.SetActive(true);
        }

        // LampController.VisualUpdate
        private void VisualUpdate(float intensity)
        {
            if (Math.Abs(intensity - _lastIntensity) < Mathf.Epsilon)
                return;
            var lamp = Lamp;
            if (!_flickArmed)
            {
                _flickArmed = intensity < lamp.FlickLowTreshold;
            }
            else if (intensity > lamp.FlickHighTreshold)
            {
                _flickArmed = false;
                if (lamp.TurnOnClips != null && lamp.TurnOnClips.Length != 0)
                    PlayClip(lamp.TurnOnClips[Random.Range(0, lamp.TurnOnClips.Length)]);
            }
            _lastIntensity = intensity;
            var lights = lamp.Lights ?? new Light[0];
            for (int i = 0; i < lights.Length && i < _lightIntensity.Length; i++)
                if (lights[i] != null)
                    lights[i].intensity = _lightIntensity[i] * intensity;
            var custom = lamp.CustomLights ?? new AdvancedLight[0];
            for (int i = 0; i < custom.Length && i < _customColor.Length; i++)
                if (custom[i] != null)
                    custom[i].Color = _customColor[i] * intensity;
            foreach (var m in Emission())
                SetEmission(m, intensity, false);
        }

        private static readonly int EmissionVisibility = Shader.PropertyToID("_EmissionVisibility");

        // MaterialEmission and MaterialColor: TurnLights / SetIntensity through a property block
        private static void SetEmission(MaterialData data, float intensity, bool onOff)
        {
            var renderer = data.Renderer;
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            switch (data)
            {
                case MaterialEmission emission:
                {
                    float max = emission._maxEmissionVisibility;
                    if (max < 0f)
                    {
                        var materials = renderer.sharedMaterials;
                        var material = emission.MaterialId >= 0 && emission.MaterialId < materials.Length ? materials[emission.MaterialId] : null;
                        max = material != null && material.HasProperty(EmissionVisibility) ? material.GetFloat(EmissionVisibility) : 1f;
                    }
                    block.SetFloat(EmissionVisibility, max * intensity);
                    break;
                }
                case MaterialColor color when !string.IsNullOrEmpty(color.ParemeterName):
                    block.SetColor(color.ParemeterName, onOff ? intensity > 0f ? color.ColorOn : color.ColorOff : Color.Lerp(color.ColorOff, color.ColorOn, intensity));
                    break;
            }
            renderer.SetPropertyBlock(block);
        }

        // LampController.method_10 through the lamp's sound controller: one-shots play at the lamp,
        // 0.5 m down, rolloff at most 20 m; with an AudioSource, a loop plays that source's own clip
        private void PlayClip(AudioClip clip, bool loop = false)
        {
            var lamp = Lamp;
            float volume = Random.Range(lamp.FlickVolume.x, lamp.FlickVolume.y);
            if (clip == null)
                return;
            if (lamp.AudioSource != null && (!lamp.AudioSource.gameObject.activeInHierarchy || !lamp.AudioSource.enabled))
                return;
            if (!loop)
            {
                TarkovPreview.Play(lamp.transform.position + new Vector3(0f, -0.5f, 0f), clip, volume, Mathf.Min(20, lamp.Rolloff));
                return;
            }
            if (lamp.AudioSource != null && (lamp.AudioSource.clip != _loop?.clip || !TarkovPreview.IsPlaying(_loop)))
                PlayLoop();
        }

        private void PlayLoop()
        {
            var source = Lamp.AudioSource;
            StopLoop();
            if (source == null || source.clip == null)
                return;
            float pitch = 1f + (_seed / 1000f - 0.5f) * Lamp.PitchRandom;
            _loop = TarkovPreview.Play(source.transform.position, source.clip, source.volume, Mathf.Min(20, Lamp.Rolloff), true, pitch, source.transform);
        }

        private void StopLoop()
        {
            TarkovPreview.Stop(_loop);
            _loop = null;
        }
    }

    // ------------------------------------------------------------ elevator doors

    // ExfiltrationDoor: moves its door transforms between their open and closed poses when its exfil's
    // status enters or leaves OpenStatus
    public class ExfiltrationDoorSim : RoutineSim
    {
        private ExfiltrationDoor Door => (ExfiltrationDoor)Target;
        private bool _isOpen;
        private object _routine;
        private ExfiltrationDoor.DoorTransform[] _transforms;

        public override string Status => (_isOpen ? "open" : "closed") + (_routine != null ? " (moving)" : "") + $"  exfil {TarkovPreview.GetStatus(Door.Subscribee)}";

        public override void Begin()
        {
            var door = Door;
            TarkovPreview.RecordActive(door.gameObject);
            _transforms = door._doorTransforms != null && door._doorTransforms.Length > 0
                ? door._doorTransforms
                : new[]
                {
                    new ExfiltrationDoor.DoorTransform
                    {
                        Transform = door.transform, OpenPosition = door.OpenPosition, ClosePosition = door.ClosePosition,
                        OpenRotation = door.OpenRotation, CloseRotation = door.CloseRotation, OpenScale = door.OpenScale, CloseScale = door.ClosedScale,
                    },
                };
            foreach (var t in _transforms)
                if (t?.Transform != null)
                    TarkovPreview.Record(t.Transform);
            _isOpen = door.OpenStatus != null && door.OpenStatus.Contains(TarkovPreview.GetStatus(door.Subscribee));
        }

        // ExfiltrationDoor.OnStatusChangedHandler
        public void OnStatusChanged(EExfiltrationStatus previous, EExfiltrationStatus status, bool force = false)
        {
            var door = Door;
            bool open = door.OpenStatus != null && door.OpenStatus.Contains(status);
            if (open == _isOpen && !force)
                return;
            _isOpen = open;
            var clip = open ? door.OpenClip : door.CloseClip;
            if (_routine != null)
                StopRoutine(_routine);
            _routine = door.Curve != null && door.Curve.length > 1
                ? StartRoutine(LerpByCurve(open, clip, door.Delay))
                : StartRoutine(LerpDoor(open, clip, door.Delay));
        }

        private static Quaternion Valid(Quaternion q) => q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f ? Quaternion.identity : q;

        // ExfiltrationDoor.LerpByCurve
        private IEnumerator LerpByCurve(bool isOpen, AudioClip clip, float delay)
        {
            var door = Door;
            yield return new Wait(delay);
            if (door.DisableWhenClosed && _isOpen)
                door.gameObject.SetActive(true);
            float start = TarkovPreview.Time;
            var from = _transforms.Select(t => t.Transform != null ? (t.Transform.localPosition, t.Transform.localRotation, t.Transform.localScale) : default).ToArray();
            foreach (var t in _transforms)
                if (t.PlaySoundAt && clip != null)
                    TarkovPreview.Play(door.transform.position, clip, door.Volume, door.Rolloff);
            float duration = door.Curve[door.Curve.length - 1].time;
            float elapsed;
            do
            {
                elapsed = TarkovPreview.Time - start;
                float v = door.Curve.Evaluate(elapsed);
                for (int i = 0; i < _transforms.Length; i++)
                {
                    var t = _transforms[i];
                    if (t.Transform == null)
                        continue;
                    t.Transform.localRotation = Quaternion.Lerp(from[i].Item2, Valid(isOpen ? t.OpenRotation : t.CloseRotation), v);
                    t.Transform.localPosition = Vector3.Lerp(from[i].Item1, isOpen ? t.OpenPosition : t.ClosePosition, v);
                    t.Transform.localScale = Vector3.Lerp(from[i].Item3, isOpen ? t.OpenScale : t.CloseScale, v);
                }
                yield return null;
            }
            while (elapsed < duration);
            foreach (var t in _transforms)
                if (t.Transform != null)
                {
                    t.Transform.localRotation = Valid(isOpen ? t.OpenRotation : t.CloseRotation);
                    t.Transform.localPosition = isOpen ? t.OpenPosition : t.ClosePosition;
                    t.Transform.localScale = isOpen ? t.OpenScale : t.CloseScale;
                }
            _routine = null;
            if (door.DisableWhenClosed && !_isOpen)
                door.gameObject.SetActive(false);
        }

        // ExfiltrationDoor.LerpDoor, for doors without a curve (the old single-transform setup)
        private IEnumerator LerpDoor(bool isOpen, AudioClip clip, float delay)
        {
            var door = Door;
            yield return new Wait(delay);
            var t = door.transform;
            var q = Valid(isOpen ? door.OpenRotation : door.CloseRotation);
            var pos = isOpen ? door.OpenPosition : door.ClosePosition;
            if (clip != null)
                TarkovPreview.Play(t.position, clip, door.Volume, door.Rolloff);
            do
            {
                t.localRotation = Quaternion.Lerp(t.localRotation, q, DeltaTime * door.LerpSpeed);
                t.localPosition = Vector3.Lerp(t.localPosition, pos, DeltaTime * door.LerpSpeed);
                yield return null;
            }
            while (Quaternion.Angle(t.localRotation, q) > 0.05f || Vector3.Distance(t.localPosition, pos) > 0.0025f);
            t.localRotation = q;
            t.localPosition = pos;
            _routine = null;
        }
    }

    // ------------------------------------------------------------ automatic doors (Animator driven by triggers)

    // HandlerAnimator plus the Animator it drives, with the controller's states, conditions and clips
    // evaluated here (transitions jump instead of blending). Animation events at OnStartOpen /
    // OnStartClose play the AutoDoorSound on the animated object, as the game's do.
    public class AnimatorHandlerSim : PreviewSim
    {
        private HandlerAnimator Handler => (HandlerAnimator)Target;
        private Animator Animator => Handler._target;
        private AnimatorController _controller;
        private AnimatorState _state;
        private float _stateTime;
        private readonly Dictionary<string, object> _parameters = new Dictionary<string, object>();
        private readonly HashSet<string> _triggers = new HashSet<string>();

        public override string Status => _controller == null ? "no AnimatorController on the target"
            : $"{_state?.name ?? "?"}  {_stateTime:0.00}s  " + string.Join(", ", _parameters.Select(p => $"{p.Key}={p.Value}"));

        public override void Begin()
        {
            if (Animator == null)
                return;
            TarkovPreview.RecordHierarchy(Animator.transform);
            _controller = Animator.runtimeAnimatorController as AnimatorController;
            if (_controller == null || _controller.layers.Length == 0)
                return;
            foreach (var p in _controller.parameters)
                _parameters[p.name] = p.type == AnimatorControllerParameterType.Bool ? (object)p.defaultBool
                    : p.type == AnimatorControllerParameterType.Int ? (object)p.defaultInt
                    : p.type == AnimatorControllerParameterType.Float ? (object)p.defaultFloat : false;
            _state = _controller.layers[0].stateMachine.defaultState;
            // the default state as it ends, quietly: the raid starts with the door at rest
            var clip = _state?.motion as AnimationClip;
            if (clip != null)
            {
                _stateTime = clip.length;
                clip.SampleAnimation(Animator.gameObject, clip.length);
            }
        }

        // HandlerAnimator.AnimatorAction.Execute
        public void OnTrigger(string triggerId)
        {
            if (Handler._animatorActions == null)
                return;
            foreach (var action in Handler._animatorActions)
            {
                if (action.TriggerId != triggerId)
                    continue;
                if (!string.IsNullOrEmpty(action.BoolAction.Name))
                    _parameters[action.BoolAction.Name] = action.BoolAction.Value;
                if (!string.IsNullOrEmpty(action.IntAction.Name))
                    _parameters[action.IntAction.Name] = action.IntAction.Value;
                if (!string.IsNullOrEmpty(action.FloatAction.Name))
                    _parameters[action.FloatAction.Name] = action.FloatAction.Value;
                if (!string.IsNullOrEmpty(action.AnimationTriggerAction.TriggerName))
                    _triggers.Add(action.AnimationTriggerAction.TriggerName);
            }
            CheckTransitions();
        }

        public override void Update(float dt)
        {
            if (_controller == null || _state == null || Animator == null)
                return;
            CheckTransitions();
            var clip = _state.motion as AnimationClip;
            if (clip == null)
                return;
            float previous = _stateTime;
            _stateTime += dt * _state.speed;
            float length = Mathf.Max(0.0001f, clip.length);
            float sample = clip.isLooping ? Mathf.Repeat(_stateTime, length) : Mathf.Min(_stateTime, length);
            clip.SampleAnimation(Animator.gameObject, sample);
            if (previous < length || clip.isLooping)
                FireEvents(clip, previous, _stateTime, false);
        }

        private void CheckTransitions()
        {
            if (_state == null)
                return;
            var machine = _controller.layers[0].stateMachine;
            foreach (var transition in machine.anyStateTransitions.Concat(_state.transitions))
            {
                if (transition.destinationState == null || transition.mute || !ConditionsMet(transition))
                    continue;
                if (transition.hasExitTime)
                {
                    var clip = _state.motion as AnimationClip;
                    if (clip == null || _stateTime / Mathf.Max(0.0001f, clip.length) < transition.exitTime)
                        continue;
                }
                foreach (var c in transition.conditions)
                    _triggers.Remove(c.parameter);
                _state = transition.destinationState;
                _stateTime = 0f;
                if (_state.motion is AnimationClip entered)
                    FireEvents(entered, 0f, 0f, true);
                return;
            }
        }

        private bool ConditionsMet(AnimatorStateTransition transition)
        {
            foreach (var c in transition.conditions)
            {
                _parameters.TryGetValue(c.parameter, out var value);
                bool isTrigger = _controller.parameters.Any(p => p.name == c.parameter && p.type == AnimatorControllerParameterType.Trigger);
                float number = value is int i ? i : value is float f ? f : 0f;
                bool flag = isTrigger ? _triggers.Contains(c.parameter) : value is bool b && b;
                switch (c.mode)
                {
                    case AnimatorConditionMode.If: if (!flag) return false; break;
                    case AnimatorConditionMode.IfNot: if (flag) return false; break;
                    case AnimatorConditionMode.Greater: if (!(number > c.threshold)) return false; break;
                    case AnimatorConditionMode.Less: if (!(number < c.threshold)) return false; break;
                    case AnimatorConditionMode.Equals: if (!Mathf.Approximately(number, c.threshold)) return false; break;
                    case AnimatorConditionMode.NotEqual: if (Mathf.Approximately(number, c.threshold)) return false; break;
                }
            }
            return true;
        }

        private void FireEvents(AnimationClip clip, float from, float to, bool atStart)
        {
            foreach (var e in clip.events)
            {
                bool due = atStart ? e.time <= 0f : e.time > from && e.time <= to;
                if (!due)
                    continue;
                // AutoDoorSound.OnStartOpen / OnStartClose: PlayAtPoint at the door, rolloff 15
                var sound = Animator.GetComponent<AutoDoorSound>();
                if (sound == null)
                    continue;
                if (e.functionName == "OnStartOpen" && sound._openClip != null)
                    TarkovPreview.Play(Animator.transform.position, sound._openClip, sound._volume, 15);
                else if (e.functionName == "OnStartClose" && sound._closeClip != null)
                    TarkovPreview.Play(Animator.transform.position, sound._closeClip, sound._volume, 15);
            }
        }
    }

    // ------------------------------------------------------------ PA announcements

    // QueuePlayer: queued clips play one after another on every child speaker at once
    public class QueuePlayerSim : RoutineSim
    {
        private QueuePlayer Player => (QueuePlayer)Target;
        private readonly List<AudioClip> _queue = new List<AudioClip>();
        private readonly List<AudioSource> _playing = new List<AudioSource>();
        private object _checking;
        private AudioSource[] _sources;

        public override string Status => _playing.Any(TarkovPreview.IsPlaying) ? $"playing, {_queue.Count} queued" : _queue.Count > 0 ? $"{_queue.Count} queued" : "idle";

        public override void Begin()
        {
            // QueuePlayer.Awake: the active speakers under it
            _sources = Player.GetComponentsInChildren<AudioSource>();
            foreach (var s in _sources)
                TarkovPreview.RecordActive(s.gameObject);
        }

        public void Play(AudioClip clip)
        {
            if (clip == null || _sources.Length == 0)
                return;
            _queue.Add(clip);
            if (_checking == null)
                _checking = StartRoutine(CheckAvailability());
        }

        // QueuePlayer.CheckAvailability
        private IEnumerator CheckAvailability()
        {
            while (_queue.Count > 0)
            {
                if (_playing.All(s => !TarkovPreview.IsPlaying(s)))
                {
                    yield return null;
                    var clip = _queue[0];
                    _queue.RemoveAt(0);
                    _playing.Clear();
                    foreach (var source in _sources)
                        if (source != null)
                            _playing.Add(TarkovPreview.Play(source.transform.position, clip, source.volume, source.maxDistance, false, source.pitch));
                }
                yield return new Wait(Mathf.Max(0.05f, Player.QueueRefreshTime));
            }
            _checking = null;
        }
    }

    // InteractiveSubscriber: sounds and views that follow a door or switch's state
    public class SubscriberSim : RoutineSim
    {
        private InteractiveSubscriber Subscriber => (InteractiveSubscriber)Target;
        private readonly Dictionary<InteractiveSubscriber.PlaySettings, AudioSource> _loops = new Dictionary<InteractiveSubscriber.PlaySettings, AudioSource>();
        private readonly List<(Transform T, Vector3 Spin)> _spins = new List<(Transform, Vector3)>();

        public override string Status => Subscriber.Subscribee == null ? "no Subscribee" : $"follows {Subscriber.Subscribee.name}";

        public override void Begin()
        {
            var subscriber = Subscriber;
            if (subscriber.Subscribee == null)
                return;
            foreach (var view in subscriber.Views ?? new InteractiveSubscriber.ViewPerState[0])
                if (view?.GameObject != null)
                {
                    TarkovPreview.RecordActive(view.GameObject);
                    TarkovPreview.Record(view.GameObject.transform);
                }
            var source = TarkovPreview.For(subscriber.Subscribee) as InteractiveSim;
            if (source == null)
                return;
            source.StateChanged += OnStatusChanged;
            // Start: the current state right away
            OnStatusChanged(source.State, source.State);
        }

        public override void End()
        {
            foreach (var loop in _loops.Values)
                TarkovPreview.Stop(loop);
        }

        public override void Update(float dt)
        {
            base.Update(dt);
            foreach (var (t, spin) in _spins)
                if (t != null)
                    t.Rotate(spin, Space.Self);
        }

        // InteractiveSubscriber.OnStatusChangedHandler
        private void OnStatusChanged(EDoorState previous, EDoorState next)
        {
            ViewsSetActive(next);
            PlaySounds(previous, next);
        }

        private void PlaySounds(EDoorState fromState, EDoorState state)
        {
            var subscriber = Subscriber;
            foreach (var sound in subscriber.Sounds ?? new InteractiveSubscriber.PlaySettings[0])
            {
                if (sound == null)
                    continue;
                if ((state & sound.State) != 0)
                {
                    if (sound.Loop)
                    {
                        if (_loops.TryGetValue(sound, out var old))
                            TarkovPreview.Stop(old);
                        var captured = sound;
                        TarkovPreview.After(sound.StartDelay, () =>
                            _loops[captured] = TarkovPreview.Play(subscriber.transform.position, captured.Clip, captured.Volume, captured.MaxDistance, true));
                    }
                    else if (subscriber.QueuePlayer != null)
                    {
                        TarkovPreview.Get<QueuePlayerSim>(subscriber.QueuePlayer).Play(sound.Clip);
                    }
                    else if (sound.FromState != EDoorState.None && (fromState & sound.FromState) != 0)
                    {
                        var captured = sound;
                        TarkovPreview.After(sound.StartDelay, () =>
                        {
                            foreach (var t in (captured.AdditionalPositions ?? new Transform[0]).Concat(new[] { subscriber.transform }))
                                if (t != null)
                                    TarkovPreview.Play(t.position, captured.Clip, captured.Volume, captured.MaxDistance);
                        });
                    }
                    continue;
                }
                if (_loops.TryGetValue(sound, out var loop))
                {
                    var captured = sound;
                    TarkovPreview.After(sound.EndDelay + sound.FadeOut, () =>
                    {
                        TarkovPreview.Stop(loop);
                        _loops.Remove(captured);
                    });
                }
            }
        }

        private void ViewsSetActive(EDoorState state)
        {
            _spins.Clear();
            foreach (var view in Subscriber.Views ?? new InteractiveSubscriber.ViewPerState[0])
            {
                if (view?.GameObject == null)
                    continue;
                bool on = (state & view.State) != 0;
                if (view.EnableDisable)
                {
                    if (view.EnableContentInCoroutine)
                        foreach (Transform child in view.GameObject.transform)
                        {
                            TarkovPreview.RecordActive(child.gameObject);
                            child.gameObject.SetActive(on);
                        }
                    else
                        view.GameObject.SetActive(on);
                }
                if (view.Spin.sqrMagnitude > 0f && on)
                    _spins.Add((view.GameObject.transform, view.Spin));
            }
        }
    }

    // ------------------------------------------------------------ sound zones and other handlers

    // HandlerPlaySoundAdvanced: a random sound from its pool, once if Play Once
    public class SoundHandlerSim : PreviewSim
    {
        private HandlerPlaySoundAdvanced Handler => (HandlerPlaySoundAdvanced)Target;
        private AudioSource _source;
        private bool _played;

        public override string Status => TarkovPreview.IsPlaying(_source) ? "playing" : _played && Handler._playOnce ? "played (once)" : "ready";

        public override void End() => TarkovPreview.Stop(_source);

        public void Play()
        {
            var pool = Handler._soundsPool;
            if (pool == null || pool.Length < 1 || Handler._playOnce && _played)
                return;
            var config = pool[Random.Range(0, pool.Length)];
            if (config.AudioClip == null)
                return;
            var position = Handler._overrideSoundPosition != null ? Handler._overrideSoundPosition.position : Handler.transform.position;
            // a forced-stereo sound isn't spatialised, but still fades out by its rolloff
            _source = TarkovPreview.Play(position, config.AudioClip, config.Volume, config.RollOff, config.Loop, config.Pitch);
            _played = true;
        }

        public void Stop()
        {
            TarkovPreview.Stop(_source);
            _source = null;
            if (!Handler._playOnce)
                _played = false;
        }
    }

    public static class SoundHandler
    {
        public static void Play(HandlerPlaySoundAdvanced handler) => TarkovPreview.Get<SoundHandlerSim>(handler).Play();
        public static void Stop(HandlerPlaySoundAdvanced handler) => TarkovPreview.Get<SoundHandlerSim>(handler).Stop();
    }

    // HandlerFilterOnce: passes the first of its inputs on, until reset
    public class FilterOnceSim : PreviewSim
    {
        private HandlerFilterOnce Filter => (HandlerFilterOnce)Target;
        private bool _done;

        public override string Status => _done ? "passed, waiting for reset" : "ready";

        public void OnTrigger(string triggerId)
        {
            var filter = Filter;
            if (_done)
            {
                if (triggerId == filter._resetTriggerId)
                    _done = false;
            }
            else if (filter._inputTriggerIds != null && filter._inputTriggerIds.Contains(triggerId))
            {
                _done = true;
                TarkovPreview.Emit(filter._outputTriggerId);
            }
        }
    }
}
