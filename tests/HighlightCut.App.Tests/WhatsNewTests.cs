using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HighlightCut.App.Demo;
using HighlightCut.App.Services;
using HighlightCut.App.ViewModels;
using HighlightCut.App.Views;

namespace HighlightCut.App.Tests;

/// <summary>What opens at start (the tour for a new user, What's new after an update, or nothing), and What's new itself.</summary>
public sealed class WhatsNewTests : IDisposable
{
    private const string Notes = """
        ## Unreleased

        ## 0.3.0 — 2026-12-01 — Chapters 📖

        ### New

        - **Chapters.** In the export.

        ## 0.2.0 — 2026-11-01

        ### Fixed

        - Undo after a split.

        ## 0.1.0 — 2026-09-30

        ### New

        - The first version.
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("highlightcut-whats-new").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string SettingsFile => Path.Combine(_dir, "settings.json");

    /// <summary>The editor as App starts it: its settings from the file (if there is one), version 0.3.0 with <see cref="Notes"/>.</summary>
    private EditorViewModel Start(AppSettings? saved, out AppSettingsStore store, string version = "0.3.0")
    {
        if (saved is not null)
            new AppSettingsStore(SettingsFile).Save(saved);
        store = new AppSettingsStore(SettingsFile);
        var editor = App.CreateEditor(null);
        editor.Settings.Load(store.Load());
        editor.Settings.Store = store;
        editor.WhatsNew.Changelog = Changelog.Parse(Notes);
        editor.WhatsNew.CurrentVersion = version;
        return editor;
    }

    private AppSettings Saved => new AppSettingsStore(SettingsFile).Load();

    [AvaloniaFact]
    public void A_new_user_gets_the_tour_and_this_version_counts_as_seen()
    {
        var editor = Start(null, out var store);
        Assert.False(store.Existed);

        Assert.Equal(StartDialog.Tour, editor.WhatsNew.OpenAtStart(null, store.Existed));

        Assert.True(editor.Tour.IsOpen);
        Assert.False(editor.WhatsNew.IsOpen);
        Assert.Equal("0.3.0", Saved.LastSeenVersion);

        // The next start: the same version, the tour already seen. Nothing.
        editor.Tour.Close();
        var next = Start(null, out store);
        Assert.True(store.Existed);
        Assert.Equal(StartDialog.None, next.WhatsNew.OpenAtStart(null, store.Existed));
    }

    [AvaloniaFact]
    public void A_new_user_with_a_file_gets_the_tour_the_next_time()
    {
        var editor = Start(null, out var store);
        Assert.Equal(StartDialog.None, editor.WhatsNew.OpenAtStart("talk.mp4", store.Existed));
        Assert.Equal("0.3.0", Saved.LastSeenVersion);

        var next = Start(null, out store);
        Assert.Equal(StartDialog.Tour, next.WhatsNew.OpenAtStart(null, store.Existed));
    }

    [AvaloniaFact]
    public void After_an_update_whats_new_lists_every_newer_version_and_counts_as_the_tour()
    {
        var editor = Start(AppSettings.Default with { LastSeenVersion = "0.1.0" }, out var store);

        Assert.Equal(StartDialog.WhatsNew, editor.WhatsNew.OpenAtStart(null, store.Existed));

        var whatsNew = editor.WhatsNew;
        Assert.True(whatsNew.IsOpen);
        Assert.False(editor.Tour.IsOpen);
        Assert.Equal("What’s new in HighlightCut 0.3.0 · Chapters", whatsNew.Title);
        Assert.Equal(["0.3.0 — 2026-12-01 — Chapters", "0.2.0 — 2026-11-01"], whatsNew.Releases.Select(r => r.Label));
        Assert.True(Saved.WelcomeTourSeen);
        Assert.Equal("0.1.0", Saved.LastSeenVersion);

        whatsNew.CloseCommand.Execute(null);
        Assert.False(whatsNew.IsOpen);
        Assert.Equal("0.3.0", Saved.LastSeenVersion);
    }

    [AvaloniaFact]
    public void Without_a_last_seen_version_only_this_versions_notes_are_shown()
    {
        // Settings from a build before versions, before the tour.
        var editor = Start(AppSettings.Default, out var store);

        Assert.Equal(StartDialog.WhatsNew, editor.WhatsNew.OpenAtStart(null, store.Existed));

        Assert.Equal([new Version(0, 3, 0)], editor.WhatsNew.Releases.Select(r => r.Version));
        Assert.False(editor.Tour.IsOpen);
        Assert.True(editor.Settings.WelcomeTourSeen);
    }

    [AvaloniaFact]
    public void Recent_files_also_mean_someone_used_the_app()
    {
        var editor = Start(null, out var store);
        editor.RecentFiles.Add(new RecentFileViewModel("talk.mp4", "talk.mp4", "", ""));

        Assert.Equal(StartDialog.WhatsNew, editor.WhatsNew.OpenAtStart(null, store.Existed));
    }

    [AvaloniaFact]
    public void It_shows_with_a_file_from_the_command_line_too()
    {
        var editor = Start(AppSettings.Default with { LastSeenVersion = "0.2.0", WelcomeTourSeen = true }, out var store);

        Assert.Equal(StartDialog.WhatsNew, editor.WhatsNew.OpenAtStart("talk.mp4", store.Existed));
        Assert.Equal([new Version(0, 3, 0)], editor.WhatsNew.Releases.Select(r => r.Version));
    }

    [AvaloniaTheory]
    [InlineData("0.3.0", "0.3.0")]
    [InlineData("0.3.0-dev.12", "0.3.0")]
    [InlineData("0.3.0-dev.12", "0.3.0-dev.9")]
    // Started an older build again: nothing either, and the newer version stays saved.
    [InlineData("0.2.0", "0.3.0")]
    public void The_same_version_again_shows_nothing(string version, string seen)
    {
        var editor = Start(AppSettings.Default with { LastSeenVersion = seen, WelcomeTourSeen = true }, out var store, version);

        Assert.Equal(StartDialog.None, editor.WhatsNew.OpenAtStart(null, store.Existed));
        Assert.False(editor.WhatsNew.IsOpen);
        Assert.False(editor.Tour.IsOpen);
        Assert.Equal(seen, Saved.LastSeenVersion);
    }

    [AvaloniaFact]
    public void Never_in_demo_mode()
    {
        var editor = App.CreateEditor(DesignScreen.Empty);
        Assert.Equal(StartDialog.None, editor.WhatsNew.OpenAtStart(null, settingsExisted: true));
        Assert.False(editor.WhatsNew.IsOpen);
        Assert.False(editor.Tour.IsOpen);
    }

    [AvaloniaFact]
    public void Enter_and_esc_close_it_and_other_editor_keys_do_nothing()
    {
        var editor = App.CreateEditor(DesignScreen.WhatsNew);
        Assert.True(editor.WhatsNew.IsOpen);
        Assert.True(editor.HasFile);

        Assert.False(Shortcuts.Handle(editor, Key.E, KeyModifiers.Control));
        Assert.False(editor.Export.IsDialogOpen);
        Assert.False(Shortcuts.Handle(editor, Key.S, KeyModifiers.None));
        Assert.True(Shortcuts.Handle(editor, Key.Enter, KeyModifiers.None));
        Assert.False(editor.WhatsNew.IsOpen);

        editor.WhatsNew.ShowCommand.Execute(null);
        Assert.True(Shortcuts.Handle(editor, Key.Escape, KeyModifiers.None));
        Assert.False(editor.WhatsNew.IsOpen);
    }

    [AvaloniaFact]
    public void Take_the_tour_closes_it_and_opens_the_tour()
    {
        var editor = Start(AppSettings.Default with { LastSeenVersion = "0.2.0" }, out var store);
        editor.WhatsNew.OpenAtStart(null, store.Existed);

        editor.WhatsNew.TakeTourCommand.Execute(null);

        Assert.False(editor.WhatsNew.IsOpen);
        Assert.True(editor.Tour.IsOpen);
        Assert.True(editor.Tour.IsOpenAndCut);
        Assert.Equal("0.3.0", Saved.LastSeenVersion);
    }

    [AvaloniaFact]
    public void The_project_menu_shows_this_versions_notes()
    {
        var editor = Start(AppSettings.Default with { LastSeenVersion = "0.3.0" }, out _);

        editor.WhatsNew.ShowCommand.Execute(null);

        Assert.True(editor.WhatsNew.IsOpen);
        Assert.Equal([new Version(0, 3, 0)], editor.WhatsNew.Releases.Select(r => r.Version));

        // The app's own notes: this version's.
        var app = App.CreateEditor(null);
        app.WhatsNew.ShowCommand.Execute(null);
        Assert.Equal([AppVersion.Release], app.WhatsNew.Releases.Select(r => r.Version));
    }

    [AvaloniaFact]
    public void Long_notes_scroll_and_the_buttons_stay_in_a_small_window()
    {
        var editor = App.CreateEditor(DesignScreen.WhatsNew);
        string many = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"- Improvement number {i}, which says what changed."));
        editor.WhatsNew.Changelog = Changelog.Parse($"## 0.3.0 — 2026-12-01\n\n### Improved\n\n{many}\n");
        editor.WhatsNew.CurrentVersion = "0.3.0";
        editor.WhatsNew.ShowCommand.Execute(null);
        var window = new MainWindow { DataContext = editor, Width = 1100, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var view = window.GetVisualDescendants().OfType<WhatsNewDialog>().Single();
        var notes = view.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "Notes");
        Assert.True(notes.Extent.Height > notes.Viewport.Height);
        var gotIt = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "GotItButton");
        var bottom = gotIt.TranslatePoint(new Point(0, gotIt.Bounds.Height), window);
        Assert.NotNull(bottom);
        Assert.True(bottom!.Value.Y <= 700, $"Got it ends at {bottom.Value.Y}");

        using var frame = window.CaptureRenderedFrame();
        frame!.Save(Path.Combine(Screenshots.Directory, "whats-new-small-window.png"), new PngBitmapEncoderOptions());
        window.Close();
    }

    [AvaloniaFact]
    public void The_bold_lead_of_each_bullet_is_bold_and_the_title_has_the_codename()
    {
        // The app's own notes, as the design screen shows them.
        var editor = App.CreateEditor(DesignScreen.WhatsNew);
        var window = new MainWindow { DataContext = editor, Width = 1280, Height = 860 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var view = window.GetVisualDescendants().OfType<WhatsNewDialog>().Single();
        var title = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "Title");
        var release = Changelog.Embedded.Find(AppVersion.Release)!;
        Assert.Equal($"What’s new in HighlightCut {AppVersion.Release.ToString(3)} · {release.Name}", title.Text);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains('\uFE0F') == true);

        var items = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("item")).ToList();
        Assert.Equal(release.Groups.Sum(g => g.Items.Count), items.Count);
        foreach (var item in items)
        {
            var runs = item.Inlines!.OfType<Run>().ToList();
            Assert.Equal(2, runs.Count);
            Assert.Equal(FontWeight.SemiBold, runs[0].FontWeight);
            Assert.False(string.IsNullOrEmpty(runs[0].Text));
            Assert.DoesNotContain("**", runs[0].Text + runs[1].Text, StringComparison.Ordinal);
        }

        using var frame = window.CaptureRenderedFrame();
        frame!.Save(Path.Combine(Screenshots.Directory, "whats-new.png"), new PngBitmapEncoderOptions());
        window.Close();
    }

    [AvaloniaFact]
    public void Without_a_codename_the_title_is_just_the_version()
    {
        var editor = Start(AppSettings.Default with { LastSeenVersion = "0.1.0" }, out _, "0.2.0");
        Assert.Equal("What’s new in HighlightCut 0.2.0", editor.WhatsNew.Title);
    }

    [Fact]
    public void The_settings_file_says_whether_it_existed_and_keeps_the_last_seen_version()
    {
        var store = new AppSettingsStore(SettingsFile);
        Assert.False(store.Existed);
        store.Save(AppSettings.Default with { LastSeenVersion = "0.1.0" });
        Assert.False(store.Existed);

        Assert.Contains("\"lastSeenVersion\": \"0.1.0\"", File.ReadAllText(SettingsFile), StringComparison.Ordinal);
        var again = new AppSettingsStore(SettingsFile);
        Assert.True(again.Existed);
        Assert.Equal("0.1.0", again.Load().LastSeenVersion);
    }
}
