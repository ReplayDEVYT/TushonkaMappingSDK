using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EFT.Interactive;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // Edits the server-side files of a map in the SPT install the Map Definition deploys to:
    //
    //   <SptRoot>/SPT_Runtime/user/mods/CustomMapLoader/maps/<MapId>/locale.json   name, description, extra keys
    //   <SptRoot>/SPT_Runtime/user/mods/CustomMapLoader/maps/<MapId>/base.json     ids, raid time, access, bots, button position
    //   <SptRoot>/SPT_Runtime/user/mods/CustomMapLoader/maps/<MapId>/menu.json     map pack, time selection, pictures, connections
    //
    // base.json and menu.json are loaded as a whole and written back with every other value untouched.
    // The SPT server reads both files at startup, so restart it after saving.
    public partial class TarkovServerDataWindow : EditorWindow
    {
        private TarkovMapDefinition _definition;
        private JObject _base;
        private string _name = "", _description = "";
        private List<KeyValuePair<string, string>> _keys = new List<KeyValuePair<string, string>>();
        private int _iconX, _iconY;
        private string _locationId = "";
        private bool _dirty;
        private string _loadedFrom;
        private Vector2 _scroll;
        private int _tab;
        private static readonly string[] Tabs = { "General", "Map Select", "Raid", "Access", "Bots", "Spawns", "Loot", "Airdrops", "JSON" };
        private const int JsonTab = 8;

        [MenuItem("Tarkov SDK/Map Settings/Data/Server Data Editor", priority = 128)]
        public static void Open() => Open(Selection.activeObject as TarkovMapDefinition);

        public static void Open(TarkovMapDefinition definition)
        {
            var window = GetWindow<TarkovServerDataWindow>("Server Data");
            window.minSize = new Vector2(420f, 360f);
            if (definition != null && definition != window._definition)
            {
                window._definition = definition;
                window.Load();
            }
        }

        private string MapDir => _definition == null ? null
            : TarkovMapBuilder.ServerMapDirectory(_definition);
        private string BasePath => Path.Combine(MapDir, "base.json");
        private string LocalePath => Path.Combine(MapDir, "locale.json");

        private void OnGUI()
        {
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                var definition = (TarkovMapDefinition)EditorGUILayout.ObjectField("Map Definition", _definition, typeof(TarkovMapDefinition), false);
                if (check.changed && ConfirmDiscard())
                {
                    _definition = definition;
                    Load();
                }
            }
            if (_definition == null)
            {
                EditorGUILayout.HelpBox("Pick the Map Definition of the map to edit.", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField(MapDir, EditorStyles.miniLabel);
            if (_base == null)
            {
                EditorGUILayout.HelpBox($"No base.json for '{_definition.MapId}' in the server mod folder.", MessageType.Warning);
                if (GUILayout.Button("Create From sdk_example Template"))
                    AfterGui(CreateFromTemplate);
                if (GUILayout.Button("Reload"))
                    AfterGui(Load);
                return;
            }

            int tab = GUILayout.Toolbar(_tab, Tabs);
            if (tab != _tab && (_tab != JsonTab || LeaveJsonTab()))
            {
                _tab = tab;
                _jsonText = null;
                GUI.FocusControl(null);
            }
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUI.BeginChangeCheck();
            switch (_tab)
            {
                case 1: DrawMapSelect(); break;
                case 2: DrawRaid(); break;
                case 3: DrawAccess(); break;
                case 4: DrawBots(); break;
                case 5: DrawSpawns(); break;
                case 6: DrawLoot(); break;
                case 7: DrawAirdropsTab(); break;
                case JsonTab: DrawJson(); break;
                default: DrawGeneral(); break;
            }
            if (EditorGUI.EndChangeCheck())
                _dirty = true;
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!_dirty))
                    if (GUILayout.Button(_dirty ? "Save*" : "Save", GUILayout.Height(24f)))
                        AfterGui(Save);
                if (GUILayout.Button("Reload", GUILayout.Height(24f)))
                    AfterGui(() =>
                    {
                        if (ConfirmDiscard())
                            Load();
                    });
                if (GUILayout.Button("Open Folder", GUILayout.Height(24f)))
                    EditorUtility.RevealInFinder(BasePath);
            }
            EditorGUILayout.HelpBox("Restart the SPT server after saving; it reads these files at startup.", MessageType.None);
        }

        private void DrawGeneral()
        {
            EditorGUILayout.LabelField("Location (base.json)", EditorStyles.boldLabel);
            _locationId = EditorGUILayout.TextField(new GUIContent("Location ID", "base.json \"Id\": the location's id in SPT and the game."), _locationId);
            if (_locationId != _definition.MapId)
            {
                EditorGUILayout.HelpBox($"The Location ID must match the map name, '{_definition.MapId}' (the Map Definition's Map Id and this " +
                                        "map's folder name). The server skips maps whose ID doesn't match.", MessageType.Warning);
                if (GUILayout.Button($"Use '{_definition.MapId}'"))
                    _locationId = _definition.MapId;
            }
            else
            {
                EditorGUILayout.HelpBox("Must match the map name (the Map Definition's Map Id). To rename the map, change the Map Id, " +
                                        "rebuild, then set this to the same value.", MessageType.None);
            }
            DrawMongoId();

            EditorGUILayout.Space();
            DrawRaidTime();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Locale (locale.json)", EditorStyles.boldLabel);
            _name = EditorGUILayout.TextField(new GUIContent("Name", "Shown on the map select screen and in the raid."), _name);
            EditorGUILayout.LabelField("Description");
            _description = EditorGUILayout.TextArea(_description, GUILayout.MinHeight(48f));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(new GUIContent("Keys", "Extra locale entries, e.g. exfil names: the key is the exfil's Settings.Name."), EditorStyles.boldLabel);
            int remove = -1;
            for (int i = 0; i < _keys.Count; i++)
                using (new EditorGUILayout.HorizontalScope())
                {
                    var key = EditorGUILayout.TextField(_keys[i].Key);
                    var value = EditorGUILayout.TextField(_keys[i].Value);
                    _keys[i] = new KeyValuePair<string, string>(key, value);
                    if (GUILayout.Button("-", GUILayout.Width(22f)))
                        remove = i;
                }
            if (remove >= 0)
                _keys.RemoveAt(remove);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Key"))
                    _keys.Add(new KeyValuePair<string, string>("", ""));
                if (GUILayout.Button(new GUIContent("Add Missing Locale Keys", "Adds the name keys of every exfil and transit in the open scenes that have none yet.")))
                    AddExfilKeys();
            }
        }

        // dialogs and file writes from inside a layout group break IMGUI's layout ("EndLayoutGroup:
        // BeginLayoutGroup must be called first"), so buttons run them after this GUI pass
        private void AfterGui(Action action)
        {
            EditorApplication.delayCall += () =>
            {
                if (this == null)
                    return; // the window was closed in the meantime
                action();
                Repaint();
            };
        }

        private bool ConfirmDiscard() =>
            !_dirty || EditorUtility.DisplayDialog("Server Data", "Discard unsaved changes?", "Discard", "Cancel");

        private void Load()
        {
            _dirty = false;
            _base = null;
            _jsonText = _jsonError = null;
            _jsonEdited = false;
            _name = _description = "";
            _keys = new List<KeyValuePair<string, string>>();
            if (_definition == null)
                return;
            try
            {
                LoadUsedIds();
                if (File.Exists(BasePath))
                {
                    _base = JObject.Parse(File.ReadAllText(BasePath));
                    PullFieldsFromBase();
                }
                LoadMenu();
                if (File.Exists(LocalePath))
                {
                    var locale = JObject.Parse(File.ReadAllText(LocalePath));
                    _name = locale.Value<string>("Name") ?? "";
                    _description = locale.Value<string>("Description") ?? "";
                    if (locale["Keys"] is JObject keys)
                        _keys = keys.Properties().Select(p => new KeyValuePair<string, string>(p.Name, (string)p.Value ?? "")).ToList();
                }
                else if (_base != null)
                {
                    _name = _base.Value<string>("Name") ?? _definition.MapId;
                    _description = _base.Value<string>("Description") ?? "";
                }
                _loadedFrom = MapDir;
            }
            catch (Exception e)
            {
                _base = null;
                Debug.LogError($"[Tarkov SDK] Could not read the server data in {MapDir}: {e.Message}");
            }
        }

        // General tab values kept outside _base until Save
        private void PullFieldsFromBase()
        {
            _locationId = _base.Value<string>("Id") ?? "";
            _mongoId = _base.Value<string>("_Id") ?? "";
            _iconX = _base.Value<int?>("IconX") ?? 0;
            _iconY = _base.Value<int?>("IconY") ?? 0;
            _sameRaidTime = RaidTimesMatch();
            _sameKeysPvE = _base["AccessKeysPvE"] == null || JToken.DeepEquals(_base["AccessKeys"], _base["AccessKeysPvE"]);
        }

        private void PushFieldsToBase()
        {
            _base["Id"] = _locationId;
            _base["_Id"] = _mongoId;
            _base["IconX"] = _iconX;
            _base["IconY"] = _iconY;
        }

        private void Save()
        {
            if (!ApplyJson())
            {
                EditorUtility.DisplayDialog("Server Data", $"The JSON tab has an error, so nothing was saved:\n\n{_jsonError}", "OK");
                return;
            }
            try
            {
                var keys = new JObject();
                foreach (var pair in _keys)
                    if (!string.IsNullOrWhiteSpace(pair.Key))
                        keys[pair.Key.Trim()] = pair.Value ?? "";
                var locale = new JObject
                {
                    ["Name"] = _name,
                    ["Description"] = _description,
                    ["Keys"] = keys,
                };
                File.WriteAllText(LocalePath, locale.ToString(Formatting.Indented));

                _locationId = (_locationId ?? "").Trim();
                if (_locationId != _definition.MapId &&
                    !EditorUtility.DisplayDialog("Server Data", $"Location ID '{_locationId}' doesn't match the map name '{_definition.MapId}', " +
                                                 "so the server will skip this map. Save anyway?", "Save", "Cancel"))
                    return;
                _mongoId = (_mongoId ?? "").Trim().ToLowerInvariant();
                if (!TarkovMongoId.IsValid(_mongoId) &&
                    !EditorUtility.DisplayDialog("Server Data", $"_Id '{_mongoId}' isn't a 24-character hex MongoID, so the server will fail to load this map. " +
                                                 "Save anyway?", "Save", "Cancel"))
                    return;
                if (_usedIds.TryGetValue(_mongoId, out var owner) &&
                    !EditorUtility.DisplayDialog("Server Data", $"'{owner}' already uses _Id '{_mongoId}', so the server will refuse to load this map. " +
                                                 "Save anyway?", "Save", "Cancel"))
                    return;
                PushFieldsToBase();
                if (_base["waves"] is JArray waves)
                    for (int i = 0; i < waves.Count; i++)
                        waves[i]["number"] = i;
                File.WriteAllText(BasePath, _base.ToString(Formatting.Indented));
                SaveMenu();

                _dirty = false;
                _jsonText = null;
                Debug.Log($"[Tarkov SDK] Saved server data for '{_definition.MapId}' to {_loadedFrom}. Restart the SPT server to apply it.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Tarkov SDK] Could not save the server data: {e.Message}");
            }
        }

        private void AddExfilKeys()
        {
            var existing = new HashSet<string>(_keys.Select(k => k.Key));
            int added = 0;
            foreach (var exfil in FindObjectsOfType<ExfiltrationPoint>(true))
            {
                var name = exfil.Settings?.Name;
                if (string.IsNullOrEmpty(name) || !existing.Add(name))
                    continue;
                _keys.Add(new KeyValuePair<string, string>(name, ObjectNames.NicifyVariableName(name.Replace('_', ' '))));
                added++;
            }
            // transits show their name, description and conditions keys in the transit UI
            foreach (var transit in FindObjectsOfType<TransitPoint>(true))
            {
                var p = transit.parameters;
                if (p == null)
                    continue;
                string destination = string.IsNullOrEmpty(p.location) ? "another map" : p.location;
                foreach (var (key, text) in new[]
                         {
                             (p.name, $"Transit to {destination}"),
                             (p.description, $"Transit to {destination}"),
                             (p.conditions, ""),
                         })
                {
                    if (string.IsNullOrEmpty(key) || !existing.Add(key))
                        continue;
                    _keys.Add(new KeyValuePair<string, string>(key, text));
                    added++;
                }
            }
            if (added > 0)
                _dirty = true;
            ShowNotification(new GUIContent(added == 0 ? "Every exfil and transit already has its keys" : $"Added {added} key(s)"));
        }

        // copies sdk_example's base.json next to the other maps, with this map's Id and a new unique _Id
        private void CreateFromTemplate()
        {
            var template = Path.Combine(Path.GetDirectoryName(MapDir), "sdk_example", "base.json");
            if (!File.Exists(template))
            {
                EditorUtility.DisplayDialog("Server Data", $"No template at {template}. Deploy the server mod first.", "OK");
                return;
            }
            var data = JObject.Parse(File.ReadAllText(template));
            data["Id"] = _definition.MapId;
            data["_Id"] = NewMongoId();
            data["Name"] = _definition.MapId;
            Directory.CreateDirectory(MapDir);
            File.WriteAllText(BasePath, data.ToString(Formatting.Indented));
            Debug.Log($"[Tarkov SDK] Created {BasePath} from the sdk_example template.");
            Load();
            _name = ObjectNames.NicifyVariableName(_definition.MapId.Replace('_', ' '));
            _dirty = true;
        }
    }
}
