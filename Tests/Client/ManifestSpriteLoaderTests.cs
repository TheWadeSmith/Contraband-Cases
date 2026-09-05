using System.Collections.Concurrent;
using System.Threading;
using ContrabandCases.Client.Opening;
using ContrabandCases.Shared.Catalog;
using Xunit;

namespace ContrabandCases.Tests.Client;

public sealed class ManifestSpriteLoaderTests
{
    [Fact]
    public void End_of_run_cache_clear_supersedes_late_artwork_and_allows_a_fresh_request()
    {
        var cache = new ManifestSpriteTaskCache<object>();
        var completion = new TaskCompletionSource<object>();
        var old = cache.GetOrLoad("template", _ => completion.Task);
        cache.Clear();
        Assert.Equal(0, cache.TaskCount);
        completion.SetResult(new object());
        Assert.Equal(ManifestSpriteResolutionKind.Superseded, cache.Resolve("template", old).Kind);
        var current = cache.GetOrLoad("template", _ => Task.FromResult(new object()));
        Assert.NotSame(old, current);
        Assert.Equal(ManifestSpriteResolutionKind.Loaded, cache.Resolve("template", current).Kind);
    }

    [Fact]
    public void Plan_skips_sealed_tiles_without_a_template()
    {
        var plan = ManifestSpritePlan.Create(
            [
                Tile("lot:anchor", "template-anchor"),
                Tile("seal:1", templateId: null),
                Tile("seal:2", templateId: null)
            ],
            "lot:anchor");

        var request = Assert.Single(plan.Requests);
        Assert.Equal("template-anchor", request.TemplateId);
        Assert.Equal(new[] { "lot:anchor" }, request.TileIds);
        Assert.Equal("template-anchor", plan.AnchorTemplateId);
    }

    [Fact]
    public void Terminal_plan_keeps_contents_for_fallback_but_excludes_other_catalog_lots()
    {
        var plan = ManifestSpritePlan.Create(
            [
                Tile("lot:anchor", "shared-template"),
                Tile("content:shared", "shared-template"),
                Tile("content:other", "other-template"),
                Tile("catalog:other", "unrelated-template")
            ],
            "lot:anchor");

        var anchorOnly = plan.LotOnly();

        Assert.Equal("lot:anchor", anchorOnly.AnchorTileId);
        Assert.Equal(2, anchorOnly.Requests.Count);
        var request = anchorOnly.Requests.Single(r => r.TemplateId == "shared-template");
        Assert.Equal("shared-template", request.TemplateId);
        Assert.Equal(new[] { "lot:anchor", "content:shared" }, request.TileIds);
        Assert.DoesNotContain(anchorOnly.Requests, r => r.TemplateId == "unrelated-template");
    }

    [Fact]
    public void Missing_terminal_anchor_is_a_no_load_cosmetic_fallback()
    {
        var plan = ManifestSpritePlan.Create(
            [Tile("lot:anchor", templateId: null)],
            "lot:anchor").LotOnly();
        var loadCalls = 0;

        var batch = ManifestSpriteLoadBatch<object>.Start(
            plan,
            new ManifestSpriteTaskCache<object>(),
            _ =>
            {
                loadCalls++;
                return Task.FromResult(new object());
            });

        Assert.Equal(0, loadCalls);
        Assert.Empty(batch.StartFailures);
        Assert.True(batch.IsComplete);
    }

    [Fact]
    public void Duplicate_template_loads_once_and_fans_out_to_every_tile()
    {
        var plan = ManifestSpritePlan.Create(
            [
                Tile("lot:anchor", "shared-template"),
                Tile("content:shared", "shared-template"),
                Tile("content:other", "other-template")
            ],
            "lot:anchor");
        var cache = new ManifestSpriteTaskCache<object>();
        var sharedSprite = new object();
        var otherSprite = new object();
        var loadCalls = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var batch = ManifestSpriteLoadBatch<object>.Start(
            plan,
            cache,
            templateId =>
            {
                loadCalls.AddOrUpdate(templateId, 1, (_, count) => count + 1);
                return Task.FromResult(
                    string.Equals(templateId, "shared-template", StringComparison.Ordinal)
                        ? sharedSprite
                        : otherSprite);
            });
        var bindings = new Dictionary<string, object>(StringComparer.Ordinal);

        var bound = batch.BindAvailable(
            () => true,
            (tileId, sprite) => bindings.Add(tileId, sprite));

        Assert.Equal(3, bound);
        Assert.Equal(2, loadCalls.Count);
        Assert.All(loadCalls.Values, count => Assert.Equal(1, count));
        Assert.Same(sharedSprite, bindings["lot:anchor"]);
        Assert.Same(sharedSprite, bindings["content:shared"]);
        Assert.Same(otherSprite, bindings["content:other"]);
        Assert.True(batch.IsComplete);
    }

    [Fact]
    public void Cache_deduplicates_concurrent_requests_by_template_id()
    {
        var cache = new ManifestSpriteTaskCache<object>();
        var pending = new TaskCompletionSource<object>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCalls = 0;
        var tasks = new Task<object>[32];

        Parallel.For(0, tasks.Length, index =>
        {
            tasks[index] = cache.GetOrLoad(
                "template",
                _ =>
                {
                    Interlocked.Increment(ref loadCalls);
                    return pending.Task;
                });
        });

        Assert.Equal(1, loadCalls);
        Assert.All(tasks, task => Assert.Same(pending.Task, task));
        Assert.Equal(1, cache.TaskCount);
    }

    [Theory]
    [InlineData(UnavailableTaskKind.Faulted)]
    [InlineData(UnavailableTaskKind.Cancelled)]
    [InlineData(UnavailableTaskKind.NullResult)]
    public void Unavailable_tasks_are_evicted_and_retryable(UnavailableTaskKind kind)
    {
        var cache = new ManifestSpriteTaskCache<object>();
        var first = cache.GetOrLoad("template", _ => UnavailableTask(kind));

        var resolution = cache.Resolve("template", first);

        Assert.Equal(ManifestSpriteResolutionKind.Unavailable, resolution.Kind);
        Assert.NotNull(resolution.Failure);
        var recoveredSprite = new object();
        var retry = cache.GetOrLoad(
            "template",
            _ => Task.FromResult(recoveredSprite));
        Assert.NotSame(first, retry);
        Assert.Equal(
            ManifestSpriteResolutionKind.Loaded,
            cache.Resolve("template", retry).Kind);
    }

    [Fact]
    public void Timed_out_task_is_evicted_without_blocking_a_later_retry()
    {
        var plan = OneTemplatePlan();
        var cache = new ManifestSpriteTaskCache<object>();
        var pending = new TaskCompletionSource<object>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCalls = 0;
        var first = ManifestSpriteLoadBatch<object>.Start(
            plan,
            cache,
            _ =>
            {
                loadCalls++;
                return pending.Task;
            });
        var timedOut = new List<string>();

        Assert.Equal(1, first.EvictPendingLoads(timedOut.Add));

        Assert.True(first.IsComplete);
        Assert.Equal(new[] { "template" }, timedOut);
        var recovered = ManifestSpriteLoadBatch<object>.Start(
            plan,
            cache,
            _ =>
            {
                loadCalls++;
                return Task.FromResult(new object());
            });
        Assert.Equal(2, loadCalls);
        Assert.Equal(1, recovered.BindAvailable(() => true, (_, _) => { }));
    }

    [Fact]
    public void Late_timed_out_result_cannot_replace_or_bind_over_its_retry()
    {
        var cache = new ManifestSpriteTaskCache<object>();
        var oldCompletion = new TaskCompletionSource<object>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oldTask = cache.GetOrLoad("template", _ => oldCompletion.Task);
        Assert.True(cache.TryEvict("template", oldTask));
        var currentSprite = new object();
        var currentTask = cache.GetOrLoad(
            "template",
            _ => Task.FromResult(currentSprite));

        oldCompletion.SetResult(new object());

        Assert.Equal(
            ManifestSpriteResolutionKind.Superseded,
            cache.Resolve("template", oldTask).Kind);
        var current = cache.Resolve("template", currentTask);
        Assert.Equal(ManifestSpriteResolutionKind.Loaded, current.Kind);
        Assert.Same(currentSprite, current.Sprite);
    }

    [Theory]
    [InlineData(true, true, false, 4, 4, false)]
    [InlineData(false, false, false, 4, 4, false)]
    [InlineData(false, true, true, 4, 4, false)]
    [InlineData(false, true, false, 5, 4, false)]
    [InlineData(false, true, false, 4, 4, true)]
    public void Binding_authority_requires_current_attached_matching_generation(
        bool disposed,
        bool current,
        bool detached,
        long generation,
        long expectedGeneration,
        bool expected)
    {
        Assert.Equal(
            expected,
            ManifestSpriteBindingAuthority.CanBind(
                disposed,
                current,
                detached,
                generation,
                expectedGeneration));
    }

    [Fact]
    public void Batch_stops_binding_as_soon_as_presentation_authority_is_lost()
    {
        var plan = ManifestSpritePlan.Create(
            [
                Tile("lot:anchor", "template"),
                Tile("content:shared", "template")
            ],
            "lot:anchor");
        var batch = ManifestSpriteLoadBatch<object>.Start(
            plan,
            new ManifestSpriteTaskCache<object>(),
            _ => Task.FromResult(new object()));
        var detached = false;
        var bindings = new List<string>();

        var firstPass = batch.BindAvailable(
            () => ManifestSpriteBindingAuthority.CanBind(
                coordinatorDisposed: false,
                runIsCurrent: true,
                presentationDetached: detached,
                presentationGeneration: 9,
                expectedPresentationGeneration: 9),
            (tileId, _) =>
            {
                bindings.Add(tileId);
                detached = true;
            });

        Assert.Equal(1, firstPass);
        Assert.Equal(new[] { "lot:anchor" }, bindings);
        Assert.False(batch.IsComplete);
        detached = false;
        Assert.Equal(1, batch.BindAvailable(() => true, (tileId, _) => bindings.Add(tileId)));
        Assert.Equal(new[] { "lot:anchor", "content:shared" }, bindings);
    }

    [Fact]
    public void Sprite_failures_are_reported_without_throwing_or_blocking_presentation()
    {
        var expected = new InvalidOperationException("cosmetic load failed");
        var batch = ManifestSpriteLoadBatch<object>.Start(
            OneTemplatePlan(),
            new ManifestSpriteTaskCache<object>(),
            _ => Task.FromException<object>(expected));
        var failures = new List<Exception>();
        var presentationContinued = false;

        var exception = Record.Exception(() =>
        {
            Assert.Equal(0, batch.BindAvailable(
                () => true,
                (_, _) => throw new InvalidOperationException("No sprite should bind."),
                (_, failure) => failures.Add(failure)));
            presentationContinued = true;
        });

        Assert.Null(exception);
        Assert.True(presentationContinued);
        Assert.Same(expected, Assert.Single(failures));
        Assert.True(batch.IsComplete);
    }

    [Fact]
    public void Synchronous_loader_failure_is_captured_as_cosmetic_only()
    {
        var expected = new InvalidOperationException("factory is not ready");

        var batch = ManifestSpriteLoadBatch<object>.Start(
            OneTemplatePlan(),
            new ManifestSpriteTaskCache<object>(),
            _ => throw expected);

        var failure = Assert.Single(batch.StartFailures);
        Assert.Equal("template", failure.TemplateId);
        Assert.Same(expected, failure.Exception);
        Assert.True(batch.IsComplete);
    }

    private static ManifestSpritePlan OneTemplatePlan() =>
        ManifestSpritePlan.Create([Tile("lot:anchor", "template")], "lot:anchor");

    private static ManifestTilePresentation Tile(string id, string? templateId) =>
        new(id, id, "detail", RewardRarity.Contractor, templateId);

    private static Task<object> UnavailableTask(UnavailableTaskKind kind) => kind switch
    {
        UnavailableTaskKind.Faulted => Task.FromException<object>(
            new InvalidOperationException("failed")),
        UnavailableTaskKind.Cancelled => Task.FromCanceled<object>(
            new CancellationToken(canceled: true)),
        UnavailableTaskKind.NullResult => Task.FromResult<object>(null!),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public enum UnavailableTaskKind
    {
        Faulted,
        Cancelled,
        NullResult
    }
}
