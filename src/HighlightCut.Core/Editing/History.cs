using HighlightCut.Core.Model;

namespace HighlightCut.Core.Editing;

/// <summary>One applied edit, with the project before and after it.</summary>
public sealed record HistoryEntry(
    IEditCommand Command,
    string Description,
    Project Before,
    Project After,
    EditOrigin Origin,
    DateTimeOffset Time,
    string? MergeKey)
{
    /// <summary>Change in output duration caused by this edit, in seconds.</summary>
    public double OutputDelta => After.OutputDuration - Before.OutputDuration;

    /// <summary>Stable number of this edit in its session (1, 2, …), e.g. for an MCP client to refer to it.</summary>
    public long Id { get; init; }

    /// <summary>Clips added or changed by this edit (not removed ones).</summary>
    public IReadOnlyList<int> ChangedClipIds =>
        [.. After.Clips.Where(c => Before.Find(c.Id) is not { } old || old != c).Select(c => c.Id)];
}

/// <summary>
/// Linear undo/redo history. Entries after <see cref="Position"/> are undone and can be redone
/// until a new edit is made.
/// </summary>
public sealed class History
{
    private readonly List<HistoryEntry> _entries = [];
    private long _lastId;

    public History(int capacity = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public IReadOnlyList<HistoryEntry> Entries => _entries;

    /// <summary>Number of entries currently applied.</summary>
    public int Position { get; private set; }

    public bool CanUndo => Position > 0;
    public bool CanRedo => Position < _entries.Count;

    public HistoryEntry? NextUndo => CanUndo ? _entries[Position - 1] : null;
    public HistoryEntry? NextRedo => CanRedo ? _entries[Position] : null;

    public bool IsApplied(HistoryEntry entry) => _entries.IndexOf(entry) is var i && i >= 0 && i < Position;

    /// <summary>The entry with <see cref="HistoryEntry.Id"/> <paramref name="id"/>, if it is still in the history.</summary>
    public HistoryEntry? Find(long id) => _entries.FirstOrDefault(e => e.Id == id);

    /// <summary>
    /// Records an edit. If <paramref name="entry"/> has the same merge key as the last applied entry
    /// (e.g. successive steps of one drag), the two become a single undo step.
    /// </summary>
    internal HistoryEntry Push(HistoryEntry entry)
    {
        if (Position < _entries.Count)
            _entries.RemoveRange(Position, _entries.Count - Position);

        if (entry.MergeKey is not null && NextUndo is { } last && last.MergeKey == entry.MergeKey)
        {
            var merged = entry with { Before = last.Before, Id = last.Id };
            _entries[Position - 1] = merged;
            return merged;
        }

        entry = entry with { Id = ++_lastId };
        _entries.Add(entry);
        if (_entries.Count > Capacity)
            _entries.RemoveAt(0);
        Position = _entries.Count;
        return entry;
    }

    internal HistoryEntry? StepBack()
    {
        if (!CanUndo)
            return null;
        Position--;
        return _entries[Position];
    }

    internal HistoryEntry? StepForward()
    {
        if (!CanRedo)
            return null;
        Position++;
        return _entries[Position - 1];
    }

    internal void Clear()
    {
        _entries.Clear();
        Position = 0;
    }
}
