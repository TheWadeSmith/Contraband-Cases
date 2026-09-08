using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class RetiredRewardTests
{
    private const string Good = "900000000000000000000001";
    private const string Missing = "900000000000000000000002";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retired_recipe_never_rolls_but_remains_exactly_resolvable_when_valid(bool oldItemInstalled)
    {
        var pack = Load();
        TemplateItem? Find(string id) => id == Good || oldItemInstalled
            ? new TemplateItem { Id = id, Parent = "54009119af1c881c07000029", Name = id, Type = "Item", Properties = new TemplateItemProperties
                { StackMaxSize = 1, Width = 1, Height = 1, Prefab = new Prefab { Path = "test.bundle" } } }
            : null;
        var snapshot = new CargoCatalogSnapshotBuilder(
            new CargoLotResolver(new CargoLotResolverDependencies(Find, _ => null)),
            new CargoLotEvaluator(Find, _ => 100_000),
            new CargoPackRequirementValidator(Find, _ => null).Validate).Build(pack);

        Assert.Equal("new", Assert.Single(snapshot.FreshOpeningLots).Identity.LotId);
        Assert.Equal(oldItemInstalled ? 2 : 1, snapshot.Lots.Count);
        Assert.Equal(oldItemInstalled ? 0 : 1, snapshot.UnavailableRetiredLots.Count);
        if (oldItemInstalled)
        {
            var old = Assert.Single(snapshot.Lots, lot => lot.Identity.LotId == "old");
            Assert.Same(old, snapshot.ResolveExact(old.Evaluation.Grade, old.Identity, old.Forest, old.Fingerprint));
        }
    }

    [Fact]
    public void Missing_active_item_still_rejects_whole_pack()
    {
        Assert.Throws<CargoCatalogValidationException>(() =>
            new CargoPackRequirementValidator(_ => null, _ => null).Validate(Load()));
    }

    [Fact]
    public void Retirement_cannot_name_nonexistent_or_duplicate_lots()
    {
        Assert.Throws<CargoCatalogValidationException>(() => Load("\"absent\""));
        Assert.Throws<CargoCatalogValidationException>(() => Load("\"old\",\"old\""));
    }

    private static CargoLotPack Load(string retired = "\"old\"") => new JsonRewardPackLoader().Load($$"""
        {"schemaVersion":1,"providerId":"test","packVersion":"1.0.0","displayLabel":"Test","providerWeight":1,
         "requiredTemplateIds":["{{Good}}","{{Missing}}"],"requiredPresetIds":[],"requiredBundleKeys":[],
         "retiredLotIds":[{{retired}}],"lots":[
          {"lotId":"old","displayName":"Old","purpose":"Old paid reward","familyId":"operator","trackId":"test",
           "anchorTemplateId":"{{Missing}}","weight":1,"usePath":{"kind":"raidRole","roleId":"medic"},
           "recipe":[{"kind":"template","templateId":"{{Missing}}","instanceCount":1,"stackCountPerInstance":1}]},
          {"lotId":"new","displayName":"New","purpose":"New reward","familyId":"operator","trackId":"test-v2",
           "anchorTemplateId":"{{Good}}","weight":1,"usePath":{"kind":"raidRole","roleId":"medic"},
           "recipe":[{"kind":"template","templateId":"{{Good}}","instanceCount":1,"stackCountPerInstance":1}]}]}
        """);
}
