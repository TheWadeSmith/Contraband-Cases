using ContrabandCases.Server.Loot;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;

namespace ContrabandCases.Server.Settlement;

[Injectable(TypePriority = Priority)]
public sealed class RaidLifecycleAfterRouter : StaticRouter
{
    internal const int Priority = OnLoadOrder.Routers + 1;

    public RaidLifecycleAfterRouter(
        JsonUtil jsonUtil,
        ProfileLockPool profileLocks,
        RaidSessionState raidSessions,
        SaveServer saveServer,
        KeyRaidAudit keyAudit)
        : base(jsonUtil,
        [
            new RouteAction<EmptyRequestData>(
                RaidLifecycleRoutes.GameStart,
                (_, _, sessionId, output, _) =>
                    CompleteGameStartAsync(sessionId, output, profileLocks, raidSessions, saveServer)),
            new RouteAction<EmptyRequestData>(
                RaidLifecycleRoutes.GameLogout,
                (_, _, sessionId, output, _) =>
                    CompleteLogoutAsync(sessionId, output, profileLocks, raidSessions)),
            new RouteAction<StartLocalRaidRequestData>(
                RaidLifecycleRoutes.LocalRaidStart,
                (_, _, sessionId, output, _) =>
                    CompleteRaidStartAsync(sessionId, output, profileLocks, raidSessions)),
            new RouteAction<EndLocalRaidRequestData>(
                RaidLifecycleRoutes.LocalRaidEnd,
                (_, request, sessionId, output, _) =>
                    CompleteRaidEndAsync(sessionId, request, output, profileLocks, raidSessions, keyAudit))
        ])
    {
    }

    internal static async ValueTask<string> CompleteGameStartAsync(
        MongoId profileId,
        string? output,
        ProfileLockPool profileLocks,
        RaidSessionState raidSessions,
        SaveServer saveServer)
    {
        await using var profileLock =
            await profileLocks.AcquireAsync(profileId, CancellationToken.None).ConfigureAwait(false);
        var profileIsReady =
            !profileId.IsEmpty &&
            saveServer.ProfileExists(profileId) &&
            !saveServer.IsProfileInvalidOrUnloadable(profileId);
        raidSessions.CompleteGameStart(profileId, profileIsReady);
        return output ?? string.Empty;
    }

    internal static async ValueTask<string> CompleteLogoutAsync(
        MongoId profileId,
        string? output,
        ProfileLockPool profileLocks,
        RaidSessionState raidSessions)
    {
        await using var profileLock =
            await profileLocks.AcquireAsync(profileId, CancellationToken.None).ConfigureAwait(false);
        raidSessions.CompleteLogout(profileId);
        return output ?? string.Empty;
    }

    internal static async ValueTask<string> CompleteRaidStartAsync(
        MongoId profileId,
        string? output,
        ProfileLockPool profileLocks,
        RaidSessionState raidSessions)
    {
        await using var profileLock =
            await profileLocks.AcquireAsync(profileId, CancellationToken.None).ConfigureAwait(false);
        raidSessions.CompleteRaidStart(profileId);
        return output ?? string.Empty;
    }

    internal static async ValueTask<string> CompleteRaidEndAsync(
        MongoId profileId,
        EndLocalRaidRequestData request,
        string? output,
        ProfileLockPool profileLocks,
        RaidSessionState raidSessions,
        KeyRaidAudit? keyAudit = null)
    {
        await using var profileLock =
            await profileLocks.AcquireAsync(profileId, CancellationToken.None).ConfigureAwait(false);
        raidSessions.CompleteRaidEnd(
            profileId,
            request.Results?.IsMapToMapTransfer() == true);
        keyAudit?.End(profileId, request, request.Results?.IsMapToMapTransfer() == true);
        return output ?? string.Empty;
    }
}
