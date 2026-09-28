namespace HighlightCut.Transcription;

public enum TranscriptState
{
    /// <summary>Not asked for (or cancelled).</summary>
    None,

    /// <summary>Asked for; starts when the rest of the file's analysis is done.</summary>
    Waiting,

    Running,
    Done,
    Failed,
}

/// <summary>What to transcribe a file with.</summary>
/// <param name="ModelDirectory">Where the model is installed.</param>
/// <param name="Language">Two-letter language code, or null to detect it.</param>
/// <param name="CreateRecognizer">Makes the recognizer (tests use a fake); sherpa-onnx if null.</param>
/// <param name="Device">Settings → Transcription → Device.</param>
public sealed record TranscriptionSetup(Models.TranscriptionModel Model, string ModelDirectory, string? Language,
    Func<ISpeechRecognizer>? CreateRecognizer = null, TranscriptionDevice Device = TranscriptionDevice.Auto)
{
    /// <summary>Same model and language: the same transcript (whichever device made it).</summary>
    public string Key => $"{Model.Id}|{Language ?? "auto"}";

    public ISpeechRecognizer Create() => CreateRecognizer?.Invoke() ?? SherpaRecognizer.Create(Model, ModelDirectory, Language, Device);
}
