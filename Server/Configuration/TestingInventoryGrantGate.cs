using System.Collections.Concurrent;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;

namespace ContrabandCases.Server.Configuration;

[Injectable(InjectionType.Singleton)]
public sealed class TestingInventoryGrantGate
{
    private readonly ConcurrentDictionary<MongoId, byte> _uncertainProfiles = new();
    private bool _initialized;
    private bool _enabled;

    public void Initialize(bool enabled)
    {
        if (_initialized)
        {
            throw new InvalidOperationException("The testing inventory grant gate was already initialized.");
        }

        _enabled = enabled;
        _initialized = true;
    }

    public void RequireEnabled()
    {
        if (!_initialized || !_enabled)
        {
            throw new InvalidOperationException(
                "Testing inventory grants are disabled by the server configuration.");
        }
    }

    internal void RequireGrantAllowed(MongoId profileId)
    {
        RequireEnabled();
        if (_uncertainProfiles.ContainsKey(profileId))
        {
            throw new InvalidOperationException(
                $"Testing inventory grants are blocked for profile '{profileId}' until the server restarts " +
                "because its previous profile save had an uncertain result.");
        }
    }

    internal void MarkCommitUncertain(MongoId profileId) =>
        _uncertainProfiles.TryAdd(profileId, 0);
}
