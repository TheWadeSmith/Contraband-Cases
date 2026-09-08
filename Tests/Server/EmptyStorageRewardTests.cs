using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class EmptyStorageRewardTests
{
    private const string ItemRoot = "54009119af1c881c07000029";
    private const string Compound = "566162e44bdc2d3f298b4573";
    private const string Storage = "5795f317245977243854e041";
    private const string InjectorCase = "619cbf7d23893217ec30b689";
    private const string OtherItem = "100000000000000000000011";

    [Theory]
    [InlineData(InjectorCase)]
    [InlineData("5d235bb686f77443f4331278")]
    [InlineData("590c60fc86f77412b13fddcf")]
    [InlineData("59fafd4b86f7745ca07e1232")]
    [InlineData("5aafbcd986f7745e590fff23")]
    [InlineData("59fb023c86f7746d0d4b423c")]
    [InlineData("5b6d9ce188a4501afc1b2b25")]
    [InlineData("59fb042886f7746c5005a7b2")]
    [InlineData("5c0a840b86f7742ffa4f2482")]
    [InlineData("5e2af55f86f7746d4159f07c")]
    [InlineData("67600929bd0a0549d70993f6")]
    public void Allowlisted_empty_storage_items_are_individual_prizes_not_wrappers(string id)
    {
        var templates = Templates(id);
        var forest = RewardForest.Create([new RewardForestNode("root", "root", id, null, null, null, 1)]);
        CargoTemplateRules.ValidateForestTopologyAndEligibility(forest, templates.GetValueOrDefault, null, new Dictionary<string, TemplateItem>());
    }

    [Theory]
    [InlineData("5448bf274bdc2dfc2f8b456a")]
    [InlineData("567583764bdc2d98058b456e")]
    [InlineData("55d720f24bdc2d88028b456d")]
    [InlineData("543be5dd4bdc2deb348b4569")]
    public void Allowlisted_identity_cannot_bypass_altered_unsafe_ancestry(string forbidden)
    {
        var templates = Templates(InjectorCase);
        templates[Storage].Parent = forbidden;
        templates[forbidden] = new TemplateItem { Id = forbidden, Parent = Compound, Type = "Node" };
        Reject(RewardForest.Create([new RewardForestNode("root", "root", InjectorCase, null, null, null, 1)]), templates);
    }

    [Fact]
    public void Other_storage_and_secure_containers_remain_forbidden()
    {
        var templates = Templates(OtherItem);
        Reject(RewardForest.Create([new RewardForestNode("root", "root", OtherItem, null, null, null, 1)]), templates);
    }

    [Fact]
    public void Allowed_storage_cannot_hold_contents_or_be_nested_inside_another_reward()
    {
        var templates = Templates(InjectorCase);
        templates[OtherItem] = Item(OtherItem, Compound);
        Reject(RewardForest.Create([
            new RewardForestNode("root", "root", InjectorCase, null, null, null, 1),
            new RewardForestNode("root", "root/child", OtherItem, "root", "main", new CanonicalInternalLocation(0, 0, CanonicalRotation.Horizontal), 1)]), templates);
        Reject(RewardForest.Create([
            new RewardForestNode("root", "root", OtherItem, null, null, null, 1),
            new RewardForestNode("root", "root/child", InjectorCase, "root", "main", new CanonicalInternalLocation(0, 0, CanonicalRotation.Horizontal), 1)]), templates);
    }

    [Fact]
    public void Allowed_storage_is_singleton_and_cannot_be_a_quest_item_or_currency()
    {
        var templates = Templates(InjectorCase);
        Reject(RewardForest.Create([new RewardForestNode("root", "root", InjectorCase, null, null, null, 2)]), templates);
        var forest = RewardForest.Create([new RewardForestNode("root", "root", InjectorCase, null, null, null, 1)]);
        templates[InjectorCase].Properties!.QuestItem = true;
        Reject(forest, templates);
        templates[InjectorCase].Properties!.QuestItem = false;
        templates[InjectorCase].Properties!.IsRagfairCurrency = true;
        Reject(forest, templates);
    }

    private static void Reject(RewardForest forest, Dictionary<string, TemplateItem> templates) =>
        Assert.Throws<CargoCatalogValidationException>(() => CargoTemplateRules.ValidateForestTopologyAndEligibility(
            forest, templates.GetValueOrDefault, null, new Dictionary<string, TemplateItem>()));

    private static TemplateItem Item(string id, string parent) => new()
    {
        Id = id, Parent = parent, Name = id, Type = "Item",
        Properties = new TemplateItemProperties { StackMaxSize = 1, Width = 1, Height = 1,
            Prefab = new Prefab { Path = "test/storage.bundle" }, Slots = [], Grids = [] }
    };

    private static Dictionary<string, TemplateItem> Templates(string id) => new()
    {
        [id] = Item(id, Storage),
        [Storage] = new() { Id = Storage, Parent = Compound, Type = "Node" },
        [Compound] = new() { Id = Compound, Parent = ItemRoot, Type = "Node" }
    };
}
