using ContrabandCases.Client.Opening;
using ContrabandCases.Shared.Catalog;
using Xunit;

namespace ContrabandCases.Tests.Client;

public sealed class CatalogGridLayoutTests
{
    [Fact]
    public void GroupOddsTilesByTier_returns_no_sections_for_an_empty_list()
    {
        var sections = CatalogGridLayout.GroupOddsTilesByTier(
            Array.Empty<CatalogGridLayout.AuditOddsTile>());

        Assert.Empty(sections);
    }

    [Fact]
    public void GroupOddsTilesByTier_throws_for_a_null_list()
    {
        Assert.Throws<ArgumentNullException>(() => CatalogGridLayout.GroupOddsTilesByTier(null!));
    }

    [Fact]
    public void GroupOddsTilesByTier_omits_tiers_with_no_entries_and_orders_present_tiers_worst_to_best()
    {
        var tiles = new[]
        {
            AuditTile("black", RewardRarity.BlackLabel),
            AuditTile("scav", RewardRarity.ScavGrade),
            AuditTile("restricted", RewardRarity.Restricted)
        };
        // Contractor has no entry at all -- it must not appear as an empty section.

        var sections = CatalogGridLayout.GroupOddsTilesByTier(tiles);

        Assert.Equal(
            new[] { RewardRarity.ScavGrade, RewardRarity.Restricted, RewardRarity.BlackLabel },
            sections.Select(section => section.Tier).ToArray());
    }

    [Fact]
    public void GroupOddsTilesByTier_keeps_each_tiers_own_flattened_order_rather_than_re_sorting()
    {
        var tiles = new[]
        {
            AuditTile("z-lot", RewardRarity.Contractor),
            AuditTile("a-lot", RewardRarity.Contractor),
            AuditTile("m-lot", RewardRarity.Contractor),
            AuditTile("other-tier", RewardRarity.ScavGrade)
        };

        var section = Assert.Single(
            CatalogGridLayout.GroupOddsTilesByTier(tiles),
            s => s.Tier == RewardRarity.Contractor);

        Assert.Equal(
            new[] { "z-lot", "a-lot", "m-lot" },
            section.Tiles.Select(tile => tile.TileId).ToArray());
    }

    private static CatalogGridLayout.AuditOddsTile AuditTile(string tileId, RewardRarity grade) =>
        new(tileId, $"{tileId} display", "12.34%", grade, $"template-{tileId}");

    [Fact]
    public void GroupByTier_returns_no_sections_for_an_empty_catalog()
    {
        var sections = CatalogGridLayout.GroupByTier(Array.Empty<ValidatedReward>());

        Assert.Empty(sections);
    }

    [Fact]
    public void GroupByTier_throws_for_a_null_catalog()
    {
        Assert.Throws<ArgumentNullException>(() => CatalogGridLayout.GroupByTier(null!));
    }

    [Fact]
    public void GroupByTier_omits_tiers_with_no_entries_and_orders_present_tiers_worst_to_best()
    {
        var rewards = BuildCatalog(
            ("black", RewardRarity.BlackLabel, 0.10d),
            ("scav", RewardRarity.ScavGrade, 0.60d),
            ("restricted", RewardRarity.Restricted, 0.30d));
        // Contractor has no entry at all -- it must not appear as an empty section.

        var sections = CatalogGridLayout.GroupByTier(rewards);

        Assert.Equal(
            new[] { RewardRarity.ScavGrade, RewardRarity.Restricted, RewardRarity.BlackLabel },
            sections.Select(section => section.Tier).ToArray());
    }

    [Fact]
    public void GroupByTier_orders_a_tier_most_common_first_by_weight()
    {
        var rewards = BuildCatalog(
            ("rare-variant", RewardRarity.Contractor, 0.05d),
            ("common-variant", RewardRarity.Contractor, 0.55d),
            ("mid-variant", RewardRarity.Contractor, 0.20d),
            ("other-tier", RewardRarity.ScavGrade, 0.20d));

        var section = Assert.Single(CatalogGridLayout.GroupByTier(rewards), s => s.Tier == RewardRarity.Contractor);

        Assert.Equal(
            new[] { "common-variant", "mid-variant", "rare-variant" },
            section.Rewards.Select(reward => reward.Id).ToArray());
    }

    [Fact]
    public void GroupByTier_breaks_equal_weights_by_display_name_for_a_stable_order()
    {
        var rewards = BuildCatalog(
            ("zebra", RewardRarity.Restricted, 0.30d),
            ("apple", RewardRarity.Restricted, 0.30d),
            ("mango", RewardRarity.Restricted, 0.30d),
            ("other-tier", RewardRarity.ScavGrade, 0.10d));

        var section = Assert.Single(CatalogGridLayout.GroupByTier(rewards), s => s.Tier == RewardRarity.Restricted);

        Assert.Equal(
            new[] { "apple display", "mango display", "zebra display" },
            section.Rewards.Select(reward => reward.DisplayName).ToArray());
    }

    [Theory]
    [InlineData(1024d, 150d, 12d, 6)]
    [InlineData(150d, 150d, 12d, 1)]
    [InlineData(10d, 150d, 12d, 1)]
    [InlineData(312d, 150d, 12d, 2)]
    public void ColumnCount_fits_as_many_fixed_cells_as_possible_but_never_fewer_than_one(
        double availableWidth, double cellWidth, double columnSpacing, int expected)
    {
        Assert.Equal(expected, CatalogGridLayout.ColumnCount(availableWidth, cellWidth, columnSpacing));
    }

    [Theory]
    [InlineData(0d, 150d, 12d)]
    [InlineData(1024d, 0d, 12d)]
    [InlineData(1024d, 150d, -1d)]
    public void ColumnCount_rejects_invalid_dimensions(double availableWidth, double cellWidth, double columnSpacing)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CatalogGridLayout.ColumnCount(availableWidth, cellWidth, columnSpacing));
    }

    [Theory]
    [InlineData(0, 6, 0)]
    [InlineData(6, 6, 1)]
    [InlineData(7, 6, 2)]
    [InlineData(13, 6, 3)]
    [InlineData(82, 6, 14)]
    public void RowCount_rounds_up_to_a_whole_row(int itemCount, int columns, int expected)
    {
        Assert.Equal(expected, CatalogGridLayout.RowCount(itemCount, columns));
    }

    [Fact]
    public void RowCount_rejects_a_negative_item_count_or_non_positive_columns()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CatalogGridLayout.RowCount(-1, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => CatalogGridLayout.RowCount(6, 0));
    }

    [Fact]
    public void CellOffset_places_the_first_cell_at_the_grid_origin()
    {
        var (x, y) = CatalogGridLayout.CellOffset(0, columns: 6, cellWidth: 150d, cellHeight: 150d, columnSpacing: 12d, rowSpacing: 12d);

        Assert.Equal(0d, x);
        Assert.Equal(0d, y);
    }

    [Fact]
    public void CellOffset_advances_across_a_row_before_wrapping_to_the_next()
    {
        var (lastColumnX, lastColumnY) = CatalogGridLayout.CellOffset(
            5, columns: 6, cellWidth: 150d, cellHeight: 150d, columnSpacing: 12d, rowSpacing: 12d);
        var (wrappedX, wrappedY) = CatalogGridLayout.CellOffset(
            6, columns: 6, cellWidth: 150d, cellHeight: 150d, columnSpacing: 12d, rowSpacing: 12d);

        Assert.Equal(5 * (150d + 12d), lastColumnX);
        Assert.Equal(0d, lastColumnY);
        Assert.Equal(0d, wrappedX);
        Assert.Equal(150d + 12d, wrappedY);
    }

    [Fact]
    public void CellOffset_rejects_a_negative_index_or_non_positive_columns()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CatalogGridLayout.CellOffset(-1, 6, 150d, 150d, 12d, 12d));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CatalogGridLayout.CellOffset(0, 0, 150d, 150d, 12d, 12d));
    }

    [Theory]
    [InlineData(0, 6, 0d)]
    [InlineData(1, 6, 150d)]
    [InlineData(6, 6, 150d)]
    [InlineData(7, 6, 150d * 2 + 12d)]
    [InlineData(82, 6, 150d * 14 + 12d * 13)]
    public void GridHeight_covers_every_row_plus_the_spacing_between_rows(
        int itemCount, int columns, double expected)
    {
        Assert.Equal(expected, CatalogGridLayout.GridHeight(itemCount, columns, cellHeight: 150d, rowSpacing: 12d));
    }

    private static IReadOnlyList<ValidatedReward> BuildCatalog(
        params (string Id, RewardRarity Rarity, double Weight)[] entries)
    {
        var definitions = entries
            .Select(entry => new RewardDefinition(
                entry.Id,
                $"{entry.Id} display",
                $"template-{entry.Id}",
                $"preset-{entry.Id}",
                entry.Rarity,
                entry.Weight))
            .ToArray();
        var catalog = RewardCatalog.Create(definitions);
        return catalog.Validate(new StubPresetResolver());
    }

    private sealed class StubPresetResolver : IRewardPresetResolver
    {
        public RewardPresetTree? Resolve(string presetId)
        {
            var templateId = "template-" + presetId["preset-".Length..];
            return new RewardPresetTree([new RewardPresetItem("root", templateId, parentId: null)]);
        }
    }
}
