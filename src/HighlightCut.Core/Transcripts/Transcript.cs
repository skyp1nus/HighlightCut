using System.Text.Json;
using System.Text.Json.Serialization;

namespace HighlightCut.Core.Transcripts;

/// <summary>A spoken word with its time on the source timeline (seconds), punctuation attached.</summary>
public sealed record Word(string Text, double Start, double End);

/// <summary>Words grouped into a sentence or phrase, for reading and for Claude.</summary>
public sealed record Phrase(double Start, double End, string Text, int FirstWord, int WordCount);

/// <summary>What was said in a file, word by word.</summary>
/// <param name="Model">The model that made it, e.g. "parakeet-tdt-0.6b-v3".</param>
/// <param name="Language">The language asked for ("auto" when detected).</param>
/// <param name="ApproximateTimes">Word times are estimated from the audio (the model gave none), good to about half a second.</param>
public sealed record Transcript(string Model, string Language, IReadOnlyList<Word> Words, bool ApproximateTimes = false)
{
    /// <summary>A pause at least this long starts a new phrase even without punctuation.</summary>
    public const double PhrasePause = 0.8;

    /// <summary>Phrases longer than this many words are split at the next pause of a quarter second.</summary>
    public const int LongPhrase = 40;

    [JsonIgnore]
    public string Text => string.Join(' ', Words.Select(w => w.Text));

    /// <summary>Sentences (ending in . ! ? …) or stretches between long pauses.</summary>
    public IReadOnlyList<Phrase> Phrases()
    {
        var phrases = new List<Phrase>();
        int first = 0;
        for (int i = 0; i < Words.Count; i++)
        {
            bool last = i == Words.Count - 1;
            double pause = last ? double.MaxValue : Words[i + 1].Start - Words[i].End;
            bool sentenceEnd = Words[i].Text.Length > 0 && Words[i].Text[^1] is '.' or '!' or '?' or '…';
            if (last || sentenceEnd || pause >= PhrasePause || (i - first + 1 >= LongPhrase && pause >= 0.25))
            {
                phrases.Add(new Phrase(Words[first].Start, Words[i].End, string.Join(' ', Words.Skip(first).Take(i - first + 1).Select(w => w.Text)),
                    first, i - first + 1));
                first = i + 1;
            }
        }
        return phrases;
    }

    /// <summary>The words overlapping a range of the source.</summary>
    public IEnumerable<Word> Between(double start, double end) => Words.Where(w => w.End > start && w.Start < end);

    public string ToJson() => JsonSerializer.Serialize(this, TranscriptJson.Default.Transcript);

    /// <summary>A transcript saved with <see cref="ToJson"/>, or null if the text is not one.</summary>
    public static Transcript? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, TranscriptJson.Default.Transcript) is { Words: not null, Model: not null } t ? t : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>File name for a transcript in the media cache: one per model and language.</summary>
    public static string CacheName(string model, string? language) => $"transcript-{model}-{language ?? "auto"}.json";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Transcript))]
internal sealed partial class TranscriptJson : JsonSerializerContext;
