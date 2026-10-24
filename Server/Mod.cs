using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace mizmii.CustomMapLoader;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.mizmii.custommaploader";
    public string Name { get; init; } = "CustomMapLoader";
    public string Author { get; init; } = "mizmii";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.5.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.6");
    public bool HasPrepatcher { get; init; } = false;
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; } = "";
    public string License { get; init; } = "Unlicense";
}
