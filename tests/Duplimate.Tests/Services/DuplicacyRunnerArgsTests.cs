using System.Collections.Generic;
using System.Linq;
using Duplimate.Services;
using Xunit;

namespace Duplimate.Tests.Services;

public class DuplicacyRunnerArgsTests
{
    // Free-form extras like '-chunk-size 4194304 -hash' need to round-trip
    // through the editor unchanged. Quoted segments must survive intact
    // so paths with spaces don't get split mid-token.
    [Fact]
    public void SplitArgString_handlesPlainTokens()
    {
        var tokens = DuplicacyRunner.SplitArgString("-chunk-size 4194304 -hash").ToList();
        Assert.Equal(new[] { "-chunk-size", "4194304", "-hash" }, tokens);
    }

    [Fact]
    public void SplitArgString_preservesDoubleQuotedTokens()
    {
        var tokens = DuplicacyRunner.SplitArgString("-comment \"my backup, with spaces\" -hash").ToList();
        Assert.Equal(new[] { "-comment", "my backup, with spaces", "-hash" }, tokens);
    }

    [Fact]
    public void SplitArgString_preservesSingleQuotedTokens()
    {
        var tokens = DuplicacyRunner.SplitArgString("--tag 'spaced tag'").ToList();
        Assert.Equal(new[] { "--tag", "spaced tag" }, tokens);
    }

    [Fact]
    public void SplitArgString_collapsesAdjacentWhitespace()
    {
        var tokens = DuplicacyRunner.SplitArgString("  -a   -b  ").ToList();
        Assert.Equal(new[] { "-a", "-b" }, tokens);
    }

    [Fact]
    public void SplitArgString_emptyInput_yieldsNothing()
    {
        Assert.Empty(DuplicacyRunner.SplitArgString(""));
        Assert.Empty(DuplicacyRunner.SplitArgString("   "));
    }
}
