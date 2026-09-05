using ContrabandCases.Shared;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace ContrabandCases.Server;

public sealed record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = ModConstants.ModId;
    public string Name { get; init; } = ModConstants.ModName;
    public string Author { get; init; } = "Wade";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new(ModConstants.ModVersion);
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public bool HasPrepatcher { get; init; }
    public string License { get; init; } = "MIT";
}
