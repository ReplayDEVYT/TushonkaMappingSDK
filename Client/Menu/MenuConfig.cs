using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SPT.Common.Http;
using UnityEngine;

namespace mizmii.CustomMapLoader.Menu
{
    // The map select screen settings the server mod collects from each map's menu.json and the
    // installed map packs (/custommaploader/menu): which page each map sits on, which maps let players
    // pick the time of day, and the pictures for pack backgrounds and location icons. Pictures are
    // served by the server under /files/, like the game's banners, and loaded once per game session.
    internal static class MenuConfig
    {
#pragma warning disable CS0649 // filled from the server's JSON
        internal sealed class Pack
        {
            public string Id;
            public string Name;
            public int Order;
            public string Background;
            public string Icon;
        }

        internal sealed class Map
        {
            public string Pack;
            public bool TimeSelection;
            public string Icon;
        }

        private sealed class Response
        {
            public List<Pack> Packs;
            public Dictionary<string, Map> Maps;
        }
#pragma warning restore CS0649

        private static readonly List<Pack> PackList = new List<Pack>();
        private static readonly Dictionary<string, Map> Maps = new Dictionary<string, Map>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> TimeSelectionList = new List<string>();

        // sorted as the server sorts them: Order, then name
        public static IReadOnlyList<Pack> Packs => PackList;
        public static IReadOnlyList<string> TimeSelectionMaps => TimeSelectionList;

        public static async void Load()
        {
            try
            {
                var json = await Task.Run(() => RequestHandler.GetJsonAsync("/custommaploader/menu"));
                var response = JsonConvert.DeserializeObject<Response>(json);
                PackList.Clear();
                Maps.Clear();
                TimeSelectionList.Clear();
                if (response?.Packs != null)
                    PackList.AddRange(response.Packs);
                if (response?.Maps != null)
                    foreach (var pair in response.Maps)
                    {
                        Maps[pair.Key] = pair.Value;
                        if (pair.Value.TimeSelection)
                            TimeSelectionList.Add(pair.Key);
                    }
                Plugin.Verbose($"Map select settings: {PackList.Count} map pack(s), {Maps.Count} map(s) with settings");
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogWarning($"Could not read the map select settings from the server ({e.Message}). " +
                                            "Map packs, location icons and time selection are off; is the CustomMapLoader server mod up to date?");
            }
        }

        public static bool HasTimeSelection(string mapId) =>
            mapId != null && Maps.TryGetValue(mapId, out var map) && map.TimeSelection;

        // 0 is Tarkov's own page; pack i is page i + 1. A map whose pack isn't installed stays on Tarkov's page.
        public static int PageCount => 1 + PackList.Count;

        public static int PageOf(string mapId)
        {
            if (mapId == null || !Maps.TryGetValue(mapId, out var map) || string.IsNullOrEmpty(map.Pack))
                return 0;
            return PackList.FindIndex(p => p.Id == map.Pack) + 1;
        }

        public static Pack PackOfPage(int page) => page > 0 && page <= PackList.Count ? PackList[page - 1] : null;

        // the map's own icon, else its pack's
        public static string IconOf(string mapId)
        {
            if (mapId == null || !Maps.TryGetValue(mapId, out var map))
                return null;
            return !string.IsNullOrEmpty(map.Icon) ? map.Icon : PackOfPage(PageOf(mapId))?.Icon;
        }

        // ------------------------------------------------------------ pictures

        private static readonly Dictionary<string, Sprite> Sprites = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, List<Action<Sprite>>> Waiting = new Dictionary<string, List<Action<Sprite>>>();

        // calls ready with the picture at /files/<path> (null if it can't be loaded), on the main thread
        public static void GetSprite(string path, Action<Sprite> ready)
        {
            if (string.IsNullOrEmpty(path))
            {
                ready(null);
                return;
            }
            if (Sprites.TryGetValue(path, out var sprite))
            {
                ready(sprite);
                return;
            }
            if (Waiting.TryGetValue(path, out var waiting))
            {
                waiting.Add(ready);
                return;
            }
            Waiting[path] = new List<Action<Sprite>> { ready };
            LoadSprite(path);
        }

        private static async void LoadSprite(string path)
        {
            Sprite sprite = null;
            try
            {
                var bytes = await Task.Run(() => RequestHandler.GetDataAsync("/files/" + path));
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = path };
                if (texture.LoadImage(bytes))
                {
                    texture.wrapMode = TextureWrapMode.Clamp;
                    sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
                    sprite.name = path;
                }
                else
                {
                    Plugin.LogSource.LogWarning($"/files/{path} is not a PNG or JPG picture");
                }
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogWarning($"Could not load /files/{path}: {e.Message}");
            }
            Sprites[path] = sprite;
            if (!Waiting.TryGetValue(path, out var callbacks))
                return;
            Waiting.Remove(path);
            foreach (var callback in callbacks)
            {
                try
                {
                    callback(sprite);
                }
                catch (Exception e)
                {
                    Plugin.LogSource.LogError($"Applying /files/{path} failed: {e}");
                }
            }
        }
    }
}
