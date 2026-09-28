using HighlightCut.Core.Transcripts;

namespace HighlightCut.App.Demo;

/// <summary>The design's transcript (<c>PARAS</c>, <c>buildTranscript</c>): word times shared out by word length.</summary>
public static class DesignTranscript
{
    public const string ModelId = "parakeet-tdt-0.6b-v3";

    /// <summary>The sample's paragraphs: start, end and what was said.</summary>
    public static IReadOnlyList<(double Start, double End, string Text)> Paragraphs { get; } =
    [
        (1.5, 10.8, "Okay, are we rolling? Good. Let me pull the mic a bit closer."),
        (12.3, 40.4, "So the question I kept asking was why cutting a video should take longer than watching it. That's where HighlightCut started. You open a file, you mark what you want, and you export. Um, no timeline gymnastics, no render queue, just the parts you meant to keep."),
        (45.6, 59.6, "I'd been doing this with ffmpeg on the command line for years, and, like, it works, but you end up typing timestamps by hand."),
        (72.4, 117.8, "A bit about me, uh, before we go on. I edit a lot of long recordings. Talks, interviews, calls with customers. Most of the work is finding the good twenty minutes inside two hours, and the tools I had were built for something else. So I wrote the one I wanted."),
        (124.2, 189.6, "Here's the setup. You drop a video in, and HighlightCut reads it first. It finds the keyframes, the silences and the scene changes, and it draws the waveform. That takes a few seconds for a file this size. Then you press I for in and O for out, and each pair becomes a clip on the right. You can drag clips to change the order, and you can switch one off without deleting it."),
        (190.4, 204.6, "Um, I'll skip the part where I explain the folders."),
        (214.4, 261.6, "The part people ask about most is Claude. HighlightCut runs a small server on your machine, and Claude can talk to it. It can open files, read the silences and the transcript, and edit the timeline. Every edit shows up as a card, and every card has its own undo."),
        (262.4, 299.6, "So let's export. This is lossless mode, so, um, nothing gets re-encoded. Each cut snaps to the keyframe before the in point, and, you know, for most recordings that's a fraction of a second."),
        (306.4, 330.6, "If you need frame accuracy you switch to re-encode, and, uh, it takes longer but it's exact."),
        (337.4, 365.2, "Merged into one file, it took eleven seconds for a four minute cut. Um, so anyway, that's the export."),
        (366.4, 409.6, "What I didn't want was another project format that locks you in. The project file is plain JSON. It lists the source, the clips and their labels, and that's it. If HighlightCut disappeared tomorrow you could still read it."),
        (410.6, 419.6, "Let me show the keyboard side quickly."),
        (470.4, 519.4, "Everything has a shortcut. Space plays, the arrows step one frame, and shift jumps a second. S splits at the playhead, E switches a clip in or out. The whole point is speed, and, uh, your hands never leave the keyboard."),
        (520.4, 547.6, "Someone asked whether it works offline. It does. The transcription runs on your own GPU, and nothing leaves your machine unless you send it somewhere."),
        (548.6, 577.8, "Another question was about long files. I've cut a six hour stream with it. The analysis takes a minute, and after that it's as fast as a short clip."),
        (578.8, 612, "And the last one: why not just use a full editor? Because most days I don't need one. I need to remove the parts nobody should watch, and, um, get on with my day."),
        (613, 689.4, "A few smaller things. It remembers where you stopped in every file. It keeps the audio tracks you choose. And if an export fails halfway, it tells you why instead of leaving a broken file somewhere in your downloads folder."),
        (700.4, 749.4, "We're almost out of time, so, uh, let me wrap up. The download is on the website, it's free for personal use, and the source for the server part is public."),
        (750.4, 821.6, "Thanks for having me. If you try it, tell me what breaks. I read every message, and most of the features in this version came from people who wrote in after the last talk. That's it from me."),
        (829.2, 868, "Is it still recording? Okay. You can stop it now. Thanks, everyone."),
    ];

    /// <summary>The whole transcript.</summary>
    public static Transcript Full { get; } = Build();

    /// <summary>The words transcribed by <paramref name="until"/> seconds (the same <see cref="Word"/> instances as <see cref="Full"/>).</summary>
    public static Transcript UpTo(double until) => new(ModelId, "en", [.. Full.Words.Where(w => w.End <= until)]);

    private static Transcript Build()
    {
        var words = new List<Word>();
        foreach (var (ps, pe, text) in Paragraphs)
        {
            string[] tokens = text.Split(' ');
            int[] weights = [.. tokens.Select(t => t.Length + 3)];
            int total = weights.Sum();
            int acc = 0;
            for (int k = 0; k < tokens.Length; k++)
            {
                // Same arithmetic order as the prototype, so the times match it exactly.
                double s = ps + (double)acc / total * (pe - ps);
                double d = (double)weights[k] / total * (pe - ps);
                acc += weights[k];
                words.Add(new Word(tokens[k], s, s + d * 0.88));
            }
        }
        return new Transcript(ModelId, "en", words);
    }
}
