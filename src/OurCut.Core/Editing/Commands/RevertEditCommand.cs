using System.Collections.Immutable;
using OurCut.Core.Model;

namespace OurCut.Core.Editing.Commands;

/// <summary>
/// Reverts one earlier edit without undoing the edits made after it ("Undo" on a single action in the
/// Claude panel). The clips that edit added, removed, changed or reordered are put back the way they were
/// before it; every other clip stays as it is now. This is a new edit, so it can itself be undone.
/// </summary>
/// <param name="Before">The project before the edit being reverted.</param>
/// <param name="After">The project right after that edit.</param>
/// <param name="Description">What the reverted edit was, e.g. "Removed 3 silences".</param>
/// <remarks>
/// Fails with <see cref="EditException"/> when a later edit changed one of the same clips, since there is
/// then no single right answer; undo the later edit first.
/// </remarks>
public sealed record RevertEditCommand(Project Before, Project After, string Description) : IEditCommand
{
    public string Name => "revert_action";

    /// <summary>A command that reverts <paramref name="entry"/>.</summary>
    public static RevertEditCommand For(HistoryEntry entry) => new(entry.Before, entry.After, entry.Description);

    /// <summary>Whether this command reverts <paramref name="entry"/>.</summary>
    public bool Reverts(HistoryEntry entry) => ReferenceEquals(Before, entry.Before) && ReferenceEquals(After, entry.After);

    public string Describe(Project before) => $"Reverted “{Description}”";

    public Project Apply(Project project)
    {
        var added = After.Clips.Where(c => Before.Find(c.Id) is null).ToList();
        var removed = Before.Clips.Where(c => After.Find(c.Id) is null).ToList();
        var changed = After.Clips.Where(c => Before.Find(c.Id) is { } old && old != c).ToList();
        var common = After.Clips.Where(c => Before.Find(c.Id) is not null).Select(c => c.Id).ToList();
        var orderBefore = Before.Clips.Select(c => c.Id).Where(common.Contains).ToList();
        bool reordered = !orderBefore.SequenceEqual(common);

        foreach (var clip in added.Concat(changed))
        {
            if (project.Find(clip.Id) != clip)
                throw Conflict(project, clip.Id);
        }
        foreach (var clip in removed)
        {
            if (project.Find(clip.Id) is not null)
                throw new EditException($"Clip {clip.Id} exists again; it was added back after “{Description}”.");
        }
        if (reordered && !project.Clips.Select(c => c.Id).Where(common.Contains).SequenceEqual(common))
            throw new EditException($"Clips were reordered after “{Description}”. Undo that first.");

        var clips = project.Clips;
        foreach (var clip in added)
            clips = clips.RemoveAll(c => c.Id == clip.Id);
        foreach (var clip in changed)
            clips = clips.Replace(clips.First(c => c.Id == clip.Id), Before.Get(clip.Id));
        if (reordered)
            clips = Reorder(clips, orderBefore);
        foreach (var clip in removed.OrderBy(c => Before.IndexOf(c.Id)))
            clips = clips.Insert(Math.Min(Before.IndexOf(clip.Id), clips.Count), clip);

        if (clips.GroupBy(c => c.Label.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new EditException($"Another clip is called “{twice.Key}” now, so “{Description}” cannot be reverted. Rename it first.");
        return clips.SequenceEqual(project.Clips) ? project : project.WithClips(clips);
    }

    /// <summary>Puts the clips listed in <paramref name="order"/> back in that order, in the slots they occupy now.</summary>
    private static ImmutableList<Clip> Reorder(ImmutableList<Clip> clips, List<int> order)
    {
        var slots = clips.Select((c, i) => (c, i)).Where(x => order.Contains(x.c.Id)).Select(x => x.i).ToList();
        var builder = clips.ToBuilder();
        for (int k = 0; k < slots.Count; k++)
            builder[slots[k]] = clips.First(c => c.Id == order[k]);
        return builder.ToImmutable();
    }

    private EditException Conflict(Project project, int clipId) =>
        project.Find(clipId) is null
            ? new EditException($"Clip {clipId} was removed after “{Description}”, so it cannot be reverted.")
            : new EditException($"Clip {project.NumberOf(clipId)} was changed after “{Description}”. Undo that change first.");
}
