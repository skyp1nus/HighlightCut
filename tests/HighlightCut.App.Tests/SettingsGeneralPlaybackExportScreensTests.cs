using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HighlightCut.App.Demo;
using HighlightCut.App.ViewModels;
using HighlightCut.App.Views;

namespace HighlightCut.App.Tests;

/// <summary>Screenshots of Settings → General, Playback and Export (artifacts/screenshots/), for comparison with the design.</summary>
public class SettingsGeneralPlaybackExportScreensTests
{
    private static (MainWindow Window, EditorViewModel Editor) Open(DesignScreen screen)
    {
        var editor = App.CreateEditor(screen);
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        Render();
        return (window, editor);
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static ScrollViewer Scroll(Window window) =>
        window.GetVisualDescendants().OfType<SettingsDialog>().Single().GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "Scroll");

    private static void Save(Window window, string name)
    {
        Render();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(new PixelSize(1440, 900), frame!.PixelSize);
        frame.Save(Path.Combine(Screenshots.Directory, name + ".png"), new PngBitmapEncoderOptions());
    }

    [AvaloniaTheory]
    [InlineData(DesignScreen.SettingsGeneral)]
    [InlineData(DesignScreen.SettingsPlayback)]
    [InlineData(DesignScreen.SettingsExport)]
    public void Settings_screen_opens_its_section(DesignScreen screen)
    {
        var (window, editor) = Open(screen);

        Assert.True(editor.Settings.IsOpen);
        Assert.Equal(DemoScenario.SettingsSection(screen), editor.Settings.Section);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(DesignScreen.SettingsGeneral)]
    [InlineData(DesignScreen.SettingsExport)]
    public void The_bottom_of_a_long_section_renders(DesignScreen screen)
    {
        var (window, _) = Open(screen);
        var scroll = Scroll(window);
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);

        scroll.ScrollToEnd();
        Save(window, Screenshots.Name(screen) + "-bottom");
        window.Close();
    }

    [AvaloniaFact]
    public void Playback_fits_without_scrolling()
    {
        var (window, _) = Open(DesignScreen.SettingsPlayback);
        var scroll = Scroll(window);
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height);
        window.Close();
    }

    [AvaloniaFact]
    public void A_fixed_folder_and_re_encoding_render()
    {
        var (window, editor) = Open(DesignScreen.SettingsExport);
        editor.Settings.ExportFolderOptions.Single(o => o.Label == "Fixed folder").PickCommand.Execute(null);
        editor.Settings.ExportModeOptions.Single(o => o.Label == "Re-encode").PickCommand.Execute(null);
        Render();

        Scroll(window).Offset = new Vector(0, 40);
        Save(window, "settings-export-fixed-folder");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Cleared_cache_and_list_and_copied_diagnostics_render()
    {
        var (window, editor) = Open(DesignScreen.SettingsGeneral);
        editor.Settings.ClearCacheCommand.Execute(null);
        editor.Settings.ClearRecentFilesCommand.Execute(null);
        await editor.Settings.CopyDiagnosticsCommand.ExecuteAsync(null);

        Scroll(window).ScrollToEnd();
        Save(window, "settings-general-cleared");
        window.Close();
    }
}
