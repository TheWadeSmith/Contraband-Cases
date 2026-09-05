using System.Collections.Concurrent;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;

namespace ContrabandCases.Server.Settlement;

[Injectable(InjectionType.Singleton)]
public sealed class ProfileLockPool
{
    private readonly ConcurrentDictionary<MongoId, SemaphoreSlim> _locks = new();
    private readonly AsyncLocal<AmbientFrame?> _ambientFrame = new();

    public ValueTask<IAsyncDisposable> AcquireAsync(MongoId profileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ambient = NormalizeAmbientFrame();
        if (ambient is not null)
        {
            if (!ambient.Acquisition.IsCompleted)
            {
                throw new InvalidOperationException(
                    "A profile lock cannot be acquired again before the current acquisition completes.");
            }

            if (ambient.ProfileId == profileId)
            {
                return ValueTask.FromResult<IAsyncDisposable>(NoopLease.Instance);
            }
        }

        var semaphore = _locks.GetOrAdd(profileId, static _ => new SemaphoreSlim(1, 1));
        var acquisition = semaphore.WaitAsync(cancellationToken);
        if (acquisition.IsCompleted)
        {
            acquisition.GetAwaiter().GetResult();
        }

        var frame = new AmbientFrame(profileId, acquisition, ambient);
        _ambientFrame.Value = frame;
        var releaser = new Releaser(this, semaphore, frame);
        return acquisition.IsCompletedSuccessfully
            ? ValueTask.FromResult<IAsyncDisposable>(releaser)
            : AwaitAcquisitionAsync(acquisition, releaser);
    }

    private static async ValueTask<IAsyncDisposable> AwaitAcquisitionAsync(
        Task acquisition,
        IAsyncDisposable releaser)
    {
        await acquisition.ConfigureAwait(false);
        return releaser;
    }

    private AmbientFrame? NormalizeAmbientFrame()
    {
        var ambient = _ambientFrame.Value;
        var normalized = ambient;
        while (normalized is not null &&
               normalized.Acquisition.IsCompleted &&
               !normalized.Acquisition.IsCompletedSuccessfully)
        {
            normalized = normalized.Parent;
        }

        if (!ReferenceEquals(ambient, normalized))
        {
            _ambientFrame.Value = normalized;
        }

        return normalized;
    }

    private void Release(AmbientFrame frame, SemaphoreSlim semaphore)
    {
        var isCurrentFrame = ReferenceEquals(_ambientFrame.Value, frame);
        if (isCurrentFrame)
        {
            _ambientFrame.Value = frame.Parent;
        }

        semaphore.Release();
        if (!isCurrentFrame)
        {
            throw new InvalidOperationException("Profile locks must be released in acquisition order.");
        }
    }

    private sealed record AmbientFrame(MongoId ProfileId, Task Acquisition, AmbientFrame? Parent);

    private sealed class Releaser : IAsyncDisposable
    {
        private LeaseState? _state;

        public Releaser(ProfileLockPool owner, SemaphoreSlim semaphore, AmbientFrame frame) =>
            _state = new LeaseState(owner, semaphore, frame);

        public ValueTask DisposeAsync()
        {
            var state = Interlocked.Exchange(ref _state, null);
            state?.Owner.Release(state.Frame, state.Semaphore);
            return ValueTask.CompletedTask;
        }

        private sealed record LeaseState(ProfileLockPool Owner, SemaphoreSlim Semaphore, AmbientFrame Frame);
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public static NoopLease Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
