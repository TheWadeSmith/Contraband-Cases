using System.Collections;
using System.Threading.Tasks;
using BepInEx.Logging;
using Comfort.Common;
using ContrabandCases.Client.Catalog;
using ContrabandCases.Client.Configuration;
using ContrabandCases.Client.UI;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ContrabandCases.Client.Opening;

internal sealed class RouletteController : IDisposable
{
    private readonly MonoBehaviour _coroutineHost;
    private readonly ManualLogSource _log;
    private readonly PresentationConfig _config;
    private readonly string _catalogPath;
    private readonly OpeningPhaseMachine _phase = new();
    private readonly CaseOperationDispatcher _dispatcher = new();
    private readonly RelaySnapshotTransport _snapshotTransport = new();
    private readonly RouletteOverlay _overlay;
    private readonly ManifestPresentationCoordinator _manifest;
    private readonly Func<bool> _lifecycleDiagnosticsEnabled;
    private readonly Dictionary<string, Task<Sprite>> _spriteTasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Sprite> _sprites = new(StringComparer.Ordinal);

    // Confirmation-screen catalog grid artwork reuses the Manifest sprite pipeline (ManifestSpritePlan/
    // ManifestSpriteLoadBatch/ManifestSpriteTaskCache) rather than the legacy _spriteTasks/_sprites
    // dictionaries above -- that pipeline already has the batched-request, cancellation, and supersession
    // handling a many-tiles-at-once load needs, and is exactly what RewardTileView's own sprite binding
    // ultimately rides on for the spin strip. Kept as its own cache (keyed by weapon template ID, same as
    // the Manifest coordinator's _spriteCache) rather than sharing one, since the two flows have unrelated
    // lifecycles.
    private readonly ManifestSpriteTaskCache<Sprite> _catalogSpriteCache = new(sprite => sprite == null);
    private ClientRewardCatalog? _catalog;
    private OpeningRun? _active;
    private Coroutine? _controllerLoop;
    private Coroutine? _selfTestCoroutine;
    private RouletteRevealPlan? _selfTestPlan;
    private ValidatedReward? _selfTestReward;
    private Coroutine? _startupRecoveryCoroutine;
    private readonly RelayStartupRecoveryGate _startupRecovery = new();
    private int _selfTestSequence;
    private bool _selfTestActive;
    private CatalogReadinessState _catalogState = CatalogReadinessState.Waiting;
    private bool _disposed;

    internal bool IsPresentationBusy =>
        _manifest.IsBusy ||
        _active is not null ||
        _selfTestActive ||
        _startupRecovery.State is RelayStartupRecoveryState.Probing or
            RelayStartupRecoveryState.Recovering or
            RelayStartupRecoveryState.Failed;

    public RouletteController(
        MonoBehaviour coroutineHost,
        ManualLogSource log,
        PresentationConfig config,
        string catalogPath,
        Func<bool>? lifecycleDiagnosticsEnabled = null)
    {
        _coroutineHost = coroutineHost ?? throw new ArgumentNullException(nameof(coroutineHost));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _catalogPath = string.IsNullOrWhiteSpace(catalogPath)
            ? throw new ArgumentException("A client reward catalog path is required.", nameof(catalogPath))
            : catalogPath;
        _lifecycleDiagnosticsEnabled = lifecycleDiagnosticsEnabled ?? (() => false);
        _overlay = new RouletteOverlay(LifecycleDiagnostic, () => _config.Volume, () => _config.ReducedMotionDefault,
            message => _log.LogInfo(message));
        _manifest = new ManifestPresentationCoordinator(
            _coroutineHost,
            _log,
            _config,
            _overlay,
            _dispatcher,
            new ManifestSnapshotTransport());
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
        SceneManager.sceneUnloaded += OnSceneUnloaded;
        _log.LogInfo("Contraband Cases Manifest UI is ready; the local reward catalog is retained only for cosmetic previews and old-save recovery.");
        _controllerLoop = _coroutineHost.StartCoroutine(ControllerLoop());
    }

    public Task<IResult> OpenAsync(ItemUiContext itemUiContext, Item targetItem)
    {
        if (_disposed)
        {
            return RejectOpen("Contraband Cases is shutting down.");
        }
        if (Singleton<GameWorld>.Instantiated)
        {
            return RejectOpen("BR-12 cases can only be opened outside a raid.");
        }
        if (_selfTestActive)
        {
            return RejectOpen("Close the cosmetic roulette self-test before opening a real BR-12 case.");
        }
        if (_active is not null || _manifest.IsBusy)
        {
            return RejectOpen("Another Contraband Cases settlement is already active.");
        }
        if (_startupRecovery.State != RelayStartupRecoveryState.Ready)
        {
            var retrying = _startupRecovery.State == RelayStartupRecoveryState.Failed &&
                TryStartStartupRecoveryFromSingleton(forceRetry: true);
            if (!retrying)
            {
                _ = TryStartStartupRecoveryFromSingleton(forceRetry: false);
            }
            return RejectOpen(retrying
                ? "Contraband Cases is retrying its old-save Relay recovery check. Unpack the case again when it finishes."
                : "Contraband Cases is checking old-save Relay recovery state before opening a Manifest.");
        }

        return _manifest.OpenAsync(itemUiContext, targetItem);
    }

    internal bool TryOpenLibrary(bool gallery, LibraryConfig settings)
    {
        if (_disposed || IsPresentationBusy || Singleton<GameWorld>.Instantiated ||
            !TryGetAuthenticatedContext(out var session, out var profile)) return false;
        return _manifest.TryOpenLibrary(session, profile, gallery, settings);
    }

    internal bool CanOpenBroker => !_disposed && !IsPresentationBusy && !Singleton<GameWorld>.Instantiated &&
        TryGetAuthenticatedContext(out _, out _);

    private Task<IResult> OpenLegacyAsync(ItemUiContext itemUiContext, Item targetItem)
    {
        if (_disposed)
        {
            return RejectOpen("Contraband Cases is shutting down.");
        }

        if (Singleton<GameWorld>.Instantiated)
        {
            return RejectOpen("BR-12 cases can only be opened outside a raid.");
        }

        if (_selfTestActive)
        {
            return RejectOpen("Close the cosmetic roulette self-test before opening a real BR-12 case.");
        }

        if (_catalogState != CatalogReadinessState.Ready || _catalog is null)
        {
            return RejectOpen(
                _catalogState == CatalogReadinessState.Invalid
                    ? "Contraband Cases presentation data is invalid; BR-12 dispatch is disabled."
                    : "Contraband Cases is still waiting for Tarkov's item presets.");
        }

        if (_startupRecovery.State != RelayStartupRecoveryState.Ready)
        {
            var retrying = _startupRecovery.State == RelayStartupRecoveryState.Failed &&
                TryStartStartupRecoveryFromSingleton(forceRetry: true);
            if (!retrying)
            {
                _ = TryStartStartupRecoveryFromSingleton(forceRetry: false);
            }

            return RejectOpen(retrying
                ? "Contraband Cases is retrying its authenticated Relay recovery check. Unpack the case again when it finishes."
                : "Contraband Cases is checking for an unfinished Relay settlement before opening another case.");
        }

        if (!_phase.TryOpen(out var token))
        {
            return RejectOpen("Another Contraband Cases opening is already active.");
        }

        OpeningRun? run = null;
        try
        {
            if (itemUiContext is null)
            {
                throw new ArgumentNullException(nameof(itemUiContext));
            }

            if (targetItem is null)
            {
                throw new ArgumentNullException(nameof(targetItem));
            }
            var session = itemUiContext.ClientSession
                ?? throw new InvalidOperationException("The authenticated Tarkov session is unavailable.");
            var profile = session.Profile
                ?? throw new InvalidOperationException("The authenticated Tarkov profile is unavailable.");
            if (!_startupRecovery.IsCurrent(_startupRecovery.Generation, session, profile, profile.Id,
                    LobbyUiContext.IsMenuOrStashReady))
            {
                throw new InvalidOperationException(
                    "The authenticated profile changed after the Relay recovery check.");
            }
            var catalog = _catalog;
            var (before, _) = CaptureSnapshot(profile);
            _ = OpeningInventoryBaseline.Capture(before, targetItem.Id);
            run = new OpeningRun(token, session, profile, catalog, targetItem.Id);
            run.InitializeCleanup(CreateRunCleanup(run));
            _active = run;

            if (!_overlay.ShowConfirmation(
                    catalog.Rewards,
                    () => Confirm(run),
                    () => Cancel(run)))
            {
                throw new InvalidOperationException("Tarkov's UI event system is unavailable.");
            }

            StartCatalogSpriteLoads(run, catalog.Rewards);
            Debug($"Opening confirmation acquired for case {targetItem.Id} on profile {profile.Id}.");
            return run.Completion.Task;
        }
        catch (Exception exception)
        {
            _log.LogError($"Contraband Cases could not prepare confirmation: {exception}");
            if (run is not null)
            {
                run.Completion.TrySetResult(new FailedResult("Contraband Cases is not ready to open this case."));
                RetirePhase(run);
                run.Cleanup.Exit(OpeningRunExit.Failure, operationPending: false);
            }
            else
            {
                _phase.TryFail(token);
                _phase.TryClose(token);
                try
                {
                    _overlay.EndRun();
                }
                catch (Exception cleanupException)
                {
                    _log.LogError($"Contraband Cases confirmation cleanup failed: {cleanupException}");
                }
            }

            return RejectOpen("Contraband Cases is not ready to open this case.");
        }
    }

    internal bool TryStartCosmeticSelfTest(
        bool testingMode,
        CosmeticOutcomeSelection selection,
        double requestedDurationSeconds,
        out string message)
    {
        var catalogReady = _catalogState == CatalogReadinessState.Ready && _catalog is not null;
        if (!CosmeticSelfTestPolicy.CanStart(
                testingMode,
                _disposed,
                _active is not null || _manifest.IsBusy,
                _selfTestActive,
                catalogReady))
        {
            message = !testingMode
                ? "Turn on Enable previews under Animation preview in MCM before playing a preview."
                : _disposed
                    ? "Contraband Cases is shutting down."
                    : _active is not null || _manifest.IsBusy
                        ? "Close the active BR-12 opening before running the cosmetic self-test."
                        : _selfTestActive
                            ? "A cosmetic roulette self-test is already active."
                            : "The cosmetic self-test is waiting for a valid local reward catalog.";
            return false;
        }

        if (_startupRecovery.State != RelayStartupRecoveryState.Ready)
        {
            message = "Wait for the authenticated Relay recovery check before opening a cosmetic preview.";
            return false;
        }

        if (Singleton<GameWorld>.Instantiated)
        {
            message = "The cosmetic roulette self-test is available only outside a raid.";
            return false;
        }

        try
        {
            var catalog = _catalog!;
            if (CosmeticPreviewPlanner.TryCreateRelay(selection, out var relayPreview))
            {
                if (!_overlay.ShowCosmeticRelayPreview(
                        relayPreview!,
                        catalog.Rewards,
                        EndCosmeticSelfTest))
                {
                    message = "Tarkov's UI event system is unavailable for the cosmetic Relay preview.";
                    return false;
                }

                _selfTestActive = true;
                _overlay.PlayOutcome(relayPreview!.Kind switch
                {
                    CosmeticRelayPreviewKind.Upgrade => ManifestRelayResult.Upgrade,
                    CosmeticRelayPreviewKind.Sidegrade => ManifestRelayResult.Sidegrade,
                    _ => ManifestRelayResult.Confiscated
                }, RewardRarity.ScavGrade);
                LifecycleDiagnostic($"Started cosmetic-only Relay {relayPreview!.Kind} preview.");
                message = "Cosmetic Relay preview started. No server request, item, odds, inventory, or profile state will change.";
                return true;
            }

            var sequence = unchecked(++_selfTestSequence);
            var selected = CosmeticSelfTestPolicy.SelectReward(
                catalog.Rewards,
                selection,
                sequence);
            var pool = catalog.Rewards.Select(reward => reward.Id).ToArray();
            var plan = RouletteRevealPlan.Create(
                pool,
                selected.Id,
                tileCount: RouletteOverlay.RevealTileCount,
                landingIndex: RouletteOverlay.LandingIndex,
                seed: StableSeed($"cosmetic:{sequence}:{selected.Id}"),
                viewportWidth: RouletteOverlay.ViewportWidth,
                tileWidth: RouletteOverlay.TileWidth,
                tileSpacing: RouletteOverlay.TileSpacing,
                reducedMotion: false);
            if (!_overlay.ShowCosmeticSelfTest(
                    plan.Strip,
                    catalog.Rewards,
                    CompleteCosmeticSelfTest))
            {
                message = "Tarkov's UI event system is unavailable for the cosmetic self-test.";
                return false;
            }

            _selfTestPlan = plan;
            _overlay.SetStripX((float)plan.StartX);
            _selfTestReward = selected;
            _selfTestActive = true;
            var duration = CosmeticSelfTestPolicy.NormalizeDuration(requestedDurationSeconds);
            _selfTestCoroutine = _coroutineHost.StartCoroutine(CosmeticSelfTestLoop(duration));
            LifecycleDiagnostic(
                $"Started cosmetic-only roulette self-test for '{selected.Id}' over {duration:0.##} seconds.");
            message = "Cosmetic roulette self-test started. No items or profile state will change.";
            return true;
        }
        catch (Exception exception)
        {
            _log.LogError($"Contraband Cases cosmetic self-test could not start: {exception}");
            EndCosmeticSelfTest();
            message = "The cosmetic roulette self-test could not be shown. Check the BepInEx log.";
            return false;
        }
    }

    private IEnumerator CosmeticSelfTestLoop(double durationSeconds)
    {
        var elapsed = 0d;
        var lastTickIndex = 0;
        while (_selfTestActive && elapsed < durationSeconds)
        {
            try
            {
                var plan = _selfTestPlan
                    ?? throw new InvalidOperationException("The cosmetic self-test reveal plan is unavailable.");
                elapsed = Math.Min(durationSeconds, elapsed + Time.unscaledDeltaTime);
                var stripX = (float)plan.PositionAt(elapsed / durationSeconds * plan.DurationSeconds);
                _overlay.SetStripX(stripX);
                _overlay.AdvanceSpinAudio(stripX, (float)(elapsed / durationSeconds), ref lastTickIndex);
            }
            catch (Exception exception)
            {
                _selfTestCoroutine = null;
                _log.LogError($"Contraband Cases cosmetic self-test presentation detached: {exception}");
                EndCosmeticSelfTest();
                yield break;
            }

            yield return null;
        }

        _selfTestCoroutine = null;
        CompleteCosmeticSelfTest();
    }

    private void CompleteCosmeticSelfTest()
    {
        if (!_selfTestActive)
        {
            return;
        }

        if (_selfTestCoroutine is not null)
        {
            _coroutineHost.StopCoroutine(_selfTestCoroutine);
            _selfTestCoroutine = null;
        }

        try
        {
            var plan = _selfTestPlan
                ?? throw new InvalidOperationException("The cosmetic self-test reveal plan is unavailable.");
            var reward = _selfTestReward
                ?? throw new InvalidOperationException("The cosmetic self-test reward is unavailable.");
            _overlay.SetStripX((float)plan.FinalX);
            if (!_overlay.ShowCosmeticSelfTestResult(reward, EndCosmeticSelfTest))
            {
                EndCosmeticSelfTest();
            }
            else
            {
                _overlay.PlayOutcome(null, reward.Rarity);
            }
        }
        catch (Exception exception)
        {
            _log.LogError($"Contraband Cases cosmetic self-test could not finish: {exception}");
            EndCosmeticSelfTest();
        }
    }

    private void EndCosmeticSelfTest()
    {
        if (_selfTestCoroutine is not null)
        {
            try
            {
                _coroutineHost.StopCoroutine(_selfTestCoroutine);
            }
            catch (Exception exception)
            {
                _log.LogError($"Contraband Cases could not stop its cosmetic self-test coroutine: {exception}");
            }

            _selfTestCoroutine = null;
        }

        _selfTestPlan = null;
        _selfTestReward = null;
        _selfTestActive = false;
        _overlay.EndRun();
        LifecycleDiagnostic("Cosmetic-only roulette self-test ended.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        if (_selfTestActive)
        {
            EndCosmeticSelfTest();
        }
        if (_controllerLoop is not null)
        {
            try
            {
                _coroutineHost.StopCoroutine(_controllerLoop);
            }
            catch (Exception exception)
            {
                _log.LogError($"Contraband Cases could not stop its lifecycle monitor cleanly: {exception}");
            }

            _controllerLoop = null;
        }
        if (_startupRecoveryCoroutine is not null)
        {
            try
            {
                _coroutineHost.StopCoroutine(_startupRecoveryCoroutine);
            }
            catch (Exception exception)
            {
                _log.LogError($"Contraband Cases could not stop its Relay recovery probe cleanly: {exception}");
            }

            _startupRecoveryCoroutine = null;
        }
        _startupRecovery.Dispose();

        _manifest.Dispose();

        var run = _active;
        if (run is not null)
        {
            var operationPending = run.OperationCallbacks.IsPending;
            if (!operationPending)
            {
                run.Completion.TrySetResult(new FailedResult("Contraband Cases presentation was closed."));
                RetirePhase(run);
            }

            run.Cleanup.Exit(OpeningRunExit.Shutdown, operationPending);
        }

        _phase.Dispose();
        _catalogState = CatalogReadinessState.Disposed;
        _spriteTasks.Clear();
        _sprites.Clear();
        try
        {
            _overlay.Dispose();
        }
        catch (Exception exception)
        {
            _log.LogError($"Contraband Cases overlay destruction recovered from an error: {exception}");
        }
    }

    private void Confirm(OpeningRun run)
    {
        if (!IsCurrent(run) || !_phase.TryConfirm(run.Token))
        {
            return;
        }

        var operationGeneration = 0L;
        try
        {
            _overlay.ShowPending(run.Catalog.Rewards);
            run.PendingCoroutine = _coroutineHost.StartCoroutine(PendingLoop(run));

            var activeProfile = run.Session.Profile
                ?? throw new InventorySnapshotException("The authenticated profile disappeared before enqueue.");
            if (!ReferenceEquals(activeProfile, run.Profile) ||
                !string.Equals(activeProfile.Id, run.Profile.Id, StringComparison.Ordinal))
            {
                throw new InventorySnapshotException("The authenticated profile changed before enqueue.");
            }

            var (dispatchSnapshot, _) = CaptureSnapshot(activeProfile);
            run.Baseline = OpeningInventoryBaseline.CaptureForDispatch(
                run.Profile.Id,
                dispatchSnapshot,
                run.CaseItemId);
            operationGeneration = run.OperationCallbacks.Begin();
            _dispatcher.Dispatch(
                run.Session,
                run.Profile,
                run.Profile.Id,
                run.CaseItemId,
                result => OnBackendCallback(run.Token, operationGeneration, result));
        }
        catch (Exception exception)
        {
            _log.LogError($"Contraband Cases dispatch failed before enqueue: {exception}");
            if (operationGeneration != 0 && !run.OperationCallbacks.TryCancel(operationGeneration))
            {
                return;
            }
            FailRun(
                run,
                "The opening request could not be sent. The case and key were not intentionally consumed.",
                new FailedResult("Contraband Cases dispatch failed."));
        }
    }

    private void Cancel(OpeningRun run)
    {
        if (!IsCurrent(run) || !_phase.TryCancel(run.Token))
        {
            return;
        }

        run.Completion.TrySetResult(new FailedResult("Contraband Cases opening was cancelled."));
        run.Cleanup.Exit(OpeningRunExit.ConfirmationCancel, operationPending: false);
    }

    private void OnBackendCallback(long token, long operationGeneration, IResult result)
    {
        var run = _active;
        if (run is null ||
            run.Token != token ||
            !run.OperationCallbacks.TryClaim(operationGeneration))
        {
            Debug($"Ignored stale or duplicate opening callback for generation {token}.");
            return;
        }

        var effectiveResult = result ?? new FailedResult("Contraband Cases received an empty backend result.");
        if (_disposed)
        {
            run.Completion.TrySetResult(effectiveResult);
            run.Cleanup.ReleaseGate();
            return;
        }

        if (effectiveResult.Failed || !effectiveResult.Succeed)
        {
            FailRun(
                run,
                "The server rejected the opening. The case and key should remain in inventory.",
                effectiveResult);
            return;
        }

        try
        {
            if (!ReferenceEquals(run.Session.Profile, run.Profile))
            {
                throw new InventorySnapshotException("The active profile object changed during settlement.");
            }

            var baseline = run.Baseline
                ?? throw new InventorySnapshotException("No dispatch-time inventory baseline was captured.");
            var (after, liveItems) = CaptureSnapshot(run.Profile);
            var committed = InventorySnapshotReconciler.Reconcile(baseline, after, run.Catalog.Rewards);
            if (!liveItems.TryGetValue(committed.RootItemId, out var committedRoot))
            {
                throw new InventorySnapshotException("The committed reward root is unavailable for presentation.");
            }

            if (!_phase.TryCommit(run.Token))
            {
                throw new InvalidOperationException("The opening state changed before the committed callback was processed.");
            }

            run.Committed = committed;
            run.CommittedRoot = committedRoot;
            run.Completion.TrySetResult(effectiveResult);
            StopPending(run);

            if (run.Cleanup.PresentationDetached)
            {
                _phase.TryComplete(run.Token);
                Secure(run, showPresentation: false);
                return;
            }

            BeginReveal(run);
        }
        catch (Exception exception)
        {
            _log.LogError($"The reward was committed but its reveal could not be identified: {exception}");
            run.Completion.TrySetResult(effectiveResult);
            if (run.Committed is not null &&
                _phase.Phase is OpeningPhase.Revealing or OpeningPhase.Result)
            {
                HandleRevealPresentationFailure(run, exception);
                return;
            }
            FailRun(
                run,
                "The server committed the opening, but the reward reveal could not be identified. Check your stash and sorting table.",
                effectiveResult);
        }
    }

    private void BeginReveal(OpeningRun run)
    {
        var committed = run.Committed
            ?? throw new InvalidOperationException("A committed reward is required before reveal.");
        var pool = run.Catalog.Rewards.Select(reward => reward.Id).ToArray();
        var plan = RouletteRevealPlan.Create(
            pool,
            committed.Reward.Id,
            tileCount: RouletteOverlay.RevealTileCount,
            landingIndex: RouletteOverlay.LandingIndex,
            seed: StableSeed(run.CaseItemId),
            viewportWidth: RouletteOverlay.ViewportWidth,
            tileWidth: RouletteOverlay.TileWidth,
            tileSpacing: RouletteOverlay.TileSpacing,
            reducedMotion: _config.ReducedMotionDefault,
            baseDurationSeconds: _config.AnimationDurationSeconds);
        run.RevealPlan = plan;
        StartRevealWhenSpritesReady(
            run,
            plan,
            () => _overlay.ShowReveal(plan.Strip, run.Catalog.Rewards, () => Skip(run)));
    }

    private IEnumerator PendingLoop(OpeningRun run)
    {
        try
        {
            while (IsCurrent(run) &&
                   _phase.Phase == OpeningPhase.Pending &&
                   !run.Cleanup.PresentationDetached)
            {
                Exception? failure = null;
                try
                {
                    var delta = Time.unscaledDeltaTime;
                    run.RevealClock.AdvancePending(delta);
                    _overlay.AdvancePending(delta);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }

                if (failure is not null)
                {
                    run.PendingCoroutine = null;
                    _log.LogError($"Contraband Cases pending presentation detached after an error: {failure}");
                    run.Cleanup.DetachPresentation();
                    yield break;
                }

                yield return null;
            }
        }
        finally
        {
            run.PendingCoroutine = null;
        }
    }

    private IEnumerator ScrollingReveal(OpeningRun run)
    {
        RouletteRevealPlan? plan = null;
        Exception? setupFailure = null;
        try
        {
            plan = run.RevealPlan
                ?? throw new InvalidOperationException("A reveal plan is required before scrolling.");
            _overlay.SetStripX((float)plan.StartX);
        }
        catch (Exception exception)
        {
            setupFailure = exception;
        }

        if (setupFailure is not null)
        {
            run.RevealCoroutine = null;
            HandleRevealPresentationFailure(run, setupFailure);
            yield break;
        }

        try
        {
            // Not the raw config value: plan.DurationSeconds already folded in a small per-seed jitter
            // (see RouletteAnimationMath.SpinDurationSeconds) so consecutive openings don't all take the
            // exact same number of seconds to land -- purely cosmetic pacing, the landing itself (FinalX)
            // is unaffected.
            var duration = plan!.DurationSeconds;
            while (IsCurrent(run) &&
                   _phase.Phase == OpeningPhase.Revealing &&
                   run.RevealClock.RevealElapsedSeconds < duration)
            {
                Exception? frameFailure = null;
                try
                {
                    run.RevealClock.AdvanceReveal(Time.unscaledDeltaTime);
                    _overlay.SetStripX((float)plan!.PositionAt(run.RevealClock.RevealElapsedSeconds));
                    _overlay.SetSkipEnabled(
                        run.RevealClock.CanSkip &&
                        _phase.CanSkip(
                            run.Token,
                            run.RevealClock.RevealElapsedSeconds));
                }
                catch (Exception exception)
                {
                    frameFailure = exception;
                }

                if (frameFailure is not null)
                {
                    run.RevealCoroutine = null;
                    HandleRevealPresentationFailure(run, frameFailure);
                    yield break;
                }

                yield return null;
            }

            if (IsCurrent(run) && _phase.TryComplete(run.Token))
            {
                try
                {
                    _overlay.SetStripX((float)plan!.FinalX);
                    FinishReveal(run);
                }
                catch (Exception exception)
                {
                    run.RevealCoroutine = null;
                    HandleRevealPresentationFailure(run, exception);
                }
            }
        }
        finally
        {
            run.RevealCoroutine = null;
        }
    }

    private IEnumerator ReducedMotionReveal(OpeningRun run)
    {
        RouletteRevealPlan? plan = null;
        Exception? setupFailure = null;
        try
        {
            plan = run.RevealPlan
                ?? throw new InvalidOperationException("A reveal plan is required before reduced-motion reveal.");
            _overlay.SetStripX((float)plan.FinalX);
            _overlay.SetSkipEnabled(false);
        }
        catch (Exception exception)
        {
            setupFailure = exception;
        }

        if (setupFailure is not null)
        {
            run.RevealCoroutine = null;
            HandleRevealPresentationFailure(run, setupFailure);
            yield break;
        }

        try
        {
            const double fadeDuration = 0.45d;
            var elapsed = 0d;
            while (IsCurrent(run) && _phase.Phase == OpeningPhase.Revealing && elapsed < fadeDuration)
            {
                Exception? frameFailure = null;
                try
                {
                    elapsed = Math.Min(fadeDuration, elapsed + Time.unscaledDeltaTime);
                    _overlay.SetPresentationAlpha((float)(elapsed / fadeDuration));
                }
                catch (Exception exception)
                {
                    frameFailure = exception;
                }

                if (frameFailure is not null)
                {
                    run.RevealCoroutine = null;
                    HandleRevealPresentationFailure(run, frameFailure);
                    yield break;
                }

                yield return null;
            }

            try
            {
                _overlay.SetPresentationAlpha(1f);
                if (IsCurrent(run) && _phase.TryComplete(run.Token))
                {
                    FinishReveal(run);
                }
            }
            catch (Exception exception)
            {
                run.RevealCoroutine = null;
                HandleRevealPresentationFailure(run, exception);
            }
        }
        finally
        {
            run.RevealCoroutine = null;
        }
    }

    private void Skip(OpeningRun run)
    {
        if (!IsCurrent(run) ||
            !run.RevealClock.CanSkip ||
            !_phase.TrySkip(run.Token, run.RevealClock.RevealElapsedSeconds))
        {
            return;
        }

        try
        {
            StopReveal(run);
            var plan = run.RevealPlan
                ?? throw new InvalidOperationException("A reveal plan is required before skip.");
            _overlay.SetStripX((float)plan.FinalX);
            FinishReveal(run);
        }
        catch (Exception exception)
        {
            HandleRevealPresentationFailure(run, exception);
        }
    }

    private void FinishReveal(OpeningRun run)
    {
        var committed = run.Committed
            ?? throw new InvalidOperationException("A committed reward is required for the result panel.");
        _sprites.TryGetValue(committed.Reward.Id, out var sprite);
        if (sprite == null)
        {
            _sprites.Remove(committed.Reward.Id);
            sprite = null;
        }
        if (run.RelayReceipt is null)
        {
            BeginDecisionFetch(run, announceLoading: true);
            return;
        }

        var outcome = RelaySnapshotEnvelope.ParseOutcome(run.RelayReceipt);
        if (RelayTerminalPolicy.After(outcome, run.RelayReceipt.Terminal) == RelayClientDestination.NextDecision)
        {
            var nextSnapshot = run.NextDecisionSnapshot
                ?? throw new RelaySnapshotException("The next Relay decision was not verified before its reveal.");
            run.DecisionSnapshot = nextSnapshot;
            run.NextDecisionSnapshot = null;
            if (!ShowDecision(run))
            {
                SecureAfterPresentationFailure(run, "The next Relay decision could not be displayed.");
            }
            return;
        }

        run.Terminal = true;
        if (!_overlay.ShowRelayTerminal(committed.Reward, sprite, run.RelayReceipt, () => Close(run)))
        {
            CloseTerminalWithoutPresentation(run);
        }
    }

    private void BeginDecisionFetch(OpeningRun run, bool announceLoading)
    {
        if (!IsCurrent(run) || _phase.Phase != OpeningPhase.Result)
        {
            return;
        }

        var committed = run.Committed
            ?? throw new InvalidOperationException("A committed reward is required before loading Relay status.");
        if (announceLoading)
        {
            _sprites.TryGetValue(committed.Reward.Id, out var sprite);
            if (!_overlay.ShowRelayStatusLoading(committed.Reward, sprite, () => Secure(run)))
            {
                SecureAfterPresentationFailure(run, "The Relay decision could not be displayed.");
                return;
            }
        }

        StartSnapshotRequest(
            run,
            committed.RootItemId,
            snapshot => HandleDecisionSnapshot(run, snapshot),
            exception =>
            {
                _log.LogError($"Contraband Cases could not load authoritative Relay status: {exception}");
                TryNotifyWarning("Relay status could not be loaded. Contraband Cases is securing the committed reward.");
                Secure(run, showPresentation: false);
            });
    }

    private void HandleDecisionSnapshot(OpeningRun run, RelaySnapshot snapshot)
    {
        if (!IsCurrent(run) || _phase.Phase != OpeningPhase.Result)
        {
            return;
        }

        ValidateSnapshotIdentity(run, snapshot);
        var recovery = RelaySnapshotRecovery.Decide(snapshot);
        if (recovery != RelayPendingRecovery.None)
        {
            run.DecisionSnapshot = snapshot;
            if (recovery == RelayPendingRecovery.ResumeSecure)
            {
                run.PendingAction = ClientRelayAction.Secure;
                run.PendingStakeRootId = snapshot.Status.StakeRootId;
                Secure(run, showPresentation: true, resumePrepared: true);
                return;
            }

            throw new RelaySnapshotException(
                "A prepared Relay mutation cannot be resumed without its original displayed candidate snapshot.");
        }

        if (snapshot.LatestReceipt is not null)
        {
            throw new RelaySnapshotException("The current reward was already settled before its decision could be shown.");
        }

        run.DecisionSnapshot = snapshot;
        if (!ShowDecision(run))
        {
            SecureAfterPresentationFailure(run, "The Relay decision could not be displayed.");
        }
    }

    private bool ShowDecision(OpeningRun run)
    {
        var committed = run.Committed
            ?? throw new InvalidOperationException("A committed reward is required for a Relay decision.");
        var snapshot = run.DecisionSnapshot
            ?? throw new RelaySnapshotException("The authoritative Relay decision snapshot is unavailable.");
        ValidateSnapshotIdentity(run, snapshot);
        var (local, liveItems) = CaptureSnapshot(run.Profile);
        if (!liveItems.ContainsKey(committed.RootItemId))
        {
            throw new InventorySnapshotException("The displayed Relay stake is no longer in local inventory.");
        }

        var hasKey = RelayInventoryBaseline.HasKey(local);
        var availability = !hasKey
            ? RelayDecisionAvailability.Disabled("RELAY — KEY REQUIRED")
            : run.RelayDisabledReason is not null
                ? RelayDecisionAvailability.Disabled(run.RelayDisabledReason)
                : RelayDecisionAvailability.Available();
        _sprites.TryGetValue(committed.Reward.Id, out var sprite);
        return _overlay.ShowRelayDecision(
            committed.Reward,
            sprite,
            snapshot,
            availability,
            () => Secure(run),
            () => Relay(run));
    }

    private void Secure(
        OpeningRun run,
        bool showPresentation = true,
        bool resumePrepared = false)
    {
        if (!IsCurrent(run) || run.Terminal)
        {
            return;
        }

        if (_phase.Phase == OpeningPhase.Result)
        {
            if (!_phase.TryContinue(run.Token))
            {
                return;
            }
            CancelSnapshotObservation(run);
        }
        else if (!resumePrepared || _phase.Phase != OpeningPhase.Pending)
        {
            return;
        }

        var committed = run.Committed
            ?? throw new InvalidOperationException("A committed reward is required to secure a Relay chain.");
        PrepareAutomaticSecureSnapshots(
            resumePrepared,
            showPresentation,
            run.RelayReceipt,
            committed.RootItemId,
            ref run.DecisionSnapshot,
            ref run.NextDecisionSnapshot);

        run.PendingAction = ClientRelayAction.Secure;
        run.PendingStakeRootId = committed.RootItemId;
        if (!resumePrepared)
        {
            run.PendingRetryCount = 0;
            run.PendingVerificationRecovery = false;
            run.VerificationRetries.Reset();
            run.RelayBaseline = null;
        }

        if (showPresentation && !run.Cleanup.PresentationDetached)
        {
            try
            {
                _overlay.ShowRelayPending(run.Catalog.Rewards, "SECURING REWARD");
                StartPendingLoopIfNeeded(run);
            }
            catch (Exception exception)
            {
                _log.LogError($"Secure presentation detached before dispatch: {exception}");
                run.Cleanup.DetachPresentation();
            }
        }

        DispatchPendingRelayAction(run);
    }

    private void Relay(OpeningRun run, bool resumePrepared = false)
    {
        if (!IsCurrent(run) || run.Terminal)
        {
            return;
        }

        if (!resumePrepared)
        {
            var decision = run.DecisionSnapshot
                ?? throw new RelaySnapshotException("Relay requires a verified decision snapshot.");
            if (!decision.Status.RelayEligible || decision.Status.SettlementPending)
            {
                TryNotifyWarning("This reward is not eligible for another Relay.");
                return;
            }
            if (run.RelayDisabledReason is not null)
            {
                TryNotifyWarning("Relay is disabled for this rejected stake. Secure the reward instead.");
                return;
            }

            var (before, _) = CaptureSnapshot(run.Profile);
            RelayInventoryBaseline baseline;
            try
            {
                baseline = RelayInventoryBaseline.Capture(before, decision.Status.StakeRootId);
            }
            catch (InventorySnapshotException exception)
            {
                TryNotifyWarning(exception.Message);
                if (!ShowDecision(run))
                {
                    SecureAfterPresentationFailure(run, "The Relay decision could not be refreshed.");
                }
                return;
            }

            if (!_phase.TryContinue(run.Token))
            {
                return;
            }

            run.RelayBaseline = baseline;
            run.PendingStakeRootId = baseline.StakeRootId;
            run.PendingAction = ClientRelayAction.Relay;
            run.PendingRetryCount = 0;
            run.PendingVerificationRecovery = false;
            run.VerificationRetries.Reset();
        }
        else if (_phase.Phase != OpeningPhase.Pending || run.RelayBaseline is null)
        {
            throw new InvalidOperationException("A prepared Relay cannot resume without its original inventory baseline.");
        }

        if (!run.Cleanup.PresentationDetached)
        {
            _overlay.ShowRelayPending(run.Catalog.Rewards, "RELAY SETTLEMENT PENDING");
            StartPendingLoopIfNeeded(run);
        }

        DispatchPendingRelayAction(run);
    }

    private void DispatchPendingRelayAction(OpeningRun run)
    {
        if (!IsCurrent(run) || _phase.Phase != OpeningPhase.Pending ||
            run.PendingAction is null || string.IsNullOrWhiteSpace(run.PendingStakeRootId))
        {
            return;
        }

        var operationGeneration = 0L;
        try
        {
            var action = run.PendingAction == ClientRelayAction.Secure
                ? ModConstants.RelaySecureAction
                : ModConstants.RelayAction;
            operationGeneration = run.OperationCallbacks.Begin();
            _dispatcher.DispatchRelay(
                run.Session,
                run.Profile,
                run.Profile.Id,
                action,
                run.PendingStakeRootId,
                RelayInteractionOrigin.RealOpening,
                result => OnRelayOperationCallback(run.Token, operationGeneration, result));
        }
        catch (Exception exception)
        {
            _log.LogError($"Contraband Cases could not dispatch the Relay transition: {exception}");
            if (operationGeneration != 0 && !run.OperationCallbacks.TryCancel(operationGeneration))
            {
                return;
            }

            if (IsRestartPreparedAction(run))
            {
                FailCommittedRun(
                    run,
                    "The unfinished Relay settlement could not be resumed. Unpack a case to retry the authenticated recovery check.");
                return;
            }

            ReturnToDecisionAfterDispatchFailure(run);
        }
    }

    private void OnRelayOperationCallback(long token, long operationGeneration, IResult result)
    {
        var run = _active;
        if (run is null || run.Token != token ||
            !run.OperationCallbacks.TryClaim(operationGeneration))
        {
            Debug($"Ignored stale or duplicate Relay callback for generation {operationGeneration}.");
            return;
        }

        var effectiveResult = result ?? new FailedResult("Contraband Cases received an empty Relay result.");
        if (_disposed)
        {
            run.Cleanup.ReleaseGate();
            return;
        }

        run.LastMutationSucceeded = effectiveResult.Succeed && !effectiveResult.Failed;
        BeginMutationVerification(run);
    }

    private void HandleMutationSnapshot(OpeningRun run, RelaySnapshot snapshot)
    {
        if (!IsCurrent(run) || _phase.Phase != OpeningPhase.Pending || run.PendingAction is null)
        {
            return;
        }

        var expectedRecovery = run.PendingAction == ClientRelayAction.Secure
            ? RelayPendingRecovery.ResumeSecure
            : RelayPendingRecovery.ResumeRelay;
        var recovery = RelaySnapshotRecovery.Decide(snapshot);
        if (recovery != RelayPendingRecovery.None)
        {
            if (recovery != expectedRecovery)
            {
                throw new RelaySnapshotException("The server prepared a different Relay action than the client submitted.");
            }
            if (run.PendingRetryCount >= 1)
            {
                throw new RelaySnapshotException("The prepared Relay transition remained unresolved after its bounded retry.");
            }

            run.PendingRetryCount++;
            DispatchPendingRelayAction(run);
            return;
        }

        var receipt = snapshot.LatestReceipt;
        if (receipt is null)
        {
            if (IsRestartPreparedAction(run))
            {
                throw new RelaySnapshotException(
                    "The persisted Relay recovery returned neither its prepared action nor its committed receipt.");
            }
            if (!run.LastMutationSucceeded)
            {
                ReturnToDecisionAfterNoOpRejection(run, snapshot);
            }
            else
            {
                HandleMutationSnapshotFailure(
                    run,
                    new RelaySnapshotException(
                        "The accepted Relay transition has no pending marker or committed receipt."));
            }
            return;
        }
        var expectedAction = run.PendingAction == ClientRelayAction.Secure ? "Secure" : "Relay";
        if (!string.Equals(receipt.Action, expectedAction, StringComparison.Ordinal) ||
            !string.Equals(receipt.StakeRootId, run.PendingStakeRootId, StringComparison.Ordinal))
        {
            throw new RelaySnapshotException("The Relay receipt does not match the submitted action and stake.");
        }

        if (run.PendingAction == ClientRelayAction.Secure)
        {
            CompleteSecure(run, receipt);
        }
        else
        {
            CompleteRelay(run, receipt);
        }
    }

    private void CompleteSecure(OpeningRun run, RelayReceipt receipt)
    {
        if (RelaySnapshotEnvelope.ParseOutcome(receipt) != RelayOutcome.Secured)
        {
            throw new RelaySnapshotException("The Secure transition returned a contradictory receipt.");
        }

        var wasRestartPrepared = IsRestartPreparedAction(run);
        if (wasRestartPrepared)
        {
            var prepared = run.DecisionSnapshot
                ?? throw new RelaySnapshotException("The prepared Secure recovery snapshot is unavailable.");
            var (current, liveItems) = CaptureSnapshot(run.Profile);
            var recovered = RelayInventoryReconciler.ReconcileRecovered(
                run.Profile.Id,
                current,
                run.Catalog.Rewards,
                prepared,
                receipt) ?? throw new InventorySnapshotException(
                    "Recovered Secure did not retain its reward.");
            if (!liveItems.TryGetValue(recovered.RootItemId, out var recoveredRoot))
            {
                throw new InventorySnapshotException(
                    "The recovered Secure reward root is unavailable for presentation.");
            }

            run.Committed = recovered;
            run.CommittedRoot = recoveredRoot;
            CompletePreparedActionRecovery(
                run.PreparedActionRecovery,
                ref run.DecisionSnapshot);
        }

        run.RelayReceipt = receipt;
        run.Terminal = true;
        if (!_phase.TryResolvePending(run.Token))
        {
            throw new RelaySnapshotException("The Secure transition returned in an invalid phase.");
        }

        StopPending(run);
        if (run.Cleanup.PresentationDetached)
        {
            _phase.TryClose(run.Token);
            run.Cleanup.ReleaseGate();
            return;
        }

        var committed = run.Committed
            ?? throw new InvalidOperationException("The secured reward identity is unavailable.");
        _sprites.TryGetValue(committed.Reward.Id, out var sprite);
        if (!_overlay.ShowRelayTerminal(committed.Reward, sprite, receipt, () => Close(run)))
        {
            CloseTerminalWithoutPresentation(run);
        }
    }

    private void CompleteRelay(OpeningRun run, RelayReceipt receipt)
    {
        var decision = run.DecisionSnapshot
            ?? throw new RelaySnapshotException("The Relay candidate snapshot is unavailable.");
        var (after, liveItems) = CaptureSnapshot(run.Profile);
        var wasRestartPrepared = IsRestartPreparedAction(run);
        var committed = wasRestartPrepared
            ? RelayInventoryReconciler.ReconcileRecovered(
                run.Profile.Id,
                after,
                run.Catalog.Rewards,
                decision,
                receipt)
            : RelayInventoryReconciler.Reconcile(
                run.RelayBaseline
                    ?? throw new InventorySnapshotException("The Relay inventory baseline is unavailable."),
                after,
                run.Catalog.Rewards,
                decision,
                receipt);
        if (wasRestartPrepared)
        {
            CompletePreparedActionRecovery(
                run.PreparedActionRecovery,
                ref run.DecisionSnapshot);
        }
        var outcome = RelaySnapshotEnvelope.ParseOutcome(receipt);
        run.RelayReceipt = receipt;
        run.RelayBaseline = null;
        run.PendingAction = null;
        run.PendingStakeRootId = null;

        if (receipt.DeliveredToMessenger)
        {
            run.Committed = committed ?? throw new InventorySnapshotException("The mailed Relay reward identity is unavailable.");
            run.Terminal = true; // End this presentation, not the server's collected physical chain.
            if (!_phase.TryResolvePending(run.Token))
                throw new RelaySnapshotException("The mail delivery returned in an invalid phase.");
            StopPending(run);
            if (run.Cleanup.PresentationDetached)
            {
                _phase.TryClose(run.Token);
                run.Cleanup.ReleaseGate();
                return;
            }
            _sprites.TryGetValue(run.Committed.Reward.Id, out var mailSprite);
            if (!_overlay.ShowRelayTerminal(run.Committed.Reward, mailSprite, receipt, () => Close(run)))
                CloseTerminalWithoutPresentation(run);
            return;
        }

        if (outcome == RelayOutcome.Confiscated)
        {
            run.Terminal = true;
            if (!_phase.TryCommit(run.Token))
            {
                throw new InvalidOperationException("The Relay phase changed before confiscation presentation.");
            }
            StopPending(run);
            if (run.Cleanup.PresentationDetached)
            {
                _phase.TryComplete(run.Token);
                _phase.TryClose(run.Token);
                run.Cleanup.ReleaseGate();
                return;
            }

            _overlay.ShowConfiscationSuspense(receipt);
            run.RevealCoroutine = _coroutineHost.StartCoroutine(ConfiscationReveal(run));
            return;
        }

        var matched = committed
            ?? throw new InventorySnapshotException("The Relay receipt requires a reward output.");
        if (!liveItems.TryGetValue(matched.RootItemId, out var root))
        {
            throw new InventorySnapshotException("The verified Relay reward root is unavailable for presentation.");
        }

        run.Committed = matched;
        run.CommittedRoot = root;
        run.RelayDisabledReason = null;
        var destination = RelayTerminalPolicy.After(outcome, receipt.Terminal);
        if (destination == RelayClientDestination.Terminal)
        {
            run.Terminal = true;
        }
        if (run.Cleanup.PresentationDetached && destination == RelayClientDestination.Terminal)
        {
            if (_phase.TryResolvePending(run.Token))
            {
                _phase.TryClose(run.Token);
            }
            run.Cleanup.ReleaseGate();
            return;
        }

        if (destination == RelayClientDestination.NextDecision)
        {
            StartSnapshotRequest(
                run,
                matched.RootItemId,
                snapshot =>
                {
                    ValidateSnapshotIdentity(run, snapshot);
                    if (snapshot.LatestReceipt is not null ||
                        RelaySnapshotRecovery.Decide(snapshot) != RelayPendingRecovery.None ||
                        !snapshot.Status.RelayEligible ||
                        snapshot.Status.Stage != receipt.Stage + 1 ||
                        snapshot.Status.RecoveryMeter != receipt.RecoveryMeter)
                    {
                        throw new RelaySnapshotException("The upgraded reward did not expose a fresh eligible Relay decision.");
                    }

                    run.NextDecisionSnapshot = snapshot;
                    if (run.Cleanup.PresentationDetached)
                    {
                        if (_phase.TryResolvePending(run.Token))
                        {
                            run.DecisionSnapshot = snapshot;
                            run.NextDecisionSnapshot = null;
                            Secure(run, showPresentation: false);
                        }
                        return;
                    }

                    BeginRelayRewardReveal(
                        run,
                        outcome,
                        decision,
                        wasRestartPrepared);
                },
                exception =>
                {
                    _log.LogError($"The upgraded reward was committed but its next Relay state could not be read: {exception}");
                    if (_phase.TryResolvePending(run.Token))
                    {
                        TryNotifyWarning("The upgraded reward is safe; its next Relay state could not be loaded, so it will be secured.");
                        Secure(run, showPresentation: false);
                    }
                    else if (_phase.Phase == OpeningPhase.Revealing)
                    {
                        HandleRevealPresentationFailure(run, exception);
                    }
                });
            return;
        }

        BeginRelayRewardReveal(run, outcome, decision, wasRestartPrepared);
    }

    private void BeginRelayRewardReveal(
        OpeningRun run,
        RelayOutcome outcome,
        RelaySnapshot decision,
        bool recoveredPreparedOutcome)
    {
        var committed = run.Committed
            ?? throw new InvalidOperationException("A Relay reward is required before reveal.");
        var pool = recoveredPreparedOutcome
            ? run.Catalog.Rewards
                .Where(reward =>
                    reward.Rarity == committed.Reward.Rarity &&
                    (outcome == RelayOutcome.RarityUpgrade ||
                     !string.Equals(reward.Id, decision.Status.RewardId, StringComparison.Ordinal)))
                .Select(reward => reward.Id)
                .ToArray()
            : (outcome == RelayOutcome.RarityUpgrade
                    ? decision.UpgradeCandidates
                    : decision.SidegradeCandidates)
                .Select(candidate => candidate.RewardId)
                .ToArray();
        if (!pool.Contains(committed.Reward.Id, StringComparer.Ordinal))
        {
            throw new RelaySnapshotException(
                "The recovered Relay reward is absent from its local rarity pool.");
        }
        var plan = RouletteRevealPlan.Create(
            pool,
            committed.Reward.Id,
            RouletteOverlay.RevealTileCount,
            RouletteOverlay.LandingIndex,
            StableSeed($"relay:{decision.Status.StakeRootId}:{decision.Status.Stage}:{committed.Reward.Id}"),
            RouletteOverlay.ViewportWidth,
            RouletteOverlay.TileWidth,
            RouletteOverlay.TileSpacing,
            _config.ReducedMotionDefault,
            baseDurationSeconds: _config.AnimationDurationSeconds);
        if (!_phase.TryCommit(run.Token))
        {
            throw new InvalidOperationException("The Relay phase changed before reward reveal.");
        }

        StopPending(run);
        run.RevealPlan = plan;
        StartRevealWhenSpritesReady(
            run,
            plan,
            () => _overlay.ShowRelayReveal(
                plan.Strip,
                run.Catalog.Rewards,
                outcome,
                () => Skip(run)));
    }

    private IEnumerator ConfiscationReveal(OpeningRun run)
    {
        const double duration = 1.4d;
        var elapsed = 0d;
        while (IsCurrent(run) && _phase.Phase == OpeningPhase.Revealing && elapsed < duration)
        {
            try
            {
                elapsed = Math.Min(duration, elapsed + Time.unscaledDeltaTime);
                _overlay.SetConfiscationProgress(elapsed / duration);
            }
            catch (Exception exception)
            {
                run.RevealCoroutine = null;
                HandleRevealPresentationFailure(run, exception);
                yield break;
            }
            yield return null;
        }

        run.RevealCoroutine = null;
        if (!IsCurrent(run) || !_phase.TryComplete(run.Token))
        {
            yield break;
        }

        run.Terminal = true;
        var receipt = run.RelayReceipt
            ?? throw new RelaySnapshotException("The confiscation receipt is unavailable.");
        try
        {
            if (!_overlay.ShowRelayTerminal(null, null, receipt, () => Close(run)))
            {
                CloseTerminalWithoutPresentation(run);
            }
        }
        catch (Exception exception)
        {
            HandleRevealPresentationFailure(run, exception);
        }
    }

    private void StartSnapshotRequest(
        OpeningRun run,
        string stakeRootId,
        Action<RelaySnapshot> success,
        Action<Exception> failure)
    {
        if (run.SnapshotCoroutine is not null)
        {
            throw new InvalidOperationException("A Relay snapshot request is already being observed.");
        }

        Task<RelaySnapshot> task;
        try
        {
            task = _snapshotTransport.FetchAsync(stakeRootId, RelayInteractionOrigin.RealOpening);
        }
        catch (Exception exception)
        {
            failure(exception);
            return;
        }

        StartSnapshotObservation(
            ref run.SnapshotCoroutine,
            () => _coroutineHost.StartCoroutine(
                SnapshotRequestLoop(run, task, success, failure)),
            exception => InvokeSnapshotFailure(run, failure, exception));
    }

    internal static void StartSnapshotObservation<TObservation>(
        ref TObservation? slot,
        Func<TObservation> start,
        Action<Exception> failure)
        where TObservation : class
    {
        if (start is null)
        {
            throw new ArgumentNullException(nameof(start));
        }
        if (failure is null)
        {
            throw new ArgumentNullException(nameof(failure));
        }
        if (slot is not null)
        {
            throw new InvalidOperationException("A Relay snapshot request is already being observed.");
        }
        try
        {
            slot = start()
                ?? throw new InvalidOperationException("The Relay snapshot observer did not start.");
        }
        catch (Exception exception)
        {
            slot = null;
            failure(exception);
        }
    }

    private IEnumerator SnapshotRequestLoop(
        OpeningRun run,
        Task<RelaySnapshot> task,
        Action<RelaySnapshot> success,
        Action<Exception> failure)
    {
        var timeout = new RelaySnapshotTimeoutBudget();
        // Ensure StartCoroutine returns and its handle is assigned before a synchronously
        // completed HTTP task can clear the observation slot.
        yield return null;
        while (!task.IsCompleted && IsCurrent(run))
        {
            if (timeout.Advance(Time.unscaledDeltaTime))
            {
                break;
            }
            yield return null;
        }

        run.SnapshotCoroutine = null;
        if (!IsCurrent(run))
        {
            yield break;
        }

        if (!task.IsCompleted)
        {
            InvokeSnapshotFailure(
                run,
                failure,
                new RelaySnapshotException(
                    $"The Relay snapshot request timed out after {RelaySnapshotTimeoutBudget.DefaultTimeoutSeconds:0} seconds."));
            yield break;
        }

        try
        {
            success(task.GetAwaiter().GetResult());
        }
        catch (Exception exception)
        {
            InvokeSnapshotFailure(run, failure, exception);
        }
    }

    private void InvokeSnapshotFailure(
        OpeningRun run,
        Action<Exception> failure,
        Exception exception)
    {
        try
        {
            failure(exception);
        }
        catch (Exception nested)
        {
            _log.LogError($"Contraband Cases Relay recovery failed: {nested}");
            FailCommittedRun(run, "The Relay state could not be safely reconciled. Check your stash and BepInEx log.");
        }
    }

    private void HandleMutationSnapshotFailure(OpeningRun run, Exception exception)
    {
        _log.LogError($"Contraband Cases could not verify the Relay mutation receipt: {exception}");
        if (!IsCurrent(run))
        {
            return;
        }

        if (run.RelayReceipt is not null)
        {
            HandleRevealPresentationFailure(run, exception);
            return;
        }

        if (_phase.Phase != OpeningPhase.Pending || run.PendingAction is null)
        {
            return;
        }

        if (run.VerificationRetries.TryTakeAutomaticRetry())
        {
            Debug("Retrying the authoritative Relay receipt query once after a transport failure.");
            BeginMutationVerification(run);
            return;
        }

        var message = run.LastMutationSucceeded
            ? "The item operation returned success, but its authoritative Relay receipt is temporarily unavailable."
            : "The Relay transition could not be verified as committed or rejected.";
        StopPending(run);
        run.PendingVerificationRecovery = true;
        TryNotifyWarning($"{message} Use Retry Verification; no additional item mutation will be sent.");
        if (run.Cleanup.PresentationDetached)
        {
            CloseDetachedVerificationRecovery(run, message);
            return;
        }
        if (!run.Cleanup.PresentationDetached &&
            _overlay.ShowRelayVerificationError(message, () => RetryMutationVerification(run)))
        {
            return;
        }

        _log.LogError("Relay verification remains pending because its recovery panel is unavailable.");
    }

    private void BeginMutationVerification(OpeningRun run)
    {
        if (!IsCurrent(run) || _phase.Phase != OpeningPhase.Pending ||
            string.IsNullOrWhiteSpace(run.PendingStakeRootId))
        {
            return;
        }

        run.PendingVerificationRecovery = false;
        StartSnapshotRequest(
            run,
            run.PendingStakeRootId!,
            snapshot => HandleMutationSnapshot(run, snapshot),
            exception => HandleMutationSnapshotFailure(run, exception));
    }

    private void RetryMutationVerification(OpeningRun run)
    {
        if (!IsCurrent(run) || _phase.Phase != OpeningPhase.Pending ||
            run.SnapshotCoroutine is not null || run.OperationCallbacks.IsPending)
        {
            return;
        }

        try
        {
            if (!run.Cleanup.PresentationDetached)
            {
                _overlay.ShowRelayPending(run.Catalog.Rewards, "VERIFYING RELAY RECEIPT");
                StartPendingLoopIfNeeded(run);
            }
            BeginMutationVerification(run);
        }
        catch (Exception exception)
        {
            HandleMutationSnapshotFailure(run, exception);
        }
    }

    private void ReturnToDecisionAfterDispatchFailure(OpeningRun run)
    {
        var recovery = RelayDispatchFailurePolicy.Decide(
            run.DecisionSnapshot is not null,
            run.Cleanup.PresentationDetached);
        if (recovery == RelayDispatchFailureRecovery.RestoreVerifiedDecision)
        {
            ReturnToDecision(
                run,
                run.DecisionSnapshot!,
                "The item request was not enqueued. Nothing was consumed; you can Secure or try again.",
                disableRelay: false);
            return;
        }

        if (!_phase.TryResolvePending(run.Token))
        {
            throw new InvalidOperationException("The failed automatic Secure request could not leave its pending phase.");
        }

        StopPending(run);
        ClearPendingRelayState(run);
        const string message =
            "The automatic Secure request was not enqueued. No Relay key or reward was consumed by that attempt.";
        TryNotifyWarning(message);
        if (recovery == RelayDispatchFailureRecovery.ReleaseDetachedRun)
        {
            _phase.TryClose(run.Token);
            run.Cleanup.ReleaseGate();
            return;
        }

        if (_overlay.ShowRelayVerificationError(
                $"{message} Reload the authoritative Relay status before choosing again.",
                () => RetryDecisionSnapshotAfterDispatchFailure(run)))
        {
            return;
        }

        _log.LogError("The pre-decision Relay recovery panel is unavailable; releasing the opening gate.");
        _phase.TryClose(run.Token);
        run.Cleanup.Exit(OpeningRunExit.PresentationException, operationPending: false);
    }

    private void RetryDecisionSnapshotAfterDispatchFailure(OpeningRun run)
    {
        if (!IsCurrent(run) || _phase.Phase != OpeningPhase.Result || run.DecisionSnapshot is not null)
        {
            return;
        }

        try
        {
            BeginDecisionFetch(run, announceLoading: true);
        }
        catch (Exception exception)
        {
            _log.LogError($"Contraband Cases could not restart authoritative Relay status verification: {exception}");
            FailCommittedRun(
                run,
                "Relay status could not be reloaded safely. Check your stash and BepInEx log.");
        }
    }

    private void ReturnToDecisionAfterNoOpRejection(OpeningRun run, RelaySnapshot snapshot)
    {
        ValidateSnapshotIdentity(run, snapshot);
        var disableRelay = run.PendingAction == ClientRelayAction.Relay;
        ReturnToDecision(
            run,
            snapshot,
            disableRelay
                ? "The server rejected Relay without changing inventory. Secure remains available; Relay is disabled for this displayed stake."
                : "The server rejected Secure without changing inventory. The decision remains open so Secure can be retried.",
            disableRelay);
    }

    private void ReturnToDecision(
        OpeningRun run,
        RelaySnapshot snapshot,
        string message,
        bool disableRelay)
    {
        if (!_phase.TryResolvePending(run.Token))
        {
            throw new InvalidOperationException("The Relay rejection could not return to its decision phase.");
        }

        StopPending(run);
        run.DecisionSnapshot = snapshot;
        ClearPendingRelayState(run);
        if (disableRelay)
        {
            run.RelayDisabledReason = "RELAY — STAKE REJECTED; SECURE INSTEAD";
        }

        TryNotifyWarning(message);
        if (run.Cleanup.PresentationDetached)
        {
            if (!run.DetachedSecureAttempted)
            {
                run.DetachedSecureAttempted = true;
                Secure(run, showPresentation: false);
            }
            else
            {
                _phase.TryClose(run.Token);
                run.Cleanup.ReleaseGate();
            }
            return;
        }

        if (!ShowDecision(run))
        {
            SecureAfterPresentationFailure(run, "The recovered Relay decision could not be displayed.");
        }
    }

    private static void ClearPendingRelayState(OpeningRun run)
    {
        run.RelayBaseline = null;
        run.PendingAction = null;
        run.PendingStakeRootId = null;
        run.PendingRetryCount = 0;
        run.PendingVerificationRecovery = false;
        run.VerificationRetries.Reset();
        run.LastMutationSucceeded = false;
    }

    private static bool IsRestartPreparedAction(OpeningRun run) =>
        run.IsStartupRecovery && run.PreparedActionRecovery.IsPending;

    internal static void CompletePreparedActionRecovery(
        RelayPreparedActionRecovery recovery,
        ref RelaySnapshot? decisionSnapshot)
    {
        if (recovery is null)
        {
            throw new ArgumentNullException(nameof(recovery));
        }
        recovery.Complete(ref decisionSnapshot);
    }

    internal static void PrepareAutomaticSecureSnapshots(
        bool resumePrepared,
        bool showPresentation,
        RelayReceipt? receipt,
        string committedRootId,
        ref RelaySnapshot? decisionSnapshot,
        ref RelaySnapshot? nextDecisionSnapshot)
    {
        if (resumePrepared ||
            showPresentation ||
            receipt is null ||
            string.Equals(
                decisionSnapshot?.Status.StakeRootId,
                committedRootId,
                StringComparison.Ordinal))
        {
            return;
        }

        decisionSnapshot = nextDecisionSnapshot;
        nextDecisionSnapshot = null;
    }

    private void ValidateSnapshotIdentity(OpeningRun run, RelaySnapshot snapshot)
    {
        var committed = run.Committed
            ?? throw new RelaySnapshotException("The local committed reward identity is unavailable.");
        if (!string.Equals(snapshot.Status.StakeRootId, committed.RootItemId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Status.RewardId, committed.Reward.Id, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Status.Rarity, committed.Reward.Rarity.ToString(), StringComparison.Ordinal))
        {
            throw new RelaySnapshotException("The server snapshot does not match the locally applied reward.");
        }

        if (run.RelayReceipt is null && snapshot.Status.Stage != 1)
        {
            throw new RelaySnapshotException("A newly opened reward must begin at Relay stage one.");
        }

        RelayCandidateCatalogValidator.Validate(
            snapshot.UpgradeCandidates.Concat(snapshot.SidegradeCandidates),
            run.Catalog.Rewards);
    }

    private void StartPendingLoopIfNeeded(OpeningRun run)
    {
        if (run.PendingCoroutine is null)
        {
            run.PendingCoroutine = _coroutineHost.StartCoroutine(PendingLoop(run));
        }
    }

    private void FailCommittedRun(OpeningRun run, string message)
    {
        if (!IsCurrent(run))
        {
            return;
        }

        StopOwnedCoroutines(run);
        _phase.TryFail(run.Token);
        TryNotifyWarning(message);
        if (run.Cleanup.PresentationDetached || !_overlay.ShowError(message, () => Close(run)))
        {
            _phase.TryClose(run.Token);
            run.Cleanup.Exit(OpeningRunExit.PresentationException, operationPending: false);
        }
    }

    private void SecureAfterPresentationFailure(OpeningRun run, string message)
    {
        _log.LogError(message);
        TryNotifyWarning($"{message} The committed reward is being secured.");
        if (_phase.Phase == OpeningPhase.Revealing)
        {
            _phase.TryAbortRevealToResult(run.Token);
        }
        run.Cleanup.DetachPresentation();
        if (_phase.Phase == OpeningPhase.Result)
        {
            run.DetachedSecureAttempted = true;
            Secure(run, showPresentation: false);
        }
    }

    private void CloseDetachedVerificationRecovery(OpeningRun run, string message)
    {
        TryNotifyWarning(
            $"{message} The item screen closed, so the in-memory verifier was released. Check your stash and log before using the chain again.");
        _phase.TryFail(run.Token);
        _phase.TryClose(run.Token);
        run.Cleanup.ReleaseGate();
    }

    private void CloseTerminalWithoutPresentation(OpeningRun run)
    {
        _phase.TryClose(run.Token);
        run.Cleanup.Exit(OpeningRunExit.Success, operationPending: false);
    }

    private void Close(OpeningRun run)
    {
        if (!IsCurrent(run) || !_phase.TryClose(run.Token))
        {
            return;
        }

        run.Cleanup.Exit(
            run.Committed is null ? OpeningRunExit.Failure : OpeningRunExit.Success,
            operationPending: false);
    }

    private void FailRun(OpeningRun run, string message, IResult result)
    {
        if (!IsCurrent(run))
        {
            return;
        }

        try
        {
            StopOwnedCoroutines(run);
        }
        catch (Exception exception)
        {
            _log.LogError($"Contraband Cases could not stop every presentation coroutine: {exception}");
        }

        _phase.TryFail(run.Token);
        run.Completion.TrySetResult(result);
        if (run.Cleanup.PresentationDetached)
        {
            _phase.TryClose(run.Token);
            run.Cleanup.Exit(OpeningRunExit.Failure, operationPending: false);
            return;
        }

        try
        {
            if (_overlay.ShowError(message, () => Close(run)))
            {
                return;
            }
        }
        catch (Exception exception)
        {
            _log.LogError($"Contraband Cases could not show its failure panel: {exception}");
        }

        _phase.TryClose(run.Token);
        run.Cleanup.Exit(OpeningRunExit.PresentationException, operationPending: false);
    }

    private void HandleRevealPresentationFailure(OpeningRun run, Exception exception)
    {
        _log.LogError($"Contraband Cases reveal presentation failed after settlement: {exception}");
        if (run.RelayReceipt is not null && _phase.Phase == OpeningPhase.Pending)
        {
            if (!_phase.TryResolvePending(run.Token))
            {
                _log.LogError("The committed Relay result could not leave its pending phase after presentation failure.");
                _phase.TryFail(run.Token);
                _phase.TryClose(run.Token);
                run.Cleanup.Exit(OpeningRunExit.PresentationException, operationPending: false);
                return;
            }

            try
            {
                StopPending(run);
            }
            catch (Exception stopException)
            {
                _log.LogWarning($"Contraband Cases could not stop the failed Relay presentation loop: {stopException.Message}");
                run.PendingCoroutine = null;
            }
        }

        var terminal = run.RelayReceipt is not null &&
            RelayTerminalPolicy.After(
                RelaySnapshotEnvelope.ParseOutcome(run.RelayReceipt),
                run.RelayReceipt.Terminal) == RelayClientDestination.Terminal;
        if (terminal)
        {
            if (_phase.Phase == OpeningPhase.Revealing)
            {
                _phase.TryAbortRevealToResult(run.Token);
            }
            run.Terminal = true;
            CloseTerminalWithoutPresentation(run);
            TryNotifyWarning("The terminal Relay result was committed, but its reveal could not be displayed. Check your stash and BepInEx log.");
            return;
        }

        SecureAfterPresentationFailure(
            run,
            "The committed reward reveal failed before its Relay decision could be shown.");
    }

    private void StartRevealWhenSpritesReady(
        OpeningRun run,
        RouletteRevealPlan plan,
        Action showReveal)
    {
        var spriteTasks = StartSpriteLoads(run);
        var visibleRewardIds = new HashSet<string>(plan.Strip, StringComparer.Ordinal);
        var visibleTasks = spriteTasks
            .Where(pair => visibleRewardIds.Contains(pair.Key))
            .Select(pair => (Task)pair.Value)
            .ToArray();
        run.RevealCoroutine = _coroutineHost.StartCoroutine(PrepareRevealWhenSpritesReady(
            run,
            plan,
            spriteTasks,
            visibleRewardIds,
            visibleTasks,
            run.Cleanup.PresentationGeneration,
            showReveal));
    }

    private IEnumerator PrepareRevealWhenSpritesReady(
        OpeningRun run,
        RouletteRevealPlan plan,
        Dictionary<string, Task<Sprite>> spriteTasks,
        HashSet<string> visibleRewardIds,
        IReadOnlyCollection<Task> visibleTasks,
        long presentationGeneration,
        Action showReveal)
    {
        var elapsed = 0d;
        while (IsCurrent(run) &&
               _phase.Phase == OpeningPhase.Revealing &&
               run.Cleanup.CanBind(presentationGeneration))
        {
            Exception? failure = null;
            var prepared = false;
            try
            {
                elapsed = Math.Min(
                    SpriteRevealReadiness.MaximumWaitSeconds,
                    elapsed + Time.unscaledDeltaTime);
                prepared = SpriteRevealReadiness.TryPrepareFirstFrame(
                    visibleTasks,
                    elapsed,
                    () =>
                    {
                        CacheCompletedSpriteTasks(
                            run,
                            presentationGeneration,
                            spriteTasks);
                        if (elapsed >= SpriteRevealReadiness.MaximumWaitSeconds)
                        {
                            EvictTimedOutSpriteTasks(spriteTasks, visibleRewardIds);
                        }
                    },
                    showReveal,
                    () => BindCachedSpritesAndWatchPending(
                        run,
                        presentationGeneration,
                        spriteTasks));
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            if (failure is not null)
            {
                run.RevealCoroutine = null;
                HandleRevealPresentationFailure(run, failure);
                yield break;
            }

            if (!prepared)
            {
                yield return null;
                continue;
            }

            if (!IsCurrent(run) ||
                _phase.Phase != OpeningPhase.Revealing ||
                !run.Cleanup.CanBind(presentationGeneration))
            {
                run.RevealCoroutine = null;
                yield break;
            }

            run.RevealClock.BeginCommittedReveal();
            yield return plan.MotionMode == RouletteMotionMode.Fade
                ? ReducedMotionReveal(run)
                : ScrollingReveal(run);
            yield break;
        }

        run.RevealCoroutine = null;
    }

    // How long a confirmation-screen catalog batch is allowed to keep waiting on stragglers before its
    // remaining pending loads are evicted and left showing the "ITEM" fallback. Slightly longer than the
    // Manifest coordinator's own 5s sprite timeout since a legacy confirmation can be requesting artwork for
    // the full reward catalog (up to ~80 templates) in one batch rather than one lot's handful of tiles.
    private const double CatalogSpriteLoadTimeoutSeconds = 8d;

    /// Starts loading artwork for every reward in the legacy confirmation screen's catalog grid, binding
    /// each tile's icon as its sprite resolves. Every tile already shows its "ITEM" text fallback the moment
    /// the grid is built (RouletteOverlay.BuildCatalogGrid binds with sprite: null first) -- this only
    /// upgrades tiles to real artwork opportunistically, and never blocks or fails the confirmation itself
    /// if artwork can't be prepared.
    private void StartCatalogSpriteLoads(OpeningRun run, IReadOnlyList<ValidatedReward> rewards)
    {
        try
        {
            var tiles = rewards
                .Select(reward => new ManifestTilePresentation(
                    reward.Id,
                    reward.DisplayName,
                    "CATALOG ENTRY",
                    reward.Rarity,
                    reward.WeaponTemplateId))
                .ToArray();
            if (tiles.Length == 0)
            {
                return;
            }

            var plan = ManifestSpritePlan.Create(tiles, tiles[0].Id);
            var batch = ManifestSpriteLoadBatch<Sprite>.Start(plan, _catalogSpriteCache, ManifestItemSpriteLoader.LoadAsync);
            foreach (var failure in batch.StartFailures)
            {
                _log.LogWarning(
                    $"Catalog artwork for template '{failure.TemplateId}' could not start; its tile keeps the ITEM fallback: {failure.Exception.Message}");
            }

            if (batch.IsComplete)
            {
                return;
            }

            var generation = run.Cleanup.PresentationGeneration;
            var lease = new SpriteCoroutineLease();
            run.SpriteCoroutines.Add(lease);
            try
            {
                lease.Coroutine = _coroutineHost.StartCoroutine(BindCatalogSpritesWhenReady(run, lease, batch, generation));
            }
            catch
            {
                run.SpriteCoroutines.Remove(lease);
                throw;
            }
        }
        catch (Exception exception)
        {
            _log.LogWarning(
                $"Catalog artwork could not start; the confirmation screen will keep the ITEM fallback for every entry: {exception.Message}");
        }
    }

    private IEnumerator BindCatalogSpritesWhenReady(
        OpeningRun run,
        SpriteCoroutineLease lease,
        ManifestSpriteLoadBatch<Sprite> batch,
        long presentationGeneration)
    {
        var elapsed = 0d;
        yield return null;
        while (run.Cleanup.CanBind(presentationGeneration))
        {
            try
            {
                batch.BindAvailable(
                    () => run.Cleanup.CanBind(presentationGeneration),
                    (rewardId, sprite) => _overlay.BindCatalogSprite(rewardId, sprite),
                    (templateId, exception) => _log.LogWarning(
                        $"Catalog artwork for template '{templateId}' is unavailable; its tile keeps the ITEM fallback: {exception.Message}"));
            }
            catch (Exception exception)
            {
                _log.LogWarning(
                    $"Catalog artwork could not be applied; the confirmation screen will keep its ITEM fallbacks: {exception.Message}");
                break;
            }

            if (batch.IsComplete)
            {
                break;
            }

            elapsed = Math.Min(CatalogSpriteLoadTimeoutSeconds, elapsed + Time.unscaledDeltaTime);
            if (elapsed >= CatalogSpriteLoadTimeoutSeconds)
            {
                batch.EvictPendingLoads(templateId =>
                    _log.LogWarning($"Catalog artwork for template '{templateId}' timed out; its tile keeps the ITEM fallback."));
                break;
            }

            yield return null;
        }

        run.SpriteCoroutines.Remove(lease);
    }

    private Dictionary<string, Task<Sprite>> StartSpriteLoads(OpeningRun run)
    {
        var committed = run.Committed
            ?? throw new InvalidOperationException("A committed reward is required before sprite loading.");
        var runTasks = new Dictionary<string, Task<Sprite>>(StringComparer.Ordinal);
        foreach (var reward in run.Catalog.Rewards)
        {
            if (_spriteTasks.TryGetValue(reward.Id, out var task) &&
                SpriteRevealReadiness.ShouldEvict(task))
            {
                EvictSpriteTask(reward.Id, task);
                task = null;
            }

            if (task is null)
            {
                try
                {
                    var item = string.Equals(reward.Id, committed.Reward.Id, StringComparison.Ordinal)
                        ? run.CommittedRoot!
                        : run.Catalog.GetPresetRoot(reward.Id);
                    task = ItemViewFactory.GetItemSpriteAsync(item, 1)
                        ?? throw new InvalidOperationException("Tarkov returned an empty sprite task.");
                    _spriteTasks.Add(reward.Id, task);
                }
                catch (Exception exception)
                {
                    _log.LogWarning($"Could not start sprite load for '{reward.Id}': {exception.Message}");
                    continue;
                }
            }

            runTasks.Add(reward.Id, task);
        }

        return runTasks;
    }

    private void CacheCompletedSpriteTasks(
        OpeningRun run,
        long presentationGeneration,
        Dictionary<string, Task<Sprite>> spriteTasks)
    {
        List<string>? unavailableRewardIds = null;
        foreach (var (rewardId, task) in spriteTasks)
        {
            if (!task.IsCompleted)
            {
                continue;
            }

            if (!TryReadSprite(rewardId, task, out var sprite))
            {
                (unavailableRewardIds ??= []).Add(rewardId);
                continue;
            }

            if (!run.Cleanup.CanBind(presentationGeneration))
            {
                return;
            }

            _ = TryStoreSpriteTask(rewardId, task, sprite!);
        }

        if (unavailableRewardIds is null)
        {
            return;
        }

        foreach (var rewardId in unavailableRewardIds)
        {
            spriteTasks.Remove(rewardId);
        }
    }

    private void BindCachedSpritesAndWatchPending(
        OpeningRun run,
        long presentationGeneration,
        IReadOnlyDictionary<string, Task<Sprite>> spriteTasks)
    {
        foreach (var (rewardId, task) in spriteTasks)
        {
            if (!run.Cleanup.CanBind(presentationGeneration))
            {
                return;
            }

            if (_sprites.TryGetValue(rewardId, out var sprite) && sprite != null)
            {
                _ = DeferredPresentationBinding.TryApply(
                    run.Cleanup,
                    presentationGeneration,
                    sprite,
                    value => _sprites[rewardId] = value,
                    value =>
                    {
                        _overlay.BindSprite(rewardId, value);
                        _overlay.SetResultSprite(rewardId, value);
                    });
                continue;
            }

            if (task.IsCompleted)
            {
                ApplyCompletedSpriteTask(
                    run,
                    presentationGeneration,
                    rewardId,
                    task);
                continue;
            }

            var lease = new SpriteCoroutineLease();
            run.SpriteCoroutines.Add(lease);
            try
            {
                lease.Coroutine = _coroutineHost.StartCoroutine(BindSpriteWhenReady(
                    run,
                    lease,
                    presentationGeneration,
                    rewardId,
                    task));
            }
            catch
            {
                run.SpriteCoroutines.Remove(lease);
                throw;
            }
        }
    }

    private void EvictTimedOutSpriteTasks(
        IReadOnlyDictionary<string, Task<Sprite>> spriteTasks,
        HashSet<string> visibleRewardIds)
    {
        foreach (var (rewardId, task) in spriteTasks)
        {
            if (visibleRewardIds.Contains(rewardId) &&
                SpriteRevealReadiness.ShouldEvict(task, readinessTimedOut: true))
            {
                EvictSpriteTask(rewardId, task);
            }
        }
    }

    private IEnumerator BindSpriteWhenReady(
        OpeningRun run,
        SpriteCoroutineLease lease,
        long presentationGeneration,
        string rewardId,
        Task<Sprite> task)
    {
        while (!task.IsCompleted)
        {
            yield return null;
        }

        run.SpriteCoroutines.Remove(lease);
        ApplyCompletedSpriteTask(run, presentationGeneration, rewardId, task);
    }

    private void ApplyCompletedSpriteTask(
        OpeningRun run,
        long presentationGeneration,
        string rewardId,
        Task<Sprite> task)
    {
        if (!TryReadSprite(rewardId, task, out var sprite))
        {
            return;
        }

        try
        {
            var ownsCache = false;
            _ = DeferredPresentationBinding.TryApply(
                run.Cleanup,
                presentationGeneration,
                sprite,
                value => value == null,
                value =>
                {
                    if (value != null)
                    {
                        ownsCache = TryStoreSpriteTask(rewardId, task, value);
                    }
                },
                value =>
                {
                    if (!ownsCache)
                    {
                        return;
                    }

                    _overlay.BindSprite(rewardId, value);
                    _overlay.SetResultSprite(rewardId, value);
                });
        }
        catch (Exception exception)
        {
            _log.LogError($"Contraband Cases sprite presentation failed: {exception}");
            if (IsCurrent(run) && !run.Cleanup.PresentationDetached)
            {
                HandleRevealPresentationFailure(run, exception);
            }
        }
    }

    private bool TryReadSprite(
        string rewardId,
        Task<Sprite> task,
        out Sprite? sprite)
    {
        try
        {
            sprite = task.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            EvictSpriteTask(rewardId, task);
            _log.LogWarning($"Could not load sprite for '{rewardId}': {exception.Message}");
            sprite = null;
            return false;
        }

        if (sprite != null)
        {
            return true;
        }

        EvictSpriteTask(rewardId, task);
        return false;
    }

    private void EvictSpriteTask(string rewardId, Task<Sprite> task)
    {
        _ = SpriteTaskCacheOwnership.TryEvict(_spriteTasks, _sprites, rewardId, task);
    }

    private bool TryStoreSpriteTask(string rewardId, Task<Sprite> task, Sprite sprite) =>
        SpriteTaskCacheOwnership.TryStore(_spriteTasks, _sprites, rewardId, task, sprite);

    private (InventorySnapshot Snapshot, IReadOnlyDictionary<string, Item> LiveItems) CaptureSnapshot(Profile profile)
    {
        var inventory = profile.Inventory
            ?? throw new InventorySnapshotException("The authenticated profile inventory is unavailable.");
        var liveItems = inventory.AllRealPlayerItems.ToArray();
        var byId = liveItems.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var nodes = liveItems.Select(item =>
        {
            var parentId = item.CurrentAddress?.Container?.ParentItem?.Id;
            return new InventorySnapshotNode(
                item.Id,
                item.StringTemplateId,
                parentId is not null && byId.ContainsKey(parentId) ? parentId : null,
                item.StackMaxSize);
        });
        return (
            new InventorySnapshot(profile.Id, nodes),
            byId);
    }

    private void OnActiveSceneChanged(Scene previous, Scene current)
    {
        AbandonPresentationForSceneChange();
    }

    private void OnSceneUnloaded(Scene scene)
    {
        AbandonPresentationForSceneChange();
    }

    private IEnumerator ControllerLoop()
    {
        while (!_disposed)
        {
            try
            {
                PollCatalogReadiness();
                PollStartupRecovery();
            }
            catch (Exception exception)
            {
                _log.LogError($"Contraband Cases lifecycle monitoring recovered from an error: {exception}");
            }

            yield return null;
        }

        _controllerLoop = null;
    }

    private void PollStartupRecovery()
    {
        if (!LobbyUiContext.IsMenuOrStashReady)
        {
            _startupRecovery.DeferProbe();
            return;
        }

        if (_active is not null ||
            _manifest.IsBusy ||
            _selfTestActive ||
            _startupRecovery.State == RelayStartupRecoveryState.Disposed)
        {
            return;
        }

        if (!TryGetAuthenticatedContext(out var session, out var profile))
        {
            return;
        }

        _ = BeginStartupRecoveryDiscovery(session, profile, forceRetry: false);
    }

    private bool TryStartStartupRecoveryFromSingleton(bool forceRetry)
    {
        return TryGetAuthenticatedContext(out var session, out var profile) &&
            BeginStartupRecoveryDiscovery(session, profile, forceRetry);
    }

    private bool BeginStartupRecoveryDiscovery(
        IClientSession session,
        Profile profile,
        bool forceRetry)
    {
        if (_disposed ||
            _active is not null ||
            _manifest.IsBusy ||
            _selfTestActive ||
            string.IsNullOrWhiteSpace(profile.Id))
        {
            return false;
        }

        if (!_startupRecovery.TryBegin(session, profile, profile.Id,
                LobbyUiContext.IsMenuOrStashReady, forceRetry, out var generation))
        {
            return false;
        }

        if (_startupRecoveryCoroutine is not null)
        {
            try
            {
                _coroutineHost.StopCoroutine(_startupRecoveryCoroutine);
            }
            catch (Exception exception)
            {
                _log.LogWarning($"Could not stop a superseded Relay recovery probe: {exception.Message}");
            }

            _startupRecoveryCoroutine = null;
        }

        Task<RelayPendingDiscovery> task;
        try
        {
            task = _snapshotTransport.DiscoverPendingAsync(RelayInteractionOrigin.RealOpening);
            // A read-only request cannot be cancelled through SPT's transport. Observe
            // late faults even if a transition has retired its presentation coroutine.
            _ = task.ContinueWith(completed => { _ = completed.Exception; },
                System.Threading.CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception exception)
        {
            FailStartupRecoveryProbe(generation, exception);
            return false;
        }

        try
        {
            _startupRecoveryCoroutine = _coroutineHost.StartCoroutine(
                StartupRecoveryDiscoveryLoop(generation, session, profile, task));
            return true;
        }
        catch (Exception exception)
        {
            _startupRecoveryCoroutine = null;
            FailStartupRecoveryProbe(generation, exception);
            return false;
        }
    }

    private IEnumerator StartupRecoveryDiscoveryLoop(
        long generation,
        IClientSession session,
        Profile profile,
        Task<RelayPendingDiscovery> task)
    {
        var timeout = new RelaySnapshotTimeoutBudget();
        yield return null;
        while (!task.IsCompleted &&
               IsStartupRecoveryContextCurrent(generation, session, profile))
        {
            if (timeout.Advance(Time.unscaledDeltaTime))
            {
                break;
            }

            yield return null;
        }

        if (generation == _startupRecovery.Generation)
        {
            _startupRecoveryCoroutine = null;
        }
        if (!IsStartupRecoveryContextCurrent(generation, session, profile))
        {
            if (!_disposed && generation == _startupRecovery.Generation)
            {
                _startupRecovery.DeferProbe();
            }
            yield break;
        }

        if (!task.IsCompleted)
        {
            FailStartupRecoveryProbe(generation, new RelaySnapshotException(
                $"The Relay recovery check timed out after {RelaySnapshotTimeoutBudget.DefaultTimeoutSeconds:0} seconds."));
            yield break;
        }

        try
        {
            var discovery = task.GetAwaiter().GetResult();
            if (discovery.PendingSnapshot is null)
            {
                _startupRecovery.TryCompleteProbe(generation, hasPending: false);
                Debug($"Relay recovery check completed for profile {profile.Id}; no action is pending.");
                yield break;
            }

            BeginStartupRecoveryRun(session, profile, discovery.PendingSnapshot);
        }
        catch (Exception exception)
        {
            FailStartupRecoveryProbe(generation, exception);
        }
    }

    private void BeginStartupRecoveryRun(
        IClientSession session,
        Profile profile,
        RelaySnapshot snapshot)
    {
        RelayPendingDiscoveryEnvelope.Validate(new RelayPendingDiscovery
        {
            PendingSnapshot = snapshot
        });
        if (!IsStartupRecoveryContextCurrent(
                _startupRecovery.Generation,
                session,
                profile) ||
            _active is not null ||
            _catalog is null)
        {
            throw new RelaySnapshotException(
                "The authenticated profile changed before Relay recovery could begin.");
        }

        var recovery = RelaySnapshotRecovery.Decide(snapshot);
        var reward = _catalog.Rewards.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, snapshot.Status.RewardId, StringComparison.Ordinal) &&
            string.Equals(candidate.Rarity.ToString(), snapshot.Status.Rarity, StringComparison.Ordinal))
            ?? throw new RelaySnapshotException(
                "The persisted Relay stake does not match the validated local reward catalog.");
        if (!_phase.TryBeginRecovery(out var token))
        {
            throw new InvalidOperationException(
                "Another Contraband Cases operation acquired the recovery gate.");
        }

        OpeningRun? run = null;
        try
        {
            run = new OpeningRun(
                token,
                session,
                profile,
                _catalog,
                snapshot.Status.StakeRootId)
            {
                IsStartupRecovery = true,
                Committed = new CommittedRewardMatch(
                    snapshot.Status.StakeRootId,
                    reward,
                    reward.Fingerprint),
                DecisionSnapshot = snapshot,
                PendingAction = recovery == RelayPendingRecovery.ResumeSecure
                    ? ClientRelayAction.Secure
                    : ClientRelayAction.Relay,
                PendingStakeRootId = snapshot.Status.StakeRootId
            };
            run.PreparedActionRecovery.Begin();
            run.InitializeCleanup(CreateRunCleanup(run));
            var (_, liveItems) = CaptureSnapshot(profile);
            if (liveItems.TryGetValue(snapshot.Status.StakeRootId, out var stakeRoot))
            {
                run.CommittedRoot = stakeRoot;
            }

            _active = run;
            _startupRecovery.TryCompleteProbe(_startupRecovery.Generation, hasPending: true);
            try
            {
                _overlay.ShowRelayPending(
                    run.Catalog.Rewards,
                    run.PendingAction == ClientRelayAction.Secure
                        ? "RECOVERING SECURE SETTLEMENT"
                        : "RECOVERING RELAY SETTLEMENT");
                StartPendingLoopIfNeeded(run);
            }
            catch (Exception exception)
            {
                _log.LogWarning($"Relay recovery will continue without its overlay: {exception.Message}");
                run.Cleanup.DetachPresentation();
            }

            _log.LogWarning(
                $"Resuming persisted {snapshot.Status.PendingAction} for Relay stake {snapshot.Status.StakeRootId}.");
            DispatchPendingRelayAction(run);
        }
        catch
        {
            if (run is not null)
            {
                _phase.TryFail(token);
                _phase.TryClose(token);
                run.Cleanup.Exit(OpeningRunExit.Failure, operationPending: false);
            }
            else
            {
                _phase.TryFail(token);
                _phase.TryClose(token);
            }

            throw;
        }
    }

    private bool IsStartupRecoveryContextCurrent(
        long generation,
        IClientSession session,
        Profile profile)
    {
        if (_disposed ||
            !_startupRecovery.IsCurrent(generation, session, profile, profile.Id, LobbyUiContext.IsMenuOrStashReady) ||
            !TryGetAuthenticatedContext(out var activeSession, out var activeProfile))
        {
            return false;
        }

        return ReferenceEquals(activeSession, session) &&
            ReferenceEquals(activeProfile, profile) &&
            string.Equals(activeProfile.Id, profile.Id, StringComparison.Ordinal);
    }

    private static bool TryGetAuthenticatedContext(
        out IClientSession session,
        out Profile profile)
    {
        session = null!;
        profile = null!;
        if (!Singleton<ClientApplication<IEftSession>>.Instantiated)
        {
            return false;
        }

        session = Singleton<ClientApplication<IEftSession>>.Instance.GetClientBackEndSession();
        profile = session?.Profile!;
        return session is not null &&
            profile is not null &&
            !string.IsNullOrWhiteSpace(profile.Id);
    }

    private void FailStartupRecoveryProbe(long generation, Exception exception)
    {
        if (!_startupRecovery.TryFail(generation)) return;
        _log.LogError($"Contraband Cases Relay restart recovery is blocked: {exception}");
        TryNotifyWarning(
            "Contraband Cases could not verify unfinished Relay state. New openings are blocked; unpack a case to retry.");
    }

    private void PollCatalogReadiness()
    {
        if (_catalogState != CatalogReadinessState.Waiting ||
            !Singleton<ItemFactory>.Instantiated)
        {
            return;
        }

        var presets = Singleton<ItemFactory>.Instance.SavedPresets;
        if (presets is null || presets.Length == 0)
        {
            return;
        }

        try
        {
            if (!File.Exists(_catalogPath))
            {
                throw new FileNotFoundException(
                    "The Contraband Cases reward catalog is missing.",
                    _catalogPath);
            }

            _catalog = ClientRewardCatalog.Load(File.ReadAllText(_catalogPath));
            _catalogState = CatalogReadinessState.Ready;
            _log.LogInfo($"Contraband Cases client catalog is ready with {_catalog.Rewards.Count} validated presets.");
        }
        catch (Exception exception)
        {
            _catalog = null;
            _catalogState = CatalogReadinessState.Invalid;
            _log.LogError(
                "Contraband Cases local preview catalog is unavailable; cosmetic self-test and old-save weapon recovery are disabled, but Manifest openings remain server-authoritative.");
            var debugDetails = CatalogReadinessDiagnostics.DebugDetails(exception, _config.DebugLogging);
            if (debugDetails is not null)
            {
                _log.LogError(debugDetails);
            }
        }
    }

    internal void HandleInventoryScreenClosing()
    {
        if (_manifest.IsBusy)
        {
            _manifest.HandleSceneTeardown();
            return;
        }

        if (_selfTestActive)
        {
            EndCosmeticSelfTest();
            return;
        }

        var run = _active;
        if (!OpeningScreenTeardownPolicy.ShouldDetach(
                _disposed,
                run is not null,
                run?.Cleanup.PresentationDetached ?? true))
        {
            return;
        }

        _log.LogWarning("Contraband Cases detached its overlay because the Tarkov inventory screen closed.");
        AbandonPresentationForSceneChange();
    }

    private void AbandonPresentationForSceneChange()
    {
        _startupRecovery.DeferProbe();
        _manifest.HandleSceneTeardown();

        if (_selfTestActive)
        {
            LifecycleDiagnostic("Scene teardown detached the cosmetic-only roulette self-test.");
            EndCosmeticSelfTest();
        }

        var run = _active;
        if (run is null || run.Cleanup.PresentationDetached)
        {
            return;
        }

        switch (_phase.Phase)
        {
            case OpeningPhase.Confirming:
                if (_phase.TryCancel(run.Token))
                {
                    run.Completion.TrySetResult(new FailedResult("Contraband Cases opening was cancelled because its item screen closed."));
                    run.Cleanup.Exit(OpeningRunExit.SceneTeardown, operationPending: false);
                }
                break;
            case OpeningPhase.Pending:
                var retryVerificationAfterDetach = run.PendingVerificationRecovery &&
                    run.SnapshotCoroutine is null &&
                    !run.OperationCallbacks.IsPending;
                if (run.PendingAction == ClientRelayAction.Secure)
                {
                    run.DetachedSecureAttempted = true;
                }
                run.Cleanup.Exit(OpeningRunExit.SceneTeardown, operationPending: true);
                if (retryVerificationAfterDetach)
                {
                    RetryMutationVerification(run);
                }
                break;
            case OpeningPhase.Revealing:
                var terminalReveal = run.RelayReceipt is not null &&
                    RelayTerminalPolicy.After(
                        RelaySnapshotEnvelope.ParseOutcome(run.RelayReceipt),
                        run.RelayReceipt.Terminal) == RelayClientDestination.Terminal;
                if (terminalReveal)
                {
                    _phase.TryAbortRevealToResult(run.Token);
                    _phase.TryClose(run.Token);
                    run.Cleanup.Exit(OpeningRunExit.SceneTeardown, operationPending: false);
                }
                else if (_phase.TryAbortRevealToResult(run.Token))
                {
                    run.DetachedSecureAttempted = true;
                    run.Cleanup.Exit(OpeningRunExit.SceneTeardown, operationPending: true);
                    Secure(run, showPresentation: false);
                }
                break;
            case OpeningPhase.Result:
                if (!run.Terminal)
                {
                    run.DetachedSecureAttempted = true;
                    run.Cleanup.Exit(OpeningRunExit.SceneTeardown, operationPending: true);
                    Secure(run, showPresentation: false);
                    break;
                }
                _phase.TryClose(run.Token);
                run.Cleanup.Exit(OpeningRunExit.SceneTeardown, operationPending: false);
                break;
            case OpeningPhase.Failed:
                _phase.TryClose(run.Token);
                run.Cleanup.Exit(OpeningRunExit.SceneTeardown, operationPending: false);
                break;
        }
    }

    private bool IsCurrent(OpeningRun run) =>
        !_disposed && ReferenceEquals(_active, run) && run.Token == _phase.Generation;

    private OpeningRunCleanup CreateRunCleanup(OpeningRun run)
    {
        return new OpeningRunCleanup(
            _overlay.DisableRunInput,
            _overlay.ClearRunListeners,
            _overlay.ReleaseRunTiles,
            () => StopOwnedCoroutines(run),
            _overlay.RestoreRunSelection,
            _overlay.DeactivateRunRoot,
            () =>
            {
                if (run.IsStartupRecovery && !_disposed)
                {
                    _startupRecovery.CompleteRecovery(run.Terminal);
                    if (run.Terminal)
                    {
                        _log.LogInfo(
                            $"Recovered Relay settlement for profile {run.Profile.Id} completed and released the opening gate.");
                    }
                }
                if (ReferenceEquals(_active, run))
                {
                    _active = null;
                }
            },
            exception => _log.LogError($"Contraband Cases cleanup recovered from an error: {exception}"));
    }

    private void StopOwnedCoroutines(OpeningRun run)
    {
        StopPending(run);
        StopReveal(run);
        foreach (var lease in run.SpriteCoroutines)
        {
            try
            {
                if (lease.Coroutine is not null)
                {
                    _coroutineHost.StopCoroutine(lease.Coroutine);
                }
            }
            catch (Exception exception)
            {
                _log.LogError($"Contraband Cases could not stop a sprite presentation coroutine: {exception}");
            }
        }

        run.SpriteCoroutines.Clear();
    }

    private void StopPending(OpeningRun run)
    {
        if (run.PendingCoroutine is null)
        {
            return;
        }

        _coroutineHost.StopCoroutine(run.PendingCoroutine);
        run.PendingCoroutine = null;
    }

    private void StopReveal(OpeningRun run)
    {
        if (run.RevealCoroutine is null)
        {
            return;
        }

        _coroutineHost.StopCoroutine(run.RevealCoroutine);
        run.RevealCoroutine = null;
    }

    private void CancelSnapshotObservation(OpeningRun run)
    {
        if (run.SnapshotCoroutine is null)
        {
            return;
        }

        try
        {
            _coroutineHost.StopCoroutine(run.SnapshotCoroutine);
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Contraband Cases could not stop a superseded snapshot observer: {exception.Message}");
        }
        finally
        {
            run.SnapshotCoroutine = null;
        }
    }

    private void RetirePhase(OpeningRun run)
    {
        switch (_phase.Phase)
        {
            case OpeningPhase.Confirming:
                _phase.TryCancel(run.Token);
                break;
            case OpeningPhase.Pending:
            case OpeningPhase.Revealing:
                _phase.TryFail(run.Token);
                _phase.TryClose(run.Token);
                break;
            case OpeningPhase.Result:
            case OpeningPhase.Failed:
                _phase.TryClose(run.Token);
                break;
        }
    }

    private void Debug(string message)
    {
        if (_config.DebugLogging)
        {
            _log.LogInfo(message);
        }
    }

    private void LifecycleDiagnostic(string message)
    {
        try
        {
            if (_lifecycleDiagnosticsEnabled())
            {
                _log.LogInfo($"[Lifecycle] {message}");
            }
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Contraband Cases lifecycle diagnostics setting could not be read: {exception.Message}");
        }
    }

    private Task<IResult> RejectOpen(string message)
    {
        TryNotifyWarning(message);
        return FailureTask(message);
    }

    private void TryNotifyWarning(string message)
    {
        try
        {
            NotificationManager.DisplayWarningNotification(message);
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Contraband Cases could not show Tarkov's fallback notification: {exception.Message}");
        }
    }

    private static int StableSeed(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var character in value)
            {
                hash = hash * 31 + character;
            }

            return hash;
        }
    }

    private static Task<IResult> FailureTask(string message) => new FailedResult(message).Task;

    private sealed class OpeningRun
    {
        public OpeningRun(
            long token,
            IClientSession session,
            Profile profile,
            ClientRewardCatalog catalog,
            string caseItemId)
        {
            Token = token;
            Session = session;
            Profile = profile;
            Catalog = catalog;
            CaseItemId = caseItemId;
        }

        public long Token { get; }
        public IClientSession Session { get; }
        public Profile Profile { get; }
        public OpeningInventoryBaseline? Baseline { get; set; }
        public ClientRewardCatalog Catalog { get; }
        public string CaseItemId { get; }
        public TaskCompletionSource<IResult> Completion { get; } = new();
        public OperationCallbackGate OperationCallbacks { get; } = new();
        public RouletteRevealClock RevealClock { get; } = new();
        public RouletteRevealPlan? RevealPlan { get; set; }
        public CommittedRewardMatch? Committed { get; set; }
        public Item? CommittedRoot { get; set; }
        public Coroutine? PendingCoroutine { get; set; }
        public Coroutine? RevealCoroutine { get; set; }
        public Coroutine? SnapshotCoroutine;
        public RelaySnapshot? DecisionSnapshot;
        public RelaySnapshot? NextDecisionSnapshot;
        public RelayReceipt? RelayReceipt { get; set; }
        public RelayInventoryBaseline? RelayBaseline { get; set; }
        public ClientRelayAction? PendingAction { get; set; }
        public string? PendingStakeRootId { get; set; }
        public int PendingRetryCount { get; set; }
        public bool LastMutationSucceeded { get; set; }
        public VerificationRetryBudget VerificationRetries { get; } = new();
        public RelayPreparedActionRecovery PreparedActionRecovery { get; } = new();
        public bool PendingVerificationRecovery { get; set; }
        public string? RelayDisabledReason { get; set; }
        public bool DetachedSecureAttempted { get; set; }
        public bool IsStartupRecovery { get; set; }
        public bool Terminal { get; set; }
        public List<SpriteCoroutineLease> SpriteCoroutines { get; } = [];
        public OpeningRunCleanup Cleanup { get; private set; } = null!;

        public void InitializeCleanup(OpeningRunCleanup cleanup)
        {
            if (Cleanup is not null)
            {
                throw new InvalidOperationException("Opening cleanup was already initialized.");
            }

            Cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
        }
    }

    private sealed class SpriteCoroutineLease
    {
        public Coroutine? Coroutine { get; set; }
    }

    private enum CatalogReadinessState
    {
        Waiting,
        Ready,
        Invalid,
        Disposed
    }

    private enum ClientRelayAction
    {
        Secure,
        Relay
    }
}

public static class SpriteTaskCacheOwnership
{
    public static bool TryStore<TSprite>(
        Dictionary<string, Task<TSprite>> taskCache,
        Dictionary<string, TSprite> spriteCache,
        string rewardId,
        Task<TSprite> task,
        TSprite sprite)
        where TSprite : class
    {
        if (taskCache is null)
        {
            throw new ArgumentNullException(nameof(taskCache));
        }

        if (spriteCache is null)
        {
            throw new ArgumentNullException(nameof(spriteCache));
        }

        if (rewardId is null)
        {
            throw new ArgumentNullException(nameof(rewardId));
        }

        if (task is null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        if (sprite is null)
        {
            throw new ArgumentNullException(nameof(sprite));
        }

        if (taskCache.TryGetValue(rewardId, out var owner) &&
            !ReferenceEquals(owner, task))
        {
            return false;
        }

        taskCache[rewardId] = task;
        spriteCache[rewardId] = sprite;
        return true;
    }

    public static bool TryEvict<TSprite>(
        Dictionary<string, Task<TSprite>> taskCache,
        Dictionary<string, TSprite> spriteCache,
        string rewardId,
        Task<TSprite> task)
        where TSprite : class
    {
        if (taskCache is null)
        {
            throw new ArgumentNullException(nameof(taskCache));
        }

        if (spriteCache is null)
        {
            throw new ArgumentNullException(nameof(spriteCache));
        }

        if (rewardId is null)
        {
            throw new ArgumentNullException(nameof(rewardId));
        }

        if (task is null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        if (!taskCache.TryGetValue(rewardId, out var owner) ||
            !ReferenceEquals(owner, task))
        {
            return false;
        }

        taskCache.Remove(rewardId);
        spriteCache.Remove(rewardId);
        return true;
    }
}
