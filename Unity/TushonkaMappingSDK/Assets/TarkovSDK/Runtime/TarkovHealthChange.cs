using System;
using UnityEngine;

namespace TarkovSDK
{
    // A change to the health of the player who fires a trigger id (CustomMapLoader's own; the game's HandlerDamage only
    // deals damage, per hitbox): damage, healing, a full restore, death, energy or hydration, once or repeated until a
    // stop trigger id. Editor-only data on an EditorOnly object: Build Map exports it to sdk_export.json and the client
    // plugin applies it through the player's ActiveHealthController.
    [AddComponentMenu("Tarkov SDK/Health Change")]
    public class TarkovHealthChange : MonoBehaviour
    {
        public enum ChangeKind
        {
            [Tooltip("Takes Amount HP from each body part picked (a destroyed head or chest kills, as in the game).")]
            Damage,
            [Tooltip("Gives Amount HP to each body part picked, up to its maximum. Destroyed parts stay destroyed.")]
            Heal,
            [Tooltip("Brings each body part picked back to full health, destroyed ones included, and stops their bleeding.")]
            Restore,
            [Tooltip("Kills the player.")]
            Kill,
            [Tooltip("Adds Amount energy (negative takes it away).")]
            Energy,
            [Tooltip("Adds Amount hydration (negative takes it away).")]
            Hydration,
        }

        // the game's EBodyPart
        [Flags]
        public enum Parts
        {
            Head = 1, Chest = 2, Stomach = 4, LeftArm = 8, RightArm = 16, LeftLeg = 32, RightLeg = 64,
            All = 127,
        }

        // the game's EDamageType, by name: what the death screen says killed the player
        public enum Cause
        {
            Environment, Undefined, Fall, Explosion, Barbed, Flame, Impact, Blunt, Poison, RadExposure, HotGases, LethalToxin,
        }

        [Tooltip("Apply it to the player who fires this trigger id (a zone or a switch; connect one in Triggers & Sounds).")]
        public string TriggerId = "";

        [Tooltip("Only once per raid to each player.")]
        public bool Once;

        [Tooltip("The chance it happens each time the trigger fires.")]
        [Range(0f, 1f)]
        public float Chance = 1f;

        public ChangeKind Change = ChangeKind.Damage;

        [Tooltip("The body parts it changes (Damage, Heal, Restore).")]
        public Parts BodyParts = Parts.All;

        [Tooltip("HP per body part (Damage, Heal), or energy / hydration points.")]
        public float Amount = 10f;

        [Tooltip("What the death screen says killed the player (Damage, Kill).")]
        public Cause DamageType = Cause.Environment;

        [Tooltip("How many times it's applied: 1 = once, 0 = until the Stop Trigger Id fires or the player dies.")]
        [Min(0)]
        public int Repeat = 1;

        [Tooltip("Seconds between repeats.")]
        [Min(0.1f)]
        public float Interval = 1f;

        [Tooltip("Stops the repeats for the player who fires it (a zone's Player Leaves, for a gas zone). Empty: they run their count.")]
        public string StopTriggerId = "";
    }
}
