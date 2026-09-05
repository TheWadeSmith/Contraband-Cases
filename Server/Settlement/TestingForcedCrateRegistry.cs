using System.Collections.Concurrent;
using ContrabandCases.Shared;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;

namespace ContrabandCases.Server.Settlement;

/// <summary>
/// A debug/testing-only, in-memory, server-process-lifetime side table from a
/// granted BR-12 Relay Case's item ID to the <see cref="TestingCrateType"/>
/// pool its opening should be forced against.
///
/// This intentionally does not persist to the profile or any disk store: it
/// exists purely so the "Testing (Inventory Grants -- Server Opt-In)" MCM
/// tooling can force a specific pool for a case instance it just created,
/// with a case item ID as the only identifying handle (mirroring how the
/// opening flow already identifies "which case is this" by item ID
/// elsewhere in <c>ManifestSettlementService.OpenAsync</c>). A server
/// restart simply forgets all outstanding tags, and any case whose tag is
/// gone (missing, evicted, or from a prior server run) falls back to the
/// normal selection for that case template. Premium testing tags force a
/// tier only when enough qualifying packages exist; unavailable forced tiers
/// fail before consumption. Once an opening is saved, its choices and tier
/// live in the durable journal, independently of these temporary tags.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class TestingForcedCrateRegistry
{
    /// <summary>
    /// A generous bound on how many outstanding forced-pool tags this
    /// debug-only table tracks at once, so a long-running server session
    /// cannot grow it without limit. Eviction under pressure can only ever
    /// cause a very old, still-unopened testing case to silently fall back
    /// to normal random behavior -- never a crash.
    /// </summary>
    internal const int MaximumTrackedCases = 256;

    private readonly ConcurrentDictionary<MongoId, TestingCrateType> _forcedCrateByCaseId = new();

    public void SetForcedCrate(MongoId caseId, TestingCrateType crateType)
    {
        if (caseId.IsEmpty)
        {
            throw new ArgumentException("A case item ID is required.", nameof(caseId));
        }

        if (crateType == TestingCrateType.TrueRandom)
        {
            // Nothing to force -- do not grow the table for the common/default case.
            _forcedCrateByCaseId.TryRemove(caseId, out _);
            return;
        }

        if (_forcedCrateByCaseId.Count >= MaximumTrackedCases &&
            !_forcedCrateByCaseId.ContainsKey(caseId))
        {
            foreach (var oldest in _forcedCrateByCaseId.Keys)
            {
                if (_forcedCrateByCaseId.TryRemove(oldest, out _))
                {
                    break;
                }
            }
        }

        _forcedCrateByCaseId[caseId] = crateType;
    }

    public bool TryGetForcedCrate(MongoId caseId, out TestingCrateType crateType) =>
        _forcedCrateByCaseId.TryGetValue(caseId, out crateType);

    internal int Count => _forcedCrateByCaseId.Count;
}
