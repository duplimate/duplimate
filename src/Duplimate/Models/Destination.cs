using System;

namespace Duplimate.Models;

/// <summary>
/// A storage target. Translated into a Duplicacy storage URL at run time.
/// Auth tokens are owned by Duplicacy's keyring — we just orchestrate the flow.
/// </summary>
public sealed class Destination
{
    /// <summary>Stable internal id (guid) so renames don't break references.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>User-facing name, e.g. "My Passport" or "Dropbox (work)".</summary>
    public string Name { get; set; } = "";

    public DestinationKind Kind { get; set; }

    /// <summary>
    /// For local / external / network: the folder path.
    /// For cloud: the sub-path within the cloud account (e.g. "local_c").
    /// </summary>
    public string PathOrSubpath { get; set; } = "";

    // ---- volume matching (local + external) ----

    /// <summary>Expected drive letter, e.g. "M:". Null = don't check.</summary>
    public string? ExpectedDriveLetter { get; set; }

    /// <summary>Expected volume label, e.g. "My Passport". Null = don't check.</summary>
    public string? ExpectedVolumeLabel { get; set; }

    // ---- network share credentials (optional) ----

    public string? NetworkUsername { get; set; }

    /// <summary>DPAPI-encrypted if present. Stored only in secrets file, not config.json.</summary>
    public string? NetworkPasswordRef { get; set; }

    // ---- S3 / S3-compatible ----

    public string? S3Endpoint { get; set; }     // e.g. s3.us-east-1.amazonaws.com, <account>.r2.cloudflarestorage.com
    public string? S3Region { get; set; }       // e.g. us-east-1, auto
    public string? S3Bucket { get; set; }
    public string? S3AccessKeyRef { get; set; } // ref into secrets file
    public string? S3SecretKeyRef { get; set; } // ref into secrets file

    // ---- cloud OAuth state (dropbox/onedrive/gdrive) ----

    /// <summary>
    /// Token obtained from duplicacy.com OAuth helper. Opaque to us — passed
    /// to duplicacy via -key during init. Null once duplicacy stores it in its keyring.
    /// </summary>
    public string? OAuthTokenRef { get; set; }

    /// <summary>
    /// When the OAuth token was last validated (we probe with a list call).
    /// Helps the UI surface a "re-authenticate" prompt proactively.
    /// </summary>
    public DateTime? LastTokenValidatedUtc { get; set; }

    // ---- encryption ----

    /// <summary>
    /// Duplicacy storage password (for -encrypt). Stored in secrets file, not here.
    /// Generated if the user leaves it blank; we show it once for them to back up.
    /// </summary>
    public string? StoragePasswordRef { get; set; }

    /// <summary>
    /// Default OFF — most users back up to local drives where the device
    /// is already in their physical possession, and a forgotten password
    /// is the easiest way to lose access to their backups. Cloud
    /// destinations get a "Recommended" badge in the editor so privacy-
    /// conscious users still get nudged to enable it there.
    /// </summary>
    public bool Encrypted { get; set; } = false;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedUtc { get; set; }

    /// <summary>
    /// Build the Duplicacy storage URL for this destination.
    /// Example: "dropbox://local_c" or "M:\Backup\Duplicacy\local_dropbox".
    /// </summary>
    public string BuildStorageUrl()
    {
        return Kind switch
        {
            DestinationKind.LocalFolder      => PathOrSubpath,
            DestinationKind.ExternalDrive    => PathOrSubpath,
            DestinationKind.NetworkShare     => PathOrSubpath,
            DestinationKind.DropboxAppScoped => $"dropbox://{PathOrSubpath.TrimStart('/')}",
            DestinationKind.DropboxFullAccess=> $"dropbox://{PathOrSubpath.TrimStart('/')}",
            DestinationKind.OneDrivePersonal => $"one://{PathOrSubpath.TrimStart('/')}",
            DestinationKind.OneDriveBusiness => $"odb://{PathOrSubpath.TrimStart('/')}",
            DestinationKind.GoogleDrive      => $"gcd://{PathOrSubpath.TrimStart('/')}",
            DestinationKind.S3Compatible     => $"s3://{S3Region}@{S3Endpoint}/{S3Bucket}/{PathOrSubpath.TrimStart('/')}",
            _ => throw new InvalidOperationException($"Unknown DestinationKind: {Kind}")
        };
    }

    public bool NeedsOAuth => Kind is DestinationKind.DropboxAppScoped
        or DestinationKind.DropboxFullAccess
        or DestinationKind.OneDrivePersonal
        or DestinationKind.OneDriveBusiness
        or DestinationKind.GoogleDrive;

    public bool IsLocalLike => Kind is DestinationKind.LocalFolder
        or DestinationKind.ExternalDrive
        or DestinationKind.NetworkShare;
}
