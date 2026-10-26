using System;
using System.Collections.Generic;
using UnityEngine;

namespace TarkovSDK
{
    // Health effects for the player who fires a trigger id (CustomMapLoader's own, a superset of the game's
    // HandlerEffect): bleeding, fractures, pain, contusion, stun, poison, stimulator buffs and the rest, through the
    // player's ActiveHealthController as the game applies them. Editor-only data on an EditorOnly object: Build Map
    // exports it to sdk_export.json and the client plugin applies it.
    [AddComponentMenu("Tarkov SDK/Effect")]
    public class TarkovEffect : MonoBehaviour
    {
        public enum EffectKind
        {
            [Tooltip("On the Body Part.")]
            LightBleeding,
            [Tooltip("On the Body Part.")]
            HeavyBleeding,
            [Tooltip("On the Body Part (arms and legs only).")]
            Fracture,
            [Tooltip("On the Body Part, for Duration seconds, at Strength.")]
            Pain,
            [Tooltip("Muffled hearing and a shaky view, for Duration seconds, at Strength.")]
            Contusion,
            [Tooltip("A flashbang's stun, up to Duration seconds, at Strength.")]
            Stun,
            [Tooltip("A swaying view, for Duration seconds.")]
            Disorientation,
            [Tooltip("Sand on the screen, for Duration seconds.")]
            SandingScreen,
            [Tooltip("Poisoning, as from bad food (an antidote or the Immunity skill may stop it).")]
            Intoxication,
            [Tooltip("Lethal toxin, as from the cultists' knives: kills unless cured. Starts after Delay, lasts Duration.")]
            LethalIntoxication,
            [Tooltip("Radiation exposure.")]
            RadExposure,
            [Tooltip("Stamina hits zero, for Duration seconds.")]
            StaminaZero,
            [Tooltip("Chronic fatigue: lower stamina, stacking.")]
            Fatigue,
            [Tooltip("Painkiller, as from a medicine.")]
            Painkiller,
            [Tooltip("A stimulator's or food's buffs and side effects, by the game's buff name (Buff).")]
            Buff,
            [Tooltip("Ends the negative effects (bleeding, fractures, pain, poison...) on the Body Part.")]
            RemoveNegativeEffects,
            [Tooltip("Stops the bleeding on the Body Part.")]
            StopBleeding,
        }

        // the game's EBodyPart
        public enum BodyPart { Head, Chest, Stomach, LeftArm, RightArm, LeftLeg, RightLeg }

        // the buff lists in globals (config.Health.Effects.Stimulator.Buffs)
        public enum BuffName
        {
            BuffsAdrenaline, BuffsGoldenStarBalm, BuffsPropital, BuffsSJ1TGLabs, BuffsSJ6TGLabs, BuffsZagustin, BuffsZombieInfection,
            Buffs_2A2bTG, Buffs_3bTG, Buffs_AHF1M, Buffs_Antidote, Buffs_BodyTemperature, Buffs_EndOfWinterBonfire, Buffs_Frostbite,
            Buffs_KultistsToxin, Buffs_L1, Buffs_MULE, Buffs_Meldonin, Buffs_Obdolbos, Buffs_Obdolbos2, Buffs_P22, Buffs_PNB,
            Buffs_Perfotoran, Buffs_SJ12_TGLabs, Buffs_Trimadol, Buffs_drink_aquamari, Buffs_drink_hotrod, Buffs_drink_jack,
            Buffs_drink_juice_army, Buffs_drink_maxenergy, Buffs_drink_milk, Buffs_drink_moonshine, Buffs_drink_purewater,
            Buffs_drink_tarcola, Buffs_drink_vodka, Buffs_drink_vodka_BAD, Buffs_drink_water, Buffs_food_alyonka, Buffs_food_beer,
            Buffs_food_borodinskiye, Buffs_food_condensed_milk, Buffs_food_emelya, Buffs_food_mayonez, Buffs_food_mre,
            Buffs_food_slippers, Buffs_food_sugar, Buffs_hultafors, Buffs_knife, Buffs_melee_bleed, Buffs_melee_blunt, BuffseTGchange,
        }

        [Serializable]
        public class Entry
        {
            public EffectKind Effect = EffectKind.Contusion;
            [Tooltip("Where it goes (bleeding, fractures, pain, removing effects).")]
            public BodyPart BodyPart = BodyPart.Chest;
            [Tooltip("Seconds it lasts (pain, contusion, stun, disorientation, sand, stamina, lethal toxin).")]
            [Min(0f)]
            public float Duration = 5f;
            [Tooltip("How strong, 0 to 1 (pain, contusion, stun).")]
            [Range(0f, 1f)]
            public float Strength = 0.5f;
            [Tooltip("Seconds before it starts (pain, lethal toxin).")]
            [Min(0f)]
            public float Delay;
            [Tooltip("The game's buff list (Buff).")]
            public BuffName Buff = BuffName.BuffsAdrenaline;
        }

        [Tooltip("Apply them to the player who fires this trigger id (a zone or a switch; connect one in Triggers & Sounds).")]
        public string TriggerId = "";

        [Tooltip("Only once per raid to each player.")]
        public bool Once;

        [Tooltip("The chance they're applied each time the trigger fires.")]
        [Range(0f, 1f)]
        public float Chance = 1f;

        public List<Entry> Effects = new List<Entry> { new Entry() };
    }
}
