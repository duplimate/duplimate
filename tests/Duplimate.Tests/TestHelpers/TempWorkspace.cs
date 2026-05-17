using System;
using System.IO;

namespace Duplimate.Tests.TestHelpers;

/// <summary>
/// Temporary per-test directory on disk, auto-deleted on Dispose.
///
/// The workspace also swaps DUPLIMATE_CONFIG_ROOT to a subdirectory of itself
/// so any Duplimate service started during this test writes config,
/// logs, and repo state under the temp tree instead of the real portable
/// config folder. Disposing restores the previous environment value.
///
/// Usage:
///   using var ws = new TempWorkspace();
///   var source = ws.Sub("source");
///   SyntheticFileTree.Generate(source, seed: 42, targetBytes: 1_000_000);
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    private readonly string? _previousConfigRoot;
    private bool _disposed;

    public string Root { get; }
    public string ConfigRoot { get; }

    public TempWorkspace(string? label = null)
    {
        var name = label is null
            ? $"duplimate-test-{Guid.NewGuid():N}"
            : $"duplimate-test-{label}-{Guid.NewGuid():N}";
        Root = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(Root);

        ConfigRoot = Path.Combine(Root, "config");
        Directory.CreateDirectory(ConfigRoot);

        _previousConfigRoot = Environment.GetEnvironmentVariable("DUPLIMATE_CONFIG_ROOT");
        Environment.SetEnvironmentVariable("DUPLIMATE_CONFIG_ROOT", ConfigRoot);
    }

    /// <summary>Create (if missing) and return the path of a subdirectory of the workspace.</summary>
    public string Sub(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Environment.SetEnvironmentVariable("DUPLIMATE_CONFIG_ROOT", _previousConfigRoot);

        // Best-effort cleanup. Tests shouldn't hold open file handles but if
        // they do (a log still being written) we swallow the exception so
        // test teardown doesn't mask the real failure.
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
