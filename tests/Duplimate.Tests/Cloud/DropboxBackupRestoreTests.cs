using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace Duplimate.Tests.Cloud;

/// <summary>
/// Interactive E2E: round-trip a synthetic source tree through a Dropbox
/// app-scoped backup.
///
/// To run this:
///
///   1. set DUPLIMATE_TEST_INTERACTIVE=1
///   2. dotnet test --filter "FullyQualifiedName~Dropbox"
///
/// The first run prompts for a Dropbox OAuth token (paste from
/// https://duplicacy.com/dropbox_start ). Subsequent runs reuse the cached
/// token from tests/Duplimate.Tests/.auth-cache/dropbox.txt — delete
/// that file to force a fresh auth flow.
///
/// The test uses a per-run unique subpath (duplimate-tests/{guid}) on
/// Dropbox, so repeated runs won't collide. The test does NOT clean up the
/// remote folder afterwards — delete test folders manually from Dropbox
/// if they pile up.
/// </summary>
public class DropboxBackupRestoreTests
{
    private const string Provider = "dropbox";
    private const string HelperUrl = "https://duplicacy.com/dropbox_start";

    private readonly ITestOutputHelper _out;
    public DropboxBackupRestoreTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// Runs the probe against a captured Dropbox refresh token and
    /// asserts the token is healthy. Also serves as executable
    /// documentation of the scope-detection limitation (see
    /// DropboxAuthProbe's header for why we can't introspect scope
    /// directly).
    /// </summary>
    [InteractiveFact]
    public async Task Dropbox_Token_PassesHealthCheck()
    {
        var token = AuthCache.PromptForToken(Provider, HelperUrl);
        var probe = ServiceLocator.DropboxProbe;
        var result = await probe.HealthCheckAsync(token, CancellationToken.None);

        _out.WriteLine($"Probe result: healthy={result.Healthy}, message={result.Message}");
        Assert.True(result.Healthy, $"Probe rejected the token: {result.Message}");

        // NOTE on scope enforcement: Dropbox does not expose the
        // app-folder-vs-full-access distinction in any API response, and
        // the refresh token duplicacy.com issues cannot be exchanged for
        // an access token without duplicacy.com's OAuth client_secret
        // (which we do not own). Scope enforcement therefore lives at the
        // URL we direct users at during OAuth:
        //
        //   DropboxAppScoped   → https://duplicacy.com/dropbox_start
        //                        (the ONLY Dropbox helper duplicacy.com
        //                        hosts — verified empirically, every
        //                        full-access variant soft-302's to home)
        //   DropboxFullAccess  → not supported via duplicacy.com's helper;
        //                        would require Duplimate to register
        //                        its own Dropbox app with Full access.
        //                        OAuthHelperUrlFor returns null for this
        //                        kind today.
        //
        // Empirical check you can run once: after this test creates the
        // remote folder, look at Dropbox. If /Apps/Duplicacy/duplimate-tests/…
        // exists, the scope is app-folder (correct). If /duplimate-tests/ at
        // the Dropbox root, the scope leaked to full-access and we need
        // to investigate duplicacy.com's app registration.
    }

    [InteractiveFact]
    public async Task Dropbox_AppScoped_Backup_Then_Restore_ContentMatches()
    {
        var token = AuthCache.PromptForToken(Provider, HelperUrl);

        ResetConfig();

        using var ws = new TempWorkspace("e2e-dropbox");
        var sourceDir  = ws.Sub("source");
        var restoreDir = ws.Sub("restored");

        const int seed = 4242;
        const long targetBytes = 512_000; // keep cloud runs short
        var summary = SyntheticFileTree.Generate(sourceDir, seed, targetBytes);
        _out.WriteLine($"Source: {summary}");
        var sourceHash = DirectoryHash.Compute(sourceDir);

        // Unique per-run subpath so concurrent / repeated runs don't step
        // on each other on the remote side.
        var subpath = $"duplimate-tests/{DateTime.UtcNow:yyyyMMdd}/{Guid.NewGuid():N}";
        _out.WriteLine($"Remote subpath: dropbox://{subpath}");

        // Store the OAuth token through SecretsStore so DuplicacyRunner can
        // fetch it the same way the production code does.
        var tokenRef = $"test-dropbox-token-{Guid.NewGuid():N}";
        ServiceLocator.Secrets.Set(tokenRef, token);

        var destination = new Destination
        {
            Name = "dropbox-test",
            Kind = DestinationKind.DropboxAppScoped,
            PathOrSubpath = subpath,
            OAuthTokenRef = tokenRef,
            Encrypted = false,
        };
        var backup = new Backup
        {
            Name = "e2e_dropbox",
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

        // Backup
        var record = await ServiceLocator.Orchestrator
            .RunBackupAsync(backup, CancellationToken.None);
        _out.WriteLine($"Backup status: {record.Status}. Summary: {record.Summary}");
        Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Cloud backup ended {record.Status}: {string.Join(" | ", record.Errors)}");

        // Pick the revision we just made
        var revisions = await ServiceLocator.Revisions.ListRevisionsAsync(
            backup, sourceDir, destination, "default", CancellationToken.None);
        Assert.NotEmpty(revisions);
        var latest = revisions[0].Number;

        // Enumerate + restore everything
        var files = await ServiceLocator.Revisions.ListFilesAsync(
            backup, sourceDir, destination, "default", latest, CancellationToken.None);
        var request = new RestoreRequest
        {
            Backup = backup,
            SourcePath = sourceDir,
            Destination = destination,
            StorageName = "default",
            Revision = latest,
            Files = files.Select(f => f.Path).ToList(),
            TargetPath = restoreDir,
            Overwrite = true,
            PreserveStructure = true,
            Threads = 2,
            RetryDelaysSeconds = new[] { 2, 5, 10 },
        };
        ServiceLocator.Restore.Log += s => _out.WriteLine($"[restore] {s}");
        var outcome = await ServiceLocator.Restore.RunAsync(request, CancellationToken.None);

        var failed = outcome.Files.Values.Where(f => f.Status == RestoreFileStatus.Failed).ToList();
        if (failed.Count > 0)
        {
            _out.WriteLine($"{failed.Count} file(s) failed to restore:");
            foreach (var f in failed.Take(5))
                _out.WriteLine($"  {f.Path} → {f.LastError}");
        }
        Assert.Equal(0, failed.Count);

        // Hash compare
        var restoredHash = DirectoryHash.Compute(restoreDir);
        Assert.Equal(sourceHash, restoredHash);
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
