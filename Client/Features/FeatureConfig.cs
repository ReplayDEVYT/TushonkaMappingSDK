using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SPT.Common.Http;

namespace mizmii.CustomMapLoader.Features
{
    // CustomMapLoader's own in-raid features (Tarkov has none of them), per map, as the SDK's Build Map exports them
    // into sdk_export.json "Features" and the server mod serves them (/custommaploader/features): forced transits,
    // gear stripping, notifications, captions, health changes and effects. Read once at startup.
    internal static class FeatureConfig
    {
#pragma warning disable CS0649 // filled from the server's JSON
        internal sealed class MapFeatures
        {
            public List<ForceTransit> ForceTransits;
            public List<GearStrip> GearStrips;
            public List<NotificationLink> Notifications;
            public List<CaptionLink> Captions;
            public List<HealthChange> HealthChanges;
            public List<EffectLink> Effects;
        }

        internal sealed class CaptionLink
        {
            public string TriggerId;
            public bool Once = true;
            public List<CaptionLine> Lines;
        }

        internal sealed class CaptionLine
        {
            public string Text;
            // seconds from the trigger
            public float Start;
            public float End;
        }

        internal sealed class HealthChange
        {
            public string TriggerId;
            public string StopTriggerId;
            public bool Once;
            public float Chance = 1f;
            // Damage, Heal, Restore, Kill, Energy, Hydration
            public string Change;
            // EBodyPart names
            public List<string> BodyParts;
            public float Amount;
            // EDamageType name
            public string DamageType;
            // 1 = once, 0 = until stopped
            public int Repeat = 1;
            public float Interval = 1f;
        }

        internal sealed class EffectLink
        {
            public string TriggerId;
            public bool Once;
            public float Chance = 1f;
            public List<EffectEntry> Effects;
        }

        internal sealed class EffectEntry
        {
            // TarkovEffect.EffectKind name
            public string Effect;
            // EBodyPart name
            public string BodyPart;
            public float Duration;
            public float Strength;
            public float Delay;
            // the stimulator buff list's name in globals
            public string Buff;
        }

        // the game's CustomNotification by name: ENotificationIconType, ENotificationDurationType, EUISoundType (or None)
        internal sealed class Notification
        {
            public string Message;
            public string Icon;
            public string Duration;
            public string Sound;
            // RGBA, or null for the default colour
            public float[] Color;
        }

        internal sealed class NotificationLink
        {
            public string TriggerId;
            public bool Once = true;
            public Notification Notification;
        }

        internal sealed class ForceTransit
        {
            public int TransitId;
            public bool OnEnter;
            public string TriggerId;
            public bool IgnoreRequirements;
            // exported inactive: the game never registers it, so only its trigger uses it
            public bool Hidden;
        }

        internal sealed class GearStrip
        {
            public string Name;
            public bool OnRaidStart;
            public string TriggerId;
            public bool Once = true;
            // Lose, MoveToContainer
            public string Mode;
            public string ContainerId;
            // EquipmentSlot names whose item is taken
            public List<string> Slots;
            public bool PocketContents;
            public bool SecuredContainerContents;
            public bool SpecialSlots;
            // shown when anything was taken, or null
            public Notification Notification;
        }

#pragma warning restore CS0649

        private static readonly Dictionary<string, MapFeatures> Maps = new Dictionary<string, MapFeatures>(StringComparer.OrdinalIgnoreCase);

        public static async void Load()
        {
            try
            {
                var json = await Task.Run(() => RequestHandler.GetJsonAsync("/custommaploader/features"));
                var maps = JsonConvert.DeserializeObject<Dictionary<string, MapFeatures>>(json);
                Maps.Clear();
                if (maps != null)
                    foreach (var pair in maps)
                        Maps[pair.Key] = pair.Value;
                Plugin.Verbose($"Map features: {Maps.Count} map(s) with their own raid features");
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogWarning($"Could not read the map features from the server ({e.Message}). " +
                                            "Forced transits, gear stripping and the other map features are off; is the CustomMapLoader server mod up to date?");
            }
        }

        public static MapFeatures For(string mapId) => mapId != null && Maps.TryGetValue(mapId, out var features) ? features : null;
    }
}
