using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class CaseOpeningJournalTests
{
    [Fact]
    public void Add_rejects_duplicate_case_ids()
    {
        var record = Record(OpeningRecordStatus.Prepared, 1);
        var journal = new CaseOpeningJournal(new[] { record });

        Assert.Throws<InvalidOperationException>(() => journal.Add(record));
    }

    [Fact]
    public void Replace_requires_matching_case_id()
    {
        var journal = new CaseOpeningJournal();

        Assert.Throws<InvalidOperationException>(() => journal.Replace(Record(OpeningRecordStatus.Prepared, 1)));
    }

    [Fact]
    public void Loaded_journal_immediately_keeps_the_prepared_and_exact_newest_256_committed_by_case_id_tie_break()
    {
        var prepared = new[] { Record(OpeningRecordStatus.Prepared, 1) };
        var committed = Enumerable.Range(1, 260)
            .Select(index => Record(OpeningRecordStatus.Committed, 1, new MongoId(index.ToString("x24"))))
            .ToArray();
        var expectedCommittedIds = committed
            .OrderByDescending(record => record.CaseId.ToString(), StringComparer.Ordinal)
            .Take(256)
            .Select(record => record.CaseId)
            .ToHashSet();

        var journal = new CaseOpeningJournal(prepared.Concat(committed));

        Assert.Equal(257, journal.Records.Count);
        Assert.All(prepared, record => Assert.Contains(record, journal.Records));
        var actualCommittedIds = journal.Records
            .Where(record => record.Status == OpeningRecordStatus.Committed)
            .Select(record => record.CaseId)
            .ToHashSet();
        Assert.Equal(expectedCommittedIds, actualCommittedIds);
    }

    [Theory]
    [InlineData(PreparedTransactionKind.Opening, PreparedTransactionKind.Opening)]
    [InlineData(PreparedTransactionKind.Opening, PreparedTransactionKind.Secure)]
    [InlineData(PreparedTransactionKind.Opening, PreparedTransactionKind.Relay)]
    [InlineData(PreparedTransactionKind.Secure, PreparedTransactionKind.Secure)]
    [InlineData(PreparedTransactionKind.Secure, PreparedTransactionKind.Relay)]
    [InlineData(PreparedTransactionKind.Relay, PreparedTransactionKind.Relay)]
    public void Construction_rejects_more_than_one_prepared_profile_transaction(
        PreparedTransactionKind first,
        PreparedTransactionKind second)
    {
        var openings = new List<CaseOpeningRecord>();
        var relays = new List<RelaySettlementRecord>();
        AddPrepared(first, openings, relays);
        AddPrepared(second, openings, relays);

        Assert.Throws<ArgumentException>(() => new CaseOpeningJournal(openings, relays));
    }

    [Theory]
    [InlineData(RelayRecordAction.Secure)]
    [InlineData(RelayRecordAction.Relay)]
    public void Add_rejects_a_prepared_opening_while_a_relay_transaction_is_prepared(RelayRecordAction action)
    {
        var journal = new CaseOpeningJournal(relayRecords: [RelayRecord(action, RelayRecordStatus.Prepared)]);

        Assert.Throws<InvalidOperationException>(() => journal.Add(Record(OpeningRecordStatus.Prepared, 1)));
    }

    [Theory]
    [InlineData(RelayRecordAction.Secure)]
    [InlineData(RelayRecordAction.Relay)]
    public void AddRelay_rejects_a_prepared_relay_transaction_while_an_opening_is_prepared(RelayRecordAction action)
    {
        var journal = new CaseOpeningJournal([Record(OpeningRecordStatus.Prepared, 1)]);

        Assert.Throws<InvalidOperationException>(() =>
            journal.AddRelay(RelayRecord(action, RelayRecordStatus.Prepared)));
    }

    [Theory]
    [InlineData(RelayRecordAction.Secure)]
    [InlineData(RelayRecordAction.Relay)]
    public void Replace_rejects_preparing_an_opening_while_a_relay_transaction_is_prepared(RelayRecordAction action)
    {
        var committed = Record(OpeningRecordStatus.Committed, 1);
        var journal = new CaseOpeningJournal(
            [committed],
            [RelayRecord(action, RelayRecordStatus.Prepared)]);
        var replacement = Record(OpeningRecordStatus.Prepared, 2, committed.CaseId);

        Assert.Throws<InvalidOperationException>(() => journal.Replace(replacement));
    }

    [Theory]
    [InlineData(RelayRecordAction.Secure)]
    [InlineData(RelayRecordAction.Relay)]
    public void ReplaceRelay_rejects_preparing_a_relay_transaction_while_an_opening_is_prepared(
        RelayRecordAction action)
    {
        var committedRelay = RelayRecord(action, RelayRecordStatus.Committed);
        var journal = new CaseOpeningJournal(
            [Record(OpeningRecordStatus.Prepared, 1)],
            [committedRelay]);
        var replacement = RelayRecord(action, RelayRecordStatus.Prepared, committedRelay.StakeRootId);

        Assert.Throws<InvalidOperationException>(() => journal.ReplaceRelay(replacement));
    }

    [Theory]
    [InlineData(RelayRecordAction.Secure)]
    [InlineData(RelayRecordAction.Relay)]
    public void Replacing_the_exact_prepared_transaction_remains_legal(RelayRecordAction action)
    {
        var opening = Record(OpeningRecordStatus.Prepared, 1);
        var openingJournal = new CaseOpeningJournal([opening]);
        var relay = RelayRecord(action, RelayRecordStatus.Prepared);
        var relayJournal = new CaseOpeningJournal(relayRecords: [relay]);

        openingJournal.Replace(opening);
        relayJournal.ReplaceRelay(relay);

        Assert.Same(opening, Assert.Single(openingJournal.Records));
        Assert.Same(relay, Assert.Single(relayJournal.RelayRecords));
    }

    [Fact]
    public void Opening_replacement_allows_exact_commit_but_rejects_rollback_and_evidence_rewrite()
    {
        var prepared = Record(OpeningRecordStatus.Prepared, 1);
        var committed = prepared.Commit(DateTimeOffset.UnixEpoch.AddSeconds(2));
        var journal = new CaseOpeningJournal([prepared]);

        journal.Replace(committed);

        Assert.Same(committed, Assert.Single(journal.Records));
        Assert.Throws<InvalidOperationException>(() => journal.Replace(prepared));

        var rewriteJournal = new CaseOpeningJournal([prepared]);
        var rewritten = Record(OpeningRecordStatus.Prepared, 2, prepared.CaseId);
        Assert.Throws<InvalidOperationException>(() => rewriteJournal.Replace(rewritten));
        Assert.Same(prepared, Assert.Single(rewriteJournal.Records));
    }

    [Fact]
    public void Opening_prepared_checkpoint_may_reconcile_root_placement_but_not_stable_reward_evidence()
    {
        var rewardRoot = new Item
        {
            Id = new MongoId(),
            Template = new MongoId(),
            ParentId = "simulated-stash",
            SlotId = "hideout",
            Location = new ItemLocation { X = 1, Y = 2 }
        };
        var prepared = new CaseOpeningRecord(
            new MongoId(),
            new MongoId(),
            "reward",
            [rewardRoot],
            DateTimeOffset.UnixEpoch,
            OpeningRecordStatus.Prepared,
            null);
        var locatedRoot = rewardRoot with
        {
            ParentId = "live-stash",
            SlotId = "hideout",
            Location = new ItemLocation { X = 3, Y = 4 }
        };
        var located = new CaseOpeningRecord(
            prepared.CaseId,
            prepared.KeyId,
            prepared.RewardId,
            [locatedRoot],
            prepared.PreparedAtUtc,
            OpeningRecordStatus.Prepared,
            null);
        var journal = new CaseOpeningJournal([prepared]);

        journal.Replace(located);

        Assert.Same(located, Assert.Single(journal.Records));

        var corruptRoot = locatedRoot with { Template = new MongoId() };
        var corrupt = new CaseOpeningRecord(
            prepared.CaseId,
            prepared.KeyId,
            prepared.RewardId,
            [corruptRoot],
            prepared.PreparedAtUtc,
            OpeningRecordStatus.Prepared,
            null);
        var corruptJournal = new CaseOpeningJournal([prepared]);
        Assert.Throws<InvalidOperationException>(() => corruptJournal.Replace(corrupt));
        Assert.Same(prepared, Assert.Single(corruptJournal.Records));
    }

    [Fact]
    public void Relay_replacement_allows_marker_checkpoint_then_exact_commit_but_never_rolls_back()
    {
        var prepared = RelayRecord(RelayRecordAction.Relay, RelayRecordStatus.Prepared);
        var commitStarted = prepared.BeginProfileCommit();
        var committed = commitStarted.Commit(DateTimeOffset.UnixEpoch.AddSeconds(1));
        var journal = new CaseOpeningJournal(relayRecords: [prepared]);

        journal.ReplaceRelay(commitStarted);
        journal.ReplaceRelay(committed);

        Assert.Same(committed, Assert.Single(journal.RelayRecords));
        Assert.Throws<InvalidOperationException>(() => journal.ReplaceRelay(commitStarted));

        var markerJournal = new CaseOpeningJournal(relayRecords: [commitStarted]);
        Assert.Throws<InvalidOperationException>(() => markerJournal.ReplaceRelay(prepared));
        Assert.Same(commitStarted, Assert.Single(markerJournal.RelayRecords));
    }

    [Fact]
    public void Relay_profile_checkpoint_may_reconcile_root_placement_but_not_stable_output_evidence()
    {
        var prepared = PreparedUpgradeRelay();
        var preparedOutput = Assert.Single(prepared.OutputItems);
        var locatedOutput = preparedOutput with
        {
            ParentId = "live-stash",
            SlotId = "hideout",
            Location = new ItemLocation { X = 3, Y = 4 }
        };
        var located = prepared.BeginProfileCommit([locatedOutput]);
        var journal = new CaseOpeningJournal(relayRecords: [prepared]);

        journal.ReplaceRelay(located);

        Assert.Same(located, Assert.Single(journal.RelayRecords));

        var corruptOutput = locatedOutput with { Template = new MongoId() };
        var corrupt = prepared.BeginProfileCommit([corruptOutput]);
        var corruptJournal = new CaseOpeningJournal(relayRecords: [prepared]);
        Assert.Throws<InvalidOperationException>(() => corruptJournal.ReplaceRelay(corrupt));
        Assert.Same(prepared, Assert.Single(corruptJournal.RelayRecords));
    }

    [Fact]
    public void Relay_replacement_rejects_immutable_transaction_identity_rewrite()
    {
        var prepared = RelayRecord(RelayRecordAction.Relay, RelayRecordStatus.Prepared);
        var rewritten = RelayRecord(
            RelayRecordAction.Secure,
            RelayRecordStatus.Prepared,
            prepared.StakeRootId);
        var journal = new CaseOpeningJournal(relayRecords: [prepared]);

        Assert.Throws<InvalidOperationException>(() => journal.ReplaceRelay(rewritten));
        Assert.Same(prepared, Assert.Single(journal.RelayRecords));
    }

    [Fact]
    public void Record_rejects_malformed_committed_timestamp_and_unknown_status()
    {
        Assert.Throws<ArgumentException>(() => new CaseOpeningRecord(
            new MongoId(), new MongoId(), "reward", new(), DateTimeOffset.UnixEpoch, OpeningRecordStatus.Committed, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaseOpeningRecord(
            new MongoId(), new MongoId(), "reward", new(), DateTimeOffset.UnixEpoch, (OpeningRecordStatus)91, null));
    }

    [Fact]
    public void Record_deep_clones_location_upd_and_extension_data_from_input_and_projection()
    {
        var location = new ItemLocation { X = 1, Y = 2 };
        var repairable = new UpdRepairable { Durability = 90, MaxDurability = 100 };
        var extensionLocation = new ItemLocation { X = 3, Y = 4 };
        var updExtensionLocation = new ItemLocation { X = 5, Y = 6 };
        var repairableExtensionLocation = new ItemLocation { X = 7, Y = 8 };
        var markerExtensionLocation = new ItemLocation { X = 9, Y = 10 };
        var marker = new MapMarker { Type = "probe", X = 1, Y = 2 };
        marker.ExtensionData!["markerExtensionLocation"] = markerExtensionLocation;
        var original = new Item
        {
            Id = new MongoId(),
            Template = new MongoId(),
            Desc = "original",
            Location = location,
            Upd = new Upd { Repairable = repairable, Map = new UpdMap { Markers = new List<MapMarker> { marker } } }
        };
        original.ExtensionData!["extensionLocation"] = extensionLocation;
        original.Upd!.ExtensionData!["updExtensionLocation"] = updExtensionLocation;
        repairable.ExtensionData["repairableExtensionLocation"] = repairableExtensionLocation;
        var record = new CaseOpeningRecord(
            new MongoId(), new MongoId(), "reward", new List<Item> { original }, DateTimeOffset.UnixEpoch, OpeningRecordStatus.Prepared, null);

        location.X = 10;
        repairable.Durability = 10;
        extensionLocation.Y = 40;
        updExtensionLocation.X = 50;
        repairableExtensionLocation.Y = 80;
        markerExtensionLocation.X = 90;
        var projection = record.RewardItems.Single();
        ((ItemLocation)projection.Location!).Y = 20;
        projection.Upd!.Repairable!.MaxDurability = 20;
        Assert.IsType<ItemLocation>(projection.ExtensionData!["extensionLocation"]).X = 30;
        Assert.IsType<ItemLocation>(projection.Upd.ExtensionData!["updExtensionLocation"]).Y = 60;
        Assert.IsType<ItemLocation>(projection.Upd.Repairable.ExtensionData["repairableExtensionLocation"]).X = 70;
        Assert.IsType<ItemLocation>(projection.Upd.Map!.Markers!.Single().ExtensionData!["markerExtensionLocation"]).Y = 100;

        var stored = record.RewardItems.Single();
        Assert.Equal(1, Assert.IsType<ItemLocation>(stored.Location).X);
        Assert.Equal(2, Assert.IsType<ItemLocation>(stored.Location).Y);
        Assert.Equal(90, stored.Upd!.Repairable!.Durability);
        Assert.Equal(100, stored.Upd.Repairable.MaxDurability);
        var storedExtension = Assert.IsType<ItemLocation>(stored.ExtensionData!["extensionLocation"]);
        Assert.Equal(3, storedExtension.X);
        Assert.Equal(4, storedExtension.Y);
        var storedUpdExtension = Assert.IsType<ItemLocation>(stored.Upd.ExtensionData!["updExtensionLocation"]);
        Assert.Equal(5, storedUpdExtension.X);
        Assert.Equal(6, storedUpdExtension.Y);
        var storedRepairableExtension = Assert.IsType<ItemLocation>(stored.Upd.Repairable.ExtensionData["repairableExtensionLocation"]);
        Assert.Equal(7, storedRepairableExtension.X);
        Assert.Equal(8, storedRepairableExtension.Y);
        var storedMarkerExtension = Assert.IsType<ItemLocation>(stored.Upd.Map!.Markers!.Single().ExtensionData!["markerExtensionLocation"]);
        Assert.Equal(9, storedMarkerExtension.X);
        Assert.Equal(10, storedMarkerExtension.Y);
        Assert.Equal(original.Id, record.ExactRewardIds.Single());
    }

    [Fact]
    public void Record_accepts_and_isolates_array_backed_sight_enumerables()
    {
        var calibrationIndexes = new[] { 1, 2, 3 };
        var selectedModes = new[] { 4, 5 };
        var original = new Item
        {
            Id = new MongoId(),
            Template = new MongoId(),
            Upd = new Upd
            {
                Sight = new UpdSight
                {
                    ScopesCurrentCalibPointIndexes = calibrationIndexes,
                    ScopesSelectedModes = selectedModes
                }
            }
        };
        var record = new CaseOpeningRecord(
            new MongoId(), new MongoId(), "reward", new List<Item> { original }, DateTimeOffset.UnixEpoch, OpeningRecordStatus.Prepared, null);

        calibrationIndexes[0] = 10;
        selectedModes[0] = 40;
        var projection = record.RewardItems.Single().Upd!.Sight!;
        Assert.Equal(new[] { 1, 2, 3 }, projection.ScopesCurrentCalibPointIndexes);
        Assert.Equal(new[] { 4, 5 }, projection.ScopesSelectedModes);
        Assert.IsAssignableFrom<IList<int>>(projection.ScopesCurrentCalibPointIndexes)[1] = 20;
        Assert.IsAssignableFrom<IList<int>>(projection.ScopesSelectedModes)[1] = 50;

        var storedSight = record.RewardItems.Single().Upd!.Sight!;
        Assert.Equal(new[] { 1, 2, 3 }, storedSight.ScopesCurrentCalibPointIndexes);
        Assert.Equal(new[] { 4, 5 }, storedSight.ScopesSelectedModes);
    }

    [Fact]
    public void Record_restores_each_clone_of_a_repeated_marker_reference()
    {
        var markerExtensionLocation = new ItemLocation { X = 9, Y = 10 };
        var marker = new MapMarker { Type = "probe", X = 1, Y = 2 };
        marker.ExtensionData!["markerExtensionLocation"] = markerExtensionLocation;
        var original = new Item
        {
            Id = new MongoId(),
            Template = new MongoId(),
            Upd = new Upd { Map = new UpdMap { Markers = new List<MapMarker> { marker, marker } } }
        };
        var record = new CaseOpeningRecord(
            new MongoId(), new MongoId(), "reward", new List<Item> { original }, DateTimeOffset.UnixEpoch, OpeningRecordStatus.Prepared, null);

        markerExtensionLocation.X = 90;
        var projection = record.RewardItems.Single();
        var projectedMarkers = projection.Upd!.Map!.Markers!;
        Assert.Equal(2, projectedMarkers.Count);
        Assert.All(projectedMarkers, projectedMarker =>
        {
            Assert.Equal("probe", projectedMarker.Type);
            Assert.Equal(1, projectedMarker.X);
            Assert.Equal(2, projectedMarker.Y);
            var projectedLocation = Assert.IsType<ItemLocation>(projectedMarker.ExtensionData!["markerExtensionLocation"]);
            Assert.Equal(9, projectedLocation.X);
            Assert.Equal(10, projectedLocation.Y);
        });
        Assert.IsType<ItemLocation>(projectedMarkers[0].ExtensionData!["markerExtensionLocation"]).Y = 100;
        projectedMarkers.Clear();

        var storedMarkers = record.RewardItems.Single().Upd!.Map!.Markers!;
        Assert.Equal(2, storedMarkers.Count);
        Assert.All(storedMarkers, storedMarker =>
        {
            var storedLocation = Assert.IsType<ItemLocation>(storedMarker.ExtensionData!["markerExtensionLocation"]);
            Assert.Equal(9, storedLocation.X);
            Assert.Equal(10, storedLocation.Y);
        });
    }

    [Fact]
    public void Record_rejects_cyclic_extension_containers_without_recursing_forever()
    {
        var cycle = new List<object?>();
        cycle.Add(cycle);
        var original = new Item
        {
            Id = new MongoId(),
            Template = new MongoId()
        };
        original.ExtensionData!["cycle"] = cycle;

        var exception = Assert.Throws<InvalidOperationException>(() => new CaseOpeningRecord(
            new MongoId(), new MongoId(), "reward", new List<Item> { original }, DateTimeOffset.UnixEpoch, OpeningRecordStatus.Prepared, null));

        Assert.Contains("reference cycles", exception.Message, StringComparison.Ordinal);
    }

    private static CaseOpeningRecord Record(OpeningRecordStatus status, int tick, MongoId? caseId = null) => new(
        caseId ?? new MongoId(),
        new MongoId(),
        "reward",
        new(),
        DateTimeOffset.UnixEpoch.AddSeconds(tick),
        status,
        status == OpeningRecordStatus.Committed ? DateTimeOffset.UnixEpoch.AddSeconds(tick) : null);

    private static RelaySettlementRecord RelayRecord(
        RelayRecordAction action,
        RelayRecordStatus status,
        MongoId? stakeRootId = null)
    {
        var root = stakeRootId ?? new MongoId();
        var committed = status == RelayRecordStatus.Committed;
        return new RelaySettlementRecord(
            new MongoId(),
            root,
            [root],
            "reward",
            RewardRarity.ScavGrade,
            1,
            action,
            action == RelayRecordAction.Relay ? new MongoId() : (MongoId?)null,
            action == RelayRecordAction.Relay ? RelayOutcome.Confiscated : RelayOutcome.Secured,
            null,
            [],
            0,
            action == RelayRecordAction.Relay ? RelayRules.MeterAfterConfiscation(0, 1) : 0,
            false,
            action == RelayRecordAction.Relay && committed,
            DateTimeOffset.UnixEpoch,
            status,
            committed ? DateTimeOffset.UnixEpoch.AddSeconds(1) : null);
    }

    private static RelaySettlementRecord PreparedUpgradeRelay()
    {
        var stakeRoot = new MongoId();
        var outputRoot = new Item
        {
            Id = new MongoId(),
            Template = new MongoId(),
            ParentId = "simulated-stash",
            SlotId = "hideout",
            Location = new ItemLocation { X = 1, Y = 2 }
        };
        return new RelaySettlementRecord(
            new MongoId(),
            stakeRoot,
            [stakeRoot],
            "reward",
            RewardRarity.ScavGrade,
            1,
            RelayRecordAction.Relay,
            new MongoId(),
            RelayOutcome.RarityUpgrade,
            "output",
            [outputRoot],
            0,
            0,
            false,
            false,
            DateTimeOffset.UnixEpoch,
            RelayRecordStatus.Prepared,
            null);
    }

    private static void AddPrepared(
        PreparedTransactionKind kind,
        ICollection<CaseOpeningRecord> openings,
        ICollection<RelaySettlementRecord> relays)
    {
        switch (kind)
        {
            case PreparedTransactionKind.Opening:
                openings.Add(Record(OpeningRecordStatus.Prepared, openings.Count + relays.Count + 1));
                break;
            case PreparedTransactionKind.Secure:
                relays.Add(RelayRecord(RelayRecordAction.Secure, RelayRecordStatus.Prepared));
                break;
            case PreparedTransactionKind.Relay:
                relays.Add(RelayRecord(RelayRecordAction.Relay, RelayRecordStatus.Prepared));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    public enum PreparedTransactionKind
    {
        Opening,
        Secure,
        Relay
    }
}
