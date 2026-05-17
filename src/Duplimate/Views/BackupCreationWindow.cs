using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Duplimate.Models;
using Duplimate.ViewModels;

namespace Duplimate.Views;

/// <summary>
/// Unified backup creation/edit flow with a mid-flow Easy/Advanced
/// toggle. Not a Window — a coordinator that loops between
/// <see cref="OnboardingWindow"/> (Easy) and
/// <see cref="BackupEditorWindow"/> (Advanced), preserving the user's
/// in-progress state through a Backup snapshot when they click the
/// toggle in either window's header. Returns the committed Backup or
/// null on cancel.
///
/// Why a coordinator and not a single shared window: refactoring both
/// large XAML files into a unified body would mean rewriting hundreds
/// of lines of layout. The coordinator gives the same UX (state
/// preserved across mode switches) at a fraction of the code, and the
/// existing windows keep working unchanged for any test that opens
/// them directly.
/// </summary>
public sealed class BackupCreationWindow
{
    private readonly Backup? _seed;
    private readonly bool _isEdit;
    private readonly bool _isFirstBackup;
    private readonly BackupEditMode _initialMode;

    public BackupCreationWindow(bool isFirstBackup)
    {
        _seed = null;
        _isEdit = false;
        _isFirstBackup = isFirstBackup;
        _initialMode = BackupEditMode.Easy;
    }

    public BackupCreationWindow(Backup existing)
    {
        _seed = existing;
        _isEdit = true;
        _isFirstBackup = false;
        _initialMode = existing.LastEditMode;
    }

    /// <summary>True iff the user clicked "Save and run" in either mode.
    /// Parent reads this on close to decide whether to fire the
    /// orchestrator after persisting.</summary>
    public bool RunAfterSave { get; private set; }

    /// <summary>
    /// Show the appropriate window for the current mode; if the user
    /// toggles modes mid-flow, close that window and reopen the other
    /// with the partial state. Loops until the user commits (Save,
    /// Save&amp;Run, or Cancel). Each window's own Cancel button
    /// returns null; toggle buttons return without committing.
    /// </summary>
    public async Task<Backup?> ShowDialog<T>(Window owner)
    {
        var mode = _initialMode;
        Backup? snapshot = _seed;

        // Loop forever — toggling Easy↔Advanced is user-driven and can
        // happen any number of times. The earlier 8-iteration cap was
        // a defensive guard against a buggy state migration looping
        // automatically; in practice it just punished users who liked
        // to flip-flop while exploring (the wizard closed silently
        // after 8 toggles). State-migration safety lives in
        // BuildSnapshot now — neither side can re-fire the toggle
        // event without a real user click.
        while (true)
        {
            if (mode == BackupEditMode.Easy)
            {
                var w = new OnboardingWindow(snapshot, _isFirstBackup, _isEdit);
                await w.ShowDialog<bool>(owner);
                if (w.SwitchToAdvanced)
                {
                    snapshot = w.SwitchSnapshot ?? snapshot;
                    mode = BackupEditMode.Advanced;
                    continue;
                }
                if (!w.DidFinish && !((OnboardingViewModel)w.DataContext!).SavedNoRun)
                    return null;
                // OnboardingWindow persists the backup itself when the
                // user clicks Save / Save&Run. The created backup is the
                // most recently-added entry in config — find by
                // matching SourcePaths/destination on the working copy.
                RunAfterSave = w.DidFinish;
                var working = ((OnboardingViewModel)w.DataContext!).Working;
                return working;
            }
            else
            {
                // Advanced editor: working copy is either the seed (edit
                // mode) or a fresh new() (new mode). The window returns
                // the saved backup directly.
                var working = snapshot ?? new Backup();
                var ed = new BackupEditorWindow(working, isNew: !_isEdit, isFirstBackup: _isFirstBackup);
                var saved = await ed.ShowDialog<Backup?>(owner);
                if (ed.SwitchToEasy)
                {
                    snapshot = ed.SwitchSnapshot ?? working;
                    mode = BackupEditMode.Easy;
                    continue;
                }
                if (saved is null) return null;
                RunAfterSave = ed.RunAfterSave;
                return saved;
            }
        }
    }

    /// <summary>Convenience wrapper matching the typed-result signature
    /// callers expect.</summary>
    public Task<Backup?> ShowDialog(Window owner) => ShowDialog<Backup?>(owner);
}
