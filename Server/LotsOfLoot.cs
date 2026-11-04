using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Services.InRaid;

namespace mizmii.CustomMapLoader;

// Lots of Loot (archangelwtf-lotsoflootredux) compatibility. It replaces SPT's loose loot generation and reads its
// active preset by map id with no fallback (`Limits[locationName]` in LotsofLootLocationLootGenerator.GenerateDynamicLoot:
// "The given key 'suburbs_mod' was not present in the dictionary" on every custom map), and it writes its loose and
// static multipliers into SPT's LocationConfig only for the maps its preset lists. Its marked and reference room
// lookups check for the map first, so they need nothing.
//
// Right before each raid's loot is generated (after Lots of Loot's own prefix there, which may swap the preset for the
// raid), a custom map's entries are put in the active preset and SPT's multipliers:
//  - Preset: the values the preset has for LikeMap (a retail map id, Customs by default), so the map gets as much
//    more loot as that map does;
//  - Ignore: SPT's own amounts (multipliers 1) and no spawn limit.
// Which one comes from config.json in this mod's folder (written with the defaults on first start; per map, then for
// all maps), else base.json's "LotsOfLoot" (the map maker's, set in the SDK's Server Data Editor), else Preset like
// bigmap. Lots of Loot is found by reflection through the DI container, so nothing changes without it.
[Injectable(InjectionType.Singleton)]
public sealed class LotsOfLootCompat(IServiceProvider services, LocationConfig locationConfig, ISptLogger<LotsOfLootCompat> logger)
{
    public const string Preset = "Preset";
    public const string Ignore = "Ignore";
    private const string DefaultLikeMap = "bigmap";

    // static: SPT makes the patch below in its own pass, which may get another instance of this "singleton" than the
    // map loader registers maps with
    private static readonly Dictionary<string, MapSetting> _maps = new(StringComparer.OrdinalIgnoreCase);
    private static LotsOfLootConfig _config = new();
    private static bool _missingLogged, _foundLogged;
    private Type? _serviceType;
    private bool _searched;

    // config.json's settings, written with the defaults when there's none
    public void LoadConfig(string modDir)
    {
        var path = Path.Combine(modDir, "config.json");
        // relaxed escaping: the help text stays readable (quotes, apostrophes) for whoever opens the file
        var options = new JsonSerializerOptions { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        try
        {
            if (File.Exists(path))
            {
                _config = (JsonSerializer.Deserialize<ModConfig>(File.ReadAllText(path), options) ?? new ModConfig()).LotsOfLoot ?? new LotsOfLootConfig();
                return;
            }
            File.WriteAllText(path, JsonSerializer.Serialize(new ModConfig { LotsOfLoot = new LotsOfLootConfig() }, options));
        }
        catch (Exception e)
        {
            logger.Warning($"[MappingSDK] {path} could not be read ({e.Message}); using the defaults");
            _config = new LotsOfLootConfig();
        }
    }

    // a custom map and its maker's setting (base.json "LotsOfLoot", may be null)
    public void Register(string mapId, MapSetting? fromMap) => _maps[mapId] = fromMap ?? new MapSetting();

    // what a map does: config.json's for the map, then its for all maps, then the map's own, then Preset like bigmap
    private (string Mode, string LikeMap) SettingFor(string mapId)
    {
        _config.Maps ??= new Dictionary<string, MapSetting>(StringComparer.OrdinalIgnoreCase);
        var perMap = _config.Maps.FirstOrDefault(m => string.Equals(m.Key, mapId, StringComparison.OrdinalIgnoreCase)).Value;
        _maps.TryGetValue(mapId, out var own);
        string mode = Pick(perMap?.Mode, _config.Mode, own?.Mode) ?? Preset;
        string like = Pick(perMap?.LikeMap, _config.LikeMap, own?.LikeMap) ?? DefaultLikeMap;
        return (string.Equals(mode, Ignore, StringComparison.OrdinalIgnoreCase) ? Ignore : Preset, like);
    }

    private static string? Pick(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    // Lots of Loot's ConfigService (null without the mod)
    private object? Service()
    {
        if (!_searched)
        {
            _searched = true;
            _serviceType = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "LotsofLoot")
                .Select(a => a.GetType("LotsofLoot.Services.ConfigService")).FirstOrDefault(t => t != null);
        }
        return _serviceType != null ? services.GetService(_serviceType) : null;
    }

    public void BeforeRaid(string location)
    {
        if (string.IsNullOrEmpty(location) || !_maps.ContainsKey(location))
            return;
        try
        {
            var service = Service();
            if (service == null)
                return;
            var preset = service.GetType().GetProperty("LotsofLootPresetConfig")?.GetValue(service);
            var loose = Table<double>(preset, "LooseLootMultiplier");
            var stat = Table<double>(preset, "StaticLootMultiplier");
            var limits = Table<int>(preset, "Limits");
            if (loose == null || stat == null || limits == null)
            {
                if (!_missingLogged)
                    logger.Warning("[MappingSDK] Lots of Loot is installed but its preset isn't laid out as expected; custom maps may fail to generate loot with it");
                _missingLogged = true;
                return;
            }
            var (mode, like) = SettingFor(location);
            // the keys it's read by: the location's id as given, and lower case as its other lookups use
            var keys = new[] { location, location.ToLowerInvariant() }.Distinct().ToList();
            double looseMultiplier = 1, staticMultiplier = 1;
            int limit = int.MaxValue;
            if (mode == Preset)
            {
                var donor = loose.ContainsKey(like) ? like : loose.ContainsKey(DefaultLikeMap) ? DefaultLikeMap : loose.Keys.FirstOrDefault();
                if (donor != null)
                {
                    if (!string.Equals(donor, like, StringComparison.OrdinalIgnoreCase))
                        logger.Warning($"[MappingSDK] Lots of Loot's preset has no '{like}' (LikeMap for {location}); using '{donor}'");
                    looseMultiplier = loose[donor];
                    staticMultiplier = stat.TryGetValue(donor, out var s) ? s : 1;
                    limit = limits.TryGetValue(donor, out var l) ? l : int.MaxValue;
                }
            }
            foreach (var key in keys)
            {
                loose[key] = looseMultiplier;
                stat[key] = staticMultiplier;
                limits[key] = limit;
                locationConfig.LooseLootMultiplier[key] = looseMultiplier;
                locationConfig.StaticLootMultiplier[key] = staticMultiplier;
            }
            if (!_foundLogged)
                logger.Info($"[MappingSDK] Lots of Loot found: custom maps use its preset ({string.Join(", ", _maps.Keys.Select(m => $"{m}: {Describe(m)}"))})");
            _foundLogged = true;
        }
        catch (Exception e)
        {
            logger.Warning($"[MappingSDK] Lots of Loot settings for {location} could not be set: {e.Message}");
        }
    }

    private string Describe(string map)
    {
        var (mode, like) = SettingFor(map);
        return mode == Ignore ? "ignored (SPT's amounts)" : $"like {like}";
    }

    private static Dictionary<string, T>? Table<T>(object? preset, string name) =>
        preset?.GetType().GetProperty(name)?.GetValue(preset) as Dictionary<string, T>;

    // config.json
    public sealed class ModConfig
    {
        [JsonPropertyName("LotsOfLoot")]
        public LotsOfLootConfig? LotsOfLoot { get; set; }
    }

    public sealed class LotsOfLootConfig
    {
        [JsonPropertyName("_help")]
        public string Help { get; set; } =
            "How much loot custom maps get with Lots of Loot installed. Mode: \"Preset\" gives a map what Lots of Loot's preset gives LikeMap (a retail map id: " +
            "bigmap, factory4_day, woods, interchange, rezervbase, shoreline, tarkovstreets, lighthouse, sandbox, laboratory, labyrinth); \"Ignore\" leaves it SPT's own amounts. " +
            "Empty: each map's own setting (its maker's), else Preset like bigmap. Maps sets them per map, e.g. \"Maps\": { \"suburbs_mod\": { \"Mode\": \"Ignore\" } }. Restart the server after a change.";

        [JsonPropertyName("Mode")]
        public string Mode { get; set; } = "";

        [JsonPropertyName("LikeMap")]
        public string LikeMap { get; set; } = "";

        [JsonPropertyName("Maps")]
        public Dictionary<string, MapSetting>? Maps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class MapSetting
    {
        [JsonPropertyName("Mode")]
        public string? Mode { get; set; }

        [JsonPropertyName("LikeMap")]
        public string? LikeMap { get; set; }
    }
}

// SPT's loot generation for a raid: the custom map's Lots of Loot entries first. Last, after Lots of Loot's prefix on the
// same method, which may switch its preset for the raid.
[Injectable]
public sealed class LotsOfLootRaidPatch : AbstractPatch
{
    private static LotsOfLootCompat _compat = default!;

    public LotsOfLootRaidPatch(LotsOfLootCompat compat)
    {
        _compat = compat;
    }

    protected override MethodBase GetTargetMethod() =>
        typeof(LocationLifecycleService).GetMethod(nameof(LocationLifecycleService.GenerateLocationAndLoot))
        ?? throw new InvalidOperationException("Could not find LocationLifecycleService.GenerateLocationAndLoot");

    [PatchPrefix]
    [HarmonyPriority(Priority.Last)]
    public static void Prefix(string name, bool generateLoot)
    {
        if (generateLoot)
            _compat.BeforeRaid(name);
    }
}
