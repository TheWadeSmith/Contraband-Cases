using ContrabandCases.Server.Configuration;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class TestingInventoryGrantTests
{
    [Theory]
    [InlineData(TestingCrateType.TrueRandom, ModConstants.CaseTemplateId)]
    [InlineData(TestingCrateType.OperationsCase, CaseContracts.Operations)]
    [InlineData(TestingCrateType.RelicsCase, CaseContracts.Relics)]
    [InlineData(TestingCrateType.BlackSiteCase, CaseContracts.BlackSite)]
    [InlineData(TestingCrateType.CashCache, CaseContracts.CashCache)]
    public void Selected_case_grants_exact_templates_with_universal_keys(TestingCrateType type, string template)
    {
        Assert.Equal(template, TestingCrateTypeCodec.CaseTemplate(type));
        var trees = SptOpeningInventory.CreateTestingGrantItems(2, 3, [], template);
        Assert.All(trees.Take(2), tree => Assert.Equal(template, tree[0].Template.ToString()));
        Assert.All(trees.Skip(2), tree => Assert.Equal(ModConstants.KeyTemplateId, tree[0].Template.ToString()));
        Assert.Equal(5, trees.SelectMany(tree => tree).Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public void Policy_accepts_bounded_mixed_grants_and_rejects_empty_or_excessive_grants()
    {
        TestingInventoryGrantPolicy.Validate(5, 10);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TestingInventoryGrantPolicy.Validate(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TestingInventoryGrantPolicy.Validate(11, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TestingInventoryGrantPolicy.Validate(1, 40));
    }

    [Fact]
    public void Prepared_grant_items_have_unique_ids_and_exact_templates()
    {
        var occupied = (MongoId)"aaaaaaaaaaaaaaaaaaaaaaaa";

        var trees = SptOpeningInventory.CreateTestingGrantItems(2, 3, [occupied]);
        var items = trees.SelectMany(tree => tree).ToArray();

        Assert.Equal(5, trees.Count);
        Assert.All(trees, tree => Assert.Single(tree));
        Assert.Equal(5, items.Select(item => item.Id).Distinct().Count());
        Assert.DoesNotContain(items, item => item.Id == occupied);
        Assert.Equal(2, items.Count(item => item.Template == (MongoId)ModConstants.CaseTemplateId));
        Assert.Equal(3, items.Count(item => item.Template == (MongoId)ModConstants.KeyTemplateId));
        Assert.All(items, item =>
        {
            Assert.Equal(1d, item.Upd!.StackObjectsCount);
            Assert.False(item.Upd.SpawnedInSession);
        });
    }

    [Fact]
    public void Server_gate_fails_closed_until_initialized_and_enabled()
    {
        var profileId = (MongoId)"aaaaaaaaaaaaaaaaaaaaaaaa";
        var disabled = new TestingInventoryGrantGate();
        Assert.Throws<InvalidOperationException>(disabled.RequireEnabled);
        Assert.Throws<InvalidOperationException>(() => disabled.RequireGrantAllowed(profileId));
        disabled.Initialize(false);
        Assert.Throws<InvalidOperationException>(disabled.RequireEnabled);
        Assert.Throws<InvalidOperationException>(() => disabled.RequireGrantAllowed(profileId));

        var enabled = new TestingInventoryGrantGate();
        enabled.Initialize(true);
        enabled.RequireEnabled();
        enabled.RequireGrantAllowed(profileId);
        Assert.Throws<InvalidOperationException>(() => enabled.Initialize(true));
    }

    [Fact]
    public void Server_gate_blocks_only_profile_with_uncertain_commit()
    {
        var uncertainProfile = (MongoId)"aaaaaaaaaaaaaaaaaaaaaaaa";
        var healthyProfile = (MongoId)"bbbbbbbbbbbbbbbbbbbbbbbb";
        var gate = new TestingInventoryGrantGate();
        gate.Initialize(true);

        gate.MarkCommitUncertain(uncertainProfile);

        Assert.Throws<InvalidOperationException>(() => gate.RequireGrantAllowed(uncertainProfile));
        gate.RequireGrantAllowed(healthyProfile);
    }

    [Fact]
    public void Server_gate_clears_uncertain_commit_state_on_new_server_instance()
    {
        var profileId = (MongoId)"aaaaaaaaaaaaaaaaaaaaaaaa";
        var priorServerGate = new TestingInventoryGrantGate();
        priorServerGate.Initialize(true);
        priorServerGate.MarkCommitUncertain(profileId);

        var restartedServerGate = new TestingInventoryGrantGate();
        restartedServerGate.Initialize(true);

        restartedServerGate.RequireGrantAllowed(profileId);
    }

    [Fact]
    public void Prepared_grant_trees_place_every_case_before_every_key_so_case_ids_can_be_extracted_by_position()
    {
        // ApplyTestingInventoryGrant relies on exactly this ordering to slice
        // out the created case IDs (the ones a forced crate tag can attach
        // to) without needing to inspect item templates again.
        var occupied = (MongoId)"aaaaaaaaaaaaaaaaaaaaaaaa";
        const int caseCount = 4;
        const int keyCount = 6;

        var trees = SptOpeningInventory.CreateTestingGrantItems(caseCount, keyCount, [occupied]);

        Assert.Equal(caseCount + keyCount, trees.Count);
        Assert.All(
            trees.Take(caseCount),
            tree => Assert.Equal((MongoId)ModConstants.CaseTemplateId, tree[0].Template));
        Assert.All(
            trees.Skip(caseCount),
            tree => Assert.Equal((MongoId)ModConstants.KeyTemplateId, tree[0].Template));
    }

    [Fact]
    public void Crate_type_wire_codec_round_trips_every_value_and_fails_safe_on_garbage()
    {
        foreach (var crateType in Enum.GetValues<TestingCrateType>())
        {
            var wireValue = TestingCrateTypeCodec.ToWireValue(crateType);
            Assert.Equal(crateType, TestingCrateTypeCodec.Parse(wireValue));
        }

        Assert.Equal(TestingCrateType.TrueRandom, TestingCrateTypeCodec.Parse(null));
        Assert.Equal(TestingCrateType.TrueRandom, TestingCrateTypeCodec.Parse(string.Empty));
        Assert.Equal(TestingCrateType.TrueRandom, TestingCrateTypeCodec.Parse("not-a-real-pool"));
        Assert.Equal(TestingCrateType.TrueRandom, TestingCrateTypeCodec.Parse("VAULT"));
    }

    [Fact]
    public void Grant_request_validation_accepts_any_crate_type_string_including_missing_or_garbage()
    {
        var request = new TestingInventoryGrantRequestData
        {
            Action = ModConstants.TestingInventoryGrantAction,
            CaseCount = 1,
            KeyCount = 1,
            CrateType = null
        };

        ContrabandCaseRouter.ValidateTestingInventoryGrantRequest(
            ModConstants.TestingInventoryGrantAction,
            request);

        request.CrateType = "not-a-real-pool";
        ContrabandCaseRouter.ValidateTestingInventoryGrantRequest(
            ModConstants.TestingInventoryGrantAction,
            request);
        Assert.Equal(TestingCrateType.TrueRandom, TestingCrateTypeCodec.Parse(request.CrateType));

        request.CrateType = "mega";
        ContrabandCaseRouter.ValidateTestingInventoryGrantRequest(
            ModConstants.TestingInventoryGrantAction,
            request);
        Assert.Equal(TestingCrateType.Mega, TestingCrateTypeCodec.Parse(request.CrateType));
    }

    [Fact]
    public void Forced_crate_registry_ignores_true_random_and_fails_safe_on_unknown_cases()
    {
        var registry = new TestingForcedCrateRegistry();
        var caseId = (MongoId)"aaaaaaaaaaaaaaaaaaaaaaaa";

        Assert.False(registry.TryGetForcedCrate(caseId, out _));

        registry.SetForcedCrate(caseId, TestingCrateType.TrueRandom);
        Assert.False(registry.TryGetForcedCrate(caseId, out _));

        registry.SetForcedCrate(caseId, TestingCrateType.Mega);
        Assert.True(registry.TryGetForcedCrate(caseId, out var crateType));
        Assert.Equal(TestingCrateType.Mega, crateType);

        // Setting TrueRandom afterward clears a prior forced tag rather than
        // leaving it stuck, and an unrelated case ID never sees another
        // profile's tag.
        registry.SetForcedCrate(caseId, TestingCrateType.TrueRandom);
        Assert.False(registry.TryGetForcedCrate(caseId, out _));
        Assert.False(registry.TryGetForcedCrate((MongoId)"bbbbbbbbbbbbbbbbbbbbbbbb", out _));
    }

    [Fact]
    public void Forced_crate_registry_evicts_under_pressure_instead_of_growing_without_bound()
    {
        var registry = new TestingForcedCrateRegistry();

        for (var index = 0; index < TestingForcedCrateRegistry.MaximumTrackedCases + 16; index++)
        {
            var caseId = (MongoId)(index + 1).ToString("x24");
            registry.SetForcedCrate(caseId, TestingCrateType.Scrap);
        }

        Assert.True(registry.Count <= TestingForcedCrateRegistry.MaximumTrackedCases);
    }
}
