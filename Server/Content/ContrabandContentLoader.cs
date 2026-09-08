using System.Reflection;
using IoPath = System.IO.Path;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Configuration;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Modding.Custom;

namespace ContrabandCases.Server.Content;

[Injectable(InjectionType.Singleton)]
public sealed class ContrabandContentState
{
    private ModConfig? _config;

    public void Initialize(ModConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (Interlocked.CompareExchange(ref _config, config, null) is not null)
        {
            throw new InvalidOperationException("Contraband Cases content state was already initialized.");
        }
    }

    public ModConfig RequireConfig() =>
        Volatile.Read(ref _config) ??
        throw new InvalidOperationException("Contraband Cases content state is not initialized.");
}

[Injectable(TypePriority = OnLoadOrder.Preload + 1)]
public sealed class ContrabandContentLoader(
    ISptLogger<ContrabandContentLoader> logger,
    CustomItemService customItemService,
    TemplateTable templates,
    TradersTable traders,
    InventoryConfig inventoryConfig,
    RagfairConfig ragfairConfig,
    ModHelper modHelper,
    ServerRewardCatalog rewardCatalog,
    ContrabandContentState contentState,
    TestingInventoryGrantGate testingInventoryGrantGate) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Register before native flea generation/its barter cache, not only after
        // final catalog prices: other mods may opt out of the BSG item blacklist.
        ContrabandFleaPolicy.RegisterExclusions(ragfairConfig);
        ContrabandContentDefinitions.EnsureNativeRandomLootRouteUnavailable(
            inventoryConfig.RandomLootContainers
            ?? throw new InvalidOperationException("SPT random loot container configuration is unavailable."));

        var modRoot = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        if (string.IsNullOrWhiteSpace(modRoot))
        {
            throw new InvalidOperationException("Could not resolve the Contraband Cases mod folder.");
        }

        var configRoot = IoPath.Combine(modRoot, "config");
        var config = ModConfig.Parse(ReadRequiredFile(IoPath.Combine(configRoot, "config.jsonc")));
        contentState.Initialize(config);
        rewardCatalog.Load(ReadRequiredFile(IoPath.Combine(configRoot, "rewards.json")));
        var prices = ContrabandContentDefinitions.CalculateRegistrationPrices(config);
        var offers = ContrabandContentDefinitions.CreateMechanicOffers(prices, config);
        var mechanicAssort = ContrabandContentDefinitions.RequireMechanicAssort(traders);

        ValidateTemplateRegistrations();
        ContrabandContentDefinitions.EnsureMechanicOfferIdsAvailable(mechanicAssort, offers);
        RegisterItem(ContrabandContentDefinitions.CreateCaseCloneDetails(prices.CasePrice), "BR-12 Relay Case");
        foreach (var template in CaseContracts.Templates.Where(t => t != ModConstants.CaseTemplateId))
            RegisterItem(ContrabandContentDefinitions.CreateCaseCloneDetails(1_000, template), CaseContracts.Name(template));
        RegisterItem(ContrabandContentDefinitions.CreateKeyCloneDetails(), "BR-12 Relay Key");
        ContrabandContentDefinitions.ApplyMechanicOffers(mechanicAssort, offers);
        ContrabandContentDefinitions.EnsureTherapistBuysConfiguredItems(traders, config);
        ContrabandContentDefinitions.ApplyKeyRegistrationPrice(templates, config);

        ContrabandContentDefinitions.EnsureNativeRandomLootRouteUnavailable(inventoryConfig.RandomLootContainers);
        logger.Success(
            $"[{ModConstants.ModName}] Loaded the legacy recovery catalog, registered a provisional " +
            "Mechanic LL1 case offer, and registered the BR-12 Relay Key as a find-only item");
        testingInventoryGrantGate.Initialize(config.TestingInventoryGrantsEnabled);
        return Task.CompletedTask;
    }

    private void ValidateTemplateRegistrations()
    {
        foreach (var sourceId in new[]
                 {
                     ContrabandContentDefinitions.CaseCloneTemplateId,
                     ContrabandContentDefinitions.KeyCloneTemplateId
                 })
        {
            if (!templates.Items.ContainsKey((MongoId)sourceId))
            {
                throw new InvalidOperationException($"Required clone template '{sourceId}' is missing.");
            }
        }

        foreach (var customId in CaseContracts.Templates.Append(ModConstants.KeyTemplateId))
        {
            if (templates.Items.ContainsKey((MongoId)customId))
            {
                throw new InvalidOperationException($"Custom template id '{customId}' is already registered.");
            }
        }
    }

    private void RegisterItem(NewItemFromCloneDetails details, string displayName)
    {
        CreateItemResult result;
        try
        {
            result = customItemService.CreateItemFromClone(details, Assembly.GetExecutingAssembly());
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Unable to register {displayName}.", exception);
        }

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Unable to register {displayName}: {string.Join("; ", result.Errors ?? [])}");
        }
    }

    private static string ReadRequiredFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Required Contraband Cases configuration file is missing.", path);
        }

        return File.ReadAllText(path);
    }
}
