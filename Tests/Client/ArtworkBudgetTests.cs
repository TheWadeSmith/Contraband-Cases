using ContrabandCases.Client.Opening;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using Xunit;

namespace ContrabandCases.Tests.Client;

public sealed class ArtworkBudgetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reveal_requests_only_rendered_artwork_and_prioritizes_the_winner(bool reduced)
    {
        var lot = new ManifestLotSnapshot("core", "Core", "lot", "Prize", "Purpose", "operator", "recon",
            RewardRarity.Contractor, "anchor", "fingerprint", 100, 100, 1, []);
        var winner = "lot:fingerprint";
        var tiles = Enumerable.Range(0, 100).Select(i => new ManifestTilePresentation($"tile:{i}", "Prize",
            "Contents", RewardRarity.Contractor, $"template:{i}")).Append(new(winner, "Winner", "Contents", lot.Grade, "anchor"))
            .ToDictionary(t => t.Id);
        var motion = RouletteRevealPlan.Create(tiles.Keys.Where(id => id != winner).ToArray(), winner,
            36, 29, 13, 1000, 200, 12, reduced);
        var reveal = new ManifestRevealPresentation(motion, tiles, lot, "REVEAL");
        var plan = ManifestSpritePlan.FromReveal(reveal);
        Assert.Equal("anchor", plan.Requests[0].TemplateId);
        Assert.All(plan.Requests.SelectMany(r => r.TileIds), id => Assert.Contains(id, motion.Strip));
        Assert.InRange(plan.Requests.Count, 1, reduced ? 7 : 36);
        var priorityTemplates = new[] { "anchor" }.Concat(motion.InitiallyVisibleTiles.Select(id => tiles[id].TemplateId))
            .Distinct().ToArray();
        Assert.Equal(priorityTemplates, plan.Requests.Take(priorityTemplates.Length).Select(r => r.TemplateId));
    }

    [Fact]
    public void Loads_are_bounded_across_frames_and_replacement_windows_even_after_cache_clear()
    {
        var tiles = Enumerable.Range(0, 20).Select(i => new ManifestTilePresentation($"tile:{i}", "Prize", "Contents",
            RewardRarity.Contractor, $"template:{i}")).ToArray();
        var plan = ManifestSpritePlan.Create(tiles, "tile:0");
        var pending = new List<TaskCompletionSource<object>>();
        Task<object> Load(string _) { var task = new TaskCompletionSource<object>(); pending.Add(task); return task.Task; }
        var cache = new ManifestSpriteTaskCache<object>();
        var batch = ManifestSpriteLoadBatch<object>.Start(plan, cache, Load);
        Assert.InRange(pending.Count, 1, 2);
        for (var frame = 0; frame < 10; frame++) batch.BindAvailable(() => true, (_, _) => { });
        Assert.InRange(pending.Count, 1, 4);
        cache.Clear();
        var next = ManifestSpriteLoadBatch<object>.Start(plan, cache, Load);
        next.BindAvailable(() => true, (_, _) => { });
        Assert.InRange(pending.Count, 1, 4);
        foreach (var task in pending.ToArray()) task.SetResult(new object());
        var before = pending.Count;
        next.BindAvailable(() => true, (_, _) => { });
        Assert.InRange(pending.Count - before, 1, 2);
    }

    [Fact]
    public void Native_launch_budget_is_shared_with_replacement_batches_in_the_same_frame()
    {
        var frame = 10;
        var cache = new ManifestSpriteTaskCache<object>(frame: () => frame);
        var plan = ManifestSpritePlan.Create(Enumerable.Range(0, 12).Select(i =>
            new ManifestTilePresentation($"tile:{i}", "Prize", "Contents", RewardRarity.Contractor, $"template:{i}")), "tile:0");
        var calls = 0;
        Task<object> Load(string _) { calls++; return Task.FromResult(new object()); }
        var batch = ManifestSpriteLoadBatch<object>.Start(plan, cache, Load);
        batch.BindAvailable(() => true, (_, _) => { });
        cache.Clear();
        var replacement = ManifestSpriteLoadBatch<object>.Start(plan, cache, Load);
        replacement.BindAvailable(() => true, (_, _) => { });
        Assert.Equal(2, calls);
        frame++;
        replacement.BindAvailable(() => true, (_, _) => { });
        Assert.Equal(4, calls);
    }

    [Fact]
    public void Revoked_authority_never_starts_queued_artwork()
    {
        var tiles = Enumerable.Range(0, 20).Select(i => new ManifestTilePresentation($"tile:{i}", "Prize", "Contents",
            RewardRarity.Contractor, $"template:{i}"));
        var calls = 0;
        var batch = ManifestSpriteLoadBatch<object>.Start(ManifestSpritePlan.Create(tiles, "tile:0"),
            new(), _ => { calls++; return Task.FromResult(new object()); });
        var before = calls;
        batch.BindAvailable(() => false, (_, _) => { });
        Assert.Equal(before, calls);
        batch.EvictPendingLoads();
        Assert.True(batch.IsComplete);
        batch.BindAvailable(() => true, (_, _) => { });
        Assert.Equal(before, calls);
    }

    [Theory]
    [InlineData(CaseContracts.Relics, "relic-expedition", "Relic Expeditions")]
    [InlineData(CaseContracts.BlackSite, "ammo", "Ammunition")]
    [InlineData(CaseContracts.BlackSite, "recon", "Recon Equipment")]
    [InlineData(CaseContracts.BlackSite, "rifle", "Precision Weapons")]
    public void Case_groups_are_readable(string template, string track, string expected) =>
        Assert.Equal(expected, CaseContracts.GroupLabel(template, track));

    [Fact]
    public void Mixed_case_has_the_same_name_in_Broker_and_inventory() =>
        Assert.Equal(BrokerPresentation.CaseName(ModConstants.CaseTemplateId), CaseContracts.Name(ModConstants.CaseTemplateId));
}
