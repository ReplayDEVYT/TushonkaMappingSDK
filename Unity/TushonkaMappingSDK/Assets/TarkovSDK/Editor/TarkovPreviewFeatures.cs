using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EFT.GameTriggers;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace TarkovSDK.Editor
{
    // Previews of the 1.8.0 map features, on the same clock and restore as TarkovPreview: fires (their particles and the
    // game's FlickeringLights), shootable triggers and explosions (TriggerBallistic, HandlerExplosion, HandlerGOState,
    // the blast's chain to other shootables), breachable doors (BrokenDoor.Breach after a kick), container groups (one
    // raid's pick, as the server mod makes it) and scripted world events (their timeline, sped up if wanted). Each
    // follows the game's or the plugin's code; the game's own effects (Effects.EmitGrenade) are stood in for by a
    // generated fireball, since they ship in the game's bundles.

    // FlickeringLights: Perlin noise between Min and Max at FlickerRate, offset per light at Start
    public class FlickerSim : PreviewSim
    {
        private float _randOffset;
        private Light _light;

        private FlickeringLights Flicker => (FlickeringLights)Target;
        public override string Status => _light != null ? $"intensity {_light.intensity:0.00}" : "no Light on the object";

        public override void Begin()
        {
            _randOffset = Random.Range(0f, 100f);
            _light = Flicker.GetComponent<Light>();
            TarkovPreview.RecordLight(_light);
        }

        public override void Update(float dt)
        {
            float t = Mathf.PerlinNoise(_randOffset, TarkovPreview.Time * Flicker.FlickerRate);
            if (_light != null)
                _light.intensity = Mathf.Lerp(Flicker.MinIntensity, Flicker.MaxIntensity, t);
        }
    }

    // a fire: every particle system and flickering light under its root, burning
    public class FireSim : PreviewSim
    {
        private static readonly Dictionary<Component, Transform> Roots = new Dictionary<Component, Transform>();

        static FireSim() => EditorApplication.hierarchyChanged += Roots.Clear;

        // the fire a part belongs to: the highest object above it whose particle systems and flickering lights all stay
        // within 4 m of it (a ripped bonfire's trigger can sit on a big scene root)
        public static Transform RootOf(Component part)
        {
            if (Roots.TryGetValue(part, out var cached) && cached != null)
                return cached;
            Transform best = null;
            var at = part.transform.position;
            for (var t = part.transform; t != null; t = t.parent)
            {
                var effects = t.GetComponentsInChildren<ParticleSystem>().Cast<Component>().Concat(t.GetComponentsInChildren<FlickeringLights>()).ToList();
                if (effects.Any(c => (c.transform.position - at).sqrMagnitude > 16f))
                    break;
                if (effects.Count > 0)
                    best = t;
            }
            return Roots[part] = best != null ? best : part.transform;
        }

        private Transform Root => (Transform)Target;
        public override string Status => "burning";

        public override void Begin()
        {
            foreach (var ps in Root.GetComponentsInChildren<ParticleSystem>())
                // the top system of each nested set (Simulate takes its children along); breach and blast effects that
                // wait for their event (not looping, not playing on awake) stay off
                if ((ps.transform.parent == null || ps.transform.parent.GetComponentInParent<ParticleSystem>() == null || ps.transform == Root)
                    && (ps.main.loop || ps.main.playOnAwake))
                    TarkovPreview.PlayParticles(ps);
            foreach (var flicker in Root.GetComponentsInChildren<FlickeringLights>())
                TarkovPreview.Get<FlickerSim>(flicker);
        }

        public override void End()
        {
            foreach (var ps in Root != null ? Root.GetComponentsInChildren<ParticleSystem>() : new ParticleSystem[0])
                TarkovPreview.StopParticles(ps);
        }

        public static void LightAll()
        {
            foreach (var trigger in Object.FindObjectsOfType<FlameDamageTrigger>())
                TarkovPreview.Get<FireSim>(RootOf(trigger));
            foreach (var flicker in Object.FindObjectsOfType<FlickeringLights>())
                TarkovPreview.Get<FireSim>(RootOf(flicker));
        }
    }

    // TriggerBallistic: a hit, or a blast whose ray reaches its collider, emits its id (once if Once)
    public class ShootableSim : PreviewSim
    {
        private bool _isDone;
        private TriggerBallistic Trigger => (TriggerBallistic)Target;
        public override string Status => _isDone ? "shot (once)" : "ready";

        // CanProceed
        private bool CanProceed() => !(Trigger._once && _isDone) && !string.IsNullOrEmpty(Trigger._triggerId) && Trigger._targetCollider != null;

        // OnHit: in the raid it emits as the shooter, on the shooter's machine only
        public void OnHit()
        {
            if (!CanProceed())
                return;
            if (Trigger._once)
                _isDone = true;
            TarkovPreview.Emit(Trigger._triggerId);
        }

        // OnExplosion: within the blast's reach and the first HighPolyCollider/Terrain hit on the line to it
        public void OnExplosion(Vector3 explosionPos, float maxDist)
        {
            if (!CanProceed())
                return;
            var position = Trigger._targetCollider.transform.position;
            if ((position - explosionPos).sqrMagnitude > maxDist * maxDist)
                return;
            if (Physics.Raycast(explosionPos, position - explosionPos, out var hit, maxDist, HighPolyWithTerrain, QueryTriggerInteraction.UseGlobal)
                && hit.collider != null && hit.collider.gameObject == Trigger._targetCollider.gameObject)
                OnHit();
        }

        private static int HighPolyWithTerrain => LayerMask.GetMask("HighPolyCollider", "Terrain");

        // ExplosionSharedMethods.OnExplosion's listeners: every shootable trigger whose Start subscribed
        public static void Explosion(Vector3 position, float maxDist)
        {
            // objects the blast just hid are out of the way
            Physics.SyncTransforms();
            foreach (var trigger in Object.FindObjectsOfType<TriggerBallistic>())
                if (trigger._targetCollider != null && !string.IsNullOrEmpty(trigger._triggerId))
                    TarkovPreview.Get<ShootableSim>(trigger).OnExplosion(position, maxDist);
        }
    }

    // HandlerExplosion: the first trigger sets it off; the effect at once, the blast a frame later
    public class ExplosionSim : PreviewSim
    {
        private bool _isDone;
        private HandlerExplosion Handler => (HandlerExplosion)Target;
        public override string Status => _isDone ? "exploded" : "ready";

        public void OnTrigger()
        {
            if (_isDone)
                return;
            _isDone = true;
            var position = Handler.transform.position;
            float radius = Handler._mineSettings._maxExplosionDistance;
            // the effect's materials may be generated assets: not from inside an inspector's GUI
            if (!string.IsNullOrEmpty(Handler._mineSettings._fxName))
                EditorApplication.delayCall += () => BlastEffect.Play(position, Mathf.Max(radius, 1f));
            TarkovPreview.After(0.02f, () =>
            {
                BlastEffect.ShowReach(position, radius);
                Debug.Log($"[Tarkov SDK] Preview: {Handler.name} explodes: {Handler._mineSettings._strength:0.#} strength and {Handler._mineSettings._fragmentsCount} fragment(s) " +
                          $"within {radius:0.#} m ({Handler._damageType}), effect '{Handler._mineSettings._fxName}' (the game's own in the raid)");
                ShootableSim.Explosion(position, radius);
            });
        }
    }

    // a stand-in for the game's explosion effect: fireball, smoke, sparks and a flash; and the blast's reach
    [InitializeOnLoad]
    public static class BlastEffect
    {
        private static readonly List<(Vector3 Position, float Radius, float Until)> Reaches = new List<(Vector3, float, float)>();

        static BlastEffect()
        {
            SceneView.duringSceneGui += view =>
            {
                if (Event.current.type != EventType.Repaint || Reaches.Count == 0)
                    return;
                Reaches.RemoveAll(r => r.Until < TarkovPreview.Time || !TarkovPreview.Running);
                foreach (var (position, radius, until) in Reaches)
                {
                    Handles.color = new Color(1f, 0.35f, 0.1f, Mathf.Clamp01(until - TarkovPreview.Time));
                    Handles.DrawWireDisc(position, Vector3.up, radius);
                    Handles.DrawWireDisc(position, Vector3.right, radius);
                    Handles.DrawWireDisc(position, Vector3.forward, radius);
                }
            };
        }

        public static void ShowReach(Vector3 position, float radius) => Reaches.Add((position, radius, TarkovPreview.Time + 1.5f));

        public static void Play(Vector3 position, float radius)
        {
            float size = Mathf.Clamp(radius / 10f, 0.4f, 3f);
            var root = TarkovPreview.Spawn("TarkovPreviewBlast", position, 8f);
            Burst(root.transform, TarkovFire.FlameMaterial(), 26, new Vector2(0.35f, 0.9f), new Vector2(2f, 7f) * size, new Vector2(1.2f, 3f) * size, 0f,
                new Color(1f, 0.85f, 0.5f), new Color(0.6f, 0.12f, 0.03f));
            Burst(root.transform, TarkovFire.SmokeMaterial(), 14, new Vector2(2.5f, 5f), new Vector2(0.8f, 2.5f) * size, new Vector2(1.5f, 3.5f) * size, -0.03f,
                new Color(0.2f, 0.19f, 0.18f), new Color(0.35f, 0.34f, 0.33f));
            var sparks = Burst(root.transform, TarkovFire.FlameMaterial(), 40, new Vector2(0.5f, 1.4f), new Vector2(6f, 14f) * size, new Vector2(0.03f, 0.08f), 1f,
                new Color(1f, 0.7f, 0.3f), new Color(1f, 0.3f, 0.1f));
            var renderer = sparks.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.velocityScale = 0.05f;

            var flash = root.AddComponent<Light>();
            flash.type = LightType.Point;
            flash.color = new Color(1f, 0.62f, 0.3f);
            flash.range = radius * 1.5f;
            float age = 0f;
            TarkovPreview.Animate(dt =>
            {
                if (flash == null)
                    return false;
                age += dt;
                flash.intensity = 8f * Mathf.Clamp01(1f - age / 0.45f);
                return age < 0.45f;
            });
        }

        private static ParticleSystem Burst(Transform parent, Material material, int count, Vector2 life, Vector2 speed, Vector2 size, float gravity, Color start, Color end)
        {
            var go = new GameObject("Burst") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = false;
            main.playOnAwake = false;
            main.duration = 0.2f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life.x, life.y);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed.x, speed.y);
            main.startSize = new ParticleSystem.MinMaxCurve(size.x, size.y);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.gravityModifier = gravity;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = count;
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Hemisphere;
            shape.radius = 0.3f;
            shape.rotation = new Vector3(-90f, 0f, 0f);
            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.dampen = 0.15f;
            limit.limit = 0.5f;
            var color = ps.colorOverLifetime;
            color.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(start, 0f), new GradientColorKey(end, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.7f, 0.4f), new GradientAlphaKey(0f, 1f) });
            color.color = gradient;
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            TarkovPreview.PlayParticles(ps);
            return ps;
        }
    }

    // HandlerGOState: its enable id switches the target on, its disable id off; the same id for both toggles
    public static class GOStateHandler
    {
        public static void OnTrigger(HandlerGOState handler, string triggerId)
        {
            if (handler._target == null)
                return;
            bool enable = handler._triggerIdEnable == triggerId, disable = handler._triggerIdDisable == triggerId;
            if (!enable && !disable)
                return;
            TarkovPreview.RecordActive(handler._target);
            bool toggle = !string.IsNullOrEmpty(handler._triggerIdEnable) && !string.IsNullOrEmpty(handler._triggerIdDisable)
                          && handler._triggerIdEnable.Equals(handler._triggerIdDisable, StringComparison.InvariantCultureIgnoreCase);
            // each subscription calls SetState; in toggle mode both are the same id, so it toggles twice, as the game does
            foreach (bool state in new[] { true, false }.Where(s => s ? enable : disable))
                if (toggle)
                    handler._target.SetActive(!handler._target.activeSelf);
                else
                    handler._target.SetActive(state);
        }
    }

    // BrokenDoor.Breach after Door.KickOpen: On objects on, Off off, itself on, its VFX played, the splinters made
    // dynamic with a small explosion force; SplintersGoneForGood then freezes them once asleep (or after 5 s)
    public class BrokenDoorSim : PreviewSim
    {
        private class Piece
        {
            public Transform Transform;
            public Vector3 Velocity, Spin;
            public bool Resting;
        }

        private readonly List<Piece> _pieces = new List<Piece>();
        private float _age;
        private bool _breached;

        private BrokenDoor Broken => (BrokenDoor)Target;
        public override string Status => !_breached ? "intact" : _pieces.All(p => p.Resting) ? "breached" : "breached, splinters falling";

        // Door.Start: transform.parent.GetComponentInChildren<BrokenDoor>(true)
        public static BrokenDoor Of(Door door) => door != null && door.transform.parent != null ? door.transform.parent.GetComponentInChildren<BrokenDoor>(true) : null;

        public static Door DoorOf(BrokenDoor broken) =>
            broken.transform.parent == null ? null : broken.transform.parent.GetComponentsInChildren<Door>(true).FirstOrDefault(d => Of(d) == broken);

        public void Breach(Vector3 playerPosition)
        {
            if (_breached)
                return;
            _breached = true;
            foreach (var go in Broken.Off ?? new GameObject[0])
                SetActive(go, false);
            foreach (var go in Broken.On ?? new GameObject[0])
                SetActive(go, true);
            SetActive(Broken.gameObject, true);
            // the game's mix of spaces, kept: both points are in the parent's space and the player's position is world
            var parent = Broken.transform.parent;
            var center = Broken.ExplosionCenter;
            var a = parent.InverseTransformPoint(center);
            var b = parent.InverseTransformPoint(new Vector3(-center.x, center.y, center.z));
            var explosion = Vector3.Distance(b, playerPosition) > Vector3.Distance(a, playerPosition) ? a : b;
            TarkovPreview.PlayParticles(Broken.VFX);
            foreach (var body in Broken.Splinters ?? new Rigidbody[0])
            {
                if (body == null)
                    continue;
                TarkovPreview.Record(body.transform);
                _pieces.Add(new Piece { Transform = body.transform, Velocity = ExplosionImpulse(body, 0.66f, explosion, 1f, 0.3f) / Mathf.Max(body.mass, 0.001f) });
            }
        }

        private static void SetActive(GameObject go, bool active)
        {
            if (go == null)
                return;
            TarkovPreview.RecordActive(go);
            go.SetActive(active);
        }

        // Rigidbody.AddExplosionForce(force, position, radius, upwardsModifier, Impulse): nothing beyond the radius
        private static Vector3 ExplosionImpulse(Rigidbody body, float force, Vector3 position, float radius, float upwards)
        {
            var from = position - Vector3.up * upwards;
            var offset = body.worldCenterOfMass - from;
            float distance = offset.magnitude;
            if (distance > radius)
                return Vector3.zero;
            return (distance > 1e-4f ? offset / distance : Vector3.up) * force * (1f - distance / radius);
        }

        public override void Update(float dt)
        {
            if (!_breached)
                return;
            _age += dt;
            var ignore = new HashSet<Collider>((Broken.IgnoreColliders ?? new Collider[0]).Where(c => c != null));
            foreach (var piece in _pieces)
            {
                if (piece.Resting || piece.Transform == null)
                    continue;
                // falling, landing with a little bounce and tumble, as the physics would leave them
                piece.Velocity += Physics.gravity * dt;
                var step = piece.Velocity * dt;
                var hits = Physics.RaycastAll(piece.Transform.position, step.normalized, step.magnitude + 0.02f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                    .Where(h => !ignore.Contains(h.collider) && !h.collider.transform.IsChildOf(Broken.transform)).OrderBy(h => h.distance).ToList();
                if (hits.Count > 0)
                {
                    var hit = hits[0];
                    piece.Transform.position = hit.point + hit.normal * 0.01f;
                    piece.Velocity = Vector3.Reflect(piece.Velocity, hit.normal) * 0.25f;
                    piece.Spin = Random.insideUnitSphere * 400f;
                    if (piece.Velocity.magnitude < 0.3f)
                    {
                        // lying on the surface
                        piece.Transform.rotation = Quaternion.FromToRotation(piece.Transform.forward, hit.normal) * piece.Transform.rotation;
                        piece.Resting = true;
                    }
                    continue;
                }
                piece.Transform.position += step;
                piece.Transform.rotation = Quaternion.Euler(piece.Spin * dt) * piece.Transform.rotation;
            }
            if (_age > 6f)
                foreach (var piece in _pieces)
                    piece.Resting = true;
        }
    }

    // one raid's pick of a group's containers, as the server mod draws it: AlwaysSpawn ones always, then Min..Max of the
    // rest weighted by their spawn chance, without putting back
    public class ContainerGroupSim : PreviewSim
    {
        private readonly List<LootableContainer> _picked = new List<LootableContainer>();
        private LootableContainersGroup Group => (LootableContainersGroup)Target;
        public override string Status => _picked.Count == 0 ? "none spawn this raid" : "this raid: " + string.Join(", ", _picked.Select(c => c.name));

        public void Roll()
        {
            var members = TarkovContainerGroups.Members(Group);
            _picked.Clear();
            var pool = new List<LootableContainer>();
            foreach (var container in members)
                if (container.SpawnType != ELootableContainerSpawnType.MayNotSpawn)
                    _picked.Add(container);
                else
                    pool.Add(container);
            int min = Mathf.Clamp(Group.Min, 0, pool.Count);
            int max = Mathf.Clamp(Mathf.Max(Group.Min, Group.Max), 0, pool.Count);
            int count = Random.Range(min, max + 1);
            for (int i = 0; i < count && pool.Count > 0; i++)
            {
                float Weight(LootableContainer c) => Mathf.Max(Mathf.Clamp(c.SpawnChance, 0, 100) / 100f, 0.001f);
                float roll = Random.value * pool.Sum(Weight);
                int pick = 0;
                for (; pick < pool.Count - 1; pick++)
                {
                    roll -= Weight(pool[pick]);
                    if (roll < 0f)
                        break;
                }
                _picked.Add(pool[pick]);
                pool.RemoveAt(pick);
            }
            foreach (var container in members)
            {
                TarkovPreview.RecordActive(container.gameObject);
                container.gameObject.SetActive(_picked.Contains(container));
            }
        }
    }

    // SdkWorldEvents.Run: the steps in order, each after its wait; a repeating event goes again after its pause
    public class WorldEventSim : RoutineSim
    {
        public static float Speed = 1f;

        private readonly Dictionary<AudioSource, AudioSource> _sounds = new Dictionary<AudioSource, AudioSource>();
        private object _routine;
        private string _status = "ready";
        private bool _running, _started;

        private TarkovWorldEvent Evt => (TarkovWorldEvent)Target;
        public override string Status => _status;

        // Begin: a trigger and the raid time both start it, the first one does; not twice at once; once unless it repeats
        public void Begin(bool force)
        {
            if (force)
            {
                StopRoutine(_routine);
                _running = false;
            }
            else if (_running || !Evt.Repeat && _started)
                return;
            _started = true;
            _running = true;
            _routine = StartRoutine(Run());
        }

        private IEnumerator Run()
        {
            var steps = Evt.Steps ?? new List<TarkovWorldEvent.Step>();
            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                if (step == null)
                    continue;
                if (step.Wait > 0f)
                {
                    _status = $"step {i + 1}/{steps.Count} in {step.Wait:0.#} s";
                    yield return new Wait(step.Wait / Mathf.Max(Speed, 0.01f));
                }
                _status = $"step {i + 1}/{steps.Count}: {TarkovWorldEvents.Describe(step)}";
                Do(step);
            }
            _running = false;
            _status = "finished";
            if (Evt.Repeat)
            {
                float pause = Random.Range(Evt.RepeatAfterMinMinutes, Mathf.Max(Evt.RepeatAfterMinMinutes, Evt.RepeatAfterMaxMinutes)) * 60f;
                _status = $"finished; again in {pause / 60f:0.#} min";
                yield return new Wait(pause / Mathf.Max(Speed, 0.01f));
                Begin(false);
            }
        }

        private void Do(TarkovWorldEvent.Step step)
        {
            switch (step.Kind)
            {
                case TarkovWorldEvent.StepKind.FireTrigger:
                    TarkovPreview.Emit(step.TriggerId);
                    break;
                case TarkovWorldEvent.StepKind.Show:
                case TarkovWorldEvent.StepKind.Hide:
                    if (step.Target != null)
                    {
                        TarkovPreview.RecordActive(step.Target);
                        step.Target.SetActive(step.Kind == TarkovWorldEvent.StepKind.Show);
                    }
                    break;
                case TarkovWorldEvent.StepKind.PlaySound:
                    if (step.Sound != null)
                    {
                        TarkovPreview.RecordActive(step.Sound.gameObject);
                        step.Sound.gameObject.SetActive(true);
                        if (_sounds.TryGetValue(step.Sound, out var playing))
                            TarkovPreview.Stop(playing);
                        _sounds[step.Sound] = TarkovPreview.Play(step.Sound.transform.position, step.Sound.clip, step.Sound.volume,
                            step.Sound.spatialBlend < 0.5f ? 0f : step.Sound.maxDistance, step.Sound.loop, step.Sound.pitch, step.Sound.transform);
                    }
                    break;
                case TarkovWorldEvent.StepKind.StopSound:
                    if (step.Sound != null && _sounds.TryGetValue(step.Sound, out var source))
                    {
                        TarkovPreview.Stop(source);
                        _sounds.Remove(step.Sound);
                    }
                    break;
                default:
                    TarkovPreview.Notify(step.Notification);
                    break;
            }
        }
    }

    // the SDK's own listeners Emit reaches that act on the world: spawns and mortar strikes (said, not done; a scene
    // prefab is shown or hidden, an asset copied in for the preview)
    public static class SpawnPreview
    {
        public static void OnTrigger(string triggerId)
        {
            foreach (var bots in Object.FindObjectsOfType<TarkovSpawnBots>(true))
                if (bots.TriggerId == triggerId)
                    Debug.Log($"[Tarkov SDK] Preview: {bots.name} would spawn {bots.Count} {bots.BotRole} bot(s) here (host only)");
            foreach (var loot in Object.FindObjectsOfType<TarkovSpawnLoot>(true))
                if (loot.TriggerId == triggerId)
                    Debug.Log($"[Tarkov SDK] Preview: {loot.name} would drop its loot here (host only)");
            foreach (var zone in Object.FindObjectsOfType<TarkovMortarZone>(true))
                if (zone.TriggerId == triggerId)
                    Debug.Log($"[Tarkov SDK] Preview: mortar zone {zone.name} would be shelled (host only)");
            foreach (var spawn in Object.FindObjectsOfType<TarkovSpawnPrefab>(true))
            {
                if (spawn.TriggerId != triggerId || spawn.Prefab == null)
                    continue;
                var prefab = spawn.Prefab;
                TarkovPreview.After(spawn.Delay, () =>
                {
                    if (prefab == null)
                        return;
                    if (spawn.Action != TarkovSpawnPrefab.SpawnAction.Spawn)
                    {
                        TarkovPreview.RecordActive(prefab);
                        prefab.SetActive(spawn.Action == TarkovSpawnPrefab.SpawnAction.Show);
                        return;
                    }
                    // a copy at each spawn point: the object's children, or the object
                    var points = spawn.transform.childCount > 0 ? spawn.transform.Cast<Transform>().ToList() : new List<Transform> { spawn.transform };
                    foreach (var point in points)
                    {
                        var holder = TarkovPreview.Spawn("TarkovPreviewSpawn", point.position, spawn.Lifetime > 0f ? spawn.Lifetime : 3600f);
                        holder.transform.rotation = point.rotation;
                        var copy = Object.Instantiate(prefab, holder.transform, false);
                        copy.transform.localPosition = Vector3.zero;
                        copy.transform.localRotation = Quaternion.identity;
                        copy.SetActive(true);
                        foreach (var t in copy.GetComponentsInChildren<Transform>(true))
                            t.gameObject.hideFlags = HideFlags.HideAndDontSave;
                    }
                });
            }
        }
    }

    // ------------------------------------------------------------ inspector panels

    [CustomEditor(typeof(FlickeringLights))]
    [CanEditMultipleObjects]
    public class FlickeringLightsPreviewEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            if (targets.Length != 1)
                return;
            var flicker = (FlickeringLights)target;
            if (flicker.GetComponent<Light>() == null)
                EditorGUILayout.HelpBox("Needs a Light on the same object: that is what it flickers.", MessageType.Warning);
            FirePreviewGui.Draw(flicker);
        }
    }

    [CustomEditor(typeof(FlameDamageTrigger))]
    public class FlameDamagePreviewEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("Everyone inside the trigger box burns (the game's flame damage, per body part). It needs a trigger collider; the game moves it to the Triggers layer.", MessageType.None);
            DrawDefaultInspector();
            FirePreviewGui.Draw((FlameDamageTrigger)target);
        }
    }

    internal static class FirePreviewGui
    {
        public static void Draw(Component part)
        {
            if (!PreviewGui.Begin("fire"))
                return;
            var root = FireSim.RootOf(part);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (PreviewGui.Button("Light", $"Plays the flames, smoke and sparks under '{root.name}' and flickers its lights as the game's FlickeringLights does."))
                    TarkovPreview.Get<FireSim>(root);
                if (PreviewGui.Button("Light Every Fire", "Every fire in the open scenes."))
                    FireSim.LightAll();
            }
            PreviewGui.End(root);
        }
    }

    [CustomEditor(typeof(TriggerBallistic))]
    public class ShootablePreviewEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("A bullet hitting Target Collider fires Trigger Id, and so does a blast whose line reaches it (so explosions chain). " +
                                    "Once: only the first time. In co-op it fires on the shooter's machine only.", MessageType.None);
            DrawDefaultInspector();
            var trigger = (TriggerBallistic)target;
            var listeners = TarkovTriggerLinks.Listeners(trigger._triggerId).ToList();
            EditorGUILayout.LabelField(listeners.Count == 0 ? $"Nothing listens to '{trigger._triggerId}'."
                                           : $"'{trigger._triggerId}' reaches: " + string.Join(", ", listeners.Select(l => $"{l.name} ({l.GetType().Name})")), EditorStyles.wordWrappedMiniLabel);
            if (!PreviewGui.Begin("shootable"))
                return;
            using (new EditorGUI.DisabledScope(trigger._targetCollider == null || string.IsNullOrEmpty(trigger._triggerId)))
                if (PreviewGui.Button("Shoot It", "A bullet hits its collider: its id fires, and whatever listens reacts (explosions, doors, lamps, sounds...)."))
                    TarkovPreview.Get<ShootableSim>(trigger).OnHit();
            PreviewGui.End(trigger);
        }
    }

    [CustomEditor(typeof(HandlerGOState))]
    public class GOStatePreviewEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("Switches Target on when the enable id fires and off on the disable id (the same id for both toggles it). Target needs a GameObjectStateSync, so players who join later see it the same.", MessageType.None);
            DrawDefaultInspector();
            var handler = (HandlerGOState)target;
            if (handler._target != null && handler._target.GetComponent<GameObjectStateSync>() == null)
                EditorGUILayout.HelpBox("Target has no GameObjectStateSync; Build Map adds one.", MessageType.Info);
            if (!PreviewGui.Begin("show / hide"))
                return;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (!string.IsNullOrEmpty(handler._triggerIdEnable) && PreviewGui.Button($"Fire '{handler._triggerIdEnable}'"))
                    TarkovPreview.Emit(handler._triggerIdEnable);
                if (!string.IsNullOrEmpty(handler._triggerIdDisable) && handler._triggerIdDisable != handler._triggerIdEnable && PreviewGui.Button($"Fire '{handler._triggerIdDisable}'"))
                    TarkovPreview.Emit(handler._triggerIdDisable);
            }
            PreviewGui.End(handler);
        }
    }

    [CustomEditor(typeof(BrokenDoor))]
    public class BrokenDoorPreviewEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("Kicking the door in turns this object and On on, Off off, plays VFX and drops the Splinters. Keep this object off; the door finds it in its own parent.", MessageType.None);
            DrawDefaultInspector();
            var broken = (BrokenDoor)target;
            var door = BrokenDoorSim.DoorOf(broken);
            if (door == null)
                EditorGUILayout.HelpBox("No Door under this object's parent uses it, so nothing breaks it.", MessageType.Warning);
            if (door == null || !PreviewGui.Begin("breach"))
                return;
            if (PreviewGui.Button("Kick In The Door", $"'{door.name}' is kicked open with its Kick Curve and Breach Sound, and this splinters it."))
                ((InteractiveSim)TarkovPreview.For(door)).PlayerInteract(EFT.EInteractionType.Breach);
            PreviewGui.End(broken);
        }
    }

    internal static class WorldEventPreviewGui
    {
        private static readonly float[] Speeds = { 1f, 4f, 16f, 60f };
        private static readonly string[] SpeedLabels = { "1x", "4x", "16x", "60x" };

        public static void Draw(TarkovWorldEvent evt)
        {
            if (!PreviewGui.Begin("world event"))
                return;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (PreviewGui.Button("Play From The Start", "Runs the steps now, as the event does when its time comes or its trigger fires: ids fire, objects switch, sounds play, notifications show."))
                    TarkovPreview.Get<WorldEventSim>(evt).Begin(true);
                int index = Mathf.Max(0, Array.IndexOf(Speeds, WorldEventSim.Speed));
                WorldEventSim.Speed = Speeds[EditorGUILayout.Popup(index, SpeedLabels, GUILayout.Width(60f))];
            }
            EditorGUILayout.LabelField(evt.Start == TarkovWorldEvent.StartMode.Trigger ? $"In the raid it starts when '{evt.TriggerId}' fires." :
                                       $"In the raid it starts {evt.EarliestMinute:0.#}-{evt.LatestMinute:0.#} min in" + (evt.Start == TarkovWorldEvent.StartMode.RaidTimeOrTrigger ? $", or when '{evt.TriggerId}' fires" : "") +
                                       (evt.Chance < 1f ? $" ({evt.Chance:P0} of raids)." : "."), EditorStyles.wordWrappedMiniLabel);
            PreviewGui.End(evt);
        }
    }
}
