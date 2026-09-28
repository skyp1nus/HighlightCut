using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OurCut.App.ViewModels;
using OurCut.App.Views;
using OurCut.Core.Model;
using OurCut.Core.Serialization;

namespace OurCut.App.Tests;

/// <summary>Each audio lane's volume: preview, project, export and the lane header.</summary>
public class AudioVolumeTests
{
    private static async Task<(EditorViewModel Editor, FakePlayer Player)> OpenAsync(bool immediate = true)
    {
        var player = new FakePlayer();
        var editor = App.CreateEditor(null, new FakeMediaOpener(), player: player);
        if (immediate)
            editor.VolumeApplyDelay = TimeSpan.Zero;
        await editor.OpenMediaAsync("/videos/fake.mp4");
        Dispatcher.UIThread.RunJobs();
        player.Calls.Clear();
        return (editor, player);
    }

    [AvaloniaFact]
    public async Task A_volume_change_reaches_the_player_and_the_project_but_not_the_undo_history()
    {
        var (editor, player) = await OpenAsync();

        editor.AudioLanes[1].GainDb = -6;

        Assert.Equal(["tracks 11 gains 0,-6"], player.Calls);
        Assert.Equal(new TrackMix(2, -6), editor.Session.Project.MixOf(2));
        Assert.True(editor.IsDirty);
        Assert.False(editor.CanUndo);
    }

    [AvaloniaFact]
    public async Task Mute_is_stored_in_the_project_too()
    {
        var (editor, player) = await OpenAsync();
        editor.AudioLanes[1].GainDb = -6;
        player.Calls.Clear();

        editor.AudioLanes[0].ToggleMuteCommand.Execute(null);

        Assert.Equal(["tracks 01 gains 0,-6"], player.Calls);
        Assert.True(editor.Session.Project.MixOf(1).IsMuted);
    }

    [AvaloniaFact]
    public async Task A_slider_drag_reaches_the_player_a_few_times_a_second_not_on_every_step()
    {
        var (editor, player) = await OpenAsync(immediate: false);
        editor.VolumeApplyDelay = TimeSpan.FromMilliseconds(30);

        foreach (double gain in (double[])[-1, -2, -3, -4])
            editor.AudioLanes[0].GainDb = gain;
        Assert.Empty(player.Calls);

        for (int i = 0; i < 50 && player.Calls.Count == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Equal(["tracks 11 gains -4,0"], player.Calls);
    }

    [AvaloniaFact]
    public async Task Reopening_a_saved_project_restores_volume_and_mute()
    {
        string dir = Directory.CreateTempSubdirectory("ourcut-volume").FullName;
        try
        {
            string path = Path.Combine(dir, "fake" + ProjectFile.Extension);
            var (editor, _) = await OpenAsync();
            editor.AudioLanes[0].GainDb = 4.5;
            editor.AudioLanes[1].IsMuted = true;
            Assert.Null(await editor.SaveToAsync(path, auto: false));

            var player = new FakePlayer();
            var other = App.CreateEditor(null, new FakeMediaOpener(), player: player);
            await other.OpenProjectFileAsync(path);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([(4.5, false), (0.0, true)], other.AudioLanes.Select(l => (l.GainDb, l.IsMuted)));
            Assert.Contains("tracks 10 gains 4.5,0", player.Calls);
            Assert.False(other.IsDirty);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [AvaloniaFact]
    public async Task Even_out_brings_the_tracks_to_about_the_same_loudness()
    {
        var (editor, _) = await OpenAsync();

        Assert.True(editor.EvenOutVolumesCommand.CanExecute(null));
        editor.EvenOutVolumesCommand.Execute(null);

        // Mic at −30 dB and Music at −10 dB meet at −20 dB.
        Assert.Equal([10.0, -10.0], editor.AudioLanes.Select(l => l.GainDb));
        Assert.Equal("Evened out: Mic +10 dB, Music −10 dB.", editor.StatusMessage);

        editor.AudioLanes[0].IsMuted = true;
        Assert.False(editor.EvenOutVolumesCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task The_export_changes_the_volume_of_the_tracks_that_have_one()
    {
        var (editor, _) = await OpenAsync();
        editor.AudioLanes[1].GainDb = -3;

        var settings = editor.Export.BuildSettings();

        Assert.Equal(new Dictionary<int, double> { [2] = -3 }, settings.AudioGainsDb);
        Assert.Contains("re-encoded (AAC 192 kb/s); the video is still copied", editor.Export.SnapNote, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Each_lane_header_has_a_volume_slider_with_its_value()
    {
        var (editor, _) = await OpenAsync();
        editor.AudioLanes[1].GainDb = -6;
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var panel = window.GetVisualDescendants().OfType<TimelinePanel>().Single();
        var sliders = panel.GetVisualDescendants().OfType<Slider>().Where(s => s.Classes.Contains("gain")).ToList();
        Assert.Equal([0.0, -6.0], sliders.Select(s => s.Value));
        var values = panel.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("gain")).Select(t => t.Text);
        Assert.Equal(["0", "−6"], values);
        window.Close();
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(3, "+3")]
    [InlineData(-6.5, "−6.5")]
    [InlineData(-0.04, "0")]
    [InlineData(-40, "−∞")]
    public void Volumes_are_shown_compactly(double gainDb, string text) => Assert.Equal(text, AudioLaneViewModel.FormatGain(gainDb));

    [Fact]
    public void The_wheel_moves_in_half_decibels_within_the_range()
    {
        var lane = new AudioLaneViewModel(0, 1, "A1", "Mic") { GainDb = 11.8 };
        lane.Nudge(1);
        Assert.Equal(12, lane.GainDb);
        lane.GainDb = -1.2;
        lane.Nudge(-1);
        Assert.Equal(-1.5, lane.GainDb);
    }
}
