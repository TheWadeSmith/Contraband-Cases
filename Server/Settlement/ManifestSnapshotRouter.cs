using System.Text.Json;
using System.Text.Json.Serialization;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services.Locales;
using SPTarkov.Server.Core.Utils;

namespace ContrabandCases.Server.Settlement;

[Injectable]
public sealed class ManifestSnapshotRouter : StaticRouter
{
    public ManifestSnapshotRouter(
        JsonUtil jsonUtil,
        HttpResponseUtil httpResponseUtil,
        SptCaseJournal journalStore,
        ProfileLockPool profileLocks,
        RaidSessionState raidSessions,
        CatalogSnapshotCoordinator catalogCoordinator,
        LocaleService localeService)
        : base(jsonUtil,
        [
            new RouteAction<ManifestCurrentRequest>(
                ModConstants.ManifestLibraryRoute,
                async (_, request, sessionId, _, cancellationToken) =>
                {
                    ArgumentNullException.ThrowIfNull(request);
                    RequireNoUnknownProperties(request.ExtensionData);
                    var journal = await LoadJournalAsync(sessionId, journalStore, profileLocks, raidSessions, cancellationToken)
                        .ConfigureAwait(false);
                    return httpResponseUtil.GetBody(ManifestLibraryProjection.Create(
                        journal, TryGetCatalog(catalogCoordinator), TryGetLocale(localeService),
                        TryGetCatalog(catalogCoordinator, CaseContracts.CashCache),
                        CaseContracts.Templates.ToDictionary(id => id,
                            id => TryReadCatalog(() => catalogCoordinator.GetCaseSnapshot(id)))));
                }),
            new RouteAction<ManifestCurrentRequest>(
                ModConstants.ManifestCurrentRoute,
                (_, request, sessionId, _, cancellationToken) => CreateCurrentResponseAsync(
                    request,
                    sessionId,
                    httpResponseUtil,
                    journalStore,
                    profileLocks,
                    raidSessions,
                    catalogCoordinator,
                    localeService,
                    cancellationToken)),
            new RouteAction<ManifestSnapshotRequest>(
                ModConstants.ManifestSnapshotRoute,
                (_, request, sessionId, _, cancellationToken) => CreateSnapshotResponseAsync(
                    request,
                    sessionId,
                    httpResponseUtil,
                    journalStore,
                    profileLocks,
                    raidSessions,
                    catalogCoordinator,
                    localeService,
                    cancellationToken))
        ])
    {
    }

    internal static async ValueTask<string> CreateCurrentResponseAsync(
        ManifestCurrentRequest request,
        MongoId profileId,
        HttpResponseUtil httpResponseUtil,
        ICaseOpeningJournalStore journalStore,
        ProfileLockPool profileLocks,
        RaidSessionState raidSessions,
        CatalogSnapshotCoordinator catalogCoordinator,
        LocaleService localeService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireNoUnknownProperties(request.ExtensionData);
        var journal = await LoadJournalAsync(
                profileId,
                journalStore,
                profileLocks,
                raidSessions,
                cancellationToken)
            .ConfigureAwait(false);
        var currentState = CreateCurrentState(
            journal,
            journal.ActiveManifest is null
                ? catalogCoordinator.GetCaseSnapshot(request.CaseTemplateId)
                : TryGetCatalog(catalogCoordinator, journal.ActiveManifest.Ticket.CaseTemplateId),
            TryGetLocale(localeService),
            request.CaseTemplateId);
        return httpResponseUtil.GetBody(currentState);
    }

    internal static async ValueTask<string> CreateSnapshotResponseAsync(
        ManifestSnapshotRequest request,
        MongoId profileId,
        HttpResponseUtil httpResponseUtil,
        ICaseOpeningJournalStore journalStore,
        ProfileLockPool profileLocks,
        RaidSessionState raidSessions,
        CatalogSnapshotCoordinator catalogCoordinator,
        LocaleService localeService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireNoUnknownProperties(request.ExtensionData);
        RequireMongoId(request.ManifestId);
        var journal = await LoadJournalAsync(
                profileId,
                journalStore,
                profileLocks,
                raidSessions,
                cancellationToken)
            .ConfigureAwait(false);

        ManifestSnapshotData snapshot;
        if (journal.ActiveManifest is { } active &&
            string.Equals(active.ManifestId, request.ManifestId, StringComparison.Ordinal))
        {
            snapshot = ManifestSnapshotProjection.FromActive(
                active,
                TryGetCatalog(catalogCoordinator, active.Ticket.CaseTemplateId),
                TryGetLocale(localeService));
        }
        else
        {
            var receipt = journal.ManifestReceipts.SingleOrDefault(candidate =>
                string.Equals(candidate.ManifestId, request.ManifestId, StringComparison.Ordinal));
            snapshot = receipt is null
                ? throw new InvalidOperationException("The requested Manifest snapshot is unavailable.")
                : ManifestSnapshotProjection.FromTerminal(receipt);
        }

        return httpResponseUtil.GetBody(new ManifestSnapshotEnvelope { Snapshot = snapshot });
    }

    internal static ManifestCurrentStateEnvelope CreateCurrentState(
        CaseOpeningJournal journal,
        CargoCatalogSnapshot? catalog,
        IReadOnlyDictionary<string, string>? locale,
        string caseTemplateId = ModConstants.CaseTemplateId)
    {
        ArgumentNullException.ThrowIfNull(journal);
        CaseContracts.Require(caseTemplateId);
        if (journal.ActiveManifest is { } active)
        {
            return ManifestCurrentStateEnvelope.FromSnapshot(
                ManifestSnapshotProjection.FromActive(active, catalog, locale));
        }

        if (catalog is null)
        {
            throw new InvalidOperationException(
                "The current Manifest catalog is unavailable.");
        }

        return ManifestCurrentStateEnvelope.FromOpeningOdds(
            ManifestOpeningOdds.Create(CaseCatalogs.ForCase(catalog, caseTemplateId)));
    }

    private static async ValueTask<CaseOpeningJournal> LoadJournalAsync(
        MongoId profileId,
        ICaseOpeningJournalStore journalStore,
        ProfileLockPool profileLocks,
        RaidSessionState raidSessions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journalStore);
        ArgumentNullException.ThrowIfNull(profileLocks);
        ArgumentNullException.ThrowIfNull(raidSessions);
        await using var profileLock = await profileLocks
            .AcquireAsync(profileId, cancellationToken)
            .ConfigureAwait(false);
        raidSessions.RequireLobby(profileId);
        return await journalStore.LoadAsync(profileId, cancellationToken).ConfigureAwait(false);
    }

    private static CargoCatalogSnapshot? TryGetCatalog(CatalogSnapshotCoordinator coordinator,
        string caseTemplateId = ModConstants.CaseTemplateId)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        return TryReadCatalog(() => caseTemplateId == CaseContracts.CashCache
            ? coordinator.GetCaseSnapshot(caseTemplateId) : coordinator.GetSnapshot());
    }

    private static CargoCatalogSnapshot? TryReadCatalog(Func<CargoCatalogSnapshot> read)
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> TryGetLocale(LocaleService localeService)
    {
        ArgumentNullException.ThrowIfNull(localeService);
        try
        {
            return localeService.GetLocaleDb(localeService.GetDesiredGameLocale());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void RequireNoUnknownProperties(
        IReadOnlyDictionary<string, JsonElement>? extensionData)
    {
        if (extensionData is { Count: > 0 })
        {
            throw new InvalidOperationException("The Manifest request contains unknown properties.");
        }
    }

    private static void RequireMongoId(string? value)
    {
        if (value is not { Length: 24 } || value.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f') and
                    not (>= 'A' and <= 'F')))
        {
            throw new InvalidOperationException("A valid Manifest ID is required.");
        }
    }
}

public sealed class ManifestCurrentRequest : IRequestData
{
    [JsonPropertyName("caseTemplateId")]
    public string CaseTemplateId { get; set; } = ModConstants.CaseTemplateId;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class ManifestSnapshotRequest : IRequestData
{
    [JsonPropertyName("manifestId")]
    public string ManifestId { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
