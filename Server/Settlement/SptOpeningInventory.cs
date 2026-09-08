using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Server.Core.Utils.Json.Converters;

namespace ContrabandCases.Server.Settlement;

[Injectable(InjectionType.Singleton)]
public sealed class SptOpeningInventory :
    IOpeningPreparation,
    IOpeningInventory,
    IRelayPreparation,
    IRelayInventory,
    IManifestClaimInventory,
    IManifestTicketInventory,
    IManifestRelayKeyInventory
{
    internal const int MaxIndexedInventoryItemCount = 65_536;

    private static readonly JsonSerializerOptions ItemComparisonOptions = CreateItemComparisonOptions();
    private readonly InventoryHelper inventoryHelper;
    private readonly ServerRewardCatalog rewardCatalog;
    private readonly ICloner cloner;
    private readonly Action<MongoId, AddItemDirectRequest, PmcData, ItemEventRouterResponse> addItemToStash;
    private readonly Action<PmcData, MongoId, MongoId, ItemEventRouterResponse> removeItem;
    private readonly Func<MongoId, bool> isAmmoTemplate;

    public SptOpeningInventory(
        InventoryHelper inventoryHelper,
        ServerRewardCatalog rewardCatalog,
        ICloner cloner,
        ItemHelper itemHelper)
    {
        this.inventoryHelper = inventoryHelper;
        this.rewardCatalog = rewardCatalog;
        this.cloner = cloner;
        addItemToStash = inventoryHelper is null
            ? MissingAddItemToStash
            : inventoryHelper.AddItemToStash;
        removeItem = inventoryHelper is null
            ? MissingRemoveItem
            : inventoryHelper.RemoveItem;
        ArgumentNullException.ThrowIfNull(itemHelper);
        isAmmoTemplate = templateId => itemHelper.IsOfBaseclass(templateId, BaseClasses.AMMO);
    }

    internal SptOpeningInventory(
        InventoryHelper inventoryHelper,
        ServerRewardCatalog rewardCatalog,
        ICloner cloner)
    {
        this.inventoryHelper = inventoryHelper;
        this.rewardCatalog = rewardCatalog;
        this.cloner = cloner;
        addItemToStash = inventoryHelper is null
            ? MissingAddItemToStash
            : inventoryHelper.AddItemToStash;
        removeItem = inventoryHelper is null
            ? MissingRemoveItem
            : inventoryHelper.RemoveItem;
        isAmmoTemplate = MissingAmmoTemplateClassifier;
    }

    private SptOpeningInventory(
        ICloner cloner,
        Action<MongoId, AddItemDirectRequest, PmcData, ItemEventRouterResponse> addItemToStash,
        Action<PmcData, MongoId, MongoId, ItemEventRouterResponse> removeItem,
        Func<MongoId, bool> isAmmoTemplate)
    {
        inventoryHelper = null!;
        rewardCatalog = null!;
        this.cloner = cloner ?? throw new ArgumentNullException(nameof(cloner));
        this.addItemToStash = addItemToStash ?? throw new ArgumentNullException(nameof(addItemToStash));
        this.removeItem = removeItem ?? throw new ArgumentNullException(nameof(removeItem));
        this.isAmmoTemplate = isAmmoTemplate ?? throw new ArgumentNullException(nameof(isAmmoTemplate));
    }

    internal static SptOpeningInventory CreateForTests(
        ICloner cloner,
        Action<MongoId, AddItemDirectRequest, PmcData, ItemEventRouterResponse> addItemToStash,
        Func<MongoId, bool>? isAmmoTemplate = null,
        Action<PmcData, MongoId, MongoId, ItemEventRouterResponse>? removeItem = null) =>
        new(cloner, addItemToStash, removeItem ?? MissingRemoveItem, isAmmoTemplate ?? (_ => true));

    public ValueTask<CaseOpeningRecord> PrepareAsync(
        OpeningContext context,
        MongoId caseId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNoWarnings(context.Response);

        var inventoryItems = RequireInventoryItems(context.PmcData);
        var caseItem = inventoryItems.SingleOrDefault(item => item.Id == caseId);
        if (caseItem is null || caseItem.Template != (MongoId)ModConstants.CaseTemplateId)
        {
            throw new InvalidOperationException("The authenticated profile does not contain the requested BR-12 case.");
        }

        var keyItem = inventoryItems
            .Where(item => item.Template == (MongoId)ModConstants.KeyTemplateId)
            .OrderBy(item => item.Id.ToString(), StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("A matching BR-12 Relay Key is required.");

        EnsureConsumedItemsAreLeaves(inventoryItems, caseId, keyItem.Id);

        var selected = WeightedRewardSelector.Select(rewardCatalog.Rewards, SecureUnitValue());
        var preparedItems = rewardCatalog.ClonePresetItems(selected.Id);
        preparedItems.ReplaceIDs();
        var exactIds = preparedItems.Select(item => item.Id).ToHashSet();
        if (preparedItems.Count == 0 || exactIds.Count != preparedItems.Count)
        {
            throw new InvalidOperationException("The selected reward did not produce a unique preset tree.");
        }

        RequireSingleRoot(preparedItems, exactIds);

        // The direct check proves the normal stash path. The cloned full-transaction
        // simulation below also proves the sorting-table fallback after consuming inputs.
        _ = inventoryHelper.CanPlaceItemsInInventory(context.ProfileId, [preparedItems]);
        var locatedItems = SimulateFullTransaction(context, caseId, keyItem.Id, preparedItems, exactIds);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(new CaseOpeningRecord(
            caseId,
            keyItem.Id,
            selected.Id,
            locatedItems,
            DateTimeOffset.UtcNow,
            OpeningRecordStatus.Prepared,
            null,
            RarityLadderVersion.FiveTier));
    }

    public InventoryEvidence Inspect(OpeningContext context, MongoId caseId, CaseOpeningRecord? record)
    {
        ArgumentNullException.ThrowIfNull(context);
        var items = RequireInventoryItems(context.PmcData);
        var casePresent = items.Any(item =>
            item.Id == caseId && item.Template == (MongoId)ModConstants.CaseTemplateId);

        if (record is null)
        {
            return new InventoryEvidence(
                casePresent,
                items.Any(item => item.Template == (MongoId)ModConstants.KeyTemplateId),
                RewardPresence.Absent);
        }

        var keyPresent = items.Any(item =>
            item.Id == record.KeyId && item.Template == (MongoId)ModConstants.KeyTemplateId);
        var expectedById = record.RewardItems.ToDictionary(item => item.Id);
        var found = items.Where(item => expectedById.ContainsKey(item.Id)).ToArray();
        var rewardPresence = found.Length switch
        {
            0 => RewardPresence.Absent,
            _ when found.Length == expectedById.Count &&
                found.All(item => item.Template == expectedById[item.Id].Template) => RewardPresence.Complete,
            _ => RewardPresence.Partial
        };

        return new InventoryEvidence(casePresent, keyPresent, rewardPresence);
    }

    public InventoryCheckpoint Capture(OpeningContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var claimCommitWitnessToken = ManifestClaimCommitWitness.CaptureToken(context.PmcData);
        var manifestInputCommitWitnessTokens = ManifestInputCommitWitness.CaptureTokens(context.PmcData);
        var profileChanges = context.Response.ProfileChanges;
        var hadProfileChangesDictionary = profileChanges is not null;
        ProfileChange? change = null;
        var hadProfileChange = profileChanges is not null && profileChanges.TryGetValue(context.ProfileId, out change);
        var hadItems = hadProfileChange && change!.Items is not null;

        return new SptInventoryCheckpoint(
            CloneRequired(context.PmcData.Inventory, "profile inventory"),
            CloneRequired(context.PmcData.InsuredItems, "insured items"),
            cloner.Clone(context.Response.Warnings),
            hadItems ? CloneRequired(change!.Items, "response item changes") : null,
            hadProfileChangesDictionary,
            hadProfileChange,
            hadItems,
            claimCommitWitnessToken,
            manifestInputCommitWitnessTokens);
    }

    public CaseOpeningRecord ApplyPrepared(OpeningContext context, CaseOpeningRecord record)
    {
        ConsumePreparedOpening(context, record);
        var changes = SptResponseChanges.GetOrCreate(context.Response, context.ProfileId);
        inventoryHelper.AddItemToStash(
            context.ProfileId, CreateAddRequest(record.RewardItems.ToList()), context.PmcData, context.Response);
        EnsureNoWarnings(context.Response);
        return ValidateAppliedChanges(context, record, changes);
    }

    internal void ConsumePreparedOpening(OpeningContext context, CaseOpeningRecord record)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(record);
        EnsureNoWarnings(context.Response);

        var evidence = Inspect(context, record.CaseId, record);
        if (evidence != new InventoryEvidence(true, true, RewardPresence.Absent))
        {
            throw new InvalidOperationException("Prepared settlement inputs no longer match the authenticated profile.");
        }

        EnsureConsumedItemsAreLeaves(
            RequireInventoryItems(context.PmcData),
            record.CaseId,
            record.KeyId);
        inventoryHelper.RemoveItem(context.PmcData, record.CaseId, context.ProfileId, context.Response);
        inventoryHelper.RemoveItem(context.PmcData, record.KeyId, context.ProfileId, context.Response);
        EnsureNoWarnings(context.Response);
        var changes = SptResponseChanges.GetOrCreate(context.Response, context.ProfileId);
        if (RequireInventoryItems(context.PmcData).Any(i => i.Id == record.CaseId || i.Id == record.KeyId) ||
            !changes.DeletedItems!.Any(i => i.Id == record.CaseId) || !changes.DeletedItems.Any(i => i.Id == record.KeyId))
            throw new InvalidOperationException("Legacy opening did not consume both saved inputs.");
    }

    public void Restore(OpeningContext context, InventoryCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (checkpoint is not SptInventoryCheckpoint sptCheckpoint)
        {
            throw new ArgumentException("Checkpoint was not created by the SPT inventory adapter.", nameof(checkpoint));
        }

        context.PmcData.Inventory = CloneRequired(sptCheckpoint.Inventory, "checkpoint inventory");
        context.PmcData.InsuredItems = CloneRequired(sptCheckpoint.InsuredItems, "checkpoint insured items");
        ManifestClaimCommitWitness.RestoreToken(
            context.PmcData,
            sptCheckpoint.ClaimCommitWitnessToken);
        ManifestInputCommitWitness.RestoreTokens(
            context.PmcData,
            sptCheckpoint.ManifestInputCommitWitnessTokens);
        context.Response.Warnings = cloner.Clone(sptCheckpoint.Warnings);
        if (!sptCheckpoint.HadProfileChangesDictionary)
        {
            context.Response.ProfileChanges = null!;
            return;
        }

        context.Response.ProfileChanges ??= [];

        if (!sptCheckpoint.HadProfileChange)
        {
            context.Response.ProfileChanges.Remove(context.ProfileId);
            return;
        }

        if (!context.Response.ProfileChanges.TryGetValue(context.ProfileId, out var profileChange))
        {
            profileChange = new ProfileChange { Id = context.ProfileId.ToString() };
            context.Response.ProfileChanges.Add(context.ProfileId, profileChange);
        }

        profileChange.Items = sptCheckpoint.HadItems
            ? CloneRequired(sptCheckpoint.ItemChanges, "checkpoint item changes")
            : null;
    }

    public void Replay(OpeningContext context, CaseOpeningRecord record)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(record);
        SptResponseChanges.Replay(context.Response, context.ProfileId, record);
    }

    public ManifestTicketPayload PrepareTicket(
        OpeningContext context,
        MongoId caseId,
        DateTimeOffset preparedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        EnsureNoWarnings(context.Response);
        ManifestRecordValidation.RequireUtc(preparedAtUtc, nameof(preparedAtUtc));

        var inventoryItems = RequireInventoryItems(context.PmcData);
        var caseMatches = inventoryItems.Where(item => item.Id == caseId).ToArray();
        if (caseMatches.Length != 1 ||
            !CaseContracts.IsCase(caseMatches[0].Template.ToString()))
        {
            throw new InvalidOperationException(
                "The authenticated profile does not contain the exact requested BR-12 case.");
        }
        EnsureItemIsLeaf(inventoryItems, caseId, "BR-12 case");

        var key = SelectLowestRelayKeyLeaf(inventoryItems, new HashSet<MongoId> { caseId });
        var prepared = new ManifestTicketPayload(
            caseId,
            key.Id,
            preparedAtUtc,
            profileCommitStarted: false,
            committed: false,
            committedAtUtc: null,
            caseTemplateId: caseMatches[0].Template.ToString());
        return ManifestInputCommitWitness.PlanTicket(context.PmcData, context.ProfileId, prepared);
    }

    public ManifestInventoryPresence InspectTicket(
        OpeningContext context,
        ManifestTicketPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prepared);
        return InspectExactLeafInputs(
            RequireInventoryItems(context.PmcData),
            [
                (prepared.CaseId, (MongoId)prepared.CaseTemplateId),
                (prepared.KeyId, (MongoId)ModConstants.KeyTemplateId)
            ]);
    }

    public ManifestCommitWitnessState InspectTicketCommit(
        OpeningContext context,
        string manifestId,
        ManifestTicketPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prepared);
        return ManifestInputCommitWitness.InspectTicket(
            context.PmcData,
            context.ProfileId,
            manifestId,
            prepared);
    }

    public InventoryCheckpoint CaptureTicket(OpeningContext context) => Capture(context);

    public ManifestTicketPayload ApplyPreparedTicket(
        OpeningContext context,
        ManifestTicketPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prepared);
        EnsureFreshPreparedTicket(prepared);
        ApplyExactLeafRemovals(
            context,
            [prepared.CaseId, prepared.KeyId],
            () => InspectTicket(context, prepared),
            "Manifest ticket");
        return prepared.BeginProfileCommit();
    }

    public void StageTicketCommit(
        OpeningContext context,
        string manifestId,
        ManifestTicketPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prepared);
        if (!prepared.ProfileCommitStarted || prepared.CommitGeneration is null)
        {
            throw new ArgumentException(
                "Only an applied Manifest ticket can stage its profile commit witness.",
                nameof(prepared));
        }
        ManifestInputCommitWitness.StageTicket(
            context.PmcData,
            context.ProfileId,
            manifestId,
            prepared);
    }

    public void RestoreTicket(OpeningContext context, InventoryCheckpoint checkpoint) =>
        Restore(context, checkpoint);

    public void ReplayTicket(OpeningContext context, ManifestTicketPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prepared);
        if (!prepared.ProfileCommitStarted || prepared.CommitGeneration is null)
        {
            throw new ArgumentException(
                "Only an applied Manifest ticket can be replayed.",
                nameof(prepared));
        }
        ReplayExactDeletedItems(
            context.Response,
            context.ProfileId,
            [prepared.CaseId, prepared.KeyId],
            "Manifest ticket");
    }

    public ManifestRelayKeyPreparation PrepareRelayKey(
        OpeningContext context,
        IReadOnlySet<MongoId> excludedIds,
        DateTimeOffset preparedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(excludedIds);
        EnsureNoWarnings(context.Response);
        ManifestRecordValidation.RequireUtc(preparedAtUtc, nameof(preparedAtUtc));

        var key = SelectLowestRelayKeyLeaf(RequireInventoryItems(context.PmcData), excludedIds);
        return ManifestInputCommitWitness.PlanRelayKey(
            context.PmcData,
            context.ProfileId,
            key.Id,
            preparedAtUtc);
    }

    public ManifestInventoryPresence InspectRelayKey(
        OpeningContext context,
        ManifestRelayPreparedPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prepared);
        return InspectExactLeafInputs(
            RequireInventoryItems(context.PmcData),
            [(prepared.KeyId, (MongoId)ModConstants.KeyTemplateId)]);
    }

    public ManifestCommitWitnessState InspectRelayKeyCommit(
        OpeningContext context,
        string manifestId,
        RewardForestFingerprintV2 inputFingerprint,
        ManifestRelayPreparedPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(inputFingerprint);
        ArgumentNullException.ThrowIfNull(prepared);
        return ManifestInputCommitWitness.InspectRelayKey(
            context.PmcData,
            context.ProfileId,
            manifestId,
            inputFingerprint,
            prepared);
    }

    public InventoryCheckpoint CaptureRelayKey(OpeningContext context) => Capture(context);

    public ManifestRelayPreparedPayload ApplyPreparedRelayKey(
        OpeningContext context,
        ManifestRelayPreparedPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prepared);
        EnsureFreshPreparedRelay(prepared);
        ApplyExactLeafRemovals(
            context,
            [prepared.KeyId],
            () => InspectRelayKey(context, prepared),
            "Manifest Relay key");
        return prepared.BeginProfileCommit();
    }

    public void StageRelayKeyCommit(
        OpeningContext context,
        string manifestId,
        RewardForestFingerprintV2 inputFingerprint,
        ManifestRelayPreparedPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(inputFingerprint);
        ArgumentNullException.ThrowIfNull(prepared);
        if (!prepared.ProfileCommitStarted || prepared.CommitGeneration is null)
        {
            throw new ArgumentException(
                "Only an applied Manifest Relay key can stage its profile commit witness.",
                nameof(prepared));
        }
        ManifestInputCommitWitness.StageRelayKey(
            context.PmcData,
            context.ProfileId,
            manifestId,
            inputFingerprint,
            prepared);
    }

    public void RestoreRelayKey(OpeningContext context, InventoryCheckpoint checkpoint) =>
        Restore(context, checkpoint);

    public void ReplayRelayKey(OpeningContext context, ManifestRelayPreparedPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prepared);
        if (!prepared.ProfileCommitStarted || prepared.CommitGeneration is null)
        {
            throw new ArgumentException(
                "Only an applied Manifest Relay key can be replayed.",
                nameof(prepared));
        }
        ReplayExactDeletedItems(
            context.Response,
            context.ProfileId,
            [prepared.KeyId],
            "Manifest Relay key");
    }

    public bool TryPrepareClaim(
        OpeningContext context,
        IReadOnlyList<Item> materializedItems,
        IReadOnlyList<MongoId> rootIds,
        DateTimeOffset preparedAtUtc,
        out ManifestClaimPreparedPayload? prepared)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(materializedItems);
        ArgumentNullException.ThrowIfNull(rootIds);
        EnsureNoWarnings(context.Response);

        var unlocated = new ManifestClaimPreparedPayload(
            materializedItems,
            rootIds,
            profileCommitStarted: false,
            preparedAtUtc);
        EnsureClaimIdsAvailable(context, unlocated);

        var simulatedProfile = CloneRequired(context.PmcData, "profile for Claim placement simulation");
        var simulatedResponse = CloneRequired(context.Response, "response for Claim placement simulation");
        EnsureClaimSimulationIsIsolated(context, simulatedProfile, simulatedResponse);
        var simulatedContext = new OpeningContext(
            simulatedProfile,
            simulatedResponse,
            context.ProfileId,
            context.SessionId);
        var baselineNewIds = SnapshotNewItemIds(simulatedResponse, context.ProfileId);

        if (!TryAddClaimTrees(
                simulatedContext,
                unlocated,
                baselineNewIds,
                warningCanMeanNoSpace: true,
                allowSptGrantNormalization: true,
                out var locatedItems))
        {
            prepared = null;
            return false;
        }

        prepared = new ManifestClaimPreparedPayload(
            locatedItems!,
            unlocated.RootIds,
            profileCommitStarted: false,
            preparedAtUtc);
        return true;
    }

    public RewardPresence InspectClaim(
        OpeningContext context,
        ManifestClaimPreparedPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prepared);
        return InspectExactClaimForestPresence(
            prepared.Items,
            prepared.RootIds,
            RequireInventoryItems(context.PmcData),
            context.PmcData);
    }

    public ManifestClaimPreparedPayload ReconcileAppliedClaim(
        OpeningContext context,
        ManifestClaimPreparedPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prepared);
        var liveItems = RequireInventoryItems(context.PmcData);
        if (InspectExactClaimForestPresence(
                prepared.Items,
                prepared.RootIds,
                liveItems,
                context.PmcData) != RewardPresence.Complete)
        {
            throw new InvalidOperationException(
                "The authenticated profile does not contain the exact applied Claim forest.");
        }

        var reconciledItems = OrderExactClaimItems(
            prepared.Items,
            liveItems,
            "applied Claim reconciliation");
        return new ManifestClaimPreparedPayload(
            reconciledItems,
            prepared.RootIds,
            profileCommitStarted: true,
            prepared.PreparedAtUtc,
            prepared.CommitGeneration,
            prepared.CommitPredecessorHash);
    }

    public ManifestClaimPreparedPayload ApplyPreparedClaim(
        OpeningContext context,
        ManifestClaimPreparedPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prepared);
        EnsureNoWarnings(context.Response);
        if (prepared.ProfileCommitStarted)
        {
            throw new InvalidOperationException("A Claim whose profile commit already started cannot be applied again.");
        }
        if (InspectClaim(context, prepared) != RewardPresence.Absent)
        {
            throw new InvalidOperationException("Prepared Claim items are already present or partially present.");
        }

        EnsureClaimIdsAvailable(context, prepared);
        var baselineNewIds = SnapshotNewItemIds(context.Response, context.ProfileId);
        if (!TryAddClaimTrees(
                context,
                prepared,
                baselineNewIds,
                warningCanMeanNoSpace: false,
                allowSptGrantNormalization: false,
                out var locatedItems))
        {
            throw new InvalidOperationException("Live Claim application unexpectedly reported no space.");
        }

        return new ManifestClaimPreparedPayload(
            locatedItems!,
            prepared.RootIds,
            profileCommitStarted: true,
            prepared.PreparedAtUtc,
            prepared.CommitGeneration,
            prepared.CommitPredecessorHash);
    }

    public void ReplayClaim(OpeningContext context, ManifestClaimPreparedPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prepared);
        EnsureNoWarnings(context.Response);
        if (!prepared.ProfileCommitStarted)
        {
            throw new ArgumentException(
                "Only a Claim whose profile commit started can be replayed.",
                nameof(prepared));
        }

        var expectedItems = prepared.Items;
        var exactIds = prepared.ExactItemIds.ToHashSet();
        var existingChanges = GetExistingItemChanges(context.Response, context.ProfileId);
        var existingNewItems = existingChanges?.NewItems ?? [];
        if ((existingChanges?.ChangedItems ?? []).Any(item => exactIds.Contains(item.Id)) ||
            (existingChanges?.DeletedItems ?? []).Any(item => exactIds.Contains(item.Id)))
        {
            throw new InvalidOperationException("Claim replay would collide with existing response changes.");
        }

        var existingClaimItems = existingNewItems.Where(item => exactIds.Contains(item.Id)).ToArray();
        if (existingClaimItems.Length == 0)
        {
            var replayCandidate = existingNewItems.Concat(expectedItems).ToArray();
            EnsureExactClaimForest(
                expectedItems,
                prepared.RootIds,
                replayCandidate,
                context.PmcData,
                "Claim replay");
            SptResponseChanges.GetOrCreate(context.Response, context.ProfileId).NewItems!.AddRange(expectedItems);
            return;
        }

        if (existingClaimItems.Length != exactIds.Count ||
            existingClaimItems.Select(item => item.Id).Distinct().Count() != exactIds.Count)
        {
            throw new InvalidOperationException("Claim replay found a partial or duplicate existing payload.");
        }

        var existingById = existingClaimItems.ToDictionary(item => item.Id);
        if (expectedItems.Any(item => !ItemsDeepEqual(item, existingById[item.Id])))
        {
            throw new InvalidOperationException("Claim replay would overwrite a conflicting existing payload.");
        }

        EnsureExactClaimForest(
            expectedItems,
            prepared.RootIds,
            existingNewItems,
            context.PmcData,
            "Claim replay");
    }

    /// <summary>
    /// Applies a validated testing inventory grant and returns the item IDs of
    /// the newly created BR-12 Relay Case instances (in no particular order),
    /// so a caller can optionally tag them with a forced Manifest reward pool
    /// via <c>TestingForcedCrateRegistry</c>. Key items are not included since
    /// only case openings consult the forced-pool registry.
    /// </summary>
    public IReadOnlyList<MongoId> ApplyTestingInventoryGrant(OpeningContext context, int caseCount, int keyCount,
        string caseTemplateId = ModConstants.CaseTemplateId)
    {
        ArgumentNullException.ThrowIfNull(context);
        TestingInventoryGrantPolicy.Validate(caseCount, keyCount);
        EnsureNoWarnings(context.Response);

        var inventoryItems = RequireInventoryItems(context.PmcData);
        var itemTrees = CreateTestingGrantItems(
            caseCount,
            keyCount,
            inventoryItems.Select(item => item.Id),
            caseTemplateId);
        var createdCaseIds = itemTrees.Take(caseCount).Select(tree => tree[0].Id).ToArray();

        var expectedItems = itemTrees.SelectMany(tree => tree).ToArray();
        var expectedById = expectedItems.ToDictionary(item => item.Id);
        var changes = SptResponseChanges.GetOrCreate(context.Response, context.ProfileId);
        var previousNewIds = (changes.NewItems ?? []).Select(item => item.Id).ToHashSet();

        foreach (var itemTree in itemTrees)
        {
            inventoryHelper.AddItemToStash(
                context.ProfileId,
                CreateAddRequest(itemTree),
                context.PmcData,
                context.Response);
        }

        EnsureNoWarnings(context.Response);
        var added = (changes.NewItems ?? [])
            .Where(item => !previousNewIds.Contains(item.Id))
            .ToArray();
        if (added.Length != expectedById.Count || added.Any(item =>
                !expectedById.TryGetValue(item.Id, out var expected) ||
                item.Template != expected.Template))
        {
            throw new InvalidOperationException(
                "The testing inventory grant response did not contain the exact prepared items.");
        }

        var liveById = RequireInventoryItems(context.PmcData).ToDictionary(item => item.Id);
        if (expectedById.Any(pair =>
                !liveById.TryGetValue(pair.Key, out var live) ||
                live.Template != pair.Value.Template))
        {
            throw new InvalidOperationException(
                "The testing inventory grant was not applied exactly to the authenticated profile.");
        }

        return createdCaseIds;
    }

    internal static List<List<Item>> CreateTestingGrantItems(
        int caseCount,
        int keyCount,
        IEnumerable<MongoId> existingIds,
        string caseTemplateId = ModConstants.CaseTemplateId)
    {
        ArgumentNullException.ThrowIfNull(existingIds);
        CaseContracts.Require(caseTemplateId);
        TestingInventoryGrantPolicy.Validate(caseCount, keyCount);
        var occupiedIds = existingIds.ToHashSet();
        var itemTrees = new List<List<Item>>(caseCount + keyCount);

        AddItems(caseCount, caseTemplateId);
        AddItems(keyCount, ModConstants.KeyTemplateId);
        return itemTrees;

        void AddItems(int count, string templateId)
        {
            for (var index = 0; index < count; index++)
            {
                var id = CreateUniqueMongoId(occupiedIds);
                itemTrees.Add(
                [
                    new Item
                    {
                        Id = id,
                        Template = (MongoId)templateId,
                        Upd = new Upd
                        {
                            StackObjectsCount = 1,
                            SpawnedInSession = false
                        }
                    }
                ]);
            }
        }
    }

    private static MongoId CreateUniqueMongoId(ISet<MongoId> occupiedIds)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidate = (MongoId)Convert.ToHexString(
                RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            if (occupiedIds.Add(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not allocate a unique testing inventory item ID.");
    }

    public ValueTask<RelaySettlementRecord> PrepareSecureAsync(
        OpeningContext context,
        RelayStake stake,
        int recoveryMeter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stake);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNoWarnings(context.Response);
        var inputItems = RequireLiveStake(context, stake, requireExactAwardedTree: false);
        return ValueTask.FromResult(new RelaySettlementRecord(
            stake.OriginCaseId,
            stake.RootId,
            inputItems.Select(item => item.Id),
            stake.RewardId,
            stake.Rarity,
            stake.Stage,
            RelayRecordAction.Secure,
            null,
            RelayOutcome.Secured,
            null,
            [],
            recoveryMeter,
            recoveryMeter,
            false,
            false,
            DateTimeOffset.UtcNow,
            RelayRecordStatus.Prepared,
            null,
            inputItems,
            stake.RarityLadderVersion));
    }

    public ValueTask<RelaySettlementRecord> PrepareRelayAsync(
        OpeningContext context,
        RelayStake stake,
        int recoveryMeter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stake);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNoWarnings(context.Response);
        if (!RelayRules.CanRelay(stake.Rarity, stake.Stage, stake.RarityLadderVersion))
        {
            throw new InvalidOperationException("The requested reward is not eligible for another Relay stage.");
        }

        var inventoryItems = RequireInventoryItems(context.PmcData);
        var inputItems = RequireLiveStake(context, stake, requireExactAwardedTree: true);
        var inputIds = inputItems.Select(item => item.Id).ToHashSet();
        var key = SelectLowestRelayKey(inventoryItems, inputIds);
        EnsureKeyIsLeaf(inventoryItems, key.Id);

        var outcome = RelayRules.SelectOutcome(stake.Stage, recoveryMeter, SecureUnitValue());
        ValidatedReward? target = null;
        List<Item> preparedOutput = [];
        if (outcome != RelayOutcome.Confiscated)
        {
            target = rewardCatalog.SelectRelayTarget(
                stake.RewardId,
                outcome,
                SecureUnitValue(),
                stake.RarityLadderVersion);
            preparedOutput = rewardCatalog.ClonePresetItems(target.Id);
            preparedOutput.ReplaceIDs();
            var preparedIds = preparedOutput.Select(item => item.Id).ToHashSet();
            if (preparedOutput.Count == 0 || preparedIds.Count != preparedOutput.Count)
            {
                throw new InvalidOperationException("The selected Relay reward did not produce a unique preset tree.");
            }
            RequireSingleRoot(preparedOutput, preparedIds);
            // New legacy-chain payouts use native Messenger. Their exact saved
            // reward tree must not depend on the recipient's current stash space.
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new RelaySettlementRecord(
            stake.OriginCaseId,
            stake.RootId,
            inputItems.Select(item => item.Id),
            stake.RewardId,
            stake.Rarity,
            stake.Stage,
            RelayRecordAction.Relay,
            key.Id,
            outcome,
            target?.Id,
            preparedOutput,
            recoveryMeter,
            RelayRules.MeterAfterOutcome(recoveryMeter, stake.Stage, outcome),
            recoveryMeter == RelayRules.MaximumRecoveryMeter,
            false,
            DateTimeOffset.UtcNow,
            RelayRecordStatus.Prepared,
            null,
            inputItems,
            stake.RarityLadderVersion));
    }

    public RelayInventoryEvidence InspectRelay(OpeningContext context, RelaySettlementRecord record)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(record);
        var items = RequireInventoryItems(context.PmcData);
        var allowedRootParents = RequireRelayRootParentIds(context.PmcData);
        var inputPresence = record.InputItems.Count > 0
            ? InspectExactTreePresence(record.InputItems, items, record.StakeRootId, allowedRootParents)
            : InspectLegacyInputPresence(record, items);
        var keyPresent = record.KeyId is MongoId keyId && items.Any(item =>
            item.Id == keyId && item.Template == (MongoId)ModConstants.KeyTemplateId);
        var outputPresence = record.OutputRootId is MongoId outputRoot
            ? InspectExactTreePresence(record.OutputItems, items, outputRoot, allowedRootParents)
            : RewardPresence.Absent;
        return new RelayInventoryEvidence(inputPresence, keyPresent, outputPresence);
    }

    public InventoryCheckpoint CaptureRelay(OpeningContext context) => Capture(context);

    public RelaySettlementRecord ApplyPreparedRelay(OpeningContext context, RelaySettlementRecord record)
    {
        ConsumePreparedRelay(context, record);
        var changes = SptResponseChanges.GetOrCreate(context.Response, context.ProfileId);
        if (record.OutputItems.Count == 0) return record.BeginProfileCommit();
        inventoryHelper.AddItemToStash(context.ProfileId, CreateAddRequest(record.OutputItems.ToList()), context.PmcData, context.Response);
        EnsureNoWarnings(context.Response);
        var outputRoot = record.OutputRootId ?? throw new InvalidOperationException("Prepared Relay output has no root item.");
        var responseOutput = GetLiveTree(changes.NewItems ?? [], outputRoot).ToList();
        var storedOutput = GetLiveTree(RequireInventoryItems(context.PmcData), outputRoot).ToList();
        var parents = RequireRelayRootParentIds(context.PmcData);
        EnsureExactStakeTree(record.OutputItems, responseOutput, outputRoot, parents);
        EnsureExactStakeTree(record.OutputItems, storedOutput, outputRoot, parents);
        ValidateMatchingLivePayload(responseOutput, storedOutput);
        return record.BeginProfileCommit(responseOutput);
    }

    internal void ConsumePreparedRelay(OpeningContext context, RelaySettlementRecord record)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(record);
        EnsureNoWarnings(context.Response);
        if (record.Action != RelayRecordAction.Relay || record.KeyId is not MongoId keyId ||
            InspectRelay(context, record) != new RelayInventoryEvidence(RewardPresence.Complete, true, RewardPresence.Absent))
        {
            throw new InvalidOperationException("Prepared Relay inputs no longer match the authenticated profile.");
        }

        if (record.InputItems.Count == 0)
        {
            throw new InvalidOperationException("Prepared Relay record is missing its exact input-tree snapshot.");
        }
        var inventoryItems = RequireInventoryItems(context.PmcData);
        var allowedRootParents = RequireRelayRootParentIds(context.PmcData);
        EnsureExactStakeTree(
            record.InputItems,
            GetLiveTree(inventoryItems, record.StakeRootId),
            record.StakeRootId,
            allowedRootParents);
        EnsureKeyIsLeaf(inventoryItems, keyId);

        var changes = SptResponseChanges.GetOrCreate(context.Response, context.ProfileId);
        inventoryHelper.RemoveItem(context.PmcData, record.StakeRootId, context.ProfileId, context.Response);
        inventoryHelper.RemoveItem(context.PmcData, keyId, context.ProfileId, context.Response);
        EnsureNoWarnings(context.Response);
        var deletedIds = (changes.DeletedItems ?? []).Select(item => item.Id).ToHashSet();
        if (record.InputItemIds.Any(id => !deletedIds.Contains(id)) || !deletedIds.Contains(keyId))
        {
            throw new InvalidOperationException("Live Relay settlement did not report every consumed input.");
        }
        var profileItems = RequireInventoryItems(context.PmcData);
        if (profileItems.Any(item => record.InputItemIds.Contains(item.Id) || item.Id == keyId))
        {
            throw new InvalidOperationException("Live Relay settlement left a consumed input in the profile.");
        }
    }

    public void RestoreRelay(OpeningContext context, InventoryCheckpoint checkpoint) => Restore(context, checkpoint);

    public void ReplayRelay(OpeningContext context, RelaySettlementRecord record) =>
        SptResponseChanges.ReplayRelay(context.Response, context.ProfileId, record);

    private static RewardPresence InspectLegacyInputPresence(
        RelaySettlementRecord record,
        IReadOnlyCollection<Item> inventoryItems)
    {
        var inputFound = inventoryItems.Count(item => record.InputItemIds.Contains(item.Id));
        return inputFound switch
        {
            0 => RewardPresence.Absent,
            _ when inputFound == record.InputItemIds.Count => RewardPresence.Complete,
            _ => RewardPresence.Partial
        };
    }

    private bool TryAddClaimTrees(
        OpeningContext context,
        ManifestClaimPreparedPayload prepared,
        IReadOnlySet<MongoId> baselineNewIds,
        bool warningCanMeanNoSpace,
        bool allowSptGrantNormalization,
        out IReadOnlyList<Item>? locatedItems)
    {
        var expectedItems = prepared.Items;
        var requestTrees = PartitionClaimTrees(prepared.Items, prepared.RootIds);
        foreach (var requestTree in requestTrees)
        {
            var beforeNewIds = SnapshotNewItemIds(context.Response, context.ProfileId);
            var requestItems = requestTree.Select(CaseOpeningRecord.CloneItem).ToList();
            requestItems[0] = NormalizeRootPlacement(requestItems[0]);
            addItemToStash(
                context.ProfileId,
                CreateAddRequest(requestItems),
                context.PmcData,
                context.Response);

            if (context.Response.Warnings is { Count: > 0 })
            {
                var treeIds = requestTree.Select(item => item.Id).ToHashSet();
                if (warningCanMeanNoSpace &&
                    HasOnlyNoSpaceWarnings(context.Response) &&
                    ClaimItemsAreAbsent(context, treeIds))
                {
                    locatedItems = null;
                    return false;
                }

                throw new InvalidOperationException(
                    "SPT reported a warning after partially applying a Claim tree.");
            }

            EnsureClaimTreeApplied(
                context,
                requestTree,
                beforeNewIds,
                allowSptGrantNormalization,
                isAmmoTemplate);
        }

        locatedItems = ReconcileClaimItems(
            context,
            prepared,
            baselineNewIds,
            allowSptGrantNormalization,
            isAmmoTemplate);
        return true;
    }

    private static void EnsureClaimTreeApplied(
        OpeningContext context,
        IReadOnlyList<Item> expectedTree,
        IReadOnlySet<MongoId> beforeNewIds,
        bool allowSptGrantNormalization,
        Func<MongoId, bool> isAmmoTemplate)
    {
        var expectedIds = expectedTree.Select(item => item.Id).ToHashSet();
        var afterNewIds = SnapshotNewItemIds(context.Response, context.ProfileId);
        if (!beforeNewIds.IsSubsetOf(afterNewIds) ||
            !afterNewIds.Except(beforeNewIds).ToHashSet().SetEquals(expectedIds))
        {
            throw new InvalidOperationException(
                "SPT did not report exactly one prepared Claim tree.");
        }

        var rootId = expectedTree[0].Id;
        var profileItems = RequireInventoryItems(context.PmcData);
        var responseItems = GetExistingItemChanges(context.Response, context.ProfileId)?.NewItems ?? [];
        var profileIndex = new ItemTreeIndex(profileItems, "profile Claim placement");
        var responseIndex = new ItemTreeIndex(responseItems, "response Claim placement");
        EnsureExactPlacedClaimTree(
            expectedTree,
            profileIndex.GetTree(rootId),
            rootId,
            context.PmcData,
            "profile Claim placement",
            allowSptGrantNormalization,
            isAmmoTemplate);
        EnsureExactPlacedClaimTree(
            expectedTree,
            responseIndex.GetTree(rootId),
            rootId,
            context.PmcData,
            "response Claim placement",
            allowSptGrantNormalization,
            isAmmoTemplate);

        var profileTreeById = profileItems
            .Where(item => expectedIds.Contains(item.Id))
            .ToDictionary(item => item.Id);
        var responseTree = responseItems.Where(item => expectedIds.Contains(item.Id)).ToArray();
        ValidateMatchingLivePayload(
            responseTree,
            responseTree.Select(item => profileTreeById[item.Id]).ToArray());
    }

    private static IReadOnlyList<Item> ReconcileClaimItems(
        OpeningContext context,
        ManifestClaimPreparedPayload prepared,
        IReadOnlySet<MongoId> baselineNewIds,
        bool allowSptGrantNormalization,
        Func<MongoId, bool> isAmmoTemplate)
    {
        var expectedItems = prepared.Items;
        var exactIds = prepared.ExactItemIds.ToHashSet();
        var responseItems = GetExistingItemChanges(context.Response, context.ProfileId)?.NewItems ?? [];
        var currentNewIds = SnapshotNewItemIds(context.Response, context.ProfileId);
        if (!baselineNewIds.IsSubsetOf(currentNewIds) ||
            !currentNewIds.Except(baselineNewIds).ToHashSet().SetEquals(exactIds))
        {
            throw new InvalidOperationException(
                "Claim application did not report the exact prepared forest.");
        }

        var profileItems = RequireInventoryItems(context.PmcData);
        EnsureExactClaimForest(
            expectedItems,
            prepared.RootIds,
            profileItems,
            context.PmcData,
            "profile Claim settlement",
            allowSptGrantNormalization,
            isAmmoTemplate);
        EnsureExactClaimForest(
            expectedItems,
            prepared.RootIds,
            responseItems,
            context.PmcData,
            "response Claim settlement",
            allowSptGrantNormalization,
            isAmmoTemplate);

        var locatedResponse = OrderExactClaimItems(expectedItems, responseItems, "response Claim settlement");
        var locatedProfile = OrderExactClaimItems(expectedItems, profileItems, "profile Claim settlement");
        ValidateMatchingLivePayload(locatedResponse, locatedProfile);
        return locatedResponse;
    }

    internal static IReadOnlyList<IReadOnlyList<Item>> PartitionClaimTrees(
        IReadOnlyList<Item> items,
        IReadOnlyList<MongoId> rootIds)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(rootIds);
        if (items.Count == 0 || rootIds.Count == 0)
        {
            throw new ArgumentException("A Claim forest must contain items and declared roots.");
        }

        var allItemIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.Id.IsEmpty || !allItemIds.Add(item.Id.ToString()))
            {
                throw new ArgumentException("Claim items must have unique non-empty IDs.", nameof(items));
            }
        }

        var rootIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < rootIds.Count; index++)
        {
            if (rootIds[index].IsEmpty || !rootIndexById.TryAdd(rootIds[index].ToString(), index))
            {
                throw new ArgumentException("Claim roots must have unique non-empty IDs.", nameof(rootIds));
            }
        }

        var trees = rootIds.Select(_ => new List<Item>()).ToArray();
        var treeIndexByItemId = new Dictionary<string, int>(StringComparer.Ordinal);
        var nextRootIndex = 0;
        foreach (var item in items)
        {
            var itemId = item.Id.ToString();
            if (rootIndexById.TryGetValue(itemId, out var rootIndex))
            {
                if (rootIndex != nextRootIndex++ ||
                    item.ParentId is not null && allItemIds.Contains(item.ParentId))
                {
                    throw new ArgumentException(
                        "Claim roots must appear in declared order and cannot be nested.",
                        nameof(items));
                }

                treeIndexByItemId.Add(itemId, rootIndex);
                trees[rootIndex].Add(item);
                continue;
            }

            if (item.ParentId is null ||
                !treeIndexByItemId.TryGetValue(item.ParentId, out var parentTreeIndex))
            {
                throw new ArgumentException(
                    "Every non-root Claim item must follow a parent in its declared tree.",
                    nameof(items));
            }

            treeIndexByItemId.Add(itemId, parentTreeIndex);
            trees[parentTreeIndex].Add(item);
        }

        if (nextRootIndex != rootIds.Count || trees.Any(tree => tree.Count == 0))
        {
            throw new ArgumentException("Every declared Claim root must identify one tree.", nameof(rootIds));
        }

        return trees;
    }

    internal static RewardPresence InspectExactClaimForestPresence(
        IReadOnlyCollection<Item> expectedItems,
        IReadOnlyList<MongoId> rootIds,
        IReadOnlyCollection<Item> inventoryItems,
        PmcData profile)
    {
        ArgumentNullException.ThrowIfNull(expectedItems);
        ArgumentNullException.ThrowIfNull(rootIds);
        ArgumentNullException.ThrowIfNull(inventoryItems);
        ArgumentNullException.ThrowIfNull(profile);
        var expectedIds = expectedItems.Select(item => item.Id).ToHashSet();
        if (expectedIds.Count == 0)
        {
            return RewardPresence.Absent;
        }

        var foundCount = inventoryItems.Count(item => expectedIds.Contains(item.Id));
        if (foundCount == 0)
        {
            return RewardPresence.Absent;
        }

        try
        {
            if (expectedIds.Count != expectedItems.Count || foundCount != expectedIds.Count)
            {
                return RewardPresence.Partial;
            }

            EnsureExactClaimForest(
                expectedItems.ToArray(),
                rootIds,
                inventoryItems,
                profile,
                "Claim presence inspection");
            return RewardPresence.Complete;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return RewardPresence.Partial;
        }
    }

    internal static void EnsureExactClaimForest(
        IReadOnlyList<Item> expectedItems,
        IReadOnlyList<MongoId> rootIds,
        IReadOnlyCollection<Item> actualItems,
        PmcData profile,
        string operation,
        bool allowSptGrantNormalization = false,
        Func<MongoId, bool>? isAmmoTemplate = null)
    {
        ArgumentNullException.ThrowIfNull(expectedItems);
        ArgumentNullException.ThrowIfNull(rootIds);
        ArgumentNullException.ThrowIfNull(actualItems);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var expectedTrees = PartitionClaimTrees(expectedItems, rootIds);
        var actualIndex = new ItemTreeIndex(actualItems, operation);
        foreach (var expectedTree in expectedTrees)
        {
            var rootId = expectedTree[0].Id;
            EnsureExactPlacedClaimTree(
                expectedTree,
                actualIndex.GetTree(rootId),
                rootId,
                profile,
                operation,
                allowSptGrantNormalization,
                isAmmoTemplate);
        }
    }

    private static void EnsureExactPlacedClaimTree(
        IReadOnlyCollection<Item> expectedItems,
        IReadOnlyCollection<Item> actualItems,
        MongoId expectedRootId,
        PmcData profile,
        string operation,
        bool allowSptGrantNormalization,
        Func<MongoId, bool>? isAmmoTemplate)
    {
        var expectedById = ToUniqueItemDictionary(expectedItems, operation);
        var actualById = ToUniqueItemDictionary(actualItems, operation);
        EnsureAcyclicParentGraph(expectedById.ToDictionary(
            pair => pair.Key.ToString(),
            pair => pair.Value,
            StringComparer.Ordinal));
        EnsureAcyclicParentGraph(actualById.ToDictionary(
            pair => pair.Key.ToString(),
            pair => pair.Value,
            StringComparer.Ordinal));
        if (expectedById.Count != expectedItems.Count ||
            actualById.Count != actualItems.Count ||
            expectedById.Count != actualById.Count ||
            !expectedById.Keys.ToHashSet().SetEquals(actualById.Keys) ||
            SettlementItemTrees.FindRootId(expectedItems) != expectedRootId ||
            SettlementItemTrees.FindRootId(actualItems) != expectedRootId)
        {
            throw new InvalidOperationException(
                $"The {operation} contains missing, added, or reparented items.");
        }

        var expectedRoot = expectedById[expectedRootId];
        var actualRoot = actualById[expectedRootId];
        EnsureExpectedClaimRootPlacement(expectedRoot, profile, operation);
        EnsureLegalClaimRootPlacement(actualRoot, profile, operation);

        foreach (var (itemId, expected) in expectedById)
        {
            var comparableExpected = CaseOpeningRecord.CloneItem(expected);
            var comparableActual = CaseOpeningRecord.CloneItem(actualById[itemId]);
            if (itemId == expectedRootId)
            {
                comparableExpected = NormalizeRootPlacement(comparableExpected);
                comparableActual = NormalizeRootPlacement(comparableActual);
            }
            if (allowSptGrantNormalization)
            {
                NormalizeSptGrantStateForComparison(
                    comparableExpected,
                    comparableActual,
                    isAmmoTemplate);
            }

            if (!ItemsDeepEqual(comparableExpected, comparableActual))
            {
                throw new InvalidOperationException($"The {operation} changed stable item state.");
            }
        }
    }

    private static IReadOnlyList<Item> OrderExactClaimItems(
        IReadOnlyList<Item> expectedItems,
        IReadOnlyCollection<Item> actualItems,
        string operation)
    {
        var exactIds = expectedItems.Select(item => item.Id).ToHashSet();
        var actualById = ToUniqueItemDictionary(
            actualItems.Where(item => exactIds.Contains(item.Id)).ToArray(),
            operation);
        if (actualById.Count != exactIds.Count)
        {
            throw new InvalidOperationException($"The {operation} is missing exact Claim item IDs.");
        }

        return expectedItems.Select(item => actualById[item.Id]).ToArray();
    }

    private static bool ClaimItemsAreAbsent(
        OpeningContext context,
        IReadOnlySet<MongoId> exactIds)
    {
        var changes = GetExistingItemChanges(context.Response, context.ProfileId);
        return !RequireInventoryItems(context.PmcData).Any(item => exactIds.Contains(item.Id)) &&
            !(changes?.NewItems ?? []).Any(item => exactIds.Contains(item.Id)) &&
            !(changes?.ChangedItems ?? []).Any(item => exactIds.Contains(item.Id)) &&
            !(changes?.DeletedItems ?? []).Any(item => exactIds.Contains(item.Id));
    }

    private static IReadOnlySet<MongoId> SnapshotNewItemIds(
        ItemEventRouterResponse response,
        MongoId profileId)
    {
        var items = GetExistingItemChanges(response, profileId)?.NewItems ?? [];
        return ToUniqueItemDictionary(items, "Claim response changes").Keys.ToHashSet();
    }

    private static ItemChanges? GetExistingItemChanges(
        ItemEventRouterResponse response,
        MongoId profileId)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.ProfileChanges is not null &&
            response.ProfileChanges.TryGetValue(profileId, out var change)
                ? change.Items
                : null;
    }

    private static void EnsureClaimSimulationIsIsolated(
        OpeningContext realContext,
        PmcData simulatedProfile,
        ItemEventRouterResponse simulatedResponse)
    {
        if (ReferenceEquals(realContext.PmcData, simulatedProfile) ||
            ReferenceEquals(realContext.PmcData.Inventory, simulatedProfile.Inventory) ||
            ReferenceEquals(realContext.PmcData.Inventory?.Items, simulatedProfile.Inventory?.Items) ||
            ReferenceEquals(realContext.PmcData.InsuredItems, simulatedProfile.InsuredItems) ||
            ReferenceEquals(realContext.Response, simulatedResponse) ||
            realContext.Response.Warnings is not null &&
            ReferenceEquals(realContext.Response.Warnings, simulatedResponse.Warnings) ||
            realContext.Response.ProfileChanges is not null &&
            ReferenceEquals(realContext.Response.ProfileChanges, simulatedResponse.ProfileChanges))
        {
            throw new InvalidOperationException("Claim placement simulation was not isolated from the live context.");
        }

        var realChanges = GetExistingItemChanges(realContext.Response, realContext.ProfileId);
        var simulatedChanges = GetExistingItemChanges(simulatedResponse, realContext.ProfileId);
        if (realChanges is not null && simulatedChanges is not null &&
            (ReferenceEquals(realChanges, simulatedChanges) ||
             ReferenceEquals(realChanges.NewItems, simulatedChanges.NewItems) ||
             ReferenceEquals(realChanges.ChangedItems, simulatedChanges.ChangedItems) ||
             ReferenceEquals(realChanges.DeletedItems, simulatedChanges.DeletedItems)))
        {
            throw new InvalidOperationException("Claim response simulation was not isolated from live response changes.");
        }
    }

    private static void EnsureClaimIdsAvailable(
        OpeningContext context,
        ManifestClaimPreparedPayload prepared)
    {
        var exactIds = prepared.ExactItemIds.ToHashSet();
        var changes = GetExistingItemChanges(context.Response, context.ProfileId);
        if (RequireInventoryItems(context.PmcData).Any(item => exactIds.Contains(item.Id)) ||
            (changes?.NewItems ?? []).Any(item => exactIds.Contains(item.Id)) ||
            (changes?.ChangedItems ?? []).Any(item => exactIds.Contains(item.Id)) ||
            (changes?.DeletedItems ?? []).Any(item => exactIds.Contains(item.Id)))
        {
            throw new InvalidOperationException("Prepared Claim item IDs collide with live profile or response state.");
        }
    }

    private List<Item> SimulateFullTransaction(
        OpeningContext context,
        MongoId caseId,
        MongoId keyId,
        List<Item> preparedItems,
        HashSet<MongoId> exactIds)
    {
        var simulatedProfile = CloneRequired(context.PmcData, "profile for placement simulation");
        var simulatedResponse = CloneRequired(context.Response, "response for placement simulation");
        var changes = SptResponseChanges.GetOrCreate(simulatedResponse, context.ProfileId);
        var baselineNewIds = (changes.NewItems ?? []).Select(item => item.Id).ToHashSet();

        inventoryHelper.RemoveItem(simulatedProfile, caseId, context.ProfileId, simulatedResponse);
        inventoryHelper.RemoveItem(simulatedProfile, keyId, context.ProfileId, simulatedResponse);
        inventoryHelper.AddItemToStash(
            context.ProfileId,
            CreateAddRequest(preparedItems),
            simulatedProfile,
            simulatedResponse);

        EnsureNoWarnings(simulatedResponse);
        var located = (changes.NewItems ?? [])
            .Where(item => !baselineNewIds.Contains(item.Id) && exactIds.Contains(item.Id))
            .ToList();
        ValidateExactRewardTree(preparedItems, located, exactIds, "placement simulation");
        RequireSingleRoot(located, exactIds);
        return located;
    }

    private IReadOnlyList<Item> RequireLiveStake(
        OpeningContext context,
        RelayStake stake,
        bool requireExactAwardedTree)
    {
        var items = RequireInventoryItems(context.PmcData);
        var byId = items.ToDictionary(item => item.Id.ToString(), StringComparer.Ordinal);
        EnsureAcyclicParentGraph(byId);
        if (!byId.TryGetValue(stake.RootId.ToString(), out var root) ||
            root.Template != (MongoId)rewardCatalog.FindReward(stake.RewardId).WeaponTemplateId)
        {
            throw new InvalidOperationException("The authenticated profile does not contain the authoritative Relay stake.");
        }

        var liveTree = GetLiveTree(items, root.Id);
        if (requireExactAwardedTree)
        {
            EnsureExactStakeTree(
                stake.ExpectedItems,
                liveTree,
                stake.RootId,
                RequireRelayRootParentIds(context.PmcData));
        }

        return liveTree;
    }

    private static IReadOnlyList<Item> GetLiveTree(IReadOnlyCollection<Item> items, MongoId rootId)
        => new ItemTreeIndex(items, "live inventory tree").GetTree(rootId);

    internal static void EnsureExactStakeTree(
        IReadOnlyCollection<Item> awardedItems,
        IReadOnlyCollection<Item> liveItems,
        MongoId expectedRootId,
        IReadOnlySet<string> allowedRootParentIds)
    {
        ArgumentNullException.ThrowIfNull(awardedItems);
        ArgumentNullException.ThrowIfNull(liveItems);
        ArgumentNullException.ThrowIfNull(allowedRootParentIds);
        if (allowedRootParentIds.Count == 0)
        {
            throw new InvalidOperationException("The profile has no valid stash placement for a Relay reward.");
        }

        var awardedById = ToUniqueItemDictionary(awardedItems, "awarded Relay tree");
        var liveById = ToUniqueItemDictionary(liveItems, "live Relay tree");
        EnsureAcyclicParentGraph(awardedById.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value, StringComparer.Ordinal));
        EnsureAcyclicParentGraph(liveById.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value, StringComparer.Ordinal));
        if (awardedById.Count != awardedItems.Count || liveById.Count != liveItems.Count ||
            awardedById.Count != liveById.Count || !awardedById.Keys.ToHashSet().SetEquals(liveById.Keys) ||
            SettlementItemTrees.FindRootId(awardedItems) != expectedRootId ||
            SettlementItemTrees.FindRootId(liveItems) != expectedRootId)
        {
            throw new InvalidOperationException("Relay requires the exact awarded reward tree; items are missing, added, or reparented.");
        }

        var liveRoot = liveById[expectedRootId];
        if (liveRoot.ParentId is null || !allowedRootParentIds.Contains(liveRoot.ParentId))
        {
            throw new InvalidOperationException(
                "Relay requires the reward root to be located in the profile stash or sorting table.");
        }

        foreach (var (itemId, awarded) in awardedById)
        {
            var live = liveById[itemId];
            var comparableAwarded = CaseOpeningRecord.CloneItem(awarded);
            var comparableLive = CaseOpeningRecord.CloneItem(live);
            if (itemId == expectedRootId)
            {
                comparableAwarded = NormalizeRootPlacement(comparableAwarded);
                comparableLive = NormalizeRootPlacement(comparableLive);
            }

            if (!ItemsDeepEqual(comparableAwarded, comparableLive))
            {
                throw new InvalidOperationException(
                    "Relay requires the exact awarded reward tree; stable item state changed.");
            }
        }
    }

    internal static void EnsureExactItemState(
        IReadOnlyCollection<Item> expectedItems,
        IReadOnlyCollection<Item> actualItems,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(expectedItems);
        ArgumentNullException.ThrowIfNull(actualItems);
        var expectedById = ToUniqueItemDictionary(expectedItems, operation);
        var actualById = ToUniqueItemDictionary(actualItems, operation);
        if (expectedById.Count != actualById.Count ||
            !expectedById.Keys.ToHashSet().SetEquals(actualById.Keys) ||
            expectedById.Any(pair => !ItemsDeepEqual(pair.Value, actualById[pair.Key])))
        {
            throw new InvalidOperationException($"The {operation} changed stable item state.");
        }
    }

    internal static RewardPresence InspectExactTreePresence(
        IReadOnlyCollection<Item> expectedItems,
        IReadOnlyCollection<Item> inventoryItems,
        MongoId expectedRootId,
        IReadOnlySet<string> allowedRootParentIds)
    {
        ArgumentNullException.ThrowIfNull(expectedItems);
        ArgumentNullException.ThrowIfNull(inventoryItems);
        var expectedIds = expectedItems.Select(item => item.Id).ToHashSet();
        if (expectedIds.Count == 0)
        {
            return RewardPresence.Absent;
        }

        var foundCount = inventoryItems.Count(item => expectedIds.Contains(item.Id));
        if (foundCount == 0)
        {
            return RewardPresence.Absent;
        }

        try
        {
            EnsureExactStakeTree(
                expectedItems,
                GetLiveTree(inventoryItems, expectedRootId),
                expectedRootId,
                allowedRootParentIds);
            return RewardPresence.Complete;
        }
        catch (InvalidOperationException)
        {
            return RewardPresence.Partial;
        }
    }

    internal static IReadOnlySet<string> RequireRelayRootParentIds(PmcData profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var inventory = profile.Inventory
            ?? throw new InvalidOperationException("The authenticated profile inventory is unavailable.");
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        if (inventory.Stash is MongoId stash && !stash.IsEmpty)
        {
            allowed.Add(stash.ToString());
        }
        if (inventory.SortingTable is MongoId sortingTable && !sortingTable.IsEmpty)
        {
            allowed.Add(sortingTable.ToString());
        }
        if (allowed.Count == 0)
        {
            throw new InvalidOperationException("The profile has no stash or sorting-table inventory root.");
        }

        return allowed;
    }

    private static IReadOnlyDictionary<MongoId, Item> ToUniqueItemDictionary(
        IReadOnlyCollection<Item> items,
        string operation)
    {
        var byId = new Dictionary<MongoId, Item>();
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (!byId.TryAdd(item.Id, item))
            {
                throw new InvalidOperationException($"The {operation} contains duplicate item IDs.");
            }
        }

        return byId;
    }

    private static void EnsureExpectedClaimRootPlacement(
        Item root,
        PmcData profile,
        string operation)
    {
        if (root.ParentId is null)
        {
            if (root.SlotId is not null || root.Location is not null)
            {
                throw new InvalidOperationException(
                    $"The {operation} expected root has incomplete placement state.");
            }

            return;
        }

        EnsureLegalClaimRootPlacement(root, profile, operation);
    }

    private static void EnsureLegalClaimRootPlacement(
        Item root,
        PmcData profile,
        string operation)
    {
        var inventory = profile.Inventory
            ?? throw new InvalidOperationException("The authenticated profile inventory is unavailable.");
        var stashParentId = inventory.Stash is MongoId stash && !stash.IsEmpty
            ? stash.ToString()
            : null;
        var sortingParentId = inventory.SortingTable is MongoId sortingTable && !sortingTable.IsEmpty
            ? sortingTable.ToString()
            : null;

        if (stashParentId is null && sortingParentId is null)
        {
            throw new InvalidOperationException("The profile has no valid Claim placement root.");
        }

        if (root.ParentId is null)
        {
            throw new InvalidOperationException(
                $"The {operation} root is not in the profile stash or sorting table.");
        }

        if (stashParentId is not null &&
            string.Equals(root.ParentId, stashParentId, StringComparison.Ordinal))
        {
            if (!string.Equals(root.SlotId, "hideout", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The {operation} stash root has an invalid slot.");
            }
        }
        else if (sortingParentId is not null &&
                 string.Equals(root.ParentId, sortingParentId, StringComparison.Ordinal))
        {
            if (root.SlotId is not null)
            {
                throw new InvalidOperationException(
                    $"The {operation} sorting-table root has an invalid slot.");
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"The {operation} root is not in the profile stash or sorting table.");
        }

        if (root.Location is not ItemLocation location ||
            location.X is not >= 0 ||
            location.Y is not >= 0 ||
            !Enum.IsDefined(location.R) ||
            location.Rotation is bool legacyRotation &&
            legacyRotation != (location.R == ItemRotation.Vertical))
        {
            throw new InvalidOperationException(
                $"The {operation} root has invalid inventory-grid placement.");
        }
    }

    private static void NormalizeSptGrantStateForComparison(
        Item expected,
        Item actual,
        Func<MongoId, bool>? isAmmoTemplate)
    {
        if (expected.Upd?.SpawnedInSession is not null)
        {
            return;
        }

        var classifyAmmo = isAmmoTemplate
            ?? throw new InvalidOperationException("The SPT ammo classifier is unavailable.");
        bool? expectedSptValue = classifyAmmo(expected.Template) ? null : false;
        if (actual.Upd?.SpawnedInSession != expectedSptValue)
        {
            throw new InvalidOperationException(
                "SPT changed Claim grant state outside the documented found-in-raid normalization.");
        }

        if (actual.Upd?.SpawnedInSession is false)
        {
            actual.Upd.SpawnedInSession = null;
        }
    }

    private static Item NormalizeRootPlacement(Item item) => item with
    {
        ParentId = null,
        SlotId = null,
        Location = null
    };

    private void ApplyExactLeafRemovals(
        OpeningContext context,
        IReadOnlyList<MongoId> exactIds,
        Func<ManifestInventoryPresence> inspect,
        string operation)
    {
        EnsureNoWarnings(context.Response);
        if (exactIds.Count == 0 || exactIds.Any(id => id.IsEmpty) ||
            exactIds.Distinct().Count() != exactIds.Count)
        {
            throw new InvalidOperationException($"The {operation} has invalid prepared input IDs.");
        }
        if (inspect() != ManifestInventoryPresence.Present)
        {
            throw new InvalidOperationException(
                $"The {operation} inputs no longer match the authenticated profile.");
        }

        var consumedIds = exactIds.ToHashSet();
        var beforeInventory = RequireInventoryItems(context.PmcData)
            .Select(CaseOpeningRecord.CloneItem)
            .ToArray();
        var changes = SptResponseChanges.GetOrCreate(context.Response, context.ProfileId);
        var beforeNewItems = (changes.NewItems ?? []).Select(CaseOpeningRecord.CloneItem).ToArray();
        var beforeChangedItems = (changes.ChangedItems ?? []).Select(CaseOpeningRecord.CloneItem).ToArray();
        var beforeDeletedIds = (changes.DeletedItems ?? []).Select(item => item.Id).ToArray();

        foreach (var itemId in exactIds)
        {
            removeItem(context.PmcData, itemId, context.ProfileId, context.Response);
        }

        EnsureNoWarnings(context.Response);
        var afterInventory = RequireInventoryItems(context.PmcData);
        if (afterInventory.Any(item => consumedIds.Contains(item.Id)) ||
            afterInventory.Count != beforeInventory.Length - consumedIds.Count)
        {
            throw new InvalidOperationException(
                $"The {operation} did not remove exactly its prepared inputs.");
        }

        var expectedSurvivors = beforeInventory
            .Where(item => !consumedIds.Contains(item.Id))
            .ToDictionary(item => item.Id);
        var actualSurvivors = afterInventory.ToDictionary(item => item.Id);
        if (expectedSurvivors.Count != actualSurvivors.Count ||
            !expectedSurvivors.Keys.ToHashSet().SetEquals(actualSurvivors.Keys) ||
            expectedSurvivors.Any(pair => !ItemsDeepEqual(pair.Value, actualSurvivors[pair.Key])))
        {
            throw new InvalidOperationException(
                $"The {operation} changed inventory outside its prepared inputs.");
        }

        var afterChanges = GetExistingItemChanges(context.Response, context.ProfileId)
            ?? throw new InvalidOperationException($"The {operation} produced no response item changes.");
        var afterNewItems = afterChanges.NewItems ?? [];
        var afterChangedItems = afterChanges.ChangedItems ?? [];
        var afterDeletedIds = (afterChanges.DeletedItems ?? []).Select(item => item.Id).ToArray();
        if (afterNewItems.Count != beforeNewItems.Length ||
            afterChangedItems.Count != beforeChangedItems.Length ||
            beforeNewItems.Where((item, index) => !ItemsDeepEqual(item, afterNewItems[index])).Any() ||
            beforeChangedItems.Where((item, index) => !ItemsDeepEqual(item, afterChangedItems[index])).Any() ||
            afterDeletedIds.Length != beforeDeletedIds.Length + exactIds.Count ||
            !afterDeletedIds.Take(beforeDeletedIds.Length).SequenceEqual(beforeDeletedIds) ||
            !afterDeletedIds.Skip(beforeDeletedIds.Length).SequenceEqual(exactIds))
        {
            throw new InvalidOperationException(
                $"The {operation} response did not report exactly its prepared deletions.");
        }
    }

    private static ManifestInventoryPresence InspectExactLeafInputs(
        IReadOnlyCollection<Item> inventoryItems,
        IReadOnlyCollection<(MongoId Id, MongoId Template)> expectedInputs)
    {
        ArgumentNullException.ThrowIfNull(inventoryItems);
        ArgumentNullException.ThrowIfNull(expectedInputs);
        if (expectedInputs.Count == 0 ||
            expectedInputs.Any(input => input.Id.IsEmpty || input.Template.IsEmpty) ||
            expectedInputs.Select(input => input.Id).Distinct().Count() != expectedInputs.Count)
        {
            throw new ArgumentException("Prepared Manifest inputs must be non-empty and unique.", nameof(expectedInputs));
        }

        var exactIds = expectedInputs.Select(input => input.Id).ToHashSet();
        var exactIdStrings = exactIds.Select(id => id.ToString()).ToHashSet(StringComparer.Ordinal);
        var matchingIds = inventoryItems.Where(item => exactIds.Contains(item.Id)).ToArray();
        var hasDescendant = inventoryItems.Any(item =>
            item.ParentId is not null && exactIdStrings.Contains(item.ParentId));
        if (matchingIds.Length == 0 && !hasDescendant)
        {
            return ManifestInventoryPresence.Absent;
        }

        if (hasDescendant || matchingIds.Length != expectedInputs.Count ||
            matchingIds.Select(item => item.Id).Distinct().Count() != expectedInputs.Count)
        {
            return ManifestInventoryPresence.Partial;
        }

        var actualById = matchingIds.ToDictionary(item => item.Id);
        return expectedInputs.All(input =>
            actualById.TryGetValue(input.Id, out var item) && item.Template == input.Template)
            ? ManifestInventoryPresence.Present
            : ManifestInventoryPresence.Partial;
    }

    private static void EnsureFreshPreparedTicket(ManifestTicketPayload prepared)
    {
        if (prepared.ProfileCommitStarted || prepared.Committed ||
            prepared.CommitGeneration is null || prepared.CommitPredecessorHash is null)
        {
            throw new ArgumentException(
                "Only a fresh planned Manifest ticket can be applied.",
                nameof(prepared));
        }
    }

    private static void EnsureFreshPreparedRelay(ManifestRelayPreparedPayload prepared)
    {
        if (prepared.ProfileCommitStarted ||
            prepared.CommitGeneration is null || prepared.CommitPredecessorHash is null)
        {
            throw new ArgumentException(
                "Only a fresh planned Manifest Relay key can be applied.",
                nameof(prepared));
        }
    }

    private static void ReplayExactDeletedItems(
        ItemEventRouterResponse response,
        MongoId profileId,
        IReadOnlyList<MongoId> exactIds,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (exactIds.Count == 0 || exactIds.Any(id => id.IsEmpty) ||
            exactIds.Distinct().Count() != exactIds.Count)
        {
            throw new InvalidOperationException($"The {operation} has invalid replay IDs.");
        }

        var changes = SptResponseChanges.GetOrCreate(response, profileId);
        var replayIds = exactIds.ToHashSet();
        if ((changes.NewItems ?? []).Any(item => replayIds.Contains(item.Id)) ||
            (changes.ChangedItems ?? []).Any(item => replayIds.Contains(item.Id)) ||
            (changes.DeletedItems ?? []).Any(item => replayIds.Contains(item.Id)))
        {
            throw new InvalidOperationException($"The {operation} replay would collide with response changes.");
        }

        changes.DeletedItems!.AddRange(exactIds.Select(id => new DeletedItem { Id = id }));
    }

    private static Item SelectLowestRelayKeyLeaf(
        IReadOnlyCollection<Item> inventoryItems,
        IReadOnlySet<MongoId> excludedIds)
    {
        ArgumentNullException.ThrowIfNull(inventoryItems);
        ArgumentNullException.ThrowIfNull(excludedIds);
        var byId = inventoryItems.ToDictionary(item => item.Id.ToString(), StringComparer.Ordinal);
        EnsureAcyclicParentGraph(byId);
        var parentIds = inventoryItems
            .Where(item => item.ParentId is not null)
            .Select(item => item.ParentId!)
            .ToHashSet(StringComparer.Ordinal);
        return inventoryItems
            .Where(item =>
                item.Template == (MongoId)ModConstants.KeyTemplateId &&
                !item.Id.IsEmpty &&
                !excludedIds.Contains(item.Id) &&
                !parentIds.Contains(item.Id.ToString()))
            .OrderBy(item => item.Id.ToString(), StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new RelayKeyRequiredException();
    }

    private static void EnsureItemIsLeaf(
        IReadOnlyCollection<Item> inventoryItems,
        MongoId itemId,
        string description)
    {
        var byId = inventoryItems.ToDictionary(item => item.Id.ToString(), StringComparer.Ordinal);
        EnsureAcyclicParentGraph(byId);
        if (inventoryItems.Any(item =>
                string.Equals(item.ParentId, itemId.ToString(), StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"The {description} must not contain attached items.");
        }
    }

    private static void EnsureKeyIsLeaf(IEnumerable<Item> inventoryItems, MongoId keyId)
    {
        var items = inventoryItems.ToArray();
        var byId = items.ToDictionary(item => item.Id.ToString(), StringComparer.Ordinal);
        EnsureAcyclicParentGraph(byId);
        if (items.Any(item => string.Equals(item.ParentId, keyId.ToString(), StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("BR-12 Relay Key instances must not contain attached items.");
        }
    }

    internal static Item SelectLowestRelayKey(
        IEnumerable<Item> inventoryItems,
        IReadOnlySet<MongoId> excludedIds)
    {
        ArgumentNullException.ThrowIfNull(inventoryItems);
        ArgumentNullException.ThrowIfNull(excludedIds);
        return inventoryItems
            .Where(item => item.Template == (MongoId)ModConstants.KeyTemplateId && !excludedIds.Contains(item.Id))
            .OrderBy(item => item.Id.ToString(), StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new RelayKeyRequiredException();
    }

    private static AddItemDirectRequest CreateAddRequest(List<Item> items) => new()
    {
        ItemWithModsToAdd = items,
        FoundInRaid = false,
        UseSortingTable = true
    };

    private static void MissingAddItemToStash(
        MongoId profileId,
        AddItemDirectRequest request,
        PmcData profile,
        ItemEventRouterResponse response) =>
        throw new InvalidOperationException("The SPT inventory helper is unavailable.");

    private static bool MissingAmmoTemplateClassifier(MongoId templateId) =>
        throw new InvalidOperationException("The SPT ammo classifier is unavailable.");

    private static void MissingRemoveItem(
        PmcData profile,
        MongoId itemId,
        MongoId profileId,
        ItemEventRouterResponse response) =>
        throw new InvalidOperationException("The SPT inventory removal helper is unavailable.");

    private static CaseOpeningRecord ValidateAppliedChanges(
        OpeningContext context,
        CaseOpeningRecord record,
        ItemChanges changes)
    {
        var exactIds = record.ExactRewardIds.ToHashSet();
        var responseRewards = (changes.NewItems ?? []).Where(item => exactIds.Contains(item.Id)).ToList();

        var deletedIds = (changes.DeletedItems ?? []).Select(item => item.Id).ToHashSet();
        if (!deletedIds.Contains(record.CaseId) || !deletedIds.Contains(record.KeyId))
        {
            throw new InvalidOperationException("Live settlement did not report both consumed inputs.");
        }

        var profileItems = RequireInventoryItems(context.PmcData);
        if (profileItems.Any(item => item.Id == record.CaseId || item.Id == record.KeyId))
        {
            throw new InvalidOperationException("Live settlement left a consumed input in the profile.");
        }

        var storedRewards = profileItems.Where(item => exactIds.Contains(item.Id)).ToList();
        return ReconcileAppliedRecord(record, responseRewards, storedRewards);
    }

    internal static CaseOpeningRecord ReconcileAppliedRecord(
        CaseOpeningRecord preparedRecord,
        IReadOnlyCollection<Item> responseRewards,
        IReadOnlyCollection<Item> storedRewards)
    {
        ArgumentNullException.ThrowIfNull(preparedRecord);
        ArgumentNullException.ThrowIfNull(responseRewards);
        ArgumentNullException.ThrowIfNull(storedRewards);

        var exactIds = preparedRecord.ExactRewardIds.ToHashSet();
        ValidateExactRewardTree(preparedRecord.RewardItems, responseRewards, exactIds, "live response");
        ValidateExactRewardTree(preparedRecord.RewardItems, storedRewards, exactIds, "profile settlement");
        RequireSingleRoot(responseRewards, exactIds);
        RequireSingleRoot(storedRewards, exactIds);
        ValidateMatchingLivePayload(responseRewards, storedRewards);

        return new CaseOpeningRecord(
            preparedRecord.CaseId,
            preparedRecord.KeyId,
            preparedRecord.RewardId,
            responseRewards.ToList(),
            preparedRecord.PreparedAtUtc,
            OpeningRecordStatus.Prepared,
            null,
            preparedRecord.RarityLadderVersion);
    }

    internal static void EnsureConsumedItemsAreLeaves(
        IEnumerable<Item> inventoryItems,
        MongoId caseId,
        MongoId keyId)
    {
        ArgumentNullException.ThrowIfNull(inventoryItems);
        var itemsById = new Dictionary<string, Item>(StringComparer.Ordinal);
        foreach (var item in inventoryItems)
        {
            ArgumentNullException.ThrowIfNull(item);
            var itemId = item.Id.ToString();
            if (string.IsNullOrEmpty(itemId) || !itemsById.TryAdd(itemId, item))
            {
                throw new InvalidOperationException(
                    "The profile inventory has ambiguous item identifiers.");
            }
        }

        EnsureAcyclicParentGraph(itemsById);
        var consumedIds = new HashSet<string>(StringComparer.Ordinal)
        {
            caseId.ToString(),
            keyId.ToString()
        };
        foreach (var item in itemsById.Values)
        {
            var parentId = item.ParentId;
            while (parentId is not null && itemsById.TryGetValue(parentId, out var parent))
            {
                if (consumedIds.Contains(parentId))
                {
                    throw new InvalidOperationException(
                        "BR-12 case and key instances must not contain attached items.");
                }

                parentId = parent.ParentId;
            }
        }
    }

    private static void EnsureAcyclicParentGraph(IReadOnlyDictionary<string, Item> itemsById)
    {
        var states = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (var startId in itemsById.Keys)
        {
            if (states.ContainsKey(startId))
            {
                continue;
            }

            var path = new List<string>();
            var currentId = startId;
            while (itemsById.TryGetValue(currentId, out var current))
            {
                if (states.TryGetValue(currentId, out var state))
                {
                    if (state == 1)
                    {
                        throw new InvalidOperationException("The profile inventory contains a parent cycle.");
                    }

                    break;
                }

                states.Add(currentId, 1);
                path.Add(currentId);
                if (current.ParentId is null || !itemsById.ContainsKey(current.ParentId))
                {
                    break;
                }

                currentId = current.ParentId;
            }

            foreach (var itemId in path)
            {
                states[itemId] = 2;
            }
        }
    }

    private static void ValidateExactRewardTree(
        IEnumerable<Item> expectedItems,
        IReadOnlyCollection<Item> actualItems,
        HashSet<MongoId> exactIds,
        string operation)
    {
        var expectedById = expectedItems.ToDictionary(item => item.Id);
        if (actualItems.Count != exactIds.Count ||
            actualItems.Select(item => item.Id).ToHashSet().Count != exactIds.Count ||
            actualItems.Any(item => !expectedById.TryGetValue(item.Id, out var expected) || item.Template != expected.Template))
        {
            throw new InvalidOperationException($"The {operation} did not preserve the exact prepared reward tree.");
        }
    }

    private static void ValidateMatchingLivePayload(
        IReadOnlyCollection<Item> responseRewards,
        IReadOnlyCollection<Item> storedRewards)
    {
        var storedById = storedRewards.ToDictionary(item => item.Id);
        if (responseRewards.Any(item => !ItemsDeepEqual(item, storedById[item.Id])))
        {
            throw new InvalidOperationException(
                "Live response reward payload does not match the profile reward payload.");
        }
    }

    private static void RequireSingleRoot(IReadOnlyCollection<Item> items, HashSet<MongoId> exactIds)
    {
        var exactIdStrings = exactIds.Select(id => id.ToString()).ToHashSet(StringComparer.Ordinal);
        if (items.Count(item => item.ParentId is null || !exactIdStrings.Contains(item.ParentId)) != 1)
        {
            throw new InvalidOperationException("A prepared reward must contain exactly one root item tree.");
        }
    }

    private static List<Item> RequireInventoryItems(PmcData profile) =>
        profile.Inventory?.Items
        ?? throw new InvalidOperationException("The authenticated profile inventory is unavailable.");

    private static void EnsureNoWarnings(ItemEventRouterResponse response)
    {
        if (response.Warnings is { Count: > 0 })
        {
            throw new InvalidOperationException("SPT reported an inventory warning during case settlement.");
        }
    }

    private static bool HasOnlyNoSpaceWarnings(ItemEventRouterResponse response) =>
        response.Warnings is { Count: > 0 } warnings &&
        warnings.All(warning => warning.Code == BackendErrorCodes.NotEnoughSpace);

    private T CloneRequired<T>(T? value, string description) where T : class =>
        cloner.Clone(value) ?? throw new InvalidOperationException($"Could not clone {description}.");

    private static bool ItemsDeepEqual(Item responseItem, Item storedItem)
    {
        try
        {
            var responseNode = JsonSerializer.SerializeToNode(responseItem, ItemComparisonOptions);
            var storedNode = JsonSerializer.SerializeToNode(storedItem, ItemComparisonOptions);
            return JsonNode.DeepEquals(responseNode, storedNode);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Could not compare the live reward payloads.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidOperationException("Could not compare the live reward payloads.", exception);
        }
    }

    private static JsonSerializerOptions CreateItemComparisonOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new StringToMongoIdConverter());
        return options;
    }

    private sealed class ItemTreeIndex
    {
        private readonly IReadOnlyList<Item> orderedItems;
        private readonly IReadOnlyDictionary<string, Item> itemsById;
        private readonly IReadOnlyDictionary<string, List<string>> childrenByParentId;

        public ItemTreeIndex(IReadOnlyCollection<Item> items, string operation)
        {
            ArgumentNullException.ThrowIfNull(items);
            ArgumentException.ThrowIfNullOrWhiteSpace(operation);
            if (items.Count > MaxIndexedInventoryItemCount)
            {
                throw new InvalidOperationException(
                    $"The {operation} exceeds the supported live-item limit.");
            }

            var ordered = new Item[items.Count];
            var indexedItems = new Dictionary<string, Item>(items.Count, StringComparer.Ordinal);
            var indexedChildren = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in items)
            {
                if (item is null || item.Id.IsEmpty)
                {
                    throw new InvalidOperationException(
                        $"The {operation} contains an item with an empty ID.");
                }

                var itemId = item.Id.ToString();
                if (!indexedItems.TryAdd(itemId, item))
                {
                    throw new InvalidOperationException(
                        $"The {operation} contains duplicate item IDs.");
                }

                ordered[index++] = item;
                if (item.ParentId is not null)
                {
                    if (!indexedChildren.TryGetValue(item.ParentId, out var children))
                    {
                        children = [];
                        indexedChildren.Add(item.ParentId, children);
                    }

                    children.Add(itemId);
                }
            }

            orderedItems = ordered;
            itemsById = indexedItems;
            childrenByParentId = indexedChildren;
        }

        public IReadOnlyList<Item> GetTree(MongoId rootId)
        {
            if (rootId.IsEmpty)
            {
                throw new ArgumentException("A live item tree root ID is required.", nameof(rootId));
            }

            var rootIdText = rootId.ToString();
            if (!itemsById.ContainsKey(rootIdText))
            {
                return [];
            }

            var descendantIds = new HashSet<string>(StringComparer.Ordinal) { rootIdText };
            var pendingIds = new Queue<string>();
            pendingIds.Enqueue(rootIdText);
            while (pendingIds.TryDequeue(out var parentId))
            {
                if (!childrenByParentId.TryGetValue(parentId, out var children))
                {
                    continue;
                }

                foreach (var childId in children)
                {
                    if (descendantIds.Add(childId))
                    {
                        pendingIds.Enqueue(childId);
                    }
                }
            }

            return orderedItems.Where(item => descendantIds.Contains(item.Id.ToString())).ToArray();
        }
    }

    private static double SecureUnitValue()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var value = BitConverter.ToUInt64(bytes) >> 11;
        return value * (1d / 9_007_199_254_740_992d);
    }

    private sealed record SptInventoryCheckpoint(
        BotBaseInventory Inventory,
        List<InsuredItem> InsuredItems,
        List<Warning>? Warnings,
        ItemChanges? ItemChanges,
        bool HadProfileChangesDictionary,
        bool HadProfileChange,
        bool HadItems,
        string? ClaimCommitWitnessToken,
        ManifestInputCommitWitnessTokens ManifestInputCommitWitnessTokens) : InventoryCheckpoint;
}

internal static class SptResponseChanges
{
    public static ItemChanges GetOrCreate(ItemEventRouterResponse response, MongoId profileId)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.ProfileChanges ??= [];
        if (!response.ProfileChanges.TryGetValue(profileId, out var profileChange))
        {
            profileChange = new ProfileChange { Id = profileId.ToString() };
            response.ProfileChanges.Add(profileId, profileChange);
        }

        profileChange.Items ??= new ItemChanges();
        profileChange.Items.NewItems ??= [];
        profileChange.Items.ChangedItems ??= [];
        profileChange.Items.DeletedItems ??= [];
        return profileChange.Items;
    }

    public static void Replay(ItemEventRouterResponse response, MongoId profileId, CaseOpeningRecord record)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(record);
        if (response.Warnings is { Count: > 0 })
        {
            throw new InvalidOperationException("Cannot replay settlement into a response that already contains warnings.");
        }

        var changes = GetOrCreate(response, profileId);
        var settlementIds = record.ExactRewardIds.ToHashSet();
        settlementIds.Add(record.CaseId);
        settlementIds.Add(record.KeyId);
        if (changes.NewItems!.Any(item => settlementIds.Contains(item.Id)) ||
            changes.ChangedItems!.Any(item => settlementIds.Contains(item.Id)) ||
            changes.DeletedItems.Any(item => settlementIds.Contains(item.Id)))
        {
            throw new InvalidOperationException("Settlement replay would collide with existing response changes.");
        }

        changes.NewItems!.AddRange(record.RewardItems);
        changes.DeletedItems.Add(new DeletedItem { Id = record.CaseId });
        changes.DeletedItems.Add(new DeletedItem { Id = record.KeyId });
    }

    public static void ReplayRelay(ItemEventRouterResponse response, MongoId profileId, RelaySettlementRecord record)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(record);
        if (record.Action != RelayRecordAction.Relay || record.KeyId is not MongoId keyId)
        {
            throw new ArgumentException("Only inventory-mutating Relay records can be replayed.", nameof(record));
        }
        if (response.Warnings is { Count: > 0 })
        {
            throw new InvalidOperationException("Cannot replay Relay settlement into a response that already contains warnings.");
        }

        var changes = GetOrCreate(response, profileId);
        var settlementIds = record.InputItemIds.Concat(record.ExactOutputIds).ToHashSet();
        settlementIds.Add(keyId);
        if (changes.NewItems!.Any(item => settlementIds.Contains(item.Id)) ||
            changes.ChangedItems!.Any(item => settlementIds.Contains(item.Id)) ||
            changes.DeletedItems.Any(item => settlementIds.Contains(item.Id)))
        {
            throw new InvalidOperationException("Relay replay would collide with existing response changes.");
        }

        changes.NewItems!.AddRange(record.OutputItems);
        changes.DeletedItems.AddRange(record.InputItemIds.Select(id => new DeletedItem { Id = id }));
        changes.DeletedItems.Add(new DeletedItem { Id = keyId });
    }
}
