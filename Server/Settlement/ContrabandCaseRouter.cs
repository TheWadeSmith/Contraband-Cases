using System.Text.Json;
using System.Text.Json.Serialization;
using ContrabandCases.Server.Configuration;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI.Routing;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Request;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace ContrabandCases.Server.Settlement;

[Injectable(TypePriority = int.MinValue)]
public sealed class ContrabandCaseRouter : ItemEventRouter
{
    public ContrabandCaseRouter(
        SptCaseJournal journal,
        SptOpeningInventory inventory,
        SptProfileCommitter committer,
        ProfileLockPool profileLocks,
        RaidSessionState raidSessions,
        ServerRewardCatalog rewardCatalog,
        CatalogSnapshotCoordinator catalogCoordinator,
        TemplateTable templates,
        ManifestClaimCommitUncertaintyCoordinator manifestCommitUncertainty,
        TestingInventoryGrantGate testingInventoryGrantGate,
        TestingForcedCrateRegistry testingForcedCrateRegistry,
        ISptLogger<ContrabandCaseRouter> logger)
        : base(CreateRoutes(
            journal,
            inventory,
            committer,
            profileLocks,
            raidSessions,
            rewardCatalog,
            catalogCoordinator,
            templates,
            manifestCommitUncertainty,
            testingInventoryGrantGate,
            testingForcedCrateRegistry,
            logger))
    {
    }

    internal static void ValidateRequest(string url, OpenRandomLootContainerRequestData request)
    {
        if (!string.Equals(url, ModConstants.OpenAction, StringComparison.Ordinal) ||
            !string.Equals(request.Action, ModConstants.OpenAction, StringComparison.Ordinal) ||
            request.Item.IsEmpty)
        {
            throw new InvalidOperationException("Contraband Cases received an invalid opening request.");
        }
    }

    internal static void ValidateRequest(
        string url,
        OpenRandomLootContainerRequestData request,
        RaidSessionState raidSessions,
        MongoId profileId)
    {
        ArgumentNullException.ThrowIfNull(raidSessions);
        ValidateRequest(url, request);
        raidSessions.RequireLobby(profileId);
    }

    internal static void ValidateRelayRequest(string url, OpenRandomLootContainerRequestData request)
    {
        if ((url != ModConstants.RelaySecureAction && url != ModConstants.RelayAction) ||
            !string.Equals(request.Action, url, StringComparison.Ordinal) ||
            request.Item.IsEmpty)
        {
            throw new InvalidOperationException("Contraband Cases received an invalid Relay request.");
        }
    }

    internal static void ValidateTestingInventoryGrantRequest(
        string url,
        TestingInventoryGrantRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(url, ModConstants.TestingInventoryGrantAction, StringComparison.Ordinal) ||
            !string.Equals(request.Action, ModConstants.TestingInventoryGrantAction, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Contraband Cases received an invalid testing inventory grant request.");
        }

        TestingInventoryGrantPolicy.Validate(request.CaseCount, request.KeyCount);
    }

    internal static ManifestOfferDecision ValidateManifestDecisionRequest(
        string url,
        ManifestDecisionRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var decision = url switch
        {
            ModConstants.ManifestLockAction => ManifestOfferDecision.Lock,
            ModConstants.ManifestBurnAction => ManifestOfferDecision.Burn,
            _ => throw new InvalidOperationException(
                "Contraband Cases received an invalid Manifest decision route.")
        };
        if (!string.Equals(request.Action, url, StringComparison.Ordinal) ||
            request.ExpectedOrdinal is < 1 or > 2 ||
            request.SelectedOrdinal is < 1 or > 3 ||
            request.SelectedOrdinal is not null && (decision != ManifestOfferDecision.Lock || request.ExpectedOrdinal != 1))
        {
            throw new InvalidOperationException(
                "Contraband Cases received an invalid Manifest decision request.");
        }

        ValidateManifestRequestEnvelope(request);
        ValidateManifestIdentifier(request.ManifestId);
        return decision;
    }

    internal static ManifestPhase ValidateManifestClaimRequest(
        string url,
        ManifestClaimRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(url, ModConstants.ManifestClaimAction, StringComparison.Ordinal) ||
            !string.Equals(request.Action, url, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Contraband Cases received an invalid Manifest Claim request.");
        }

        ValidateManifestRequestEnvelope(request);
        ValidateManifestIdentifier(request.ManifestId);
        return ParseManifestPhase(
            request.ExpectedPhase,
            ManifestPhase.Entitlement,
            ManifestPhase.ClaimPrepared,
            ManifestPhase.RewardOwed);
    }

    internal static (ManifestPhase ExpectedPhase, int ExpectedRelayStage)
        ValidateManifestRelayRequest(
            string url,
            ManifestRelayRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(url, ModConstants.ManifestRelayAction, StringComparison.Ordinal) ||
            !string.Equals(request.Action, url, StringComparison.Ordinal) ||
            request.ExpectedRelayStage is < 1 or > RelayRules.MaximumStage)
        {
            throw new InvalidOperationException(
                "Contraband Cases received an invalid Manifest Relay request.");
        }

        ValidateManifestRequestEnvelope(request);
        ValidateManifestIdentifier(request.ManifestId);
        var phase = ParseManifestPhase(
            request.ExpectedPhase,
            ManifestPhase.Entitlement,
            ManifestPhase.RelayPrepared);
        return (phase, request.ExpectedRelayStage);
    }

    internal static ManifestPhase ValidateManifestForfeitRequest(
        string url,
        ManifestForfeitRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(url, ModConstants.ManifestForfeitAction, StringComparison.Ordinal) ||
            !string.Equals(request.Action, url, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Contraband Cases received an invalid Manifest Forfeit request.");
        }

        ValidateManifestRequestEnvelope(request);
        ValidateManifestIdentifier(request.ManifestId);
        return ParseManifestPhase(
            request.ExpectedPhase,
            ManifestPhase.Offer1,
            ManifestPhase.Offer2,
            ManifestPhase.Entitlement);
    }

    internal static bool ShouldUseLegacyOpening(
        CaseOpeningJournal journal,
        MongoId caseId)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return journal.Find(caseId) is not null;
    }

    internal static ItemEventRouterResponse RejectMissingKey(ItemEventRouterResponse output)
    {
        ArgumentNullException.ThrowIfNull(output);
        // Use SPT's ordinary item-event warning contract, never a fabricated error code.
        output.Warnings ??= [];
        output.Warnings.Add(new Warning
        {
            Code = BackendErrorCodes.HTTPBadRequest,
            ErrorMessage = new RelayKeyRequiredException().Message
        });
        return output;
    }

    private static IEnumerable<ItemRouteAction> CreateRoutes(
        SptCaseJournal journal,
        SptOpeningInventory inventory,
        SptProfileCommitter committer,
        ProfileLockPool profileLocks,
        RaidSessionState raidSessions,
        ServerRewardCatalog rewardCatalog,
        CatalogSnapshotCoordinator catalogCoordinator,
        TemplateTable templates,
        ManifestClaimCommitUncertaintyCoordinator manifestCommitUncertainty,
        TestingInventoryGrantGate testingInventoryGrantGate,
        TestingForcedCrateRegistry testingForcedCrateRegistry,
        ISptLogger<ContrabandCaseRouter> logger)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(templates.Items);
        var legacyOpeningService = new CaseOpeningService(
            journal, inventory, inventory, committer, profileLocks, raidSessions);
        var legacyRelayService = new RelaySettlementService(
            journal, inventory, inventory, committer, profileLocks, raidSessions, rewardCatalog);
        var materializer = new CargoLotMaterializer(
            templateId => templates.Items.GetValueOrDefault((MongoId)templateId));
        var manifestService = new ManifestSettlementService(
            journal,
            inventory,
            committer,
            profileLocks,
            raidSessions,
            materializer,
            uncertaintyCoordinator: manifestCommitUncertainty,
            ticketInventory: inventory,
            relayKeyInventory: inventory,
            catalogCoordinator: catalogCoordinator,
            catalogSelector: new ManifestCatalogSelector(),
            forcedCrateRegistry: testingForcedCrateRegistry);
        return
        [
            CreateOpenRoute(
                journal,
                legacyOpeningService,
                manifestService,
                profileLocks,
                raidSessions,
                logger),
            CreateManifestDecisionRoute(
                ModConstants.ManifestLockAction,
                manifestService,
                logger),
            CreateManifestDecisionRoute(
                ModConstants.ManifestBurnAction,
                manifestService,
                logger),
            CreateManifestClaimRoute(manifestService, logger),
            CreateManifestRelayRoute(manifestService, logger),
            CreateManifestForfeitRoute(manifestService, logger),
            CreateRelayRoute(
                ModConstants.RelaySecureAction,
                legacyRelayService,
                raidSessions,
                logger),
            CreateRelayRoute(
                ModConstants.RelayAction,
                legacyRelayService,
                raidSessions,
                logger),
            CreateTestingInventoryGrantRoute(
                journal,
                inventory,
                committer,
                profileLocks,
                raidSessions,
                testingInventoryGrantGate,
                testingForcedCrateRegistry,
                logger)
        ];
    }

    private static ItemRouteAction<ManifestOpenRequestData> CreateOpenRoute(
        ICaseOpeningJournalStore journalStore,
        CaseOpeningService legacyService,
        ManifestSettlementService manifestService,
        ProfileLockPool profileLocks,
        RaidSessionState raidSessions,
        ISptLogger<ContrabandCaseRouter> logger)
    {
        return new ItemRouteAction<ManifestOpenRequestData>(
            ModConstants.OpenAction,
            async (url, pmcData, request, sessionId, output, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(pmcData);
                ArgumentNullException.ThrowIfNull(request);
                ArgumentNullException.ThrowIfNull(output);
                if (pmcData.Inventory?.Items is null)
                {
                    throw new InvalidOperationException("The authenticated profile inventory is unavailable.");
                }

                ValidateRequest(url, request, raidSessions, sessionId);
                var context = new OpeningContext(pmcData, output, sessionId, sessionId.ToString());
                try
                {
                    await using var profileLock = await profileLocks
                        .AcquireAsync(sessionId, cancellationToken)
                        .ConfigureAwait(false);
                    var journal = await journalStore
                        .LoadAsync(sessionId, cancellationToken)
                        .ConfigureAwait(false);
                    return ShouldUseLegacyOpening(journal, request.Item)
                        ? await legacyService
                            .OpenAsync(context, request.Item, cancellationToken)
                            .ConfigureAwait(false)
                        : await manifestService
                            .OpenAsync(context, request.Item, cancellationToken, request.ExpectedCatalogSnapshotId ?? string.Empty)
                            .ConfigureAwait(false);
                }
                catch (RelayKeyRequiredException)
                {
                    return RejectMissingKey(output);
                }
                catch
                {
                    logger.Error(
                        $"action={ModConstants.OpenAction} caseId={request.Item} profileId={context.ProfileId}");
                    throw;
                }
            });
    }

    private static ItemRouteAction<ManifestDecisionRequestData> CreateManifestDecisionRoute(
        string action,
        ManifestSettlementService service,
        ISptLogger<ContrabandCaseRouter> logger) =>
        new(
            action,
            async (url, pmcData, request, sessionId, output, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(pmcData);
                ArgumentNullException.ThrowIfNull(request);
                ArgumentNullException.ThrowIfNull(output);
                var decision = ValidateManifestDecisionRequest(url, request);
                var context = new OpeningContext(pmcData, output, sessionId, sessionId.ToString());
                try
                {
                    return await service.DecideOfferAsync(
                            context,
                            request.ManifestId,
                            request.ExpectedOrdinal,
                            decision,
                            cancellationToken,
                            request.SelectedOrdinal)
                        .ConfigureAwait(false);
                }
                catch
                {
                    logger.Error(
                        $"action={action} manifestId={request.ManifestId} " +
                        $"expectedOrdinal={request.ExpectedOrdinal} profileId={sessionId}");
                    throw;
                }
            });

    private static ItemRouteAction<ManifestClaimRequestData> CreateManifestClaimRoute(
        ManifestSettlementService service,
        ISptLogger<ContrabandCaseRouter> logger) =>
        new(
            ModConstants.ManifestClaimAction,
            async (url, pmcData, request, sessionId, output, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(pmcData);
                ArgumentNullException.ThrowIfNull(request);
                ArgumentNullException.ThrowIfNull(output);
                var expectedPhase = ValidateManifestClaimRequest(url, request);
                var context = new OpeningContext(pmcData, output, sessionId, sessionId.ToString());
                try
                {
                    var result = await service.ClaimAsync(
                            context,
                            request.ManifestId,
                            expectedPhase,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return result.Response;
                }
                catch
                {
                    logger.Error(
                        $"action={ModConstants.ManifestClaimAction} manifestId={request.ManifestId} " +
                        $"expectedPhase={request.ExpectedPhase} profileId={sessionId}");
                    throw;
                }
            });

    private static ItemRouteAction<ManifestRelayRequestData> CreateManifestRelayRoute(
        ManifestSettlementService service,
        ISptLogger<ContrabandCaseRouter> logger) =>
        new(
            ModConstants.ManifestRelayAction,
            async (url, pmcData, request, sessionId, output, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(pmcData);
                ArgumentNullException.ThrowIfNull(request);
                ArgumentNullException.ThrowIfNull(output);
                var validated = ValidateManifestRelayRequest(url, request);
                var context = new OpeningContext(pmcData, output, sessionId, sessionId.ToString());
                try
                {
                    return await service.RelayAsync(
                            context,
                            request.ManifestId,
                            validated.ExpectedPhase,
                            validated.ExpectedRelayStage,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (RelayKeyRequiredException)
                {
                    return RejectMissingKey(output);
                }
                catch
                {
                    logger.Error(
                        $"action={ModConstants.ManifestRelayAction} manifestId={request.ManifestId} " +
                        $"expectedPhase={request.ExpectedPhase} " +
                        $"expectedRelayStage={request.ExpectedRelayStage} profileId={sessionId}");
                    throw;
                }
            });

    private static ItemRouteAction<ManifestForfeitRequestData> CreateManifestForfeitRoute(
        ManifestSettlementService service,
        ISptLogger<ContrabandCaseRouter> logger) =>
        new(
            ModConstants.ManifestForfeitAction,
            async (url, pmcData, request, sessionId, output, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(pmcData);
                ArgumentNullException.ThrowIfNull(request);
                ArgumentNullException.ThrowIfNull(output);
                var expectedPhase = ValidateManifestForfeitRequest(url, request);
                var context = new OpeningContext(pmcData, output, sessionId, sessionId.ToString());
                try
                {
                    return await service.ForfeitMissingContentAsync(
                            context,
                            request.ManifestId,
                            expectedPhase,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    logger.Error(
                        $"action={ModConstants.ManifestForfeitAction} manifestId={request.ManifestId} " +
                        $"expectedPhase={request.ExpectedPhase} profileId={sessionId}");
                    throw;
                }
            });

    private static ItemRouteAction<OpenRandomLootContainerRequestData> CreateRelayRoute(
        string action,
        RelaySettlementService service,
        RaidSessionState raidSessions,
        ISptLogger<ContrabandCaseRouter> logger) =>
        new(
            action,
            async (url, pmcData, request, sessionId, output, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(pmcData);
                ArgumentNullException.ThrowIfNull(request);
                ArgumentNullException.ThrowIfNull(output);
                ValidateRelayRequest(url, request);
                raidSessions.RequireLobby(sessionId);
                var context = new OpeningContext(pmcData, output, sessionId, sessionId.ToString());
                try
                {
                    return action == ModConstants.RelaySecureAction
                        ? await service.SecureAsync(context, request.Item, cancellationToken).ConfigureAwait(false)
                        : await service.RelayAsync(context, request.Item, cancellationToken).ConfigureAwait(false);
                }
                catch (RelayKeyRequiredException)
                {
                    return RejectMissingKey(output);
                }
                catch
                {
                    logger.Error($"action={action} stakeRootId={request.Item} profileId={sessionId}");
                    throw;
                }
            });

    private static ItemRouteAction<TestingInventoryGrantRequestData> CreateTestingInventoryGrantRoute(
        SptCaseJournal journalStore,
        SptOpeningInventory inventory,
        SptProfileCommitter committer,
        ProfileLockPool profileLocks,
        RaidSessionState raidSessions,
        TestingInventoryGrantGate gate,
        TestingForcedCrateRegistry forcedCrateRegistry,
        ISptLogger<ContrabandCaseRouter> logger) =>
        new(
            ModConstants.TestingInventoryGrantAction,
            async (url, pmcData, request, sessionId, output, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(pmcData);
                ArgumentNullException.ThrowIfNull(output);
                ValidateTestingInventoryGrantRequest(url, request);
                var crateType = TestingCrateTypeCodec.Parse(request.CrateType);
                var caseTemplate = TestingCrateTypeCodec.CaseTemplate(crateType);

                await using var profileLock = await profileLocks
                    .AcquireAsync(sessionId, cancellationToken)
                    .ConfigureAwait(false);
                gate.RequireGrantAllowed(sessionId);
                raidSessions.RequireLobby(sessionId);
                var journal = await journalStore
                    .LoadAsync(sessionId, cancellationToken)
                    .ConfigureAwait(false);
                if (journal.PreparedOpening is not null || journal.PreparedRelay is not null)
                {
                    throw new InvalidOperationException(
                        "Testing items cannot be granted while a settlement transaction is pending.");
                }

                var context = new OpeningContext(pmcData, output, sessionId, sessionId.ToString());
                var checkpoint = inventory.Capture(context);
                IReadOnlyList<MongoId> createdCaseIds;
                try
                {
                    createdCaseIds = inventory.ApplyTestingInventoryGrant(context, request.CaseCount, request.KeyCount, caseTemplate);
                }
                catch
                {
                    inventory.Restore(context, checkpoint);
                    logger.Error(
                        $"action={ModConstants.TestingInventoryGrantAction} profileId={sessionId} " +
                        $"caseCount={request.CaseCount} keyCount={request.KeyCount}");
                    throw;
                }

                try
                {
                    await committer.CommitAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    gate.MarkCommitUncertain(sessionId);
                    logger.Error(
                        $"action={ModConstants.TestingInventoryGrantAction} profileId={sessionId} " +
                        $"caseCount={request.CaseCount} keyCount={request.KeyCount} commitStarted=true " +
                        "futureTestingGrantsBlocked=true");
                    throw;
                }

                // Tag only takes effect after the grant is durably committed, and only
                // when a non-default pool was requested; a normal ("true random") grant
                // never touches the registry, so a normal case's opening is completely
                // unaffected by this feature. A failure here is a debug-only convenience
                // miss, never a reason to fail an otherwise-successful item grant.
                if (crateType != TestingCrateType.TrueRandom &&
                    (caseTemplate == ModConstants.CaseTemplateId || TestingCrateTypeCodec.PremiumTier(crateType).HasValue))
                {
                    try
                    {
                        foreach (var caseId in createdCaseIds)
                        {
                            forcedCrateRegistry.SetForcedCrate(caseId, crateType);
                        }
                    }
                    catch (Exception exception)
                    {
                        logger.Error(
                            $"[{ModConstants.ModName}] Could not tag granted testing cases with forced " +
                            $"crate '{request.CrateType}' for profile {sessionId}: {exception.Message}");
                    }
                }

                logger.Info(
                    $"[{ModConstants.ModName}] Granted {request.CaseCount} testing cases and " +
                    $"{request.KeyCount} testing keys to profile {sessionId}" +
                    (caseTemplate != ModConstants.CaseTemplateId ? $" ({request.CrateType})." :
                        crateType == TestingCrateType.TrueRandom ? "." : $", forced to the '{request.CrateType}' pool."));
                return output;
            });

    private static void ValidateManifestRequestEnvelope(BaseInteractionRequestData request)
    {
        if (request.FromOwner is not null ||
            request.ToOwner is not null ||
            request.ExtensionData is { Count: > 0 })
        {
            throw new InvalidOperationException(
                "Contraband Cases received unsupported Manifest request fields.");
        }
    }

    private static void ValidateManifestIdentifier(string? manifestId)
    {
        if (manifestId is not { Length: 24 } || manifestId.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')))
        {
            throw new InvalidOperationException(
                "Contraband Cases received an invalid Manifest identifier.");
        }
    }

    private static ManifestPhase ParseManifestPhase(
        string? value,
        params ManifestPhase[] permitted)
    {
        if (value is null ||
            !Enum.TryParse<ManifestPhase>(value, ignoreCase: false, out var phase) ||
            !string.Equals(value, phase.ToString(), StringComparison.Ordinal) ||
            !permitted.Contains(phase))
        {
            throw new InvalidOperationException(
                "Contraband Cases received an invalid Manifest phase.");
        }

        return phase;
    }
}

public sealed record ManifestDecisionRequestData : BaseInteractionRequestData
{
    [JsonPropertyName("selectedOrdinal")]
    public int? SelectedOrdinal { get; set; }

    [JsonPropertyName("manifestId")]
    public string ManifestId { get; set; } = string.Empty;

    [JsonPropertyName("expectedOrdinal")]
    public int ExpectedOrdinal { get; set; }
}

public sealed record ManifestClaimRequestData : BaseInteractionRequestData
{
    [JsonPropertyName("manifestId")]
    public string ManifestId { get; set; } = string.Empty;

    [JsonPropertyName("expectedPhase")]
    public string ExpectedPhase { get; set; } = string.Empty;
}

public sealed record ManifestRelayRequestData : BaseInteractionRequestData
{
    [JsonPropertyName("manifestId")]
    public string ManifestId { get; set; } = string.Empty;

    [JsonPropertyName("expectedPhase")]
    public string ExpectedPhase { get; set; } = string.Empty;

    [JsonPropertyName("expectedRelayStage")]
    public int ExpectedRelayStage { get; set; }
}

public sealed record ManifestForfeitRequestData : BaseInteractionRequestData
{
    [JsonPropertyName("manifestId")]
    public string ManifestId { get; set; } = string.Empty;

    [JsonPropertyName("expectedPhase")]
    public string ExpectedPhase { get; set; } = string.Empty;
}

public sealed record TestingInventoryGrantRequestData : BaseInteractionRequestData
{
    [JsonPropertyName("caseCount")]
    public int CaseCount { get; set; }

    [JsonPropertyName("keyCount")]
    public int KeyCount { get; set; }

    /// <summary>
    /// The wire-encoded <see cref="TestingCrateType"/> (see
    /// <see cref="TestingCrateTypeCodec"/>) the granted cases should be forced
    /// to draw from once opened. Optional: missing, blank, or unrecognized
    /// values decode to <see cref="TestingCrateType.TrueRandom"/> (no
    /// forcing), so an older client that never sends this field still works
    /// exactly as before.
    /// </summary>
    [JsonPropertyName("crateType")]
    public string? CrateType { get; set; }
}
