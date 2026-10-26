using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using Upd = SPTarkov.Server.Core.Models.Eft.Common.Tables.Upd;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Server.Core.Utils.Json;

namespace mizmii.CustomMapLoader;

// Registers every map under <mod>/maps/<id>/ as a new location:
//
//   base.json         location settings (copy maps/sdk_example/base.json as a template)
//   sdk_export.json   written by the Unity SDK build: spawn points, exits, export timestamp
//   locale.json       optional: { "Name": "...", "Description": "...", "Keys": { "<key>": "<text>" } }
//   menu.json         optional: map select screen settings and pictures (see MapMenu.cs)
//
// SPT 4.1's LocationTable.GetDictionary() returns the live dictionary that GetLocation and the
// client's location list read, so adding an entry is enough to make the map selectable.
// (Registration path documented by ManimalIcebreaker's STANDALONE-LOCATION.md.)
[Injectable(TypePriority = OnLoadOrder.Preload + 90000)]
public class CustomMapLoader(
    LocationTable locationTable,
    LocaleTable localeTable,
    GlobalTable globalTable,
    TemplateTable templateTable,
    LocationConfig locationConfig,
    BotConfig botConfig,
    PmcConfig pmcConfig,
    ICloner cloner,
    JsonUtil jsonUtil,
    ImageRouter imageRouter,
    MapMenuData menuData,
    MapFeaturesData featuresData,
    ISptLogger<CustomMapLoader> logger) : IOnLoad
{
    // map id -> location ids it connects to on the map select screen, resolved once every map is in
    private readonly Dictionary<string, List<string>> _connections = new(StringComparer.OrdinalIgnoreCase);

    // loot pools and ammo are borrowed from this map until a map ships its own
    private const string DonorLocation = "factory4_day";

    // the maps this mod registered, and the container types they use
    private readonly HashSet<string> _customMaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<MongoId> _containerTypes = [];
    private readonly HashSet<MongoId> _customContainerTypes = [];
    private const string WoodenCrate = "578f87ad245977356274f2cc";
    private readonly object _staticLootLock = new();
    private Dictionary<MongoId, StaticLootDetails>? _staticLoot;

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modDir = Path.GetDirectoryName(typeof(CustomMapLoader).Assembly.Location)!;
        var mapsDir = Path.Combine(modDir, "maps");
        if (!Directory.Exists(mapsDir))
        {
            logger.Warning($"[MappingSDK] No maps folder at {mapsDir}");
            return;
        }

        foreach (var dir in Directory.GetDirectories(mapsDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await LoadMap(dir, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.Error($"[MappingSDK] Map in {dir} was not loaded: {e.Message}");
            }
        }

        // after every map is registered, so a transit or connection can lead to another custom map
        foreach (var dir in Directory.GetDirectories(mapsDir))
            ResolveTransitTargets(Path.GetFileName(dir));
        AddConnections();
        await LoadPacks(Path.Combine(modDir, "packs"), cancellationToken);
    }

    // Map packs: extra pages of the map select screen, each with its own background
    private async Task LoadPacks(string packsDir, CancellationToken cancellationToken)
    {
        var packs = menuData.Response.Packs;
        if (Directory.Exists(packsDir))
            foreach (var dir in Directory.GetDirectories(packsDir))
            {
                var id = Path.GetFileName(dir).ToLowerInvariant();
                var path = Path.Combine(dir, "pack.json");
                if (!File.Exists(path))
                    continue;
                try
                {
                    var pack = await jsonUtil.DeserializeFromFileAsync<MapPackFile>(path, cancellationToken)
                               ?? throw new InvalidDataException("pack.json could not be parsed");
                    var background = MenuImages.Register(imageRouter, dir, $"packs/{id}", pack.Background);
                    if (background == null)
                        logger.Warning($"[MappingSDK] Map pack '{id}' has no background picture; it shows Tarkov's map");
                    packs.Add(new MenuPack
                    {
                        Id = id,
                        Name = string.IsNullOrWhiteSpace(pack.Name) ? id : pack.Name,
                        Order = pack.Order,
                        Background = background,
                        Icon = MenuImages.Register(imageRouter, dir, $"packs/{id}", pack.Icon),
                    });
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.Error($"[MappingSDK] Map pack in {dir} was not loaded: {e.Message}");
                }
            }
        packs.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        foreach (var (mapId, map) in menuData.Response.Maps)
            if (!string.IsNullOrEmpty(map.Pack) && packs.All(p => p.Id != map.Pack))
                logger.Warning($"[MappingSDK] {mapId}: map pack '{map.Pack}' is not installed, so the map shows on Tarkov's page");
        if (packs.Count > 0)
            logger.Success($"[MappingSDK] Map packs: {string.Join(", ", packs.Select(p => p.Name))}");
    }

    // A map's connections draw the dotted lines between locations on the map select screen, like
    // retail's (LocationsBase.Paths, by _Id). Only lines between locations on the same page show.
    private void AddConnections()
    {
        var locations = locationTable.GetDictionary();
        var paths = locationTable.Base!.Paths ??= [];
        foreach (var (mapId, targets) in _connections)
        {
            if (!locations.TryGetValue(mapId, out var source) || source?.Base == null)
                continue;
            foreach (var target in targets)
            {
                var destination = locations.Values.FirstOrDefault(l =>
                    l?.Base != null && string.Equals(l.Base.Id, target, StringComparison.OrdinalIgnoreCase));
                if (destination == null)
                {
                    logger.Warning($"[MappingSDK] {mapId}: connection to unknown location '{target}' skipped");
                    continue;
                }
                string from = source.Base.IdField.ToString(), to = destination.Base!.IdField.ToString();
                if (from == to || paths.Any(p => p.Source == from && p.Destination == to && p.Event != true))
                    continue;
                paths.Add(new SPTarkov.Server.Core.Models.Eft.Common.Tables.Path { Source = from, Destination = to, Event = false });
            }
        }
    }

    // The SDK exports a transit's destination as "location" (a map id); the game also needs
    // "target", the destination's _Id, which only the server knows for every map.
    private void ResolveTransitTargets(string mapId)
    {
        var transits = locationTable.GetDictionary().TryGetValue(mapId, out var location) ? location?.Base?.Transits : null;
        if (transits == null)
            return;
        foreach (var transit in transits)
        {
            if (!string.IsNullOrEmpty(transit.Target) || string.IsNullOrEmpty(transit.Location))
                continue;
            var destination = locationTable.GetDictionary().Values.FirstOrDefault(l =>
                l?.Base != null && string.Equals(l.Base.Id, transit.Location, StringComparison.OrdinalIgnoreCase));
            if (destination == null)
            {
                logger.Warning($"[MappingSDK] {mapId}: transit {transit.Id} leads to unknown location '{transit.Location}'");
                continue;
            }
            transit.Location = destination.Base!.Id; // the game matches the id's exact case
            transit.Target = destination.Base.IdField.ToString();
        }
    }

    private async Task LoadMap(string dir, CancellationToken cancellationToken)
    {
        var mapId = Path.GetFileName(dir);
        var basePath = Path.Combine(dir, "base.json");
        if (!File.Exists(basePath))
        {
            logger.Warning($"[MappingSDK] {dir} has no base.json, skipped");
            return;
        }

        var locationBase = await jsonUtil.DeserializeFromFileAsync<LocationBase>(basePath, cancellationToken)
                           ?? throw new InvalidDataException("base.json could not be parsed");
        if (!string.Equals(locationBase.Id, mapId, StringComparison.Ordinal))
            throw new InvalidDataException($"base.json Id '{locationBase.Id}' must equal the folder name '{mapId}'");
        EnsureAvailable(mapId, locationBase.IdField);

        // the bundle keys the client plugin serves, as built by the SDK
        locationBase.Scene = new Scene
        {
            Path = $"maps/{mapId}.bundle",
            Rcid = $"{mapId}.scenespreset.asset",
        };
        // retail banner rows point at files nothing serves; the client then shows an empty error.
        // The map's own pictures come from menu.json.
        locationBase.Banners = [];
        var menuPath = Path.Combine(dir, "menu.json");
        var menu = File.Exists(menuPath)
            ? await jsonUtil.DeserializeFromFileAsync<MapMenuFile>(menuPath, cancellationToken)
            : null;
        // ServerAirdropManager.Init reads AirdropParameters[0] unconditionally at raid start, and an
        // explicit [] replaces the client's one-entry default, so an empty list fails the raid load
        if (locationBase.AirdropParameters is not { Count: > 0 })
            locationBase.AirdropParameters = [DisabledAirdrops()];

        var exportPath = Path.Combine(dir, "sdk_export.json");
        List<SdkLootSpawn> lootSpawns = [];
        List<SdkContainer> containers = [];
        var extraLocales = new Dictionary<string, string>();
        // container types whose loot this map sets (custom containers, changed retail ones)
        var lootOverrides = new Dictionary<MongoId, SdkContainerType>();
        if (File.Exists(exportPath))
        {
            var export = await jsonUtil.DeserializeFromFileAsync<SdkExport>(exportPath, cancellationToken)
                         ?? throw new InvalidDataException("sdk_export.json could not be parsed");
            lootSpawns = export.LootSpawns ?? [];
            foreach (var container in export.Containers ?? [])
            {
                if (string.IsNullOrEmpty(container.Id) || !Regex.IsMatch(container.Template, "^[0-9a-fA-F]{24}$"))
                {
                    logger.Warning($"[MappingSDK] {mapId}: container '{container.Id}' has no valid Template, so it can't be searched");
                    continue;
                }
                containers.Add(container);
                _containerTypes.Add(new MongoId(container.Template));
            }
            locationBase.SpawnPointParams = export.SpawnPointParams ?? [];
            locationBase.Exits = export.Exits ?? [];
            if (export.Transits is { Count: > 0 })
                locationBase.Transits = export.Transits;
            // the client only accepts backend spawn points when the scene is not newer than this
            locationBase.UnixDateTime = Math.Max(locationBase.UnixDateTime, export.UnixDateTime);
            if (export.Btr != null)
                RegisterBtr(mapId, export.Btr, extraLocales);
            foreach (var type in export.ContainerTypes ?? [])
                if (RegisterContainerType(mapId, type, extraLocales))
                    lootOverrides[new MongoId(type.Template)] = type;
            if (export.Features is { ValueKind: JsonValueKind.Object } features)
                featuresData.Maps[mapId] = features.Clone();
        }
        else
        {
            logger.Warning($"[MappingSDK] {mapId}: no sdk_export.json, using spawn points and exits from base.json. Build the map in the SDK to create it.");
        }

        var donor = locationTable.GetLocation(DonorLocation);
        var location = new Location
        {
            Base = locationBase,
            StaticAmmo = donor?.StaticAmmo ?? new Dictionary<string, IEnumerable<StaticAmmoDetails>>(),
            // every retail map's container pools, keyed by container type, with this map's own on top; only read, never mutated
            StaticLoot = new LazyLoad<Dictionary<MongoId, StaticLootDetails>>(() => MapStaticLoot(lootOverrides), cacheValue: true),
            // not cached: each raid rolls which containers spawn
            StaticContainers = new LazyLoad<StaticContainerDetails>(() => BuildStaticContainers(mapId, containers), cacheValue: false),
            // not cached: SPT's loot generator mutates the loose loot it reads each raid
            LooseLoot = new LazyLoad<LooseLoot>(() => BuildLooseLoot(lootSpawns), cacheValue: false),
            AllExtracts = [],
        };

        locationTable.GetDictionary().Add(mapId, location);
        _customMaps.Add(mapId);
        ConfigureLocation(mapId);
        var bannerTexts = ApplyMenu(dir, mapId, locationBase, menu);
        await RegisterLocales(dir, locationBase, bannerTexts, extraLocales, cancellationToken);

        logger.Success($"[MappingSDK] Registered map '{mapId}' ({locationBase.IdField}): " +
                       $"{locationBase.SpawnPointParams?.Count() ?? 0} spawn point(s), {locationBase.Exits?.Count() ?? 0} exit(s), " +
                       $"{lootSpawns.Count} loot spawn(s), {containers.Count} container(s)");
    }

    // The scene's lootable containers in retail's staticContainers layout: the root item is the
    // container type and the Id is the LootableContainer's. SPT spawns every container on a map
    // without container randomisation, so the spawn chance is rolled here, once per raid.
    private StaticContainerDetails BuildStaticContainers(string mapId, List<SdkContainer> containers)
    {
        var list = new List<StaticContainerData>();
        foreach (var container in containers)
        {
            // SPT's loot generator throws on a container type that isn't an item (its grid has no size), failing
            // the whole raid; e.g. a custom container type from an SDK newer than this server mod
            if (!templateTable.Items.TryGetValue(new MongoId(container.Template), out var item) || item.Properties?.Grids?.Any() != true)
            {
                logger.Warning($"[MappingSDK] {mapId}: container '{container.Id}' uses type {container.Template}, which isn't a container item; it's left out of the raid");
                continue;
            }
            if (!container.AlwaysSpawn && Random.Shared.NextDouble() >= container.Probability)
                continue;
            var root = new MongoId();
            list.Add(new StaticContainerData
            {
                Probability = 1,
                Template = new SpawnpointTemplate
                {
                    Id = container.Id,
                    IsContainer = true,
                    UseGravity = false,
                    RandomRotation = false,
                    Position = new Vector3(0, 0, 0),
                    Rotation = new Vector3(0, 0, 0),
                    IsGroupPosition = false,
                    GroupPositions = [],
                    IsAlwaysSpawn = true,
                    Root = root.ToString(),
                    Items =
                    [
                        new SptLootItem
                        {
                            Id = root,
                            Template = new MongoId(container.Template),
                            Upd = new Upd { StackObjectsCount = 1 },
                        },
                    ],
                },
            });
        }
        return new StaticContainerDetails { StaticWeapons = [], StaticContainers = list, StaticForced = [] };
    }

    // Container pools from every retail map (factory first), so any retail container type gets its
    // loot. SPT indexes the pool by container type unchecked, so a type no map has gets an empty
    // pool: it opens empty instead of failing the raid's loot.
    private Dictionary<MongoId, StaticLootDetails> MergedStaticLoot()
    {
        lock (_staticLootLock)
        {
            if (_staticLoot != null)
                return _staticLoot;
            var merged = new Dictionary<MongoId, StaticLootDetails>();
            var locations = locationTable.GetDictionary();
            foreach (var (id, location) in locations.OrderBy(l => l.Key == DonorLocation ? 0 : 1))
            {
                if (_customMaps.Contains(id) || location?.StaticLoot == null)
                    continue;
                try
                {
                    foreach (var (type, pool) in location.StaticLoot.Value ?? [])
                        merged.TryAdd(type, pool);
                }
                catch (Exception e)
                {
                    logger.Warning($"[MappingSDK] Container loot of '{id}' could not be read: {e.Message}");
                }
            }
            foreach (var type in _containerTypes)
                if (!merged.ContainsKey(type))
                {
                    logger.Warning($"[MappingSDK] No retail map has loot for container type {type}; those containers open empty");
                    merged[type] = new StaticLootDetails
                    {
                        ItemCountDistribution = [new ItemCountDistribution { Count = 0, RelativeProbability = 1 }],
                        ItemDistribution = [],
                    };
                }
            return _staticLoot = merged;
        }
    }

    // The map's container types over the retail pools: a custom container's own loot, or a retail type's loot changed
    // for this map. The merged retail pools are shared by every map, so a map's changes go in its own copy.
    private Dictionary<MongoId, StaticLootDetails> MapStaticLoot(Dictionary<MongoId, SdkContainerType> overrides)
    {
        var merged = MergedStaticLoot();
        if (overrides.Count == 0)
            return merged;
        var loot = new Dictionary<MongoId, StaticLootDetails>(merged);
        foreach (var (type, settings) in overrides)
            loot[type] = BuildLoot(settings, merged);
        return loot;
    }

    private StaticLootDetails BuildLoot(SdkContainerType type, Dictionary<MongoId, StaticLootDetails> retail)
    {
        StaticLootDetails? copied = null;
        if (type.Loot is "CopyRetail" or "RetailPlusCustom" && Regex.IsMatch(type.RetailLootTemplate ?? "", "^[0-9a-fA-F]{24}$"))
            retail.TryGetValue(new MongoId(type.RetailLootTemplate!), out copied);
        if (type.Loot == "CopyRetail" && copied != null)
            return copied;

        var items = new List<ItemDistribution>();
        if (type.Loot == "RetailPlusCustom" && copied?.ItemDistribution != null)
            items.AddRange(copied.ItemDistribution);
        if (type.Loot is "Custom" or "RetailPlusCustom")
            foreach (var item in type.Items ?? [])
                if (Regex.IsMatch(item.Template, "^[0-9a-fA-F]{24}$") && templateTable.Items.ContainsKey(new MongoId(item.Template)))
                    items.Add(new ItemDistribution { Tpl = new MongoId(item.Template), RelativeProbability = Math.Max(1, item.Weight) });

        IEnumerable<ItemCountDistribution> counts;
        if (type.Loot == "Empty" || items.Count == 0)
            counts = [new ItemCountDistribution { Count = 0, RelativeProbability = 1 }];
        else if (type.Loot == "RetailPlusCustom" && copied?.ItemCountDistribution != null)
            counts = copied.ItemCountDistribution;
        else
        {
            int min = Math.Max(0, type.MinItems), max = Math.Max(min, type.MaxItems);
            counts = Enumerable.Range(min, max - min + 1).Select(n => new ItemCountDistribution { Count = n, RelativeProbability = 1 }).ToList();
        }
        return new StaticLootDetails { ItemCountDistribution = counts, ItemDistribution = items };
    }

    // A custom container: a new item made from a retail loot container (its sounds, search time and menu look),
    // with its own grid and name. Retail types only change their loot. Several maps can share a custom type.
    private bool RegisterContainerType(string mapId, SdkContainerType type, Dictionary<string, string> locales)
    {
        if (!Regex.IsMatch(type.Template, "^[0-9a-fA-F]{24}$"))
        {
            logger.Warning($"[MappingSDK] {mapId}: container type '{type.Name}' has no valid Template, skipped");
            return false;
        }
        var id = new MongoId(type.Template);
        _containerTypes.Add(id);
        if (!type.New)
        {
            if (!templateTable.Items.ContainsKey(id))
            {
                logger.Warning($"[MappingSDK] {mapId}: container type {id} is not an item; its loot change is skipped");
                return false;
            }
            return true;
        }
        if (templateTable.Items.TryGetValue(id, out var existing) && !_customContainerTypes.Contains(id))
        {
            logger.Warning($"[MappingSDK] {mapId}: custom container '{type.Name}' uses the template id of existing item '{existing.Name}'; give it a new id in the SDK");
            return false;
        }
        var baseId = Regex.IsMatch(type.BaseTemplate ?? "", "^[0-9a-fA-F]{24}$") ? new MongoId(type.BaseTemplate!) : new MongoId(WoodenCrate);
        if (!templateTable.Items.TryGetValue(baseId, out var baseItem) && !templateTable.Items.TryGetValue(new MongoId(WoodenCrate), out baseItem))
            return false;
        if (_customContainerTypes.Add(id))
        {
            var item = cloner.Clone(baseItem)!;
            item.Id = id;
            item.Name = "custommaploader_" + Regex.Replace(type.Name ?? "container", "[^A-Za-z0-9]+", "_").ToLowerInvariant();
            var grid = cloner.Clone(baseItem.Properties!.Grids!.First())!;
            grid.Id = new MongoId();
            grid.Parent = id;
            grid.Properties!.CellsH = Math.Clamp(type.Width, 1, 20);
            grid.Properties.CellsV = Math.Clamp(type.Height, 1, 40);
            item.Properties!.Grids = [grid];
            templateTable.Items[id] = item;
        }
        locales[$"{id} Name"] = type.Name ?? "Container";
        locales[$"{id} ShortName"] = string.IsNullOrEmpty(type.ShortName) ? type.Name ?? "Container" : type.ShortName;
        locales[$"{id} Description"] = type.Description ?? "";
        return true;
    }

    // menu.json's pictures as the location's banners: the preview first (the map select screen's
    // info panel shows Banners[0]), then the loading screen banners (the loading screen cycles
    // through all of them). Returns each banner's title and description; null ones take the map's.
    private List<(string Id, string? Name, string? Description)> ApplyMenu(string dir, string mapId, LocationBase locationBase, MapMenuFile? menu)
    {
        var texts = new List<(string, string?, string?)>();
        if (menu == null)
            return texts;
        var images = Path.Combine(dir, "images");
        var banners = new List<Banner>();
        void Add(string? file, string? name, string? description)
        {
            var path = MenuImages.Register(imageRouter, images, mapId, file);
            if (path == null)
            {
                if (!string.IsNullOrWhiteSpace(file))
                    logger.Warning($"[MappingSDK] {mapId}: picture images/{file} is missing");
                return;
            }
            // the client reads "<id> Name" / "<id> Description" from locales
            var id = $"{locationBase.IdField}_banner{banners.Count}";
            banners.Add(new Banner
            {
                Id = id,
                Picture = new Pic { File = Path.GetFileName(path), Path = path, Rcid = "", Type = "banners" },
            });
            texts.Add((id, name, description));
        }
        Add(menu.Preview, null, null);
        foreach (var banner in menu.Banners ?? [])
            Add(banner.Image, string.IsNullOrWhiteSpace(banner.Name) ? null : banner.Name, banner.Description ?? "");
        locationBase.Banners = banners;

        if (menu.Connections is { Count: > 0 })
            _connections[mapId] = menu.Connections;
        menuData.Response.Maps[mapId] = new MenuMap
        {
            Pack = string.IsNullOrWhiteSpace(menu.Pack) ? null : menu.Pack.ToLowerInvariant(),
            TimeSelection = menu.TimeSelection,
            Icon = MenuImages.Register(imageRouter, images, mapId, menu.Icon),
        };
        return texts;
    }

    // SDK loot spawns as SPT loose loot, in retail's layout: every candidate item is a root item in
    // the template, keyed by composedKey, and itemDistribution weights the keys; SPT's generator
    // picks one per spawnpoint. Always-spawn points go to spawnpointsForced. On average a raid
    // spawns the sum of the other points' probabilities.
    private static LooseLoot BuildLooseLoot(List<SdkLootSpawn> spawns)
    {
        var points = new List<Spawnpoint>();
        var forced = new List<Spawnpoint>();
        double expected = 0;
        foreach (var spawn in spawns)
        {
            if (spawn.Items is not { Count: > 0 })
                continue;
            var items = new List<SptLootItem>();
            var distribution = new List<LooseLootItemDistribution>();
            foreach (var entry in spawn.Items)
            {
                var id = new MongoId();
                var key = id.ToString();
                items.Add(new SptLootItem
                {
                    Id = id,
                    Template = new MongoId(entry.Template),
                    Upd = new Upd { StackObjectsCount = Math.Max(1, entry.StackCount) },
                    ComposedKey = key,
                });
                distribution.Add(new LooseLootItemDistribution
                {
                    ComposedKey = new ComposedKey { Key = key },
                    RelativeProbability = Math.Max(1, entry.Weight),
                });
            }
            var position = spawn.Position ?? new Vector3(0, 0, 0);
            var point = new Spawnpoint
            {
                LocationId = $"({position.X}, {position.Y}, {position.Z})",
                Probability = spawn.AlwaysSpawn ? 1 : Math.Clamp(spawn.Probability, 0, 1),
                Template = new SpawnpointTemplate
                {
                    Id = spawn.Id,
                    IsContainer = false,
                    UseGravity = spawn.UseGravity,
                    RandomRotation = spawn.RandomRotation,
                    Position = position,
                    Rotation = spawn.Rotation ?? new Vector3(0, 0, 0),
                    IsGroupPosition = false,
                    GroupPositions = [],
                    IsAlwaysSpawn = spawn.AlwaysSpawn,
                    Root = items[0].Id.ToString(),
                    Items = items,
                },
                ItemDistribution = distribution,
            };
            if (spawn.AlwaysSpawn)
            {
                forced.Add(point);
            }
            else
            {
                points.Add(point);
                expected += point.Probability ?? 0;
            }
        }
        return new LooseLoot
        {
            SpawnpointCount = new SpawnpointCount { Mean = expected, Std = Math.Sqrt(expected) * 0.5 },
            Spawnpoints = points,
            SpawnpointsForced = forced,
        };
    }

    // the client's own AirdropParameters defaults, with no plane drops (chance and max of 0)
    private static AirdropParameter DisabledAirdrops() => new()
    {
        PlaneAirdropStartMin = 250,
        PlaneAirdropStartMax = 550,
        PlaneAirdropEnd = 2200,
        PlaneAirdropChance = 0,
        PlaneAirdropMax = 0,
        PlaneAirdropCooldownMin = 550,
        PlaneAirdropCooldownMax = 850,
        AirdropPointDeactivateDistance = 60,
        MinimumPlayersCountToSpawnAirdrop = 6,
        UnsuccessfulTryPenalty = 120,
    };

    private void EnsureAvailable(string mapId, MongoId id)
    {
        var locations = locationTable.GetDictionary();
        if (locations.ContainsKey(mapId))
            throw new InvalidOperationException($"location key '{mapId}' is already registered");
        foreach (var (key, existing) in locations)
            if (existing?.Base != null && existing.Base.IdField == id)
                throw new InvalidOperationException($"_Id {id} is already used by '{key}'; give base.json a new unique 24-character hex _Id");
    }

    // per-map config lookups that otherwise miss for an unknown location id
    private void ConfigureLocation(string mapId)
    {
        if (locationConfig.ScavRaidTimeSettings.Maps.TryGetValue(DonorLocation, out var raidTime))
            locationConfig.ScavRaidTimeSettings.Maps.TryAdd(mapId, cloner.Clone(raidTime)!);
        locationConfig.StaticLootMultiplier.TryAdd(mapId, 1);
        locationConfig.LooseLootMultiplier.TryAdd(mapId, 1);

        // the client reads these brain weights by map id when a bot's brain activates
        // (/singleplayer/settings/bot/getBotBehaviours/); a missing key throws inside
        // BotOwner activation and every bot is left standing in place (BotState.ActiveFail)
        CopyDonorEntry(botConfig.AssaultBrainType, mapId);
        CopyDonorEntry(botConfig.PlayerScavBrainType, mapId);
        foreach (var (_, byMap) in pmcConfig.PmcType)
            CopyDonorEntry(byMap, mapId);
        if (botConfig.MaxBotCap.TryGetValue(DonorLocation, out var botCap))
            botConfig.MaxBotCap.TryAdd(mapId, botCap);
    }

    private void CopyDonorEntry<T>(Dictionary<string, T> byMap, string mapId) where T : class
    {
        if (byMap.TryGetValue(DonorLocation, out var donor))
            byMap.TryAdd(mapId, cloner.Clone(donor)!);
    }

    // the map select screen reads "<_Id> Name" / "<_Id> Description" from locales, not base.json
    private async Task RegisterLocales(string dir, LocationBase locationBase, List<(string Id, string? Name, string? Description)> banners,
                                       Dictionary<string, string> extra, CancellationToken cancellationToken)
    {
        var path = Path.Combine(dir, "locale.json");
        var locale = File.Exists(path)
            ? await jsonUtil.DeserializeFromFileAsync<MapLocale>(path, cancellationToken)
            : null;

        var name = locale?.Name ?? locationBase.Name ?? locationBase.Id;
        var description = locale?.Description ?? locationBase.Description ?? "";
        var id = locationBase.IdField.ToString();

        foreach (var (_, lazy) in localeTable.Global)
        {
            lazy.AddTransformer(entries =>
            {
                if (entries == null)
                    return entries;
                entries[$"{id} Name"] = name;
                entries[$"{id} Description"] = description;
                entries[locationBase.Id] = name;
                foreach (var banner in banners)
                {
                    entries[$"{banner.Id} Name"] = banner.Name ?? name;
                    entries[$"{banner.Id} Description"] = banner.Description ?? description;
                }
                foreach (var (key, text) in extra)
                    entries[key] = text;
                if (locale?.Keys != null)
                    foreach (var (key, text) in locale.Keys)
                        entries[key] = text;
                return entries;
            });
        }
    }

    // The BTR (BaseLocalGame creates its BtrController when globals BTRSettings.LocationsWithBTR lists the map):
    // the routes go in BTRSettings.MapsConfigs (the client reads them by mapID), the timing in locationServices
    // ServerMapBTRSettings (sent with the raid), both copied from Woods' for the wheel and suspension values;
    // the taxi menu names the stops from Trading/Dialog/PlayerTaxi/<map>/<stop>/Name
    private void RegisterBtr(string mapId, SdkBtr btr, Dictionary<string, string> locales)
    {
        var settings = globalTable.Configuration.BTRSettings;
        var template = settings.MapsConfigs.Values.FirstOrDefault(m => m.MapID == "Woods") ?? settings.MapsConfigs.Values.FirstOrDefault();
        var server = templateTable.LocationServices.BtrServerSettings;
        var serverTemplate = server.ServerMapBTRSettings.Values.FirstOrDefault(m => m.MapID == "Woods") ?? server.ServerMapBTRSettings.Values.FirstOrDefault();
        if (template == null || serverTemplate == null)
        {
            logger.Warning($"[MappingSDK] {mapId}: SPT has no BTR settings to copy, so the map has no BTR");
            return;
        }
        var config = cloner.Clone(template)!;
        config.MapID = mapId;
        config.BtrSkin = string.IsNullOrEmpty(btr.Skin) ? template.BtrSkin : btr.Skin;
        config.PathsConfigurations = (btr.Routes ?? []).Select(r => new PathConfig
        {
            Active = r.Active,
            Id = r.Id,
            EnterPoint = r.EnterPoint,
            ExitPoint = r.ExitPoint,
            PathPoints = r.PathPoints ?? [],
            Once = r.Once,
            Circle = r.Circle,
            CircleCount = r.CircleCount,
            SkinType = [],
        }).ToList();
        settings.MapsConfigs[mapId] = config;
        settings.LocationsWithBTR = settings.LocationsWithBTR.Where(l => l != mapId).Append(mapId).ToList();

        var timing = cloner.Clone(serverTemplate)!;
        timing.MapID = mapId;
        timing.ChanceSpawn = btr.ChanceSpawn;
        timing.SpawnPeriod = new Vector3((float)btr.SpawnPeriodMin, (float)btr.SpawnPeriodMax, 0);
        timing.MoveSpeed = btr.MoveSpeed;
        timing.PauseDurationRange = new Vector3((float)btr.PauseMin, (float)btr.PauseMax, 0);
        server.ServerMapBTRSettings[mapId] = timing;

        foreach (var stop in btr.Stops ?? [])
        {
            locales[$"Trading/Dialog/PlayerTaxi/{mapId}/{stop.Id}/Name"] = stop.Name;
            locales[$"Trading/Dialog/PlayerTaxi/{mapId}/{stop.Id}/Description"] = stop.Description;
        }
    }

    private sealed record SdkBtr
    {
        [JsonPropertyName("ChanceSpawn")] public double ChanceSpawn { get; init; } = 100;
        [JsonPropertyName("SpawnPeriodMin")] public double SpawnPeriodMin { get; init; } = 300;
        [JsonPropertyName("SpawnPeriodMax")] public double SpawnPeriodMax { get; init; } = 600;
        [JsonPropertyName("MoveSpeed")] public float MoveSpeed { get; init; } = 8;
        [JsonPropertyName("PauseMin")] public double PauseMin { get; init; } = 90;
        [JsonPropertyName("PauseMax")] public double PauseMax { get; init; } = 150;
        [JsonPropertyName("Skin")] public string? Skin { get; init; }
        [JsonPropertyName("Routes")] public List<SdkBtrRoute>? Routes { get; init; }
        [JsonPropertyName("Stops")] public List<SdkBtrStop>? Stops { get; init; }
    }

    private sealed record SdkBtrRoute
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";
        [JsonPropertyName("active")] public bool Active { get; init; } = true;
        [JsonPropertyName("enterPoint")] public string EnterPoint { get; init; } = "";
        [JsonPropertyName("exitPoint")] public string ExitPoint { get; init; } = "";
        [JsonPropertyName("once")] public bool Once { get; init; }
        [JsonPropertyName("circle")] public bool Circle { get; init; }
        [JsonPropertyName("circleCount")] public double CircleCount { get; init; }
        [JsonPropertyName("pathPoints")] public List<string>? PathPoints { get; init; }
    }

    private sealed record SdkBtrStop
    {
        [JsonPropertyName("Id")] public string Id { get; init; } = "";
        [JsonPropertyName("Name")] public string Name { get; init; } = "";
        [JsonPropertyName("Description")] public string Description { get; init; } = "";
    }

    private sealed record SdkExport
    {
        [JsonPropertyName("MapId")]
        public string? MapId { get; init; }

        [JsonPropertyName("UnixDateTime")]
        public long UnixDateTime { get; init; }

        [JsonPropertyName("SpawnPointParams")]
        public List<SpawnPointParam>? SpawnPointParams { get; init; }

        [JsonPropertyName("Exits")]
        public List<Exit>? Exits { get; init; }

        [JsonPropertyName("Transits")]
        public List<Transit>? Transits { get; init; }

        [JsonPropertyName("LootSpawns")]
        public List<SdkLootSpawn>? LootSpawns { get; init; }

        [JsonPropertyName("Containers")]
        public List<SdkContainer>? Containers { get; init; }

        [JsonPropertyName("Btr")]
        public SdkBtr? Btr { get; init; }

        [JsonPropertyName("ContainerTypes")]
        public List<SdkContainerType>? ContainerTypes { get; init; }

        // passed to the client as it is (MapFeatures.cs)
        [JsonPropertyName("Features")]
        public JsonElement? Features { get; init; }
    }

    private sealed record SdkContainerType
    {
        [JsonPropertyName("Template")] public string Template { get; init; } = "";
        [JsonPropertyName("New")] public bool New { get; init; }
        [JsonPropertyName("Name")] public string? Name { get; init; }
        [JsonPropertyName("ShortName")] public string? ShortName { get; init; }
        [JsonPropertyName("Description")] public string? Description { get; init; }
        [JsonPropertyName("Width")] public int Width { get; init; } = 4;
        [JsonPropertyName("Height")] public int Height { get; init; } = 4;
        [JsonPropertyName("BaseTemplate")] public string? BaseTemplate { get; init; }
        // Empty, CopyRetail, Custom, RetailPlusCustom
        [JsonPropertyName("Loot")] public string Loot { get; init; } = "Custom";
        [JsonPropertyName("RetailLootTemplate")] public string? RetailLootTemplate { get; init; }
        [JsonPropertyName("MinItems")] public int MinItems { get; init; } = 1;
        [JsonPropertyName("MaxItems")] public int MaxItems { get; init; } = 4;
        [JsonPropertyName("Items")] public List<SdkLootItem>? Items { get; init; }
    }

    private sealed record SdkContainer
    {
        [JsonPropertyName("Id")] public string Id { get; init; } = "";
        [JsonPropertyName("Template")] public string Template { get; init; } = "";
        [JsonPropertyName("Probability")] public double Probability { get; init; } = 1;
        [JsonPropertyName("AlwaysSpawn")] public bool AlwaysSpawn { get; init; } = true;
    }

    private sealed record SdkLootSpawn
    {
        [JsonPropertyName("Id")] public string Id { get; init; } = "";
        [JsonPropertyName("Position")] public Vector3? Position { get; init; }
        [JsonPropertyName("Rotation")] public Vector3? Rotation { get; init; }
        [JsonPropertyName("Probability")] public double Probability { get; init; }
        [JsonPropertyName("AlwaysSpawn")] public bool AlwaysSpawn { get; init; }
        [JsonPropertyName("UseGravity")] public bool UseGravity { get; init; }
        [JsonPropertyName("RandomRotation")] public bool RandomRotation { get; init; }
        [JsonPropertyName("Items")] public List<SdkLootItem>? Items { get; init; }
    }

    private sealed record SdkLootItem
    {
        [JsonPropertyName("Template")] public string Template { get; init; } = "";
        [JsonPropertyName("Weight")] public int Weight { get; init; } = 1;
        [JsonPropertyName("StackCount")] public int StackCount { get; init; } = 1;
    }

    private sealed record MapLocale
    {
        [JsonPropertyName("Name")]
        public string? Name { get; init; }

        [JsonPropertyName("Description")]
        public string? Description { get; init; }

        [JsonPropertyName("Keys")]
        public Dictionary<string, string>? Keys { get; init; }
    }
}
