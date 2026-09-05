using System.Collections.Concurrent;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;

namespace ContrabandCases.Server.Settlement;

[Injectable(InjectionType.Singleton)]
public sealed class RaidSessionState
{
    private readonly ConcurrentDictionary<MongoId, RaidSessionPhase> _phases = new();

    internal RaidSessionPhase GetPhase(MongoId profileId) =>
        _phases.TryGetValue(profileId, out var phase) ? phase : RaidSessionPhase.Unknown;

    internal void BeginGameStart(MongoId profileId) =>
        Transition(
            profileId,
            RaidSessionPhase.Initializing,
            "begin game start",
            RaidSessionPhase.Unknown,
            // A client crash does not send logout. Native startup must also be
            // retryable after a failed initialization; neither path opens the
            // lobby until the after-router verifies that the profile is ready.
            RaidSessionPhase.Initializing,
            RaidSessionPhase.Lobby);

    internal void CompleteGameStart(MongoId profileId, bool profileIsReady) =>
        Transition(
            profileId,
            profileIsReady ? RaidSessionPhase.Lobby : RaidSessionPhase.Initializing,
            "complete game start",
            RaidSessionPhase.Initializing);

    internal void BeginRaidStart(MongoId profileId) =>
        Transition(
            profileId,
            RaidSessionPhase.RaidStarting,
            "begin raid start",
            RaidSessionPhase.Lobby,
            RaidSessionPhase.Transit);

    internal void CompleteRaidStart(MongoId profileId) =>
        Transition(
            profileId,
            RaidSessionPhase.InRaid,
            "complete raid start",
            RaidSessionPhase.RaidStarting);

    internal void BeginRaidEnd(MongoId profileId) =>
        Transition(
            profileId,
            RaidSessionPhase.RaidEnding,
            "begin raid end",
            RaidSessionPhase.InRaid);

    internal void CompleteRaidEnd(MongoId profileId, bool isTransit) =>
        Transition(
            profileId,
            isTransit ? RaidSessionPhase.Transit : RaidSessionPhase.Lobby,
            "complete raid end",
            RaidSessionPhase.RaidEnding);

    internal void BeginLogout(MongoId profileId) =>
        Transition(
            profileId,
            RaidSessionPhase.LoggingOut,
            "begin logout",
            RaidSessionPhase.Unknown,
            RaidSessionPhase.Initializing,
            RaidSessionPhase.Lobby,
            RaidSessionPhase.RaidStarting,
            RaidSessionPhase.InRaid,
            RaidSessionPhase.RaidEnding,
            RaidSessionPhase.Transit);

    internal void CompleteLogout(MongoId profileId)
    {
        while (true)
        {
            var phase = GetPhase(profileId);
            EnsureExpectedPhase(profileId, phase, "complete logout", RaidSessionPhase.LoggingOut);
            if (((ICollection<KeyValuePair<MongoId, RaidSessionPhase>>)_phases).Remove(
                    new KeyValuePair<MongoId, RaidSessionPhase>(profileId, RaidSessionPhase.LoggingOut)))
            {
                return;
            }
        }
    }

    internal void RequireLobby(MongoId profileId)
    {
        if (GetPhase(profileId) != RaidSessionPhase.Lobby)
        {
            throw new InvalidOperationException("Contraband cases can be opened only from the lobby.");
        }
    }

    private void Transition(
        MongoId profileId,
        RaidSessionPhase nextPhase,
        string operation,
        params RaidSessionPhase[] expectedPhases)
    {
        while (true)
        {
            var currentPhase = GetPhase(profileId);
            EnsureExpectedPhase(profileId, currentPhase, operation, expectedPhases);
            var transitioned = currentPhase == RaidSessionPhase.Unknown
                ? _phases.TryAdd(profileId, nextPhase)
                : _phases.TryUpdate(profileId, nextPhase, currentPhase);
            if (transitioned)
            {
                return;
            }
        }
    }

    private static void EnsureExpectedPhase(
        MongoId profileId,
        RaidSessionPhase currentPhase,
        string operation,
        params RaidSessionPhase[] expectedPhases)
    {
        if (!expectedPhases.Contains(currentPhase))
        {
            throw new InvalidOperationException(
                $"Cannot {operation} for profile '{profileId}' while its raid session is {currentPhase}.");
        }
    }
}

internal enum RaidSessionPhase
{
    Unknown,
    Initializing,
    Lobby,
    RaidStarting,
    InRaid,
    RaidEnding,
    Transit,
    LoggingOut
}

internal static class RaidLifecycleRoutes
{
    public const string GameStart = "/client/game/start";
    public const string GameLogout = "/client/game/logout";
    public const string LocalRaidStart = "/client/match/local/start";
    public const string LocalRaidEnd = "/client/match/local/end";
}
