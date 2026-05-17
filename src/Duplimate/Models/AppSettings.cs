using System;
using System.Collections.Generic;

namespace Duplimate.Models;

/// <summary>
/// Persisted to config.json. Holds all backups, destinations, and app-level settings.
/// Secrets (API keys, OAuth tokens, SMTP passwords) live in a separate DPAPI-encrypted
/// file referenced by *Ref suffixes.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Bumped when the schema changes; ConfigStore migrates on load.</summary>
    public int SchemaVersion { get; set; } = 1;

    public List<Destination> Destinations { get; set; } = new();

    public List<Backup> Backups { get; set; } = new();

    public AppPreferences Preferences { get; set; } = new();

    public MonitoringSettings Monitoring { get; set; } = new();

    public NotificationSettings Notifications { get; set; } = new();

    public MailSettings Mail { get; set; } = new();

    public RestoreSettings Restore { get; set; } = new();

    public LoggingSettings Logging { get; set; } = new();

    /// <summary>
    /// Optional override for the duplicacy.exe binary used at runtime. When
    /// set and the file exists, the runner uses this path instead of the
    /// embedded copy. Lets users:
    ///   • upgrade to a newer Duplicacy CLI without waiting for an
    ///     Duplimate release,
    ///   • run a custom Duplicacy fork,
    ///   • use a separately-licensed Duplicacy commercial build.
    /// Empty/null means "use the embedded copy". Validated at use-time —
    /// if the path goes stale (file deleted), the runner falls back to
    /// the embedded path and emits a warning.
    /// </summary>
    public string? DuplicacyBinaryPathOverride { get; set; }
}

public sealed class LoggingSettings
{
    /// <summary>How many days of rolling log files to retain. Default 30.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// Minimum level written to the file sink. One of:
    /// Verbose, Debug, Information, Warning, Error, Fatal.
    /// Default Information in release; Verbose is forced on when the app
    /// is launched with DUPLIMATE_DEBUG=1 (see launch-debug.bat).
    /// </summary>
    public string MinimumLevel { get; set; } = "Information";
}

public sealed class RestoreSettings
{
    /// <summary>
    /// Default parallelism for bulk restore and per-file retry pool. 4 is a
    /// reasonable balance for cloud destinations (which bottleneck on auth)
    /// and local ones (which can handle much more).
    /// </summary>
    public int DefaultThreads { get; set; } = 4;

    /// <summary>
    /// Retry delays applied in order to a failed file. Exhausting this list
    /// marks the file as permanently failed for this pass (the user can
    /// trigger "retry all failed" from the summary screen, which runs the
    /// full cascade again).
    /// </summary>
    public List<int> RetryDelaysSeconds { get; set; } = new() { 1, 5, 10, 30, 60 };

    /// <summary>Default for the "preserve directory structure" option in the restore dialog.</summary>
    public bool PreserveStructureByDefault { get; set; } = true;

    /// <summary>Default for the "overwrite existing files" option in the restore dialog.</summary>
    public bool OverwriteByDefault { get; set; } = true;
}

public sealed class AppPreferences
{
    public AppTheme Theme { get; set; } = AppTheme.Light;

    public AccentColor Accent { get; set; } = AccentColor.Ocean;

    /// <summary>If false, app starts minimized to the tray on launch.</summary>
    public bool OpenToMainWindow { get; set; } = true;

    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>Where the app persists its own data. Default: %APPDATA%\Duplimate\.</summary>
    public string? DataRootOverride { get; set; }
}

public enum AppTheme
{
    Dark,
    Light,
    System,
}

public enum AccentColor
{
    /// <summary>Default — deep saturated blue; reads as trustworthy and calm.</summary>
    Ocean,
    /// <summary>Calm, natural, green.</summary>
    Emerald,
    /// <summary>Rich, creative, purple.</summary>
    Plum,
    /// <summary>Neutral, quiet, near-black.</summary>
    Graphite,
    /// <summary>Warm, considered, earthy.</summary>
    Terracotta,
}

public sealed class MonitoringSettings
{
    // ---- Healthchecks.io ----

    public bool HealthchecksEnabled { get; set; }

    /// <summary>User's personal Healthchecks.io API key. Stored in secrets file.</summary>
    public string? HealthchecksApiKeyRef { get; set; }

    /// <summary>Self-hosted instance URL, or null for the hosted service.</summary>
    public string HealthchecksBaseUrl { get; set; } = "https://healthchecks.io";

    /// <summary>Project ID or "" for personal project.</summary>
    public string HealthchecksProject { get; set; } = "";

    // ---- local watchdog ----

    /// <summary>
    /// Parse stdout of each run for these substrings (case-insensitive) and raise
    /// a warning immediately. Matches "ERROR", "Failed to", "access denied" by default.
    /// </summary>
    public List<string> LocalErrorPatterns { get; set; } = new()
    {
        "ERROR",
        "Failed to",
        "access is denied",
        "access denied",
    };

    /// <summary>
    /// If the last successful run is older than this, the dashboard nags the user
    /// even if no scheduled run has officially failed. Belt and braces.
    /// </summary>
    public TimeSpan StaleBackupThreshold { get; set; } = TimeSpan.FromDays(3);
}

public sealed class NotificationSettings
{
    public bool ToastsEnabled { get; set; } = true;

    /// <summary>
    /// On failure, raise a topmost alert window (+ sound) — bypasses Focus Assist / DND.
    /// Default on for failures: the whole point of a backup is to know when it didn't work.
    /// </summary>
    public bool BypassDndOnFailure { get; set; } = true;

    public bool BypassDndOnSuccess { get; set; } = false;

    public bool PlaySound { get; set; } = true;
}

public enum MailProvider
{
    Disabled,
    Resend,
    Mailgun,
}

/// <summary>
/// Mailgun has separate API hosts for US and EU customers. Picking the
/// wrong one returns "domain not found" — so we surface this in the UI
/// rather than silently defaulting US-only.
/// </summary>
public enum MailgunRegion
{
    US,
    EU,
}

public sealed class MailSettings
{
    /// <summary>Hard cap on simultaneous recipients. The UI enforces
    /// this, the sender enforces this, and the Mailgun-sandbox case
    /// already caps at 5 anyway. Five is also Resend's batched-send
    /// best-practice for transactional notifications — beyond that
    /// it's a mailing list, not a backup notification.</summary>
    public const int MaxRecipients = 5;

    public MailProvider Provider { get; set; } = MailProvider.Disabled;

    public string FromAddress { get; set; } = "";

    /// <summary>
    /// Legacy single-address field, kept on disk for migration from
    /// older configs. New code reads / writes <see cref="ToAddresses"/>;
    /// on load, a non-empty <c>ToAddress</c> is appended to
    /// <c>ToAddresses</c> if the list is empty (one-shot migration).
    /// </summary>
    public string ToAddress { get; set; } = "";

    /// <summary>
    /// Up to <see cref="MaxRecipients"/> destination addresses. The UI
    /// renders these as removable pills above the entry input.
    /// </summary>
    public List<string> ToAddresses { get; set; } = new();

    public bool OnSuccess { get; set; } = false;
    public bool OnFailure { get; set; } = true;
    public bool OnSkip { get; set; } = false;

    // ---- Resend (API key only) ----

    /// <summary>Stored in secrets file.</summary>
    public string? ResendApiKeyRef { get; set; }

    // ---- Mailgun (HTTP API, NOT SMTP) ----
    //
    // Mailgun's HTTP API is the modern path: single API key, no SMTP
    // ports / credentials / TLS dance. .NET's System.Net.Mail.SmtpClient
    // is also obsolete per Microsoft. Sending is a POST to
    //   https://api.mailgun.net/v3/{domain}/messages    (US)
    //   https://api.eu.mailgun.net/v3/{domain}/messages (EU)
    // with HTTP Basic auth (username "api", password = API key).

    /// <summary>
    /// The verified sending domain on the Mailgun account, e.g.
    /// <c>mg.example.com</c>. Required: Mailgun's API URL embeds this.
    /// </summary>
    public string MailgunDomain { get; set; } = "";

    /// <summary>US (default) or EU. Picks the API host.</summary>
    public MailgunRegion MailgunRegion { get; set; } = MailgunRegion.US;

    /// <summary>Stored in secrets file.</summary>
    public string? MailgunApiKeyRef { get; set; }
}
