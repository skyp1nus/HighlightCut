using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HighlightCut.App.ViewModels;

/// <summary>Settings → Keyboard: the shortcut list with search, recording a new key and resolving a clash.</summary>
public sealed partial class KeyboardSettingsViewModel : ViewModelBase
{
    public KeyboardSettingsViewModel()
        : this(new KeyMap())
    {
    }

    public KeyboardSettingsViewModel(KeyMap map)
    {
        Map = map;
        Groups = [.. KeyMap.Catalog.GroupBy(i => i.Group)
            .Select(g => new ShortcutGroupViewModel(g.Key, [.. g.Select(i => new ShortcutRowViewModel(this, i))]))];
        map.Changed += (_, _) =>
        {
            RefreshRows();
            ApplyFilter();
        };
    }

    public KeyMap Map { get; }

    public IReadOnlyList<ShortcutGroupViewModel> Groups { get; }

    public IEnumerable<ShortcutRowViewModel> Rows => Groups.SelectMany(g => g.Rows);

    public ShortcutRowViewModel Row(ShortcutAction action) => Rows.Single(r => r.Action == action);

    /// <summary>Filters the rows by name or key.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    public partial string Query { get; set; } = "";

    /// <summary>No row matches <see cref="Query"/>.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    public string EmptyText => "No shortcuts match “" + Query + "”";

    [ObservableProperty]
    public partial ShortcutRowViewModel? Selected { get; private set; }

    /// <summary>The selected row waits for its new key.</summary>
    [ObservableProperty]
    public partial bool IsRecording { get; private set; }

    /// <summary>The recorded key belongs to another action; Replace or Cancel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConflict))]
    public partial ShortcutConflict? Conflict { get; private set; }

    public bool HasConflict => Conflict is not null;

    public void Select(ShortcutRowViewModel row)
    {
        if (Selected == row)
            return;
        Selected = row;
        IsRecording = false;
        Conflict = null;
    }

    public void StartRecording(ShortcutRowViewModel row)
    {
        Selected = row;
        Conflict = null;
        IsRecording = true;
    }

    /// <summary>A key pressed while recording: Esc stops, a modifier alone keeps waiting, anything else is the new key.</summary>
    public void Record(Key key, KeyModifiers modifiers)
    {
        if (!IsRecording || Selected is null)
            return;
        if (key == Key.Escape)
        {
            IsRecording = false;
            return;
        }
        if (KeyCombo.From(key, modifiers) is not { } combo)
            return;
        IsRecording = false;
        if (Map.FindConflict(combo, Selected.Action) is { } other)
            Conflict = new ShortcutConflict(combo, other);
        else
            Map.Assign(Selected.Action, combo);
    }

    /// <summary>Drops the conflict; false if there was none.</summary>
    public bool CancelConflict()
    {
        if (Conflict is null)
            return false;
        Conflict = null;
        return true;
    }

    /// <summary>Stops recording and drops a conflict; the selection stays.</summary>
    public void CancelRecording()
    {
        IsRecording = false;
        Conflict = null;
    }

    /// <summary>Back to an unfiltered list with nothing selected.</summary>
    public void Clear()
    {
        Query = "";
        Selected = null;
        CancelRecording();
    }

    [RelayCommand]
    private void ResetAll()
    {
        Map.ResetAll();
        CancelRecording();
    }

    /// <summary>Gives the conflicting key to the selected action, taking it from the other one.</summary>
    internal void Replace()
    {
        if (Selected is null || Conflict is not { } conflict)
            return;
        Conflict = null;
        Map.Assign(Selected.Action, conflict.Combo);
    }

    partial void OnQueryChanged(string value) => ApplyFilter();

    partial void OnSelectedChanged(ShortcutRowViewModel? value) => RefreshRows();

    partial void OnIsRecordingChanged(bool value) => RefreshRows();

    partial void OnConflictChanged(ShortcutConflict? value) => RefreshRows();

    private void RefreshRows()
    {
        foreach (var row in Rows)
            row.Refresh();
    }

    private void ApplyFilter()
    {
        string q = Query.Trim();
        foreach (var group in Groups)
        {
            foreach (var row in group.Rows)
            {
                row.IsMatch = q.Length == 0 || row.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || row.KeysText.Contains(q, StringComparison.OrdinalIgnoreCase);
            }
            group.IsMatch = group.Rows.Any(r => r.IsMatch);
        }
        IsEmpty = !Groups.Any(g => g.IsMatch);
    }
}

/// <summary>A recorded key that another action already has.</summary>
public sealed record ShortcutConflict(KeyCombo Combo, ShortcutAction Other);

/// <summary>A chip of a row; "or" goes before every chip but the first.</summary>
public sealed record KeyChip(string Label, bool ShowOr);

/// <summary>A group of Settings → Keyboard (Playback, Editing, File, View).</summary>
public sealed partial class ShortcutGroupViewModel(string label, IReadOnlyList<ShortcutRowViewModel> rows) : ViewModelBase
{
    public string Label { get; } = label;
    public IReadOnlyList<ShortcutRowViewModel> Rows { get; } = rows;

    /// <summary>Some row of the group matches the search.</summary>
    [ObservableProperty]
    public partial bool IsMatch { get; set; } = true;
}

/// <summary>An action's row: its keys, or the recording pill, or the conflict line.</summary>
public sealed partial class ShortcutRowViewModel(KeyboardSettingsViewModel owner, ShortcutInfo info) : ViewModelBase
{
    private IReadOnlyList<KeyCombo> Keys => owner.Map[Action];

    public ShortcutAction Action => info.Action;
    public string Name => info.Name;

    public IReadOnlyList<KeyChip> Chips => [.. Keys.Select((k, i) => new KeyChip(k.Label, i > 0))];

    /// <summary>The chip labels, for the search.</summary>
    public string KeysText => string.Join(' ', Keys.Select(k => k.Label));

    /// <summary>The keys differ from the defaults ("changed").</summary>
    public bool IsModified => !owner.Map.IsDefault(Action);

    public bool IsSelected => owner.Selected == this;
    public bool IsRecording => IsSelected && owner.IsRecording;
    public bool HasConflict => IsSelected && owner.HasConflict;
    public bool ShowPill => IsRecording || HasConflict;
    public bool ShowKeys => !ShowPill && Keys.Count > 0;
    public bool IsNotSet => !ShowPill && Keys.Count == 0;
    public bool ShowActions => IsSelected && !ShowPill;
    public string PillText => HasConflict && owner.Conflict is { } c ? c.Combo.Label : "Press the new shortcut…";

    public string ConflictText => HasConflict && owner.Conflict is { } c
        ? $"{c.Combo.Format('+')} is used by {KeyMap.Info(c.Other).Name}."
        : "";

    public double ResetOpacity => IsModified ? 1 : 0.4;

    /// <summary>The row matches the search.</summary>
    [ObservableProperty]
    public partial bool IsMatch { get; set; } = true;

    [RelayCommand]
    private void Select() => owner.Select(this);

    [RelayCommand]
    private void Change() => owner.StartRecording(this);

    [RelayCommand]
    private void Reset() => owner.Map.Reset(Action);

    [RelayCommand]
    private void Replace() => owner.Replace();

    [RelayCommand]
    private void CancelConflict() => owner.CancelConflict();

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Chips));
        OnPropertyChanged(nameof(KeysText));
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(HasConflict));
        OnPropertyChanged(nameof(ShowPill));
        OnPropertyChanged(nameof(ShowKeys));
        OnPropertyChanged(nameof(IsNotSet));
        OnPropertyChanged(nameof(ShowActions));
        OnPropertyChanged(nameof(PillText));
        OnPropertyChanged(nameof(ConflictText));
        OnPropertyChanged(nameof(ResetOpacity));
    }
}
