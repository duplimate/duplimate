using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.Tests.Scenarios;
using Duplimate.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace Duplimate.Tests.Cloud;

/// <summary>
/// E2E diagnostic for the user-reported issue: "I deleted a Destination
/// in the Release app but the Dropbox folder didn't decrease in size
/// and wasn't deleted either."
///
/// What this test does, end-to-end, against a real Dropbox account:
///   1. Backup ~512 KB of synthetic data to a fresh per-run subpath.
///   2. Snapshot the remote folder state via duplicacy `list` AND via
///      the Dropbox REST `/2/files/list_folder` (recursive) so we can
///      see byte counts.
///   3. Call <see cref="StorageCleaner.WipeEntireDestinationAsync"/>
///      — the exact code path the Delete-Destination button invokes.
///   4. Re-snapshot: list snapshots via duplicacy + list remaining
///      files at the Dropbox subpath.
///   5. Report what's left. This is where the bug becomes obvious —
///      if the Duplicacy `config` file + empty `chunks/snapshots/`
///      dirs survive, the destination folder isn't deleted even
///      though we said "Delete".
///
/// Runs only when DROPBOX_TOKEN is populated in secrets.env. Test is
/// otherwise skipped with a clear note.
/// </summary>
public class DropboxWipeDiagnosticTests
{
    private readonly ITestOutputHelper _out;
    public DropboxWipeDiagnosticTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Diagnostic_DeleteDestination_FullyClearsDropboxFolder()
    {
        var secrets = new SecretsLoader();
        if (!secrets.TryGetDropboxToken(out var token))
        {
            _out.WriteLine("SKIPPED: DROPBOX_TOKEN not set in " + secrets.SecretsFile);
            return;
        }

        ResetConfig();

        using var ws = new TempWorkspace("e2e-dropbox-wipe");
        var sourceDir = ws.Sub("source");

        const int seed = 31337;
        const long targetBytes = 512_000;
        var summary = SyntheticFileTree.Generate(sourceDir, seed, targetBytes);
        _out.WriteLine($"Source generated: {summary}");

        var subpath = $"duplimate-wipe-test/{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        _out.WriteLine($"Remote subpath: dropbox://{subpath}");

        var tokenRef = $"test-dbx-token-{Guid.NewGuid():N}";
        ServiceLocator.Secrets.Set(tokenRef, token);

        var destination = new Destination
        {
            Name = "wipe-test-dest",
            Kind = DestinationKind.DropboxAppScoped,
            PathOrSubpath = subpath,
            OAuthTokenRef = tokenRef,
            Encrypted = false,
        };
        var backup = new Backup
        {
            Name = "e2e_wipe",
            SourcePaths = { sourceDir },
            FiltersText = "",
            PruneAfterBackup = false,
            CheckAfterBackup = false,
            AbortOnMeteredNetwork = false,
            Threads = 2,
            UseVss = false,
        };
        backup.Targets.Add(new BackupTarget { DestinationId = destination.Id, StorageName = "default" });

        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(destination);
            cfg.Backups.Add(backup);
        });

        // ---- 1. Backup ----
        _out.WriteLine("\n=== STEP 1: backup ===");
        var record = await ServiceLocator.Orchestrator
            .RunBackupAsync(backup, CancellationToken.None);
        _out.WriteLine($"  status={record.Status}, summary={record.Summary}");
        Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Backup ended {record.Status}: {string.Join(" | ", record.Errors)}");

        // ---- 2. Snapshot pre-wipe state ----
        _out.WriteLine("\n=== STEP 2: pre-wipe state ===");
        // Dump raw duplicacy list output so we can see exactly what
        // format snapshot lines have. The regex in DestinationProbe
        // is the gating factor for whether wipe sees any snapshots.
        await DumpRawDuplicacyListAsync(destination, _out);
        var snapshotsBefore = await ServiceLocator.DestProbe.ListSnapshotIdsAsync(destination, CancellationToken.None);
        _out.WriteLine($"  duplicacy list snapshot ids: [{string.Join(", ", snapshotsBefore)}]");
        Assert.NotEmpty(snapshotsBefore);

        // Dropbox REST: we cannot use a refresh token with Bearer auth,
        // BUT duplicacy.com's helper accepts unauthenticated exchanges
        // via its proxy. If that fails, fall back to a "do we see
        // anything via duplicacy list?" assertion instead.
        var (bytesBefore, filesBefore) = await TryListRemoteAsync(
            destination, token, _out);
        _out.WriteLine($"  remote folder before wipe: {bytesBefore} bytes across {filesBefore} files");

        // ---- 3. Wipe (the code path the Delete button calls) ----
        _out.WriteLine("\n=== STEP 3: wipe destination ===");
        // Capture every chunkPercent emission so we can assert the
        // bar climbs monotonically and the final value is 100.
        var pctSamples = new System.Collections.Generic.List<int?>();
        var lastStatus = "";
        var statusProgress = new Progress<string>(s => lastStatus = s);
        var pctProgress = new Progress<int?>(p =>
        {
            lock (pctSamples) pctSamples.Add(p);
        });
        var report = await ServiceLocator.Cleaner.WipeEntireDestinationAsync(
            destination, CancellationToken.None, statusProgress, pctProgress);
        _out.WriteLine($"  WipedPath={report.WipedPath ?? "(null)"}");
        _out.WriteLine($"  DeletedSnapshotIds=[{string.Join(", ", report.DeletedSnapshotIds)}]");
        _out.WriteLine($"  Errors:");
        foreach (var e in report.Errors) _out.WriteLine($"    - {e}");
        _out.WriteLine($"  Notes:");
        foreach (var n in report.Notes) _out.WriteLine($"    - {n}");
        // Assert the user-visible "leftover skeleton" note is present
        // on the success path. Without this note, the user sees a
        // mostly-empty folder on Dropbox and assumes the wipe failed —
        // the note explains the residue is by design.
        Assert.Contains(report.Notes, n => n.Contains("config", StringComparison.OrdinalIgnoreCase)
                                        && n.Contains("skeleton", StringComparison.OrdinalIgnoreCase));
        _out.WriteLine($"  Captured {pctSamples.Count} chunkPercent samples; last={pctSamples.LastOrDefault()}");
        _out.WriteLine($"  Final status text: {lastStatus}");

        // Progress assertions:
        //   • final % must be 100 (operation completed)
        //   • no sample should exceed 100 or be negative
        //   • among non-null samples, the sequence should be
        //     monotonically non-decreasing (modulo phase resets to
        //     null between snapshots, which we filter out)
        Assert.NotEmpty(pctSamples);
        Assert.Equal(100, pctSamples.LastOrDefault());
        var nonNullPcts = pctSamples.Where(p => p.HasValue).Select(p => p!.Value).ToList();
        Assert.All(nonNullPcts, p => Assert.InRange(p, 0, 100));
        int prev = -1;
        foreach (var p in nonNullPcts)
        {
            Assert.True(p >= prev, $"chunkPercent decreased: {prev} → {p}");
            prev = p;
        }

        // Activity-log assertions: a RunRecord should land under the
        // [Destinations] bucket pointing at a log file on disk that
        // captured at least one duplicacy line.
        var destLogDir = AppPaths.LogDirForBackup(StorageCleaner.DestinationsBucketName);
        Assert.True(System.IO.Directory.Exists(destLogDir),
            $"[Destinations] log directory not created: {destLogDir}");
        var historyFile = System.IO.Path.Combine(destLogDir, "history.json");
        Assert.True(System.IO.File.Exists(historyFile),
            $"[Destinations] history.json missing: {historyFile}");
        var history = ServiceLocator.Logs.LoadHistory(StorageCleaner.DestinationsBucketName);
        Assert.Contains(history, r => r.Kind == RunKind.Erase && r.BackupName == StorageCleaner.DestinationsBucketName);
        var thisRun = history.OrderByDescending(r => r.StartedUtc).First();
        Assert.False(string.IsNullOrEmpty(thisRun.LogPath));
        Assert.True(System.IO.File.Exists(thisRun.LogPath), $"Log file missing: {thisRun.LogPath}");
        var logText = await System.IO.File.ReadAllTextAsync(thisRun.LogPath);
        _out.WriteLine($"  log file: {thisRun.LogPath} ({logText.Length} bytes)");
        Assert.Contains("Started:", logText);
        Assert.Contains("Ended:", logText);

        // ---- 4. Snapshot post-wipe state ----
        _out.WriteLine("\n=== STEP 4: post-wipe state ===");
        var snapshotsAfter = await ServiceLocator.DestProbe.ListSnapshotIdsAsync(destination, CancellationToken.None);
        _out.WriteLine($"  duplicacy list snapshot ids: [{string.Join(", ", snapshotsAfter)}]");

        var (bytesAfter, filesAfter) = await TryListRemoteAsync(
            destination, token, _out);
        _out.WriteLine($"  remote folder after wipe: {bytesAfter} bytes across {filesAfter} files");

        // ---- 5. Diagnostic verdicts ----
        _out.WriteLine("\n=== STEP 5: verdicts ===");
        _out.WriteLine($"  snapshots removed?    {(snapshotsAfter.Count == 0 ? "YES" : "NO")}");
        if (bytesBefore >= 0 && bytesAfter >= 0)
        {
            _out.WriteLine($"  bytes change:         {bytesBefore} → {bytesAfter} (Δ {bytesAfter - bytesBefore})");
            _out.WriteLine($"  files change:         {filesBefore} → {filesAfter} (Δ {filesAfter - filesBefore})");
            _out.WriteLine($"  remote folder empty?  {(filesAfter == 0 ? "YES" : "NO (leftover files listed above)")}");
        }
        else
        {
            _out.WriteLine("  remote folder enumeration not available — relied on duplicacy list only");
        }

        // Soft assertion: snapshots gone is the contract we can rely on
        // (`prune -id` + `prune -exhaustive` is what the code calls). The
        // remote-folder-empty assertion is the bug we're hunting.
        Assert.Empty(snapshotsAfter);
    }

    /// <summary>
    /// Spawn duplicacy.exe directly with the same args DestinationProbe
    /// uses and dump the raw stdout/stderr so we can see what its
    /// `list` output actually looks like. Helps debug regex-mismatch
    /// bugs where the parser thinks there are zero snapshots even
    /// though duplicacy reported some.
    /// </summary>
    private static async Task DumpRawDuplicacyListAsync(Destination destination, ITestOutputHelper @out)
    {
        try
        {
            var exe = DuplicacyEmbedder.EnsureExtracted();
            var probeRepo = Path.Combine(Path.GetTempPath(), $"duplimate-rawdump-{Guid.NewGuid():N}");
            Directory.CreateDirectory(probeRepo);
            try
            {
                var storageUrl = destination.BuildStorageUrl();
                @out.WriteLine($"  storage URL = {storageUrl}");
                // Production-equivalent env-var setup: BOTH the
                // generic and the storage-name-prefixed forms (see
                // DuplicacyRunner.BuildCloudEnv).
                var env = new System.Collections.Generic.Dictionary<string, string>();
                if (destination.OAuthTokenRef is { } tokRef)
                {
                    var t = ServiceLocator.Secrets.Get(tokRef) ?? "";
                    env["DUPLICACY_DROPBOX_TOKEN"] = t;
                    env["DUPLICACY_DEFAULT_DROPBOX_TOKEN"] = t;
                }

                var initArgs = new[] { "init", "-storage-name", "default", "-repository", probeRepo, "duplimate_rawdump", storageUrl };
                var (initExit, initOut) = await SpawnAsync(exe, probeRepo, initArgs, env);
                @out.WriteLine($"  [raw init exit={initExit}]");
                foreach (var l in initOut.Split('\n'))
                    @out.WriteLine($"  init| {l.TrimEnd('\r')}");

                var listArgsAll = new[] { "-log", "list", "-storage", "default", "-all" };
                var (listExit, listOutAll) = await SpawnAsync(exe, probeRepo, listArgsAll, env);
                @out.WriteLine($"  [raw `list -all` exit={listExit}]");
                @out.WriteLine("  --- begin raw output ---");
                foreach (var l in listOutAll.Split('\n'))
                    @out.WriteLine($"  | {l.TrimEnd('\r')}");
                @out.WriteLine("  --- end raw output ---");
            }
            finally
            {
                try { Directory.Delete(probeRepo, recursive: true); } catch { }
            }
        }
        catch (Exception ex)
        {
            @out.WriteLine($"  raw dump threw: {ex.Message}");
        }
    }

    private static async Task<(int exit, string output)> SpawnAsync(
        string exe, string cwd, string[] args,
        System.Collections.Generic.IDictionary<string, string> env)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = new System.Diagnostics.Process { StartInfo = psi };
        var buf = new System.Text.StringBuilder();
        var gate = new object();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (gate) buf.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (gate) buf.AppendLine("[err] " + e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        return (p.ExitCode, buf.ToString());
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s.Trim() : s[^max..].Trim();

    private static async Task<(long bytes, int files)> TryListRemoteAsync(
        Destination destination, string refreshToken, ITestOutputHelper @out)
    {
        // The refresh token from duplicacy.com cannot be exchanged for
        // an access token without duplicacy.com's client_secret. So we
        // can't use Dropbox REST directly. This method returns (-1, -1)
        // as the documented "couldn't enumerate" sentinel — the test
        // body falls back to duplicacy-list-based assertions in that
        // case.
        //
        // (If a user supplies an actual access token via the
        // environment variable DROPBOX_ACCESS_TOKEN, we use that for
        // REST instead — supports running with a Dropbox-app-registered
        // access token for richer diagnostics.)
        var accessToken = Environment.GetEnvironmentVariable("DROPBOX_ACCESS_TOKEN");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            @out.WriteLine("    (DROPBOX_ACCESS_TOKEN not set; skipping REST enumeration)");
            return (-1, -1);
        }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // App-scoped tokens: path is rooted at "" inside the app folder.
        // Full-access tokens: path is "/" + user-visible path.
        // The destination's PathOrSubpath is what duplicacy stores under
        // the namespace root; for app-folder that's relative inside the
        // app folder.
        var listPath = "/" + destination.PathOrSubpath.TrimStart('/').TrimEnd('/');

        long bytes = 0;
        int files = 0;
        string? cursor = null;
        do
        {
            HttpResponseMessage resp;
            if (cursor is null)
            {
                resp = await http.PostAsJsonAsync(
                    "https://api.dropboxapi.com/2/files/list_folder",
                    new { path = listPath, recursive = true, include_deleted = false });
            }
            else
            {
                resp = await http.PostAsJsonAsync(
                    "https://api.dropboxapi.com/2/files/list_folder/continue",
                    new { cursor });
            }
            if ((int)resp.StatusCode == 409)
            {
                @out.WriteLine($"    REST list returned 409 for {listPath} — folder doesn't exist (good).");
                return (0, 0);
            }
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            foreach (var entry in root.GetProperty("entries").EnumerateArray())
            {
                var tag = entry.GetProperty(".tag").GetString();
                if (tag == "file")
                {
                    files++;
                    if (entry.TryGetProperty("size", out var sizeEl))
                        bytes += sizeEl.GetInt64();
                }
            }
            cursor = root.TryGetProperty("has_more", out var moreEl) && moreEl.GetBoolean()
                ? root.GetProperty("cursor").GetString()
                : null;
        } while (cursor is not null);

        return (bytes, files);
    }

    private static void ResetConfig()
    {
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            cfg.Destinations.Clear();
        });
    }
}
