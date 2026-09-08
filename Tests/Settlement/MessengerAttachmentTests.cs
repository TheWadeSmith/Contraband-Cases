using System.Text.Json;
using ContrabandCases.Server.Settlement;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils.Json;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class MessengerAttachmentTests
{
    [Fact]
    public void Historical_48_item_prize_is_six_collectible_native_messages_and_survives_partial_collection_reload()
    {
        var profileId = new MongoId();
        var items = Enumerable.Range(0, 48).Select(_ => new Item
        {
            Id = new MongoId(), Template = "544fb3f34bdc2d03748b456a", Upd = new() { StackObjectsCount = 1 }
        }).ToArray();
        var payload = new ManifestClaimPreparedPayload(items, items.Select(i => i.Id), true,
            DateTimeOffset.UtcNow, delivery: ClaimDeliveryKind.Messenger);
        var messages = SptManifestRewardDelivery.CreateMessages(profileId, payload).ToList();
        Assert.Equal(6, messages.Count);
        Assert.All(messages, m =>
        {
            Assert.Equal(8, m.Items!.Data!.Count);
            Assert.True(m.HasRewards);
            Assert.False(m.RewardCollected);
            Assert.All(m.Items.Data, i => { Assert.Equal(m.Items.Stash.ToString(), i.ParentId); Assert.Equal("main", i.SlotId); Assert.Null(i.Location); });
        });
        Assert.Equal(items.Select(i => i.Id), messages.SelectMany(m => m.Items!.Data!).Select(i => i.Id));
        var profile = new SptProfile { ProfileInfo = new() { ProfileId = profileId },
            DialogueRecords = new() { [SptManifestRewardDelivery.SenderId] = new()
            { Id = SptManifestRewardDelivery.SenderId, Messages = messages, New = 6, AttachmentsNew = 6 } } };
        var helper = NativeHelper(profile);
        var message = messages[0];
        for (var i = 0; i < 3; i++)
        {
            var contents = helper.GetMessageItemContents(message.Id, profileId, items[i].Id)!;
            // Native InventoryHelper moves this returned tree, removing only those
            // IDs from mail; exercise native tree lookup and collection flags here.
            var selected = contents.GetItemWithChildren(items[i].Id);
            Assert.Single(selected);
            contents.RemoveAll(item => selected.Any(taken => taken.Id == item.Id));
        }
        Assert.True(message.HasRewards);
        Assert.False(message.RewardCollected);
        Assert.Equal(5, message.Items!.Data!.Count);
        var options = new JsonSerializerOptions();
        foreach (var converter in new SptJsonConverterRegistrator().GetJsonConverters()) options.Converters.Add(converter);
        var restored = JsonSerializer.Deserialize<SptProfile>(JsonSerializer.Serialize(profile, options), options)!;
        var remaining = restored.DialogueRecords![SptManifestRewardDelivery.SenderId].Messages!;
        Assert.Equal(45, remaining.Sum(m => m.Items!.Data!.Count));
        Assert.DoesNotContain(remaining.SelectMany(m => m.Items!.Data!), i => items.Take(3).Any(t => t.Id == i.Id));
        helper = NativeHelper(restored);
        foreach (var item in remaining[0].Items!.Data!.ToArray())
        {
            var contents = helper.GetMessageItemContents(message.Id, profileId, item.Id)!;
            contents.RemoveAll(candidate => candidate.Id == item.Id);
        }
        Assert.True(remaining[0].RewardCollected);
        Assert.False(remaining[0].HasRewards);
        Assert.Equal(5, restored.DialogueRecords[SptManifestRewardDelivery.SenderId].AttachmentsNew);
        Assert.Equal(messages.Select(m => m.Id), SptManifestRewardDelivery.CreateMessages(profileId, payload).Select(m => m.Id));
        Assert.Empty(messages.Select(m => m.Id).Intersect(SptManifestRewardDelivery.CreateMessages(new MongoId(), payload).Select(m => m.Id)));
    }

    [Fact]
    public void Equipped_reward_tree_stays_intact_in_its_message()
    {
        var root = new Item { Id = new MongoId(), Template = new MongoId() };
        var child = new Item { Id = new MongoId(), Template = new MongoId(), ParentId = root.Id, SlotId = "mod_scope", Upd = new() { StackObjectsCount = 1 } };
        var payload = new ManifestClaimPreparedPayload([root, child], [root.Id], false, DateTimeOffset.UtcNow,
            delivery: ClaimDeliveryKind.Messenger);
        var message = Assert.Single(SptManifestRewardDelivery.CreateMessages(new MongoId(), payload));
        var tree = message.Items!.Data!.GetItemWithChildren(root.Id);
        Assert.Equal(2, tree.Count);
        Assert.Equal(root.Id.ToString(), tree.Single(i => i.Id == child.Id).ParentId);
        Assert.Equal("mod_scope", tree.Single(i => i.Id == child.Id).SlotId);
    }

    private static DialogueHelper NativeHelper(SptProfile profile)
    {
        // These native helpers are exercised in memory only. No Load/Save method,
        // listener, filesystem service or server startup is invoked.
        var save = new SaveServer(null!, [], null!, null!, null!, null!, null!, null!);
        save.AddProfile(profile);
        var helper = new ProfileHelper(null!, null!, null!, null!, save, null!, null!, null!, null!);
        return new DialogueHelper(null!, helper);
    }
}
