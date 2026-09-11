using System.Collections.Concurrent;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;
using Microsoft.AspNetCore.Http;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class ManifestSettlementServiceTests
{
    [Fact]
    public async Task Saved_sidegrade_is_readable_after_restart_and_delivers_to_Messenger_once()
    {
        var lots = RelayLots();
        var catalog = CatalogWithUnrelatedDrift(lots.Select(lot => lot.Resolved));
        var fixture = Fixture.FromJournal(new CaseOpeningJournal(activeManifest: EntitlementManifest(RelayCandidates(lots))));
        var keyInventory = new FreshRelayKeyInventoryProbe();
        var service = fixture.CreateService(relayKeyInventory: keyInventory,
            catalogCoordinator: Coordinator(catalog),
            nextUnitNumerator: () => CanonicalRngEvidence.UnitDenominator * 70 / 100);
        await service.RelayAsync(fixture.Context, ManifestId, ManifestPhase.Entitlement, 1, CancellationToken.None);

        var active = fixture.Store.Stored.ActiveManifest!;
        Assert.Equal(ManifestRelayResult.Sidegrade, Assert.Single(active.RelayHistory).Outcome);
        var projected = ManifestSnapshotProjection.FromActive(active, catalog, null);
        var response = System.Text.Json.JsonSerializer.Serialize(new
        { err = 0, errmsg = (string?)null, data = new ManifestSnapshotEnvelope { Snapshot = projected } });
        var parsed = global::ContrabandCases.Client.Opening.ManifestSnapshotEnvelope.Parse(response, ManifestId);
        Assert.True(parsed.RelayTerminal);
        Assert.True(parsed.AvailableActions.CanClaim);
        Assert.False(parsed.AvailableActions.CanRelay);
        Assert.Null(parsed.Relay!.UpgradeGrade);
        Assert.Contains("sidegrade", parsed.Relay.TerminalReason);
        Assert.Equal(active.Entitlement!.Fingerprint.Sha256Hex, parsed.CurrentLot!.Fingerprint);

        var profile = new SptProfile { CharacterData = new() { PmcData = fixture.Context.PmcData } };
        var delivery = new SptManifestRewardDelivery(fixture.Inventory, _ => profile,
            (_, _) => Task.CompletedTask, _ => { });
        var restarted = fixture.CreateService(catalogCoordinator: Coordinator(catalog), claimInventory: delivery,
            nextUnitNumerator: () => throw new Exception("Must not reroll a saved sidegrade"));
        await restarted.ClaimAsync(fixture.Context, ManifestId, CancellationToken.None);
        var replay = await restarted.ClaimAsync(fixture.NewContextWithLostResponse(), ManifestId, CancellationToken.None);
        Assert.True(replay.Replay);
        Assert.Single(profile.DialogueRecords![SptManifestRewardDelivery.SenderId].Messages!);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(1, keyInventory.ApplyCalls);
        Assert.Equal(active.Entitlement.Fingerprint, Assert.Single(fixture.Store.Stored.ManifestReceipts).Entitlement!.Fingerprint);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Ticket_and_relay_key_staging_exclude_native_saves_until_witness_or_rollback(bool relay, bool failJournal)
    {
        var lots = RelayLots();
        var active = relay ? EntitlementManifest(RelayCandidates(lots)) : TicketPreparedManifest(false);
        var fixture = Fixture.FromJournal(new CaseOpeningJournal(activeManifest: active));
        using var gate = new SemaphoreSlim(1, 1);
        var staged = false;
        var restored = false;
        var released = 0;
        fixture.Committer.MutationLeaseFactory = async (_, token) =>
        {
            await gate.WaitAsync(token);
            return new TestSaveLease(() =>
            {
                Assert.Equal(!failJournal, staged);
                Assert.Equal(failJournal, restored);
                released++;
                gate.Release();
            });
        };
        Action apply = () => Assert.False(gate.Wait(0));
        Action stage = () => { Assert.False(gate.Wait(0)); staged = true; };
        var ticket = new FreshTicketInventoryProbe { BeforeApply = apply, OnStage = stage, OnRestore = () => restored = true };
        var key = new FreshRelayKeyInventoryProbe { BeforeApply = apply, OnStage = stage, OnRestore = () => restored = true };
        fixture.Store.BeforeSave = attempt =>
        {
            if (attempt != (relay ? 2 : 1)) return;
            Assert.False(gate.Wait(0));
            if (failJournal) throw new IOException("staged input journal failure");
        };
        fixture.Committer.BeforeCommit = () => { Assert.True(gate.Wait(0)); gate.Release(); };
        var service = fixture.CreateService(ticketInventory: ticket, relayKeyInventory: key,
            catalogCoordinator: Coordinator(relay ? CatalogWithUnrelatedDrift(lots.Select(l => l.Resolved)) : Catalog()),
            nextUnitNumerator: () => CanonicalRngEvidence.UnitDenominator - 1);
        var operation = relay
            ? service.RelayAsync(fixture.Context, ManifestId, ManifestPhase.Entitlement, 1, CancellationToken.None)
            : service.OpenAsync(fixture.Context, CaseId, CancellationToken.None);
        if (failJournal) await Assert.ThrowsAsync<IOException>(() => operation);
        else await operation;
        Assert.Equal(1, released);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Messenger_staging_and_rollback_exclude_background_profile_saves(int failedJournalSave)
    {
        var fixture = Fixture.Entitlement();
        var profile = new SptProfile { CharacterData = new() { PmcData = fixture.Context.PmcData } };
        var delivery = new SptManifestRewardDelivery(fixture.Inventory, _ => profile,
            (_, _) => Task.CompletedTask, _ => { });
        using var nativeSaveGate = new SemaphoreSlim(1, 1);
        var releases = 0;
        fixture.Committer.MutationLeaseFactory = async (_, token) =>
        {
            await nativeSaveGate.WaitAsync(token);
            return new TestSaveLease(() =>
            {
                // A save immediately after release must see either a complete
                // delivery + witness, or a completely restored pre-claim profile.
                var mail = profile.DialogueRecords?.Values.SelectMany(d => d.Messages ?? []).ToArray() ?? [];
                if (failedJournalSave == 0)
                {
                    Assert.Single(mail);
                    var active = fixture.Store.Stored.ActiveManifest!;
                    Assert.Equal(ManifestClaimCommitWitnessInspection.Current,
                        ManifestClaimCommitWitness.Inspect(fixture.Context.PmcData, ProfileId,
                            ManifestId, active.Entitlement!.Fingerprint, active.ClaimPrepared!));
                }
                else
                {
                    Assert.Empty(mail);
                    Assert.Null(ManifestClaimCommitWitness.CaptureToken(fixture.Context.PmcData));
                }
                releases++;
                nativeSaveGate.Release();
            });
        };
        fixture.Store.BeforeSave = attempt =>
        {
            if (attempt is 2 or 3)
            {
                Assert.NotEmpty(profile.DialogueRecords![SptManifestRewardDelivery.SenderId].Messages!);
                Assert.Null(ManifestClaimCommitWitness.CaptureToken(fixture.Context.PmcData));
                Assert.False(nativeSaveGate.Wait(0));
            }
        };
        fixture.Committer.BeforeCommit = () =>
        {
            Assert.True(nativeSaveGate.Wait(0));
            nativeSaveGate.Release();
        };
        if (failedJournalSave != 0) fixture.Store.FailBeforeSaveAttempt = failedJournalSave;
        var claim = fixture.CreateService(claimInventory: delivery)
            .ClaimAsync(fixture.Context, ManifestId, CancellationToken.None);
        if (failedJournalSave == 0) await claim;
        else await Assert.ThrowsAsync<IOException>(() => claim);
        Assert.Equal(1, releases);
        Assert.Equal(1, nativeSaveGate.CurrentCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Messenger_claim_ignores_stash_capacity_and_never_reissues_collected_or_deleted_mail(bool deleteMail)
    {
        var fixture = Fixture.Entitlement();
        fixture.Inventory.PrepareSucceeds = false;
        var profile = new SptProfile { CharacterData = new() { PmcData = fixture.Context.PmcData } };
        var notifications = 0;
        var delivery = new SptManifestRewardDelivery(fixture.Inventory, _ => profile, (_, _) =>
        {
            Assert.Equal(1, fixture.Committer.Calls);
            Assert.Single(fixture.Store.Stored.ManifestClaimGrants);
            notifications++;
            return Task.CompletedTask;
        }, _ => throw new Exception("Unexpected notification failure"));
        var service = fixture.CreateService(claimInventory: delivery);
        var before = fixture.Context.PmcData.Inventory!.Items!.Count;
        await service.ClaimAsync(fixture.Context, ManifestId, CancellationToken.None);
        Assert.Equal(before, fixture.Context.PmcData.Inventory.Items.Count);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(0, fixture.Inventory.PrepareCalls);
        Assert.Empty(fixture.Context.Response.ProfileChanges?.GetValueOrDefault(ProfileId)?.Items?.NewItems ?? []);
        var message = Assert.Single(profile.DialogueRecords![SptManifestRewardDelivery.SenderId].Messages!);
        Assert.Equal(RewardId, Assert.Single(message.Items!.Data!).Id);
        Assert.Equal(1, notifications);
        Assert.Equal(ClaimDeliveryKind.Messenger, Assert.Single(fixture.Store.Stored.ManifestClaimGrants).ClaimPayload.Delivery);
        if (deleteMail) profile.DialogueRecords.Clear();
        else { message.Items.Data!.Clear(); message.HasRewards = false; message.RewardCollected = true; }
        var replay = await fixture.CreateService(claimInventory: delivery)
            .ClaimAsync(fixture.NewContextWithLostResponse(), ManifestId, CancellationToken.None);
        Assert.True(replay.Replay);
        Assert.Empty(profile.DialogueRecords.Values.SelectMany(d => d.Messages ?? []).SelectMany(m => m.Items?.Data ?? []));
        Assert.Equal(1, fixture.Committer.Calls);
        Assert.Equal(1, notifications);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Messenger_journal_failure_recovers_exactly_once(int failureSave)
    {
        var fixture = Fixture.Entitlement();
        var profile = new SptProfile { CharacterData = new() { PmcData = fixture.Context.PmcData } };
        var delivery = new SptManifestRewardDelivery(fixture.Inventory, _ => profile,
            (_, _) => Task.CompletedTask, _ => { });
        fixture.Store.FailAfterSaveAttempt = failureSave;
        await Assert.ThrowsAsync<IOException>(() => fixture.CreateService(claimInventory: delivery)
            .ClaimAsync(fixture.Context, ManifestId, CancellationToken.None));
        fixture.Store.FailAfterSaveAttempt = null;
        if (failureSave == 4)
        {
            // Profile and terminal journal committed; player took an attachment before retry.
            profile.DialogueRecords!.Clear();
        }
        await fixture.CreateService(claimInventory: delivery).ClaimAsync(fixture.NewContextWithLostResponse(), ManifestId, CancellationToken.None);
        Assert.Single(fixture.Store.Stored.ManifestClaimGrants);
        Assert.Equal(1, fixture.Committer.Calls);
        Assert.Equal(failureSave == 4 ? 0 : 1, profile.DialogueRecords?.Values.SelectMany(d => d.Messages ?? []).Count() ?? 0);
    }

    [Theory]
    [InlineData(ClaimStage.PreparedFalse)]
    [InlineData(ClaimStage.RewardOwed)]
    public async Task Legacy_uncommitted_claim_migrates_to_mail_without_changing_prize(ClaimStage stage)
    {
        var fixture = Fixture.ActiveClaim(stage, materializerMustNotRun: true);
        var before = fixture.Store.Stored.ActiveManifest!.ClaimPrepared!;
        var profile = new SptProfile { CharacterData = new() { PmcData = fixture.Context.PmcData } };
        var delivery = new SptManifestRewardDelivery(fixture.Inventory, _ => profile, (_, _) => Task.CompletedTask, _ => { });
        await fixture.CreateService(claimInventory: delivery).ClaimAsync(fixture.Context, ManifestId, CancellationToken.None);
        var after = Assert.Single(fixture.Store.Stored.ManifestClaimGrants).ClaimPayload;
        Assert.Equal(ClaimDeliveryKind.Messenger, after.Delivery);
        Assert.Equal(before.ExactItemIds, after.ExactItemIds);
        SptOpeningInventory.EnsureExactItemState(before.Items, after.Items, "legacy migration test");
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
    }

    [Fact]
    public async Task Saved_mail_survives_notification_failure_without_failed_claim_or_resend()
    {
        var fixture = Fixture.Entitlement();
        var profile = new SptProfile { CharacterData = new() { PmcData = fixture.Context.PmcData } };
        var errors = 0;
        var delivery = new SptManifestRewardDelivery(fixture.Inventory, _ => profile,
            (_, _) => throw new IOException("offline notification"), _ => errors++);
        await fixture.CreateService(claimInventory: delivery).ClaimAsync(fixture.Context, ManifestId, CancellationToken.None);
        Assert.Equal(1, errors);
        Assert.Single(profile.DialogueRecords![SptManifestRewardDelivery.SenderId].Messages!);
        Assert.Single(fixture.Store.Stored.ManifestClaimGrants);
    }

    [Fact]
    public async Task Held_Messenger_notification_releases_claim_request_and_snapshot_read_without_resending_mail()
    {
        var fixture = Fixture.Entitlement();
        var profile = new SptProfile { CharacterData = new() { PmcData = fixture.Context.PmcData } };
        var notificationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<Exception>();
        var notifications = 0;
        var delivery = new SptManifestRewardDelivery(fixture.Inventory, _ => profile, (_, _) =>
        {
            notifications++;
            notificationStarted.TrySetResult();
            return heldNotification.Task;
        }, errors.Enqueue);
        var service = fixture.CreateService(claimInventory: delivery);
        ManifestClaimResult? result = null;
        // The native item-event listener owns an outer lock too. Releasing only
        // settlement's nested lease would still leave the player's profile stuck.
        var listener = new ProfileItemEventGateListener(_ => true, async (_, _, token) =>
            result = await service.ClaimAsync(fixture.Context, ManifestId, token), fixture.ProfileLocks);
        var request = listener.HandleAsync(ProfileId, new DefaultHttpContext());
        try
        {
            await notificationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(fixture.Store.Stored.ManifestClaimGrants);
            var read = fixture.ReadSnapshotAsync();

            await request.WaitAsync(TimeSpan.FromSeconds(5));
            var snapshot = global::ContrabandCases.Client.Opening.ManifestSnapshotEnvelope.Parse(
                await read.WaitAsync(TimeSpan.FromSeconds(5)), ManifestId);
            Assert.Equal(ManifestPhase.Granted, snapshot.Phase);
            Assert.Equal(ManifestClaimResultKind.Granted, result!.Kind);
            Assert.False(heldNotification.Task.IsCompleted);
            Assert.IsType<TimeoutException>(Assert.Single(errors));

            profile.DialogueRecords!.Clear(); // Player discarded the saved message.
            var replay = await service.ClaimAsync(fixture.NewContextWithLostResponse(), ManifestId, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(replay.Replay);
            Assert.Empty(profile.DialogueRecords);
            Assert.Single(fixture.Store.Stored.ManifestClaimGrants);
            Assert.Equal(1, fixture.Committer.Calls);
            Assert.Equal(1, notifications);
        }
        finally
        {
            heldNotification.TrySetResult();
            await request.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Late_Messenger_notification_reads_detached_items_after_claim_releases_profile()
    {
        var fixture = Fixture.Entitlement();
        var profile = new SptProfile { CharacterData = new() { PmcData = fixture.Context.PmcData } };
        var releaseNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<(int ItemCount, double? StackCount, string? Text)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = new SptManifestRewardDelivery(fixture.Inventory, _ => profile, async (_, message) =>
        {
            await releaseNotification.Task;
            observed.SetResult((message.Items!.Data!.Count,
                message.Items.Data.FirstOrDefault()?.Upd?.StackObjectsCount, message.Text));
        }, _ => { });
        var claim = fixture.CreateService(claimInventory: delivery).ClaimAsync(fixture.Context, ManifestId, CancellationToken.None);
        try
        {
            await claim.WaitAsync(TimeSpan.FromSeconds(5));
            var saved = Assert.Single(profile.DialogueRecords![SptManifestRewardDelivery.SenderId].Messages!);
            var originalText = saved.Text;
            saved.Items!.Data![0].Upd!.StackObjectsCount = 999;
            saved.Items.Data.Clear();
            saved.Text = "changed after commit";
            releaseNotification.SetResult();
            var sent = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, sent.ItemCount);
            Assert.Equal(1, sent.StackCount);
            Assert.Equal(originalText, sent.Text);
            Assert.Empty(saved.Items.Data);
        }
        finally
        {
            releaseNotification.TrySetResult();
            await claim.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Late_Messenger_notification_fault_is_observed_without_changing_committed_reward()
    {
        var fixture = Fixture.Entitlement();
        var profile = new SptProfile { CharacterData = new() { PmcData = fixture.Context.PmcData } };
        var heldNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateFailure = new IOException("socket failed after notification deadline");
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = new SptManifestRewardDelivery(fixture.Inventory, _ => profile, (_, _) => heldNotification.Task,
            error => { if (error is not TimeoutException) observed.TrySetResult(error); });
        var claim = fixture.CreateService(claimInventory: delivery).ClaimAsync(fixture.Context, ManifestId, CancellationToken.None);
        try
        {
            await claim.WaitAsync(TimeSpan.FromSeconds(5));
            heldNotification.SetException(lateFailure);
            Assert.Same(lateFailure, await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Single(profile.DialogueRecords![SptManifestRewardDelivery.SenderId].Messages!);
            Assert.Single(fixture.Store.Stored.ManifestClaimGrants);
            Assert.Equal(1, fixture.Committer.Calls);
        }
        finally
        {
            heldNotification.TrySetResult();
            await claim.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Messenger_notification_failure_reporter_cannot_fail_a_committed_claim()
    {
        var fixture = Fixture.Entitlement();
        var profile = new SptProfile { CharacterData = new() { PmcData = fixture.Context.PmcData } };
        var delivery = new SptManifestRewardDelivery(fixture.Inventory, _ => profile,
            (_, _) => throw new IOException("notification unavailable"),
            _ => throw new IOException("logger unavailable"));
        var result = await fixture.CreateService(claimInventory: delivery)
            .ClaimAsync(fixture.Context, ManifestId, CancellationToken.None);
        Assert.Equal(ManifestClaimResultKind.Granted, result.Kind);
        Assert.Single(profile.DialogueRecords![SptManifestRewardDelivery.SenderId].Messages!);
        Assert.Single(fixture.Store.Stored.ManifestClaimGrants);
    }

    [Fact]
    public async Task Messenger_notification_timeout_stops_remaining_batch_without_removing_saved_attachments()
    {
        var fixture = Fixture.Entitlement();
        var profile = new SptProfile { CharacterData = new() { PmcData = fixture.Context.PmcData } };
        var heldNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = 0;
        var errors = new ConcurrentQueue<Exception>();
        var delivery = new SptManifestRewardDelivery(fixture.Inventory, _ => profile, (_, _) =>
        {
            notifications++;
            return notifications == 1 ? heldNotification.Task : Task.CompletedTask;
        }, errors.Enqueue);
        var items = Enumerable.Range(0, 9).Select(_ => new Item
        { Id = new MongoId(), Template = TemplateA, Upd = new() { StackObjectsCount = 1 } }).ToArray();
        var prepared = new ManifestClaimPreparedPayload(items, items.Select(item => item.Id), false,
            CompletionAt, delivery: ClaimDeliveryKind.Messenger);
        var applied = delivery.ApplyPreparedClaim(fixture.Context, prepared);
        var notification = delivery.NotifyClaimAsync(fixture.Context, applied);
        try
        {
            await notification.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(heldNotification.Task.IsCompleted);
            Assert.Equal(1, notifications);
            Assert.IsType<TimeoutException>(Assert.Single(errors));
            var messages = profile.DialogueRecords![SptManifestRewardDelivery.SenderId].Messages!;
            Assert.Equal(2, messages.Count);
            Assert.Equal(items.Select(item => item.Id), messages.SelectMany(message => message.Items!.Data!).Select(item => item.Id));
        }
        finally
        {
            heldNotification.TrySetResult();
            await notification.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Messenger_matching_profile_witness_never_refills_mail_after_terminal_journal_failure(bool deleteMessage)
    {
        var fixture = Fixture.Entitlement();
        var profile = new SptProfile { CharacterData = new() { PmcData = fixture.Context.PmcData } };
        var delivery = new SptManifestRewardDelivery(fixture.Inventory, _ => profile, (_, _) => Task.CompletedTask, _ => { });
        fixture.Store.FailBeforeSaveAttempt = 4;
        await Assert.ThrowsAsync<IOException>(() => fixture.CreateService(claimInventory: delivery)
            .ClaimAsync(fixture.Context, ManifestId, CancellationToken.None));
        Assert.Equal(ManifestPhase.RewardOwed, fixture.Store.Stored.ActiveManifest!.FlowState.Phase);
        Assert.Equal(1, fixture.Committer.Calls);
        var dialogue = profile.DialogueRecords![SptManifestRewardDelivery.SenderId];
        if (deleteMessage) dialogue.Messages!.Clear();
        else dialogue.Messages![0].Items!.Data!.Clear();
        fixture.Store.FailBeforeSaveAttempt = null;
        await fixture.CreateService(claimInventory: delivery).ClaimAsync(fixture.NewContextWithLostResponse(), ManifestId, CancellationToken.None);
        Assert.Empty(dialogue.Messages!.SelectMany(m => m.Items?.Data ?? []));
        Assert.Single(fixture.Store.Stored.ManifestClaimGrants);
        Assert.Equal(1, fixture.Committer.Calls);
    }

    [Fact]
    public async Task Messenger_uncertain_profile_commit_stops_retries_until_restart_without_notification()
    {
        var fixture = Fixture.Entitlement();
        var profile = new SptProfile { CharacterData = new() { PmcData = fixture.Context.PmcData } };
        var notifications = 0;
        var delivery = new SptManifestRewardDelivery(fixture.Inventory, _ => profile,
            (_, _) => { notifications++; return Task.CompletedTask; }, _ => { });
        fixture.Committer.ExceptionAfterBoundary = new IOException("uncertain save");
        await Assert.ThrowsAsync<IOException>(() => fixture.CreateService(claimInventory: delivery)
            .ClaimAsync(fixture.Context, ManifestId, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateService(claimInventory: delivery)
            .ClaimAsync(fixture.Context, ManifestId, CancellationToken.None));
        Assert.Equal(0, notifications);
        Assert.Single(profile.DialogueRecords![SptManifestRewardDelivery.SenderId].Messages!);
        fixture.Committer.ExceptionAfterBoundary = null;
        await fixture.CreateService(uncertaintyCoordinator: new ManifestClaimCommitUncertaintyCoordinator(), claimInventory: delivery)
            .ClaimAsync(fixture.Context, ManifestId, CancellationToken.None);
        Assert.Single(fixture.Store.Stored.ManifestClaimGrants);
        Assert.Equal(1, fixture.Committer.Calls);
    }
    [Theory]
    [InlineData(TestingCrateType.EpicMixed)]
    [InlineData(TestingCrateType.LegendaryMixed)]
    public async Task Unavailable_forced_premium_tier_preserves_case_and_key_with_clear_message(TestingCrateType type)
    {
        var fixture = Fixture.FromJournal(new CaseOpeningJournal());
        var inventory = new FreshTicketInventoryProbe { FreshCaseTemplate = ModConstants.CaseTemplateId };
        var registry = new TestingForcedCrateRegistry();
        registry.SetForcedCrate(CaseId, type);
        var service = fixture.CreateService(ticketInventory: inventory, forcedCrateRegistry: registry);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.OpenAsync(fixture.Context, CaseId, CancellationToken.None));
        Assert.Contains("not enough qualifying packages", error.Message);
        Assert.Contains("case and key were not consumed", error.Message);
        Assert.Null(fixture.Store.Stored.ActiveManifest);
        Assert.Equal(0, inventory.ApplyCalls);
        Assert.Empty(fixture.Store.Saved);
    }

    [Theory]
    [InlineData(TestingCrateType.EpicMixed)]
    [InlineData(TestingCrateType.LegendaryMixed)]
    [InlineData(TestingCrateType.EpicOperations)]
    [InlineData(TestingCrateType.LegendaryOperations)]
    [InlineData(TestingCrateType.EpicRelics)]
    [InlineData(TestingCrateType.LegendaryRelics)]
    [InlineData(TestingCrateType.EpicBlackSite)]
    [InlineData(TestingCrateType.LegendaryBlackSite)]
    public async Task Forced_premium_themes_survive_prepared_ticket_restart_without_reroll(TestingCrateType type)
    {
        var template = TestingCrateTypeCodec.CaseTemplate(type);
        var lots = new[] { "core", "eco-ww2.relic-cache", "vault" }.SelectMany(provider =>
            new[] { "arsenal", "operator", "field-supply" }.SelectMany((family, index) => new[]
            {
                Lot(provider, "premium-" + index, family, TemplateA, RewardRarity.BlackLabel, index,
                    trackId: "track-" + index).Resolved,
                Lot(provider, "ordinary-" + index, family, TemplateB, RewardRarity.Uncommon, index,
                    weight: 100, trackId: "track-" + index).Resolved
            })).ToArray();
        var catalog = Catalog(lots, providerWeights: lots.Select(lot => lot.Identity.ProviderId)
            .Distinct().ToDictionary(provider => provider, _ => 1d));
        var coordinator = Coordinator(catalog);
        var view = coordinator.GetCaseSnapshot(template);
        var fixture = Fixture.FromJournal(new CaseOpeningJournal());
        var inventory = new FreshTicketInventoryProbe { FreshCaseTemplate = template };
        var registry = new TestingForcedCrateRegistry();
        registry.SetForcedCrate(CaseId, type);
        fixture.Store.FailAfterSaveAttempt = 1;
        var service = fixture.CreateService(ticketInventory: inventory, catalogCoordinator: coordinator,
            forcedCrateRegistry: registry, nextUnitNumerator: () => CanonicalRngEvidence.UnitDenominator - 1);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.OpenAsync(fixture.Context, CaseId, CancellationToken.None, view.SnapshotId));
        var prepared = fixture.Store.Stored.ActiveManifest!;
        Assert.NotNull(prepared);
        Assert.Equal(ManifestPhase.TicketPrepared, prepared.FlowState.Phase);
        Assert.Equal(TestingCrateTypeCodec.PremiumTier(type), prepared.Ticket.OpeningQuality!.Tier);
        Assert.True(prepared.Ticket.OpeningQuality.ForcedTest);
        Assert.Equal(0, inventory.ApplyCalls);
        Assert.All(prepared.Offers, offer => Assert.True(CaseCatalogs.Includes(template, offer.Identity)));
        // New service has no testing tags or usable random source. Saved evidence wins.
        var recoveredInventory = new FreshTicketInventoryProbe();
        var restarted = fixture.CreateService(ticketInventory: recoveredInventory, catalogCoordinator: coordinator,
            nextUnitNumerator: () => throw new Exception("Tier rerolled"),
            offerSelector: new ManifestCatalogSelector(() => throw new Exception("Packages rerolled")));
        await restarted.OpenAsync(fixture.Context, CaseId, CancellationToken.None, view.SnapshotId);
        var recovered = fixture.Store.Stored.ActiveManifest!;
        Assert.Equal(TestingCrateTypeCodec.PremiumTier(type) == ManifestOpeningTier.Legendary
            ? ManifestPhase.Entitlement : ManifestPhase.Offer1, recovered.FlowState.Phase);
        Assert.Equal(prepared.Ticket.OpeningQuality, recovered.Ticket.OpeningQuality);
        Assert.Equal(prepared.Offers.Select(offer => offer.Fingerprint), recovered.Offers.Select(offer => offer.Fingerprint));
        Assert.Equal(1, recoveredInventory.ApplyCalls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Epic_open_choose_restart_claim_is_exactly_once(int ordinal)
    {
        var fixture = Fixture.FromJournal(new CaseOpeningJournal());
        var lots = new[] { TemplateA, TemplateB, TemplateC }.Select((template, i) =>
            Lot("premium-" + i, "premium-" + i, "family-" + i, template, RewardRarity.BlackLabel, i).Resolved).ToArray();
        var coordinator = Coordinator(Catalog(lots));
        var inventory = new FreshTicketInventoryProbe { FreshCaseTemplate = ModConstants.CaseTemplateId };
        var tierDraws = 0;
        var offerDraws = 0;
        var service = fixture.CreateService(ticketInventory: inventory, catalogCoordinator: coordinator,
            nextUnitNumerator: () => { tierDraws++; return CanonicalRngEvidence.UnitDenominator / 100; },
            offerSelector: new ManifestCatalogSelector(() => { offerDraws++; return 0; }));
        await service.OpenAsync(fixture.Context, CaseId, CancellationToken.None);
        var active = fixture.Store.Stored.ActiveManifest!;
        Assert.Equal(ManifestOpeningTier.Epic, active.Ticket.OpeningQuality!.Tier);
        Assert.Equal(ManifestPhase.TicketPrepared, fixture.Store.Saved[0].ActiveManifest!.FlowState.Phase);
        Assert.Equal(active.Ticket.OpeningQuality, fixture.Store.Saved[0].ActiveManifest!.Ticket.OpeningQuality);
        Assert.Equal(1, inventory.ApplyCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecideOfferAsync(fixture.Context,
            active.ManifestId, 1, ManifestOfferDecision.Burn, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecideOfferAsync(fixture.Context,
            active.ManifestId, 1, ManifestOfferDecision.Lock, CancellationToken.None));
        await service.DecideOfferAsync(fixture.Context, active.ManifestId, 1, ManifestOfferDecision.Lock,
            CancellationToken.None, ordinal);
        var restarted = fixture.CreateService(catalogCoordinator: coordinator,
            nextUnitNumerator: () => throw new Exception("Must not reroll"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.DecideOfferAsync(fixture.Context,
            active.ManifestId, 1, ManifestOfferDecision.Lock, CancellationToken.None, ordinal));
        await restarted.ClaimAsync(fixture.Context, active.ManifestId, CancellationToken.None);
        var replay = await restarted.ClaimAsync(fixture.NewContextWithLostResponse(), active.ManifestId, CancellationToken.None);
        Assert.True(replay.Replay);
        Assert.Equal(1, fixture.Inventory.ApplyCalls);
        Assert.Equal(1, tierDraws);
        Assert.Equal(3, offerDraws);
        var receipt = Assert.Single(fixture.Store.Stored.ManifestReceipts);
        Assert.Equal(active.Offers[ordinal - 1].Fingerprint, receipt.Entitlement!.Fingerprint);
        Assert.Equal(ManifestOpeningTier.Epic, receipt.OpeningQuality!.Tier);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legendary_awards_first_draw_without_choice_and_Messenger_claim_is_exactly_once(bool forced)
    {
        var fixture = Fixture.FromJournal(new CaseOpeningJournal());
        var lots = new[] { TemplateA, TemplateB, TemplateC }.Select((template, i) =>
            Lot("premium-" + i, "premium-" + i, "family-" + i, template, RewardRarity.BlackLabel, i).Resolved).ToArray();
        var coordinator = Coordinator(Catalog(lots));
        var registry = new TestingForcedCrateRegistry();
        if (forced) registry.SetForcedCrate(CaseId, TestingCrateType.LegendaryMixed);
        var inventory = new FreshTicketInventoryProbe { FreshCaseTemplate = ModConstants.CaseTemplateId };
        await fixture.CreateService(ticketInventory: inventory, catalogCoordinator: coordinator,
            forcedCrateRegistry: registry, nextUnitNumerator: () => 0)
            .OpenAsync(fixture.Context, CaseId, CancellationToken.None);
        var active = fixture.Store.Stored.ActiveManifest!;
        Assert.True(active.Ticket.OpeningQuality!.SinglePrize);
        Assert.Equal(ManifestPhase.Entitlement, active.FlowState.Phase);
        Assert.Equal(active.Offers[0].Fingerprint, active.Entitlement!.Fingerprint);
        Assert.Equal(1, inventory.ApplyCalls);

        var profile = new SptProfile { CharacterData = new() { PmcData = fixture.Context.PmcData } };
        var delivery = new SptManifestRewardDelivery(fixture.Inventory, _ => profile,
            (_, _) => Task.CompletedTask, _ => { });
        var restarted = fixture.CreateService(catalogCoordinator: coordinator, claimInventory: delivery,
            nextUnitNumerator: () => throw new Exception("Must not reroll Legendary"));
        foreach (var ordinal in new[] { 1, 2, 3 })
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.DecideOfferAsync(fixture.Context,
                active.ManifestId, 1, ManifestOfferDecision.Lock, CancellationToken.None, ordinal));
        await restarted.ClaimAsync(fixture.Context, active.ManifestId, CancellationToken.None);
        var replay = await restarted.ClaimAsync(fixture.NewContextWithLostResponse(), active.ManifestId, CancellationToken.None);
        Assert.True(replay.Replay);
        Assert.Single(profile.DialogueRecords![SptManifestRewardDelivery.SenderId].Messages!);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        var receipt = Assert.Single(fixture.Store.Stored.ManifestReceipts);
        Assert.Equal(active.Offers[0].Fingerprint, receipt.Entitlement!.Fingerprint);
        Assert.Equal(1, Assert.Single(receipt.Decisions).Ordinal);
    }

    [Theory]
    [InlineData(3_000_000, 0)]
    [InlineData(5_000_000, 0)]
    [InlineData(5_000_000, 1)]
    [InlineData(5_000_000, 2)]
    [InlineData(5_000_000, 3)]
    [InlineData(5_000_000, 4)]
    [InlineData(5_000_000, -1)]
    public async Task Equipment_jackpot_cash_and_gear_survive_mail_recovery_and_partial_collection(int bonus, int failureSave)
    {
        TemplateItem? find(string id)
        {
            if (id is not (TemplateA or TemplateB or TemplateC)) return ContrabandCases.Tests.Server.JackpotBonusTests.FindTemplate(id);
            var template = ContrabandCases.Tests.Server.JackpotBonusTests.FindTemplate(ContrabandCases.Tests.Server.JackpotBonusTests.Equipment)!;
            template.Id = id;
            return template;
        }
        var definition = Assert.Single(ContrabandCases.Tests.Server.JackpotBonusTests.Pack(bonus).Lots);
        var lot = new CargoLotEvaluator(find, _ => 100_000).Evaluate(
            new CargoLotResolver(new CargoLotResolverDependencies(find, _ => null)).Resolve(definition));
        var ordinary = new[] { TemplateA, TemplateB, TemplateC }.Select((template, i) =>
            Lot("premium-" + i, "premium-" + i, "family-" + i, template, RewardRarity.BlackLabel, i).Resolved).ToArray();
        var coordinator = Coordinator(Catalog(ordinary.Append(lot)));
        var fixture = Fixture.FromJournal(new CaseOpeningJournal());
        var registry = new TestingForcedCrateRegistry();
        registry.SetForcedCrate(CaseId, TestingCrateType.LegendaryMixed);
        var ticketInventory = new FreshTicketInventoryProbe { FreshCaseTemplate = ModConstants.CaseTemplateId };
        var materializer = new CargoLotMaterializer(find);
        await fixture.CreateService(catalogCoordinator: coordinator, forcedCrateRegistry: registry,
            ticketInventory: ticketInventory, materializer: materializer,
            offerSelector: new ManifestCatalogSelector(() => CanonicalRngEvidence.UnitDenominator - 1))
            .OpenAsync(fixture.Context, CaseId, CancellationToken.None);
        var active = fixture.Store.Stored.ActiveManifest!;
        Assert.Equal(lot.Fingerprint, active.Entitlement!.Fingerprint);
        var profile = new SptProfile { CharacterData = new() { PmcData = fixture.Context.PmcData } };
        var delivery = new SptManifestRewardDelivery(fixture.Inventory, _ => profile, (_, _) => Task.CompletedTask, _ => { });
        var before = fixture.Context.PmcData.Inventory!.Items!.Count;
        var service = fixture.CreateService(catalogCoordinator: coordinator, materializer: materializer, claimInventory: delivery);
        fixture.Store.FailAfterSaveAttempt = failureSave <= 0 ? null : fixture.Store.SaveAttempts + failureSave;
        if (failureSave > 0)
            await Assert.ThrowsAsync<IOException>(() => service.ClaimAsync(fixture.Context, active.ManifestId, CancellationToken.None));
        fixture.Store.FailAfterSaveAttempt = null;
        if (failureSave == -1)
        {
            fixture.Committer.ExceptionAfterBoundary = new IOException("uncertain jackpot profile save");
            await Assert.ThrowsAsync<IOException>(() => service.ClaimAsync(fixture.Context, active.ManifestId, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ClaimAsync(fixture.Context, active.ManifestId, CancellationToken.None));
            fixture.Committer.ExceptionAfterBoundary = null;
        }
        var restartUncertainty = failureSave == -1 ? new ManifestClaimCommitUncertaintyCoordinator() : null;
        await fixture.CreateService(catalogCoordinator: coordinator, materializer: materializer, claimInventory: delivery,
            uncertaintyCoordinator: restartUncertainty)
            .ClaimAsync(fixture.NewContextWithLostResponse(), active.ManifestId, CancellationToken.None);
        Assert.Equal(before, fixture.Context.PmcData.Inventory.Items.Count);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        var messages = profile.DialogueRecords![SptManifestRewardDelivery.SenderId].Messages!;
        var items = messages.SelectMany(m => m.Items!.Data!).ToArray();
        Assert.Single(items, i => i.Template.ToString() == ContrabandCases.Tests.Server.JackpotBonusTests.Equipment);
        Assert.Equal(bonus, items.Where(i => i.Template.ToString() == CashPayouts.Roubles).Sum(i => i.Upd!.StackObjectsCount ?? 1));
        Assert.All(messages, m => Assert.InRange(m.Items!.Data!.Count, 1, 8));
        var ids = items.Select(i => i.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        messages[0].Items!.Data!.RemoveAt(0);
        var replay = await fixture.CreateService(catalogCoordinator: coordinator, claimInventory: delivery,
            uncertaintyCoordinator: restartUncertainty)
            .ClaimAsync(fixture.NewContextWithLostResponse(), active.ManifestId, CancellationToken.None);
        Assert.True(replay.Replay);
        Assert.Equal(ids.Length - 1, messages.Sum(m => m.Items!.Data!.Count));
        Assert.Single(fixture.Store.Stored.ManifestClaimGrants);
        Assert.Equal(1, ticketInventory.ApplyCalls);
    }

    private const string ManifestId = "aaaaaaaaaaaaaaaaaaaaaac1";
    private const string ItemRootTemplateId = "54009119af1c881c07000029";
    private const string TemplateA = "710000000000000000000001";
    private const string TemplateB = "710000000000000000000002";
    private const string TemplateC = "710000000000000000000003";
    private static readonly MongoId ProfileId = Id(1);
    private static readonly MongoId StashId = Id(2);
    private static readonly MongoId SortingTableId = Id(3);
    private static readonly MongoId CaseId = Id(4);
    private static readonly MongoId KeyId = Id(5);
    private static readonly MongoId BaselineId = Id(6);
    private static readonly MongoId RewardId = Id(100);
    private static readonly DateTimeOffset TicketPreparedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);
    private static readonly DateTimeOffset TicketCommittedAt = DateTimeOffset.UnixEpoch.AddMinutes(2);
    private static readonly DateTimeOffset DecisionAt = DateTimeOffset.UnixEpoch.AddMinutes(3);
    private static readonly DateTimeOffset ClaimPreparedAt = DateTimeOffset.UnixEpoch.AddMinutes(4);
    private static readonly DateTimeOffset CompletionAt = DateTimeOffset.UnixEpoch.AddMinutes(10);

    [Fact]
    public async Task Fifty_bitcoin_jackpot_is_mailed_in_small_batches_and_replay_never_reissues_collected_coins()
    {
        var cash = ContrabandCases.Tests.Server.CashCacheTests.Catalog();
        var coordinator = new CatalogSnapshotCoordinator(() => Catalog(), () => cash);
        coordinator.MarkStartupComplete();
        var fixture = Fixture.FromJournal(new CaseOpeningJournal(recoveryMeter: 2));
        fixture.Inventory.PrepareSucceeds = false;
        var profile = new SptProfile { CharacterData = new() { PmcData = fixture.Context.PmcData } };
        var delivery = new SptManifestRewardDelivery(fixture.Inventory, _ => profile,
            (_, _) => Task.CompletedTask, _ => throw new Exception("Unexpected notification failure"));
        var ticketInventory = new FreshTicketInventoryProbe { FreshCaseTemplate = CaseContracts.CashCache };
        var service = fixture.CreateService(ticketInventory: ticketInventory, catalogCoordinator: coordinator,
            claimInventory: delivery,
            offerSelector: new ManifestCatalogSelector(() =>
                (long)(95m * CanonicalRngEvidence.UnitDenominator / 10_000)),
            materializer: new CargoLotMaterializer(ContrabandCases.Tests.Server.CashCacheTests.FindTemplate));
        await service.OpenAsync(fixture.Context, CaseId, CancellationToken.None, cash.SnapshotId);
        var active = fixture.Store.Stored.ActiveManifest!;
        Assert.Equal(50, active.Entitlement!.Forest.Nodes.Sum(node => node.StackCount));
        var before = fixture.Context.PmcData.Inventory!.Items!.Count;
        await service.ClaimAsync(fixture.Context, active.ManifestId, CancellationToken.None);
        Assert.Equal(before, fixture.Context.PmcData.Inventory.Items.Count);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        var messages = profile.DialogueRecords![SptManifestRewardDelivery.SenderId].Messages!;
        Assert.Equal(new[] { 8, 8, 8, 8, 8, 8, 2 }, messages.Select(message => message.Items!.Data!.Count));
        var coins = messages.SelectMany(message => message.Items!.Data!).ToArray();
        Assert.Equal(50, coins.Select(item => item.Id).Distinct().Count());
        Assert.All(coins, item =>
        {
            Assert.Equal(CashPayouts.Bitcoin, item.Template.ToString());
            Assert.Equal(1, item.Upd!.StackObjectsCount);
        });
        messages[0].Items!.Data!.RemoveAt(0); // Partial native collection before a lost-response retry.
        var replay = await fixture.CreateService(catalogCoordinator: coordinator, claimInventory: delivery)
            .ClaimAsync(fixture.NewContextWithLostResponse(), active.ManifestId, CancellationToken.None);
        Assert.True(replay.Replay);
        Assert.Equal(49, messages.Sum(message => message.Items!.Data!.Count));
        Assert.Single(fixture.Store.Stored.ManifestClaimGrants);
        Assert.Equal(1, ticketInventory.ApplyCalls);
        Assert.Equal(2, fixture.Store.Stored.BrokerFavor);
    }

    [Theory]
    [InlineData("unchanged", 0, "btc-1")]
    [InlineData("new-quotes", 0, "btc-1")]
    [InlineData("missing-quotes", 0, "btc-1")]
    [InlineData("bitcoin-budget", 0, "btc-1")]
    [InlineData("unchanged", 95, "btc-2")]
    [InlineData("new-quotes", 95, "btc-2")]
    [InlineData("missing-quotes", 95, "btc-2")]
    [InlineData("bitcoin-budget", 95, "btc-2")]
    [InlineData("unchanged", 700, "gp-10")]
    [InlineData("new-quotes", 700, "gp-10")]
    [InlineData("missing-quotes", 700, "gp-10")]
    [InlineData("bitcoin-budget", 700, "gp-10")]
    [InlineData("unchanged", 950, "gp-25")]
    [InlineData("new-quotes", 950, "gp-25")]
    [InlineData("missing-quotes", 950, "gp-25")]
    [InlineData("bitcoin-budget", 950, "gp-25")]
    public async Task Cash_open_full_stash_claim_retry_and_replay_preserve_one_payout_and_Favor(
        string quoteState, int drawBasisPoints, string expectedPayout)
    {
        expectedPayout = expectedPayout == "btc-2" ? "btc-50.jackpot-v1" : expectedPayout + ".shipment-v1";
        var cash = ContrabandCases.Tests.Server.CashCacheTests.Catalog();
        var coordinator = new CatalogSnapshotCoordinator(() => Catalog(), () => cash);
        coordinator.MarkStartupComplete();
        var fixture = Fixture.FromJournal(new CaseOpeningJournal(recoveryMeter: 2));
        var ticketInventory = new FreshTicketInventoryProbe { FreshCaseTemplate = CaseContracts.CashCache };
        var draws = 0;
        var service = fixture.CreateService(ticketInventory: ticketInventory, catalogCoordinator: coordinator,
            offerSelector: new ManifestCatalogSelector(() =>
            {
                draws++;
                return (long)(drawBasisPoints * (decimal)CanonicalRngEvidence.UnitDenominator / 10_000);
            }),
            materializer: new CargoLotMaterializer(ContrabandCases.Tests.Server.CashCacheTests.FindTemplate));
        await service.OpenAsync(fixture.Context, CaseId, CancellationToken.None, cash.SnapshotId);
        var active = fixture.Store.Stored.ActiveManifest!;
        Assert.Equal(ManifestPhase.Entitlement, active.FlowState.Phase);
        Assert.Single(active.Offers);
        Assert.Equal(expectedPayout, active.Entitlement!.Identity.LotId);
        Assert.Equal(1, draws);
        Assert.Equal(1, ticketInventory.ApplyCalls);
        var fingerprint = active.Entitlement!.Fingerprint;
        var relayInventory = new MustNotRunRelayKeyInventory();
        var relayService = fixture.CreateService(relayKeyInventory: relayInventory, catalogCoordinator: coordinator);
        await Assert.ThrowsAsync<InvalidOperationException>(() => relayService.RelayAsync(
            fixture.Context, active.ManifestId, ManifestPhase.Entitlement, 1, CancellationToken.None));
        fixture.Inventory.PrepareSucceeds = false;
        var noSpace = await service.ClaimAsync(fixture.Context, active.ManifestId, CancellationToken.None);
        Assert.Equal(ManifestClaimResultKind.NoSpace, noSpace.Kind);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(fingerprint, fixture.Store.Stored.ActiveManifest!.Entitlement!.Fingerprint);
        var currentCash = quoteState switch
        {
            "new-quotes" => CashPayoutCatalog.Build(ContrabandCases.Tests.Server.CashCacheTests.FindTemplate, 160, 190, 900_000, 7_500),
            "missing-quotes" => CashPayoutCatalog.Disabled("Missing FX offers", ContrabandCases.Tests.Server.CashCacheTests.FindTemplate),
            "bitcoin-budget" => CatalogSnapshotCoordinator.FreezeCash(
                () => ContrabandCases.Tests.Server.CashCacheTests.Catalog(100_000_000),
                ContrabandCases.Tests.Server.CashCacheTests.FindTemplate, _ => { }),
            _ => cash
        };
        var restartedCoordinator = new CatalogSnapshotCoordinator(() => Catalog(), () => currentCash);
        restartedCoordinator.MarkStartupComplete();
        service = fixture.CreateService(catalogCoordinator: restartedCoordinator,
            materializer: new CargoLotMaterializer(ContrabandCases.Tests.Server.CashCacheTests.FindTemplate));
        var recoveredSnapshot = ManifestSnapshotProjection.FromActive(active, currentCash, null);
        Assert.True(recoveredSnapshot.AvailableActions.CanClaim);
        Assert.False(recoveredSnapshot.MissingContentBlocked);
        fixture.Inventory.PrepareSucceeds = true;
        await service.ClaimAsync(fixture.Context, active.ManifestId, CancellationToken.None);
        Assert.Equal(1, fixture.Inventory.ApplyCalls);
        var expectedLot = cash.Lots.Single(lot => lot.Identity.LotId == expectedPayout);
        Assert.Equal(expectedLot.Forest.Nodes.Sum(node => node.StackCount),
            fixture.Inventory.PreparedMaterializedItems!.Sum(item => item.Upd!.StackObjectsCount));
        Assert.All(fixture.Inventory.PreparedMaterializedItems!,
            item => Assert.Equal(expectedLot.Identity.AnchorTemplateId, item.Template.ToString()));
        Assert.Equal(2, fixture.Store.Stored.BrokerFavor);
        Assert.Null(fixture.Store.Stored.ActiveManifest);
        var replay = await service.ClaimAsync(fixture.NewContextWithLostResponse(), active.ManifestId, CancellationToken.None);
        Assert.True(replay.Replay);
        Assert.Equal(1, fixture.Inventory.ApplyCalls);
        Assert.Equal(1, draws);
        Assert.Equal(fingerprint, Assert.Single(fixture.Store.Stored.ManifestReceipts).Entitlement!.Fingerprint);
    }

    [Fact]
    public async Task Cash_ticket_recovery_activates_original_payout_without_catalog_or_new_draw()
    {
        var prepared = ContrabandCases.Tests.Server.CashCacheTests.Prepare(
            ContrabandCases.Tests.Server.CashCacheTests.Catalog()).BeginTicketProfileCommit();
        var fixture = Fixture.FromJournal(new CaseOpeningJournal(recoveryMeter: 2, activeManifest: prepared), materializerMustNotRun: true);
        var inventory = new PreparedTicketRecoveryInventory();
        var coordinator = UnavailableCatalogCoordinator();
        var service = fixture.CreateService(ticketInventory: inventory, catalogCoordinator: coordinator,
            offerSelector: new ManifestCatalogSelector(() => throw new Xunit.Sdk.XunitException("Cash recovery rerolled.")));
        await service.OpenAsync(fixture.Context, prepared.Ticket.CaseId, CancellationToken.None);
        var recovered = fixture.Store.Stored.ActiveManifest!;
        Assert.Equal(ManifestPhase.Entitlement, recovered.FlowState.Phase);
        Assert.Equal(prepared.Offers[0].Fingerprint, recovered.Entitlement!.Fingerprint);
        Assert.Equal(1, inventory.ReplayCalls);
        Assert.False(coordinator.IsFrozen);
        Assert.Equal(2, fixture.Store.Stored.BrokerFavor);
    }

    [Fact]
    public async Task Historical_component_package_remains_claimable_after_fresh_opening_retirement()
    {
        var retired = Lot("eco-attachment.elite-optics", "micro-red-dot-mounts", "family-a",
            TemplateA, RewardRarity.ScavGrade, 1).Resolved;
        var lots = new[] { retired, BaseCatalogLots()[1], BaseCatalogLots()[2] };
        var offers = lots.Select((lot, i) => new ManifestOfferSnapshot(i + 1, lot.Evaluation.Grade,
            lot.Identity, lot.Forest, lot.Fingerprint,
            new CanonicalRngEvidence(ManifestRngPurpose.OfferSelection, i + 1, i + 1))).ToArray();
        var active = new ManifestRecord(ManifestId, "historical-component-catalog",
            ManifestCommitmentEvidence.CreateWithRandomNonce(ManifestId, "historical-component-catalog", offers),
            ManifestFlowState.PrepareTicket(),
            new ManifestTicketPayload(CaseId, KeyId, TicketPreparedAt, false, false, null)
                .WithCommitPlan(1, ManifestInputCommitWitness.GenesisHash),
            offers, null, null, null, null, 0)
            .BeginTicketProfileCommit().ActivateTicket(TicketCommittedAt)
            .DecideOffer(ManifestOfferDecision.Lock, DecisionAt, []);
        var journalJson = System.Text.Json.JsonSerializer.Serialize(
            SptCaseJournal.ToDocument(new CaseOpeningJournal(activeManifest: active)));
        var journal = SptCaseJournal.FromDocument(System.Text.Json.JsonSerializer
            .Deserialize<SptCaseJournalDocument>(journalJson)!);
        var fixture = Fixture.FromJournal(journal);
        var current = Catalog(lots);
        Assert.False(current.OpeningEnabled);
        Assert.DoesNotContain(retired, current.FreshOpeningLots);
        var service = fixture.CreateService(catalogCoordinator: Coordinator(current));

        await service.ClaimAsync(fixture.Context, ManifestId, CancellationToken.None);

        var receipt = Assert.Single(fixture.Store.Stored.ManifestReceipts);
        Assert.Equal(ManifestPhase.Granted, receipt.TerminalPhase);
        Assert.Equal(retired.Fingerprint, receipt.Entitlement!.Fingerprint);
        Assert.Equal(retired.Identity.PackVersion, receipt.Entitlement.Identity.PackVersion);
        Assert.Null(fixture.Store.Stored.ActiveManifest);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Old_frozen_relay_upgrade_can_continue_through_exclusions_only_after_catalog_change(
        bool catalogChanged, bool retiredContinuation)
    {
        var stake = Lot("a", "stake", "family-a", TemplateA, RewardRarity.Contractor, 1, trackId: "shared").Resolved;
        var replacement = Lot("b", "replacement", "family-b", TemplateB, RewardRarity.Contractor, 2, trackId: "shared").Resolved;
        var upgrade = Lot("c", "upgrade", "family-c", TemplateC, RewardRarity.Restricted, 3, trackId: "shared").Resolved;
        var upgradePeer = Lot("d", "upgrade-peer", "family-c", TemplateB, RewardRarity.Restricted, 4, trackId: "shared").Resolved;
        var chase = retiredContinuation
            ? Lot("eco-attachment.elite-optics", "micro-red-dot-mounts", "family-c", TemplateC,
                RewardRarity.BlackLabel, 5, trackId: "shared").Resolved
            : Lot("core", "black-site-marksman", "family-c", TemplateC,
                RewardRarity.BlackLabel, 5, trackId: "shared").Resolved;
        var current = Catalog([stake, replacement, upgrade, upgradePeer, chase]);
        var snapshotId = catalogChanged ? "old-catalog" : current.SnapshotId;
        var offers = new[] { stake, replacement, upgrade }.Select((lot, i) => new ManifestOfferSnapshot(i + 1,
            lot.Evaluation.Grade, lot.Identity, lot.Forest, lot.Fingerprint,
            new CanonicalRngEvidence(ManifestRngPurpose.OfferSelection, i + 1, 0))).ToArray();
        var oldCandidates = new ManifestCatalogSelector().CreateRelayCandidatesForStage(current,
            Entitlement(offers[0]), 1, allowOpeningChases: true);
        Assert.NotEmpty(oldCandidates);
        var active = new ManifestRecord(ManifestId, snapshotId,
            ManifestCommitmentEvidence.CreateWithRandomNonce(ManifestId, snapshotId, offers),
            ManifestFlowState.PrepareTicket(),
            new ManifestTicketPayload(CaseId, KeyId, TicketPreparedAt, false, false, null),
            offers, null, null, null, null, 0)
            .BeginTicketProfileCommit().ActivateTicket(TicketCommittedAt)
            .DecideOffer(ManifestOfferDecision.Lock, DecisionAt, oldCandidates);
        var fixture = Fixture.FromJournal(new CaseOpeningJournal(activeManifest: active));
        var inventory = new FreshRelayKeyInventoryProbe();
        var service = fixture.CreateService(relayKeyInventory: inventory,
            catalogCoordinator: Coordinator(current), nextUnitNumerator: () => 0);
        var run = () => service.RelayAsync(fixture.Context, ManifestId, ManifestPhase.Entitlement, 1, CancellationToken.None);
        if (!catalogChanged)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(run);
            Assert.Equal(0, inventory.ApplyCalls);
            Assert.Equal(0, fixture.Store.SaveAttempts);
            return;
        }
        await run();
        var completed = fixture.Store.Stored.ActiveManifest!;
        Assert.Equal(upgrade.Fingerprint, completed.Entitlement!.Fingerprint);
        Assert.Equal(2, completed.FlowState.RelayStage);
        Assert.Contains(completed.RelayCandidates, candidate => candidate.Fingerprint.Equals(chase.Fingerprint));
        Assert.Equal(1, inventory.ApplyCalls);
    }

    [Fact]
    public async Task Claim_rejects_outside_lobby_before_loading_or_materializing()
    {
        var fixture = Fixture.Entitlement(enterLobby: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ClaimAsync());

        Assert.Equal(0, fixture.Store.LoadCalls);
        Assert.Equal(0, fixture.Materializer.FindCalls);
        Assert.Equal(0, fixture.Inventory.PrepareCalls);
    }

    [Fact]
    public async Task Claim_rejects_unknown_manifest_before_materialization()
    {
        var fixture = Fixture.Entitlement();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ClaimAsync(fixture.Context, "other-manifest", CancellationToken.None));

        Assert.Equal(0, fixture.Materializer.FindCalls);
        Assert.Equal(0, fixture.Inventory.PrepareCalls);
        Assert.Equal(0, fixture.Store.SaveAttempts);
    }

    [Fact]
    public async Task Claim_rejects_a_nonclaimable_manifest_phase_before_materialization()
    {
        var fixture = Fixture.FromJournal(
            new CaseOpeningJournal(activeManifest: OfferTwoManifest()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ClaimAsync());

        Assert.Equal(0, fixture.Materializer.FindCalls);
        Assert.Equal(0, fixture.Inventory.PrepareCalls);
        Assert.Equal(0, fixture.Store.SaveAttempts);
    }

    [Fact]
    public async Task Claim_rejects_competing_legacy_preparation_before_materialization()
    {
        var active = EntitlementManifest();
        var legacy = new CaseOpeningRecord(
            Id(20),
            Id(21),
            "legacy-reward",
            [],
            TicketPreparedAt,
            OpeningRecordStatus.Prepared,
            committedAtUtc: null);
        var fixture = Fixture.FromJournal(
            new CaseOpeningJournal([legacy], activeManifest: active));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ClaimAsync());

        Assert.Equal(0, fixture.Materializer.FindCalls);
        Assert.Equal(0, fixture.Inventory.PrepareCalls);
        Assert.Equal(0, fixture.Store.SaveAttempts);
    }

    [Fact]
    public async Task Fresh_ticket_rejects_current_offer_catalog_mismatch_before_consuming_case_and_key()
    {
        var mutated = BaseCatalogLots();
        mutated[0] = Lot(
            "provider-a",
            "lot-a",
            "family-a",
            TemplateA,
            RewardRarity.ScavGrade,
            1,
            weight: 2d).Resolved;
        var fixture = Fixture.FromJournal(
            new CaseOpeningJournal(activeManifest: TicketPreparedManifest(profileCommitStarted: false)));
        var inventory = new FreshTicketInventoryProbe();
        var service = fixture.CreateService(
            ticketInventory: inventory,
            catalogCoordinator: Coordinator(Catalog(mutated, hashCharacter: 'd')));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.OpenAsync(fixture.Context, CaseId, CancellationToken.None));

        Assert.Contains("prepared Manifest offer", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, inventory.ApplyCalls);
        Assert.Equal(1, inventory.RestoreCalls);
        Assert.Equal(0, fixture.Store.SaveAttempts);
        Assert.Equal(ManifestPhase.TicketPrepared, fixture.Store.Stored.ActiveManifest!.FlowState.Phase);
    }

    [Fact]
    public async Task Burn_remains_available_after_unrelated_catalog_and_provider_weight_drift()
    {
        var fixture = Fixture.FromJournal(
            new CaseOpeningJournal(activeManifest: OfferOneManifest()));
        var service = fixture.CreateService(
            catalogCoordinator: Coordinator(CatalogWithUnrelatedDrift()));

        await service.DecideOfferAsync(
            fixture.Context,
            ManifestId,
            expectedOrdinal: 1,
            ManifestOfferDecision.Burn,
            CancellationToken.None);

        Assert.Equal(ManifestPhase.Offer2, fixture.Store.Stored.ActiveManifest!.FlowState.Phase);
        Assert.Equal(ManifestOfferDecision.Burn, Assert.Single(fixture.Store.Stored.ActiveManifest.Decisions).Decision);
    }

    [Fact]
    public async Task Claim_remains_available_after_unrelated_catalog_and_provider_weight_drift()
    {
        var fixture = Fixture.Entitlement();
        var service = fixture.CreateService(
            catalogCoordinator: Coordinator(CatalogWithUnrelatedDrift()));

        var result = await service.ClaimAsync(
            fixture.Context,
            ManifestId,
            CancellationToken.None);

        Assert.Equal(ManifestClaimResultKind.Granted, result.Kind);
        Assert.Equal(1, fixture.Inventory.ApplyCalls);
        Assert.Null(fixture.Store.Stored.ActiveManifest);
    }

    [Fact]
    public async Task Relay_remains_available_after_unrelated_catalog_and_provider_weight_drift()
    {
        var relayLots = RelayLots();
        var active = EntitlementManifest(RelayCandidates(relayLots));
        var fixture = Fixture.FromJournal(new CaseOpeningJournal(activeManifest: active));
        var inventory = new FreshRelayKeyInventoryProbe();
        var service = fixture.CreateService(
            relayKeyInventory: inventory,
            catalogCoordinator: Coordinator(CatalogWithUnrelatedDrift(
                relayLots.Select(lot => lot.Resolved))),
            nextUnitNumerator: () => CanonicalRngEvidence.UnitDenominator - 1);

        await service.RelayAsync(
            fixture.Context,
            ManifestId,
            ManifestPhase.Entitlement,
            expectedRelayStage: 1,
            CancellationToken.None);

        Assert.Equal(1, inventory.PrepareCalls);
        Assert.Equal(1, inventory.ApplyCalls);
        Assert.Equal(1, fixture.Committer.Calls);
        Assert.Null(fixture.Store.Stored.ActiveManifest);
        Assert.Equal(
            ManifestPhase.Confiscated,
            Assert.Single(fixture.Store.Stored.ManifestReceipts).TerminalPhase);
    }

    [Fact]
    public async Task Missing_next_offer_disables_burn_but_preserves_lock()
    {
        var active = OfferOneManifest();
        var currentOnly = Catalog(
            [Lot("provider-a", "lot-a", "family-a", TemplateA, RewardRarity.ScavGrade, 1).Resolved],
            hashCharacter: 'd');
        var snapshot = ManifestSnapshotProjection.FromActive(active, currentOnly, locale: null);
        var fixture = Fixture.FromJournal(new CaseOpeningJournal(activeManifest: active));
        var service = fixture.CreateService(catalogCoordinator: Coordinator(currentOnly));

        Assert.True(snapshot.AvailableActions.CanLock);
        Assert.False(snapshot.AvailableActions.CanBurn);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecideOfferAsync(
            fixture.Context,
            ManifestId,
            expectedOrdinal: 1,
            ManifestOfferDecision.Burn,
            CancellationToken.None));

        await service.DecideOfferAsync(
            fixture.Context,
            ManifestId,
            expectedOrdinal: 1,
            ManifestOfferDecision.Lock,
            CancellationToken.None);

        Assert.Equal(ManifestPhase.Entitlement, fixture.Store.Stored.ActiveManifest!.FlowState.Phase);
        Assert.Empty(fixture.Store.Stored.ActiveManifest.RelayCandidates);
    }

    [Fact]
    public async Task Mutated_current_offer_blocks_decisions_and_enables_forfeit()
    {
        var active = OfferOneManifest();
        var mutated = BaseCatalogLots();
        mutated[0] = Lot(
            "provider-a",
            "lot-a",
            "family-a",
            TemplateA,
            RewardRarity.ScavGrade,
            1,
            weight: 2d).Resolved;
        var currentCatalog = Catalog(mutated, hashCharacter: 'e');
        var snapshot = ManifestSnapshotProjection.FromActive(active, currentCatalog, locale: null);
        var fixture = Fixture.FromJournal(new CaseOpeningJournal(activeManifest: active));
        var service = fixture.CreateService(catalogCoordinator: Coordinator(currentCatalog));

        Assert.True(snapshot.MissingContentBlocked);
        Assert.False(snapshot.AvailableActions.CanLock);
        Assert.False(snapshot.AvailableActions.CanBurn);
        Assert.True(snapshot.AvailableActions.CanForfeit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecideOfferAsync(
            fixture.Context,
            ManifestId,
            expectedOrdinal: 1,
            ManifestOfferDecision.Lock,
            CancellationToken.None));

        await service.ForfeitMissingContentAsync(
            fixture.Context,
            ManifestId,
            ManifestPhase.Offer1,
            CancellationToken.None);

        Assert.Null(fixture.Store.Stored.ActiveManifest);
        Assert.Equal(ManifestPhase.Forfeited, Assert.Single(fixture.Store.Stored.ManifestReceipts).TerminalPhase);
    }

    [Theory]
    [InlineData(EntitlementCatalogMismatch.Missing)]
    [InlineData(EntitlementCatalogMismatch.Mutated)]
    public async Task Missing_or_mutated_entitlement_blocks_claim_before_materialization(
        EntitlementCatalogMismatch mismatch)
    {
        var currentLots = BaseCatalogLots().ToList();
        if (mismatch == EntitlementCatalogMismatch.Missing)
        {
            currentLots.RemoveAt(0);
        }
        else
        {
            currentLots[0] = Lot(
                "provider-a",
                "lot-a",
                "family-a",
                TemplateA,
                RewardRarity.ScavGrade,
                1,
                weight: 2d).Resolved;
        }
        var fixture = Fixture.Entitlement();
        var service = fixture.CreateService(
            catalogCoordinator: Coordinator(Catalog(currentLots, hashCharacter: 'e')));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ClaimAsync(fixture.Context, ManifestId, CancellationToken.None));

        Assert.Contains("Claim entitlement", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Materializer.FindCalls);
        Assert.Equal(0, fixture.Materializer.IdCalls);
        Assert.Equal(0, fixture.Inventory.PrepareCalls);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(0, fixture.Store.SaveAttempts);
        Assert.Equal([BaselineId], fixture.Context.PmcData.Inventory!.Items!.Select(item => item.Id));
    }

    [Fact]
    public async Task Missing_frozen_relay_candidate_disables_relay_but_preserves_claim()
    {
        var active = EntitlementManifest(RelayCandidates());
        var catalog = Catalog();
        var snapshot = ManifestSnapshotProjection.FromActive(active, catalog, locale: null);
        var fixture = Fixture.FromJournal(new CaseOpeningJournal(activeManifest: active));
        var relayInventory = new MustNotRunRelayKeyInventory();
        var service = fixture.CreateService(
            relayKeyInventory: relayInventory,
            catalogCoordinator: Coordinator(catalog));

        Assert.True(snapshot.AvailableActions.CanClaim);
        Assert.False(snapshot.AvailableActions.CanRelay);
        Assert.Equal(
            "Frozen Relay content is unavailable; Claim remains safe.",
            snapshot.Relay!.TerminalReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RelayAsync(
            fixture.Context,
            ManifestId,
            ManifestPhase.Entitlement,
            expectedRelayStage: 1,
            CancellationToken.None));
        Assert.Equal(0, relayInventory.CallCount);

        var result = await service.ClaimAsync(fixture.Context, ManifestId, CancellationToken.None);

        Assert.Equal(ManifestClaimResultKind.Granted, result.Kind);
    }

    [Fact]
    public async Task Materialization_avoids_profile_response_and_ticket_ids()
    {
        var responseCollision = Id(7);
        var fixture = Fixture.Entitlement(
            generatedIds: [BaselineId, CaseId, KeyId, responseCollision, RewardId]);
        fixture.Inventory.PrepareSucceeds = false;
        var changes = SptResponseChanges.GetOrCreate(fixture.Context.Response, ProfileId);
        changes.NewItems!.Add(new Item { Id = responseCollision, Template = TemplateB });

        var result = await fixture.ClaimAsync();

        Assert.Equal(ManifestClaimResultKind.NoSpace, result.Kind);
        Assert.Equal(RewardId, Assert.Single(fixture.Inventory.PreparedMaterializedItems!).Id);
        Assert.Equal(5, fixture.Materializer.IdCalls);
        Assert.Same(fixture.Store.Initial.ActiveManifest, fixture.Store.Stored.ActiveManifest);
    }

    [Fact]
    public async Task No_space_leaves_the_entitlement_and_response_unchanged()
    {
        var fixture = Fixture.Entitlement();
        fixture.Inventory.PrepareSucceeds = false;

        var result = await fixture.ClaimAsync();

        Assert.Equal(ManifestClaimResultKind.NoSpace, result.Kind);
        Assert.False(result.Replay);
        Assert.Equal(ManifestPhase.Entitlement, fixture.Store.Stored.ActiveManifest!.FlowState.Phase);
        Assert.Equal(0, fixture.Store.SaveAttempts);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Null(fixture.Context.Response.ProfileChanges);
    }

    [Fact]
    public async Task Preflight_failure_leaves_the_entitlement_and_profile_unchanged()
    {
        var fixture = Fixture.Entitlement();
        fixture.Inventory.PrepareException = new IOException("preflight failed");

        await Assert.ThrowsAsync<IOException>(() => fixture.ClaimAsync());

        Assert.Equal(ManifestPhase.Entitlement, fixture.Store.Stored.ActiveManifest!.FlowState.Phase);
        Assert.Equal(0, fixture.Store.SaveAttempts);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal([BaselineId], fixture.Context.PmcData.Inventory!.Items!.Select(item => item.Id));
    }

    [Fact]
    public async Task Fresh_claim_persists_each_boundary_then_atomically_finishes_with_exact_grant()
    {
        using var callerCancellation = new CancellationTokenSource();
        var fixture = Fixture.Entitlement();

        var result = await fixture.ClaimAsync(callerCancellation.Token);

        Assert.Equal(ManifestClaimResultKind.Granted, result.Kind);
        Assert.False(result.Replay);
        Assert.Equal(1, fixture.Inventory.PrepareCalls);
        Assert.Equal(1, fixture.Inventory.ApplyCalls);
        Assert.Equal(1, fixture.Committer.Calls);
        Assert.Equal(
            [ManifestPhase.ClaimPrepared, ManifestPhase.ClaimPrepared, ManifestPhase.RewardOwed, null],
            fixture.Store.Saved.Select(snapshot => snapshot.ActiveManifest?.FlowState.Phase));
        Assert.False(fixture.Store.Saved[0].ActiveManifest!.ClaimPrepared!.ProfileCommitStarted);
        Assert.True(fixture.Store.Saved[1].ActiveManifest!.ClaimPrepared!.ProfileCommitStarted);
        Assert.True(fixture.Store.Saved[2].ActiveManifest!.ClaimPrepared!.ProfileCommitStarted);
        Assert.Single(fixture.Store.Saved[3].ManifestReceipts);
        var grant = Assert.IsType<ManifestClaimGrantRecord>(
            fixture.Store.Saved[3].FindManifestClaimGrant(ManifestId));
        Assert.Equal([RewardId], grant.ClaimPayload.ExactItemIds);
        Assert.Equal(2, Assert.IsType<ItemLocation>(grant.ClaimPayload.Items[0].Location).X);
        Assert.Equal([true, false, false, false],
            fixture.Store.SaveTokens.Select(token => token.CanBeCanceled));
        Assert.False(Assert.Single(fixture.Committer.Tokens).CanBeCanceled);
        Assert.Equal(
             ManifestClaimCommitWitnessInspection.Current,
            ManifestClaimCommitWitness.Inspect(
                fixture.Context.PmcData,
                 ProfileId,
                 ManifestId,
                 grant.Entitlement.Fingerprint,
                 grant.ClaimPayload));
    }

    [Theory]
    [InlineData(1, ManifestPhase.ClaimPrepared, false, 1)]
    [InlineData(2, ManifestPhase.ClaimPrepared, false, 2)]
    [InlineData(3, ManifestPhase.RewardOwed, false, 2)]
    [InlineData(4, null, true, 1)]
    public async Task Failure_after_any_durable_journal_save_has_a_safe_retry(
        int failedSave,
        ManifestPhase? durablePhase,
        bool durableGrant,
        int expectedApplyCalls)
    {
        var fixture = Fixture.Entitlement();
        fixture.Store.FailAfterSaveAttempt = failedSave;

        await Assert.ThrowsAsync<IOException>(() => fixture.ClaimAsync());

        Assert.Equal(durablePhase, fixture.Store.Stored.ActiveManifest?.FlowState.Phase);
        Assert.Equal(durableGrant, fixture.Store.Stored.FindManifestClaimGrant(ManifestId) is not null);
        Assert.Equal(failedSave == 4 ? RewardPresence.Complete : RewardPresence.Absent, fixture.Inventory.Presence);
        Assert.Equal(failedSave is 2 or 3 ? 1 : 0, fixture.Inventory.RestoreCalls);
        if (failedSave < 4)
        {
            Assert.Equal([BaselineId], fixture.Context.PmcData.Inventory!.Items!.Select(item => item.Id));
            Assert.Equal(
                 ManifestClaimCommitWitnessInspection.Predecessor,
                 InspectWitness(
                     fixture.Context.PmcData,
                     Assert.IsType<ManifestRecord>(fixture.Store.Stored.ActiveManifest)));
        }

        var retryContext = failedSave == 4
            ? fixture.NewContextWithLostResponse()
            : fixture.Context;
        var retry = await fixture.Service.ClaimAsync(
            retryContext,
            ManifestId,
            CancellationToken.None);

        Assert.Equal(ManifestClaimResultKind.Granted, retry.Kind);
        Assert.Equal(failedSave == 4, retry.Replay);
        Assert.Equal(expectedApplyCalls, fixture.Inventory.ApplyCalls);
        Assert.NotNull(fixture.Store.Stored.FindManifestClaimGrant(ManifestId));
        Assert.Null(fixture.Store.Stored.ActiveManifest);
    }

    [Fact]
    public async Task Apply_failure_rolls_back_profile_response_and_witness_then_retries_exact_payload()
    {
        var fixture = Fixture.Entitlement();
        fixture.Inventory.ApplyExceptionAfterMutation = new IOException("apply failed");

        await Assert.ThrowsAsync<IOException>(() => fixture.ClaimAsync());

        Assert.Equal(RewardPresence.Absent, fixture.Inventory.Presence);
        Assert.Equal(1, fixture.Inventory.RestoreCalls);
        Assert.Equal([BaselineId], fixture.Context.PmcData.Inventory!.Items!.Select(item => item.Id));
        Assert.Null(fixture.Context.Response.ProfileChanges);
        Assert.Equal(ManifestPhase.ClaimPrepared, fixture.Store.Stored.ActiveManifest!.FlowState.Phase);
        Assert.False(fixture.Store.Stored.ActiveManifest.ClaimPrepared!.ProfileCommitStarted);

        fixture.Inventory.ApplyExceptionAfterMutation = null;
        var retry = await fixture.ClaimAsync();

        Assert.Equal(ManifestClaimResultKind.Granted, retry.Kind);
        Assert.Equal(2, fixture.Inventory.ApplyCalls);
        Assert.Equal([RewardId],
            fixture.Store.Stored.FindManifestClaimGrant(ManifestId)!.ClaimPayload.ExactItemIds);
    }

    [Fact]
    public async Task Profile_commit_failure_never_rolls_back_and_requires_process_restart_recovery()
    {
        var fixture = Fixture.Entitlement();
        fixture.Committer.ExceptionAfterBoundary = new IOException("commit result uncertain");

        await Assert.ThrowsAsync<IOException>(() => fixture.ClaimAsync());

        Assert.Equal(RewardPresence.Complete, fixture.Inventory.Presence);
        Assert.Equal(0, fixture.Inventory.RestoreCalls);
        Assert.Equal(ManifestPhase.RewardOwed, fixture.Store.Stored.ActiveManifest!.FlowState.Phase);
        Assert.Contains(fixture.Context.PmcData.Inventory!.Items!, item => item.Id == RewardId);
        Assert.Equal(
            ManifestClaimCommitWitnessInspection.Current,
            InspectWitness(
                fixture.Context.PmcData,
                Assert.IsType<ManifestRecord>(fixture.Store.Stored.ActiveManifest)));
        var loadCalls = fixture.Store.LoadCalls;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ClaimAsync());
        Assert.Equal(loadCalls, fixture.Store.LoadCalls);

        fixture.Committer.ExceptionAfterBoundary = null;
        var reconstructedService = fixture.CreateService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => reconstructedService.ClaimAsync(
            fixture.NewContextWithLostResponse(),
            ManifestId,
            CancellationToken.None));
        Assert.Equal(loadCalls, fixture.Store.LoadCalls);

        var restartedService = fixture.CreateService(new ManifestClaimCommitUncertaintyCoordinator());
        var retry = await restartedService.ClaimAsync(
            fixture.NewContextWithLostResponse(),
            ManifestId,
            CancellationToken.None);

        Assert.Equal(ManifestClaimResultKind.Granted, retry.Kind);
        Assert.False(retry.Replay);
        Assert.Equal(1, fixture.Inventory.ApplyCalls);
        Assert.Equal(1, fixture.Committer.Calls);
        Assert.NotNull(fixture.Store.Stored.FindManifestClaimGrant(ManifestId));
    }

    [Fact]
    public async Task Failure_before_terminal_save_finishes_without_regranting_or_recommitting()
    {
        var fixture = Fixture.Entitlement();
        fixture.Store.FailBeforeSaveAttempt = 4;

        await Assert.ThrowsAsync<IOException>(() => fixture.ClaimAsync());

        Assert.Equal(ManifestPhase.RewardOwed, fixture.Store.Stored.ActiveManifest!.FlowState.Phase);
        Assert.Equal(RewardPresence.Complete, fixture.Inventory.Presence);
        Assert.Equal(0, fixture.Inventory.RestoreCalls);
        var retry = await fixture.ClaimAsync(fixture.NewContextWithLostResponse());

        Assert.Equal(ManifestClaimResultKind.Granted, retry.Kind);
        Assert.False(retry.Replay);
        Assert.Equal(1, fixture.Inventory.ApplyCalls);
        Assert.Equal(1, fixture.Committer.Calls);
    }

    [Theory]
    [MemberData(nameof(WitnessCases))]
    public async Task Witness_and_inventory_matrix_fails_closed_or_recovers_exactly(
        ClaimStage stage,
        MarkerState marker,
        RewardPresence presence,
        bool succeeds,
        bool reapplies)
    {
        var fixture = Fixture.ActiveClaim(stage);
        fixture.Inventory.Presence = presence;
        SetWitness(fixture.Context.PmcData, fixture.Store.Stored.ActiveManifest!, marker);

        if (!succeeds)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ClaimAsync());
            Assert.Equal(0, fixture.Inventory.ApplyCalls);
            Assert.Equal(0, fixture.Committer.Calls);
            Assert.Equal(0, fixture.Store.SaveAttempts);
            return;
        }

        var result = await fixture.ClaimAsync();

        Assert.Equal(ManifestClaimResultKind.Granted, result.Kind);
        Assert.False(result.Replay);
        Assert.Equal(reapplies ? 1 : 0, fixture.Inventory.ApplyCalls);
        Assert.Equal(marker == MarkerState.Predecessor ? 1 : 0, fixture.Committer.Calls);
        Assert.NotNull(fixture.Store.Stored.FindManifestClaimGrant(ManifestId));
        Assert.Equal(
            marker == MarkerState.Current && presence == RewardPresence.Complete ? 1 : 0,
            fixture.Inventory.ReconcileCalls);
    }

    [Fact]
    public async Task Other_witness_with_absent_inventory_fails_closed_without_regranting()
    {
        var fixture = Fixture.ActiveClaim(ClaimStage.RewardOwed);
        fixture.Inventory.Presence = RewardPresence.Absent;
        SetWitness(
            fixture.Context.PmcData,
            fixture.Store.Stored.ActiveManifest!,
            MarkerState.Other);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ClaimAsync());

        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(0, fixture.Committer.Calls);
        Assert.Equal(0, fixture.Store.SaveAttempts);
        Assert.Null(fixture.Store.Stored.FindManifestClaimGrant(ManifestId));
    }

    [Theory]
    [InlineData(ClaimStage.PreparedFalse)]
    [InlineData(ClaimStage.PreparedTrue)]
    [InlineData(ClaimStage.RewardOwed)]
    public async Task Malformed_profile_witness_fails_closed_before_inventory_inspection(ClaimStage stage)
    {
        var fixture = Fixture.ActiveClaim(stage);
        fixture.Context.PmcData.ExtensionData ??= [];
        fixture.Context.PmcData.ExtensionData[ManifestClaimCommitWitness.ExtensionDataKey] = "v1:broken";

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.ClaimAsync());

        Assert.Equal(0, fixture.Inventory.InspectCalls);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(0, fixture.Committer.Calls);
        Assert.Equal(0, fixture.Store.SaveAttempts);
    }

    [Theory]
    [InlineData(ClaimStage.PreparedFalse)]
    [InlineData(ClaimStage.PreparedTrue)]
    [InlineData(ClaimStage.RewardOwed)]
    public async Task Prepared_claim_recovery_does_not_require_the_catalog_or_materializer_provider(
        ClaimStage stage)
    {
        var fixture = Fixture.ActiveClaim(stage, materializerMustNotRun: true);
        fixture.Inventory.Presence = RewardPresence.Absent;
        var unavailableCatalog = UnavailableCatalogCoordinator();
        var service = fixture.CreateService(catalogCoordinator: unavailableCatalog);

        var result = await service.ClaimAsync(
            fixture.Context,
            ManifestId,
            CancellationToken.None);

        Assert.Equal(ManifestClaimResultKind.Granted, result.Kind);
        Assert.False(unavailableCatalog.IsFrozen);
        Assert.Equal(0, fixture.Materializer.FindCalls);
        Assert.Equal(0, fixture.Materializer.IdCalls);
    }

    [Theory]
    [InlineData(ModConstants.CaseTemplateId)]
    [InlineData(CaseContracts.Operations)]
    [InlineData(CaseContracts.Relics)]
    [InlineData(CaseContracts.BlackSite)]
    public async Task Prepared_ticket_recovery_does_not_regenerate_the_catalog(string template)
    {
        var fixture = Fixture.FromJournal(
            new CaseOpeningJournal(activeManifest: TicketPreparedManifest(caseTemplateId: template)));
        var inventory = new PreparedTicketRecoveryInventory();
        var unavailableCatalog = UnavailableCatalogCoordinator();
        var service = fixture.CreateService(
            ticketInventory: inventory,
            catalogCoordinator: unavailableCatalog);

        await service.OpenAsync(fixture.Context, CaseId, CancellationToken.None);

        Assert.False(unavailableCatalog.IsFrozen);
        Assert.Equal(1, inventory.ReplayCalls);
        Assert.Equal(ManifestPhase.Offer1, fixture.Store.Stored.ActiveManifest!.FlowState.Phase);
        Assert.Equal(template, fixture.Store.Stored.ActiveManifest.Ticket.CaseTemplateId);
    }

    [Theory]
    [InlineData(ModConstants.CaseTemplateId, "stale")]
    [InlineData(ModConstants.CaseTemplateId, "")]
    [InlineData(CaseContracts.Operations, "stale")]
    [InlineData(CaseContracts.Relics, "wrong-case")]
    [InlineData(CaseContracts.BlackSite, null)]
    public async Task Fresh_ticket_rejects_stale_missing_or_wrong_case_authority_before_rng_or_writes(
        string template, string? expected)
    {
        var fixture = Fixture.FromJournal(new CaseOpeningJournal());
        var inventory = new FreshTicketInventoryProbe { FreshCaseTemplate = template };
        var draws = 0;
        var service = fixture.CreateService(ticketInventory: inventory,
            offerSelector: new ManifestCatalogSelector(() => { draws++; return 0; }));
        if (expected == "wrong-case") expected = fixture.CatalogCoordinator.GetSnapshot().SnapshotId;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.OpenAsync(fixture.Context, CaseId, CancellationToken.None, expected));

        Assert.Contains("Review its current odds", exception.Message);
        Assert.Equal(0, draws);
        Assert.Equal(0, inventory.ApplyCalls);
        Assert.Equal(0, inventory.RestoreCalls);
        Assert.Equal(0, fixture.Store.SaveAttempts);
        Assert.Null(fixture.Store.Stored.ActiveManifest);
        Assert.Equal(0, fixture.Committer.Calls);
    }

    [Fact]
    public async Task Prepared_relay_recovery_does_not_regenerate_the_catalog()
    {
        var fixture = Fixture.FromJournal(
            new CaseOpeningJournal(activeManifest: RelayPreparedManifest()));
        var inventory = new PreparedRelayRecoveryInventory();
        var unavailableCatalog = UnavailableCatalogCoordinator();
        var service = fixture.CreateService(
            relayKeyInventory: inventory,
            catalogCoordinator: unavailableCatalog);

        await service.RelayAsync(
            fixture.Context,
            ManifestId,
            ManifestPhase.RelayPrepared,
            expectedRelayStage: 1,
            CancellationToken.None);

        Assert.False(unavailableCatalog.IsFrozen);
        Assert.Equal(1, inventory.ReplayCalls);
        Assert.Null(fixture.Store.Stored.ActiveManifest);
        Assert.Equal(ManifestPhase.Confiscated, Assert.Single(fixture.Store.Stored.ManifestReceipts).TerminalPhase);
    }

    [Fact]
    public async Task Durable_grant_with_complete_inventory_replays_exact_ledger_ids_without_new_settlement()
    {
        var journal = GrantedJournal(out var exactGrant);
        var fixture = Fixture.FromJournal(journal, materializerMustNotRun: true);
        fixture.Inventory.Presence = RewardPresence.Complete;

        var result = await fixture.ClaimAsync();

        Assert.Equal(ManifestClaimResultKind.Granted, result.Kind);
        Assert.True(result.Replay);
        Assert.Equal(1, fixture.Inventory.ReplayCalls);
        Assert.Equal(exactGrant.ClaimPayload.ExactItemIds, fixture.Inventory.LastReplayed!.ExactItemIds);
        Assert.Equal(0, fixture.Inventory.PrepareCalls);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(0, fixture.Committer.Calls);
        Assert.Equal(0, fixture.Store.SaveAttempts);
        Assert.Equal(0, fixture.Materializer.FindCalls);
    }

    [Fact]
    public async Task Durable_grant_with_absent_inventory_does_not_emit_ghost_new_items()
    {
        var fixture = Fixture.FromJournal(GrantedJournal(out _), materializerMustNotRun: true);
        fixture.Inventory.Presence = RewardPresence.Absent;

        var result = await fixture.ClaimAsync();

        Assert.Equal(ManifestClaimResultKind.Granted, result.Kind);
        Assert.True(result.Replay);
        Assert.Equal(1, fixture.Inventory.InspectCalls);
        Assert.Equal(0, fixture.Inventory.ReplayCalls);
        Assert.Null(fixture.Context.Response.ProfileChanges);
        Assert.Equal(0, fixture.Committer.Calls);
        Assert.Equal(0, fixture.Store.SaveAttempts);
    }

    [Fact]
    public async Task Durable_grant_with_partial_inventory_fails_closed()
    {
        var fixture = Fixture.FromJournal(GrantedJournal(out _), materializerMustNotRun: true);
        fixture.Inventory.Presence = RewardPresence.Partial;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ClaimAsync());

        Assert.Equal(0, fixture.Inventory.ReplayCalls);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(0, fixture.Committer.Calls);
        Assert.Equal(0, fixture.Store.SaveAttempts);
    }

    [Fact]
    public async Task Durable_grant_replay_collision_propagates_without_any_new_settlement()
    {
        var fixture = Fixture.FromJournal(GrantedJournal(out _), materializerMustNotRun: true);
        fixture.Inventory.Presence = RewardPresence.Complete;
        fixture.Inventory.ReplayException = new InvalidOperationException("response collision");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ClaimAsync());

        Assert.Equal(1, fixture.Inventory.ReplayCalls);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(0, fixture.Committer.Calls);
        Assert.Equal(0, fixture.Store.SaveAttempts);
    }

    [Fact]
    public async Task Clock_rollback_clamps_terminal_completion_to_persisted_claim_time()
    {
        var preparedAt = CompletionAt.AddMinutes(5);
        var clock = new SequenceClock(preparedAt, DateTimeOffset.UnixEpoch);
        var fixture = Fixture.Entitlement(clock: clock.Read);

        await fixture.ClaimAsync();

        var grant = fixture.Store.Stored.FindManifestClaimGrant(ManifestId)!;
        Assert.Equal(preparedAt, grant.CommittedAtUtc);
        Assert.Equal(preparedAt, Assert.Single(fixture.Store.Stored.ManifestReceipts).CompletedAtUtc);
    }

    [Fact]
    public async Task Caller_cancellation_after_profile_boundary_starts_cannot_cancel_commit_or_terminal_save()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = Fixture.Entitlement();
        fixture.Committer.CloseGate();

        var claim = fixture.ClaimAsync(cancellation.Token);
        await fixture.Committer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        fixture.Committer.OpenGate();
        var result = await claim;

        Assert.Equal(ManifestClaimResultKind.Granted, result.Kind);
        Assert.False(Assert.Single(fixture.Committer.Tokens).CanBeCanceled);
        Assert.False(fixture.Store.SaveTokens[^1].CanBeCanceled);
    }

    [Fact]
    public async Task Simultaneous_fresh_claims_have_one_grant_and_one_exact_ledger_replay()
    {
        var fixture = Fixture.Entitlement();
        fixture.Committer.CloseGate();
        var firstContext = fixture.Context;
        var secondContext = fixture.NewContextWithLostResponse();

        var first = fixture.Service.ClaimAsync(firstContext, ManifestId, CancellationToken.None);
        await fixture.Committer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = fixture.Service.ClaimAsync(secondContext, ManifestId, CancellationToken.None);
        fixture.Committer.OpenGate();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(result => !result.Replay));
        Assert.Equal(1, results.Count(result => result.Replay));
        Assert.Equal(1, fixture.Materializer.IdCalls);
        Assert.Equal(1, fixture.Inventory.PrepareCalls);
        Assert.Equal(1, fixture.Inventory.ApplyCalls);
        Assert.Equal(1, fixture.Committer.Calls);
        Assert.Equal(2, fixture.Inventory.ReplayCalls);
        Assert.Single(fixture.Store.Stored.ManifestReceipts);
        Assert.NotNull(fixture.Store.Stored.FindManifestClaimGrant(ManifestId));
    }

    [Fact]
    public async Task Simultaneous_committed_reward_owed_claims_finish_once_then_replay_ledger_once()
    {
        var fixture = Fixture.ActiveClaim(ClaimStage.RewardOwed, materializerMustNotRun: true);
        fixture.Inventory.Presence = RewardPresence.Complete;
        SetWitness(
            fixture.Context.PmcData,
            fixture.Store.Stored.ActiveManifest!,
            MarkerState.Current);
        var secondContext = fixture.NewContextWithLostResponse();

        var first = fixture.ClaimAsync();
        var second = fixture.Service.ClaimAsync(secondContext, ManifestId, CancellationToken.None);
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(result => !result.Replay));
        Assert.Equal(1, results.Count(result => result.Replay));
        Assert.Equal(0, fixture.Materializer.FindCalls);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(0, fixture.Committer.Calls);
        Assert.Equal(2, fixture.Inventory.ReplayCalls);
    }

    public static IEnumerable<object[]> WitnessCases()
    {
        foreach (var stage in Enum.GetValues<ClaimStage>())
        {
            foreach (var marker in Enum.GetValues<MarkerState>())
            {
                foreach (var presence in Enum.GetValues<RewardPresence>())
                {
                    var witnessIsCurrent = marker == MarkerState.Current;
                    var witnessIsPredecessor = marker == MarkerState.Predecessor;
                    var preparedFalse = stage == ClaimStage.PreparedFalse;
                    var succeeds = preparedFalse
                        ? witnessIsPredecessor && presence == RewardPresence.Absent ||
                          witnessIsCurrent && presence == RewardPresence.Complete
                        : witnessIsCurrent
                            ? presence != RewardPresence.Partial
                            : witnessIsPredecessor && presence == RewardPresence.Absent;
                    var reapplies = succeeds && witnessIsPredecessor;
                    yield return [stage, marker, presence, succeeds, reapplies];
                }
            }
        }
    }

    private static ManifestRecord EntitlementManifest(
        IEnumerable<ManifestRelayCandidateSnapshot>? relayCandidates = null)
    {
        var offers = Offers();
        var activeTicket = ManifestFlowState.ActivateTicket(ManifestFlowState.PrepareTicket());
        var entitlementState = ManifestStateMachine.DecideOffer(
            activeTicket,
            ManifestOfferDecision.Lock);
        return new ManifestRecord(
            ManifestId,
            "catalog-claim-service",
            Commitment(offers),
            entitlementState,
            Ticket(),
            offers,
            [new ManifestDecisionRecord(1, ManifestOfferDecision.Lock, DecisionAt)],
            Entitlement(offers[0]),
            relayCandidates,
            relayHistory: null,
            brokerFavor: 0);
    }

    private static ManifestRecord TicketPreparedManifest(bool profileCommitStarted = true,
        string caseTemplateId = ModConstants.CaseTemplateId)
    {
        var offers = Offers();
        return new ManifestRecord(
            ManifestId,
            "catalog-claim-service",
            Commitment(offers),
            ManifestFlowState.PrepareTicket(),
            new ManifestTicketPayload(
                CaseId,
                KeyId,
                TicketPreparedAt,
                profileCommitStarted,
                committed: false,
                committedAtUtc: null,
                commitGeneration: 1,
                commitPredecessorHash: ManifestInputCommitWitness.GenesisHash,
                caseTemplateId: caseTemplateId),
            offers,
            decisions: null,
            entitlement: null,
            relayCandidates: null,
            relayHistory: null,
            brokerFavor: 0);
    }

    private static ManifestRecord RelayPreparedManifest()
    {
        var active = EntitlementManifest(RelayCandidates());
        var prepared = new ManifestRelayPreparedPayload(
            Id(60),
            ManifestRelayResult.Confiscated,
            output: null,
            RelayRules.GetOdds(1),
            new CanonicalRngEvidence(
                ManifestRngPurpose.RelayOutcome,
                drawOrdinal: 1,
                CanonicalRngEvidence.UnitDenominator - 1),
            targetRng: null,
            brokerFavorBefore: 0,
            brokerFavorAfter: 1,
            profileCommitStarted: false,
            ClaimPreparedAt,
            nextRelayCandidates: null,
            commitGeneration: 1,
            commitPredecessorHash: ManifestInputCommitWitness.GenesisHash);
        return active.BeginRelay(prepared).BeginRelayProfileCommit();
    }

    private static ManifestRecord OfferOneManifest()
    {
        var offers = Offers();
        return new ManifestRecord(
            ManifestId,
            "catalog-claim-service",
            Commitment(offers),
            ManifestFlowState.ActivateTicket(ManifestFlowState.PrepareTicket()),
            Ticket(),
            offers,
            decisions: null,
            entitlement: null,
            relayCandidates: null,
            relayHistory: null,
            brokerFavor: 0);
    }

    private static ManifestRecord OfferTwoManifest()
    {
        var offers = Offers();
        var activeTicket = ManifestFlowState.ActivateTicket(ManifestFlowState.PrepareTicket());
        var offerTwo = ManifestStateMachine.DecideOffer(
            activeTicket,
            ManifestOfferDecision.Burn);
        return new ManifestRecord(
            ManifestId,
            "catalog-claim-service",
            Commitment(offers),
            offerTwo,
            Ticket(),
            offers,
            [new ManifestDecisionRecord(1, ManifestOfferDecision.Burn, DecisionAt)],
            entitlement: null,
            relayCandidates: null,
            relayHistory: null,
            brokerFavor: 0);
    }

    private static ManifestRecord ActiveClaimManifest(ClaimStage stage)
    {
        var entitlement = EntitlementManifest();
        var claimState = ManifestStateMachine.PrepareClaim(entitlement.FlowState);
        var prepared = PreparedPayload(stage != ClaimStage.PreparedFalse);
        var flowState = stage == ClaimStage.RewardOwed
            ? ManifestStateMachine.MarkRewardOwed(claimState)
            : claimState;
        return new ManifestRecord(
            entitlement.ManifestId,
            entitlement.CatalogSnapshotId,
            entitlement.Commitment,
            flowState,
            entitlement.Ticket,
            entitlement.Offers,
            entitlement.Decisions,
            entitlement.Entitlement,
            entitlement.RelayCandidates,
            entitlement.RelayHistory,
            entitlement.BrokerFavor,
            prepared);
    }

    private static CaseOpeningJournal GrantedJournal(out ManifestClaimGrantRecord grant)
    {
        var active = ActiveClaimManifest(ClaimStage.PreparedTrue);
        var completion = active.CompleteClaim(CompletionAt);
        var journal = new CaseOpeningJournal(activeManifest: active);
        journal.FinishGrantedActiveManifest(completion.TerminalManifest, completion.Grant);
        grant = completion.Grant;
        return journal;
    }

    private static ManifestClaimPreparedPayload PreparedPayload(bool profileCommitStarted)
    {
        var item = new Item
        {
            Id = RewardId,
            Template = TemplateA,
            ParentId = StashId.ToString(),
            SlotId = "hideout",
            Location = new ItemLocation
            {
                X = 1,
                Y = 0,
                R = ItemRotation.Horizontal,
                Rotation = false
            },
            Upd = new Upd { StackObjectsCount = 1 }
        };
        return new ManifestClaimPreparedPayload(
            [item],
            [RewardId],
            profileCommitStarted,
            ClaimPreparedAt,
            commitGeneration: 1,
            commitPredecessorHash: ManifestClaimCommitWitness.GenesisHash);
    }

    private static ManifestOfferSnapshot[] Offers()
    {
        var lots = new[]
        {
            Lot("provider-a", "lot-a", "family-a", TemplateA, RewardRarity.ScavGrade, 1),
            Lot("provider-b", "lot-b", "family-b", TemplateB, RewardRarity.Contractor, 2),
            Lot("provider-c", "lot-c", "family-c", TemplateC, RewardRarity.Restricted, 3)
        };
        return lots.Select((lot, index) => new ManifestOfferSnapshot(
            index + 1,
            lot.Rarity,
            lot.Identity,
            lot.Forest,
            lot.Fingerprint,
            new CanonicalRngEvidence(
                ManifestRngPurpose.OfferSelection,
                index + 1,
                index + 1)))
            .ToArray();
    }

    private static ManifestRelayCandidateSnapshot[] RelayCandidates(
        IReadOnlyList<LotData>? source = null)
    {
        var lots = source ?? RelayLots();
        return
        [
            new ManifestRelayCandidateSnapshot(
                ManifestRelayResult.Upgrade,
                lots[0].Rarity,
                lots[0].Identity,
                lots[0].Forest,
                lots[0].Fingerprint),
            new ManifestRelayCandidateSnapshot(
                ManifestRelayResult.Sidegrade,
                lots[1].Rarity,
                lots[1].Identity,
                lots[1].Forest,
                lots[1].Fingerprint)
        ];
    }

    private static LotData[] RelayLots()
    {
        var trackId = Offers()[0].Identity.TrackId.Value;
        var upgrade = Lot(
            "provider-relay-upgrade",
            "relay-upgrade",
            "family-relay",
            TemplateB,
            RewardRarity.Uncommon,
            20,
            trackId: trackId);
        var sidegrade = Lot(
            "provider-relay-sidegrade",
            "relay-sidegrade",
            "family-relay",
            TemplateC,
            RewardRarity.ScavGrade,
            21,
            trackId: trackId);
        return [upgrade, sidegrade];
    }

    private static LotData Lot(
        string providerId,
        string lotId,
        string familyId,
        string templateId,
        RewardRarity rarity,
        int seed,
        double weight = 1d,
        string? trackId = null)
    {
        var forest = RewardForest.Create(
            [new RewardForestNode("root", "root", templateId, null, null, null, 1)]);
        var fingerprint = RewardForestFingerprintV2.Compute(providerId, lotId, forest);
        var definition = new CargoLotDefinition(
            providerId,
            "1.0.0",
            lotId,
            "Display " + lotId,
            "Settlement service test lot " + lotId,
            new FamilyId(familyId),
            new TrackId(trackId ?? "claim-track-" + seed),
            templateId,
            weight,
            new RaidRole("claim-role-" + seed),
            [new TemplateLine(templateId, 1, 1)]);
        var identity = CargoLotIdentitySnapshot.Capture(definition, fingerprint);
        var value = rarity switch
        {
            RewardRarity.ScavGrade => 20_000L,
            RewardRarity.Uncommon => 50_000L,
            RewardRarity.Contractor => 100_000L,
            RewardRarity.Restricted => 200_000L,
            RewardRarity.BlackLabel => 400_000L,
            _ => throw new ArgumentOutOfRangeException(nameof(rarity))
        };
        var resolved = new ResolvedCargoLot(
            definition,
            forest,
            fingerprint,
            identity,
            new CargoLotEvaluation(value, value, 1, rarity));
        return new LotData(rarity, identity, forest, fingerprint, resolved);
    }

    private static ResolvedCargoLot[] BaseCatalogLots() =>
    [
        Lot("provider-a", "lot-a", "family-a", TemplateA, RewardRarity.ScavGrade, 1).Resolved,
        Lot("provider-b", "lot-b", "family-b", TemplateB, RewardRarity.Contractor, 2).Resolved,
        Lot("provider-c", "lot-c", "family-c", TemplateC, RewardRarity.Restricted, 3).Resolved
    ];

    private static CargoCatalogSnapshot Catalog(
        IEnumerable<ResolvedCargoLot>? source = null,
        char hashCharacter = 'b',
        IReadOnlyDictionary<string, double>? providerWeights = null)
    {
        var lots = (source ?? BaseCatalogLots()).ToArray();
        return new CargoCatalogSnapshot(
            new string(hashCharacter, 64),
            lots,
            [],
            providerWeights ?? lots.ToDictionary(
                    lot => lot.Identity.ProviderId,
                    _ => 1d,
                    StringComparer.Ordinal));
    }

    private static CargoCatalogSnapshot CatalogWithUnrelatedDrift(
        IEnumerable<ResolvedCargoLot>? additionalLots = null)
    {
        var lots = BaseCatalogLots()
            .Concat(additionalLots ?? [])
            .Append(Lot(
                "provider-unrelated",
                "lot-unrelated",
                "family-unrelated",
                TemplateC,
                RewardRarity.ScavGrade,
                30).Resolved)
            .ToArray();
        var providerWeights = lots.ToDictionary(
            lot => lot.Identity.ProviderId,
            _ => 1d,
            StringComparer.Ordinal);
        providerWeights["provider-a"] = 7d;
        return Catalog(lots, hashCharacter: 'c', providerWeights);
    }

    private static CatalogSnapshotCoordinator Coordinator(CargoCatalogSnapshot catalog)
    {
        var coordinator = new CatalogSnapshotCoordinator(() => catalog);
        coordinator.MarkStartupComplete();
        return coordinator;
    }

    private static CatalogSnapshotCoordinator UnavailableCatalogCoordinator()
    {
        var coordinator = new CatalogSnapshotCoordinator(() =>
            throw new Xunit.Sdk.XunitException("Prepared recovery regenerated the Manifest catalog."));
        coordinator.MarkStartupComplete();
        return coordinator;
    }

    private static ManifestEntitlementSnapshot Entitlement(ManifestOfferSnapshot offer) => new(
        offer.Rarity,
        offer.Identity,
        offer.Forest,
        offer.Fingerprint);

    private static ManifestCommitmentEvidence Commitment(
        IEnumerable<ManifestOfferSnapshot> offers) =>
        ManifestCommitmentEvidence.Create(
            ManifestId,
            "catalog-claim-service",
            new string('a', ManifestCommitmentEvidence.NonceByteCount * 2),
            offers);

    private static ManifestTicketPayload Ticket() => new(
        CaseId,
        KeyId,
        TicketPreparedAt,
        profileCommitStarted: true,
        committed: true,
        TicketCommittedAt);

    private static ManifestClaimCommitWitnessInspection InspectWitness(
        PmcData profile,
        ManifestRecord active) =>
        ManifestClaimCommitWitness.Inspect(
            profile,
            ProfileId,
            active.ManifestId,
            active.Entitlement!.Fingerprint,
            active.ClaimPrepared!);

    private static void SetWitness(PmcData profile, ManifestRecord active, MarkerState marker)
    {
        profile.ExtensionData?.Remove(ManifestClaimCommitWitness.ExtensionDataKey);
        if (marker == MarkerState.Predecessor)
        {
            return;
        }

        ManifestClaimCommitWitness.Stage(
            profile,
            ProfileId,
            marker == MarkerState.Current ? active.ManifestId : active.ManifestId + "-other",
            active.Entitlement!.Fingerprint,
            active.ClaimPrepared!);
    }

    private static Item LocatedClone(Item source, int x)
    {
        var item = CaseOpeningRecord.CloneItem(source);
        item.ParentId = StashId.ToString();
        item.SlotId = "hideout";
        item.Location = new ItemLocation
        {
            X = x,
            Y = 0,
            R = ItemRotation.Horizontal,
            Rotation = false
        };
        return item;
    }

    private static Item BaselineItem() => new()
    {
        Id = BaselineId,
        Template = TemplateC,
        ParentId = StashId.ToString(),
        SlotId = "hideout",
        Location = new ItemLocation
        {
            X = 0,
            Y = 0,
            R = ItemRotation.Horizontal,
            Rotation = false
        }
    };

    private static PmcData Profile() => new()
    {
        Inventory = new BotBaseInventory
        {
            Stash = StashId,
            SortingTable = SortingTableId,
            Items = [BaselineItem()]
        },
        InsuredItems = []
    };

    private static MongoId Id(int value) => (MongoId)value.ToString("x24");

    public enum ClaimStage
    {
        PreparedFalse,
        PreparedTrue,
        RewardOwed
    }

    public enum EntitlementCatalogMismatch
    {
        Missing,
        Mutated
    }

    public enum MarkerState
    {
        Predecessor,
        Current,
        Other
    }

    private sealed record LotData(
        RewardRarity Rarity,
        CargoLotIdentitySnapshot Identity,
        RewardForest Forest,
        RewardForestFingerprintV2 Fingerprint,
        ResolvedCargoLot Resolved);

    private sealed class Fixture
    {
        private readonly Func<DateTimeOffset> _clock;
        private readonly ProfileLockPool _lockPool;
        private readonly ManifestClaimCommitUncertaintyCoordinator _uncertaintyCoordinator;

        private Fixture(
            CaseOpeningJournal journal,
            bool enterLobby,
            IEnumerable<MongoId>? generatedIds,
            bool materializerMustNotRun,
            Func<DateTimeOffset>? clock)
        {
            Store = new FakeJournalStore(journal);
            Inventory = new FakeManifestClaimInventory();
            Committer = new GatedCommitter();
            RaidSessions = new RaidSessionState();
            _lockPool = new ProfileLockPool();
            _uncertaintyCoordinator = new ManifestClaimCommitUncertaintyCoordinator();
            _clock = clock ?? (() => CompletionAt);
            Materializer = new MaterializerProbe(
                generatedIds ?? [RewardId],
                materializerMustNotRun);
            CatalogCoordinator = new CatalogSnapshotCoordinator(() => Catalog());
            CatalogCoordinator.MarkStartupComplete();
            Context = new OpeningContext(Profile(), new ItemEventRouterResponse(), ProfileId);
            if (enterLobby)
            {
                RaidSessions.BeginGameStart(ProfileId);
                RaidSessions.CompleteGameStart(ProfileId, profileIsReady: true);
            }

            Service = CreateService();
        }

        public FakeJournalStore Store { get; }
        public FakeManifestClaimInventory Inventory { get; }
        public GatedCommitter Committer { get; }
        public RaidSessionState RaidSessions { get; }
        public MaterializerProbe Materializer { get; }
        public CatalogSnapshotCoordinator CatalogCoordinator { get; }
        public OpeningContext Context { get; }
        public ManifestSettlementService Service { get; }
        public ProfileLockPool ProfileLocks => _lockPool;

        public Task<string> ReadSnapshotAsync() => ManifestSnapshotRouter.CreateSnapshotResponseAsync(
            new ManifestSnapshotRequest { ManifestId = ManifestId }, ProfileId,
            new HttpResponseUtil(new JsonUtil([]), null!), Store, _lockPool, RaidSessions,
            CatalogCoordinator, null!, CancellationToken.None).AsTask();

        public static Fixture Entitlement(
            bool enterLobby = true,
            IEnumerable<MongoId>? generatedIds = null,
            Func<DateTimeOffset>? clock = null) =>
            new(
                new CaseOpeningJournal(activeManifest: EntitlementManifest()),
                enterLobby,
                generatedIds,
                materializerMustNotRun: false,
                clock);

        public static Fixture ActiveClaim(
            ClaimStage stage,
            bool materializerMustNotRun = false) =>
            new(
                new CaseOpeningJournal(activeManifest: ActiveClaimManifest(stage)),
                enterLobby: true,
                generatedIds: null,
                materializerMustNotRun,
                clock: null);

        public static Fixture FromJournal(
            CaseOpeningJournal journal,
            bool materializerMustNotRun = false) =>
            new(
                journal,
                enterLobby: true,
                generatedIds: null,
                materializerMustNotRun,
                clock: null);

        public ManifestSettlementService CreateService(
            ManifestClaimCommitUncertaintyCoordinator? uncertaintyCoordinator = null,
            IManifestTicketInventory? ticketInventory = null,
            IManifestRelayKeyInventory? relayKeyInventory = null,
            CatalogSnapshotCoordinator? catalogCoordinator = null,
            Func<long>? nextUnitNumerator = null,
            ManifestCatalogSelector? offerSelector = null,
            CargoLotMaterializer? materializer = null,
            TestingForcedCrateRegistry? forcedCrateRegistry = null,
            IManifestClaimInventory? claimInventory = null) => new(
            Store,
            claimInventory ?? Inventory,
            Committer,
            _lockPool,
            RaidSessions,
            materializer ?? Materializer.Instance,
            _clock,
            uncertaintyCoordinator ?? _uncertaintyCoordinator,
            ticketInventory,
            relayKeyInventory,
            catalogCoordinator ?? CatalogCoordinator,
            offerSelector ?? new ManifestCatalogSelector(() => 0),
            nextUnitNumerator,
            forcedCrateRegistry: forcedCrateRegistry);

        public Task<ManifestClaimResult> ClaimAsync(
            CancellationToken cancellationToken = default) =>
            Service.ClaimAsync(Context, ManifestId, cancellationToken);

        public Task<ManifestClaimResult> ClaimAsync(OpeningContext context) =>
            Service.ClaimAsync(context, ManifestId, CancellationToken.None);

        public OpeningContext NewContextWithLostResponse() =>
            new(Context.PmcData, new ItemEventRouterResponse(), ProfileId);
    }

    private sealed class MaterializerProbe
    {
        private readonly object _sync = new();
        private readonly Queue<MongoId> _ids;
        private readonly bool _mustNotRun;
        private int _findCalls;
        private int _idCalls;

        public MaterializerProbe(IEnumerable<MongoId> ids, bool mustNotRun)
        {
            _ids = new Queue<MongoId>(ids);
            _mustNotRun = mustNotRun;
            Instance = new CargoLotMaterializer(FindTemplate, CreateId);
        }

        public CargoLotMaterializer Instance { get; }
        public int FindCalls => Volatile.Read(ref _findCalls);
        public int IdCalls => Volatile.Read(ref _idCalls);

        private TemplateItem? FindTemplate(string id)
        {
            Interlocked.Increment(ref _findCalls);
            if (_mustNotRun)
            {
                throw new Xunit.Sdk.XunitException("Prepared Claim recovery used the cargo provider.");
            }

            return id is TemplateA or TemplateB or TemplateC ? Template(id) : null;
        }

        private MongoId CreateId()
        {
            Interlocked.Increment(ref _idCalls);
            if (_mustNotRun)
            {
                throw new Xunit.Sdk.XunitException("Prepared Claim recovery allocated a cargo ID.");
            }

            lock (_sync)
            {
                if (_ids.Count == 0)
                {
                    throw new Xunit.Sdk.XunitException("The deterministic cargo ID queue was exhausted.");
                }
                return _ids.Dequeue();
            }
        }

        private static TemplateItem Template(string id) => new()
        {
            Id = id,
            Parent = ItemRootTemplateId,
            Name = id,
            Type = "Item",
            Properties = new TemplateItemProperties
            {
                StackMaxSize = 1,
                Width = 1,
                Height = 1,
                QuestItem = false,
                DogTagQualities = false,
                Slots = [],
                Chambers = [],
                Cartridges = [],
                StackSlots = [],
                Grids = [],
                Prefab = new Prefab { Path = $"test/{id}.bundle" }
            }
        };
    }

    private sealed class FakeJournalStore : ICaseOpeningJournalStore
    {
        private readonly object _sync = new();
        private int _loadCalls;

        public FakeJournalStore(CaseOpeningJournal stored)
        {
            Initial = Copy(stored);
            Stored = Copy(stored);
        }

        public CaseOpeningJournal Initial { get; }
        public CaseOpeningJournal Stored { get; private set; }
        public List<CaseOpeningJournal> Saved { get; } = [];
        public List<CancellationToken> SaveTokens { get; } = [];
        public int LoadCalls => Volatile.Read(ref _loadCalls);
        public int SaveAttempts { get; private set; }
        public int? FailBeforeSaveAttempt { get; set; }
        public int? FailAfterSaveAttempt { get; set; }
        public Action<int>? BeforeSave { get; set; }

        public ValueTask<CaseOpeningJournal> LoadAsync(
            MongoId profileId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _loadCalls);
            lock (_sync)
            {
                return ValueTask.FromResult(Copy(Stored));
            }
        }

        public ValueTask SaveAsync(
            MongoId profileId,
            CaseOpeningJournal journal,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                SaveAttempts++;
                SaveTokens.Add(cancellationToken);
                BeforeSave?.Invoke(SaveAttempts);
                if (FailBeforeSaveAttempt == SaveAttempts)
                {
                    throw new IOException("journal save failed before persistence");
                }

                Stored = Copy(journal);
                Saved.Add(Copy(journal));
                if (FailAfterSaveAttempt == SaveAttempts)
                {
                    throw new IOException("journal save failed after persistence");
                }
                return ValueTask.CompletedTask;
            }
        }

        private static CaseOpeningJournal Copy(CaseOpeningJournal journal) => new(
            journal.Records,
            journal.RelayRecords,
            journal.RecoveryMeter,
            journal.LegacySecuredStakeRoots,
            journal.ActiveManifest,
            journal.ManifestReceipts,
            journal.ManifestClaimGrants);
    }

    private sealed class FakeManifestClaimInventory : IManifestClaimInventory
    {
        private int _prepareCalls;
        private int _inspectCalls;
        private int _reconcileCalls;
        private int _applyCalls;
        private int _restoreCalls;
        private int _replayCalls;
        private RewardPresence _presence = RewardPresence.Absent;

        public bool PrepareSucceeds { get; set; } = true;
        public Exception? PrepareException { get; set; }
        public Exception? ApplyExceptionAfterMutation { get; set; }
        public Exception? ReplayException { get; set; }
        public IReadOnlyList<Item>? PreparedMaterializedItems { get; private set; }
        public ManifestClaimPreparedPayload? LastApplied { get; private set; }
        public ManifestClaimPreparedPayload? LastReplayed { get; private set; }
        public int PrepareCalls => Volatile.Read(ref _prepareCalls);
        public int InspectCalls => Volatile.Read(ref _inspectCalls);
        public int ReconcileCalls => Volatile.Read(ref _reconcileCalls);
        public int ApplyCalls => Volatile.Read(ref _applyCalls);
        public int RestoreCalls => Volatile.Read(ref _restoreCalls);
        public int ReplayCalls => Volatile.Read(ref _replayCalls);

        public RewardPresence Presence
        {
            get => _presence;
            set => _presence = value;
        }

        public bool TryPrepareClaim(
            OpeningContext context,
            IReadOnlyList<Item> materializedItems,
            IReadOnlyList<MongoId> rootIds,
            DateTimeOffset preparedAtUtc,
            out ManifestClaimPreparedPayload? prepared)
        {
            Interlocked.Increment(ref _prepareCalls);
            PreparedMaterializedItems = materializedItems.Select(CaseOpeningRecord.CloneItem).ToArray();
            if (PrepareException is not null)
            {
                throw PrepareException;
            }
            if (!PrepareSucceeds)
            {
                prepared = null;
                return false;
            }

            var located = LocateRoots(materializedItems, rootIds, x: 1);
            prepared = new ManifestClaimPreparedPayload(
                located,
                rootIds,
                profileCommitStarted: false,
                preparedAtUtc);
            return true;
        }

        public RewardPresence InspectClaim(
            OpeningContext context,
            ManifestClaimPreparedPayload prepared)
        {
            Interlocked.Increment(ref _inspectCalls);
            return Presence;
        }

        public ManifestClaimPreparedPayload ReconcileAppliedClaim(
            OpeningContext context,
            ManifestClaimPreparedPayload prepared)
        {
            Interlocked.Increment(ref _reconcileCalls);
            if (Presence != RewardPresence.Complete)
            {
                throw new InvalidOperationException("cannot reconcile an incomplete fake payload");
            }

            return new ManifestClaimPreparedPayload(
                prepared.Items,
                prepared.RootIds,
                profileCommitStarted: true,
                prepared.PreparedAtUtc,
                prepared.CommitGeneration,
                prepared.CommitPredecessorHash);
        }

        public InventoryCheckpoint Capture(OpeningContext context)
        {
            var response = CloneResponse(context.Response);
            return new FakeCheckpoint(
                Presence,
                context.PmcData.Inventory!.Items!
                    .Select(CaseOpeningRecord.CloneItem)
                    .ToArray(),
                ManifestClaimCommitWitness.CaptureToken(context.PmcData),
                response);
        }

        public ManifestClaimPreparedPayload ApplyPreparedClaim(
            OpeningContext context,
            ManifestClaimPreparedPayload prepared,
            string? rewardName = null)
        {
            Interlocked.Increment(ref _applyCalls);
            if (Presence != RewardPresence.Absent)
            {
                throw new InvalidOperationException("fake Claim payload is not absent");
            }

            var liveItems = LocateRoots(prepared.Items, prepared.RootIds, x: 2);
            var applied = new ManifestClaimPreparedPayload(
                liveItems,
                prepared.RootIds,
                profileCommitStarted: true,
                prepared.PreparedAtUtc,
                prepared.CommitGeneration,
                prepared.CommitPredecessorHash);
            context.PmcData.Inventory!.Items!.AddRange(
                liveItems.Select(CaseOpeningRecord.CloneItem));
            Presence = RewardPresence.Complete;
            LastApplied = applied;
            if (ApplyExceptionAfterMutation is not null)
            {
                throw ApplyExceptionAfterMutation;
            }
            return applied;
        }

        public void Restore(OpeningContext context, InventoryCheckpoint checkpoint)
        {
            Interlocked.Increment(ref _restoreCalls);
            var state = Assert.IsType<FakeCheckpoint>(checkpoint);
            Presence = state.Presence;
            context.PmcData.Inventory!.Items = state.Items
                .Select(CaseOpeningRecord.CloneItem)
                .ToList();
            ManifestClaimCommitWitness.RestoreToken(context.PmcData, state.WitnessToken);
            RestoreResponse(context.Response, state.Response);
        }

        public void ReplayClaim(
            OpeningContext context,
            ManifestClaimPreparedPayload prepared)
        {
            Interlocked.Increment(ref _replayCalls);
            LastReplayed = prepared;
            if (ReplayException is not null)
            {
                throw ReplayException;
            }

            var changes = SptResponseChanges.GetOrCreate(context.Response, context.ProfileId);
            var exactIds = prepared.ExactItemIds.ToHashSet();
            var existing = (changes.NewItems ?? []).Where(item => exactIds.Contains(item.Id)).ToArray();
            if (existing.Length == 0)
            {
                changes.NewItems!.AddRange(prepared.Items.Select(CaseOpeningRecord.CloneItem));
            }
            else if (existing.Length != exactIds.Count)
            {
                throw new InvalidOperationException("fake replay found a partial response payload");
            }
        }

        private static Item[] LocateRoots(
            IReadOnlyList<Item> items,
            IReadOnlyList<MongoId> rootIds,
            int x)
        {
            var roots = rootIds.ToHashSet();
            return items.Select(item =>
            {
                var clone = CaseOpeningRecord.CloneItem(item);
                if (roots.Contains(clone.Id))
                {
                    clone = LocatedClone(clone, x);
                }
                return clone;
            }).ToArray();
        }

        private static ItemEventRouterResponse CloneResponse(ItemEventRouterResponse response) => new()
        {
            Warnings = response.Warnings?.Select(warning => new Warning
            {
                Index = warning.Index,
                ErrorMessage = warning.ErrorMessage,
                Code = warning.Code,
                Data = warning.Data
            }).ToList(),
            ProfileChanges = response.ProfileChanges?.ToDictionary(
                pair => pair.Key,
                pair => new ProfileChange
                {
                    Id = pair.Value.Id,
                    Items = pair.Value.Items is null
                        ? null
                        : new ItemChanges
                        {
                            NewItems = pair.Value.Items.NewItems?
                                .Select(CaseOpeningRecord.CloneItem)
                                .ToList(),
                            ChangedItems = pair.Value.Items.ChangedItems?
                                .Select(CaseOpeningRecord.CloneItem)
                                .ToList(),
                            DeletedItems = pair.Value.Items.DeletedItems?
                                .Select(item => new DeletedItem { Id = item.Id })
                                .ToList() ?? []
                        }
                })!
        };

        private static void RestoreResponse(
            ItemEventRouterResponse target,
            ItemEventRouterResponse snapshot)
        {
            var restored = CloneResponse(snapshot);
            target.Warnings = restored.Warnings;
            target.ProfileChanges = restored.ProfileChanges;
        }

        private sealed record FakeCheckpoint(
            RewardPresence Presence,
            IReadOnlyList<Item> Items,
            string? WitnessToken,
            ItemEventRouterResponse Response) : InventoryCheckpoint;
    }

    private sealed record EmptyInventoryCheckpoint : InventoryCheckpoint;

    private sealed class FreshTicketInventoryProbe : IManifestTicketInventory
    {
        private int _applyCalls;
        private int _restoreCalls;

        public int ApplyCalls => Volatile.Read(ref _applyCalls);
        public int RestoreCalls => Volatile.Read(ref _restoreCalls);
        public string? FreshCaseTemplate { get; init; }
        public Action? BeforeApply { get; init; }
        public Action? OnStage { get; init; }
        public Action? OnRestore { get; init; }

        public ManifestTicketPayload PrepareTicket(
            OpeningContext context,
            MongoId caseId,
            DateTimeOffset preparedAtUtc) => FreshCaseTemplate is { } template
                ? new ManifestTicketPayload(caseId, KeyId, preparedAtUtc, false, false, null,
                    1, ManifestInputCommitWitness.GenesisHash, template)
                : throw new Xunit.Sdk.XunitException(
                "Fresh ticket recovery attempted to prepare a replacement ticket.");

        public ManifestInventoryPresence InspectTicket(
            OpeningContext context,
            ManifestTicketPayload prepared) => ManifestInventoryPresence.Present;

        public ManifestCommitWitnessState InspectTicketCommit(
            OpeningContext context,
            string manifestId,
            ManifestTicketPayload prepared) => ManifestCommitWitnessState.Predecessor;

        public InventoryCheckpoint CaptureTicket(OpeningContext context) => new EmptyInventoryCheckpoint();

        public ManifestTicketPayload ApplyPreparedTicket(
            OpeningContext context,
            ManifestTicketPayload prepared)
        {
            BeforeApply?.Invoke();
            Interlocked.Increment(ref _applyCalls);
            return prepared.BeginProfileCommit();
        }

        public void StageTicketCommit(
            OpeningContext context,
            string manifestId,
            ManifestTicketPayload prepared)
        {
            OnStage?.Invoke();
        }

        public void RestoreTicket(OpeningContext context, InventoryCheckpoint checkpoint)
        {
            OnRestore?.Invoke();
            Interlocked.Increment(ref _restoreCalls);
        }

        public void ReplayTicket(
            OpeningContext context,
            ManifestTicketPayload prepared) => throw new Xunit.Sdk.XunitException(
                "A rejected fresh ticket was replayed.");
    }

    private sealed class PreparedTicketRecoveryInventory : IManifestTicketInventory
    {
        private int _replayCalls;

        public int ReplayCalls => Volatile.Read(ref _replayCalls);

        public ManifestTicketPayload PrepareTicket(
            OpeningContext context,
            MongoId caseId,
            DateTimeOffset preparedAtUtc) => throw UnexpectedMutation();

        public ManifestInventoryPresence InspectTicket(
            OpeningContext context,
            ManifestTicketPayload prepared) => ManifestInventoryPresence.Absent;

        public ManifestCommitWitnessState InspectTicketCommit(
            OpeningContext context,
            string manifestId,
            ManifestTicketPayload prepared) => ManifestCommitWitnessState.Current;

        public InventoryCheckpoint CaptureTicket(OpeningContext context) => new EmptyInventoryCheckpoint();

        public ManifestTicketPayload ApplyPreparedTicket(
            OpeningContext context,
            ManifestTicketPayload prepared) => throw UnexpectedMutation();

        public void StageTicketCommit(
            OpeningContext context,
            string manifestId,
            ManifestTicketPayload prepared) => throw UnexpectedMutation();

        public void RestoreTicket(
            OpeningContext context,
            InventoryCheckpoint checkpoint) => throw UnexpectedMutation();

        public void ReplayTicket(OpeningContext context, ManifestTicketPayload prepared)
        {
            Interlocked.Increment(ref _replayCalls);
        }

        private static Exception UnexpectedMutation() => new Xunit.Sdk.XunitException(
            "Prepared ticket recovery attempted a fresh inventory mutation.");
    }

    private sealed class PreparedRelayRecoveryInventory : IManifestRelayKeyInventory
    {
        private int _replayCalls;

        public int ReplayCalls => Volatile.Read(ref _replayCalls);

        public ManifestRelayKeyPreparation PrepareRelayKey(
            OpeningContext context,
            IReadOnlySet<MongoId> excludedIds,
            DateTimeOffset preparedAtUtc) => throw UnexpectedMutation();

        public ManifestInventoryPresence InspectRelayKey(
            OpeningContext context,
            ManifestRelayPreparedPayload prepared) => ManifestInventoryPresence.Absent;

        public ManifestCommitWitnessState InspectRelayKeyCommit(
            OpeningContext context,
            string manifestId,
            RewardForestFingerprintV2 inputFingerprint,
            ManifestRelayPreparedPayload prepared) => ManifestCommitWitnessState.Current;

        public InventoryCheckpoint CaptureRelayKey(OpeningContext context) => new EmptyInventoryCheckpoint();

        public ManifestRelayPreparedPayload ApplyPreparedRelayKey(
            OpeningContext context,
            ManifestRelayPreparedPayload prepared) => throw UnexpectedMutation();

        public void StageRelayKeyCommit(
            OpeningContext context,
            string manifestId,
            RewardForestFingerprintV2 inputFingerprint,
            ManifestRelayPreparedPayload prepared) => throw UnexpectedMutation();

        public void RestoreRelayKey(
            OpeningContext context,
            InventoryCheckpoint checkpoint) => throw UnexpectedMutation();

        public void ReplayRelayKey(
            OpeningContext context,
            ManifestRelayPreparedPayload prepared)
        {
            Interlocked.Increment(ref _replayCalls);
        }

        private static Exception UnexpectedMutation() => new Xunit.Sdk.XunitException(
            "Prepared Relay recovery attempted a fresh inventory mutation.");
    }

    private sealed class FreshRelayKeyInventoryProbe : IManifestRelayKeyInventory
    {
        private int _prepareCalls;
        private int _applyCalls;

        public int PrepareCalls => Volatile.Read(ref _prepareCalls);
        public int ApplyCalls => Volatile.Read(ref _applyCalls);
        public Action? BeforeApply { get; init; }
        public Action? OnStage { get; init; }
        public Action? OnRestore { get; init; }

        public ManifestRelayKeyPreparation PrepareRelayKey(
            OpeningContext context,
            IReadOnlySet<MongoId> excludedIds,
            DateTimeOffset preparedAtUtc)
        {
            Interlocked.Increment(ref _prepareCalls);
            Assert.Contains(CaseId, excludedIds);
            Assert.Contains(KeyId, excludedIds);
            return new ManifestRelayKeyPreparation(
                Id(50),
                preparedAtUtc,
                commitGeneration: 1,
                commitPredecessorHash: ManifestInputCommitWitness.GenesisHash);
        }

        public ManifestInventoryPresence InspectRelayKey(
            OpeningContext context,
            ManifestRelayPreparedPayload prepared) => ManifestInventoryPresence.Present;

        public ManifestCommitWitnessState InspectRelayKeyCommit(
            OpeningContext context,
            string manifestId,
            RewardForestFingerprintV2 inputFingerprint,
            ManifestRelayPreparedPayload prepared) => ManifestCommitWitnessState.Predecessor;

        public InventoryCheckpoint CaptureRelayKey(OpeningContext context) => new EmptyInventoryCheckpoint();

        public ManifestRelayPreparedPayload ApplyPreparedRelayKey(
            OpeningContext context,
            ManifestRelayPreparedPayload prepared)
        {
            BeforeApply?.Invoke();
            Interlocked.Increment(ref _applyCalls);
            return prepared.BeginProfileCommit();
        }

        public void StageRelayKeyCommit(
            OpeningContext context,
            string manifestId,
            RewardForestFingerprintV2 inputFingerprint,
            ManifestRelayPreparedPayload prepared)
        {
            OnStage?.Invoke();
        }

        public void RestoreRelayKey(
            OpeningContext context,
            InventoryCheckpoint checkpoint)
        {
            if (OnRestore is not null) OnRestore();
            else throw new Xunit.Sdk.XunitException("A successful fresh Relay restored its inventory checkpoint.");
        }

        public void ReplayRelayKey(
            OpeningContext context,
            ManifestRelayPreparedPayload prepared) => throw new Xunit.Sdk.XunitException(
                "A successful fresh Relay replayed instead of applying its key mutation.");
    }

    private sealed class MustNotRunRelayKeyInventory : IManifestRelayKeyInventory
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ManifestRelayKeyPreparation PrepareRelayKey(
            OpeningContext context,
            IReadOnlySet<MongoId> excludedIds,
            DateTimeOffset preparedAtUtc) => throw Failure();

        public ManifestInventoryPresence InspectRelayKey(
            OpeningContext context,
            ManifestRelayPreparedPayload prepared) => throw Failure();

        public ManifestCommitWitnessState InspectRelayKeyCommit(
            OpeningContext context,
            string manifestId,
            RewardForestFingerprintV2 inputFingerprint,
            ManifestRelayPreparedPayload prepared) => throw Failure();

        public InventoryCheckpoint CaptureRelayKey(OpeningContext context) => throw Failure();

        public ManifestRelayPreparedPayload ApplyPreparedRelayKey(
            OpeningContext context,
            ManifestRelayPreparedPayload prepared) => throw Failure();

        public void StageRelayKeyCommit(
            OpeningContext context,
            string manifestId,
            RewardForestFingerprintV2 inputFingerprint,
            ManifestRelayPreparedPayload prepared) => throw Failure();

        public void RestoreRelayKey(
            OpeningContext context,
            InventoryCheckpoint checkpoint) => throw Failure();

        public void ReplayRelayKey(
            OpeningContext context,
            ManifestRelayPreparedPayload prepared) => throw Failure();

        private Exception Failure()
        {
            Interlocked.Increment(ref _callCount);
            return new Xunit.Sdk.XunitException(
                "Relay-key inventory ran before frozen catalog compatibility was proven.");
        }
    }

    private sealed class GatedCommitter : IProfileCommitter
    {
        private readonly object _sync = new();
        private TaskCompletionSource? _gate;
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public ConcurrentQueue<CancellationToken> Tokens { get; } = new();
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? ExceptionAfterBoundary { get; set; }
        public Func<MongoId, CancellationToken, ValueTask<IDisposable?>>? MutationLeaseFactory { get; set; }
        public Action? BeforeCommit { get; set; }

        public ValueTask<IDisposable?> AcquireMutationLeaseAsync(MongoId profileId, CancellationToken token) =>
            MutationLeaseFactory?.Invoke(profileId, token) ?? ValueTask.FromResult<IDisposable?>(null);

        public void CloseGate()
        {
            lock (_sync)
            {
                _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void OpenGate()
        {
            lock (_sync)
            {
                _gate?.TrySetResult();
            }
        }

        public async Task CommitAsync(MongoId profileId, CancellationToken cancellationToken)
        {
            BeforeCommit?.Invoke();
            Interlocked.Increment(ref _calls);
            Tokens.Enqueue(cancellationToken);
            Started.TrySetResult();
            Task? gate;
            lock (_sync)
            {
                gate = _gate?.Task;
            }
            if (gate is not null)
            {
                await gate.ConfigureAwait(false);
            }
            if (ExceptionAfterBoundary is not null)
            {
                throw ExceptionAfterBoundary;
            }
        }
    }

    private sealed class TestSaveLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private sealed class SequenceClock(params DateTimeOffset[] values)
    {
        private readonly Queue<DateTimeOffset> _values = new(values);
        private DateTimeOffset _last = values.Last();

        public DateTimeOffset Read()
        {
            if (_values.Count > 0)
            {
                _last = _values.Dequeue();
            }
            return _last;
        }
    }
}
