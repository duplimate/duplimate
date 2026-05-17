namespace Duplimate.Models;

/// <summary>
/// Per-kind defaults we want the whole app to agree on. Runner, orchestrator
/// and the UI all go through here so "what's a good default for N threads
/// on a NAS" has exactly one answer.
/// </summary>
public static class DestinationKindExtensions
{
    /// <summary>
    /// How many Duplicacy upload threads to use when the user hasn't
    /// chosen a value.
    ///
    /// Rationale:
    ///   • Local SSD is already saturated at 1 thread — more just adds CPU
    ///     contention without any throughput win.
    ///   • Local HDD actively suffers from multi-thread writes (head-seek
    ///     thrash), so 1 is safer.
    ///   • SMB / NAS: single-stream writes play much nicer with consumer
    ///     SMB implementations; 1 avoids rate-limit / lock-contention
    ///     edge cases.
    ///   • Cloud / S3: 4 matches Duplicacy's own documented recommendation;
    ///     round-trip latency is the bottleneck so parallel upload lanes
    ///     are a big win.
    /// </summary>
    public static int RecommendedThreads(this DestinationKind kind) => kind switch
    {
        DestinationKind.LocalFolder       => 1,
        DestinationKind.ExternalDrive     => 1,
        DestinationKind.NetworkShare      => 1,
        DestinationKind.DropboxAppScoped  => 4,
        DestinationKind.DropboxFullAccess => 4,
        DestinationKind.OneDrivePersonal  => 4,
        DestinationKind.OneDriveBusiness  => 4,
        DestinationKind.GoogleDrive       => 4,
        DestinationKind.S3Compatible      => 4,
        _                                 => 1,
    };

    /// <summary>True for Dropbox / OneDrive / Google Drive.</summary>
    public static bool IsCloud(this DestinationKind kind) => kind switch
    {
        DestinationKind.DropboxAppScoped  => true,
        DestinationKind.DropboxFullAccess => true,
        DestinationKind.OneDrivePersonal  => true,
        DestinationKind.OneDriveBusiness  => true,
        DestinationKind.GoogleDrive       => true,
        _                                 => false,
    };
}
