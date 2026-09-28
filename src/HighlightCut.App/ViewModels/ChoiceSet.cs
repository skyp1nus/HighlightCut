namespace HighlightCut.App.ViewModels;

/// <summary>The segmented options of one setting, with the current value marked.</summary>
internal sealed class ChoiceSet<T>
{
    private readonly T[] _values;

    public ChoiceSet(IReadOnlyList<(T Value, string Label)> items, Action<T> pick, T selected)
    {
        _values = [.. items.Select(i => i.Value)];
        Options = [.. items.Select(i => new ChoiceOption(i.Label, () => pick(i.Value)))];
        Select(selected);
    }

    public IReadOnlyList<ChoiceOption> Options { get; }

    public void Select(T value)
    {
        for (int i = 0; i < _values.Length; i++)
            Options[i].IsSelected = EqualityComparer<T>.Default.Equals(_values[i], value);
    }
}
