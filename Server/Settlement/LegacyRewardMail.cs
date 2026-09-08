using System.Security.Cryptography;
using System.Text;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace ContrabandCases.Server.Settlement;

// Legacy rewards predate canonical cargo forests. Keep their saved physical
// recipe and use its immutable catalog identity in a separate witness namespace;
// exact item IDs, roots, profile, timestamp and chain generation are also bound
// by the existing reward commit witness. No legacy recipe is re-materialized.
internal static class LegacyRewardMail
{
    internal static string OpeningId(CaseOpeningRecord record) => "legacy-opening-" + record.CaseId;
    internal static string RelayId(RelaySettlementRecord record) => "legacy-relay-" + record.StakeRootId;
    internal static RewardForestFingerprintV2 Identity(string rewardId) => new(
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("legacy-catalog-reward/v1/" + rewardId))).ToLowerInvariant());

    internal static ManifestClaimPreparedPayload MarkApplied(ManifestClaimPreparedPayload p) =>
        new(p.Items, p.RootIds, true, p.PreparedAtUtc, p.CommitGeneration, p.CommitPredecessorHash, p.Delivery);
    internal static ManifestClaimPreparedPayload Retry(ManifestClaimPreparedPayload p) =>
        new(p.Items, p.RootIds, false, p.PreparedAtUtc, p.CommitGeneration, p.CommitPredecessorHash, p.Delivery);

    internal static void Validate(ManifestClaimPreparedPayload? mail, IReadOnlyCollection<Item> items,
        DateTimeOffset preparedAt, bool committed)
    {
        if (mail is null) return;
        if (mail.Delivery != ClaimDeliveryKind.Messenger || mail.PreparedAtUtc != preparedAt ||
            mail.CommitGeneration is null || mail.CommitPredecessorHash is null ||
            committed && !mail.ProfileCommitStarted)
            throw new ArgumentException("Legacy mail must retain its planned Messenger commit evidence.");
        SptOpeningInventory.EnsureExactItemState(items, mail.Items, "saved legacy mail prize");
    }

    internal static void ValidateReplacement(ManifestClaimPreparedPayload? before,
        ManifestClaimPreparedPayload? after, bool committed)
    {
        if (before is null)
        {
            if (committed && after is not null)
                throw new InvalidOperationException("Already-paid inventory prizes cannot be reissued through Messenger.");
            return;
        }
        if (after is null || before.Delivery != after.Delivery ||
            before.CommitGeneration != after.CommitGeneration || before.CommitPredecessorHash != after.CommitPredecessorHash ||
            before.PreparedAtUtc != after.PreparedAtUtc || !before.RootIds.SequenceEqual(after.RootIds) ||
            before.ProfileCommitStarted && !after.ProfileCommitStarted ||
            committed && before.ProfileCommitStarted != after.ProfileCommitStarted)
            throw new InvalidOperationException("Legacy mail commit evidence cannot be rewritten or downgraded.");
        SptOpeningInventory.EnsureExactItemState(before.Items, after.Items, "legacy mail evidence");
    }
}
