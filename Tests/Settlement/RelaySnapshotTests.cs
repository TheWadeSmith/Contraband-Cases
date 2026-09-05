using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;
using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class RelaySnapshotTests
{
    private static readonly MongoId ProfileId = (MongoId)"111111111111111111111111";
    private static readonly MongoId OtherProfileId = (MongoId)"222222222222222222222222";

    [Fact]
    public void Snapshot_publishes_authoritative_meter_ladder_and_exact_target_pools()
    {
        var root = new MongoId();
        var opening = new CaseOpeningRecord(
            new MongoId(), new MongoId(), "scav-a",
            [new Item { Id = root, Template = (MongoId)"aaaaaaaaaaaaaaaaaaaaaaaa", ParentId = "stash", SlotId = "hideout" }],
            DateTimeOffset.UnixEpoch, OpeningRecordStatus.Committed, DateTimeOffset.UnixEpoch);
        var journal = new CaseOpeningJournal([opening], recoveryMeter: RelayRules.MaximumRecoveryMeter);
        var catalog = new Catalog();
        var stake = RelayChainResolver.Resolve(journal, root, catalog.FindReward);

        var snapshot = RelaySnapshotRouter.BuildSnapshot(journal, stake, catalog);

        Assert.Equal(3, snapshot.Status.RecoveryMeter);
        Assert.True(snapshot.Status.GuaranteedUpgradeReady);
        Assert.True(snapshot.Status.RelayEligible);
        Assert.Collection(snapshot.PublishedLadder,
            stage => Assert.Equal((1, 55, 30, 15), (stage.Stage, stage.UpgradePercent, stage.SidegradePercent, stage.ConfiscatePercent)),
            stage => Assert.Equal((2, 45, 25, 30), (stage.Stage, stage.UpgradePercent, stage.SidegradePercent, stage.ConfiscatePercent)),
            stage => Assert.Equal((3, 35, 20, 45), (stage.Stage, stage.UpgradePercent, stage.SidegradePercent, stage.ConfiscatePercent)));
        Assert.Equal(new[] { "uncommon-a", "uncommon-b" }, snapshot.UpgradeCandidates.Select(candidate => candidate.RewardId));
        Assert.Equal("scav-b", Assert.Single(snapshot.SidegradeCandidates).RewardId);
        Assert.Null(snapshot.LatestReceipt);
    }

    [Fact]
    public async Task Pending_discovery_returns_none_when_authenticated_profile_has_no_prepared_relay()
    {
        var store = new Store(new Dictionary<MongoId, CaseOpeningJournal>
        {
            [ProfileId] = new CaseOpeningJournal()
        });
        var sessions = EnterLobby(ProfileId);

        var discovery = await RelaySnapshotRouter.DiscoverPendingAsync(
            ProfileId,
            store,
            new ProfileLockPool(),
            sessions,
            new Catalog(),
            CancellationToken.None);

        Assert.Null(discovery.PendingSnapshot);
        Assert.Equal(ProfileId, Assert.Single(store.LoadedProfiles));
    }

    [Theory]
    [InlineData(RelayRecordAction.Secure, "Secure")]
    [InlineData(RelayRecordAction.Relay, "Relay")]
    public async Task Pending_discovery_returns_the_exact_prepared_action_and_root_for_authenticated_profile(
        RelayRecordAction action,
        string expectedAction)
    {
        var (journal, prepared) = PreparedJournal(action);
        var store = new Store(new Dictionary<MongoId, CaseOpeningJournal>
        {
            [ProfileId] = journal
        });

        var discovery = await RelaySnapshotRouter.DiscoverPendingAsync(
            ProfileId,
            store,
            new ProfileLockPool(),
            EnterLobby(ProfileId),
            new Catalog(),
            CancellationToken.None);

        var snapshot = Assert.IsType<RelaySnapshot>(discovery.PendingSnapshot);
        Assert.Equal(prepared.StakeRootId.ToString(), snapshot.Status.StakeRootId);
        Assert.Equal(expectedAction, snapshot.Status.PendingAction);
        Assert.True(snapshot.Status.SettlementPending);
        Assert.False(snapshot.Status.RelayEligible);
        Assert.Null(snapshot.LatestReceipt);
    }

    [Fact]
    public async Task Pending_discovery_never_reads_another_authenticated_profiles_journal()
    {
        var (ownJournal, ownPrepared) = PreparedJournal(RelayRecordAction.Secure);
        var (otherJournal, _) = PreparedJournal(RelayRecordAction.Relay);
        var store = new Store(new Dictionary<MongoId, CaseOpeningJournal>
        {
            [ProfileId] = ownJournal,
            [OtherProfileId] = otherJournal
        });

        var discovery = await RelaySnapshotRouter.DiscoverPendingAsync(
            ProfileId,
            store,
            new ProfileLockPool(),
            EnterLobby(ProfileId),
            new Catalog(),
            CancellationToken.None);

        Assert.Equal(
            ownPrepared.StakeRootId.ToString(),
            Assert.IsType<RelaySnapshot>(discovery.PendingSnapshot).Status.StakeRootId);
        Assert.Equal([ProfileId], store.LoadedProfiles);
    }

    [Fact]
    public void Pending_discovery_request_has_no_caller_selected_inventory_identity()
    {
        var request = JsonSerializer.Deserialize<RelayPendingRequest>(
            "{\"stakeRootId\":\"aaaaaaaaaaaaaaaaaaaaaaaa\"}");

        Assert.NotNull(request);
        Assert.Empty(typeof(RelayPendingRequest).GetProperties());
        Assert.Empty(typeof(RelayPendingRequest).GetFields());
    }

    [Fact]
    public void Pending_discovery_wire_contract_keeps_exact_lower_camel_null_property()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        var emptyJson = JsonSerializer.Serialize(
            RelaySnapshotRouter.CreatePendingResponseData(new RelayPendingDiscovery()),
            options);
        var pendingJson = JsonSerializer.Serialize(
            RelaySnapshotRouter.CreatePendingResponseData(new RelayPendingDiscovery
            {
                PendingSnapshot = new RelaySnapshot()
            }),
            options);

        Assert.Equal("{\"pendingSnapshot\":null}", emptyJson);
        Assert.StartsWith("{\"pendingSnapshot\":{", pendingJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"PendingSnapshot\"", pendingJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pending_discovery_waits_for_the_shared_profile_lock_before_loading_the_journal()
    {
        var store = new Store(new Dictionary<MongoId, CaseOpeningJournal>
        {
            [ProfileId] = PreparedJournal(RelayRecordAction.Relay).Journal
        });
        var locks = new ProfileLockPool();
        await using var heldLock = await locks.AcquireAsync(ProfileId, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var discoveryStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<RelayPendingDiscovery> discovery;
        using (ExecutionContext.SuppressFlow())
        {
            discovery = Task.Run(async () =>
            {
                var pending = RelaySnapshotRouter.DiscoverPendingAsync(
                    ProfileId,
                    store,
                    locks,
                    EnterLobby(ProfileId),
                    new Catalog(),
                    cancellation.Token);
                discoveryStarted.SetResult();
                return await pending;
            });
        }

        await discoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(discovery.IsCompleted);
        Assert.Empty(store.LoadedProfiles);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery);
    }

    [Fact]
    public async Task Pending_discovery_rejects_non_lobby_profiles_before_loading_the_journal()
    {
        var store = new Store(new Dictionary<MongoId, CaseOpeningJournal>
        {
            [ProfileId] = PreparedJournal(RelayRecordAction.Relay).Journal
        });
        var sessions = EnterLobby(ProfileId);
        sessions.BeginRaidStart(ProfileId);
        sessions.CompleteRaidStart(ProfileId);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await RelaySnapshotRouter.DiscoverPendingAsync(
                ProfileId,
                store,
                new ProfileLockPool(),
                sessions,
                new Catalog(),
                CancellationToken.None));

        Assert.Empty(store.LoadedProfiles);
    }

    [Fact]
    public void Pending_discovery_rejects_a_prepared_relay_without_authoritative_chain_ancestry()
    {
        var root = new MongoId();
        var orphan = new RelaySettlementRecord(
            new MongoId(),
            root,
            [root],
            "scav-a",
            RewardRarity.ScavGrade,
            1,
            RelayRecordAction.Secure,
            null,
            RelayOutcome.Secured,
            null,
            [],
            0,
            0,
            false,
            false,
            DateTimeOffset.UnixEpoch,
            RelayRecordStatus.Prepared,
            null);

        Assert.Throws<InvalidOperationException>(() =>
            RelaySnapshotRouter.BuildPendingDiscovery(
                new CaseOpeningJournal(relayRecords: [orphan]),
                new Catalog()));
    }

    private static (CaseOpeningJournal Journal, RelaySettlementRecord Prepared) PreparedJournal(
        RelayRecordAction action)
    {
        var root = new MongoId();
        var input = new Item
        {
            Id = root,
            Template = (MongoId)"aaaaaaaaaaaaaaaaaaaaaaaa",
            ParentId = "stash",
            SlotId = "hideout"
        };
        var opening = new CaseOpeningRecord(
            new MongoId(),
            new MongoId(),
            "scav-a",
            [input],
            DateTimeOffset.UnixEpoch,
            OpeningRecordStatus.Committed,
            DateTimeOffset.UnixEpoch);
        var output = action == RelayRecordAction.Relay
            ? new[]
            {
                new Item
                {
                    Id = new MongoId(),
                    Template = (MongoId)"cccccccccccccccccccccccc",
                    ParentId = "stash",
                    SlotId = "hideout"
                }
            }
            : [];
        var prepared = new RelaySettlementRecord(
            opening.CaseId,
            root,
            opening.ExactRewardIds,
            opening.RewardId,
            RewardRarity.ScavGrade,
            1,
            action,
            action == RelayRecordAction.Relay ? new MongoId() : (MongoId?)null,
            action == RelayRecordAction.Relay ? RelayOutcome.RarityUpgrade : RelayOutcome.Secured,
            action == RelayRecordAction.Relay ? "uncommon-a" : null,
            output,
            0,
            0,
            false,
            false,
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            RelayRecordStatus.Prepared,
            null,
            opening.RewardItems);
        return (new CaseOpeningJournal([opening], [prepared]), prepared);
    }

    private static RaidSessionState EnterLobby(MongoId profileId)
    {
        var sessions = new RaidSessionState();
        sessions.BeginGameStart(profileId);
        sessions.CompleteGameStart(profileId, profileIsReady: true);
        return sessions;
    }

    private sealed class Catalog : IRelayRewardCatalog
    {
        private readonly IReadOnlyList<ValidatedReward> _rewards = RewardCatalog.Create([
            new RewardDefinition("scav-a", "Scav A", "aaaaaaaaaaaaaaaaaaaaaaaa", "p1", RewardRarity.ScavGrade, 0.25),
            new RewardDefinition("scav-b", "Scav B", "bbbbbbbbbbbbbbbbbbbbbbbb", "p2", RewardRarity.ScavGrade, 0.25),
            new RewardDefinition("uncommon-a", "Uncommon A", "cccccccccccccccccccccccc", "p3", RewardRarity.Uncommon, 0.25),
            new RewardDefinition("uncommon-b", "Uncommon B", "dddddddddddddddddddddddd", "p4", RewardRarity.Uncommon, 0.25)
        ]).Validate(new Resolver());

        public IReadOnlyList<ValidatedReward> Rewards => _rewards;
        public ValidatedReward FindReward(string rewardId) => _rewards.Single(reward => reward.Id == rewardId);

        private sealed class Resolver : IRewardPresetResolver
        {
            public RewardPresetTree Resolve(string presetId)
            {
                var template = presetId switch
                {
                    "p1" => "aaaaaaaaaaaaaaaaaaaaaaaa",
                    "p2" => "bbbbbbbbbbbbbbbbbbbbbbbb",
                    "p3" => "cccccccccccccccccccccccc",
                    "p4" => "dddddddddddddddddddddddd",
                    "p5" => "eeeeeeeeeeeeeeeeeeeeeeee",
                    "p6" => "ffffffffffffffffffffffff",
                    _ => throw new InvalidOperationException()
                };
                return new RewardPresetTree([new RewardPresetItem($"root-{presetId}", template, null)], 100);
            }
        }
    }

    private sealed class Store(IReadOnlyDictionary<MongoId, CaseOpeningJournal> journals)
        : ICaseOpeningJournalStore
    {
        public List<MongoId> LoadedProfiles { get; } = [];

        public ValueTask<CaseOpeningJournal> LoadAsync(
            MongoId profileId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadedProfiles.Add(profileId);
            return ValueTask.FromResult(journals[profileId]);
        }

        public ValueTask SaveAsync(
            MongoId profileId,
            CaseOpeningJournal journal,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
