using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class ManifestClaimCommitWitnessTests
{
    private static readonly MongoId ProfileId = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly RewardForestFingerprintV2 Fingerprint = new(new string('a', 64));

    [Fact]
    public void Chain_accepts_only_its_exact_predecessor_or_current_head()
    {
        var profile = new PmcData();
        var first = ManifestClaimCommitWitness.PlanNext(profile, ProfileId, Prepared("111111111111111111111111"));
        Assert.Equal(1, first.CommitGeneration);
        Assert.Equal(ManifestClaimCommitWitness.GenesisHash, first.CommitPredecessorHash);
        Assert.Equal(ManifestClaimCommitWitnessInspection.Predecessor,
            ManifestClaimCommitWitness.Inspect(profile, ProfileId, "manifest-a", Fingerprint, first));

        ManifestClaimCommitWitness.Stage(profile, ProfileId, "manifest-a", Fingerprint, first);
        Assert.Equal(ManifestClaimCommitWitnessInspection.Current,
            ManifestClaimCommitWitness.Inspect(profile, ProfileId, "manifest-a", Fingerprint, first));

        var second = ManifestClaimCommitWitness.PlanNext(profile, ProfileId, Prepared("333333333333333333333333"));
        Assert.Equal(2, second.CommitGeneration);
        Assert.Equal(ManifestClaimCommitWitnessInspection.Predecessor,
            ManifestClaimCommitWitness.Inspect(profile, ProfileId, "manifest-b", Fingerprint, second));
        ManifestClaimCommitWitness.Stage(profile, ProfileId, "manifest-b", Fingerprint, second);
        Assert.Equal(ManifestClaimCommitWitnessInspection.Other,
            ManifestClaimCommitWitness.Inspect(profile, ProfileId, "manifest-a", Fingerprint, first));
    }

    [Fact]
    public void Chain_rejects_same_generation_rewritten_evidence_and_other_profile()
    {
        var profile = new PmcData();
        var planned = ManifestClaimCommitWitness.PlanNext(profile, ProfileId, Prepared("111111111111111111111111"));
        ManifestClaimCommitWitness.Stage(profile, ProfileId, "manifest-a", Fingerprint, planned);
        var rewritten = new ManifestClaimPreparedPayload(
            Prepared("111111111111111111111111").Items,
            ["111111111111111111111111"], false,
            DateTimeOffset.UnixEpoch.AddMinutes(2),
            planned.CommitGeneration,
            planned.CommitPredecessorHash);
        Assert.Equal(ManifestClaimCommitWitnessInspection.Other,
            ManifestClaimCommitWitness.Inspect(profile, ProfileId, "manifest-a", Fingerprint, rewritten));
        Assert.Throws<InvalidOperationException>(() => ManifestClaimCommitWitness.ReadHead(profile, "bbbbbbbbbbbbbbbbbbbbbbbb"));
    }

    [Fact]
    public void Malformed_v2_head_fails_closed()
    {
        var profile = new PmcData
        {
            ExtensionData = new Dictionary<string, object>
            { [ManifestClaimCommitWitness.ExtensionDataKey] = "v2:bad" }
        };
        Assert.Throws<InvalidOperationException>(() => ManifestClaimCommitWitness.ReadHead(profile, ProfileId));
    }

    [Fact]
    public void Commit_uncertainty_is_shared_across_service_reconstruction_boundary()
    {
        var coordinator = new ManifestClaimCommitUncertaintyCoordinator();
        coordinator.MarkUncertain(ProfileId);
        Assert.Throws<InvalidOperationException>(() => coordinator.ThrowIfUncertain(ProfileId));
    }

    private static ManifestClaimPreparedPayload Prepared(string rootId) => new(
        [new Item { Id = rootId, Template = "aaaaaaaaaaaaaaaaaaaaaaa1", Upd = new Upd { StackObjectsCount = 1 } }],
        [rootId], false, DateTimeOffset.UnixEpoch.AddMinutes(1));
}
