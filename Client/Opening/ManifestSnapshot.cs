using System.Collections.ObjectModel;
using System.Text;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;

namespace ContrabandCases.Client.Opening;

public enum ManifestRiskBand
{
    Mixed
}

public sealed class ManifestFamilySealSnapshot
{
    internal ManifestFamilySealSnapshot(
        int ordinal,
        string familyId,
        string familyLabel,
        ManifestRiskBand riskBand,
        bool revealed,
        bool burned,
        bool locked)
    {
        Ordinal = ordinal;
        FamilyId = familyId;
        FamilyLabel = familyLabel;
        RiskBand = riskBand;
        Revealed = revealed;
        Burned = burned;
        Locked = locked;
    }

    public int Ordinal { get; }

    public string FamilyId { get; }

    public string FamilyLabel { get; }

    public ManifestRiskBand RiskBand { get; }

    public bool Revealed { get; }

    public bool Burned { get; }

    public bool Locked { get; }
}

public sealed class ManifestLotContentSnapshot
{
    internal ManifestLotContentSnapshot(string templateId, string displayName, long quantity)
    {
        TemplateId = templateId;
        DisplayName = displayName;
        Quantity = quantity;
    }

    public string TemplateId { get; }

    public string DisplayName { get; }

    public long Quantity { get; }
}

public sealed class ManifestLotSnapshot
{
    internal ManifestLotSnapshot(
        string providerId,
        string providerLabel,
        string lotId,
        string displayName,
        string purpose,
        string familyId,
        string trackId,
        RewardRarity grade,
        string anchorTemplateId,
        string fingerprint,
        long? liquidationValue,
        long? useValue,
        int? footprintCells,
        IEnumerable<ManifestLotContentSnapshot> contents,
        long? traderResaleEstimate = null)
    {
        ProviderId = providerId;
        ProviderLabel = providerLabel;
        LotId = lotId;
        DisplayName = displayName;
        Purpose = purpose;
        FamilyId = familyId;
        TrackId = trackId;
        Grade = grade;
        AnchorTemplateId = anchorTemplateId;
        Fingerprint = fingerprint;
        LiquidationValue = liquidationValue;
        UseValue = useValue;
        FootprintCells = footprintCells;
        Contents = new ReadOnlyCollection<ManifestLotContentSnapshot>(contents.ToArray());
        TraderResaleEstimate = traderResaleEstimate;
    }

    public string ProviderId { get; }

    public string ProviderLabel { get; }

    public string LotId { get; }

    public string DisplayName { get; }

    public string Purpose { get; }

    public string FamilyId { get; }

    public string TrackId { get; }

    public RewardRarity Grade { get; }

    public string AnchorTemplateId { get; }

    public string Fingerprint { get; }

    public long? LiquidationValue { get; }

    public long? UseValue { get; }

    public long? TraderResaleEstimate { get; }

    public int? FootprintCells { get; }

    public IReadOnlyList<ManifestLotContentSnapshot> Contents { get; }
}

public sealed class ManifestAvailableActionsSnapshot
{
    internal ManifestAvailableActionsSnapshot(
        bool canLock,
        bool canBurn,
        bool canClaim,
        bool canRelay,
        bool canForfeit,
        int expectedOrdinal,
        ManifestPhase expectedPhase,
        int expectedRelayStage)
    {
        CanLock = canLock;
        CanBurn = canBurn;
        CanClaim = canClaim;
        CanRelay = canRelay;
        CanForfeit = canForfeit;
        ExpectedOrdinal = expectedOrdinal;
        ExpectedPhase = expectedPhase;
        ExpectedRelayStage = expectedRelayStage;
    }

    public bool CanLock { get; }

    public bool CanBurn { get; }

    public bool CanClaim { get; }

    public bool CanRelay { get; }

    public bool CanForfeit { get; }

    public int ExpectedOrdinal { get; }

    public ManifestPhase ExpectedPhase { get; }

    public int ExpectedRelayStage { get; }
}

public sealed class ManifestRelayPropositionSnapshot
{
    internal ManifestRelayPropositionSnapshot(
        int stage,
        int upgradePercent,
        int sidegradePercent,
        int confiscatePercent,
        int favorBefore,
        int favorAfterOnLoss,
        bool guaranteeActive,
        long keyCost,
        RewardRarity? upgradeGrade,
        long? candidateValueMin,
        long? candidateValueMax,
        bool sidegradeEndsChain,
        string? terminalReason)
    {
        Stage = stage;
        UpgradePercent = upgradePercent;
        SidegradePercent = sidegradePercent;
        ConfiscatePercent = confiscatePercent;
        FavorBefore = favorBefore;
        FavorAfterOnLoss = favorAfterOnLoss;
        GuaranteeActive = guaranteeActive;
        KeyCost = keyCost;
        UpgradeGrade = upgradeGrade;
        CandidateValueMin = candidateValueMin;
        CandidateValueMax = candidateValueMax;
        SidegradeEndsChain = sidegradeEndsChain;
        TerminalReason = terminalReason;
    }

    public int Stage { get; }

    public int UpgradePercent { get; }

    public int SidegradePercent { get; }

    public int ConfiscatePercent { get; }

    public int FavorBefore { get; }

    public int FavorAfterOnLoss { get; }

    public bool GuaranteeActive { get; }

    public long KeyCost { get; }

    public RewardRarity? UpgradeGrade { get; }

    public long? CandidateValueMin { get; }

    public long? CandidateValueMax { get; }

    public bool SidegradeEndsChain { get; }

    public string? TerminalReason { get; }
}

public sealed class ManifestRelayReceiptSnapshot
{
    internal ManifestRelayReceiptSnapshot(
        int stage,
        ManifestRelayResult outcome,
        string inputDisplayName,
        RewardRarity inputGrade,
        string? outputDisplayName,
        RewardRarity? outputGrade,
        int brokerFavorBefore,
        int brokerFavorAfter)
    {
        Stage = stage;
        Outcome = outcome;
        InputDisplayName = inputDisplayName;
        InputGrade = inputGrade;
        OutputDisplayName = outputDisplayName;
        OutputGrade = outputGrade;
        BrokerFavorBefore = brokerFavorBefore;
        BrokerFavorAfter = brokerFavorAfter;
    }

    public int Stage { get; }

    public ManifestRelayResult Outcome { get; }

    public string InputDisplayName { get; }

    public RewardRarity InputGrade { get; }

    public string? OutputDisplayName { get; }

    public RewardRarity? OutputGrade { get; }

    public int BrokerFavorBefore { get; }

    public int BrokerFavorAfter { get; }
}

public sealed class ManifestOpeningLotOddsSnapshot
{
    internal ManifestOpeningLotOddsSnapshot(
        string providerId,
        string providerLabel,
        string lotId,
        string displayName,
        RewardRarity grade,
        string anchorTemplateId,
        string conditionalNumerator,
        string conditionalDenominator,
        string conditionalPercent)
    {
        ProviderId = providerId;
        ProviderLabel = providerLabel;
        LotId = lotId;
        DisplayName = displayName;
        Grade = grade;
        AnchorTemplateId = anchorTemplateId;
        ConditionalNumerator = conditionalNumerator;
        ConditionalDenominator = conditionalDenominator;
        ConditionalPercent = conditionalPercent;
    }

    public string ProviderId { get; }

    public string ProviderLabel { get; }

    public string LotId { get; }

    public string DisplayName { get; }

    public RewardRarity Grade { get; }

    /// The item template this lot's disclosed odds entry should be
    /// illustrated with -- the same anchor template ID the corresponding
    /// ManifestLotSnapshot carries for the reveal/manifest display, so the
    /// pre-opening audit grid can request artwork through the identical
    /// Manifest sprite pipeline (ManifestSpritePlan) rather than a second,
    /// bespoke lookup.
    public string AnchorTemplateId { get; }

    public string ConditionalNumerator { get; }

    public string ConditionalDenominator { get; }

    public string ConditionalPercent { get; }
}

public sealed class ManifestOpeningFamilyOddsSnapshot
{
    internal ManifestOpeningFamilyOddsSnapshot(
        string familyId,
        string familyLabel,
        string perSlotNumerator,
        string perSlotDenominator,
        string perSlotPercent,
        string inclusionNumerator,
        string inclusionDenominator,
        string inclusionPercent,
        IEnumerable<ManifestOpeningLotOddsSnapshot> lots)
    {
        FamilyId = familyId;
        FamilyLabel = familyLabel;
        PerSlotNumerator = perSlotNumerator;
        PerSlotDenominator = perSlotDenominator;
        PerSlotPercent = perSlotPercent;
        InclusionNumerator = inclusionNumerator;
        InclusionDenominator = inclusionDenominator;
        InclusionPercent = inclusionPercent;
        Lots = new ReadOnlyCollection<ManifestOpeningLotOddsSnapshot>(lots.ToArray());
    }

    public string FamilyId { get; }

    public string FamilyLabel { get; }

    public string PerSlotNumerator { get; }

    public string PerSlotDenominator { get; }

    public string PerSlotPercent { get; }

    public string InclusionNumerator { get; }

    public string InclusionDenominator { get; }

    public string InclusionPercent { get; }

    public IReadOnlyList<ManifestOpeningLotOddsSnapshot> Lots { get; }
}

public sealed class ManifestOpeningOddsSnapshot
{
    public const int CurrentProtocolVersion = 3;
    public const string CanonicalSelectionRule = "UniformFamilyThenChaseCappedProviderLot";
    public const int RequiredOfferCount = 3;
    public const int MaximumFamilies = 128;
    public const int MaximumLotsPerFamily = 512;
    public const int MaximumTotalLots = 2_048;
    public const int MaximumRationalDigits = 1_024;

    internal ManifestOpeningOddsSnapshot(
        int protocolVersion,
        string catalogSnapshotId,
        int offerCount,
        int familyCount,
        string selectionRule,
        IEnumerable<ManifestOpeningFamilyOddsSnapshot> families,
        string caseTemplateId = ModConstants.CaseTemplateId,
        long? casePrice = null,
        ManifestPremiumOddsSnapshot? premiumOdds = null)
    {
        CaseTemplateId = CaseContracts.Require(caseTemplateId);
        CasePrice = casePrice;
        PremiumOdds = premiumOdds;
        ProtocolVersion = protocolVersion;
        CatalogSnapshotId = catalogSnapshotId;
        OfferCount = offerCount;
        FamilyCount = familyCount;
        SelectionRule = selectionRule;
        Families = new ReadOnlyCollection<ManifestOpeningFamilyOddsSnapshot>(families.ToArray());
    }

    public int ProtocolVersion { get; }

    public string CatalogSnapshotId { get; }

    public int OfferCount { get; }

    public int FamilyCount { get; }

    public string SelectionRule { get; }

    public IReadOnlyList<ManifestOpeningFamilyOddsSnapshot> Families { get; }

    public string CaseTemplateId { get; }

    public long? CasePrice { get; }

    public ManifestPremiumOddsSnapshot? PremiumOdds { get; }
}

public sealed class ManifestPremiumTierOddsSnapshot
{
    internal ManifestPremiumTierOddsSnapshot(int chanceBasisPoints, long minimumUseValue,
        IEnumerable<ManifestOpeningLotOddsSnapshot> lots)
    {
        ChanceBasisPoints = chanceBasisPoints;
        MinimumUseValue = minimumUseValue;
        Lots = new ReadOnlyCollection<ManifestOpeningLotOddsSnapshot>(lots.ToArray());
    }
    public int ChanceBasisPoints { get; }
    public long MinimumUseValue { get; }
    public IReadOnlyList<ManifestOpeningLotOddsSnapshot> Lots { get; }
}

public sealed class ManifestPremiumOddsSnapshot
{
    internal ManifestPremiumOddsSnapshot(ManifestPremiumTierOddsSnapshot epic, ManifestPremiumTierOddsSnapshot legendary)
    {
        Epic = epic;
        Legendary = legendary;
    }
    public ManifestPremiumTierOddsSnapshot Epic { get; }
    public ManifestPremiumTierOddsSnapshot Legendary { get; }
}

public sealed class ManifestCurrentState
{
    internal ManifestCurrentState(
        ManifestSnapshot? snapshot,
        ManifestOpeningOddsSnapshot? openingOdds,
        LegacyOpeningSnapshot? legacyOpening = null)
    {
        if ((snapshot is null ? 0 : 1) + (openingOdds is null ? 0 : 1) + (legacyOpening is null ? 0 : 1) != 1)
        {
            throw new ArgumentException(
                "Current Manifest state must contain exactly one snapshot or opening-odds disclosure.");
        }

        Snapshot = snapshot;
        OpeningOdds = openingOdds;
        LegacyOpening = legacyOpening;
    }

    public ManifestSnapshot? Snapshot { get; }

    public ManifestOpeningOddsSnapshot? OpeningOdds { get; }
    public LegacyOpeningSnapshot? LegacyOpening { get; }
}

public sealed class LegacyOpeningSnapshot
{
    internal LegacyOpeningSnapshot(string caseId, string rewardId, bool committed, bool deliveredToMessenger)
    {
        if (!RelaySnapshotEnvelope.IsMongoId(caseId) || deliveredToMessenger && !committed)
            throw new ManifestSnapshotException("The saved legacy opening has invalid delivery evidence.");
        CaseId = caseId;
        RewardId = ManifestProtocolValidation.RequireIdentifier(rewardId, nameof(rewardId));
        Committed = committed;
        DeliveredToMessenger = deliveredToMessenger;
    }
    public string CaseId { get; }
    public string RewardId { get; }
    public bool Committed { get; }
    public bool DeliveredToMessenger { get; }
}

public sealed class ManifestSnapshot
{
    public const int CurrentProtocolVersion = 3;

    internal ManifestSnapshot(
        int protocolVersion,
        string manifestId,
        ManifestPhase phase,
        int currentOrdinal,
        int? lockedOrdinal,
        int relayStage,
        bool relayTerminal,
        RarityLadderVersion rarityLadderVersion,
        string? catalogSnapshotId,
        bool ticketCommitted,
        int brokerFavor,
        int brokerFavorMaximum,
        IEnumerable<ManifestFamilySealSnapshot> familySeals,
        ManifestLotSnapshot? currentLot,
        ManifestAvailableActionsSnapshot availableActions,
        ManifestRelayPropositionSnapshot? relay,
        ManifestRelayReceiptSnapshot? latestReceipt,
        bool missingContentBlocked,
        string? recoveryCaseItemId,
        string caseTemplateId = ModConstants.CaseTemplateId,
        ManifestOpeningTier openingTier = ManifestOpeningTier.Normal,
        IEnumerable<ManifestLotSnapshot>? premiumChoices = null,
        bool deliveredToMessenger = false)
    {
        CaseTemplateId = CaseContracts.Require(caseTemplateId);
        OpeningTier = openingTier;
        PremiumChoices = new ReadOnlyCollection<ManifestLotSnapshot>((premiumChoices ?? []).ToArray());
        ProtocolVersion = protocolVersion;
        ManifestId = manifestId;
        Phase = phase;
        CurrentOrdinal = currentOrdinal;
        LockedOrdinal = lockedOrdinal;
        RelayStage = relayStage;
        RelayTerminal = relayTerminal;
        RarityLadderVersion = rarityLadderVersion;
        CatalogSnapshotId = catalogSnapshotId;
        TicketCommitted = ticketCommitted;
        BrokerFavor = brokerFavor;
        BrokerFavorMaximum = brokerFavorMaximum;
        FamilySeals = new ReadOnlyCollection<ManifestFamilySealSnapshot>(familySeals.ToArray());
        CurrentLot = currentLot;
        AvailableActions = availableActions;
        Relay = relay;
        LatestReceipt = latestReceipt;
        MissingContentBlocked = missingContentBlocked;
        RecoveryCaseItemId = recoveryCaseItemId;
        DeliveredToMessenger = deliveredToMessenger;
    }

    public int ProtocolVersion { get; }

    public string ManifestId { get; }

    public ManifestPhase Phase { get; }

    public int CurrentOrdinal { get; }

    public int? LockedOrdinal { get; }

    public int RelayStage { get; }

    public bool RelayTerminal { get; }

    public RarityLadderVersion RarityLadderVersion { get; }

    public string? CatalogSnapshotId { get; }

    public bool TicketCommitted { get; }

    public int BrokerFavor { get; }

    public int BrokerFavorMaximum { get; }

    public IReadOnlyList<ManifestFamilySealSnapshot> FamilySeals { get; }

    public ManifestLotSnapshot? CurrentLot { get; }

    public ManifestAvailableActionsSnapshot AvailableActions { get; }

    public ManifestRelayPropositionSnapshot? Relay { get; }

    public ManifestRelayReceiptSnapshot? LatestReceipt { get; }

    public bool MissingContentBlocked { get; }

    public bool DeliveredToMessenger { get; }

    public string? RecoveryCaseItemId { get; }

    public ManifestOpeningTier OpeningTier { get; }

    public bool IsPremium => OpeningTier != ManifestOpeningTier.Normal;

    public IReadOnlyList<ManifestLotSnapshot> PremiumChoices { get; }

    public string CaseTemplateId { get; }
}

internal static class ManifestProtocolValidation
{
    internal const int MaximumIdentifierBytes = 512;
    internal const int MaximumTextBytes = 4_096;
    internal const long MaximumMoneyValue = 1_000_000_000_000L;
    internal const int MaximumFootprintCells = 65_536;
    internal const int MaximumResponseCharacters = 4_194_304;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string RequireIdentifier(string? value, string parameterName) =>
        RequireString(value, parameterName, MaximumIdentifierBytes);

    internal static string RequireText(string? value, string parameterName) =>
        RequireString(value, parameterName, MaximumTextBytes);

    private static string RequireString(string? value, string parameterName, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException("A canonical non-empty string is required.", parameterName);
        }

        try
        {
            if (StrictUtf8.GetByteCount(value) > maximumBytes)
            {
                throw new ArgumentException("The string exceeds the supported size.", parameterName);
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("The string contains invalid Unicode scalar text.", parameterName, exception);
        }

        return value;
    }
}
