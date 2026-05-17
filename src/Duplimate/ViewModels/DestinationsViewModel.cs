using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.Views;

namespace Duplimate.ViewModels;

public sealed partial class DestinationsViewModel : ViewModelBase, IDisposable
{
    private static Serilog.ILogger _log => AppLogger.For<DestinationsViewModel>();

    public ObservableCollection<DestinationRow> Rows { get; } = new();
    [ObservableProperty] private bool _isEmpty;

    /// <summary>
    /// Singular / plural word ("Destination" / "Destinations") to pair
    /// with <see cref="Rows"/>.Count in the toolbar's count display.
    /// Mirrors BackupsViewModel.BackupsCountWord so the two pages
    /// share the same count-figure + count-word visual treatment.
    /// </summary>
    public string DestinationsCountWord => Rows.Count == 1 ? "DESTINATION" : "DESTINATIONS";

    private readonly UiThreadDebouncer _refreshDebouncer;

    public DestinationsViewModel()
    {
        _refreshDebouncer = new UiThreadDebouncer(Refresh);
        Refresh();
        ServiceLocator.Config.Changed += OnConfigChanged;
    }

    private void OnConfigChanged(object? sender, EventArgs e) => _refreshDebouncer.Schedule();

    public void Dispose()
    {
        ServiceLocator.Config.Changed -= OnConfigChanged;
        _refreshDebouncer.Dispose();
    }

    /// <summary>
    /// Diff-based refresh: keep existing DestinationRow instances for
    /// destinations still in config, replace mutated ones, remove
    /// deleted ones, insert new ones in alphabetical order. Without
    /// this, every Config.Changed (which fires for any backup edit too,
    /// not just destination edits) clears + rebuilds the list, dropping
    /// any per-row UI state and producing visible flicker.
    /// </summary>
    public void Refresh()
    {
        var desired = ServiceLocator.Config.Current.Destinations
            .OrderBy(d => d.Name).ToList();
        var existing = Rows.ToDictionary(r => r.Id);

        var keep = new HashSet<string>(desired.Select(d => d.Id));
        for (int i = Rows.Count - 1; i >= 0; i--)
            if (!keep.Contains(Rows[i].Id))
                Rows.RemoveAt(i);

        for (int i = 0; i < desired.Count; i++)
        {
            var d = desired[i];
            // DestinationRow snapshots fields at construction time. If
            // any field changed (rename, encrypt toggle, etc.), the
            // existing row is stale — replace by index. Otherwise leave
            // the row alone to preserve per-row UI affordances.
            if (existing.TryGetValue(d.Id, out var row) && DestinationRow.HasSameFields(row, d))
            {
                var currentIndex = Rows.IndexOf(row);
                if (currentIndex != i) Rows.Move(currentIndex, i);
            }
            else if (existing.TryGetValue(d.Id, out var stale))
            {
                var currentIndex = Rows.IndexOf(stale);
                Rows[currentIndex] = new DestinationRow(d);
                if (currentIndex != i) Rows.Move(currentIndex, i);
            }
            else
            {
                Rows.Insert(i, new DestinationRow(d));
            }
        }

        IsEmpty = Rows.Count == 0;
        OnPropertyChanged(nameof(DestinationsCountWord));
    }

    [RelayCommand]
    private async Task AddNew()
    {
        var dlg = new DestinationEditorWindow(new Destination(), isNew: true);
        var owner = Owner();
        if (owner is not null)
        {
            var result = await dlg.ShowDialog<Destination?>(owner);
            if (result is not null)
            {
                ServiceLocator.Config.Update(cfg => cfg.Destinations.Add(result));
                _log.Information("Destination created: name={Name} kind={Kind} encrypted={Enc}",
                    result.Name, result.Kind, result.Encrypted);
            }
            else
            {
                _log.Debug("Destination creation cancelled");
            }
        }
    }

    [RelayCommand]
    private async Task Edit(DestinationRow row)
    {
        var dest = ServiceLocator.Config.Current.Destinations.FirstOrDefault(d => d.Id == row.Id);
        if (dest is null) return;

        // Block edits while any backup using this destination is in
        // flight. Rotating an OAuth token or changing the storage URL
        // mid-run would either fail the live backup mid-write or
        // (worse, on encryption-key changes) write a snapshot Duplicacy
        // can't read back.
        var runningUsers = ServiceLocator.Config.Current.Backups
            .Where(b => b.Targets.Any(t => t.DestinationId == dest.Id))
            .Where(b => ServiceLocator.Orchestrator.IsRunning(b.Id))
            .Select(b => b.Name)
            .ToList();
        if (runningUsers.Count > 0)
        {
            var ownerForBlock = Owner();
            if (ownerForBlock is null) return;
            // Informational block — no typing gate; user just acknowledges.
            await DestructiveConfirmWindow.ShowInfoAsync(ownerForBlock,
                title: "Can't edit — a backup is using it right now",
                subtitle:
                    $"«{dest.Name}» is currently in use by " +
                    $"{(runningUsers.Count == 1 ? "the running backup" : "running backups")}: " +
                    $"{string.Join(", ", runningUsers)}. " +
                    "Stop the run from the Backups page first — changing credentials or paths " +
                    "while a backup is writing can break the snapshot chain.");
            return;
        }

        // Edit a clone so cancel is actually cancel.
        var clone = new Destination
        {
            Id = dest.Id,
            Name = dest.Name,
            Kind = dest.Kind,
            PathOrSubpath = dest.PathOrSubpath,
            ExpectedDriveLetter = dest.ExpectedDriveLetter,
            ExpectedVolumeLabel = dest.ExpectedVolumeLabel,
            Encrypted = dest.Encrypted,
            S3Endpoint = dest.S3Endpoint,
            S3Region = dest.S3Region,
            S3Bucket = dest.S3Bucket,
            S3AccessKeyRef = dest.S3AccessKeyRef,
            S3SecretKeyRef = dest.S3SecretKeyRef,
            NetworkUsername = dest.NetworkUsername,
            NetworkPasswordRef = dest.NetworkPasswordRef,
            OAuthTokenRef = dest.OAuthTokenRef,
            StoragePasswordRef = dest.StoragePasswordRef,
            LastTokenValidatedUtc = dest.LastTokenValidatedUtc,
            CreatedUtc = dest.CreatedUtc,
            LastUsedUtc = dest.LastUsedUtc,
        };

        var dlg = new DestinationEditorWindow(clone, isNew: false);
        var owner = Owner();
        if (owner is not null)
        {
            var result = await dlg.ShowDialog<Destination?>(owner);
            if (result is not null)
            {
                ServiceLocator.Config.Update(cfg =>
                {
                    var idx = cfg.Destinations.FindIndex(x => x.Id == result.Id);
                    if (idx >= 0) cfg.Destinations[idx] = result;
                });
                _log.Information("Destination updated: name={Name} kind={Kind}", result.Name, result.Kind);
            }
            else
            {
                _log.Debug("Destination edit cancelled for {Name}", dest.Name);
            }
        }
    }

    [RelayCommand]
    private async Task Delete(DestinationRow row)
    {
        var dest = ServiceLocator.Config.Current.Destinations.FirstOrDefault(d => d.Id == row.Id);
        if (dest is null) return;

        var owner = Owner();
        if (owner is null) return;

        // Refuse the wipe outright if any backup that targets this
        // destination is currently running. Earlier the wipe would race
        // duplicacy.exe — a recursive Directory.Delete tripping over
        // open file handles, or a duplicacy prune racing the live
        // backup's snapshot writes — and crash the whole app. The user
        // hit this with a Dropbox destination mid-run; the only safe
        // answer is "stop the backup first, then remove the
        // destination".
        var runningUsers = ServiceLocator.Config.Current.Backups
            .Where(b => b.Targets.Any(t => t.DestinationId == dest.Id))
            .Where(b => ServiceLocator.Orchestrator.IsRunning(b.Id))
            .Select(b => b.Name)
            .ToList();
        if (runningUsers.Count > 0)
        {
            // Informational block — no typing gate.
            await DestructiveConfirmWindow.ShowInfoAsync(owner,
                title: "Can't remove — a backup is using it right now",
                subtitle:
                    $"«{dest.Name}» is currently in use by " +
                    $"{(runningUsers.Count == 1 ? "the running backup" : "running backups")}: " +
                    $"{string.Join(", ", runningUsers)}. " +
                    "Stop the run from the Backups page (Stop button on the card) and try again — " +
                    "removing a destination mid-run can corrupt the snapshot chain or destroy data " +
                    "the live backup is currently writing.");
            return;
        }

        // Safety: refuse to delete a destination that backups still point at.
        // Cascading a full wipe while backups still target the folder would
        // leave those backups pointing at freshly-empty storage.
        var usedBy = ServiceLocator.Config.Current.Backups
            .Where(b => b.Targets.Any(t => t.DestinationId == dest.Id))
            .Select(b => b.Name)
            .ToList();

        // Used by N backups: don't block — explain the implication and
        // let the user proceed. On confirm we strip this destination
        // from every referencing backup. Compute which backups would
        // be left with ZERO targets (fully-orphaned) vs. which still
        // have other destinations after this removal — the message
        // text changes accordingly so we don't claim "no destination"
        // about a backup that retains a sibling target.
        var orphaned = new System.Collections.Generic.List<string>();
        var diminished = new System.Collections.Generic.List<string>();
        foreach (var b in ServiceLocator.Config.Current.Backups)
        {
            if (!b.Targets.Any(t => t.DestinationId == dest.Id)) continue;
            var remaining = b.Targets.Count(t => t.DestinationId != dest.Id);
            (remaining == 0 ? orphaned : diminished).Add(b.Name);
        }
        string usedBySuffix = "";
        if (orphaned.Count > 0 && diminished.Count > 0)
        {
            usedBySuffix =
                $" «{dest.Name}» is also used by: " +
                $"{string.Join(", ", orphaned.Concat(diminished))}. " +
                $"Removing it will leave {string.Join(", ", orphaned)} " +
                $"with no destination at all (won't run until you add one back); " +
                $"{string.Join(", ", diminished)} will keep their other destinations.";
        }
        else if (orphaned.Count > 0)
        {
            usedBySuffix =
                $" «{dest.Name}» is currently used by: {string.Join(", ", orphaned)}. " +
                $"Removing it will leave {(orphaned.Count == 1 ? "that backup" : "those backups")} " +
                "with no destination at all — they won't run again until you add one back.";
        }
        else if (diminished.Count > 0)
        {
            usedBySuffix =
                $" «{dest.Name}» is currently used by: {string.Join(", ", diminished)}. " +
                $"They keep their other destinations after the removal.";
        }

        // Refuse to wipe if a SIBLING destination points at the same
        // remote storage (same URL). Wiping one would silently destroy
        // the other's data even though no backup currently references
        // either. Rare but a real footgun for users who duplicate
        // destinations.
        var thisUrl = dest.BuildStorageUrl();
        var sharedWith = ServiceLocator.Config.Current.Destinations
            .Where(d => d.Id != dest.Id
                        && string.Equals(d.BuildStorageUrl(), thisUrl, System.StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Name)
            .ToList();
        if (sharedWith.Count > 0)
        {
            // Informational block — no typing gate.
            await DestructiveConfirmWindow.ShowInfoAsync(owner,
                title: "Can't wipe — storage is shared",
                subtitle:
                    $"«{dest.Name}» writes to the same place as: {string.Join(", ", sharedWith)}. " +
                    "Wiping it would destroy data those destinations rely on. Remove the duplicate destinations first, " +
                    "then come back here.");
            return;
        }

        // Be precise about what we'll actually delete. The Local /
        // External / Network path only removes the Duplicacy-owned
        // artefacts (snapshots/, chunks/, .duplicacy/, config) — any
        // unrelated files the user keeps in the same folder stay put,
        // and we tear down the parent folder only if it ends up
        // empty after our cleanup. The cloud / S3 path only prunes
        // snapshot ids that match a known backup-chain id (foreign
        // snapshots are never touched). Match the dialog copy to that
        // contract so the user knows their other files are safe.
        var where = dest.IsLocalLike
            ? $"the Duplicacy data inside «{dest.PathOrSubpath}» (snapshots/, chunks/, config — any other files in that folder are left alone)"
            : $"the snapshot chains Duplimate created at «{dest.Name}» (foreign snapshots from other tools or other machines stay untouched)";

        var dlg = new DestructiveConfirmWindow(
            title: "Remove this destination and its data?",
            subtitle:
                $"The destination will disappear from Duplimate AND {where} will be erased. " +
                "There's no undo — type OK to confirm." + usedBySuffix,
            confirmString: "OK",
            caseInsensitive: true);
        var ok = await dlg.ShowDialog<bool>(owner);
        if (!ok) return;

        // Strip the destination's id out of every backup that references
        // it. Backups left with zero targets are still kept in config
        // (the user explicitly said: don't auto-delete — let them
        // either add a new destination or delete the backup later).
        // The orchestrator's RunBackupCoreAsync already fast-fails on
        // sources.Count==0; an explicit "no destinations" path is
        // handled symmetrically in Targets.Count==0 below.
        if (usedBy.Count > 0)
        {
            ServiceLocator.Config.Update(cfg =>
            {
                foreach (var b in cfg.Backups)
                    b.Targets.RemoveAll(t => t.DestinationId == dest.Id);
            });
            _log.Information(
                "Stripped destination {Dest} from {Count} backup(s): {Names}",
                dest.Name, usedBy.Count, string.Join(", ", usedBy));
        }

        _log.Information("Destination wipe starting: name={Name} kind={Kind}", dest.Name, dest.Kind);

        // Always remove the destination from config — even if the wipe
        // throws or partially fails. A user who chose "Remove" expects
        // the row gone from the app; the alternative ("crash leaves
        // dead destination wired up") is far worse than orphan data on
        // remote storage that the user can clean up by hand.
        CleanupReport? report = null;
        Exception? wipeError = null;

        // Surface a modal "Erasing…" dialog so the user knows the
        // wipe is in flight (otherwise the Destinations tab sat
        // motionless for the duration — multi-minute on cloud) and
        // so the cleaner's per-step messages have somewhere to land.
        // Same RunAsync pattern as the per-target erase in
        // BackupEditorViewModel — the dialog is intentionally
        // non-cancellable to avoid orphaning fossils on Duplicacy
        // storages.
        try
        {
            await ErasingProgressWindow.RunAsync(
                owner,
                $"Erasing «{dest.Name}»",
                async (progress, chunkPct, ct) =>
                {
                    // Take the global maintenance lock so no backup /
                    // restore / test-restore overlaps the wipe. Cancels
                    // any in-flight backup before proceeding.
                    using var maintenance = await ServiceLocator.Maintenance.AcquireAsync(
                        $"Erasing «{dest.Name}»", progress, cancelGracePeriod: null, ct);
                    try
                    {
                        report = await ServiceLocator.Cleaner.WipeEntireDestinationAsync(dest, ct, progress, chunkPct);
                        _log.Information("Destination wipe done: {Summary}", report.Summarize());
                    }
                    catch (Exception ex)
                    {
                        wipeError = ex;
                        _log.Error(ex, "Destination wipe threw: name={Name} kind={Kind}", dest.Name, dest.Kind);
                    }
                });
        }
        catch (Exception ex)
        {
            // The maintenance Acquire / dialog itself threw before
            // the wipe could even start — surface as a wipe error
            // and fall through to the config mutation below. The
            // "always remove from config" comment above is a
            // contract: if the user clicked Remove we MUST detach
            // this destination. Without this catch, an OCE on app
            // shutdown or a bug in the dialog wiring would leave the
            // backups' Targets stripped (line 322-327 above already
            // ran) but the destination still in the list — orphan
            // state with no UI affordance to recover.
            wipeError ??= ex;
            _log.Error(ex, "Erase orchestration threw before wipe could complete: name={Name}", dest.Name);
        }

        // Reset run state on every backup that referenced this
        // destination — the data is gone, so showing "Last successful
        // run on date X" against a now-empty destination is misleading.
        // Per-source size cache + verify state are reset for the same
        // reason. The user explicitly asked: "invalidate the status
        // of the backup writing to this destination so it becomes
        // like when it was never run."
        var affected = usedBy.ToHashSet(StringComparer.OrdinalIgnoreCase);
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.RemoveAll(x => x.Id == row.Id);
            foreach (var b in cfg.Backups)
            {
                if (!affected.Contains(b.Name)) continue;
                b.LastRunStatus = BackupRunStatus.NeverRun;
                b.LastRunSummary = null;
                b.LastRunStartUtc = null;
                b.LastRunEndUtc = null;
                b.SourceLastSizeBytes.Clear();
                b.LastVerifyUtc = null;
                b.LastVerifyPass = null;
                b.LastVerifySummary = null;
            }
        });
        // Drop the per-destination wipe gate. Without this, a long-running
        // app accumulates a SemaphoreSlim per ever-deleted destination —
        // small but unbounded.
        ServiceLocator.Cleaner.ForgetWipeGate(dest.Id);
        _log.Information("Destination removed from config: name={Name}", dest.Name);

        // Surface partial-failure state so the user can decide whether
        // to clean up the remote by hand (Dropbox cloud paths in
        // particular — the cleaner tries to enumerate snapshot ids and
        // delete only known-owned folders; if enumeration fails it
        // skips the wipe rather than risk wiping foreign data).
        if (wipeError is not null)
        {
            // Post-action info dialog — no typing gate.
            await DestructiveConfirmWindow.ShowInfoAsync(owner,
                title: "Removed from Duplimate, but storage cleanup failed",
                subtitle:
                    $"«{dest.Name}» is gone from the app, but the cleanup of its stored data ran into an error: " +
                    $"{wipeError.Message}. You may want to delete the data by hand on the destination side.");
        }
        else if (report is not null && report.Errors.Count > 0)
        {
            await DestructiveConfirmWindow.ShowInfoAsync(owner,
                title: "Removed from Duplimate, but cleanup is partial",
                subtitle:
                    $"«{dest.Name}» is gone from the app. The storage cleanup reported: " +
                    $"{string.Join(" · ", report.Errors)}. " +
                    "Anything left on the destination is yours to remove if you'd like.");
        }
        else if (!dest.IsLocalLike)
        {
            // Cloud destinations: chunks are gone, but Duplicacy leaves
            // the storage's `config` file + an empty directory skeleton
            // behind by design (the storage is treated as a re-usable
            // resource, not a decommissionable one — see the matching
            // Note in WipeRemoteRootAsync). Surface that as a calm
            // info dialog so the user isn't surprised when they look
            // at the remote and still see a folder. Local destinations
            // don't have this problem — `WipeLocalRoot` does a real
            // recursive delete.
            await DestructiveConfirmWindow.ShowInfoAsync(owner,
                title: $"«{dest.Name}» removed",
                subtitle:
                    "Backup data has been removed from the destination. " +
                    "An empty Duplicacy storage skeleton (a ~1 KB `config` file and empty " +
                    "`chunks/` + `snapshots/` directory structure) stays behind — " +
                    "Duplicacy's prune doesn't remove the storage's root folder. " +
                    "If you want a fully clean remote, delete the folder via the " +
                    "provider's web UI.");
        }
    }

    private static Avalonia.Controls.Window? Owner()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime l)
            return l.MainWindow;
        return null;
    }
}

public sealed partial class DestinationRow : ObservableObject
{
    public string Id { get; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _kind;
    [ObservableProperty] private string _url;
    [ObservableProperty] private string _kindGlyph;

    public DestinationRow(Destination d)
    {
        Id = d.Id;
        _name = d.Name;
        _kind = Humanize(d.Kind);
        _url = d.BuildStorageUrl();
        _kindGlyph = GlyphFor(d.Kind);
    }

    /// <summary>
    /// True iff every visible field on <paramref name="row"/> still
    /// matches the underlying destination — used by the diff-merge in
    /// <see cref="DestinationsViewModel.Refresh"/> to decide whether
    /// the existing row instance can be kept (preserving any per-row
    /// UI state) or must be replaced by a new one. Compares the same
    /// projections the constructor sets.
    /// </summary>
    public static bool HasSameFields(DestinationRow row, Destination d) =>
        string.Equals(row.Name, d.Name, StringComparison.Ordinal)
        && string.Equals(row.Kind, Humanize(d.Kind), StringComparison.Ordinal)
        && string.Equals(row.Url, d.BuildStorageUrl(), StringComparison.Ordinal)
        && string.Equals(row.KindGlyph, GlyphFor(d.Kind), StringComparison.Ordinal);

    /// <summary>
    /// Resource key for the PathIcon used in the kind-glyph badge. The
    /// corresponding StreamGeometry lives in Themes/Icons.axaml.
    /// </summary>
    private static string GlyphFor(DestinationKind k) => k switch
    {
        DestinationKind.LocalFolder       => "Icon.Folder",
        DestinationKind.ExternalDrive     => "Icon.HardDrive",
        DestinationKind.NetworkShare      => "Icon.Network",
        DestinationKind.DropboxAppScoped  => "Icon.Cloud",
        DestinationKind.DropboxFullAccess => "Icon.Cloud",
        DestinationKind.OneDrivePersonal  => "Icon.Cloud",
        DestinationKind.OneDriveBusiness  => "Icon.Cloud",
        DestinationKind.GoogleDrive       => "Icon.Cloud",
        DestinationKind.S3Compatible      => "Icon.Database",
        _                                 => "Icon.Folder",
    };

    private static string Humanize(DestinationKind k) => k switch
    {
        DestinationKind.LocalFolder       => "Local folder",
        DestinationKind.ExternalDrive     => "External drive",
        DestinationKind.NetworkShare      => "Network share",
        DestinationKind.DropboxAppScoped  => "Dropbox (app-scoped)",
        DestinationKind.DropboxFullAccess => "Dropbox (full access)",
        DestinationKind.OneDrivePersonal  => "OneDrive",
        DestinationKind.OneDriveBusiness  => "OneDrive for Business",
        DestinationKind.GoogleDrive       => "Google Drive",
        DestinationKind.S3Compatible      => "S3-compatible",
        _                                  => k.ToString(),
    };
}
