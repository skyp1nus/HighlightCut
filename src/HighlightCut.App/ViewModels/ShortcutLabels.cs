using CommunityToolkit.Mvvm.ComponentModel;

namespace HighlightCut.App.ViewModels;

/// <summary>
/// The keys Settings → Keyboard gives the editor's actions, as the hints show them (the status bar, the empty screen,
/// the mark buttons, the Jump chips): each action's first key, or "—" when it has none.
/// </summary>
public sealed class ShortcutLabels : ObservableObject
{
    private static readonly string[] Properties =
    [
        nameof(PlayPause), nameof(SetIn), nameof(SetOut), nameof(InOut), nameof(FrameStep), nameof(DeleteClip), nameof(Undo),
        nameof(OpenVideo), nameof(JumpBack), nameof(JumpForward),
    ];

    private readonly KeyMap _map;

    public ShortcutLabels(KeyMap map)
    {
        _map = map;
        map.Changed += (_, _) =>
        {
            foreach (string name in Properties)
                OnPropertyChanged(name);
        };
    }

    public string PlayPause => Label(ShortcutAction.PlayPause);
    public string SetIn => Label(ShortcutAction.SetIn);
    public string SetOut => Label(ShortcutAction.SetOut);

    /// <summary>"I / O".</summary>
    public string InOut => $"{SetIn} / {SetOut}";

    /// <summary>"← / →".</summary>
    public string FrameStep => $"{Label(ShortcutAction.PreviousFrame)} / {Label(ShortcutAction.NextFrame)}";

    public string DeleteClip => Label(ShortcutAction.DeleteClip);
    public string Undo => Label(ShortcutAction.Undo);
    public string OpenVideo => Label(ShortcutAction.OpenVideo);
    public string JumpBack => Label(ShortcutAction.JumpBack);
    public string JumpForward => Label(ShortcutAction.JumpForward);

    public string Label(ShortcutAction action) => _map[action] is [var first, ..] ? first.Label : "—";
}
