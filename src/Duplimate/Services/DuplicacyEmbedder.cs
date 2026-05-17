using System;
using System.IO;
using System.Reflection;
using Duplimate.Services.Platform;
using Avalonia.Platform;

namespace Duplimate.Services;

/// <summary>
/// Extracts the embedded Duplicacy CLI to the config root on first run,
/// and on every run if the asset resource is newer than the extracted
/// copy (keeps the binary fresh across app updates without asking the
/// user to redo anything).
///
/// <para>
/// Binary name is platform-specific: <c>duplicacy.exe</c> on Windows,
/// <c>duplicacy</c> (no extension, chmod-755) on macOS / Linux. The
/// rest of the codebase only sees the canonical name through
/// <see cref="AppPaths.DuplicacyBinary"/>.
/// </para>
///
/// <para>
/// Look-ups in this order:
/// <list type="number">
///   <item><c>DuplicacyBinaryPathOverride</c> from settings (BYO binary).</item>
///   <item>Embedded resource <c>avares://Duplimate/Assets/&lt;binary&gt;</c>.</item>
///   <item>Sibling binary next to the app exe (dev convenience).</item>
///   <item>Cached extraction at <see cref="AppPaths.DuplicacyBinary"/>.</item>
/// </list>
/// </para>
/// </summary>
public static class DuplicacyEmbedder
{
    /// <summary>Logical asset path inside the bundled Avalonia resources.</summary>
    public static string AssetLogicalPath => "Assets/" + PlatformInfo.DuplicacyBinaryFileName;

    /// <summary>
    /// Cached resolved path. Once <see cref="EnsureExtracted"/> succeeds,
    /// every subsequent call returns the cached string without re-
    /// opening the embedded resource stream. Each backup run spawns
    /// duplicacy ~3-5 times (init, backup, prune, check); without the
    /// cache each spawn opens the avares stream just to read its Length
    /// for the same-size check. Reset by <see cref="ResetCache"/> if
    /// the override changes.
    /// </summary>
    private static string? _cachedPath;
    private static readonly object _cacheGate = new();

    /// <summary>Clear the cached path. Call after the user changes the
    /// DuplicacyBinaryPathOverride setting so the next spawn re-resolves.
    /// </summary>
    public static void ResetCache()
    {
        lock (_cacheGate)
        {
            // Volatile.Write inside the lock pairs with Volatile.Read
            // on the unlocked fast path — guarantees the null write is
            // published to other CPUs immediately, not just at the
            // next memory barrier. lock-exit IS a release barrier in
            // CLR's memory model so Volatile.Write is technically
            // redundant here, but spelling it out documents the
            // cross-thread visibility contract and survives a future
            // refactor that removes the lock.
            System.Threading.Volatile.Write(ref _cachedPath, null);
        }
    }

    /// <summary>
    /// Returns the full path to a runnable Duplicacy binary, extracting from
    /// embedded resources if needed. Throws if no binary can be located.
    /// </summary>
    public static string EnsureExtracted()
    {
        // Fast path: serve from cache. The cache invariant is
        // "the file at _cachedPath still exists" — verified cheaply on
        // each call so a user who deleted the cached binary mid-session
        // doesn't get a stale path. Volatile.Read pairs with the
        // Volatile.Write inside ResetCache + the lock-protected writes
        // below to ensure visibility across CPU caches.
        var cached = System.Threading.Volatile.Read(ref _cachedPath);
        if (cached is not null && File.Exists(cached)) return cached;
        AppPaths.EnsureAll();
        var target = AppPaths.DuplicacyBinary;

        // Bring-your-own-binary override: when Settings has a non-empty
        // DuplicacyBinaryPathOverride and the file exists, use it. The
        // override skips extraction entirely so the user's chosen build
        // is what runs (newer CLI version, custom fork, separately-
        // licensed commercial build). Stale path → warn + fall through
        // to the embedded extraction.
        try
        {
            var locator = ServiceLocator.Initialized
                ? ServiceLocator.Config.Current.DuplicacyBinaryPathOverride
                : null;
            if (!string.IsNullOrWhiteSpace(locator))
            {
                if (File.Exists(locator))
                {
                    EnsureExecutable(locator);
                    lock (_cacheGate) _cachedPath = locator;
                    return locator;
                }
                AppLogger.For(nameof(DuplicacyEmbedder)).Warning(
                    "DuplicacyBinaryPathOverride is set but the file is missing: {Path}. " +
                    "Falling back to the embedded binary.", locator);
            }
        }
        catch { /* config not yet available — fall through */ }

        using var stream = OpenEmbeddedResource();
        if (stream is not null)
        {
            WriteIfNewer(stream, target);
            EnsureExecutable(target);
            lock (_cacheGate) _cachedPath = target;
            return target;
        }

        // Dev fallback — the file might sit next to the binary.
        var exeDir = Path.GetDirectoryName(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
        var nextToExe = Path.Combine(exeDir, PlatformInfo.DuplicacyBinaryFileName);
        if (File.Exists(nextToExe))
        {
            EnsureExecutable(nextToExe);
            lock (_cacheGate) _cachedPath = nextToExe;
            return nextToExe;
        }

        // Last-ditch: if the user previously launched and we already extracted something.
        if (File.Exists(target))
        {
            EnsureExecutable(target);
            lock (_cacheGate) _cachedPath = target;
            return target;
        }

        // User-facing message — surfaces in the failure email, the
        // notification, the Logs view, and the per-cell error column.
        // Avoid developer-only language so a non-technical user reading
        // this knows what to do without a terminal.
        var fileName = PlatformInfo.DuplicacyBinaryFileName;
        throw new FileNotFoundException(
            $"Duplimate couldn't find {fileName}. The app's install looks " +
            $"incomplete — the Duplicacy binary that does the actual backing-up " +
            $"isn't bundled with this copy. Reinstall Duplimate from " +
            $"https://github.com/duplimate/duplimate/releases/latest, " +
            $"or place a Duplicacy CLI build (renamed to {fileName}) " +
            $"next to the Duplimate binary and reopen the app.");
    }

    /// <summary>
    /// Cheap check used at app launch (and elsewhere) to detect a broken
    /// install BEFORE the user kicks off a backup that's doomed to fail
    /// halfway through with the not-found error. Returns true if a
    /// Duplicacy binary is reachable through any of the lookup paths;
    /// false otherwise. Does NOT extract — extraction happens lazily on
    /// the first real call to <see cref="EnsureExtracted"/>.
    /// </summary>
    public static bool IsAvailable()
    {
        // User's bring-your-own-binary override — same precedence
        // EnsureExtracted uses, so a startup availability check
        // matches what the actual run-time resolution will see.
        // Without this, a user who configured an override would get
        // a "duplicacy missing" notification on every launch even
        // though the override resolves cleanly at run time.
        try
        {
            if (ServiceLocator.Initialized)
            {
                var locator = ServiceLocator.Config.Current.DuplicacyBinaryPathOverride;
                if (!string.IsNullOrWhiteSpace(locator) && File.Exists(locator)) return true;
            }
        }
        catch { /* config not yet available — fall through */ }

        try
        {
            // Embedded resource present?
            using var stream = OpenEmbeddedResource();
            if (stream is not null) return true;
        }
        catch { /* fall through */ }

        // Sibling binary?
        try
        {
            var exeDir = Path.GetDirectoryName(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
            if (File.Exists(Path.Combine(exeDir, PlatformInfo.DuplicacyBinaryFileName))) return true;
        }
        catch { /* fall through */ }

        // Already-extracted cache?
        try { if (File.Exists(AppPaths.DuplicacyBinary)) return true; }
        catch { /* fall through */ }

        return false;
    }

    private static Stream? OpenEmbeddedResource()
    {
        // PRIMARY: AvaloniaResource items are exposed via avares:// URIs,
        // NOT via Assembly.GetManifestResourceStream. Earlier builds of
        // this method only used GetManifestResourceStream and silently
        // returned null in published self-contained builds — the user's
        // first backup attempt failed with "duplicacy is neither
        // embedded in this build" even though the binary WAS bundled.
        try
        {
            var uri = new Uri("avares://Duplimate/" + AssetLogicalPath);
            return AssetLoader.Open(uri);
        }
        catch { /* fall through to manifest-resource fallback */ }

        // FALLBACK: keep the manifest-resource lookup for any future
        // build that uses <EmbeddedResource> instead of (or alongside)
        // <AvaloniaResource>. Cheap to try.
        var asm = Assembly.GetExecutingAssembly();
        var asmName = asm.GetName().Name ?? "Duplimate";
        var fname = PlatformInfo.DuplicacyBinaryFileName;
        string[] candidates =
        {
            $"{asmName}.Assets.{fname}",
            $"{asmName}.Assets/{fname}",
            $"Assets.{fname}",
            AssetLogicalPath,
        };
        foreach (var name in candidates)
        {
            var s = asm.GetManifestResourceStream(name);
            if (s is not null) return s;
        }
        return null;
    }

    private static void WriteIfNewer(Stream resource, string target)
    {
        // Per-call unique tmp suffix. Two concurrent callers (e.g. two
        // backup cells starting in parallel on a process whose
        // _cachedPath is still null) used to race on the same
        // `target + ".tmp"` path: File.Create on the second attempt
        // either threw a sharing violation or clobbered the in-flight
        // CopyTo of the first, then File.Replace failed with
        // FileNotFoundException. Distinct suffixes serialise atomically
        // at the File.Replace step instead.
        var tmp = $"{target}.{Guid.NewGuid():N}.tmp";

        // Cheap "is it different?" check — compare length. Perfect collision is
        // astronomically unlikely for a single pinned binary and keeps startup fast.
        // Some Avalonia builds expose avares:// resources via a wrapper
        // stream where CanSeek == false and Length throws
        // NotSupportedException. In that case skip the optimisation and
        // always extract — re-extraction is harmless, the alternative
        // is surfacing a "duplicacy missing" error to the user even
        // though the binary IS bundled.
        long? resourceLength = null;
        try { if (resource.CanSeek) resourceLength = resource.Length; }
        catch (NotSupportedException) { /* non-seekable stream */ }
        if (resourceLength is long rl && File.Exists(target))
        {
            try
            {
                var existingLen = new FileInfo(target).Length;
                if (existingLen == rl) return;
            }
            catch { /* fall through and re-extract */ }
        }

        try
        {
            using (var outStream = File.Create(tmp))
            {
                resource.CopyTo(outStream);
            }

            if (File.Exists(target))
                File.Replace(tmp, target, destinationBackupFileName: null);
            else
                File.Move(tmp, target);
        }
        finally
        {
            // Best-effort cleanup if we exited via exception or if a
            // concurrent extractor already moved its tmp into place
            // ahead of ours (File.Replace would have moved ours into
            // target, but a failed Move/Replace leaves us holding the
            // tmp).
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// Ensures the on-disk binary has the +x bit set. No-op on Windows
    /// (NTFS doesn't carry the executable bit; the .exe extension is
    /// authoritative). On macOS / Linux a fresh AvaloniaResource extract
    /// arrives as 0644 — without this, <see cref="System.Diagnostics.Process.Start(string)"/>
    /// fails with "Permission denied" before duplicacy ever sees argv.
    /// </summary>
    private static void EnsureExecutable(string path)
    {
        if (PlatformInfo.IsWindows) return;
        try
        {
            const UnixFileMode rwxr_xr_x =
                UnixFileMode.UserRead   | UnixFileMode.UserWrite   | UnixFileMode.UserExecute  |
                UnixFileMode.GroupRead  | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead  | UnixFileMode.OtherExecute;
            var current = File.GetUnixFileMode(path);
            if ((current & UnixFileMode.UserExecute) == 0)
                File.SetUnixFileMode(path, rwxr_xr_x);
        }
        catch (Exception ex)
        {
            AppLogger.For(nameof(DuplicacyEmbedder)).Warning(ex,
                "Couldn't chmod +x on {Path}; the next spawn may fail with Permission denied", path);
        }
    }
}
