using Duplimate.Services;
using Xunit;

namespace Duplimate.Tests.Services;

/// <summary>
/// Pins the centralized verbose-display filter shared by the Restore
/// wizard's terminal pane, the LogsView live tail, and the LogsView
/// past-run pane. The user's invariant: "DEBUG/TRACE lines should be
/// filtered out from display when verbose is off, but ALWAYS stored —
/// verbose is only a display thing, not a recording filter." These
/// tests pin the filter's behavior; the storage-stays-raw invariant
/// is pinned by the call-site tests that assert
/// _progressLogBuffer / _liveBuffer keep every line regardless of
/// the verbose setting.
/// </summary>
public class VerboseLogFilterTests
{
    [Fact]
    public void IsDuplicacyDebugLine_AnchoredToHeader()
    {
        // Both DEBUG and TRACE at the standard duplicacy header
        // position should classify as debug-level. INFO/WARN/ERROR
        // and lines that merely embed the substring " DEBUG " in a
        // path or message body should NOT — earlier the predicate
        // was a bare Contains and a folder named "C:\My DEBUG Stuff"
        // disappeared from the restore pane.
        Assert.True(VerboseLogFilter.IsDuplicacyDebugLine(
            "2026-04-22 15:56:29.939 DEBUG SOMETHING noisy detail"));
        Assert.True(VerboseLogFilter.IsDuplicacyDebugLine(
            "2026-05-01 15:35:27.456 TRACE RESTORE_PATTERN +launcher.20251120.log"));
        // Without fractional seconds — duplicacy occasionally omits.
        Assert.True(VerboseLogFilter.IsDuplicacyDebugLine(
            "2026-04-22 15:56:29 DEBUG something"));

        Assert.False(VerboseLogFilter.IsDuplicacyDebugLine(
            "2026-04-22 15:56:29.939 INFO RUN_BACKUP started"));
        Assert.False(VerboseLogFilter.IsDuplicacyDebugLine(
            "2026-04-22 15:56:29.939 WARN something odd"));
        Assert.False(VerboseLogFilter.IsDuplicacyDebugLine(
            "2026-04-22 15:56:29.939 ERROR FATAL kaboom"));
        Assert.False(VerboseLogFilter.IsDuplicacyDebugLine(
            "2026-04-22 15:56:29.939 INFO RESTORE_FILE C:\\My DEBUG Stuff\\note.txt restored"));
        Assert.False(VerboseLogFilter.IsDuplicacyDebugLine(
            "2026-04-22 15:56:29.939 INFO RUN_INFO Set debug level: from DEBUG to INFO"));
        Assert.False(VerboseLogFilter.IsDuplicacyDebugLine("DEBUG something"));
        Assert.False(VerboseLogFilter.IsDuplicacyDebugLine(""));
    }

    [Fact]
    public void FilterForDisplay_VerboseOff_StripsDebugAndTrace()
    {
        ResetConfig();
        SetVerbose(false);

        var raw =
            "2026-04-22 15:56:29.939 INFO RUN_BACKUP started\n" +
            "2026-04-22 15:56:29.940 DEBUG NOISE detail noise noise\n" +
            "2026-04-22 15:56:29.941 TRACE PATTERN +foo\n" +
            "2026-04-22 15:56:29.942 WARN something odd\n" +
            "2026-04-22 15:56:29.943 ERROR FATAL kaboom\n";

        var filtered = VerboseLogFilter.FilterForDisplay(raw);

        Assert.Contains("INFO RUN_BACKUP started", filtered);
        Assert.Contains("WARN something odd", filtered);
        Assert.Contains("ERROR FATAL kaboom", filtered);
        Assert.DoesNotContain("DEBUG NOISE", filtered);
        Assert.DoesNotContain("TRACE PATTERN", filtered);
    }

    [Fact]
    public void FilterForDisplay_VerboseOn_PassesEverythingThrough()
    {
        ResetConfig();
        SetVerbose(true);

        var raw =
            "2026-04-22 15:56:29.939 INFO RUN_BACKUP started\n" +
            "2026-04-22 15:56:29.940 DEBUG NOISE detail noise noise\n" +
            "2026-04-22 15:56:29.941 TRACE PATTERN +foo\n";

        var filtered = VerboseLogFilter.FilterForDisplay(raw);

        // When verbose is on the filter is a pass-through (returns
        // the same instance). Use Equal for content; reference
        // equality is allowed to drift if the impl ever changes to
        // always copy.
        Assert.Equal(raw, filtered);
    }

    [Fact]
    public void FilterForDisplay_PreservesNonTerminatedTrailingLine()
    {
        // A live-tail flush often hands us a buffer whose last line
        // hasn't received its '\n' yet (the next chunk will append
        // it). Dropping it would lose data; appending a stray '\n'
        // would smash the next chunk's first line into it.
        ResetConfig();
        SetVerbose(false);

        var raw =
            "2026-04-22 15:56:29.939 INFO RUN_BACKUP started\n" +
            "2026-04-22 15:56:29.940 DEBUG drop me\n" +
            "partial trailing line no newline";

        var filtered = VerboseLogFilter.FilterForDisplay(raw);

        Assert.Contains("INFO RUN_BACKUP started", filtered);
        Assert.DoesNotContain("DEBUG drop me", filtered);
        Assert.EndsWith("partial trailing line no newline", filtered);
    }

    [Fact]
    public void FilterForDisplay_PreservesCRLFTerminators()
    {
        // duplicacy.exe emits CRLF on Windows. The filter must not
        // mangle Windows line endings into bare LFs (the persisted
        // file would diff for no functional reason, and clipboard
        // pastes into Notepad would break).
        ResetConfig();
        SetVerbose(false);

        var raw =
            "2026-04-22 15:56:29.939 INFO line a\r\n" +
            "2026-04-22 15:56:29.940 DEBUG drop\r\n" +
            "2026-04-22 15:56:29.941 INFO line b\r\n";

        var filtered = VerboseLogFilter.FilterForDisplay(raw);

        Assert.Contains("INFO line a\r\n", filtered);
        Assert.Contains("INFO line b\r\n", filtered);
        Assert.DoesNotContain("DEBUG drop", filtered);
    }

    [Fact]
    public void FilterForDisplay_EmptyInput_ReturnsEmpty()
    {
        ResetConfig();
        SetVerbose(false);
        Assert.Equal("", VerboseLogFilter.FilterForDisplay(""));
        // Null is contractually allowed since some callers materialize
        // from a possibly-empty StringBuilder.
        Assert.Equal("", VerboseLogFilter.FilterForDisplay(null!));
    }

    [Fact]
    public void IsVerboseOn_TracksLoggingMinimumLevel()
    {
        ResetConfig();
        SetVerbose(false);
        Assert.False(VerboseLogFilter.IsVerboseOn());

        SetVerbose(true);
        Assert.True(VerboseLogFilter.IsVerboseOn());

        // "Debug" is also accepted as verbose-on — Serilog's Debug
        // minimum level emits both DEBUG and TRACE. The user-facing
        // toggle writes "Verbose" but legacy configs and CLI flags
        // can produce "Debug".
        ServiceLocator.Config.Update(cfg => cfg.Logging.MinimumLevel = "Debug");
        Assert.True(VerboseLogFilter.IsVerboseOn());
    }

    private static void ResetConfig()
    {
        if (!ServiceLocator.Initialized) ServiceLocator.InitializeCore();
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            cfg.Destinations.Clear();
        });
    }

    private static void SetVerbose(bool on) =>
        ServiceLocator.Config.Update(cfg =>
            cfg.Logging.MinimumLevel = on ? "Verbose" : "Information");
}
