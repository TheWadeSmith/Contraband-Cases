using System.Security.Cryptography;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace ContrabandCases.Server.Settlement;

public enum ManifestClaimResultKind
{
    Granted,
    NoSpace
}

public sealed record ManifestClaimResult(
    ManifestClaimResultKind Kind,
    ItemEventRouterResponse Response,
    bool Replay);

public sealed class ManifestSettlementService
{
    private readonly ICaseOpeningJournalStore _journalStore;
    private readonly IManifestClaimInventory _inventory;
    private readonly IManifestTicketInventory? _ticketInventory;
    private readonly IManifestRelayKeyInventory? _relayKeyInventory;
    private readonly IProfileCommitter _committer;
    private readonly ProfileLockPool _lockPool;
    private readonly RaidSessionState _raidSessions;
    private readonly CargoLotMaterializer _materializer;
    private readonly CatalogSnapshotCoordinator? _catalogCoordinator;
    private readonly ManifestCatalogSelector? _catalogSelector;
    private readonly TestingForcedCrateRegistry? _forcedCrateRegistry;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<long> _nextUnitNumerator;
    private readonly Func<string> _manifestIdFactory;
    private readonly ManifestClaimCommitUncertaintyCoordinator _uncertaintyCoordinator;

    public ManifestSettlementService(
        ICaseOpeningJournalStore journalStore,
        IManifestClaimInventory inventory,
        IProfileCommitter committer,
        ProfileLockPool lockPool,
        RaidSessionState raidSessions,
        CargoLotMaterializer materializer,
        Func<DateTimeOffset>? utcNow = null,
        ManifestClaimCommitUncertaintyCoordinator? uncertaintyCoordinator = null,
        IManifestTicketInventory? ticketInventory = null,
        IManifestRelayKeyInventory? relayKeyInventory = null,
        CatalogSnapshotCoordinator? catalogCoordinator = null,
        ManifestCatalogSelector? catalogSelector = null,
        Func<long>? nextUnitNumerator = null,
        Func<string>? manifestIdFactory = null,
        TestingForcedCrateRegistry? forcedCrateRegistry = null)
    {
        _journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _committer = committer ?? throw new ArgumentNullException(nameof(committer));
        _lockPool = lockPool ?? throw new ArgumentNullException(nameof(lockPool));
        _raidSessions = raidSessions ?? throw new ArgumentNullException(nameof(raidSessions));
        _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
        _ticketInventory = ticketInventory;
        _relayKeyInventory = relayKeyInventory;
        _catalogCoordinator = catalogCoordinator;
        _catalogSelector = catalogSelector;
        _forcedCrateRegistry = forcedCrateRegistry;
        _uncertaintyCoordinator = uncertaintyCoordinator ?? ManifestClaimCommitUncertaintyCoordinator.Process;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _nextUnitNumerator = nextUnitNumerator ?? NextUnitNumerator;
        _manifestIdFactory = manifestIdFactory ?? CreateManifestId;
    }

    public async Task<ItemEventRouterResponse> OpenAsync(
        OpeningContext context,
        MongoId caseId,
        CancellationToken cancellationToken,
        string? expectedCatalogSnapshotId = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (caseId.IsEmpty)
        {
            throw new ArgumentException("A case item ID is required.", nameof(caseId));
        }

        var ticketInventory = _ticketInventory ??
            throw new InvalidOperationException("Manifest ticket inventory support is unavailable.");
        var coordinator = _catalogCoordinator ??
            throw new InvalidOperationException("The finalized Manifest catalog is unavailable.");
        var selector = _catalogSelector ??
            throw new InvalidOperationException("Manifest catalog selection is unavailable.");

        await using var profileLock = await _lockPool
            .AcquireAsync(context.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        _raidSessions.RequireLobby(context.ProfileId);
        _uncertaintyCoordinator.ThrowIfUncertain(context.ProfileId);

        var journal = await _journalStore
            .LoadAsync(context.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (journal.PreparedOpening is not null || journal.PreparedRelay is not null)
        {
            throw new InvalidOperationException(
                "A legacy settlement transaction must be resumed before opening a Manifest.");
        }

        var active = journal.ActiveManifest;
        if (active is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ticket = ticketInventory.PrepareTicket(context, caseId, UtcNow());
            var catalog = coordinator.GetCaseSnapshot(ticket.CaseTemplateId);
            if ((expectedCatalogSnapshotId is not null || ticket.CaseTemplateId != ModConstants.CaseTemplateId) &&
                !string.Equals(expectedCatalogSnapshotId, catalog.SnapshotId, StringComparison.Ordinal))
                throw new InvalidOperationException("The case catalog changed. Review its current odds before opening.");
            ManifestOpeningQuality? quality = null;
            if (ticket.CaseTemplateId != CaseContracts.CashCache)
            {
                var epic = ManifestPremiumPool.IsAvailable(catalog, ManifestOpeningTier.Epic);
                var legendary = ManifestPremiumPool.IsAvailable(catalog, ManifestOpeningTier.Legendary);
                var draw = _nextUnitNumerator();
                var tier = ManifestOpeningTierRules.Select(draw, epic, legendary);
                var forced = _forcedCrateRegistry?.TryGetForcedCrate(caseId, out var testingType) == true
                    ? TestingCrateTypeCodec.PremiumTier(testingType) : null;
                if (forced == ManifestOpeningTier.Epic && !epic ||
                    forced == ManifestOpeningTier.Legendary && !legendary)
                    throw new InvalidOperationException(
                        $"{forced} testing is unavailable for this case: not enough qualifying packages are installed. " +
                        "Your case and key were not consumed. Spawn a different testing case or restore its reward packs.");
                quality = new ManifestOpeningQuality(forced ?? tier, draw, epic, legendary, forced.HasValue,
                    singlePrize: (forced ?? tier) == ManifestOpeningTier.Legendary);
                ticket = ticket.WithOpeningQuality(quality);
            }
            var offers = quality?.IsPremium == true
                ? selector.CreatePremiumOffers(catalog, quality.Tier)
                : selector.CreateOffers(catalog, ticket.CaseTemplateId == ModConstants.CaseTemplateId
                    ? ResolveForcedProviderIds(caseId) : null);
            var manifestId = AllocateManifestId(journal);
            active = new ManifestRecord(
                manifestId,
                catalog.SnapshotId,
                ManifestCommitmentEvidence.CreateWithRandomNonce(
                    manifestId,
                    catalog.SnapshotId,
                    offers),
                ManifestFlowState.PrepareTicket(),
                ticket,
                offers,
                decisions: null,
                entitlement: null,
                relayCandidates: null,
                relayHistory: null,
                journal.BrokerFavor,
                rarityLadderVersion: RarityLadderVersion.FiveTier);
            journal.SetActiveManifest(active);
            await _journalStore
                .SaveAsync(context.ProfileId, journal, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (active.Ticket.CaseId != caseId)
        {
            throw new InvalidOperationException(
                "A different Manifest is already active for this profile.");
        }

        if (active.FlowState.Phase != ManifestPhase.TicketPrepared)
        {
            RequireCommittedTicketReplay(context, active, ticketInventory);
            return context.Response;
        }

        return await CompletePreparedTicketAsync(
                context,
                journal,
                active,
                ticketInventory,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Debug/testing-only forced-pool lookup. Returns <see langword="null"/>
    /// (meaning "no restriction, use the normal full blended catalog") unless
    /// a registry is configured *and* this exact case ID was tagged with a
    /// non-default <see cref="TestingCrateType"/> by the testing inventory
    /// grant flow. A case bought from Mechanic, or any case not created by
    /// that debug tool, is never present in the registry and therefore always
    /// resolves exactly as it did before this feature existed.
    /// </summary>
    private IReadOnlySet<string>? ResolveForcedProviderIds(MongoId caseId)
    {
        if (_forcedCrateRegistry is null ||
            !_forcedCrateRegistry.TryGetForcedCrate(caseId, out var crateType))
        {
            return null;
        }

        return TestingCrateProviderMap.GetAllowedProviderIds(crateType);
    }

    public async Task<ItemEventRouterResponse> DecideOfferAsync(
        OpeningContext context,
        string manifestId,
        int expectedOrdinal,
        ManifestOfferDecision decision,
        CancellationToken cancellationToken,
        int? selectedOrdinal = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ManifestRecordValidation.RequireIdentifier(manifestId, nameof(manifestId));
        if (expectedOrdinal is < 1 or > ManifestRecord.OfferCount)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedOrdinal));
        }
        if (decision is not (ManifestOfferDecision.Lock or ManifestOfferDecision.Burn))
        {
            throw new ArgumentOutOfRangeException(nameof(decision));
        }

        await using var profileLock = await _lockPool
            .AcquireAsync(context.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        _raidSessions.RequireLobby(context.ProfileId);
        _uncertaintyCoordinator.ThrowIfUncertain(context.ProfileId);
        var journal = await _journalStore
            .LoadAsync(context.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        var active = RequireActiveManifest(journal, manifestId);
        RejectLegacyPreparedTransaction(journal);
        var premium = active.Ticket.OpeningQuality?.IsPremium == true;
        if (premium && (selectedOrdinal is null or < 1 or > 3 || decision != ManifestOfferDecision.Lock) ||
            !premium && selectedOrdinal is not null)
            throw new InvalidOperationException("The package choice does not match this opening. Your saved rewards are unchanged.");
        if (active.FlowState.Phase is not (ManifestPhase.Offer1 or ManifestPhase.Offer2) ||
            active.FlowState.CurrentOrdinal != expectedOrdinal)
        {
            throw new InvalidOperationException(
                "The Manifest offer changed before this decision was applied.");
        }

        var catalog = premium ? RequireCatalog(active.Ticket.CaseTemplateId) : RequireDecisionCatalog(active, decision);
        if (premium)
        {
            // Fail closed if any frozen choice vanished; never silently substitute a new offer.
            foreach (var choice in active.Offers)
                RequireExactLot(catalog, active.RarityLadderVersion, choice.Rarity, choice.Identity,
                    choice.Forest, choice.Fingerprint, "saved premium choice");
        }
        IReadOnlyList<ManifestRelayCandidateSnapshot> relayCandidates = [];
        var settlesEntitlement = decision == ManifestOfferDecision.Lock ||
            active.FlowState.Phase == ManifestPhase.Offer2;
        if (settlesEntitlement)
        {
            var entitlementOrdinal = selectedOrdinal ?? (decision == ManifestOfferDecision.Lock
                ? active.FlowState.CurrentOrdinal
                : ManifestRecord.OfferCount);
            var offer = active.Offers[entitlementOrdinal - 1];
            var entitlement = new ManifestEntitlementSnapshot(
                offer.Rarity,
                offer.Identity,
                offer.Forest,
                offer.Fingerprint);
            relayCandidates = FreezeRelayCandidates(
                _catalogCoordinator!.GetCaseSnapshot(active.Ticket.CaseTemplateId),
                entitlement,
                active.FlowState.RelayStage,
                active.RarityLadderVersion);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var next = premium
            ? active.ChoosePremiumOffer(selectedOrdinal!.Value, UtcNow(), relayCandidates)
            : active.DecideOffer(decision, UtcNow(), relayCandidates);
        journal.ReplaceActiveManifest(next);
        await _journalStore
            .SaveAsync(context.ProfileId, journal, cancellationToken)
            .ConfigureAwait(false);
        return context.Response;
    }

    public async Task<ItemEventRouterResponse> RelayAsync(
        OpeningContext context,
        string manifestId,
        ManifestPhase expectedPhase,
        int expectedRelayStage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ManifestRecordValidation.RequireIdentifier(manifestId, nameof(manifestId));
        if (!Enum.IsDefined(expectedPhase))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedPhase));
        }
        _ = RelayRules.GetOdds(expectedRelayStage);
        var relayInventory = _relayKeyInventory ??
            throw new InvalidOperationException("Manifest Relay-key inventory support is unavailable.");

        await using var profileLock = await _lockPool
            .AcquireAsync(context.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        _raidSessions.RequireLobby(context.ProfileId);
        _uncertaintyCoordinator.ThrowIfUncertain(context.ProfileId);
        var journal = await _journalStore
            .LoadAsync(context.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        var active = RequireActiveManifest(journal, manifestId);
        RejectLegacyPreparedTransaction(journal);
        if (active.FlowState.Phase != expectedPhase ||
            active.FlowState.RelayStage != expectedRelayStage)
        {
            throw new InvalidOperationException(
                "The Manifest Relay state changed before this request was applied.");
        }

        if (active.FlowState.Phase == ManifestPhase.Entitlement)
        {
            active = await PrepareRelayAsync(
                    context,
                    journal,
                    active,
                    relayInventory,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (active.FlowState.Phase != ManifestPhase.RelayPrepared)
        {
            throw new InvalidOperationException(
                $"Cannot Relay a Manifest while it is in phase '{active.FlowState.Phase}'.");
        }

        return await CompletePreparedRelayAsync(
                context,
                journal,
                active,
                relayInventory,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ItemEventRouterResponse> CompletePreparedTicketAsync(
        OpeningContext context,
        CaseOpeningJournal journal,
        ManifestRecord active,
        IManifestTicketInventory inventory,
        CancellationToken cancellationToken)
    {
        using var saveLease = await _committer
            .AcquireMutationLeaseAsync(context.ProfileId, cancellationToken).ConfigureAwait(false);
        var checkpoint = inventory.CaptureTicket(context);
        var profileCommitBoundaryStarted = false;
        var appliedThisRequest = false;
        try
        {
            var presence = inventory.InspectTicket(context, active.Ticket);
            var witness = inventory.InspectTicketCommit(
                context,
                active.ManifestId,
                active.Ticket);
            var needsProfileCommit = false;

            if (witness == ManifestCommitWitnessState.Predecessor &&
                presence == ManifestInventoryPresence.Present)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireFreshTicketContent(active);
                var applied = inventory.ApplyPreparedTicket(context, active.Ticket);
                ValidateAppliedTicket(active.Ticket, applied);
                appliedThisRequest = true;
                needsProfileCommit = true;
                if (!active.Ticket.ProfileCommitStarted)
                {
                    active = active.BeginTicketProfileCommit();
                    journal.ReplaceActiveManifest(active);
                    await _journalStore
                        .SaveAsync(context.ProfileId, journal, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            else if (witness == ManifestCommitWitnessState.Current &&
                     presence == ManifestInventoryPresence.Absent)
            {
                if (!active.Ticket.ProfileCommitStarted)
                {
                    active = active.BeginTicketProfileCommit();
                    journal.ReplaceActiveManifest(active);
                    await _journalStore
                        .SaveAsync(context.ProfileId, journal, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                throw new InvalidOperationException(
                    "The prepared Manifest ticket contradicts its durable witness or live inventory.");
            }

            if (needsProfileCommit)
            {
                inventory.StageTicketCommit(context, active.ManifestId, active.Ticket);
                profileCommitBoundaryStarted = true;
                saveLease?.Dispose();
                try
                {
                    await _committer
                        .CommitAsync(context.ProfileId, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    _uncertaintyCoordinator.MarkUncertain(context.ProfileId);
                    throw;
                }
            }

            active = active.ActivateTicket(UtcNow());
            journal.ReplaceActiveManifest(active);
            await _journalStore
                .SaveAsync(context.ProfileId, journal, CancellationToken.None)
                .ConfigureAwait(false);
            if (!appliedThisRequest)
            {
                inventory.ReplayTicket(context, active.Ticket);
            }
            return context.Response;
        }
        catch when (!profileCommitBoundaryStarted)
        {
            inventory.RestoreTicket(context, checkpoint);
            throw;
        }
    }

    private async Task<ManifestRecord> PrepareRelayAsync(
        OpeningContext context,
        CaseOpeningJournal journal,
        ManifestRecord active,
        IManifestRelayKeyInventory inventory,
        CancellationToken cancellationToken)
    {
        var entitlement = active.Entitlement
            ?? throw new InvalidOperationException("The active Manifest has no Relay entitlement.");
        if (active.FlowState.RelayTerminal || active.RelayCandidates.Count == 0)
        {
            throw new InvalidOperationException("The current entitlement is not Relay-eligible.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var catalog = RequireRelayCatalog(active);
        var key = inventory.PrepareRelayKey(
            context,
            new HashSet<MongoId> { active.Ticket.CaseId, active.Ticket.KeyId },
            UtcNow());
        var stage = active.FlowState.RelayStage;
        var odds = RelayRules.GetOdds(stage);
        var outcomeRng = new CanonicalRngEvidence(
            ManifestRngPurpose.RelayOutcome,
            stage,
            RequireUnitNumerator(_nextUnitNumerator()));
        var outcome = ManifestRecordValidation.SelectRelayOutcome(
            odds,
            active.BrokerFavor,
            outcomeRng);

        CanonicalRngEvidence? targetRng = null;
        ManifestEntitlementSnapshot? output = null;
        IReadOnlyList<ManifestRelayCandidateSnapshot> nextCandidates = [];
        if (outcome != ManifestRelayResult.Confiscated)
        {
            var eligible = active.RelayCandidates
                .Where(candidate => candidate.TargetResult == outcome)
                .ToArray();
            if (eligible.Length == 0)
            {
                throw new InvalidOperationException(
                    "The frozen Relay pool has no target for the selected outcome.");
            }
            targetRng = new CanonicalRngEvidence(
                ManifestRngPurpose.RelayTargetSelection,
                stage,
                RequireUnitNumerator(_nextUnitNumerator()));
            var selected = ManifestRecordValidation.SelectRelayTarget(eligible, targetRng);
            output = new ManifestEntitlementSnapshot(
                selected.Rarity,
                selected.Identity,
                selected.Forest,
                selected.Fingerprint);

            var continues = outcome == ManifestRelayResult.Upgrade &&
                stage < RelayRules.MaximumStage &&
                output.Rarity != RewardRarity.BlackLabel;
            if (continues)
            {
                var currentCaseCatalog = _catalogCoordinator!.GetCaseSnapshot(active.Ticket.CaseTemplateId);
                nextCandidates = FreezeRelayCandidates(
                    currentCaseCatalog,
                    output,
                    stage + 1,
                    active.RarityLadderVersion);
                // An old frozen upgrade must not become impossible merely
                // because its only continuation is now opening-only. Grandfather
                // that continuation only across a catalog change and only when
                // the ordinary pool is empty; already saved inputs stay exact.
                if (nextCandidates.Count == 0 && active.CatalogSnapshotId != currentCaseCatalog.SnapshotId)
                    nextCandidates = _catalogSelector!.CreateRelayCandidatesForStage(currentCaseCatalog,
                        output, stage + 1, active.RarityLadderVersion, allowOpeningChases: true);
                if (nextCandidates.Count == 0)
                {
                    throw new InvalidOperationException(
                        "The selected Relay upgrade has no complete next-stage pool.");
                }
            }
        }

        var favorAfter = active.BrokerFavor == RelayRules.MaximumRecoveryMeter
            ? 0
            : outcome == ManifestRelayResult.Confiscated
                ? checked(active.BrokerFavor + 1)
                : active.BrokerFavor;
        var prepared = new ManifestRelayPreparedPayload(
            key.KeyId,
            outcome,
            output,
            odds,
            outcomeRng,
            targetRng,
            active.BrokerFavor,
            favorAfter,
            profileCommitStarted: false,
            key.PreparedAtUtc,
            nextCandidates,
            key.CommitGeneration,
            key.CommitPredecessorHash);
        active = active.BeginRelay(prepared);
        journal.ReplaceActiveManifest(active);
        await _journalStore
            .SaveAsync(context.ProfileId, journal, cancellationToken)
            .ConfigureAwait(false);
        return active;
    }

    private async Task<ItemEventRouterResponse> CompletePreparedRelayAsync(
        OpeningContext context,
        CaseOpeningJournal journal,
        ManifestRecord active,
        IManifestRelayKeyInventory inventory,
        CancellationToken cancellationToken)
    {
        var prepared = active.RelayPrepared
            ?? throw new InvalidOperationException("The active Manifest has no prepared Relay.");
        var inputFingerprint = active.Entitlement?.Fingerprint
            ?? throw new InvalidOperationException("The prepared Relay has no input entitlement.");
        using var saveLease = await _committer
            .AcquireMutationLeaseAsync(context.ProfileId, cancellationToken).ConfigureAwait(false);
        var checkpoint = inventory.CaptureRelayKey(context);
        var profileCommitBoundaryStarted = false;
        var appliedThisRequest = false;
        try
        {
            var presence = inventory.InspectRelayKey(context, prepared);
            var witness = inventory.InspectRelayKeyCommit(
                context,
                active.ManifestId,
                inputFingerprint,
                prepared);
            var needsProfileCommit = false;
            if (witness == ManifestCommitWitnessState.Predecessor &&
                presence == ManifestInventoryPresence.Present)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var applied = inventory.ApplyPreparedRelayKey(context, prepared);
                ValidateAppliedRelay(prepared, applied);
                appliedThisRequest = true;
                needsProfileCommit = true;
                if (!prepared.ProfileCommitStarted)
                {
                    active = active.BeginRelayProfileCommit();
                    journal.ReplaceActiveManifest(active);
                    await _journalStore
                        .SaveAsync(context.ProfileId, journal, CancellationToken.None)
                        .ConfigureAwait(false);
                    prepared = active.RelayPrepared!;
                }
            }
            else if (witness == ManifestCommitWitnessState.Current &&
                     presence == ManifestInventoryPresence.Absent)
            {
                if (!prepared.ProfileCommitStarted)
                {
                    active = active.BeginRelayProfileCommit();
                    journal.ReplaceActiveManifest(active);
                    await _journalStore
                        .SaveAsync(context.ProfileId, journal, CancellationToken.None)
                        .ConfigureAwait(false);
                    prepared = active.RelayPrepared!;
                }
            }
            else
            {
                throw new InvalidOperationException(
                    "The prepared Manifest Relay contradicts its durable witness or live inventory.");
            }

            if (needsProfileCommit)
            {
                inventory.StageRelayKeyCommit(
                    context,
                    active.ManifestId,
                    inputFingerprint,
                    prepared);
                profileCommitBoundaryStarted = true;
                saveLease?.Dispose();
                try
                {
                    await _committer
                        .CommitAsync(context.ProfileId, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    _uncertaintyCoordinator.MarkUncertain(context.ProfileId);
                    throw;
                }
            }

            var completed = active.CompleteRelay(UtcNow());
            journal.ReplaceActiveManifest(completed);
            if (completed.IsTerminal)
            {
                journal.FinishActiveManifest();
            }
            await _journalStore
                .SaveAsync(context.ProfileId, journal, CancellationToken.None)
                .ConfigureAwait(false);
            if (!appliedThisRequest)
            {
                inventory.ReplayRelayKey(context, prepared);
            }
            return context.Response;
        }
        catch when (!profileCommitBoundaryStarted)
        {
            inventory.RestoreRelayKey(context, checkpoint);
            throw;
        }
    }

    public async Task<ManifestClaimResult> ClaimAsync(
        OpeningContext context,
        string manifestId,
        CancellationToken cancellationToken) =>
        await ClaimCoreAsync(context, manifestId, expectedPhase: null, cancellationToken)
            .ConfigureAwait(false);

    public async Task<ManifestClaimResult> ClaimAsync(
        OpeningContext context,
        string manifestId,
        ManifestPhase expectedPhase,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(expectedPhase))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedPhase));
        }

        return await ClaimCoreAsync(context, manifestId, expectedPhase, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ItemEventRouterResponse> ForfeitMissingContentAsync(
        OpeningContext context,
        string manifestId,
        ManifestPhase expectedPhase,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ManifestRecordValidation.RequireIdentifier(manifestId, nameof(manifestId));
        if (expectedPhase is not (
                ManifestPhase.Offer1 or
                ManifestPhase.Offer2 or
                ManifestPhase.Entitlement))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedPhase));
        }

        var coordinator = _catalogCoordinator ??
            throw new InvalidOperationException("The finalized Manifest catalog is unavailable.");
        await using var profileLock = await _lockPool
            .AcquireAsync(context.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        _raidSessions.RequireLobby(context.ProfileId);
        _uncertaintyCoordinator.ThrowIfUncertain(context.ProfileId);

        var journal = await _journalStore
            .LoadAsync(context.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        var priorReceipt = journal.ManifestReceipts.SingleOrDefault(receipt =>
            string.Equals(receipt.ManifestId, manifestId, StringComparison.Ordinal));
        if (priorReceipt is not null)
        {
            if (priorReceipt.TerminalPhase != ManifestPhase.Forfeited ||
                ResolveForfeitSourcePhase(priorReceipt) != expectedPhase)
            {
                throw new InvalidOperationException(
                    "The requested Manifest Forfeit does not match its terminal receipt.");
            }

            return context.Response;
        }

        var active = RequireActiveManifest(journal, manifestId);
        RejectLegacyPreparedTransaction(journal);
        if (active.FlowState.Phase != expectedPhase)
        {
            throw new InvalidOperationException(
                "The Manifest Forfeit state changed before this request was applied.");
        }

        CargoCatalogSnapshot? catalog;
        try
        {
            catalog = coordinator.GetSnapshot();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            catalog = null;
        }
        if (!ManifestSnapshotProjection.IsMissingCurrentContent(active, catalog))
        {
            throw new InvalidOperationException(
                "A Manifest can be forfeited only when its current content is unavailable.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var terminal = active.ForfeitMissingContent(UtcNow());
        journal.ReplaceActiveManifest(terminal);
        journal.FinishActiveManifest();
        await _journalStore
            .SaveAsync(context.ProfileId, journal, cancellationToken)
            .ConfigureAwait(false);
        return context.Response;
    }

    private async Task<ManifestClaimResult> ClaimCoreAsync(
        OpeningContext context,
        string manifestId,
        ManifestPhase? expectedPhase,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ManifestRecordValidation.RequireIdentifier(manifestId, nameof(manifestId));

        await using var profileLock = await _lockPool
            .AcquireAsync(context.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        _raidSessions.RequireLobby(context.ProfileId);
        _uncertaintyCoordinator.ThrowIfUncertain(context.ProfileId);

        var journal = await _journalStore
            .LoadAsync(context.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (journal.FindManifestClaimGrant(manifestId) is ManifestClaimGrantRecord committedGrant)
        {
            if (committedGrant.ClaimPayload.Delivery == ClaimDeliveryKind.Messenger)
            {
                // Collection, selling and deleting mail are not failed deliveries.
                // The durable grant is authoritative; never reconstruct attachments.
                return new ManifestClaimResult(ManifestClaimResultKind.Granted, context.Response, Replay: true);
            }
            var grantPresence = _inventory.InspectClaim(context, committedGrant.ClaimPayload);
            if (grantPresence == RewardPresence.Partial)
            {
                throw new InvalidOperationException("The durable Claim grant has partial or mutated live inventory evidence.");
            }
            if (grantPresence == RewardPresence.Complete)
            {
                _inventory.ReplayClaim(
                    context,
                    _inventory.ReconcileAppliedClaim(context, committedGrant.ClaimPayload));
            }
            return new ManifestClaimResult(
                ManifestClaimResultKind.Granted,
                context.Response,
                Replay: true);
        }

        var active = journal.ActiveManifest;
        if (active is null ||
            !string.Equals(active.ManifestId, manifestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The requested manifest is not the active manifest.");
        }
        if (expectedPhase is ManifestPhase phase && active.FlowState.Phase != phase)
        {
            throw new InvalidOperationException(
                "The Manifest Claim state changed before this request was applied.");
        }
        if (journal.PreparedOpening is not null || journal.PreparedRelay is not null)
        {
            throw new InvalidOperationException(
                "The profile already has a different prepared settlement transaction that must be resumed first.");
        }

        if (active.FlowState.Phase == ManifestPhase.Entitlement)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entitlement = active.Entitlement
                ?? throw new InvalidOperationException("The active manifest has no Claim entitlement.");
            RequireExactLot(
                RequireCatalog(active.Ticket.CaseTemplateId),
                active.RarityLadderVersion,
                entitlement.Rarity,
                entitlement.Identity,
                entitlement.Forest,
                entitlement.Fingerprint,
                "Claim entitlement");
            var occupiedIds = CollectOccupiedIds(context, active);
            var materialized = active.Ticket.CaseTemplateId == CaseContracts.CashCache
                ? _materializer.MaterializeCashPayout(entitlement.Forest, occupiedIds)
                : _materializer.Materialize(entitlement.Forest, occupiedIds);
            if (!_inventory.TryPrepareClaim(
                    context,
                    materialized.Items,
                    materialized.CanonicalRootIds,
                    UtcNow(),
                    out var prepared))
            {
                if (prepared is not null)
                {
                    throw new InvalidOperationException(
                        "A no-space Claim preflight returned unexpected prepared evidence.");
                }

                return new ManifestClaimResult(
                    ManifestClaimResultKind.NoSpace,
                    context.Response,
                    Replay: false);
            }
            if (prepared is null)
            {
                throw new InvalidOperationException(
                    "A successful Claim preflight returned no prepared evidence.");
            }

            prepared = ManifestClaimCommitWitness.PlanNext(context.PmcData, context.ProfileId, prepared);
            active = active.BeginClaim(prepared);
            journal.ReplaceActiveManifest(active);
            await _journalStore
                .SaveAsync(context.ProfileId, journal, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        else if (active.FlowState.Phase is not (
                     ManifestPhase.ClaimPrepared or
                     ManifestPhase.RewardOwed))
        {
            throw new InvalidOperationException(
                $"Cannot Claim a manifest while it is in phase '{active.FlowState.Phase}'.");
        }

        return await CompletePreparedClaimAsync(context, journal, active, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ManifestClaimResult> CompletePreparedClaimAsync(
        OpeningContext context,
        CaseOpeningJournal journal,
        ManifestRecord active,
        CancellationToken cancellationToken)
    {
        var prepared = active.ClaimPrepared
            ?? throw new InvalidOperationException("The active Claim has no prepared physical payload.");
        var entitlement = active.Entitlement
            ?? throw new InvalidOperationException("The active Claim has no entitlement.");
        if (prepared.Delivery == ClaimDeliveryKind.LegacyInventory &&
            ManifestClaimCommitWitness.Inspect(context.PmcData, context.ProfileId, active.ManifestId,
                entitlement.Fingerprint, prepared) == ManifestClaimCommitWitnessInspection.Predecessor &&
            _inventory.InspectClaim(context, prepared) == RewardPresence.Absent)
        {
            var migrated = _inventory.PrepareDelivery(prepared);
            if (migrated.Delivery != prepared.Delivery)
            {
                active = active.WithClaimDelivery(migrated);
                journal.ReplaceActiveManifest(active);
                await _journalStore.SaveAsync(context.ProfileId, journal, cancellationToken).ConfigureAwait(false);
                prepared = migrated;
            }
        }
        if (!prepared.ProfileCommitStarted)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        using var saveLease = await _committer
            .AcquireMutationLeaseAsync(context.ProfileId, cancellationToken).ConfigureAwait(false);
        var checkpoint = _inventory.Capture(context);
        var profileCommitBoundaryStarted = false;
        try
        {
            var witness = ManifestClaimCommitWitness.Inspect(
                context.PmcData,
                context.ProfileId,
                active.ManifestId,
                entitlement.Fingerprint,
                prepared);
            var presence = _inventory.InspectClaim(context, prepared);
            var payloadIsPresent = false;
            var needsProfileCommit = witness == ManifestClaimCommitWitnessInspection.Predecessor;

            if (!prepared.ProfileCommitStarted)
            {
                if (witness == ManifestClaimCommitWitnessInspection.Current &&
                    presence == RewardPresence.Complete)
                {
                    var reconciled = _inventory.ReconcileAppliedClaim(context, prepared);
                    active = active.ReconcileClaim(reconciled);
                    journal.ReplaceActiveManifest(active);
                    await _journalStore.SaveAsync(context.ProfileId, journal, CancellationToken.None).ConfigureAwait(false);
                    prepared = active.ClaimPrepared!;
                    payloadIsPresent = true;
                }
                else if (witness == ManifestClaimCommitWitnessInspection.Predecessor &&
                         presence == RewardPresence.Absent)
                {
                    var applied = _inventory.ApplyPreparedClaim(context, prepared, entitlement.Identity.DisplayName);
                    active = active.ReconcileClaim(applied);
                    journal.ReplaceActiveManifest(active);
                    await _journalStore.SaveAsync(context.ProfileId, journal, CancellationToken.None).ConfigureAwait(false);
                    prepared = active.ClaimPrepared!;
                    payloadIsPresent = true;
                }
                else throw new InvalidOperationException("Fresh Claim evidence contradicts its durable commit chain or live inventory.");
            }
            else if (witness == ManifestClaimCommitWitnessInspection.Current &&
                     prepared.Delivery == ClaimDeliveryKind.Messenger)
            {
                // The whole profile (mail + witness) committed. Native collection
                // can have removed any subset since then, including every item.
            }
            else if (witness == ManifestClaimCommitWitnessInspection.Current)
            {
                if (presence == RewardPresence.Partial)
                {
                    throw new InvalidOperationException(
                        "A committed Claim witness has partial or mutated inventory evidence.");
                }
                if (presence == RewardPresence.Complete)
                {
                    var reconciled = _inventory.ReconcileAppliedClaim(context, prepared);
                    active = active.ReconcileClaim(reconciled);
                    journal.ReplaceActiveManifest(active);
                    await _journalStore
                        .SaveAsync(context.ProfileId, journal, CancellationToken.None)
                        .ConfigureAwait(false);
                    prepared = active.ClaimPrepared!;
                    payloadIsPresent = true;
                }
            }
            else if (witness == ManifestClaimCommitWitnessInspection.Predecessor &&
                     presence == RewardPresence.Absent)
            {
                if (presence != RewardPresence.Absent)
                {
                    throw new InvalidOperationException(
                        "Uncommitted Claim evidence is present without its matching profile witness.");
                }

                var recoveryInput = new ManifestClaimPreparedPayload(
                    prepared.Items,
                    prepared.RootIds,
                    profileCommitStarted: false,
                    prepared.PreparedAtUtc,
                    prepared.CommitGeneration,
                    prepared.CommitPredecessorHash,
                    prepared.Delivery);
                var recovered = _inventory.ApplyPreparedClaim(context, recoveryInput, entitlement.Identity.DisplayName);
                active = active.ReconcileClaim(recovered);
                journal.ReplaceActiveManifest(active);
                await _journalStore
                    .SaveAsync(context.ProfileId, journal, CancellationToken.None)
                    .ConfigureAwait(false);
                prepared = active.ClaimPrepared!;
                payloadIsPresent = true;
            }
            else throw new InvalidOperationException("A prepared Claim does not match its durable commit chain or live inventory evidence.");

            if (active.FlowState.Phase == ManifestPhase.ClaimPrepared)
            {
                active = active.MarkClaimRewardOwed();
                journal.ReplaceActiveManifest(active);
                await _journalStore
                    .SaveAsync(context.ProfileId, journal, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (payloadIsPresent)
            {
                _inventory.ReplayClaim(context, prepared);
            }

            if (needsProfileCommit)
            {
                ManifestClaimCommitWitness.Stage(
                    context.PmcData,
                    context.ProfileId,
                    active.ManifestId,
                    active.Entitlement!.Fingerprint,
                    prepared);
                profileCommitBoundaryStarted = true;
                // From this point a native autosave is allowed to persist the
                // complete mail + witness. Do not roll back after releasing it.
                saveLease?.Dispose();
                try
                {
                    await _committer.CommitAsync(context.ProfileId, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    _uncertaintyCoordinator.MarkUncertain(context.ProfileId);
                    throw;
                }
            }

            var completion = active.CompleteClaim(UtcNow());
            journal.FinishGrantedActiveManifest(
                completion.TerminalManifest,
                completion.Grant);
            await _journalStore
                .SaveAsync(context.ProfileId, journal, CancellationToken.None)
                .ConfigureAwait(false);
            await _inventory.NotifyClaimAsync(context, prepared).ConfigureAwait(false);
            return new ManifestClaimResult(
                ManifestClaimResultKind.Granted,
                context.Response,
                Replay: false);
        }
        catch when (!profileCommitBoundaryStarted)
        {
            _inventory.Restore(context, checkpoint);
            throw;
        }
    }

    private static IReadOnlyCollection<MongoId> CollectOccupiedIds(
        OpeningContext context,
        ManifestRecord active)
    {
        var inventoryItems = context.PmcData.Inventory?.Items
            ?? throw new InvalidOperationException("The authenticated profile inventory is unavailable.");
        if (inventoryItems.Any(item => item.Id.IsEmpty) ||
            inventoryItems.Select(item => item.Id).Distinct().Count() != inventoryItems.Count)
        {
            throw new InvalidOperationException(
                "The authenticated profile inventory contains invalid or duplicate item IDs.");
        }

        var occupied = inventoryItems.Select(item => item.Id).ToHashSet();
        if (context.Response.ProfileChanges is not null &&
            context.Response.ProfileChanges.TryGetValue(context.ProfileId, out var profileChange) &&
            profileChange.Items is { } changes)
        {
            occupied.UnionWith((changes.NewItems ?? []).Select(item => item.Id));
            occupied.UnionWith((changes.ChangedItems ?? []).Select(item => item.Id));
            occupied.UnionWith((changes.DeletedItems ?? []).Select(item => item.Id));
        }
        occupied.Add(active.Ticket.CaseId);
        occupied.Add(active.Ticket.KeyId);
        if (occupied.Any(id => id.IsEmpty) ||
            occupied.Count > CargoLotMaterializer.MaxExistingIdCount)
        {
            throw new InvalidOperationException(
                "Claim item ID collision input exceeds the supported bound.");
        }

        return occupied;
    }

    private void RequireCommittedTicketReplay(
        OpeningContext context,
        ManifestRecord active,
        IManifestTicketInventory inventory)
    {
        if (!active.Ticket.Committed ||
            inventory.InspectTicket(context, active.Ticket) != ManifestInventoryPresence.Absent ||
            inventory.InspectTicketCommit(context, active.ManifestId, active.Ticket) !=
                ManifestCommitWitnessState.Current)
        {
            throw new InvalidOperationException(
                "The active Manifest ticket does not match the committed profile evidence.");
        }

        inventory.ReplayTicket(context, active.Ticket);
    }

    private static void ValidateAppliedTicket(
        ManifestTicketPayload expected,
        ManifestTicketPayload actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        if (!actual.ProfileCommitStarted || actual.Committed ||
            actual.CaseTemplateId != expected.CaseTemplateId ||
            actual.CaseId != expected.CaseId ||
            actual.KeyId != expected.KeyId ||
            actual.PreparedAtUtc != expected.PreparedAtUtc ||
            actual.CommitGeneration != expected.CommitGeneration ||
            !string.Equals(
                actual.CommitPredecessorHash,
                expected.CommitPredecessorHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Live ticket mutation changed the prepared Manifest identity.");
        }
    }

    private static void ValidateAppliedRelay(
        ManifestRelayPreparedPayload expected,
        ManifestRelayPreparedPayload actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        if (!actual.ProfileCommitStarted ||
            actual.KeyId != expected.KeyId ||
            actual.Outcome != expected.Outcome ||
            actual.Odds != expected.Odds ||
            actual.BrokerFavorBefore != expected.BrokerFavorBefore ||
            actual.BrokerFavorAfter != expected.BrokerFavorAfter ||
            actual.PreparedAtUtc != expected.PreparedAtUtc ||
            actual.CommitGeneration != expected.CommitGeneration ||
            !string.Equals(
                actual.CommitPredecessorHash,
                expected.CommitPredecessorHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Live Relay-key mutation changed the prepared Manifest identity.");
        }
    }

    private static ManifestRecord RequireActiveManifest(
        CaseOpeningJournal journal,
        string manifestId)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var active = journal.ActiveManifest;
        if (active is null ||
            !string.Equals(active.ManifestId, manifestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The requested Manifest is not active.");
        }

        return active;
    }

    private static ManifestPhase ResolveForfeitSourcePhase(
        ManifestTerminalReceipt receipt)
    {
        if (receipt.Entitlement is not null)
        {
            return ManifestPhase.Entitlement;
        }

        return receipt.Decisions.Count switch
        {
            0 => ManifestPhase.Offer1,
            1 when receipt.Decisions[0].Ordinal == 1 &&
                   receipt.Decisions[0].Decision == ManifestOfferDecision.Burn =>
                ManifestPhase.Offer2,
            _ => throw new InvalidOperationException(
                "The Manifest Forfeit receipt has no valid source phase.")
        };
    }

    private static void RejectLegacyPreparedTransaction(CaseOpeningJournal journal)
    {
        if (journal.PreparedOpening is not null || journal.PreparedRelay is not null)
        {
            throw new InvalidOperationException(
                "A legacy settlement transaction must be resumed before changing a Manifest.");
        }
    }

    private CargoCatalogSnapshot RequireCatalog(string? caseTemplateId = null)
    {
        var coordinator = _catalogCoordinator ??
            throw new InvalidOperationException("The finalized Manifest catalog is unavailable.");
        return caseTemplateId == CaseContracts.CashCache
            ? coordinator.GetCaseSnapshot(caseTemplateId) : coordinator.GetSnapshot();
    }

    private CargoCatalogSnapshot RequireDecisionCatalog(
        ManifestRecord manifest,
        ManifestOfferDecision decision)
    {
        var catalog = RequireCatalog();
        var current = manifest.Offers[manifest.FlowState.CurrentOrdinal - 1];
        RequireExactLot(
            catalog,
            manifest.RarityLadderVersion,
            current.Rarity,
            current.Identity,
            current.Forest,
            current.Fingerprint,
            "current Manifest offer");
        if (decision == ManifestOfferDecision.Burn)
        {
            var nextOrdinal = checked(manifest.FlowState.CurrentOrdinal + 1);
            if (nextOrdinal > ManifestRecord.OfferCount)
            {
                throw new InvalidOperationException("The Manifest has no later offer to Burn into.");
            }

            var next = manifest.Offers[nextOrdinal - 1];
            RequireExactLot(
                catalog,
                manifest.RarityLadderVersion,
                next.Rarity,
                next.Identity,
                next.Forest,
                next.Fingerprint,
                "next Manifest offer");
        }

        return catalog;
    }

    private void RequireFreshTicketContent(ManifestRecord manifest)
    {
        var catalog = RequireCatalog(manifest.Ticket.CaseTemplateId);
        foreach (var offer in manifest.Offers)
        {
            RequireExactLot(
                catalog,
                manifest.RarityLadderVersion,
                offer.Rarity,
                offer.Identity,
                offer.Forest,
                offer.Fingerprint,
                "prepared Manifest offer");
        }
    }

    private CargoCatalogSnapshot RequireRelayCatalog(ManifestRecord manifest)
    {
        var catalog = RequireCatalog();
        var entitlement = manifest.Entitlement
            ?? throw new InvalidOperationException("The active Manifest has no Relay entitlement.");
        RequireExactLot(
            catalog,
            manifest.RarityLadderVersion,
            entitlement.Rarity,
            entitlement.Identity,
            entitlement.Forest,
            entitlement.Fingerprint,
            "Relay entitlement");
        foreach (var candidate in manifest.RelayCandidates)
        {
            RequireExactLot(
                catalog,
                manifest.RarityLadderVersion,
                candidate.Rarity,
                candidate.Identity,
                candidate.Forest,
                candidate.Fingerprint,
                "frozen Relay candidate");
        }

        return catalog;
    }

    private void RequireExactLot(
        CargoCatalogSnapshot catalog,
        RarityLadderVersion rarityLadderVersion,
        RewardRarity rarity,
        CargoLotIdentitySnapshot identity,
        RewardForest forest,
        RewardForestFingerprintV2 fingerprint,
        string purpose)
    {
        if (catalog.ResolveExact(rarity, identity, forest, fingerprint, rarityLadderVersion) is null)
        {
            throw new InvalidOperationException(
                $"The {purpose} is unavailable in the current finalized catalog.");
        }

        if (catalog.CaseTemplateId == CaseContracts.CashCache) _materializer.ValidateCashPayout(forest);
        else _materializer.Validate(forest);
    }

    private IReadOnlyList<ManifestRelayCandidateSnapshot> FreezeRelayCandidates(
        CargoCatalogSnapshot catalog,
        ManifestEntitlementSnapshot entitlement,
        int stage,
        RarityLadderVersion rarityLadderVersion)
    {
        var selector = _catalogSelector ??
            throw new InvalidOperationException("Manifest catalog selection is unavailable.");
        return selector.CreateRelayCandidatesForStage(catalog, entitlement, stage, rarityLadderVersion);
    }

    private string AllocateManifestId(CaseOpeningJournal journal)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var candidate = _manifestIdFactory();
            if (!IsMongoId(candidate))
            {
                throw new InvalidOperationException(
                    "The Manifest ID source returned a non-canonical identifier.");
            }
            if ((journal.ActiveManifest is null ||
                 !string.Equals(journal.ActiveManifest.ManifestId, candidate, StringComparison.Ordinal)) &&
                journal.ManifestReceipts.All(receipt =>
                    !string.Equals(receipt.ManifestId, candidate, StringComparison.Ordinal)))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not allocate a unique Manifest identifier.");
    }

    private static bool IsMongoId(string? value) =>
        value is { Length: 24 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string CreateManifestId() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

    private static long NextUnitNumerator()
    {
        var bytes = RandomNumberGenerator.GetBytes(sizeof(ulong));
        return checked((long)(BitConverter.ToUInt64(bytes) >> 11));
    }

    private static long RequireUnitNumerator(long value)
    {
        if (value is < 0 or >= CanonicalRngEvidence.UnitDenominator)
        {
            throw new InvalidOperationException(
                "The Manifest random source returned an out-of-range canonical draw.");
        }

        return value;
    }

    private DateTimeOffset UtcNow()
    {
        var value = _utcNow();
        if (value.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Manifest settlement timestamps must be UTC.");
        }

        return value;
    }
}
