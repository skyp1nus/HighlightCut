using Avalonia.Threading;
using HighlightCut.Core.Transcripts;
using HighlightCut.Transcription;

namespace HighlightCut.App.Demo;

/// <summary>The sample's transcript: done, missing, or being transcribed like the prototype's <c>trTick</c>.</summary>
public sealed partial class DesignSample : IDisposable
{
    private const double TranscribeStep = 0.005;
    private static readonly TimeSpan TranscribeTick = TimeSpan.FromMilliseconds(250);

    private Transcript? _transcript;
    private TranscriptState _transcriptState;
    private double _transcriptProgress;
    private DispatcherTimer? _transcribeTimer;

    public Transcript? Transcript => _transcript;
    public TranscriptState TranscriptState => _transcriptState;
    public double TranscriptProgress => _transcriptProgress;

    /// <summary>Raised when the simulated transcription moves on (the rest of the sample is complete from the start).</summary>
    public event EventHandler? Changed;

    /// <summary>The whole design transcript (<see cref="TranscriptState.Done"/>) or none, without a timer.</summary>
    public void SetTranscript(TranscriptState state)
    {
        StopTranscribing();
        bool done = state == TranscriptState.Done;
        _transcriptState = done ? TranscriptState.Done : TranscriptState.None;
        _transcript = done ? DesignTranscript.Full : null;
        _transcriptProgress = done ? 1 : 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Transcribes from <paramref name="progress"/> (0..1) on, 0.5 % every 250 ms.</summary>
    public void StartTranscribing(double progress)
    {
        StopTranscribing();
        SetProgress(progress);
        _transcribeTimer = new DispatcherTimer(TranscribeTick, DispatcherPriority.Background, (_, _) => TickTranscription());
        _transcribeTimer.Start();
    }

    /// <summary>One step of the simulated transcription; at the end the transcript is done.</summary>
    public void TickTranscription()
    {
        if (_transcriptState != TranscriptState.Running)
            return;
        // Tolerance for the rounding of 0.005 steps.
        if (_transcriptProgress + TranscribeStep >= 1 - 1e-9)
            SetTranscript(TranscriptState.Done);
        else
            SetProgress(_transcriptProgress + TranscribeStep);
    }

    public void Dispose() => StopTranscribing();

    private void SetProgress(double progress)
    {
        _transcriptState = TranscriptState.Running;
        _transcriptProgress = progress;
        _transcript = DesignTranscript.UpTo(progress * SampleDuration);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void StopTranscribing()
    {
        _transcribeTimer?.Stop();
        _transcribeTimer = null;
    }
}
