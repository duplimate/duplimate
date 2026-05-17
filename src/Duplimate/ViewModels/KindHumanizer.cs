using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Duplimate.Models;

namespace Duplimate.ViewModels;

public static class KindHumanizer
{
    public static readonly IValueConverter To = new Converter();

    private sealed class Converter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is DestinationKind k ? Humanize(k) : value;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Avalonia.Data.BindingOperations.DoNothing;
    }

    public static string Humanize(DestinationKind k) => k switch
    {
        DestinationKind.LocalFolder       => "Local folder",
        DestinationKind.ExternalDrive     => "External drive",
        DestinationKind.NetworkShare      => "Network share (SMB)",
        DestinationKind.DropboxAppScoped  => "Dropbox — app-scoped",
        DestinationKind.DropboxFullAccess => "Dropbox — full access (v1.1)",
        DestinationKind.OneDrivePersonal  => "OneDrive",
        DestinationKind.OneDriveBusiness  => "OneDrive for Business",
        DestinationKind.GoogleDrive       => "Google Drive",
        DestinationKind.S3Compatible      => "S3 / R2 / Wasabi / B2 / MinIO",
        _ => k.ToString(),
    };
}
