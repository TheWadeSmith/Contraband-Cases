using System.Collections.Concurrent;
using ContrabandCases.Shared.Relay;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Services.Modding;

namespace ContrabandCases.Server.Settlement;

[Injectable(InjectionType.Singleton)]
public sealed class SptCaseJournal : ICaseOpeningJournalStore
{
    private static readonly ConcurrentDictionary<MongoId, byte> PoisonedProfiles = new();
    private readonly Func<MongoId, CancellationToken, Task<SptCaseJournalDocument?>> _loadDocument;
    private readonly Func<MongoId, SptCaseJournalDocument, CancellationToken, Task> _saveDocument;

    public const string JournalKey = "contraband-cases-openings-v1";

    public SptCaseJournal(ProfileDataService profileDataService)
    {
        ArgumentNullException.ThrowIfNull(profileDataService);
        _loadDocument = (profileId, cancellationToken) =>
            profileDataService.GetProfileDataAsync<SptCaseJournalDocument>(
                profileId,
                JournalKey,
                cancellationToken);
        _saveDocument = (profileId, document, cancellationToken) =>
            profileDataService.SaveProfileDataAsync(
                profileId,
                JournalKey,
                document,
                cancellationToken);
    }

    internal SptCaseJournal(
        Func<MongoId, CancellationToken, Task<SptCaseJournalDocument?>> loadDocument,
        Func<MongoId, SptCaseJournalDocument, CancellationToken, Task> saveDocument)
    {
        _loadDocument = loadDocument ?? throw new ArgumentNullException(nameof(loadDocument));
        _saveDocument = saveDocument ?? throw new ArgumentNullException(nameof(saveDocument));
    }

    public async ValueTask<CaseOpeningJournal> LoadAsync(
        MongoId profileId,
        CancellationToken cancellationToken)
    {
        EnsureProfileIsHealthy(profileId);
        var document = await _loadDocument(profileId, cancellationToken).ConfigureAwait(false);
        return document is null ? new CaseOpeningJournal() : FromDocument(document);
    }

    public async ValueTask SaveAsync(
        MongoId profileId,
        CaseOpeningJournal journal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var document = ToDocument(journal);
        EnsureProfileIsHealthy(profileId);
        try
        {
            await _saveDocument(profileId, document, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            PoisonedProfiles.TryAdd(profileId, 0);
            throw;
        }
    }

    private static void EnsureProfileIsHealthy(MongoId profileId)
    {
        if (PoisonedProfiles.ContainsKey(profileId))
        {
            throw new InvalidOperationException(
                $"Contraband Cases journal persistence is unavailable for profile '{profileId}' until the server restarts.");
        }
    }

    internal static SptCaseJournalDocument ToDocument(CaseOpeningJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return new SptCaseJournalDocument
        {
            SchemaVersion = SptCaseJournalDocument.CurrentSchemaVersion,
            RecoveryMeter = journal.RecoveryMeter,
            LegacySecuredStakeRoots = journal.LegacySecuredStakeRoots.ToList(),
            Records = journal.Records.Select(record => new SptCaseOpeningDocument
            {
                CaseId = record.CaseId,
                KeyId = record.KeyId,
                RewardId = record.RewardId,
                RewardItems = record.RewardItems.ToList(),
                PreparedAtUtc = record.PreparedAtUtc,
                Status = record.Status,
                CommittedAtUtc = record.CommittedAtUtc,
                RarityLadderVersion = record.RarityLadderVersion,
                MailDelivery = record.MailDelivery is null ? null : SptManifestJournalMapper.ToDocument(record.MailDelivery)
            }).ToList(),
            RelayRecords = journal.RelayRecords.Select(record => new SptRelaySettlementDocument
            {
                OriginCaseId = record.OriginCaseId,
                StakeRootId = record.StakeRootId,
                InputItemIds = record.InputItemIds.ToList(),
                InputItems = record.InputItems.ToList(),
                InputRewardId = record.InputRewardId,
                InputRarity = record.InputRarity,
                Stage = record.Stage,
                Action = record.Action,
                KeyId = record.KeyId,
                Outcome = record.Outcome,
                OutputRewardId = record.OutputRewardId,
                OutputItems = record.OutputItems.ToList(),
                MeterBefore = record.MeterBefore,
                MeterAfter = record.MeterAfter,
                GuaranteedUpgrade = record.GuaranteedUpgrade,
                ProfileCommitStarted = record.ProfileCommitStarted,
                PreparedAtUtc = record.PreparedAtUtc,
                Status = record.Status,
                CommittedAtUtc = record.CommittedAtUtc,
                RarityLadderVersion = record.RarityLadderVersion,
                MailDelivery = record.MailDelivery is null ? null : SptManifestJournalMapper.ToDocument(record.MailDelivery)
            }).ToList(),
            ActiveManifest = journal.ActiveManifest is null
                ? null
                : SptManifestJournalMapper.ToDocument(journal.ActiveManifest),
            ManifestReceipts = SptManifestJournalMapper.ToDocuments(journal.ManifestReceipts),
            ManifestClaimGrants = SptManifestJournalMapper.ToClaimGrantDocuments(journal.ManifestClaimGrants)
        };
    }

    internal static CaseOpeningJournal FromDocument(SptCaseJournalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion is < 0 or > SptCaseJournalDocument.CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"Unsupported Contraband Cases journal schema '{document.SchemaVersion}'.");
        }
        if (document.SchemaVersion <= 2 &&
            (document.ActiveManifest is not null ||
             document.ManifestReceipts is { Count: > 0 } ||
             document.ManifestClaimGrants is { Count: > 0 }))
        {
            throw new InvalidOperationException(
                "A legacy Contraband Cases journal cannot contain Manifest schema data.");
        }
        if (document.SchemaVersion == 3 &&
            (document.ActiveManifest is not null ||
             document.ManifestReceipts is { Count: > 0 } ||
             document.ManifestClaimGrants is { Count: > 0 }))
        {
            throw new InvalidOperationException(
                "A schema-three Contraband Cases journal cannot safely restore Manifest data.");
        }
        if (document.SchemaVersion == 4 && document.ActiveManifest is not null)
        {
            throw new InvalidOperationException(
                "A schema-four Contraband Cases journal cannot safely restore an active Manifest Claim.");
        }
        if (document.Records is null)
        {
            throw new InvalidOperationException("The Contraband Cases journal has no records collection.");
        }

        if (document.SchemaVersion < 8 && (document.Records.Any(r => r?.MailDelivery is not null) ||
            document.RelayRecords?.Any(r => r?.MailDelivery is not null) == true))
            throw new InvalidOperationException("An older journal schema cannot contain legacy Messenger delivery evidence.");
        if (document.SchemaVersion < 7 &&
            (document.ActiveManifest?.ClaimPrepared?.Delivery == ClaimDeliveryKind.Messenger ||
             document.ManifestClaimGrants?.Any(g => g?.ClaimPayload?.Delivery == ClaimDeliveryKind.Messenger) == true))
            throw new InvalidOperationException("An older journal schema cannot contain Messenger claim destinations.");

        var fallbackRarityLadderVersion = document.SchemaVersion <= 5
            ? RarityLadderVersion.LegacyFourTier
            : RarityLadderVersion.FiveTier;
        var requirePersistedRarityLadderVersion = document.SchemaVersion >= 6;
        var openings = document.Records.Select(record =>
        {
            if (record is null || record.RewardItems is null)
            {
                throw new InvalidOperationException("The Contraband Cases journal contains an invalid record.");
            }

            return new CaseOpeningRecord(
                record.CaseId,
                record.KeyId,
                record.RewardId,
                record.RewardItems,
                record.PreparedAtUtc,
                record.Status,
                record.CommittedAtUtc,
                ResolveRarityLadderVersion(
                    record.RarityLadderVersion,
                    fallbackRarityLadderVersion,
                    requirePersistedRarityLadderVersion,
                    "opening rarity ladder version"),
                record.MailDelivery is null ? null : SptManifestJournalMapper.FromDocument(record.MailDelivery));
        });

        var relayDocuments = document.SchemaVersion <= 1
            ? document.RelayRecords ?? []
            : document.RelayRecords ?? throw new InvalidOperationException(
                "The Contraband Cases journal has no Relay records collection.");
        var relays = relayDocuments.Select(record =>
        {
            if (record is null || record.InputItemIds is null || record.InputItems is null || record.OutputItems is null)
            {
                throw new InvalidOperationException("The Contraband Cases journal contains an invalid Relay record.");
            }

            return new RelaySettlementRecord(
                record.OriginCaseId,
                record.StakeRootId,
                record.InputItemIds,
                record.InputRewardId,
                record.InputRarity,
                record.Stage,
                record.Action,
                record.KeyId,
                record.Outcome,
                record.OutputRewardId,
                record.OutputItems,
                record.MeterBefore,
                record.MeterAfter,
                record.GuaranteedUpgrade,
                record.ProfileCommitStarted,
                record.PreparedAtUtc,
                record.Status,
                record.CommittedAtUtc,
                record.InputItems,
                ResolveRarityLadderVersion(
                    record.RarityLadderVersion,
                    fallbackRarityLadderVersion,
                    requirePersistedRarityLadderVersion,
                    "Relay record rarity ladder version"),
                record.MailDelivery is null ? null : SptManifestJournalMapper.FromDocument(record.MailDelivery));
        });

        var openingList = openings.ToList();
        var legacySecuredRoots = document.SchemaVersion <= 1
            ? openingList
                .Where(record => record.Status == OpeningRecordStatus.Committed)
                .Select(record => record.RewardRootId)
                .OfType<MongoId>()
                .ToList()
            : document.LegacySecuredStakeRoots ?? throw new InvalidOperationException(
                "The Contraband Cases journal has no legacy secured-stake collection.");

        ManifestRecord? activeManifest = null;
        IReadOnlyList<ManifestTerminalReceipt> manifestReceipts = [];
        IReadOnlyList<ManifestClaimGrantRecord> manifestClaimGrants = [];
        if (document.SchemaVersion >= 4)
        {
            activeManifest = document.ActiveManifest is null
                ? null
                : SptManifestJournalMapper.FromDocument(
                    document.ActiveManifest,
                    fallbackRarityLadderVersion,
                    requirePersistedRarityLadderVersion);
            manifestReceipts = SptManifestJournalMapper.FromDocuments(
                document.ManifestReceipts,
                fallbackRarityLadderVersion,
                requirePersistedRarityLadderVersion);
            manifestClaimGrants = SptManifestJournalMapper.FromClaimGrantDocuments(
                document.ManifestClaimGrants);
        }

        return new CaseOpeningJournal(
            openingList,
            relays,
            document.RecoveryMeter,
            legacySecuredRoots,
            activeManifest,
            manifestReceipts,
            manifestClaimGrants);
    }

    private static RarityLadderVersion ResolveRarityLadderVersion(
        RarityLadderVersion? persistedValue,
        RarityLadderVersion fallbackValue,
        bool requirePersistedValue,
        string field)
    {
        RelayRules.ValidateLadderVersion(fallbackValue, nameof(fallbackValue));
        if (persistedValue is null)
        {
            if (requirePersistedValue)
            {
                throw new InvalidOperationException($"The Contraband Cases journal has no {field}.");
            }

            return fallbackValue;
        }

        RelayRules.ValidateLadderVersion(persistedValue.Value, field);
        if (!requirePersistedValue && persistedValue.Value != fallbackValue)
        {
            throw new InvalidOperationException($"The Contraband Cases journal has an invalid {field}.");
        }

        return persistedValue.Value;
    }
}

internal sealed class SptCaseJournalDocument
{
    public const int CurrentSchemaVersion = 8;

    public int SchemaVersion { get; set; }
    public int RecoveryMeter { get; set; }
    public List<MongoId>? LegacySecuredStakeRoots { get; set; } = [];
    public List<SptCaseOpeningDocument>? Records { get; set; } = [];
    public List<SptRelaySettlementDocument>? RelayRecords { get; set; } = [];
    public SptManifestRecordDocument? ActiveManifest { get; set; }
    public List<SptManifestTerminalReceiptDocument>? ManifestReceipts { get; set; } = [];
    public List<SptManifestClaimGrantDocument>? ManifestClaimGrants { get; set; } = [];
}

internal sealed class SptCaseOpeningDocument
{
    public SptManifestClaimPreparedDocument? MailDelivery { get; set; }
    public MongoId CaseId { get; set; }
    public MongoId KeyId { get; set; }
    public string RewardId { get; set; } = string.Empty;
    public List<Item>? RewardItems { get; set; } = [];
    public DateTimeOffset PreparedAtUtc { get; set; }
    public OpeningRecordStatus Status { get; set; }
    public DateTimeOffset? CommittedAtUtc { get; set; }
    public RarityLadderVersion? RarityLadderVersion { get; set; }
}

internal sealed class SptRelaySettlementDocument
{
    public SptManifestClaimPreparedDocument? MailDelivery { get; set; }
    public MongoId OriginCaseId { get; set; }
    public MongoId StakeRootId { get; set; }
    public List<MongoId>? InputItemIds { get; set; } = [];
    public List<Item>? InputItems { get; set; } = [];
    public string InputRewardId { get; set; } = string.Empty;
    public ContrabandCases.Shared.Catalog.RewardRarity InputRarity { get; set; }
    public int Stage { get; set; }
    public RelayRecordAction Action { get; set; }
    public MongoId? KeyId { get; set; }
    public ContrabandCases.Shared.Relay.RelayOutcome Outcome { get; set; }
    public string? OutputRewardId { get; set; }
    public List<Item>? OutputItems { get; set; } = [];
    public int MeterBefore { get; set; }
    public int MeterAfter { get; set; }
    public bool GuaranteedUpgrade { get; set; }
    public bool ProfileCommitStarted { get; set; }
    public DateTimeOffset PreparedAtUtc { get; set; }
    public RelayRecordStatus Status { get; set; }
    public DateTimeOffset? CommittedAtUtc { get; set; }
    public RarityLadderVersion? RarityLadderVersion { get; set; }
}
