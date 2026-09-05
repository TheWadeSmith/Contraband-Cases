using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Enums;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class RaidLifecycleTests
{
    private static readonly MongoId ProfileId = "aaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Client_relaunch_without_logout_reinitializes_a_lobby_session()
    {
        var state = new RaidSessionState();
        EnterLobby(state);
        var output = await RaidLifecycleBeforeRouter.ApplyTransitionAsync(
            RaidLifecycleRoutes.GameStart, ProfileId, "native-output",
            new ProfileLockPool(), state, CancellationToken.None);

        Assert.Equal("native-output", output);
        Assert.Equal(RaidSessionPhase.Initializing, state.GetPhase(ProfileId));
        Assert.Throws<InvalidOperationException>(() => state.RequireLobby(ProfileId));
        state.CompleteGameStart(ProfileId, profileIsReady: true);
        state.RequireLobby(ProfileId);
    }

    [Fact]
    public void Retried_game_start_after_native_failure_stays_closed_until_ready()
    {
        var state = new RaidSessionState();
        state.BeginGameStart(ProfileId);
        // The native handler failed, so its after-router never ran.
        state.BeginGameStart(ProfileId);
        state.CompleteGameStart(ProfileId, profileIsReady: false);
        Assert.Throws<InvalidOperationException>(() => state.RequireLobby(ProfileId));
        state.BeginGameStart(ProfileId);
        state.CompleteGameStart(ProfileId, profileIsReady: true);
        state.RequireLobby(ProfileId);
    }

    [Fact]
    public void Direct_open_route_rejects_unknown_and_in_raid_sessions()
    {
        var state = new RaidSessionState();
        var request = ValidOpeningRequest();

        Assert.Throws<InvalidOperationException>(() =>
            ContrabandCaseRouter.ValidateRequest(ModConstants.OpenAction, request, state, ProfileId));

        EnterLobby(state);
        ContrabandCaseRouter.ValidateRequest(ModConstants.OpenAction, request, state, ProfileId);
        state.BeginRaidStart(ProfileId);
        state.CompleteRaidStart(ProfileId);

        Assert.Throws<InvalidOperationException>(() =>
            ContrabandCaseRouter.ValidateRequest(ModConstants.OpenAction, request, state, ProfileId));
    }

    [Fact]
    public async Task Raid_start_waits_for_the_shared_profile_lock_then_closes_opening()
    {
        var state = new RaidSessionState();
        EnterLobby(state);
        var locks = new ProfileLockPool();
        var (holder, releaseHolder) = await HoldProfileLockIndependentlyAsync(locks, ProfileId);
        try
        {
            var startTask = RaidLifecycleBeforeRouter.ApplyTransitionAsync(
                RaidLifecycleRoutes.LocalRaidStart,
                ProfileId,
                "core-output",
                locks,
                state,
                CancellationToken.None).AsTask();

            Assert.False(startTask.IsCompleted);
            releaseHolder.TrySetResult();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("core-output", await startTask.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(RaidSessionPhase.RaidStarting, state.GetPhase(ProfileId));
            Assert.Throws<InvalidOperationException>(() => state.RequireLobby(ProfileId));
        }
        finally
        {
            releaseHolder.TrySetResult();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Transit_end_remains_blocked_while_a_normal_end_returns_to_lobby()
    {
        var state = new RaidSessionState();
        var locks = new ProfileLockPool();
        EnterRaid(state);
        state.BeginRaidEnd(ProfileId);

        var transitOutput = await RaidLifecycleAfterRouter.CompleteRaidEndAsync(
            ProfileId,
            EndRequest(ExitStatus.TRANSIT),
            "transit-output",
            locks,
            state);

        Assert.Equal("transit-output", transitOutput);
        Assert.Equal(RaidSessionPhase.Transit, state.GetPhase(ProfileId));
        Assert.Throws<InvalidOperationException>(() => state.RequireLobby(ProfileId));

        state.BeginRaidStart(ProfileId);
        state.CompleteRaidStart(ProfileId);
        state.BeginRaidEnd(ProfileId);
        await RaidLifecycleAfterRouter.CompleteRaidEndAsync(
            ProfileId,
            EndRequest(ExitStatus.SURVIVED),
            null,
            locks,
            state);

        Assert.Equal(RaidSessionPhase.Lobby, state.GetPhase(ProfileId));
        state.RequireLobby(ProfileId);
    }

    [Fact]
    public void Incomplete_lifecycle_transitions_and_invalid_game_start_fail_closed()
    {
        var state = new RaidSessionState();

        state.BeginGameStart(ProfileId);
        Assert.Throws<InvalidOperationException>(() => state.RequireLobby(ProfileId));
        state.CompleteGameStart(ProfileId, profileIsReady: false);
        Assert.Equal(RaidSessionPhase.Initializing, state.GetPhase(ProfileId));
        Assert.Throws<InvalidOperationException>(() => state.RequireLobby(ProfileId));

        Assert.Throws<InvalidOperationException>(() => state.BeginRaidEnd(ProfileId));
        Assert.Equal(RaidSessionPhase.Initializing, state.GetPhase(ProfileId));
        Assert.Throws<InvalidOperationException>(() => state.RequireLobby(ProfileId));

        state.BeginLogout(ProfileId);
        state.CompleteLogout(ProfileId);
        Assert.Equal(RaidSessionPhase.Unknown, state.GetPhase(ProfileId));
        Assert.Throws<InvalidOperationException>(() => state.RequireLobby(ProfileId));
    }

    [Fact]
    public void Stale_or_forged_completions_cannot_reopen_in_raid_or_transit_sessions()
    {
        var state = new RaidSessionState();
        EnterRaid(state);

        Assert.Throws<InvalidOperationException>(() => state.CompleteGameStart(ProfileId, profileIsReady: true));
        Assert.Throws<InvalidOperationException>(() => state.CompleteRaidEnd(ProfileId, isTransit: false));
        Assert.Equal(RaidSessionPhase.InRaid, state.GetPhase(ProfileId));
        Assert.Throws<InvalidOperationException>(() => state.RequireLobby(ProfileId));

        state.BeginRaidEnd(ProfileId);
        state.CompleteRaidEnd(ProfileId, isTransit: true);

        Assert.Throws<InvalidOperationException>(() => state.CompleteGameStart(ProfileId, profileIsReady: true));
        Assert.Throws<InvalidOperationException>(() => state.CompleteRaidStart(ProfileId));
        Assert.Throws<InvalidOperationException>(() => state.CompleteRaidEnd(ProfileId, isTransit: false));
        Assert.Equal(RaidSessionPhase.Transit, state.GetPhase(ProfileId));
        Assert.Throws<InvalidOperationException>(() => state.RequireLobby(ProfileId));
    }

    [Fact]
    public void Invalid_begin_transitions_fail_without_changing_the_current_phase()
    {
        var state = new RaidSessionState();

        Assert.Throws<InvalidOperationException>(() => state.BeginRaidStart(ProfileId));
        Assert.Equal(RaidSessionPhase.Unknown, state.GetPhase(ProfileId));

        EnterRaid(state);
        Assert.Throws<InvalidOperationException>(() => state.BeginGameStart(ProfileId));
        Assert.Throws<InvalidOperationException>(() => state.BeginRaidStart(ProfileId));
        Assert.Equal(RaidSessionPhase.InRaid, state.GetPhase(ProfileId));
    }

    [Fact]
    public async Task Concurrent_valid_start_and_forged_end_completions_serialize_without_reopening_lobby()
    {
        var state = new RaidSessionState();
        EnterLobby(state);
        state.BeginRaidStart(ProfileId);
        var locks = new ProfileLockPool();
        var (holder, releaseHolder) = await HoldProfileLockIndependentlyAsync(locks, ProfileId);
        try
        {
            var validStart = RaidLifecycleAfterRouter.CompleteRaidStartAsync(
                ProfileId,
                "start-output",
                locks,
                state).AsTask();
            var forgedEnd = RaidLifecycleAfterRouter.CompleteRaidEndAsync(
                ProfileId,
                EndRequest(ExitStatus.SURVIVED),
                "end-output",
                locks,
                state).AsTask();

            Assert.False(validStart.IsCompleted);
            Assert.False(forgedEnd.IsCompleted);
            releaseHolder.TrySetResult();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("start-output", await validStart.WaitAsync(TimeSpan.FromSeconds(1)));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                forgedEnd.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(RaidSessionPhase.InRaid, state.GetPhase(ProfileId));
            Assert.Throws<InvalidOperationException>(() => state.RequireLobby(ProfileId));
        }
        finally
        {
            releaseHolder.TrySetResult();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void Companion_routers_straddle_the_native_router_priority()
    {
        Assert.Equal(399999, RaidLifecycleBeforeRouter.Priority);
        Assert.Equal(400000, OnLoadOrder.Routers);
        Assert.Equal(400001, RaidLifecycleAfterRouter.Priority);
    }

    [Theory]
    [InlineData(RaidLifecycleRoutes.LocalRaidStart, "RaidStarting")]
    [InlineData(RaidLifecycleRoutes.LocalRaidEnd, "RaidEnding")]
    public async Task Native_route_failure_leaves_the_before_phase_closed_to_opening(string route, string expectedPhase)
    {
        var state = new RaidSessionState();
        var locks = new ProfileLockPool();
        if (route == RaidLifecycleRoutes.LocalRaidEnd)
        {
            EnterRaid(state);
        }
        else
        {
            EnterLobby(state);
        }

        await RaidLifecycleBeforeRouter.ApplyTransitionAsync(
            route,
            ProfileId,
            "before-output",
            locks,
            state,
            CancellationToken.None);

        Assert.Equal(expectedPhase, state.GetPhase(ProfileId).ToString());
        Assert.Throws<InvalidOperationException>(() => state.RequireLobby(ProfileId));
    }

    [Fact]
    public async Task Raid_start_before_transition_wins_a_queued_opening_and_the_service_rechecks_lobby_inside_the_lock()
    {
        var state = new RaidSessionState();
        var locks = new ProfileLockPool();
        EnterLobby(state);
        var serviceProbe = new ServiceProbe(locks, state);
        var (holder, releaseHolder) = await HoldProfileLockIndependentlyAsync(locks, ProfileId);
        try
        {
            var raidStart = RaidLifecycleBeforeRouter.ApplyTransitionAsync(
                RaidLifecycleRoutes.LocalRaidStart,
                ProfileId,
                "native-input",
                locks,
                state,
                CancellationToken.None).AsTask();
            var opening = serviceProbe.Service.OpenAsync(
                serviceProbe.Context,
                serviceProbe.CaseId,
                CancellationToken.None);

            Assert.False(raidStart.IsCompleted);
            Assert.False(opening.IsCompleted);
            releaseHolder.TrySetResult();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("native-input", await raidStart.WaitAsync(TimeSpan.FromSeconds(1)));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                opening.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(RaidSessionPhase.RaidStarting, state.GetPhase(ProfileId));
            Assert.Equal(0, serviceProbe.Journal.LoadCalls);
            Assert.Equal(0, serviceProbe.Preparation.Calls);
        }
        finally
        {
            releaseHolder.TrySetResult();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<(Task Holder, TaskCompletionSource Release)>
        HoldProfileLockIndependentlyAsync(ProfileLockPool locks, MongoId profileId)
    {
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task holder;
        using (ExecutionContext.SuppressFlow())
        {
            holder = Task.Run(async () =>
            {
                await using var profileLock = await locks.AcquireAsync(
                    profileId,
                    CancellationToken.None);
                acquired.SetResult();
                await release.Task;
            });
        }

        await acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return (holder, release);
    }

    private static OpenRandomLootContainerRequestData ValidOpeningRequest() => new()
    {
        Action = ModConstants.OpenAction,
        Item = "bbbbbbbbbbbbbbbbbbbbbbbb"
    };

    private static EndLocalRaidRequestData EndRequest(ExitStatus result) => new()
    {
        Results = new EndRaidResult { Result = result }
    };

    private static void EnterLobby(RaidSessionState state)
    {
        state.BeginGameStart(ProfileId);
        state.CompleteGameStart(ProfileId, profileIsReady: true);
    }

    private static void EnterRaid(RaidSessionState state)
    {
        EnterLobby(state);
        state.BeginRaidStart(ProfileId);
        state.CompleteRaidStart(ProfileId);
    }

    private sealed class ServiceProbe
    {
        public ServiceProbe(ProfileLockPool locks, RaidSessionState state)
        {
            CaseId = "bbbbbbbbbbbbbbbbbbbbbbbb";
            Context = new OpeningContext(null!, new ItemEventRouterResponse(), ProfileId);
            Journal = new ProbeJournal();
            Preparation = new ProbePreparation();
            Service = new CaseOpeningService(
                Journal,
                Preparation,
                new ProbeInventory(),
                new ProbeCommitter(),
                locks,
                state);
        }

        public MongoId CaseId { get; }
        public OpeningContext Context { get; }
        public ProbeJournal Journal { get; }
        public ProbePreparation Preparation { get; }
        public CaseOpeningService Service { get; }
    }

    private sealed class ProbeJournal : ICaseOpeningJournalStore
    {
        public int LoadCalls { get; private set; }

        public ValueTask<CaseOpeningJournal> LoadAsync(MongoId profileId, CancellationToken cancellationToken)
        {
            LoadCalls++;
            return ValueTask.FromResult(new CaseOpeningJournal());
        }

        public ValueTask SaveAsync(MongoId profileId, CaseOpeningJournal journal, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class ProbePreparation : IOpeningPreparation
    {
        public int Calls { get; private set; }

        public ValueTask<CaseOpeningRecord> PrepareAsync(OpeningContext context, MongoId caseId, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("The opening should be rejected before preparation.");
        }
    }

    private sealed class ProbeInventory : IOpeningInventory
    {
        public InventoryEvidence Inspect(OpeningContext context, MongoId caseId, CaseOpeningRecord? record) =>
            throw new InvalidOperationException("The opening should be rejected before inventory inspection.");

        public InventoryCheckpoint Capture(OpeningContext context) =>
            throw new InvalidOperationException("The opening should be rejected before inventory capture.");

        public CaseOpeningRecord ApplyPrepared(OpeningContext context, CaseOpeningRecord record) =>
            throw new InvalidOperationException("The opening should be rejected before inventory mutation.");

        public void Restore(OpeningContext context, InventoryCheckpoint checkpoint) =>
            throw new InvalidOperationException("The opening should be rejected before inventory restoration.");

        public void Replay(OpeningContext context, CaseOpeningRecord record) =>
            throw new InvalidOperationException("The opening should be rejected before replay.");
    }

    private sealed class ProbeCommitter : IProfileCommitter
    {
        public Task CommitAsync(MongoId profileId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The opening should be rejected before profile commit.");
    }
}
