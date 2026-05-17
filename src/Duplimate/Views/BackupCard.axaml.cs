using Avalonia.Controls;

namespace Duplimate.Views;

public partial class BackupCard : UserControl
{
    // The footer's wrap-detection logic is gone with the redesign:
    // the verify lane and the action shelf are now stacked rows by
    // construction (Grid.Row=2 and Grid.Row=3), each with its own
    // hairline rule above it. There's no longer a single WrapPanel
    // mixing verify + buttons that could wrap mid-row, so no
    // dangling-vertical-separator case to handle.
    public BackupCard()
    {
        InitializeComponent();
    }
}
