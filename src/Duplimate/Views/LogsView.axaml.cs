using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Duplimate.ViewModels;

namespace Duplimate.Views;

public partial class LogsView : UserControl
{
    private LogsViewModel? _boundVm;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => UnhookViewModel();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnhookViewModel();
        if (DataContext is LogsViewModel vm)
        {
            _boundVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void UnhookViewModel()
    {
        if (_boundVm is null) return;
        _boundVm.PropertyChanged -= OnVmPropertyChanged;
        _boundVm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Always auto-scroll to the bottom when DisplayText changes,
        // both for the live tail (so streaming lines remain visible
        // without user intervention) AND for past-run loads (so the
        // user lands on the most recent / final lines, which is
        // typically where the verdict and any warning/error sits).
        // The user reported: "in the logs, always auto-scroll to
        // the bottom if new lines are appended" — explicitly
        // overriding the prior smart-follow behaviour that yielded
        // when they scrolled up. If the user scrolls up to read,
        // the next flush will scroll them back down; that's the
        // contract they asked for.
        if (e.PropertyName != nameof(LogsViewModel.DisplayText)) return;

        // Defer the scroll until after layout has measured the new
        // text. Loaded priority runs after Layout, so Extent.Height
        // reflects the just-arrived chunk.
        Dispatcher.UIThread.Post(ScrollLogToBottom, DispatcherPriority.Loaded);
    }

    private void ScrollLogToBottom()
    {
        if (LogScroll is null) return;
        var extent = LogScroll.Extent;
        // Push the offset to the maximum scrollable Y. The ScrollViewer
        // clamps to (Extent - Viewport) internally, so passing the raw
        // Extent.Height is safe and idempotent.
        LogScroll.Offset = new Vector(LogScroll.Offset.X, extent.Height);
    }

    private void OnLogScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // No-op: kept so the XAML's ScrollChanged="OnLogScrollChanged"
        // wiring still resolves. Earlier this method tracked "did the
        // user scroll away from the bottom?" to gate auto-follow; the
        // user explicitly asked us to always follow, so the gate is
        // gone and there's nothing to track here. Removing the XAML
        // attribute would require a one-line edit on every refactor —
        // keeping the empty handler is cheaper.
    }
}
