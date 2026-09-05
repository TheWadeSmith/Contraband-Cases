using ContrabandCases.Server.Loot;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Utils;

namespace ContrabandCases.Server.Settlement;

[Injectable(TypePriority = Priority)]
public sealed class RaidLifecycleBeforeRouter : StaticRouter
{
    internal const int Priority = OnLoadOrder.Routers - 1;

    public RaidLifecycleBeforeRouter(
        JsonUtil jsonUtil,
        ProfileLockPool profileLocks,
        RaidSessionState raidSessions,
        KeyRaidAudit keyAudit)
        : base(jsonUtil,
        [
            new RouteAction<EmptyRequestData>(
                RaidLifecycleRoutes.GameStart,
                async (url, _, sessionId, output, cancellationToken) =>
                {
                    var result = await ApplyTransitionAsync(url, sessionId, output, profileLocks, raidSessions, cancellationToken);
                    keyAudit.Forget(sessionId);
                    return result;
                }),
            new RouteAction<EmptyRequestData>(
                RaidLifecycleRoutes.GameLogout,
                async (url, _, sessionId, output, cancellationToken) =>
                {
                    var result = await ApplyTransitionAsync(url, sessionId, output, profileLocks, raidSessions, cancellationToken);
                    keyAudit.Forget(sessionId);
                    return result;
                }),
            new RouteAction<StartLocalRaidRequestData>(
                RaidLifecycleRoutes.LocalRaidStart,
                async (url, request, sessionId, output, cancellationToken) =>
                {
                    await using var profileLock = await profileLocks.AcquireAsync(sessionId, cancellationToken);
                    var transit = raidSessions.GetPhase(sessionId) == RaidSessionPhase.Transit;
                    raidSessions.BeginRaidStart(sessionId);
                    keyAudit.Begin(sessionId, request, transit);
                    return output ?? string.Empty;
                }),
            new RouteAction<EndLocalRaidRequestData>(
                RaidLifecycleRoutes.LocalRaidEnd,
                (url, _, sessionId, output, cancellationToken) =>
                    ApplyTransitionAsync(url, sessionId, output, profileLocks, raidSessions, cancellationToken))
        ])
    {
    }

    internal static async ValueTask<string> ApplyTransitionAsync(
        string url,
        MongoId profileId,
        string? output,
        ProfileLockPool profileLocks,
        RaidSessionState raidSessions,
        CancellationToken cancellationToken)
    {
        await using var profileLock =
            await profileLocks.AcquireAsync(profileId, cancellationToken).ConfigureAwait(false);
        switch (url)
        {
            case RaidLifecycleRoutes.GameStart:
                raidSessions.BeginGameStart(profileId);
                break;
            case RaidLifecycleRoutes.GameLogout:
                raidSessions.BeginLogout(profileId);
                break;
            case RaidLifecycleRoutes.LocalRaidStart:
                raidSessions.BeginRaidStart(profileId);
                break;
            case RaidLifecycleRoutes.LocalRaidEnd:
                raidSessions.BeginRaidEnd(profileId);
                break;
            default:
                throw new InvalidOperationException($"Unsupported raid lifecycle route '{url}'.");
        }

        return output ?? string.Empty;
    }
}
