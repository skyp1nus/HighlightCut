namespace HighlightCut.App.Services;

/// <summary>
/// One part of a file's analysis (the keyframes, the waveform) that runs only when something needs it: a timeline chip
/// (<see cref="Show"/>) or a feature that waits for the result (<see cref="WaitAsync"/>), such as a lossless export. It
/// runs once and what it read stays. Stopped before it finishes (the chip turned off with nobody else waiting), it is
/// cancelled and <paramref name="drop"/> throws away what it had, so nothing half done is kept.
/// </summary>
/// <param name="read">Reads the part; returns normally when done or failed (a failure is not retried).</param>
/// <param name="drop">Forgets what a stopped read had.</param>
/// <param name="changed">Called when a read starts or ends.</param>
/// <param name="lifetime">Cancelled when the file is closed.</param>
internal sealed class OnDemandRead(Func<CancellationToken, Task> read, Action drop, Action changed, CancellationToken lifetime)
{
    private readonly Lock _lock = new();
    private CancellationTokenSource? _cts;
    private Task _task = Task.CompletedTask;
    private bool _shown;
    private int _waiting;
    private volatile bool _done;
    private volatile bool _running;

    /// <summary>The read has finished (or failed); what it read stays.</summary>
    public bool IsDone => _done;

    /// <summary>A read is under way.</summary>
    public bool IsRunning => _running;

    /// <summary>A chip shows the part, or a feature waits for it.</summary>
    public bool IsRequested
    {
        get
        {
            lock (_lock)
                return _shown || _waiting > 0;
        }
    }

    /// <summary>Completes when the latest read has finished, failed or been stopped; done while nobody asked.</summary>
    public Task Task
    {
        get
        {
            lock (_lock)
                return _task;
        }
    }

    /// <summary>The chip is on: read the part unless that is done or under way.</summary>
    public void Show()
    {
        lock (_lock)
        {
            _shown = true;
            StartLocked();
        }
    }

    /// <summary>The chip is off: an unfinished read stops unless a feature is waiting for it.</summary>
    public void Hide()
    {
        lock (_lock)
        {
            _shown = false;
            StopIfUnwantedLocked();
        }
    }

    /// <summary>
    /// Reads the part if it is not read yet and waits until it is. Cancelling the wait stops the read too, unless the chip
    /// or another feature still wants it.
    /// </summary>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (_lock)
        {
            _waiting++;
            StartLocked();
            task = _task;
        }
        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                _waiting--;
                StopIfUnwantedLocked();
            }
        }
    }

    private void StartLocked()
    {
        if (_done || _running || lifetime.IsCancellationRequested)
            return;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _cts = cts;
        _running = true;
        var stopped = _task;
        _task = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                // A read stopped a moment ago lets go of its ffmpeg (and drops what it had) first.
                await stopped.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                await read(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (_lock)
                {
                    if (cts.IsCancellationRequested)
                        drop();
                    else
                        _done = true;
                    if (ReferenceEquals(_cts, cts))
                    {
                        _cts = null;
                        _running = false;
                    }
                }
                cts.Dispose();
                changed();
            }
        }, CancellationToken.None);
        changed();
    }

    private void StopIfUnwantedLocked()
    {
        if (_shown || _waiting > 0 || _cts is not { } cts)
            return;
        _cts = null;
        _running = false;
        cts.Cancel();
        changed();
    }
}
