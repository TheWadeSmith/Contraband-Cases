using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;

namespace ContrabandCases.Server.Catalog;

/// <summary>Views of resolved content, sharing the original exact immutable lots.</summary>
public static class CaseCatalogs
{
    internal static CargoCatalogSnapshot WithPrice(CargoCatalogSnapshot source, long price)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (price <= 0) throw new ArgumentOutOfRangeException(nameof(price));
        if (source.CasePrice == price) return source;
        var identity = string.Join("\n", "priced-case-v1", source.SnapshotId,
            source.CaseTemplateId, price.ToString(CultureInfo.InvariantCulture));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return new CargoCatalogSnapshot(hash, source.Lots, source.SkippedPacks,
            source.ProviderWeights, source.CaseTemplateId, price);
    }

    public static CargoCatalogSnapshot ForCase(CargoCatalogSnapshot source, string template)
    {
        ArgumentNullException.ThrowIfNull(source);
        CaseContracts.Require(template);
        if (source.CaseTemplateId == template) return source;
        if (template == CaseContracts.CashCache)
            throw new ArgumentException("Cash Cache requires its own validated payout catalog.", nameof(template));
        if (source.CaseTemplateId != ModConstants.CaseTemplateId)
            throw new ArgumentException("Case views must be derived from the complete mixed catalog.", nameof(source));

        var lots = source.Lots.Where(lot => Includes(template, lot.Identity)).ToArray();
        var identity = string.Join("\n", CaseContracts.SelectionVersion, source.SnapshotId, template);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var catalog = new CargoCatalogSnapshot(hash, lots, source.SkippedPacks, source.ProviderWeights, template);
        if (!catalog.OpeningEnabled) return catalog;

        var price = ManifestCatalogEconomy.CalculateAutomaticPrices(catalog).CasePrice;
        var pricedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            identity + "\n" + price.ToString(CultureInfo.InvariantCulture)))).ToLowerInvariant();
        return new CargoCatalogSnapshot(pricedHash, lots, source.SkippedPacks,
            source.ProviderWeights, template, price);
    }

    internal static bool Includes(string template, CargoLotIdentitySnapshot identity)
    {
        CaseContracts.Require(template);
        var relic = identity.UsePath is Collection || identity.ProviderId is
            "eco-ww2.relic-cache" or "amonya.arcane-cache";
        var specialist = identity.ProviderId is "vault" or "isb-aishi.elite-armory" or
            "natalya.elite-armor" or "wtt-contentbackport.elite-optics" or
            "eco-attachment.elite-optics" or "sjx.combat-chemistry" or "vultify.cooler-stims" ||
            identity.ProviderId == "core" && identity.TrackId.Value is "night" or "ordnance" or "chemistry" or "vault";
        return template switch
        {
            CaseContracts.Relics => relic,
            CaseContracts.BlackSite => !relic && specialist,
            CaseContracts.Operations => !relic && !specialist,
            _ => true
        };
    }
}
