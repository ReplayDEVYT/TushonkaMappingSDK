using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using EFT;
using EFT.Game.Spawning;
using EFT.Interactive;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Writes sdk_export.json: the scene-authored parts of base.json (spawn points, exits and the
    // export timestamp). The server mod merges it over the map's base.json at startup, so base.json
    // only holds settings that are not placed in the scene.
    public static class TarkovExportJson
    {
        public static string Build(string mapId, TarkovScenePreparer.Report report)
        {
            var w = new JsonWriter();
            w.BeginObject();
            w.Property("MapId", mapId);
            w.Property("UnixDateTime", report.UnixDateTime);

            w.Name("SpawnPointParams");
            w.BeginArray();
            foreach (var marker in report.SpawnPoints)
                WriteSpawnPoint(w, marker);
            w.EndArray();

            w.Name("Exits");
            w.BeginArray();
            foreach (var exfil in report.Exfils)
                WriteExit(w, exfil);
            w.EndArray();

            // a hidden forced transit is inactive for the game (no zone, not listed); only its trigger uses it
            var hidden = new HashSet<TransitPoint>(report.ForceTransits.Where(f => f.Hidden).Select(f => f.Transit));
            w.Name("Transits");
            w.BeginArray();
            foreach (var transit in report.Transits)
                WriteTransit(w, transit, hidden.Contains(transit));
            w.EndArray();

            w.Name("LootSpawns");
            w.BeginArray();
            foreach (var spawn in report.LootSpawns)
                WriteLootSpawn(w, spawn);
            w.EndArray();

            // a gear strip's stash must be in every raid, or the client has nowhere to put the gear
            var stashes = new HashSet<LootableContainer>(report.GearStrips.Where(s => s.Mode == TarkovSDK.TarkovGearStrip.StripMode.MoveToContainer && s.Container != null).Select(s => s.Container));
            w.Name("Containers");
            w.BeginArray();
            foreach (var container in report.Containers)
                WriteContainer(w, container, stashes.Contains(container));
            w.EndArray();

            w.Name("ContainerTypes");
            w.BeginArray();
            foreach (var type in report.ContainerTypes)
                WriteContainerType(w, type);
            w.EndArray();

            // the server picks Min..Max of each group's containers per raid (by their Probability)
            w.Name("ContainerGroups");
            w.BeginArray();
            foreach (var group in report.ContainerGroups)
            {
                w.BeginObject();
                w.Property("Id", group.Id);
                w.Property("Min", group.Min);
                w.Property("Max", group.Max);
                w.EndObject();
            }
            w.EndArray();

            // the server adds each gun's weapon to the raid's loot as a static weapon (retail Woods' / Reserve's rows)
            w.Name("MountedGuns");
            w.BeginArray();
            foreach (var gun in report.MountedGuns)
            {
                w.BeginObject();
                w.Property("Id", gun.Id);
                w.Property("Weapon", gun.Weapon);
                w.EndObject();
            }
            w.EndArray();

            w.Name("Features");
            WriteFeatures(w, report);

            if (report.Btr != null)
            {
                w.Name("Btr");
                WriteBtr(w, report.Btr);
            }

            if (report.Mortar != null)
            {
                w.Name("Mortar");
                WriteMortar(w, report);
            }

            w.EndObject();
            return w.ToString();
        }

        private static void WriteSpawnPoint(JsonWriter w, SpawnPointMarker marker)
        {
            var sp = marker._spawnPoint;
            var sphere = marker.GetComponent<SphereCollider>();
            float radius = sphere != null ? sphere.radius * MaxAbs(marker.transform.lossyScale) : 20f;

            w.BeginObject();
            w.Property("BotZoneName", sp.BotZone != null ? sp.BotZone.name : "");
            w.Name("Categories");
            WriteFlags(w, (int)sp.Categories, typeof(ESpawnCategoryMask), "All");
            w.Name("ColliderParams");
            w.BeginObject();
            w.Property("_parent", "SpawnSphereParams");
            w.Name("_props");
            w.BeginObject();
            w.Name("Center");
            WriteVector(w, Vector3.zero);
            w.Property("Radius", radius);
            w.EndObject();
            w.EndObject();
            w.Property("CorePointId", sp.CorePointId);
            w.Property("DelayToCanSpawnSec", sp.DelayToCanSpawnSec);
            w.Property("Id", sp.Id);
            w.Property("Infiltration", sp.Infiltration ?? "");
            w.Name("Position");
            WriteVector(w, sp.Position);
            w.Property("Rotation", marker.transform.eulerAngles.y);
            w.Name("Sides");
            WriteSides(w, sp.Sides);
            w.EndObject();
        }

        // the exit's requirement is the server's (ExfiltrationPoint.LoadSettings builds it from PassageRequirement,
        // Id, Count, RequiredSlot and RequirementTip), so the scene's first one is written here
        private static void WriteExit(JsonWriter w, ExfiltrationPoint exfil)
        {
            var s = exfil.Settings;
            var req = exfil.Requirements != null && exfil.Requirements.Length > 0 ? exfil.Requirements[0] : null;
            string requirement = req != null ? req.Requirement.ToString() : "None";
            string tip = req?.RequirementTip ?? "";

            w.BeginObject();
            w.Property("Chance", s.Chance);
            w.Property("ChancePVE", s.Chance);
            w.Property("Count", req?.Count ?? 0);
            w.Property("CountPVE", req?.Count ?? 0);
            w.Property("EntryPoints", s.EntryPoints ?? "");
            w.Property("EventAvailable", s.EventAvailable);
            w.Property("ExfiltrationTime", s.ExfiltrationTime);
            w.Property("ExfiltrationTimePVE", s.ExfiltrationTime);
            w.Property("ExfiltrationType", s.ExfiltrationType.ToString());
            w.Property("Id", !string.IsNullOrEmpty(req?.Id) ? req.Id : s.Id ?? "");
            w.Property("MaxTime", s.MaxTime);
            w.Property("MaxTimePVE", s.MaxTime);
            w.Property("MinTime", s.MinTime);
            w.Property("MinTimePVE", s.MinTime);
            w.Property("Name", s.Name);
            w.Property("PassageRequirement", requirement);
            w.Property("PlayersCount", s.PlayersCount);
            w.Property("PlayersCountPVE", s.PlayersCount);
            w.Property("RequirementTip", tip);
            if (req != null)
                w.Property("RequiredSlot", req.RequiredSlot.ToString());
            w.EndObject();
        }

        // "target" (the destination's _Id) is left for the server mod to fill from "location", since
        // only the server knows the _Id of every map, custom ones included
        private static void WriteTransit(JsonWriter w, TransitPoint transit, bool hidden)
        {
            var p = transit.parameters;
            w.BeginObject();
            w.Property("activateAfterSec", p.activateAfterSec);
            w.Property("active", p.active && !hidden);
            w.Property("conditions", p.conditions ?? "");
            w.Property("description", p.description ?? "");
            w.Property("events", p.events);
            w.Property("hideIfNoKey", p.hideIfNoKey);
            w.Property("id", p.id);
            w.Property("location", p.location ?? "");
            w.Property("name", p.name ?? "");
            w.Property("target", p.target ?? "");
            w.Property("time", (int)p.time);
            w.EndObject();
        }

        // CustomMapLoader puts the routes in globals (BTRSettings.MapsConfigs), the timing in locationServices
        // (ServerMapBTRSettings) and the stop names in the locales (Trading/Dialog/PlayerTaxi/<map>/<stop>/Name)
        private static void WriteBtr(JsonWriter w, EFT.Vehicle.MapPathConfig config)
        {
            var settings = TarkovBtr.Settings(config, false);
            w.BeginObject();
            w.Property("ChanceSpawn", settings != null ? settings.ChanceSpawn : 100f);
            w.Property("SpawnPeriodMin", settings != null ? settings.SpawnPeriod.x : 300f);
            w.Property("SpawnPeriodMax", settings != null ? settings.SpawnPeriod.y : 600f);
            w.Property("MoveSpeed", settings != null ? settings.MoveSpeed : 8f);
            w.Property("PauseMin", settings != null ? settings.PauseDuration.x : 90f);
            w.Property("PauseMax", settings != null ? settings.PauseDuration.y : 150f);
            w.Property("Skin", settings != null && !string.IsNullOrEmpty(settings.Skin) ? settings.Skin : "Dirt");
            w.Name("Routes");
            w.BeginArray();
            foreach (var route in config.PathsConfiguration.pathsConfigurations ?? new PathConfig[0])
            {
                w.BeginObject();
                w.Property("id", route.id ?? "");
                w.Property("active", route.active);
                w.Property("enterPoint", route.enterPoint ?? "");
                w.Property("exitPoint", route.exitPoint ?? "");
                w.Property("once", route.once);
                w.Property("circle", route.circle);
                w.Property("circleCount", route.circleCount);
                w.Name("pathPoints");
                w.BeginArray();
                foreach (var point in route.pathPoints ?? new List<string>())
                    w.Value(point);
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();
            w.Name("Stops");
            w.BeginArray();
            foreach (var stop in TarkovBtr.Stops(config))
            {
                var entry = settings?.Stops.FirstOrDefault(s => s.Id == stop.id);
                w.BeginObject();
                w.Property("Id", stop.id);
                w.Property("Name", string.IsNullOrEmpty(entry?.Name) ? stop.id : entry.Name);
                w.Property("Description", entry?.Description ?? "");
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
        }

        // globals ArtilleryShelling.ArtilleryMapsConfigs[map] as the game reads it (ArtilleryShellingMapConfiguration);
        // CustomMapLoader puts it there as it is. The Vector2 fields ride in x/y with z 0, as retail
        private static void WriteMortar(JsonWriter w, TarkovScenePreparer.Report report)
        {
            var s = report.Mortar;
            void Range(string name, Vector2 v)
            {
                w.Name(name);
                WriteVector(w, new Vector3(v.x, v.y, 0f));
            }
            w.BeginObject();
            w.Property("PlanedShellingOn", s.PlannedShelling);
            w.Property("InitShellingTimer", s.FirstShellingAfter);
            w.Property("InitCalledShellingTime", s.CalledStrikeDelay);
            w.Property("BeforeShellingSignalTime", s.WarningTime);
            w.Property("ShellingCount", s.Shellings);
            w.Property("ZonesInShelling", s.ZonesPerShelling);
            w.Property("NewZonesForEachShelling", s.NewZonesEachShelling);
            Range("PauseBetweenShellings", s.PauseBetweenShellings);
            // retail's, switched off: the shelling's own airdrop
            w.Name("ArtilleryShellingAirDropSettings");
            w.BeginObject();
            w.Property("UseAirDrop", false);
            w.Property("AirDropTime", 600);
            w.Name("AirDropPosition");
            WriteVector(w, Vector3.zero);
            w.Property("LootTemplateId", "66da1b49099cf6adcc07a36b");
            w.EndObject();
            // the called-strike template first: the game takes the first UseInCalledShelling zone
            w.Name("ShellingZones");
            w.BeginArray();
            foreach (var zone in report.MortarZones.OrderByDescending(z => z.CalledStrikeTemplate))
            {
                w.BeginObject();
                w.Property("ID", zone.Id);
                Range("PointsInShellings", zone.PointsPerShelling);
                w.Property("ShellingRounds", zone.Rounds);
                w.Property("ShotCount", zone.ShotsPerRound);
                Range("PauseBetweenRounds", zone.PauseBetweenRounds);
                Range("PauseBetweenShots", zone.PauseBetweenShots);
                w.Name("Center");
                WriteVector(w, zone.transform.position);
                Range("ExplosionDistanceRange", zone.BurstHeight);
                w.Property("Rotate", zone.transform.eulerAngles.y);
                Range("GridStep", zone.GridStep);
                Range("Points", zone.Points);
                w.Property("PointRadius", zone.PointRadius);
                w.Name("AlarmStages");
                w.BeginArray();
                foreach (var stage in TarkovMortar.RetailAlarmStages)
                {
                    w.BeginObject();
                    w.Name("Value");
                    WriteVector(w, new Vector3(stage.x, stage.y, 0f));
                    w.EndObject();
                }
                w.EndArray();
                w.Property("BeforeShellingSignalTime", s.WarningTime);
                w.Property("UsedInPlanedShelling", zone.Planned && !zone.CalledStrikeTemplate);
                w.Property("UseInCalledShelling", zone.CalledStrikeTemplate);
                w.Property("IsActive", zone.Active);
                w.EndObject();
            }
            w.EndArray();
            w.Name("Brigades");
            w.BeginArray();
            foreach (var battery in report.MortarBatteries)
            {
                w.BeginObject();
                w.Property("ID", battery.Id);
                w.Name("ArtilleryGuns");
                w.BeginArray();
                foreach (var gun in TarkovMortar.Guns(battery))
                {
                    w.BeginObject();
                    w.Name("Position");
                    WriteVector(w, gun.position);
                    w.EndObject();
                }
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
        }

        // CustomMapLoader turns these into SPT loose loot spawnpoints
        private static void WriteLootSpawn(JsonWriter w, TarkovSDK.TarkovLootSpawn spawn)
        {
            w.BeginObject();
            var p = spawn.transform.position;
            w.Property("Id", string.Format(CultureInfo.InvariantCulture, "{0} [{1:F2},{2:F2},{3:F2}]", spawn.name, p.x, p.y, p.z));
            w.Name("Position");
            WriteVector(w, spawn.transform.position);
            w.Name("Rotation");
            WriteVector(w, spawn.transform.eulerAngles);
            w.Property("Probability", spawn.Probability);
            w.Property("AlwaysSpawn", spawn.AlwaysSpawn);
            w.Property("UseGravity", spawn.UseGravity);
            w.Property("RandomRotation", spawn.RandomRotation);
            w.Name("Items");
            w.BeginArray();
            foreach (var item in spawn.Items)
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(item.Template ?? "", "^[0-9a-fA-F]{24}$"))
                    continue;
                w.BeginObject();
                w.Property("Template", item.Template.ToLowerInvariant());
                w.Property("Weight", Mathf.Max(1, item.Weight));
                w.Property("StackCount", Mathf.Max(1, item.StackCount));
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
        }

        // CustomMapLoader turns these into SPT static containers: the game only gives a container an
        // inventory (LootableContainer.Init) when the raid's loot lists it by Id. As in
        // LootableContainer.AsLootPointParameters, CanBeEmpty and AlwaysHasLoot always spawn.
        private static void WriteContainer(JsonWriter w, LootableContainer container, bool alwaysSpawn)
        {
            w.BeginObject();
            w.Property("Id", container.Id);
            w.Property("Template", container.Template.Trim().ToLowerInvariant());
            w.Property("Probability", Mathf.Clamp(container.SpawnChance, 0, 100) / 100f);
            w.Property("AlwaysSpawn", alwaysSpawn || container.SpawnType != ELootableContainerSpawnType.MayNotSpawn);
            var group = TarkovContainerGroups.GroupOf(container);
            w.Property("Group", group != null ? group.Id : "");
            w.EndObject();
        }

        // CustomMapLoader registers a new type as an item made from BaseTemplate with its own grid, and sets the
        // map's loot pool for it (or for the retail type)
        private static void WriteContainerType(JsonWriter w, TarkovSDK.TarkovContainerDefinition type)
        {
            w.BeginObject();
            w.Property("Template", (type.Template ?? "").Trim().ToLowerInvariant());
            w.Property("New", type.NewContainer);
            w.Property("Name", type.DisplayName ?? "");
            w.Property("ShortName", type.ShortName ?? "");
            w.Property("Description", type.Description ?? "");
            w.Property("Width", type.Width);
            w.Property("Height", type.Height);
            w.Property("BaseTemplate", (type.BaseTemplate ?? "").Trim().ToLowerInvariant());
            w.Property("Loot", type.Loot.ToString());
            w.Property("RetailLootTemplate", (type.RetailLootTemplate ?? "").Trim().ToLowerInvariant());
            w.Property("MinItems", type.MinItems);
            w.Property("MaxItems", type.MaxItems);
            w.Name("Items");
            w.BeginArray();
            foreach (var item in type.Items.Where(i => System.Text.RegularExpressions.Regex.IsMatch(i.Template ?? "", "^[0-9a-fA-F]{24}$")))
            {
                w.BeginObject();
                w.Property("Template", item.Template.Trim().ToLowerInvariant());
                w.Property("Weight", item.Weight);
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
        }

        // the client plugin's own raid features (Client/Features/FeatureConfig.cs reads these names)
        private static void WriteFeatures(JsonWriter w, TarkovScenePreparer.Report report)
        {
            w.BeginObject();
            w.Name("ForceTransits");
            w.BeginArray();
            foreach (var force in report.ForceTransits)
            {
                w.BeginObject();
                w.Property("TransitId", force.Transit.parameters.id);
                w.Property("OnEnter", force.OnEnter);
                w.Property("TriggerId", force.TriggerId ?? "");
                w.Property("IgnoreRequirements", force.IgnoreRequirements);
                w.Property("Hidden", force.Hidden);
                w.EndObject();
            }
            w.EndArray();

            w.Name("GearStrips");
            w.BeginArray();
            foreach (var strip in report.GearStrips)
            {
                w.BeginObject();
                w.Property("Name", strip.name);
                w.Property("OnRaidStart", strip.OnRaidStart);
                w.Property("TriggerId", strip.TriggerId ?? "");
                w.Property("Once", strip.Once);
                w.Property("Mode", strip.Mode.ToString());
                w.Property("ContainerId", strip.Container != null ? strip.Container.Id ?? "" : "");
                w.Name("Slots");
                w.BeginArray();
                foreach (var (slot, taken) in new[]
                         {
                             ("Headwear", strip.Headwear), ("Earpiece", strip.Earpiece), ("FaceCover", strip.FaceCover), ("Eyewear", strip.Eyewear),
                             ("ArmBand", strip.ArmBand), ("ArmorVest", strip.ArmorVest), ("TacticalVest", strip.TacticalVest), ("Backpack", strip.Backpack),
                             ("FirstPrimaryWeapon", strip.FirstPrimaryWeapon), ("SecondPrimaryWeapon", strip.SecondPrimaryWeapon), ("Holster", strip.Holster),
                             ("Scabbard", strip.Scabbard),
                         })
                    if (taken)
                        w.Value(slot);
                w.EndArray();
                w.Property("PocketContents", strip.PocketContents);
                w.Property("SecuredContainerContents", strip.SecuredContainerContents);
                w.Property("SpecialSlots", strip.SpecialSlots);
                if (strip.Notify)
                {
                    w.Name("Notification");
                    WriteNotification(w, strip.Notification);
                }
                w.EndObject();
            }
            w.EndArray();

            w.Name("Notifications");
            w.BeginArray();
            foreach (var notification in report.Notifications)
            {
                w.BeginObject();
                w.Property("TriggerId", notification.TriggerId ?? "");
                w.Property("Once", notification.Once);
                w.Name("Notification");
                WriteNotification(w, notification.Notification);
                w.EndObject();
            }
            w.EndArray();

            w.Name("Captions");
            w.BeginArray();
            foreach (var caption in report.Captions)
            {
                w.BeginObject();
                w.Property("TriggerId", caption.TriggerId ?? "");
                w.Property("Once", caption.Once);
                w.Name("Lines");
                w.BeginArray();
                foreach (var line in caption.Lines.Where(l => l != null && !string.IsNullOrWhiteSpace(l.Text)))
                {
                    w.BeginObject();
                    w.Property("Text", line.Text);
                    w.Property("Start", line.StartSeconds);
                    w.Property("End", line.StartSeconds + line.Duration);
                    w.EndObject();
                }
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();

            // the game's EBodyPart / EDamageType names
            w.Name("HealthChanges");
            w.BeginArray();
            foreach (var health in report.HealthChanges)
            {
                w.BeginObject();
                w.Property("TriggerId", health.TriggerId ?? "");
                w.Property("StopTriggerId", health.StopTriggerId ?? "");
                w.Property("Once", health.Once);
                w.Property("Chance", health.Chance);
                w.Property("Change", health.Change.ToString());
                w.Name("BodyParts");
                w.BeginArray();
                foreach (TarkovSDK.TarkovHealthChange.Parts part in Enum.GetValues(typeof(TarkovSDK.TarkovHealthChange.Parts)))
                    if (part != TarkovSDK.TarkovHealthChange.Parts.All && (health.BodyParts & part) != 0)
                        w.Value(part.ToString());
                w.EndArray();
                w.Property("Amount", health.Amount);
                w.Property("DamageType", health.DamageType.ToString());
                w.Property("Repeat", health.Repeat);
                w.Property("Interval", health.Interval);
                w.EndObject();
            }
            w.EndArray();

            w.Name("Effects");
            w.BeginArray();
            foreach (var effect in report.Effects)
            {
                w.BeginObject();
                w.Property("TriggerId", effect.TriggerId ?? "");
                w.Property("Once", effect.Once);
                w.Property("Chance", effect.Chance);
                w.Name("Effects");
                w.BeginArray();
                foreach (var e in effect.Effects.Where(e => e != null))
                {
                    w.BeginObject();
                    w.Property("Effect", e.Effect.ToString());
                    w.Property("BodyPart", e.BodyPart.ToString());
                    w.Property("Duration", e.Duration);
                    w.Property("Strength", e.Strength);
                    w.Property("Delay", e.Delay);
                    w.Property("Buff", e.Buff.ToString());
                    w.EndObject();
                }
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();

            // the zones a trigger shells (the shelling itself is the game's, set up from "Mortar")
            w.Name("MortarStrikes");
            w.BeginArray();
            foreach (var zone in report.MortarZones.Where(z => !string.IsNullOrEmpty(z.TriggerId)))
            {
                w.BeginObject();
                w.Property("TriggerId", zone.TriggerId);
                w.Property("ZoneId", zone.Id.ToString(CultureInfo.InvariantCulture));
                w.Property("Delay", zone.TriggerDelay);
                w.Property("Once", zone.Once);
                w.EndObject();
            }
            w.EndArray();

            // the game's WildSpawnType / BotDifficulty names
            w.Name("SpawnBots");
            w.BeginArray();
            foreach (var bots in report.SpawnBots)
            {
                w.BeginObject();
                w.Property("TriggerId", bots.TriggerId);
                w.Property("Once", bots.Once);
                w.Property("Chance", bots.Chance);
                w.Property("Role", bots.BotRole.ToString());
                w.Property("Difficulty", bots.BotDifficulty.ToString());
                w.Property("Count", bots.Count);
                w.Property("AsGroup", bots.AsGroup);
                w.Property("Delay", bots.Delay);
                w.Property("MaxAliveBots", bots.MaxAliveBots);
                w.Name("Points");
                WritePoints(w, TarkovTriggeredSpawns.Points(bots));
                w.EndObject();
            }
            w.EndArray();

            w.Name("SpawnLoot");
            w.BeginArray();
            foreach (var loot in report.SpawnLoot)
            {
                w.BeginObject();
                w.Property("TriggerId", loot.TriggerId);
                w.Property("Once", loot.Once);
                w.Property("Chance", loot.Chance);
                w.Property("Pick", loot.Pick.ToString());
                w.Property("UseGravity", loot.UseGravity);
                w.Property("RandomRotation", loot.RandomRotation);
                w.Property("Delay", loot.Delay);
                w.Name("Items");
                w.BeginArray();
                foreach (var item in loot.Items.Where(i => i != null && System.Text.RegularExpressions.Regex.IsMatch(i.Template?.Trim() ?? "", "^[0-9a-fA-F]{24}$")))
                {
                    w.BeginObject();
                    w.Property("Template", item.Template.Trim().ToLowerInvariant());
                    w.Property("Weight", Mathf.Max(1, item.Weight));
                    w.Property("StackCount", Mathf.Max(1, item.StackCount));
                    w.EndObject();
                }
                w.EndArray();
                w.Name("Points");
                WritePoints(w, TarkovTriggeredSpawns.Points(loot));
                w.EndObject();
            }
            w.EndArray();

            // the SDK's mounted gun markers: the client places the game's prefab with its root here
            w.Name("MountedGuns");
            w.BeginArray();
            foreach (var gun in report.MountedGuns.Where(g => !g.InScene))
            {
                w.BeginObject();
                w.Property("Id", gun.Id);
                w.Property("Gun", gun.Weapon);
                w.Name("Position");
                WriteVector(w, gun.Marker.position + Vector3.up * TarkovMountedGuns.UtesRootHeight);
                w.Name("Rotation");
                WriteVector(w, new Vector3(0f, gun.Marker.eulerAngles.y, 0f));
                w.EndObject();
            }
            w.EndArray();

            // times in seconds; a step's object by scene and hierarchy path
            w.Name("WorldEvents");
            w.BeginArray();
            foreach (var (evt, name, steps) in report.WorldEvents)
            {
                w.BeginObject();
                w.Property("Name", name);
                w.Property("Start", evt.Start.ToString());
                w.Property("Earliest", evt.EarliestMinute * 60f);
                w.Property("Latest", Mathf.Max(evt.EarliestMinute, evt.LatestMinute) * 60f);
                w.Property("TriggerId", evt.TriggerId ?? "");
                w.Property("Chance", evt.Chance);
                w.Property("Repeat", evt.Repeat);
                w.Property("RepeatMin", evt.RepeatAfterMinMinutes * 60f);
                w.Property("RepeatMax", Mathf.Max(evt.RepeatAfterMinMinutes, evt.RepeatAfterMaxMinutes) * 60f);
                w.Name("Steps");
                w.BeginArray();
                foreach (var (step, scene, path) in steps)
                {
                    w.BeginObject();
                    w.Property("Wait", Mathf.Max(0f, step.Wait));
                    w.Property("Kind", step.Kind.ToString());
                    w.Property("TriggerId", step.TriggerId ?? "");
                    w.Property("Scene", scene ?? "");
                    w.Property("Path", path ?? "");
                    if (step.Kind == TarkovSDK.TarkovWorldEvent.StepKind.Notification)
                    {
                        w.Name("Notification");
                        WriteNotification(w, step.Notification);
                    }
                    w.EndObject();
                }
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();

            w.Name("SpawnPrefabs");
            w.BeginArray();
            foreach (var (spawn, scene, path) in report.SpawnPrefabs)
            {
                w.BeginObject();
                w.Property("TriggerId", spawn.TriggerId);
                w.Property("Once", spawn.Once);
                w.Property("Chance", spawn.Chance);
                w.Property("Action", spawn.Action.ToString());
                w.Property("Scene", scene);
                w.Property("Path", path);
                w.Property("Delay", spawn.Delay);
                w.Property("Lifetime", spawn.Lifetime);
                w.Name("Points");
                WritePoints(w, TarkovTriggeredSpawns.Points(spawn));
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
        }

        // spawn points: position and rotation (euler degrees)
        private static void WritePoints(JsonWriter w, IEnumerable<Transform> points)
        {
            w.BeginArray();
            foreach (var p in points)
            {
                w.BeginObject();
                w.Name("Position");
                WriteVector(w, p.position);
                w.Name("Rotation");
                WriteVector(w, p.eulerAngles);
                w.EndObject();
            }
            w.EndArray();
        }

        // retail data names composite masks ("Pmc") when the value is exactly one of them
        private static void WriteSides(JsonWriter w, EPlayerSideMask sides)
        {
            if (sides == EPlayerSideMask.None)
            {
                w.BeginArray();
                w.Value("None");
                w.EndArray();
                return;
            }
            if (sides == EPlayerSideMask.Pmc || sides == EPlayerSideMask.All)
            {
                w.BeginArray();
                w.Value(sides.ToString());
                w.EndArray();
                return;
            }
            WriteFlags(w, (int)sides, typeof(EPlayerSideMask), "Pmc", "All");
        }

        private static void WriteFlags(JsonWriter w, int value, Type enumType, params string[] composites)
        {
            w.BeginArray();
            foreach (var name in Enum.GetNames(enumType))
            {
                if (composites.Contains(name))
                    continue;
                int flag = (int)Enum.Parse(enumType, name);
                if (flag != 0 && (value & flag) == flag)
                    w.Value(name);
            }
            w.EndArray();
        }

        // the game's CustomNotification values by name (ENotificationIconType, ENotificationDurationType, EUISoundType)
        private static void WriteNotification(JsonWriter w, TarkovSDK.TarkovNotification.Settings n)
        {
            w.BeginObject();
            w.Property("Message", n.Message ?? "");
            w.Property("Icon", n.Icon.ToString());
            w.Property("Duration", n.Duration.ToString());
            w.Property("Sound", n.Sound.ToString());
            if (n.UseTextColor)
            {
                w.Name("Color");
                WriteFloats(w, n.TextColor.r, n.TextColor.g, n.TextColor.b, n.TextColor.a);
            }
            w.EndObject();
        }

        private static void WriteFloats(JsonWriter w, params float[] values)
        {
            w.BeginArray();
            foreach (var v in values)
                w.Value(v);
            w.EndArray();
        }

        private static void WriteVector(JsonWriter w, Vector3 v)
        {
            w.BeginObject();
            w.Property("x", v.x);
            w.Property("y", v.y);
            w.Property("z", v.z);
            w.EndObject();
        }

        private static float MaxAbs(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));

        // minimal pretty-printing writer; avoids a JSON package dependency in the SDK project
        private sealed class JsonWriter
        {
            private readonly StringBuilder _sb = new StringBuilder();
            private readonly Stack<bool> _hasItems = new Stack<bool>();
            private bool _afterName;

            public void BeginObject() { BeforeValue(); _sb.Append('{'); _hasItems.Push(false); }
            public void EndObject() { Close('}'); }
            public void BeginArray() { BeforeValue(); _sb.Append('['); _hasItems.Push(false); }
            public void EndArray() { Close(']'); }

            public void Name(string name)
            {
                BeforeValue();
                _sb.Append(Quote(name)).Append(": ");
                _afterName = true;
            }

            public void Value(string value) { BeforeValue(); _sb.Append(Quote(value)); }
            public void Value(float value) => Raw(value.ToString("R", CultureInfo.InvariantCulture));
            public void Property(string name, string value) { Name(name); Value(value); }
            public void Property(string name, bool value) { Name(name); Raw(value ? "true" : "false"); }
            public void Property(string name, int value) { Name(name); Raw(value.ToString(CultureInfo.InvariantCulture)); }
            public void Property(string name, long value) { Name(name); Raw(value.ToString(CultureInfo.InvariantCulture)); }
            public void Property(string name, float value) { Name(name); Raw(value.ToString("R", CultureInfo.InvariantCulture)); }

            private void Raw(string text) { BeforeValue(); _sb.Append(text); }

            private void BeforeValue()
            {
                if (_afterName)
                {
                    _afterName = false;
                    return;
                }
                if (_hasItems.Count == 0)
                    return;
                if (_hasItems.Peek())
                    _sb.Append(',');
                _hasItems.Pop();
                _hasItems.Push(true);
                NewLine(_hasItems.Count);
            }

            private void Close(char c)
            {
                bool had = _hasItems.Pop();
                if (had)
                    NewLine(_hasItems.Count);
                _sb.Append(c);
            }

            private void NewLine(int depth) => _sb.Append('\n').Append(' ', depth * 2);

            private static string Quote(string s)
            {
                var sb = new StringBuilder("\"");
                foreach (var ch in s ?? "")
                {
                    switch (ch)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (ch < 32) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                            else sb.Append(ch);
                            break;
                    }
                }
                return sb.Append('"').ToString();
            }

            public override string ToString() => _sb.ToString() + "\n";
        }
    }
}
