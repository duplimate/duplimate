using Duplimate.Models;
using Xunit;

namespace Duplimate.Tests.Models;

/// <summary>
/// Encryption was previously ON by default, which is the canonical
/// trade-off for cloud-first backup tools (Backblaze, Restic, Arq) but
/// wrong for local-first ones (Time Machine, File History, Synology
/// Hyper Backup). Duplimate defaults users into the local-first
/// camp because losing a forgotten password is the most common way for
/// non-technical users to lose access to their own backups; the
/// destination editor still nudges users on cloud / S3 destinations
/// via a "Recommended" badge tied to <c>ShowEncryptionRecommendedBadge</c>.
/// This test pins that default so a future "tighten security" PR
/// doesn't silently flip it back without a UX conversation.
/// </summary>
public class DestinationDefaultsTests
{
    [Fact]
    public void NewDestination_Encrypted_DefaultsToFalse()
    {
        var d = new Destination();
        Assert.False(d.Encrypted, "Default encryption should be OFF; cloud destinations get a Recommended badge in the editor.");
    }
}
