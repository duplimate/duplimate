using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Duplimate.Models;
using Serilog;

namespace Duplimate.Services;

/// <summary>
/// Loads, saves, and watches config.json. Writes are atomic (write tmp, replace).
/// A single instance is shared app-wide; readers get a stable snapshot, writers
/// hold a mutex across load-modify-save.
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static ILogger _log => AppLogger.For<ConfigStore>();
    private readonly object _gate = new();
    private AppConfig _current = new();
    /// <summary>
    /// Per-thread depth counter for re-entrant <see cref="Update"/>
    /// calls. A handler subscribed to <see cref="Changed"/> that calls
    /// <see cref="Update"/> again would otherwise fire two Save_NoLock
    /// rounds and two RaiseChanged events for one logical mutation —
    /// subscribers see the same state twice in close succession, and
    /// any in-between observer can read a still-saving file. We
    /// suppress the OUTER RaiseChanged when an inner Update has
    /// already raised one for the latest state; the inner save+raise
    /// is sufficient because it ran AFTER all the outer mutator's
    /// changes (the outer was already past its mutate(_current)
    /// statement when the handler-triggered inner Update started).
    /// </summary>
    [ThreadStatic] private static int _updateDepth;

    /// <summary>
    /// CAUTION: returns a live reference to the in-memory <see cref="AppConfig"/>,
    /// NOT a snapshot. Reads of immutable fields (strings, enums, scalars)
    /// are safe; reading collections is safe under the assumption that
    /// no caller mutates them outside <see cref="Update"/>. Mutating any
    /// field on the returned object directly is a bug — those changes
    /// would not be saved to disk and would race with concurrent
    /// <see cref="Update"/> calls. Always go through <see cref="Update"/>
    /// for writes.
    /// </summary>
    public AppConfig Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler? Changed;

    public void Load()
    {
        AppPaths.EnsureAll();
        bool changed = false;
        lock (_gate)
        {
            if (!File.Exists(AppPaths.ConfigFile))
            {
                _log.Information("No config file at {Path} — initializing defaults", AppPaths.ConfigFile);
                _current = new AppConfig();
                Save_NoLock();
                changed = true;
            }
            else
            {
                try
                {
                    var text = File.ReadAllText(AppPaths.ConfigFile);
                    _current = JsonSerializer.Deserialize<AppConfig>(text, Json) ?? new AppConfig();
                    _log.Information(
                        "Loaded config: {BackupCount} backup(s), {DestCount} destination(s)",
                        _current.Backups.Count, _current.Destinations.Count);
                    changed = true;
                }
                catch (Exception ex)
                {
                    // Never crash on a bad config — preserve it for forensics, start fresh.
                    var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                    var backup = AppPaths.ConfigFile + $".broken.{stamp}";
                    try { File.Copy(AppPaths.ConfigFile, backup, overwrite: true); } catch { }
                    _log.Error(ex, "Corrupt config file — preserved at {Backup}, starting fresh", backup);
                    _current = new AppConfig();
                    Save_NoLock();
                    changed = true;
                }
            }
        }
        if (changed) RaiseChanged();
    }

    /// <summary>
    /// Read-modify-save under the mutex. The callback receives a mutable reference
    /// to the live config; changes are saved atomically on return.
    /// Re-entrant: a Changed-handler that calls Update again is fine; only
    /// the OUTERMOST Update raises Changed (the inner already
    /// represented the latest state, and the outer's mutator has
    /// already completed by the time the handler ran).
    /// </summary>
    public void Update(Action<AppConfig> mutate)
    {
        _updateDepth++;
        try
        {
            lock (_gate)
            {
                mutate(_current);
                Save_NoLock();
            }
            // Only the outermost Update fires Changed. Inner re-entrant
            // calls (handler → Update → handler again) have already
            // saved their state under the same lock; firing twice would
            // double-broadcast.
            if (_updateDepth == 1) RaiseChanged();
        }
        finally
        {
            _updateDepth--;
        }
    }

    public void Save()
    {
        lock (_gate) Save_NoLock();
        RaiseChanged();
    }

    /// <summary>
    /// Mutate-and-save WITHOUT raising <see cref="Changed"/>. Use ONLY
    /// for state updates that have a more direct UI signalling path
    /// (e.g. orchestrator's per-run status updates already broadcast
    /// via <see cref="BackupOrchestrator.RunEnded"/>; firing Changed
    /// too would trigger every subscribed VM's Refresh redundantly,
    /// which the debouncer mitigates but doesn't eliminate).
    /// Save still runs atomically under the mutex; the only thing
    /// suppressed is the broadcast.
    /// </summary>
    public void UpdateQuiet(Action<AppConfig> mutate)
    {
        lock (_gate)
        {
            mutate(_current);
            Save_NoLock();
        }
    }

    /// <summary>
    /// Fires <see cref="Changed"/> outside the gate (subscribers might
    /// call back into <see cref="Update"/>; firing inside the lock would
    /// nest reentrantly — currently safe with `lock` semantics, but
    /// fragile). Catches subscriber exceptions individually so one
    /// throwing handler doesn't starve the rest.
    /// </summary>
    private void RaiseChanged()
    {
        var handler = Changed;
        if (handler is null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((EventHandler)d).Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _log.Warning(ex, "Config.Changed subscriber threw"); }
        }
    }

    private void Save_NoLock()
    {
        AppPaths.EnsureAll();
        var text = JsonSerializer.Serialize(_current, Json);
        var tmp = AppPaths.ConfigFile + ".tmp";
        try
        {
            // Pre-clear any stale .tmp left over from a prior crashed
            // save — File.WriteAllText overwrites fine, but AV scanners
            // sometimes hold a transient handle on a freshly-created
            // .tmp that makes the subsequent File.Replace fail with
            // sharing violation. Best-effort delete here narrows that
            // window.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

            File.WriteAllText(tmp, text);
            // Replace/Move with a tiny retry loop. Real-world cause:
            // anti-virus on-access scanning briefly opens the .tmp file
            // we just wrote, returning IOException(SharingViolation) on
            // the Replace call. A single 50ms retry past the AV's read
            // window resolves it without bothering the user with a
            // failed-save toast.
            const int maxAttempts = 3;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(AppPaths.ConfigFile))
                        File.Replace(tmp, AppPaths.ConfigFile, destinationBackupFileName: null);
                    else
                        File.Move(tmp, AppPaths.ConfigFile);
                    break;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    System.Threading.Thread.Sleep(50 * attempt);
                }
            }
            _log.Debug("Saved config: {BackupCount} backup(s), {DestCount} destination(s)",
                _current.Backups.Count, _current.Destinations.Count);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to save config to {Path}", AppPaths.ConfigFile);
            // Best-effort cleanup of the orphaned .tmp so the next save
            // attempt isn't blocked by it. We've already failed; no
            // point making it worse by leaving litter.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }
}
