using ContrabandCases.Shared;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ContrabandCases.Tests.Client;

public sealed class ManifestCoordinatorRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_fresh_preflight_retry_reads_again_before_dispatching_one_validated_opening(bool throwsSynchronously)
    {
        using var flow = new ManifestCoordinatorHarness();
        flow.Responses.Enqueue(() => throwsSynchronously
            ? throw new IOException("offline")
            : Task.FromException<string>(new IOException("offline")));
        flow.Confirm();
        flow.TickUntil(() => flow.Retry is not null);
        Assert.Empty(flow.Operations);
        var retry = flow.Retry!;
        var response = new TaskCompletionSource<string>();
        flow.Responses.Enqueue(() => response.Task);

        retry();

        Assert.Equal(2, flow.Reads.Count);
        Assert.Empty(flow.Operations);
        retry();
        Assert.Equal(2, flow.Reads.Count);
        response.SetResult(ManifestCoordinatorHarness.CurrentResponse());
        flow.TickUntil(() => flow.Operations.Count == 1);
        var operation = JObject.FromObject(Assert.Single(flow.Operations));
        Assert.Equal(ModConstants.OpenAction, (string?)operation["Action"]);
        Assert.Equal("222222222222222222222222", (string?)operation["item"]);
        Assert.All(flow.Reads, read => Assert.Equal(ModConstants.ManifestCurrentRoute, read.Route));
    }

    [Fact]
    public void Failed_preflight_can_close_without_spending_or_allowing_a_stale_retry()
    {
        using var flow = new ManifestCoordinatorHarness();
        flow.Responses.Enqueue(() => Task.FromException<string>(new IOException("offline")));
        flow.Confirm();
        flow.TickUntil(() => flow.Retry is not null);
        var retry = flow.Retry!;
        Assert.NotNull(flow.Close);

        flow.Close!();
        retry();

        Assert.False(flow.Busy);
        Assert.Single(flow.Reads);
        Assert.Empty(flow.Operations);
    }

    [Fact]
    public void Prepared_recovery_close_preserves_the_saved_action_and_disables_stale_resume()
    {
        using var flow = new ManifestCoordinatorHarness();
        flow.ShowPrepared();
        var resume = flow.Retry!;
        Assert.NotNull(flow.Close);

        flow.Close!();
        resume();

        Assert.False(flow.Busy);
        Assert.Empty(flow.Reads);
        Assert.Empty(flow.Operations);
    }

    [Fact]
    public void Closing_during_result_check_keeps_observation_until_late_saved_result_without_resending()
    {
        using var flow = new ManifestCoordinatorHarness();
        flow.Responses.Enqueue(() => Task.FromResult(ManifestCoordinatorHarness.CurrentResponse()));
        flow.Confirm();
        flow.TickUntil(() => flow.Operations.Count == 1);
        flow.Responses.Enqueue(() => Task.FromException<string>(new IOException("offline after commit")));
        flow.CompleteOperation();
        flow.TickUntil(() => flow.Retry is not null);
        var retry = flow.Retry!;
        Assert.NotNull(flow.Close);
        var close = flow.Close!;
        var savedResult = new TaskCompletionSource<string>();
        flow.Responses.Enqueue(() => savedResult.Task);

        retry();
        retry();
        Assert.Equal(3, flow.Reads.Count);
        close();

        Assert.True(flow.Detached);
        Assert.True(flow.Busy);
        Assert.True(flow.Observers > 0);
        retry();
        Assert.Equal(3, flow.Reads.Count);
        savedResult.SetResult(ManifestCoordinatorHarness.CurrentResponse("TerminalSnapshot"));
        flow.TickUntil(() => !flow.Busy);
        Assert.Single(flow.Operations);
        Assert.Equal(0, flow.Observers);
    }

    [Fact]
    public void Closing_while_prepared_resume_callback_is_pending_does_not_forget_or_repeat_it()
    {
        using var flow = new ManifestCoordinatorHarness();
        flow.ShowPrepared();
        var resume = flow.Retry!;
        Assert.NotNull(flow.Close);
        var close = flow.Close!;

        resume();
        resume();
        Assert.Single(flow.Operations);
        close();
        Assert.True(flow.Busy);
        Assert.True(flow.Detached);
        resume();
        Assert.Single(flow.Operations);
        flow.Responses.Enqueue(() => Task.FromResult(ManifestCoordinatorHarness.CurrentResponse("TerminalSnapshot")));
        flow.CompleteOperation();
        flow.TickUntil(() => !flow.Busy);
        Assert.Single(flow.Operations);
        Assert.Single(flow.Reads);
    }

    [Fact]
    public void Retry_rechecks_the_catalog_and_never_dispatches_against_changed_odds()
    {
        using var flow = new ManifestCoordinatorHarness();
        flow.Responses.Enqueue(() => Task.FromException<string>(new IOException("offline")));
        flow.Confirm();
        flow.TickUntil(() => flow.Retry is not null);
        flow.Responses.Enqueue(() => Task.FromResult(ManifestCoordinatorHarness.CurrentResponse(catalogId: new string('d', 64))));

        flow.Retry!();
        flow.TickUntil(() => flow.Confirmations > 0);

        Assert.Empty(flow.Operations);
        Assert.Equal(2, flow.Reads.Count);
        Assert.Equal("Confirming", flow.Stage);
    }

    [Fact]
    public void Timed_out_preflight_late_response_cannot_dispatch_during_new_retry()
    {
        using var flow = new ManifestCoordinatorHarness();
        var oldRead = new TaskCompletionSource<string>();
        flow.Responses.Enqueue(() => oldRead.Task);
        flow.Confirm();
        flow.AdvancePastReadTimeout();
        Assert.NotNull(flow.Retry);
        var freshRead = new TaskCompletionSource<string>();
        flow.Responses.Enqueue(() => freshRead.Task);

        flow.Retry!();
        oldRead.SetResult(ManifestCoordinatorHarness.CurrentResponse());
        flow.Tick();
        flow.Tick();

        Assert.Empty(flow.Operations);
        Assert.Equal(2, flow.Reads.Count);
        freshRead.SetResult(ManifestCoordinatorHarness.CurrentResponse());
        flow.TickUntil(() => flow.Operations.Count == 1);
        Assert.Single(flow.Operations);
    }
}
