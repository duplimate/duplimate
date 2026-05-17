using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Models;
using Serilog;

namespace Duplimate.Services;

/// <summary>
/// Lists the snapshot-ids already present inside a Destination's storage.
///
/// We use this at Backup-save time so the auto-name generator's 6-char
/// suffix can be proven unique against the REMOTE state as well as the
/// local config. That closes the cross-machine collision window the user
/// flagged (two machines rolling the same <c>{leaf}-{rand6}</c> into the
/// same Dropbox folder would otherwise mix unrelated snapshot chains).
///
/// Strategy per destination kind:
///   • Local / External / Network : enumerate <c>{root}/snapshots/*</c>
///     directly via the filesystem — instant, no side effects.
///   • Cloud (Dropbox, OneDrive, Google Drive) and S3 : shell out to
///     <c>duplicacy init</c> + <c>duplicacy list</c> against an ephemeral
///     repo. This is the same bridge <see cref="DropboxAuthProbe"/> uses
///     and it reuses the exact credential/OAuth path a real backup will
///     take, so if it works here it works later. ~2–5s round-trip
///     latency, acceptable for a one-shot save-time check.
///
/// Accepted race: two machines saving a Backup at the exact same second
/// can each see the other's candidate as "not taken yet" and both roll
/// the same suffix. Combined with 36^6 ≈ 2.2B, the practical failure
/// rate is negligible. (User has explicitly accepted this.)
/// </summary>
public sealed class DestinationProbe
{
    private readonly SecretsStore _secrets;
    private static ILogger _log => AppLogger.For<DestinationProbe>();

    // Actual duplicacy `-log list` output:
    //   2026-05-13 18:20:07.523 INFO SNAPSHOT_INFO Snapshot my-backup revision 42 created at ...
    // (without -log, the line starts at "Snapshot ..." but with the
    // same `Snapshot <id> revision N` shape — the older "Snapshot <id>
    // at revision N" form documented in earlier duplicacy versions
    // doesn't show up in current output anymore.) This regex tolerates
    // either: a leading timestamp/log-level prefix, and either "at
    // revision" or just "revision" between the id and the number.
    //
    // BUG HISTORY: the prior anchor `^Snapshot\s+...\s+at\s+revision`
    // never matched real output, so every cloud probe returned an
    // empty snapshot-id list. That made `WipeEntireDestinationAsync`
    // believe there was nothing to prune for any Dropbox / OneDrive
    // / GDrive / S3 destination — so "Delete Destination" left the
    // remote folder untouched. Caught by DropboxWipeDiagnosticTests.
    private static readonly Regex SnapshotLineRegex = new(
        @"\bSnapshot\s+(?<id>[A-Za-z0-9_\-]+)\s+(?:at\s+)?revision\s+\d",
        RegexOptions.Compiled);

    public DestinationProbe(SecretsStore secrets) => _secrets = secrets;

    /// <summary>
    /// Local-storage state used by the destination-editor pre-flight.
    /// Cheap synchronous check: just <see cref="File.Exists(string)"/>
    /// against the well-known Duplicacy artifacts at the storage root.
    /// We do NOT shell out to duplicacy — the only thing we need to know
    /// at save time is "does this folder already contain a Duplicacy
    /// storage we'd be re-initialising on top of". If yes, the editor
    /// surfaces a recovery path (turn on Encryption + supply the
    /// existing password, or pick a different folder, or manually wipe
    /// the folder) BEFORE the user discovers it via a failed run.
    /// Returns false for non-local-like kinds (probing cloud/S3 needs a
    /// live connection — handled at run-time by the typed-exception path
    /// in DuplicacyRunner.EnsureInitializedAsync).
    /// </summary>
    public static bool LocalStorageAlreadyInitialised(Destination destination)
    {
        if (!destination.IsLocalLike) return false;
        if (string.IsNullOrWhiteSpace(destination.PathOrSubpath)) return false;
        var root = destination.PathOrSubpath;
        try
        {
            // Duplicacy creates a top-level `config` file at storage init
            // time (the encrypted/plain key bundle). The presence of any
            // ONE of these is conclusive — chunks/ and snapshots/ are
            // populated on first backup but `config` lands on init.
            if (File.Exists(Path.Combine(root, "config"))) return true;
            if (Directory.Exists(Path.Combine(root, "chunks"))) return true;
            if (Directory.Exists(Path.Combine(root, "snapshots"))) return true;
        }
        catch
        {
            // Path inaccessible (offline drive, missing share, perms) —
            // treat as "not initialised here". The actual init at run
            // time will surface the real error if there is one.
            return false;
        }
        return false;
    }

    /// <summary>
    /// Inspects an existing Duplicacy <c>config</c> file at a local
    /// storage root and returns whether it was created with encryption.
    ///
    /// Duplicacy writes one of two formats:
    ///   • Unencrypted: a plain JSON document starting with <c>'{'</c>
    ///     (the chunk-size, hash-algo, and storage parameters are
    ///     all plaintext).
    ///   • Encrypted: an opaque binary blob (HMAC-prefixed AES-256
    ///     ciphertext); the first byte is never <c>'{'</c>.
    ///
    /// We don't parse the JSON; checking the first non-whitespace byte
    /// is enough to distinguish the two formats and lets the editor
    /// skip the "you must enter a password" gate when adopting an
    /// unencrypted storage. Returns:
    ///   • <c>true</c> if the file looks encrypted,
    ///   • <c>false</c> if it parses as plaintext JSON,
    ///   • <c>null</c> if the file doesn't exist, can't be read, or
    ///     is empty (caller should fall back to "treat as encrypted"
    ///     for safety, since prompting for a password is recoverable
    ///     while accidentally adopting an encrypted storage as
    ///     plaintext is not).
    /// </summary>
    public static bool? LocalStorageIsEncrypted(Destination destination)
    {
        if (!destination.IsLocalLike) return null;
        if (string.IsNullOrWhiteSpace(destination.PathOrSubpath)) return null;
        var configPath = Path.Combine(destination.PathOrSubpath, "config");
        try
        {
            if (!File.Exists(configPath)) return null;
            // Read just enough bytes to identify the format. JSON
            // configs are typically ~200-400 bytes; encrypted blobs
            // are larger but the first byte is always non-printable.
            using var fs = File.Open(configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> buf = stackalloc byte[16];
            var n = fs.Read(buf);
            if (n <= 0) return null;
            // Skip leading whitespace just in case (unlikely but
            // robust against editors that wrote a BOM/newline).
            for (int i = 0; i < n; i++)
            {
                var b = buf[i];
                if (b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n') continue;
                return b != (byte)'{';
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the set of snapshot-ids (i.e. Duplicacy snapshot folder
    /// names) already present at this destination's storage root.
    /// Never throws — returns empty on any error, with a logged warning,
    /// so save-time checks stay best-effort.
    /// </summary>
    public async Task<IReadOnlyCollection<string>> ListSnapshotIdsAsync(
        Destination destination, CancellationToken ct)
    {
        try
        {
            return destination.Kind switch
            {
                DestinationKind.LocalFolder
                    or DestinationKind.ExternalDrive
                    or DestinationKind.NetworkShare
                    => ListLocal(destination),

                DestinationKind.DropboxAppScoped
                    or DestinationKind.DropboxFullAccess
                    or DestinationKind.OneDrivePersonal
                    or DestinationKind.OneDriveBusiness
                    or DestinationKind.GoogleDrive
                    or DestinationKind.S3Compatible
                    => await ListViaDuplicacyAsync(destination, ct),

                _ => Array.Empty<string>(),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warning(ex, "Snapshot-id listing failed for destination {Name} ({Kind}) — treating as empty",
                destination.Name, destination.Kind);
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyCollection<string> ListLocal(Destination destination)
    {
        if (string.IsNullOrWhiteSpace(destination.PathOrSubpath)) return Array.Empty<string>();
        var snapshotsRoot = Path.Combine(destination.PathOrSubpath, "snapshots");
        if (!Directory.Exists(snapshotsRoot)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(snapshotsRoot)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Cloud/S3 path: create a throwaway repo, init it pointing at the
    /// same storage URL a real backup would use, then run
    /// <c>duplicacy list</c> and scrape snapshot-ids from stdout.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> ListViaDuplicacyAsync(
        Destination destination, CancellationToken ct)
    {
        // Opportunistic janitor: every probe sweeps any leftover
        // duplimate-probe-* directories in TEMP that are older than 1 hour.
        // The recursive-delete in this method's finally block can fail
        // (Windows file handles linger past process exit, anti-virus
        // scanning, etc.); without a sweep, those orphaned dirs
        // accumulate on the user's disk for the lifetime of the
        // installation. 1 hour is well past the longest plausible
        // active probe.
        TrySweepOrphanedProbeDirs();

        var probeRepo = Path.Combine(Path.GetTempPath(), $"duplimate-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(probeRepo);
        try
        {
            var duplicacyExe = DuplicacyEmbedder.EnsureExtracted();
            var storageUrl = destination.BuildStorageUrl();
            var envVars = BuildEnv(destination);

            // init first — required before `list` will talk to the
            // backend. The init itself is local-only (writes preferences)
            // but duplicacy validates the URL syntax.
            var initArgs = new[]
            {
                "init",
                "-storage-name", "default",
                "-repository",   probeRepo,
                "duplimate_probe",
                storageUrl,
            };
            var (initExit, initOut) = await RunAsync(duplicacyExe, probeRepo, initArgs, envVars, ct);
            if (initExit != 0)
            {
                _log.Warning("Probe init failed for {Name}: {Tail}", destination.Name, Tail(initOut));
                return Array.Empty<string>();
            }

            // list — may emit "Snapshot <id> at revision <n> ..." per
            // existing snapshot. Parse those; ignore everything else.
            //
            // `-all` is REQUIRED: without it, `duplicacy list` only
            // emits snapshots that share THIS repo's snapshot-id
            // (`duplimate_probe`, set by the init above). The probe is a
            // fresh throwaway repo so its own snapshot space is
            // always empty — meaning every "what's at this remote?"
            // query returned an empty list, even when real backups
            // had landed many snapshots there.
            //
            // Downstream consequence (the bug this test caught):
            // StorageCleaner.WipeEntireDestinationAsync relies on
            // this enumeration to decide what to prune. Empty
            // enumeration → "nothing of ours to remove" branch →
            // wipe returns successfully without calling any prune
            // commands → chunks/snapshots remain on the remote. The
            // user saw their "deleted" Dropbox folder still at the
            // pre-wipe byte count.
            var listArgs = new[] { "-log", "list", "-storage", "default", "-all" };
            var (listExit, listOut) = await RunAsync(duplicacyExe, probeRepo, listArgs, envVars, ct);
            if (listExit != 0)
            {
                _log.Warning("Probe list failed for {Name}: {Tail}", destination.Name, Tail(listOut));
                return Array.Empty<string>();
            }

            return ExtractSnapshotIds(listOut);
        }
        finally
        {
            try { Directory.Delete(probeRepo, recursive: true); } catch { /* best-effort — sweep below catches it next time */ }
        }
    }

    /// <summary>
    /// Best-effort cleanup of <c>duplimate-probe-*</c> directories left
    /// behind by previous probes (process killed mid-flight, AV scanner
    /// holding a file handle, etc.). Only deletes ones older than 1
    /// hour to avoid racing a concurrent probe. Silently swallows all
    /// failures — this is opportunistic disk hygiene, never a fatal
    /// error.
    /// </summary>
    /// <summary>Run-once-per-process flag. Sweeping %TEMP% for stale
    /// duplimate-probe-* dirs is a 1-second-or-so I/O-heavy walk on dev
    /// machines (50k+ entries common) and the orphans we're cleaning
    /// up are at least 1h old, so once per session is plenty.
    /// Without this flag every probe call did the full walk.</summary>
    private static int _orphanSweepDone;

    private static void TrySweepOrphanedProbeDirs()
    {
        if (System.Threading.Interlocked.Exchange(ref _orphanSweepDone, 1) != 0) return;
        try
        {
            var temp = Path.GetTempPath();
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (var dir in Directory.EnumerateDirectories(temp, "duplimate-probe-*"))
            {
                try
                {
                    var info = new DirectoryInfo(dir);
                    if (info.CreationTimeUtc <= cutoff && info.LastWriteTimeUtc <= cutoff)
                        Directory.Delete(dir, recursive: true);
                }
                catch { /* one bad dir doesn't stop the sweep */ }
            }
        }
        catch { /* TEMP unreachable — nothing we can do */ }
    }

    public static HashSet<string> ExtractSnapshotIds(string duplicacyListOutput)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in duplicacyListOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var m = SnapshotLineRegex.Match(line);
            if (m.Success) ids.Add(m.Groups["id"].Value);
        }
        return ids;
    }

    private Dictionary<string, string> BuildEnv(Destination dest)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // CRITICAL: duplicacy.exe resolves OAuth / S3 secrets via env
        // vars in the form `DUPLICACY_<STORAGENAME>_<PROVIDER>_TOKEN`,
        // where STORAGENAME = the `-storage-name` arg passed to
        // `duplicacy init` (always "default" in this probe). The
        // unprefixed `DUPLICACY_<PROVIDER>_TOKEN` form is only a
        // fallback. Previously this method set ONLY the unprefixed
        // form — duplicacy fell back to interactive prompts on the
        // probe init, which in our non-tty subprocess hung or
        // returned an "Invalid authorization" error, causing the
        // probe to silently report zero remote snapshots. Downstream
        // bug: WipeEntireDestinationAsync sees an "empty" remote and
        // bails out without pruning, so "Delete Destination" leaves
        // every chunk on Dropbox/OneDrive/GDrive untouched — exactly
        // the user-reported regression.
        //
        // Fix mirrors `DuplicacyRunner.BuildCloudEnv`: set BOTH the
        // generic and the storage-name-prefixed env var so duplicacy
        // resolves the secret without prompting regardless of which
        // form it consults first.
        const string storageName = "default";
        var prefix = $"DUPLICACY_{storageName.ToUpperInvariant()}_";

        // Storage password: we can't list an encrypted chunk index without
        // the storage password. Probe-time list only needs the manifest
        // listing, but duplicacy still reads the config file which may be
        // encrypted. Supplying it is cheap if we have it.
        if (dest.StoragePasswordRef is { } spRef)
        {
            var pwd = _secrets.Get(spRef) ?? "";
            env["DUPLICACY_PASSWORD"] = pwd;
            env[prefix + "PASSWORD"] = pwd;
        }

        switch (dest.Kind)
        {
            case DestinationKind.DropboxAppScoped:
            case DestinationKind.DropboxFullAccess:
                if (dest.OAuthTokenRef is { } dbxRef)
                {
                    var t = _secrets.Get(dbxRef) ?? "";
                    env["DUPLICACY_DROPBOX_TOKEN"] = t;
                    env[prefix + "DROPBOX_TOKEN"] = t;
                }
                break;

            case DestinationKind.OneDrivePersonal:
                if (dest.OAuthTokenRef is { } oneRef)
                {
                    var t = _secrets.Get(oneRef) ?? "";
                    env["DUPLICACY_ONE_TOKEN"] = t;
                    env[prefix + "ONE_TOKEN"] = t;
                }
                break;

            case DestinationKind.OneDriveBusiness:
                if (dest.OAuthTokenRef is { } odbRef)
                {
                    var t = _secrets.Get(odbRef) ?? "";
                    env["DUPLICACY_ODB_TOKEN"] = t;
                    env[prefix + "ODB_TOKEN"] = t;
                }
                break;

            case DestinationKind.GoogleDrive:
                if (dest.OAuthTokenRef is { } gdRef)
                {
                    var t = _secrets.Get(gdRef) ?? "";
                    env["DUPLICACY_GCD_TOKEN"] = t;
                    env[prefix + "GCD_TOKEN"] = t;
                }
                break;

            case DestinationKind.S3Compatible:
                if (dest.S3AccessKeyRef is { } ak)
                {
                    var v = _secrets.Get(ak) ?? "";
                    env["DUPLICACY_S3_ID"] = v;
                    env[prefix + "S3_ID"] = v;
                }
                if (dest.S3SecretKeyRef is { } sk)
                {
                    var v = _secrets.Get(sk) ?? "";
                    env["DUPLICACY_S3_SECRET"] = v;
                    env[prefix + "S3_SECRET"] = v;
                }
                break;
        }

        return env;
    }

    private static async Task<(int exit, string output)> RunAsync(
        string exe, string cwd, string[] args,
        IDictionary<string, string> env, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var buf = new StringBuilder();
        var gate = new object();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (gate) buf.AppendLine(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) lock (gate) buf.AppendLine("[err] " + e.Data); };

        p.Start();
        KillOnExitJobObject.TryAssign(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { p.StandardInput.Close(); } catch { }

        using (ct.Register(() => { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } }))
        {
            // Pass `ct` so a cancellation whose Kill failed still
            // unwinds the wait — without this, a stuck duplicacy
            // probe pegged the thread forever.
            await p.WaitForExitAsync(ct);
        }
        return (p.ExitCode, buf.ToString());
    }

    private static string Tail(string s, int max = 240) =>
        s.Length <= max ? s.Trim() : s[^max..].Trim();
}
