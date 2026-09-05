namespace ContrabandCases.Client.Opening;

public enum OpeningPhase
{
    Idle,
    Confirming,
    Pending,
    Revealing,
    Result,
    Failed,
    Disposed
}

public sealed class OpeningPhaseMachine : IDisposable
{
    public const double SkipDelaySeconds = 1.2;

    private readonly object _sync = new();
    private OpeningPhase _phase = OpeningPhase.Idle;
    private long _generation;

    public OpeningPhase Phase
    {
        get
        {
            lock (_sync)
            {
                return _phase;
            }
        }
    }

    public long Generation
    {
        get
        {
            lock (_sync)
            {
                return _generation;
            }
        }
    }

    public bool TryOpen(out long token)
    {
        lock (_sync)
        {
            if (_phase != OpeningPhase.Idle)
            {
                token = 0;
                return false;
            }

            token = ++_generation;
            _phase = OpeningPhase.Confirming;
            return true;
        }
    }

    // Restart recovery resumes an already-prepared server transaction without
    // manufacturing a confirmation or a second user decision.
    public bool TryBeginRecovery(out long token)
    {
        lock (_sync)
        {
            if (_phase != OpeningPhase.Idle)
            {
                token = 0;
                return false;
            }

            token = ++_generation;
            _phase = OpeningPhase.Pending;
            return true;
        }
    }

    public bool TryCancel(long token) => TryRelease(token, OpeningPhase.Confirming);

    // The caller receiving true owns the one dispatch for this opening.
    public bool TryConfirm(long token) =>
        TryTransition(token, OpeningPhase.Confirming, OpeningPhase.Pending);

    public bool TryCommit(long token) =>
        TryTransition(token, OpeningPhase.Pending, OpeningPhase.Revealing);

    public bool CanSkip(long token, double elapsedUnscaledSeconds)
    {
        lock (_sync)
        {
            return IsCurrent(token)
                && _phase == OpeningPhase.Revealing
                && IsEligibleSkipTime(elapsedUnscaledSeconds);
        }
    }

    public bool TrySkip(long token, double elapsedUnscaledSeconds)
    {
        lock (_sync)
        {
            if (!IsCurrent(token)
                || _phase != OpeningPhase.Revealing
                || !IsEligibleSkipTime(elapsedUnscaledSeconds))
            {
                return false;
            }

            _phase = OpeningPhase.Result;
            return true;
        }
    }

    public bool TryComplete(long token) =>
        TryTransition(token, OpeningPhase.Revealing, OpeningPhase.Result);

    // Secure or Relay owns one new server operation while preserving this opening's gate.
    public bool TryContinue(long token) =>
        TryTransition(token, OpeningPhase.Result, OpeningPhase.Pending);

    // Secure and presentation-only confiscation settle without another reward strip.
    public bool TryResolvePending(long token) =>
        TryTransition(token, OpeningPhase.Pending, OpeningPhase.Result);

    public bool TryAbortRevealToResult(long token) =>
        TryTransition(token, OpeningPhase.Revealing, OpeningPhase.Result);

    public bool TryFail(long token)
    {
        lock (_sync)
        {
            if (!IsCurrent(token)
                || _phase is not (OpeningPhase.Confirming or OpeningPhase.Pending or OpeningPhase.Revealing))
            {
                return false;
            }

            _phase = OpeningPhase.Failed;
            return true;
        }
    }

    public bool TryClose(long token)
    {
        lock (_sync)
        {
            if (!IsCurrent(token)
                || _phase is not (OpeningPhase.Result or OpeningPhase.Failed))
            {
                return false;
            }

            _phase = OpeningPhase.Idle;
            _generation++;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _generation++;
            _phase = OpeningPhase.Disposed;
        }
    }

    private bool TryTransition(long token, OpeningPhase expected, OpeningPhase next)
    {
        lock (_sync)
        {
            if (!IsCurrent(token) || _phase != expected)
            {
                return false;
            }

            _phase = next;
            return true;
        }
    }

    private bool IsCurrent(long token) => token != 0 && token == _generation;

    private bool TryRelease(long token, OpeningPhase expected)
    {
        lock (_sync)
        {
            if (!IsCurrent(token) || _phase != expected)
            {
                return false;
            }

            _phase = OpeningPhase.Idle;
            _generation++;
            return true;
        }
    }

    private static bool IsEligibleSkipTime(double elapsedUnscaledSeconds) =>
        !double.IsNaN(elapsedUnscaledSeconds)
        && !double.IsInfinity(elapsedUnscaledSeconds)
        && elapsedUnscaledSeconds >= SkipDelaySeconds;
}
