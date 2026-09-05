using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;

namespace ContrabandCases.Server.Settlement;

internal static class ManifestLibraryProjection
{
    internal static ManifestLibraryData Create(CaseOpeningJournal journal, CargoCatalogSnapshot? catalog,
        IReadOnlyDictionary<string, string> locale, CargoCatalogSnapshot? cashCatalog = null,
        IReadOnlyDictionary<string, CargoCatalogSnapshot?>? caseCatalogs = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var dossier = new StringBuilder($"BROKER FAVOR {journal.BrokerFavor}/3\n");
        dossier.AppendLine("Three losses charge a guaranteed upgrade on your next eligible Relay. Favor persists across cases.");
        dossier.AppendLine(journal.ActiveManifest is null
            ? "No unfinished manifest."
            : "An unfinished manifest is saved. Choose Resume Saved Reward; no new case or key is required.");
        dossier.AppendLine($"\nKeys spent in retained completed openings: {journal.ManifestReceipts.Count + journal.ManifestReceipts.Sum(r => r.RelayHistory.Count)} (opening + Relay keys; not a lifetime total).");
        dossier.AppendLine("\nRECENT SETTLEMENTS (newest first; up to 50 retained receipts)\n");
        foreach (var receipt in journal.ManifestReceipts.OrderByDescending(r => r.CompletedAtUtc).Take(50))
        {
            var outcome = receipt.TerminalPhase switch
            {
                ManifestPhase.Granted => "SECURED",
                ManifestPhase.Confiscated => "LOST",
                _ => "FORFEITED"
            };
            dossier.AppendLine($"{receipt.CompletedAtUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)} • {outcome}");
            var title = receipt.Entitlement?.Identity.DisplayName ?? "Blocked manifest cleared";
            // Titles may legally be much longer than this read-only summary.
            // Bound display text so valid history cannot block the entire library response.
            dossier.AppendLine(title.Length <= 240 ? title : title[..239] + "…");
            foreach (var relay in receipt.RelayHistory)
                dossier.AppendLine($"  Relay {relay.RelayStage}: {relay.Outcome} • Favor {relay.BrokerFavorBefore} → {relay.BrokerFavorAfter}");
            dossier.AppendLine($"Favor after: {receipt.BrokerFavorAfter}/3\n");
        }
        if (journal.ManifestReceipts.Count == 0) dossier.AppendLine("No settled manifests yet.");
        var publicLots = (catalog?.FreshOpeningLots ?? []).Concat(cashCatalog?.FreshOpeningLots ?? [])
            .OrderBy(l => l.Identity.ProviderId, StringComparer.Ordinal)
            .ThenBy(l => l.Identity.LotId, StringComparer.Ordinal)
            .Take(ManifestOpeningOddsData.MaximumTotalLotCount).ToArray();
        var publicIds = publicLots.Select(l => $"{l.Identity.ProviderId}/{l.Identity.LotId}").ToHashSet(StringComparer.Ordinal);
        // Only terminal outcomes and public catalog definitions are projected.
        // Never include active offer identities, draws, inventory IDs or witnesses.
        return new ManifestLibraryData
        {
            Dossier = dossier.ToString(),
            HasPending = journal.ActiveManifest is not null,
            Status = BuildStatus(catalog, cashCatalog, caseCatalogs),
            Cases = (caseCatalogs ?? new Dictionary<string, CargoCatalogSnapshot?>()).Select(pair => new ManifestLibraryCaseData
            {
                TemplateId = pair.Key,
                LotIds = (pair.Value?.FreshOpeningLots ?? []).Select(l => $"{l.Identity.ProviderId}/{l.Identity.LotId}")
                    .Where(publicIds.Contains).ToArray()
            }).ToArray(),
            Lots = publicLots
                .Select(l => ManifestSnapshotProjection.CreateLot(l.Evaluation.Grade, l.Identity,
                    l.Forest, l.Fingerprint, l, locale)).ToArray()
        };
    }

    private static string BuildStatus(CargoCatalogSnapshot? catalog, CargoCatalogSnapshot? cash,
        IReadOnlyDictionary<string, CargoCatalogSnapshot?>? cases)
    {
        var b = new StringBuilder($"SERVER VERSION {ModConstants.ModVersion}\n\n");
        var total = (catalog?.FreshOpeningLots.Count ?? 0) + (cash?.FreshOpeningLots.Count ?? 0);
        if (total > ManifestOpeningOddsData.MaximumTotalLotCount)
            b.AppendLine($"Browser shows the first {ManifestOpeningOddsData.MaximumTotalLotCount} of {total} packages. Opening odds include their full applicable catalog.");
        if (catalog is null) b.AppendLine("Cargo catalog unavailable. Saved rewards remain protected.");
        foreach (var group in (catalog?.FreshOpeningLots ?? []).GroupBy(l => l.Identity.ProviderId))
            b.AppendLine($"AVAILABLE: {group.Key} — {group.Count()} packages");
        foreach (var skipped in (catalog?.SkippedPacks ?? []).Concat(cash?.SkippedPacks ?? []))
            b.AppendLine($"\nSKIPPED: {skipped.ProviderId}\n{skipped.Reason}");
        b.AppendLine("\nCASE AVAILABILITY");
        foreach (var pair in cases ?? new Dictionary<string, CargoCatalogSnapshot?>())
            b.AppendLine($"{CaseContracts.ShortName(pair.Key)}: " + (pair.Value?.OpeningEnabled == true
                ? $"available • published purchase price ₽{pair.Value.CasePrice:N0}"
                : pair.Value?.OpeningDisabledReason ?? "catalog unavailable"));
        b.AppendLine("\nKey supply: find-only; 2% eligible pool weight is NOT 2% per raid.\nPMC key-raid audits are diagnostic only; scav/transit/mail are not counted. Testing grants must be excluded from balance measurements.");
        var text = b.ToString();
        return text.Length <= 32_768 ? text : text[..32_700] + "\nAdditional diagnostics are in the server log.";
    }
}

internal sealed class ManifestLibraryData
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; } = 2;
    [JsonPropertyName("hasPending")]
    public bool HasPending { get; init; }
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;
    [JsonPropertyName("cases")]
    public IReadOnlyList<ManifestLibraryCaseData> Cases { get; init; } = [];
    [JsonPropertyName("dossier")]
    public string Dossier { get; init; } = string.Empty;
    [JsonPropertyName("lots")]
    public IReadOnlyList<ManifestLotData> Lots { get; init; } = [];
}

internal sealed class ManifestLibraryCaseData
{
    [JsonPropertyName("templateId")]
    public string TemplateId { get; init; } = string.Empty;
    [JsonPropertyName("lotIds")]
    public IReadOnlyList<string> LotIds { get; init; } = [];
}
