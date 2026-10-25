using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // _Id, raid time and the Access tab of the Server Data Editor: who may enter the map
    // (PMC level range, scav raids, required items).
    public partial class TarkovServerDataWindow
    {
        private string _mongoId = "";
        private Dictionary<string, string> _usedIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool _sameRaidTime = true, _sameKeysPvE = true;
        private string _keySearch = "", _keyId = "";
        private Vector2 _keyScroll;

        // ------------------------------------------------------------ _Id

        private void DrawMongoId()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                _mongoId = EditorGUILayout.TextField(new GUIContent("_Id", "base.json \"_Id\": a unique 24-character hex MongoID. " +
                                                                          "The server registers the map's locale keys and transit targets under it."), _mongoId);
                if (GUILayout.Button("Generate", GUILayout.Width(70f)))
                {
                    _mongoId = NewMongoId();
                    GUI.FocusControl(null);
                    GUI.changed = true;
                }
            }
            var id = (_mongoId ?? "").Trim();
            if (!TarkovMongoId.IsValid(id))
                EditorGUILayout.HelpBox("_Id must be 24 hex characters (0-9, a-f). Press Generate for a new one.", MessageType.Error);
            else if (_usedIds.TryGetValue(id, out var owner))
                EditorGUILayout.HelpBox($"'{owner}' already uses this _Id, so the server will refuse to load this map. Press Generate for a new one.", MessageType.Error);
        }

        // _Ids of every other custom map next to this one and every retail location in the SPT install,
        // and how each shows on the map select screen (for the Map Select tab)
        private void LoadUsedIds()
        {
            _usedIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _others = new List<(string, string, Vector2, bool, bool, string)>();
            var dirs = new List<string>();
            var modMaps = Path.GetDirectoryName(MapDir);
            if (Directory.Exists(modMaps))
                dirs.AddRange(Directory.GetDirectories(modMaps));
            var retail = Path.Combine(_definition.SptRoot ?? "", "SPT_Runtime", "SPT_Data", "database", "locations");
            if (Directory.Exists(retail))
                dirs.AddRange(Directory.GetDirectories(retail));

            var self = NormalizePath(MapDir);
            foreach (var dir in dirs)
            {
                var path = Path.Combine(dir, "base.json");
                if (NormalizePath(dir) == self || !File.Exists(path))
                    continue;
                try
                {
                    var data = JObject.Parse(File.ReadAllText(path));
                    var id = data.Value<string>("_Id");
                    var folder = Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(id))
                        _usedIds[id] = folder;
                    int x = data.Value<int?>("IconX") ?? 0, y = data.Value<int?>("IconY") ?? 0;
                    bool custom = NormalizePath(Path.GetDirectoryName(dir)) == NormalizePath(modMaps);
                    var locationId = data.Value<string>("Id");
                    // sandbox_high shares Ground Zero's button, factory4_night Factory's; hideout isn't a raid
                    if (string.IsNullOrEmpty(locationId) || folder == "sandbox_high" || folder == "factory4_night" || folder == "hideout")
                        continue;
                    string name = RetailNames.TryGetValue(folder, out var retailName) ? retailName : folder;
                    if (custom && File.Exists(Path.Combine(dir, "locale.json")))
                        name = JObject.Parse(File.ReadAllText(Path.Combine(dir, "locale.json"))).Value<string>("Name") ?? folder;
                    string pack = custom ? TarkovMapBuilder.MenuPackId(dir) ?? "" : "";
                    _others.Add((locationId, name, new Vector2(x, y), custom, data.Value<bool?>("Enabled") ?? false, pack));
                }
                catch (Exception)
                {
                    // another map's broken base.json is not this map's problem
                }
            }
        }

        // ------------------------------------------------------------ other locations

        private List<(string Id, string Name, Vector2 Icon, bool Custom, bool Enabled, string Pack)> _others =
            new List<(string, string, Vector2, bool, bool, string)>();

        private static readonly Dictionary<string, string> RetailNames = new Dictionary<string, string>
        {
            { "bigmap", "Customs" }, { "factory4_day", "Factory" }, { "interchange", "Interchange" },
            { "laboratory", "Labs" }, { "labyrinth", "Labyrinth" }, { "lighthouse", "Lighthouse" }, { "rezervbase", "Reserve" },
            { "sandbox", "Ground Zero" }, { "shoreline", "Shoreline" }, { "tarkovstreets", "Streets" }, { "terminal", "Terminal" }, { "woods", "Woods" },
        };

        private string NewMongoId()
        {
            string id;
            do id = TarkovMongoId.New();
            while (_usedIds.ContainsKey(id));
            return id;
        }

        private static string NormalizePath(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();

        // ------------------------------------------------------------ raid time

        private void DrawRaidTime()
        {
            EditorGUILayout.LabelField("Raid (base.json)", EditorStyles.boldLabel);
            int time = _base.Value<int?>("EscapeTimeLimit") ?? 40;
            int newTime = Mathf.Max(1, EditorGUILayout.IntField(new GUIContent("Raid Time (min)", "EscapeTimeLimit: when it runs out, everyone still in the raid is MIA."), time));
            if (newTime != time)
            {
                _base["EscapeTimeLimit"] = newTime;
                if (_sameRaidTime)
                    SetOtherRaidTimes(newTime);
            }
            bool same = EditorGUILayout.Toggle(new GUIContent("Same In PvE And Co-op", "Also writes EscapeTimeLimitPVE and EscapeTimeLimitCoop."), _sameRaidTime);
            if (same != _sameRaidTime)
            {
                _sameRaidTime = same;
                if (same)
                    SetOtherRaidTimes(newTime);
            }
            if (!_sameRaidTime)
                using (new EditorGUI.IndentLevelScope())
                {
                    IntField("PvE (min)", "EscapeTimeLimitPVE", "EscapeTimeLimitPVE");
                    IntField("Co-op (min)", "EscapeTimeLimitCoop", "EscapeTimeLimitCoop");
                }

            int botStop = _base.Value<int?>("BotStop") ?? 0;
            if (botStop > newTime * 60)
                EditorGUILayout.HelpBox($"Bots spawn until {botStop}s (Bots tab), after the raid ends at {newTime * 60}s.", MessageType.Info);
            EditorGUILayout.HelpBox("Scav raids start with less time left; the server mod copies those settings from factory4_day.", MessageType.None);
        }

        private void SetOtherRaidTimes(int minutes)
        {
            _base["EscapeTimeLimitPVE"] = minutes;
            _base["EscapeTimeLimitCoop"] = minutes;
        }

        private bool RaidTimesMatch()
        {
            int time = _base.Value<int?>("EscapeTimeLimit") ?? 0;
            return (_base.Value<int?>("EscapeTimeLimitPVE") ?? time) == time && (_base.Value<int?>("EscapeTimeLimitCoop") ?? time) == time;
        }

        // ------------------------------------------------------------ access

        private void DrawAccess()
        {
            EditorGUILayout.LabelField("Map select screen", EditorStyles.boldLabel);
            BoolField("Enabled", "Enabled", "Listed on the map select screen.");
            BoolField("Locked", "Locked", "Listed, but can't be picked.");
            BoolField("Insurance", "Insurance", "Insurance works on this map. Labs has it off.");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("PMC raids", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                IntField("Level Min", "RequiredPlayerLevelMin", "RequiredPlayerLevelMin: lowest PMC level that may enter.");
                IntField("Max", "RequiredPlayerLevelMax", "RequiredPlayerLevelMax: highest PMC level that may enter. Ground Zero uses 20, other maps 100.");
            }
            if ((_base.Value<int?>("RequiredPlayerLevelMin") ?? 0) > (_base.Value<int?>("RequiredPlayerLevelMax") ?? 0))
                EditorGUILayout.HelpBox("Level Min is above Level Max: no PMC can enter.", MessageType.Error);
            IntField("Max Group Size", "PmcMaxPlayersInGroup", "PmcMaxPlayersInGroup");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scav raids", EditorStyles.boldLabel);
            bool scavs = !(_base.Value<bool?>("DisabledForScav") ?? false);
            bool newScavs = EditorGUILayout.Toggle(new GUIContent("Allow Scav Raids", "Clears DisabledForScav. Labs and Ground Zero 21+ have scav raids off."), scavs);
            if (newScavs != scavs)
                _base["DisabledForScav"] = !newScavs;
            using (new EditorGUI.DisabledScope(!newScavs))
                IntField("Max Group Size", "ScavMaxPlayersInGroup", "ScavMaxPlayersInGroup");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Required items", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Items a player needs to enter, like the Labs keycard (AccessKeys).", EditorStyles.wordWrappedMiniLabel);
            IntField("Min Level For Items", "MinPlayerLvlAccessKeys", "MinPlayerLvlAccessKeys. Every retail map uses 0.");
            var keys = EnsureArray("AccessKeys");
            DrawKeyList(keys);
            bool sameKeys = EditorGUILayout.Toggle(new GUIContent("Same In PvE", "Also writes the list to AccessKeysPvE. SPT raids are PvE raids."), _sameKeysPvE);
            if (sameKeys != _sameKeysPvE)
                _sameKeysPvE = sameKeys;
            JArray pveKeys = null;
            if (!_sameKeysPvE)
            {
                EditorGUILayout.LabelField("PvE (AccessKeysPvE)", EditorStyles.miniBoldLabel);
                DrawKeyList(pveKeys = EnsureArray("AccessKeysPvE"));
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                _keyId = EditorGUILayout.TextField(new GUIContent("Add By Template ID"), _keyId);
                using (new EditorGUI.DisabledScope(!TarkovMongoId.IsValid(_keyId.Trim())))
                    if (GUILayout.Button("Add", GUILayout.Width(44f)))
                    {
                        AddKey(keys, _keyId.Trim());
                        _keyId = "";
                        GUI.FocusControl(null);
                    }
            }
            DrawKeySearch(keys, pveKeys);

            if (_sameKeysPvE && !JToken.DeepEquals(_base["AccessKeysPvE"], keys))
                _base["AccessKeysPvE"] = keys.DeepClone();

            DrawRandomTraps();
        }

        // base.json AirdropParameters[0] (the client reads index 0; later entries take over by PlaneAirdropEnd).
        // ServerAirdropManager counts only human players for MinPlayersCountToSpawnAirdrop, so retail's 6
        // means no plane drops in a solo SPT raid
        private void DrawAirdrops()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Plane drops", EditorStyles.boldLabel);
            var list = EnsureArray("AirdropParameters");
            if (list.Count == 0)
                list.Add(new JObject { ["AirdropPointDeactivateDistance"] = 60, ["MinPlayersCountToSpawnAirdrop"] = 1, ["PlaneAirdropChance"] = 0, ["PlaneAirdropCooldownMax"] = 850,
                                       ["PlaneAirdropCooldownMin"] = 550, ["PlaneAirdropEnd"] = 2200, ["PlaneAirdropMax"] = 0, ["PlaneAirdropStartMax"] = 550,
                                       ["PlaneAirdropStartMin"] = 250, ["UnsuccessfulTryPenalty"] = 120 });
            var p = (JObject)list[0];
            bool on = (p.Value<double?>("PlaneAirdropChance") ?? 0) > 0 && (p.Value<int?>("PlaneAirdropMax") ?? 0) > 0;
            bool newOn = EditorGUILayout.Toggle(new GUIContent("Plane Drops", "A plane drops crates on its own during the raid."), on);
            if (newOn != on)
            {
                p["PlaneAirdropChance"] = newOn ? 0.5 : 0;
                p["PlaneAirdropMax"] = newOn ? 1 : 0;
                if (newOn)
                    p["MinPlayersCountToSpawnAirdrop"] = 1;
                GUI.changed = true;
            }
            if (!newOn)
                return;
            using (new EditorGUI.IndentLevelScope())
            {
                float chance = EditorGUILayout.Slider(new GUIContent("Chance Per Try", "PlaneAirdropChance: each try drops a crate with this chance; a miss waits the Retry Delay."),
                                                      (float)(p.Value<double?>("PlaneAirdropChance") ?? 0), 0.01f, 1f);
                if (!Mathf.Approximately(chance, (float)(p.Value<double?>("PlaneAirdropChance") ?? 0)))
                    p["PlaneAirdropChance"] = System.Math.Round(chance, 2);
                AirdropInt(p, "Max Crates", "PlaneAirdropMax", "How many plane drops a raid can have.");
                using (new EditorGUILayout.HorizontalScope())
                {
                    AirdropInt(p, "First Try After (s) Min", "PlaneAirdropStartMin", "Seconds into the raid of the first try: between Min and Max.");
                    AirdropInt(p, "Max", "PlaneAirdropStartMax", null);
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    AirdropInt(p, "Between Drops (s) Min", "PlaneAirdropCooldownMin", "Seconds after a drop before the next try.");
                    AirdropInt(p, "Max", "PlaneAirdropCooldownMax", null);
                }
                AirdropInt(p, "Retry Delay (s)", "UnsuccessfulTryPenalty", "UnsuccessfulTryPenalty: seconds after a try that rolled no drop.");
                AirdropInt(p, "No Drops After (s)", "PlaneAirdropEnd", "PlaneAirdropEnd: no plane drops after this many seconds.");
                AirdropInt(p, "Min Players", "MinPlayersCountToSpawnAirdrop", "Living human players needed (bots don't count). Retail uses 6; keep 1 for solo SPT.");
            }
        }

        private static void AirdropInt(JObject p, string label, string key, string tooltip)
        {
            int value = p.Value<int?>(key) ?? 0;
            int next = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent(label, tooltip), value));
            if (next != value)
                p[key] = next;
        }

        // base.json EventTrapsData: when present, the game (LabyrinthSyncableTraps) picks the scene's
        // TrapSyncables each raid; Random.Range's max is exclusive
        private void DrawRandomTraps()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Random traps", EditorStyles.boldLabel);
            var data = _base["EventTrapsData"] as JObject;
            bool on = EditorGUILayout.Toggle(new GUIContent("Random Traps",
                "EventTrapsData: each raid opens some trap doors and keeps only some of the wires and spike traps marked Random Per Raid, as Labyrinth does. " +
                "Off: every trap stays as placed."), data != null);
            if (on != (data != null))
            {
                if (on)
                    _base["EventTrapsData"] = data = new JObject { ["MinTrapDoors"] = 1, ["MaxTrapDoors"] = 3, ["MinBarbedWires"] = 3, ["MaxBarbedWires"] = 6 };
                else
                {
                    _base.Remove("EventTrapsData");
                    data = null;
                }
                GUI.changed = true;
            }
            if (data == null)
                return;
            using (new EditorGUI.IndentLevelScope())
            {
                TrapRange(data, "Open Trap Doors", "MinTrapDoors", "MaxTrapDoors");
                TrapRange(data, "Wires And Spikes", "MinBarbedWires", "MaxBarbedWires");
                EditorGUILayout.LabelField("Each raid picks a count from Min up to one below Max (never more than the map has). Labyrinth: 5-9 trap doors, 10-20 wires and spikes.",
                                           EditorStyles.wordWrappedMiniLabel);
            }
        }

        private static void TrapRange(JObject data, string label, string minKey, string maxKey)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                int min = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent(label + " Min"), data.Value<int?>(minKey) ?? 0));
                int max = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent("Max"), data.Value<int?>(maxKey) ?? 0));
                if (min != (data.Value<int?>(minKey) ?? 0))
                    data[minKey] = min;
                if (max != (data.Value<int?>(maxKey) ?? 0))
                    data[maxKey] = max;
            }
        }

        private static void DrawKeyList(JArray keys)
        {
            if (keys.Count == 0)
                EditorGUILayout.LabelField("None: no item needed.", EditorStyles.miniLabel);
            int remove = -1;
            for (int i = 0; i < keys.Count; i++)
                using (new EditorGUILayout.HorizontalScope())
                {
                    var id = (string)keys[i] ?? "";
                    EditorGUILayout.LabelField(ItemName(id), GUILayout.MinWidth(120f));
                    EditorGUILayout.SelectableLabel(id, EditorStyles.miniLabel, GUILayout.Width(170f), GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    if (GUILayout.Button("-", GUILayout.Width(22f)))
                        remove = i;
                }
            if (remove >= 0)
            {
                keys.RemoveAt(remove);
                GUI.changed = true;
            }
        }

        // item search from the SPT item database, as in the loot spawn inspector
        private void DrawKeySearch(JArray keys, JArray pveKeys)
        {
            var items = TarkovLootSpawnEditor.ItemDatabase.Items;
            if (items == null)
            {
                EditorGUILayout.HelpBox(TarkovLootSpawnEditor.ItemDatabase.Error ?? "Load the item database to search items by name.", MessageType.Info);
                if (GUILayout.Button("Load Item Database"))
                    TarkovLootSpawnEditor.ItemDatabase.Load();
                return;
            }
            _keySearch = EditorGUILayout.TextField("Search Items", _keySearch);
            if (_keySearch.Length < 2)
                return;
            var matches = items.Where(i => i.Name.IndexOf(_keySearch, StringComparison.OrdinalIgnoreCase) >= 0
                                           || i.Template.StartsWith(_keySearch, StringComparison.OrdinalIgnoreCase)).Take(40).ToList();
            _keyScroll = EditorGUILayout.BeginScrollView(_keyScroll, GUILayout.MaxHeight(220f));
            foreach (var item in matches)
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(item.Name, GUILayout.MinWidth(120f));
                    EditorGUILayout.LabelField(item.Category, EditorStyles.miniLabel, GUILayout.Width(110f));
                    if (GUILayout.Button("Add", GUILayout.Width(44f)))
                        AddKey(keys, item.Template);
                    if (pveKeys != null && GUILayout.Button("Add PvE", GUILayout.Width(60f)))
                        AddKey(pveKeys, item.Template);
                }
            EditorGUILayout.EndScrollView();
        }

        private static void AddKey(JArray keys, string template)
        {
            if (keys.Any(k => string.Equals((string)k, template, StringComparison.OrdinalIgnoreCase)))
                return;
            keys.Add(template);
            GUI.changed = true;
        }

        private static string ItemName(string template)
        {
            var items = TarkovLootSpawnEditor.ItemDatabase.Items;
            if (items == null)
                return "(item database not loaded)";
            int index = items.FindIndex(i => i.Template == template);
            return index >= 0 ? items[index].Name : "(unknown item)";
        }
    }

    // MongoDB ObjectIds, the 24-character hex ids SPT uses for locations, items and profiles:
    // 4 bytes of Unix time, 5 random bytes and a 3-byte counter
    public static class TarkovMongoId
    {
        private static int _counter = new System.Random().Next(0x1000000);

        public static string New()
        {
            var bytes = new byte[12];
            uint time = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bytes[0] = (byte)(time >> 24);
            bytes[1] = (byte)(time >> 16);
            bytes[2] = (byte)(time >> 8);
            bytes[3] = (byte)time;
            using (var rng = RandomNumberGenerator.Create())
            {
                var random = new byte[5];
                rng.GetBytes(random);
                Array.Copy(random, 0, bytes, 4, 5);
            }
            _counter = (_counter + 1) & 0xFFFFFF;
            bytes[9] = (byte)(_counter >> 16);
            bytes[10] = (byte)(_counter >> 8);
            bytes[11] = (byte)_counter;
            return string.Concat(bytes.Select(b => b.ToString("x2")));
        }

        public static bool IsValid(string id) =>
            id != null && id.Length == 24 && id.All(Uri.IsHexDigit);
    }
}
