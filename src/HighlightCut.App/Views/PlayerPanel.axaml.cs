using Avalonia.Controls;

namespace HighlightCut.App.Views;

public partial class PlayerPanel : UserControl
{
    public PlayerPanel() => InitializeComponent();

    /// <summary>Highlights the drop zone while a file is dragged over the window.</summary>
    public void SetDropHover(bool over) => DropZone.Classes.Set("over", over);
}
