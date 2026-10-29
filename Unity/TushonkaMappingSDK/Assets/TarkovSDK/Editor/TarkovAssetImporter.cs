using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // Tarkov SDK > Tools > Asset Importer...: the retail maps' objects, read straight from the game install
    // (TarkovGameFiles), one tab per map and one per kind of object, and imported into the project as prefabs with
    // their meshes, materials, textures and game components (TarkovGameImport). A map is scanned once (in the
    // background) and remembered in Library/TarkovSDK/AssetImporter; thumbnails are made only for what's on screen and
    // kept there too, so a map's thousands of props stay quick to browse. Prefabs go where the Prefab Library keeps
    // the map's (Prefabs/<Category>/ in the map's folder), their assets under Assets/TarkovGameAssets. The Materials
    // and Sounds kinds are the map's materials and sounds on their own, imported as assets (a .mat on the SDK's shader
    // stand-ins; a .wav decoded by TarkovGameSounds); Data is the map's lighting (applied to the open scenes) and its
    // camera's PRISM post-processing (a PrismPreset asset), see TarkovGamePresets.
    public class TarkovAssetImporter : EditorWindow
    {
        private const string CacheFolder = "Library/TarkovSDK/AssetImporter";
        private const int CacheVersion = 4;
        private const string DragKey = "TarkovSDK.GameAsset";
        private static readonly int[] PageSizes = { 24, 48, 96, 192 };
        private static readonly string[] Sorts = { "Name", "Most Used", "Largest", "Smallest" };

        [Serializable]
        private class IndexCache
        {
            public int Version;
            public string Folder;
            public long Stamp;
            public List<GameIndexEntry> Entries = new List<GameIndexEntry>();
        }

        private List<GameMap> _maps;
        private int _map;
        private readonly Dictionary<string, List<GameIndexEntry>> _indexes = new Dictionary<string, List<GameIndexEntry>>();
        private int _category;
        private string _search = "";
        private int _sort;
        private int _pageSize = 1;
        private int _page;
        private float _size = 110f;
        private Vector2 _scroll;
        private readonly List<GameIndexEntry> _selected = new List<GameIndexEntry>();
        private string _status;
        private bool _place;

        // the filtered, sorted list, remade only when what it depends on changes
        private List<GameIndexEntry> _shown = new List<GameIndexEntry>();
        private string _shownKey;
        // kept between redraws: the kind counts of the shown index, and where prefabs go (a search of the project)
        private List<GameIndexEntry> _countsOf;
        private Dictionary<string, int> _counts;
        private string _prefabsFolder;

        // background scan
        private Task _scan;
        private CancellationTokenSource _cancel;
        private volatile string _scanStatus;
        private volatile float _scanProgress;
        private string _scanMap;
        private List<GameIndexEntry> _scanResult;
        private string _scanMapKey;
        private string _scanError;

        [MenuItem("Tarkov SDK/Tools/Asset Importer...", priority = 136)]
        public static void Open() => GetWindow<TarkovAssetImporter>("Asset Importer").minSize = new Vector2(520f, 480f);

        private void OnFocus() => _prefabsFolder = null;

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGui;
            UnityEditor.SceneManagement.EditorSceneManager.activeSceneChangedInEditMode += OnActiveScene;
            EditorApplication.update += TarkovGameThumbnails.Process;
            _maps = null;
        }

        private void OnActiveScene(Scene a, Scene b) => _prefabsFolder = null;

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGui;
            UnityEditor.SceneManagement.EditorSceneManager.activeSceneChangedInEditMode -= OnActiveScene;
            EditorApplication.update -= TarkovGameThumbnails.Process;
            _cancel?.Cancel();
            TarkovGameSounds.Release();
            TarkovGameThumbnails.Release();
            TarkovGameImportJob.Release();
        }

        private List<GameMap> Maps
        {
            get
            {
                if (_maps == null)
                {
                    _maps = TarkovGameData.Available ? GameIndex.Maps(TarkovGameData.Scenes) : new List<GameMap>();
                    // the game's effects (explosions, impacts), from its effects bundle
                    if (File.Exists(Path.Combine(TarkovGameData.Folder, "StreamingAssets/Windows", GameIndex.EffectsBundle)))
                        _maps.Add(new GameMap { Key = GameIndex.EffectsKey, Name = "Effects" });
                }
                return _maps;
            }
        }

        private static string CachePath(GameMap map) => $"{CacheFolder}/{map.Key}.json";

        private static long Stamp() => File.GetLastWriteTimeUtc(Path.Combine(TarkovGameData.Folder, "globalgamemanagers")).Ticks;

        private List<GameIndexEntry> IndexOf(GameMap map)
        {
            if (_indexes.TryGetValue(map.Key, out var entries))
                return entries;
            var path = CachePath(map);
            if (!File.Exists(path))
                return null;
            try
            {
                var cache = JsonUtility.FromJson<IndexCache>(File.ReadAllText(path));
                if (cache == null || cache.Version != CacheVersion || cache.Folder != TarkovGameData.Folder || cache.Stamp != Stamp())
                    return null;
                return _indexes[map.Key] = cache.Entries;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------ window

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label(new GUIContent("Game: " + TarkovGameData.Folder, "The game install's EscapeFromTarkov_Data, from the Map Definition's SPT root unless picked here."),
                    EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Change...", EditorStyles.toolbarButton))
                    EditorApplication.delayCall += PickFolder;
                TarkovGameModelPreview.Enabled = GUILayout.Toggle(TarkovGameModelPreview.Enabled,
                    new GUIContent("Scene Models", "Draws the game's models for mounted guns, loot spawns, airdrop points and BTR stops in the Scene view."), EditorStyles.toolbarButton);
            }
            if (!TarkovGameData.Available)
            {
                EditorGUILayout.HelpBox($"No game install at {TarkovGameData.Folder}. Set Spt Root on your Map Definition, or press Change... and pick the game's EscapeFromTarkov_Data folder.",
                    MessageType.Warning);
                return;
            }
            var maps = Maps;
            if (maps.Count == 0)
            {
                EditorGUILayout.HelpBox("The game's scene list didn't read.", MessageType.Warning);
                return;
            }
            EditorGUILayout.LabelField("Objects from the retail maps: pick a map, a kind and objects, then Import (or drag one into the Scene view). They come in as " +
                                       "prefabs with the game's meshes, materials, textures and components, into the map's Prefabs folder (the Prefab Library shows them). " +
                                       "Materials and Sounds come in as assets: drop a material on an object to put it on, a sound to place a sound source.",
                EditorStyles.wordWrappedMiniLabel);
            int columns = Mathf.Max(1, (int)(position.width / 92f));
            int map = GUILayout.SelectionGrid(Mathf.Clamp(_map, 0, maps.Count - 1), maps.Select(m => m.Name).ToArray(), Mathf.Min(columns, maps.Count), EditorStyles.toolbarButton);
            if (map != _map)
            {
                _map = map;
                _category = 0;
                _page = 0;
                _selected.Clear();
                _shownKey = null;
            }
            var current = maps[_map];
            if (_scan != null)
            {
                DrawScanning();
                return;
            }
            var index = IndexOf(current);
            if (index == null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(current.Key == GameIndex.EffectsKey
                    ? "The game's own effects: explosions (Gas_explosion, Grenade_new, big_explosion ...), bullet impacts and flashes, from its effects bundle. Each game effect " +
                      "comes whole (every particle system it plays), and so do the pieces it's made of. Reading takes a second."
                    : $"{current.Name} hasn't been read yet ({current.Scenes.Count} scenes). Reading it takes from a few seconds to a couple of minutes " +
                      "(Streets is the largest) and is remembered.", MessageType.Info);
                if (!string.IsNullOrEmpty(_scanError))
                    EditorGUILayout.HelpBox(_scanError, MessageType.Error);
                if (GUILayout.Button($"Read {current.Name}", GUILayout.Height(28f)))
                    StartScan(current);
                return;
            }
            DrawBrowser(current, index);
        }

        private void PickFolder()
        {
            var picked = EditorUtility.OpenFolderPanel("The game's EscapeFromTarkov_Data folder", TarkovGameData.Folder, "");
            if (string.IsNullOrEmpty(picked))
                return;
            if (!File.Exists(Path.Combine(picked, "globalgamemanagers")) && File.Exists(Path.Combine(picked, "EscapeFromTarkov_Data", "globalgamemanagers")))
                picked = Path.Combine(picked, "EscapeFromTarkov_Data");
            TarkovGameData.Folder = picked;
            _maps = null;
            _indexes.Clear();
            _shownKey = null;
            TarkovGameThumbnails.Release();
            Repaint();
        }

        private void DrawScanning()
        {
            EditorGUILayout.Space();
            var rect = GUILayoutUtility.GetRect(100f, 20f, GUILayout.ExpandWidth(true));
            EditorGUI.ProgressBar(rect, _scanProgress, $"Reading {_scanMap}: {_scanStatus}");
            if (GUILayout.Button("Cancel"))
                _cancel?.Cancel();
            if (_scan.IsCompleted)
                FinishScan();
            Repaint();
        }

        private void StartScan(GameMap map)
        {
            _scanError = null;
            _scanMap = map.Name;
            _scanStatus = "starting";
            _scanProgress = 0f;
            _cancel = new CancellationTokenSource();
            var token = _cancel.Token;
            var kinds = TarkovGameData.KindsByScript();
            var files = TarkovGameData.Files;
            var scenes = TarkovGameData.Scenes;
            GameTypeTrees.Get(1);
            var result = new List<GameIndexEntry>();
            _scanResult = result;
            _scan = Task.Run(() =>
            {
                if (map.Key == GameIndex.EffectsKey)
                {
                    _scanStatus = "effects bundle";
                    try
                    {
                        result.AddRange(GameIndex.ScanEffects(files));
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[Tarkov SDK] Asset Importer: the effects bundle didn't read ({e.Message})");
                    }
                    return;
                }
                for (int i = 0; i < map.Scenes.Count && !token.IsCancellationRequested; i++)
                {
                    _scanStatus = GameIndex.SceneName(scenes, map.Scenes[i]);
                    _scanProgress = i / (float)map.Scenes.Count;
                    try
                    {
                        result.AddRange(GameIndex.Scan(files, map.Scenes[i], c => kinds.TryGetValue(c, out var k) ? k : null, () => token.IsCancellationRequested));
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[Tarkov SDK] Asset Importer: {_scanStatus} not read ({e.Message})");
                    }
                }
            }, token);
            _scanMapKey = map.Key;
        }

        private void FinishScan()
        {
            bool cancelled = _cancel.IsCancellationRequested;
            if (_scan.IsFaulted)
                _scanError = "Reading failed: " + _scan.Exception?.GetBaseException().Message;
            else if (!cancelled)
            {
                // the same object in two of the map's scenes is one entry
                var merged = new Dictionary<string, GameIndexEntry>();
                foreach (var e in _scanResult)
                {
                    if (merged.TryGetValue(e.Signature, out var known))
                        known.Count += e.Count;
                    else
                        merged[e.Signature] = e;
                }
                var entries = merged.Values.ToList();
                var map = Maps.First(m => m.Key == _scanMapKey);
                _indexes[map.Key] = entries;
                try
                {
                    Directory.CreateDirectory(CacheFolder);
                    File.WriteAllText(CachePath(map), JsonUtility.ToJson(new IndexCache
                        { Version = CacheVersion, Folder = TarkovGameData.Folder, Stamp = Stamp(), Entries = entries }));
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Tarkov SDK] Asset Importer: the map's list wasn't saved: " + e.Message);
                }
                Debug.Log($"[Tarkov SDK] Asset Importer: {map.Name} has {entries.Count} distinct objects " +
                          $"({string.Join(", ", entries.GroupBy(e => e.Category).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}"))})");
            }
            _scan = null;
            _scanResult = null;
            _shownKey = null;
        }

        private void DrawBrowser(GameMap map, List<GameIndexEntry> index)
        {
            if (_countsOf != index)
            {
                _countsOf = index;
                _counts = index.GroupBy(e => e.Category).ToDictionary(g => g.Key, g => g.Count());
            }
            var counts = _counts;
            var categories = GameIndex.Categories.Where(counts.ContainsKey).ToList();
            var labels = categories.Select(c => $"{c} ({counts[c]})").ToArray();
            if (categories.Count == 0)
            {
                EditorGUILayout.HelpBox("Nothing found in this map.", MessageType.Info);
                return;
            }
            _category = Mathf.Clamp(_category, 0, categories.Count - 1);
            int perRow = Mathf.Max(1, (int)(position.width / 120f));
            int category = GUILayout.SelectionGrid(_category, labels, Mathf.Min(perRow, labels.Length));
            if (category != _category)
            {
                _category = category;
                _page = 0;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                var search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);
                if (search != _search)
                {
                    _search = search;
                    _page = 0;
                }
                _sort = EditorGUILayout.Popup(_sort, Sorts, GUILayout.Width(90f));
                _size = GUILayout.HorizontalSlider(_size, 64f, 192f, GUILayout.Width(80f));
                if (GUILayout.Button(new GUIContent("Read Again", "Reads the map again (after a game update)."), EditorStyles.miniButton, GUILayout.Width(80f)))
                {
                    _indexes.Remove(map.Key);
                    File.Delete(CachePath(map));
                    StartScan(map);
                    return;
                }
            }

            string key = $"{map.Key}|{categories[_category]}|{_search}|{_sort}|{index.Count}";
            if (key != _shownKey)
            {
                _shownKey = key;
                IEnumerable<GameIndexEntry> shown = index.Where(e => e.Category == categories[_category] &&
                                                                    (_search.Length == 0 || e.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0));
                switch (_sort)
                {
                    case 1: shown = shown.OrderByDescending(e => e.Count).ThenBy(e => e.Name); break;
                    case 2: shown = shown.OrderByDescending(Largest); break;
                    case 3: shown = shown.OrderBy(Largest); break;
                    default: shown = shown.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase); break;
                }
                _shown = shown.ToList();
            }

            int pageSize = PageSizes[Mathf.Clamp(_pageSize, 0, PageSizes.Length - 1)];
            int pages = Mathf.Max(1, (_shown.Count + pageSize - 1) / pageSize);
            _page = Mathf.Clamp(_page, 0, pages - 1);
            DrawPager(pages);

            var page = _shown.Skip(_page * pageSize).Take(pageSize).ToList();
            DrawGrid(map, page);
            DrawSelection(map);
        }

        // sounds sort by length
        private static float Largest(GameIndexEntry e) => e.Category == GameIndex.Sounds ? e.Seconds : Mathf.Max(e.SizeX, Mathf.Max(e.SizeY, e.SizeZ));

        private void DrawPager(int pages)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_page == 0))
                {
                    if (GUILayout.Button("<<", EditorStyles.miniButtonLeft, GUILayout.Width(28f)))
                        _page = 0;
                    if (GUILayout.Button("<", EditorStyles.miniButtonMid, GUILayout.Width(28f)))
                        _page--;
                }
                GUILayout.Label($"Page {_page + 1} of {pages} ({_shown.Count} objects)", EditorStyles.centeredGreyMiniLabel, GUILayout.ExpandWidth(true));
                using (new EditorGUI.DisabledScope(_page >= pages - 1))
                {
                    if (GUILayout.Button(">", EditorStyles.miniButtonMid, GUILayout.Width(28f)))
                        _page++;
                    if (GUILayout.Button(">>", EditorStyles.miniButtonRight, GUILayout.Width(28f)))
                        _page = pages - 1;
                }
                int size = EditorGUILayout.Popup(_pageSize, PageSizes.Select(p => p + " per page").ToArray(), GUILayout.Width(100f));
                if (size != _pageSize)
                {
                    _pageSize = size;
                    _page = 0;
                }
            }
        }

        // the page's cells, drawn only where the scroll view shows them
        private void DrawGrid(GameMap map, List<GameIndexEntry> page)
        {
            float cell = _size + 6f, cellHeight = _size + 30f;
            float width = position.width - 20f;
            int columns = Mathf.Max(1, (int)(width / cell));
            int rows = (page.Count + columns - 1) / columns;
            float gridHeight = Mathf.Max(80f, position.height * 0.45f);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(gridHeight));
            var content = GUILayoutUtility.GetRect(width, rows * cellHeight);
            int first = Mathf.Max(0, (int)(_scroll.y / cellHeight)), last = Mathf.Min(rows - 1, (int)((_scroll.y + gridHeight) / cellHeight));
            var wanted = new List<GameIndexEntry>();
            for (int row = first; row <= last; row++)
                for (int col = 0; col < columns; col++)
                {
                    int i = row * columns + col;
                    if (i >= page.Count)
                        break;
                    var rect = new Rect(content.x + col * cell, content.y + row * cellHeight, _size, cellHeight - 4f);
                    Cell(map, page[i], rect, page);
                    if (page[i].Category != GameIndex.Sounds && page[i].Category != GameIndex.Data)
                        wanted.Add(page[i]);
                }
            EditorGUILayout.EndScrollView();
            if (Event.current.type == EventType.Repaint)
                TarkovGameThumbnails.Want(map.Key, wanted, Repaint);
        }

        private void Cell(GameMap map, GameIndexEntry entry, Rect rect, List<GameIndexEntry> page)
        {
            var image = new Rect(rect.x, rect.y, _size, _size);
            bool selected = _selected.Contains(entry);
            if (Event.current.type == EventType.Repaint)
            {
                if (selected)
                    EditorGUI.DrawRect(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), new Color(0.24f, 0.48f, 0.9f, 0.6f));
                if (entry.Category == GameIndex.Sounds || entry.Category == GameIndex.Data)
                    DrawSoundCard(entry, image);
                else
                {
                    var thumb = TarkovGameThumbnails.Get(map.Key, entry);
                    if (thumb != null)
                        GUI.DrawTexture(image, thumb, ScaleMode.ScaleToFit);
                    else
                        GUI.Box(image, TarkovGameThumbnails.Failed(entry) ? "no preview" : "...");
                }
            }
            GUI.Label(new Rect(rect.x, rect.y + _size, _size, 14f), new GUIContent(entry.Name, $"{entry.Name}\n{CellTip(entry)}"), EditorStyles.centeredGreyMiniLabel);
            GUI.Label(new Rect(rect.x, rect.y + _size + 12f, _size, 14f), CellLine(entry), EditorStyles.centeredGreyMiniLabel);
            var e = Event.current;
            if (!rect.Contains(e.mousePosition))
                return;
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                if (e.shift && _selected.Count > 0)
                {
                    int a = page.IndexOf(_selected[_selected.Count - 1]), b = page.IndexOf(entry);
                    if (a >= 0)
                        foreach (var x in page.Skip(Mathf.Min(a, b)).Take(Mathf.Abs(a - b) + 1))
                            if (!_selected.Contains(x))
                                _selected.Add(x);
                }
                else if (e.control || e.command)
                {
                    if (!_selected.Remove(entry))
                        _selected.Add(entry);
                }
                else
                {
                    _selected.Clear();
                    _selected.Add(entry);
                }
                if (e.clickCount == 2)
                {
                    var picked = new List<GameIndexEntry> { entry };
                    // a sound plays; a material or data is imported and shown (lighting applied); an object is imported and placed
                    if (entry.Category == GameIndex.Sounds)
                        EditorApplication.delayCall += () => TogglePlay(entry);
                    else
                        EditorApplication.delayCall += () => Import(map, picked, entry.Category != GameIndex.Materials && entry.Category != GameIndex.Data, null);
                }
                e.Use();
            }
            else if (e.type == EventType.MouseDrag)
            {
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.objectReferences = Array.Empty<Object>();
                DragAndDrop.SetGenericData(DragKey, new KeyValuePair<GameMap, GameIndexEntry>(map, entry));
                DragAndDrop.StartDrag(entry.Name);
                e.Use();
            }
        }

        private static string SizeText(GameIndexEntry e) => $"{e.SizeX:0.0} x {e.SizeY:0.0} x {e.SizeZ:0.0} m";

        private static string Seconds(float s) => s >= 60f ? $"{(int)(s / 60f)}:{(int)(s % 60f):00}" : $"{s:0.0} s";

        private static string CellTip(GameIndexEntry e) =>
            e.Category == GameIndex.Data ? e.Detail :
            e.Category == GameIndex.Materials ? $"{e.Detail}, on {e.Count} renderer(s)" :
            e.Category == GameIndex.Sounds ? $"{Seconds(e.Seconds)}, {e.Detail}" + (e.Count > 0 ? $", played by {e.Count} sound source(s)" : "") :
            $"{SizeText(e)}, {e.Triangles} triangles, {e.Count} in the map";

        private static string CellLine(GameIndexEntry e) =>
            e.Category == GameIndex.Data ? (GameIndex.IsLighting(e) ? "lighting" : "PRISM preset") :
            e.Category == GameIndex.Materials ? $"x{e.Count}" :
            e.Category == GameIndex.Sounds ? Seconds(e.Seconds) + (TarkovGameSounds.IsPlaying(e) ? "  playing" : "") :
            $"x{e.Count}  {Largest(e):0.#} m";

        private static void DrawSoundCard(GameIndexEntry entry, Rect rect)
        {
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);
            var icon = EditorGUIUtility.IconContent(entry.Category == GameIndex.Data ? (GameIndex.IsLighting(entry) ? "Light Icon" : "Camera Icon")
                : TarkovGameSounds.IsPlaying(entry) ? "d_PlayButton On" : "AudioClip Icon");
            float s = Mathf.Min(48f, rect.width * 0.5f);
            if (icon?.image != null)
                GUI.DrawTexture(new Rect(rect.center.x - s * 0.5f, rect.center.y - s * 0.5f - 6f, s, s), icon.image, ScaleMode.ScaleToFit);
            GUI.Label(new Rect(rect.x, rect.yMax - 18f, rect.width, 14f), entry.Category == GameIndex.Data ? (GameIndex.IsLighting(entry) ? entry.Detail?.Split(':')[0] : "post-processing")
                : entry.Detail?.Split(',')[0], EditorStyles.centeredGreyMiniLabel);
        }

        private void TogglePlay(GameIndexEntry entry)
        {
            if (TarkovGameSounds.IsPlaying(entry))
                TarkovGameSounds.Stop();
            else if (!TarkovGameSounds.Play(entry, out var error))
                _status = $"{entry.Name} didn't play: {error}";
            Repaint();
        }

        private void DrawSoundPreview(GameIndexEntry entry, Rect rect)
        {
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);
            DrawSoundCard(entry, new Rect(rect.x, rect.y, rect.width, rect.height - 30f));
            bool playing = TarkovGameSounds.IsPlaying(entry);
            if (GUI.Button(new Rect(rect.x + 8f, rect.yMax - 28f, rect.width - 16f, 22f), playing ? "Stop" : "Play"))
                EditorApplication.delayCall += () => TogglePlay(entry);
            // the cell says "playing" until the clip ends
            if (playing)
                Repaint();
        }

        private void DrawSelection(GameMap map)
        {
            EditorGUILayout.Space(2f);
            using (new EditorGUILayout.HorizontalScope())
            {
                var previewRect = GUILayoutUtility.GetRect(180f, 180f, GUILayout.Width(180f), GUILayout.Height(180f));
                var entry = _selected.Count > 0 ? _selected[_selected.Count - 1] : null;
                if (entry != null && entry.Category == GameIndex.Sounds)
                    DrawSoundPreview(entry, previewRect);
                else if (entry != null && entry.Category == GameIndex.Data)
                    DrawSoundCard(entry, previewRect);
                else if (entry != null)
                    TarkovGameThumbnails.DrawInteractive(map.Key, entry, previewRect);
                else
                    GUI.Box(previewRect, "Pick an object", EditorStyles.helpBox);
                using (new EditorGUILayout.VerticalScope())
                {
                    if (entry != null && entry.Category == GameIndex.Data)
                    {
                        EditorGUILayout.LabelField(entry.Name, EditorStyles.boldLabel);
                        EditorGUILayout.LabelField(GameIndex.IsLighting(entry) ? "The map's " + entry.Detail : entry.Detail + $" (in {entry.Asset})", EditorStyles.wordWrappedMiniLabel);
                        EditorGUILayout.LabelField(GameIndex.IsLighting(entry)
                                ? "Apply puts its lighting values (ambient, fog, sun, sky and time of day curves) on the open scenes' own components; their materials, lights and cubemaps stay. Undo reverts it."
                                : "Import makes a PRISM preset (exposure, tonemap, vignette, bloom, AO, LUT ...) for LevelSettings' Prism Preset Prefab.",
                            EditorStyles.wordWrappedMiniLabel);
                    }
                    else if (entry != null && GameIndex.IsAsset(entry))
                    {
                        EditorGUILayout.LabelField(entry.Name, EditorStyles.boldLabel);
                        EditorGUILayout.LabelField(entry.Category == GameIndex.Materials ? "Shader: " + entry.Detail : $"{Seconds(entry.Seconds)}, {entry.Detail}", EditorStyles.miniLabel);
                        EditorGUILayout.LabelField(entry.Category == GameIndex.Materials ? $"On {entry.Count} renderer(s) in {map.Name}; in {entry.Asset}"
                            : (entry.Count > 0 ? $"Played by {entry.Count} sound source(s) in {map.Name}" : $"In {GameIndex.SceneName(TarkovGameData.Scenes, entry.Scene)}'s sound files") + $"; in {entry.Asset}",
                            EditorStyles.miniLabel);
                        if (_selected.Count > 1)
                            EditorGUILayout.LabelField($"{_selected.Count} picked (Ctrl/Shift-click for more)", EditorStyles.miniLabel);
                    }
                    else if (entry != null)
                    {
                        EditorGUILayout.LabelField(entry.Name, EditorStyles.boldLabel);
                        EditorGUILayout.LabelField($"{entry.Category}, {SizeText(entry)}, {entry.Triangles} triangles (LOD0)", EditorStyles.miniLabel);
                        EditorGUILayout.LabelField(entry.IsGroup ? $"The game's {entry.Name} effect: {entry.Parts.Length} effect object(s), from the effects bundle"
                            : !string.IsNullOrEmpty(entry.Bundle) ? (entry.Count > 0 ? $"Used by {entry.Count} game effect(s); from the effects bundle" : "From the effects bundle")
                            : $"{entry.Count} in {map.Name}; from {GameIndex.SceneName(TarkovGameData.Scenes, entry.Scene)} (level{entry.Scene})", EditorStyles.miniLabel);
                        if (_selected.Count > 1)
                            EditorGUILayout.LabelField($"{_selected.Count} picked (Ctrl/Shift-click for more)", EditorStyles.miniLabel);
                    }
                    else
                        EditorGUILayout.LabelField("Click to pick, Ctrl/Shift-click for more, double-click to import and place, or drag into the Scene view.",
                            EditorStyles.wordWrappedMiniLabel);
                    GUILayout.FlexibleSpace();
                    if (entry != null && GameIndex.IsLighting(entry))
                        EditorGUILayout.LabelField("Onto the open scenes' LevelSettings, time of day and ambient light", EditorStyles.miniLabel);
                    else if (entry != null && entry.Category == GameIndex.Data)
                    {
                        EditorGUILayout.LabelField($"Into {TarkovGameAssets.Root}/<game file>/Presets", EditorStyles.miniLabel);
                        _place = EditorGUILayout.ToggleLeft(new GUIContent("Put it on the scene's LevelSettings too", "LevelSettings' Prism Preset Prefab, which the raid camera takes its post-processing from."), _place);
                    }
                    else if (entry != null && GameIndex.IsAsset(entry))
                    {
                        EditorGUILayout.LabelField($"Into {TarkovGameAssets.Root}/<game file>/{entry.Category}", EditorStyles.miniLabel);
                        _place = EditorGUILayout.ToggleLeft(entry.Category == GameIndex.Materials
                            ? new GUIContent("Put it on the selected objects too", "Every material slot of the selected objects' renderers gets the imported material.")
                            : new GUIContent("Place a sound source too", "A 3D sound source where the Scene view looks, set up like an Ambient Sound Point (looping if the sound is 5 s or longer)."), _place);
                    }
                    else
                    {
                        var folder = _prefabsFolder ?? (_prefabsFolder = TarkovGameImportJob.PrefabsFolder());
                        EditorGUILayout.LabelField("Into " + folder + "/<Category>", EditorStyles.miniLabel);
                        _place = EditorGUILayout.ToggleLeft(new GUIContent("Place in the scene too", "Each imported prefab is also placed where the Scene view looks."), _place);
                    }
                    using (new EditorGUI.DisabledScope(_selected.Count == 0))
                        if (GUILayout.Button(_selected.Count > 1 ? $"Import {_selected.Count} Objects" : entry != null && GameIndex.IsLighting(entry) ? "Apply To The Open Scenes" : "Import", GUILayout.Height(26f)))
                        {
                            var picked = _selected.ToList();
                            EditorApplication.delayCall += () => Import(map, picked, _place, null);
                        }
                    // materials go in a kit's ground slots, objects in its prefab lists
                    bool materials = _selected.Count > 0 && _selected.All(e => e.Category == GameIndex.Materials);
                    using (new EditorGUI.DisabledScope(!materials && (_selected.Count == 0 || _selected.Any(GameIndex.IsAsset))))
                        if (GUILayout.Button(new GUIContent("Add To City Kit", "Imports the picked objects (or materials) and puts them in the City Builder's kit " +
                                                                                 "(Tarkov SDK > Tools > Geometry > City Builder...), in the list you pick.")))
                            KitMenu(_selected.ToList(), materials);
                    if (!string.IsNullOrEmpty(_status))
                        EditorGUILayout.LabelField(_status, EditorStyles.wordWrappedMiniLabel);
                }
            }
        }

        private void KitMenu(List<GameIndexEntry> entries, bool materials)
        {
            var kit = TarkovCityBuilder.CurrentKit;
            var menu = new GenericMenu();
            menu.AddDisabledItem(new GUIContent(kit != null ? "Into " + kit.name : "Into a new City Kit"));
            menu.AddSeparator("");
            foreach (var (field, label) in materials ? TarkovCityBuilder.MaterialSlots : TarkovCityBuilder.PieceSlots)
                menu.AddItem(new GUIContent(label), false, () => EditorApplication.delayCall += () => AddToKit(entries, field));
            menu.ShowAsContext();
        }

        private void AddToKit(List<GameIndexEntry> entries, string field)
        {
            var kit = TarkovCityBuilder.CurrentKit ?? TarkovCityBuilder.CreateKit();
            List<string> notes;
            var made = entries.All(e => e.Category == GameIndex.Materials)
                ? TarkovGameImportJob.ImportAssets(entries, out notes)
                : TarkovGameImportJob.Import(entries, out notes).Cast<Object>().ToList();
            int added = TarkovCityBuilder.AddToKit(kit, field, made.Where(o => o != null));
            _status = $"Added {added} to {kit.name}'s {TarkovCityBuilder.SlotLabel(field).ToLowerInvariant()}" +
                      (notes.Count > 0 ? $" ({notes.Count} notes in the Console)." : ".");
            EditorGUIUtility.PingObject(kit);
            TarkovCityBuilderWindow.RepaintOpen();
            Repaint();
        }

        private void Import(GameMap map, List<GameIndexEntry> entries, bool place, Vector3? at)
        {
            if (entries.Count > 0 && entries.All(GameIndex.IsAsset))
            {
                ImportAssets(entries, place, at, null);
                return;
            }
            // picked across kinds: the materials and sounds come in too, without being placed
            var assets = entries.Where(GameIndex.IsAsset).ToList();
            if (assets.Count > 0)
                TarkovGameImportJob.ImportAssets(assets, out _);
            entries = entries.Where(e => !GameIndex.IsAsset(e)).ToList();
            var prefabs = TarkovGameImportJob.Import(entries, out var notes);
            _status = $"Imported {prefabs.Count(p => p != null)} of {entries.Count}" + (notes.Count > 0 ? $" ({notes.Count} notes in the Console)" : ".");
            if (place)
                foreach (var prefab in prefabs.Where(p => p != null))
                    Place(prefab, at);
            Repaint();
        }

        // materials and sounds: imported as assets; with place, a material goes on target (or the selected objects) and
        // a sound gets a sound source at the point (or where the view looks)
        private void ImportAssets(List<GameIndexEntry> entries, bool place, Vector3? at, GameObject target)
        {
            var targets = target != null ? new[] { target } : Selection.gameObjects;
            // lighting replaces the open scenes' lighting values
            var lighting = entries.FirstOrDefault(GameIndex.IsLighting);
            if (lighting != null && !EditorUtility.DisplayDialog("Asset Importer",
                    $"Put {lighting.Name.Replace("Lighting: ", "")}'s lighting on the open scenes? It replaces their LevelSettings, time of day and ambient light values (Undo reverts it).", "Apply", "Cancel"))
                entries = entries.Where(e => !GameIndex.IsLighting(e)).ToList();
            if (entries.Count == 0)
                return;
            // .wav is uncompressed: about 10 MB a minute (the radio's podcasts run half an hour)
            float megabytes = entries.Where(e => e.Category == GameIndex.Sounds && !File.Exists(TarkovGameSounds.AssetPathOf(e))).Sum(e => e.Seconds) * 176400f / (1 << 20);
            if (megabytes > 100f && !EditorUtility.DisplayDialog("Asset Importer",
                    $"These sounds make about {megabytes:0} MB of .wav files in the project (Unity compresses them again when it builds the map).", "Import", "Cancel"))
                return;
            var assets = TarkovGameImportJob.ImportAssets(entries, out var notes);
            // lighting makes no asset: it counts as done unless it left a note
            int lit = entries.Count(e => GameIndex.IsLighting(e) && !notes.Any(n => n.StartsWith(e.Name + ":")));
            _status = (lit > 0 ? "Lighting applied to the open scenes (the Console says what). " : "") +
                      (entries.Count(e => !GameIndex.IsLighting(e)) > 0 ? $"Imported {assets.Count(a => a != null)} of {entries.Count(e => !GameIndex.IsLighting(e))}" : "") +
                      (notes.Count > 0 ? $" ({notes.Count} notes in the Console)." : ".");
            var clips = assets.OfType<AudioClip>().ToList();
            if (place)
            {
                var material = assets.OfType<Material>().FirstOrDefault();
                if (material != null)
                {
                    int renderers = 0;
                    foreach (var go in targets)
                        foreach (var renderer in go.GetComponentsInChildren<Renderer>())
                        {
                            Undo.RecordObject(renderer, "Put on " + material.name);
                            renderer.sharedMaterials = Enumerable.Repeat(material, Mathf.Max(1, renderer.sharedMaterials.Length)).ToArray();
                            renderers++;
                        }
                    _status += renderers > 0 ? $" Put on {renderers} renderer(s)." : " Nothing was selected to put it on.";
                }
                foreach (var clip in clips)
                    Selection.activeGameObject = TarkovGameSounds.Place(clip, at ?? ViewPoint() ?? Vector3.zero);
                var preset = assets.OfType<Prism.Utils.PrismPreset>().LastOrDefault();
                if (preset != null)
                {
                    int levels = TarkovGamePresets.Assign(preset);
                    _status += levels > 0 ? $" Put on {levels} LevelSettings." : " The open scenes have no LevelSettings to put it on.";
                }
            }
            // otherwise show what came in
            if ((!place || clips.Count == 0) && target == null)
            {
                var shown = assets.Where(a => a != null).ToArray();
                if (shown.Length > 0)
                {
                    Selection.objects = shown;
                    EditorGUIUtility.PingObject(shown[shown.Length - 1]);
                }
            }
            Repaint();
        }

        // where the Scene view looks: the first solid surface, else its pivot
        private static Vector3? ViewPoint()
        {
            var view = SceneView.lastActiveSceneView;
            if (view == null)
                return null;
            if (Physics.Raycast(view.camera.transform.position, view.camera.transform.forward, out var hit, 2000f, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore))
                return hit.point;
            return view.pivot;
        }

        private static void Place(GameObject prefab, Vector3? at)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, SceneManager.GetActiveScene());
            var point = at ?? ViewPoint();
            if (point != null)
                instance.transform.position = point.Value;
            Undo.RegisterCreatedObjectUndo(instance, "Place " + prefab.name);
            Selection.activeGameObject = instance;
        }

        // a cell dropped into the Scene view: imported, then placed under the mouse
        private void OnSceneGui(SceneView view)
        {
            var e = Event.current;
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform)
                return;
            if (!(DragAndDrop.GetGenericData(DragKey) is KeyValuePair<GameMap, GameIndexEntry> data) || data.Value == null)
                return;
            // a material goes on the object under the mouse
            if (data.Value.Category == GameIndex.Materials)
            {
                var under = HandleUtility.PickGameObject(e.mousePosition, false);
                bool onto = under != null && under.GetComponentInChildren<Renderer>() != null;
                DragAndDrop.visualMode = onto ? DragAndDropVisualMode.Link : DragAndDropVisualMode.Rejected;
                if (e.type == EventType.DragPerform && onto)
                {
                    DragAndDrop.AcceptDrag();
                    DragAndDrop.SetGenericData(DragKey, null);
                    var entries = new List<GameIndexEntry> { data.Value };
                    EditorApplication.delayCall += () => ImportAssets(entries, true, null, under);
                }
                e.Use();
                return;
            }
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                DragAndDrop.SetGenericData(DragKey, null);
                var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                Vector3 at = Physics.Raycast(ray, out var hit, 5000f, TarkovRainCover.SolidMask, QueryTriggerInteraction.Ignore) ? hit.point : ray.GetPoint(10f);
                var picked = new List<GameIndexEntry> { data.Value };
                EditorApplication.delayCall += () => Import(data.Key, picked, true, at);
            }
            e.Use();
        }
    }

    // Imports index entries as prefabs (scenes read once per job and kept for the next)
    public static class TarkovGameImportJob
    {
        public static void Release() => GameSceneCache.Clear();

        public static GameSceneTree Tree(int scene) => GameSceneCache.Get(TarkovGameData.Files, scene);

        public static GameSceneTree Tree(GameIndexEntry entry) => GameSceneCache.For(TarkovGameData.Files, entry);

        // where imported prefabs go: the Prefab Library's (the map's folder) or, with no saved scene, the game assets'
        public static string PrefabsFolder()
        {
            var folder = TarkovPrefabLibrary.MapFolder();
            return folder != null ? folder + "/Prefabs" : TarkovGameAssets.Root + "/Prefabs";
        }

        private const string UserDataPrefix = "TarkovGame:";

        // materials and sounds: one asset each (null where it didn't import), made once and then reused
        public static List<Object> ImportAssets(List<GameIndexEntry> entries, out List<string> notes)
        {
            notes = new List<string>();
            var made = new List<Object>();
            var files = TarkovGameData.Files;
            var store = new GameAssetStore(files);
            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (EditorUtility.DisplayCancelableProgressBar("Asset Importer", entry.Name, i / (float)entries.Count))
                        break;
                    Object asset = null;
                    string error = null;
                    try
                    {
                        if (entry.Category == GameIndex.Materials)
                        {
                            var file = files.Get(entry.Asset);
                            asset = file != null ? store.Material(file, new GamePPtr { PathId = entry.AssetId }) : null;
                            error = file == null ? $"{entry.Asset} didn't read" : "the material didn't read";
                        }
                        else if (GameIndex.IsLighting(entry))
                        {
                            error = TarkovGamePresets.ApplyLighting(entry, out bool applied);
                            if (applied)
                            {
                                Debug.Log("[Tarkov SDK] " + error);
                                error = null;
                            }
                            // nothing is made: the scene's components changed
                            made.Add(null);
                            if (error != null)
                                notes.Add($"{entry.Name}: {error}");
                            continue;
                        }
                        else if (entry.Category == GameIndex.Data)
                            asset = TarkovGamePresets.ImportPrism(entry, store, out error);
                        else
                            asset = TarkovGameSounds.Import(entry, out error);
                    }
                    catch (Exception e)
                    {
                        error = e.Message;
                        Debug.LogException(e);
                    }
                    if (asset == null)
                        notes.Add($"{entry.Name}: not imported ({error})");
                    notes.AddRange(store.Notes.Distinct().Select(n => $"{entry.Name}: {n}"));
                    store.Notes.Clear();
                    made.Add(asset);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
            }
            Debug.Log($"[Tarkov SDK] Asset Importer: {made.Count(a => a != null)} asset(s) in {TarkovGameAssets.Root}" +
                      (notes.Count == 0 ? "" : ":\n" + string.Join("\n", notes.Take(40)) + (notes.Count > 40 ? $"\n... and {notes.Count - 40} more" : "")));
            return made;
        }

        public static List<GameObject> Import(List<GameIndexEntry> entries, out List<string> notes)
        {
            notes = new List<string>();
            var prefabs = new List<GameObject>();
            var root = PrefabsFolder();
            // prefabs already imported, by the object they came from
            var existing = new Dictionary<string, string>();
            if (AssetDatabase.IsValidFolder(root))
                foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { root }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var data = AssetImporter.GetAtPath(path)?.userData;
                    if (data != null && data.StartsWith(UserDataPrefix))
                        existing[data.Substring(UserDataPrefix.Length)] = path;
                }
            var files = TarkovGameData.Files;
            var store = new GameAssetStore(files);
            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (EditorUtility.DisplayCancelableProgressBar("Asset Importer", entry.Name, i / (float)entries.Count))
                        break;
                    if (existing.TryGetValue(entry.Signature, out var known))
                    {
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(known);
                        if (prefab != null)
                        {
                            prefabs.Add(prefab);
                            continue;
                        }
                    }
                    var tree = Tree(entry);
                    var nodes = (entry.IsGroup ? entry.Parts : new[] { entry.Transform }).Select(t => tree?.Get(t)).Where(n => n != null).ToList();
                    if (nodes.Count == 0)
                    {
                        notes.Add($"{entry.Name}: not found in {(string.IsNullOrEmpty(entry.Bundle) ? "level" + entry.Scene : entry.Bundle)} (read it again)");
                        prefabs.Add(null);
                        continue;
                    }
                    var dir = $"{root}/{entry.Category}";
                    TarkovGeneratedAssets.EnsureFolder(dir);
                    var target = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{GameIndex.Clean(entry.Name)}.prefab");
                    store.Notes.Clear();
                    GameObject made = null;
                    try
                    {
                        made = entry.IsGroup ? TarkovGameObjects.ImportGroup(files, tree, nodes, entry.Name, target, store)
                            : TarkovGameObjects.ImportPrefab(files, tree, nodes[0], target, store);
                    }
                    catch (Exception e)
                    {
                        notes.Add($"{entry.Name}: not imported ({e.Message})");
                        Debug.LogException(e);
                    }
                    if (made != null)
                    {
                        var importer = AssetImporter.GetAtPath(target);
                        if (importer != null)
                        {
                            importer.userData = UserDataPrefix + entry.Signature;
                            importer.SaveAndReimport();
                        }
                        existing[entry.Signature] = target;
                    }
                    notes.AddRange(store.Notes.Distinct().Select(n => $"{entry.Name}: {n}"));
                    prefabs.Add(made);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
            }
            Debug.Log($"[Tarkov SDK] Asset Importer: {prefabs.Count(p => p != null)} prefab(s) in {root}, {store.Created} new asset(s) in {TarkovGameAssets.Root}" +
                      (notes.Count == 0 ? "" : ":\n" + string.Join("\n", notes.Take(40)) + (notes.Count > 40 ? $"\n... and {notes.Count - 40} more" : "")));
            return prefabs;
        }
    }

    // Reads preview models on a background thread (the scene, the meshes, small texture mips): the main thread only
    // makes the Unity objects from what it read. One thread; the picked object's big preview goes before thumbnails, and
    // thumbnails no longer on screen are dropped from its queue.
    [InitializeOnLoad]
    public static class TarkovGameModelWorker
    {
        public sealed class Job
        {
            public string Key;
            public GameIndexEntry Entry;
            public int TextureSize;
            public GameModelData Result;
        }

        private static readonly object Lock = new object();
        private static List<Job> _thumbs = new List<Job>();
        private static Job _big;
        private static string _current;
        private static readonly Queue<Job> Done = new Queue<Job>();
        private static readonly AutoResetEvent Signal = new AutoResetEvent(false);
        private static Thread _thread;
        private static volatile bool _stop;
        private static GameFiles _files;

        static TarkovGameModelWorker()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
        }

        private static void Start()
        {
            if (_thread != null && _thread.IsAlive)
                return;
            _stop = false;
            _files = TarkovGameData.Files;
            GameTypeTrees.Get(1);
            _thread = new Thread(Loop) { IsBackground = true, Name = "Tarkov SDK game models", Priority = System.Threading.ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public static void Stop()
        {
            _stop = true;
            Signal.Set();
            _thread?.Join(2000);
            _thread = null;
            lock (Lock)
            {
                _thumbs = new List<Job>();
                _big = null;
                Done.Clear();
            }
        }

        // the thumbnails wanted now (what's being read or already read and not yet taken is left out)
        public static void Want(List<Job> thumbs)
        {
            if (thumbs.Count > 0)
                Start();
            lock (Lock)
            {
                var done = new HashSet<string>(Done.Select(j => j.Key));
                _thumbs = thumbs.Where(j => j.Key != _current && !done.Contains(j.Key)).ToList();
            }
            Signal.Set();
        }

        public static void WantBig(Job job)
        {
            Start();
            lock (Lock)
                _big = job;
            Signal.Set();
        }

        public static bool TryTake(out Job job)
        {
            lock (Lock)
            {
                job = Done.Count > 0 ? Done.Dequeue() : null;
                return job != null;
            }
        }

        private static void Loop()
        {
            while (!_stop)
            {
                Job job = null;
                lock (Lock)
                {
                    if (_big != null)
                    {
                        job = _big;
                        _big = null;
                    }
                    else if (_thumbs.Count > 0)
                    {
                        job = _thumbs[0];
                        _thumbs.RemoveAt(0);
                    }
                    _current = job?.Key;
                }
                if (job == null)
                {
                    Signal.WaitOne(250);
                    continue;
                }
                try
                {
                    job.Result = GameModelReader.Model(_files, job.Entry, job.TextureSize);
                }
                catch (Exception e)
                {
                    job.Result = new GameModelData { Error = e.Message };
                }
                lock (Lock)
                {
                    _current = null;
                    if (!_stop)
                        Done.Enqueue(job);
                }
            }
        }
    }

    // The Asset Importer's thumbnails: only for the cells on screen, read by TarkovGameModelWorker, made into Unity
    // objects and rendered in a few milliseconds per editor tick, saved as PNGs in the Library (loaded from there next
    // time) and kept in memory for the last few pages
    public static class TarkovGameThumbnails
    {
        private const string Folder = "Library/TarkovSDK/AssetImporter/Thumbnails";
        private const string BigPrefix = "big:";
        private const int Size = 128;
        private const int MemoryLimit = 400;
        private const double Budget = 0.008;
        private static readonly Dictionary<string, Texture2D> Loaded = new Dictionary<string, Texture2D>();
        private static readonly LinkedList<string> Order = new LinkedList<string>();
        private static readonly HashSet<string> Failures = new HashSet<string>();
        private static readonly Dictionary<string, string> Signatures = new Dictionary<string, string>();
        private static HashSet<string> _onDisk;
        private static readonly List<string> DiskQueue = new List<string>();
        private static Action _repaint;
        private static bool _dirty;
        private static double _lastRepaint;
        private static PreviewRenderUtility _render;

        // the picked object's preview, rendered again only when it or the view changes
        private static GameIndexEntry _bigEntry;
        private static GameModel _bigModel;
        private static Texture2D _bigImage;
        private static Vector2 _angles = new Vector2(-35f, 20f);
        private static bool _bigDirty;
        private static Vector2Int _bigSize;

        // a file name (material signatures hold "file:id")
        private static string KeyOf(string map, GameIndexEntry e) => map + "_" + e.Signature.Replace("|", "_").Replace(" ", "").Replace(":", "_");

        public static bool Failed(GameIndexEntry e) => Failures.Contains(e.Signature);

        public static Texture2D Get(string map, GameIndexEntry e)
        {
            var key = KeyOf(map, e);
            return Loaded.TryGetValue(key, out var texture) && texture != null ? texture : null;
        }

        private static bool OnDisk(string key)
        {
            if (_onDisk == null)
            {
                _onDisk = new HashSet<string>();
                if (Directory.Exists(Folder))
                    foreach (var path in Directory.GetFiles(Folder, "*.png"))
                        _onDisk.Add(Path.GetFileNameWithoutExtension(path));
            }
            return _onDisk.Contains(key);
        }

        // the cells on screen now: anything not on screen leaves the queues
        public static void Want(string map, List<GameIndexEntry> entries, Action repaint)
        {
            _repaint = repaint;
            DiskQueue.Clear();
            var jobs = new List<TarkovGameModelWorker.Job>();
            // by scene, so each scene is read once for the page
            foreach (var e in entries.OrderBy(e => e.Scene))
            {
                var key = KeyOf(map, e);
                if (Loaded.ContainsKey(key) || Failures.Contains(e.Signature))
                    continue;
                Signatures[key] = e.Signature;
                if (OnDisk(key))
                    DiskQueue.Add(key);
                else
                    jobs.Add(new TarkovGameModelWorker.Job { Key = key, Entry = e, TextureSize = Size });
            }
            TarkovGameModelWorker.Want(jobs);
        }

        public static void Release()
        {
            TarkovGameModelWorker.Want(new List<TarkovGameModelWorker.Job>());
            foreach (var t in Loaded.Values)
                if (t != null)
                    Object.DestroyImmediate(t);
            Loaded.Clear();
            Order.Clear();
            DiskQueue.Clear();
            _bigModel?.Dispose();
            _bigModel = null;
            _bigEntry = null;
            if (_bigImage != null)
                Object.DestroyImmediate(_bigImage);
            _bigImage = null;
            _render?.Cleanup();
            _render = null;
        }

        public static void Process()
        {
            double start = EditorApplication.timeSinceStartup;
            bool changed = false;
            while (DiskQueue.Count > 0 && EditorApplication.timeSinceStartup - start < Budget)
            {
                var key = DiskQueue[0];
                DiskQueue.RemoveAt(0);
                if (Loaded.ContainsKey(key))
                    continue;
                var texture = new Texture2D(2, 2, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    if (texture.LoadImage(File.ReadAllBytes($"{Folder}/{key}.png")))
                    {
                        Remember(key, texture);
                        changed = true;
                        continue;
                    }
                }
                catch (IOException)
                {
                }
                Object.DestroyImmediate(texture);
                _onDisk?.Remove(key);
            }
            while (EditorApplication.timeSinceStartup - start < Budget && TarkovGameModelWorker.TryTake(out var job))
            {
                changed = true;
                if (job.Key.StartsWith(BigPrefix))
                {
                    if (_bigEntry != null && job.Key == BigPrefix + _bigEntry.Signature)
                    {
                        _bigModel?.Dispose();
                        _bigModel = TarkovGameAssets.CreateModel(job.Result);
                        _bigDirty = true;
                    }
                    continue;
                }
                var model = TarkovGameAssets.CreateModel(job.Result);
                var image = model.Parts.Count > 0 ? Render(model, Size, Size, new Vector2(-35f, 20f)) : null;
                model.Dispose();
                if (image == null)
                {
                    if (Signatures.TryGetValue(job.Key, out var signature))
                        Failures.Add(signature);
                    continue;
                }
                Remember(job.Key, image);
                var bytes = image.EncodeToPNG();
                var path = $"{Folder}/{job.Key}.png";
                _onDisk?.Add(job.Key);
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        Directory.CreateDirectory(Folder);
                        File.WriteAllBytes(path, bytes);
                    }
                    catch (IOException)
                    {
                    }
                });
            }
            if (changed)
                _dirty = true;
            if (_dirty && EditorApplication.timeSinceStartup - _lastRepaint > 0.1)
            {
                _dirty = false;
                _lastRepaint = EditorApplication.timeSinceStartup;
                _repaint?.Invoke();
            }
        }

        private static void Remember(string key, Texture2D texture)
        {
            Loaded[key] = texture;
            Order.AddLast(key);
            while (Order.Count > MemoryLimit)
            {
                var old = Order.First.Value;
                Order.RemoveFirst();
                if (Loaded.TryGetValue(old, out var t) && t != null)
                    Object.DestroyImmediate(t);
                Loaded.Remove(old);
            }
        }

        private static PreviewRenderUtility Renderer()
        {
            if (_render == null)
            {
                _render = new PreviewRenderUtility();
                _render.camera.fieldOfView = 30f;
                _render.camera.clearFlags = CameraClearFlags.SolidColor;
                _render.camera.backgroundColor = new Color(0.18f, 0.18f, 0.18f);
                _render.lights[0].intensity = 1.2f;
                _render.lights[0].transform.rotation = Quaternion.Euler(40f, 40f, 0f);
                _render.lights[1].intensity = 0.7f;
                _render.lights[1].transform.rotation = Quaternion.Euler(-20f, -140f, 0f);
                _render.ambientColor = new Color(0.4f, 0.4f, 0.4f);
            }
            return _render;
        }

        private static Texture2D Render(GameModel model, int width, int height, Vector2 angles)
        {
            var render = Renderer();
            render.BeginStaticPreview(new Rect(0f, 0f, width, height));
            var camera = render.camera;
            var bounds = model.Bounds;
            float radius = Mathf.Max(0.05f, bounds.extents.magnitude);
            var rotation = Quaternion.Euler(-angles.y, -angles.x, 0f);
            float distance = radius / Mathf.Sin(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            camera.transform.rotation = rotation;
            camera.transform.position = bounds.center - rotation * Vector3.forward * distance;
            camera.nearClipPlane = Mathf.Max(0.01f, distance - radius * 1.5f);
            camera.farClipPlane = distance + radius * 1.5f;
            foreach (var part in model.Parts)
                for (int i = 0; i < part.Mesh.subMeshCount; i++)
                {
                    var material = part.Materials.Length > 0 ? part.Materials[Mathf.Min(i, part.Materials.Length - 1)] : null;
                    if (material != null)
                        render.DrawMesh(part.Mesh, part.Matrix, material, i);
                }
            render.Render(true);
            var texture = render.EndStaticPreview();
            texture.hideFlags = HideFlags.HideAndDontSave;
            return texture;
        }

        // the picked object, turned by dragging
        public static void DrawInteractive(string map, GameIndexEntry entry, Rect rect)
        {
            if (entry != _bigEntry)
            {
                _bigEntry = entry;
                _bigModel?.Dispose();
                _bigModel = null;
                _bigDirty = true;
                TarkovGameModelWorker.WantBig(new TarkovGameModelWorker.Job { Key = BigPrefix + entry.Signature, Entry = entry, TextureSize = 512 });
            }
            var e = Event.current;
            if (rect.Contains(e.mousePosition) && e.type == EventType.MouseDrag)
            {
                _angles += e.delta;
                _angles.y = Mathf.Clamp(_angles.y, -89f, 89f);
                _bigDirty = true;
                e.Use();
            }
            if (e.type != EventType.Repaint)
                return;
            if (_bigModel == null)
            {
                GUI.Box(rect, "Loading...", EditorStyles.helpBox);
                return;
            }
            if (_bigModel.Parts.Count == 0)
            {
                GUI.Box(rect, _bigModel.Error ?? "nothing to show", EditorStyles.helpBox);
                return;
            }
            int width = Mathf.Max(16, (int)rect.width), height = Mathf.Max(16, (int)rect.height);
            if (_bigDirty || _bigImage == null || _bigSize != new Vector2Int(width, height))
            {
                if (_bigImage != null)
                    Object.DestroyImmediate(_bigImage);
                _bigImage = Render(_bigModel, width, height, _angles);
                _bigSize = new Vector2Int(width, height);
                _bigDirty = false;
            }
            GUI.DrawTexture(rect, _bigImage);
        }
    }
}
