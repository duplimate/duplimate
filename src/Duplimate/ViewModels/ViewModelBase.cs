using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Duplimate.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>
    /// Run <paramref name="action"/> on the UI thread. Synchronous if the
    /// caller is already on the UI thread (so unit tests + UI-driven
    /// code paths see immediate updates); marshaled via
    /// <see cref="Dispatcher.UIThread"/> otherwise.
    /// <para>
    /// This is the canonical wrapper for Config.Changed handlers in
    /// view-models — Config.Update can fire from a background thread
    /// (orchestrator persisting a finished run, scheduled-task upsert
    /// from a worker, etc.) and modifying Avalonia-bound collections
    /// from the wrong thread silently leaves the UI showing stale
    /// state. The synchronous fast-path keeps deterministic behaviour
    /// for tests that mutate config from the UI thread.
    /// </para>
    /// </summary>
    protected static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}
