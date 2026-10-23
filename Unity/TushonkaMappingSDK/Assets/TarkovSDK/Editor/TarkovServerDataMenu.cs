using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // The Map Select tab of the Server Data Editor: how the map shows on the map select and loading
    // screens. Everything but the button position (base.json IconX/IconY) lives in the map's menu.json,
    // read by the server mod and the client plugin:
    //
    //   Pack           the map pack page the map sits on ("" = Tarkov's page)
    //   TimeSelection  players pick the raid's time of day, as on retail maps other than Labs
    //   Icon           replaces the white dot on the map's button
    //   Preview        the picture in the location panel, also the first loading screen banner
    //   Banners        more loading screen pictures, each with a title and description
    //   Connections    location ids the map has a dotted line to, like retail's routes between maps
    //
    // Pictures are copied into the map's images/ folder on Save.
    public partial class TarkovServerDataWindow
    {
        private class Picture
        {
            public string File;
            public Texture2D Picked;
        }

        private class BannerEntry
        {
            public Picture Picture = new Picture();
            public string Name = "", Description = "";
        }

        private JObject _menu;
        private string _pack = "";
        private bool _timeSelection;
        private Picture _icon = new Picture(), _preview = new Picture();
        private List<BannerEntry> _banners = new List<BannerEntry>();
        private List<string> _connections = new List<string>();
        private List<(string Id, string Name)> _packs = new List<(string, string)>();

        private string MenuPath => Path.Combine(MapDir, "menu.json");
        private string ImagesDir => Path.Combine(MapDir, "images");

        private void LoadMenu()
        {
            _menu = new JObject();
            _pack = "";
            _timeSelection = false;
            _icon = new Picture();
            _preview = new Picture();
            _banners = new List<BannerEntry>();
            _connections = new List<string>();
            _packs = TarkovMapPack.Available(_definition.SptRoot);
            if (!File.Exists(MenuPath))
                return;
            try
            {
                _menu = JObject.Parse(File.ReadAllText(MenuPath));
            }
            catch (Exception e)
            {
                // the rest of the editor still works; saving writes a fresh menu.json
                Debug.LogError($"[Tarkov SDK] Could not read {MenuPath}: {e.Message}");
                return;
            }
            _pack = _menu.Value<string>("Pack") ?? "";
            _timeSelection = _menu.Value<bool?>("TimeSelection") ?? false;
            _icon.File = _menu.Value<string>("Icon");
            _preview.File = _menu.Value<string>("Preview");
            if (_menu["Banners"] is JArray banners)
                foreach (var banner in banners.OfType<JObject>())
                    _banners.Add(new BannerEntry
                    {
                        Picture = { File = banner.Value<string>("Image") },
                        Name = banner.Value<string>("Name") ?? "",
                        Description = banner.Value<string>("Description") ?? "",
                    });
            if (_menu["Connections"] is JArray connections)
                _connections = connections.Select(c => (string)c).Where(c => !string.IsNullOrEmpty(c)).ToList();
        }

        private void SaveMenu()
        {
            string Store(Picture picture, string slot)
            {
                if (picture.Picked != null)
                {
                    if (TarkovMenuPictures.IsSupported(picture.Picked, out var error))
                    {
                        picture.File = TarkovMenuPictures.Copy(picture.Picked, ImagesDir, slot);
                        picture.Picked = null;
                    }
                    else
                    {
                        Debug.LogError($"[Tarkov SDK] Picture not saved: {error}");
                    }
                }
                return picture.File ?? "";
            }

            _menu["Pack"] = _pack ?? "";
            _menu["TimeSelection"] = _timeSelection;
            _menu["Icon"] = Store(_icon, "icon");
            _menu["Preview"] = Store(_preview, "preview");
            _menu["Banners"] = new JArray(_banners.Select(b => new JObject
            {
                ["Image"] = Store(b.Picture, "banner"),
                ["Name"] = b.Name ?? "",
                ["Description"] = b.Description ?? "",
            }));
            _menu["Connections"] = new JArray(_connections);
            File.WriteAllText(MenuPath, _menu.ToString(Formatting.Indented));
            TarkovMenuPictures.DeleteUnused(ImagesDir,
                new[] { _icon.File, _preview.File }.Concat(_banners.Select(b => b.Picture.File)),
                new[] { "icon", "preview", "banner" });
        }

        private Texture2D Current(Picture picture) =>
            string.IsNullOrEmpty(picture.File) ? null : TarkovMenuPictures.Load(Path.Combine(ImagesDir, picture.File));

        private void PictureField(GUIContent label, Picture picture, string hint)
        {
            var picked = TarkovMenuPictures.Field(label, picture.Picked, Current(picture), hint, out bool clear);
            if (clear)
            {
                picture.File = null;
                picture.Picked = null;
                GUI.changed = true;
            }
            else if (picked != picture.Picked)
            {
                picture.Picked = picked;
            }
        }

        private void DrawMapSelect()
        {
            EditorGUILayout.LabelField("Page", EditorStyles.boldLabel);
            var ids = new List<string> { "" };
            var names = new List<string> { "Tarkov (no pack)" };
            foreach (var (id, name) in _packs)
            {
                ids.Add(id);
                names.Add($"{name} ({id})");
            }
            if (!string.IsNullOrEmpty(_pack) && !ids.Contains(_pack))
            {
                ids.Add(_pack);
                names.Add($"{_pack} (not found)");
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                int index = EditorGUILayout.Popup(new GUIContent("Map Pack", "The map select screen page this map is on. Players turn pages with " +
                                                                             "the PREVIOUS / NEXT buttons in the screen's bottom row."), ids.IndexOf(_pack ?? ""), names.ToArray());
                _pack = ids[Mathf.Max(0, index)];
                if (GUILayout.Button(new GUIContent("Refresh", "Find packs made or deployed since this window opened."), GUILayout.Width(60f)))
                {
                    _packs = TarkovMapPack.Available(_definition.SptRoot);
                    LoadUsedIds();
                }
            }
            if (!string.IsNullOrEmpty(_pack) && TarkovMapPack.Find(_pack) == null && !Directory.Exists(TarkovMapPack.PackDirectory(_definition.SptRoot, _pack)))
                EditorGUILayout.HelpBox($"Map pack '{_pack}' is not in this project or the SPT install; the map shows on Tarkov's page until it is.", MessageType.Warning);
            if (_packs.Count == 0)
                EditorGUILayout.HelpBox("Make a map pack with Tarkov SDK > Map > New Map Pack to give this map a page with its own background.", MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Button position (base.json)", EditorStyles.boldLabel);
            _iconX = EditorGUILayout.IntField(new GUIContent("Icon X", "IconX: thousandths of the page's picture width, from its left edge."), _iconX);
            _iconY = EditorGUILayout.IntField(new GUIContent("Icon Y", "IconY: thousandths of the picture's height, from its bottom edge."), _iconY);
            DrawIconPreview();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Time of day", EditorStyles.boldLabel);
            _timeSelection = EditorGUILayout.Toggle(new GUIContent("Time Selection", "Players pick the raid's time: now, or 12 hours later, " +
                                                                                     "like on every retail map but Labs. Off: the raid starts at the current time."), _timeSelection);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Pictures", EditorStyles.boldLabel);
            PictureField(new GUIContent("Location Icon", "Replaces the white dot on this map's button."), _icon,
                         "PNG with transparency, drawn 49x49. Empty: the pack's icon, else the game's dot.");
            EditorGUILayout.Space(4f);
            PictureField(new GUIContent("Preview", "The picture in the location panel when the map is selected, and the first loading screen banner."), _preview,
                         "764x460 like the game's. Empty: the game's Factory placeholder.");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(new GUIContent("Loading Screen Banners", "Shown in turn while the raid loads, after the Preview, with a title and description."),
                                       EditorStyles.boldLabel);
            int remove = -1, up = -1;
            for (int i = 0; i < _banners.Count; i++)
            {
                var banner = _banners[i];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    PictureField(new GUIContent($"Banner {i + 1}"), banner.Picture, "764x460 like the game's.");
                    banner.Name = EditorGUILayout.TextField(new GUIContent("Title", "Empty: the map's name."), banner.Name);
                    banner.Description = EditorGUILayout.TextField("Description", banner.Description);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        using (new EditorGUI.DisabledScope(i == 0))
                            if (GUILayout.Button("Move Up", GUILayout.Width(70f)))
                                up = i;
                        if (GUILayout.Button("Remove", GUILayout.Width(70f)))
                            remove = i;
                    }
                }
            }
            if (up > 0)
            {
                (_banners[up - 1], _banners[up]) = (_banners[up], _banners[up - 1]);
                GUI.changed = true;
            }
            if (remove >= 0)
            {
                _banners.RemoveAt(remove);
                GUI.changed = true;
            }
            if (GUILayout.Button("Add Banner"))
            {
                _banners.Add(new BannerEntry());
                GUI.changed = true;
            }

            EditorGUILayout.Space();
            DrawConnections();
        }

        private void DrawConnections()
        {
            EditorGUILayout.LabelField(new GUIContent("Connections", "Dotted lines from this map to other locations on the map select screen, " +
                                                                    "like the game's routes between maps. Only lines to locations on the same page show."),
                                       EditorStyles.boldLabel);
            if (_connections.Count == 0)
                EditorGUILayout.LabelField("None.", EditorStyles.miniLabel);
            int remove = -1;
            for (int i = 0; i < _connections.Count; i++)
                using (new EditorGUILayout.HorizontalScope())
                {
                    var other = _others.FirstOrDefault(o => string.Equals(o.Id, _connections[i], StringComparison.OrdinalIgnoreCase));
                    EditorGUILayout.LabelField(other.Id == null ? $"{_connections[i]} (not found)" : $"{other.Name} ({other.Id})");
                    if (other.Id != null && other.Pack != (_pack ?? ""))
                        EditorGUILayout.LabelField("other page", EditorStyles.miniLabel, GUILayout.Width(64f));
                    if (GUILayout.Button("-", GUILayout.Width(22f)))
                        remove = i;
                }
            if (remove >= 0)
            {
                _connections.RemoveAt(remove);
                GUI.changed = true;
            }
            var choices = _others.Where(o => (o.Enabled || o.Custom) && !_connections.Contains(o.Id, StringComparer.OrdinalIgnoreCase)).OrderBy(o => o.Pack != (_pack ?? "")).ThenBy(o => o.Name).ToList();
            int add = EditorGUILayout.Popup("Add Connection", -1, choices.Select(o => $"{o.Name} ({o.Id})" + (o.Pack != (_pack ?? "") ? " - other page" : "")).ToArray());
            if (add >= 0)
            {
                _connections.Add(choices[add].Id);
                GUI.changed = true;
            }
        }

        // ------------------------------------------------------------ button position preview

        // the map select screen's picture in miniature: the game puts each location's button at
        // (IconX, IconY) thousandths of the page's picture, from its bottom-left corner
        // (LocationButton anchors at Location.RelativeMapPos). Click to move this map's button there.
        private void DrawIconPreview()
        {
            var background = string.IsNullOrEmpty(_pack) ? null : TarkovMapPack.BackgroundOf(_definition.SptRoot, _pack);
            float width = 260f, height = 260f;
            if (background != null && background.width > 0)
            {
                height = width * background.height / background.width;
                if (height > 400f)
                {
                    width *= 400f / height;
                    height = 400f;
                }
            }
            Rect rect;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                rect = GUILayoutUtility.GetRect(width, height, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
            }
            if (background != null)
            {
                GUI.DrawTexture(rect, background, ScaleMode.StretchToFill);
            }
            else
            {
                EditorGUI.DrawRect(rect, new Color(0.11f, 0.12f, 0.11f));
                var grid = new Color(1f, 1f, 1f, 0.06f);
                for (int i = 1; i < 4; i++)
                {
                    EditorGUI.DrawRect(new Rect(rect.x + rect.width * i / 4f, rect.y, 1f, rect.height), grid);
                    EditorGUI.DrawRect(new Rect(rect.x, rect.y + rect.height * i / 4f, rect.width, 1f), grid);
                }
            }

            Vector2 ToScreen(Vector2 icon) => new Vector2(rect.x + icon.x / 1000f * rect.width, rect.yMax - icon.y / 1000f * rect.height);
            var me = ToScreen(new Vector2(_iconX, _iconY));
            var page = _others.Where(o => o.Enabled && o.Pack == (_pack ?? "") && o.Icon != Vector2.zero).ToList();

            if (Event.current.type == EventType.Repaint)
            {
                Handles.BeginGUI();
                Handles.color = new Color(0.95f, 0.8f, 0.35f, 0.8f);
                foreach (var target in page.Where(o => _connections.Contains(o.Id, StringComparer.OrdinalIgnoreCase)))
                    Handles.DrawAAPolyLine(2f, me, ToScreen(target.Icon));
                Handles.EndGUI();
            }

            var label = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(0.85f, 0.85f, 0.82f) } };
            foreach (var other in page)
            {
                var p = ToScreen(other.Icon);
                EditorGUI.DrawRect(new Rect(p.x - 3f, p.y - 3f, 6f, 6f), other.Custom ? new Color(0.45f, 0.7f, 0.95f) : new Color(0.6f, 0.6f, 0.58f));
                GUI.Label(new Rect(p.x + 5f, p.y - 8f, 110f, 16f), other.Name, label);
            }

            // this map's button: a small plate like the game's location buttons
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
                EditorGUILayout.HelpBox("Off the picture: the button won't be visible. Keep both between 0 and 1000.", MessageType.Warning);
            EditorGUILayout.LabelField(string.IsNullOrEmpty(_pack)
                                           ? "Tarkov's page. Grey: retail maps. Blue: your other custom maps. Click to move this map's button."
                                           : "The pack's background. Blue: the pack's other maps. Click to move this map's button.",
                                       EditorStyles.centeredGreyMiniLabel);
        }
    }
}
