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

        private static void WriteExit(JsonWriter w, ExfiltrationPoint exfil)
        {
            var s = exfil.Settings;
            string requirement = exfil.Requirements != null && exfil.Requirements.Length > 0
                ? exfil.Requirements[0].Requirement.ToString()
                : "None";
            string tip = exfil.Requirements != null && exfil.Requirements.Length > 0
                ? exfil.Requirements[0].RequirementTip ?? ""
                : "";

            w.BeginObject();
            w.Property("Chance", s.Chance);
            w.Property("ChancePVE", s.Chance);
            w.Property("Count", 0);
            w.Property("CountPVE", 0);
            w.Property("EntryPoints", s.EntryPoints ?? "");
            w.Property("EventAvailable", s.EventAvailable);
            w.Property("ExfiltrationTime", s.ExfiltrationTime);
            w.Property("ExfiltrationTimePVE", s.ExfiltrationTime);
            w.Property("ExfiltrationType", s.ExfiltrationType.ToString());
            w.Property("Id", s.Id ?? "");
            w.Property("MaxTime", s.MaxTime);
            w.Property("MaxTimePVE", s.MaxTime);
            w.Property("MinTime", s.MinTime);
            w.Property("MinTimePVE", s.MinTime);
            w.Property("Name", s.Name);
            w.Property("PassageRequirement", requirement);
            w.Property("PlayersCount", s.PlayersCount);
            w.Property("PlayersCountPVE", s.PlayersCount);
            w.Property("RequirementTip", tip);
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
