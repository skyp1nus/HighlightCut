using HighlightCut.Core.Model;

namespace HighlightCut.Core.Editing.Commands;

/// <summary>
/// Adds a video at the end of the timeline. It gets the next free id (see <see cref="Project.LastSourceId"/>), whatever
/// id <paramref name="Source"/> carries. The output order stays the clip list's: a new video has no clips yet.
/// </summary>
/// <param name="Id">Explicit id (e.g. to restore a removed video); by default the next free id.</param>
public sealed record AddSourceCommand(SourceMedia Source, int? Id = null) : IEditCommand
{
    public string Name => "add_video";

    public string Describe(Project before) => $"Added video {before.Sources.Count + 1} ({Source.FileName})";

    public Project Apply(Project project)
    {
        if (string.IsNullOrWhiteSpace(Source.Path))
            throw new EditException("The video has no path.");
        if (!double.IsFinite(Source.Duration) || Source.Duration <= 0)
            throw new EditException($"{Source.FileName} has no length, so it cannot go on the timeline.");
        int id = Id ?? project.NextSourceId;
        if (id < 1)
            throw new EditException($"Video ids start at 1, not {id}.");
        if (project.FindSource(id) is not null)
            throw new EditException($"Video {id} is already in the project.");
        return project.WithSources(project.Sources.Add(Source with { Id = id }));
    }
}

/// <summary>
/// Removes a video from the timeline together with every clip cut from it; undo brings both back. The videos after it
/// move up, their clips with them. A project keeps at least one video.
/// </summary>
public sealed record RemoveSourceCommand(int SourceId) : IEditCommand
{
    public string Name => "remove_video";

    public string Describe(Project before)
    {
        var source = before.GetSource(SourceId);
        int clips = before.ClipsOf(SourceId).Count();
        return $"Removed video {before.SourceNumberOf(SourceId)} ({source.FileName})" +
               (clips == 0 ? "" : $" and its {clips} clip{(clips == 1 ? "" : "s")}");
    }

    public Project Apply(Project project)
    {
        var source = project.GetSource(SourceId);
        if (project.Sources.Count == 1)
            throw new EditException($"{source.FileName} is the project's only video; add another one before removing it.");
        return project.WithSources(project.Sources.Remove(source)).WithClips(project.Clips.RemoveAll(c => c.SourceId == SourceId));
    }
}

/// <summary>
/// Moves a video to another place on the timeline (0-based index). Its clips move with it, since their times are seconds
/// on their video; the output order (the clip list) does not change.
/// </summary>
public sealed record MoveSourceCommand(int SourceId, int ToIndex) : IEditCommand
{
    public string Name => "move_video";

    public string Describe(Project before) => $"Moved video {before.SourceNumberOf(SourceId)} to position {ToIndex + 1}";

    public Project Apply(Project project)
    {
        var source = project.GetSource(SourceId);
        if (ToIndex < 0 || ToIndex >= project.Sources.Count)
            throw new EditException($"Position {ToIndex + 1} is outside the video list (1–{project.Sources.Count}).");
        int from = project.SourceIndexOf(SourceId);
        return from == ToIndex
            ? project
            : project with { Sources = project.Sources.RemoveAt(from).Insert(ToIndex, source) };
    }
}
