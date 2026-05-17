using System;

namespace Duplimate.Services;

/// <summary>
/// Marker for exceptions that the orchestrator should NOT retry.
/// "Failed to read preferences" doesn't get better with a 60-second
/// nap — it gets better when the user fixes the underlying
/// configuration. Implementing this on a thrown exception causes
/// <see cref="BackupOrchestrator.RunCellWithRetryAsync"/> to bail
/// after the first attempt instead of cycling through the
/// 5s/20s/60s backoff cascade.
/// </summary>
public interface INonRetriable { }

/// <summary>
/// Thrown by <see cref="DuplicacyRunner.EnsureInitializedAsync"/> when
/// `duplicacy init` exits non-zero. Carries the captured stdout so the
/// failure toast can show the real reason (auth failure, unreachable
/// storage, etc.) instead of a generic "Backup failed" line.
/// </summary>
public sealed class DuplicacyInitException : Exception, INonRetriable
{
    public int ExitCode { get; }
    public string Output { get; }
    /// <summary>Path of the per-cell log file we wrote init's stdout
    /// into before throwing. Set by the runner so the orchestrator can
    /// surface this log via CellOutcome.LogPath / RunRecord.LogPath,
    /// making "Selected run" non-empty even when init was the only
    /// thing that ran.</summary>
    public string? LogPath { get; set; }

    public DuplicacyInitException(string message, int exitCode, string output)
        : base(message)
    {
        ExitCode = exitCode;
        Output = output;
    }
}

/// <summary>
/// Specialised <see cref="DuplicacyInitException"/> for the case where
/// Duplicacy refuses init because the destination already contains a
/// (likely encrypted) Duplicacy storage. Detected via the stdout line
/// "The storage is likely to have been initialized with a password
/// before". Carries enough context (storage URL + destination id) for
/// the UI to offer a recovery path: enter the existing password,
/// erase the storage, or pick a different folder.
/// </summary>
public sealed class StoragePreviouslyInitializedException : Exception, INonRetriable
{
    public string DestinationId { get; }
    public string DestinationName { get; }
    public string StorageUrl { get; }
    public int ExitCode { get; }
    public string Output { get; }
    public string? LogPath { get; set; }

    public StoragePreviouslyInitializedException(
        string destinationId, string destinationName, string storageUrl,
        int exitCode, string output)
        : base(BuildMessage(destinationName, storageUrl))
    {
        DestinationId = destinationId;
        DestinationName = destinationName;
        StorageUrl = storageUrl;
        ExitCode = exitCode;
        Output = output;
    }

    private static string BuildMessage(string destinationName, string storageUrl) =>
        $"The destination \"{destinationName}\" already contains a Duplicacy storage that we can't read " +
        $"(likely encrypted with a different password). Open the destination editor and either " +
        $"set the existing storage password, erase the storage to start fresh, or point this " +
        $"destination at a different folder.\n\nStorage: {storageUrl}";
}

/// <summary>
/// Thrown when a Duplicacy stdout line matches a known non-retriable
/// ERROR pattern (PREFERENCE_OPEN, STORAGE_INIT, etc.) — see
/// <see cref="DuplicacyErrorClassifier"/>. Caught by the orchestrator's
/// retry cascade and short-circuits the remaining attempts.
/// </summary>
public sealed class NonRetriableDuplicacyException : Exception, INonRetriable
{
    public string ErrorCode { get; }

    public NonRetriableDuplicacyException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Decides whether a Duplicacy ERROR line means "no point retrying".
/// Centralised so the runner and the orchestrator share one rulebook.
///
/// Rule of thumb: errors that come from local config (preferences /
/// storage URL / encryption mismatch / missing exe) are non-retriable;
/// errors that come from network / remote-state interaction (timeout,
/// rate limit, transient 5xx) are retriable. When in doubt we default
/// to retriable — a wasted retry beats a stuck user.
/// </summary>
public static class DuplicacyErrorClassifier
{
    /// <summary>Known non-retriable Duplicacy error codes. Matched
    /// case-insensitively against the ERROR-line third token.</summary>
    private static readonly System.Collections.Generic.HashSet<string> NonRetriableCodes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // The repo's .duplicacy/preferences file is missing or unreadable.
            // No amount of retrying recreates the file — usually means init
            // failed or the user manually deleted the metadata.
            "PREFERENCE_OPEN",
            "PREFERENCE_INVALID",

            // Storage URL syntax / config issues — local, won't change between attempts.
            "STORAGE_PARAMETERS",
            "STORAGE_CREATE",

            // Wrong encryption password against an already-encrypted storage.
            "CONFIG_DECRYPT_FAILED",
            "CONFIG_DECRYPT",

            // Auth failures from the cloud provider — the token isn't
            // going to start working in 60 seconds.
            "AUTH_FAILURE",
            "BACKUP_AUTHORIZATION",
        };

    /// <summary>
    /// Inspect a single duplicacy stdout line and decide whether it
    /// signals a non-retriable failure. Returns the matched error code
    /// (the third whitespace-separated token after "ERROR") or null.
    /// Format we look for:
    ///   "HH:MM:SS yyyy-MM-dd HH:MM:SS.fff ERROR &lt;CODE&gt; &lt;message...&gt;"
    /// or the simpler "ERROR &lt;CODE&gt; &lt;message&gt;" without the
    /// timestamp prefix.
    /// </summary>
    public static string? ClassifyNonRetriable(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        // Find " ERROR " followed by an UPPERCASE_TOKEN.
        var idx = line.IndexOf(" ERROR ", StringComparison.Ordinal);
        if (idx < 0) return null;
        var rest = line.AsSpan(idx + " ERROR ".Length).Trim();
        // Cut at first whitespace.
        var ws = rest.IndexOfAny(new[] { ' ', '\t' });
        var code = ws > 0 ? rest[..ws].ToString() : rest.ToString();
        if (string.IsNullOrEmpty(code)) return null;
        return NonRetriableCodes.Contains(code) ? code : null;
    }
}
