using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class OptionalRewardPackTests
{
    private const string AmbiguousSjxHydraTemplateId = "683361256ef8a3ee9b04d226";

    [Fact]
    public void Audited_optional_packs_load_with_exact_values_and_complete_relay_tracks()
    {
        var loader = new JsonRewardPackLoader();

        foreach (var expected in Expectations)
        {
            var pack = loader.LoadFile(RewardPackPath(expected.FileName));

            Assert.Equal(expected.ProviderId, pack.ProviderId);
            Assert.Equal(expected.PackVersion, pack.PackVersion);
            Assert.Equal(expected.DisplayLabel, pack.DisplayLabel);
            Assert.Equal(expected.ProviderWeight, pack.ProviderWeight, 12);
            Assert.All(pack.Lots.SelectMany(lot => lot.RecipeLines).OfType<PresetLine>(),
                line => Assert.Contains(line.PresetId, pack.RequiredPresetIds));
            Assert.Empty(pack.RequiredBundleKeys);
            Assert.Equal(24, pack.Lots.Count);

            var expectedRequirements = expected.UnitValues.Keys
                .Append(expected.ContainerTemplateId)
                .Where(templateId => templateId is not null)
                .Cast<string>()
                .OrderBy(templateId => templateId, StringComparer.Ordinal);
            Assert.All(expectedRequirements, id => Assert.Contains(id, pack.RequiredTemplateIds));
            Assert.All(pack.Lots.SelectMany(lot => lot.RecipeLines).OfType<TemplateLine>(),
                line => Assert.Contains(line.TemplateId, pack.RequiredTemplateIds));
            Assert.Equal(
                expected.LotValues.Keys.OrderBy(lotId => lotId, StringComparer.Ordinal),
                pack.Lots.Where(lot => ShipmentEconomy.Generation(lot.LotId) == 0)
                    .Select(lot => lot.LotId).OrderBy(lotId => lotId, StringComparer.Ordinal));

            var evaluated = pack.Lots.Where(lot => ShipmentEconomy.Generation(lot.LotId) == 0)
                .Select(lot => EvaluateLot(lot, expected, pack.RequiredTemplateIds))
                .ToArray();

            // The 40k Common split preserves the pack-authored two-lot
            // Uncommon-to-Legendary Relay ladders. These themed packs do not
            // contain a Common lot; Common is supplied by the core catalog.
            Assert.Equal(0, evaluated.Count(lot => lot.Grade == RewardRarity.ScavGrade));
            Assert.Equal(2, evaluated.Count(lot => lot.Grade == RewardRarity.Uncommon));
            Assert.Equal(2, evaluated.Count(lot => lot.Grade == RewardRarity.Contractor));
            Assert.Equal(2, evaluated.Count(lot => lot.Grade == RewardRarity.Restricted));
            Assert.Equal(2, evaluated.Count(lot => lot.Grade == RewardRarity.BlackLabel));

            foreach (var current in evaluated.Where(lot => lot.Grade != RewardRarity.BlackLabel))
            {
                Assert.Contains(
                    evaluated,
                    candidate =>
                        candidate.Lot.LotId != current.Lot.LotId &&
                        candidate.Lot.TrackId.Equals(current.Lot.TrackId) &&
                        candidate.Grade == current.Grade);
                Assert.Contains(
                    evaluated,
                    candidate =>
                        candidate.Lot.TrackId.Equals(current.Lot.TrackId) &&
                        candidate.Grade == RelayRules.GetUpgradeRarity(current.Grade));
            }
        }
    }

    [Fact]
    public void Sjx_pack_excludes_ambiguous_hydra_and_retains_a_complete_relay_track()
    {
        var pack = new JsonRewardPackLoader().LoadFile(
            RewardPackPath("sjx.combat-chemistry.json"));

        Assert.Equal("sjx.combat-chemistry", pack.ProviderId);
        Assert.Equal("1.0.2", pack.PackVersion);
        Assert.Equal(20, pack.RequiredTemplateIds.Count);
        Assert.Equal(2, pack.RequiredPresetIds.Count);
        Assert.Equal(15, pack.Lots.Count);
        Assert.DoesNotContain(AmbiguousSjxHydraTemplateId, pack.RequiredTemplateIds);
        Assert.All(pack.Lots, lot => Assert.DoesNotContain(
            lot.RecipeLines.OfType<TemplateLine>(),
            line => string.Equals(
                line.TemplateId,
                AmbiguousSjxHydraTemplateId,
                StringComparison.Ordinal)));

        var recipeTemplateIds = pack.Lots
            .SelectMany(lot => lot.RecipeLines)
            .OfType<TemplateLine>().Select(line => line.TemplateId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(templateId => templateId, StringComparer.Ordinal);
        Assert.Equal(pack.RequiredTemplateIds, recipeTemplateIds);

        var rapidMobility = Assert.Single(
            pack.Lots,
            lot => string.Equals(lot.LotId, "rapid-mobility", StringComparison.Ordinal));
        Assert.Equal(
            [
                "68331b17cccc72d0db1f764b",
                "68337a2a20c7d7d769369919",
                "68337b7aff2eb46775159b1e"
            ],
            rapidMobility.RecipeLines
                .OfType<TemplateLine>()
                .Select(line => line.TemplateId));

        var covertSustainment = Assert.Single(
            pack.Lots,
            lot => string.Equals(lot.LotId, "covert-sustainment", StringComparison.Ordinal));
        Assert.False(covertSustainment.Purpose.Contains(
            "hydration",
            StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, covertSustainment.RecipeLines.Count);

        var evaluated = pack.Lots.Where(lot => ShipmentEconomy.Generation(lot.LotId) == 0)
            .Select(lot => new EvaluatedLot(
                lot,
                CargoGradeBands.Assign(EvaluateSjxLotValue(lot))))
            .ToArray();
        Assert.Equal(4, evaluated.Count(lot => lot.Grade == RewardRarity.Restricted));
        Assert.Single(evaluated, lot => lot.Grade == RewardRarity.BlackLabel);

        foreach (var current in evaluated.Where(lot => lot.Grade != RewardRarity.BlackLabel))
        {
            Assert.Contains(
                evaluated,
                candidate =>
                    candidate.Lot.LotId != current.Lot.LotId &&
                    candidate.Lot.TrackId.Equals(current.Lot.TrackId) &&
                    candidate.Grade == current.Grade);
            Assert.Contains(
                evaluated,
                candidate =>
                    candidate.Lot.TrackId.Equals(current.Lot.TrackId) &&
                    candidate.Grade == RelayRules.GetUpgradeRarity(current.Grade));
        }
    }

    private static long EvaluateSjxLotValue(CargoLotDefinition lot)
    {
        long value = 0;
        foreach (var recipeLine in lot.RecipeLines)
        {
            var template = Assert.IsType<TemplateLine>(recipeLine);
            Assert.True(
                SjxUnitValues.TryGetValue(template.TemplateId, out var unitValue),
                $"Lot '{lot.LotId}' contains unaudited SJX template '{template.TemplateId}'.");
            value = checked(value + checked(
                unitValue * template.InstanceCount * template.StackCountPerInstance));
        }

        return value;
    }

    private static EvaluatedLot EvaluateLot(
        CargoLotDefinition lot,
        PackExpectation expected,
        IReadOnlyCollection<string> requiredTemplateIds)
    {
        Assert.Equal("field-supply", lot.FamilyId.Value);
        Assert.Equal(expected.TrackId, lot.TrackId.Value);
        Assert.NotEmpty(lot.DisplayName);
        Assert.NotEmpty(lot.Purpose);

        long value = 0;
        foreach (var recipeLine in lot.RecipeLines)
        {
            var template = Assert.IsType<TemplateLine>(recipeLine);
            Assert.Equal(1, template.StackCountPerInstance);
            Assert.Contains(template.TemplateId, requiredTemplateIds);
            Assert.True(
                expected.UnitValues.TryGetValue(template.TemplateId, out var unitValue),
                $"Lot '{lot.LotId}' contains unaudited template '{template.TemplateId}'.");
            if (expected.ContainerTemplateId is not null)
            {
                Assert.Equal(1, template.InstanceCount);
            }

            value = checked(value + checked(
                unitValue * template.InstanceCount * template.StackCountPerInstance));
        }

        Assert.Contains(
            lot.AnchorTemplateId,
            lot.RecipeLines.OfType<TemplateLine>().Select(line => line.TemplateId));
        Assert.True(expected.LotValues.TryGetValue(lot.LotId, out var expectedValue));
        Assert.Equal(expectedValue, value);

        var grade = CargoGradeBands.Assign(value);
        Assert.Equal(ExpectedWeight(grade), lot.Weight, 12);
        AssertUsePath(lot.UsePath, expected);
        return new EvaluatedLot(lot, grade);
    }

    private static void AssertUsePath(UsePath usePath, PackExpectation expected)
    {
        if (expected.ContainerTemplateId is not null)
        {
            var collection = Assert.IsType<Collection>(usePath);
            Assert.Equal(expected.ContainerTemplateId, collection.ContainerTemplateId);
            Assert.Equal(expected.UseId, collection.CollectionId);
            return;
        }

        var raidRole = Assert.IsType<RaidRole>(usePath);
        Assert.Equal(expected.UseId, raidRole.RoleId);
    }

    private static double ExpectedWeight(RewardRarity grade) => grade switch
    {
        RewardRarity.Uncommon => 1d,
        RewardRarity.Contractor => 0.75d,
        RewardRarity.Restricted => 0.45d,
        RewardRarity.BlackLabel => 0.15d,
        _ => throw new ArgumentOutOfRangeException(nameof(grade))
    };

    private static string RewardPackPath(string fileName) =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../config/reward-packs",
            fileName));

    private sealed record EvaluatedLot(CargoLotDefinition Lot, RewardRarity Grade);

    private sealed record PackExpectation(
        string FileName,
        string ProviderId,
        string PackVersion,
        string DisplayLabel,
        double ProviderWeight,
        string TrackId,
        string? ContainerTemplateId,
        string UseId,
        IReadOnlyDictionary<string, long> UnitValues,
        IReadOnlyDictionary<string, long> LotValues);

    private static readonly PackExpectation[] Expectations =
    [
        new(
            "krackasourus.anime-cards.json",
            "krackasourus.anime-cards",
            "1.5.2",
            "Krackasourus Anime Cards",
            0.12d,
            "anime-cards",
            "6699b7fd0d1d25cf00072c49",
            "anime-cards",
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["6699b7fd0d1d25cf00072bf6"] = 311_943,
                ["6699b7fd0d1d25cf00072bf8"] = 317_325,
                ["6699b7fd0d1d25cf00072bf9"] = 60_781,
                ["6699b7fd0d1d25cf00072bfa"] = 72_065,
                ["6699b7fd0d1d25cf00072bfb"] = 48_257,
                ["6699b7fd0d1d25cf00072bfc"] = 46_809,
                ["6699b7fd0d1d25cf00072bfd"] = 73_945,
                ["6699b7fd0d1d25cf00072bfe"] = 62_461,
                ["6699b7fd0d1d25cf00072bff"] = 64_172,
                ["6699b7fd0d1d25cf00072c01"] = 50_200,
                ["6699b7fd0d1d25cf00072c02"] = 67_794
            },
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["crystal-showcase"] = 50_200,
                ["angel-showcase"] = 67_794,
                ["nivea-rachel-duo"] = 95_066,
                ["vanity-gigi-duo"] = 123_242,
                ["crystal-vanity-angel-set"] = 178_775,
                ["eleanor-tricia-lucia-set"] = 210_182,
                ["mai-ultimate"] = 311_943,
                ["erica-ultimate"] = 317_325
            }),
        new(
            "krackasourus.pokemon-cards.json",
            "krackasourus.pokemon-cards",
            "1.1.2",
            "Krackasourus Pokemon Cards",
            0.12d,
            "pokemon-cards",
            "669997e1c73060411f04d301",
            "pokemon-cards",
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["669979d0c73060411f04d26a"] = 51_949,
                ["669979d0c73060411f04d286"] = 71_618,
                ["669979d0c73060411f04d28e"] = 66_168,
                ["669979d0c73060411f04d2a0"] = 49_394,
                ["669979d0c73060411f04d2a1"] = 341_402,
                ["669979d0c73060411f04d2b2"] = 315_821,
                ["669979d0c73060411f04d2cc"] = 73_348
            },
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["dragonair-rare"] = 49_394,
                ["ditto-rare"] = 66_168,
                ["dragonair-electrode-pair"] = 101_343,
                ["ditto-electabuzz-pair"] = 137_786,
                ["rare-evolution-trio"] = 167_511,
                ["powerhouse-trio"] = 194_360,
                ["venusaur-holo"] = 315_821,
                ["dragonite-holo"] = 341_402
            }),
        new(
            "krackasourus.yugioh-cards.json",
            "krackasourus.yugioh-cards",
            "0.1.2",
            "Krackasourus Yu-Gi-Oh Cards",
            0.12d,
            "yugioh-cards",
            "669c7c89721191eae60910d9",
            "yugioh-cards",
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["669c812e721191eae60910e1"] = 138_302,
                ["669c812e721191eae60910ec"] = 160_389,
                ["669c812e721191eae60910f4"] = 164_653,
                ["669c812e721191eae6091102"] = 388_775,
                ["669c812e721191eae609112d"] = 50_126,
                ["669c812e721191eae6091133"] = 132_066,
                ["669c812e721191eae6091145"] = 41_121,
                ["669c812e721191eae609114a"] = 471_872
            },
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["swords-of-revealing-light"] = 41_121,
                ["pot-of-greed"] = 50_126,
                ["red-eyes-black-dragon"] = 132_066,
                ["blue-eyes-white-dragon"] = 138_302,
                ["dark-magician"] = 160_389,
                ["exodia"] = 164_653,
                ["gaia-dragon-champion"] = 388_775,
                ["tri-horned-dragon"] = 471_872
            }),
        new(
            "vultify.cooler-stims.json",
            "vultify.cooler-stims",
            "2.0.2",
            "Vultify CoolerStims",
            0.25d,
            "cooler-stims",
            null,
            "combat-chemistry",
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["5c0a1b2c3d4e5f6789abcde1"] = 42_000,
                ["5c0a1b2c3d4e5f6789abcde3"] = 50_000,
                ["5c0a1b2c3d4e5f6789abcde5"] = 55_000,
                ["5c0a1b2c3d4e5f6789abcde6"] = 42_000,
                ["5c0a1b2c3d4e5f6789abcde8"] = 22_000
            },
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["rapid-recon"] = 64_000,
                ["field-triage"] = 72_000,
                ["search-and-recover"] = 92_000,
                ["assault-cycle"] = 97_000,
                ["heavy-push"] = 169_000,
                ["recovery-sweep"] = 169_000,
                ["overrun-protocol"] = 316_000,
                ["hunter-killer-pharmacy"] = 316_000
            })
    ];

    private static readonly IReadOnlyDictionary<string, long> SjxUnitValues =
        new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["68331b17cccc72d0db1f764b"] = 100_000,
            ["68331d7a8f97a4202729e883"] = 120_000,
            ["68331fef74231628e6e63f25"] = 185_000,
            ["6833237c8ac71af4b28cbe84"] = 210_000,
            ["6833259d5bbfac390bc044cf"] = 145_000,
            ["68336a67ce152b72dc40abf6"] = 60_000,
            ["68336b8fb68adf25e7314cc3"] = 155_000,
            ["68336d5f2edc79e911bb9952"] = 80_000,
            ["68336d708e51c90780988824"] = 72_000,
            ["68336e67abd18e611ecbf182"] = 50_000,
            ["68336ee3b0aaa5f9f2c08975"] = 60_000,
            ["68337a2a20c7d7d769369919"] = 28_000,
            ["68337b7aff2eb46775159b1e"] = 70_000
        };
}
