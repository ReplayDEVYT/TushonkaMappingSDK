using System.Linq;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // CustomMapLoader's own raid events, which Tarkov doesn't have; the client plugin runs them from the map's
    // sdk_export.json "Features" (Client/Features):
    //  - Forced Transit: a transit (TransitPoint) that sends the player on at once, without the transit menu,
    //    its confirmation or the countdown, when they walk into it and/or when a trigger id fires.
    //  - Gear Strip: takes the player's gear at raid start and/or on a trigger id, into a stash container on the
    //    map (a "Gear Stash" container type, empty, 10 x 16) or for good.
    //  - Notification: the game's notification popup (icon, sound, duration) for the player who fires a trigger id.
    //  - Caption: lines in the game's caption view (SubtitlesView), each at its time from the trigger.
    //  - Health Change: damage, healing, restore, death, energy or hydration, once or repeated until a stop id.
    //  - Effect: health effects (bleeding, fractures, pain, contusion, stun, poison, stimulator buffs...).
    // Their settings are SDK components on EditorOnly objects (TarkovForceTransit, TarkovGearStrip, TarkovNotification,
    // TarkovCaption, TarkovHealthChange, TarkovEffect).
    public static class TarkovMapEvents
    {
        private const string Menu = "GameObject/Tarkov SDK/";

        [MenuItem(Menu + "Exfils/Forced Transit", priority = 22)]
        private static void MenuForcedTransit(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p =>
        {
            var transit = TarkovSceneTools.CreateTransit(p, Vector3.zero);
            transit.gameObject.name = "ForcedTransit";
            AddForce(transit);
            return transit.gameObject;
        });

        // the settings object under a transit (made if needed)
        public static TarkovForceTransit AddForce(TransitPoint transit)
        {
            var existing = transit.GetComponentInChildren<TarkovForceTransit>(true);
            if (existing != null)
                return existing;
            var go = EditorOnlyChild(transit.transform, "SDK_ForceTransit");
            var force = go.AddComponent<TarkovForceTransit>();
            force.Transit = transit;
            return force;
        }

        [MenuItem(Menu + "Triggers/Gear Strip", priority = 87)]
        private static void MenuGearStrip(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateGearStrip(p).transform.parent.gameObject);

        // a root with the settings (EditorOnly) and the stash under it
        public static TarkovGearStrip CreateGearStrip(Transform parent)
        {
            var root = new GameObject("GearStrip");
            if (parent != null)
                root.transform.SetParent(parent, false);
            var strip = EditorOnlyChild(root.transform, "SDK_GearStrip").AddComponent<TarkovGearStrip>();
            var stash = TarkovPrefabTools.CreateContainer(root.transform);
            stash.name = "GearStash";
            stash.transform.localPosition = new Vector3(1.5f, 0f, 0f);
            TarkovLootContainers.Use(stash, TarkovLootContainers.GearStash());
            // the client needs it to exist in every raid
            stash.SpawnType = ELootableContainerSpawnType.AlwaysHasLoot;
            strip.Container = stash;
            return strip;
        }

        [MenuItem(Menu + "Triggers/Notification", priority = 83)]
        private static void MenuNotification(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateNotification(p).gameObject);

        public static TarkovNotification CreateNotification(Transform parent)
        {
            var go = EditorOnlyChild(parent, "SDK_Notification");
            return go.AddComponent<TarkovNotification>();
        }

        [MenuItem(Menu + "Triggers/Caption", priority = 84)]
        private static void MenuCaption(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateCaption(p).gameObject);

        public static TarkovCaption CreateCaption(Transform parent) => EditorOnlyChild(parent, "SDK_Caption").AddComponent<TarkovCaption>();

        [MenuItem(Menu + "Triggers/Health Change", priority = 85)]
        private static void MenuHealth(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateHealthChange(p).gameObject);

        public static TarkovHealthChange CreateHealthChange(Transform parent) => EditorOnlyChild(parent, "SDK_HealthChange").AddComponent<TarkovHealthChange>();

        [MenuItem(Menu + "Triggers/Effect", priority = 86)]
        private static void MenuEffect(MenuCommand cmd) => TarkovSceneTools.Place(cmd, p => CreateEffect(p).gameObject);

        public static TarkovEffect CreateEffect(Transform parent) => EditorOnlyChild(parent, "SDK_Effect").AddComponent<TarkovEffect>();

        internal static GameObject EditorOnlyChild(Transform parent, string name)
        {
            var go = new GameObject(name) { tag = "EditorOnly" };
            if (parent != null)
                go.transform.SetParent(parent, false);
            return go;
        }

        // ------------------------------------------------------------ checks (Build Map)

        public static void Check(UnityEngine.SceneManagement.Scene scene, GameObject[] roots, TarkovScenePreparer.Report report)
        {
            foreach (var force in roots.SelectMany(r => r.GetComponentsInChildren<TarkovForceTransit>(true)))
            {
                if (force.Transit == null)
                    report.Errors.Add($"{scene.name}: forced transit '{force.name}' has no Transit");
                else if (force.Hidden && string.IsNullOrEmpty(force.TriggerId))
                    report.Errors.Add($"{scene.name}: forced transit '{force.Transit.name}' is hidden but has no trigger id, so nothing can ever use it");
                else if (!force.Hidden && !force.OnEnter && string.IsNullOrEmpty(force.TriggerId))
                    report.Warnings.Add($"{scene.name}: forced transit '{force.Transit.name}' is neither forced on entering nor by a trigger, so it's an ordinary transit");
                else
                    report.ForceTransits.Add(force);
            }
            foreach (var strip in roots.SelectMany(r => r.GetComponentsInChildren<TarkovGearStrip>(true)))
            {
                if (!strip.OnRaidStart && string.IsNullOrEmpty(strip.TriggerId))
                {
                    report.Warnings.Add($"{scene.name}: gear strip '{strip.name}' runs neither at raid start nor on a trigger, so it never runs");
                    continue;
                }
                if (strip.Mode == TarkovGearStrip.StripMode.MoveToContainer)
                {
                    if (strip.Container == null)
                    {
                        report.Errors.Add($"{scene.name}: gear strip '{strip.name}' moves gear to a container but has none");
                        continue;
                    }
                    if (string.IsNullOrEmpty(strip.Container.Id))
                        report.Errors.Add($"{scene.name}: gear strip '{strip.name}': its container '{strip.Container.name}' has no Id");
                    var type = TarkovLootContainers.Find(strip.Container.Template);
                    if (type == null || type.Loot != TarkovContainerDefinition.LootSource.Empty)
                        report.Warnings.Add($"{scene.name}: gear strip '{strip.name}': its container '{strip.Container.name}' isn't an empty container type, so loot can take the gear's space");
                    else if (type.NewContainer && type.Width * type.Height < 100)
                        report.Warnings.Add($"{scene.name}: gear strip '{strip.name}': its container is {type.Width} x {type.Height}; a full kit needs about 10 x 16, and what doesn't fit stays on the player");
                }
                if (strip.Notify && string.IsNullOrWhiteSpace(strip.Notification.Message))
                    report.Warnings.Add($"{scene.name}: gear strip '{strip.name}' notifies the player but has no message");
                report.GearStrips.Add(strip);
            }
            foreach (var notification in roots.SelectMany(r => r.GetComponentsInChildren<TarkovNotification>(true)))
            {
                if (string.IsNullOrEmpty(notification.TriggerId))
                    report.Warnings.Add($"{scene.name}: notification '{notification.name}' has no trigger id, so it's never shown (connect one in Triggers & Sounds)");
                else if (string.IsNullOrWhiteSpace(notification.Notification.Message))
                    report.Warnings.Add($"{scene.name}: notification '{notification.name}' has no message");
                else
                    report.Notifications.Add(notification);
            }
            foreach (var caption in roots.SelectMany(r => r.GetComponentsInChildren<TarkovCaption>(true)))
            {
                if (string.IsNullOrEmpty(caption.TriggerId))
                    report.Warnings.Add($"{scene.name}: caption '{caption.name}' has no trigger id, so it's never shown (connect one in Triggers & Sounds)");
                else if (caption.Lines == null || !caption.Lines.Any(l => l != null && !string.IsNullOrWhiteSpace(l.Text)))
                    report.Warnings.Add($"{scene.name}: caption '{caption.name}' has no lines");
                else
                    report.Captions.Add(caption);
            }
            foreach (var health in roots.SelectMany(r => r.GetComponentsInChildren<TarkovHealthChange>(true)))
            {
                bool parts = health.Change == TarkovHealthChange.ChangeKind.Damage || health.Change == TarkovHealthChange.ChangeKind.Heal || health.Change == TarkovHealthChange.ChangeKind.Restore;
                if (string.IsNullOrEmpty(health.TriggerId))
                    report.Warnings.Add($"{scene.name}: health change '{health.name}' has no trigger id, so it never happens (connect one in Triggers & Sounds)");
                else if (parts && (health.BodyParts & TarkovHealthChange.Parts.All) == 0)
                    report.Warnings.Add($"{scene.name}: health change '{health.name}' changes no body parts");
                else
                {
                    if (health.Repeat == 0 && string.IsNullOrEmpty(health.StopTriggerId) && health.Change != TarkovHealthChange.ChangeKind.Kill)
                        report.Warnings.Add($"{scene.name}: health change '{health.name}' repeats until a stop trigger id, but has none, so it runs until the player dies");
                    report.HealthChanges.Add(health);
                }
            }
            foreach (var effect in roots.SelectMany(r => r.GetComponentsInChildren<TarkovEffect>(true)))
            {
                if (string.IsNullOrEmpty(effect.TriggerId))
                    report.Warnings.Add($"{scene.name}: effect '{effect.name}' has no trigger id, so it's never applied (connect one in Triggers & Sounds)");
                else if (effect.Effects == null || effect.Effects.Count == 0)
                    report.Warnings.Add($"{scene.name}: effect '{effect.name}' has no effects");
                else
                {
                    foreach (var e in effect.Effects.Where(e => e != null && e.Effect == TarkovEffect.EffectKind.Fracture &&
                                                                e.BodyPart != TarkovEffect.BodyPart.LeftArm && e.BodyPart != TarkovEffect.BodyPart.RightArm &&
                                                                e.BodyPart != TarkovEffect.BodyPart.LeftLeg && e.BodyPart != TarkovEffect.BodyPart.RightLeg))
                        report.Warnings.Add($"{scene.name}: effect '{effect.name}': a fracture on the {e.BodyPart} does nothing (arms and legs only)");
                    report.Effects.Add(effect);
                }
            }
        }
    }
}
