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
    // (PMC level range, scav raids, required items) and how it shows on the map select screen.
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
        // and where each enabled one puts its button on the map select screen
        private void LoadUsedIds()
        {
            _usedIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _mapIcons = new List<(string, Vector2, bool)>();
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
                    // sandbox_high shares Ground Zero's button
                    if ((data.Value<bool?>("Enabled") ?? false) && (x != 0 || y != 0) && folder != "sandbox_high")
                        _mapIcons.Add((RetailNames.TryGetValue(folder, out var name) ? name : folder, new Vector2(x, y), custom));
                }
                catch (Exception)
                {
                    // another map's broken base.json is not this map's problem
                }
            }
        }

        // ------------------------------------------------------------ map select icon

        private List<(string Name, Vector2 Icon, bool Custom)> _mapIcons = new List<(string, Vector2, bool)>();

        private static readonly Dictionary<string, string> RetailNames = new Dictionary<string, string>
        {
            { "bigmap", "Customs" }, { "factory4_day", "Factory" }, { "factory4_night", "Factory (night)" }, { "interchange", "Interchange" },
            { "laboratory", "Labs" }, { "labyrinth", "Labyrinth" }, { "lighthouse", "Lighthouse" }, { "rezervbase", "Reserve" },
            { "sandbox", "Ground Zero" }, { "shoreline", "Shoreline" }, { "tarkovstreets", "Streets" }, { "terminal", "Terminal" }, { "woods", "Woods" },
        };

        // the map select screen in miniature: the game puts each location's button at (IconX, IconY)
        // thousandths of the map picture, from its bottom-left corner (LocationButton anchors at
        // Location.RelativeMapPos). Click to move this map's button there.
        private void DrawIconPreview()
        {
            const float size = 260f;
            Rect rect;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
            }
            EditorGUI.DrawRect(rect, new Color(0.11f, 0.12f, 0.11f));
            var grid = new Color(1f, 1f, 1f, 0.06f);
            for (int i = 1; i < 4; i++)
            {
                EditorGUI.DrawRect(new Rect(rect.x + rect.width * i / 4f, rect.y, 1f, rect.height), grid);
                EditorGUI.DrawRect(new Rect(rect.x, rect.y + rect.height * i / 4f, rect.width, 1f), grid);
            }

            Vector2 ToScreen(Vector2 icon) => new Vector2(rect.x + icon.x / 1000f * rect.width, rect.yMax - icon.y / 1000f * rect.height);
            var label = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(0.75f, 0.75f, 0.72f) } };
            foreach (var (name, icon, custom) in _mapIcons)
            {
                var p = ToScreen(icon);
                EditorGUI.DrawRect(new Rect(p.x - 3f, p.y - 3f, 6f, 6f), custom ? new Color(0.45f, 0.7f, 0.95f) : new Color(0.6f, 0.6f, 0.58f));
                GUI.Label(new Rect(p.x + 5f, p.y - 8f, 110f, 16f), name, label);
            }

            // this map's button: a small plate like the game's location buttons
            var me = ToScreen(new Vector2(_iconX, _iconY));
            var plate = new Rect(me.x - 9f, me.y - 6f, 18f, 12f);
            EditorGUI.DrawRect(new Rect(plate.x - 1f, plate.y - 1f, plate.width + 2f, plate.height + 2f), Color.black);
            EditorGUI.DrawRect(plate, new Color(0.95f, 0.75f, 0.25f));
            var mine = new GUIStyle(EditorStyles.miniBoldLabel) { normal = { textColor = new Color(0.95f, 0.8f, 0.35f) } };
            GUI.Label(new Rect(me.x + 11f, me.y - 8f, 140f, 16f), string.IsNullOrEmpty(_name) ? _definition.MapId : _name, mine);

            var e = Event.current;
            if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && e.button == 0 && rect.Contains(e.mousePosition))
            {
                _iconX = Mathf.RoundToInt((e.mousePosition.x - rect.x) / rect.width * 1000f);
                _iconY = Mathf.RoundToInt((rect.yMax - e.mousePosition.y) / rect.height * 1000f);
                GUI.changed = true;
                e.Use();
            }
            if (_iconX < 0 || _iconX > 1000 || _iconY < 0 || _iconY > 1000)
                EditorGUILayout.HelpBox("Off the map picture: the button won't be visible. Keep both between 0 and 1000.", MessageType.Warning);
            EditorGUILayout.LabelField("Grey: retail maps. Blue: your other custom maps. Click to move this map's button.", EditorStyles.centeredGreyMiniLabel);
        }

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
