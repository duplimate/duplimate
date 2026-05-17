using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Duplimate.Models;

namespace Duplimate.Services;

/// <summary>
/// One-shot importer that turns existing Duplicacy state into Duplimate
/// Backups + Destinations. Supports two layouts:
///
/// 1. **Single repo (plain Duplicacy users)** — the picked folder *is* the
///    repository: it contains <c>.duplicacy/preferences</c> directly. This
///    matches anyone who ran <c>duplicacy init</c> in a project folder and
///    never used the duplicacy-util wrapper.
///
/// 2. **Parent of many repos (duplicacy-util users)** — the picked folder
///    contains one subfolder per repo, each with its own <c>.duplicacy</c>
///    directory; the parent also holds the per-repo <c>&lt;name&gt;.yaml</c> files
///    that duplicacy-util reads.
///
/// The importer is intentionally tolerant: unknown keys are logged and skipped,
/// but anything we recognize is preserved. Running it twice is idempotent —
/// backups are keyed by the snapshot id (preferences.id, with folder-name
/// fallback for the parent-of-many layout where that convention coincides).
/// </summary>
public sealed class ConfigMigrator
{
    private readonly ConfigStore _config;

    public ConfigMigrator(ConfigStore config) => _config = config;

    public MigrationReport MigrateFrom(string legacyDir)
    {
        var report = new MigrationReport { LegacyDir = legacyDir };

        if (!Directory.Exists(legacyDir))
        {
            report.Skipped = true;
            report.Reason = "Directory does not exist";
            return report;
        }

        // Two layouts:
        //   1. legacyDir itself is a Duplicacy repository (contains .duplicacy/preferences) — plain user.
        //   2. legacyDir contains repos as children — duplicacy-util convention.
        // The yaml directory (where duplicacy-util's per-repo yamls live) differs
        // between the two: for plain users it's the parent of the repo dir,
        // for duplicacy-util users it IS legacyDir.
        var selfIsRepo = File.Exists(Path.Combine(legacyDir, ".duplicacy", "preferences"));

        var repoDirs = selfIsRepo
            ? new List<string> { legacyDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) }
            : Directory.GetDirectories(legacyDir)
                .Where(d => File.Exists(Path.Combine(d, ".duplicacy", "preferences")))
                .ToList();

        var yamlDir = selfIsRepo
            ? Path.GetDirectoryName(legacyDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? legacyDir
            : legacyDir;

        if (repoDirs.Count == 0)
        {
            report.Skipped = true;
            report.Reason = "No Duplicacy repositories found. Pick the folder that contains your .duplicacy directory, or a parent folder that contains several.";
            return report;
        }

        _config.Update(cfg =>
        {
            foreach (var repoDir in repoDirs)
            {
                // Folder names on disk can contain spaces, parentheses,
                // dots — anything the user named the repo. Backup.Name
                // must be ^[A-Za-z0-9_-]+$ because it ends up inside the
                // scheduled-task cmd /S /C wrapper. Sanitize at the boundary.
                var rawName = Path.GetFileName(repoDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;
                var fallbackName = Backup.SanitizeName(rawName);
                try
                {
                    var imported = ImportOne(cfg, yamlDir, repoDir, fallbackName);
                    if (imported is not null) report.ImportedBackups.Add(imported);
                }
                catch (Exception ex)
                {
                    report.Errors.Add($"{fallbackName}: {ex.Message}");
                }
            }
        });

        return report;
    }

    // ---- core translation ----

    private string? ImportOne(AppConfig cfg, string yamlDir, string repoDir, string fallbackName)
    {
        // 1) read .duplicacy/preferences — authoritative source for storage URL + repository path
        var prefsPath = File.Exists(Path.Combine(repoDir, ".duplicacy", "preferences"))
            ? Path.Combine(repoDir, ".duplicacy", "preferences")
            : Path.Combine(repoDir, "preferences");

        var prefsJson = File.ReadAllText(prefsPath);
        var prefs = JsonSerializer.Deserialize<List<LegacyPreferences>>(prefsJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? new();

        if (prefs.Count == 0)
            return null;

        // Prefer the snapshot id (preferences.id) — that's the name Duplicacy
        // itself uses on disk inside snapshots/. For duplicacy-util layouts
        // this typically equals the folder name, so the result is the same.
        // For plain users the folder might be "Documents" or "C:\Projects",
        // which would sanitize to something ugly; the snapshot id is cleaner.
        var idFromPrefs = prefs[0].Id;
        var repoName = !string.IsNullOrWhiteSpace(idFromPrefs)
            ? Backup.SanitizeName(idFromPrefs)
            : fallbackName;
        if (string.IsNullOrWhiteSpace(repoName))
            repoName = "imported";

        // 2) read per-repo yaml (e.g. local_c.yaml) next to yamlDir — has threads/keep/prune/check.
        //    Plain Duplicacy users don't have one; we fall back to defaults silently.
        //    We try both repoName.yaml (the sanitized id) and the raw folder name
        //    to cover duplicacy-util setups where the yaml is named after the folder.
        var repoYaml = ReadFirstExistingYaml(yamlDir, repoName, fallbackName);

        // 3) filters file next to the repo's preferences, if any
        var filtersPath = File.Exists(Path.Combine(repoDir, ".duplicacy", "filters"))
            ? Path.Combine(repoDir, ".duplicacy", "filters")
            : Path.Combine(repoDir, "filters");
        var filters = File.Exists(filtersPath) ? File.ReadAllText(filtersPath) : "";

        // Upsert the backup by name (idempotent on re-run)
        var backup = cfg.Backups.FirstOrDefault(b => string.Equals(b.Name, repoName, StringComparison.OrdinalIgnoreCase))
                     ?? new Backup { Name = repoName };

        // Source path = repository field from preferences (first entry wins).
        // Legacy duplicacy-util is single-source per repo, so we seed a
        // one-element SourcePaths list. The new model supports multi-source
        // per backup; users can add more after import via the editor.
        backup.SourcePaths.Clear();
        var sp = prefs[0].Repository ?? "";
        if (!string.IsNullOrWhiteSpace(sp)) backup.SourcePaths.Add(sp);
        backup.FiltersText = filters;

        // Legacy yaml storage[] → one BackupTarget each, pointing at an upserted Destination.
        backup.Targets.Clear();
        foreach (var p in prefs)
        {
            var dest = UpsertDestinationFromPreference(cfg, repoName, p);
            backup.Targets.Add(new BackupTarget
            {
                DestinationId = dest.Id,
                StorageName = string.IsNullOrEmpty(p.Name) ? "default" : p.Name,
                ThreadsOverride = LookupThreadsOverride(repoYaml, p.Name),
            });
        }

        // Pull up common options from yaml, falling back to sensible defaults.
        var firstStorage = repoYaml.Storage?.FirstOrDefault();
        backup.Threads = firstStorage?.Threads ?? 1;
        backup.UseVss = firstStorage?.Vss ?? true;

        var firstPrune = repoYaml.Prune?.FirstOrDefault();
        backup.KeepPolicy = firstPrune?.Keep ?? "0:365 30:90 7:30 1:7";
        backup.PruneAfterBackup = repoYaml.Prune is { Count: > 0 };
        backup.CheckAfterBackup = repoYaml.Check is { Count: > 0 };

        // Default schedule matches the legacy scheduled-task.xml: daily at 20:00.
        backup.Schedule = new BackupSchedule
        {
            Frequency = ScheduleFrequency.Daily,
            TimeOfDay = TimeSpan.FromHours(20),
            SkipOnBattery = true,
            StopOnBattery = true,
            RequireNetwork = true,
            CatchUpMissedRuns = true,
            ExecutionTimeLimit = TimeSpan.FromHours(72),
        };

        // Add if not present; otherwise the in-place mutation above already updated it.
        if (!cfg.Backups.Contains(backup))
            cfg.Backups.Add(backup);

        return backup.Name;
    }

    private Destination UpsertDestinationFromPreference(AppConfig cfg, string backupName, LegacyPreferences p)
    {
        var url = p.Storage ?? "";
        var kind = DetectKind(url);
        var subpath = ExtractSubpath(url, kind);

        // Name we'll show in the UI. For local paths, look for a volume label match.
        var displayName = BuildDisplayName(kind, url, subpath);

        // Match existing destination by exact storage URL to avoid duplicates on re-run.
        var existing = cfg.Destinations.FirstOrDefault(d => string.Equals(d.BuildStorageUrl(), url, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var dest = new Destination
        {
            Name = displayName,
            Kind = kind,
            PathOrSubpath = subpath,
            Encrypted = p.Encrypted,
        };

        // External-drive heuristic: if URL starts with a drive letter we don't think is the system drive,
        // fill in expected-drive-letter and expected-volume-label so the user can confirm in the UI.
        if (kind is DestinationKind.LocalFolder or DestinationKind.ExternalDrive
            && url.Length >= 2 && url[1] == ':')
        {
            var letter = url[..2].ToUpperInvariant();
            dest.ExpectedDriveLetter = letter;
            // Best-effort volume label lookup; silent if drive isn't mounted right now.
            try
            {
                var drive = new DriveInfo(letter);
                if (drive.IsReady && !string.IsNullOrEmpty(drive.VolumeLabel))
                {
                    dest.ExpectedVolumeLabel = drive.VolumeLabel;
                    // Promote to ExternalDrive if it's a removable / fixed non-system drive.
                    if (drive.DriveType == DriveType.Removable ||
                        (drive.DriveType == DriveType.Fixed && !IsSystemDrive(letter)))
                        dest.Kind = DestinationKind.ExternalDrive;
                }
            }
            catch { /* drive not mounted — leave label unset */ }
        }

        cfg.Destinations.Add(dest);
        return dest;
    }

    private static bool IsSystemDrive(string letter)
    {
        var sys = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System))
            ?? "C:\\";
        return sys.StartsWith(letter, StringComparison.OrdinalIgnoreCase);
    }

    private static DestinationKind DetectKind(string url)
    {
        if (url.StartsWith("dropbox://", StringComparison.OrdinalIgnoreCase))   return DestinationKind.DropboxAppScoped;
        if (url.StartsWith("one://", StringComparison.OrdinalIgnoreCase))       return DestinationKind.OneDrivePersonal;
        if (url.StartsWith("odb://", StringComparison.OrdinalIgnoreCase))       return DestinationKind.OneDriveBusiness;
        if (url.StartsWith("gcd://", StringComparison.OrdinalIgnoreCase))       return DestinationKind.GoogleDrive;
        if (url.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))        return DestinationKind.S3Compatible;
        if (url.StartsWith("\\\\", StringComparison.Ordinal))                   return DestinationKind.NetworkShare;
        return DestinationKind.LocalFolder;
    }

    private static string ExtractSubpath(string url, DestinationKind kind)
    {
        return kind switch
        {
            DestinationKind.DropboxAppScoped or DestinationKind.DropboxFullAccess
                => url.Substring("dropbox://".Length),
            DestinationKind.OneDrivePersonal => url.Substring("one://".Length),
            DestinationKind.OneDriveBusiness => url.Substring("odb://".Length),
            DestinationKind.GoogleDrive      => url.Substring("gcd://".Length),
            DestinationKind.S3Compatible     => url.Substring("s3://".Length),
            _ => url, // local / external / network — the path IS the url
        };
    }

    private static string BuildDisplayName(DestinationKind kind, string url, string subpath) => kind switch
    {
        DestinationKind.DropboxAppScoped  => $"Dropbox — {subpath}",
        DestinationKind.DropboxFullAccess => $"Dropbox (full) — {subpath}",
        DestinationKind.OneDrivePersonal  => $"OneDrive — {subpath}",
        DestinationKind.OneDriveBusiness  => $"OneDrive Business — {subpath}",
        DestinationKind.GoogleDrive       => $"Google Drive — {subpath}",
        DestinationKind.S3Compatible      => $"S3 — {subpath}",
        DestinationKind.NetworkShare      => $"Network — {url}",
        DestinationKind.ExternalDrive     => $"External — {url}",
        _                                 => $"Local — {url}",
    };

    private static SimpleYaml.Root ReadFirstExistingYaml(string yamlDir, params string[] candidateNames)
    {
        foreach (var name in candidateNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var path = Path.Combine(yamlDir, name + ".yaml");
            if (File.Exists(path))
                return SimpleYaml.Parse(File.ReadAllText(path));
        }
        return new SimpleYaml.Root();
    }

    private static int? LookupThreadsOverride(SimpleYaml.Root yaml, string? storageName)
    {
        if (yaml.Storage is null || storageName is null) return null;
        foreach (var s in yaml.Storage)
            if (string.Equals(s.Name, storageName, StringComparison.OrdinalIgnoreCase))
                return s.Threads;
        return null;
    }

    // ---- legacy JSON shape ----

    private sealed class LegacyPreferences
    {
        public string? Name { get; set; }
        public string? Id { get; set; }
        public string? Repository { get; set; }
        public string? Storage { get; set; }
        public bool Encrypted { get; set; }
    }
}

public sealed class MigrationReport
{
    public string? LegacyDir { get; set; }
    public bool Skipped { get; set; }
    public string? Reason { get; set; }
    public List<string> ImportedBackups { get; } = new();
    public List<string> Errors { get; } = new();
}

/// <summary>
/// A tiny purpose-built parser for the specific shape of duplicacy-util repo yamls
/// the user has. We don't add a full YAML dependency for one file format with a
/// 4-key vocabulary.
/// </summary>
internal static class SimpleYaml
{
    // Example input:
    //   repository: .
    //   storage:
    //       -   name: dropbox
    //           threads: 1
    //           vss: true
    //   prune:
    //       -   storage: dropbox
    //           keep: "0:700 30:365 7:90 1:14"
    //           threads: 6
    //   check:
    //       -   storage: dropbox

    public sealed class Root
    {
        public string? Repository { get; set; }
        public List<StorageEntry>? Storage { get; set; }
        public List<PruneEntry>? Prune { get; set; }
        public List<CheckEntry>? Check { get; set; }
    }

    public sealed class StorageEntry
    {
        public string? Name { get; set; }
        public int? Threads { get; set; }
        public bool? Vss { get; set; }
    }

    public sealed class PruneEntry
    {
        public string? Storage { get; set; }
        public string? Keep { get; set; }
        public int? Threads { get; set; }
    }

    public sealed class CheckEntry
    {
        public string? Storage { get; set; }
    }

    public static Root Parse(string text)
    {
        var root = new Root();
        string? currentList = null;            // "storage" | "prune" | "check"
        Dictionary<string, string>? currentItem = null;

        void Flush()
        {
            if (currentList is null || currentItem is null) return;
            switch (currentList)
            {
                case "storage":
                    (root.Storage ??= new()).Add(new StorageEntry
                    {
                        Name = currentItem.GetValueOrDefault("name"),
                        Threads = int.TryParse(currentItem.GetValueOrDefault("threads"), out var t) ? t : null,
                        Vss = bool.TryParse(currentItem.GetValueOrDefault("vss"), out var v) ? v : null,
                    });
                    break;
                case "prune":
                    (root.Prune ??= new()).Add(new PruneEntry
                    {
                        Storage = currentItem.GetValueOrDefault("storage"),
                        Keep = currentItem.GetValueOrDefault("keep"),
                        Threads = int.TryParse(currentItem.GetValueOrDefault("threads"), out var tp) ? tp : null,
                    });
                    break;
                case "check":
                    (root.Check ??= new()).Add(new CheckEntry
                    {
                        Storage = currentItem.GetValueOrDefault("storage"),
                    });
                    break;
            }
            currentItem = null;
        }

        var listHeader = new Regex(@"^\s*(storage|prune|check)\s*:\s*$", RegexOptions.IgnoreCase);
        var itemStart = new Regex(@"^\s*-\s*(?<k>[\w-]+)\s*:\s*(?<v>.*?)\s*$");
        var kvPair = new Regex(@"^\s{2,}(?<k>[\w-]+)\s*:\s*(?<v>.*?)\s*$");
        var rootKv = new Regex(@"^(?<k>[\w-]+)\s*:\s*(?<v>.*?)\s*$");

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) continue;

            var hm = listHeader.Match(line);
            if (hm.Success) { Flush(); currentList = hm.Groups[1].Value.ToLowerInvariant(); continue; }

            var im = itemStart.Match(line);
            if (im.Success && currentList is not null)
            {
                Flush();
                currentItem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [im.Groups["k"].Value] = TrimQuotes(im.Groups["v"].Value),
                };
                continue;
            }

            var km = kvPair.Match(line);
            if (km.Success && currentItem is not null)
            {
                currentItem[km.Groups["k"].Value] = TrimQuotes(km.Groups["v"].Value);
                continue;
            }

            // Root-level key (e.g. "repository: .")
            var rm = rootKv.Match(line);
            if (rm.Success && currentList is null)
            {
                if (string.Equals(rm.Groups["k"].Value, "repository", StringComparison.OrdinalIgnoreCase))
                    root.Repository = TrimQuotes(rm.Groups["v"].Value);
            }
        }
        Flush();
        return root;
    }

    private static string TrimQuotes(string s)
    {
        if (s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '\'' && s[^1] == '\''))
            return s.Substring(1, s.Length - 2);
        return s;
    }
}
