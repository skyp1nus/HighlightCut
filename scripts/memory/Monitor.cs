using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace HighlightCut.MemoryBench;

/// <summary>One step of the session, measured at its end (without forcing a collection) and at its peak.</summary>
public sealed record StepResult(
    string Name,
    double Seconds,
    long WorkingSet,
    long PeakWorkingSet,
    long Private,
    long Managed,
    long PeakManaged,
    long GcCommitted,
    long Allocated,
    int Gen2,
    long? MallocFree,
    IReadOnlyList<(string Type, long Bytes, long Largest)> TopAllocations,
    string? Note);

/// <summary>
/// Watches the process: working set and private bytes (native memory too: mpv, ONNX Runtime, Skia), the managed heap,
/// what was allocated, and which types the biggest allocations were (the runtime's allocation ticks, one every ~100 KB).
/// </summary>
public sealed class Monitor : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Thread _sampler;
    private readonly AllocationListener _allocations = new();
    private volatile bool _stopped;
    private long _peakWorkingSet, _peakManaged;
    private string? _name;
    private Stopwatch _watch = new();
    private long _allocatedAtStart;
    private int _gen2AtStart;

    public Monitor()
    {
        _sampler = new Thread(Sample) { IsBackground = true, Name = "memory sampler" };
        _sampler.Start();
    }

    public List<StepResult> Steps { get; } = [];

    public void Begin(string name)
    {
        _name = name;
        _allocations.Reset();
        Interlocked.Exchange(ref _peakWorkingSet, 0);
        Interlocked.Exchange(ref _peakManaged, 0);
        _allocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
        _gen2AtStart = GC.CollectionCount(2);
        _watch = Stopwatch.StartNew();
        Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {name}…");
    }

    public StepResult End(string? note = null)
    {
        double seconds = _watch.Elapsed.TotalSeconds;
        var (workingSet, privateBytes) = Now();
        long managed = GC.GetTotalMemory(forceFullCollection: false);
        var info = GC.GetGCMemoryInfo();
        var step = new StepResult(_name!, seconds, workingSet, Math.Max(workingSet, Interlocked.Read(ref _peakWorkingSet)), privateBytes,
            managed, Math.Max(managed, Interlocked.Read(ref _peakManaged)), info.TotalCommittedBytes,
            GC.GetTotalAllocatedBytes(precise: false) - _allocatedAtStart, GC.CollectionCount(2) - _gen2AtStart, MallocFree(),
            _allocations.Top(4), note);
        Steps.Add(step);
        Console.Error.WriteLine("    gen2 reasons: " + _allocations.Gen2Reasons());
        Console.Error.WriteLine($"    {seconds:0.0} s · working set {Mb(workingSet)} MB (peak {Mb(step.PeakWorkingSet)}) · private {Mb(privateBytes)} MB"
                                + $" · managed {Mb(managed)} MB · allocated {Mb(step.Allocated)} MB{(note is null ? "" : " · " + note)}");
        return step;
    }

    /// <summary>Everything allocated in the whole session, largest first.</summary>
    public IReadOnlyList<(string Type, long Bytes, long Largest)> AllAllocations(int count) => _allocations.Top(count, total: true);

    /// <summary>Working set and private bytes. On Linux "private" is the resident anonymous memory (RssAnon), which is what the process itself uses.</summary>
    public (long WorkingSet, long Private) Now()
    {
        _process.Refresh();
        long privateBytes = OperatingSystem.IsLinux() && ReadStatus("RssAnon:") is { } anon ? anon : _process.PrivateMemorySize64;
        return (_process.WorkingSet64, privateBytes);
    }

    /// <summary>
    /// Linux: memory the native code freed that glibc's malloc still holds (<c>mallinfo2().fordblks</c>), which counts as
    /// private until malloc hands it back. Null elsewhere.
    /// </summary>
    private static long? MallocFree()
    {
        if (!OperatingSystem.IsLinux())
            return null;
        try
        {
            return (long)MallInfo2().FreeBytes;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MallInfo
    {
        public readonly nuint Arena, OrdBlks, SmBlks, HBlks, HBlkHd, UsmBlks, FsmBlks, UordBlks, FordBlks, KeepCost;

        public nuint FreeBytes => FordBlks;
    }

    [DllImport("libc", EntryPoint = "mallinfo2")]
    private static extern MallInfo MallInfo2();

    private static long? ReadStatus(string field)
    {
        foreach (string line in File.ReadLines("/proc/self/status"))
        {
            if (line.StartsWith(field, StringComparison.Ordinal))
                return long.Parse(line[field.Length..].Trim().Split(' ')[0], CultureInfo.InvariantCulture) * 1024;
        }
        return null;
    }

    private void Sample()
    {
        using var process = Process.GetCurrentProcess();
        while (!_stopped)
        {
            process.Refresh();
            Max(ref _peakWorkingSet, process.WorkingSet64);
            Max(ref _peakManaged, GC.GetTotalMemory(forceFullCollection: false));
            Thread.Sleep(50);
        }

        static void Max(ref long peak, long value)
        {
            long old;
            while (value > (old = Interlocked.Read(ref peak)) && Interlocked.CompareExchange(ref peak, value, old) != old)
            {
            }
        }
    }

    public static string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0", CultureInfo.InvariantCulture);

    public string Table()
    {
        var text = new StringBuilder();
        bool malloc = Steps.Any(s => s.MallocFree is not null);
        text.AppendLine("| Step | Time | Working set | Peak WS | Private | Managed | Peak managed | GC committed | Allocated | Gen2 GCs |"
            + (malloc ? " Held by malloc |" : ""));
        text.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|" + (malloc ? "---:|" : ""));
        foreach (var s in Steps)
        {
            text.Append(CultureInfo.InvariantCulture, $"| {s.Name}{(s.Note is null ? "" : $" ({s.Note})")} | {s.Seconds:0.0} s | {Mb(s.WorkingSet)} MB | ")
                .Append(CultureInfo.InvariantCulture, $"{Mb(s.PeakWorkingSet)} MB | {Mb(s.Private)} MB | {Mb(s.Managed)} MB | {Mb(s.PeakManaged)} MB | ")
                .Append(CultureInfo.InvariantCulture, $"{Mb(s.GcCommitted)} MB | {Mb(s.Allocated)} MB | {s.Gen2} |")
                .AppendLine(malloc ? $" {Mb(s.MallocFree ?? 0)} MB |" : "");
        }
        text.AppendLine();
        text.AppendLine("Largest allocations per step (sampled by the runtime every ~100 KB; the size of the largest single object):");
        text.AppendLine();
        foreach (var s in Steps.Where(s => s.TopAllocations.Count > 0))
        {
            text.AppendLine($"- {s.Name}: " + string.Join("; ",
                s.TopAllocations.Select(a => $"{a.Type} {Mb(a.Bytes)} MB (largest {a.Largest / 1024} KB)")));
        }
        return text.ToString();
    }

    public void Dispose()
    {
        _stopped = true;
        _sampler.Join();
        _allocations.Dispose();
        _process.Dispose();
    }

    /// <summary>The runtime's GCAllocationTick events: the type allocated when another ~100 KB had been allocated.</summary>
    private sealed class AllocationListener : EventListener
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, (long Bytes, long Largest)> _step = [], _total = [];

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName is not null && e.EventName.StartsWith("GCStart", StringComparison.Ordinal) && e.Payload is not null
                && Convert.ToInt32(Field(e, "Depth"), CultureInfo.InvariantCulture) == 2)
            {
                lock (_lock)
                {
                    string reason = "gen2 GC, reason " + Field(e, "Reason");
                    _step.TryGetValue(reason, out var count);
                    _step[reason] = (count.Bytes + 1, 0);
                }
            }
            if (e.EventName is null || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal) || e.Payload is null)
                return;
            string type = Field(e, "TypeName") as string ?? "?";
            long amount = Convert.ToInt64(Field(e, "AllocationAmount64") ?? Field(e, "AllocationAmount") ?? 0L, CultureInfo.InvariantCulture);
            long size = Convert.ToInt64(Field(e, "ObjectSize") ?? 0L, CultureInfo.InvariantCulture);
            if (Convert.ToInt32(Field(e, "AllocationKind") ?? 0, CultureInfo.InvariantCulture) == 1)
                type += " (LOH)";
            lock (_lock)
            {
                Add(_step);
                Add(_total);
            }

            void Add(Dictionary<string, (long Bytes, long Largest)> to)
            {
                to.TryGetValue(type, out var old);
                to[type] = (old.Bytes + amount, Math.Max(old.Largest, size));
            }
        }

        private static object? Field(EventWrittenEventArgs e, string name)
        {
            int i = e.PayloadNames?.IndexOf(name) ?? -1;
            return i >= 0 ? e.Payload![i] : null;
        }

        public void Reset()
        {
            lock (_lock)
                _step.Clear();
        }

        public IReadOnlyList<(string, long, long)> Top(int count, bool total = false)
        {
            lock (_lock)
                return [.. (total ? _total : _step).Where(p => !p.Key.StartsWith("gen2", StringComparison.Ordinal))
                    .OrderByDescending(p => p.Value.Bytes).Take(count).Select(p => (p.Key, p.Value.Bytes, p.Value.Largest))];
        }

        /// <summary>Why the step's full collections ran: "induced 3, low memory 1".</summary>
        public string Gen2Reasons()
        {
            lock (_lock)
                return string.Join(", ", _step.Where(p => p.Key.StartsWith("gen2", StringComparison.Ordinal))
                    .Select(p => $"{p.Key["gen2 GC, reason ".Length..]}×{p.Value.Bytes}"));
        }
    }
}
