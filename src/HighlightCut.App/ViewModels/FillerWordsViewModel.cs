using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fillers = HighlightCut.Core.Transcripts.FillerWords;

namespace HighlightCut.App.ViewModels;

/// <summary>One language's filler words in Settings → Transcription: chips, then the "Add…" box.</summary>
public sealed partial class FillerLanguageViewModel : ViewModelBase
{
    private readonly SettingsViewModel _owner;

    internal FillerLanguageViewModel(SettingsViewModel owner, string code, string label, IReadOnlyList<string> words)
    {
        _owner = owner;
        Code = code;
        Label = label;
        Entries = [this];
        Show(words);
    }

    /// <summary>"en", "uk".</summary>
    public string Code { get; }

    /// <summary>"English", "Ukrainian".</summary>
    public string Label { get; }

    public ObservableCollection<FillerWordViewModel> Words { get; } = [];

    /// <summary>The chips, then this language itself (the "Add…" box), so both wrap in one panel.</summary>
    public ObservableCollection<object> Entries { get; }

    /// <summary>What is typed in the "Add…" box.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveLastCommand))]
    public partial string Draft { get; set; } = "";

    /// <summary>Enter: adds the typed word (trimmed, lower case) unless it is already there; the box empties either way.</summary>
    [RelayCommand]
    private void AddDraft()
    {
        var word = Fillers.Clean([Draft]);
        if (word.Count > 0 && !Words.Any(w => w.Word == word[0]))
            Save([.. Words.Select(w => w.Word), word[0]]);
        Draft = "";
    }

    /// <summary>Backspace in the empty box: removes the last word.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveLast))]
    private void RemoveLast() => Save(Words.SkipLast(1).Select(w => w.Word));

    private bool CanRemoveLast() => Draft.Length == 0 && Words.Count > 0;

    internal void Remove(FillerWordViewModel word) => Save(Words.Where(w => w != word).Select(w => w.Word));

    private void Save(IEnumerable<string> words) => _owner.SetFillerWords(Code, words);

    /// <summary>Shows <paramref name="words"/> as chips; the "Add…" box stays, and keeps its focus.</summary>
    internal void Show(IReadOnlyList<string> words)
    {
        if (Words.Select(w => w.Word).SequenceEqual(words, StringComparer.Ordinal))
            return;
        Words.Clear();
        while (Entries.Count > 1)
            Entries.RemoveAt(0);
        foreach (string word in words)
        {
            var chip = new FillerWordViewModel(this, word);
            Words.Add(chip);
            Entries.Insert(Entries.Count - 1, chip);
        }
        RemoveLastCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>A filler word chip with its ✕.</summary>
public sealed partial class FillerWordViewModel(FillerLanguageViewModel owner, string word) : ViewModelBase
{
    public string Word { get; } = word;

    [RelayCommand]
    private void Remove() => owner.Remove(this);
}
