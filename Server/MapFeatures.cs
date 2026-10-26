using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Utils;

namespace mizmii.CustomMapLoader;

// CustomMapLoader's own in-raid features, which Tarkov doesn't have, as each map's sdk_export.json "Features"
// lists them (written by the SDK's Build Map): forced transits, gear stripping, notifications, captions, health changes and effects.
// The client plugin reads them once from /custommaploader/features ({ "<map id>": <Features> }) and runs them in
// the raid; the server only passes them on.
[Injectable(InjectionType.Singleton)]
public class MapFeaturesData
{
    public Dictionary<string, JsonElement> Maps { get; } = new(StringComparer.OrdinalIgnoreCase);
}

[Injectable]
public class MapFeaturesRouter(JsonUtil jsonUtil, MapFeaturesData data) : StaticRouter(jsonUtil, [
    new RouteAction<EmptyRequestData>("/custommaploader/features",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(JsonSerializer.Serialize(data.Maps))),
])
{
}
