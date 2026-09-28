using OurCut.Core.Model;
using OurCut.Media.Export;
using static OurCut.Media.Tests.Export.ExportSample;

namespace OurCut.Media.Tests.Export;

public class ExportPlannerTests
{
    [Fact]
    public void Lossless_merge_cuts_each_clip_to_a_temp_file_then_concatenates()
    {
        var plan = Plan(Settings());

        Assert.Equal([Out("demo-cut.mp4")], plan.Outputs);
        Assert.Equal([ExportStepKind.Cut, ExportStepKind.Cut, ExportStepKind.Concat], plan.Steps.Select(s => s.Kind));
        Assert.Equal(Out(".ourcut-tmp-t1-001.mp4"), plan.Steps[0].OutputPath);
        Assert.Equal(Out(".ourcut-tmp-t1-002.mp4"), plan.Steps[1].OutputPath);
        Assert.True(plan.Steps[0].IsTemporary);
        Assert.False(plan.Steps[2].IsTemporary);
        Assert.Equal(Out("demo-cut.mp4"), plan.Steps[2].OutputPath);
        Assert.Equal(Out(".ourcut-tmp-t1.ffconcat"), plan.ConcatListPath);
        Assert.Equal(Out(".ourcut-tmp-t1.ffmeta"), plan.ChaptersPath);
        Assert.Equal(
            [Out(".ourcut-tmp-t1-001.mp4"), Out(".ourcut-tmp-t1-002.mp4"), Out(".ourcut-tmp-t1.ffconcat"), Out(".ourcut-tmp-t1.ffmeta")],
            plan.TemporaryFiles);
    }

    [Fact]
    public void Excluded_clips_are_not_exported() =>
        Assert.Equal([1, 2], Plan(Settings()).Clips.Select(c => c.ClipId));

    [Fact]
    public void Lossless_clips_start_at_the_previous_keyframe()
    {
        var plan = Plan(Settings());
        Assert.Equal(1.0, plan.Clips[0].OutputStart);
        Assert.Equal(2.2, plan.Clips[0].OutputDuration, 9);
        Assert.Equal(5.0, plan.Clips[1].OutputStart);
        Assert.Equal(4.2, plan.OutputDuration, 9);
    }

    [Fact]
    public void Step_weights_add_up_to_one()
    {
        foreach (var settings in new[]
                 {
                     Settings(), Settings(merge: false), Settings(CutMode.Reencode), Settings(CutMode.Reencode, merge: false),
                 })
        {
            Assert.Equal(1.0, Plan(settings).Steps.Sum(s => s.Weight), 9);
        }
    }

    [Fact]
    public void Cut_weights_follow_clip_duration()
    {
        var steps = Plan(Settings()).Steps;
        Assert.Equal((1 - ExportPlanner.ConcatWeight) * 2.2 / 4.2, steps[0].Weight, 9);
        Assert.Equal(ExportPlanner.ConcatWeight, steps[2].Weight, 9);
    }

    [Fact]
    public void Separate_files_are_numbered_by_the_default_pattern()
    {
        var plan = Plan(Settings(merge: false));
        Assert.Equal([Out("demo-cut-01.mp4"), Out("demo-cut-02.mp4")], plan.Outputs);
        Assert.All(plan.Steps, s => Assert.Equal(ExportStepKind.Cut, s.Kind));
        Assert.All(plan.Steps, s => Assert.False(s.IsTemporary));
        Assert.Null(plan.ConcatListPath);
        Assert.Null(plan.ChaptersPath);
        Assert.Empty(plan.TemporaryFiles);
    }

    [Fact]
    public void Reencoded_merge_is_one_step_with_exact_cut_points()
    {
        var plan = Plan(Settings(CutMode.Reencode));
        var step = Assert.Single(plan.Steps);
        Assert.Equal(ExportStepKind.EncodeMerged, step.Kind);
        Assert.Equal(1, step.Weight);
        Assert.All(plan.Clips, c => Assert.Null(c.Lossless));
        Assert.Equal(1.5, plan.Clips[0].OutputStart);
        Assert.Equal(3.7, plan.OutputDuration, 9);
        Assert.Null(plan.ConcatListPath);
        Assert.Equal(Out(".ourcut-tmp-t1.ffmeta"), plan.ChaptersPath);
    }

    [Fact]
    public void Overlapping_clips_of_an_old_project_are_not_exported_twice()
    {
        // Saved before clips were kept apart: clip 2 repeats 2.5–3.2 of clip 1, clip 3 lies inside clip 1.
        var project = SampleProject with
        {
            Clips = [new Clip(1, "Intro", 1.5, 3.2), new Clip(2, "Demo", 2.5, 6), new Clip(3, "Again", 2, 3)],
        };
        foreach (var settings in new[] { Settings(CutMode.Reencode), Settings(CutMode.Reencode, merge: false), Settings(merge: false) })
        {
            var plan = Plan(settings, project);
            Assert.Equal([(1, 1.5, 3.2), (2, 3.2, 6.0)], plan.Clips.Select(c => (c.ClipId, c.Start, c.End)));
            for (int i = 1; i < plan.Clips.Count; i++)
                Assert.True(plan.Clips[i].Start >= plan.Clips[i - 1].End);
            Assert.Equal(settings.Merge ? 1 : 2, plan.Outputs.Count);
        }
        Assert.Equal(4.5, Plan(Settings(CutMode.Reencode), project).OutputDuration, 9);
    }

    [Fact]
    public void A_lossless_merge_cuts_touching_clips_as_one_so_the_keyframe_lead_in_is_not_repeated()
    {
        var project = SampleProject with
        {
            Clips = [new Clip(1, "Intro", 1.5, 3.2), new Clip(2, "Demo", 3.2, 6), new Clip(3, "Outro", 8, 9)],
        };

        var plan = Plan(Settings(), project);

        // Cut separately, clip 2 would start at the keyframe at 3 s and play 3.0–3.2 again.
        Assert.Equal([(1, 1.5, 6.0), (3, 8.0, 9.0)], plan.Clips.Select(c => (c.ClipId, c.Start, c.End)));
        Assert.Equal(1.0, plan.Clips[0].OutputStart);
        Assert.Equal(5.0 + 1.0, plan.OutputDuration, 9);
        // Separate files are whole files each, so they stay one per clip.
        Assert.Equal(3, Plan(Settings(merge: false), project).Clips.Count);
    }

    [Fact]
    public void Chapters_can_be_turned_off() =>
        Assert.Null(Plan(Settings() with { AddChapters = false }).ChaptersPath);

    [Fact]
    public void Container_decides_the_extension()
    {
        var plan = Plan(Settings() with { Container = OutputContainer.Mkv });
        Assert.Equal([Out("demo-cut.mkv")], plan.Outputs);
        Assert.Equal(Out(".ourcut-tmp-t1-001.mkv"), plan.Steps[0].OutputPath);
    }

    [Fact]
    public void Existing_files_are_not_overwritten()
    {
        var taken = new HashSet<string> { Out("demo-cut.mp4"), Out("demo-cut (2).mp4") };
        Assert.Equal([Out("demo-cut (3).mp4")], Plan(Settings(), fileExists: taken.Contains).Outputs);
    }

    [Fact]
    public void Separate_outputs_never_collide_with_each_other()
    {
        var project = SampleProject with
        {
            Clips = [new Clip(1, "Take", 1, 2), new Clip(2, "Take", 3, 4)],
        };
        var taken = new HashSet<string> { Out("demo-take.mp4") };
        // Without {n} the pattern gives both clips one name.
        var plan = Plan(Settings(merge: false) with { FileNamePattern = "{project}-{label}" }, project, fileExists: taken.Contains);
        Assert.Equal([Out("demo-take (2).mp4"), Out("demo-take (3).mp4")], plan.Outputs);

        var replacing = Plan(Settings(merge: false) with { FileNamePattern = "{project}-{label}", Overwrite = true }, project,
            fileExists: taken.Contains);
        Assert.Equal([Out("demo-take.mp4"), Out("demo-take (2).mp4")], replacing.Outputs);
    }

    [Fact]
    public void Names_follow_the_pattern()
    {
        var settings = Settings(merge: false) with { FileNamePattern = "{date} {label} {n}", Date = new DateOnly(2026, 9, 25) };
        Assert.Equal([Out("2026-09-25 intro 01.mp4"), Out("2026-09-25 demo-import 02.mp4")], Plan(settings).Outputs);
        Assert.Equal(Plan(settings).Outputs, ExportPlanner.OutputNames(SampleProject, settings));

        // A merged file has no number or label.
        var merged = Settings() with { FileNamePattern = "{project}_{n}_{label}_final" };
        Assert.Equal([Out("demo_final.mp4")], Plan(merged).Outputs);
    }

    [Fact]
    public void An_existing_file_is_replaced_only_once_the_new_one_is_written()
    {
        var taken = new HashSet<string> { Out("demo-cut.mp4") };
        var plan = Plan(Settings() with { Overwrite = true }, fileExists: taken.Contains);

        Assert.Equal([Out("demo-cut.mp4")], plan.Outputs);
        var concat = plan.Steps[^1];
        Assert.Equal(Out(".ourcut-tmp-t1-new001.mp4"), concat.OutputPath);
        Assert.Equal(Out("demo-cut.mp4"), plan.Replacements![concat.OutputPath]);
        Assert.Contains(concat.OutputPath, plan.TemporaryFiles);

        // A name that is free is written directly.
        var fresh = Plan(Settings() with { Overwrite = true });
        Assert.Null(fresh.Replacements);
        Assert.Equal(Out("demo-cut.mp4"), fresh.Steps[^1].OutputPath);
    }

    [Fact]
    public void Nothing_included_is_an_error()
    {
        var project = SampleProject with { Clips = [new Clip(1, "A", 1, 2, IsIncluded: false)] };
        Assert.Throws<InvalidOperationException>(() => Plan(Settings(), project));
    }

    [Fact]
    public void Smart_cut_is_not_available_yet() =>
        Assert.Throws<InvalidOperationException>(() => Plan(Settings(CutMode.SmartCut)));

    [Fact]
    public void An_export_never_overwrites_its_source()
    {
        var info = Info with { Path = Out("demo-cut.mp4") };
        var ex = Assert.Throws<InvalidOperationException>(() => Plan(Settings(), info: info));
        Assert.Contains("source", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Temp_ids_are_random_by_default()
    {
        var a = ExportPlanner.Plan(SampleProject, Info, Keyframes, Settings(), _ => false);
        var b = ExportPlanner.Plan(SampleProject, Info, Keyframes, Settings(), _ => false);
        Assert.NotEqual(a.Steps[0].OutputPath, b.Steps[0].OutputPath);
        Assert.StartsWith(".ourcut-tmp-", Path.GetFileName(a.Steps[0].OutputPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Intro", "intro")]
    [InlineData("Demo — import", "demo-import")]
    [InlineData("Q&A", "q-a")]
    [InlineData("  Take 2!  ", "take-2")]
    [InlineData("Вступне слово", "вступне-слово")]
    [InlineData("?!", "clip")]
    [InlineData("", "clip")]
    public void Slug_makes_labels_file_name_friendly(string label, string expected) =>
        Assert.Equal(expected, ExportPlanner.Slug(label));
}
