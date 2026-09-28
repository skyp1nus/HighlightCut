namespace HighlightCut.Transcription.Models;

/// <summary>
/// The models folder: each installed model in a folder named after it. A download in progress (or
/// interrupted, to be resumed) lives in "&lt;id&gt;.partial" and becomes "&lt;id&gt;" only once complete.
/// </summary>
public sealed class ModelStore(string folder)
{
    public string Folder { get; } = folder;

    public string DirectoryOf(TranscriptionModel model) => Path.Combine(Folder, model.Id);

    public string PartialOf(TranscriptionModel model) => Path.Combine(Folder, model.Id + ".partial");

    /// <summary>Every file the model needs is there.</summary>
    public bool IsInstalled(TranscriptionModel model)
    {
        try
        {
            return model.Files.All(f => File.Exists(Path.Combine(DirectoryOf(model), f)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>An interrupted download that can be resumed.</summary>
    public bool HasPartial(TranscriptionModel model) => Directory.Exists(PartialOf(model));

    /// <summary>Bytes the installed model takes.</summary>
    public long SizeOnDisk(TranscriptionModel model)
    {
        string dir = DirectoryOf(model);
        return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
    }

    /// <summary>Removes the model and any unfinished download of it.</summary>
    public void Delete(TranscriptionModel model)
    {
        DeleteDirectory(DirectoryOf(model));
        DeletePartial(model);
    }

    public void DeletePartial(TranscriptionModel model) => DeleteDirectory(PartialOf(model));

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
