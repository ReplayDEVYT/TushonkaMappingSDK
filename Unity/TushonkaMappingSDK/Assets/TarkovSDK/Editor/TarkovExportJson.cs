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

            w.Name("Transits");
            w.BeginArray();
            foreach (var transit in report.Transits)
                WriteTransit(w, transit);
            w.EndArray();

            w.Name("LootSpawns");
            w.BeginArray();
            foreach (var spawn in report.LootSpawns)
                WriteLootSpawn(w, spawn);
            w.EndArray();

            w.Name("Containers");
            w.BeginArray();
            foreach (var container in report.Containers)
                WriteContainer(w, container);
            w.EndArray();

            if (report.Btr != null)
            {
                w.Name("Btr");
                WriteBtr(w, report.Btr);
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
        private static void WriteTransit(JsonWriter w, TransitPoint transit)
        {
            var p = transit.parameters;
            w.BeginObject();
            w.Property("activateAfterSec", p.activateAfterSec);
            w.Property("active", p.active);
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
        private static void WriteContainer(JsonWriter w, LootableContainer container)
        {
            w.BeginObject();
            w.Property("Id", container.Id);
            w.Property("Template", container.Template.Trim().ToLowerInvariant());
            w.Property("Probability", Mathf.Clamp(container.SpawnChance, 0, 100) / 100f);
            w.Property("AlwaysSpawn", container.SpawnType != ELootableContainerSpawnType.MayNotSpawn);
            w.EndObject();
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
