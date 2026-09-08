using System.Collections.ObjectModel;
using ContrabandCases.Shared.Catalog;

namespace ContrabandCases.Shared.Relay;

public enum RelayOutcome
{
    Secured,
    RarityUpgrade,
    SameRaritySidegrade,
    Confiscated
}

/// <summary>
/// Identifies the published rarity ladder that authenticated a Relay transaction.
/// Older journals predate the Uncommon tier and must continue to validate against
/// their original four-tier transition rather than being reinterpreted on load.
/// </summary>
public enum RarityLadderVersion
{
    LegacyFourTier,
    FiveTier
}

public readonly record struct RelayOdds(int UpgradePercent, int SidegradePercent, int ConfiscatePercent)
{
    public int TotalPercent => checked(UpgradePercent + SidegradePercent + ConfiscatePercent);
}

public static class RelayRules
{
    public const int MaximumStage = 3;
    public const int MaximumRecoveryMeter = 3;

    public static RelayOdds GetOdds(int stage) => stage switch
    {
        1 => new RelayOdds(55, 30, 15),
        2 => new RelayOdds(45, 25, 30),
        3 => new RelayOdds(35, 20, 45),
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Relay stage must be between one and three.")
    };

    public static RelayOutcome SelectOutcome(int stage, int recoveryMeter, double unitValue)
    {
        ValidateMeter(recoveryMeter);
        if (!double.IsFinite(unitValue) || unitValue < 0d || unitValue >= 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(unitValue));
        }

        var odds = GetOdds(stage);
        if (recoveryMeter == MaximumRecoveryMeter)
        {
            return RelayOutcome.RarityUpgrade;
        }

        var percentile = unitValue * 100d;
        if (percentile < odds.UpgradePercent)
        {
            return RelayOutcome.RarityUpgrade;
        }

        return percentile < odds.UpgradePercent + odds.SidegradePercent
            ? RelayOutcome.SameRaritySidegrade
            : RelayOutcome.Confiscated;
    }

    public static bool CanRelay(
        RewardRarity rarity,
        int stage,
        RarityLadderVersion rarityLadderVersion = RarityLadderVersion.FiveTier) =>
        IsSupportedRarity(rarity, rarityLadderVersion) &&
        rarity != RewardRarity.BlackLabel &&
        stage is >= 1 and <= MaximumStage;

    public static RewardRarity GetUpgradeRarity(
        RewardRarity rarity,
        RarityLadderVersion rarityLadderVersion = RarityLadderVersion.FiveTier)
    {
        ValidateLadderVersion(rarityLadderVersion, nameof(rarityLadderVersion));
        return rarityLadderVersion switch
        {
            RarityLadderVersion.LegacyFourTier => rarity switch
            {
                RewardRarity.ScavGrade => RewardRarity.Contractor,
                RewardRarity.Contractor => RewardRarity.Restricted,
                RewardRarity.Restricted => RewardRarity.BlackLabel,
                RewardRarity.BlackLabel => throw new InvalidOperationException("Legendary rewards are terminal."),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(rarity),
                    rarity,
                    "Reward rarity is not recognized by the legacy Relay ladder.")
            },
            RarityLadderVersion.FiveTier => rarity switch
            {
                RewardRarity.ScavGrade => RewardRarity.Uncommon,
                RewardRarity.Uncommon => RewardRarity.Contractor,
                RewardRarity.Contractor => RewardRarity.Restricted,
                RewardRarity.Restricted => RewardRarity.BlackLabel,
                RewardRarity.BlackLabel => throw new InvalidOperationException("Legendary rewards are terminal."),
                _ => throw new ArgumentOutOfRangeException(nameof(rarity), rarity, "Reward rarity is not recognized.")
            },
            _ => throw new ArgumentOutOfRangeException(nameof(rarityLadderVersion))
        };
    }

    public static bool IsSupportedRarity(
        RewardRarity rarity,
        RarityLadderVersion rarityLadderVersion)
    {
        if (!IsDefinedLadderVersion(rarityLadderVersion))
        {
            return false;
        }

        return rarityLadderVersion == RarityLadderVersion.LegacyFourTier
            ? rarity is RewardRarity.ScavGrade or
                RewardRarity.Contractor or
                RewardRarity.Restricted or
                RewardRarity.BlackLabel
            : Enum.IsDefined(typeof(RewardRarity), rarity);
    }

    public static RewardRarity CatalogRarityForLadder(RewardRarity grade, RarityLadderVersion version)
    {
        ValidateLadderVersion(version, nameof(version));
        if (!Enum.IsDefined(typeof(RewardRarity), grade)) throw new ArgumentOutOfRangeException(nameof(grade));
        // The five-tier release split only the former <75k Common band.
        // Old commitments keep that original grade, never a rewritten reward.
        return version == RarityLadderVersion.LegacyFourTier && grade == RewardRarity.Uncommon
            ? RewardRarity.ScavGrade : grade;
    }

    public static bool IsDefinedLadderVersion(RarityLadderVersion rarityLadderVersion) =>
        Enum.IsDefined(typeof(RarityLadderVersion), rarityLadderVersion);

    public static void ValidateLadderVersion(
        RarityLadderVersion rarityLadderVersion,
        string parameterName)
    {
        if (!IsDefinedLadderVersion(rarityLadderVersion))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                rarityLadderVersion,
                "Relay rarity ladder version is not recognized.");
        }
    }

    public static int MeterAfterConfiscation(int recoveryMeter, int failedStage)
    {
        ValidateMeter(recoveryMeter);
        _ = GetOdds(failedStage);
        return Math.Min(MaximumRecoveryMeter, checked(recoveryMeter + failedStage));
    }

    public static int MeterAfterOutcome(int recoveryMeter, int stage, RelayOutcome outcome)
    {
        ValidateMeter(recoveryMeter);
        _ = GetOdds(stage);
        if (recoveryMeter == MaximumRecoveryMeter)
        {
            if (outcome != RelayOutcome.RarityUpgrade)
            {
                throw new InvalidOperationException("A full recovery meter must settle as a rarity upgrade.");
            }

            return 0;
        }

        return outcome == RelayOutcome.Confiscated
            ? MeterAfterConfiscation(recoveryMeter, stage)
            : recoveryMeter;
    }

    private static void ValidateMeter(int recoveryMeter)
    {
        if (recoveryMeter is < 0 or > MaximumRecoveryMeter)
        {
            throw new ArgumentOutOfRangeException(nameof(recoveryMeter));
        }
    }
}

public sealed record RelayEconomyReward(
    string Id,
    RewardRarity Rarity,
    long HandbookValue,
    double OpeningWeight);

public sealed record RelayEconomyOpportunity(
    string RewardId,
    RewardRarity Rarity,
    int Stage,
    long StakeValue,
    decimal ExpectedOutputValue,
    decimal ExpectedNetValue,
    RelayOdds Odds);

public sealed class RelayEconomyReport
{
    internal RelayEconomyReport(long keyPrice, IEnumerable<RelayEconomyOpportunity> opportunities)
    {
        KeyPrice = keyPrice;
        Opportunities = new ReadOnlyCollection<RelayEconomyOpportunity>(opportunities.ToArray());
    }

    public long KeyPrice { get; }
    public IReadOnlyList<RelayEconomyOpportunity> Opportunities { get; }
    public bool IncludesRecoveryGuarantee => false;
    public string Disclosure =>
        "Relay analysis is separate from base-case pricing and does not include the persistent recovery guarantee.";
}

public static class RelayEconomyAnalyzer
{
    public static RelayEconomyReport Analyze(IReadOnlyList<RelayEconomyReward> rewards, long keyPrice)
    {
        if (rewards is null)
        {
            throw new ArgumentNullException(nameof(rewards));
        }
        if (rewards.Count == 0)
        {
            throw new ArgumentException("A non-empty reward catalog is required.", nameof(rewards));
        }
        if (keyPrice <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keyPrice));
        }

        var duplicate = rewards.GroupBy(reward => reward.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() != 1);
        if (duplicate is not null || rewards.Any(reward =>
                string.IsNullOrWhiteSpace(reward.Id) ||
                reward.HandbookValue < 0 ||
                !double.IsFinite(reward.OpeningWeight) ||
                reward.OpeningWeight <= 0d ||
                !Enum.IsDefined(typeof(RewardRarity), reward.Rarity)))
        {
            throw new ArgumentException("Relay economy rewards must be unique validated catalog entries.", nameof(rewards));
        }

        var opportunities = new List<RelayEconomyOpportunity>();
        foreach (var reward in rewards.Where(reward => reward.Rarity != RewardRarity.BlackLabel))
        {
            var upgrades = rewards.Where(candidate => candidate.Rarity == RelayRules.GetUpgradeRarity(reward.Rarity)).ToArray();
            var sidegrades = rewards.Where(candidate => candidate.Rarity == reward.Rarity && candidate.Id != reward.Id).ToArray();
            if (upgrades.Length == 0 || sidegrades.Length == 0)
            {
                throw new InvalidOperationException($"Reward '{reward.Id}' has no complete Relay target pool.");
            }

            var upgradeValue = WeightedAverage(upgrades);
            var sidegradeValue = WeightedAverage(sidegrades);
            for (var stage = 1; stage <= RelayRules.MaximumStage; stage++)
            {
                var odds = RelayRules.GetOdds(stage);
                var expectedOutput =
                    upgradeValue * odds.UpgradePercent / 100m +
                    sidegradeValue * odds.SidegradePercent / 100m;
                opportunities.Add(new RelayEconomyOpportunity(
                    reward.Id,
                    reward.Rarity,
                    stage,
                    reward.HandbookValue,
                    expectedOutput,
                    expectedOutput - reward.HandbookValue - keyPrice,
                    odds));
            }
        }

        return new RelayEconomyReport(keyPrice, opportunities);
    }

    private static decimal WeightedAverage(IReadOnlyCollection<RelayEconomyReward> rewards)
    {
        var totalWeight = rewards.Sum(reward => (decimal)reward.OpeningWeight);
        return rewards.Sum(reward => (decimal)reward.OpeningWeight * reward.HandbookValue) / totalWeight;
    }
}

public sealed class RelayReceipt
{
    public bool DeliveredToMessenger { get; init; }
    public string Action { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string StakeRootId { get; init; } = string.Empty;
    public int Stage { get; init; }
    public string PreviousRewardId { get; init; } = string.Empty;
    public string PreviousRarity { get; init; } = string.Empty;
    public string? RewardId { get; init; }
    public string? RewardRootId { get; init; }
    public string? Rarity { get; init; }
    public string RarityLadderVersion { get; init; } = string.Empty;
    public int RecoveryMeter { get; init; }
    public int RecoveryMeterMaximum { get; init; } = RelayRules.MaximumRecoveryMeter;
    public bool GuaranteedUpgrade { get; init; }
    public bool Terminal { get; init; }
    public bool Replay { get; init; }
}

public sealed class RelayStatus
{
    public string StakeRootId { get; init; } = string.Empty;
    public string RewardId { get; init; } = string.Empty;
    public string Rarity { get; init; } = string.Empty;
    public string RarityLadderVersion { get; init; } = string.Empty;
    public int Stage { get; init; }
    public int RecoveryMeter { get; init; }
    public int RecoveryMeterMaximum { get; init; } = RelayRules.MaximumRecoveryMeter;
    public bool RelayEligible { get; init; }
    public bool GuaranteedUpgradeReady { get; init; }
    public bool Terminal { get; init; }
    public string? TerminalOutcome { get; init; }
    public bool SettlementPending { get; init; }
    public string? PendingAction { get; init; }
}

public sealed class RelayCandidate
{
    public string RewardId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Rarity { get; init; } = string.Empty;
    public long HandbookValue { get; init; }
}

public sealed class RelayPublishedStage
{
    public int Stage { get; init; }
    public int UpgradePercent { get; init; }
    public int SidegradePercent { get; init; }
    public int ConfiscatePercent { get; init; }
}

public sealed class RelaySnapshot
{
    public RelayStatus Status { get; init; } = new RelayStatus();
    public RelayReceipt? LatestReceipt { get; init; }
    public IReadOnlyList<RelayPublishedStage> PublishedLadder { get; init; } = Array.Empty<RelayPublishedStage>();
    public IReadOnlyList<RelayCandidate> UpgradeCandidates { get; init; } = Array.Empty<RelayCandidate>();
    public IReadOnlyList<RelayCandidate> SidegradeCandidates { get; init; } = Array.Empty<RelayCandidate>();
}

public sealed class RelayPendingDiscovery
{
    public RelaySnapshot? PendingSnapshot { get; init; }
}
