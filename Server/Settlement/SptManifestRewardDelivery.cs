using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Servers;

namespace ContrabandCases.Server.Settlement;

/// <summary>
/// Stages native Messenger attachments in the same profile as the Claim witness.
/// MailSendService cannot be used here: it reallocates item IDs and notifies before
/// durable commit. Native Messenger still owns all subsequent item collection.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class SptManifestRewardDelivery : IManifestClaimInventory
{
    internal const int RootsPerMessage = 8;
    // Native SPT treats zero/null lifetime as expired, not unlimited. Avoid the
    // two-day mail default without overflowing the client's signed seconds field.
    internal const long StorageSeconds = 10L * 365 * 24 * 60 * 60;
    internal static readonly MongoId SenderId = "5a7c2eca46aef81a7ca2145d"; // Mechanic
    private static readonly TimeSpan NotificationBudget = TimeSpan.FromSeconds(2);
    private readonly IManifestClaimInventory _legacy;
    private readonly Func<MongoId, SptProfile> _profile;
    private readonly Func<MongoId, Message, Task> _notify;
    private readonly Action<Exception> _notificationFailure;

    public SptManifestRewardDelivery(SptOpeningInventory legacy, SaveServer saveServer,
        NotifierHelper notifier, NotificationSendHelper notifications,
        ISptLogger<SptManifestRewardDelivery> logger)
        : this(legacy, saveServer.GetProfile,
            async (id, message) => await notifications.SendMessageAsync(id,
                notifier.CreateNewMessageNotification(message)).ConfigureAwait(false),
            error => logger.Warning("Contraband Cases reward mail is saved; notification failed. Open Messenger to collect.", error))
    {
    }

    internal SptManifestRewardDelivery(IManifestClaimInventory legacy,
        Func<MongoId, SptProfile> profile, Func<MongoId, Message, Task> notify,
        Action<Exception> notificationFailure)
    {
        _legacy = legacy;
        _profile = profile;
        _notify = notify;
        _notificationFailure = notificationFailure;
    }

    public ManifestClaimPreparedPayload PrepareDelivery(ManifestClaimPreparedPayload prepared) =>
        prepared.WithDelivery(ClaimDeliveryKind.Messenger);

    public bool TryPrepareClaim(OpeningContext context, IReadOnlyList<Item> materializedItems,
        IReadOnlyList<MongoId> rootIds, DateTimeOffset preparedAtUtc,
        out ManifestClaimPreparedPayload? prepared)
    {
        RequireCleanResponse(context);
        prepared = new ManifestClaimPreparedPayload(materializedItems, rootIds, false, preparedAtUtc,
            delivery: ClaimDeliveryKind.Messenger);
        EnsureIdsAvailable(context, prepared);
        return true; // Mail delivery does not depend on stash capacity.
    }

    public RewardPresence InspectClaim(OpeningContext context, ManifestClaimPreparedPayload prepared)
    {
        if (prepared.Delivery == ClaimDeliveryKind.LegacyInventory)
            return _legacy.InspectClaim(context, prepared);
        var profile = RequireProfile(context);
        var expected = CreateMessages(context.ProfileId, prepared);
        var messages = AllMessages(profile).Where(m => expected.Any(e => e.Id == m.Id)).ToArray();
        if (messages.Length == 0)
        {
            var ids = prepared.ExactItemIds.ToHashSet();
            return AllItems(profile).Any(i => ids.Contains(i.Id)) ? RewardPresence.Partial : RewardPresence.Absent;
        }
        if (messages.Length != expected.Count || messages.Select(m => m.Id).Distinct().Count() != messages.Length)
            return RewardPresence.Partial;
        try
        {
            foreach (var message in expected)
            {
                var actual = messages.Single(m => m.Id == message.Id);
                if (actual.UserId != SenderId || actual.Items?.Stash != message.Items!.Stash)
                    return RewardPresence.Partial;
                SptOpeningInventory.EnsureExactItemState(message.Items!.Data!, actual.Items!.Data ?? [], "reward mail");
            }
            return RewardPresence.Complete;
        }
        catch (InvalidOperationException) { return RewardPresence.Partial; }
    }

    public ManifestClaimPreparedPayload ApplyPreparedClaim(OpeningContext context, ManifestClaimPreparedPayload prepared,
        string? rewardName = null)
    {
        RequireCleanResponse(context);
        if (prepared.Delivery != ClaimDeliveryKind.Messenger || prepared.ProfileCommitStarted)
            throw new InvalidOperationException("Only an uncommitted Messenger delivery may be applied.");
        EnsureIdsAvailable(context, prepared);
        var profile = RequireProfile(context);
        var messages = CreateMessages(context.ProfileId, prepared, rewardName);
        profile.DialogueRecords ??= [];
        if (!profile.DialogueRecords.TryGetValue(SenderId, out var dialogue))
        {
            dialogue = new Dialogue { Id = SenderId, Type = MessageType.NpcTraderMessage,
                Messages = [], Pinned = false, New = 0, AttachmentsNew = 0 };
            profile.DialogueRecords.Add(SenderId, dialogue);
        }
        dialogue.Messages ??= [];
        dialogue.Messages.AddRange(messages);
        dialogue.New = checked((dialogue.New ?? 0) + messages.Count);
        dialogue.AttachmentsNew = checked((dialogue.AttachmentsNew ?? 0) + messages.Count);
        return Applied(prepared);
    }

    public ManifestClaimPreparedPayload ReconcileAppliedClaim(OpeningContext context, ManifestClaimPreparedPayload prepared)
    {
        if (prepared.Delivery == ClaimDeliveryKind.LegacyInventory)
            return _legacy.ReconcileAppliedClaim(context, prepared);
        if (InspectClaim(context, prepared) != RewardPresence.Complete)
            throw new InvalidOperationException("The exact staged reward mail is unavailable.");
        return Applied(prepared);
    }

    public void ReplayClaim(OpeningContext context, ManifestClaimPreparedPayload prepared)
    {
        if (prepared.Delivery == ClaimDeliveryKind.LegacyInventory) _legacy.ReplayClaim(context, prepared);
        // Never put attachments into ItemEvent.NewItems: that would inject them into
        // the live inventory. Never reconstruct mail on a lost-response retry.
    }

    public InventoryCheckpoint Capture(OpeningContext context)
    {
        var profile = RequireProfile(context);
        var dialogue = profile.DialogueRecords?.GetValueOrDefault(SenderId);
        return new MailCheckpoint(_legacy.Capture(context), profile.DialogueRecords is not null,
            dialogue, dialogue?.Messages?.ToList(), dialogue?.New, dialogue?.AttachmentsNew);
    }

    public void Restore(OpeningContext context, InventoryCheckpoint checkpoint)
    {
        var saved = checkpoint as MailCheckpoint ?? throw new ArgumentException("Wrong delivery checkpoint.", nameof(checkpoint));
        _legacy.Restore(context, saved.Inventory);
        var profile = RequireProfile(context);
        if (saved.Dialogue is null) profile.DialogueRecords?.Remove(SenderId);
        else
        {
            saved.Dialogue.Messages = saved.Messages;
            saved.Dialogue.New = saved.New;
            saved.Dialogue.AttachmentsNew = saved.AttachmentsNew;
            profile.DialogueRecords![SenderId] = saved.Dialogue;
        }
        if (!saved.HadDialogues && profile.DialogueRecords?.Count == 0) profile.DialogueRecords = null;
    }

    public async Task NotifyClaimAsync(OpeningContext context, ManifestClaimPreparedPayload prepared)
    {
        if (prepared.Delivery != ClaimDeliveryKind.Messenger) return;
        try
        {
            var profileId = context.ProfileId;
            var ids = CreateMessages(profileId, prepared).Select(m => m.Id).ToHashSet();
            // A native send cannot be cancelled and may outlive the profile lock.
            // Snapshot our generated messages and their mutable attachment trees
            // before allowing collection or another profile operation to proceed.
            var messages = AllMessages(RequireProfile(context)).Where(m => ids.Contains(m.Id))
                .Select(message => message with
                {
                    Items = message.Items is null ? null : message.Items with
                    { Data = message.Items.Data?.Select(CaseOpeningRecord.CloneItem).ToList() }
                }).ToArray();
            using var deadline = new CancellationTokenSource(NotificationBudget);
            foreach (var message in messages)
            {
                Task? notification = null;
                try
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    notification = _notify(profileId, message);
                    await notification.WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    if (notification is not null) _ = ObserveLateNotificationAsync(notification);
                    ReportNotificationFailure(new TimeoutException(
                        "Reward mail is saved, but its live notification exceeded the delivery deadline."));
                    return; // One budget for the entire batch, not a delay per message.
                }
                catch (Exception error) { ReportNotificationFailure(error); }
            }
        }
        catch (Exception error) { ReportNotificationFailure(error); }
    }

    private async Task ObserveLateNotificationAsync(Task notification)
    {
        try { await notification.ConfigureAwait(false); }
        catch (Exception error) { ReportNotificationFailure(error); }
    }

    private void ReportNotificationFailure(Exception error)
    {
        try { _notificationFailure(error); }
        catch (Exception)
        {
            // Neither a socket failure nor an unavailable logger may turn an
            // already committed Messenger payout into a failed Claim response.
        }
    }

    internal static IReadOnlyList<Message> CreateMessages(MongoId profileId, ManifestClaimPreparedPayload prepared,
        string? rewardName = null)
    {
        var trees = SptOpeningInventory.PartitionClaimTrees(prepared.Items, prepared.RootIds);
        var batches = trees.Chunk(RootsPerMessage).ToArray();
        var result = new List<Message>();
        for (var index = 0; index < batches.Length; index++)
        {
            var seed = "contraband-cases/mail/v1/" + profileId + "/" +
                prepared.PreparedAtUtc.ToString("O", CultureInfo.InvariantCulture) + "/" +
                string.Join(',', prepared.ExactItemIds) + "/" + index.ToString(CultureInfo.InvariantCulture);
            var stash = StableId(seed + "/stash");
            var roots = prepared.RootIds.ToHashSet();
            var items = batches[index].SelectMany(tree => tree).Select(item => roots.Contains(item.Id)
                ? item with { ParentId = stash.ToString(), SlotId = "main", Location = null }
                : CaseOpeningRecord.CloneItem(item)).ToList();
            result.Add(new Message
            {
                Id = StableId(seed + "/message"), UserId = SenderId,
                MessageType = MessageType.MessageWithItems, DateTime = prepared.PreparedAtUtc.ToUnixTimeSeconds(),
                Text = $"Contraband Cases — {DeliveryLabel(rewardName)} {index + 1}/{batches.Length}. " +
                    $"Delivery ref: {StableId(seed + "/message")}. " +
                    "Collect individual items as stash space allows. Remaining attachments stay here for 10 years. " +
                    "Deleting this message discards its uncollected items. This is your saved prize, not a new roll.",
                Items = new MessageItems { Stash = stash, Data = items },
                HasRewards = true, RewardCollected = false, MaxStorageTime = StorageSeconds
            });
        }
        return result;
    }

    private static string DeliveryLabel(string? name)
    {
        // Display-only metadata never enters the deterministic identity/witness.
        if (string.IsNullOrWhiteSpace(name)) return "reward delivery";
        var clean = new string(name.Where(c => !char.IsControl(c) && c is not ('<' or '>')).Take(120).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "reward delivery" : clean.Trim() + " — delivery";
    }

    private void EnsureIdsAvailable(OpeningContext context, ManifestClaimPreparedPayload prepared)
    {
        var profile = RequireProfile(context);
        var messages = CreateMessages(context.ProfileId, prepared);
        var ids = prepared.ExactItemIds.Concat(messages.Select(m => m.Id))
            .Concat(messages.Select(m => m.Items!.Stash!.Value)).ToArray();
        if (ids.Distinct().Count() != ids.Length)
            throw new InvalidOperationException("Reward mail identifiers collide.");
        var occupied = AllItems(profile).Select(i => i.Id).Concat(AllMessages(profile).Select(m => m.Id))
            .Concat(AllMessages(profile).Where(m => m.Items?.Stash is not null).Select(m => m.Items!.Stash!.Value)).ToHashSet();
        if (context.Response.ProfileChanges?.GetValueOrDefault(context.ProfileId)?.Items is { } changes)
            occupied.UnionWith((changes.NewItems ?? []).Concat(changes.ChangedItems ?? []).Select(i => i.Id)
                .Concat((changes.DeletedItems ?? []).Select(i => i.Id)));
        if (ids.Any(occupied.Contains)) throw new InvalidOperationException("Reward mail is already present or its item IDs are occupied.");
    }

    private SptProfile RequireProfile(OpeningContext context)
    {
        var profile = _profile(context.ProfileId) ?? throw new InvalidOperationException("Reward recipient is unavailable.");
        if (!ReferenceEquals(profile.CharacterData?.PmcData, context.PmcData))
            throw new InvalidOperationException("Reward recipient does not match the authenticated profile.");
        return profile;
    }

    private static IEnumerable<Message> AllMessages(SptProfile profile) =>
        (profile.DialogueRecords?.Values ?? Enumerable.Empty<Dialogue>()).SelectMany(d => d.Messages ?? []);
    private static IEnumerable<Item> AllItems(SptProfile profile) =>
        (profile.CharacterData?.PmcData?.Inventory?.Items ?? []).Concat(AllMessages(profile).SelectMany(m => m.Items?.Data ?? []));
    private static MongoId StableId(string seed) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..24].ToLowerInvariant());
    private static ManifestClaimPreparedPayload Applied(ManifestClaimPreparedPayload p) =>
        new(p.Items, p.RootIds, true, p.PreparedAtUtc, p.CommitGeneration, p.CommitPredecessorHash, p.Delivery);
    private static void RequireCleanResponse(OpeningContext context)
    {
        if (context.Response.Warnings?.Count > 0) throw new InvalidOperationException("Cannot deliver rewards on a failed item event.");
    }
    private sealed record MailCheckpoint(InventoryCheckpoint Inventory, bool HadDialogues,
        Dialogue? Dialogue, List<Message>? Messages, int? New, int? AttachmentsNew) : InventoryCheckpoint;
}
