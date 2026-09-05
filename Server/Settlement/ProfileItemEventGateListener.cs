using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers.Http;

namespace ContrabandCases.Server.Settlement;

[Injectable(TypePriority = int.MinValue)]
public sealed class ProfileItemEventGateListener : IHttpListener
{
    internal const string ItemEventPath = "/client/game/profile/items/moving";

    private readonly Func<HttpContext, bool> _innerCanHandle;
    private readonly Func<MongoId, HttpContext, CancellationToken, Task> _innerHandleAsync;
    private readonly ProfileLockPool _profileLocks;

    public ProfileItemEventGateListener(SptHttpListener inner, ProfileLockPool profileLocks)
        : this(inner.CanHandle, inner.HandleAsync, profileLocks)
    {
    }

    internal ProfileItemEventGateListener(
        Func<HttpContext, bool> innerCanHandle,
        Func<MongoId, HttpContext, CancellationToken, Task> innerHandleAsync,
        ProfileLockPool profileLocks)
    {
        ArgumentNullException.ThrowIfNull(innerCanHandle);
        ArgumentNullException.ThrowIfNull(innerHandleAsync);
        ArgumentNullException.ThrowIfNull(profileLocks);

        _innerCanHandle = innerCanHandle;
        _innerHandleAsync = innerHandleAsync;
        _profileLocks = profileLocks;
    }

    public bool CanHandle(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return string.Equals(context.Request.Path.Value, ItemEventPath, StringComparison.Ordinal) &&
               _innerCanHandle(context);
    }

    public async Task HandleAsync(
        MongoId sessionId,
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using var profileLock =
            await _profileLocks.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await _innerHandleAsync(sessionId, context, cancellationToken).ConfigureAwait(false);
    }
}
