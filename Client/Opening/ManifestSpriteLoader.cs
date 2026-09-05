using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using UnityEngine;

namespace ContrabandCases.Client.Opening;

internal sealed class ManifestSpriteRequest
{
    public ManifestSpriteRequest(string templateId, IEnumerable<string> tileIds)
    {
        TemplateId = string.IsNullOrWhiteSpace(templateId)
            ? throw new ArgumentException("A sprite template ID is required.", nameof(templateId))
            : templateId;
        if (tileIds is null)
        {
            throw new ArgumentNullException(nameof(tileIds));
        }

        var ids = tileIds.ToArray();
        if (ids.Length == 0 || ids.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "A sprite request requires at least one non-empty tile ID.",
                nameof(tileIds));
        }
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
        {
            throw new ArgumentException(
                "A sprite request cannot contain duplicate tile IDs.",
                nameof(tileIds));
        }

        TileIds = Array.AsReadOnly(ids);
    }

    public string TemplateId { get; }

    public IReadOnlyList<string> TileIds { get; }
}

internal sealed class ManifestSpritePlan
{
    private ManifestSpritePlan(
        string anchorTileId,
        string? anchorTemplateId,
        IReadOnlyList<ManifestSpriteRequest> requests)
    {
        AnchorTileId = anchorTileId;
        AnchorTemplateId = anchorTemplateId;
        Requests = requests;
    }

    public string AnchorTileId { get; }

    public string? AnchorTemplateId { get; }

    public IReadOnlyList<ManifestSpriteRequest> Requests { get; }

    public ManifestSpritePlan LotOnly()
    {
        var requests = Requests.Select(request => new
        {
            request.TemplateId,
            Ids = request.TileIds.Where(id => id == AnchorTileId || id.StartsWith("content:", StringComparison.Ordinal)).ToArray()
        })
            .Where(request => request.Ids.Length > 0)
            .Select(request => new ManifestSpriteRequest(request.TemplateId, request.Ids)).ToArray();
        return new ManifestSpritePlan(
            AnchorTileId,
            AnchorTemplateId,
            Array.AsReadOnly(requests));
    }

    public static ManifestSpritePlan FromReveal(ManifestRevealPresentation reveal)
    {
        if (reveal is null)
        {
            throw new ArgumentNullException(nameof(reveal));
        }

        return Create(reveal.Tiles.Values, AnchorId(reveal.Lot));
    }

    public static ManifestSpritePlan ForLot(ManifestLotSnapshot lot)
    {
        if (lot is null)
        {
            throw new ArgumentNullException(nameof(lot));
        }

        var anchorId = AnchorId(lot);
        return Create(
            new[]
            {
                new ManifestTilePresentation(
                    anchorId,
                    lot.DisplayName,
                    "MANIFEST LOT ANCHOR",
                    lot.Grade,
                    lot.AnchorTemplateId)
            }.Concat(lot.Contents.Select(content => new ManifestTilePresentation(
                $"content:{content.TemplateId}", content.DisplayName, "CONTENTS", lot.Grade, content.TemplateId))),
            anchorId);
    }

    public static ManifestSpritePlan ForPremiumChoices(IReadOnlyList<ManifestLotSnapshot> lots, int selected)
    {
        if (lots.Count != 3 || selected < 0 || selected >= lots.Count)
            throw new ArgumentException("Three premium choices and a valid selection are required.");
        var lot = lots[selected];
        var tiles = lots.Select((choice, index) => new ManifestTilePresentation(
            $"premium:{index}", choice.DisplayName, "SAVED CHOICE", choice.Grade, choice.AnchorTemplateId))
            .Concat(new[] { new ManifestTilePresentation(AnchorId(lot), lot.DisplayName, "SELECTED", lot.Grade, lot.AnchorTemplateId) })
            .Concat(lot.Contents.Select(content => new ManifestTilePresentation(
                $"content:{content.TemplateId}", content.DisplayName, "CONTENTS", lot.Grade, content.TemplateId)));
        return Create(tiles, AnchorId(lot));
    }

    internal static ManifestSpritePlan Create(
        IEnumerable<ManifestTilePresentation> tiles,
        string anchorTileId)
    {
        if (tiles is null)
        {
            throw new ArgumentNullException(nameof(tiles));
        }
        if (string.IsNullOrWhiteSpace(anchorTileId))
        {
            throw new ArgumentException("A Manifest anchor tile ID is required.", nameof(anchorTileId));
        }

        var tileArray = tiles.ToArray();
        if (tileArray.Any(tile => tile is null))
        {
            throw new ArgumentException("Manifest sprite tiles cannot contain null entries.", nameof(tiles));
        }
        if (tileArray.Select(tile => tile.Id).Distinct(StringComparer.Ordinal).Count() != tileArray.Length)
        {
            throw new ArgumentException("Manifest sprite tiles cannot contain duplicate IDs.", nameof(tiles));
        }

        var anchorTiles = tileArray
            .Where(tile => string.Equals(tile.Id, anchorTileId, StringComparison.Ordinal))
            .ToArray();
        if (anchorTiles.Length != 1)
        {
            throw new ArgumentException(
                "A Manifest sprite plan requires exactly one matching anchor tile.",
                nameof(anchorTileId));
        }

        var byTemplate = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var anchorTemplateId = string.IsNullOrWhiteSpace(anchorTiles[0].TemplateId)
            ? null
            : anchorTiles[0].TemplateId;
        foreach (var tile in tileArray)
        {
            if (string.IsNullOrWhiteSpace(tile.TemplateId))
            {
                continue;
            }

            if (!byTemplate.TryGetValue(tile.TemplateId, out var tileIds))
            {
                tileIds = [];
                byTemplate.Add(tile.TemplateId, tileIds);
            }
            if (!tileIds.Contains(tile.Id, StringComparer.Ordinal))
            {
                tileIds.Add(tile.Id);
            }
        }

        var requests = byTemplate
            .Select(pair => new ManifestSpriteRequest(pair.Key, pair.Value))
            .ToArray();
        return new ManifestSpritePlan(
            anchorTileId,
            anchorTemplateId,
            Array.AsReadOnly(requests));
    }

    private static string AnchorId(ManifestLotSnapshot lot) => $"lot:{lot.Fingerprint}";
}

internal enum ManifestSpriteResolutionKind
{
    Pending,
    Loaded,
    Unavailable,
    Superseded
}

internal readonly struct ManifestSpriteResolution<TSprite>
    where TSprite : class
{
    private ManifestSpriteResolution(
        ManifestSpriteResolutionKind kind,
        TSprite? sprite,
        Exception? failure)
    {
        Kind = kind;
        Sprite = sprite;
        Failure = failure;
    }

    public ManifestSpriteResolutionKind Kind { get; }

    public TSprite? Sprite { get; }

    public Exception? Failure { get; }

    public static ManifestSpriteResolution<TSprite> Pending() =>
        new(ManifestSpriteResolutionKind.Pending, null, null);

    public static ManifestSpriteResolution<TSprite> Loaded(TSprite sprite) =>
        new(ManifestSpriteResolutionKind.Loaded, sprite, null);

    public static ManifestSpriteResolution<TSprite> Unavailable(Exception failure) =>
        new(ManifestSpriteResolutionKind.Unavailable, null, failure);

    public static ManifestSpriteResolution<TSprite> Superseded() =>
        new(ManifestSpriteResolutionKind.Superseded, null, null);
}

internal sealed class ManifestSpriteTaskCache<TSprite>
    where TSprite : class
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Task<TSprite>> _tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TSprite> _sprites = new(StringComparer.Ordinal);
    private readonly Func<TSprite?, bool> _isUnavailable;

    public ManifestSpriteTaskCache(Func<TSprite?, bool>? isUnavailable = null)
    {
        _isUnavailable = isUnavailable ?? (sprite => sprite is null);
    }

    internal int TaskCount
    {
        get
        {
            lock (_sync)
            {
                return _tasks.Count;
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _tasks.Clear();
            _sprites.Clear();
        }
    }

    public Task<TSprite> GetOrLoad(
        string templateId,
        Func<string, Task<TSprite>> load)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            throw new ArgumentException("A sprite template ID is required.", nameof(templateId));
        }
        if (load is null)
        {
            throw new ArgumentNullException(nameof(load));
        }

        lock (_sync)
        {
            if (_tasks.TryGetValue(templateId, out var cachedTask))
            {
                if (!NeedsRetry(cachedTask))
                {
                    return cachedTask;
                }

                _tasks.Remove(templateId);
                _sprites.Remove(templateId);
            }

            var task = load(templateId)
                ?? throw new InvalidOperationException(
                    $"The sprite loader returned no task for template '{templateId}'.");
            _tasks.Add(templateId, task);
            return task;
        }
    }

    public ManifestSpriteResolution<TSprite> Resolve(
        string templateId,
        Task<TSprite> task)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            throw new ArgumentException("A sprite template ID is required.", nameof(templateId));
        }
        if (task is null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        lock (_sync)
        {
            if (!_tasks.TryGetValue(templateId, out var owner) ||
                !ReferenceEquals(owner, task))
            {
                return ManifestSpriteResolution<TSprite>.Superseded();
            }
            if (_sprites.TryGetValue(templateId, out var cachedSprite) &&
                !_isUnavailable(cachedSprite))
            {
                return ManifestSpriteResolution<TSprite>.Loaded(cachedSprite);
            }
        }
        if (!task.IsCompleted)
        {
            return ManifestSpriteResolution<TSprite>.Pending();
        }

        TSprite? sprite;
        try
        {
            sprite = task.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            TryEvict(templateId, task);
            return ManifestSpriteResolution<TSprite>.Unavailable(exception);
        }

        if (_isUnavailable(sprite))
        {
            TryEvict(templateId, task);
            return ManifestSpriteResolution<TSprite>.Unavailable(
                new InvalidOperationException(
                    $"Tarkov returned no sprite for template '{templateId}'."));
        }

        lock (_sync)
        {
            if (!_tasks.TryGetValue(templateId, out var owner) ||
                !ReferenceEquals(owner, task))
            {
                return ManifestSpriteResolution<TSprite>.Superseded();
            }

            _sprites[templateId] = sprite!;
            return ManifestSpriteResolution<TSprite>.Loaded(sprite!);
        }
    }

    public bool TryEvict(string templateId, Task<TSprite> task)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            throw new ArgumentException("A sprite template ID is required.", nameof(templateId));
        }
        if (task is null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        lock (_sync)
        {
            if (!_tasks.TryGetValue(templateId, out var owner) ||
                !ReferenceEquals(owner, task))
            {
                return false;
            }

            _tasks.Remove(templateId);
            _sprites.Remove(templateId);
            return true;
        }
    }

    private bool NeedsRetry(Task<TSprite> task)
    {
        if (task.IsCanceled)
        {
            return true;
        }
        if (task.IsFaulted)
        {
            _ = task.Exception;
            return true;
        }
        return task.IsCompleted && _isUnavailable(task.GetAwaiter().GetResult());
    }
}

internal sealed class ManifestSpriteLoadFailure
{
    public ManifestSpriteLoadFailure(string templateId, Exception exception)
    {
        TemplateId = string.IsNullOrWhiteSpace(templateId)
            ? throw new ArgumentException("A sprite template ID is required.", nameof(templateId))
            : templateId;
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    public string TemplateId { get; }

    public Exception Exception { get; }
}

internal sealed class ManifestSpriteLoadBatch<TSprite>
    where TSprite : class
{
    private readonly ManifestSpriteTaskCache<TSprite> _cache;
    private readonly Dictionary<string, PendingRequest> _pending;

    private ManifestSpriteLoadBatch(
        ManifestSpritePlan plan,
        ManifestSpriteTaskCache<TSprite> cache,
        Dictionary<string, PendingRequest> pending,
        IReadOnlyList<ManifestSpriteLoadFailure> startFailures)
    {
        Plan = plan;
        _cache = cache;
        _pending = pending;
        StartFailures = startFailures;
    }

    public ManifestSpritePlan Plan { get; }

    public IReadOnlyList<ManifestSpriteLoadFailure> StartFailures { get; }

    public bool IsComplete => _pending.Count == 0;

    public static ManifestSpriteLoadBatch<TSprite> Start(
        ManifestSpritePlan plan,
        ManifestSpriteTaskCache<TSprite> cache,
        Func<string, Task<TSprite>> load)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }
        if (cache is null)
        {
            throw new ArgumentNullException(nameof(cache));
        }
        if (load is null)
        {
            throw new ArgumentNullException(nameof(load));
        }

        var pending = new Dictionary<string, PendingRequest>(StringComparer.Ordinal);
        var failures = new List<ManifestSpriteLoadFailure>();
        foreach (var request in plan.Requests)
        {
            try
            {
                var task = cache.GetOrLoad(request.TemplateId, load);
                pending.Add(request.TemplateId, new PendingRequest(request, task));
            }
            catch (Exception exception)
            {
                failures.Add(new ManifestSpriteLoadFailure(request.TemplateId, exception));
            }
        }

        return new ManifestSpriteLoadBatch<TSprite>(
            plan,
            cache,
            pending,
            new ReadOnlyCollection<ManifestSpriteLoadFailure>(failures));
    }

    public int BindAvailable(
        Func<bool> canBind,
        Action<string, TSprite> bind,
        Action<string, Exception>? reportUnavailable = null)
    {
        if (canBind is null)
        {
            throw new ArgumentNullException(nameof(canBind));
        }
        if (bind is null)
        {
            throw new ArgumentNullException(nameof(bind));
        }
        if (!canBind())
        {
            return 0;
        }

        var bound = 0;
        foreach (var entry in _pending.Values.ToArray())
        {
            if (!canBind())
            {
                break;
            }

            if (entry.Sprite is null)
            {
                var resolution = _cache.Resolve(entry.Request.TemplateId, entry.Task);
                switch (resolution.Kind)
                {
                    case ManifestSpriteResolutionKind.Pending:
                        continue;
                    case ManifestSpriteResolutionKind.Unavailable:
                        _pending.Remove(entry.Request.TemplateId);
                        reportUnavailable?.Invoke(
                            entry.Request.TemplateId,
                            resolution.Failure ?? new InvalidOperationException(
                                "A Manifest sprite load failed without an error."));
                        continue;
                    case ManifestSpriteResolutionKind.Superseded:
                        _pending.Remove(entry.Request.TemplateId);
                        continue;
                    case ManifestSpriteResolutionKind.Loaded:
                        entry.Sprite = resolution.Sprite
                            ?? throw new InvalidOperationException(
                                "A loaded Manifest sprite result had no sprite.");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            while (entry.NextTileIndex < entry.Request.TileIds.Count && canBind())
            {
                bind(entry.Request.TileIds[entry.NextTileIndex], entry.Sprite);
                entry.NextTileIndex++;
                bound++;
            }

            if (entry.NextTileIndex == entry.Request.TileIds.Count)
            {
                _pending.Remove(entry.Request.TemplateId);
            }
        }

        return bound;
    }

    public int EvictPendingLoads(Action<string>? reportTimeout = null)
    {
        var evicted = 0;
        foreach (var entry in _pending.Values.ToArray())
        {
            if (!entry.Task.IsCompleted)
            {
                _ = _cache.TryEvict(entry.Request.TemplateId, entry.Task);
                reportTimeout?.Invoke(entry.Request.TemplateId);
                evicted++;
            }

            _pending.Remove(entry.Request.TemplateId);
        }

        return evicted;
    }

    private sealed class PendingRequest
    {
        public PendingRequest(ManifestSpriteRequest request, Task<TSprite> task)
        {
            Request = request;
            Task = task;
        }

        public ManifestSpriteRequest Request { get; }

        public Task<TSprite> Task { get; }

        public TSprite? Sprite { get; set; }

        public int NextTileIndex { get; set; }
    }
}

internal static class ManifestSpriteBindingAuthority
{
    public static bool CanBind(
        bool coordinatorDisposed,
        bool runIsCurrent,
        bool presentationDetached,
        long presentationGeneration,
        long expectedPresentationGeneration) =>
        !coordinatorDisposed &&
        runIsCurrent &&
        !presentationDetached &&
        presentationGeneration == expectedPresentationGeneration;
}

internal static class ManifestItemSpriteLoader
{
    public static Task<Sprite> LoadAsync(string templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            throw new ArgumentException("A sprite template ID is required.", nameof(templateId));
        }
        if (!Singleton<ItemFactory>.Instantiated)
        {
            throw new InvalidOperationException("Tarkov's display item factory is not ready.");
        }

        var displayItem = Singleton<ItemFactory>.Instance.GetPresetItem(templateId)
            ?? throw new InvalidOperationException(
                $"Tarkov could not create a display-only item for template '{templateId}'.");
        return ItemViewFactory.GetItemSpriteAsync(displayItem, 1)
            ?? throw new InvalidOperationException(
                $"Tarkov returned no sprite task for template '{templateId}'.");
    }
}
