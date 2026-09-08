using System.Collections.Concurrent;
using ContrabandCases.Server.Content;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Servers;

namespace ContrabandCases.Server.Loot;

/// <summary>
/// Observes newly retained PMC key and case instances after normal raid settlement.
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
            var supply = ReadSupply(profile);
            if (supply is not null)
                _starts[profile] = new Sample(supply.Keys.ToHashSet(), content.RequireConfig().TestingInventoryGrantsEnabled);
        }
        catch (Exception exception)
        {
            // Optional diagnostics cannot break raid entry or settlement.
            logger.Warning($"[{ModConstants.ModName}] Supply raid audit skipped start: {exception.GetType().Name}");
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
            var supply = ReadSupply(profile);
            if (supply is null) return;
            logger.Info($"[{ModConstants.ModName}] " + DescribeRetained(sample.Items, supply,
                sample.Testing || content.RequireConfig().TestingInventoryGrantsEnabled));
        }
        catch (Exception exception)
        {
            logger.Warning($"[{ModConstants.ModName}] Supply raid audit skipped completion: {exception.GetType().Name}");
        }
    }

    internal void Forget(MongoId profile) => _starts.TryRemove(profile, out _);

    private Dictionary<MongoId, string>? ReadSupply(MongoId profile) => saves.GetProfile(profile)?.CharacterData?.PmcData?
        .Inventory?.Items?.Where(item => item.Template == ModConstants.KeyTemplateId || CaseContracts.IsCase(item.Template.ToString()))
        .ToDictionary(item => item.Id, item => item.Template.ToString());

    internal static string DescribeRetained(IReadOnlySet<MongoId> before,
        IReadOnlyDictionary<MongoId, string> after, bool testing)
    {
        int Count(string template) => CountNewKeys(before, after.Where(item => item.Value == template).Select(item => item.Key));
        var cases = string.Join(", ", CaseContracts.Templates.Select(template => $"{CaseContracts.ShortName(template)}={Count(template)}"));
        return $"Supply raid audit: PMC completed; new retained key instances={Count(ModConstants.KeyTemplateId)}; " +
            $"new retained case instances=[{cases}]; testing grants enabled={testing}. " +
            "Includes zero-find completions; excludes carried items, mail/BTR delivery and scav transfers. Retained items, not all spawned or encountered loot.";
    }

    internal static int CountNewKeys(IReadOnlySet<MongoId> before, IEnumerable<MongoId> after) =>
        after.Distinct().Count(id => !before.Contains(id));

    private sealed record Sample(HashSet<MongoId> Items, bool Testing);
}
