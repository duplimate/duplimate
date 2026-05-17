using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Duplimate.ViewModels;

namespace Duplimate.Views;

public partial class BackupsView : UserControl
{
    public BackupsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private BackupsViewModel? _wired;

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        // Detach from the previous VM so a re-template (rare — only when
        // hot-reload swaps the DataContext) doesn't end up double-wired
        // and double-scrolling on a single chip click.
        if (_wired is not null) _wired.JumpToBackupRequested -= OnJumpToBackup;
        _wired = DataContext as BackupsViewModel;
        if (_wired is not null) _wired.JumpToBackupRequested += OnJumpToBackup;
    }

    /// <summary>
    /// Scroll the BackupCard whose VM has the matching id into view.
    /// Triggered by JumpToBackupCommand on a health-banner chip. Walks
    /// the visual tree to find the right ContentPresenter inside the
    /// cards ItemsControl (we can't address them by id without a custom
    /// container generator) and calls BringIntoView. The scroll lands
    /// the card near the top edge of the parent ScrollViewer.
    /// </summary>
    private void OnJumpToBackup(string backupId)
    {
        var match = this.GetVisualDescendants()
            .OfType<BackupCard>()
            .FirstOrDefault(c => (c.DataContext as BackupCardViewModel)?.Id == backupId);
        if (match is null) return;
        match.BringIntoView();
    }

    /// <summary>
    /// Forward wheel events from the page-level Grid to the cards
    /// ScrollViewer when the wheel landed outside it (e.g. over the
    /// pinned header). Avalonia's default behaviour is for wheel
    /// events to scroll only the ScrollViewer directly under the
    /// cursor, which leaves the page header feeling dead — the user
    /// reported "scrollbar is only triggered if my mouse is under the
    /// backup section". When the wheel bubbles up to this handler
    /// without an inner scroller having handled it (e.Handled stays
    /// false), translate the delta into an Offset change on the cards
    /// scroller so the user can scroll the list from anywhere on the
    /// page.
    /// </summary>
    private void OnPagePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled) return;
        if (CardsScroller is null || !CardsScroller.IsVisible) return;

        // 50px per wheel notch matches Avalonia's built-in ScrollViewer
        // default — keeps the felt-feel identical to scrolling directly
        // over the cards.
        var delta = e.Delta.Y * 50;
        var current = CardsScroller.Offset;
        var maxY = Math.Max(0, CardsScroller.Extent.Height - CardsScroller.Viewport.Height);
        var newY = Math.Clamp(current.Y - delta, 0, maxY);
        CardsScroller.Offset = new Vector(current.X, newY);
        e.Handled = true;
    }
}
