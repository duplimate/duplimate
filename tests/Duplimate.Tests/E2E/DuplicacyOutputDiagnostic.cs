using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace Duplimate.Tests.E2E;

/// <summary>
/// Diagnostic test that runs duplicacy commands against a fresh storage and
/// dumps the raw stdout lines. Useful when tightening parsers in
/// RevisionBrowser after a duplicacy version bump changes output shape.
/// Run with:
///   dotnet test --filter "FullyQualifiedName~DuplicacyOutputDiagnostic" -v detailed
/// </summary>
public class DuplicacyOutputDiagnostic
{
    private readonly ITestOutputHelper _out;
    public DuplicacyOutputDiagnostic(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Dump_List_And_ListFiles_RawOutput()
    {
        ResetConfig();
        using var ws = new TempWorkspace("diag");
        var sourceDir  = ws.Sub("source");
        var storageDir = ws.Sub("storage");

        SyntheticFileTree.Generate(sourceDir, seed: 7, targetBytes: 200_000);

        var destination = new Destination
        {
            Name = "diag-local",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = storageDir,
            Encrypted = false,
        };
        var backup = new Backup
        {
            Name = "diag_backup",
            SourcePaths = { sourceDir },
            PruneAfterBackup = false,
            CheckAfterBackup = false,
            UseVss = false,
            Threads = 1,
        };
        backup.Targets.Add(new BackupTarget { DestinationId = destination.Id, StorageName = "default" });

        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(destination);
            cfg.Backups.Add(backup);
        });

        var record = await ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None);
        _out.WriteLine($"Backup: {record.Status}  summary={record.Summary}");

        var repo = AppPaths.RepoDirForBackup(backup.Name);
        var exe  = DuplicacyEmbedder.EnsureExtracted();

        _out.WriteLine("");
        _out.WriteLine("=== duplicacy -log list -storage default ===");
        await RunAndDump(exe, repo, new[] { "-log", "list", "-storage", "default" });

        _out.WriteLine("");
        _out.WriteLine("=== duplicacy -log list -files -r 1 -storage default ===");
        await RunAndDump(exe, repo, new[] { "-log", "list", "-files", "-r", "1", "-storage", "default" });
    }

    private async Task RunAndDump(string exe, string cwd, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new List<string>();
        var stderr = new List<string>();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.Add(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderr.Add(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(CancellationToken.None);

        _out.WriteLine($"exit={p.ExitCode} stdout-lines={stdout.Count} stderr-lines={stderr.Count}");
        foreach (var l in stdout) _out.WriteLine($"  STDOUT | {l}");
        foreach (var l in stderr) _out.WriteLine($"  STDERR | {l}");
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
