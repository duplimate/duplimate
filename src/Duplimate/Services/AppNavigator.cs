using System;
using Duplimate.Models;
using Duplimate.ViewModels;

namespace Duplimate.Services;

/// <summary>
/// A single static event hub other VMs can raise to request sidebar
/// nav changes (Go) or to seed the Restore wizard with a specific
/// backup before navigating to it (PreselectRestoreBackup). Avoids
/// coupling child VMs to the parent nav state.
/// </summary>
public static class AppNavigator
{
    public static event Action<NavItem>? Requested;

    /// <summary>Raised when a caller wants the Restore wizard to land
    /// with a specific backup already chosen — used by the per-backup
    /// "Restore files" button on the BackupCard so the wizard skips
    /// the pick-step.</summary>
    public static event Action<Backup>? RestorePreselectRequested;

    /// <summary>
    /// Raised when a non-backup live-activity stream begins (the
    /// Restore wizard entering Progress, or a Test restore drill
    /// kicking off). The optional <c>backupName</c> tells subscribers
    /// which backup the activity belongs to so the LogsView can
    /// switch SelectedBackup before flipping into Live mode — that
    /// way once the run ends and the persisted RunRecord lands in
    /// that backup's history, the user is already viewing it.
    /// Subscribers should tolerate a null name (legacy callers /
    /// system-level activity).
    /// </summary>
    public static event Action<string?>? LiveActivityStarted;

    public static void Go(NavItem target) => Requested?.Invoke(target);

    public static void PreselectRestoreBackup(Backup backup) =>
        RestorePreselectRequested?.Invoke(backup);

    public static void SignalLiveActivity(string? backupName = null) =>
        LiveActivityStarted?.Invoke(backupName);
}
