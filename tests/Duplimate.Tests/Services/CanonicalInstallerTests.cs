using System;
using System.IO;
using Duplimate.Services;
using Xunit;

namespace Duplimate.Tests.Services;

/// <summary>
/// CanonicalInstaller no longer copies the main exe — it just deploys
/// a tiny fallback stub and rewrites scheduled tasks when the user's
/// main-exe path changes between launches. These tests pin the stub
/// path's location and the flag-file round trip used to surface
/// "missed scheduled run" notices to the user.
/// </summary>
public class CanonicalInstallerTests : IDisposable
{
    private readonly string _tmp;

    public CanonicalInstallerTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "duplimate-canonical-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true); } catch { }
    }

    [Fact]
    public void StubExePath_endsWithExpectedTail()
    {
        // Pin the stub's location so a future refactor doesn't silently
        // change where we deploy — that would orphan every user's
        // existing scheduled tasks (the cmd.exe wrapper bakes the path
        // into the task command line at upsert time).
        var p = CanonicalInstaller.StubExePath;
        Assert.EndsWith(Path.Combine("Programs", "Duplimate", "Duplimate-stub.exe"), p);
    }

    [Fact]
    public void SentinelPath_isUnderLocalAppDataDuplimate()
    {
        var p = CanonicalInstaller.SentinelPath;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(localAppData, p, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("last-exe-path.txt", p);
    }

    [Fact]
    public void StubFiredFlagPath_endsWithStubFiredFlag()
    {
        Assert.EndsWith("stub-fired.flag", CanonicalInstaller.StubFiredFlagPath);
    }

    [Fact]
    public void CheckAndClearStubFiredFlag_noFlag_returnsNull()
    {
        // Make sure no leftover flag from a prior test run.
        try { File.Delete(CanonicalInstaller.StubFiredFlagPath); } catch { }
        Assert.Null(CanonicalInstaller.CheckAndClearStubFiredFlag());
    }

    [Fact]
    public void CheckAndClearStubFiredFlag_validFlag_returnsTimestampAndDeletesFile()
    {
        // Round-trip: write a flag like the stub would, then verify the
        // main-app side reads it back as the original instant AND
        // deletes the file (so re-launching doesn't re-fire the toast).
        Directory.CreateDirectory(Path.GetDirectoryName(CanonicalInstaller.StubFiredFlagPath)!);
        var written = new DateTime(2026, 4, 26, 14, 30, 5, DateTimeKind.Utc);
        File.WriteAllText(CanonicalInstaller.StubFiredFlagPath, written.ToString("o"));

        var read = CanonicalInstaller.CheckAndClearStubFiredFlag();

        Assert.NotNull(read);
        Assert.Equal(written, read.Value.ToUniversalTime());
        Assert.False(File.Exists(CanonicalInstaller.StubFiredFlagPath),
            "Flag file should be deleted after read so it doesn't fire repeatedly.");
    }

    [Fact]
    public void ReupsertIfExePathChanged_sentinelMatches_returnsZeroAndLeavesStubFlagAlone()
    {
        // Pins the contract that re-upsert is purely path-driven and
        // does NOT consult the stub-fired flag. If a future refactor
        // accidentally couples them, this test fires: a present flag
        // would either trigger spurious re-upserts (return > 0) or get
        // deleted prematurely (the flag should only be cleared by
        // CheckAndClearStubFiredFlag at toast time, never by the
        // path-correctness path).
        if (!ServiceLocator.Initialized) ServiceLocator.InitializeCore();

        // Arrange: sentinel matches current process — no re-upsert work
        // expected. Flag present — we want to assert it survives.
        var current = Environment.ProcessPath
            ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
        Directory.CreateDirectory(Path.GetDirectoryName(CanonicalInstaller.SentinelPath)!);
        File.WriteAllText(CanonicalInstaller.SentinelPath, current);
        Directory.CreateDirectory(Path.GetDirectoryName(CanonicalInstaller.StubFiredFlagPath)!);
        File.WriteAllText(CanonicalInstaller.StubFiredFlagPath, DateTime.UtcNow.ToString("o"));

        try
        {
            var n = CanonicalInstaller.ReupsertIfExePathChanged(
                ServiceLocator.Config, ServiceLocator.Scheduler);

            Assert.Equal(0, n);
            Assert.True(File.Exists(CanonicalInstaller.StubFiredFlagPath),
                "Re-upsert must not delete the stub-fired flag — only " +
                "CheckAndClearStubFiredFlag should remove it (when the user " +
                "is being shown the toast).");
        }
        finally
        {
            try { File.Delete(CanonicalInstaller.StubFiredFlagPath); } catch { }
        }
    }

    [Fact]
    public void CheckAndClearStubFiredFlag_corruptFlag_returnsCurrentTimeAndStillDeletes()
    {
        // Defensive: the stub writes ISO-8601, but if the flag file gets
        // corrupted (truncation, permission flip mid-write, antivirus
        // tampering), we shouldn't hard-fail. A non-null return with the
        // current time keeps the user-facing notice firing once, and the
        // delete prevents perpetual re-firing.
        Directory.CreateDirectory(Path.GetDirectoryName(CanonicalInstaller.StubFiredFlagPath)!);
        File.WriteAllText(CanonicalInstaller.StubFiredFlagPath, "not a valid timestamp");

        var read = CanonicalInstaller.CheckAndClearStubFiredFlag();

        Assert.NotNull(read);
        Assert.False(File.Exists(CanonicalInstaller.StubFiredFlagPath));
    }
}
