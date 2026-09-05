using ContrabandCases.Shared.Catalog;
using Xunit;

namespace ContrabandCases.Tests.Catalog;

public sealed class CargoLotDefinitionTests
{
    [Fact]
    public void Constructor_preserves_a_complete_purpose_driven_lot()
    {
        var lines = new List<RewardRecipeLine>
        {
            new TemplateLine("medical-template", 2, 3),
            new PresetLine("rig-preset")
        };

        var lot = new CargoLotDefinition(
            "sjx.combat-chemistry",
            "1.0.2",
            "trauma-response",
            "Trauma Response",
            "Stabilizes a squad after a difficult firefight.",
            new FamilyId("field-supply"),
            new TrackId("medical"),
            "medical-template",
            0.75d,
            new RaidRole("trauma-response"),
            lines);

        lines.Clear();

        Assert.Equal("sjx.combat-chemistry", lot.ProviderId);
        Assert.Equal("1.0.2", lot.PackVersion);
        Assert.Equal("trauma-response", lot.LotId);
        Assert.Equal("Trauma Response", lot.DisplayName);
        Assert.Equal("Stabilizes a squad after a difficult firefight.", lot.Purpose);
        Assert.Equal(new FamilyId("field-supply"), lot.FamilyId);
        Assert.Equal(new TrackId("medical"), lot.TrackId);
        Assert.Equal("medical-template", lot.AnchorTemplateId);
        Assert.Equal(0.75d, lot.Weight);
        Assert.IsType<RaidRole>(lot.UsePath);
        Assert.Equal(2, lot.RecipeLines.Count);
        Assert.Equal(2, Assert.IsType<TemplateLine>(lot.RecipeLines[0]).InstanceCount);
        Assert.Equal("rig-preset", Assert.IsType<PresetLine>(lot.RecipeLines[1]).PresetId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Semantic_ids_reject_blank_values(string value)
    {
        Assert.Throws<CargoCatalogValidationException>(() => new FamilyId(value));
        Assert.Throws<CargoCatalogValidationException>(() => new TrackId(value));
        Assert.Throws<CargoCatalogValidationException>(() => new TemplateLine(value, 1, 1));
        Assert.Throws<CargoCatalogValidationException>(() => new PresetLine(value));
        Assert.Throws<CargoCatalogValidationException>(() => new RaidRole(value));
        Assert.Throws<CargoCatalogValidationException>(() => new Craft(value));
    }

    [Fact]
    public void Closed_use_paths_preserve_only_their_required_identity()
    {
        var raidRole = new RaidRole("night-runner");
        var collection = new Collection("binder-template", "kanto-151");
        var craft = new Craft("production-id");
        var barter = new Barter("mechanic", "assort-id");

        Assert.Equal("night-runner", raidRole.RoleId);
        Assert.Equal("binder-template", collection.ContainerTemplateId);
        Assert.Equal("kanto-151", collection.CollectionId);
        Assert.Equal("production-id", craft.ProductionId);
        Assert.Equal("mechanic", barter.TraderId);
        Assert.Equal("assort-id", barter.AssortId);
        Assert.Throws<CargoCatalogValidationException>(() => new Collection("binder", " "));
        Assert.Throws<CargoCatalogValidationException>(() => new Barter(" ", "assort"));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void Template_line_rejects_non_positive_counts(int instanceCount, int stackCount)
    {
        Assert.Throws<CargoCatalogValidationException>(() =>
            new TemplateLine("template", instanceCount, stackCount));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-0.1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Lot_rejects_non_positive_or_non_finite_weight(double weight)
    {
        Assert.Throws<CargoCatalogValidationException>(() => ValidLot(weight));
    }

    [Fact]
    public void Lot_rejects_missing_identity_purpose_or_recipe()
    {
        Assert.Throws<CargoCatalogValidationException>(() => ValidLot(providerId: " "));
        Assert.Throws<CargoCatalogValidationException>(() => ValidLot(packVersion: " "));
        Assert.Throws<CargoCatalogValidationException>(() => ValidLot(lotId: " "));
        Assert.Throws<CargoCatalogValidationException>(() => ValidLot(displayName: " "));
        Assert.Throws<CargoCatalogValidationException>(() => ValidLot(purpose: " "));
        Assert.Throws<CargoCatalogValidationException>(() => ValidLot(anchorTemplateId: " "));
        Assert.Throws<CargoCatalogValidationException>(() => ValidLot(recipeLines: []));
        Assert.Throws<CargoCatalogValidationException>(() => ValidLot(recipeLines: new RewardRecipeLine[] { null! }));
    }

    [Fact]
    public void Identity_snapshot_freezes_definition_and_fingerprint_identity()
    {
        var definition = ValidLot();
        var forest = RewardForest.Create([
            new RewardForestNode("root", "root", "anchor", null, null, null, 1)
        ]);
        var fingerprint = RewardForestFingerprintV2.Compute(definition.ProviderId, definition.LotId, forest);

        var snapshot = CargoLotIdentitySnapshot.Capture(definition, fingerprint);

        Assert.Equal(definition.ProviderId, snapshot.ProviderId);
        Assert.Equal(definition.PackVersion, snapshot.PackVersion);
        Assert.Equal(definition.LotId, snapshot.LotId);
        Assert.Equal(definition.DisplayName, snapshot.DisplayName);
        Assert.Equal(definition.Purpose, snapshot.Purpose);
        Assert.Equal(definition.FamilyId, snapshot.FamilyId);
        Assert.Equal(definition.TrackId, snapshot.TrackId);
        Assert.Equal(definition.AnchorTemplateId, snapshot.AnchorTemplateId);
        Assert.Same(definition.UsePath, snapshot.UsePath);
        Assert.Equal(fingerprint, snapshot.Fingerprint);
    }

    private static CargoLotDefinition ValidLot(
        double weight = 1d,
        string providerId = "core",
        string packVersion = "0.3.0",
        string lotId = "night-runner",
        string displayName = "Night Runner",
        string purpose = "Outfits one night raid.",
        string anchorTemplateId = "anchor",
        IEnumerable<RewardRecipeLine>? recipeLines = null) =>
        new(
            providerId,
            packVersion,
            lotId,
            displayName,
            purpose,
            new FamilyId("operator"),
            new TrackId("night-operations"),
            anchorTemplateId,
            weight,
            new RaidRole("night-runner"),
            recipeLines ?? [new PresetLine("night-runner-preset")]);
}
