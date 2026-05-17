using System.Collections.Generic;
using System.IO;
using Duplimate.Models;
using Duplimate.Services;
using Xunit;

namespace Duplimate.Tests.Services;

public class DestinationProbeTests
{
    [Fact]
    public void ExtractSnapshotIds_parsesDuplicacyListOutput()
    {
        // Sample shape duplicacy emits from `list`. Noise lines in between
        // should be ignored; ids should de-duplicate case-insensitively.
        var output = string.Join("\n", new[]
        {
            "Storage set to dropbox://test",
            "Snapshot documents-ab12cd at revision 1 created at 2026-04-01",
            "Snapshot documents-ab12cd at revision 2 created at 2026-04-15",
            "Snapshot PHOTOS-xy34zz at revision 1 created at 2026-04-10",
            "random log line we should ignore",
            "Snapshot multi-3-99aa11 at revision 1 created at 2026-04-20",
        });

        var ids = DestinationProbe.ExtractSnapshotIds(output);

        Assert.Contains("documents-ab12cd", ids);
        Assert.Contains("photos-xy34zz", ids); // case-insensitive
        Assert.Contains("multi-3-99aa11", ids);
        Assert.Equal(3, ids.Count);
    }

    [Fact]
    public void ExtractSnapshotIds_emptyOutput_returnsEmpty()
    {
        var ids = DestinationProbe.ExtractSnapshotIds("");
        Assert.Empty(ids);
    }

    [Fact]
    public void ExtractSnapshotIds_ignoresMalformedLines()
    {
        var output = "Snapshot at revision X\nSnapshot notarealid\n";
        var ids = DestinationProbe.ExtractSnapshotIds(output);
        Assert.Empty(ids);
    }
}
