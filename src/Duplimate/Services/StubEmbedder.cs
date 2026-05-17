using System;
using System.IO;
using System.Reflection;
using Avalonia.Platform;
using Serilog;

namespace Duplimate.Services;

/// <summary>
/// Extracts the embedded <c>Duplimate-stub.exe</c> to its canonical
/// per-user location so scheduled tasks can fall back to it when the
/// user's main exe is missing. Mirrors <see cref="DuplicacyEmbedder"/>'s
/// pattern: read the embedded Avalonia resource, write it to disk if
/// the cached copy isn't current, no-op otherwise.
///
/// Lookup order (matches DuplicacyEmbedder):
///   1. Embedded resource <c>Assets/Duplimate-stub.exe</c>.
///   2. Fall back to a sibling <c>Duplimate-stub.exe</c> next to the
///      running exe (dev convenience: drop a freshly-built stub in
///      bin/ for quick iteration).
///
/// If neither source exists, the extractor returns false and the
/// scheduled-task command falls back to a non-wrapped form (main exe
/// directly, no missing-exe warning safety net). This keeps the build
/// resilient: developers without the stub project published can still
/// run the main app.
/// </summary>
public static class StubEmbedder
{
    public const string AssetLogicalPath = "Assets/Duplimate-stub.exe";

    private static ILogger _log => AppLogger.For("StubEmbedder");

    /// <summary>
    /// Idempotently writes the embedded stub to <paramref name="targetPath"/>.
    /// No-op if the existing file's length already matches the resource.
    /// Returns true if the target ends up with a usable stub binary,
    /// false if no source could be found.
    /// </summary>
    public static bool EnsureExtracted(string targetPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            using var stream = OpenEmbeddedResource();
            if (stream is not null)
            {
                WriteIfNewer(stream, targetPath);
                return true;
            }

            // Dev fallback — pick up a freshly-built stub from bin/.
            var exeDir = Path.GetDirectoryName(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
            var nextToExe = Path.Combine(exeDir, "Duplimate-stub.exe");
            if (File.Exists(nextToExe))
            {
                File.Copy(nextToExe, targetPath, overwrite: true);
                return true;
            }

            // Last-ditch: a previous launch already extracted something.
            if (File.Exists(targetPath)) return true;

            _log.Warning(
                "Stub binary not embedded in this build and not present next to the app; " +
                "scheduled tasks will run without the missing-exe fallback warning. " +
                "Run launch-release.bat to publish the stub.");
            return false;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Stub extraction to {Target} failed", targetPath);
            return false;
        }
    }

    private static Stream? OpenEmbeddedResource()
    {
        // Same fix as DuplicacyEmbedder: AvaloniaResource items live
        // under avares://, NOT in the manifest-resource stream. Try
        // the Avalonia loader first; fall back to GetManifestResourceStream
        // for any future build that switches to <EmbeddedResource>.
        try
        {
            var uri = new Uri("avares://Duplimate/Assets/Duplimate-stub.exe");
            return AssetLoader.Open(uri);
        }
        catch { /* fall through */ }

        var asm = Assembly.GetExecutingAssembly();
        var asmName = asm.GetName().Name ?? "Duplimate";
        string[] candidates =
        {
            $"{asmName}.Assets.Duplimate-stub.exe",
            $"{asmName}.Assets/Duplimate-stub.exe",
            "Assets.Duplimate-stub.exe",
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
        var resourceLength = resource.Length;
        if (File.Exists(target))
        {
            try
            {
                var existingLen = new FileInfo(target).Length;
                if (existingLen == resourceLength) return;
            }
            catch { /* re-extract */ }
        }

        var tmp = target + ".tmp";
        using (var outStream = File.Create(tmp))
        {
            resource.CopyTo(outStream);
        }

        if (File.Exists(target))
            File.Replace(tmp, target, destinationBackupFileName: null);
        else
            File.Move(tmp, target);
    }
}
