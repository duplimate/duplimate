using Duplimate.Models;
using Xunit;

namespace Duplimate.Tests.Models;

/// <summary>
/// Backup.Name is a free-form display string (the user can type
/// "Daily — work laptop" if they want); the regex-locked shape was
/// relaxed when filesystem and snapshot-id consumers started doing
/// their own sanitisation downstream (AppPaths.Sanitize for paths,
/// SnapshotIdSeed for the Duplicacy snapshot id). Two contracts
/// remain pinned here:
///   1. IsValidName accepts non-empty trimmed strings up to
///      Backup.MaxNameLength (255) chars and rejects empty / null /
///      over-cap.
///   2. SanitizeName still produces a filesystem-safe ASCII-slug
///      result — it's what locks the SnapshotIdSeed at first save,
///      so its output must round-trip through duplicacy snapshot id
///      rules even if the display Name itself is permissive.
/// </summary>
public class BackupNameSanitizationTests
{
    [Theory]
    [InlineData("simple")]
    [InlineData("with-dashes")]
    [InlineData("with_underscores")]
    [InlineData("MixedCase123")]
    [InlineData("a")] // single char
    [InlineData("with spaces")]
    [InlineData("My Backup (work)")]
    [InlineData("foo.bar")]
    [InlineData("Daily — work laptop")]
    [InlineData("with/slash")]
    [InlineData("with\"quote")]
    [InlineData("with;semicolon")]
    [InlineData("with&ampersand")]
    public void IsValidName_acceptsAnyNonEmptyTrimmedString(string name)
    {
        Assert.True(Backup.IsValidName(name), $"'{name}' should be valid (free-form display name)");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("\t")]
    public void IsValidName_rejectsEmptyOrWhitespaceOnly(string? name)
    {
        Assert.False(Backup.IsValidName(name), $"'{name ?? "<null>"}' should be rejected");
    }

    [Fact]
    public void IsValidName_rejectsOverMaxLength()
    {
        var tooLong = new string('a', Backup.MaxNameLength + 1);
        Assert.False(Backup.IsValidName(tooLong),
            $"name longer than {Backup.MaxNameLength} chars must be rejected");
    }

    [Fact]
    public void IsValidName_acceptsExactlyMaxLength()
    {
        var atCap = new string('a', Backup.MaxNameLength);
        Assert.True(Backup.IsValidName(atCap),
            $"name exactly {Backup.MaxNameLength} chars must be accepted");
    }

    [Theory]
    [InlineData("My Backup (work)", "My_Backup_work")]
    [InlineData("foo.bar",          "foobar")]
    [InlineData("\"quoted\"",       "quoted")]
    [InlineData("trailing  ",       "trailing")]
    [InlineData("with;semis",       "withsemis")]
    [InlineData("",                 "my-backup")] // fallback
    [InlineData(null,               "my-backup")]
    [InlineData("   ",              "my-backup")]
    [InlineData("___",              "my-backup")] // sanitiser trims leading/trailing _ -
    public void SanitizeName_coercesToValidShape(string? raw, string expected)
    {
        var clean = Backup.SanitizeName(raw);
        Assert.Equal(expected, clean);
        // SanitizeName output is used as the SnapshotIdSeed — assert
        // it stays in the canonical [A-Za-z0-9_-]+ shape so the
        // derived Duplicacy snapshot id is always well-formed.
        Assert.Matches("^[A-Za-z0-9_-]+$", clean);
    }

    [Fact]
    public void SanitizeName_isIdempotent_onAlreadyValidNames()
    {
        // Pin idempotency so re-sanitising can't drift the seed —
        // round-tripping the sanitizer must be a no-op for names
        // that are already canonical.
        var canonical = "Already-Valid_123";
        Assert.Equal(canonical, Backup.SanitizeName(canonical));
        Assert.Equal(canonical, Backup.SanitizeName(Backup.SanitizeName(canonical)));
    }
}
