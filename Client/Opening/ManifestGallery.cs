using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;

namespace ContrabandCases.Client.Opening;

internal enum GalleryState { Offer1, Offer2, ReadyToClaim, Upgrade, Replacement, Confiscated, GuaranteedUpgrade, Claimed }
internal enum GalleryRarity { Any, Common, Uncommon, Rare, Epic, Legendary }

internal static class ManifestGallery
{
    public static bool Matches(ManifestLotSnapshot lot, string id, GalleryRarity rarity) =>
        (string.IsNullOrWhiteSpace(id) || id == lot.LotId || id == $"{lot.ProviderId}/{lot.LotId}") &&
        (rarity == GalleryRarity.Any || rarity.ToString() == RewardRarities.GetInfo(lot.Grade).DisplayName);

    public static ManifestLotSnapshot DisplayLot(ManifestLotSnapshot source, bool longName) =>
        !longName ? source : new ManifestLotSnapshot(source.ProviderId, source.ProviderLabel,
            source.LotId, "Experimental expedition recovery equipment, medical supplies and classified technology collection",
            source.Purpose, source.FamilyId, source.TrackId, source.Grade, source.AnchorTemplateId, source.Fingerprint,
            source.LiquidationValue, source.UseValue, source.FootprintCells, source.Contents, source.TraderResaleEstimate);

    // These snapshots never cross the transport boundary or enter settlement.
    // The library coordinator blocks dispatch and supplies navigation-only callbacks.
    public static ManifestSnapshot Create(ManifestLotSnapshot source, GalleryState state, bool longName)
    {
        var lot = DisplayLot(source, longName);
        var granted = state == GalleryState.Claimed;
        if (lot.ProviderId == CashPayouts.Provider)
        {
            var cashPhase = granted ? ManifestPhase.Granted : ManifestPhase.Entitlement;
            return new ManifestSnapshot(ManifestSnapshot.CurrentProtocolVersion, new string('f', 24), cashPhase,
                1, 1, 1, granted, RarityLadderVersion.FiveTier, null, true, 0, 3,
                new[] { new ManifestFamilySealSnapshot(1, lot.FamilyId, "Cash payout", ManifestRiskBand.Mixed, true, false, true) },
                granted ? null : lot,
                new ManifestAvailableActionsSnapshot(false, false, !granted, false, false, 1, cashPhase, 1),
                null, null, false, null, CaseContracts.CashCache, deliveredToMessenger: granted);
        }
        var ordinal = state == GalleryState.Offer2 ? 2 : 1;
        var offer = state is GalleryState.Offer1 or GalleryState.Offer2;
        var confiscated = state == GalleryState.Confiscated;
        var terminal = confiscated || granted;
        var replacement = state == GalleryState.Replacement;
        var phase = confiscated ? ManifestPhase.Confiscated : granted ? ManifestPhase.Granted :
            offer ? (ordinal == 1 ? ManifestPhase.Offer1 : ManifestPhase.Offer2) : ManifestPhase.Entitlement;
        var favor = state == GalleryState.GuaranteedUpgrade ? 3 : confiscated ? 1 : 0;
        var relay = !offer && !terminal && !replacement && source.Grade != RewardRarity.BlackLabel;
        var stage = state == GalleryState.Upgrade ? 2 : 1;
        var odds = RelayRules.GetOdds(stage);
        return new ManifestSnapshot(ManifestSnapshot.CurrentProtocolVersion, new string('f', 24), phase,
            ordinal, offer ? null : ordinal, stage, terminal || replacement, RarityLadderVersion.FiveTier,
            null, true, favor, 3,
            Enumerable.Range(1, 3).Select(i => new ManifestFamilySealSnapshot(i, $"sealed-{i}",
                i <= ordinal ? CargoFamilies.Label(lot.FamilyId) : "Supplies signal", ManifestRiskBand.Mixed,
                i <= ordinal, i < ordinal, !offer && i == ordinal)), terminal ? null : lot,
            new ManifestAvailableActionsSnapshot(offer, offer, !offer && !terminal, relay, false, ordinal, phase, stage),
            relay ? new ManifestRelayPropositionSnapshot(stage, favor == 3 ? 100 : odds.UpgradePercent,
                favor == 3 ? 0 : odds.SidegradePercent, favor == 3 ? 0 : odds.ConfiscatePercent,
                favor, Math.Min(3, favor + 1), favor == 3, 1, RelayRules.GetUpgradeRarity(lot.Grade),
                lot.UseValue ?? 0, lot.UseValue ?? 0, true, null) : null,
            replacement || confiscated ? new ManifestRelayReceiptSnapshot(1,
                confiscated ? ManifestRelayResult.Confiscated : ManifestRelayResult.Sidegrade,
                "Previous preview reward", lot.Grade, confiscated ? null : lot.DisplayName,
                confiscated ? null : lot.Grade, 0, favor) : null, false, null,
            deliveredToMessenger: granted);
    }
}
