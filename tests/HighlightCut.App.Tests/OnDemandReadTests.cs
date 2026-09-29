using HighlightCut.App.Services;

namespace HighlightCut.App.Tests;

/// <summary>
/// A part of the analysis read on demand: by a chip, or by a feature that waits for it. It runs once, keeps what it read,
/// and a stopped read keeps nothing.
/// </summary>
public sealed class OnDemandReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A read that runs until <see cref="Finish"/> (or until it is cancelled), counting runs and drops.</summary>
    private sealed class Part
    {
        private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Part() => Read = new OnDemandRead(RunAsync, () => Drops++, () => { }, CancellationToken.None);

        public OnDemandRead Read { get; }
        public int Runs { get; private set; }
        public int Drops { get; private set; }

        public void Finish() => _gate.TrySetResult();

        private async Task RunAsync(CancellationToken ct)
        {
            Runs++;
            await _gate.Task.WaitAsync(ct);
            _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    [Fact]
    public async Task A_chip_reads_once_and_what_was_read_stays()
    {
        var part = new Part();
        part.Read.Show();
        Assert.True(part.Read.IsRunning);
        part.Finish();
        await part.Read.Task.WaitAsync(Ct);

        Assert.True(part.Read.IsDone);
        part.Read.Hide();
        part.Read.Show();
        await part.Read.WaitAsync(Ct);
        Assert.Equal((1, 0), (part.Runs, part.Drops));
    }

    [Fact]
    public async Task Turning_the_chip_off_stops_the_read_and_drops_it_and_on_again_reads_afresh()
    {
        var part = new Part();
        part.Read.Show();
        var first = part.Read.Task;
        part.Read.Hide();
        await first.WaitAsync(Ct);

        Assert.False(part.Read.IsDone || part.Read.IsRunning || part.Read.IsRequested);
        Assert.Equal(1, part.Drops);

        part.Read.Show();
        part.Finish();
        await part.Read.Task.WaitAsync(Ct);
        Assert.True(part.Read.IsDone);
        Assert.Equal(2, part.Runs);
    }

    [Fact]
    public async Task A_feature_waiting_for_the_read_keeps_it_going_when_the_chip_turns_off()
    {
        var part = new Part();
        part.Read.Show();
        var waiting = part.Read.WaitAsync(Ct);
        part.Read.Hide();

        Assert.True(part.Read.IsRunning);
        part.Finish();
        await waiting;
        Assert.True(part.Read.IsDone);
        Assert.Equal((1, 0), (part.Runs, part.Drops));
    }

    [Fact]
    public async Task A_cancelled_wait_stops_the_read_unless_the_chip_wants_it()
    {
        var part = new Part();
        using (var cts = new CancellationTokenSource())
        {
            var waiting = part.Read.WaitAsync(cts.Token);
            Assert.True(part.Read.IsRunning);
            var read = part.Read.Task;
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            await read.WaitAsync(Ct);
        }
        Assert.False(part.Read.IsDone || part.Read.IsRunning);
        Assert.Equal(1, part.Drops);

        part.Read.Show();
        using (var cts = new CancellationTokenSource())
        {
            var waiting = part.Read.WaitAsync(cts.Token);
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        Assert.True(part.Read.IsRunning);
        part.Finish();
        await part.Read.Task.WaitAsync(Ct);
        Assert.True(part.Read.IsDone);
    }
}
