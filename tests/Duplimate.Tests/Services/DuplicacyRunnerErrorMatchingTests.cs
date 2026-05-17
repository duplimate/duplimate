using Duplimate.Services;
using Xunit;

namespace Duplimate.Tests.Services;

/// <summary>
/// Pins which lines DuplicacyRunner.ParseLine considers eligible for
/// substring-matching against MonitoringSettings.LocalErrorPatterns
/// (default: "ERROR", "Failed to", "access denied", "access is denied").
///
/// The user reported a clean Success run being classified as Warning:
/// "backup ended with warning, but I couldn't find anything wrong in
/// the logs that'd justify a warning". Root cause was the bare-substring
/// match flagging INFO lines that happened to contain "ERROR" or
/// "Failed to" inside their content (e.g. an "INFO RUN_INFO Failed to
/// acquire lock, retrying" benign retry message). Fix: anchor the
/// match to either the structured duplicacy log header at WARN/ERROR/
/// FATAL level, or to lines without a duplicacy header at all (init
/// banners, plain stderr text). INFO/DEBUG/TRACE lines pass through
/// untouched.
/// </summary>
public class DuplicacyRunnerErrorMatchingTests
{
    [Theory]
    // INFO lines with the substring "ERROR" or "Failed to" in their
    // content are NOT actionable — the bare-substring matcher used
    // to false-positive on these.
    [InlineData("01:46:48 2026-05-02 01:46:48.897 INFO RUN_INFO Failed to acquire lock, retrying", false)]
    [InlineData("01:46:48 2026-05-02 01:46:48.897 INFO RUN_INFO ERROR_RECOVERY restored", false)]
    [InlineData("01:46:48 2026-05-02 01:46:48.897 INFO REPOSITORY_SET Repository set to C:\\My ERROR Folder", false)]
    // DEBUG / TRACE lines are also exempt — typically diagnostic
    // chatter, often verbose enough to embed unrelated keywords.
    [InlineData("01:46:48 2026-05-02 01:46:48.897 DEBUG NOISE failed to lookup chunk meta", false)]
    [InlineData("01:46:48 2026-05-02 01:46:48.897 TRACE PATTERN ERROR-prefixed source dir", false)]
    public void InfoLevelLines_AreNotActionable(string line, bool expected)
    {
        Assert.Equal(expected, DuplicacyRunner.LineIsActionableForErrorMatching(line));
    }

    [Theory]
    // Stderr-prefixed lines (we ourselves prepend "[err] " from
    // process.ErrorDataReceived) are always actionable — they're
    // genuine OS-level error output.
    [InlineData("01:46:48 [err] access is denied: D:\\backups\\foo")]
    [InlineData("01:46:48 [err] something broke")]
    // duplicacy WARN/ERROR/FATAL header lines are actionable.
    [InlineData("01:46:48 2026-05-02 01:46:48.897 WARN SOMETHING odd")]
    [InlineData("01:46:48 2026-05-02 01:46:48.897 ERROR FATAL kaboom")]
    [InlineData("01:46:48 2026-05-02 01:46:48.897 FATAL panicked")]
    // Plain-text lines without a duplicacy header (e.g. our own
    // StampNow banners, init output, legacy lines) fall back to
    // substring matching since we can't classify their level.
    [InlineData("01:46:48 === multi-2-d4krr4 (C:\\Jts) → Dropbox === ")]
    [InlineData("01:46:48 --- init (ensure preferences exist) ---")]
    public void ActionableLines_AllowSubstringMatching(string line)
    {
        Assert.True(DuplicacyRunner.LineIsActionableForErrorMatching(line));
    }

    [Fact]
    public void NoStampNowPrefix_IsHandledLikeStamped()
    {
        // Lines emitted from contexts that bypass the StampNow wrapper
        // (e.g. an internal callback that hands a raw duplicacy line
        // to LineWritten) should still be classified correctly. The
        // helper strips the "HH:MM:SS " prefix only when it matches —
        // if it's missing, the rest of the matcher runs against the
        // raw input.
        Assert.False(DuplicacyRunner.LineIsActionableForErrorMatching(
            "2026-05-02 01:46:48.897 INFO BACKUP_END completed"));
        Assert.True(DuplicacyRunner.LineIsActionableForErrorMatching(
            "2026-05-02 01:46:48.897 ERROR FATAL kaboom"));
        Assert.True(DuplicacyRunner.LineIsActionableForErrorMatching(
            "[err] raw stderr leak"));
    }
}
