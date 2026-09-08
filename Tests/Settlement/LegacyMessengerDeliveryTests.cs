using ContrabandCases.Server.Settlement;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Profile;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class LegacyMessengerDeliveryTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public async Task Legacy_mail_transaction_is_atomic_and_retries_never_reissue_collected_prizes(int failedSave, bool relay)
    {
        var fixture = new Fixture();
        var relayRecord = fixture.Relay();
        var store = new Store(relay ? new CaseOpeningJournal(relayRecords: [relayRecord]) : new CaseOpeningJournal([fixture.Record]))
            { FailedSave = failedSave == 4 ? 0 : failedSave };
        var committer = new Committer { Fail = failedSave == 4 };
        var raid = new RaidSessionState();
        raid.BeginGameStart(fixture.Context.ProfileId);
        raid.CompleteGameStart(fixture.Context.ProfileId, true);
        var locks = new ProfileLockPool();
        var service = new CaseOpeningService(store, new Preparation(), fixture.Delivery, committer, locks, raid,
            () => DateTimeOffset.UnixEpoch.AddMinutes(2));
        var relayService = new RelaySettlementService(store, new RelayPreparation(), fixture.Delivery, committer, locks, raid,
            new Catalog(fixture.Record.RewardId, fixture.Record.RewardItems[0].Template), () => DateTimeOffset.UnixEpoch.AddMinutes(2));
        Task Open(OpeningContext context) => relay ? relayService.RelayAsync(context, relayRecord.StakeRootId, CancellationToken.None)
            : service.OpenAsync(context, fixture.Record.CaseId, CancellationToken.None);
        store.BeforeSave = count =>
        {
            if (count <= 2) Assert.True(committer.Leased);
            else Assert.False(committer.Leased);
        };
        var originalItems = fixture.Context.PmcData.Inventory!.Items!.Select(CaseOpeningRecord.CloneItem).ToList();
        if (failedSave == 0) await Open(fixture.Context);
        else await Assert.ThrowsAsync<IOException>(() => Open(fixture.Context));
        Assert.False(committer.Leased);
        if (failedSave is 1 or 2)
        {
            Assert.Equal(2, fixture.Context.PmcData.Inventory!.Items!.Count);
            Assert.Null(fixture.Profile.DialogueRecords);
            Assert.Null(ManifestClaimCommitWitness.CaptureToken(fixture.Context.PmcData));
        }
        else
        {
            Assert.Empty(fixture.Context.PmcData.Inventory!.Items!);
            Assert.Single(fixture.Profile.DialogueRecords![SptManifestRewardDelivery.SenderId].Messages!);
        }
        store.FailedSave = 0;
        store.BeforeSave = null;
        committer.Fail = false;
        if (failedSave == 4)
        {
            // Native SaveServer caches MD5 before writing. A second same-process
            // call could falsely succeed without another disk write.
            await Assert.ThrowsAsync<InvalidOperationException>(() => Open(new OpeningContext(fixture.Context.PmcData,
                new ItemEventRouterResponse(), fixture.Context.ProfileId)));
            // Simulate restart after an unsuccessful profile write: journal has
            // the applied marker but the profile still has its original inputs.
            fixture.Context.PmcData.Inventory!.Items = originalItems;
            ManifestClaimCommitWitness.RestoreToken(fixture.Context.PmcData, null);
            fixture.Profile.DialogueRecords = null;
            var restarted = new ManifestClaimCommitUncertaintyCoordinator();
            service = new CaseOpeningService(store, new Preparation(), fixture.Delivery, committer, locks, raid,
                () => DateTimeOffset.UnixEpoch.AddMinutes(2), restarted);
            relayService = new RelaySettlementService(store, new RelayPreparation(), fixture.Delivery, committer, locks, raid,
                new Catalog(fixture.Record.RewardId, fixture.Record.RewardItems[0].Template), () => DateTimeOffset.UnixEpoch.AddMinutes(2), restarted);
        }
        if (failedSave is 0 or 3) fixture.Profile.DialogueRecords!.Clear();
        var retry = new OpeningContext(fixture.Context.PmcData, new ItemEventRouterResponse(), fixture.Context.ProfileId);
        await Open(retry);
        if (relay) Assert.Equal(RelayRecordStatus.Committed, Assert.Single(store.Journal.RelayRecords).Status);
        else Assert.Equal(OpeningRecordStatus.Committed, Assert.Single(store.Journal.Records).Status);
        Assert.Empty(retry.Response.ProfileChanges?.GetValueOrDefault(retry.ProfileId)?.Items?.NewItems ?? []);
        if (failedSave is 0 or 3) Assert.Empty(fixture.Profile.DialogueRecords!);
        else Assert.Single(fixture.Profile.DialogueRecords![SptManifestRewardDelivery.SenderId].Messages!);
    }

    [Fact]
    public void Winning_pending_legacy_relay_mails_its_exact_output_and_preserves_chain_identity()
    {
        var fixture = new Fixture();
        var relay = fixture.Relay();
        var planned = fixture.Delivery.PrepareRelayDelivery(fixture.Context, relay);
        var applied = fixture.Delivery.ApplyPreparedRelay(fixture.Context, planned);
        fixture.Delivery.StageRelayDeliveryCommit(fixture.Context, applied);
        Assert.True(applied.ProfileCommitStarted);
        Assert.Equal(relay.ExactOutputIds, applied.ExactOutputIds);
        Assert.Equal(relay.InputItemIds, applied.InputItemIds);
        var journal = new CaseOpeningJournal(relayRecords: [planned]);
        journal.ReplaceRelay(applied);
        applied = SptCaseJournal.FromDocument(SptCaseJournal.ToDocument(journal)).PreparedRelay!;
        fixture.Profile.DialogueRecords!.Clear();
        Assert.Equal(new RelayInventoryEvidence(RewardPresence.Absent, false, RewardPresence.Complete),
            fixture.Delivery.InspectRelay(fixture.Context, applied));
        fixture.Delivery.ReplayRelay(fixture.Context, applied.Commit(DateTimeOffset.UnixEpoch.AddMinutes(2)));
        Assert.Empty(fixture.Profile.DialogueRecords);
    }

    [Fact]
    public void Pending_legacy_opening_mails_exact_prize_and_recovers_after_attachment_collection()
    {
        var fixture = new Fixture();
        var prepared = fixture.Delivery.PrepareDelivery(fixture.Context, fixture.Record);
        Assert.NotNull(prepared.MailDelivery);
        var journal = new CaseOpeningJournal([fixture.Record]);
        journal.Replace(prepared);
        var restored = SptCaseJournal.FromDocument(SptCaseJournal.ToDocument(journal)).PreparedOpening!;
        var applied = fixture.Delivery.ApplyPrepared(fixture.Context, restored);
        Assert.Equal(fixture.Record.ExactRewardIds, applied.ExactRewardIds);
        Assert.Empty(fixture.Context.PmcData.Inventory!.Items!);
        Assert.Empty(fixture.Context.Response.ProfileChanges?.GetValueOrDefault(fixture.Context.ProfileId)?.Items?.NewItems ?? []);
        var message = Assert.Single(fixture.Profile.DialogueRecords![SptManifestRewardDelivery.SenderId].Messages!);
        Assert.Equal(fixture.Record.ExactRewardIds, message.Items!.Data!.Select(i => i.Id));
        fixture.Delivery.StageDeliveryCommit(fixture.Context, applied);
        message.Items.Data!.Clear(); // Native collection or deliberate message deletion must never cause a new grant.
        Assert.Equal(new InventoryEvidence(false, false, RewardPresence.Complete),
            fixture.Delivery.Inspect(fixture.Context, applied.CaseId, applied));
        fixture.Delivery.Replay(fixture.Context, applied.Commit(DateTimeOffset.UnixEpoch.AddMinutes(2)));
        Assert.Empty(message.Items.Data);
    }

    [Fact]
    public void Paid_legacy_inventory_prizes_keep_the_original_destination()
    {
        var fixture = new Fixture();
        var committed = fixture.Record.Commit(DateTimeOffset.UnixEpoch.AddMinutes(2));
        Assert.Same(committed, fixture.Delivery.PrepareDelivery(fixture.Context, committed));
        Assert.Null(committed.MailDelivery);
    }

    [Fact]
    public void Legacy_mail_plan_cannot_be_downgraded_or_attached_to_an_old_schema()
    {
        var fixture = new Fixture();
        var prepared = fixture.Delivery.PrepareDelivery(fixture.Context, fixture.Record);
        var journal = new CaseOpeningJournal([fixture.Record]);
        journal.Replace(prepared);
        Assert.Throws<InvalidOperationException>(() => journal.Replace(fixture.Record));
        var document = SptCaseJournal.ToDocument(journal);
        document.SchemaVersion = 7;
        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(document));
    }

    private sealed class Fixture
    {
        public OpeningContext Context { get; }
        public SptProfile Profile { get; }
        public CaseOpeningRecord Record { get; }
        public SptLegacyRewardDelivery Delivery { get; }
        public RelaySettlementRecord Relay()
        {
            var stake = Context.PmcData.Inventory!.Items!.Single(i => i.Id == Record.CaseId);
            return new(stake.Id, stake.Id, [stake.Id], "old-input", RewardRarity.ScavGrade,
                1, RelayRecordAction.Relay, Record.KeyId, RelayOutcome.RarityUpgrade, Record.RewardId,
                Record.RewardItems, 0, 0, false, false, Record.PreparedAtUtc, RelayRecordStatus.Prepared, null, [stake]);
        }
        public Fixture()
        {
            var caseId = new MongoId(); var keyId = new MongoId();
            var root = new Item { Id = new MongoId(), Template = new MongoId(), Upd = new() { StackObjectsCount = 7 } };
            Record = new(caseId, keyId, "saved-legacy-prize", [root], DateTimeOffset.UnixEpoch,
                OpeningRecordStatus.Prepared, null);
            var pmc = new PmcData { Inventory = new() { Items =
            [new() { Id = caseId, Template = ModConstants.CaseTemplateId }, new() { Id = keyId, Template = ModConstants.KeyTemplateId }] } };
            Context = new(pmc, new ItemEventRouterResponse(), new MongoId());
            Profile = new() { CharacterData = new() { PmcData = pmc } };
            var legacy = new Inventory();
            var mail = new SptManifestRewardDelivery(legacy, _ => Profile, (_, _) => Task.CompletedTask, _ => { });
            Delivery = new SptLegacyRewardDelivery(legacy, legacy, mail,
                (context, record) => Consume(context, [record.CaseId, record.KeyId]),
                (context, record) => Consume(context, record.InputItemIds.Append(record.KeyId!.Value)));
        }
        private static void Consume(OpeningContext context, IEnumerable<MongoId> ids)
        {
            var set = ids.ToHashSet();
            context.PmcData.Inventory!.Items!.RemoveAll(i => set.Contains(i.Id));
            SptResponseChanges.GetOrCreate(context.Response, context.ProfileId).DeletedItems!.AddRange(set.Select(i => new DeletedItem { Id = i }));
        }
    }

    private sealed class Inventory : IOpeningInventory, IManifestClaimInventory, IRelayInventory
    {
        public InventoryEvidence Inspect(OpeningContext c, MongoId id, CaseOpeningRecord? r) =>
            new(c.PmcData.Inventory!.Items!.Any(i => i.Id == id), c.PmcData.Inventory.Items!.Any(i => i.Id == r?.KeyId),
                c.PmcData.Inventory.Items!.Any(i => r?.ExactRewardIds.Contains(i.Id) == true) ? RewardPresence.Complete : RewardPresence.Absent);
        public InventoryCheckpoint Capture(OpeningContext c) => new Checkpoint(c.PmcData.Inventory!.Items!.Select(CaseOpeningRecord.CloneItem).ToList(), ManifestClaimCommitWitness.CaptureToken(c.PmcData));
        public CaseOpeningRecord ApplyPrepared(OpeningContext c, CaseOpeningRecord r) => throw new Xunit.Sdk.XunitException("Physical payout must not run");
        public void Restore(OpeningContext c, InventoryCheckpoint p)
        {
            var saved = (Checkpoint)p;
            c.PmcData.Inventory!.Items = saved.Items;
            ManifestClaimCommitWitness.RestoreToken(c.PmcData, saved.Witness);
            c.Response.ProfileChanges = null!;
        }
        public void Replay(OpeningContext c, CaseOpeningRecord r) => throw new Xunit.Sdk.XunitException("Physical replay must not run");
        public bool TryPrepareClaim(OpeningContext c, IReadOnlyList<Item> i, IReadOnlyList<MongoId> roots, DateTimeOffset t, out ManifestClaimPreparedPayload? p) => throw new NotSupportedException();
        public RewardPresence InspectClaim(OpeningContext c, ManifestClaimPreparedPayload p) => throw new NotSupportedException();
        public ManifestClaimPreparedPayload ReconcileAppliedClaim(OpeningContext c, ManifestClaimPreparedPayload p) => throw new NotSupportedException();
        public ManifestClaimPreparedPayload ApplyPreparedClaim(OpeningContext c, ManifestClaimPreparedPayload p) => throw new NotSupportedException();
        public void ReplayClaim(OpeningContext c, ManifestClaimPreparedPayload p) => throw new NotSupportedException();
        public RelayInventoryEvidence InspectRelay(OpeningContext c, RelaySettlementRecord r) => new(
            c.PmcData.Inventory!.Items!.Any(i => r.InputItemIds.Contains(i.Id)) ? RewardPresence.Complete : RewardPresence.Absent,
            c.PmcData.Inventory.Items!.Any(i => i.Id == r.KeyId),
            c.PmcData.Inventory.Items!.Any(i => r.ExactOutputIds.Contains(i.Id)) ? RewardPresence.Complete : RewardPresence.Absent);
        public InventoryCheckpoint CaptureRelay(OpeningContext c) => Capture(c);
        public RelaySettlementRecord ApplyPreparedRelay(OpeningContext c, RelaySettlementRecord r) => throw new Xunit.Sdk.XunitException("Physical Relay payout must not run");
        public void RestoreRelay(OpeningContext c, InventoryCheckpoint p) => Restore(c, p);
        public void ReplayRelay(OpeningContext c, RelaySettlementRecord r) => throw new Xunit.Sdk.XunitException("Physical Relay replay must not run");
        private sealed record Checkpoint(List<Item> Items, string? Witness) : InventoryCheckpoint;
    }
    private sealed class Preparation : IOpeningPreparation
    {
        public ValueTask<CaseOpeningRecord> PrepareAsync(OpeningContext c, MongoId id, CancellationToken token) =>
            throw new Xunit.Sdk.XunitException("Saved legacy rewards must not be rerolled");
    }
    private sealed class Store(CaseOpeningJournal initial) : ICaseOpeningJournalStore
    {
        public CaseOpeningJournal Journal = initial;
        public int FailedSave;
        public Action<int>? BeforeSave;
        private int _saves;
        public ValueTask<CaseOpeningJournal> LoadAsync(MongoId id, CancellationToken token) => ValueTask.FromResult(Copy(Journal));
        public ValueTask SaveAsync(MongoId id, CaseOpeningJournal journal, CancellationToken token)
        {
            _saves++; BeforeSave?.Invoke(_saves);
            if (_saves == FailedSave) throw new IOException("Simulated journal save failure");
            Journal = Copy(journal);
            return ValueTask.CompletedTask;
        }
        private static CaseOpeningJournal Copy(CaseOpeningJournal j) => SptCaseJournal.FromDocument(SptCaseJournal.ToDocument(j));
    }
    private sealed class Committer : IProfileCommitter
    {
        public bool Leased;
        public bool Fail;
        public ValueTask<IDisposable?> AcquireMutationLeaseAsync(MongoId id, CancellationToken token)
        { Assert.False(Leased); Leased = true; return ValueTask.FromResult<IDisposable?>(new Lease(() => Leased = false)); }
        public Task CommitAsync(MongoId id, CancellationToken token)
        { Assert.False(Leased); return Fail ? Task.FromException(new IOException("Profile write failure")) : Task.CompletedTask; }
        private sealed class Lease(Action release) : IDisposable
        {
            private Action? _release = release;
            public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }

    private sealed class RelayPreparation : IRelayPreparation
    {
        public ValueTask<RelaySettlementRecord> PrepareSecureAsync(OpeningContext c, RelayStake s, int meter, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<RelaySettlementRecord> PrepareRelayAsync(OpeningContext c, RelayStake s, int meter, CancellationToken token) => throw new Xunit.Sdk.XunitException("Pending Relay must not reroll");
    }
    private sealed class Catalog(string rewardId, string template) : IRelayRewardCatalog
    {
        public IReadOnlyList<ValidatedReward> Rewards { get; } = RewardCatalog.Create([
            new RewardDefinition(rewardId, "Saved reward", template, "preset", RewardRarity.Uncommon, 1d)
        ]).Validate(new Resolver(template));
        public ValidatedReward FindReward(string id) => Rewards.Single(r => r.Id == id);
        private sealed class Resolver(string tpl) : IRewardPresetResolver
        { public RewardPresetTree Resolve(string id) => new([new RewardPresetItem("root", tpl, null)]); }
    }
}
