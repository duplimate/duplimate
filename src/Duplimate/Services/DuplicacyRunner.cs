using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Models;
using Serilog;

namespace Duplimate.Services;

/// <summary>
/// Wraps duplicacy.exe. Streams stdout lines live into a log file AND to
/// subscribers (live log view), parses errors/warnings as they fly by, and
/// returns a structured RunRecord at the end.
///
/// This is the only service that spawns the CLI. Every other service delegates here.
/// </summary>
public sealed class DuplicacyRunner
{
    private readonly ConfigStore _config;
    private readonly SecretsStore _secrets;
    private readonly MonitoringSettings _monitoringSnapshot;
    private static ILogger _log => AppLogger.For<DuplicacyRunner>();

    public DuplicacyRunner(ConfigStore config, SecretsStore secrets)
    {
        _config = config;
        _secrets = secrets;
        _monitoringSnapshot = _config.Current.Monitoring;
    }

    /// <summary>Fires for every line of stdout (already stamped with HH:mm:ss prefix).</summary>
    public event Action<string>? LineWritten;

    /// <summary>
    /// Re-broadcast a line into the global live-tail feed. Used by
    /// services that spawn duplicacy.exe directly (RestoreEngine,
    /// RevisionBrowser, BackupVerifier) so their output also surfaces
    /// in the LogsViewModel's Live pane — without this, the user
    /// running a restore or test-restore saw nothing in Activity &amp;
    /// logs even though duplicacy.exe was busy producing output.
    /// </summary>
    public void RaiseLineWritten(string line)
    {
        try { LineWritten?.Invoke(line); } catch { /* live-tail subscriber failure shouldn't escape */ }
    }

    // ---- top-level operations ----
    //
    // All cell operations now take a <sourcePath> in addition to the
    // (backup, destination, storageName) triple. The tuple
    // (backup, sourcePath) uniquely identifies a duplicacy repository:
    // one .duplicacy/ directory, one snapshot id, one revision chain.

    public Task<RunRecord> BackupAsync(Backup backup, string sourcePath, Destination destination, string storageName, CancellationToken ct, Action<string>? lineSink = null)
        => RunSequenceAsync(backup, sourcePath, destination, storageName, new[] { DuplicacyOperation.Backup }, ct, lineSink);

    public Task<RunRecord> BackupPruneCheckAsync(Backup backup, string sourcePath, Destination destination, string storageName, CancellationToken ct, Action<string>? lineSink = null)
    {
        var ops = new List<DuplicacyOperation> { DuplicacyOperation.Backup };
        if (backup.PruneAfterBackup) ops.Add(DuplicacyOperation.Prune);
        if (backup.CheckAfterBackup) ops.Add(DuplicacyOperation.Check);
        return RunSequenceAsync(backup, sourcePath, destination, storageName, ops, ct, lineSink);
    }

    public Task<RunRecord> PruneAsync(Backup backup, string sourcePath, Destination destination, string storageName, CancellationToken ct)
        => RunSequenceAsync(backup, sourcePath, destination, storageName, new[] { DuplicacyOperation.Prune }, ct);

    public Task<RunRecord> CheckAsync(Backup backup, string sourcePath, Destination destination, string storageName, CancellationToken ct)
        => RunSequenceAsync(backup, sourcePath, destination, storageName, new[] { DuplicacyOperation.Check }, ct);

    public Task<RunRecord> PruneToLatestAsync(Backup backup, string sourcePath, Destination destination, string storageName, CancellationToken ct)
        => RunSequenceAsync(backup, sourcePath, destination, storageName, new[] { DuplicacyOperation.PruneAll }, ct);

    /// <summary>
    /// Removes every revision of a specific snapshot id from the given
    /// destination, and (via <c>-exclusive</c>) garbage-collects the
    /// chunks that only belonged to that chain. Leaves chunks shared
    /// with other snapshots alone. Invoked by <see cref="StorageCleaner"/>
    /// during cascading backup delete against cloud / S3 targets where
    /// we can't do the filesystem trick directly.
    /// </summary>
    public async Task PruneSnapshotIdAsync(
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        string snapshotId,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(backup, sourcePath, destination, storageName, ct);

        var repoDir = AppPaths.RepoDirForBackupSource(backup.Name, sourcePath);
        // `-threads N` is critical for cloud destinations. Duplicacy's
        // CLI defaults `prune` to threads=1 — every chunk-delete /
        // chunk-fossilize is a sequential API call, which on Dropbox
        // (~10 ops/s effective per-app-token rate limit) means a 24-h
        // wall-clock event on a 1.6 TB repo with significant churn.
        // Reusing the backup's EffectiveThreads gives us the per-kind
        // recommended count (4 for cloud, 1 for local) and respects
        // any user override. Verified by source-level inspection of
        // duplicacy_chunkoperator.go — every Delete/MoveFile call goes
        // through the same threaded job queue that `-threads` controls.
        var threads = EffectiveThreads(backup, destination);
        var args = new List<string>(Verbosity())
        {
            "prune",
            "-storage", storageName,
            "-id", snapshotId,
            "-r", "1-999999",
            "-threads", threads.ToString(),
            "-exclusive",
        };
        var password = destination.StoragePasswordRef is null ? "" : _secrets.Get(destination.StoragePasswordRef);
        var env = BuildCloudEnv(destination, storageName);
        var exit = await RunRawAsync(repoDir, args, password, ct, line: null, envVars: env);
        if (exit != 0) throw new Exception($"duplicacy prune -id {snapshotId} exited with code {exit}");
    }

    /// <summary>
    /// Runs <c>duplicacy prune -exhaustive -exclusive</c> against the
    /// storage, deleting every chunk that isn't referenced by a current
    /// snapshot. Used by <see cref="StorageCleaner"/> as the final step
    /// of a destination-wipe after every snapshot id has been pruned.
    /// </summary>
    public async Task PruneExhaustiveAsync(
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(backup, sourcePath, destination, storageName, ct);

        var repoDir = AppPaths.RepoDirForBackupSource(backup.Name, sourcePath);
        // See PruneSnapshotIdAsync above for the `-threads` rationale —
        // exhaustive sweeps deal with the same per-chunk Delete/MoveFile
        // API-call shape and benefit from the same parallelism.
        var threads = EffectiveThreads(backup, destination);
        var args = new List<string>(Verbosity())
        {
            "prune",
            "-storage", storageName,
            "-exhaustive",
            "-threads", threads.ToString(),
            "-exclusive",
        };
        var password = destination.StoragePasswordRef is null ? "" : _secrets.Get(destination.StoragePasswordRef);
        var env = BuildCloudEnv(destination, storageName);
        var exit = await RunRawAsync(repoDir, args, password, ct, line: null, envVars: env);
        if (exit != 0) throw new Exception($"duplicacy prune -exhaustive exited with code {exit}");
    }

    public Task<RunRecord> ListRevisionsAsync(Backup backup, string sourcePath, Destination destination, string storageName, CancellationToken ct)
        => RunSequenceAsync(backup, sourcePath, destination, storageName, new[] { DuplicacyOperation.List }, ct);

    /// <summary>
    /// Runs <c>duplicacy password -storage &lt;name&gt;</c> against an
    /// existing initialized storage to rotate the password used to
    /// encrypt the master key in the storage's <c>config</c> file.
    /// <para>
    /// Cost: this is INSTANT — Duplicacy decrypts the small (~1 KB)
    /// <c>config</c> file with the old password, re-encrypts the master
    /// key with the new password (using PBKDF2 with the configured
    /// iteration count), and uploads. Chunks are NOT re-encrypted; they
    /// stay encrypted with the unchanged master key. So the operation
    /// touches a single small file regardless of how much data is in
    /// the storage.
    /// </para>
    /// <para>
    /// Failure modes: wrong old password → decrypt fails before any
    /// write, the storage is untouched. Network error during upload →
    /// duplicacy uses atomic upload (write tmp, rename) so the previous
    /// config remains intact. Storage not initialised → init step below
    /// fails clearly, no config to change anyway.
    /// </para>
    /// <para>
    /// Caller responsibility: persist <paramref name="newPassword"/>
    /// into <see cref="SecretsStore"/> ONLY after this returns success
    /// — until then the storage's config is still locked with the old
    /// password.
    /// </para>
    /// </summary>
    /// <returns>Exit code (0 = success) and the captured stdout for
    /// surfacing in the UI on failure.</returns>
    public async Task<(int exitCode, string output)> ChangeStoragePasswordAsync(
        Destination destination,
        string storageName,
        string oldPassword,
        string newPassword,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(oldPassword))
            throw new ArgumentException("Old password is required to decrypt the storage's master key.", nameof(oldPassword));
        if (newPassword is null || newPassword.Length < 8)
            throw new ArgumentException("New password must be at least 8 characters (Duplicacy refuses shorter ones).", nameof(newPassword));

        // Synthetic ephemeral repo — same pattern as
        // StorageCleaner.WipeRemoteRootAsync. We don't touch any real
        // backup's repo dir, so a failed change can't leave a real
        // backup with a stale .duplicacy/preferences.
        var synthetic = new Backup
        {
            Name = $"_pwchg_{Guid.NewGuid():N}".Substring(0, 16),
            SourcePaths = { Path.GetTempPath() },
        };
        var sourcePath = synthetic.SourcePaths[0];
        var repoDir = AppPaths.RepoDirForBackupSource(synthetic.Name, sourcePath);
        var captured = new StringBuilder();
        Action<string> tee = ln => captured.AppendLine(ln);

        try
        {
            // Step 1: init the synthetic repo with the OLD password.
            // This decrypts the existing storage config — if oldPassword
            // is wrong, init fails here BEFORE we issue the password
            // command, so the storage isn't touched.
            await InitWithExplicitPasswordAsync(
                synthetic, sourcePath, destination, storageName, oldPassword, tee, ct);

            // Step 2: run `duplicacy password -storage <name>`.
            // duplicacy prompts for THREE values (in order):
            //   1. "Enter old password for storage X" — answered via
            //      DUPLICACY_PASSWORD env var so it skips the stdin
            //      prompt.
            //   2. "Enter new storage password"  — first stdin line.
            //   3. "Re-enter new storage password" — second stdin line.
            // RunRawAsync writes `storagePasswordOnStdin` twice (it was
            // designed for init's confirm-password flow), so passing the
            // new password here covers both prompts (2) and (3).
            var env = new Dictionary<string, string>(
                BuildCloudEnv(destination, storageName))
            {
                ["DUPLICACY_PASSWORD"] = oldPassword,
            };
            var args = new List<string>(Verbosity())
            {
                "password",
                "-storage", storageName,
            };
            var exit = await RunRawAsync(
                repoDir, args,
                storagePasswordOnStdin: newPassword,
                ct,
                line: tee,
                envVars: env);
            return (exit, captured.ToString());
        }
        finally
        {
            // Best-effort cleanup of the synthetic repo dir. A leak here
            // is harmless — the directory is small, and the next probe
            // sweep / restart cleans up.
            try { if (Directory.Exists(repoDir)) Directory.Delete(repoDir, recursive: true); }
            catch (Exception ex)
            {
                _log.Warning(ex, "Couldn't delete synthetic password-change repo at {Path}", repoDir);
            }
        }
    }

    /// <summary>
    /// Variant of <see cref="EnsureInitializedAsync"/> that takes the
    /// storage password as an explicit argument instead of reading it
    /// from <see cref="SecretsStore"/>. Used by the password-change
    /// flow where we need to init with the OLD password (the secrets
    /// store still has the old value at that point — we update it
    /// only after the change succeeds).
    /// </summary>
    private async Task InitWithExplicitPasswordAsync(
        Backup backup, string sourcePath, Destination destination,
        string storageName, string explicitPassword, Action<string> line,
        CancellationToken ct)
    {
        var repoDir = AppPaths.RepoDirForBackupSource(backup.Name, sourcePath);
        Directory.CreateDirectory(repoDir);
        var prefDir = Path.Combine(repoDir, ".duplicacy");
        Directory.CreateDirectory(prefDir);
        File.WriteAllText(Path.Combine(prefDir, "filters"), "");

        var snapshotId = SnapshotIdFor(backup, sourcePath);
        var args = new List<string> { "init" };
        if (destination.Encrypted) args.Add("-encrypt");
        args.Add("-storage-name"); args.Add(storageName);
        args.Add("-repository");   args.Add(sourcePath);
        args.Add(snapshotId);
        args.Add(destination.BuildStorageUrl());

        var env = BuildCloudEnv(destination, storageName);
        var captured = new StringBuilder();
        Action<string> tee = ln =>
        {
            captured.AppendLine(ln);
            try { line(ln); } catch { /* ignore subscriber failures */ }
        };
        // init / password-change preflight is sub-second — no idle
        // watchdog needed. Skipping the watchdog avoids a 30-second
        // Task.Delay allocation + Task.Run for every short spawn.
        var exit = await RunRawAsync(repoDir, args, explicitPassword, ct, line: tee, envVars: env, enableIdleWatchdog: false);
        if (exit != 0)
        {
            throw new DuplicacyInitException(
                $"`duplicacy init` (password-change preflight) failed with exit code {exit}. " +
                $"This usually means the old password is wrong or the destination is unreachable. " +
                $"Output:\n{captured.ToString().TrimEnd()}",
                exit, captured.ToString().TrimEnd());
        }
    }

    public Task<RunRecord> EnumOnlyAsync(Backup backup, string sourcePath, Destination destination, string storageName, CancellationToken ct, Action<string>? lineSink = null)
        => RunSequenceAsync(backup, sourcePath, destination, storageName, new[] { DuplicacyOperation.EnumOnly }, ct, lineSink);

    // ---- repository lifecycle ----

    /// <summary>
    /// Stable snapshot id for a (backup, sourcePath) cell. Duplicacy uses
    /// snapshot id as a directory name under the storage root and rejects
    /// anything outside <c>[A-Za-z0-9_-]</c> — including Unicode letters
    /// like 'é', which the previous <c>char.IsLetterOrDigit</c>-based
    /// sanitiser was waving through ("Modules - Scripts pour télécharger"
    /// → kept the é, init failed with "invalid snapshot id"). Now:
    /// <list type="bullet">
    ///   <item>Single-source backup → <c>{Name}</c> (sanitised).</item>
    ///   <item>Multi-source backup → <c>{Name}-{6-char hex hash of
    ///   sourcePath}</c>. The hash gives each cell a stable, ASCII-safe
    ///   suffix without exposing the source path's unicode/spaces/slashes
    ///   to Duplicacy.</item>
    /// </list>
    /// Collisions across machines are guarded by the destination probe
    /// run at save time (cf. <see cref="DestinationProbe"/>), which is
    /// what the user pointed at when asking for this redesign.
    /// </summary>
    public static string SnapshotIdFor(Backup backup, string sourcePath)
    {
        // Prefer the immutable SnapshotIdSeed so a rename of the
        // user-facing Name doesn't fork a new chain on the storage.
        // Legacy backups (created before SnapshotIdSeed existed) have
        // an empty seed; for those we fall back to the current Name
        // so their existing chain stays addressable.
        var seed = !string.IsNullOrEmpty(backup.SnapshotIdSeed)
            ? backup.SnapshotIdSeed
            : backup.Name;
        var name = SanitizeSnapshotName(seed);
        if (backup.SourcePaths.Count <= 1) return name;
        return $"{name}-{HashSourcePath(sourcePath)}";
    }

    /// <summary>
    /// Deterministic 6-char hex hash of a source path. Stable across
    /// runs (so a saved backup keeps the same snapshot id forever) and
    /// purely ASCII (so it's always Duplicacy-safe).
    /// </summary>
    private static string HashSourcePath(string sourcePath)
    {
        var normalised = (sourcePath ?? "").TrimEnd('\\', '/');
        var bytes = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalised));
        var sb = new System.Text.StringBuilder(6);
        for (int i = 0; i < 3; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    private static string SanitizeSnapshotName(string s)
    {
        // Duplicacy snapshot-id alphabet is strictly ASCII
        // <c>[A-Za-z0-9_-]</c>. char.IsLetterOrDigit is Unicode-aware
        // (returns true for é, ñ, etc.) — so we use an explicit ASCII
        // range check instead. Anything outside the alphabet collapses
        // to '_' so the result stays a single contiguous token.
        if (string.IsNullOrEmpty(s)) return "_";
        var buf = new char[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            bool ok = (c >= 'A' && c <= 'Z')
                   || (c >= 'a' && c <= 'z')
                   || (c >= '0' && c <= '9')
                   || c == '-' || c == '_';
            buf[i] = ok ? c : '_';
        }
        return new string(buf);
    }

    /// <summary>
    /// Ensures a per-(backup, source) repo directory exists under
    /// <c>%LOCALAPPDATA%\Duplimate\repos\&lt;backup-name&gt;\&lt;source-slug&gt;\</c>
    /// and is <c>duplicacy init</c>'d for the given destination.
    ///
    /// Throws <see cref="DuplicacyInitException"/> (a NonRetriable error)
    /// when init fails — earlier this method swallowed the exit code,
    /// which let the orchestrator march on into <c>duplicacy backup</c>
    /// with no preferences file present. That's the failure mode behind
    /// the user-reported PREFERENCE_OPEN cascade in the 2026-04-28 log:
    /// init silently fails (bad token, unreachable bucket, encryption-
    /// password mismatch) → no preferences are written → every backup
    /// retry hits the same wall and wastes ~91s with no diagnostic.
    /// Capturing init's stdout in <see cref="DuplicacyInitException.Output"/>
    /// surfaces the real reason in the failure toast / email / logs.
    /// </summary>
    public Task EnsureInitializedAsync(Backup backup, string sourcePath, Destination destination, string storageName, CancellationToken ct)
        => EnsureInitializedAsync(backup, sourcePath, destination, storageName, ct, line: null);

    public async Task EnsureInitializedAsync(Backup backup, string sourcePath, Destination destination, string storageName, CancellationToken ct, Action<string>? line)
    {
        var repoDir = AppPaths.RepoDirForBackupSource(backup.Name, sourcePath);
        Directory.CreateDirectory(repoDir);

        // Write the filters file (Duplicacy reads .duplicacy/filters automatically).
        var prefDir = Path.Combine(repoDir, ".duplicacy");
        Directory.CreateDirectory(prefDir);
        File.WriteAllText(Path.Combine(prefDir, "filters"), backup.FiltersText ?? "");

        // Multi-target init logic (the reason this isn't just "if prefs
        // exist, return"). A backup with N targets shares one repo dir
        // per source — each target uses a different Duplicacy "storage
        // name". Duplicacy stores those names in `.duplicacy/preferences`
        // and refuses to use a storage that isn't registered ("No
        // storage named 'targetN' is found"). The first target gets
        // registered via `duplicacy init`; subsequent targets must be
        // registered via `duplicacy add` — and the original code
        // skipped that step entirely whenever the prefs file already
        // existed.
        var prefsFile = Path.Combine(prefDir, "preferences");
        var alreadyRegistered = PreferencesContainsStorageName(prefsFile, storageName);
        if (alreadyRegistered)
        {
            // Self-heal: older runs (pre-2026-04-29) added storages
            // without `-repository`, so their preferences entry has an
            // empty repository field. Without it, `duplicacy backup`
            // indexes cwd (the generated repoDir, only containing
            // .duplicacy/) and emits SNAPSHOT_EMPTY. Patch the entry's
            // repository field in place so existing setups recover on
            // their next run instead of silently failing forever.
            try { TryPatchStorageRepository(prefsFile, storageName, sourcePath); }
            catch (Exception ex) { _log.Warning(ex, "Failed to patch repository for {Storage}", storageName); }

            // Proactive recovery for STORAGE_NOT_CONFIGURED. Local-
            // folder destinations have a `config` file at their
            // storage root that Duplicacy reads to validate the
            // store. If the user manually deleted the destination
            // folder (or its contents), preferences still says
            // "registered" but the storage itself is gone — duplicacy
            // then fails with STORAGE_NOT_CONFIGURED. Detect this
            // here for local-like destinations and fall through to
            // the init/add path so the storage is re-created.
            // Cloud destinations (Dropbox, S3, etc.) skip this probe
            // — a HEAD round-trip would slow every run, and the
            // common-case "user wiped the destination" doesn't apply
            // to remote storages they don't directly manage.
            if (destination.IsLocalLike && IsLocalStorageMissing(destination))
            {
                _log.Warning(
                    "Local storage at {Path} is missing its config file — forcing re-init for {Storage}",
                    destination.PathOrSubpath, storageName);
                line?.Invoke(StampNow($"=== storage missing — re-initializing {destination.Name} ==="));
                // Delete the stale preferences entry so the code
                // below treats this as a fresh init/add.
                try { TryRemoveStorageEntry(prefsFile, storageName); } catch (Exception ex)
                { _log.Warning(ex, "Failed to remove stale preferences entry for {Storage}", storageName); }
                // Fall through to the init/add path below.
            }
            else
            {
                return;
            }
        }

        var prefsExists = File.Exists(prefsFile);
        var snapshotId = SnapshotIdFor(backup, sourcePath);

        // First target on this repo → `init` (creates preferences).
        // Subsequent target on this repo → `add` (appends another
        // storage entry to preferences).
        var subcommand = prefsExists ? "add" : "init";
        var args = new List<string> { subcommand };
        if (destination.Encrypted) args.Add("-encrypt");
        // `-repository <sourcePath>` MUST be passed to BOTH init AND add
        // (user-reported 2026-04-29: Dropbox cell indexed the Duplimate
        // repo dir instead of the actual source D:\Antigravity\..., logging
        // SNAPSHOT_EMPTY "No files under the repository to be backed up").
        // Root cause: `add` previously omitted -repository, so the new
        // storage entry's `repository` field in .duplicacy/preferences
        // was empty. At backup time `duplicacy backup -storage <name>`
        // runs from cwd=repoDir; with no per-storage repository field
        // duplicacy falls back to the cwd, which is the generated
        // .duplicacy holder dir (only contains .duplicacy/, no source
        // files → SNAPSHOT_EMPTY). The local cell only worked by
        // coincidence — it happened to be the FIRST init in some
        // configurations, getting the per-storage repository written
        // by init's -repository flag. Now both subcommands stamp the
        // per-storage repository explicitly so order doesn't matter.
        if (subcommand == "init")
        {
            args.Add("-storage-name"); args.Add(storageName);
            args.Add("-repository");   args.Add(sourcePath);
            args.Add(snapshotId);
            args.Add(destination.BuildStorageUrl());
        }
        else
        {
            args.Add("-repository");   args.Add(sourcePath);
            args.Add(storageName);
            args.Add(snapshotId);
            args.Add(destination.BuildStorageUrl());
        }

        var storagePassword = destination.StoragePasswordRef is null
            ? ""
            : _secrets.Get(destination.StoragePasswordRef);
        var env = BuildCloudEnv(destination, storageName);

        // Capture stdout so a failed init/add can blame itself instead
        // of dumping the user into "preference file not found" 8 lines
        // later. Pipe through to LineWritten and the caller-supplied
        // line callback so the per-cell log file (set up in
        // RunSequenceAsync) captures the output.
        var captured = new System.Text.StringBuilder();
        Action<string> tee = ln =>
        {
            captured.AppendLine(ln);
            try { LineWritten?.Invoke(ln); } catch { /* swallow — log shouldn't kill init */ }
            try { line?.Invoke(ln); } catch { }
        };
        // init/add are sub-second — opt out of the idle watchdog to
        // avoid the 30 s Task.Delay allocation + Task.Run for every
        // short spawn (one per cell × N cells per backup).
        var exit = await RunRawAsync(repoDir, args, storagePassword, ct, line: tee, envVars: env, enableIdleWatchdog: false);
        if (exit != 0)
        {
            // Best-effort: nuke the half-written .duplicacy folder on a
            // FIRST-time init so a retry isn't fooled into thinking
            // we're already initialised (preferences file exists but
            // is invalid). On `add`, leave it alone — the existing
            // storages are still good.
            if (subcommand == "init")
            {
                try { if (File.Exists(prefsFile)) File.Delete(prefsFile); }
                catch { }
            }
            var output = captured.ToString().TrimEnd();

            // Specialised diagnosis: when init fails because the
            // destination already has a Duplicacy storage we can't read
            // (typical case: previously encrypted with a different
            // password, or a stranded storage from an older install),
            // Duplicacy emits "The storage is likely to have been
            // initialized with a password before". Re-throw as the
            // typed exception so the UI can offer a real recovery path
            // ("enter the existing password / erase storage / pick a
            // different folder") instead of just printing the cryptic
            // duplicacy line. The previous behaviour was to surface a
            // generic "init failed with exit code 100" — the user
            // reported this as confusing and blocked them entirely.
            if (output.Contains("likely to have been initialized with a password",
                                 StringComparison.OrdinalIgnoreCase))
            {
                throw new StoragePreviouslyInitializedException(
                    destination.Id, destination.Name, destination.BuildStorageUrl(),
                    exit, output);
            }

            throw new DuplicacyInitException(
                $"`duplicacy {subcommand}` failed with exit code {exit}. " +
                $"Output:\n{(string.IsNullOrEmpty(output) ? "(none)" : output)}",
                exit, output);
        }
    }

    /// <summary>
    /// Parse a Duplicacy <c>preferences</c> file (a JSON array of
    /// storage entries) and return true iff one of them has the given
    /// <paramref name="storageName"/>. Returns false if the file
    /// doesn't exist, can't be read, or doesn't contain a matching
    /// entry. Invalid JSON is treated as "not registered" so the
    /// caller falls through to the init/add path and surfaces the
    /// real problem.
    /// </summary>
    /// <summary>
    /// True iff this is a local-like destination (LocalFolder /
    /// ExternalDrive / NetworkShare) whose storage root is missing
    /// its <c>config</c> file. Duplicacy writes <c>config</c> at the
    /// root of every initialised storage; absence means either the
    /// destination was never initialised here or the user wiped it.
    /// Either way, calling this and seeing true means we must
    /// re-init the storage before backup or duplicacy will fail
    /// with STORAGE_NOT_CONFIGURED.
    /// </summary>
    private static bool IsLocalStorageMissing(Destination destination)
    {
        try
        {
            var path = destination.PathOrSubpath;
            if (string.IsNullOrWhiteSpace(path)) return false;
            var configFile = Path.Combine(path, "config");
            return !File.Exists(configFile);
        }
        catch { return false; }
    }

    /// <summary>
    /// Remove a stale storage entry from <c>.duplicacy/preferences</c>.
    /// Called when we detect the underlying storage is gone — without
    /// this, the next init pass sees the entry, thinks it's already
    /// registered, and skips back to the same broken state.
    /// </summary>
    private static void TryRemoveStorageEntry(string prefsFile, string storageName)
    {
        if (!File.Exists(prefsFile)) return;
        var json = File.ReadAllText(prefsFile);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return;

        var kept = new List<System.Text.Json.JsonElement>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (entry.TryGetProperty("name", out var nameProp)
                && nameProp.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(nameProp.GetString(), storageName, StringComparison.Ordinal))
            {
                continue; // drop
            }
            kept.Add(entry);
        }

        // Re-serialise the surviving entries.
        using var ms = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms,
            new System.Text.Json.JsonWriterOptions { Indented = true }))
        {
            w.WriteStartArray();
            foreach (var e in kept) e.WriteTo(w);
            w.WriteEndArray();
        }
        File.WriteAllBytes(prefsFile, ms.ToArray());
    }

    private static bool PreferencesContainsStorageName(string prefsFile, string storageName)
    {
        if (!File.Exists(prefsFile)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(prefsFile));
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return false;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("name", out var nameProp)
                    && nameProp.ValueKind == System.Text.Json.JsonValueKind.String
                    && string.Equals(nameProp.GetString(), storageName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Couldn't parse preferences {Path} — treating as not-registered", prefsFile);
        }
        return false;
    }

    /// <summary>
    /// Repair an existing preferences file's storage entry by setting
    /// its <c>repository</c> field to <paramref name="sourcePath"/> if
    /// it's currently empty or missing. We deliberately don't touch a
    /// non-empty mismatching value — the user may have manually
    /// configured a different repo, and silently overwriting their
    /// choice would be a surprise. No-op when the file is missing or
    /// unparseable; this is best-effort housekeeping, never a fatal
    /// step. Targets only the named storage so a multi-storage
    /// preferences file with one valid entry and one stale entry only
    /// patches the stale one.
    /// </summary>
    private static void TryPatchStorageRepository(string prefsFile, string storageName, string sourcePath)
    {
        if (!File.Exists(prefsFile)) return;
        if (string.IsNullOrEmpty(sourcePath)) return;
        var raw = File.ReadAllText(prefsFile);
        // Round-trip via System.Text.Json with case-preserving property
        // names so we don't accidentally rewrite the file with a
        // different key casing — duplicacy's own writer uses lowercase
        // and we mirror that.
        var nodes = System.Text.Json.Nodes.JsonNode.Parse(raw) as System.Text.Json.Nodes.JsonArray;
        if (nodes is null) return;
        bool patched = false;
        foreach (var node in nodes)
        {
            if (node is not System.Text.Json.Nodes.JsonObject obj) continue;
            var name = obj["name"]?.GetValue<string>();
            if (!string.Equals(name, storageName, StringComparison.Ordinal)) continue;
            var existing = obj["repository"]?.GetValue<string>();
            if (string.IsNullOrEmpty(existing))
            {
                obj["repository"] = sourcePath;
                patched = true;
            }
            break;
        }
        if (patched)
        {
            // Match duplicacy's own preferences-writer style as closely
            // as possible so a side-by-side diff against a duplicacy-
            // rewritten file doesn't churn noise on every patch. The
            // duplicacy CLI uses Go's json.MarshalIndent with one-tab
            // indentation; System.Text.Json doesn't expose tab-indent
            // in current .NET, so we hand-build the output via a
            // Utf8JsonWriter with two-space indent (the closest stable
            // approximation that still parses identically) and a final
            // newline to match the trailing-EOL convention.
            using var ms = new MemoryStream();
            var writerOpts = new System.Text.Json.JsonWriterOptions { Indented = true };
            using (var writer = new System.Text.Json.Utf8JsonWriter(ms, writerOpts))
            {
                nodes.WriteTo(writer);
            }
            ms.WriteByte((byte)'\n');
            File.WriteAllBytes(prefsFile, ms.ToArray());
            _log.Information("Patched preferences {Path}: storage {Storage} repository → {Source}",
                prefsFile, storageName, sourcePath);
        }
    }

    public async Task ReInitializeForRestoreAsync(Backup backup, string sourcePath, Destination destination, string storageName, string restoreRepoPath, CancellationToken ct)
    {
        Directory.CreateDirectory(restoreRepoPath);
        var snapshotId = SnapshotIdFor(backup, sourcePath);
        var args = new List<string>
        {
            "init",
            "-repository", restoreRepoPath,
            "-storage-name", storageName,
        };
        if (destination.Encrypted) args.Add("-encrypt");
        args.Add(snapshotId);
        args.Add(destination.BuildStorageUrl());

        var storagePassword = destination.StoragePasswordRef is null
            ? ""
            : _secrets.Get(destination.StoragePasswordRef);
        var env = BuildCloudEnv(destination, storageName);

        // ReInitializeForRestoreAsync is a sub-second init shim — no
        // idle watchdog allocation needed.
        await RunRawAsync(restoreRepoPath, args, storagePassword, ct, envVars: env, enableIdleWatchdog: false);
    }

    // ---- core loop ----

    private async Task<RunRecord> RunSequenceAsync(
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        IReadOnlyList<DuplicacyOperation> operations,
        CancellationToken ct,
        Action<string>? lineSink = null)
    {
        // Log file FIRST, init second. Earlier the order was reversed:
        // EnsureInitializedAsync ran first and could throw — leaving
        // the per-run log file uncreated, so the user-facing "Selected
        // run" pane in the Logs view rendered empty for any backup
        // that failed during init. Now init's stdout (including
        // failure diagnostics) flows through the same Line() pump that
        // writes to the log file, so the user can read why init failed
        // without having to dig into the app log.
        var repoDir = AppPaths.RepoDirForBackupSource(backup.Name, sourcePath);
        var logPath = PrepareLogFile(backup.Name);
        // try/finally below disposes this on every return path. Earlier
        // the success path and most non-init failure paths leaked the
        // file handle, leaving per-run log files locked against
        // LogStore's orphan-prune until process exit and eventually
        // hitting the Win32 handle quota on long-running unattended
        // hosts.
        var logStream = new StreamWriter(logPath, append: false) { AutoFlush = true };

        var record = new RunRecord
        {
            BackupId = backup.Id,
            BackupName = backup.Name,
            StartedUtc = DateTime.UtcNow,
            LogPath = logPath,
            Status = BackupRunStatus.Running,
        };

        var errorsCaught = 0;
        string? nonRetriableCode = null;

        void Line(string line)
        {
            try { logStream.WriteLine(line); } catch { /* log failure shouldn't abort the run */ }
            // Per-call lineSink fires BEFORE the shared event. Concurrent
            // runs (one per Backup.Id, since the cross-process gate is
            // per-id) write into the SAME singleton runner; if both ends
            // subscribed to LineWritten the second cell's lines would
            // arrive at the first cell's handler too. The per-call sink
            // routes lines to exactly the cell that triggered them.
            // LineWritten remains for the global "live tail" subscriber
            // (LogsViewModel) which wants the union of all runs.
            if (lineSink is not null) try { lineSink(line); } catch { }
            LineWritten?.Invoke(line);
            ParseLine(line, record, ref errorsCaught);
            // Watch for known-fatal duplicacy ERROR codes — when one
            // shows up we'll throw NonRetriableDuplicacyException after
            // the operation exits, so the orchestrator can skip the
            // 5/20/60s retry cascade for an error that won't heal.
            if (nonRetriableCode is null)
                nonRetriableCode = DuplicacyErrorClassifier.ClassifyNonRetriable(line);
        }

        try
        {
            Line(StampNow($"=== {backup.Name} ({sourcePath}) → {destination.Name} ({destination.BuildStorageUrl()}) ==="));

            // Init runs AFTER the Line pump is set up so its output lands
            // in the log file. On failure we re-throw so the orchestrator
            // sees the non-retriable signal, but the log file already
            // captures what went wrong.
            try
            {
                Line(StampNow("--- init (ensure preferences exist) ---"));
                await EnsureInitializedAsync(backup, sourcePath, destination, storageName, ct, line: Line);
            }
            catch (DuplicacyInitException ex)
            {
                Line(StampNow($"=== INIT FAILED: {ex.Message} ==="));
                record.Errors.Add(ex.Message);
                record.Status = BackupRunStatus.Failed;
                record.EndedUtc = DateTime.UtcNow;
                record.Summary = "init failed";
                ex.LogPath = logPath;  // so the orchestrator can plumb it into CellOutcome.LogPath
                throw;
            }

            foreach (var op in operations)
            {
                if (ct.IsCancellationRequested) break;

                Line(StampNow($"--- {op} ---"));
                var exitCode = await RunOperationAsync(op, backup, sourcePath, destination, storageName, repoDir, Line, ct);
                if (exitCode != 0)
                {
                    record.Errors.Add($"{op} exited with code {exitCode}");
                    if (op is DuplicacyOperation.Backup or DuplicacyOperation.Init or DuplicacyOperation.PruneAll)
                    {
                        // Non-retriable error spotted in stdout (PREFERENCE_OPEN
                        // and friends) — propagate as an exception that the
                        // orchestrator's RunCellWithRetryAsync recognizes and
                        // skips the retry cascade for. Ensures init / config
                        // mistakes fail fast with the real error message.
                        if (nonRetriableCode is not null)
                            throw new NonRetriableDuplicacyException(
                                nonRetriableCode,
                                $"{op} hit a non-retriable Duplicacy error ({nonRetriableCode}). " +
                                "Fix the underlying configuration and try again.");

                        // Fatal for the run but retriable.
                        record.Status = ct.IsCancellationRequested ? BackupRunStatus.Skipped : BackupRunStatus.Failed;
                        record.EndedUtc = DateTime.UtcNow;
                        return record;
                    }
                    // Prune/check failures are logged but don't fail the whole run.
                }
            }

            record.EndedUtc = DateTime.UtcNow;
            if (ct.IsCancellationRequested)
                record.Status = BackupRunStatus.Skipped;
            else if (record.Errors.Count > 0)
                record.Status = BackupRunStatus.Warning;
            else
                record.Status = BackupRunStatus.Success;

            record.Summary = BuildSummary(record);
            Line(StampNow($"=== done: {record.Status} — {record.Summary} ==="));
            return record;
        }
        finally
        {
            try { logStream.Flush(); } catch { }
            try { logStream.Dispose(); } catch { }
        }
    }

    private async Task<int> RunOperationAsync(
        DuplicacyOperation op,
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        string repoDir,
        Action<string> line,
        CancellationToken ct)
    {
        var args = BuildArgs(op, backup, sourcePath, storageName, destination);
        var password = destination.StoragePasswordRef is null ? "" : _secrets.Get(destination.StoragePasswordRef);
        var env = BuildCloudEnv(destination, storageName);
        return await RunRawAsync(repoDir, args, password, ct, line, envVars: env);
    }

    // sourcePath is accepted (and currently unused) for a future
    // per-source arg (e.g. "-repository" override) — kept in the
    // signature so callers don't have to change again when we need it.
    /// <summary>
    /// Resolve the effective upload-thread count for a given (backup,
    /// destination) cell. Priority: user-set per-target override → user-set
    /// global on the backup → per-kind recommended default. Caller passes
    /// the destination because Backup.Threads=0 means "auto" and we need
    /// the kind to pick a sensible number (1 for local / network, 4 for
    /// cloud). See DestinationKindExtensions.RecommendedThreads.
    /// </summary>
    public static int EffectiveThreads(Backup backup, Destination destination, int? perTargetOverride = null)
    {
        if (perTargetOverride is int per && per > 0) return per;
        if (backup.Threads > 0) return backup.Threads;
        return destination.Kind.RecommendedThreads();
    }

    /// <summary>
    /// Global verbosity preamble for every duplicacy.exe invocation.
    /// <c>-log</c> flips on the structured <c>YYYY-MM-DD HH:mm:ss.fff LEVEL CATEGORY message</c>
    /// format that <see cref="ParseLine"/> depends on (without it, output
    /// is bare stdout which we can't classify into warnings/errors).
    /// <c>-d</c> turns on Duplicacy's DEBUG/TRACE level — gigantic dumps
    /// of compiled-regex internals, every PASSWORD_ENV_VAR read, every
    /// VSS struct field, every filter pattern. Useful for diagnosing
    /// failures, way too noisy for everyday runs (the user reported
    /// hundreds of lines per backup), so we gate it behind
    /// <c>DUPLIMATE_DEBUG=1</c> via <see cref="AppLogger.IsDebugMode"/>.
    /// </summary>
    // Cached so each spawn doesn't allocate a fresh array. Verbosity is
    // immutable for the process lifetime — DUPLIMATE_DEBUG is read once at
    // AppLogger.Initialize and doesn't change.
    private static readonly string[] _verbosityDebug = { "-d", "-log" };
    private static readonly string[] _verbosityNormal = { "-log" };

    private static IEnumerable<string> Verbosity()
        => AppLogger.IsDebugMode ? _verbosityDebug : _verbosityNormal;

    private static List<string> BuildArgs(DuplicacyOperation op, Backup backup, string sourcePath, string storageName, Destination destination)
    {
        var v = Verbosity();
        var args = op switch
        {
            DuplicacyOperation.Backup => new(v)
            {
                "backup",
                "-storage", storageName,
                "-threads", EffectiveThreads(backup, destination).ToString(),
                backup.UseVss ? "-vss" : "",
                "-stats",
            },
            DuplicacyOperation.EnumOnly => new(v)
            {
                "backup",
                "-storage", storageName,
                "-enum-only",
                backup.UseVss ? "-vss" : "",
            },
            DuplicacyOperation.Prune => BuildPruneArgs(backup.KeepPolicy, storageName, exhaustive: false),
            DuplicacyOperation.PruneAll => BuildPruneArgs("0:1", storageName, exhaustive: true),
            DuplicacyOperation.Check => new(v)
            {
                "check",
                "-storage", storageName,
            },
            DuplicacyOperation.List => new(v)
            {
                "list",
                "-storage", storageName,
            },
            DuplicacyOperation.Init => new(),  // handled separately
            _ => new List<string>(),
        };

        // Bandwidth throttling. Duplicacy's -limit-rate is in KB/s and
        // applies to upload-heavy ops (backup, copy). Apply on backup
        // ops; pruning/checking/listing are tiny and not worth limiting.
        if (backup.BandwidthLimitKBps > 0
            && op is DuplicacyOperation.Backup or DuplicacyOperation.EnumOnly)
        {
            args.Add("-limit-rate");
            args.Add(backup.BandwidthLimitKBps.ToString());
        }

        // Drop empties (the conditional "" entries above for -vss off).
        args.RemoveAll(string.IsNullOrEmpty);

        // Append user's free-form extras at the end so they override any
        // earlier flags duplicacy would otherwise see.
        if (op is DuplicacyOperation.Backup
            && !string.IsNullOrWhiteSpace(backup.ExtraBackupArgs))
        {
            foreach (var token in SplitArgString(backup.ExtraBackupArgs))
                args.Add(token);
        }

        return args;
    }

    /// <summary>
    /// Splits a free-form extra-args string the same way a shell would —
    /// honors single/double quotes so paths with spaces survive intact.
    /// </summary>
    internal static IEnumerable<string> SplitArgString(string raw)
    {
        var sb = new System.Text.StringBuilder();
        char? quote = null;
        for (int i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (quote is null && (c == '"' || c == '\''))
            {
                quote = c;
            }
            else if (quote == c)
            {
                quote = null;
            }
            else if (quote is null && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>
    /// Build the <c>duplicacy prune</c> arg list. The CLI wants one
    /// <c>-keep</c> flag per policy pair — passing <c>"-keep 0:365 30:90 7:30 1:7"</c>
    /// as a single token is rejected as "Invalid retention policy".
    /// We store the policy in the <see cref="Backup.KeepPolicy"/> string
    /// as whitespace-separated pairs (legacy duplicacy-util format) and
    /// fan them out here.
    /// </summary>
    private static List<string> BuildPruneArgs(string keepPolicy, string storageName, bool exhaustive)
    {
        var args = new List<string>(Verbosity()) { "prune", "-storage", storageName };
        if (!string.IsNullOrWhiteSpace(keepPolicy))
        {
            var pairs = keepPolicy.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                args.Add("-keep");
                args.Add(pair);
            }
        }
        if (exhaustive) args.Add("-exhaustive");
        return args;
    }

    private async Task<int> RunRawAsync(
        string workingDir,
        IEnumerable<string> args,
        string? storagePasswordOnStdin,
        CancellationToken ct,
        Action<string>? line = null,
        IReadOnlyDictionary<string, string>? envVars = null,
        bool enableIdleWatchdog = true)
    {
        var duplicacyPath = DuplicacyEmbedder.EnsureExtracted();

        var psi = new ProcessStartInfo
        {
            FileName = duplicacyPath,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            if (!string.IsNullOrEmpty(a)) psi.ArgumentList.Add(a);

        // Duplicacy reads cloud credentials and storage passwords from env
        // vars when present (DUPLICACY_PASSWORD, DUPLICACY_DROPBOX_TOKEN,
        // DUPLICACY_S3_ID, etc). That lets us skip the interactive prompt
        // ordering headache — one env var per secret, order-independent.
        if (envVars is not null)
        {
            foreach (var (k, v) in envVars)
                if (!string.IsNullOrEmpty(v)) psi.Environment[k] = v;
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        // Track last-output time across both stdout and stderr — drives the
        // idle-output watchdog below. Volatile reads are fine for our
        // monotonic "last seen" tick.
        var lastOutputUtc = DateTime.UtcNow;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lastOutputUtc = DateTime.UtcNow;
            line?.Invoke(StampNow(e.Data));
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lastOutputUtc = DateTime.UtcNow;
            line?.Invoke(StampNow("[err] " + e.Data));
        };

        process.Start();
        // Assign to the kill-on-exit Job Object so a crashed-app or
        // hard-killed Duplimate doesn't orphan duplicacy.exe in
        // Task Manager. The Process.Kill cooperative path inside the
        // CancellationTokenRegistration below handles graceful cancel;
        // the job object is the safety net for catastrophic exit.
        KillOnExitJobObject.TryAssign(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Duplicacy prompts for passwords on stdin. Feed what we have, flush,
        // and close. Without the explicit FlushAsync, on a slow pipe the
        // bytes can sit in the StreamWriter buffer when Close runs, causing
        // duplicacy to hang waiting for input.
        if (!string.IsNullOrEmpty(storagePasswordOnStdin))
        {
            try
            {
                await process.StandardInput.WriteLineAsync(storagePasswordOnStdin);
                // Also feed it again for the "confirm password" prompt that init shows.
                await process.StandardInput.WriteLineAsync(storagePasswordOnStdin);
                await process.StandardInput.FlushAsync();
            }
            catch { /* if the process never asks, the pipe is just closed */ }
        }
        try { process.StandardInput.Close(); } catch { }

        // Idle-output watchdog: if duplicacy goes silent for IdleTimeout
        // we assume it's hung (network stall, deadlocked thread, etc.) and
        // kill the process tree. Active backups emit continuous progress
        // lines so legitimate long-running runs are unaffected; this only
        // cuts in when something is actually stuck.
        // Skipped for opt-out callers (probes, init-only spawns) — they
        // run sub-second and don't need a 30s polling loop + Task.Delay
        // allocation per spawn.
        using var idleCts = new CancellationTokenSource();
        var idleWatcher = enableIdleWatchdog
            ? Task.Run(async () =>
            {
                // Catch ALL exceptions, not just TaskCanceledException.
                // After the catch path below throws, `process` and
                // `idleCts` get disposed by their `using`s — if this
                // watcher is mid-Delay it'll wake up and touch
                // `process.HasExited` on a disposed Process
                // (InvalidOperationException), which would otherwise
                // surface as an unobserved-task exception and crash
                // the process at GC time.
                try
                {
                    while (!process.HasExited && !idleCts.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), idleCts.Token);
                        if (DateTime.UtcNow - lastOutputUtc > IdleTimeout)
                        {
                            _log.Warning("Duplicacy idle for {Min} min — killing process tree (PID {Pid})",
                                (int)IdleTimeout.TotalMinutes, process.Id);
                            SafeKill(process, "idle timeout");
                            break;
                        }
                    }
                }
                catch { /* watcher is best-effort; nothing useful to do on failure */ }
            }, idleCts.Token)
            : Task.CompletedTask;

        // Forward caller cancellation by killing the process tree. We pass
        // `ct` to WaitForExitAsync (instead of None) so cancellation
        // surfaces as a TaskCanceledException even if Kill itself fails
        // — the alternative was waiting forever on a process that
        // refused to die. The caller catches OperationCanceledException
        // upstream.
        using (ct.Register(() => SafeKill(process, "caller cancelled")))
        {
            try { await process.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                // Best-effort second kill in case the first one was
                // refused (race between Register firing and process
                // having actually exited).
                SafeKill(process, "post-cancel cleanup");
                // Tear the watchdog down BEFORE rethrowing. Without
                // this, the throw skipped the Cancel + await pair
                // below, leaving the watcher to wake up on a disposed
                // Process / CancellationTokenSource and fault with an
                // unobserved-task exception.
                try { idleCts.Cancel(); } catch { }
                try { await idleWatcher; } catch { }
                throw;
            }
        }

        // Stop the watchdog — process is gone.
        try { idleCts.Cancel(); } catch { }
        try { await idleWatcher; } catch { }

        return process.ExitCode;
    }

    /// <summary>
    /// Best-effort kill of duplicacy.exe and its child processes. Logs
    /// the failure when Kill throws (access denied / process already
    /// exited / handle invalid) so a stuck process doesn't disappear
    /// silently from operator visibility. Does not re-throw — the
    /// caller has already decided the process needs to die; surfacing
    /// a kill failure as an exception would only mask the original
    /// cancellation/idle reason.
    /// </summary>
    private static void SafeKill(Process process, string reason)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Couldn't kill duplicacy process tree (reason={Reason}, PID may be stale)", reason);
        }
    }

    /// <summary>
    /// How long the runner waits for duplicacy to produce ANY output line
    /// before assuming the process is hung and killing it. Backups emit
    /// continuous progress, so 15 minutes of silence is a strong signal
    /// that something is stuck (a deadlocked TLS handshake, a NAS that
    /// stopped responding, an OAuth refresh waiting for a server that's
    /// down). Set generously enough that slow cloud uploads with rate-
    /// limit backoffs don't trip it.
    /// </summary>
    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Resolves a destination's cloud credentials into the DUPLICACY_* env
    /// vars that duplicacy.exe reads in non-interactive mode. Covers the
    /// kinds Duplimate exposes today: Dropbox (app + full), OneDrive
    /// (personal + business), Google Drive, S3. Local/external/network
    /// destinations return an empty dict. The storage password (for
    /// -encrypt) is added when a StoragePasswordRef is set.
    ///
    /// Exposed so read-only spawners (<see cref="RevisionBrowser"/>) can
    /// apply the same credentials when they shell out to duplicacy outside
    /// of this runner's own code path.
    /// </summary>
    public Dictionary<string, string> BuildCloudEnv(Destination destination, string storageName)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        // Storage encryption password. Duplicacy looks at DUPLICACY_PASSWORD
        // (default storage) and DUPLICACY_<NAME>_PASSWORD (named). Set both
        // to be safe — whichever matches the current invocation wins.
        if (destination.StoragePasswordRef is not null)
        {
            var pwd = _secrets.Get(destination.StoragePasswordRef);
            if (!string.IsNullOrEmpty(pwd))
            {
                env["DUPLICACY_PASSWORD"] = pwd;
                env[$"DUPLICACY_{storageName.ToUpperInvariant()}_PASSWORD"] = pwd;
            }
        }

        // OAuth-based cloud providers — one env var per protocol prefix.
        if (destination.OAuthTokenRef is not null)
        {
            var token = _secrets.Get(destination.OAuthTokenRef);
            if (!string.IsNullOrEmpty(token))
            {
                var varName = destination.Kind switch
                {
                    DestinationKind.DropboxAppScoped  => "DROPBOX_TOKEN",
                    DestinationKind.DropboxFullAccess => "DROPBOX_TOKEN",
                    DestinationKind.OneDrivePersonal  => "ONE_TOKEN",
                    DestinationKind.OneDriveBusiness  => "ODB_TOKEN",
                    DestinationKind.GoogleDrive       => "GCD_TOKEN",
                    _ => null,
                };
                if (varName is not null)
                {
                    env[$"DUPLICACY_{varName}"] = token;
                    env[$"DUPLICACY_{storageName.ToUpperInvariant()}_{varName}"] = token;
                }
            }
        }

        // S3 + S3-compatible: access key and secret key.
        if (destination.Kind == DestinationKind.S3Compatible)
        {
            if (destination.S3AccessKeyRef is not null)
            {
                var id = _secrets.Get(destination.S3AccessKeyRef);
                if (!string.IsNullOrEmpty(id))
                {
                    env["DUPLICACY_S3_ID"] = id;
                    env[$"DUPLICACY_{storageName.ToUpperInvariant()}_S3_ID"] = id;
                }
            }
            if (destination.S3SecretKeyRef is not null)
            {
                var secret = _secrets.Get(destination.S3SecretKeyRef);
                if (!string.IsNullOrEmpty(secret))
                {
                    env["DUPLICACY_S3_SECRET"] = secret;
                    env[$"DUPLICACY_{storageName.ToUpperInvariant()}_S3_SECRET"] = secret;
                }
            }
        }

        return env;
    }

    // ---- helpers ----

    private static string PrepareLogFile(string backupName)
    {
        var dir = AppPaths.LogDirForBackup(backupName);
        Directory.CreateDirectory(dir);
        // UTC + millisecond precision + 6-char random suffix. Using
        // DateTime.Now means a backup run that starts an hour after a
        // DST fall-back (clocks rolled 02:59 → 02:00) would generate
        // the same filename as the prior run an hour earlier — the
        // second StreamWriter(append:false) would truncate the first
        // run's log. UTC sidesteps DST entirely; the milliseconds and
        // random suffix defend against millisecond-level collisions
        // when two cells of the same multi-source backup start in the
        // same tick.
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var rand = Guid.NewGuid().ToString("N").Substring(0, 6);
        var name = $"{stamp}-{rand}.log";
        return Path.Combine(dir, name);
    }

    private static string StampNow(string s) => $"{DateTime.Now:HH:mm:ss} {s}";

    private void ParseLine(string line, RunRecord record, ref int errorsCaught)
    {
        // Error signals: configurable substrings from MonitoringSettings.
        // Mirror these into the structured app log so they show up next
        // to the backup-lifecycle narrative, not only buried in the raw
        // duplicacy log.
        //
        // INFO/DEBUG/TRACE lines are EXEMPT from substring matching —
        // duplicacy frequently emits informational lines that mention
        // "ERROR" or "Failed to" inside their content (e.g.
        // "INFO RUN_INFO Failed to acquire lock, retrying" is a benign
        // retry message). Pre-fix, those were classifying clean Success
        // runs as Warning. The user reported: "backup ended with
        // warning, but I couldn't find anything wrong in the logs that
        // would justify a warning" — for a run whose persisted log
        // showed only INFO-level activity. Anchoring the pattern check
        // to actual WARN/ERROR/FATAL levels (or to lines with no
        // duplicacy header at all — those are stderr-prefixed `[err]`
        // lines or legacy text) restores the right semantics.
        if (LineIsActionableForErrorMatching(line))
        {
            foreach (var pat in _monitoringSnapshot.LocalErrorPatterns)
                if (line.Contains(pat, StringComparison.OrdinalIgnoreCase))
                {
                    record.Errors.Add(line);
                    errorsCaught++;
                    _log.Warning("duplicacy: {Line}", line);
                    break;
                }
        }

        // Structured markers Duplicacy emits.
        var m = RevisionRx.Match(line);
        if (m.Success)
        {
            record.RevisionNumber = int.Parse(m.Groups[1].Value);
            _log.Information("duplicacy: revision {Rev} created", record.RevisionNumber);
        }

        m = UploadedBytesRx.Match(line);
        if (m.Success)
        {
            record.BytesUploaded = long.Parse(m.Groups[1].Value);
            _log.Information("duplicacy: uploaded {Bytes} bytes", record.BytesUploaded);
        }

        m = NewModRemRx.Match(line);
        if (m.Success)
        {
            record.FilesNew = int.Parse(m.Groups[1].Value);
            record.FilesModified = int.Parse(m.Groups[2].Value);
            record.FilesRemoved = int.Parse(m.Groups[3].Value);
            _log.Information("duplicacy: {New} new / {Mod} modified / {Rem} removed",
                record.FilesNew, record.FilesModified, record.FilesRemoved);
        }
    }

    /// <summary>True iff <paramref name="line"/> is a line where
    /// substring-matching against <see cref="MonitoringSettings.LocalErrorPatterns"/>
    /// is still meaningful. We strip our own "HH:MM:SS " StampNow
    /// prefix before checking the duplicacy header — without that,
    /// every line would look "headerless" to the matcher and the
    /// INFO-exempt path would never fire.
    /// <list type="bullet">
    ///   <item><c>[err] ...</c> — stderr lines we ourselves prefixed; always actionable.</item>
    ///   <item><c>YYYY-MM-DD HH:MM:SS LEVEL ...</c> — only WARN/ERROR/FATAL are flagged.</item>
    ///   <item>Anything else (init banners, plain stderr text, legacy log lines) — still substring-checked.</item>
    /// </list>
    /// </summary>
    internal static bool LineIsActionableForErrorMatching(string line)
    {
        // Strip the "HH:MM:SS " StampNow prefix the runner adds to
        // every captured line — the duplicacy header sits AFTER it.
        var body = StampPrefixRx.IsMatch(line) ? line.Substring(9) : line;
        if (body.StartsWith("[err]", StringComparison.Ordinal)) return true;

        var header = DuplicacyLogHeaderRx.Match(body);
        if (!header.Success) return true; // headerless → fall through to substring match
        var level = header.Groups[1].Value;
        return string.Equals(level, "WARN",  StringComparison.OrdinalIgnoreCase)
            || string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase)
            || string.Equals(level, "FATAL", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Regex StampPrefixRx =
        new(@"^\d{2}:\d{2}:\d{2} ", RegexOptions.Compiled);

    private static readonly Regex DuplicacyLogHeaderRx = new(
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d+)? (INFO|DEBUG|TRACE|WARN|ERROR|FATAL) ",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Duplicacy log fragments we care about:
    //   "Backup for ... at revision 1234 completed"
    //   "Uploaded 12345 bytes"
    //   "Files: 10 new, 2 changed, 0 deleted"   (actual phrasing varies — regex is tolerant)
    private static readonly Regex RevisionRx =
        new(@"revision\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UploadedBytesRx =
        new(@"uploaded\s+(\d+)\s*bytes", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NewModRemRx =
        new(@"(\d+)\s+new,\s+(\d+)\s+(?:changed|modified),\s+(\d+)\s+(?:deleted|removed)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string BuildSummary(RunRecord r)
    {
        var duration = (r.EndedUtc ?? DateTime.UtcNow) - r.StartedUtc;
        var parts = new List<string>();
        if (r.RevisionNumber is int rev) parts.Add($"rev {rev}");
        if (r.FilesNew > 0 || r.FilesModified > 0 || r.FilesRemoved > 0)
            parts.Add($"{r.FilesNew} new / {r.FilesModified} mod / {r.FilesRemoved} rem");
        if (r.BytesUploaded > 0) parts.Add($"{HumanSize(r.BytesUploaded)} uploaded");
        parts.Add($"in {HumanDuration(duration)}");
        return string.Join(" · ", parts);
    }

    public static string HumanSize(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }

    public static string HumanDuration(TimeSpan d)
    {
        if (d.TotalSeconds < 60) return $"{d.TotalSeconds:0}s";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m {d.Seconds:00}s";
        if (d.TotalHours   < 24) return $"{(int)d.TotalHours}h {d.Minutes:00}m";
        return $"{(int)d.TotalDays}d {d.Hours:00}h";
    }

    private enum DuplicacyOperation { Init, Backup, EnumOnly, Prune, PruneAll, Check, List }
}
