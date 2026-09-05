using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Utils.Cloners;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class ManifestInputInventoryTests
{
    private static readonly MongoId ProfileId = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly MongoId StashId = "bbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly MongoId CaseId = "000000000000000000000010";
    private static readonly MongoId AttachedKeyId = "000000000000000000000020";
    private static readonly MongoId SelectedKeyId = "000000000000000000000021";
    private static readonly MongoId OtherKeyId = "000000000000000000000022";
    private static readonly MongoId BaselineId = "000000000000000000000030";
    private static readonly DateTimeOffset PreparedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);
    private static readonly RewardForestFingerprintV2 InputFingerprint = new(new string('a', 64));

    [Theory]
    [InlineData(ModConstants.CaseTemplateId)]
    [InlineData(CaseContracts.Operations)]
    [InlineData(CaseContracts.Relics)]
    [InlineData(CaseContracts.BlackSite)]
    public void Every_case_consumes_the_same_key_and_preserves_its_template(string template)
    {
        var item = Case(CaseId);
        item.Template = template;
        var context = CreateContext(item, Key(SelectedKeyId), Key(OtherKeyId));
        var inventory = CreateInventory();
        var ticket = inventory.PrepareTicket(context, CaseId, PreparedAt);
        Assert.Equal(template, ticket.CaseTemplateId);
        Assert.Equal(SelectedKeyId, ticket.KeyId);
        var applied = inventory.ApplyPreparedTicket(context, ticket);
        Assert.Equal(template, applied.CaseTemplateId);
        Assert.Equal(template, applied.Commit(PreparedAt.AddMinutes(1)).CaseTemplateId);
        Assert.Equal(OtherKeyId, Assert.Single(context.PmcData.Inventory!.Items!).Id);
    }

    [Fact]
    public void Swapping_the_physical_case_template_invalidates_prepared_inputs_and_witness()
    {
        var item = Case(CaseId);
        item.Template = CaseContracts.Relics;
        var context = CreateContext(item, Key(SelectedKeyId));
        var inventory = CreateInventory();
        var ticket = inventory.PrepareTicket(context, CaseId, PreparedAt);
        context.PmcData.Inventory!.Items!.Single(i => i.Id == CaseId).Template = CaseContracts.Operations;
        Assert.Equal(ManifestInventoryPresence.Partial, inventory.InspectTicket(context, ticket));
        Assert.Throws<InvalidOperationException>(() => inventory.ApplyPreparedTicket(context, ticket));
        Assert.Equal(2, context.PmcData.Inventory.Items!.Count);

        var applied = ticket.BeginProfileCommit();
        ManifestInputCommitWitness.StageTicket(context.PmcData, ProfileId, "manifest-a", applied);
        var substituted = new ManifestTicketPayload(ticket.CaseId, ticket.KeyId, ticket.PreparedAtUtc,
            true, false, null, ticket.CommitGeneration, ticket.CommitPredecessorHash, CaseContracts.Operations);
        Assert.Equal(ManifestCommitWitnessState.Other,
            ManifestInputCommitWitness.InspectTicket(context.PmcData, ProfileId, "manifest-a", substituted));
        Assert.Equal(ManifestCommitWitnessState.Current,
            ManifestInputCommitWitness.InspectTicket(context.PmcData, ProfileId, "manifest-a", applied));
    }

    [Fact]
    public void Ticket_preparation_selects_the_requested_case_and_lowest_nonattached_key()
    {
        var context = CreateContext(
            Case(CaseId),
            Key(OtherKeyId),
            Key(AttachedKeyId),
            ChildOf(AttachedKeyId),
            Key(SelectedKeyId));

        var prepared = CreateInventory().PrepareTicket(context, CaseId, PreparedAt);

        Assert.Equal(CaseId, prepared.CaseId);
        Assert.Equal(SelectedKeyId, prepared.KeyId);
        Assert.Equal(1, prepared.CommitGeneration);
        Assert.Equal(ManifestInputCommitWitness.GenesisHash, prepared.CommitPredecessorHash);
        Assert.False(prepared.ProfileCommitStarted);
    }

    [Fact]
    public void Ticket_preparation_rejects_a_nonleaf_requested_case()
    {
        var context = CreateContext(Case(CaseId), ChildOf(CaseId), Key(SelectedKeyId));

        Assert.Throws<InvalidOperationException>(() =>
            CreateInventory().PrepareTicket(context, CaseId, PreparedAt));
    }

    [Fact]
    public void Missing_usable_key_leaves_ticket_and_relay_inputs_untouched()
    {
        var context = CreateContext(Case(CaseId), Key(AttachedKeyId), ChildOf(AttachedKeyId), Baseline());
        var before = context.PmcData.Inventory!.Items!.ToArray();
        var inventory = CreateInventory();
        Assert.Throws<RelayKeyRequiredException>(() => inventory.PrepareTicket(context, CaseId, PreparedAt));
        Assert.Throws<RelayKeyRequiredException>(() => inventory.PrepareRelayKey(context, new HashSet<MongoId>(), PreparedAt));
        Assert.Equal(before, context.PmcData.Inventory.Items);
        Assert.Empty(context.Response.Warnings ?? []);
    }

    [Fact]
    public void Ticket_presence_distinguishes_present_absent_and_partial_exactly()
    {
        var prepared = PlannedTicket();
        var inventory = CreateInventory();

        Assert.Equal(
            ManifestInventoryPresence.Present,
            inventory.InspectTicket(CreateContext(Case(CaseId), Key(SelectedKeyId)), prepared));
        Assert.Equal(
            ManifestInventoryPresence.Absent,
            inventory.InspectTicket(CreateContext(), prepared));
        Assert.Equal(
            ManifestInventoryPresence.Partial,
            inventory.InspectTicket(CreateContext(Case(CaseId)), prepared));
        Assert.Equal(
            ManifestInventoryPresence.Partial,
            inventory.InspectTicket(
                CreateContext(Case(CaseId), new Item { Id = SelectedKeyId, Template = BaselineId }),
                prepared));
        Assert.Equal(
            ManifestInventoryPresence.Partial,
            inventory.InspectTicket(
                CreateContext(Case(CaseId), Key(SelectedKeyId), ChildOf(SelectedKeyId)),
                prepared));
    }

    [Fact]
    public void Ticket_apply_removes_only_prepared_inputs_and_replay_emits_exact_deletions()
    {
        var context = CreateContext(Case(CaseId), Key(SelectedKeyId), Key(OtherKeyId), Baseline());
        var inventory = CreateInventory();
        var prepared = inventory.PrepareTicket(context, CaseId, PreparedAt);

        var applied = inventory.ApplyPreparedTicket(context, prepared);

        Assert.True(applied.ProfileCommitStarted);
        Assert.Equal(new[] { OtherKeyId, BaselineId }, context.PmcData.Inventory!.Items!.Select(item => item.Id));
        Assert.Equal(
            new[] { CaseId, SelectedKeyId },
            context.Response.ProfileChanges[ProfileId].Items!.DeletedItems.Select(item => item.Id));

        var replayContext = CreateContext();
        inventory.ReplayTicket(replayContext, applied);
        Assert.Equal(
            new[] { CaseId, SelectedKeyId },
            replayContext.Response.ProfileChanges[ProfileId].Items!.DeletedItems.Select(item => item.Id));
        Assert.Throws<InvalidOperationException>(() => inventory.ReplayTicket(replayContext, applied));
    }

    [Fact]
    public void Relay_key_preparation_selects_lowest_nonexcluded_leaf_and_apply_is_exact()
    {
        var context = CreateContext(
            Key(AttachedKeyId),
            ChildOf(AttachedKeyId),
            Key(SelectedKeyId),
            Key(OtherKeyId),
            Baseline());
        var inventory = CreateInventory();

        var selection = inventory.PrepareRelayKey(
            context,
            new HashSet<MongoId> { SelectedKeyId },
            PreparedAt);
        Assert.Equal(OtherKeyId, selection.KeyId);
        var prepared = RelayPayload(selection);
        Assert.Equal(ManifestInventoryPresence.Present, inventory.InspectRelayKey(context, prepared));

        var applied = inventory.ApplyPreparedRelayKey(context, prepared);

        Assert.True(applied.ProfileCommitStarted);
        Assert.DoesNotContain(context.PmcData.Inventory!.Items!, item => item.Id == OtherKeyId);
        Assert.Contains(context.PmcData.Inventory.Items!, item => item.Id == SelectedKeyId);
        Assert.Equal(
            new[] { OtherKeyId },
            context.Response.ProfileChanges[ProfileId].Items!.DeletedItems.Select(item => item.Id));

        var replayContext = CreateContext();
        inventory.ReplayRelayKey(replayContext, applied);
        Assert.Equal(
            new[] { OtherKeyId },
            replayContext.Response.ProfileChanges[ProfileId].Items!.DeletedItems.Select(item => item.Id));
    }

    [Fact]
    public void Relay_key_presence_treats_wrong_missing_or_attached_evidence_as_partial_or_absent()
    {
        var prepared = RelayPayload(new ManifestRelayKeyPreparation(
            SelectedKeyId,
            PreparedAt,
            1,
            ManifestInputCommitWitness.GenesisHash));
        var inventory = CreateInventory();

        Assert.Equal(ManifestInventoryPresence.Absent, inventory.InspectRelayKey(CreateContext(), prepared));
        Assert.Equal(
            ManifestInventoryPresence.Partial,
            inventory.InspectRelayKey(
                CreateContext(new Item { Id = SelectedKeyId, Template = BaselineId }),
                prepared));
        Assert.Equal(
            ManifestInventoryPresence.Partial,
            inventory.InspectRelayKey(
                CreateContext(Key(SelectedKeyId), ChildOf(SelectedKeyId)),
                prepared));
    }

    [Fact]
    public void Ticket_witness_accepts_only_its_predecessor_or_exact_current_head()
    {
        var profile = CreateContext().PmcData;
        var first = ManifestInputCommitWitness.PlanTicket(profile, ProfileId, UnplannedTicket(CaseId, SelectedKeyId));
        Assert.Equal(
            ManifestCommitWitnessState.Predecessor,
            ManifestInputCommitWitness.InspectTicket(profile, ProfileId, "manifest-a", first));

        var applied = first.BeginProfileCommit();
        ManifestInputCommitWitness.StageTicket(profile, ProfileId, "manifest-a", applied);
        Assert.Equal(
            ManifestCommitWitnessState.Current,
            ManifestInputCommitWitness.InspectTicket(profile, ProfileId, "manifest-a", applied));

        var rewritten = new ManifestTicketPayload(
            CaseId,
            OtherKeyId,
            PreparedAt,
            profileCommitStarted: true,
            committed: false,
            committedAtUtc: null,
            first.CommitGeneration,
            first.CommitPredecessorHash);
        Assert.Equal(
            ManifestCommitWitnessState.Other,
            ManifestInputCommitWitness.InspectTicket(profile, ProfileId, "manifest-a", rewritten));

        var second = ManifestInputCommitWitness.PlanTicket(
            profile,
            ProfileId,
            UnplannedTicket("000000000000000000000011", OtherKeyId));
        Assert.Equal(2, second.CommitGeneration);
        Assert.Equal(
            ManifestCommitWitnessState.Predecessor,
            ManifestInputCommitWitness.InspectTicket(profile, ProfileId, "manifest-b", second));
    }

    [Fact]
    public void Relay_witness_binds_profile_manifest_input_and_prepared_outcome()
    {
        var profile = CreateContext().PmcData;
        var selection = ManifestInputCommitWitness.PlanRelayKey(
            profile,
            ProfileId,
            SelectedKeyId,
            PreparedAt);
        var prepared = RelayPayload(selection);
        Assert.Equal(
            ManifestCommitWitnessState.Predecessor,
            ManifestInputCommitWitness.InspectRelayKey(
                profile,
                ProfileId,
                "manifest-a",
                InputFingerprint,
                prepared));

        var applied = prepared.BeginProfileCommit();
        ManifestInputCommitWitness.StageRelayKey(
            profile,
            ProfileId,
            "manifest-a",
            InputFingerprint,
            applied);
        Assert.Equal(
            ManifestCommitWitnessState.Current,
            ManifestInputCommitWitness.InspectRelayKey(
                profile,
                ProfileId,
                "manifest-a",
                InputFingerprint,
                applied));
        Assert.Equal(
            ManifestCommitWitnessState.Other,
            ManifestInputCommitWitness.InspectRelayKey(
                profile,
                ProfileId,
                "manifest-b",
                InputFingerprint,
                applied));
        Assert.Equal(
            ManifestCommitWitnessState.Other,
            ManifestInputCommitWitness.InspectRelayKey(
                profile,
                ProfileId,
                "manifest-a",
                new RewardForestFingerprintV2(new string('b', 64)),
                applied));
        Assert.Throws<InvalidOperationException>(() =>
            ManifestInputCommitWitness.InspectRelayKey(
                profile,
                "cccccccccccccccccccccccc",
                "manifest-a",
                InputFingerprint,
                applied));
    }

    [Fact]
    public void Checkpoint_restores_both_manifest_input_witness_tokens_and_response_shape()
    {
        var context = CreateContext(Case(CaseId), Key(SelectedKeyId));
        var inventory = CreateInventory();
        var ticket = ManifestInputCommitWitness.PlanTicket(
            context.PmcData,
            ProfileId,
            UnplannedTicket(CaseId, SelectedKeyId)).BeginProfileCommit();
        ManifestInputCommitWitness.StageTicket(context.PmcData, ProfileId, "manifest-ticket", ticket);
        var relaySelection = ManifestInputCommitWitness.PlanRelayKey(
            context.PmcData,
            ProfileId,
            OtherKeyId,
            PreparedAt.AddMinutes(1));
        var relay = RelayPayload(relaySelection).BeginProfileCommit();
        ManifestInputCommitWitness.StageRelayKey(
            context.PmcData,
            ProfileId,
            "manifest-relay",
            InputFingerprint,
            relay);
        var beforeTokens = ManifestInputCommitWitness.CaptureTokens(context.PmcData);
        var checkpoint = inventory.CaptureTicket(context);

        context.PmcData.ExtensionData!.Remove(ManifestInputCommitWitness.TicketExtensionDataKey);
        context.PmcData.ExtensionData.Remove(ManifestInputCommitWitness.RelayKeyExtensionDataKey);
        context.PmcData.Inventory!.Items!.Clear();
        SptResponseChanges.GetOrCreate(context.Response, ProfileId)
            .DeletedItems.Add(new DeletedItem { Id = BaselineId });

        inventory.RestoreTicket(context, checkpoint);

        Assert.Equal(beforeTokens, ManifestInputCommitWitness.CaptureTokens(context.PmcData));
        Assert.Equal(new[] { CaseId, SelectedKeyId }, context.PmcData.Inventory.Items.Select(item => item.Id));
        Assert.Null(context.Response.ProfileChanges);
    }

    [Fact]
    public void Malformed_manifest_input_witness_fails_closed_before_capture_or_inspection()
    {
        var context = CreateContext();
        context.PmcData.ExtensionData = new Dictionary<string, object>
        {
            [ManifestInputCommitWitness.TicketExtensionDataKey] = "v1:bad"
        };

        Assert.Throws<InvalidOperationException>(() => CreateInventory().CaptureTicket(context));
        Assert.Throws<InvalidOperationException>(() =>
            ManifestInputCommitWitness.InspectTicket(
                context.PmcData,
                ProfileId,
                "manifest-a",
                PlannedTicket()));
    }

    private static SptOpeningInventory CreateInventory() =>
        SptOpeningInventory.CreateForTests(
            new TestCloner(),
            (_, _, _, _) => throw new InvalidOperationException("Unexpected add operation."),
            removeItem: RemoveItem);

    private static void RemoveItem(
        PmcData profile,
        MongoId itemId,
        MongoId profileId,
        ItemEventRouterResponse response)
    {
        var items = profile.Inventory?.Items
            ?? throw new InvalidOperationException("Missing inventory.");
        if (items.RemoveAll(item => item.Id == itemId) != 1)
        {
            throw new InvalidOperationException("Expected exactly one removable item.");
        }
        SptResponseChanges.GetOrCreate(response, profileId)
            .DeletedItems.Add(new DeletedItem { Id = itemId });
    }

    private static OpeningContext CreateContext(params Item[] items) => new(
        new PmcData
        {
            Inventory = new BotBaseInventory
            {
                Stash = StashId,
                Items = items.Select(CaseOpeningRecord.CloneItem).ToList()
            },
            InsuredItems = []
        },
        new ItemEventRouterResponse(),
        ProfileId);

    private static ManifestTicketPayload PlannedTicket() => new(
        CaseId,
        SelectedKeyId,
        PreparedAt,
        profileCommitStarted: false,
        committed: false,
        committedAtUtc: null,
        commitGeneration: 1,
        commitPredecessorHash: ManifestInputCommitWitness.GenesisHash);

    private static ManifestTicketPayload UnplannedTicket(MongoId caseId, MongoId keyId) => new(
        caseId,
        keyId,
        PreparedAt,
        profileCommitStarted: false,
        committed: false,
        committedAtUtc: null);

    private static ManifestRelayPreparedPayload RelayPayload(ManifestRelayKeyPreparation selection) => new(
        selection.KeyId,
        ManifestRelayResult.Confiscated,
        output: null,
        new RelayOdds(0, 0, 100),
        new CanonicalRngEvidence(ManifestRngPurpose.RelayOutcome, 1, 0),
        targetRng: null,
        brokerFavorBefore: 0,
        brokerFavorAfter: 1,
        profileCommitStarted: false,
        selection.PreparedAtUtc,
        nextRelayCandidates: null,
        selection.CommitGeneration,
        selection.CommitPredecessorHash);

    private static Item Case(MongoId id) => new()
    {
        Id = id,
        Template = (MongoId)ModConstants.CaseTemplateId,
        ParentId = StashId.ToString(),
        SlotId = "hideout"
    };

    private static Item Key(MongoId id) => new()
    {
        Id = id,
        Template = (MongoId)ModConstants.KeyTemplateId,
        ParentId = StashId.ToString(),
        SlotId = "hideout"
    };

    private static Item Baseline() => new()
    {
        Id = BaselineId,
        Template = "dddddddddddddddddddddddd",
        ParentId = StashId.ToString(),
        SlotId = "hideout"
    };

    private static Item ChildOf(MongoId parentId) => new()
    {
        Id = new MongoId(),
        Template = "eeeeeeeeeeeeeeeeeeeeeeee",
        ParentId = parentId.ToString(),
        SlotId = "child"
    };

    private sealed class TestCloner : ICloner
    {
        public T? Clone<T>(T? value)
        {
            if (value is null)
            {
                return default;
            }

            object clone = value switch
            {
                BotBaseInventory inventory => new BotBaseInventory
                {
                    Items = (inventory.Items ?? []).Select(CaseOpeningRecord.CloneItem).ToList(),
                    Equipment = inventory.Equipment,
                    Stash = inventory.Stash,
                    SortingTable = inventory.SortingTable,
                    QuestRaidItems = inventory.QuestRaidItems,
                    QuestStashItems = inventory.QuestStashItems,
                    HideoutCustomizationStashId = inventory.HideoutCustomizationStashId
                },
                List<InsuredItem> insured => insured.Select(item => new InsuredItem
                {
                    TId = item.TId,
                    ItemId = item.ItemId
                }).ToList(),
                ItemChanges changes => new ItemChanges
                {
                    NewItems = changes.NewItems?.Select(CaseOpeningRecord.CloneItem).ToList(),
                    ChangedItems = changes.ChangedItems?.Select(CaseOpeningRecord.CloneItem).ToList(),
                    DeletedItems = changes.DeletedItems?.Select(item => new DeletedItem { Id = item.Id }).ToList() ?? []
                },
                List<Warning> warnings => warnings.Select(item => new Warning
                {
                    Index = item.Index,
                    ErrorMessage = item.ErrorMessage,
                    Code = item.Code,
                    Data = item.Data
                }).ToList(),
                _ => throw new NotSupportedException($"Unsupported test clone type {typeof(T).FullName}.")
            };
            return (T)clone;
        }
    }
}
