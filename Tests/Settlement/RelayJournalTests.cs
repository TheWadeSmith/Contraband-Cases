using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class RelayJournalTests
{
    [Fact]
    public void V1_committed_opening_migrates_to_legacy_secure_terminal_without_meter_change()
    {
        var opening = Opening(0);
        var document = new SptCaseJournalDocument
        {
            SchemaVersion = 1,
            Records = [OpeningDocument(opening)]
        };

        var journal = SptCaseJournal.FromDocument(document);
        var root = opening.RewardRootId!.Value;
        var stake = RelayChainResolver.Resolve(journal, root, FindReward);

        Assert.True(journal.IsLegacySecured(root));
        Assert.Equal(0, journal.RecoveryMeter);
        Assert.Empty(journal.RelayRecords);
        Assert.True(stake.Terminal);
        Assert.Equal(RelayOutcome.Secured, stake.TerminalOutcome);
    }

    [Fact]
    public void Forward_journal_schema_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(new SptCaseJournalDocument
        {
            SchemaVersion = SptCaseJournalDocument.CurrentSchemaVersion + 1,
            Records = []
        }));
    }

    [Fact]
    public void V2_roundtrip_preserves_input_snapshot_outputless_marker_and_meter()
    {
        var root = new MongoId();
        var input = new Item
        {
            Id = root,
            Template = (MongoId)"aaaaaaaaaaaaaaaaaaaaaaaa",
            ParentId = "stash",
            SlotId = "hideout"
        };
        var record = new RelaySettlementRecord(
            new MongoId(), root, [root], "reward", RewardRarity.ScavGrade, 1,
            RelayRecordAction.Relay, new MongoId(), RelayOutcome.Confiscated, null, [],
            0, 1, false, true, DateTimeOffset.UnixEpoch, RelayRecordStatus.Prepared, null, [input]);
        var journal = new CaseOpeningJournal(relayRecords: [record], recoveryMeter: 0);

        var document = SptCaseJournal.ToDocument(journal);
        var restored = SptCaseJournal.FromDocument(document);

        Assert.Equal(SptCaseJournalDocument.CurrentSchemaVersion, document.SchemaVersion);
        var restoredRecord = Assert.Single(restored.RelayRecords);
        Assert.True(restoredRecord.ProfileCommitStarted);
        Assert.Empty(restoredRecord.OutputItems);
        Assert.Equal(input.Template, Assert.Single(restoredRecord.InputItems).Template);
        Assert.Equal(0, restored.RecoveryMeter);
    }

    [Fact]
    public void Old_unresolved_opening_survives_more_than_history_limit_later_terminal_openings()
    {
        var unresolved = Opening(0);
        var terminal = Enumerable.Range(1, 300).Select(tick => Opening(tick)).ToArray();
        var journal = new CaseOpeningJournal(
            [unresolved, .. terminal],
            recoveryMeter: 0,
            legacySecuredStakeRoots: terminal.Select(record => record.RewardRootId!.Value));

        Assert.Contains(journal.Records, record => record.CaseId == unresolved.CaseId);
        Assert.Equal(257, journal.Records.Count);
    }

    [Fact]
    public void Terminal_relay_history_is_bounded_but_old_active_upgrade_tip_is_preserved()
    {
        var active = UpgradeRecord(-100);
        var terminal = Enumerable.Range(1, 300).Select(SecureRecord).ToArray();

        var journal = new CaseOpeningJournal(relayRecords: [active, .. terminal]);

        Assert.Contains(journal.RelayRecords, record => record.StakeRootId == active.StakeRootId);
        Assert.Equal(257, journal.RelayRecords.Count);
    }

    [Fact]
    public void Old_secure_chain_never_becomes_eligible_when_history_is_pruned()
    {
        var oldOpening = Opening(-1000);
        var openings = new List<CaseOpeningRecord> { oldOpening };
        var relays = new List<RelaySettlementRecord> { SecureRecord(oldOpening, -999) };
        for (var tick = 1; tick <= 300; tick++)
        {
            var opening = Opening(tick);
            openings.Add(opening);
            relays.Add(SecureRecord(opening, tick));
        }

        var journal = new CaseOpeningJournal(openings, relays);
        var oldRoot = oldOpening.RewardRootId!.Value;

        Assert.DoesNotContain(journal.Records, record => record.CaseId == oldOpening.CaseId);
        Assert.Null(journal.FindRelay(oldRoot));
        Assert.Throws<InvalidOperationException>(() =>
            RelayChainResolver.Resolve(journal, oldRoot, FindReward));
    }

    [Fact]
    public void Prepared_action_keeps_its_opening_and_producer_ancestry_after_pruning()
    {
        var opening = Opening(-1000);
        var producer = UpgradeRecord(opening, -999, RewardRarity.ScavGrade, "contractor");
        var prepared = new RelaySettlementRecord(
            opening.CaseId,
            producer.OutputRootId!.Value,
            producer.ExactOutputIds,
            "contractor",
            RewardRarity.Contractor,
            2,
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
            null,
            producer.OutputItems);
        var openings = new List<CaseOpeningRecord> { opening };
        var relays = new List<RelaySettlementRecord> { producer, prepared };
        AddTerminalHistory(openings, relays, 300);

        var journal = new CaseOpeningJournal(openings, relays);
        var stake = RelayChainResolver.Resolve(journal, prepared.StakeRootId, FindReward);
        var snapshot = RelaySnapshotRouter.BuildSnapshot(journal, stake, new TestCatalog());

        Assert.Contains(journal.Records, record => record.CaseId == opening.CaseId);
        Assert.Contains(journal.RelayRecords, record => record.StakeRootId == producer.StakeRootId);
        Assert.Same(journal.PreparedRelay, journal.FindRelay(prepared.StakeRootId));
        Assert.True(snapshot.Status.SettlementPending);
        Assert.Equal("Secure", snapshot.Status.PendingAction);
    }

    [Fact]
    public void Just_committed_consumer_keeps_its_opening_for_immediate_snapshot()
    {
        var openings = new List<CaseOpeningRecord>();
        var relays = new List<RelaySettlementRecord>();
        AddTerminalHistory(openings, relays, 300, firstTick: -500);
        var opening = Opening(1000);
        var secure = SecureRecord(opening, 1001);
        openings.Add(opening);
        relays.Add(secure);

        var journal = new CaseOpeningJournal(openings, relays);
        var stake = RelayChainResolver.Resolve(journal, opening.RewardRootId!.Value, FindReward);
        var snapshot = RelaySnapshotRouter.BuildSnapshot(journal, stake, new TestCatalog());

        Assert.NotNull(snapshot.LatestReceipt);
        Assert.Equal("Secure", snapshot.LatestReceipt!.Action);
        Assert.True(snapshot.LatestReceipt.Replay);
    }

    [Fact]
    public void Old_relay_eligible_upgrade_tip_and_ancestry_remain_resolvable()
    {
        var opening = Opening(-1000);
        var active = UpgradeRecord(opening, -999, RewardRarity.ScavGrade, "contractor");
        var openings = new List<CaseOpeningRecord> { opening };
        var relays = new List<RelaySettlementRecord> { active };
        AddTerminalHistory(openings, relays, 300);

        var journal = new CaseOpeningJournal(openings, relays);
        var stake = RelayChainResolver.Resolve(journal, active.OutputRootId!.Value, FindReward);

        Assert.Contains(journal.Records, record => record.CaseId == opening.CaseId);
        Assert.Contains(journal.RelayRecords, record => record.StakeRootId == active.StakeRootId);
        Assert.Equal(2, stake.Stage);
        Assert.False(stake.Terminal);
    }

    [Fact]
    public void Black_label_terminal_upgrade_ages_out_with_bounded_history()
    {
        var opening = Opening(
            -1000,
            "restricted",
            "cccccccccccccccccccccccc");
        var blackLabel = UpgradeRecord(opening, -999, RewardRarity.Restricted, "black");
        var openings = new List<CaseOpeningRecord> { opening };
        var relays = new List<RelaySettlementRecord> { blackLabel };
        AddTerminalHistory(openings, relays, 300);

        var journal = new CaseOpeningJournal(openings, relays);

        Assert.DoesNotContain(journal.Records, record => record.CaseId == opening.CaseId);
        Assert.DoesNotContain(journal.RelayRecords, record => record.StakeRootId == blackLabel.StakeRootId);
        Assert.Throws<InvalidOperationException>(() =>
            RelayChainResolver.Resolve(journal, blackLabel.OutputRootId!.Value, FindReward));
        Assert.True(journal.RelayRecords.Count <= 256);
    }

    [Fact]
    public void Prepared_consumer_snapshot_explicitly_tells_client_which_action_to_resume()
    {
        var opening = Opening(0);
        var root = opening.RewardRootId!.Value;
        var prepared = new RelaySettlementRecord(
            opening.CaseId, root, opening.ExactRewardIds, opening.RewardId, RewardRarity.ScavGrade, 1,
            RelayRecordAction.Secure, null, RelayOutcome.Secured, null, [], 0, 0, false, false,
            DateTimeOffset.UnixEpoch.AddSeconds(1), RelayRecordStatus.Prepared, null);
        var journal = new CaseOpeningJournal([opening], [prepared]);
        var stake = RelayChainResolver.Resolve(journal, root, FindReward);

        var status = RelaySnapshotRouter.CreateStatus(journal, stake);

        Assert.True(status.SettlementPending);
        Assert.Equal("Secure", status.PendingAction);
        Assert.False(status.RelayEligible);
    }

    [Fact]
    public void Journal_rejects_more_than_one_prepared_profile_transaction()
    {
        var first = PreparedSecureRecord(1);
        var second = PreparedSecureRecord(2);

        Assert.Throws<ArgumentException>(() =>
            new CaseOpeningJournal(relayRecords: [first, second]));
    }

    [Fact]
    public void Journal_rejects_adding_a_second_prepared_profile_transaction()
    {
        var journal = new CaseOpeningJournal(relayRecords: [PreparedSecureRecord(1)]);

        Assert.Throws<InvalidOperationException>(() =>
            journal.AddRelay(PreparedSecureRecord(2)));
    }

    [Fact]
    public void Different_stake_snapshot_is_not_actionable_while_profile_transaction_is_pending()
    {
        var pendingOpening = Opening(1);
        var otherOpening = Opening(2);
        var pending = new RelaySettlementRecord(
            pendingOpening.CaseId,
            pendingOpening.RewardRootId!.Value,
            pendingOpening.ExactRewardIds,
            pendingOpening.RewardId,
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
            null,
            pendingOpening.RewardItems);
        var journal = new CaseOpeningJournal([pendingOpening, otherOpening], [pending]);
        var otherStake = RelayChainResolver.Resolve(
            journal,
            otherOpening.RewardRootId!.Value,
            FindReward);

        var status = RelaySnapshotRouter.CreateStatus(journal, otherStake);

        Assert.False(status.RelayEligible);
        Assert.False(status.SettlementPending);
        Assert.Null(status.PendingAction);
    }

    [Fact]
    public void Applying_committed_meter_requires_the_exact_meter_before_value()
    {
        var root = new MongoId();
        var committed = new RelaySettlementRecord(
            new MongoId(), root, [root], "reward", RewardRarity.ScavGrade, 2,
            RelayRecordAction.Relay, new MongoId(), RelayOutcome.Confiscated, null, [],
            1, 3, false, true, DateTimeOffset.UnixEpoch, RelayRecordStatus.Committed,
            DateTimeOffset.UnixEpoch.AddSeconds(1));
        var journal = new CaseOpeningJournal(relayRecords: [committed], recoveryMeter: 3);

        Assert.Throws<InvalidOperationException>(() => journal.ApplyCommittedMeter(committed));
    }

    [Theory]
    [InlineData(RelayOutcome.RarityUpgrade, 2, false)]
    [InlineData(RelayOutcome.SameRaritySidegrade, 1, true)]
    public void Only_rarity_upgrade_unlocks_the_next_stage(
        RelayOutcome outcome,
        int expectedStage,
        bool expectedTerminal)
    {
        var opening = Opening(0);
        var inputRoot = opening.RewardRootId!.Value;
        var output = new Item
        {
            Id = new MongoId(),
            Template = outcome == RelayOutcome.RarityUpgrade
                ? (MongoId)"bbbbbbbbbbbbbbbbbbbbbbbb"
                : (MongoId)"aaaaaaaaaaaaaaaaaaaaaaaa",
            ParentId = "stash",
            SlotId = "hideout"
        };
        var record = new RelaySettlementRecord(
            opening.CaseId, inputRoot, opening.ExactRewardIds, "reward", RewardRarity.ScavGrade, 1,
            RelayRecordAction.Relay, new MongoId(), outcome,
            outcome == RelayOutcome.RarityUpgrade ? "contractor" : "reward", [output], 0, 0, false, true,
            DateTimeOffset.UnixEpoch, RelayRecordStatus.Committed, DateTimeOffset.UnixEpoch);
        var journal = new CaseOpeningJournal([opening], [record]);

        var stake = RelayChainResolver.Resolve(journal, output.Id, FindReward);

        Assert.Equal(expectedStage, stake.Stage);
        Assert.Equal(expectedTerminal, stake.Terminal);
    }

    private static CaseOpeningRecord Opening(
        int tick,
        string rewardId = "reward",
        string templateId = "aaaaaaaaaaaaaaaaaaaaaaaa")
    {
        var root = new Item
        {
            Id = new MongoId(),
            Template = (MongoId)templateId,
            ParentId = "stash",
            SlotId = "hideout"
        };
        return new CaseOpeningRecord(
            new MongoId(), new MongoId(), rewardId, [root], DateTimeOffset.UnixEpoch.AddSeconds(tick),
            OpeningRecordStatus.Committed, DateTimeOffset.UnixEpoch.AddSeconds(tick));
    }

    private static SptCaseOpeningDocument OpeningDocument(CaseOpeningRecord record) => new()
    {
        CaseId = record.CaseId,
        KeyId = record.KeyId,
        RewardId = record.RewardId,
        RewardItems = record.RewardItems.ToList(),
        PreparedAtUtc = record.PreparedAtUtc,
        Status = record.Status,
        CommittedAtUtc = record.CommittedAtUtc
    };

    private static RelaySettlementRecord SecureRecord(int tick)
    {
        var root = new MongoId();
        return new RelaySettlementRecord(
            new MongoId(), root, [root], "reward", RewardRarity.ScavGrade, 1,
            RelayRecordAction.Secure, null, RelayOutcome.Secured, null, [], 0, 0, false, false,
            DateTimeOffset.UnixEpoch.AddSeconds(tick), RelayRecordStatus.Committed,
            DateTimeOffset.UnixEpoch.AddSeconds(tick));
    }

    private static RelaySettlementRecord PreparedSecureRecord(int tick)
    {
        var root = new MongoId();
        return new RelaySettlementRecord(
            new MongoId(), root, [root], "reward", RewardRarity.ScavGrade, 1,
            RelayRecordAction.Secure, null, RelayOutcome.Secured, null, [], 0, 0, false, false,
            DateTimeOffset.UnixEpoch.AddSeconds(tick), RelayRecordStatus.Prepared, null);
    }

    private static RelaySettlementRecord UpgradeRecord(int tick)
    {
        var root = new MongoId();
        return new RelaySettlementRecord(
            new MongoId(), root, [root], "reward", RewardRarity.ScavGrade, 1,
            RelayRecordAction.Relay, new MongoId(), RelayOutcome.RarityUpgrade, "output",
            [new Item { Id = new MongoId(), Template = new MongoId(), ParentId = "stash", SlotId = "hideout" }],
            0, 0, false, true, DateTimeOffset.UnixEpoch.AddSeconds(tick), RelayRecordStatus.Committed,
            DateTimeOffset.UnixEpoch.AddSeconds(tick));
    }

    private static RelaySettlementRecord SecureRecord(CaseOpeningRecord opening, int tick)
    {
        var root = opening.RewardRootId!.Value;
        return new RelaySettlementRecord(
            opening.CaseId,
            root,
            opening.ExactRewardIds,
            opening.RewardId,
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
            DateTimeOffset.UnixEpoch.AddSeconds(tick),
            RelayRecordStatus.Committed,
            DateTimeOffset.UnixEpoch.AddSeconds(tick),
            opening.RewardItems);
    }

    private static RelaySettlementRecord UpgradeRecord(
        CaseOpeningRecord opening,
        int tick,
        RewardRarity inputRarity,
        string outputRewardId)
    {
        var outputTemplate = outputRewardId switch
        {
            "contractor" => "bbbbbbbbbbbbbbbbbbbbbbbb",
            "black" => "dddddddddddddddddddddddd",
            _ => throw new InvalidOperationException()
        };
        var output = new Item
        {
            Id = new MongoId(),
            Template = (MongoId)outputTemplate,
            ParentId = "stash",
            SlotId = "hideout"
        };
        return new RelaySettlementRecord(
            opening.CaseId,
            opening.RewardRootId!.Value,
            opening.ExactRewardIds,
            opening.RewardId,
            inputRarity,
            1,
            RelayRecordAction.Relay,
            new MongoId(),
            RelayOutcome.RarityUpgrade,
            outputRewardId,
            [output],
            0,
            0,
            false,
            true,
            DateTimeOffset.UnixEpoch.AddSeconds(tick),
            RelayRecordStatus.Committed,
            DateTimeOffset.UnixEpoch.AddSeconds(tick),
            opening.RewardItems);
    }

    private static void AddTerminalHistory(
        ICollection<CaseOpeningRecord> openings,
        ICollection<RelaySettlementRecord> relays,
        int count,
        int firstTick = 1)
    {
        for (var offset = 0; offset < count; offset++)
        {
            var tick = firstTick + offset;
            var opening = Opening(tick);
            openings.Add(opening);
            relays.Add(SecureRecord(opening, tick));
        }
    }

    private static readonly IReadOnlyList<ValidatedReward> Rewards = RewardCatalog.Create([
        new RewardDefinition("reward", "Reward", "aaaaaaaaaaaaaaaaaaaaaaaa", "preset", RewardRarity.ScavGrade, 0.25d),
        new RewardDefinition("contractor", "Contractor", "bbbbbbbbbbbbbbbbbbbbbbbb", "contractor-preset", RewardRarity.Contractor, 0.25d),
        new RewardDefinition("restricted", "Restricted", "cccccccccccccccccccccccc", "restricted-preset", RewardRarity.Restricted, 0.25d),
        new RewardDefinition("black", "Black Label", "dddddddddddddddddddddddd", "black-preset", RewardRarity.BlackLabel, 0.25d)
    ]).Validate(new Resolver());

    private static ValidatedReward FindReward(string rewardId) =>
        Rewards.Single(reward => reward.Id == rewardId);

    private sealed class TestCatalog : IRelayRewardCatalog
    {
        public IReadOnlyList<ValidatedReward> Rewards => RelayJournalTests.Rewards;
        public ValidatedReward FindReward(string rewardId) => RelayJournalTests.FindReward(rewardId);
    }

    private sealed class Resolver : IRewardPresetResolver
    {
        public RewardPresetTree Resolve(string presetId) => presetId switch
        {
            "preset" => new([new RewardPresetItem("root", "aaaaaaaaaaaaaaaaaaaaaaaa", null)]),
            "contractor-preset" => new([new RewardPresetItem("root-b", "bbbbbbbbbbbbbbbbbbbbbbbb", null)]),
            "restricted-preset" => new([new RewardPresetItem("root-c", "cccccccccccccccccccccccc", null)]),
            "black-preset" => new([new RewardPresetItem("root-d", "dddddddddddddddddddddddd", null)]),
            _ => throw new InvalidOperationException()
        };
    }
}
