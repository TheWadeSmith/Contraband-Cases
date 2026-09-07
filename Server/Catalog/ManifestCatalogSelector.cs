using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Security.Cryptography;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;

namespace ContrabandCases.Server.Catalog;

/// <summary>
/// Pure selection over one immutable, finalized catalog snapshot. The selected
/// semantic lots and their canonical draws are ready to persist in a manifest.
/// </summary>
public sealed class ManifestCatalogSelector
{
    private readonly Func<long> _nextUnitNumerator;

    public ManifestCatalogSelector()
        : this(NextCryptographicUnitNumerator)
    {
    }

    internal ManifestCatalogSelector(Func<long> nextUnitNumerator)
    {
        _nextUnitNumerator = nextUnitNumerator ??
            throw new ArgumentNullException(nameof(nextUnitNumerator));
    }

    /// <param name="catalog">The finalized, frozen catalog snapshot.</param>
    /// <param name="allowedProviderIds">
    /// Debug/testing-only forced-pool override (see
    /// <c>TestingForcedCrateRegistry</c> and <c>TestingCrateProviderMap</c>).
    /// When <see langword="null"/> (the default, and the only value any
    /// normal Mechanic-purchased or untagged case ever passes), selection is
    /// completely unchanged from the original full blended behavior. When
    /// non-null and non-empty, each offer's per-family provider draw is
    /// restricted to providers in this set <em>whenever that family has at
    /// least one lot from an allowed provider</em>; a family with no lots
    /// from the allowed set falls back to the normal unrestricted draw for
    /// that one family rather than throwing, because the existing "at least
    /// three distinct families" invariant already guarantees every offer's
    /// family must resolve to a real provider draw, and a forced pool that
    /// does not span a given family (e.g. the single-family "vault" pool)
    /// has nothing else it could correctly do for that offer.
    /// </param>
    public IReadOnlyList<ManifestOfferSnapshot> CreateOffers(
        CargoCatalogSnapshot catalog,
        IReadOnlySet<string>? allowedProviderIds = null)
    {
        EnsureOpeningEnabled(catalog);

        var canonicalLots = ManifestSelectionMath.CanonicalLots(catalog.FreshOpeningLots);
        var remainingFamilies = canonicalLots
            .Select(catalog.SelectionFamily)
            .Distinct()
            .OrderBy(family => family)
            .ToList();
        if (remainingFamilies.Count < catalog.OfferCount)
        {
            throw new CargoCatalogValidationException(
                "An opening-enabled catalog must contain at least three distinct families.");
        }

        var offers = new ManifestOfferSnapshot[catalog.OfferCount];
        for (var ordinal = 1; ordinal <= catalog.OfferCount; ordinal++)
        {
            var evidence = new CanonicalRngEvidence(
                ManifestRngPurpose.OfferSelection,
                ordinal,
                _nextUnitNumerator());
            var point = UnitPoint.FromCanonical(evidence.UnitNumerator);
            var family = SelectUniform(remainingFamilies, point, out point);
            remainingFamilies.Remove(family);

            var familyLots = canonicalLots
                .Where(lot => catalog.SelectionFamily(lot).Equals(family))
                .ToArray();
            var pool = ManifestOpeningPool.Create(catalog, familyLots, allowedProviderIds);
            var lot = SelectWeighted(pool.Lots, pool.Weights, point);

            offers[ordinal - 1] = new ManifestOfferSnapshot(
                ordinal,
                lot.Evaluation.Grade,
                lot.Identity,
                lot.Forest,
                lot.Fingerprint,
                evidence);
        }

        return new ReadOnlyCollection<ManifestOfferSnapshot>(offers);
    }

    public IReadOnlyList<ManifestOfferSnapshot> CreatePremiumOffers(
        CargoCatalogSnapshot catalog, ManifestOpeningTier tier)
    {
        EnsureOpeningEnabled(catalog);
        if (!ManifestPremiumPool.IsAvailable(catalog, tier))
            throw new InvalidOperationException("This case does not have three qualifying premium packages. Nothing was spent.");
        var remaining = ManifestPremiumPool.Candidates(catalog, tier).ToList();
        var offers = new List<ManifestOfferSnapshot>(ManifestRecord.OfferCount);
        for (var ordinal = 1; ordinal <= ManifestRecord.OfferCount; ordinal++)
        {
            var evidence = new CanonicalRngEvidence(ManifestRngPurpose.OfferSelection, ordinal, _nextUnitNumerator());
            var pool = ManifestOpeningPool.Create(catalog, remaining);
            var lot = SelectWeighted(pool.Lots, pool.Weights, UnitPoint.FromCanonical(evidence.UnitNumerator));
            remaining.Remove(lot);
            offers.Add(new ManifestOfferSnapshot(ordinal, lot.Evaluation.Grade, lot.Identity,
                lot.Forest, lot.Fingerprint, evidence));
        }
        return offers.AsReadOnly();
    }

    public IReadOnlyList<ManifestRelayCandidateSnapshot> CreateRelayCandidates(
        CargoCatalogSnapshot catalog,
        ManifestEntitlementSnapshot entitlement,
        RarityLadderVersion rarityLadderVersion = RarityLadderVersion.FiveTier,
        bool allowOpeningChases = false)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(entitlement);
        if (catalog.CaseTemplateId == CaseContracts.CashCache || entitlement.Rarity == RewardRarity.BlackLabel)
        {
            return Array.Empty<ManifestRelayCandidateSnapshot>();
        }

        var upgradeRarity = RelayRules.GetUpgradeRarity(entitlement.Rarity, rarityLadderVersion);
        var eligible = ManifestSelectionMath.CanonicalLots(catalog.Lots)
            // Cheap historical entitlements cannot enter the higher-stakes
            // generation, even when recovery allows retired/chase candidates.
            .Where(lot => ShipmentEconomy.IsShipment(lot.Identity.LotId) ==
                ShipmentEconomy.IsShipment(entitlement.Identity.LotId))
            .Where(lot => lot.Identity.TrackId.Equals(entitlement.Identity.TrackId))
            .Where(lot => !SameSemanticIdentity(lot, entitlement))
            // Fresh Relay/Favor pools exclude chases and retired component kits.
            // The legacy flag permits either only when settlement has established
            // a necessary continuation for an older frozen catalog.
            .Where(lot => allowOpeningChases ||
                (ManifestOpeningPool.IsFreshEligible(lot) && !ManifestOpeningPool.IsChase(lot)))
            .ToArray();
        EnsureDistinctSemanticLots(eligible);

        var upgrades = eligible
            .Where(lot => RelayRules.CatalogRarityForLadder(lot.Evaluation.Grade, rarityLadderVersion) == upgradeRarity)
            .Select(lot => ToRelayCandidate(ManifestRelayResult.Upgrade, lot, rarityLadderVersion))
            .ToArray();
        var sidegrades = eligible
            .Where(lot => RelayRules.CatalogRarityForLadder(lot.Evaluation.Grade, rarityLadderVersion) == entitlement.Rarity)
            .Select(lot => ToRelayCandidate(ManifestRelayResult.Sidegrade, lot, rarityLadderVersion))
            .ToArray();
        if (upgrades.Length == 0 || sidegrades.Length == 0)
        {
            return Array.Empty<ManifestRelayCandidateSnapshot>();
        }

        var selected = new List<ManifestRelayCandidateSnapshot>(
            Math.Min(
                ManifestRecord.MaximumRelayCandidateCount,
                upgrades.Length + sidegrades.Length));
        var nodeCount = 0;
        if (!TryAddBounded(selected, upgrades[0], ref nodeCount) ||
            !TryAddBounded(selected, sidegrades[0], ref nodeCount))
        {
            return Array.Empty<ManifestRelayCandidateSnapshot>();
        }

        var maximumLength = Math.Max(upgrades.Length, sidegrades.Length);
        for (var index = 1;
             index < maximumLength && selected.Count < ManifestRecord.MaximumRelayCandidateCount;
             index++)
        {
            if (index < upgrades.Length)
            {
                TryAddBounded(selected, upgrades[index], ref nodeCount);
            }
            if (index < sidegrades.Length &&
                selected.Count < ManifestRecord.MaximumRelayCandidateCount)
            {
                TryAddBounded(selected, sidegrades[index], ref nodeCount);
            }
        }

        return new ReadOnlyCollection<ManifestRelayCandidateSnapshot>(
            ManifestRecordValidation.CanonicalOrder(selected).ToArray());
    }

    public IReadOnlyList<ManifestRelayCandidateSnapshot> CreateRelayCandidatesForStage(
        CargoCatalogSnapshot catalog,
        ManifestEntitlementSnapshot entitlement,
        int stage,
        RarityLadderVersion rarityLadderVersion = RarityLadderVersion.FiveTier,
        bool allowOpeningChases = false)
    {
        _ = RelayRules.GetOdds(stage);
        var candidates = CreateRelayCandidates(catalog, entitlement, rarityLadderVersion, allowOpeningChases);
        if (candidates.Count == 0 || stage == RelayRules.MaximumStage) return candidates;
        var sidegrades = candidates.Where(c => c.TargetResult == ManifestRelayResult.Sidegrade).ToArray();
        var upgrades = candidates.Where(c => c.TargetResult == ManifestRelayResult.Upgrade)
            .Where(c => c.Rarity == RewardRarity.BlackLabel || CreateRelayCandidates(catalog,
                new ManifestEntitlementSnapshot(c.Rarity, c.Identity, c.Forest, c.Fingerprint), rarityLadderVersion, allowOpeningChases).Count > 0)
            .ToArray();
        return sidegrades.Length == 0 || upgrades.Length == 0 ? [] :
            ManifestRecordValidation.CanonicalOrder(upgrades.Concat(sidegrades)).ToArray();
    }

    private static void EnsureOpeningEnabled(CargoCatalogSnapshot? catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.OpeningEnabled)
        {
            throw new InvalidOperationException(
                catalog.OpeningDisabledReason ?? "The frozen cargo catalog does not permit Manifest openings.");
        }
    }

    private static ManifestRelayCandidateSnapshot ToRelayCandidate(
        ManifestRelayResult result,
        ResolvedCargoLot lot,
        RarityLadderVersion rarityLadderVersion) =>
        new(
            result,
            RelayRules.CatalogRarityForLadder(lot.Evaluation.Grade, rarityLadderVersion),
            lot.Identity,
            lot.Forest,
            lot.Fingerprint);

    private static bool TryAddBounded(
        ICollection<ManifestRelayCandidateSnapshot> selected,
        ManifestRelayCandidateSnapshot candidate,
        ref int nodeCount)
    {
        if (selected.Count >= ManifestRecord.MaximumRelayCandidateCount ||
            candidate.Forest.Nodes.Count > ManifestRecord.MaximumFrozenRelayNodeCount - nodeCount)
        {
            return false;
        }

        selected.Add(candidate);
        nodeCount += candidate.Forest.Nodes.Count;
        return true;
    }

    private static bool SameSemanticIdentity(
        ResolvedCargoLot lot,
        ManifestEntitlementSnapshot entitlement) =>
        string.Equals(
            lot.Identity.ProviderId,
            entitlement.Identity.ProviderId,
            StringComparison.Ordinal) &&
        string.Equals(
            lot.Identity.LotId,
            entitlement.Identity.LotId,
            StringComparison.Ordinal) &&
        lot.Fingerprint.Equals(entitlement.Fingerprint);

    private static void EnsureDistinctSemanticLots(IEnumerable<ResolvedCargoLot> lots)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lot in lots)
        {
            var key = string.Concat(
                lot.Identity.ProviderId,
                "\0",
                lot.Identity.LotId,
                "\0",
                lot.Fingerprint.Sha256Hex);
            if (!seen.Add(key))
            {
                throw new CargoCatalogValidationException(
                    "Frozen catalog contains duplicate semantic Relay candidates.");
            }
        }
    }

    private static T SelectUniform<T>(
        IReadOnlyList<T> candidates,
        UnitPoint point,
        out UnitPoint residual)
    {
        if (candidates.Count == 0)
        {
            throw new CargoCatalogValidationException("Uniform selection requires at least one candidate.");
        }

        var scaled = point.Numerator * candidates.Count;
        var index = checked((int)(scaled / point.Denominator));
        residual = new UnitPoint(scaled % point.Denominator, point.Denominator);
        return candidates[index];
    }

    private static T SelectWeighted<T>(
        IReadOnlyList<T> candidates,
        ExactWeightSet weights,
        UnitPoint point)
    {
        if (candidates.Count == 0)
        {
            throw new CargoCatalogValidationException("Weighted selection requires at least one candidate.");
        }

        var position = point.Numerator * weights.Total;
        var cumulative = BigInteger.Zero;
        for (var index = 0; index < candidates.Count; index++)
        {
            cumulative += weights[index];
            if (position < point.Denominator * cumulative)
            {
                return candidates[index];
            }
        }

        throw new InvalidOperationException("A canonical weighted draw did not select a candidate.");
    }

    private static long NextCryptographicUnitNumerator()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(bytes);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return checked((long)(value & ((ulong)CanonicalRngEvidence.UnitDenominator - 1UL)));
    }

    private readonly struct UnitPoint
    {
        public UnitPoint(BigInteger numerator, BigInteger denominator)
        {
            if (denominator <= BigInteger.Zero ||
                numerator < BigInteger.Zero ||
                numerator >= denominator)
            {
                throw new ArgumentOutOfRangeException(nameof(numerator));
            }

            var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
            Numerator = numerator / divisor;
            Denominator = denominator / divisor;
        }

        public BigInteger Numerator { get; }

        public BigInteger Denominator { get; }

        public static UnitPoint FromCanonical(long numerator) =>
            new(numerator, CanonicalRngEvidence.UnitDenominator);
    }
}
