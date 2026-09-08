using ContrabandCases.Server.Settlement;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Profile;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class MessengerSafetyTests
{
    [Theory]
    [InlineData("inventory")]
    [InlineData("mail-item")]
    [InlineData("message")]
    [InlineData("stash")]
    [InlineData("response-new")]
    [InlineData("response-changed")]
    [InlineData("response-deleted")]
    public void Mail_rejects_occupied_identifiers_without_mutating_any_dialogue(string collision)
    {
        var (context, profile, delivery, prepared) = Fixture();
        var expected = Assert.Single(SptManifestRewardDelivery.CreateMessages(context.ProfileId, prepared));
        var item = prepared.Items.Single();
        var changes = SptResponseChanges.GetOrCreate(context.Response, context.ProfileId);
        var occupied = new Message { Id = new MongoId(), Items = new() { Stash = new MongoId(), Data = [] } };
        profile.DialogueRecords = new() { [SptManifestRewardDelivery.SenderId] = new()
        { Id = SptManifestRewardDelivery.SenderId, Messages = [occupied], New = 1, AttachmentsNew = 0 } };
        switch (collision)
        {
            case "inventory": context.PmcData.Inventory!.Items!.Add(item); break;
            case "mail-item": occupied.Items.Data!.Add(item); break;
            case "message": occupied.Id = expected.Id; break;
            case "stash": occupied.Items.Stash = expected.Items!.Stash; break;
            case "response-new": changes.NewItems!.Add(item); break;
            case "response-changed": changes.ChangedItems!.Add(item); break;
            case "response-deleted": changes.DeletedItems!.Add(new DeletedItem { Id = item.Id }); break;
        }
        Assert.Throws<InvalidOperationException>(() => delivery.ApplyPreparedClaim(context, prepared));
        Assert.Same(occupied, Assert.Single(profile.DialogueRecords[SptManifestRewardDelivery.SenderId].Messages!));
        Assert.Equal(1, profile.DialogueRecords[SptManifestRewardDelivery.SenderId].New);
    }

    [Fact]
    public void Mail_rejects_an_authenticated_profile_reference_mismatch()
    {
        var (context, profile, delivery, prepared) = Fixture();
        profile.CharacterData!.PmcData = new PmcData();
        Assert.Throws<InvalidOperationException>(() => delivery.TryPrepareClaim(context, prepared.Items,
            prepared.RootIds, prepared.PreparedAtUtc, out _));
        Assert.Throws<InvalidOperationException>(() => delivery.ApplyPreparedClaim(context, prepared));
        Assert.Null(profile.DialogueRecords);
    }

    private static (OpeningContext, SptProfile, SptManifestRewardDelivery, ManifestClaimPreparedPayload) Fixture()
    {
        var pmc = new PmcData { Inventory = new() { Items = [] } };
        var context = new OpeningContext(pmc, new ItemEventRouterResponse(), new MongoId());
        var profile = new SptProfile { CharacterData = new() { PmcData = pmc } };
        var delivery = new SptManifestRewardDelivery(null!, _ => profile, (_, _) => Task.CompletedTask, _ => { });
        var item = new Item { Id = new MongoId(), Template = new MongoId(), Upd = new() { StackObjectsCount = 1 } };
        var prepared = new ManifestClaimPreparedPayload([item], [item.Id], false, DateTimeOffset.UnixEpoch,
            delivery: ClaimDeliveryKind.Messenger);
        return (context, profile, delivery, prepared);
    }
}
