using System.Text;
using HighlightCut.Core.Transcripts;

namespace HighlightCut.Transcription;

/// <summary>Turns a model's subword tokens (a leading space starts a word) into words with times.</summary>
public static class WordBuilder
{
    /// <summary>How long the last token of a chunk is taken to last when nothing follows it.</summary>
    public const double LastTokenLength = 0.3;

    /// <summary>A gap longer than this after a word's expected length counts as a pause.</summary>
    public const double PauseAfter = 0.3;

    /// <summary>The words of tokens that come without times (e.g. Whisper models exported without attention outputs).</summary>
    public static List<string> Texts(IReadOnlyList<string> tokens)
    {
        float[] zero = new float[tokens.Count];
        return [.. Build(tokens, zero, 0, 0).Select(w => w.Text)];
    }

    /// <summary>
    /// Gives untimed words times from the audio: the speech in <paramref name="samples"/> (16 kHz; stretches louder
    /// than the background, joined across gaps under 0.3 s) is shared out among the words in proportion to their
    /// length, in order.
    /// </summary>
    public static List<Word> Estimate(IReadOnlyList<string> words, ReadOnlySpan<float> samples, double offset)
    {
        const int Rate = 16000, Frame = Rate / 100;
        int frames = samples.Length / Frame;
        if (words.Count == 0 || frames == 0)
            return [];
        var db = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = f * Frame; i < (f + 1) * Frame; i++)
                sum += samples[i] * samples[i];
            db[f] = 10 * Math.Log10(sum / Frame + 1e-12);
        }
        double floor = db.Order().ElementAt(frames / 10);
        double threshold = Math.Max(floor + 10, -50);
        var speech = new List<(int Start, int End)>();
        for (int f = 0; f < frames; f++)
        {
            if (db[f] < threshold)
                continue;
            if (speech.Count > 0 && f - speech[^1].End <= 30)
                speech[^1] = (speech[^1].Start, f + 1);
            else
                speech.Add((f, f + 1));
        }
        if (speech.Count == 0)
            speech.Add((0, frames));
        int total = speech.Sum(s => s.End - s.Start);
        double weightSum = words.Sum(w => w.Length + 1.0);
        var timed = new List<Word>(words.Count);
        double done = 0;
        foreach (string w in words)
        {
            double from = done / weightSum * total, to = (done + w.Length + 1) / weightSum * total;
            timed.Add(new Word(w, offset + At(from) / 100, offset + At(to) / 100));
            done += w.Length + 1;
        }
        return timed;

        // Frame on the audio at a position counted in speech frames only.
        double At(double position)
        {
            foreach (var (start, end) in speech)
            {
                if (position <= end - start)
                    return start + position;
                position -= end - start;
            }
            return speech[^1].End;
        }
    }

    /// <param name="tokens">Tokens as the model gives them; a word starts with a space (or "▁").</param>
    /// <param name="starts">Start of each token in seconds, relative to <paramref name="offset"/>.</param>
    /// <param name="offset">Where the decoded audio starts on the source timeline.</param>
    /// <param name="end">Where the decoded audio ends (bounds the last word).</param>
    public static List<Word> Build(IReadOnlyList<string> tokens, IReadOnlyList<float> starts, double offset, double end)
    {
        var words = new List<Word>();
        var text = new StringBuilder();
        double wordStart = 0;
        int count = Math.Min(tokens.Count, starts.Count);
        for (int i = 0; i < count; i++)
        {
            string token = tokens[i].Replace('▁', ' ');
            bool startsWord = token.StartsWith(' ') || text.Length == 0;
            bool punctuation = token.Trim().Length > 0 && token.Trim().All(c => char.IsPunctuation(c));
            if (startsWord && !punctuation && text.Length > 0)
            {
                words.Add(new Word(text.ToString(), wordStart, Math.Max(wordStart, offset + starts[i])));
                text.Clear();
            }
            if (text.Length == 0)
                wordStart = offset + starts[i];
            text.Append(token.Trim());
        }
        if (text.Length > 0)
            words.Add(new Word(text.ToString(), wordStart, Math.Min(end, Math.Max(wordStart, offset + starts[count - 1]) + LastTokenLength)));
        // A word ends where the next one starts, unless a pause follows it: then it ends after about as long as
        // it takes to say (the models give start times only), so it does not stretch over the pause.
        for (int i = 0; i < words.Count - 1; i++)
        {
            var w = words[i];
            double spoken = Math.Max(LastTokenLength, (w.Text.Length + 1) * 0.09);
            if (w.End - w.Start > spoken + PauseAfter)
                words[i] = w with { End = w.Start + spoken };
        }
        return words;
    }
}
