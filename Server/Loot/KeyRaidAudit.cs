using System.Collections.Concurrent;
using ContrabandCases.Server.Content;
using ContrabandCases.Shared;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Servers;

namespace ContrabandCases.Server.Loot;

/// <summary>
/// Observes newly retained PMC key instances after normal raid settlement.
/// Never changes profiles, loot or odds; incomplete/transit/scav samples are omitted.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class KeyRaidAudit(SaveServer saves, ContrabandContentState content,
    ISptLogger<KeyRaidAudit> logger)
{
    private readonly ConcurrentDictionary<MongoId, Sample> _starts = new();

    internal void Begin(MongoId profile, StartLocalRaidRequestData request, bool transit)
    {
        Forget(profile);
        if (transit || !string.Equals(request.PlayerSide, "pmc", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var keys = ReadKeys(profile);
            if (keys is not null)
                _starts[profile] = new Sample(keys, content.RequireConfig().TestingInventoryGrantsEnabled);
        }
        catch (Exception exception)
        {
            // Optional diagnostics cannot break raid entry or settlement.
            logger.Warning($"[{ModConstants.ModName}] Key raid audit skipped start: {exception.GetType().Name}");
        }
    }

    internal void End(MongoId profile, EndLocalRaidRequestData request, bool transit)
    {
        // RaidSessionState already serializes and authenticates the lifecycle.
        // SPT generates a different ServerId in its start response; the incoming
        // start request's ID must not be used to correlate the completion.
        if (!_starts.TryRemove(profile, out var sample) || transit || request.Results is null) return;
        try
        {
            var keys = ReadKeys(profile);
            if (keys is null) return;
            var retained = CountNewKeys(sample.Keys, keys);
            logger.Info($"[{ModConstants.ModName}] Key raid audit: PMC completed; new retained key instances={retained}; " +
                $"testing grants enabled={sample.Testing}. Excludes carried keys, mail/BTR delivery and scav transfers.");
        }
        catch (Exception exception)
        {
            logger.Warning($"[{ModConstants.ModName}] Key raid audit skipped completion: {exception.GetType().Name}");
        }
    }

    internal void Forget(MongoId profile) => _starts.TryRemove(profile, out _);

    private HashSet<MongoId>? ReadKeys(MongoId profile) => saves.GetProfile(profile)?.CharacterData?.PmcData?
        .Inventory?.Items?.Where(item => item.Template == ModConstants.KeyTemplateId)
        .Select(item => item.Id).ToHashSet();

    internal static int CountNewKeys(IReadOnlySet<MongoId> before, IEnumerable<MongoId> after) =>
        after.Distinct().Count(id => !before.Contains(id));

    private sealed record Sample(HashSet<MongoId> Keys, bool Testing);
}
