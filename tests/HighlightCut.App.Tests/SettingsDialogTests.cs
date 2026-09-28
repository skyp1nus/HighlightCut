using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HighlightCut.App.Demo;
using HighlightCut.App.ViewModels;
using HighlightCut.App.Views;
using HighlightCut.App.Views.Settings;

namespace HighlightCut.App.Tests;

/// <summary>The settings dialog's sections, and the design screens that open it.</summary>
public class SettingsDialogTests
{
    private static (MainWindow Window, EditorViewModel Editor) Open(DesignScreen screen)
    {
        var editor = App.CreateEditor(screen);
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        Pump();
        return (window, editor);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    public static TheoryData<DesignScreen> AllScreens => [.. Enum.GetValues<DesignScreen>()];

    [Theory]
    [InlineData("claude-export-failed", DesignScreen.ClaudeExportFailed)]
    [InlineData("settings-keyboard-recording", DesignScreen.SettingsKeyboardRecording)]
    [InlineData("no-model", DesignScreen.NoModel)]
    [InlineData("SettingsMcp", DesignScreen.SettingsMcp)]
    [InlineData("settingsgeneral", DesignScreen.SettingsGeneral)]
    [InlineData("settings", DesignScreen.Settings)]
    [InlineData("nope", DesignScreen.Editing)]
    [InlineData("99", DesignScreen.Editing)]
    public void Demo_screens_are_named_in_any_case_or_kebab_case(string name, DesignScreen screen) =>
        Assert.Equal(screen, App.ParseDemoScreen(["--demo", name]));

    [Fact]
    public void Demo_without_a_screen_is_the_editing_screen_and_without_the_flag_none()
    {
        Assert.Equal(DesignScreen.Editing, App.ParseDemoScreen(["--demo"]));
        Assert.Null(App.ParseDemoScreen(["talk.mp4"]));
    }

    [AvaloniaFact]
    public void Each_section_has_its_own_flag_and_subtitle()
    {
        var settings = App.CreateEditor(null).Settings;
        Assert.Equal("Transcription", settings.Section);
        var notes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var option in settings.SectionOptions)
        {
            option.PickCommand.Execute(null);
            bool[] flags = [settings.IsGeneral, settings.IsPlayback, settings.IsExport, settings.IsTranscription, settings.IsKeyboard, settings.IsMcp];

            Assert.Equal(option.Label, settings.Section);
            Assert.Equal(SettingsViewModel.Sections.ToList().IndexOf(option.Label), Array.IndexOf(flags, true));
            Assert.Single(flags, f => f);
            Assert.Single(settings.SectionOptions, o => o.IsSelected);
            Assert.True(notes.Add(settings.SectionNote));
        }
        Assert.Equal("Lets Claude Desktop and Claude Code open videos and edit the timeline in this window.", settings.SectionNote);
    }

    [AvaloniaTheory]
    [InlineData(DesignScreen.Settings, "Transcription")]
    [InlineData(DesignScreen.SettingsGeneral, "General")]
    [InlineData(DesignScreen.SettingsPlayback, "Playback")]
    [InlineData(DesignScreen.SettingsExport, "Export")]
    [InlineData(DesignScreen.SettingsKeyboard, "Keyboard")]
    [InlineData(DesignScreen.SettingsKeyboardRecording, "Keyboard")]
    [InlineData(DesignScreen.SettingsKeyboardConflict, "Keyboard")]
    [InlineData(DesignScreen.SettingsMcp, "MCP server")]
    public void Settings_screens_open_on_their_section_over_the_editing_screen(DesignScreen screen, string section)
    {
        var editor = App.CreateEditor(screen);

        Assert.True(editor.Settings.IsOpen);
        Assert.Equal(section, editor.Settings.Section);
        Assert.Equal(2, editor.SelectedClip?.Id);
        Assert.Equal(151.066, editor.Time, 3);
    }

    [AvaloniaTheory]
    [MemberData(nameof(AllScreens))]
    public void Every_screen_renders_and_only_settings_screens_show_the_dialog(DesignScreen screen)
    {
        var (window, editor) = Open(screen);

        Assert.Equal(DemoScenario.SettingsSection(screen) is not null, editor.Settings.IsOpen);
        if (!editor.Settings.IsOpen)
            Assert.Equal("Transcription", editor.Settings.Section);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(new PixelSize(1440, 900), frame!.PixelSize);
        window.Close();
    }

    [AvaloniaFact]
    public void Only_the_chosen_section_is_shown()
    {
        var (window, editor) = Open(DesignScreen.SettingsMcp);
        var dialog = window.GetVisualDescendants().OfType<SettingsDialog>().Single();
        UserControl[] sections =
        [
            dialog.GetVisualDescendants().OfType<GeneralSection>().Single(),
            dialog.GetVisualDescendants().OfType<PlaybackSection>().Single(),
            dialog.GetVisualDescendants().OfType<ExportSection>().Single(),
            dialog.GetVisualDescendants().OfType<TranscriptionSection>().Single(),
            dialog.GetVisualDescendants().OfType<KeyboardSection>().Single(),
            dialog.GetVisualDescendants().OfType<McpSection>().Single(),
        ];

        foreach (var option in editor.Settings.SectionOptions)
        {
            option.PickCommand.Execute(null);
            Pump();
            int shown = SettingsViewModel.Sections.ToList().IndexOf(option.Label);
            for (int i = 0; i < sections.Length; i++)
                Assert.Equal(i == shown, sections[i].IsEffectivelyVisible);
        }
        window.Close();
    }

    [AvaloniaFact]
    public void Switching_sections_starts_the_new_one_at_its_top()
    {
        var (window, editor) = Open(DesignScreen.Settings);
        var scroll = window.GetVisualDescendants().OfType<SettingsDialog>().Single()
            .GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "Scroll");
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        Pump();
        Assert.True(scroll.Offset.Y > 0);

        editor.Settings.SectionOptions.Single(o => o.Label == "MCP server").PickCommand.Execute(null);
        Pump();

        Assert.Equal(0, scroll.Offset.Y);
        window.Close();
    }
}
