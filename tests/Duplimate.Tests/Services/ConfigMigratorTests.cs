using System;
using System.IO;
using System.Linq;
using Duplimate.Models;
using Duplimate.Services;
using Xunit;

namespace Duplimate.Tests.Services;

/// <summary>
/// Covers both layouts the importer must handle:
///   1. plain Duplicacy — picked folder IS the repo (contains .duplicacy/preferences)
///   2. duplicacy-util — picked folder CONTAINS many repo folders, plus per-repo yamls
/// Plus the "no repos found" path so the friendly error text doesn't regress.
/// </summary>
public class ConfigMigratorTests
{
    [Fact]
    public void PlainUser_singleRepoLayout_importsBackupAndDestination()
    {
        using var sandbox = new ConfigSandbox();
        using var src = new TempDir();

        // Layout: <repoRoot>/.duplicacy/preferences
        var repoRoot = src.Path;
        var duplDir = Path.Combine(repoRoot, ".duplicacy");
        Directory.CreateDirectory(duplDir);
        File.WriteAllText(Path.Combine(duplDir, "preferences"),
            """
            [
              {
                "name": "default",
                "id": "my-laptop",
                "repository": "C:\\Users\\jane\\Documents",
                "storage": "dropbox://Apps/Backups/me",
                "encrypted": true
              }
            ]
            """);
        // Filters file should be picked up too.
        File.WriteAllText(Path.Combine(duplDir, "filters"), "+*.txt\n-*.log\n");

        var store = new ConfigStore();
        var migrator = new ConfigMigrator(store);
        var report = migrator.MigrateFrom(repoRoot);

        Assert.False(report.Skipped, report.Reason);
        Assert.Empty(report.Errors);
        Assert.Single(report.ImportedBackups);

        var cfg = store.Current;
        var backup = Assert.Single(cfg.Backups);
        Assert.Equal("my-laptop", backup.Name);                       // id from preferences wins
        Assert.Equal(new[] { @"C:\Users\jane\Documents" }, backup.SourcePaths.ToArray());
        Assert.Contains("+*.txt", backup.FiltersText);
        Assert.Contains("-*.log", backup.FiltersText);

        var dest = Assert.Single(cfg.Destinations);
        Assert.Equal(DestinationKind.DropboxAppScoped, dest.Kind);
        Assert.Equal("Apps/Backups/me", dest.PathOrSubpath);
        Assert.True(dest.Encrypted);

        // Backup.Target points at the imported destination, with the legacy storage name.
        var target = Assert.Single(backup.Targets);
        Assert.Equal(dest.Id, target.DestinationId);
        Assert.Equal("default", target.StorageName);
    }

    [Fact]
    public void PlainUser_localFolderStorage_isImportedAsLocalKind()
    {
        using var sandbox = new ConfigSandbox();
        using var src = new TempDir();
        using var storage = new TempDir();

        Directory.CreateDirectory(Path.Combine(src.Path, ".duplicacy"));
        File.WriteAllText(Path.Combine(src.Path, ".duplicacy", "preferences"),
            $$"""
            [
              {
                "name": "default",
                "id": "docs",
                "repository": "{{src.Path.Replace("\\", "\\\\")}}",
                "storage": "{{storage.Path.Replace("\\", "\\\\")}}",
                "encrypted": false
              }
            ]
            """);

        var store = new ConfigStore();
        new ConfigMigrator(store).MigrateFrom(src.Path);

        var dest = Assert.Single(store.Current.Destinations);
        Assert.Contains(dest.Kind, new[] { DestinationKind.LocalFolder, DestinationKind.ExternalDrive });
        Assert.Equal(storage.Path, dest.PathOrSubpath);
        Assert.False(dest.Encrypted);
    }

    [Fact]
    public void DuplicacyUtilUser_parentOfManyLayout_importsAllReposAndAppliesYamlSettings()
    {
        using var sandbox = new ConfigSandbox();
        using var parent = new TempDir();

        // Two repos as siblings under parent, plus a duplicacy-util yaml for one of them.
        MakeRepo(parent.Path, "local_c", id: "local_c",   storage: @"D:\Backups\c",                   encrypted: false);
        MakeRepo(parent.Path, "dropbox", id: "dropbox", storage: "dropbox://Apps/dupli/main",       encrypted: true);

        // duplicacy-util style yaml: threads + keep + prune for the dropbox repo.
        File.WriteAllText(Path.Combine(parent.Path, "dropbox.yaml"),
            """
            repository: .
            storage:
                -   name: default
                    threads: 4
                    vss: true
            prune:
                -   storage: default
                    keep: "0:180 30:60 7:14"
                    threads: 6
            check:
                -   storage: default
            """);

        var store = new ConfigStore();
        var report = new ConfigMigrator(store).MigrateFrom(parent.Path);

        Assert.False(report.Skipped, report.Reason);
        Assert.Equal(2, report.ImportedBackups.Count);

        var cfg = store.Current;
        Assert.Equal(2, cfg.Backups.Count);
        var dropbox = cfg.Backups.Single(b => b.Name == "dropbox");
        Assert.Equal(4, dropbox.Threads);
        Assert.True(dropbox.UseVss);
        Assert.Equal("0:180 30:60 7:14", dropbox.KeepPolicy);
        Assert.True(dropbox.PruneAfterBackup);
        Assert.True(dropbox.CheckAfterBackup);

        // The local-only repo with no yaml falls back to defaults — not an error.
        var local = cfg.Backups.Single(b => b.Name == "local_c");
        Assert.False(local.PruneAfterBackup);
        Assert.False(local.CheckAfterBackup);
    }

    [Fact]
    public void RunningTwice_isIdempotent_noDuplicates()
    {
        using var sandbox = new ConfigSandbox();
        using var src = new TempDir();
        Directory.CreateDirectory(Path.Combine(src.Path, ".duplicacy"));
        File.WriteAllText(Path.Combine(src.Path, ".duplicacy", "preferences"),
            """[ { "name":"default","id":"x","repository":".","storage":"dropbox://a/b","encrypted":false } ]""");

        var store = new ConfigStore();
        new ConfigMigrator(store).MigrateFrom(src.Path);
        new ConfigMigrator(store).MigrateFrom(src.Path);

        Assert.Single(store.Current.Backups);
        Assert.Single(store.Current.Destinations);
    }

    [Fact]
    public void EmptyFolder_reportsSkippedWithFriendlyReason()
    {
        using var sandbox = new ConfigSandbox();
        using var empty = new TempDir();

        var report = new ConfigMigrator(new ConfigStore()).MigrateFrom(empty.Path);

        Assert.True(report.Skipped);
        Assert.Contains(".duplicacy", report.Reason!);
    }

    [Fact]
    public void NonexistentPath_reportsSkipped()
    {
        using var sandbox = new ConfigSandbox();
        var missing = Path.Combine(Path.GetTempPath(), "definitely-not-a-real-path-" + Guid.NewGuid().ToString("N"));

        var report = new ConfigMigrator(new ConfigStore()).MigrateFrom(missing);

        Assert.True(report.Skipped);
        Assert.Equal("Directory does not exist", report.Reason);
    }

    // ---- helpers ----

    private static void MakeRepo(string parent, string folderName, string id, string storage, bool encrypted)
    {
        var repoDir = Path.Combine(parent, folderName);
        var duplDir = Path.Combine(repoDir, ".duplicacy");
        Directory.CreateDirectory(duplDir);
        var json = $$"""
            [
              {
                "name": "default",
                "id": "{{id}}",
                "repository": ".",
                "storage": "{{storage.Replace("\\", "\\\\")}}",
                "encrypted": {{(encrypted ? "true" : "false")}}
              }
            ]
            """;
        File.WriteAllText(Path.Combine(duplDir, "preferences"), json);
    }

    /// <summary>
    /// Redirects AppPaths.ConfigRoot to a throwaway temp dir for the duration
    /// of the test so ConfigStore.Save_NoLock doesn't write to the real
    /// %USERPROFILE% config.
    /// </summary>
    private sealed class ConfigSandbox : IDisposable
    {
        private readonly string? _previous;
        public string Root { get; }
        public ConfigSandbox()
        {
            _previous = Environment.GetEnvironmentVariable("DUPLIMATE_CONFIG_ROOT");
            Root = Path.Combine(Path.GetTempPath(), "fb-cfg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Environment.SetEnvironmentVariable("DUPLIMATE_CONFIG_ROOT", Root);
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DUPLIMATE_CONFIG_ROOT", _previous);
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fb-migrator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
