using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.Views;

namespace Duplimate.ViewModels;

/// <summary>
/// Backups view-model — also the post-merge home of what used to be the
/// separate Dashboard tab. The two views were redundant (same content,
/// different layouts), so the user-facing model is now: one tab,
/// "Backups", showing every configured backup as a standardised card
/// (see <see cref="BackupCardViewModel"/>) with status, sources,
/// destinations, last/next run, verify state, action buttons, and a
/// live progress bar while running.
///
/// Health-banner aggregation that previously lived in DashboardViewModel
/// has moved here so the at-a-glance "everything's fine / a backup
/// failed" cue stays one click off the sidebar root.
/// </summary>
public sealed partial class BackupsViewModel : ViewModelBase, IDisposable
{
    private static Serilog.ILogger _log => AppLogger.For<BackupsViewModel>();

    public ObservableCollection<BackupCardViewModel> Cards { get; } = new();
    [ObservableProperty] private bool _isEmpty;

    /// <summary>"1 Backup" / "2 Backups" — proper singular/plural,
    /// rendered as a section-title rather than the previous dim
    /// throwaway. Recomputed on every Refresh.</summary>
    public string BackupsCountLabel =>
        Cards.Count == 1 ? "1 Backup" : $"{Cards.Count} Backups";

    /// <summary>
    /// Singular / plural word ("Backup" / "Backups") to pair with
    /// <see cref="Cards"/>.Count in the toolbar's count display. The
    /// count display now shows the figure in large accent type with
    /// the word as a small caps label next to it, so the figure and
    /// word need to bind separately rather than as one
    /// <see cref="BackupsCountLabel"/> string.
    /// </summary>
    public string BackupsCountWord => Cards.Count == 1 ? "BACKUP" : "BACKUPS";

    // ---- health summary banner (was on Dashboard) ----
    [ObservableProperty] private bool _hasHealthBanner;
    /// <summary>Headline summarises status COUNTS (e.g. "1 backup
    /// needs attention", "Everything looks good") — never names a
    /// specific backup. The user-facing rule: backup names appear
    /// EXACTLY ONCE in the banner, and only as clickable
    /// jump-to-backup pills below the headline.</summary>
    [ObservableProperty] private string _healthHeadline = "";
    /// <summary>Sub-line: last-activity timestamp + ok-count when
    /// there is one (e.g. "Last activity 1h ago · 1 ok"). Calm, dim,
    /// typographic — not a clickable affordance.</summary>
    [ObservableProperty] private string _healthDetail = "";
    [ObservableProperty] private string _healthClass = "ok"; // ok | warn | fail | idle

    /// <summary>Backups whose last run is in a noteworthy state (failed,
    /// warning, skipped). Surfaced as click-to-jump chips inside the
    /// health banner so a user with many backups can find the one that
    /// needs attention without scanning the list. Empty when every
    /// backup is healthy or the banner is in the "everything's fine"
    /// state.</summary>
    public ObservableCollection<HealthBackupReference> AffectedBackups { get; } = new();

    /// <summary>One entry per noteworthy backup in the health banner.
    /// Carries the id so a click can locate the matching card +
    /// scroll-into-view, and the StatusClass so the chip's leading
    /// glyph matches the card's leading glyph (visual continuity —
    /// "the warning chip in the banner is the same glyph as the
    /// warning icon on the card it points at").</summary>
    public sealed class HealthBackupReference
    {
        public string Id { get; }
        public string Name { get; }
        public string StatusClass { get; }
        public HealthBackupReference(string id, string name, string statusClass)
        { Id = id; Name = name; StatusClass = statusClass; }
    }

    /// <summary>Coalesces a flurry of Config.Changed firings (e.g. add a
    /// backup → apply schedule fires Config.Changed twice within ~5ms)
    /// into a single Refresh on the UI thread. Without this, the
    /// second-and-later events do redundant work and can clobber a
    /// scroll position the user just set.</summary>
    private readonly UiThreadDebouncer _refreshDebouncer;

    /// <summary>VM-scoped cancellation for fire-and-forget background runs
    /// kicked off via Save-and-Run. Without this, a run started milliseconds
    /// before page navigation / shutdown couldn't be signalled to stop and
    /// the orchestrator kept spawning duplicacy.exe past the VM's lifetime.</summary>
    private readonly CancellationTokenSource _backgroundRunCts = new();

    public BackupsViewModel()
    {
        _refreshDebouncer = new UiThreadDebouncer(Refresh);
        Refresh();
        ServiceLocator.Config.Changed += OnConfigChanged;
        ServiceLocator.Orchestrator.RunStarted += OnRunStarted;
        ServiceLocator.Orchestrator.RunEnded += OnRunEnded;
        ServiceLocator.Orchestrator.RunQueuedReasonChanged += OnRunQueuedReasonChanged;
        ServiceLocator.Maintenance.MaintenanceStateChanged += OnMaintenanceStateChanged;
    }

    private void OnConfigChanged(object? sender, EventArgs e) => _refreshDebouncer.Schedule();

    public void Dispose()
    {
        ServiceLocator.Config.Changed -= OnConfigChanged;
        ServiceLocator.Orchestrator.RunStarted -= OnRunStarted;
        ServiceLocator.Orchestrator.RunEnded -= OnRunEnded;
        ServiceLocator.Orchestrator.RunQueuedReasonChanged -= OnRunQueuedReasonChanged;
        ServiceLocator.Maintenance.MaintenanceStateChanged -= OnMaintenanceStateChanged;
        try { _backgroundRunCts.Cancel(); } catch { }
        _backgroundRunCts.Dispose();
        _refreshDebouncer.Dispose();
        foreach (var c in Cards) c.Dispose();
        Cards.Clear();
    }

    private void OnRunStarted(string backupId) =>
        OnUiThread(() =>
        {
            Cards.FirstOrDefault(c => c.Id == backupId)?.SetRunning();
            // A run starting may make peer cards' Run buttons need to
            // disable (shared-destination conflict). Recompute reasons
            // for every card so the user sees a tooltip-bearing disable
            // instead of the previous silent cross-card lockout.
            RecomputeRunConflicts();
        });

    /// <summary>
    /// On a run ending we mutate just the matching card and re-aggregate
    /// the health banner — full Refresh() would dispose+recreate every
    /// other card on the page, losing scroll position and resetting
    /// per-card subscriptions for backups that didn't even run. Health-
    /// banner aggregation reads <c>Backup.LastRunStatus</c> from the
    /// config store, which the orchestrator's <c>UpdateBackupLastRun</c>
    /// already wrote before firing this event, so a re-aggregate from
    /// the current config is correct without re-creating cards.
    /// </summary>
    private void OnRunEnded(string backupId, RunRecord record) =>
        OnUiThread(() =>
        {
            var card = Cards.FirstOrDefault(c => c.Id == backupId);
            if (card is not null)
            {
                // Pull a fresh Backup snapshot from config so the card's
                // header (LastRunLabel, NextRunLabel, etc.) is rebuilt
                // from the post-run state, then layer the run-specific
                // status from the RunRecord on top.
                var fresh = ServiceLocator.Config.Current.Backups
                    .FirstOrDefault(b => b.Id == backupId);
                if (fresh is not null) card.Apply(fresh);
                card.Apply(record);
            }
            RecomputeHealth(ServiceLocator.Config.Current);
            // A run ending may unblock peer cards whose Run buttons
            // were disabled by a destination collision. Re-derive
            // every card's RunBlockedReason from the now-shrunk set
            // of in-flight backups.
            RecomputeRunConflicts();
        });

    /// <summary>
    /// Routes the orchestrator's "this backup is queued waiting for
    /// destination X" signal to the matching card so its inline label
    /// flips to "Queued — …". Called with an empty reason once the
    /// gate is acquired so the label flips back to "Running…".
    /// </summary>
    private void OnRunQueuedReasonChanged(string backupId, string reason) =>
        OnUiThread(() => Cards.FirstOrDefault(c => c.Id == backupId)?.SetQueuedReason(reason));

    /// <summary>
    /// Mirror the global maintenance state into every card's
    /// RunBlockedReason so the Run Now / Test restore buttons grey
    /// out while an Erase / Wipe / Prune is in flight. The user
    /// reported: "ensure that NO backup or restore at all can run …
    /// while one of these sensitive operations is in progress."
    /// On release, RecomputeRunConflicts re-derives the per-card
    /// reasons from the current set of in-flight runs (which is
    /// empty by definition just after maintenance released — the
    /// coordinator cancelled them on entry).
    /// </summary>
    private void OnMaintenanceStateChanged(bool active, string reason) =>
        OnUiThread(() =>
        {
            if (active)
            {
                var msg = string.IsNullOrEmpty(reason)
                    ? "A storage-maintenance task is running. Please wait."
                    : $"A storage-maintenance task is running ({reason}). Please wait.";
                foreach (var c in Cards) c.RunBlockedReason = msg;
            }
            else
            {
                RecomputeRunConflicts();
            }
        });

    /// <summary>
    /// Diff-based refresh: keeps existing card instances for backups
    /// still present in the config, removes cards for backups deleted,
    /// inserts cards for new backups, and reorders to match the config's
    /// alphabetical order. The previous implementation tore down ALL
    /// cards on every config change — losing scroll position, resetting
    /// per-card subscriptions, and producing a visible flicker on a list
    /// of N backups when only one had changed.
    ///
    /// Card identity is by Backup.Id (not name) so a rename keeps the
    /// same instance (and its progress/run subscriptions) rather than
    /// disposing+recreating.
    /// </summary>
    public void Refresh()
    {
        var cfg = ServiceLocator.Config.Current;
        var desired = cfg.Backups.OrderBy(b => b.Name).ToList();
        var existing = Cards.ToDictionary(c => c.Id);

        // Step 1: remove cards whose backup is no longer in config.
        // Walk in reverse so RemoveAt indices stay valid.
        var keep = new HashSet<string>(desired.Select(b => b.Id));
        for (int i = Cards.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Cards[i].Id))
            {
                Cards[i].Dispose();
                Cards.RemoveAt(i);
            }
        }

        // Step 2: insert / update / move to match the desired order.
        for (int i = 0; i < desired.Count; i++)
        {
            var b = desired[i];
            if (existing.TryGetValue(b.Id, out var card))
            {
                // Update in place — preserves the card's event
                // subscriptions and its visual state (e.g. expanded
                // progress bars).
                card.Apply(b);
                var currentIndex = Cards.IndexOf(card);
                if (currentIndex != i) Cards.Move(currentIndex, i);
            }
            else
            {
                var fresh = new BackupCardViewModel(b, showActions: true, showProgress: true);
                fresh.EditDestinationRequested = id => EditDestinationByIdAsync(id);
                Cards.Insert(i, fresh);
            }
        }

        IsEmpty = Cards.Count == 0;
        OnPropertyChanged(nameof(BackupsCountLabel));
        OnPropertyChanged(nameof(BackupsCountWord));
        RecomputeHealth(cfg);
        RecomputeRunConflicts();
    }

    /// <summary>
    /// Clears every card's <see cref="BackupCardViewModel.RunBlockedReason"/>
    /// for shared-destination conflicts. Earlier this method gated
    /// the Run button when another backup was using the same storage,
    /// but the orchestrator now serialises shared-destination runs at
    /// the per-(DestId, Storage) named-semaphore layer, so a
    /// concurrent click is safe — it just queues. The user
    /// explicitly asked for this: "If a backup is running, it should
    /// be possible to Run now another one (right now it can be
    /// disabled if a common storage is used), simply queue it."
    ///
    /// Once the orchestrator picks up the queued run, it fires
    /// RunQueuedReasonChanged with a "waiting for «X»" message that
    /// flips the card's inline label to "Queued — …" — the user gets
    /// the queueing context EX POST instead of having the button
    /// pre-disabled. The maintenance gate (Erase / Wipe / Prune)
    /// still sets RunBlockedReason via OnMaintenanceStateChanged
    /// because those operations genuinely can't overlap with backups.
    /// </summary>
    private void RecomputeRunConflicts()
    {
        // Maintenance state, if active, has already stamped its own
        // RunBlockedReason via OnMaintenanceStateChanged — don't
        // clobber it here. Otherwise: a recompute happens on every
        // RunStarted / RunEnded, and we don't want to clear a
        // maintenance gate just because a backup happened to start
        // or end.
        if (ServiceLocator.Maintenance.IsActive) return;

        foreach (var c in Cards)
        {
            // Idle and no-maintenance: nothing blocks the Run button.
            // Shared-destination contention is now handled at the
            // orchestrator level (queueing), not the UI level.
            c.RunBlockedReason = "";
        }
    }

    private void RecomputeHealth(Models.AppConfig cfg)
    {
        if (cfg.Backups.Count == 0)
        {
            HasHealthBanner = false;
            HealthHeadline = "";
            HealthDetail = "";
            HealthClass = "idle";
            return;
        }

        var failed = new System.Collections.Generic.List<Backup>();
        var warned = new System.Collections.Generic.List<Backup>();
        var runningCount = 0;
        var skipped = new System.Collections.Generic.List<Backup>();
        var neverRun = new System.Collections.Generic.List<Backup>();
        var okCount = 0;
        DateTime? mostRecent = null;
        foreach (var b in cfg.Backups)
        {
            switch (b.LastRunStatus)
            {
                case BackupRunStatus.Failed:  failed.Add(b);   break;
                case BackupRunStatus.Warning: warned.Add(b);   break;
                case BackupRunStatus.Skipped: skipped.Add(b);  break;
                case BackupRunStatus.Success: okCount++;       break;
                // Running backups are mid-flight — the per-card live
                // progress UI is the primary signal, not the banner.
                // Counted separately so the all-ok branch below can
                // suppress its "All N backups are healthy" copy when
                // the only thing happening is a run in progress (the
                // user reported "1 backup hasn't run yet" on a
                // freshly-running backup; the all-ok branch then
                // misclassified the count as zero after we excluded
                // Running from neverRun).
                case BackupRunStatus.Running: runningCount++;  break;
                default: neverRun.Add(b);                      break;
            }
            if (b.LastRunEndUtc is DateTime t && (mostRecent is null || t > mostRecent)) mostRecent = t;
        }

        var ago = mostRecent is DateTime when ? HumanAgo(when) : "never";
        // Sub-line carries last-activity + an "ok N" tail when there
        // are healthy backups to mention. Stays calm + typographic;
        // the chips below carry the actionable info.
        var okTail = okCount > 0 ? $" · {okCount} ok" : "";

        AffectedBackups.Clear();
        // Headlines are count-based ONLY — they never name a specific
        // backup. The user explicitly asked: "include backup names
        // exactly once in this card, and as clickable pills". So the
        // headline says "1 backup skipped on its last run" while the
        // pill row below carries the actual «name» as the single
        // place the user clicks to jump.
        if (failed.Count > 0)
        {
            HealthClass = "fail";
            HealthHeadline = failed.Count == 1
                ? "1 backup failed on its last run — needs attention."
                : $"{failed.Count} backups failed on their last run — need attention.";
            HealthDetail = $"Last activity {ago}{okTail}";
            foreach (var b in failed)
                AffectedBackups.Add(new HealthBackupReference(b.Id, b.Name, "fail"));
            foreach (var b in warned)  AffectedBackups.Add(new HealthBackupReference(b.Id, b.Name, "warn"));
            foreach (var b in skipped) AffectedBackups.Add(new HealthBackupReference(b.Id, b.Name, "skipped"));
        }
        else if (warned.Count > 0 || skipped.Count > 0)
        {
            HealthClass = "warn";
            if (warned.Count > 0 && skipped.Count > 0)
            {
                var noun = (warned.Count + skipped.Count) == 1 ? "backup" : "backups";
                HealthHeadline = $"{warned.Count + skipped.Count} {noun} need attention.";
            }
            else if (warned.Count > 0)
            {
                HealthHeadline = warned.Count == 1
                    ? "1 backup finished with warnings."
                    : $"{warned.Count} backups finished with warnings.";
            }
            else
            {
                HealthHeadline = skipped.Count == 1
                    ? "1 backup was skipped on its last run."
                    : $"{skipped.Count} backups were skipped on their last run.";
            }
            HealthDetail = $"Last activity {ago}{okTail}";
            foreach (var b in warned)  AffectedBackups.Add(new HealthBackupReference(b.Id, b.Name, "warn"));
            foreach (var b in skipped) AffectedBackups.Add(new HealthBackupReference(b.Id, b.Name, "skipped"));
        }
        else if (neverRun.Count > 0)
        {
            HealthClass = "idle";
            HealthHeadline = neverRun.Count == 1
                ? "1 backup hasn't run yet."
                : $"{neverRun.Count} backups haven't run yet.";
            HealthDetail = $"Last activity {ago}{okTail}";
            foreach (var b in neverRun) AffectedBackups.Add(new HealthBackupReference(b.Id, b.Name, "idle"));
        }
        else if (okCount > 0)
        {
            HealthClass = "ok";
            HealthHeadline = okCount == 1
                ? "Everything looks good."
                : $"All {okCount} backups are healthy.";
            HealthDetail = $"Last successful run {ago}";
            // No chips in the all-green case — nothing needs attention.
        }
        else
        {
            // Only running backups (no failed/warned/skipped/never-
            // run/ok). The card's own live progress UI is the
            // signal — suppress the banner so we don't render
            // "All 0 backups are healthy" or similar nonsense.
            // runningCount is tracked but only used here to
            // distinguish "config has only running backups" from
            // "config has nothing to report".
            _ = runningCount;
            HealthHeadline = "";
            HealthDetail = "";
            HealthClass = "idle";
        }
        HasHealthBanner = !string.IsNullOrEmpty(HealthHeadline);
    }

    /// <summary>Raised by a click on an AffectedBackups chip — the view
    /// listens and scrolls the matching BackupCard into view. Routed
    /// through an event (not a command) so the view can do scrolling /
    /// flash-highlight which is purely a UI concern.</summary>
    public event Action<string>? JumpToBackupRequested;

    [RelayCommand]
    private void JumpToBackup(string backupId)
    {
        if (string.IsNullOrEmpty(backupId)) return;
        JumpToBackupRequested?.Invoke(backupId);
    }

    private static string HumanAgo(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1))   return $"{(int)d.TotalMinutes}m ago";
        if (d < TimeSpan.FromDays(1))    return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }

    [RelayCommand]
    private async Task AddNew()
    {
        var owner = Owner();
        if (owner is null) return;

        // Unified creation flow: opens BackupCreationWindow (Easy mode by
        // default) for new users; the user can flip to Advanced from the
        // window's top-right toggle and we'll preserve the partial state
        // across modes. For first-backup empty-state, the title/copy
        // adjusts via the CreationContext.IsFirstBackup flag.
        var dlg = new BackupCreationWindow(isFirstBackup: Cards.Count == 0);
        var result = await dlg.ShowDialog<Backup?>(owner);
        if (result is not null)
        {
            // Upsert: the Easy-mode wizard persists itself before
            // returning (so the orchestrator's RunBackupAsync has a
            // canonical config entry to read). For Advanced mode, this
            // is the first time the backup hits config. FindIndex+
            // replace handles both with one path.
            ServiceLocator.Config.Update(cfg =>
            {
                var idx = cfg.Backups.FindIndex(b => b.Id == result.Id);
                if (idx >= 0) cfg.Backups[idx] = result;
                else cfg.Backups.Add(result);
            });
            ApplyScheduleIfPossible(result);
            _log.Information("Backup created: name={Name} sources={SourceCount} targets={TargetCount} enabled={Enabled} runAfterSave={RunAfterSave}",
                result.Name, result.SourcePaths.Count, result.Targets.Count, result.Enabled, dlg.RunAfterSave);
            if (dlg.RunAfterSave) FireBackgroundRun(result);
        }
        else
        {
            _log.Debug("Backup creation cancelled");
        }
    }

    [RelayCommand]
    private async Task Edit(BackupCardViewModel card)
    {
        if (card is null) return;
        var owner = Owner();
        if (owner is null) return;
        var b = card.Snapshot;

        // Reuse the same unified creation window for editing — it
        // detects "edit" mode by being constructed with a non-null
        // existing backup and opens in the mode the user last used for
        // that backup (Backup.LastEditMode).
        var dlg = new BackupCreationWindow(b);
        var result = await dlg.ShowDialog<Backup?>(owner);
        if (result is not null)
        {
            ServiceLocator.Config.Update(cfg =>
            {
                var idx = cfg.Backups.FindIndex(x => x.Id == result.Id);
                if (idx >= 0) cfg.Backups[idx] = result;
                else cfg.Backups.Add(result);
            });
            ApplyScheduleIfPossible(result);
            _log.Information("Backup edited: name={Name}", result.Name);
            if (dlg.RunAfterSave) FireBackgroundRun(result);
        }
    }

    [RelayCommand]
    private async Task Delete(BackupCardViewModel card)
    {
        if (card is null) return;
        var owner = Owner();
        if (owner is null) return;
        var b = card.Snapshot;
        var dlg = new DestructiveConfirmWindow(
            title: "Remove this backup?",
            subtitle:
                $"«{b.Name}» will no longer run on schedule and will disappear from Duplimate. " +
                "The backup data already stored at your destinations stays where it is — " +
                "you can delete it from there separately if you want. Type the backup name to confirm.",
            confirmString: b.Name,
            caseInsensitive: true);
        var ok = await dlg.ShowDialog<bool>(owner);
        if (!ok) return;

        ServiceLocator.Config.Update(cfg => cfg.Backups.RemoveAll(x => x.Id == b.Id));
        try { ServiceLocator.Scheduler.DeleteTask(b); } catch (Exception ex) { _log.Warning(ex, "Scheduler delete failed for {Name}", b.Name); }
        _log.Information("Backup removed: {Name}", b.Name);
    }

    /// <summary>
    /// Run-now is shared by every card via the parent VM. Async
    /// <see cref="RelayCommandAttribute"/> defaults to
    /// <c>AllowConcurrentExecutions = false</c>, which means while ONE
    /// card's run is awaiting inside this command, the framework
    /// disables the bound button on every OTHER card too — without a
    /// tooltip, without a reason. The user just sees a dead button.
    /// Setting concurrent-allowed restores per-card independence: each
    /// invocation runs its own task, the orchestrator's per-backup
    /// semaphore (<see cref="BackupOrchestrator.RunBackupAsync"/>) still
    /// protects against double-running the SAME backup, and the
    /// destination-conflict guard below surfaces a readable reason
    /// rather than a silent disable when two backups would collide on a
    /// shared destination.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunNow(BackupCardViewModel card)
    {
        if (card is null) return;
        card.SetRunning();
        try
        {
            var record = await ServiceLocator.Orchestrator.RunBackupAsync(card.Snapshot, CancellationToken.None);
            card.Apply(record);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Run-now threw for {Name}", card.Snapshot.Name);
        }
    }

    /// <summary>Backups that already have a stop-watchdog armed. Guards
    /// against duplicate Stop clicks stacking N watchdog tasks (each
    /// would race on TryRemove inside ForceFinaliseRun; the loser arms
    /// fire harmlessly but waste CPU + log noise + risk double-
    /// AppendRun if the registry race window is wider than expected).
    /// Cleared inside the watchdog task so a SECOND legitimate Stop
    /// after a finished+restarted run can re-arm.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte>
        _stopWatchdogArmed = new();

    [RelayCommand]
    private void CancelRun(BackupCardViewModel card)
    {
        if (card is null) return;
        // Stamp the cancel reason so the run's final Summary reads
        // "Manually skipped · skipped in 1m 12s" instead of the
        // previous generic "Skipped in 1m 12s" / "drive unplugged or
        // network gone" line that was wrong for the manual case.
        ServiceLocator.Orchestrator.CancelRun(card.Id, "Manually skipped");
        // Hard-cancel safety net (user-reported 2026-04-29: a run that
        // failed silently in init left the card stuck on "Running…" with
        // Stop doing nothing for 7+ minutes). The cooperative cancel
        // path above signals the CTS, but if the run thread is wedged
        // somewhere non-cancellable (a stuck duplicacy.exe wait, a
        // disk-IO blocked finally) the natural finally never fires and
        // the registry stays populated forever. After 8 seconds, force-
        // finalise: the orchestrator synthesises a Skipped RunRecord,
        // fires RunEnded, and clears the registry — the card flips back
        // to idle and the user can try again.
        // Only one watchdog per backup at a time — repeated Stop clicks
        // shouldn't stack tasks.
        var backupId = card.Id;
        if (!_stopWatchdogArmed.TryAdd(backupId, 1)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                try
                {
                    await ServiceLocator.Orchestrator.WaitForRunCompletionAsync(
                        backupId, TimeSpan.FromSeconds(8), CancellationToken.None);
                }
                catch { /* swallow — the force path below handles "still running" */ }
                if (ServiceLocator.Orchestrator.IsRunning(backupId))
                {
                    ServiceLocator.Orchestrator.ForceFinaliseRun(
                        backupId,
                        "Stopped by user — the run thread didn't release its registry entry within 8 seconds, so we force-finalised it. " +
                        "If a duplicacy process is still running in Task Manager, it may need to be killed manually.");
                }
            }
            finally
            {
                _stopWatchdogArmed.TryRemove(backupId, out _);
            }
        });
    }

    [RelayCommand]
    private async Task TestRestore(BackupCardViewModel card)
    {
        if (card is null) return;
        card.IsVerifying = true;
        try
        {
            var outcome = await ServiceLocator.Verifier.VerifyAsync(card.Snapshot, CancellationToken.None);
            ServiceLocator.Verifier.PersistOutcome(outcome);
            var fresh = ServiceLocator.Config.Current.Backups.FirstOrDefault(x => x.Id == card.Id);
            if (fresh is not null) card.ApplyVerifyState(fresh);

            // Fire-and-forget the toast: a Failure notification is
            // shown as a topmost alert that completes its Task only
            // when the user dismisses it. Awaiting that pinned the
            // card's "Verifying…" spinner on screen until the user
            // closed the alert. The user reported: "test restore
            // will wait for the user to clear the notification before
            // being marked as completed. Don't do this, it is not
            // intuitive. Notification is fire and forget, it should
            // not prevent the UI from being updated." Same pattern
            // already used in Program.cs for unattended runs.
            _ = ServiceLocator.Notifier.NotifyAsync(
                title: outcome.OverallPass
                    ? $"{card.Snapshot.Name}: Test restore succeeded"
                    : $"{card.Snapshot.Name}: Test restore FAILED",
                detail: outcome.Summary,
                severity: outcome.OverallPass ? NotificationSeverity.Success : NotificationSeverity.Failure,
                // Show success/failure long enough that the user can
                // actually read the per-destination breakdown without
                // having to chase the toast — 8s default felt clipped
                // for the multi-line list.
                dismissAfter: TimeSpan.FromSeconds(13));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Test-restore threw for {Name}", card.Snapshot.Name);
        }
        finally
        {
            card.IsVerifying = false;
        }
    }

    /// <summary>
    /// Per-backup "Restore files" action. Routes the user to the
    /// existing Restore wizard with this backup pre-selected. Replaces
    /// the now-removed top-level "Restore files" sidebar entry.
    /// </summary>
    [RelayCommand]
    private void RestoreFiles(BackupCardViewModel card)
    {
        if (card is null) return;
        // Navigate to the (still-existing) Restore page; the
        // RestoreViewModel auto-selects the first backup, but for an
        // explicit per-backup launch we want THIS backup pre-selected
        // regardless. Resolve the live Backup model and push it onto
        // the shared restore VM.
        var b = ServiceLocator.Config.Current.Backups.FirstOrDefault(x => x.Id == card.Id);
        if (b is null) return;
        // Navigate FIRST so MainWindowViewModel lazy-constructs the
        // RestoreViewModel and the new instance subscribes to
        // RestorePreselectRequested. Only THEN raise the preselect
        // event — otherwise the event fires into a dead AppNavigator
        // (no subscribers yet) and the wizard lands on step 1 with no
        // backup picked, defeating the whole "skip step 1" UX. Nav also
        // fires RestartWizard on the VM, which clears any prior
        // SelectedBackup; the preselect that follows immediately re-
        // populates it and auto-advances past PickBackup.
        AppNavigator.Go(NavItem.Restore);
        AppNavigator.PreselectRestoreBackup(b);
    }

    /// <summary>
    /// Click-handler for a destination pill on a BackupCard. Opens the
    /// Destination editor for the given id; persists changes via
    /// upsert (the editor returns a Destination instance, possibly
    /// with mutations on the working copy).
    /// </summary>
    private async Task EditDestinationByIdAsync(string destinationId)
    {
        var owner = Owner();
        if (owner is null) return;
        var dest = ServiceLocator.Config.Current.Destinations.FirstOrDefault(d => d.Id == destinationId);
        if (dest is null) return;
        var dlg = new DestinationEditorWindow(dest, isNew: false);
        var saved = await dlg.ShowDialog<Destination?>(owner);
        if (saved is null) return;
        ServiceLocator.Config.Update(cfg =>
        {
            var idx = cfg.Destinations.FindIndex(x => x.Id == saved.Id);
            if (idx >= 0) cfg.Destinations[idx] = saved;
        });
    }

    private static void ApplyScheduleIfPossible(Backup b)
    {
        try
        {
            var actualExe = Environment.ProcessPath
                ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            ServiceLocator.Scheduler.UpsertTask(b, actualExe);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Scheduler upsert failed for {Name}", b.Name);
        }
    }

    private void FireBackgroundRun(Backup b)
    {
        // Capture the token up-front: if Dispose runs before Task.Run
        // schedules, we want this attempt to short-circuit, not crash on
        // a disposed CTS.
        CancellationToken ct;
        try { ct = _backgroundRunCts.Token; }
        catch (ObjectDisposedException) { return; }
        _ = Task.Run(async () =>
        {
            try { await ServiceLocator.Orchestrator.RunBackupAsync(b, ct); }
            catch (OperationCanceledException) { /* VM tore down mid-run */ }
            catch (Exception ex) { _log.Error(ex, "Background run-after-save threw for {Name}", b.Name); }
        });
    }

    private static Avalonia.Controls.Window? Owner()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime l)
            return l.MainWindow;
        return null;
    }
}
