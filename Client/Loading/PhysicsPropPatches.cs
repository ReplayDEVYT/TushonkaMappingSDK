using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace mizmii.CustomMapLoader.Loading
{
    // Physics props (SmallPhysicsObject, from Arena) apply a push only to a rigidbody that is asleep:
    // PlayCollision skips any that is awake, so a prop already flying isn't pushed again. Awake puts
    // them to sleep, so the first push always works; after that a prop must fall asleep again, and one
    // that has landed on a mesh collider, uneven ground or another prop keeps jittering just above
    // Unity's sleep threshold and then ignores every bullet, touch and grenade.
    //
    //  - PhysicsPropAwakePatch: a higher sleep threshold, so a landed prop actually comes to rest
    //  - PhysicsPropPushPatch: just before a push, a prop that's awake but has all but stopped is put
    //    to sleep, so the push applies; one still moving is left alone, as the game intends
    internal class PhysicsPropAwakePatch : ModulePatch
    {
        // mass-normalised kinetic energy; Unity's default is 0.005, too low for small props on mesh colliders
        private const float SleepThreshold = 0.05f;

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(SmallPhysicsObject), "Awake");

        [PatchPostfix]
        private static void Postfix(SmallPhysicsObject __instance)
        {
            if (!MapRegistry.IsCustomMapActive() || __instance.Rigidbodies == null)
                return;
            foreach (var body in __instance.Rigidbodies)
                if (body != null)
                    body.sleepThreshold = Mathf.Max(body.sleepThreshold, SleepThreshold);
        }
    }

    internal class PhysicsPropPushPatch : ModulePatch
    {
        private const float RestingSpeed = 0.5f;        // m/s
        private const float RestingSpin = 1f;           // rad/s

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(SmallPhysicsObject), "PlayCollision");

        [PatchPrefix]
        private static void Prefix(SmallPhysicsObject __instance)
        {
            if (!MapRegistry.IsCustomMapActive() || __instance.Rigidbodies == null)
                return;
            foreach (var body in __instance.Rigidbodies)
            {
                if (body == null || body.isKinematic || body.IsSleeping())
                    continue;
                if (body.velocity.sqrMagnitude < RestingSpeed * RestingSpeed && body.angularVelocity.sqrMagnitude < RestingSpin * RestingSpin)
                    body.Sleep();
            }
        }
    }
}

namespace mizmii.CustomMapLoader.Loading
{
    // Explosions push physics props. SmallPhysicsObject has OnBlowUp (a push away from the blast, within
    // BlowUpThreshold metres, weaker further out), but Tarkov never calls it: only Arena did. Every explosion
    // (grenades, mines, mortar shells, HandlerExplosion) ends in ExplosionSharedMethods.Explosion, which runs on
    // every machine, so after it each prop is offered the blast. The Building Builder's breakable wall holes are
    // props too: the blast swaps their intact piece for the loose chunks and throws those.
    internal class PhysicsPropExplosionPatch : ModulePatch
    {
        private static readonly FieldInfo Instance = AccessTools.Field(typeof(SmallPhysicsSystem), "_instance");
        private static readonly FieldInfo Objects = AccessTools.Field(typeof(SmallPhysicsSystem), "_smallPhysicsObjects");

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(EFT.ExplosionSharedMethods), nameof(EFT.ExplosionSharedMethods.Explosion));

        [PatchPostfix]
        private static void Postfix(EFT.IExplosiveItem grenadeItem, Vector3 explosionPosition)
        {
            if (!MapRegistry.IsCustomMapActive() || grenadeItem == null || grenadeItem.IsDummy)
                return;
            // the system's own getter would make one when the map has no props
            var system = Instance?.GetValue(null) as SmallPhysicsSystem;
            if (system == null || !(Objects?.GetValue(system) is System.Collections.Generic.List<SmallPhysicsObject> list))
                return;
            // a breaking hole wakes its chunks, which add themselves to the list
            foreach (var prop in list.ToArray())
            {
                if (prop == null || !prop.gameObject.activeInHierarchy)
                    continue;
                try
                {
                    prop.OnBlowUp(explosionPosition);
                }
                catch (System.Exception e)
                {
                    Plugin.LogSource.LogWarning($"physics prop {prop.name} on explosion: {e.Message}");
                }
            }
        }
    }
}
