using System.Reflection;
using ContrabandCases.Server.Settlement;
using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers.Http;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class ProfileItemEventGateListenerTests
{
    [Fact]
    public void Listener_has_first_priority_and_depends_on_the_concrete_host_listener()
    {
        var listenerType = typeof(ProfileItemEventGateListener);
        var attribute = Assert.Single(listenerType.GetCustomAttributes<Injectable>());
        var constructor = Assert.Single(listenerType.GetConstructors());

        Assert.Equal(int.MinValue, attribute.TypePriority);
        Assert.True(typeof(IHttpListener).IsAssignableFrom(listenerType));
        Assert.Equal(typeof(SptHttpListener), constructor.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void CanHandle_requires_the_exact_item_event_path_and_the_inner_listener()
    {
        var innerCalls = 0;
        var listener = CreateListener(
            new ProfileLockPool(),
            _ =>
            {
                innerCalls++;
                return true;
            });

        var handled = listener.CanHandle(CreateContext(ProfileItemEventGateListener.ItemEventPath));

        Assert.True(handled);
        Assert.Equal(1, innerCalls);
    }

    [Theory]
    [InlineData("/client/game/profile/items/moving/")]
    [InlineData("/CLIENT/GAME/PROFILE/ITEMS/MOVING")]
    [InlineData("/client/game/profile/items/moving/extra")]
    public void CanHandle_rejects_non_exact_paths_without_calling_the_inner_listener(string path)
    {
        var innerCalls = 0;
        var listener = CreateListener(
            new ProfileLockPool(),
            _ =>
            {
                innerCalls++;
                return true;
            });

        var handled = listener.CanHandle(CreateContext(path));

        Assert.False(handled);
        Assert.Equal(0, innerCalls);
    }

    [Fact]
    public void CanHandle_rejects_the_exact_path_when_the_inner_listener_rejects_it()
    {
        var innerCalls = 0;
        var listener = CreateListener(
            new ProfileLockPool(),
            _ =>
            {
                innerCalls++;
                return false;
            });

        var handled = listener.CanHandle(CreateContext(ProfileItemEventGateListener.ItemEventPath));

        Assert.False(handled);
        Assert.Equal(1, innerCalls);
    }

    [Fact]
    public async Task HandleAsync_delegates_exactly_once_with_the_HttpServer_supplied_session_id()
    {
        var expectedSessionId = new MongoId();
        var cookieSessionId = new MongoId();
        var context = CreateContext(ProfileItemEventGateListener.ItemEventPath);
        context.Request.Headers.Cookie = $"PHPSESSID={cookieSessionId}";
        var calls = 0;
        var actualSessionId = MongoId.Empty();
        var listener = CreateListener(
            new ProfileLockPool(),
            handleAsync: (sessionId, _, _) =>
            {
                calls++;
                actualSessionId = sessionId;
                return Task.CompletedTask;
            });

        await listener.HandleAsync(expectedSessionId, context, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(expectedSessionId, actualSessionId);
        Assert.NotEqual(cookieSessionId, actualSessionId);
    }

    [Fact]
    public async Task Same_profile_callbacks_are_serialized_across_the_entire_inner_lifecycle()
    {
        var profileId = new MongoId();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var listener = CreateListener(
            new ProfileLockPool(),
            handleAsync: async (_, _, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstEntered.SetResult();
                    await releaseFirst.Task;
                }
            });

        var first = listener.HandleAsync(profileId, CreateContext(), CancellationToken.None);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = listener.HandleAsync(profileId, CreateContext(), CancellationToken.None);

        try
        {
            Assert.False(second.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            releaseFirst.TrySetResult();
        }

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Different_profile_callbacks_remain_concurrent()
    {
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBoth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var listener = CreateListener(
            new ProfileLockPool(),
            handleAsync: async (_, _, _) =>
            {
                if (Interlocked.Increment(ref active) == 2)
                {
                    bothEntered.SetResult();
                }

                await releaseBoth.Task;
                Interlocked.Decrement(ref active);
            });

        var first = listener.HandleAsync(new MongoId(), CreateContext(), CancellationToken.None);
        var second = listener.HandleAsync(new MongoId(), CreateContext(), CancellationToken.None);
        try
        {
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, Volatile.Read(ref active));
        }
        finally
        {
            releaseBoth.TrySetResult();
        }

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Inner_service_lock_reenters_the_ambient_profile_lock()
    {
        var profileLocks = new ProfileLockPool();
        var profileId = new MongoId();
        var nestedEntered = false;
        var listener = CreateListener(
            profileLocks,
            handleAsync: async (sessionId, _, cancellationToken) =>
            {
                await using var nested =
                    await profileLocks.AcquireAsync(sessionId, cancellationToken);
                nestedEntered = true;
            });

        await listener
            .HandleAsync(profileId, CreateContext(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(nestedEntered);
    }

    [Fact]
    public async Task Inner_error_releases_the_profile_lock()
    {
        var calls = 0;
        var listener = CreateListener(
            new ProfileLockPool(),
            handleAsync: (_, _, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new InvalidOperationException("expected");
                }

                return Task.CompletedTask;
            });
        var profileId = new MongoId();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            listener.HandleAsync(profileId, CreateContext(), CancellationToken.None));
        await listener
            .HandleAsync(profileId, CreateContext(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Inner_cancellation_releases_the_profile_lock()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var listener = CreateListener(
            new ProfileLockPool(),
            handleAsync: async (_, _, cancellationToken) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstEntered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
            });
        var profileId = new MongoId();
        using var cancellation = new CancellationTokenSource();
        var first = listener.HandleAsync(profileId, CreateContext(), cancellation.Token);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await listener
            .HandleAsync(profileId, CreateContext(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);
    }

    private static ProfileItemEventGateListener CreateListener(
        ProfileLockPool profileLocks,
        Func<HttpContext, bool>? canHandle = null,
        Func<MongoId, HttpContext, CancellationToken, Task>? handleAsync = null) =>
        new(
            canHandle ?? (_ => true),
            handleAsync ?? ((_, _, _) => Task.CompletedTask),
            profileLocks);

    private static DefaultHttpContext CreateContext(
        string path = ProfileItemEventGateListener.ItemEventPath)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        return context;
    }
}
