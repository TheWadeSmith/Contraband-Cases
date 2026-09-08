using System.Text.Json.Serialization;

namespace ContrabandCases.Server.Settlement;

public sealed class ManifestSnapshotEnvelope
{
    [JsonPropertyName("snapshot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ManifestSnapshotData? Snapshot { get; init; }
}

public sealed class ManifestCurrentStateEnvelope
{
    private ManifestCurrentStateEnvelope(
        ManifestSnapshotData? snapshot,
        ManifestOpeningOddsData? openingOdds,
        LegacyOpeningData? legacyOpening = null)
    {
        if ((snapshot is null ? 0 : 1) + (openingOdds is null ? 0 : 1) + (legacyOpening is null ? 0 : 1) != 1)
        {
            throw new ArgumentException(
                "Current state requires exactly one Manifest, opening-odds, or legacy-recovery payload.");
        }

        Snapshot = snapshot;
        OpeningOdds = openingOdds;
        LegacyOpening = legacyOpening;
    }

    [JsonPropertyName("snapshot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ManifestSnapshotData? Snapshot { get; }

    [JsonPropertyName("openingOdds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ManifestOpeningOddsData? OpeningOdds { get; }

    [JsonPropertyName("legacyOpening")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LegacyOpeningData? LegacyOpening { get; }

    public static ManifestCurrentStateEnvelope FromLegacyOpening(CaseOpeningRecord record) => new(null, null,
        new LegacyOpeningData
        {
            CaseId = record.CaseId.ToString(), RewardId = record.RewardId,
            Committed = record.Status == OpeningRecordStatus.Committed,
            DeliveredToMessenger = record.Status == OpeningRecordStatus.Committed && record.MailDelivery is not null
        });

    public static ManifestCurrentStateEnvelope FromSnapshot(ManifestSnapshotData snapshot) =>
        new(snapshot ?? throw new ArgumentNullException(nameof(snapshot)), null);

    public static ManifestCurrentStateEnvelope FromOpeningOdds(ManifestOpeningOddsData openingOdds) =>
        new(null, openingOdds ?? throw new ArgumentNullException(nameof(openingOdds)));
}

public sealed class LegacyOpeningData
{
    [JsonPropertyName("caseId")]
    public string CaseId { get; init; } = string.Empty;
    [JsonPropertyName("rewardId")]
    public string RewardId { get; init; } = string.Empty;
    [JsonPropertyName("committed")]
    public bool Committed { get; init; }
    [JsonPropertyName("deliveredToMessenger")]
    public bool DeliveredToMessenger { get; init; }
}

public sealed class ManifestOpeningOddsData
{
    [JsonPropertyName("premiumOdds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ManifestPremiumOddsData? PremiumOdds { get; init; }
    public const int CurrentProtocolVersion = 3;

    [JsonPropertyName("caseTemplateId")]
    public string CaseTemplateId { get; init; } = ContrabandCases.Shared.ModConstants.CaseTemplateId;

    [JsonPropertyName("casePrice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? CasePrice { get; init; }
    public const int MaximumFamilyCount = 128;
    public const int MaximumLotsPerFamily = 512;
    public const int MaximumTotalLotCount = 2_048;
    public const int MaximumRationalDigits = 1_024;
    public const string SelectionRuleValue =
        "UniformFamilyThenChaseCappedProviderLot";

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; } = CurrentProtocolVersion;

    [JsonPropertyName("catalogSnapshotId")]
    public string CatalogSnapshotId { get; init; } = string.Empty;

    [JsonPropertyName("offerCount")]
    public int OfferCount { get; init; }

    [JsonPropertyName("familyCount")]
    public int FamilyCount { get; init; }

    [JsonPropertyName("selectionRule")]
    public string SelectionRule { get; init; } = SelectionRuleValue;

    [JsonPropertyName("families")]
    public IReadOnlyList<ManifestOpeningFamilyOddsData> Families { get; init; } = [];
}

public sealed class ManifestPremiumOddsData
{
    [JsonPropertyName("epic")]
    public ManifestPremiumTierOddsData Epic { get; init; } = new();
    [JsonPropertyName("legendary")]
    public ManifestPremiumTierOddsData Legendary { get; init; } = new();
}

public sealed class ManifestPremiumTierOddsData
{
    [JsonPropertyName("chanceBasisPoints")]
    public int ChanceBasisPoints { get; init; }
    [JsonPropertyName("minimumUseValue")]
    public long MinimumUseValue { get; init; }
    [JsonPropertyName("lots")]
    public IReadOnlyList<ManifestOpeningLotOddsData> Lots { get; init; } = [];
}

public sealed class ManifestOpeningFamilyOddsData
{
    [JsonPropertyName("familyId")]
    public string FamilyId { get; init; } = string.Empty;

    [JsonPropertyName("familyLabel")]
    public string FamilyLabel { get; init; } = string.Empty;

    [JsonPropertyName("perSlotNumerator")]
    public string PerSlotNumerator { get; init; } = string.Empty;

    [JsonPropertyName("perSlotDenominator")]
    public string PerSlotDenominator { get; init; } = string.Empty;

    [JsonPropertyName("perSlotPercent")]
    public string PerSlotPercent { get; init; } = string.Empty;

    [JsonPropertyName("inclusionNumerator")]
    public string InclusionNumerator { get; init; } = string.Empty;

    [JsonPropertyName("inclusionDenominator")]
    public string InclusionDenominator { get; init; } = string.Empty;

    [JsonPropertyName("inclusionPercent")]
    public string InclusionPercent { get; init; } = string.Empty;

    [JsonPropertyName("lots")]
    public IReadOnlyList<ManifestOpeningLotOddsData> Lots { get; init; } = [];
}

public sealed class ManifestOpeningLotOddsData
{
    [JsonPropertyName("providerId")]
    public string ProviderId { get; init; } = string.Empty;

    [JsonPropertyName("providerLabel")]
    public string ProviderLabel { get; init; } = string.Empty;

    [JsonPropertyName("lotId")]
    public string LotId { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("grade")]
    public string Grade { get; init; } = string.Empty;

    [JsonPropertyName("anchorTemplateId")]
    public string AnchorTemplateId { get; init; } = string.Empty;

    [JsonPropertyName("conditionalNumerator")]
    public string ConditionalNumerator { get; init; } = string.Empty;

    [JsonPropertyName("conditionalDenominator")]
    public string ConditionalDenominator { get; init; } = string.Empty;

    [JsonPropertyName("conditionalPercent")]
    public string ConditionalPercent { get; init; } = string.Empty;
}

public sealed class ManifestSnapshotData
{
    [JsonPropertyName("deliveredToMessenger")]
    public bool DeliveredToMessenger { get; init; }

    [JsonPropertyName("openingTier")]
    public string OpeningTier { get; init; } = "Normal";

    [JsonPropertyName("premiumChoices")]
    public IReadOnlyList<ManifestLotData> PremiumChoices { get; init; } = [];

    public const int CurrentProtocolVersion = 3;

    [JsonPropertyName("caseTemplateId")]
    public string CaseTemplateId { get; init; } = ContrabandCases.Shared.ModConstants.CaseTemplateId;

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; } = CurrentProtocolVersion;

    [JsonPropertyName("manifestId")]
    public string ManifestId { get; init; } = string.Empty;

    [JsonPropertyName("phase")]
    public string Phase { get; init; } = string.Empty;

    [JsonPropertyName("currentOrdinal")]
    public int CurrentOrdinal { get; init; }

    [JsonPropertyName("lockedOrdinal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? LockedOrdinal { get; init; }

    [JsonPropertyName("relayStage")]
    public int RelayStage { get; init; }

    [JsonPropertyName("relayTerminal")]
    public bool RelayTerminal { get; init; }

    [JsonPropertyName("rarityLadderVersion")]
    public string RarityLadderVersion { get; init; } = string.Empty;

    [JsonPropertyName("catalogSnapshotId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? CatalogSnapshotId { get; init; }

    [JsonPropertyName("ticketCommitted")]
    public bool TicketCommitted { get; init; }

    [JsonPropertyName("recoveryCaseItemId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? RecoveryCaseItemId { get; init; }

    [JsonPropertyName("brokerFavor")]
    public int BrokerFavor { get; init; }

    [JsonPropertyName("brokerFavorMaximum")]
    public int BrokerFavorMaximum { get; init; }

    [JsonPropertyName("familySeals")]
    public IReadOnlyList<ManifestFamilySealData> FamilySeals { get; init; } = [];

    [JsonPropertyName("currentLot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ManifestLotData? CurrentLot { get; init; }

    [JsonPropertyName("availableActions")]
    public ManifestAvailableActionsData AvailableActions { get; init; } = new();

    [JsonPropertyName("relay")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ManifestRelayPropositionData? Relay { get; init; }

    [JsonPropertyName("latestReceipt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ManifestRelayReceiptData? LatestReceipt { get; init; }

    [JsonPropertyName("missingContentBlocked")]
    public bool MissingContentBlocked { get; init; }
}

public sealed class ManifestFamilySealData
{
    [JsonPropertyName("ordinal")]
    public int Ordinal { get; init; }

    [JsonPropertyName("familyId")]
    public string FamilyId { get; init; } = string.Empty;

    [JsonPropertyName("familyLabel")]
    public string FamilyLabel { get; init; } = string.Empty;

    [JsonPropertyName("riskBand")]
    public string RiskBand { get; init; } = "Mixed";

    [JsonPropertyName("revealed")]
    public bool Revealed { get; init; }

    [JsonPropertyName("burned")]
    public bool Burned { get; init; }

    [JsonPropertyName("locked")]
    public bool Locked { get; init; }
}

public sealed class ManifestLotData
{
    [JsonPropertyName("providerId")]
    public string ProviderId { get; init; } = string.Empty;

    [JsonPropertyName("providerLabel")]
    public string ProviderLabel { get; init; } = string.Empty;

    [JsonPropertyName("lotId")]
    public string LotId { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("purpose")]
    public string Purpose { get; init; } = string.Empty;

    [JsonPropertyName("familyId")]
    public string FamilyId { get; init; } = string.Empty;

    [JsonPropertyName("trackId")]
    public string TrackId { get; init; } = string.Empty;

    [JsonPropertyName("grade")]
    public string Grade { get; init; } = string.Empty;

    [JsonPropertyName("anchorTemplateId")]
    public string AnchorTemplateId { get; init; } = string.Empty;

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; init; } = string.Empty;

    [JsonPropertyName("liquidationValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? LiquidationValue { get; init; }

    [JsonPropertyName("useValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? UseValue { get; init; }

    [JsonPropertyName("traderResaleEstimate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TraderResaleEstimate { get; init; }

    [JsonPropertyName("footprintCells")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? FootprintCells { get; init; }

    [JsonPropertyName("contents")]
    public IReadOnlyList<ManifestLotContentData> Contents { get; init; } = [];
}

public sealed class ManifestLotContentData
{
    [JsonPropertyName("templateId")]
    public string TemplateId { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("quantity")]
    public int Quantity { get; init; }
}

public sealed class ManifestAvailableActionsData
{
    [JsonPropertyName("canLock")]
    public bool CanLock { get; init; }

    [JsonPropertyName("canBurn")]
    public bool CanBurn { get; init; }

    [JsonPropertyName("canClaim")]
    public bool CanClaim { get; init; }

    [JsonPropertyName("canRelay")]
    public bool CanRelay { get; init; }

    [JsonPropertyName("canForfeit")]
    public bool CanForfeit { get; init; }

    [JsonPropertyName("expectedOrdinal")]
    public int ExpectedOrdinal { get; init; }

    [JsonPropertyName("expectedPhase")]
    public string ExpectedPhase { get; init; } = string.Empty;

    [JsonPropertyName("expectedRelayStage")]
    public int ExpectedRelayStage { get; init; }
}

public sealed class ManifestRelayPropositionData
{
    [JsonPropertyName("stage")]
    public int Stage { get; init; }

    [JsonPropertyName("upgradePercent")]
    public int UpgradePercent { get; init; }

    [JsonPropertyName("sidegradePercent")]
    public int SidegradePercent { get; init; }

    [JsonPropertyName("confiscatePercent")]
    public int ConfiscatePercent { get; init; }

    [JsonPropertyName("favorBefore")]
    public int FavorBefore { get; init; }

    [JsonPropertyName("favorAfterOnLoss")]
    public int FavorAfterOnLoss { get; init; }

    [JsonPropertyName("guaranteeActive")]
    public bool GuaranteeActive { get; init; }

    [JsonPropertyName("keyCost")]
    public int KeyCost { get; init; } = 1;

    [JsonPropertyName("upgradeGrade")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? UpgradeGrade { get; init; }

    [JsonPropertyName("candidateValueMin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? CandidateValueMin { get; init; }

    [JsonPropertyName("candidateValueMax")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? CandidateValueMax { get; init; }

    [JsonPropertyName("sidegradeEndsChain")]
    public bool SidegradeEndsChain { get; init; } = true;

    [JsonPropertyName("terminalReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? TerminalReason { get; init; }
}

public sealed class ManifestRelayReceiptData
{
    [JsonPropertyName("stage")]
    public int Stage { get; init; }

    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = string.Empty;

    [JsonPropertyName("inputDisplayName")]
    public string InputDisplayName { get; init; } = string.Empty;

    [JsonPropertyName("inputGrade")]
    public string InputGrade { get; init; } = string.Empty;

    [JsonPropertyName("outputDisplayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? OutputDisplayName { get; init; }

    [JsonPropertyName("outputGrade")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? OutputGrade { get; init; }

    [JsonPropertyName("brokerFavorBefore")]
    public int BrokerFavorBefore { get; init; }

    [JsonPropertyName("brokerFavorAfter")]
    public int BrokerFavorAfter { get; init; }
}
