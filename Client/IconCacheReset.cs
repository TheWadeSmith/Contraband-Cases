using System;
using System.IO;
using BepInEx.Logging;

namespace ContrabandCases.Client;

/// <summary>
/// One-time reset ordering, separated from the live Tarkov API so failures and retries
/// can be exercised against real marker files without starting the game.
/// </summary>
internal static class IconCacheResetGate
{
    public static bool ShouldReset(bool markerFileExists) => !markerFileExists;

    public static bool RunOnce(string markerPath, Action clearCache, Action<Exception> reportFailure)
    {
        if (!ShouldReset(File.Exists(markerPath))) return false;
        try
        {
            clearCache();
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O"));
            return true;
        }
        catch (Exception exception)
        {
            // A failed clear or marker write remains retryable on the next launch.
            reportFailure(exception);
            return false;
        }
    }
}

/// <summary>
/// One-shot, version-gated reset of Tarkov's own on-disk item-icon cache
/// (the engine's <c>ItemIconCache</c>, rooted at <c>Application.temporaryCachePath</c>).
///
/// That cache keys purely off each item's TemplateId (see the engine's
/// <c>IconsHash.HashForItem</c>, confirmed by decompiling the live
/// Assembly-CSharp.dll: it XORs <c>item.TemplateId.GetHashCode()</c> plus a couple of
/// component-state bits that don't apply to a case or a key) -- never off the
/// AssetBundle's content or hash. Our case/key TemplateIds
/// (<see cref="Shared.ModConstants.CaseTemplateId"/> / <see cref="Shared.ModConstants.KeyTemplateId"/>)
/// never change across a model swap or a prefab rotation change, so once the engine
/// renders and disk-caches an icon for either template, every later lookup for that same
/// TemplateId loads the stale PNG straight off disk -- forever, surviving even a full
/// quit-and-relaunch, until that file is actually removed. This is why the 0.3.13
/// crate-model swap never picked up a fresh inventory-grid icon on its own, and why the
/// 0.3.16 case-icon-rotation fix needs this same reset to run again (bumping the marker
/// below) -- the cache doesn't know the icon's *content* changed, only that its
/// TemplateId didn't.
///
/// Runs <c>ItemIconCache.ClearIconCache()</c> -- the engine's own existing, official
/// cache-clear entry point, not a hand-rolled file delete -- until success is recorded
/// in a version-named marker file in the mod's own config folder, so it
/// doesn't force every item in the game (not just ours) to re-render its icon on every
/// single launch. Bump <see cref="MarkerFileName"/> to a new version string any time a
/// future round changes what the case/key icon actually renders as (model, material, or
/// PreviewPivot rotation) -- otherwise the on-disk cache will silently keep serving the
/// old render.
/// </summary>
internal static class IconCacheReset
{
    // Older markers could record a failed attempt, so they are not success evidence.
    internal const string MarkerFileName = "icon-cache-reset-0.4.5.marker";

    public static void RunOnce(string configRoot, ManualLogSource log)
    {
        if (string.IsNullOrWhiteSpace(configRoot) || log is null)
        {
            return;
        }

        string markerPath;
        try
        {
            markerPath = Path.Combine(configRoot, MarkerFileName);
        }
        catch (Exception exception)
        {
            log.LogWarning(
                $"Contraband Cases could not check the icon-cache-reset marker; skipping this launch: {exception.Message}");
            return;
        }

        if (IconCacheResetGate.RunOnce(markerPath, () => global::ItemIconCache.ClearIconCache(),
                exception => log.LogWarning($"Contraband Cases could not reset the item icon cache or record completion; will retry next launch: {exception.Message}")))
        {
            log.LogInfo(
                "Contraband Cases cleared Tarkov's item icon cache once, to refresh case/key artwork " +
                "(the cache is keyed by TemplateId, not by the icon's actual rendered " +
                "content, so it never invalidates itself on a model, material, or rotation change). Every " +
                "item's icon will simply re-render on next view, same as a fresh cache.");
        }
    }
}
