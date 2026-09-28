using HighlightCut.App.Services;

namespace HighlightCut.App.Tests;

/// <summary>Moving the app's folder from its old name (OurCut) on the first start after the rename.</summary>
public sealed class AppDataFolderTests : IDisposable
{
    // Stands in for %LOCALAPPDATA%.
    private readonly string _dir = Directory.CreateTempSubdirectory("highlightcut-appdata").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Old(params string[] parts) => Path.Combine([_dir, AppDataFolder.LegacyName, .. parts]);
    private string New(params string[] parts) => Path.Combine([_dir, AppDataFolder.Name, .. parts]);

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void The_old_folder_moves_whole_when_there_is_no_new_one()
    {
        Write(Old("settings.json"), "{}");
        Write(Old("recent.json"), "[]");
        Write(Old("models", "whisper-base.en", "model.onnx"), "weights");
        Write(Old("cache", "abc", "keyframes.bin"), "k");
        Write(Old("logs", "ourcut-20260901.log"), "log");

        var result = AppDataFolder.MoveLegacy(_dir);

        Assert.True(result.Found);
        Assert.Equal([Old()], result.Moved);
        Assert.Empty(result.Failed);
        Assert.False(Directory.Exists(Old()));
        Assert.Equal("{}", File.ReadAllText(New("settings.json")));
        Assert.Equal("[]", File.ReadAllText(New("recent.json")));
        Assert.Equal("weights", File.ReadAllText(New("models", "whisper-base.en", "model.onnx")));
        Assert.True(File.Exists(New("cache", "abc", "keyframes.bin")));
        Assert.True(File.Exists(New("logs", "ourcut-20260901.log")));
        Assert.True(File.Exists(New(AppDataFolder.MovedMarker)));
        Assert.Equal(New("settings.json"), AppDataFolder.PathFor(_dir, "settings.json"));
    }

    [Fact]
    public void Nothing_in_the_new_folder_is_overwritten()
    {
        Write(Old("settings.json"), "old");
        Write(Old("recent.json"), "[]");
        Write(Old("models", "whisper-base.en", "model.onnx"), "old weights");
        Write(Old("models", "parakeet", "model.onnx"), "parakeet");
        Write(New("settings.json"), "new");
        Write(New("models", "whisper-base.en", "model.onnx"), "new weights");

        var result = AppDataFolder.MoveLegacy(_dir);

        Assert.Equal("new", File.ReadAllText(New("settings.json")));
        Assert.Equal("new weights", File.ReadAllText(New("models", "whisper-base.en", "model.onnx")));
        // What only the old folder had moves over: the recent list and the other model.
        Assert.Equal("[]", File.ReadAllText(New("recent.json")));
        Assert.Equal("parakeet", File.ReadAllText(New("models", "parakeet", "model.onnx")));
        // The copies the new folder already had stay where they were.
        Assert.Equal("old", File.ReadAllText(Old("settings.json")));
        Assert.Equal("old weights", File.ReadAllText(Old("models", "whisper-base.en", "model.onnx")));
        Assert.Equal([Old("models", "whisper-base.en"), Old("settings.json")], result.Kept.Order());
        Assert.Empty(result.Failed);
    }

    [Fact]
    public void No_old_folder_means_nothing_to_do()
    {
        var result = AppDataFolder.MoveLegacy(_dir);

        Assert.False(result.Found);
        Assert.False(Directory.Exists(New()));
    }

    [Fact]
    public void What_could_not_be_moved_is_still_read_from_the_old_folder()
    {
        // A file that is in use on Windows stays where it is; the app keeps using it there.
        Write(Old("settings.json"), "{}");
        Directory.CreateDirectory(New("logs"));

        Assert.Equal(Old("settings.json"), AppDataFolder.PathFor(_dir, "settings.json"));
        Assert.Equal(New("logs"), AppDataFolder.PathFor(_dir, "logs"));
        Assert.Equal(New("recent.json"), AppDataFolder.PathFor(_dir, "recent.json"));
    }

    [Fact]
    public void A_models_folder_the_user_chose_stays_unless_it_was_inside_the_old_folder()
    {
        string chosen = Path.Combine(_dir, "D", "models");
        Write(Old("my-models", "a", "model.onnx"), "a");
        AppDataFolder.MoveLegacy(_dir);

        Assert.Equal(chosen, AppDataFolder.Relocate(_dir, chosen));
        Assert.Equal(New("my-models"), AppDataFolder.Relocate(_dir, Old("my-models")));
    }
}
