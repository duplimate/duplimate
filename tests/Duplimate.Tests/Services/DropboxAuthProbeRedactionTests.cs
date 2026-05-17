using Duplimate.Services;
using Xunit;

namespace Duplimate.Tests.Services;

/// <summary>
/// Guards the Dropbox OAuth refresh-token redaction. If duplicacy.exe
/// echoes the token in an error message, neither the user-visible probe
/// status nor the structured log line should ever contain it verbatim.
/// </summary>
public class DropboxAuthProbeRedactionTests
{
    [Fact]
    public void Redact_replacesExactSecret()
    {
        const string secret = "sl.AbcdEfghIjklMnopQrstUvwxyz1234567890";
        var input = $"Authentication failed for token={secret} (HTTP 401)";
        var redacted = DropboxAuthProbe.Redact(input, secret);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void Redact_handlesTokenShapesEvenWithoutExactSecret()
    {
        // Sometimes duplicacy emits a derivative or differently-cased
        // chunk of the token. The pattern fallback should still scrub it.
        var input = "Bearer ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789xyz refused";
        var redacted = DropboxAuthProbe.Redact(input, secret: "");
        Assert.Contains("[REDACTED]", redacted);
        Assert.DoesNotContain("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789xyz", redacted);
    }

    [Fact]
    public void Redact_scrubsSlPrefixedDropboxRefreshTokens()
    {
        var input = "init failed: token sl.X9Y8Z7W6V5U4T3S2R1Q0P9N8M7L6K5 invalid";
        var redacted = DropboxAuthProbe.Redact(input, secret: "");
        Assert.DoesNotContain("sl.X9Y8Z7W6V5U4T3S2R1Q0P9N8M7L6K5", redacted);
    }

    [Fact]
    public void Redact_leavesSafeTextAlone()
    {
        var input = "init failed: 401 Unauthorized";
        var redacted = DropboxAuthProbe.Redact(input, secret: "any-secret-123456789");
        Assert.Equal(input, redacted);
    }

    [Fact]
    public void Redact_withShortSecretIgnoresSecretArg()
    {
        // < 8 chars is too short to safely string-replace (would clobber
        // legitimate substrings). Pattern fallback still applies.
        var input = "harmless line with the word abc in it";
        var redacted = DropboxAuthProbe.Redact(input, secret: "abc");
        Assert.Equal(input, redacted);
    }
}
