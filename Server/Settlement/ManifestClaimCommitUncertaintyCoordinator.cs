using System.Collections.Concurrent;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;

namespace ContrabandCases.Server.Settlement;

/// <summary>Process-lifetime quarantine after an indeterminate profile save.</summary>
[Injectable(InjectionType.Singleton)]
public sealed class ManifestClaimCommitUncertaintyCoordinator
{
    internal static ManifestClaimCommitUncertaintyCoordinator Process { get; } = new();
    private readonly ConcurrentDictionary<MongoId, byte> _uncertainProfiles = new();

    public void ThrowIfUncertain(MongoId profileId)
    {
        if (_uncertainProfiles.ContainsKey(profileId))
        {
            throw new InvalidOperationException(
                "A previous reward settlement save has an uncertain result; restart the server before retrying.");
        }
    }

    public void MarkUncertain(MongoId profileId) => _uncertainProfiles.TryAdd(profileId, 0);
}
