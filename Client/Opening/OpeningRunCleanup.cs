namespace ContrabandCases.Client.Opening;

public enum OpeningRunExit
{
    ConfirmationCancel,
    Success,
    Failure,
    PresentationException,
    SceneTeardown,
    Shutdown
}

public sealed class OpeningRunCleanup
{
    private readonly object _sync = new();
    private readonly Action[] _presentationCleanup;
    private readonly Action _releaseGate;
    private readonly Action<Exception> _reportError;
    private bool _presentationDetached;
    private bool _gateReleased;
    private bool _disposed;
    private long _presentationGeneration = 1;

    public OpeningRunCleanup(
        Action disableBlocker,
        Action removeListeners,
        Action releaseTiles,
        Action stopVisuals,
        Action restoreSelection,
        Action deactivateRoot,
        Action releaseGate,
        Action<Exception> reportError)
    {
        _presentationCleanup =
        [
            disableBlocker ?? throw new ArgumentNullException(nameof(disableBlocker)),
            removeListeners ?? throw new ArgumentNullException(nameof(removeListeners)),
            releaseTiles ?? throw new ArgumentNullException(nameof(releaseTiles)),
            stopVisuals ?? throw new ArgumentNullException(nameof(stopVisuals)),
            restoreSelection ?? throw new ArgumentNullException(nameof(restoreSelection)),
            deactivateRoot ?? throw new ArgumentNullException(nameof(deactivateRoot))
        ];
        _releaseGate = releaseGate ?? throw new ArgumentNullException(nameof(releaseGate));
        _reportError = reportError ?? throw new ArgumentNullException(nameof(reportError));
    }

    public bool PresentationDetached
    {
        get
        {
            lock (_sync)
            {
                return _presentationDetached;
            }
        }
    }

    public bool GateReleased
    {
        get
        {
            lock (_sync)
            {
                return _gateReleased;
            }
        }
    }

    public long PresentationGeneration
    {
        get
        {
            lock (_sync)
            {
                return _presentationGeneration;
            }
        }
    }

    public bool CanBind(long generation)
    {
        lock (_sync)
        {
            return !_disposed &&
                !_presentationDetached &&
                generation == _presentationGeneration;
        }
    }

    public void Exit(OpeningRunExit exit, bool operationPending)
    {
        if (exit == OpeningRunExit.Shutdown)
        {
            lock (_sync)
            {
                _disposed = true;
            }
        }

        DetachPresentation();
        if (!operationPending || exit is not (OpeningRunExit.SceneTeardown or OpeningRunExit.Shutdown))
        {
            ReleaseGate();
        }
    }

    public void DetachPresentation()
    {
        lock (_sync)
        {
            if (_presentationDetached)
            {
                return;
            }

            _presentationDetached = true;
            _presentationGeneration++;
        }

        foreach (var cleanup in _presentationCleanup)
        {
            RunGuarded(cleanup);
        }
    }

    public void ReleaseGate()
    {
        lock (_sync)
        {
            if (_gateReleased)
            {
                return;
            }

            _gateReleased = true;
        }

        RunGuarded(_releaseGate);
    }

    private void RunGuarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            try
            {
                _reportError(exception);
            }
            catch
            {
                // Cleanup must continue even if diagnostics are unavailable during teardown.
            }
        }
    }
}
