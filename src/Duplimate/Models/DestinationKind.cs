namespace Duplimate.Models;

public enum DestinationKind
{
    LocalFolder,
    ExternalDrive,
    NetworkShare,
    DropboxAppScoped,
    DropboxFullAccess,   // v1.1
    OneDrivePersonal,
    OneDriveBusiness,
    GoogleDrive,
    S3Compatible,        // covers S3, R2, Wasabi, MinIO, B2-via-S3
}
