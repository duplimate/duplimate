using System;
using System.IO;
using System.Runtime.CompilerServices;
using Duplimate.Services;
using Xunit;

// Tests mutate process-wide state (the DUPLIMATE_CONFIG_ROOT env var,
// ServiceLocator, Avalonia's dispatcher). If xUnit ran collections in
// parallel, one TempWorkspace's setenv would clobber another's and
// subsequent Process.Start calls would use the wrong config dir.
// Disable parallelization at the assembly level so every test runs serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Duplimate.Tests.TestHelpers;

/// <summary>
/// Process-wide bootstrap that runs before anything else in this assembly,
/// including xUnit's discovery. Sets up an isolated config root and spins
/// up <see cref="ServiceLocator"/> so plain [Fact] tests that don't touch
/// Avalonia can still use Config, SecretsStore, Orchestrator, etc. without
/// waiting for the headless app to come up.
///
/// [ModuleInitializer] was added in C# 9 and executes exactly once per
/// loaded assembly. We use it here because static constructors on test
/// classes aren't reliable for setup that must happen before the xUnit
/// test-discovery pass.
/// </summary>
internal static class TestSetup
{
    private static string? _suiteRoot;

    [ModuleInitializer]
    internal static void Init()
    {
        var existing = Environment.GetEnvironmentVariable("DUPLIMATE_CONFIG_ROOT");
        if (string.IsNullOrWhiteSpace(existing))
        {
            _suiteRoot = Path.Combine(Path.GetTempPath(),
                $"duplimate-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_suiteRoot);
            Environment.SetEnvironmentVariable("DUPLIMATE_CONFIG_ROOT", _suiteRoot);

            // Without this, every test run on a dev box leaks the
            // suite-level temp dir (config + extracted duplicacy.exe
            // ~12 MB each). Hooked on ProcessExit so even a Ctrl-C
            // tries to clean up; best-effort because the runtime can
            // give us only a couple of seconds in that handler.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteSuiteRoot();
        }

        // Idempotent — fine to call again when the Avalonia test app boots.
        ServiceLocator.InitializeCore();
    }

    private static void TryDeleteSuiteRoot()
    {
        if (string.IsNullOrEmpty(_suiteRoot)) return;
        try
        {
            if (Directory.Exists(_suiteRoot)) Directory.Delete(_suiteRoot, recursive: true);
        }
        catch { /* best-effort — locked files, AV, etc. */ }
    }
}
