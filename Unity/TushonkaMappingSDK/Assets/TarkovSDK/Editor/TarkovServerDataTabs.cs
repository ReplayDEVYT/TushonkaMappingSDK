using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Game.Spawning;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Bots, Spawns and Loot tabs of the Server Data Editor. Bot settings, waves and boss spawns are
    // base.json values; spawn points and loot spawns are scene objects, listed here for overview.
    public partial class TarkovServerDataWindow
    {
        private static readonly string[] Difficulties = { "easy", "normal", "hard", "impossible" };
        private static readonly string[] Sides = { "Savage", "Bear", "Usec" };
        private static readonly string[] BotTypes = Enum.GetNames(typeof(WildSpawnType)).OrderBy(n => n).ToArray();
        private static readonly string[] BossTypes = BotTypes.Where(n => n.StartsWith("boss") || n.StartsWith("pmc") || n == "exUsec" || n == "sectantPriest").ToArray();
        private static readonly string[] EscortTypes = BotTypes.Where(n => n.StartsWith("follower") || n.StartsWith("pmc") || n == "exUsec" || n == "assault" || n.StartsWith("sectant")).ToArray();
        private readonly HashSet<int> _openWaves = new HashSet<int>(), _openBosses = new HashSet<int>();

        // ------------------------------------------------------------ bots

        private void DrawBots()
        {
            var zones = SceneZones();
            EditorGUILayout.LabelField("Bot limits", EditorStyles.boldLabel);
            IntField("Max Bots", "BotMax", "Most bots alive at once.");
            IntField("Max Bots (PvE)", "BotMaxPvE", "Most bots alive at once in PvE raids (SPT).");
            IntField("Max Bots Per Zone", "MaxBotPerZone");
            IntField("Start Spawning After (s)", "BotStart");
            IntField("Stop Spawning After (s)", "BotStop");
            IntField("Spawn Check Period (s)", "BotSpawnPeriodCheck");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scav mix (percent)", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                IntField("Easy", "BotEasy"); IntField("Normal", "BotNormal");
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                IntField("Hard", "BotHard"); IntField("Impossible", "BotImpossible");
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                IntField("Assault", "BotAssault"); IntField("Marksman", "BotMarksman");
            }

            EditorGUILayout.Space();
            var open = _base.Value<string>("OpenZones") ?? "";
            var newOpen = ZoneField(new GUIContent("Open Zones", "Bot zones random spawns may use."), open, zones);
            if (newOpen != open)
                _base["OpenZones"] = newOpen;

            // -------------------------------------------- waves
            EditorGUILayout.Space();
            var waves = EnsureArray("waves");
            EditorGUILayout.LabelField($"Waves ({waves.Count})", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Groups of bots that spawn in a zone during the raid. Time -1 means at raid start.", EditorStyles.wordWrappedMiniLabel);
            int remove = -1;
            for (int i = 0; i < waves.Count; i++)
            {
                var w = (JObject)waves[i];
                bool expanded = _openWaves.Contains(i);
                using (new EditorGUILayout.HorizontalScope())
                {
                    var title = $"#{i}  {w.Value<string>("WildSpawnType")} x{w.Value<int>("slots_min")}-{w.Value<int>("slots_max")}  in {Short(w.Value<string>("SpawnPoints"))}";
                    if (EditorGUILayout.Foldout(expanded, title, true) != expanded)
                        if (expanded) _openWaves.Remove(i); else _openWaves.Add(i);
                    if (GUILayout.Button("-", GUILayout.Width(22f)))
                        remove = i;
                }
                if (!_openWaves.Contains(i))
                    continue;
                using (new EditorGUI.IndentLevelScope())
                {
                    Set(w, "SpawnPoints", ZoneField(new GUIContent("Zones"), w.Value<string>("SpawnPoints") ?? "", zones));
                    Set(w, "WildSpawnType", Popup("Bot Type", w.Value<string>("WildSpawnType"), BotTypes));
                    Set(w, "BotPreset", Popup("Difficulty", w.Value<string>("BotPreset"), Difficulties));
                    Set(w, "BotSide", Popup("Side", w.Value<string>("BotSide"), Sides));
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        Set(w, "slots_min", EditorGUILayout.IntField("Count Min", w.Value<int?>("slots_min") ?? 1));
                        Set(w, "slots_max", EditorGUILayout.IntField("Max", w.Value<int?>("slots_max") ?? 1));
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        Set(w, "time_min", EditorGUILayout.IntField("Time Min (s)", w.Value<int?>("time_min") ?? -1));
                        Set(w, "time_max", EditorGUILayout.IntField("Max", w.Value<int?>("time_max") ?? -1));
                    }
                    Set(w, "KeepZoneOnSpawn", EditorGUILayout.Toggle("Keep Zone On Spawn", w.Value<bool?>("KeepZoneOnSpawn") ?? false));
                    Set(w, "isPlayers", EditorGUILayout.Toggle(new GUIContent("Player Scav Wave", "Spawns as player-scav style bots."), w.Value<bool?>("isPlayers") ?? false));
                }
            }
            if (remove >= 0)
            {
                waves.RemoveAt(remove);
                _openWaves.Clear();
                GUI.changed = true;
            }
            if (GUILayout.Button("Add Wave"))
            {
                waves.Add(new JObject
                {
                    ["BotPreset"] = "normal", ["BotSide"] = "Savage", ["KeepZoneOnSpawn"] = false,
                    ["SpawnMode"] = new JArray("pve", "regular"), ["SpawnPoints"] = zones.FirstOrDefault() ?? "",
                    ["WildSpawnType"] = "assault", ["isPlayers"] = false, ["number"] = waves.Count,
                    ["slots_max"] = 2, ["slots_min"] = 1, ["time_max"] = -1, ["time_min"] = -1,
                });
                _openWaves.Add(waves.Count - 1);
                GUI.changed = true;
            }

            // -------------------------------------------- bosses
            EditorGUILayout.Space();
            var bosses = EnsureArray("BossLocationSpawn");
            EditorGUILayout.LabelField($"Boss spawns ({bosses.Count})", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("A boss, raider or rogue group with escorts, rolled once per raid.", EditorStyles.wordWrappedMiniLabel);
            remove = -1;
            for (int i = 0; i < bosses.Count; i++)
            {
                var b = (JObject)bosses[i];
                bool expanded = _openBosses.Contains(i);
                using (new EditorGUILayout.HorizontalScope())
                {
                    var title = $"#{i}  {b.Value<string>("BossName")} {b.Value<int?>("BossChance") ?? 0}%  in {Short(b.Value<string>("BossZone"))}";
                    if (EditorGUILayout.Foldout(expanded, title, true) != expanded)
                        if (expanded) _openBosses.Remove(i); else _openBosses.Add(i);
                    if (GUILayout.Button("-", GUILayout.Width(22f)))
                        remove = i;
                }
                if (!_openBosses.Contains(i))
                    continue;
                using (new EditorGUI.IndentLevelScope())
                {
                    Set(b, "BossName", Popup("Boss", b.Value<string>("BossName"), BossTypes));
                    Set(b, "BossChance", EditorGUILayout.IntSlider("Chance (%)", b.Value<int?>("BossChance") ?? 100, 0, 100));
                    Set(b, "BossZone", ZoneField(new GUIContent("Zones"), b.Value<string>("BossZone") ?? "", zones));
                    Set(b, "BossDifficult", Popup("Difficulty", b.Value<string>("BossDifficult"), Difficulties));
                    Set(b, "BossEscortType", Popup("Escort Type", b.Value<string>("BossEscortType"), EscortTypes));
                    Set(b, "BossEscortAmount", EditorGUILayout.TextField(new GUIContent("Escort Amount", "A number, or weights like \"0,1,2\"."), b.Value<string>("BossEscortAmount") ?? "0"));
                    Set(b, "BossEscortDifficult", Popup("Escort Difficulty", b.Value<string>("BossEscortDifficult"), Difficulties));
                    Set(b, "Time", EditorGUILayout.IntField(new GUIContent("Time (s)", "-1 = at raid start"), b.Value<int?>("Time") ?? -1));
                    Set(b, "ForceSpawn", EditorGUILayout.Toggle("Force Spawn", b.Value<bool?>("ForceSpawn") ?? false));
                    Set(b, "IgnoreMaxBots", EditorGUILayout.Toggle("Ignore Max Bots", b.Value<bool?>("IgnoreMaxBots") ?? true));
                }
            }
            if (remove >= 0)
            {
                bosses.RemoveAt(remove);
                _openBosses.Clear();
                GUI.changed = true;
            }
            if (GUILayout.Button("Add Boss Spawn"))
            {
                bosses.Add(new JObject
                {
                    ["BossChance"] = 50, ["BossDifficult"] = "normal", ["BossEscortAmount"] = "2", ["BossEscortDifficult"] = "normal",
                    ["BossEscortType"] = "followerBully", ["BossName"] = "bossBully", ["BossPlayer"] = false,
                    ["BossZone"] = zones.FirstOrDefault() ?? "", ["Delay"] = 0, ["DependKarma"] = false, ["DependKarmaPVE"] = false,
                    ["ForceSpawn"] = false, ["IgnoreMaxBots"] = true, ["RandomTimeSpawn"] = false, ["ShowOnTarkovMap"] = false,
                    ["ShowOnTarkovMapPvE"] = false, ["SpawnMode"] = new JArray("regular", "pve"), ["Supports"] = null,
                    ["Time"] = -1, ["TriggerId"] = "", ["TriggerName"] = "",
                });
                _openBosses.Add(bosses.Count - 1);
                GUI.changed = true;
            }
        }

        // ------------------------------------------------------------ spawns

        private void DrawSpawns()
        {
            var markers = UnityEngine.Object.FindObjectsOfType<SpawnPointMarker>(true).OrderBy(m => m.name).ToList();
            int pmc = markers.Count(m => m._spawnPoint != null && (m._spawnPoint.Sides & EPlayerSideMask.Pmc) != 0 && (m._spawnPoint.Categories & ESpawnCategoryMask.Player) != 0);
            int bots = markers.Count(m => m._spawnPoint != null && (m._spawnPoint.Categories & ESpawnCategoryMask.Bot) != 0);
            EditorGUILayout.LabelField($"Spawn points in the open scenes: {markers.Count} ({pmc} player, {bots} bot)", EditorStyles.boldLabel);
            if (pmc == 0)
                EditorGUILayout.HelpBox("No player (PMC) spawn point: the raid cannot start.", MessageType.Error);
            if (bots == 0)
                EditorGUILayout.HelpBox("No bot spawn point: waves have nowhere to spawn.", MessageType.Warning);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Player Spawn At View"))
                    AddSpawn(true);
                if (GUILayout.Button("Add Bot Spawn At View"))
                    AddSpawn(false);
            }
            EditorGUILayout.Space();
            foreach (var m in markers)
            {
                var sp = m._spawnPoint;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(m.name, GUILayout.MinWidth(110f));
                    EditorGUILayout.LabelField(sp == null ? "-" : $"{sp.Sides} / {sp.Categories}", EditorStyles.miniLabel, GUILayout.MinWidth(120f));
                    EditorGUILayout.LabelField(sp?.BotZone != null ? sp.BotZone.name : "no zone", EditorStyles.miniLabel, GUILayout.Width(90f));
                    if (GUILayout.Button("Select", GUILayout.Width(52f)))
                    {
                        Selection.activeGameObject = m.gameObject;
                        SceneView.FrameLastActiveSceneView();
                    }
                }
            }
            EditorGUILayout.HelpBox("Edit a spawn point's sides, categories and entry point (Infiltration) in its inspector. Bot spawns belong under a Bot Zone.", MessageType.None);
        }

        private static void AddSpawn(bool player)
        {
            var zone = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<BotZone>() : null;
            if (zone == null)
                zone = UnityEngine.Object.FindObjectOfType<BotZone>();
            var marker = TarkovSceneTools.CreateSpawnPoint(zone != null ? zone.transform : null, player ? "Spawn_PMC" : "Spawn_Bot", Vector3.zero, player);
            if (SceneView.lastActiveSceneView != null)
                marker.transform.position = SceneView.lastActiveSceneView.pivot;
            Undo.RegisterCreatedObjectUndo(marker.gameObject, "Add spawn point");
            Selection.activeGameObject = marker.gameObject;
        }

        // ------------------------------------------------------------ loot

        private void DrawLoot()
        {
            var spawns = UnityEngine.Object.FindObjectsOfType<TarkovLootSpawn>(true).OrderBy(s => s.name).ToList();
            float expected = spawns.Where(s => !s.AlwaysSpawn && s.Items.Count > 0).Sum(s => s.Probability);
            int always = spawns.Count(s => s.AlwaysSpawn && s.Items.Count > 0);
            EditorGUILayout.LabelField($"Loot spawns in the open scenes: {spawns.Count}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"A raid spawns about {expected:0.#} loose item(s), plus {always} always-spawn item(s).", EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button("Add Loot Spawn At View"))
            {
                var go = new GameObject("LootSpawn") { tag = "EditorOnly" };
                if (SceneView.lastActiveSceneView != null)
                    go.transform.position = SceneView.lastActiveSceneView.pivot;
                go.AddComponent<TarkovLootSpawn>();
                Undo.RegisterCreatedObjectUndo(go, "Add loot spawn");
                Selection.activeGameObject = go;
            }
            EditorGUILayout.Space();
            foreach (var s in spawns)
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(s.name, GUILayout.MinWidth(110f));
                    EditorGUILayout.LabelField(s.Items.Count == 0 ? "no items" : $"{s.Items.Count} item(s), {(s.AlwaysSpawn ? "always" : $"{s.Probability:P0}")}",
                        EditorStyles.miniLabel, GUILayout.MinWidth(120f));
                    if (GUILayout.Button("Select", GUILayout.Width(52f)))
                    {
                        Selection.activeGameObject = s.gameObject;
                        SceneView.FrameLastActiveSceneView();
                    }
                }
            EditorGUILayout.HelpBox("Pick items in a loot spawn's inspector (search by name). Build Map exports them; the server turns them into the map's loose loot.", MessageType.None);
        }

        // ------------------------------------------------------------ helpers

        private void IntField(string label, string key, string tooltip = null)
        {
            int current = _base.Value<int?>(key) ?? 0;
            int value = EditorGUILayout.IntField(new GUIContent(label, tooltip), current);
            if (value != current)
                _base[key] = value;
        }

        private void BoolField(string label, string key, string tooltip = null)
        {
            bool current = _base.Value<bool?>(key) ?? false;
            bool value = EditorGUILayout.Toggle(new GUIContent(label, tooltip), current);
            if (value != current)
                _base[key] = value;
        }

        private JArray EnsureArray(string key)
        {
            if (!(_base[key] is JArray array))
                _base[key] = array = new JArray();
            return array;
        }

        private static void Set(JObject o, string key, JToken value)
        {
            if (!JToken.DeepEquals(o[key], value))
                o[key] = value;
        }

        private static string Popup(string label, string current, string[] options)
        {
            var list = options.ToList();
            if (!string.IsNullOrEmpty(current) && !list.Contains(current))
                list.Insert(0, current);
            int index = Mathf.Max(0, list.IndexOf(current ?? ""));
            return list.Count == 0 ? current : list[EditorGUILayout.Popup(label, index, list.ToArray())];
        }

        // zones as a multi-select of the scene's bot zones; names not in the scene are kept
        private static string ZoneField(GUIContent label, string current, List<string> zones)
        {
            var selected = current.Split(',').Select(z => z.Trim()).Where(z => z.Length > 0).ToList();
            if (zones.Count == 0 || zones.Count > 31)
                return EditorGUILayout.TextField(label, current);
            int mask = 0;
            for (int i = 0; i < zones.Count; i++)
                if (selected.Contains(zones[i]))
                    mask |= 1 << i;
            int newMask = EditorGUILayout.MaskField(label, mask, zones.ToArray());
            if (newMask == mask)
                return current;
            var result = selected.Where(z => !zones.Contains(z)).ToList();
            for (int i = 0; i < zones.Count; i++)
                if ((newMask & (1 << i)) != 0)
                    result.Add(zones[i]);
            return string.Join(",", result);
        }

        private static List<string> SceneZones() =>
            UnityEngine.Object.FindObjectsOfType<BotZone>(true).Select(z => z.name).Distinct().OrderBy(n => n).ToList();

        private static string Short(string zones)
        {
            if (string.IsNullOrEmpty(zones))
                return "no zone";
            var parts = zones.Split(',');
            return parts.Length <= 2 ? zones : $"{parts[0]},{parts[1]} +{parts.Length - 2}";
        }
    }
}
