using ContrabandCases.Server.Settlement;
using SPTarkov.Server.Core.Models.Common;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class ProfileLockPoolTests
{
    [Fact]
    public async Task Same_profile_is_mutually_exclusive()
    {
        var pool = new ProfileLockPool();
        var profileId = new MongoId();
        await using var first = await pool.AcquireAsync(profileId, CancellationToken.None);
        var acquisitionStarted = StartedSignal();
        var second = AcquireIndependentlyAsync(
            pool,
            profileId,
            CancellationToken.None,
            acquisitionStarted);

        await acquisitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(second.IsCompleted);
        await first.DisposeAsync();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Nested_same_profile_acquisition_is_a_no_op_until_outer_lease_releases()
    {
        var pool = new ProfileLockPool();
        var profileId = new MongoId();
        await using var outer = await pool.AcquireAsync(profileId, CancellationToken.None);

        var nested = pool.AcquireAsync(profileId, CancellationToken.None).AsTask();

        Assert.True(nested.IsCompletedSuccessfully);
        await using (await nested)
        {
        }

        var acquisitionStarted = StartedSignal();
        var independent = AcquireIndependentlyAsync(
            pool,
            profileId,
            CancellationToken.None,
            acquisitionStarted);
        await acquisitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(independent.IsCompleted);

        await outer.DisposeAsync();
        await independent.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Nested_reentry_survives_an_asynchronously_completed_outer_acquisition()
    {
        var pool = new ProfileLockPool();
        var profileId = new MongoId();
        await using var held = await pool.AcquireAsync(profileId, CancellationToken.None);
        var contenderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contender = RunIndependentlyAsync(async () =>
        {
            var acquisition = pool.AcquireAsync(profileId, CancellationToken.None);
            contenderStarted.SetResult();
            await using var outer = await acquisition;
            await using var nested = await pool.AcquireAsync(profileId, CancellationToken.None);
        });

        await contenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(contender.IsCompleted);

        await held.DisposeAsync();
        await contender.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Different_profiles_do_not_block_each_other()
    {
        var pool = new ProfileLockPool();
        await using var first = await pool.AcquireAsync(new MongoId(), CancellationToken.None);

        var second = AcquireIndependentlyAsync(pool, new MongoId(), CancellationToken.None);

        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Cancellation_while_waiting_does_not_acquire_or_release_the_lock()
    {
        var pool = new ProfileLockPool();
        var profileId = new MongoId();
        await using var first = await pool.AcquireAsync(profileId, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waitingStarted = StartedSignal();
        var waiting = AcquireIndependentlyAsync(
            pool,
            profileId,
            cancellation.Token,
            waitingStarted);

        await waitingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var nextStarted = StartedSignal();
        var next = AcquireIndependentlyAsync(
            pool,
            profileId,
            CancellationToken.None,
            nextStarted);
        await nextStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(next.IsCompleted);
        await first.DisposeAsync();
        await next.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static Task AcquireIndependentlyAsync(
        ProfileLockPool pool,
        MongoId profileId,
        CancellationToken cancellationToken,
        TaskCompletionSource? acquisitionStarted = null) =>
        RunIndependentlyAsync(async () =>
        {
            var acquisition = pool.AcquireAsync(profileId, cancellationToken);
            acquisitionStarted?.SetResult();
            await using var acquired = await acquisition;
        });

    private static TaskCompletionSource StartedSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task RunIndependentlyAsync(Func<Task> action)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(action);
        }
    }
}
