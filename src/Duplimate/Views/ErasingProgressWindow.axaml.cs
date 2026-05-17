using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Duplimate.Views;

/// <summary>
/// Modal dialog shown while a destination wipe / per-backup erase is
/// running. Lives for the lifetime of the operation, surfaces a live
/// status line via an <see cref="IProgress{T}"/> callback the cleaner
/// pushes updates through, and closes itself once the awaited task
/// completes.
///
/// Intentionally non-cancellable: aborting mid-erase against a
/// Duplicacy storage can orphan chunk fossils that confuse the next
/// prune pass. The user explicitly asked: "show a dialog asking the
/// user to wait until erasing process is complete."
/// </summary>
public partial class ErasingProgressWindow : Window
{
    public ErasingProgressWindow() : this("Erasing…") { }

    public ErasingProgressWindow(string title)
    {
        InitializeComponent();
        TitleBlock.Text = title;
    }

    /// <summary>Update the live status line. Marshalled to the UI
    /// thread so the cleaner can call this from any context.</summary>
    public void SetStatus(string status)
    {
        if (Dispatcher.UIThread.CheckAccess()) StatusBlock.Text = status ?? "";
        else Dispatcher.UIThread.Post(() => StatusBlock.Text = status ?? "");
    }

    /// <summary>
    /// Update the progress bar's value. A non-null value switches the
    /// bar to determinate mode; null (or out-of-range) flips it back
    /// to indeterminate / animating. The cleaner emits non-null only
    /// while it's parsing "Deleted X of Y chunks" lines from duplicacy
    /// prune — between snapshots, or during phases without a known
    /// total (fossil marking, exhaustive sweep), it sends null so the
    /// bar reverts to "I'm working, just don't know how much is left".
    /// </summary>
    public void SetPercent(int? percent)
    {
        void Apply()
        {
            if (percent is int p && p >= 0 && p <= 100)
            {
                Progress.IsIndeterminate = false;
                Progress.Minimum = 0;
                Progress.Maximum = 100;
                Progress.Value = p;
                Progress.ShowProgressText = true;
                Progress.ProgressTextFormat = "{1:0}%";
            }
            else
            {
                Progress.IsIndeterminate = true;
                Progress.ShowProgressText = false;
            }
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    /// <summary>
    /// Show the dialog as a modal, run <paramref name="work"/> with
    /// two <see cref="IProgress{T}"/> channels — string status text
    /// and an integer percent (0-100, or null for indeterminate) —
    /// then close the dialog when the work completes (success OR
    /// throw — the throw is rethrown after the close).
    /// </summary>
    public static async Task RunAsync(
        Window owner,
        string title,
        Func<IProgress<string>, IProgress<int?>, CancellationToken, Task> work)
    {
        var dlg = new ErasingProgressWindow(title);
        var statusProgress = new Progress<string>(dlg.SetStatus);
        var pctProgress = new Progress<int?>(dlg.SetPercent);
        var dialogTask = dlg.ShowDialog(owner);
        try
        {
            await work(statusProgress, pctProgress, CancellationToken.None);
        }
        finally
        {
            // Closing inside the finally guarantees the modal
            // dismisses even if work() throws — a stuck modal would
            // brick the editor window.
            try { dlg.Close(); } catch { }
            try { await dialogTask; } catch { }
        }
    }

    /// <summary>Back-compat: status-only callers that don't need the
    /// percent channel.</summary>
    public static Task RunAsync(
        Window owner,
        string title,
        Func<IProgress<string>, CancellationToken, Task> work)
        => RunAsync(owner, title, (status, _pct, ct) => work(status, ct));
}
