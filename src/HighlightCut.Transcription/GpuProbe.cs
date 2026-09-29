using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HighlightCut.Transcription.Models;

namespace HighlightCut.Transcription;

/// <summary>What the DirectML check found for a model on this machine's GPU.</summary>
/// <param name="Works">DirectML started and recognized the sample.</param>
/// <param name="Speedup">
/// How many times more audio a second DirectML recognized than the CPU plan (below 1: slower); null when it does not work.
/// </param>
/// <param name="Problem">Why it does not work, e.g. "there is no GPU it can use".</param>
/// <param name="SameWords">DirectML gave the same words as the CPU for the sample.</param>
public sealed record GpuCheck(bool Works, double? Speedup = null, string? Problem = null, bool SameWords = true)
{
    /// <summary>Auto uses DirectML: it works and is at least as fast as the CPU.</summary>
    [JsonIgnore]
    public bool Faster => Works && Speedup >= 1;

    /// <summary>"3.2×".</summary>
    [JsonIgnore]
    public string SpeedupText => (Speedup ?? 0).ToString(Speedup >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture) + "×";

    public static GpuCheck NoGpu { get; } = new(false, Problem: "there is no GPU it can use");

    /// <summary>The check itself could not run (not the GPU's fault): not remembered, so it is tried again.</summary>
    [JsonIgnore]
    public bool Retry { get; init; }
}

/// <summary>
/// Checks DirectML before Auto relies on it. A GPU or driver that fails inside native code can end the process instead
/// of throwing, so the check runs in a child process (<see cref="RunChild"/>): it loads the model with DirectML,
/// recognizes the model's own sample, then does the same on the CPU to compare the words and the speed. The result is
/// remembered per GPU, driver, runtime and model, so each is checked once.
/// </summary>
public static partial class GpuProbe
{
    /// <summary>
    /// Starts the child that runs <see cref="RunChild"/>: HighlightCut.exe and "--probe-gpu-child", set by the app.
    /// Null (tests, other hosts): DirectML cannot be checked, so Auto stays on the CPU.
    /// </summary>
    public static (string FileName, IReadOnlyList<string> Arguments)? Command { get; set; }

    /// <summary>Where results are kept between runs; null keeps them for this run only.</summary>
    public static string? CacheFile { get; set; }

    /// <summary>A check gave a result (on the thread that ran it).</summary>
    public static event EventHandler? Checked;

    /// <summary>Longest a check may take; a hung driver counts as not working.</summary>
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The GPU checked; tests set it.</summary>
    internal static GpuAdapter? Adapter { get; set; } = GpuAdapter.Current;

    private static readonly Lock Gate = new();
    private static Dictionary<string, GpuCheck>? _results;

    /// <summary>Forgets the results read from <see cref="CacheFile"/>, so the next use reads it again (tests).</summary>
    internal static void Reset()
    {
        lock (Gate)
            _results = null;
    }

    /// <summary>What an earlier check found for <paramref name="model"/> on this GPU, or null if it has not been checked.</summary>
    public static GpuCheck? Known(TranscriptionModel? model)
    {
        if (Adapter is not { IsSoftware: false } adapter)
            return GpuCheck.NoGpu;
        if (model is null)
            return null;
        lock (Gate)
            return Results().GetValueOrDefault(Key(adapter, model));
    }

    /// <summary>
    /// The result for <paramref name="model"/>: remembered, or found by running the check now (seconds; up to
    /// <see cref="Timeout"/>). Null when it cannot be checked (<see cref="Command"/> is not set).
    /// </summary>
    public static GpuCheck? Check(TranscriptionModel model, string directory, string? language)
    {
        if (Adapter is not { IsSoftware: false } adapter)
            return GpuCheck.NoGpu;
        if (Known(model) is { } known)
            return known;
        if (Command is null)
            return null;
        var result = Run(model, directory, language);
        if (!result.Retry)
        {
            lock (Gate)
            {
                Results()[Key(adapter, model)] = result;
                Save();
            }
        }
        Checked?.Invoke(null, EventArgs.Empty);
        return result;
    }

    private static string Key(GpuAdapter adapter, TranscriptionModel model) => $"{adapter.Key}|{RecognizerPlan.GpuRuntimeStamp}|{model.Id}";

    private static Dictionary<string, GpuCheck> Results()
    {
        if (_results is null)
        {
            try
            {
                _results = CacheFile is { } file && File.Exists(file)
                    ? JsonSerializer.Deserialize(File.ReadAllBytes(file), GpuProbeJson.Default.DictionaryStringGpuCheck)
                    : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
            }
            _results ??= [];
        }
        return _results;
    }

    private static void Save()
    {
        if (CacheFile is not { } file)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, JsonSerializer.SerializeToUtf8Bytes(_results!, GpuProbeJson.Default.DictionaryStringGpuCheck));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Runs the check now, whatever the GPU, and does not remember the result (HighlightCut --probe-gpu).
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="Command"/> is not set.</exception>
    public static GpuCheck Run(TranscriptionModel model, string directory, string? language)
    {
        var command = Command ?? throw new InvalidOperationException("No command to run the GPU check with.");
        var psi = new ProcessStartInfo(command.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in (string[])[.. command.Arguments, model.Id, directory, language ?? ""])
            psi.ArgumentList.Add(a);
        try
        {
            using var child = Process.Start(psi)!;
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(Timeout))
            {
                try
                {
                    child.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                return new GpuCheck(false, Problem: $"the check did not finish in {Timeout.TotalMinutes:0} minutes");
            }
            child.WaitForExit();
            return Interpret(child.ExitCode, output.Result, errors.Result);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new GpuCheck(false, Problem: "the check could not start: " + e.Message) { Retry = true };
        }
    }

    /// <summary>What the child's exit code, output (<see cref="ChildResult"/> as JSON) and errors say.</summary>
    internal static GpuCheck Interpret(int exitCode, string output, string errors)
    {
        // sherpa-onnx carries on on the CPU when DirectML does not start, and says so on stderr.
        if (FallbackLine().Match(errors) is { Success: true } fallback)
            return new GpuCheck(false, Problem: "it did not start (" + Shorten(fallback.Groups["why"].Value) + ")");
        ChildResult? result = null;
        try
        {
            string? line = output.Split('\n').LastOrDefault(l => l.TrimStart().StartsWith('{'));
            result = line is null ? null : JsonSerializer.Deserialize(line, GpuProbeJson.Default.ChildResult);
        }
        catch (JsonException)
        {
        }
        if (exitCode != 0 || result is null)
        {
            string last = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
            string code = exitCode < 0 ? "0x" + exitCode.ToString("X8", CultureInfo.InvariantCulture) : exitCode.ToString(CultureInfo.InvariantCulture);
            return new GpuCheck(false, Problem: $"the check stopped (exit code {code})" + (last.Length > 0 ? ": " + Shorten(last) : ""));
        }
        if (result.GpuWords == 0 && result.CpuWords > 0)
            return new GpuCheck(false, Problem: "it recognized no words");
        return new GpuCheck(true, result.CpuSeconds / (result.CpuPieces * result.GpuSeconds), SameWords: result.SameWords);
    }

    // "…session.cc:Run:369 Failed to enable DirectML: <message>. Fallback to cpu"
    [GeneratedRegex(@"Failed to enable DirectML: (?<why>.*?)\.? Fallback to cpu", RegexOptions.Singleline)]
    private static partial Regex FallbackLine();

    // ONNX Runtime's errors carry file names and addresses; the Windows message after the HRESULT is what says why.
    [GeneratedRegex(@"\b[0-9A-F]{8} (?<message>[^\r\n]+)$")]
    private static partial Regex HresultMessage();

    private static string Shorten(string message)
    {
        message = message.Trim();
        message = HresultMessage().Match(message) is { Success: true } m ? m.Groups["message"].Value : message;
        message = message.Trim().TrimEnd('.');
        return message.Length <= 120 ? message : message[..117] + "…";
    }

    /// <summary>What the child found: seconds for one piece on the GPU, for <see cref="CpuPieces"/> at once on the CPU.</summary>
    internal sealed record ChildResult(double GpuSeconds, double CpuSeconds, int CpuPieces, int GpuWords, int CpuWords, bool SameWords);

    /// <summary>
    /// The check, in the child process: args are the model id, its folder and the language ("" to detect). Prints a
    /// <see cref="ChildResult"/> as one line of JSON. If DirectML fails badly, this process ends and the parent sees it.
    /// </summary>
    public static int RunChild(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || ModelCatalog.Find(args[0]) is not { } model)
        {
            Console.Error.WriteLine("usage: --probe-gpu-child <model id> <model folder> [language]");
            return 2;
        }
        string directory = args[1];
        string? language = args.Count > 2 && args[2].Length > 0 ? args[2] : null;
        float[] sample = Sample(directory);
        int cores = Environment.ProcessorCount;

        IReadOnlyList<Core.Transcripts.Word> gpuWords;
        double gpuSeconds;
        using (var gpu = new SherpaRecognizer(model, directory, language, RecognizerPlan.DirectML(cores)))
        {
            // The first run compiles the GPU's shaders; the second is the speed of the ones after it.
            gpu.Recognize(sample, 0);
            var clock = Stopwatch.StartNew();
            gpuWords = gpu.Recognize(sample, 0);
            gpuSeconds = clock.Elapsed.TotalSeconds;
        }

        // The CPU as transcription uses it: several pieces at once.
        var cpuPlan = RecognizerPlan.Cpu(cores);
        IReadOnlyList<Core.Transcripts.Word> cpuWords;
        double cpuSeconds;
        using (var cpu = new SherpaRecognizer(model, directory, language, cpuPlan))
        {
            var clock = Stopwatch.StartNew();
            var pieces = Enumerable.Range(0, cpuPlan.Parallelism)
                .Select(_ => Task.Factory.StartNew(() => cpu.Recognize(sample, 0), TaskCreationOptions.LongRunning)).ToArray();
            Task.WaitAll(pieces);
            cpuSeconds = clock.Elapsed.TotalSeconds;
            cpuWords = pieces[0].Result;
        }

        bool same = gpuWords.Select(w => w.Text).SequenceEqual(cpuWords.Select(w => w.Text), StringComparer.OrdinalIgnoreCase);
        var result = new ChildResult(gpuSeconds, cpuSeconds, cpuPlan.Parallelism, gpuWords.Count, cpuWords.Count, same);
        Console.Out.WriteLine(JsonSerializer.Serialize(result, GpuProbeJson.Default.ChildResult));
        Console.Out.Flush();
        return 0;
    }

    /// <summary>
    /// About 20 s of the model's own recorded samples (test_wavs, 16 kHz 16-bit mono WAV) one after another, or of a
    /// tone when there are none: a piece as long as transcription gives the recognizer.
    /// </summary>
    internal static float[] Sample(string directory)
    {
        const int Length = 20 * TranscriptionPipeline.SampleRate;
        string wavs = Path.Combine(directory, "test_wavs");
        var clips = Directory.Exists(wavs)
            ? Directory.GetFiles(wavs, "*.wav").Order(StringComparer.Ordinal).Select(ReadWav).OfType<float[]>().Where(c => c.Length > 0).ToList()
            : [];
        var sample = new float[Length];
        if (clips.Count == 0)
        {
            for (int i = 0; i < Length; i++)
                sample[i] = (float)(0.2 * Math.Sin(2 * Math.PI * 220 * i / TranscriptionPipeline.SampleRate));
            return sample;
        }
        // Each clip is followed by a short pause, like speech.
        int filled = 0;
        for (int n = 0; filled < Length; n++)
        {
            var clip = clips[n % clips.Count];
            int count = Math.Min(clip.Length, Length - filled);
            Array.Copy(clip, 0, sample, filled, count);
            filled = Math.Min(Length, filled + count + TranscriptionPipeline.SampleRate / 3);
        }
        return sample;
    }

    /// <summary>The samples of a 16 kHz 16-bit PCM mono WAV file, or null for any other file.</summary>
    internal static float[]? ReadWav(string path)
    {
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path), Encoding.ASCII);
            if (new string(reader.ReadChars(4)) != "RIFF")
                return null;
            reader.ReadInt32();
            if (new string(reader.ReadChars(4)) != "WAVE")
                return null;
            bool pcm16Mono = false;
            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                string chunk = new(reader.ReadChars(4));
                int size = reader.ReadInt32();
                long next = reader.BaseStream.Position + size + (size & 1);
                if (chunk == "fmt ")
                {
                    short format = reader.ReadInt16(), channels = reader.ReadInt16();
                    int rate = reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt16();
                    short bits = reader.ReadInt16();
                    pcm16Mono = format == 1 && channels == 1 && rate == TranscriptionPipeline.SampleRate && bits == 16;
                }
                else if (chunk == "data")
                {
                    if (!pcm16Mono)
                        return null;
                    var samples = new float[Math.Min(size, (int)(reader.BaseStream.Length - reader.BaseStream.Position)) / 2];
                    for (int i = 0; i < samples.Length; i++)
                        samples[i] = reader.ReadInt16() / 32768f;
                    return samples;
                }
                reader.BaseStream.Position = next;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, GpuCheck>))]
[JsonSerializable(typeof(GpuProbe.ChildResult))]
internal sealed partial class GpuProbeJson : JsonSerializerContext;
