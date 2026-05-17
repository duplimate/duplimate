using System;
using System.IO;
using System.Reflection;
using Duplimate.Services.Platform;

namespace Duplimate.Services;

/// <summary>
/// Single source of truth for where we store things on disk.
///
/// Duplimate is portable: everything the app creates lives under
/// <c>&lt;exe-dir&gt;/Duplimate.config/</c> so a user can back up that single
/// folder (or drop it onto a new machine alongside the exe) and be back in
/// business. The only machine-bound files are the secrets vault
/// (<c>secrets.bin</c>) and, on Unix, its keyfile — neither can travel
/// between machines without the user re-entering destination secrets.
/// Backup <em>definitions</em> (config.json) travel losslessly.
///
/// <para>
/// Dev-time layout: when running under <c>dotnet run</c>,
/// <see cref="ExeDirectory"/> resolves to <c>bin/Debug/&lt;tfm&gt;/</c> — config
/// sits there during development, which keeps local-user state out of
/// the user's home directory but means a <c>dotnet clean</c> would wipe
/// it. Published single-file binaries produce a clean layout.
/// </para>
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "Duplimate.config";

    /// <summary>
    /// Directory containing the app executable. Uses Environment.ProcessPath
    /// so this resolves correctly for both a JIT'd DLL under `dotnet run` and
    /// a published single-file binary.
    /// </summary>
    public static string ExeDirectory
    {
        get
        {
            var procPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(procPath))
            {
                var dir = Path.GetDirectoryName(procPath);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
            // Fallback (design-time): the entry assembly location.
            var asmLoc = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(asmLoc))
            {
                var dir = Path.GetDirectoryName(asmLoc);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
            return AppContext.BaseDirectory;
        }
    }

    /// <summary>
    /// Overridable at process start via the DUPLIMATE_CONFIG_ROOT environment
    /// variable. Production paths through <see cref="ExeDirectory"/>; the
    /// override exists so the test harness can give each test its own isolated
    /// config directory (under TMPDIR) without poking the user's real state.
    /// </summary>
    public static string ConfigRoot
    {
        get
        {
            var overrideRoot = Environment.GetEnvironmentVariable("DUPLIMATE_CONFIG_ROOT");
            if (!string.IsNullOrWhiteSpace(overrideRoot)) return overrideRoot;
            return Path.Combine(ExeDirectory, AppFolderName);
        }
    }

    /// <summary>
    /// Everything lives under ConfigRoot now (portable). Kept as a distinct
    /// property name so the logs/repos callers don't need to care that we
    /// collapsed the old %APPDATA% / %LOCALAPPDATA% split.
    /// </summary>
    public static string LocalRoot => ConfigRoot;

    public static string ConfigFile  => Path.Combine(ConfigRoot, "config.json");
    public static string SecretsFile => Path.Combine(ConfigRoot, "secrets.bin");

    /// <summary>
    /// Path to the bundled Duplicacy CLI. Filename is platform-specific:
    /// <c>duplicacy.exe</c> on Windows, <c>duplicacy</c> on macOS / Linux.
    /// </summary>
    public static string DuplicacyBinary => Path.Combine(ConfigRoot, PlatformInfo.DuplicacyBinaryFileName);

    public static string LogsRoot  => Path.Combine(ConfigRoot, "logs");
    public static string ReposRoot => Path.Combine(ConfigRoot, "repos");

    /// <summary>
    /// Application-level logs (Serilog — structured, daily-rolling, the
    /// human-scale diagnostic trail). Distinct from per-backup-run logs
    /// under <see cref="LogDirForBackup"/>, which capture raw duplicacy stdout
    /// (machine-scale, can be hundreds of MB per run).
    /// </summary>
    public static string AppLogsDir => Path.Combine(LogsRoot, "app");

    /// <summary>Root of raw-duplicacy per-run transcripts.</summary>
    public static string DuplicacyLogsRoot => Path.Combine(LogsRoot, "duplicacy");

    public static string LogDirForBackup(string backupName) =>
        Path.Combine(DuplicacyLogsRoot, Sanitize(backupName));

    /// <summary>
    /// Per-backup repo root. Each source path of the backup gets its own
    /// child under here (see <see cref="RepoDirForBackupSource"/>) — we
    /// keep them grouped so deleting a backup cleans up all its sources
    /// in one rmdir. Single-source backups still get a single subdir
    /// named for the source slug; there's no special-case "flat" layout.
    /// </summary>
    public static string RepoDirForBackup(string backupName) =>
        Path.Combine(ReposRoot, Sanitize(backupName));

    /// <summary>
    /// Per-(backup, source) repo dir. <paramref name="sourcePath"/> is
    /// the absolute source path; we turn it into a filesystem-safe slug
    /// so the repo path is stable (backup runs won't move because the
    /// slug is derived deterministically from the source path).
    /// </summary>
    public static string RepoDirForBackupSource(string backupName, string sourcePath) =>
        Path.Combine(RepoDirForBackup(backupName), SourceSlug(sourcePath));

    /// <summary>
    /// Deterministic filesystem-safe slug for a source path. Keeps a
    /// best-effort representation of the path tail so users browsing
    /// <c>&lt;config-root&gt;/repos/</c> can tell which source is which
    /// without decoding a hash. Falls back to a short SHA-derived suffix
    /// if the sanitized form collapses to an empty string (e.g. unnamed
    /// UNC shares, or a Unix path that consisted only of slashes).
    /// </summary>
    public static string SourceSlug(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return "_";
        var trimmed = sourcePath.TrimEnd('\\', '/');
        // Drive-letter colon (Windows: "C:") and Unix root-marker — both
        // collapse to underscore so the slug stays filename-safe.
        var withoutColon = trimmed.Replace(":", "_");
        var sanitized = Sanitize(withoutColon.Replace('\\', '_').Replace('/', '_'));
        if (string.IsNullOrWhiteSpace(sanitized) || sanitized == "_")
        {
            return "src_" + ShortHash(sourcePath);
        }

        // Deep paths can blow past Windows MAX_PATH when combined with
        // the repos\<name>\<slug>\.duplicacy\ prefix. Cap at ~50 chars.
        // Naive truncation collides when two sources share a long common
        // prefix (think two subfolders under
        // `C:\Users\me\AppData\Local\Temp\duplimate-…-{guid}\`) — so we
        // always append a short content hash to disambiguate, even when
        // the sanitized form fits inside the cap. Costs 9 characters;
        // pays for itself the first time a user has two `Documents` in
        // different branches.
        const int MaxHeadLen = 44;
        var head = sanitized.Length <= MaxHeadLen
            ? sanitized
            : sanitized.Substring(sanitized.Length - MaxHeadLen); // prefer the *tail* — leaf folder names are more informative
        return head + "_" + ShortHash(sourcePath);
    }

    private static string ShortHash(string s)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).Substring(0, 8).ToLowerInvariant();
    }

    public static void EnsureAll()
    {
        Directory.CreateDirectory(ConfigRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(ReposRoot);
        Directory.CreateDirectory(AppLogsDir);
        Directory.CreateDirectory(DuplicacyLogsRoot);
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buf = stackalloc char[name.Length];
        for (int i = 0; i < name.Length; i++)
            buf[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
        return new string(buf);
    }
}
