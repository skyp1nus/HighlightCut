using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HighlightCut.App.Demo;
using HighlightCut.App.Services;
using HighlightCut.Core.Serialization;

namespace HighlightCut.App.Tests;

public class ProjectFileUiTests
{
    private sealed class FakeDialogs(string path) : IFileDialogs
    {
        public Task<string?> PickMediaToOpenAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickProjectToOpenAsync() => Task.FromResult<string?>(path);
        public Task<string?> PickProjectSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(path);
        public Task<string?> PickFolderAsync(string title, string? startFolder) => Task.FromResult<string?>(null);
    }

    [AvaloniaFact]
    public async Task Save_then_open_restores_the_clips()
    {
        string dir = Directory.CreateTempSubdirectory("highlightcut-ui").FullName;
        try
        {
            string path = Path.Combine(dir, "keynote" + ProjectFile.Extension);
            var editor = App.CreateEditor(null);
            editor.Dialogs = new FakeDialogs(path);
            DemoScenario.OpenSample(editor);
            editor.Select(editor.Clips[0]);
            editor.ToggleExclude();

            await editor.SaveProject();
            Assert.Equal(path, editor.ProjectPath);
            Assert.False(editor.IsDirty);
            Assert.EndsWith("saved", editor.StatusRight, StringComparison.Ordinal);

            var other = App.CreateEditor(null, new SampleOpener());
            await other.OpenProjectFileAsync(path);
            Assert.Equal(editor.Clips.Select(c => (c.Id, c.Label, c.Start, c.End, c.IsIncluded)),
                other.Clips.Select(c => (c.Id, c.Label, c.Start, c.End, c.IsIncluded)));
            Assert.Equal("interview_final_v3 — HighlightCut", other.WindowTitle);
            Assert.False(other.CanUndo);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [AvaloniaFact]
    public async Task A_saved_project_is_autosaved_after_edits()
    {
        string dir = Directory.CreateTempSubdirectory("highlightcut-ui").FullName;
        try
        {
            string path = Path.Combine(dir, "keynote" + ProjectFile.Extension);
            var editor = App.CreateEditor(null);
            DemoScenario.OpenSample(editor);
            await editor.SaveToAsync(path, auto: false);
            editor.AutosaveDelay = TimeSpan.FromMilliseconds(10);

            editor.Select(editor.Clips[1]);
            editor.ToggleExclude();
            Assert.True(editor.IsDirty);

            for (int i = 0; i < 100 && editor.IsDirty; i++)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.False(editor.IsDirty);
            Assert.EndsWith("autosaved", editor.StatusRight, StringComparison.Ordinal);
            var saved = await ProjectFile.LoadAsync(path, TestContext.Current.CancellationToken);
            Assert.False(saved.Get(editor.Clips[1].Id).IsIncluded);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [AvaloniaFact]
    public async Task Opening_a_broken_project_shows_a_message()
    {
        string dir = Directory.CreateTempSubdirectory("highlightcut-ui").FullName;
        try
        {
            string path = Path.Combine(dir, "broken" + ProjectFile.Extension);
            await File.WriteAllTextAsync(path, "{ nope", TestContext.Current.CancellationToken);
            var editor = App.CreateEditor(null, new SampleOpener());
            await editor.OpenProjectFileAsync(path);
            Assert.False(editor.HasFile);
            Assert.StartsWith("Could not open the project", editor.StatusMessage, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
