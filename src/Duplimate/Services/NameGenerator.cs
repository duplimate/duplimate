using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Duplimate.Models;

namespace Duplimate.Services;

/// <summary>
/// Generates friendly, collision-safe default names for Backups and
/// Destinations. The goal is that the user never needs to think about
/// naming up front — the suggested name is always sensible and unique
/// within the local config — but can override it in the "Advanced"
/// section if they want a specific label in their cloud storage browser.
///
/// Naming scheme:
///   Backup      : <c>{leaf-or-multi}-{rand6}</c>
///   Destination : <c>{Kind} - {account-or-path}[ - {subfolder}]</c>
///
/// Uniqueness: we check the local config for the same name, and for
/// destinations we also honour a caller-supplied "remote folder exists"
/// probe so we can avoid colliding with a sibling machine writing to the
/// same bucket. On collision the 6-char suffix is regenerated; with a
/// ~2.2B-entry alphabet (36^6) the retry loop is effectively one-shot.
/// </summary>
public static class NameGenerator
{
    private const string SuffixAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const int SuffixLength = 6;

    // =====================================================================
    // Backup names
    // =====================================================================

    /// <summary>
    /// "documents-ab12cd" for single-source, "multi-3-ab12cd" for
    /// multi-source. Caller should pass in the list of existing backup
    /// names so we can ensure uniqueness.
    /// </summary>
    public static string ForBackup(
        System.Collections.Generic.IReadOnlyList<string> sourcePaths,
        Func<string, bool> existsInConfig)
    {
        var label = BackupLabel(sourcePaths);
        return GenerateUnique(label, existsInConfig);
    }

    private static string BackupLabel(System.Collections.Generic.IReadOnlyList<string> sourcePaths)
    {
        if (sourcePaths.Count == 0) return "backup";
        if (sourcePaths.Count == 1)
        {
            var leaf = Path.GetFileName(sourcePaths[0].TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(leaf))
            {
                // Root-of-drive case: "C:\" → "drive-c".
                var drive = sourcePaths[0].TrimEnd('\\', '/').TrimEnd(':');
                return string.IsNullOrWhiteSpace(drive) ? "backup" : $"drive-{drive.ToLowerInvariant()}";
            }
            return SanitizeLabel(leaf);
        }
        return $"multi-{sourcePaths.Count}";
    }

    // =====================================================================
    // Destination names
    // =====================================================================

    /// <summary>
    /// Human-readable default name for a destination. The caller supplies
    /// the identifying bits that are relevant for this Kind — account
    /// email for OAuth, path for local, bucket for S3, etc.
    /// </summary>
    public static string ForDestination(
        DestinationKind kind,
        string? accountOrPath,
        string? subfolder,
        Func<string, bool> existsInConfig)
    {
        var label = DestinationLabel(kind, accountOrPath, subfolder);
        return GenerateUniqueDestination(label, existsInConfig);
    }

    private static string DestinationLabel(DestinationKind kind, string? accountOrPath, string? subfolder)
    {
        var prefix = kind switch
        {
            DestinationKind.LocalFolder       => "Local",
            DestinationKind.ExternalDrive     => "External",
            DestinationKind.NetworkShare      => "Network",
            DestinationKind.DropboxAppScoped  => "Dropbox",
            DestinationKind.DropboxFullAccess => "Dropbox",
            DestinationKind.OneDrivePersonal  => "OneDrive",
            DestinationKind.OneDriveBusiness  => "OneDrive Business",
            DestinationKind.GoogleDrive       => "Google Drive",
            DestinationKind.S3Compatible      => "S3",
            _                                 => kind.ToString(),
        };

        var id = (accountOrPath ?? "").Trim();
        var sub = (subfolder ?? "").Trim();

        // No identifier AND no subfolder → just the kind prefix.
        if (id.Length == 0 && sub.Length == 0) return prefix;

        // No identifier but a subfolder is present → "{Kind} - {subfolder}".
        // This is the cloud-destination shape: callers pass null as the
        // account-or-path because Duplicacy doesn't expose the OAuth
        // account email, but the subfolder (e.g. "duplimate-abcd1234")
        // is still meaningful and uniquely identifies the destination.
        // Earlier this branch fell through to the prefix-only return above,
        // which silently dropped the subfolder slot — and the caller's
        // workaround (passing the subpath as BOTH account-or-path AND
        // subfolder) produced names like "Dropbox - foo - foo".
        if (id.Length == 0) return $"{prefix} - {sub}";

        // Local paths get the tail only so "Local - C:\Backups\Duplicacy"
        // doesn't include the whole path; cloud accounts show the email
        // verbatim; S3 buckets show bucket name.
        id = kind switch
        {
            DestinationKind.LocalFolder or DestinationKind.ExternalDrive
                => TrimPathToTail(id, maxSegments: 2),
            DestinationKind.NetworkShare
                => id, // keep full UNC so "Network - \\server\share" reads right
            _ => id,
        };

        return sub.Length == 0
            ? $"{prefix} - {id}"
            : $"{prefix} - {id} - {sub}";
    }

    private static string TrimPathToTail(string path, int maxSegments)
    {
        var p = path.TrimEnd('\\', '/');
        var segments = p.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= maxSegments) return p;
        return "…\\" + string.Join("\\", segments[^maxSegments..]);
    }

    // =====================================================================
    // Shared generation
    // =====================================================================

    /// <summary>
    /// Adds a 6-char base36 suffix and ensures uniqueness. Bails after 32
    /// tries (impossible in practice — 36^6 ≈ 2.2 billion combinations).
    /// </summary>
    private static string GenerateUnique(string label, Func<string, bool> exists)
    {
        for (int i = 0; i < 32; i++)
        {
            var candidate = $"{label}-{RandomSuffix()}";
            if (!exists(candidate)) return candidate;
        }
        // 32 collisions on a 2.2B-entry keyspace means something is wrong
        // with the caller. Fall back to GUID to at least avoid blocking.
        return $"{label}-{Guid.NewGuid():N}".Substring(0, Math.Min(32, label.Length + 1 + 32));
    }

    /// <summary>
    /// Destination default names don't carry a random suffix by default
    /// (humans prefer reading "Dropbox - you@x.com" over
    /// "Dropbox - you@x.com - ab12cd"), but if a same-name destination
    /// already exists, we disambiguate with a " (2)", " (3)" etc. suffix.
    /// </summary>
    private static string GenerateUniqueDestination(string label, Func<string, bool> exists)
    {
        if (!exists(label)) return label;
        for (int n = 2; n < 1000; n++)
        {
            var candidate = $"{label} ({n})";
            if (!exists(candidate)) return candidate;
        }
        return $"{label} ({Guid.NewGuid():N})"[..48];
    }

    public static string RandomSuffix()
    {
        Span<byte> bytes = stackalloc byte[SuffixLength];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[SuffixLength];
        for (int i = 0; i < SuffixLength; i++)
            chars[i] = SuffixAlphabet[bytes[i] % SuffixAlphabet.Length];
        return new string(chars);
    }

    public static string SanitizeLabel(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "backup";
        // Lowercase, keep letters/digits/hyphen/underscore; replace spaces
        // with hyphens; collapse runs of underscores/hyphens.
        var buf = new System.Text.StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) buf.Append(c);
            else if (c == '-' || c == '_') buf.Append(c);
            else if (char.IsWhiteSpace(c) || c == '.') buf.Append('-');
            // drop everything else
        }
        var result = buf.ToString().Trim('-', '_');
        while (result.Contains("--")) result = result.Replace("--", "-");
        return string.IsNullOrEmpty(result) ? "backup" : result;
    }
}
