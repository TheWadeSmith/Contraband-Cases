using System.Collections;
using System.Threading.Tasks;
using BepInEx.Logging;
using Comfort.Common;
using ContrabandCases.Client.Configuration;
using ContrabandCases.Client.UI;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using UnityEngine;

namespace ContrabandCases.Client.Opening;

internal sealed partial class ManifestPresentationCoordinator : IDisposable
{
    private const double SpriteLoadTimeoutSeconds = 5d;

    private readonly MonoBehaviour _host;
    private readonly ManualLogSource _log;
    private readonly PresentationConfig _config;
    private readonly RouletteOverlay _overlay;
    private readonly CaseOperationDispatcher _dispatcher;
    private readonly ManifestSnapshotTransport _transport;
    private readonly ManifestSpriteTaskCache<Sprite> _spriteCache = new(sprite => sprite == null);
    private ManifestRun? _active;
    private long _generation;
    private bool _disposed;

    public ManifestPresentationCoordinator(
        MonoBehaviour host,
        ManualLogSource log,
        PresentationConfig config,
        RouletteOverlay overlay,
        CaseOperationDispatcher dispatcher,
        ManifestSnapshotTransport transport)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public bool IsBusy => _active is not null;

    public Task<IResult> OpenAsync(ItemUiContext itemUiContext, Item targetItem)
    {
        if (_disposed)
        {
            return Reject("Contraband Cases is shutting down.");
        }
        if (_active is not null)
        {
            return Reject("Another Manifest presentation is already active.");
        }
        if (Singleton<GameWorld>.Instantiated)
        {
            return Reject("BR-12 manifests can only be opened outside a raid.");
        }

        try
        {
            var session = itemUiContext?.ClientSession
                ?? throw new InvalidOperationException("The authenticated Tarkov session is unavailable.");
            var profile = session.Profile
                ?? throw new InvalidOperationException("The authenticated Tarkov profile is unavailable.");
            if (targetItem is null || string.IsNullOrWhiteSpace(targetItem.Id))
            {
                throw new InvalidOperationException("The requested BR-12 case identity is unavailable.");
            }

            var run = new ManifestRun(
                checked(++_generation),
                session,
                profile,
                targetItem.Id,
                targetItem.StringTemplateId);
            _active = run;
            if (!_overlay.ShowManifestPending(
                    "CHECKING MANIFEST LEDGER",
                    "The server is checking for an unfinished entitlement before allowing a new case action.",
                    allowRebuild: true))
            {
                throw new InvalidOperationException("Tarkov's UI event system is unavailable.");
            }

            BeginInitialFetch(run);
            return run.Completion.Task;
        }
        catch (Exception exception)
        {
            _log.LogError($"Contraband Cases could not start its Manifest UI: {exception}");
            End(_active, "The Manifest window could not be opened.", operationPending: false);
            return Reject("The Manifest window could not be opened. Check the BepInEx log.");
        }
    }

    public void HandleSceneTeardown()
    {
        var run = _active;
        if (run is null || run.PresentationDetached)
        {
            return;
        }

        run.PresentationDetached = true;
        run.PresentationGeneration++;
        StopSpriteBinding(run);
        StopCatalogSpriteBinding(run);
        try
        {
            _overlay.EndRun();
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Manifest scene cleanup recovered from an error: {exception.Message}");
        }

        if (!ManifestPresentationPolicy.KeepDetachedObservationAlive(
                run.CallbackGate.IsPending,
                run.Stage == ManifestClientStage.OperationPending,
                run.Stage == ManifestClientStage.VerifyingOperation,
                run.ObservationCoroutine is not null))
        {
            End(run, "The Manifest window closed; its authoritative state will resume next time.", operationPending: false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var run = _active;
        if (run is not null)
        {
            run.PresentationDetached = true;
            run.PresentationGeneration++;
            End(run, "Contraband Cases shut down while the Manifest was active.", operationPending: false);
        }
    }

    private void BeginInitialFetch(ManifestRun run)
    {
        Task<ManifestCurrentState> task;
        try
        {
            task = _transport.FetchCurrentAsync(RelayInteractionOrigin.RealOpening, run.RequestedCaseTemplateId);
        }
        catch (Exception exception)
        {
            Fail(run, "The active Manifest could not be checked.", exception);
            return;
        }

        Observe(
            run,
            task,
            current =>
            {
                RequireProfile(run);
                if (ManifestPresentationPolicy.Resume(current.Snapshot, run.RecoveryOnly) == ManifestResumeKind.NoPending)
                {
                    run.Stage = ManifestClientStage.Terminal;
                    if (run.PresentationDetached || !_overlay.ShowDossier(
                            "No pending Manifest or payout was found. No case or key was spent.",
                            () => End(run, null, operationPending: false)))
                        End(run, null, operationPending: false);
                    return;
                }
                if (current.Snapshot is null)
                {
                    run.OpeningOdds = current.OpeningOdds
                        ?? throw new ManifestSnapshotException(
                            "The server did not publish opening odds for a new Manifest.");
                    if (run.OpeningOdds.CaseTemplateId != run.RequestedCaseTemplateId)
                        throw new ManifestSnapshotException("The server odds belong to a different case type.");
                    ShowConfirmation(run, catalogChanged: false);
                    return;
                }

                run.RecoveryOnly = true;
                run.Completion.TrySetResult(new FailedResult(
                    "An unfinished Manifest was resumed; the selected case was not consumed."));
                Present(run, current.Snapshot, before: null, recovering: true, claimRetry: false);
            },
            exception => Fail(run, "The active Manifest could not be verified. No case action was sent.", exception),
            "current Manifest check");
    }

    private void ShowConfirmation(ManifestRun run, bool catalogChanged)
    {
        if (!IsCurrent(run))
        {
            return;
        }
        var openingOdds = run.OpeningOdds
            ?? throw new InvalidOperationException(
                "Authoritative opening odds are required before confirmation.");
        run.Stage = ManifestClientStage.Confirming;
        if (!_overlay.ShowManifestConfirmation(
                openingOdds,
                catalogChanged,
                () => RevalidateNewTicket(run),
                () => Cancel(run)))
        {
            Fail(run, "The Manifest confirmation could not be displayed.", null);
            return;
        }
        StartCatalogSpriteLoads(run, openingOdds);
    }

    private void RevalidateNewTicket(ManifestRun run)
    {
        if (!IsCurrentAt(run, ManifestClientStage.Confirming))
        {
            return;
        }
        ShowPending(
            run,
            "VERIFYING NEW MANIFEST",
            "No case or key will be consumed until the server confirms there is no active Manifest.");
        Task<ManifestCurrentState> task;
        try
        {
            task = _transport.FetchCurrentAsync(RelayInteractionOrigin.RealOpening, run.RequestedCaseTemplateId);
        }
        catch (Exception exception)
        {
            ShowVerificationFailure(run, exception, () => RevalidateNewTicket(run));
            return;
        }

        Observe(
            run,
            task,
            current =>
            {
                var displayedOdds = run.OpeningOdds
                    ?? throw new ManifestSnapshotException(
                        "The displayed Manifest catalog authority was lost.");
                if (current.OpeningOdds is { } freshOdds && freshOdds.CaseTemplateId != run.RequestedCaseTemplateId)
                    throw new ManifestSnapshotException("The server odds belong to a different case type.");
                switch (ManifestPresentationPolicy.OpeningPreflight(displayedOdds, current))
                {
                    case ManifestOpeningPreflightDecision.Dispatch:
                        Dispatch(run, ManifestEconomicAction.OpenTicket, before: null, run.RequestedCaseId);
                        return;
                    case ManifestOpeningPreflightDecision.Reconfirm:
                        run.OpeningOdds = current.OpeningOdds
                            ?? throw new ManifestSnapshotException(
                                "The replacement Manifest catalog did not publish opening odds.");
                        TryWarn(
                            "The Manifest catalog changed. Review the new server odds and confirm again.");
                        ShowConfirmation(run, catalogChanged: true);
                        return;
                    case ManifestOpeningPreflightDecision.ResumeActive:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                run.RecoveryOnly = true;
                run.Completion.TrySetResult(new FailedResult(
                    "An unfinished Manifest appeared; the selected case was not consumed."));
                Present(
                    run,
                    current.Snapshot
                        ?? throw new ManifestSnapshotException(
                            "The server reported an active Manifest without its snapshot."),
                    before: null,
                    recovering: true,
                    claimRetry: false);
            },
            exception => ShowVerificationFailure(run, exception, () => RevalidateNewTicket(run)),
            "new Manifest preflight");
    }

    private void RevalidateAction(
        ManifestRun run,
        ManifestSnapshot expected,
        ManifestEconomicAction action,
        int? selectedOrdinal = null)
    {
        if (!IsCurrent(run) || run.Stage != ManifestClientStage.Decision)
        {
            return;
        }
        ShowPending(
            run,
            "VERIFYING MANIFEST STATE",
            "The live server snapshot must still match every displayed precondition.");
        Task<ManifestCurrentState> task;
        try
        {
            task = _transport.FetchCurrentAsync(RelayInteractionOrigin.RealOpening);
        }
        catch (Exception exception)
        {
            ShowVerificationFailure(run, exception, () => RevalidateAction(run, expected, action, selectedOrdinal));
            return;
        }

        Observe(
            run,
            task,
            currentState =>
            {
                var current = currentState.Snapshot;
                if (!ManifestPresentationPolicy.MatchesActionPrecondition(expected, current, action))
                {
                    if (current is null)
                    {
                        Fail(run, "The active Manifest disappeared before the action was sent.", null);
                        return;
                    }

                    TryWarn("The Manifest changed while the decision was open. The authoritative state was restored.");
                    Present(run, current, expected, recovering: true, claimRetry: false);
                    return;
                }

                Dispatch(run, action, current, caseItemId: null, selectedOrdinal);
            },
            exception => ShowVerificationFailure(
                run,
                exception,
                () => RevalidateAction(run, expected, action, selectedOrdinal)),
            "Manifest action preflight");
    }

    private void Dispatch(
        ManifestRun run,
        ManifestEconomicAction action,
        ManifestSnapshot? before,
        string? caseItemId,
        int? selectedOrdinal = null)
    {
        if (!IsCurrent(run))
        {
            return;
        }

        ManifestPresentationPolicy.RequireDispatchAuthority(run.LibraryOnly, run.RecoveryOnly, action, before);
        RequireProfile(run);
        if (ManifestPresentationPolicy.RequiresFreshOpeningKey(action, before) && !HasRelayKey(run.Profile))
        {
            FailKeyRequired(run);
            return;
        }
        ShowPending(run, PendingTitle(action), "The server owns this transition. It will never be repeated automatically.");
        run.Stage = ManifestClientStage.OperationPending;
        run.PendingAction = action;
        run.BeforeAction = before;
        var completion = new TaskCompletionSource<IResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackGeneration = run.CallbackGate.Begin();
        try
        {
            void Callback(IResult result)
            {
                if (run.CallbackGate.TryClaim(callbackGeneration))
                {
                    completion.TrySetResult(result ?? new FailedResult("The server returned an empty item-operation result."));
                }
            }

            switch (action)
            {
                case ManifestEconomicAction.OpenTicket:
                    _dispatcher.DispatchManifestOpen(
                        run.Session,
                        run.Profile,
                        run.Profile.Id,
                        caseItemId ?? throw new InvalidOperationException("A ticket case ID is required."),
                        run.OpeningOdds?.CatalogSnapshotId,
                        Callback);
                    break;
                case ManifestEconomicAction.Lock:
                case ManifestEconomicAction.Burn:
                    _dispatcher.DispatchManifestDecision(
                        run.Session,
                        run.Profile,
                        run.Profile.Id,
                        action == ManifestEconomicAction.Lock
                            ? ModConstants.ManifestLockAction
                            : ModConstants.ManifestBurnAction,
                        before!.ManifestId,
                        before.AvailableActions.ExpectedOrdinal,
                        RelayInteractionOrigin.RealOpening,
                        Callback,
                        selectedOrdinal);
                    break;
                case ManifestEconomicAction.Claim:
                    _dispatcher.DispatchManifestClaim(
                        run.Session,
                        run.Profile,
                        run.Profile.Id,
                        before!.ManifestId,
                        before.AvailableActions.ExpectedPhase,
                        RelayInteractionOrigin.RealOpening,
                        Callback);
                    break;
                case ManifestEconomicAction.Relay:
                    _dispatcher.DispatchManifestRelay(
                        run.Session,
                        run.Profile,
                        run.Profile.Id,
                        before!.ManifestId,
                        before.AvailableActions.ExpectedPhase,
                        before.AvailableActions.ExpectedRelayStage,
                        RelayInteractionOrigin.RealOpening,
                        Callback);
                    break;
                case ManifestEconomicAction.Forfeit:
                    _dispatcher.DispatchManifestForfeit(
                        run.Session,
                        run.Profile,
                        run.Profile.Id,
                        before!.ManifestId,
                        before.AvailableActions.ExpectedPhase,
                        RelayInteractionOrigin.RealOpening,
                        Callback);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }
        }
        catch (Exception exception)
        {
            run.CallbackGate.TryCancel(callbackGeneration);
            _log.LogWarning($"Manifest {action} dispatch did not complete normally: {exception}");
            // A dispatch exception can occur after the request was sent. Read
            // server state before presenting any claim that nothing changed.
            BeginOperationVerification(run, action, before, new FailedResult(exception.Message));
            return;
        }

        Observe(
            run,
            completion.Task,
            result => BeginOperationVerification(run, action, before, result),
            exception =>
            {
                run.CallbackGate.TryCancel(callbackGeneration);
                BeginOperationVerification(
                    run,
                    action,
                    before,
                    new FailedResult($"The {action} callback timed out; authoritative verification is required."));
            },
            $"{action} item operation");
    }

    private void BeginOperationVerification(
        ManifestRun run,
        ManifestEconomicAction action,
        ManifestSnapshot? before,
        IResult operationResult)
    {
        if (!IsCurrent(run))
        {
            return;
        }
        run.Stage = ManifestClientStage.VerifyingOperation;
        ShowPending(
            run,
            "VERIFYING AUTHORITATIVE RESULT",
            "The animation waits for the server snapshot; inventory differences and local reward files are not used.");
        StartOperationSnapshotFetch(run, action, before, operationResult);
    }

    private void StartOperationSnapshotFetch(
        ManifestRun run,
        ManifestEconomicAction action,
        ManifestSnapshot? before,
        IResult operationResult)
    {
        Task<ManifestSnapshot?> task;
        try
        {
            task = before is null
                ? AsSnapshot(_transport.FetchCurrentAsync(RelayInteractionOrigin.RealOpening))
                : AsNullable(_transport.FetchAsync(before.ManifestId, RelayInteractionOrigin.RealOpening));
        }
        catch (Exception exception)
        {
            ShowOperationVerificationFailure(run, action, before, operationResult, exception);
            return;
        }

        Observe(
            run,
            task,
            after => HandleOperationSnapshot(run, action, before, operationResult, after),
            exception => ShowOperationVerificationFailure(
                run,
                action,
                before,
                operationResult,
                exception),
            $"{action} result verification");
    }

    private void HandleOperationSnapshot(
        ManifestRun run,
        ManifestEconomicAction action,
        ManifestSnapshot? before,
        IResult operationResult,
        ManifestSnapshot? after)
    {
        if (!IsCurrent(run))
        {
            return;
        }
        if (after is null)
        {
            if (action == ManifestEconomicAction.OpenTicket)
            {
                run.Completion.TrySetResult(operationResult);
            }
            ShowOperationRejected(
                run,
                action,
                before,
                new ManifestSnapshotException("No committed Manifest exists after the item operation."),
                operationResult.Error);
            return;
        }

        if (before is not null &&
            !string.Equals(before.ManifestId, after.ManifestId, StringComparison.Ordinal))
        {
            Fail(run, "The server returned a different Manifest after settlement.", null);
            return;
        }

        if (!run.RecoveryOnly && action == ManifestEconomicAction.OpenTicket)
        {
            run.Completion.TrySetResult(operationResult);
        }

        var unchanged = before is not null &&
            ManifestPresentationPolicy.MatchesActionPrecondition(before, after, action);
        if ((operationResult.Failed || !operationResult.Succeed) && unchanged)
        {
            _log.LogWarning($"Manifest {action} rejected (code {operationResult.ErrorCode}): {operationResult.Error}");
            ShowOperationRejected(run, action, after, null, operationResult.Error);
            return;
        }
        if (ManifestPresentationPolicy.AfterOperation(after) ==
            ManifestPostOperationRoute.ManualPreparedRecovery)
        {
            ShowPreparedRetry(run, after);
            return;
        }
        if (before is not null && ManifestPresentationPolicy.IsClaimRetry(action, before, after))
        {
            Present(run, after, before, recovering: false, claimRetry: true);
            return;
        }

        Present(run, after, before, recovering: false, claimRetry: false);
    }

    private void Present(
        ManifestRun run,
        ManifestSnapshot snapshot,
        ManifestSnapshot? before,
        bool recovering,
        bool claimRetry)
    {
        if (!IsCurrent(run))
        {
            return;
        }
        RequireProfile(run);
        run.Snapshot = snapshot;
        if (run.PresentationDetached)
        {
            End(run, "Manifest settlement completed after its window closed; it will resume from the server next time.", operationPending: false);
            return;
        }

        if (snapshot.MissingContentBlocked)
        {
            ShowMissingContent(run, snapshot);
            return;
        }
        if (ManifestPresentationPolicy.ShouldRevealAfter(before, snapshot, recovering))
        {
            BeginReveal(run, snapshot);
            return;
        }

        switch (ManifestPresentationPolicy.Resume(snapshot))
        {
            case ManifestResumeKind.ResumeTicket:
                Dispatch(
                    run,
                    ManifestEconomicAction.OpenTicket,
                    snapshot,
                    snapshot.RecoveryCaseItemId ?? throw new ManifestSnapshotException(
                        "Ticket recovery did not publish its exact case ID."));
                break;
            case ManifestResumeKind.ShowOffer:
                ShowOffer(run, snapshot);
                break;
            case ManifestResumeKind.ShowEntitlement:
                ShowEntitlement(run, snapshot, claimRetry);
                break;
            case ManifestResumeKind.ResumeClaim:
                Dispatch(run, ManifestEconomicAction.Claim, snapshot, caseItemId: null);
                break;
            case ManifestResumeKind.ResumeRelay:
                Dispatch(run, ManifestEconomicAction.Relay, snapshot, caseItemId: null);
                break;
            case ManifestResumeKind.ShowTerminal:
                ShowTerminal(run, snapshot);
                break;
            case ManifestResumeKind.ConfirmNew:
            default:
                throw new InvalidOperationException("An active snapshot cannot request new-case confirmation.");
        }
    }

    private void BeginReveal(ManifestRun run, ManifestSnapshot snapshot)
    {
        run.ReducedMotion = _config.ReducedMotionDefault;
        var reveal = ManifestPresentationPolicy.CreateReveal(
            snapshot,
            run.ReducedMotion,
            StableSeed($"{snapshot.ManifestId}:{snapshot.CurrentOrdinal}:{snapshot.RelayStage}"),
            RouletteOverlay.RevealTileCount,
            RouletteOverlay.LandingIndex,
            RouletteOverlay.ViewportWidth,
            RouletteOverlay.TileWidth,
            RouletteOverlay.TileSpacing,
            baseDurationSeconds: _config.AnimationDurationSeconds,
            publishedCatalog: snapshot.LatestReceipt is null ? run.OpeningOdds : null);
        var spriteBatch = PrepareSpriteLoads(run, ManifestSpritePlan.FromReveal(reveal));
        run.Reveal = reveal;
        run.RevealClock.BeginCommittedReveal();
        run.Stage = ManifestClientStage.Revealing;
        if (!_overlay.ShowManifestReveal(reveal, () => SkipReveal(run)))
        {
            Fail(run, "The committed Manifest reveal could not be displayed.", null);
            return;
        }
        _overlay.PlayLatch();
        BeginSpriteBinding(run, spriteBatch, run.PresentationGeneration);
        run.VisualCoroutine = _host.StartCoroutine(RevealLoop(run, run.PresentationGeneration));
    }

    // Landing-reveal pulse/flash: how long the tile holds its flash before
    // the panel advances, and how pronounced the scale punch is. Skipped
    // reveals (the player pressed Skip) bypass this hold entirely -- Skip
    // means "get to the result now", not "play the flourish faster".
    private const float LandingPulseHoldSeconds = 0.6f;
    private const float LandingPulseScaleAmplitude = 0.14f;

    // How dark the non-landing tiles get once the spotlight ramps in.
    // Kept well short of full black so the strip stays legible, not a
    // silhouette.
    private const float LandingSpotlightMaxDim = 0.6f;

    private IEnumerator RevealLoop(ManifestRun run, long presentationGeneration)
    {
        // Not the raw config value for the scrolling branch: run.Reveal.Motion.DurationSeconds already
        // folded in a small per-seed jitter (see RouletteAnimationMath.SpinDurationSeconds) so consecutive
        // openings don't all take the exact same number of seconds to land -- purely cosmetic pacing, the
        // landing itself (FinalX) is unaffected. The reduced-motion branch is an instant alpha fade, not a
        // spin, so it keeps its own fixed duration regardless.
        var duration = run.ReducedMotion ? 0.45d : run.Reveal!.Motion.DurationSeconds;
        var lastTickIndex = 0;
        while (IsCurrentAt(run, ManifestClientStage.Revealing) &&
               !run.PresentationDetached &&
               run.PresentationGeneration == presentationGeneration &&
               run.RevealClock.RevealElapsedSeconds < duration)
        {
            try
            {
                run.RevealClock.AdvanceReveal(Time.unscaledDeltaTime);
                var plan = run.Reveal!.Motion;
                if (run.ReducedMotion)
                {
                    _overlay.SetStripX((float)plan.FinalX);
                    _overlay.SetPresentationAlpha((float)Math.Min(1d, run.RevealClock.RevealElapsedSeconds / duration));
                }
                else
                {
                    var progress = duration > 0d
                        ? (float)Math.Min(1d, run.RevealClock.RevealElapsedSeconds / duration)
                        : 1f;
                    var currentX = (float)plan.PositionAt(run.RevealClock.RevealElapsedSeconds);
                    _overlay.SetStripX(currentX);
                    _overlay.SetSkipEnabled(run.RevealClock.CanSkip);

                    // Idle shimmer on any visible BlackLabel tile, and a
                    // rising tension drone under the ticks -- both purely
                    // cosmetic and both driven straight off this loop's own
                    // progress/elapsed clock so they never drift from what
                    // the player is watching.
                    _overlay.AdvanceShimmer((float)run.RevealClock.RevealElapsedSeconds);
                    _overlay.AdvanceSpinAudio(currentX, progress, ref lastTickIndex);
                }
            }
            catch (Exception exception)
            {
                run.VisualCoroutine = null;
                Fail(run, "The committed Manifest reveal detached after a Unity presentation error.", exception);
                yield break;
            }
            yield return null;
        }

        run.VisualCoroutine = null;
        if (IsCurrentAt(run, ManifestClientStage.Revealing) && !run.PresentationDetached)
        {
            try
            {
                FinishReveal(run, allowPulseHold: true);
            }
            catch (Exception exception)
            {
                Fail(run, "The committed Manifest reveal could not finish.", exception);
            }
        }
    }

    private void SkipReveal(ManifestRun run)
    {
        if (!IsCurrentAt(run, ManifestClientStage.Revealing) || !run.RevealClock.CanSkip)
        {
            return;
        }
        if (run.Reveal is null)
        {
            // The reveal already finished and is holding on its landing
            // pulse; a stray Skip click while the skip button is between
            // disabled states must not re-run FinishReveal.
            return;
        }
        StopVisual(run);
        try
        {
            FinishReveal(run, allowPulseHold: false);
        }
        catch (Exception exception)
        {
            Fail(run, "The committed Manifest reveal could not finish.", exception);
        }
    }

    private void FinishReveal(ManifestRun run, bool allowPulseHold)
    {
        var snapshot = run.Snapshot
            ?? throw new InvalidOperationException("A Manifest reveal has no authoritative snapshot.");
        _overlay.SetPresentationAlpha(1f);
        _overlay.SetStripX((float)run.Reveal!.Motion.FinalX);
        _overlay.RevealLandingSelection(RouletteOverlay.LandingIndex);
        _overlay.PlayOutcome(snapshot.LatestReceipt?.Outcome, snapshot.CurrentLot!.Grade);
        // The spin is over the instant the winner is shown -- the tension
        // drone and idle shimmer both belong to "still spinning," not to
        // the landing celebration that follows.
        _overlay.StopTension();
        _overlay.ResetShimmer();
        // The reveal is finished the instant the winner is shown -- disable
        // Skip immediately so a click during the landing-pulse hold can't
        // re-enter FinishReveal a second time.
        _overlay.SetSkipEnabled(false);
        run.Reveal = null;

        if (allowPulseHold && !run.ReducedMotion)
        {
            run.VisualCoroutine = _host.StartCoroutine(
                PlayLandingPulse(run, snapshot, run.PresentationGeneration));
            return;
        }

        _overlay.SetLandingPulse(RouletteOverlay.LandingIndex, 1f, 0f);
        _overlay.SetSpotlight(RouletteOverlay.LandingIndex, 0f);
        AdvanceAfterReveal(run, snapshot);
    }

    private IEnumerator PlayLandingPulse(ManifestRun run, ManifestSnapshot snapshot, long presentationGeneration)
    {
        // The landing celebration scales with the winner's rarity: a
        // BlackLabel win holds longer, punches harder, and flashes
        // brighter than a ScavGrade one, and only BlackLabel earns a
        // confetti burst -- see RarityCelebrationTuning for the exact
        // multipliers. Seal/placeholder lots are never the winner here
        // (this only runs once a real lot has landed), so no special-
        // casing is needed for the ScavGrade fallback below.
        var winnerGrade = snapshot.CurrentLot?.Grade ?? RewardRarity.ScavGrade;
        var holdSeconds = LandingPulseHoldSeconds * (float)RarityCelebrationTuning.PulseHoldMultiplier(winnerGrade);
        var scaleAmplitude = LandingPulseScaleAmplitude * (float)RarityCelebrationTuning.PulseScaleMultiplier(winnerGrade);
        var flashMultiplier = (float)RarityCelebrationTuning.PulseFlashMultiplier(winnerGrade);
        var burstConfetti = snapshot.CaseTemplateId != CaseContracts.CashCache && RarityCelebrationTuning.ShouldBurstConfetti(winnerGrade);
        if (burstConfetti)
        {
            try
            {
                _overlay.BeginConfetti();
            }
            catch (Exception)
            {
                // Confetti is a pure flourish; losing it must never cost
                // the rest of the landing celebration.
                burstConfetti = false;
            }
        }

        var elapsed = 0f;
        while (elapsed < holdSeconds &&
               IsCurrentAt(run, ManifestClientStage.Revealing) &&
               !run.PresentationDetached &&
               run.PresentationGeneration == presentationGeneration)
        {
            elapsed += Time.unscaledDeltaTime;
            var t = Mathf.Clamp01(elapsed / holdSeconds);
            var eased = (float)RouletteAnimationMath.EaseOutCubic(t);
            var scale = 1f + scaleAmplitude * (1f - eased) * Mathf.Sin(Mathf.Min(t, 1f) * Mathf.PI);
            var flash = Mathf.Clamp01((1f - eased) * flashMultiplier);
            try
            {
                _overlay.SetLandingPulse(RouletteOverlay.LandingIndex, scale, flash);
                _overlay.SetSpotlight(RouletteOverlay.LandingIndex, eased * LandingSpotlightMaxDim);
            }
            catch (Exception exception)
            {
                run.VisualCoroutine = null;
                Fail(run, "The committed Manifest reveal detached during the landing pulse.", exception);
                yield break;
            }
            if (burstConfetti)
            {
                _overlay.AdvanceConfetti(Time.unscaledDeltaTime);
            }
            yield return null;
        }

        run.VisualCoroutine = null;
        try
        {
            _overlay.SetLandingPulse(RouletteOverlay.LandingIndex, 1f, 0f);
            _overlay.SetSpotlight(RouletteOverlay.LandingIndex, 0f);
        }
        catch (Exception)
        {
            // The pulse is a cosmetic reset only; a failure here must not
            // block advancing past the reveal.
        }
        _overlay.EndConfetti();

        if (IsCurrentAt(run, ManifestClientStage.Revealing) && !run.PresentationDetached)
        {
            try
            {
                AdvanceAfterReveal(run, snapshot);
            }
            catch (Exception exception)
            {
                Fail(run, "The committed Manifest reveal could not finish.", exception);
            }
        }
    }

    private void AdvanceAfterReveal(ManifestRun run, ManifestSnapshot snapshot)
    {
        if (snapshot.Phase is ManifestPhase.Offer1 or ManifestPhase.Offer2)
        {
            ShowOffer(run, snapshot);
        }
        else if (snapshot.Phase == ManifestPhase.Entitlement)
        {
            ShowEntitlement(run, snapshot, claimRetry: false);
        }
        else
        {
            Present(run, snapshot, snapshot, recovering: true, claimRetry: false);
        }
    }

    private void ShowOffer(ManifestRun run, ManifestSnapshot snapshot)
    {
        if (snapshot.IsPremium)
        {
            ShowPremiumChoice(run, snapshot, 0);
            return;
        }
        var spriteBatch = PrepareLotSpriteLoads(run, snapshot);
        run.Stage = ManifestClientStage.Decision;
        if (!_overlay.ShowManifestOffer(
                snapshot,
                () => RevalidateAction(run, snapshot, ManifestEconomicAction.Lock),
                () => RevalidateAction(run, snapshot, ManifestEconomicAction.Burn),
                () => SaveAndClose(run)))
        {
            Fail(run, "The Lock/Burn decision could not be displayed.", null);
            return;
        }
        BeginSpriteBinding(run, spriteBatch, run.PresentationGeneration);
    }

    private void ShowPremiumChoice(ManifestRun run, ManifestSnapshot snapshot, int index)
    {
        if (!IsCurrent(run)) return;
        var spriteBatch = PrepareSpriteLoads(run, ManifestSpritePlan.ForPremiumChoices(snapshot.PremiumChoices, index));
        run.Stage = ManifestClientStage.Decision;
        if (!_overlay.ShowManifestPremiumChoice(snapshot, index,
                next => ShowPremiumChoice(run, snapshot, next),
                () => RevalidateAction(run, snapshot, ManifestEconomicAction.Lock, index + 1),
                () => SaveAndClose(run)))
        {
            Fail(run, "The saved premium choices could not be displayed.", null);
            return;
        }
        BeginSpriteBinding(run, spriteBatch, run.PresentationGeneration);
    }

    private void ShowEntitlement(ManifestRun run, ManifestSnapshot snapshot, bool claimRetry)
    {
        var spriteBatch = PrepareLotSpriteLoads(run, snapshot);
        run.Stage = ManifestClientStage.Decision;
        if (!_overlay.ShowManifestEntitlement(
                snapshot,
                claimRetry,
                () => RevalidateAction(run, snapshot, ManifestEconomicAction.Claim),
                () => RevalidateAction(run, snapshot, ManifestEconomicAction.Relay),
                () => SaveAndClose(run)))
        {
            Fail(run, "The Claim/Relay decision could not be displayed.", null);
            return;
        }
        BeginSpriteBinding(run, spriteBatch, run.PresentationGeneration);
    }

    private void SaveAndClose(ManifestRun run)
    {
        if (IsCurrentAt(run, ManifestClientStage.Decision))
            End(run, null, operationPending: false);
    }

    private void ShowMissingContent(ManifestRun run, ManifestSnapshot snapshot)
    {
        if (!snapshot.AvailableActions.CanForfeit)
        {
            Fail(run, "This Manifest is blocked by missing content, but no safe recovery action was published.", null);
            return;
        }
        run.Stage = ManifestClientStage.Decision;
        if (!_overlay.ShowManifestMissingContent(
                snapshot,
                () => End(run, "The blocked Manifest was kept for later recovery.", operationPending: false),
                () => RevalidateAction(run, snapshot, ManifestEconomicAction.Forfeit)))
        {
            Fail(run, "The missing-content recovery decision could not be displayed.", null);
        }
    }

    private void ShowTerminal(ManifestRun run, ManifestSnapshot snapshot)
    {
        if (snapshot.Phase == ManifestPhase.Confiscated)
            _overlay.PlayOutcome(ManifestRelayResult.Confiscated, snapshot.LatestReceipt?.InputGrade ?? RewardRarity.ScavGrade);
        var spriteBatch = run.LastLotSpritePlan is { } plan
            ? PrepareSpriteLoads(run, plan)
            : null;
        run.Stage = ManifestClientStage.Terminal;
        if (!_overlay.ShowManifestTerminal(snapshot, () => End(run, null, operationPending: false)))
        {
            End(run, "The terminal Manifest result could not be displayed.", operationPending: false);
            return;
        }
        if (spriteBatch is not null)
        {
            BeginSpriteBinding(run, spriteBatch, run.PresentationGeneration);
        }
    }

    private ManifestSpriteLoadBatch<Sprite> PrepareLotSpriteLoads(
        ManifestRun run,
        ManifestSnapshot snapshot)
    {
        var lot = snapshot.CurrentLot
            ?? throw new ManifestSnapshotException(
                "A disclosed current lot is required for Manifest item artwork.");
        return PrepareSpriteLoads(run, ManifestSpritePlan.ForLot(lot));
    }

    private ManifestSpriteLoadBatch<Sprite> PrepareSpriteLoads(
        ManifestRun run,
        ManifestSpritePlan plan)
    {
        StopSpriteBinding(run);
        var batch = ManifestSpriteLoadBatch<Sprite>.Start(
            plan,
            _spriteCache,
            ManifestItemSpriteLoader.LoadAsync);
        run.SpriteBatch = batch;
        run.LastLotSpritePlan = plan.LotOnly();
        foreach (var failure in batch.StartFailures)
        {
            LogSpriteFailure(failure.TemplateId, failure.Exception);
        }
        return batch;
    }

    private void BeginSpriteBinding(
        ManifestRun run,
        ManifestSpriteLoadBatch<Sprite> batch,
        long presentationGeneration)
    {
        if (!CanBindSprites(run, batch, presentationGeneration))
        {
            return;
        }

        try
        {
            run.SpriteCoroutine = _host.StartCoroutine(BindSpritesWhenReady(
                run,
                batch,
                presentationGeneration));
        }
        catch (Exception exception)
        {
            run.SpriteCoroutine = null;
            _log.LogWarning(
                $"Manifest item artwork could not start; the authoritative flow will continue: {exception.Message}");
            _overlay.CompleteSpriteLoading(batch.Plan.Requests.SelectMany(request => request.TileIds));
        }
    }

    private IEnumerator BindSpritesWhenReady(
        ManifestRun run,
        ManifestSpriteLoadBatch<Sprite> batch,
        long presentationGeneration)
    {
        var elapsed = 0d;
        yield return null;
        while (CanBindSprites(run, batch, presentationGeneration))
        {
            try
            {
                batch.BindAvailable(
                    () => CanBindSprites(run, batch, presentationGeneration),
                    (tileId, sprite) =>
                    {
                        _overlay.BindSprite(tileId, sprite);
                        _overlay.SetResultSprite(tileId, sprite);
                    },
                    LogSpriteFailure);
            }
            catch (Exception exception)
            {
                _log.LogWarning(
                    $"Manifest item artwork could not be applied; the authoritative flow will continue: {exception.Message}");
                break;
            }

            if (batch.IsComplete)
            {
                break;
            }

            elapsed = Math.Min(
                SpriteLoadTimeoutSeconds,
                elapsed + Time.unscaledDeltaTime);
            if (elapsed >= SpriteLoadTimeoutSeconds)
            {
                batch.EvictPendingLoads(templateId =>
                    _log.LogWarning(
                        $"Manifest item artwork for template '{templateId}' timed out; the authoritative flow will continue."));
                break;
            }

            yield return null;
        }

        if (CanBindSprites(run, batch, presentationGeneration))
            _overlay.CompleteSpriteLoading(batch.Plan.Requests.SelectMany(request => request.TileIds));
        if (ReferenceEquals(run.SpriteBatch, batch))
        {
            run.SpriteCoroutine = null;
        }
    }

    private bool CanBindSprites(
        ManifestRun run,
        ManifestSpriteLoadBatch<Sprite> batch,
        long presentationGeneration) =>
        ManifestSpriteBindingAuthority.CanBind(
            _disposed,
            IsCurrent(run) && ReferenceEquals(run.SpriteBatch, batch),
            run.PresentationDetached,
            run.PresentationGeneration,
            presentationGeneration);

    private void LogSpriteFailure(string templateId, Exception exception) =>
        _log.LogWarning(
            $"Manifest item artwork for template '{templateId}' is unavailable; the authoritative flow will continue: {exception.Message}");

    private void ShowPreparedRetry(ManifestRun run, ManifestSnapshot snapshot)
    {
        if (run.PresentationDetached)
        {
            End(run, "Prepared Manifest recovery remains on the server.", operationPending: false);
            return;
        }
        var action = ManifestPresentationPolicy.PreparedRecoveryAction(snapshot);
        run.Snapshot = snapshot;
        run.Stage = ManifestClientStage.Decision;
        if (!_overlay.ShowManifestVerificationError(
                "An earlier action has not finished saving.\n\nResume that same action using its saved result. This will not start a new opening or reroll your reward.",
                "RESUME SAVED ACTION",
                () => Dispatch(
                    run,
                    action,
                    snapshot,
                    action == ManifestEconomicAction.OpenTicket ? snapshot.RecoveryCaseItemId : null)))
        {
            End(run, "Prepared Manifest recovery remains on the server.", operationPending: false);
        }
    }

    private void ShowOperationRejected(
        ManifestRun run,
        ManifestEconomicAction action,
        ManifestSnapshot? snapshot,
        Exception? exception,
        string? serverError = null)
    {
        if (run.PresentationDetached)
        {
            End(run, $"The server did not advance {action}; its Manifest state remains recoverable.", operationPending: false);
            return;
        }
        if (exception is not null)
        {
            _log.LogWarning($"Manifest {action} was not advanced: {exception}");
        }
        if (snapshot is null && action == ManifestEconomicAction.OpenTicket)
        {
            _log.LogWarning($"Manifest opening rejected: {serverError}");
            Fail(run, ManifestPresentationPolicy.RejectedActionMessage(action, serverError), null);
            return;
        }
        if (snapshot?.Phase is ManifestPhase.TicketPrepared or
            ManifestPhase.ClaimPrepared or ManifestPhase.RewardOwed or ManifestPhase.RelayPrepared)
        {
            ShowPreparedRetry(run, snapshot);
            return;
        }
        if (snapshot is not null)
        {
            run.Stage = ManifestClientStage.Decision;
            if (_overlay.ShowManifestVerificationError(
                    ManifestPresentationPolicy.RejectedActionMessage(action, serverError),
                    action is ManifestEconomicAction.Relay or ManifestEconomicAction.Claim ? "BACK TO REWARD" : "BACK TO OPENING",
                    () => Present(run, snapshot, snapshot, recovering: true, claimRetry: false),
                    action == ManifestEconomicAction.Relay ? "RELAY NOT COMPLETED" : "ACTION NOT COMPLETED"))
            {
                return;
            }
        }

        Fail(run, $"The server did not advance {action}.", exception);
    }

    private void ShowOperationVerificationFailure(
        ManifestRun run,
        ManifestEconomicAction action,
        ManifestSnapshot? before,
        IResult operationResult,
        Exception exception)
    {
        _log.LogError($"Manifest {action} result verification failed: {exception}");
        if (run.PresentationDetached)
        {
            End(run, "Manifest verification remains available on the server after restart.", operationPending: false);
            return;
        }
        run.Stage = ManifestClientStage.VerifyingOperation;
        if (!_overlay.ShowManifestVerificationError(
                "We could not confirm whether your last action finished.\n\nCheck the saved result before doing anything else. This button only checks; it does not spend items or roll again.",
                "CHECK SAVED RESULT",
                () => StartOperationSnapshotFetch(run, action, before, operationResult)))
        {
            End(run, "Manifest verification remains available on the server after restart.", operationPending: false);
        }
    }

    private void ShowVerificationFailure(ManifestRun run, Exception exception, Action retry)
    {
        _log.LogError($"Manifest preflight verification failed: {exception}");
        if (run.PresentationDetached)
        {
            End(run, "No Manifest action was sent.", operationPending: false);
            return;
        }
        run.Stage = ManifestClientStage.Decision;
        if (!_overlay.ShowManifestVerificationError(
                "We could not connect to your saved opening. No new action was sent.\n\nCheck that the SPT server is running, then try again.",
                "TRY AGAIN",
                retry))
        {
            End(run, "No Manifest action was sent.", operationPending: false);
        }
    }

    private void ShowPending(ManifestRun run, string title, string detail)
    {
        if (run.PresentationDetached)
        {
            return;
        }
        StopSpriteBinding(run);
        StopCatalogSpriteBinding(run);
        if (!_overlay.ShowManifestPending(title, detail, allowRebuild: false))
        {
            run.PresentationDetached = true;
            run.PresentationGeneration++;
        }
    }

    private void Observe<T>(
        ManifestRun run,
        Task<T> task,
        Action<T> success,
        Action<Exception> failure,
        string operation)
    {
        if (!IsCurrent(run))
        {
            return;
        }
        var observation = checked(++run.ObservationGeneration);
        run.ObservationCoroutine = _host.StartCoroutine(ObserveLoop(
            run,
            observation,
            task,
            success,
            failure,
            operation));
    }

    private IEnumerator ObserveLoop<T>(
        ManifestRun run,
        long observation,
        Task<T> task,
        Action<T> success,
        Action<Exception> failure,
        string operation)
    {
        var timeout = new RelaySnapshotTimeoutBudget();
        yield return null;
        while (!task.IsCompleted && IsObservationCurrent(run, observation))
        {
            if (timeout.Advance(Time.unscaledDeltaTime))
            {
                break;
            }
            yield return null;
        }

        if (!IsObservationCurrent(run, observation))
        {
            yield break;
        }
        run.ObservationCoroutine = null;
        if (!task.IsCompleted)
        {
            failure(new TimeoutException(
                $"The {operation} timed out after {RelaySnapshotTimeoutBudget.DefaultTimeoutSeconds:0} seconds."));
            yield break;
        }

        try
        {
            success(task.GetAwaiter().GetResult());
        }
        catch (Exception exception)
        {
            failure(exception);
        }
    }

    private void Cancel(ManifestRun run)
    {
        if (!IsCurrentAt(run, ManifestClientStage.Confirming))
        {
            return;
        }
        run.Completion.TrySetResult(new FailedResult("Manifest opening was cancelled."));
        End(run, null, operationPending: false);
    }

    private void Fail(ManifestRun run, string message, Exception? exception)
    {
        if (!IsCurrent(run))
        {
            return;
        }
        if (exception is not null)
        {
            _log.LogError($"{message} {exception}");
        }
        run.Stage = ManifestClientStage.Terminal;
        run.Completion.TrySetResult(new FailedResult(message));
        TryWarn(message);
        if (run.PresentationDetached || !_overlay.ShowError(message, () => End(run, null, operationPending: false)))
        {
            End(run, message, operationPending: false);
        }
    }

    /// <summary>
    /// A distinguishable sibling of <see cref="Fail"/> for the one rejection reason common enough to
    /// deserve its own title and copy: no BR-12 Relay Key anywhere in the stash. This is only reached
    /// as a local short-circuit -- <see cref="Dispatch"/> checks <see cref="HasRelayKey"/> before ever
    /// sending a fresh OpenTicket. Prepared-ticket recovery bypasses this local check because
    /// the server may already have consumed the last key. The server still
    /// independently requires the key (see <c>SptOpeningInventory.PrepareTicket</c>); this only makes
    /// the client's own pre-check produce a friendlier message than the generic "did not advance"
    /// fallback that <see cref="Fail"/> shows for every other rejection reason.
    /// </summary>
    private void FailKeyRequired(ManifestRun run)
    {
        if (!IsCurrent(run))
        {
            return;
        }
        const string message =
            "You need a BR-12 Relay Key to open this case. Insert a key and try again.";
        run.Stage = ManifestClientStage.Terminal;
        run.Completion.TrySetResult(new FailedResult(message));
        TryWarn(message);
        if (run.PresentationDetached ||
            !_overlay.ShowKeyRequiredError(message, () => End(run, null, operationPending: false)))
        {
            End(run, message, operationPending: false);
        }
    }

    /// <summary>
    /// Local-only convenience check: does the live profile's stash contain a BR-12 Relay Key anywhere?
    /// This never substitutes for server-side validation -- the server still independently requires the
    /// key when it prepares the ticket. It exists purely so <see cref="Dispatch"/> can tell "no key" apart
    /// from every other rejection reason before ever sending OpenTicket, matching how
    /// <c>RelayInventoryBaseline.HasKey</c> already lets the Relay decision screen disable its button with
    /// a "RELAY -- KEY REQUIRED" label instead of dispatching a doomed request.
    /// </summary>
    private static bool HasRelayKey(Profile profile)
    {
        var inventory = profile.Inventory
            ?? throw new InvalidOperationException("The authenticated profile inventory is unavailable.");
        return inventory.AllRealPlayerItems.Any(item =>
            string.Equals(item.StringTemplateId, ModConstants.KeyTemplateId, StringComparison.Ordinal));
    }

    private void End(ManifestRun? run, string? warning, bool operationPending)
    {
        if (run is null || !ReferenceEquals(_active, run))
        {
            return;
        }
        if (warning is not null)
        {
            TryWarn(warning);
        }
        run.Completion.TrySetResult(new FailedResult(
            warning ?? "The Manifest presentation ended before a new case operation completed."));
        StopVisual(run);
        StopSpriteBinding(run);
        StopCatalogSpriteBinding(run);
        StopObservation(run);
        _spriteCache.Clear();
        try
        {
            _overlay.EndRun();
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Manifest overlay cleanup recovered from an error: {exception.Message}");
        }
        run.Stage = ManifestClientStage.Closed;
        _active = null;
        _generation++;
    }

    private void StopVisual(ManifestRun run)
    {
        if (run.VisualCoroutine is null)
        {
            return;
        }
        try
        {
            _host.StopCoroutine(run.VisualCoroutine);
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Could not stop a Manifest reveal coroutine: {exception.Message}");
        }
        run.VisualCoroutine = null;
    }

    private void StopSpriteBinding(ManifestRun run)
    {
        run.SpriteBatch = null;
        if (run.SpriteCoroutine is null)
        {
            return;
        }
        try
        {
            _host.StopCoroutine(run.SpriteCoroutine);
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Could not stop a Manifest sprite coroutine: {exception.Message}");
        }
        run.SpriteCoroutine = null;
    }

    // Only the case hero needs artwork. Readable odds/audit pages are text-only;
    // do not request the entire catalog merely to open the confirmation window.
    private void StartCatalogSpriteLoads(ManifestRun run, ManifestOpeningOddsSnapshot openingOdds)
    {
        try
        {
            var tiles = new[] { new ManifestTilePresentation("case:hero", CaseContracts.Name(openingOdds.CaseTemplateId),
                "CASE", RewardRarity.ScavGrade, openingOdds.CaseTemplateId) };

            StopCatalogSpriteBinding(run);
            var plan = ManifestSpritePlan.Create(tiles, tiles[0].Id);
            var batch = ManifestSpriteLoadBatch<Sprite>.Start(plan, _spriteCache, ManifestItemSpriteLoader.LoadAsync);
            run.CatalogSpriteBatch = batch;
            foreach (var failure in batch.StartFailures)
            {
                LogSpriteFailure(failure.TemplateId, failure.Exception);
            }

            BeginCatalogSpriteBinding(run, batch, run.PresentationGeneration);
        }
        catch (Exception exception)
        {
            _log.LogWarning(
                $"Case artwork could not start; opening remains available: {exception.Message}");
            _overlay.CompleteSpriteLoading(new[] { "case:hero" }, catalog: true);
        }
    }

    private void BeginCatalogSpriteBinding(
        ManifestRun run,
        ManifestSpriteLoadBatch<Sprite> batch,
        long presentationGeneration)
    {
        if (!CanBindCatalogSprites(run, batch, presentationGeneration))
        {
            return;
        }

        try
        {
            run.CatalogSpriteCoroutine = _host.StartCoroutine(BindCatalogSpritesWhenReady(
                run,
                batch,
                presentationGeneration));
        }
        catch (Exception exception)
        {
            run.CatalogSpriteCoroutine = null;
            _log.LogWarning(
                $"Case artwork could not start; opening remains available: {exception.Message}");
            _overlay.CompleteSpriteLoading(batch.Plan.Requests.SelectMany(request => request.TileIds), catalog: true);
        }
    }

    private IEnumerator BindCatalogSpritesWhenReady(
        ManifestRun run,
        ManifestSpriteLoadBatch<Sprite> batch,
        long presentationGeneration)
    {
        var elapsed = 0d;
        yield return null;
        while (CanBindCatalogSprites(run, batch, presentationGeneration))
        {
            try
            {
                batch.BindAvailable(
                    () => CanBindCatalogSprites(run, batch, presentationGeneration),
                    (tileId, sprite) => _overlay.BindCatalogSprite(tileId, sprite),
                    LogSpriteFailure);
            }
            catch (Exception exception)
            {
                _log.LogWarning(
                    $"Case artwork could not be applied; opening remains available: {exception.Message}");
                break;
            }

            if (batch.IsComplete)
            {
                break;
            }

            elapsed = Math.Min(
                SpriteLoadTimeoutSeconds,
                elapsed + Time.unscaledDeltaTime);
            if (elapsed >= SpriteLoadTimeoutSeconds)
            {
                batch.EvictPendingLoads(templateId =>
                    _log.LogWarning(
                        $"Case artwork for template '{templateId}' timed out; opening remains available."));
                break;
            }

            yield return null;
        }

        if (CanBindCatalogSprites(run, batch, presentationGeneration))
            _overlay.CompleteSpriteLoading(batch.Plan.Requests.SelectMany(request => request.TileIds), catalog: true);
        if (ReferenceEquals(run.CatalogSpriteBatch, batch))
        {
            run.CatalogSpriteCoroutine = null;
        }
    }

    private bool CanBindCatalogSprites(
        ManifestRun run,
        ManifestSpriteLoadBatch<Sprite> batch,
        long presentationGeneration) =>
        ManifestSpriteBindingAuthority.CanBind(
            _disposed,
            IsCurrent(run) && ReferenceEquals(run.CatalogSpriteBatch, batch),
            run.PresentationDetached,
            run.PresentationGeneration,
            presentationGeneration);

    private void StopCatalogSpriteBinding(ManifestRun run)
    {
        run.CatalogSpriteBatch = null;
        if (run.CatalogSpriteCoroutine is null)
        {
            return;
        }
        try
        {
            _host.StopCoroutine(run.CatalogSpriteCoroutine);
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Could not stop a Manifest audit-grid sprite coroutine: {exception.Message}");
        }
        run.CatalogSpriteCoroutine = null;
    }

    private void StopObservation(ManifestRun run)
    {
        run.ObservationGeneration++;
        if (run.ObservationCoroutine is null)
        {
            return;
        }
        try
        {
            _host.StopCoroutine(run.ObservationCoroutine);
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Could not stop a Manifest observer coroutine: {exception.Message}");
        }
        run.ObservationCoroutine = null;
    }

    private bool IsCurrent(ManifestRun run) =>
        !_disposed && ReferenceEquals(_active, run) && run.Generation == _generation;

    private bool IsCurrentAt(ManifestRun run, ManifestClientStage stage) =>
        IsCurrent(run) && run.Stage == stage;

    private bool IsObservationCurrent(ManifestRun run, long observation) =>
        IsCurrent(run) && run.ObservationGeneration == observation;

    private static void RequireProfile(ManifestRun run)
    {
        if (!ReferenceEquals(run.Session.Profile, run.Profile) ||
            !string.Equals(run.Session.Profile?.Id, run.Profile.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The authenticated profile changed during the Manifest flow.");
        }
    }

    private void TryWarn(string message)
    {
        try
        {
            EFT.Communications.NotificationManager.DisplayWarningNotification(message);
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Could not display a Manifest notification: {exception.Message}");
        }
    }

    private Task<IResult> Reject(string message)
    {
        TryWarn(message);
        return new FailedResult(message).Task;
    }

    private static async Task<ManifestSnapshot?> AsNullable(Task<ManifestSnapshot> task) =>
        await task.ConfigureAwait(false);

    private static async Task<ManifestSnapshot?> AsSnapshot(Task<ManifestCurrentState> task) =>
        (await task.ConfigureAwait(false)).Snapshot;

    private static string PendingTitle(ManifestEconomicAction action) => action switch
    {
        ManifestEconomicAction.OpenTicket => "COMMITTING MANIFEST TICKET",
        ManifestEconomicAction.Lock => "LOCKING DISPLAYED LOT",
        ManifestEconomicAction.Burn => "BURNING OFFER",
        ManifestEconomicAction.Claim => "CLAIMING MIXED LOT",
        ManifestEconomicAction.Relay => "RELAYING MIXED LOT",
        ManifestEconomicAction.Forfeit => "FORFEITING BLOCKED MANIFEST",
        _ => "COMMITTING MANIFEST ACTION"
    };

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

    private sealed class ManifestRun
    {
        public ManifestRun(
            long generation,
            IClientSession session,
            Profile profile,
            string requestedCaseId,
            string requestedCaseTemplateId = ModConstants.CaseTemplateId)
        {
            Generation = generation;
            Session = session;
            Profile = profile;
            RequestedCaseId = requestedCaseId;
            RequestedCaseTemplateId = CaseContracts.Require(requestedCaseTemplateId);
        }

        public long Generation { get; }
        public IClientSession Session { get; }
        public Profile Profile { get; }
        public string RequestedCaseId { get; }
        public string RequestedCaseTemplateId { get; }
        public TaskCompletionSource<IResult> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public OperationCallbackGate CallbackGate { get; } = new();
        public RouletteRevealClock RevealClock { get; } = new();
        public ManifestClientStage Stage { get; set; } = ManifestClientStage.Probing;
        public ManifestSnapshot? Snapshot { get; set; }
        public ManifestOpeningOddsSnapshot? OpeningOdds { get; set; }
        public ManifestSnapshot? BeforeAction { get; set; }
        public ManifestEconomicAction? PendingAction { get; set; }
        public ManifestRevealPresentation? Reveal { get; set; }
        public Coroutine? ObservationCoroutine { get; set; }
        public Coroutine? VisualCoroutine { get; set; }
        public Coroutine? SpriteCoroutine { get; set; }
        public ManifestSpriteLoadBatch<Sprite>? SpriteBatch { get; set; }
        public ManifestSpritePlan? LastLotSpritePlan { get; set; }
        public Coroutine? CatalogSpriteCoroutine { get; set; }
        public ManifestSpriteLoadBatch<Sprite>? CatalogSpriteBatch { get; set; }
        public long ObservationGeneration { get; set; }
        public long PresentationGeneration { get; set; } = 1;
        public bool RecoveryOnly { get; set; }
        public bool ReducedMotion { get; set; }
        public bool LibraryOnly { get; set; }
        public bool PresentationDetached { get; set; }
    }

    private enum ManifestClientStage
    {
        Probing,
        Confirming,
        Decision,
        OperationPending,
        VerifyingOperation,
        Revealing,
        Terminal,
        Closed
    }
}
