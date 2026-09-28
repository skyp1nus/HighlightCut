namespace HighlightCut.Media.Playback;

/// <summary>
/// Which seeks mpv has finished, and the position to show meanwhile. Seeks are numbered; mpv replies to each seek
/// command and then restarts playback. A restart settles every seek replied to so far, and <see cref="IsSeeking"/>
/// holds while a newer one is still in flight. Everything is a generation number that only grows, so a restart left
/// over from an earlier seek (or from loading the file) can never mark a newer seek as done, whatever the thread timing.
/// </summary>
/// <remarks>
/// mpv may send the restart before the <c>time-pos</c> of the new position; the last one it reported is then still
/// from before the seek. So a restart that settles a seek makes the target the position until mpv reports the next one,
/// and the playhead never jumps back to where it was before the seek.
/// </remarks>
internal sealed class SeekState
{
    private long _requested, _replied, _settled;
    private double _target, _position;

    public bool IsSeeking => Volatile.Read(ref _settled) < Volatile.Read(ref _requested);

    /// <summary>The newest seek's target while seeking, else the position mpv reported last.</summary>
    public double Position => IsSeeking ? Volatile.Read(ref _target) : Volatile.Read(ref _position);

    /// <summary>A new seek to <paramref name="target"/>; returns its number.</summary>
    public long Request(double target)
    {
        Volatile.Write(ref _target, target);
        return Interlocked.Increment(ref _requested);
    }

    /// <summary>mpv reported the playback position (<c>time-pos</c>).</summary>
    public void Reported(double position) => Volatile.Write(ref _position, position);

    /// <summary>mpv replied to seek <paramref name="generation"/>. A failed seek brings no restart, so it settles now.</summary>
    public void Replied(long generation, bool failed)
    {
        Max(ref _replied, generation);
        if (failed)
            Max(ref _settled, generation);
    }

    /// <summary>Playback restarted: the seeks replied to so far are done.</summary>
    public void Restarted()
    {
        long before = Volatile.Read(ref _settled);
        Max(ref _settled, Volatile.Read(ref _replied));
        // Only a restart that ends a seek: others (a new audio mix, say) keep the position mpv reported.
        if (Volatile.Read(ref _settled) > before && !IsSeeking)
            Volatile.Write(ref _position, Volatile.Read(ref _target));
    }

    private static void Max(ref long field, long value)
    {
        long current = Volatile.Read(ref field);
        while (value > current)
        {
            long seen = Interlocked.CompareExchange(ref field, value, current);
            if (seen == current)
                return;
            current = seen;
        }
    }
}
