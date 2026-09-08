using System.Reflection;
using System.Collections.Concurrent;
using ContrabandCases.Shared;
using ContrabandCases.Server.Configuration;
using ContrabandCases.Server.Content;
using ContrabandCases.Server.Loot;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Economy;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Services.Ragfair;
using IoPath = System.IO.Path;

namespace ContrabandCases.Server.Catalog;

[Injectable(InjectionType.Singleton)]
public sealed class CatalogSnapshotCoordinator
{
    private readonly Lazy<CargoCatalogSnapshot> _snapshot;
    private readonly Lazy<CargoCatalogSnapshot> _cashSnapshot;
    private readonly ConcurrentDictionary<string, Lazy<CargoCatalogSnapshot>> _cases = new(StringComparer.Ordinal);
    private int _startupComplete;

    public CatalogSnapshotCoordinator(
        TemplateTable templates,
        GlobalTable globals,
        TradersTable traders,
        ModHelper modHelper,
        ISptLogger<CatalogSnapshotCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(globals);
        ArgumentNullException.ThrowIfNull(traders);
        ArgumentNullException.ThrowIfNull(modHelper);
        ArgumentNullException.ThrowIfNull(logger);
        _snapshot = new Lazy<CargoCatalogSnapshot>(
            () => Freeze(templates, globals, traders, modHelper, logger),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _cashSnapshot = new Lazy<CargoCatalogSnapshot>(() =>
        {
            var cash = FreezeCash(() => CashCurrencyQuotes.Freeze(templates.Items, traders, BuildHandbookPrices(templates)),
                id => templates.Items?.GetValueOrDefault((MongoId)id),
                message => logger.Warning($"[Contraband Cases] Cash Cache unavailable: {message}"));
            try
            {
                var modRoot = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
                if (string.IsNullOrWhiteSpace(modRoot)) throw new IOException("Mod report folder is unavailable.");
                var path = IoPath.Combine(modRoot, "cash-catalog-report.json");
                File.WriteAllText(path + ".tmp", System.Text.Json.JsonSerializer.Serialize(CashPayoutCatalog.Report(cash),
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
            {
                logger.Warning($"[Contraband Cases] Cash catalog report unavailable: {exception.Message}");
            }
            return cash;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal CatalogSnapshotCoordinator(Func<CargoCatalogSnapshot> freeze, Func<CargoCatalogSnapshot>? freezeCash = null)
    {
        ArgumentNullException.ThrowIfNull(freeze);
        _snapshot = new Lazy<CargoCatalogSnapshot>(
            freeze,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _cashSnapshot = new Lazy<CargoCatalogSnapshot>(freezeCash ??
            (() => CashPayoutCatalog.Disabled("No currency quote source was supplied.")),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsStartupComplete => Volatile.Read(ref _startupComplete) == 1;

    internal static CargoCatalogSnapshot FreezeCash(Func<CargoCatalogSnapshot> freeze,
        Func<string, TemplateItem?> findTemplate, Action<string> warn)
    {
        try { return freeze(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            // Optional modded pricing must never poison the ordinary case catalog.
            warn(exception.Message);
            return CashPayoutCatalog.Disabled(exception.Message, findTemplate);
        }
    }

    public bool IsFrozen => _snapshot.IsValueCreated;

    public CargoCatalogSnapshot GetSnapshot()
    {
        if (!IsStartupComplete)
        {
            throw new InvalidOperationException(
                "The Manifest catalog cannot freeze before all SPT load hooks complete.");
        }

        return _snapshot.Value;
    }

    public CargoCatalogSnapshot GetCaseSnapshot(string template)
    {
        CaseContracts.Require(template);
        var source = GetSnapshot();
        if (template == CaseContracts.CashCache) return _cashSnapshot.Value;
        return _cases.GetOrAdd(template, id => new Lazy<CargoCatalogSnapshot>(
                () => CaseCatalogs.ForCase(source, id), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    internal void PublishMixedPrice(long price)
    {
        var priced = CaseCatalogs.WithPrice(GetSnapshot(), price);
        // Publish once at the startup barrier, before any opening can read this view.
        // The complete source catalog remains unchanged for diagnostics and saved lots.
        if (!_cases.TryAdd(ModConstants.CaseTemplateId, new Lazy<CargoCatalogSnapshot>(() => priced)))
            throw new InvalidOperationException("The Mixed case catalog was accessed before price finalization or finalized twice.");
    }

    internal void MarkStartupComplete()
    {
        Interlocked.Exchange(ref _startupComplete, 1);
    }

    private static CargoCatalogSnapshot Freeze(
        TemplateTable templates,
        GlobalTable globals,
        TradersTable traders,
        ModHelper modHelper,
        ISptLogger<CatalogSnapshotCoordinator> logger)
    {
        var modRoot = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        if (string.IsNullOrWhiteSpace(modRoot))
        {
            throw new CargoCatalogValidationException(
                "Could not resolve the Contraband Cases mod folder for catalog freezing.");
        }

        var packRoot = IoPath.Combine(modRoot, "config", "reward-packs");
        if (!Directory.Exists(packRoot))
        {
            throw new CargoCatalogValidationException(
                "The required reward-pack directory is missing.");
        }

        var loader = new JsonRewardPackLoader();
        var corePath = IoPath.Combine(packRoot, "core.json");
        var core = loader.LoadFile(corePath);
        EnsureFileIdentity(corePath, core);
        if (!string.Equals(core.ProviderId, "core", StringComparison.Ordinal))
        {
            throw new CargoCatalogValidationException(
                "The required core.json pack must declare providerId 'core'.");
        }

        var optional = new List<CargoLotPack>();
        var skipped = new List<SkippedCargoLotPack>();
        foreach (var path in Directory
                     .EnumerateFiles(packRoot, "*.json", SearchOption.TopDirectoryOnly)
                     .Where(path => !string.Equals(
                         IoPath.GetFullPath(path),
                         IoPath.GetFullPath(corePath),
                         StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => IoPath.GetFileName(path), StringComparer.Ordinal))
        {
            try
            {
                var pack = loader.LoadFile(path);
                EnsureFileIdentity(path, pack);
                optional.Add(pack);
            }
            catch (CargoCatalogValidationException exception)
            {
                skipped.Add(new SkippedCargoLotPack(
                    IoPath.GetFileNameWithoutExtension(path),
                    "unknown",
                    exception.Message));
            }
        }

        TemplateItem? FindTemplate(string id)
        {
            try
            {
                return templates.Items.GetValueOrDefault((MongoId)id);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return null;
            }
        }

        Preset? FindPreset(string id)
        {
            try
            {
                return globals.ItemPresets.GetValueOrDefault((MongoId)id);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return null;
            }
        }

        var handbookPrices = BuildHandbookPrices(templates);
        var resolver = new CargoLotResolver(new CargoLotResolverDependencies(
            FindTemplate,
            FindPreset));
        var evaluator = new CargoLotEvaluator(
            FindTemplate,
            id => handbookPrices.GetValueOrDefault(id),
            new CargoTraderResale(FindTemplate, handbookPrices,
                traders.Values.Where(trader => trader?.Base is not null).Select(trader => trader.Base)).Estimate);
        var requirements = new CargoPackRequirementValidator(
            FindTemplate,
            FindPreset);
        var snapshot = new CargoCatalogSnapshotBuilder(
            resolver,
            evaluator,
            requirements.Validate).Build(core, optional, skipped);
        logger.Success(
            $"[Contraband Cases] Froze Manifest catalog {snapshot.SnapshotId} " +
            $"with {snapshot.Lots.Count} lots and " +
            $"{snapshot.FamilyAssignments.Count} three-family assignments.");
        foreach (var rejected in snapshot.SkippedPacks)
        {
            logger.Info(
                $"[Contraband Cases] Skipped reward pack '{rejected.ProviderId}' " +
                $"({rejected.PackVersion}): {rejected.Reason}");
        }

        if (snapshot.UnavailableRetiredLots.Count > 0)
            logger.Info($"[Contraband Cases] {snapshot.UnavailableRetiredLots.Count} retired reward definitions cannot currently be recovered; current packs are unaffected. Details: catalog-report.json.");

        // Diagnostics are generated from finalized live templates, never from
        // hand-maintained price guesses. Failure to write a report is visible
        // but cannot invalidate an otherwise safe catalog.
        try
        {
            var reportPath = IoPath.Combine(modRoot, "catalog-report.json");
            var pendingPath = reportPath + ".tmp";
            File.WriteAllText(pendingPath, System.Text.Json.JsonSerializer.Serialize(
                ManifestEconomyReport.Create(snapshot),
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.Move(pendingPath, reportPath, overwrite: true);
            logger.Info("[Contraband Cases] Wrote resolved catalog and Relay economy report to catalog-report.json");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.Warning($"[Contraband Cases] Catalog report unavailable: {exception.Message}");
        }

        return snapshot;
    }

    private static IReadOnlyDictionary<string, double> BuildHandbookPrices(
        TemplateTable templates)
    {
        if (templates.Handbook?.Items is null)
        {
            throw new CargoCatalogValidationException(
                "The finalized SPT handbook is unavailable.");
        }

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var entry in templates.Handbook.Items)
        {
            if (entry is null ||
                string.IsNullOrWhiteSpace(entry.Id.ToString()) ||
                entry.Price is null ||
                !result.TryAdd(entry.Id.ToString(), entry.Price.Value))
            {
                throw new CargoCatalogValidationException(
                    "The finalized SPT handbook contains invalid or duplicate entries.");
            }
        }

        return result;
    }

    private static void EnsureFileIdentity(string path, CargoLotPack pack)
    {
        var fileIdentity = IoPath.GetFileNameWithoutExtension(path);
        if (!string.Equals(fileIdentity, pack.ProviderId, StringComparison.Ordinal))
        {
            throw new CargoCatalogValidationException(
                $"Reward-pack file '{IoPath.GetFileName(path)}' must match providerId " +
                $"'{pack.ProviderId}'.");
        }
    }

}

/// <summary>
/// This final load hook opens the coordinator's lazy gate, freezes the finalized
/// catalog, and publishes the resulting prices to the tables and native caches
/// before SPT accepts client requests.
/// </summary>
[Injectable(TypePriority = int.MaxValue)]
public sealed class CatalogStartupBarrier(
    CatalogSnapshotCoordinator coordinator,
    ContrabandContentState contentState,
    TemplateTable templates,
    TradersTable traders,
    HandbookHelper handbookHelper,
    RagfairPriceService ragfairPriceService,
    LocationTable locations,
    BotTable bots,
    PmcConfig pmcConfig,
    ISptLogger<CatalogStartupBarrier> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = contentState.RequireConfig();
        var mechanicAssort = ContrabandContentDefinitions.RequireMechanicAssort(traders);
        var priceCaches = SptTraderPriceCaches.Bind(handbookHelper, ragfairPriceService);
        var prices = FinalizeStartup(coordinator, config, templates, mechanicAssort);
        ContrabandContentDefinitions.PublishThemedCases(templates, mechanicAssort, config, coordinator);
        priceCaches.Publish(templates);
        var lootMaps = ContrabandCaseLootInjector.Register(locations, bots, pmcConfig, coordinator, config);
        logger.Info($"[Contraband Cases] Registered crate-only case loot on {lootMaps} map(s): " +
            $"{config.CaseLootWeightPercent}% combined added pool weight; cases excluded from bot spawn loot.");
        foreach (var template in CaseContracts.Templates.Where(t => t != ModConstants.CaseTemplateId))
        {
            var view = coordinator.GetCaseSnapshot(template);
            logger.Info($"[Contraband Cases] {CaseContracts.Name(template)}: {view.Lots.Count} lots; " +
                (view.OpeningEnabled ? $"{view.CasePrice} RUB" : $"unavailable: {view.OpeningDisabledReason}"));
        }
        logger.Success(
            $"[Contraband Cases] Finalized Mechanic case price at {prices.CasePrice} RUB");
        return Task.CompletedTask;
    }

    internal static TicketPrices FinalizeStartup(
        CatalogSnapshotCoordinator coordinator,
        ModConfig config,
        TemplateTable templates,
        TraderAssort mechanicAssort)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(mechanicAssort);

        coordinator.MarkStartupComplete();
        var catalog = coordinator.GetSnapshot();
        var prices = ContrabandContentDefinitions.CalculatePrices(catalog, config);
        ContrabandContentDefinitions.ApplyFinalizedPrices(templates, mechanicAssort, prices, config);
        coordinator.PublishMixedPrice(prices.CasePrice);
        return prices;
    }
}
