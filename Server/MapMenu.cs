using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Utils;

namespace mizmii.CustomMapLoader;

// How maps show on the map select screen, written by the SDK's Server Data Editor and map packs:
//
//   maps/<id>/menu.json      pack, time selection, location icon, preview picture, loading banners, connections
//   maps/<id>/images/        the pictures menu.json names
//   packs/<pack>/pack.json   name, order, background picture, default location icon (files next to it)
//
// Pictures are served like the game's own banners, under /files/custommaploader/... (SPT's ImageRouter).
// The client plugin reads the rest from /custommaploader/menu: which maps sit on which pack page, which
// let players pick the time, and the pictures to put on the screen.
[Injectable(InjectionType.Singleton)]
public class MapMenuData
{
    public MenuResponse Response { get; } = new();
}

[Injectable]
public class MapMenuRouter(JsonUtil jsonUtil, MapMenuData data) : StaticRouter(jsonUtil, [
    new RouteAction<EmptyRequestData>("/custommaploader/menu",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(jsonUtil.Serialize(data.Response)!)),
])
{
}

public sealed record MapMenuFile
{
    [JsonPropertyName("Pack")] public string? Pack { get; init; }
    [JsonPropertyName("TimeSelection")] public bool TimeSelection { get; init; }
    [JsonPropertyName("Icon")] public string? Icon { get; init; }
    [JsonPropertyName("Preview")] public string? Preview { get; init; }
    [JsonPropertyName("Banners")] public List<MapMenuBanner>? Banners { get; init; }
    [JsonPropertyName("Connections")] public List<string>? Connections { get; init; }
}

public sealed record MapMenuBanner
{
    [JsonPropertyName("Image")] public string? Image { get; init; }
    [JsonPropertyName("Name")] public string? Name { get; init; }
    [JsonPropertyName("Description")] public string? Description { get; init; }
}

public sealed record MapPackFile
{
    [JsonPropertyName("Name")] public string? Name { get; init; }
    [JsonPropertyName("Order")] public int Order { get; init; }
    [JsonPropertyName("Background")] public string? Background { get; init; }
    [JsonPropertyName("Icon")] public string? Icon { get; init; }
}

public sealed record MenuResponse
{
    [JsonPropertyName("Packs")] public List<MenuPack> Packs { get; init; } = [];
    [JsonPropertyName("Maps")] public Dictionary<string, MenuMap> Maps { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record MenuPack
{
    [JsonPropertyName("Id")] public string Id { get; init; } = "";
    [JsonPropertyName("Name")] public string Name { get; init; } = "";
    [JsonPropertyName("Order")] public int Order { get; init; }
    [JsonPropertyName("Background")] public string? Background { get; init; }
    [JsonPropertyName("Icon")] public string? Icon { get; init; }
}

public sealed record MenuMap
{
    [JsonPropertyName("Pack")] public string? Pack { get; init; }
    [JsonPropertyName("TimeSelection")] public bool TimeSelection { get; init; }
    [JsonPropertyName("Icon")] public string? Icon { get; init; }
}

internal static class MenuImages
{
    // Registers <file> for the game's image loader and returns its path under /files/
    // (banners take it as pic.path; the client plugin asks for "/files/" + it). The router's
    // keys are lowercase and extension-less, like SPT's own images.
    public static string? Register(ImageRouter router, string folder, string urlFolder, string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return null;
        var path = Path.Combine(folder, file);
        if (!File.Exists(path))
            return null;
        var relative = $"custommaploader/{urlFolder}/{file}";
        router.AddRoute("/files/" + Path.ChangeExtension(relative, null), path);
        return relative;
    }
}
