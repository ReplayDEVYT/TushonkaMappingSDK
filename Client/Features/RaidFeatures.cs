using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.Communications;
using EFT.GameTriggers;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using mizmii.CustomMapLoader.Loading;
using SPT.Reflection.Patching;
using UnityEngine;

namespace mizmii.CustomMapLoader.Features
{
    // Runs the map's FeatureConfig entries in the raid. At GameWorld.OnGameStarted (the player is in and the
    // trigger emitter exists) it strips gear set to go at raid start and subscribes the trigger ids:
    //  - forced transit: Transit() straight away, with the arguments TransitPoint's countdown gives it (a new hash,
    //    the player's key), skipping LocalTransitController's interaction menu and the group timer
    //  - gear strip: each item taken is moved into the container's root item (ItemManipulator.QuickFindAppropriatePlace,
    //    as the game's quick move) or removed, through the player's own inventory transactions; what can't be moved
    //    stays on the player and is reported. The item in the hands can't be moved while it's held, so when it's among
    //    them (or inside one) the hands are emptied first
    // Triggers only act for the player who fired them, and only for this game's own player.
    internal static class RaidFeatures
    {
        private static FeatureConfig.MapFeatures _features;
        public static FeatureConfig.MapFeatures Features => _features;
        // "<kind>:<index>:<profile>" for features that only run once
        private static readonly HashSet<string> Done = new HashSet<string>();

        public static void Start(GameWorld world)
        {
            _features = null;
            Done.Clear();
            SdkHealth.Reset();
            SdkCaptions.Reset();
            SdkSpawns.Reset();
            SdkWorldEvents.Reset();
            if (world == null || !MapRegistry.IsCustomMapActive())
                return;
            Loading.WindFallback.Apply();
            var features = FeatureConfig.For(world.LocationId);
            if (features == null)
                return;
            _features = features;
            var emitter = world.TriggersEmitter;

            var transits = features.ForceTransits ?? new List<FeatureConfig.ForceTransit>();
            foreach (var transit in transits.Where(t => !string.IsNullOrEmpty(t.TriggerId)))
                Subscribe(emitter, transit.TriggerId, player => ForceTransit(transit.TransitId, player, transit.IgnoreRequirements, transit.Hidden));

            var strips = features.GearStrips ?? new List<FeatureConfig.GearStrip>();
            for (int i = 0; i < strips.Count; i++)
            {
                var strip = strips[i];
                string key = "strip:" + i;
                if (!string.IsNullOrEmpty(strip.TriggerId))
                    Subscribe(emitter, strip.TriggerId, player => Strip(strip, key, player));
                if (strip.OnRaidStart && world.MainPlayer != null)
                    Strip(strip, key, world.MainPlayer);
            }

            var notifications = features.Notifications ?? new List<FeatureConfig.NotificationLink>();
            for (int i = 0; i < notifications.Count; i++)
            {
                var notification = notifications[i];
                string key = "notification:" + i;
                if (!string.IsNullOrEmpty(notification.TriggerId))
                    Subscribe(emitter, notification.TriggerId, player =>
                    {
                        if (FirstTime(notification.Once, key, player))
                            Notify(notification.Notification);
                    });
            }

            var captions = features.Captions ?? new List<FeatureConfig.CaptionLink>();
            for (int i = 0; i < captions.Count; i++)
            {
                var caption = captions[i];
                string key = "caption:" + i;
                if (!string.IsNullOrEmpty(caption.TriggerId))
                    Subscribe(emitter, caption.TriggerId, player =>
                    {
                        if (FirstTime(caption.Once, key, player))
                            SdkCaptions.Show(caption);
                    });
            }

            var healthChanges = features.HealthChanges ?? new List<FeatureConfig.HealthChange>();
            for (int i = 0; i < healthChanges.Count; i++)
            {
                var change = healthChanges[i];
                int index = i;
                string key = "health:" + i;
                if (!string.IsNullOrEmpty(change.TriggerId))
                    Subscribe(emitter, change.TriggerId, player =>
                    {
                        if (FirstTime(change.Once, key, player))
                            SdkHealth.Change(change, index, player);
                    });
                if (!string.IsNullOrEmpty(change.StopTriggerId))
                    Subscribe(emitter, change.StopTriggerId, player => SdkHealth.Stop(index, player));
            }

            var effects = features.Effects ?? new List<FeatureConfig.EffectLink>();
            for (int i = 0; i < effects.Count; i++)
            {
                var effect = effects[i];
                string key = "effect:" + i;
                if (!string.IsNullOrEmpty(effect.TriggerId))
                    Subscribe(emitter, effect.TriggerId, player =>
                    {
                        if (FirstTime(effect.Once, key, player))
                            SdkHealth.Effects(effect, player);
                    });
            }

            // bots, loot and shelling belong to the host in co-op; prefabs are shown on every machine
            bool host = SdkSpawns.IsHost();
            var mortar = features.MortarStrikes ?? new List<FeatureConfig.MortarStrike>();
            for (int i = 0; i < mortar.Count; i++)
            {
                var strike = mortar[i];
                string key = "mortar:" + i;
                if (host && !string.IsNullOrEmpty(strike.TriggerId))
                    SubscribeAny(emitter, strike.TriggerId, player =>
                    {
                        if (SdkSpawns.FirstTime(strike.Once, key))
                            SdkSpawns.Mortar(strike);
                    });
            }

            var bots = features.SpawnBots ?? new List<FeatureConfig.SpawnBotsLink>();
            for (int i = 0; i < bots.Count; i++)
            {
                var spawn = bots[i];
                string key = "bots:" + i;
                if (host && !string.IsNullOrEmpty(spawn.TriggerId))
                    SubscribeAny(emitter, spawn.TriggerId, player =>
                    {
                        if (SdkSpawns.FirstTime(spawn.Once, key))
                            SdkSpawns.Bots(spawn);
                    });
            }

            var loot = features.SpawnLoot ?? new List<FeatureConfig.SpawnLootLink>();
            for (int i = 0; i < loot.Count; i++)
            {
                var spawn = loot[i];
                string key = "loot:" + i;
                if (host && !string.IsNullOrEmpty(spawn.TriggerId))
                    SubscribeAny(emitter, spawn.TriggerId, player =>
                    {
                        if (SdkSpawns.FirstTime(spawn.Once, key))
                            SdkSpawns.Loot(spawn);
                    });
            }

            var prefabs = features.SpawnPrefabs ?? new List<FeatureConfig.SpawnPrefabLink>();
            for (int i = 0; i < prefabs.Count; i++)
            {
                var spawn = prefabs[i];
                string key = "prefab:" + i;
                if (!string.IsNullOrEmpty(spawn.TriggerId))
                    SubscribeAny(emitter, spawn.TriggerId, player =>
                    {
                        if (SdkSpawns.FirstTime(spawn.Once, key))
                            SdkSpawns.Prefab(spawn);
                    });
            }

            // after every listener above is subscribed: an event's first step can fire straight away
            var worldEvents = features.WorldEvents ?? new List<FeatureConfig.WorldEvent>();
            SdkWorldEvents.Start(world, worldEvents);

            Plugin.Verbose($"Map features: {transits.Count} forced transit(s), {strips.Count} gear strip(s), {notifications.Count} notification(s), " +
                           $"{captions.Count} caption(s), {healthChanges.Count} health change(s), {effects.Count} effect(s), {mortar.Count} mortar strike(s), " +
                           $"{bots.Count} bot spawn(s), {loot.Count} loot spawn(s), {prefabs.Count} prefab spawn(s), {worldEvents.Count} world event(s)" +
                           (host ? "" : " (bots, loot and shelling run on the host)"));
        }

        private static void Subscribe(TriggersEmitter emitter, string triggerId, Action<Player> action)
        {
            if (emitter == null)
            {
                Plugin.LogSource.LogWarning($"Map features: the raid has no trigger emitter, so trigger '{triggerId}' does nothing");
                return;
            }
            emitter.Subscribe(triggerId, (TriggerEvent e) =>
            {
                try
                {
                    var player = Singleton<GameWorld>.Instance?.GetAlivePlayerByProfileID(e.OriginProfileId);
                    if (player != null && player.IsYourPlayer)
                        action(player);
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogError($"Map features: trigger '{triggerId}' failed: {ex}");
                }
            });
        }

        // for triggered spawns: whoever fires it (any player alive in the raid, a bot included)
        private static void SubscribeAny(TriggersEmitter emitter, string triggerId, Action<Player> action)
        {
            if (emitter == null)
            {
                Plugin.LogSource.LogWarning($"Map features: the raid has no trigger emitter, so trigger '{triggerId}' does nothing");
                return;
            }
            emitter.Subscribe(triggerId, (TriggerEvent e) =>
            {
                try
                {
                    var player = Singleton<GameWorld>.Instance?.GetAlivePlayerByProfileID(e.OriginProfileId);
                    if (player != null)
                        action(player);
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogError($"Map features: trigger '{triggerId}' failed: {ex}");
                }
            });
        }

        private static bool FirstTime(bool once, string key, Player player) => !once || Done.Add(key + ":" + player.ProfileId);

        // the game's notification popup, as NotificationManager.DisplayMessageNotification makes it, plus its UI sound
        // (NotifierView plays Notification.SoundType when it shows)
        public static void Notify(FeatureConfig.Notification settings)
        {
            if (settings == null || string.IsNullOrEmpty(settings.Message))
                return;
            var icon = Enum.TryParse<ENotificationIconType>(settings.Icon, out var i) ? i : ENotificationIconType.Default;
            var duration = Enum.TryParse<ENotificationDurationType>(settings.Duration, out var d) ? d : ENotificationDurationType.Default;
            Color? color = settings.Color != null && settings.Color.Length == 4 ? new Color(settings.Color[0], settings.Color[1], settings.Color[2], settings.Color[3]) : (Color?)null;
            var notification = new CustomNotification(settings.Message, duration, icon, color);
            if (Enum.TryParse<EFT.UI.EUISoundType>(settings.Sound, out var sound))
                notification.SoundType = sound;
            NotificationManager.DisplayNotification(notification);
        }

        // ------------------------------------------------------------ forced transits

        public static FeatureConfig.ForceTransit ForcedOnEnter(int transitId) =>
            _features?.ForceTransits?.FirstOrDefault(t => t.OnEnter && !t.Hidden && t.TransitId == transitId);

        private static void ForceTransit(int transitId, Player player, bool ignoreRequirements, bool hidden)
        {
            var controller = Singleton<GameWorld>.Instance?.TransitController as LocalTransitController;
            if (controller == null)
            {
                Plugin.LogSource.LogWarning($"Map features: the raid has no transits, so transit {transitId} can't be forced");
                return;
            }
            if (!controller.pointsById.TryGetValue(transitId, out var point))
            {
                // InitTransitPoints gives an inactive point its server parameters, switches its object off and doesn't
                // register it. The access check (ProtectedLocation) reads pointsById, so it's registered now; its object
                // stays off, so it still has no zone and isn't listed (the list is built at raid start)
                point = hidden ? InactivePoint(transitId) : null;
                if (point == null)
                {
                    Plugin.LogSource.LogWarning($"Map features: transit {transitId} isn't in the raid (is it active in the map's transits?)");
                    return;
                }
                point.Controller = controller;
                controller.pointsById[transitId] = point;
            }
            Force(controller, point, player, ignoreRequirements);
        }

        // a switched-off TransitPoint in the loaded custom scenes
        private static TransitPoint InactivePoint(int transitId) =>
            Resources.FindObjectsOfTypeAll<TransitPoint>().FirstOrDefault(t => t != null && t.gameObject.scene.IsValid() && MapRegistry.IsCustomScene(t.gameObject.scene)
                                                                              && t.parameters != null && t.parameters.id == transitId);

        public static void Force(LocalTransitController controller, TransitPoint point, Player player, bool ignoreRequirements)
        {
            if (controller.alreadyTransits.ContainsKey(player.ProfileId))
                return;
            if (!controller.TryGetAccessToLocation(player, point.parameters.id, out var keyId))
            {
                if (!ignoreRequirements)
                {
                    controller.AccessNotGrantedNotification();
                    return;
                }
                keyId = "";
            }
            keyId ??= "";
            controller.profileKeys[player.ProfileId] = keyId;
            var keys = new Dictionary<string, ProfileKey>
            {
                [player.ProfileId] = new ProfileKey { _id = player.ProfileId, keyId = keyId, isSolo = string.IsNullOrEmpty(player.GroupId) },
            };
            Plugin.Verbose($"Map features: forced transit {point.parameters.id} to {point.parameters.location}");
            controller.Transit(point, 1, Guid.NewGuid().ToString(), keys, player);
        }

        // ------------------------------------------------------------ gear stripping

        private static async void Strip(FeatureConfig.GearStrip strip, string key, Player player)
        {
            try
            {
                if (!FirstTime(strip.Once, key, player))
                    return;
                var controller = player.InventoryController;
                var equipment = player.Inventory?.Equipment;
                if (controller == null || equipment == null)
                    return;

                var items = new List<Item>();
                foreach (var name in strip.Slots ?? new List<string>())
                    if (Enum.TryParse<EquipmentSlot>(name, out var slot) && slot != EquipmentSlot.Pockets && slot != EquipmentSlot.SecuredContainer && slot != EquipmentSlot.Dogtag)
                    {
                        var item = equipment.GetSlot(slot)?.ContainedItem;
                        if (item != null)
                            items.Add(item);
                    }
                var pockets = equipment.GetSlot(EquipmentSlot.Pockets)?.ContainedItem as CompoundItem;
                if (strip.PocketContents && pockets?.Grids != null)
                    items.AddRange(pockets.Grids.SelectMany(g => g.Items).ToList());
                if (strip.SpecialSlots && pockets?.Slots != null)
                    items.AddRange(pockets.Slots.Where(s => s.ID.StartsWith("SpecialSlot", StringComparison.Ordinal) && s.ContainedItem != null).Select(s => s.ContainedItem));
                var secure = equipment.GetSlot(EquipmentSlot.SecuredContainer)?.ContainedItem as CompoundItem;
                if (strip.SecuredContainerContents && secure?.Grids != null)
                    items.AddRange(secure.Grids.SelectMany(g => g.Items).ToList());
                if (items.Count == 0)
                    return;

                if (!await InventoryFree(controller))
                {
                    Error(strip, "your inventory stayed blocked (still interacting with something?), so nothing was taken");
                    return;
                }

                var held = player.HandsController?.Item;
                if (held != null && !HandsEmpty(player) && items.Any(i => i == held || held.GetAllParentItems().Contains(i)) && !await EmptyHands(player))
                {
                    Error(strip, "what's in your hands couldn't be put away, so nothing was taken");
                    return;
                }

                CompoundItem target = null;
                ItemController owner = null;
                if (strip.Mode == "MoveToContainer")
                {
                    var container = UnityEngine.Object.FindObjectsOfType<LootableContainer>().FirstOrDefault(c => c.Id == strip.ContainerId);
                    owner = container?.ItemOwner;
                    target = owner?.RootItem as CompoundItem;
                    if (target == null)
                    {
                        // never lose gear because the stash is missing
                        Error(strip, $"its container '{strip.ContainerId}' {(container == null ? "isn't in the map" : "has no inventory in this raid")}, so nothing was taken");
                        return;
                    }
                    // a move into a container the player hasn't searched fails (CanTransferTo: UnknownAddressError), and
                    // nobody has searched the stash yet; it's theirs to take the gear back from anyway
                    if (target is SearchableItem searchable)
                        player.SearchController?.SetItemAsSearched(searchable);
                }

                var failed = new List<string>();
                bool noRoom = false;
                foreach (var item in items)
                {
                    string name = item.ShortName.Localized();
                    // only if something locked it meanwhile; waiting half a second per item made a strip crawl
                    if (controller.IsInventoryBlocked())
                        await InventoryFree(controller);
                    OperationResult operation = target != null
                        ? (OperationResult)ItemManipulator.QuickFindAppropriatePlace(item, controller, new[] { target }, ItemManipulator.EMoveItemOrder.ForcePush, simulate: true)
                        : ItemManipulator.Remove(item, controller, simulate: true);
                    string reason = null;
                    if (operation.Failed)
                    {
                        reason = operation.Error?.ToString();
                        // fallback: straight into the container's grid as its own controller
                        if (target != null)
                        {
                            var direct = await PlaceDirectly(item, controller, target, owner);
                            if (direct == null)
                                continue;
                            noRoom |= direct == NoRoom;
                            reason += "; directly: " + direct;
                        }
                    }
                    else
                    {
                        var result = await controller.TryRunNetworkTransaction(operation);
                        if (result.Succeed)
                            continue;
                        // "Can not execute": the move simulates fine but its CanExecute (Item.CheckAction: both owners'
                        // CheckItemAction) refuses it; the direct placement below doesn't go through that check
                        var check = item.CheckAction(null);
                        reason = result.Error + (check.Failed ? $" ({check.Error})" : "");
                        if (target != null)
                        {
                            var direct = await PlaceDirectly(item, controller, target, owner);
                            if (direct == null)
                                continue;
                            noRoom |= direct == NoRoom;
                            reason += "; directly: " + direct;
                        }
                    }
                    failed.Add(name);
                    Plugin.LogSource.LogWarning($"Map features: gear strip '{strip.Name}': {name} stays: {reason}");
                }

                Plugin.Verbose($"Map features: gear strip '{strip.Name}': {items.Count - failed.Count} of {items.Count} item(s) " +
                                         (target != null ? "moved to the container" : "removed"));
                if (failed.Count < items.Count)
                    Notify(strip.Notification);
                if (failed.Count > 0)
                    Error(strip, (target != null ? (noRoom ? "these didn't fit in its container and stay on you: " : "these couldn't be moved to its container and stay on you: ")
                                                 : "these couldn't be taken: ") + string.Join(", ", failed));
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogError($"Map features: gear strip '{strip.Name}' failed: {e}");
            }
        }

        private const string NoRoom = "no room in the container";

        // A switch or door fires its trigger as the interaction starts, and DoorInteractState (ApproachState too) then
        // locks the inventory for the animation (MovementContext.StateLocksInventory), so every move failed "Inventory is
        // blocked". Waits (up to 10 s) until it has been unlocked for half a second straight.
        private static async System.Threading.Tasks.Task<bool> InventoryFree(InventoryController controller)
        {
            float until = Time.realtimeSinceStartup + 10f;
            float freeSince = -1f;
            while (Time.realtimeSinceStartup < until)
            {
                if (controller.IsInventoryBlocked())
                    freeSince = -1f;
                else if (freeSince < 0f)
                    freeSince = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - freeSince >= 0.5f)
                    return true;
                await System.Threading.Tasks.Task.Yield();
            }
            return !controller.IsInventoryBlocked();
        }

        // Takes the item off the player and adds it to the container's grid as its own controller, when there is room.
        // Puts it back where it was if the add fails, so gear is never lost. Null on success, otherwise why not.
        private static async System.Threading.Tasks.Task<string> PlaceDirectly(Item item, InventoryController controller, CompoundItem target, ItemController owner)
        {
            var address = target.Grids?.FindLocationForItem(item);
            if (address == null)
                return NoRoom;
            var from = item.Parent;
            var remove = ItemManipulator.Remove(item, controller, simulate: true);
            if (remove.Failed)
                return "remove: " + remove.Error;
            var removed = await controller.TryRunNetworkTransaction(remove);
            if (removed.Failed)
                return "remove: " + removed.Error;
            var add = ItemManipulator.AddWithoutRestrictions(item, address, owner);
            if (add.Succeeded)
                return null;
            ItemManipulator.AddWithoutRestrictions(item, from, controller);
            return "add: " + add.Error;
        }

        private static void Error(FeatureConfig.GearStrip strip, string message)
        {
            Plugin.LogSource.LogError($"Map features: gear strip '{strip.Name}': {message}");
            NotificationManager.DisplayWarningNotification($"Gear strip: {message}");
        }

        // ------------------------------------------------------------ hands

        public static bool HandsEmpty(Player player) =>
            player.HandsController is IEmptyHandsController && player.HandsController.ControllerGameObject != null;

        // Empties the hands and waits (up to 5 s) for the empty-hands controller, then a frame more: SetEmptyHands'
        // callback runs while the old controller is still being torn down (ControllerGameObject null).
        public static async System.Threading.Tasks.Task<bool> EmptyHands(Player player)
        {
            if (HandsEmpty(player))
                return true;
            player.SetEmptyHands(result => { });
            float until = Time.realtimeSinceStartup + 5f;
            while (player != null && !HandsEmpty(player) && Time.realtimeSinceStartup < until)
                await System.Threading.Tasks.Task.Yield();
            await System.Threading.Tasks.Task.Yield();
            return player != null && HandsEmpty(player);
        }
    }

    internal class RaidFeaturesStartPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted));

        [PatchPostfix]
        private static void Postfix(GameWorld __instance) => RaidFeatures.Start(__instance);
    }

    // a forced transit's zone transits at once instead of opening the transit menu
    internal class ForceTransitEnterPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(LocalTransitController), nameof(LocalTransitController.PlayerEnter));

        [PatchPrefix]
        private static bool Prefix(LocalTransitController __instance, TransitPoint point, Player player) => Enter(__instance, point, player);

        internal static bool Enter(LocalTransitController controller, TransitPoint point, Player player)
        {
            var forced = point != null ? RaidFeatures.ForcedOnEnter(point.parameters.id) : null;
            if (forced == null || player == null || !player.IsYourPlayer)
                return true;
            RaidFeatures.Force(controller, point, player, forced.IgnoreRequirements);
            return false;
        }
    }

    // Fika's host controller (a LocalTransitController) swaps PlayerEnter for its own OnHostPlayerEnter; enabled when Fika is installed
    internal class FikaForceTransitEnterPatch : ModulePatch
    {
        private static MethodBase Target => AccessTools.Method(AccessTools.TypeByName("Fika.Core.Main.HostClasses.FikaHostTransitController"), "OnHostPlayerEnter");

        public static bool Available() => AccessTools.TypeByName("Fika.Core.Main.HostClasses.FikaHostTransitController") != null && Target != null;

        protected override MethodBase GetTargetMethod() => Target;

        [PatchPrefix]
        private static bool Prefix(object __instance, TransitPoint point, Player player) =>
            !(__instance is LocalTransitController controller) || ForceTransitEnterPatch.Enter(controller, point, player);
    }
}
