using ContrabandCases.Client.Opening;
using ContrabandCases.Client.UI;
using ContrabandCases.Shared.Catalog;
using Xunit;

namespace ContrabandCases.Tests.Client;

public sealed class RouletteLifecycleTests
{
    [Theory]
    [InlineData(0d, 0d, 0, 1.5f, 0.5f)]
    [InlineData(-188d, 0.5d, 1, 1.175f, 0.38f)]
    [InlineData(-564d, 1d, 3, 0.85f, 0.26f)]
    [InlineData(-100000d, 2d, 29, 0.85f, 0.26f)]
    [InlineData(188d, -1d, 0, 1.5f, 0.5f)]
    public void Spin_audio_tracks_tile_crossings_and_clamps_timing(
        double stripX, double progress, int tick, float pitch, float volume)
    {
        var frame = RouletteAnimationMath.SpinAudio(stripX, progress, 188d, 29);
        Assert.Equal(tick, frame.TickIndex);
        Assert.Equal(pitch, frame.Pitch, 5);
        Assert.Equal(volume, frame.Volume, 5);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Spin_audio_rejects_non_finite_inputs(double invalid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RouletteAnimationMath.SpinAudio(invalid, 0d, 188d, 29));
        Assert.Throws<ArgumentOutOfRangeException>(() => RouletteAnimationMath.SpinAudio(0d, invalid, 188d, 29));
    }

    private static readonly string[] Actions =
    {
        "Open",
        "Recover",
        "Cancel",
        "Confirm",
        "Commit",
        "Complete",
        "Skip",
        "Fail",
        "Close"
    };

    [Fact]
    public void Phase_set_is_exact_and_a_new_machine_is_idle()
    {
        Assert.Equal(
            new[] { "Idle", "Confirming", "Pending", "Revealing", "Result", "Failed", "Disposed" },
            Enum.GetNames<OpeningPhase>());
        Assert.Equal(OpeningPhase.Idle, new OpeningPhaseMachine().Phase);
    }

    [Fact]
    public void A_second_open_is_rejected_without_replacing_the_active_attempt()
    {
        var machine = new OpeningPhaseMachine();

        Assert.True(machine.TryOpen(out var token));
        Assert.False(machine.TryOpen(out var rejectedToken));
        Assert.Equal(0, rejectedToken);
        Assert.Equal(token, machine.Generation);
        Assert.Equal(OpeningPhase.Confirming, machine.Phase);
    }

    [Fact]
    public void Restart_recovery_acquires_idle_gate_directly_in_pending_phase()
    {
        var machine = new OpeningPhaseMachine();

        Assert.True(machine.TryBeginRecovery(out var token));
        Assert.NotEqual(0, token);
        Assert.Equal(OpeningPhase.Pending, machine.Phase);
        Assert.False(machine.TryBeginRecovery(out var rejected));
        Assert.Equal(0, rejected);
        Assert.True(machine.TryResolvePending(token));
    }

    [Fact]
    public void Committed_relay_presentation_failure_can_resolve_pending_then_auto_secure()
    {
        var machine = new OpeningPhaseMachine();
        Assert.True(machine.TryOpen(out var token));
        Assert.True(machine.TryConfirm(token));
        Assert.True(machine.TryCommit(token));
        Assert.True(machine.TryComplete(token));
        Assert.True(machine.TryContinue(token));

        // The Relay receipt is committed, but presentation fails before the reveal starts.
        Assert.True(machine.TryResolvePending(token));
        Assert.Equal(OpeningPhase.Result, machine.Phase);

        // The safe fallback can submit Secure, and a synchronous enqueue failure can
        // return to Result without leaving the opening gate stuck.
        Assert.True(machine.TryContinue(token));
        Assert.True(machine.TryResolvePending(token));
        Assert.Equal(OpeningPhase.Result, machine.Phase);
    }

    [Fact]
    public void Committed_terminal_presentation_failure_can_resolve_and_close_from_pending()
    {
        var machine = new OpeningPhaseMachine();
        Assert.True(machine.TryOpen(out var token));
        Assert.True(machine.TryConfirm(token));
        Assert.True(machine.TryCommit(token));
        Assert.True(machine.TryComplete(token));
        Assert.True(machine.TryContinue(token));

        Assert.True(machine.TryResolvePending(token));
        Assert.True(machine.TryClose(token));
        Assert.Equal(OpeningPhase.Idle, machine.Phase);
    }

    [Fact]
    public void Confirmation_returns_dispatch_ownership_exactly_once()
    {
        var machine = new OpeningPhaseMachine();

        Assert.False(machine.TryConfirm(1));
        Assert.True(machine.TryOpen(out var token));
        Assert.False(machine.TryCommit(token));
        Assert.Equal(OpeningPhase.Confirming, machine.Phase);

        Assert.True(machine.TryConfirm(token));
        Assert.False(machine.TryConfirm(token));
        Assert.Equal(OpeningPhase.Pending, machine.Phase);
    }

    [Fact]
    public void Cancel_before_dispatch_releases_the_opening_gate()
    {
        var machine = new OpeningPhaseMachine();
        Assert.True(machine.TryOpen(out var cancelledToken));

        Assert.True(machine.TryCancel(cancelledToken));
        Assert.Equal(OpeningPhase.Idle, machine.Phase);
        Assert.True(machine.TryOpen(out var nextToken));
        Assert.NotEqual(cancelledToken, nextToken);
        Assert.False(machine.TryConfirm(cancelledToken));
        Assert.Equal(OpeningPhase.Confirming, machine.Phase);
    }

    [Fact]
    public void Pending_attempt_cannot_skip_at_any_elapsed_time()
    {
        var (machine, token) = CreateAt(OpeningPhase.Pending);

        Assert.False(machine.CanSkip(token, double.MaxValue));
        Assert.False(machine.TrySkip(token, double.MaxValue));
        Assert.Equal(OpeningPhase.Pending, machine.Phase);
    }

    [Fact]
    public void Reveal_skip_unlocks_at_the_exact_unscaled_time_threshold()
    {
        var (machine, token) = CreateAt(OpeningPhase.Revealing);

        Assert.False(machine.CanSkip(token, 1.199));
        Assert.False(machine.TrySkip(token, 1.199));
        Assert.Equal(OpeningPhase.Revealing, machine.Phase);
        Assert.True(machine.CanSkip(token, 1.2));
        Assert.True(machine.TrySkip(token, 1.2));
        Assert.Equal(OpeningPhase.Result, machine.Phase);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)]
    public void Reveal_skip_rejects_non_finite_and_negative_elapsed_time(double elapsed)
    {
        var (machine, token) = CreateAt(OpeningPhase.Revealing);

        Assert.False(machine.CanSkip(token, elapsed));
        Assert.False(machine.TrySkip(token, elapsed));
        Assert.Equal(OpeningPhase.Revealing, machine.Phase);
    }

    [Fact]
    public void Releasing_an_attempt_invalidates_its_generation_immediately()
    {
        var machine = new OpeningPhaseMachine();
        Assert.True(machine.TryOpen(out var cancelledToken));

        Assert.True(machine.TryCancel(cancelledToken));
        Assert.NotEqual(cancelledToken, machine.Generation);
        Assert.False(machine.TryConfirm(cancelledToken));

        Assert.True(machine.TryOpen(out var completedToken));
        Assert.True(machine.TryConfirm(completedToken));
        Assert.True(machine.TryCommit(completedToken));
        Assert.True(machine.TryComplete(completedToken));
        Assert.True(machine.TryClose(completedToken));
        Assert.NotEqual(completedToken, machine.Generation);
        Assert.False(machine.TryClose(completedToken));
    }

    [Theory]
    [InlineData(OpeningPhase.Confirming)]
    [InlineData(OpeningPhase.Pending)]
    [InlineData(OpeningPhase.Revealing)]
    public void Active_attempts_can_fail(OpeningPhase activePhase)
    {
        var (machine, token) = CreateAt(activePhase);

        Assert.True(machine.TryFail(token));
        Assert.Equal(OpeningPhase.Failed, machine.Phase);
    }

    [Theory]
    [InlineData(OpeningPhase.Result)]
    [InlineData(OpeningPhase.Failed)]
    public void Closing_a_terminal_panel_releases_the_opening_gate(OpeningPhase terminalPhase)
    {
        var (machine, token) = CreateAt(terminalPhase);

        Assert.True(machine.TryClose(token));
        Assert.Equal(OpeningPhase.Idle, machine.Phase);
        Assert.True(machine.TryOpen(out var nextToken));
        Assert.NotEqual(token, nextToken);
    }

    [Theory]
    [InlineData(OpeningPhase.Idle)]
    [InlineData(OpeningPhase.Confirming)]
    [InlineData(OpeningPhase.Pending)]
    [InlineData(OpeningPhase.Revealing)]
    [InlineData(OpeningPhase.Result)]
    [InlineData(OpeningPhase.Failed)]
    public void Dispose_is_terminal_from_every_non_disposed_phase(OpeningPhase phase)
    {
        var (machine, token) = CreateAt(phase);

        machine.Dispose();
        machine.Dispose();

        Assert.Equal(OpeningPhase.Disposed, machine.Phase);
        Assert.False(machine.TryOpen(out _));
        Assert.False(machine.TryBeginRecovery(out _));
        Assert.False(machine.TryCancel(token));
        Assert.False(machine.TryConfirm(token));
        Assert.False(machine.TryCommit(token));
        Assert.False(machine.TryComplete(token));
        Assert.False(machine.TrySkip(token, double.MaxValue));
        Assert.False(machine.TryFail(token));
        Assert.False(machine.TryClose(token));
    }

    [Theory]
    [MemberData(nameof(IllegalTransitions))]
    public void Illegal_transitions_are_rejected_without_state_mutation(OpeningPhase phase, string action)
    {
        var (machine, token) = CreateAt(phase);
        var originalPhase = machine.Phase;
        var originalGeneration = machine.Generation;

        var accepted = Invoke(machine, token, action);

        Assert.False(accepted);
        Assert.Equal(originalPhase, machine.Phase);
        Assert.Equal(originalGeneration, machine.Generation);
    }

    [Fact]
    public void Stale_callbacks_cannot_transition_a_later_attempt()
    {
        var machine = new OpeningPhaseMachine();
        Assert.True(machine.TryOpen(out var firstToken));
        Assert.True(machine.TryFail(firstToken));
        Assert.True(machine.TryClose(firstToken));
        Assert.True(machine.TryOpen(out var secondToken));
        Assert.True(machine.TryConfirm(secondToken));

        Assert.False(machine.TryCommit(firstToken));
        Assert.False(machine.TryFail(firstToken));
        Assert.Equal(OpeningPhase.Pending, machine.Phase);
        Assert.True(machine.TryCommit(secondToken));
    }

    [Theory]
    [InlineData(1200d, 176d, 12d, 29, -4940d)]
    [InlineData(800d, 100d, 0d, 0, 350d)]
    [InlineData(1000d, 200d, 20d, 2, -40d)]
    public void Landing_equation_centers_the_requested_tile(
        double viewportWidth,
        double tileWidth,
        double spacing,
        int landingIndex,
        double expected)
    {
        Assert.Equal(
            expected,
            RouletteAnimationMath.LandingX(viewportWidth, tileWidth, spacing, landingIndex),
            precision: 8);
    }

    [Theory]
    [InlineData(2560f, 1080f)]
    [InlineData(1920f, 1080f)]
    [InlineData(1280f, 1024f)]
    public void Overlay_scaling_uses_the_limiting_axis_to_keep_panels_inside_the_screen(
        float width,
        float height)
    {
        var match = RouletteOverlay.ResponsiveScreenMatch(width, height);

        Assert.InRange(match, 0f, 1f);
        if (width == 1920f && height == 1080f)
        {
            Assert.Equal(0.5f, match);
        }

        var widthScale = width / 1920d;
        var heightScale = height / 1080d;
        var canvasScale = Math.Pow(
            2d,
            Math.Log(widthScale, 2d) * (1d - match) + Math.Log(heightScale, 2d) * match);
        Assert.True((1380d + 48d) * canvasScale <= width + 0.001d);
        Assert.True((900d + 48d) * canvasScale <= height + 0.001d);
    }

    [Fact]
    public void Overlay_scaling_falls_back_safely_for_an_unavailable_screen_size()
    {
        Assert.Equal(0.5f, RouletteOverlay.ResponsiveScreenMatch(0f, 1080f));
        Assert.Equal(0.5f, RouletteOverlay.ResponsiveScreenMatch(float.NaN, 1080f));
        Assert.Equal(0.5f, RouletteOverlay.ResponsiveScreenMatch(1920f, float.PositiveInfinity));
    }

    [Fact]
    public void Normal_reveal_ease_is_monotonic_clamped_and_assigns_the_exact_endpoint()
    {
        var samples = Enumerable.Range(0, 101)
            .Select(index => RouletteAnimationMath.Position(0d, -4940d, index / 100d * 4.5d, 4.5d))
            .ToArray();

        Assert.Equal(0d, samples[0]);
        Assert.All(samples.Zip(samples.Skip(1)), pair => Assert.True(pair.First >= pair.Second));
        Assert.Equal(-4940d, samples[^1]);
        Assert.Equal(-4940d, RouletteAnimationMath.Position(0d, -4940d, 99d, 4.5d));
        Assert.Equal(0d, RouletteAnimationMath.EaseOutCubic(-1d));
        Assert.Equal(1d, RouletteAnimationMath.EaseOutCubic(2d));
    }

    [Fact]
    public void Spin_ease_matches_the_css_go_style_fast_blur_then_long_dramatic_crawl_shape()
    {
        // Most of the travel distance is already covered by the end of the acceleration ramp (30% of the
        // timeline) -- the fast, blurry burst through the bulk of the strip that CS:GO-style openings open
        // with, rather than a slow, even coast. (Widened from an earlier 18%/60% pairing that produced a
        // sharp velocity kink right at this boundary -- a player-reported "too much accel" -- see the
        // SpinAccelTimeShare/SpinAccelDistanceShare doc comments in RouletteAnimationMath.)
        var atRampEnd = RouletteAnimationMath.EaseInOutSpin(0.30d);
        Assert.InRange(atRampEnd, 0.40d, 0.50d);

        // By the halfway point in time, the majority of the distance is already covered -- meaning
        // whatever's left plays out over the remaining half of the animation's real-world duration. At
        // this project's 6.5s default, that's still several full seconds of slow, visible motion through
        // the last few tiles: the long dramatic crawl, expressed as remaining time rather than remaining
        // fractional distance.
        var atHalfway = RouletteAnimationMath.EaseInOutSpin(0.5d);
        Assert.InRange(atHalfway, 0.75d, 0.85d);

        // The ramp's exit speed and the following ease-out's entry speed are close to each other -- no
        // jarring kink at the transition (unlike the old 18%/60% pairing, where the ramp exited roughly
        // 4.6x faster than the ease-out began). Sampled as the secant slope either side of the boundary.
        const double delta = 0.001d;
        var rampExitSlope =
            (RouletteAnimationMath.EaseInOutSpin(0.30d) - RouletteAnimationMath.EaseInOutSpin(0.30d - delta)) / delta;
        var easeOutEntrySlope =
            (RouletteAnimationMath.EaseInOutSpin(0.30d + delta) - RouletteAnimationMath.EaseInOutSpin(0.30d)) / delta;
        Assert.InRange(rampExitSlope / easeOutEntrySlope, 1d, 2d);

        // Strictly monotonic across the full timeline -- the reel never reverses or stalls outright.
        double[] samples = [0d, 0.1d, 0.3d, 0.5d, 0.7d, 0.9d, 1d];
        for (var i = 1; i < samples.Length; i++)
        {
            Assert.True(
                RouletteAnimationMath.EaseInOutSpin(samples[i]) >= RouletteAnimationMath.EaseInOutSpin(samples[i - 1]));
        }
    }

    [Fact]
    public void Spin_duration_jitter_is_deterministic_per_seed_and_bounded_within_ten_percent()
    {
        const double baseDuration = 6.5d;

        var repeat = RouletteAnimationMath.SpinDurationSeconds(baseDuration, 417);
        Assert.Equal(repeat, RouletteAnimationMath.SpinDurationSeconds(baseDuration, 417));

        for (var seed = 0; seed < 200; seed++)
        {
            var duration = RouletteAnimationMath.SpinDurationSeconds(baseDuration, seed);
            Assert.InRange(duration, baseDuration * 0.9d, baseDuration * 1.1d);
        }

        // Different seeds should not all collapse to the exact same duration (i.e. it's actually varying),
        // and it shouldn't just track LandingJitterOffset's own draw for the same seed lockstep -- the two
        // are salted differently on purpose.
        var distinctDurations = Enumerable.Range(0, 20)
            .Select(seed => RouletteAnimationMath.SpinDurationSeconds(baseDuration, seed))
            .Distinct()
            .Count();
        Assert.True(distinctDurations > 1);
    }

    [Fact]
    public void Reveal_plan_is_deterministic_and_preserves_the_committed_winner_at_landing()
    {
        var pool = new[] { "alpha", "bravo", "charlie", "winner" };

        var first = RouletteRevealPlan.Create(
            pool,
            "winner",
            tileCount: 36,
            landingIndex: 29,
            seed: 417,
            viewportWidth: 1200d,
            tileWidth: 176d,
            tileSpacing: 12d,
            reducedMotion: false);
        var second = RouletteRevealPlan.Create(
            pool,
            "winner",
            tileCount: 36,
            landingIndex: 29,
            seed: 417,
            viewportWidth: 1200d,
            tileWidth: 176d,
            tileSpacing: 12d,
            reducedMotion: false);

        Assert.Equal(first.Strip, second.Strip);
        Assert.Equal("winner", first.CommittedWinnerId);
        Assert.Equal("winner", first.Strip[first.LandingIndex]);
        Assert.Equal(RouletteMotionMode.Scroll, first.MotionMode);

        // FinalX is dead-center (-4940d) plus a small deterministic per-seed jitter -- same seed always
        // produces the same offset (first == second), and it never drifts far enough to look like a
        // different tile landed.
        Assert.Equal(second.FinalX, first.FinalX);
        var expectedFinalX = -4940d + RouletteAnimationMath.LandingJitterOffset(176d, 417);
        Assert.Equal(expectedFinalX, first.FinalX, precision: 8);
        Assert.InRange(first.FinalX, -4940d - 176d * 0.25d, -4940d + 176d * 0.25d);
    }

    [Fact]
    public void Landing_jitter_is_deterministic_per_seed_and_bounded_well_within_a_tile()
    {
        const double tileWidth = 176d;

        var repeat = RouletteAnimationMath.LandingJitterOffset(tileWidth, 417);
        Assert.Equal(repeat, RouletteAnimationMath.LandingJitterOffset(tileWidth, 417));

        for (var seed = 0; seed < 200; seed++)
        {
            var offset = RouletteAnimationMath.LandingJitterOffset(tileWidth, seed);
            Assert.InRange(offset, -tileWidth * 0.25d, tileWidth * 0.25d);
        }

        // Different seeds should not all collapse to the exact same offset (i.e. it's actually varying).
        var distinctOffsets = Enumerable.Range(0, 20)
            .Select(seed => RouletteAnimationMath.LandingJitterOffset(tileWidth, seed))
            .Distinct()
            .Count();
        Assert.True(distinctOffsets > 1);
    }

    [Fact]
    public void Reveal_plan_accepts_near_miss_candidates_without_disturbing_the_committed_winner()
    {
        var pool = new[] { "alpha", "bravo", "winner" };
        var plan = RouletteRevealPlan.Create(
            pool,
            "winner",
            tileCount: 36,
            landingIndex: 29,
            seed: 417,
            viewportWidth: 1200d,
            tileWidth: 176d,
            tileSpacing: 12d,
            reducedMotion: false,
            nearMissCandidates: new[] { "alpha", "bravo" },
            nearMissChancePercent: 100);

        Assert.Equal("winner", plan.Strip[plan.LandingIndex]);
        Assert.All(plan.Strip, tile => Assert.Contains(tile, pool));
        Assert.Contains(plan.Strip[plan.LandingIndex - 1], new[] { "alpha", "bravo" });
        Assert.Contains(plan.Strip[plan.LandingIndex + 1], new[] { "alpha", "bravo" });
    }

    [Fact]
    public void Reduced_motion_uses_fade_without_changing_the_committed_winner()
    {
        var plan = RouletteRevealPlan.Create(
            new[] { "alpha", "winner" },
            "winner",
            tileCount: 36,
            landingIndex: 29,
            seed: 9,
            viewportWidth: 1200d,
            tileWidth: 176d,
            tileSpacing: 12d,
            reducedMotion: true);

        Assert.Equal(RouletteMotionMode.Fade, plan.MotionMode);
        Assert.False(plan.UsesScrolling);
        Assert.Equal("winner", plan.Strip[plan.LandingIndex]);
    }

    [Fact]
    public void Normal_completion_and_skip_share_the_same_committed_winner_and_exact_landing()
    {
        var plan = RouletteRevealPlan.Create(
            new[] { "alpha", "winner", "charlie" },
            "winner",
            tileCount: 36,
            landingIndex: 29,
            seed: 27,
            viewportWidth: 1200d,
            tileWidth: 176d,
            tileSpacing: 12d,
            reducedMotion: false);

        var normalEndpoint = RouletteAnimationMath.Position(0d, plan.FinalX, 4.5d, 4.5d);
        var skipEndpoint = plan.FinalX;

        Assert.Equal("winner", plan.Strip[plan.LandingIndex]);
        Assert.Equal(plan.FinalX, normalEndpoint);
        Assert.Equal(plan.FinalX, skipEndpoint);
    }

    [Fact]
    public void Dispatch_is_owned_only_after_confirmation_and_duplicate_confirmation_cannot_dispatch()
    {
        var machine = new OpeningPhaseMachine();
        var dispatches = 0;
        Assert.True(machine.TryOpen(out var token));
        Assert.Equal(0, dispatches);

        if (machine.TryConfirm(token))
        {
            Assert.Equal(OpeningPhase.Pending, machine.Phase);
            dispatches++;
        }

        if (machine.TryConfirm(token))
        {
            dispatches++;
        }

        Assert.Equal(1, dispatches);
    }

    [Theory]
    [InlineData(OpeningRunExit.ConfirmationCancel)]
    [InlineData(OpeningRunExit.Success)]
    [InlineData(OpeningRunExit.Failure)]
    [InlineData(OpeningRunExit.PresentationException)]
    public void Terminal_exit_cleans_every_visual_resource_and_gate_exactly_once(OpeningRunExit exit)
    {
        var calls = new List<string>();
        var cleanup = Cleanup(calls);

        cleanup.Exit(exit, operationPending: false);
        cleanup.Exit(exit, operationPending: false);

        Assert.Equal(
            new[] { "blocker", "listeners", "tiles", "visuals", "selection", "root", "gate" },
            calls);
        Assert.True(cleanup.PresentationDetached);
        Assert.True(cleanup.GateReleased);
    }

    [Theory]
    [InlineData(OpeningRunExit.SceneTeardown)]
    [InlineData(OpeningRunExit.Shutdown)]
    public void Pending_scene_or_shutdown_cleanup_detaches_visuals_but_preserves_the_operation_gate(
        OpeningRunExit exit)
    {
        var calls = new List<string>();
        var cleanup = Cleanup(calls);

        cleanup.Exit(exit, operationPending: true);
        cleanup.Exit(exit, operationPending: true);

        Assert.Equal(
            new[] { "blocker", "listeners", "tiles", "visuals", "selection", "root" },
            calls);
        Assert.True(cleanup.PresentationDetached);
        Assert.False(cleanup.GateReleased);

        cleanup.ReleaseGate();
        cleanup.ReleaseGate();

        Assert.Equal("gate", calls[^1]);
        Assert.Equal(1, calls.Count(call => call == "gate"));
    }

    [Theory]
    [InlineData(OpeningRunExit.SceneTeardown)]
    [InlineData(OpeningRunExit.Shutdown)]
    public void Non_pending_scene_or_shutdown_exit_releases_every_resource_once(OpeningRunExit exit)
    {
        var calls = new List<string>();
        var cleanup = Cleanup(calls);

        cleanup.Exit(exit, operationPending: false);
        cleanup.Exit(exit, operationPending: false);

        Assert.Equal(
            new[] { "blocker", "listeners", "tiles", "visuals", "selection", "root", "gate" },
            calls);
    }

    [Fact]
    public void Cleanup_continues_after_a_presentation_step_throws_and_reports_the_exception_once()
    {
        var calls = new List<string>();
        var errors = new List<Exception>();
        var cleanup = new OpeningRunCleanup(
            () => calls.Add("blocker"),
            () => throw new InvalidOperationException("listener cleanup failed"),
            () => calls.Add("tiles"),
            () => calls.Add("visuals"),
            () => calls.Add("selection"),
            () => calls.Add("root"),
            () => calls.Add("gate"),
            errors.Add);

        cleanup.Exit(OpeningRunExit.PresentationException, operationPending: false);
        cleanup.Exit(OpeningRunExit.PresentationException, operationPending: false);

        Assert.Equal(new[] { "blocker", "tiles", "visuals", "selection", "root", "gate" }, calls);
        Assert.Single(errors);
        Assert.Equal("listener cleanup failed", errors[0].Message);
    }

    [Fact]
    public void Missing_event_system_is_fail_closed_before_confirmation_or_dispatch()
    {
        var machine = new OpeningPhaseMachine();
        var dispatches = 0;
        Assert.True(machine.TryOpen(out var token));

        if (!OpeningPresentationGuard.CanActivate(disposed: false, eventSystemAvailable: false))
        {
            Assert.True(machine.TryCancel(token));
        }
        else if (machine.TryConfirm(token))
        {
            dispatches++;
        }

        Assert.Equal(0, dispatches);
        Assert.Equal(OpeningPhase.Idle, machine.Phase);
    }

    [Theory]
    [InlineData(OpeningPhase.Confirming, false, OpeningCancelIntent.CancelConfirmation)]
    [InlineData(OpeningPhase.Pending, false, OpeningCancelIntent.Ignore)]
    [InlineData(OpeningPhase.Pending, true, OpeningCancelIntent.Ignore)]
    [InlineData(OpeningPhase.Revealing, false, OpeningCancelIntent.Ignore)]
    [InlineData(OpeningPhase.Revealing, true, OpeningCancelIntent.SkipReveal)]
    [InlineData(OpeningPhase.Result, false, OpeningCancelIntent.CloseTerminal)]
    [InlineData(OpeningPhase.Failed, false, OpeningCancelIntent.CloseTerminal)]
    [InlineData(OpeningPhase.Disposed, true, OpeningCancelIntent.Ignore)]
    public void Cancel_policy_is_phase_aware(
        OpeningPhase phase,
        bool skipEligible,
        OpeningCancelIntent expected)
    {
        Assert.Equal(expected, OpeningCancelPolicy.Resolve(phase, skipEligible));
    }

    [Fact]
    public void Skip_focus_is_requested_on_the_eligibility_edge_even_when_an_inactive_button_is_selected()
    {
        Assert.True(OpeningPresentationGuard.ShouldFocusSkip(wasEnabled: false, isEnabled: true));
        Assert.False(OpeningPresentationGuard.ShouldFocusSkip(wasEnabled: true, isEnabled: true));
        Assert.False(OpeningPresentationGuard.ShouldFocusSkip(wasEnabled: false, isEnabled: false));
    }

    [Fact]
    public void Catalog_terminal_logging_hides_identifiers_and_paths_unless_debug_is_enabled()
    {
        const string rewardId = "secret-reward-id";
        const string catalogPath = "C:\\private\\rewards.json";
        var validation = new RewardCatalogValidationException($"Preset '{rewardId}' is invalid.");
        var missingFile = new FileNotFoundException("Catalog missing.", catalogPath);

        var publicValidation = CatalogReadinessDiagnostics.PublicTerminalMessage(validation);
        var publicMissingFile = CatalogReadinessDiagnostics.PublicTerminalMessage(missingFile);

        Assert.DoesNotContain(rewardId, publicValidation, StringComparison.Ordinal);
        Assert.DoesNotContain(catalogPath, publicMissingFile, StringComparison.Ordinal);
        Assert.Null(CatalogReadinessDiagnostics.DebugDetails(validation, debugLogging: false));
        Assert.Contains(
            rewardId,
            CatalogReadinessDiagnostics.DebugDetails(validation, debugLogging: true),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    public void Inventory_screen_close_detaches_only_a_live_active_presentation(
        bool disposed,
        bool hasActiveRun,
        bool presentationDetached,
        bool expected)
    {
        Assert.Equal(
            expected,
            OpeningScreenTeardownPolicy.ShouldDetach(
                disposed,
                hasActiveRun,
                presentationDetached));
    }

    [Fact]
    public void Unavailable_deferred_value_is_normalized_before_cache_and_ui_binding()
    {
        var cleanup = Cleanup(new List<string>());
        var generation = cleanup.PresentationGeneration;
        var fakeNull = new object();
        object? cached = fakeNull;
        object? bound = fakeNull;

        Assert.True(DeferredPresentationBinding.TryApply(
            cleanup,
            generation,
            fakeNull,
            value => ReferenceEquals(value, fakeNull),
            value => cached = value,
            value => bound = value));

        Assert.Null(cached);
        Assert.Null(bound);
    }

    [Fact]
    public void Late_sprite_completion_cannot_cache_or_bind_after_generation_change_or_disposal()
    {
        var cleanup = Cleanup(new List<string>());
        var generation = cleanup.PresentationGeneration;
        var cacheWrites = 0;
        var uiBinds = 0;

        Assert.True(cleanup.CanBind(generation));
        cleanup.DetachPresentation();
        Assert.False(cleanup.CanBind(generation));
        Assert.False(cleanup.CanBind(cleanup.PresentationGeneration));
        Assert.False(DeferredPresentationBinding.TryApply(
            cleanup,
            generation,
            "late sprite",
            _ => cacheWrites++,
            _ => uiBinds++));
        Assert.Equal(0, cacheWrites);
        Assert.Equal(0, uiBinds);

        var disposed = Cleanup(new List<string>());
        var disposedGeneration = disposed.PresentationGeneration;
        disposed.Exit(OpeningRunExit.Shutdown, operationPending: true);
        Assert.False(disposed.CanBind(disposedGeneration));
    }

    [Fact]
    public void Deferred_binding_rechecks_generation_between_cache_and_ui_bind()
    {
        var cleanup = Cleanup(new List<string>());
        var generation = cleanup.PresentationGeneration;
        var cacheWrites = 0;
        var uiBinds = 0;

        Assert.False(DeferredPresentationBinding.TryApply(
            cleanup,
            generation,
            "sprite",
            _ =>
            {
                cacheWrites++;
                cleanup.DetachPresentation();
            },
            _ => uiBinds++));

        Assert.Equal(1, cacheWrites);
        Assert.Equal(0, uiBinds);
    }

    [Fact]
    public void Sprite_reveal_waits_for_all_visible_loads_then_prepares_the_first_frame_in_order()
    {
        var pending = new TaskCompletionSource<object?>();
        var tasks = new Task[] { Task.CompletedTask, pending.Task };
        var calls = new List<string>();

        Assert.False(SpriteRevealReadiness.TryPrepareFirstFrame(
            tasks,
            elapsedSeconds: 0d,
            () => calls.Add("cache"),
            () => calls.Add("show"),
            () => calls.Add("bind")));
        Assert.Empty(calls);

        pending.SetResult(null);

        Assert.True(SpriteRevealReadiness.TryPrepareFirstFrame(
            tasks,
            elapsedSeconds: 0d,
            () => calls.Add("cache"),
            () => calls.Add("show"),
            () => calls.Add("bind")));
        Assert.Equal(new[] { "cache", "show", "bind" }, calls);
    }

    [Fact]
    public void Sprite_reveal_timeout_prevents_a_hung_load_from_freezing_a_committed_settlement()
    {
        var hung = new TaskCompletionSource<object?>();
        var calls = new List<string>();

        Assert.False(SpriteRevealReadiness.TryPrepareFirstFrame(
            new Task[] { hung.Task },
            SpriteRevealReadiness.MaximumWaitSeconds - 0.001d,
            () => calls.Add("cache"),
            () => calls.Add("show"),
            () => calls.Add("bind")));
        Assert.True(SpriteRevealReadiness.TryPrepareFirstFrame(
            new Task[] { hung.Task },
            SpriteRevealReadiness.MaximumWaitSeconds,
            () => calls.Add("cache"),
            () => calls.Add("show"),
            () => calls.Add("bind")));
        Assert.Equal(new[] { "cache", "show", "bind" }, calls);
    }

    [Fact]
    public void Sprite_task_cache_retries_faulted_cancelled_or_timed_out_tasks()
    {
        var pending = new TaskCompletionSource<object?>();
        var cancelled = Task.FromCanceled(new CancellationToken(canceled: true));
        var faulted = Task.FromException(new InvalidOperationException("sprite failed"));

        Assert.False(SpriteRevealReadiness.ShouldEvict(Task.CompletedTask));
        Assert.False(SpriteRevealReadiness.ShouldEvict(pending.Task));
        Assert.True(SpriteRevealReadiness.ShouldEvict(
            pending.Task,
            readinessTimedOut: true));
        Assert.True(SpriteRevealReadiness.ShouldEvict(cancelled));
        Assert.True(SpriteRevealReadiness.ShouldEvict(faulted));
    }

    [Fact]
    public void Timed_out_sprite_failure_cannot_evict_a_newer_successful_generation()
    {
        const string rewardId = "reward";
        var taskCache = new Dictionary<string, Task<object>>(StringComparer.Ordinal);
        var spriteCache = new Dictionary<string, object>(StringComparer.Ordinal);
        var oldGeneration = new TaskCompletionSource<object>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newGeneration = new TaskCompletionSource<object>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newSprite = new object();
        taskCache.Add(rewardId, oldGeneration.Task);

        Assert.True(SpriteTaskCacheOwnership.TryEvict(
            taskCache,
            spriteCache,
            rewardId,
            oldGeneration.Task));
        taskCache.Add(rewardId, newGeneration.Task);
        newGeneration.SetResult(newSprite);
        Assert.True(SpriteTaskCacheOwnership.TryStore(
            taskCache,
            spriteCache,
            rewardId,
            newGeneration.Task,
            newSprite));

        oldGeneration.SetException(new InvalidOperationException("old sprite failed"));
        Assert.NotNull(oldGeneration.Task.Exception);
        Assert.False(SpriteTaskCacheOwnership.TryEvict(
            taskCache,
            spriteCache,
            rewardId,
            oldGeneration.Task));

        Assert.Same(newGeneration.Task, taskCache[rewardId]);
        Assert.Same(newSprite, spriteCache[rewardId]);
    }

    [Fact]
    public void Timed_out_sprite_success_rehabilitates_only_when_no_newer_generation_owns_the_cache()
    {
        const string rewardId = "reward";
        var taskCache = new Dictionary<string, Task<object>>(StringComparer.Ordinal);
        var spriteCache = new Dictionary<string, object>(StringComparer.Ordinal);
        var oldSprite = new object();
        var oldGeneration = Task.FromResult(oldSprite);
        var newGeneration = new TaskCompletionSource<object>(
            TaskCreationOptions.RunContinuationsAsynchronously).Task;

        Assert.True(SpriteTaskCacheOwnership.TryStore(
            taskCache,
            spriteCache,
            rewardId,
            oldGeneration,
            oldSprite));
        Assert.Same(oldGeneration, taskCache[rewardId]);
        Assert.Same(oldSprite, spriteCache[rewardId]);

        Assert.True(SpriteTaskCacheOwnership.TryEvict(
            taskCache,
            spriteCache,
            rewardId,
            oldGeneration));
        taskCache.Add(rewardId, newGeneration);
        Assert.False(SpriteTaskCacheOwnership.TryStore(
            taskCache,
            spriteCache,
            rewardId,
            oldGeneration,
            oldSprite));
        Assert.Same(newGeneration, taskCache[rewardId]);
        Assert.False(spriteCache.ContainsKey(rewardId));
    }

    [Fact]
    public void Sprite_reveal_without_loadable_tasks_prepares_immediately()
    {
        var calls = new List<string>();

        Assert.True(SpriteRevealReadiness.TryPrepareFirstFrame(
            Array.Empty<Task>(),
            elapsedSeconds: 0d,
            () => calls.Add("cache"),
            () => calls.Add("show"),
            () => calls.Add("bind")));
        Assert.Equal(new[] { "cache", "show", "bind" }, calls);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.001d)]
    public void Sprite_reveal_rejects_invalid_elapsed_time(double elapsedSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SpriteRevealReadiness.TryPrepareFirstFrame(
                Array.Empty<Task>(),
                elapsedSeconds,
                () => { },
                () => { },
                () => { }));
    }

    [Fact]
    public void Sprite_reveal_readiness_rejects_missing_dependencies_and_null_task_entries()
    {
        var tasks = Array.Empty<Task>();
        Action callback = () => { };

        Assert.Throws<ArgumentNullException>(() =>
            SpriteRevealReadiness.TryPrepareFirstFrame(
                null!, 0d, callback, callback, callback));
        Assert.Throws<ArgumentNullException>(() =>
            SpriteRevealReadiness.TryPrepareFirstFrame(
                tasks, 0d, null!, callback, callback));
        Assert.Throws<ArgumentNullException>(() =>
            SpriteRevealReadiness.TryPrepareFirstFrame(
                tasks, 0d, callback, null!, callback));
        Assert.Throws<ArgumentNullException>(() =>
            SpriteRevealReadiness.TryPrepareFirstFrame(
                tasks, 0d, callback, callback, null!));
        Assert.Throws<ArgumentException>(() =>
            SpriteRevealReadiness.TryPrepareFirstFrame(
                new Task[] { null! }, 0d, callback, callback, callback));
        Assert.Throws<ArgumentNullException>(() =>
            SpriteRevealReadiness.ShouldEvict(null!));
    }

    [Fact]
    public void Commitment_resets_reveal_elapsed_so_pending_time_never_unlocks_skip()
    {
        var clock = new RouletteRevealClock();
        clock.AdvancePending(30d);

        clock.BeginCommittedReveal();

        Assert.Equal(0d, clock.RevealElapsedSeconds);
        Assert.False(clock.CanSkip);
        clock.AdvanceReveal(1.199d);
        Assert.False(clock.CanSkip);
        clock.AdvanceReveal(0.001d);
        Assert.True(clock.CanSkip);
    }

    [Theory]
    [InlineData(false, true, true, true, OverlayActivationDecision.Reuse)]
    [InlineData(false, true, false, false, OverlayActivationDecision.Rebuild)]
    [InlineData(false, true, true, false, OverlayActivationDecision.Rebuild)]
    [InlineData(false, false, false, false, OverlayActivationDecision.Reject)]
    [InlineData(true, true, false, false, OverlayActivationDecision.Reject)]
    public void Overlay_activation_rebuilds_any_incomplete_Unity_tree_before_a_run(
        bool disposed,
        bool eventSystemAvailable,
        bool rootAvailable,
        bool essentialSubtreeAvailable,
        OverlayActivationDecision expected)
    {
        Assert.Equal(
            expected,
            OverlayTreeLifecycle.DecideActivation(
                disposed,
                eventSystemAvailable,
                rootAvailable,
                essentialSubtreeAvailable));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void Active_presentation_requires_both_its_root_and_essential_subtree(
        bool rootAvailable,
        bool essentialSubtreeAvailable,
        bool expected)
    {
        Assert.Equal(
            expected,
            OverlayTreeLifecycle.IsPresentationAvailable(
                rootAvailable,
                essentialSubtreeAvailable));
    }

    [Theory]
    [InlineData(true, false, false, false, true, true)]
    [InlineData(false, false, false, false, true, false)]
    [InlineData(true, true, false, false, true, false)]
    [InlineData(true, false, true, false, true, false)]
    [InlineData(true, false, false, true, true, false)]
    [InlineData(true, false, false, false, false, false)]
    public void Cosmetic_self_test_is_available_only_behind_testing_mode_and_outside_real_runs(
        bool testingMode,
        bool disposed,
        bool openingActive,
        bool selfTestActive,
        bool catalogReady,
        bool expected)
    {
        Assert.Equal(
            expected,
            CosmeticSelfTestPolicy.CanStart(
                testingMode,
                disposed,
                openingActive,
                selfTestActive,
                catalogReady));
    }

    [Fact]
    public void Cosmetic_self_test_trigger_ignores_a_false_request_without_callbacks()
    {
        var consumeCalls = 0;
        var launchCalls = 0;
        var request = new CosmeticSelfTestRequest(
            TestingMode: true,
            CosmeticOutcomeSelection.RelayUpgradePreview,
            DurationSeconds: 1.5d);

        var result = CosmeticSelfTestPolicy.ProcessTrigger(
            requested: false,
            () => consumeCalls++,
            request,
            _ =>
            {
                launchCalls++;
                return new CosmeticSelfTestLaunchResult(true, "started");
            });

        Assert.Null(result);
        Assert.Equal(0, consumeCalls);
        Assert.Equal(0, launchCalls);
    }

    [Fact]
    public void Cosmetic_self_test_trigger_consumes_before_launch_and_forwards_exact_values()
    {
        var calls = new List<string>();
        var request = new CosmeticSelfTestRequest(
            TestingMode: true,
            CosmeticOutcomeSelection.RelaySidegradePreview,
            DurationSeconds: 2.75d);
        var forwarded = default(CosmeticSelfTestRequest);
        var expected = new CosmeticSelfTestLaunchResult(true, "preview started");

        var result = CosmeticSelfTestPolicy.ProcessTrigger(
            requested: true,
            () => calls.Add("consume"),
            request,
            actual =>
            {
                calls.Add("launch");
                forwarded = actual;
                return expected;
            });

        Assert.Equal(new[] { "consume", "launch" }, calls);
        Assert.Equal(request, forwarded);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Cosmetic_self_test_trigger_without_a_launcher_is_consumed_and_rejected_as_not_ready()
    {
        var consumeCalls = 0;
        var request = new CosmeticSelfTestRequest(
            TestingMode: true,
            CosmeticOutcomeSelection.CycleRewards,
            DurationSeconds: 1d);

        var result = CosmeticSelfTestPolicy.ProcessTrigger(
            requested: true,
            () => consumeCalls++,
            request,
            launch: null);

        Assert.Equal(1, consumeCalls);
        Assert.Equal(
            new CosmeticSelfTestLaunchResult(
                false,
                "Contraband Cases is not ready for a cosmetic self-test."),
            result);
    }

    [Fact]
    public void Cosmetic_self_test_trigger_preserves_a_launcher_rejection_message()
    {
        var consumeCalls = 0;
        var rejection = new CosmeticSelfTestLaunchResult(
            false,
            "Close the active BR-12 opening before running the cosmetic self-test.");

        var result = CosmeticSelfTestPolicy.ProcessTrigger(
            requested: true,
            () => consumeCalls++,
            new CosmeticSelfTestRequest(
                TestingMode: true,
                CosmeticOutcomeSelection.BlackLabel,
                DurationSeconds: 0.5d),
            _ => rejection);

        Assert.Equal(1, consumeCalls);
        Assert.Equal(rejection, result);
    }

    [Fact]
    public void Cosmetic_self_test_trigger_propagates_launcher_exceptions_after_consuming()
    {
        var consumeCalls = 0;
        var expected = new InvalidOperationException("launcher failed");

        var actual = Assert.Throws<InvalidOperationException>(() =>
            CosmeticSelfTestPolicy.ProcessTrigger(
                requested: true,
                () => consumeCalls++,
                new CosmeticSelfTestRequest(
                    TestingMode: true,
                    CosmeticOutcomeSelection.RelayConfiscationPreview,
                    DurationSeconds: 3d),
                _ => throw expected));

        Assert.Same(expected, actual);
        Assert.Equal(1, consumeCalls);
    }

    [Theory]
    [InlineData(0.01, 0.25)]
    [InlineData(0.25, 0.25)]
    [InlineData(1.1, 1.1)]
    [InlineData(3.0, 3.0)]
    [InlineData(20.0, 3.0)]
    public void Cosmetic_self_test_duration_is_short_and_bounded(double requested, double expected)
    {
        Assert.Equal(expected, CosmeticSelfTestPolicy.NormalizeDuration(requested), precision: 8);
    }

    [Theory]
    [InlineData(CosmeticOutcomeSelection.ScavGrade, RewardRarity.ScavGrade)]
    [InlineData(CosmeticOutcomeSelection.Uncommon, RewardRarity.Uncommon)]
    [InlineData(CosmeticOutcomeSelection.Contractor, RewardRarity.Contractor)]
    [InlineData(CosmeticOutcomeSelection.Restricted, RewardRarity.Restricted)]
    [InlineData(CosmeticOutcomeSelection.BlackLabel, RewardRarity.BlackLabel)]
    public void Cosmetic_outcome_selection_chooses_only_a_matching_catalog_preview(
        CosmeticOutcomeSelection selection,
        RewardRarity expectedRarity)
    {
        var rewards = Enum.GetValues<RewardRarity>()
            .Select(rarity => CosmeticReward(rarity.ToString(), rarity))
            .ToArray();

        var selected = CosmeticSelfTestPolicy.SelectReward(rewards, selection, cycleIndex: 0);

        Assert.Equal(expectedRarity, selected.Rarity);
    }

    public static IEnumerable<object[]> IllegalTransitions()
    {
        foreach (var phase in Enum.GetValues<OpeningPhase>())
        {
            foreach (var action in Actions)
            {
                if (!IsAllowed(phase, action))
                {
                    yield return new object[] { phase, action };
                }
            }
        }
    }

    private static bool IsAllowed(OpeningPhase phase, string action) => action switch
    {
        "Open" => phase == OpeningPhase.Idle,
        "Recover" => phase == OpeningPhase.Idle,
        "Cancel" or "Confirm" => phase == OpeningPhase.Confirming,
        "Commit" => phase == OpeningPhase.Pending,
        "Complete" or "Skip" => phase == OpeningPhase.Revealing,
        "Fail" => phase is OpeningPhase.Confirming or OpeningPhase.Pending or OpeningPhase.Revealing,
        "Close" => phase is OpeningPhase.Result or OpeningPhase.Failed,
        _ => false
    };

    private static bool Invoke(OpeningPhaseMachine machine, long token, string action) => action switch
    {
        "Open" => machine.TryOpen(out _),
        "Recover" => machine.TryBeginRecovery(out _),
        "Cancel" => machine.TryCancel(token),
        "Confirm" => machine.TryConfirm(token),
        "Commit" => machine.TryCommit(token),
        "Complete" => machine.TryComplete(token),
        "Skip" => machine.TrySkip(token, OpeningPhaseMachine.SkipDelaySeconds),
        "Fail" => machine.TryFail(token),
        "Close" => machine.TryClose(token),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
    };

    private static (OpeningPhaseMachine Machine, long Token) CreateAt(OpeningPhase phase)
    {
        var machine = new OpeningPhaseMachine();
        if (phase == OpeningPhase.Idle)
        {
            return (machine, 0);
        }

        Assert.True(machine.TryOpen(out var token));
        switch (phase)
        {
            case OpeningPhase.Confirming:
                break;
            case OpeningPhase.Pending:
                Assert.True(machine.TryConfirm(token));
                break;
            case OpeningPhase.Revealing:
                Assert.True(machine.TryConfirm(token));
                Assert.True(machine.TryCommit(token));
                break;
            case OpeningPhase.Result:
                Assert.True(machine.TryConfirm(token));
                Assert.True(machine.TryCommit(token));
                Assert.True(machine.TryComplete(token));
                break;
            case OpeningPhase.Failed:
                Assert.True(machine.TryFail(token));
                break;
            case OpeningPhase.Disposed:
                machine.Dispose();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(phase), phase, null);
        }

        return (machine, token);
    }

    private static OpeningRunCleanup Cleanup(List<string> calls) => new(
        () => calls.Add("blocker"),
        () => calls.Add("listeners"),
        () => calls.Add("tiles"),
        () => calls.Add("visuals"),
        () => calls.Add("selection"),
        () => calls.Add("root"),
        () => calls.Add("gate"),
        _ => { });

    private static ValidatedReward CosmeticReward(string id, RewardRarity rarity)
    {
        var templateId = $"template-{id}";
        var catalog = RewardCatalog.Create([
            new RewardDefinition(
                id,
                $"{id} display",
                templateId,
                $"preset-{id}",
                rarity,
                1d)
        ]);
        return Assert.Single(catalog.Validate(new CosmeticRewardResolver(templateId)));
    }

    private sealed class CosmeticRewardResolver(string templateId) : IRewardPresetResolver
    {
        public RewardPresetTree? Resolve(string presetId) => new([
            new RewardPresetItem("root", templateId, parentId: null)
        ]);
    }
}
