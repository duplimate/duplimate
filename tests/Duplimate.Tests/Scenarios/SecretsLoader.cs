using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// Loads secrets that scenario tests need to talk to live services
/// (Dropbox, OneDrive, S3, Healthchecks, mail providers, etc.).
///
/// All credentials live in ONE file:
/// <c>tests/Duplimate.Tests/secrets.env</c> (gitignored — sits
/// directly beside the test .csproj, no subfolder). Format is
/// dotenv-style: one <c>KEY=value</c> per line, lines starting
/// with <c>#</c> are comments. Empty values are treated the same
/// as missing — the scenario marks itself Skipped with a reason,
/// and the suite-level SUMMARY.md prints exactly which keys are
/// still empty so the user knows what to fill in next.
///
/// On first call <see cref="EnsureFileExists"/> writes a fully-
/// commented template at the expected path so the user never has
/// to look up which keys exist.
/// </summary>
public sealed class SecretsLoader
{
    private static readonly Lazy<string> _secretsFileLazy =
        new(ResolveSecretsFile, isThreadSafe: true);

    private static readonly Lazy<Dictionary<string, string>> _values =
        new(LoadValues, isThreadSafe: true);

    private readonly ConcurrentDictionary<string, string> _missing = new();

    public string SecretsFile => _secretsFileLazy.Value;

    /// <summary>Snapshot of every key that was looked up but resolved
    /// to empty/absent on disk. Value is the absolute path the user
    /// edits to provide a value (always the same secrets.env). Drives
    /// the "Missing secrets" table in SUMMARY.md.</summary>
    public IReadOnlyDictionary<string, string> MissingSecretsByName => _missing;

    /// <summary>Try to read a named secret. Returns false (and records
    /// the miss) if the key is absent, blank, or the file doesn't
    /// exist yet.</summary>
    public bool TryGet(string key, out string value)
    {
        var dict = _values.Value;
        if (dict.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
        {
            value = v;
            return true;
        }
        _missing[key] = SecretsFile;
        value = "";
        return false;
    }

    public bool TryGetDropboxToken(out string token)        => TryGet("DROPBOX_TOKEN", out token);
    public bool TryGetOneDriveToken(out string token)       => TryGet("ONEDRIVE_TOKEN", out token);
    public bool TryGetGoogleDriveToken(out string token)    => TryGet("GDRIVE_TOKEN", out token);
    public bool TryGetHealthchecksApiKey(out string key)    => TryGet("HEALTHCHECKS_API_KEY", out key);
    public bool TryGetResendApiKey(out string key)          => TryGet("RESEND_API_KEY", out key);

    public bool TryGetS3Credentials(out string accessKey, out string secretKey, out string endpoint, out string bucket)
    {
        // Run all four lookups unconditionally so MissingSecretsByName
        // records every absent key (instead of short-circuiting on
        // the first failure) — the user gets one comprehensive list.
        var a = TryGet("S3_ACCESS_KEY", out accessKey);
        var s = TryGet("S3_SECRET_KEY", out secretKey);
        var e = TryGet("S3_ENDPOINT",   out endpoint);
        var b = TryGet("S3_BUCKET",     out bucket);
        return a && s && e && b;
    }

    public bool TryGetMailgun(out string apiKey, out string domain)
    {
        var k = TryGet("MAILGUN_API_KEY", out apiKey);
        var d = TryGet("MAILGUN_DOMAIN",  out domain);
        return k && d;
    }

    // ---- internals ----

    private static Dictionary<string, string> LoadValues()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = _secretsFileLazy.Value;
        EnsureFileExists(path);
        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Trim();
                // Strip surrounding quotes — common dotenv convention,
                // user-friendly when a token has special chars.
                if (val.Length >= 2 && (val[0] == '"' && val[^1] == '"'
                                     || val[0] == '\'' && val[^1] == '\''))
                    val = val[1..^1];
                dict[key] = val;
            }
        }
        catch
        {
            // Surface the read failure as "all keys missing" rather
            // than throwing — every scenario will skip with the
            // same recorded path so the user sees "fix this file".
        }
        return dict;
    }

    private static string ResolveSecretsFile()
    {
        // Walk up from the test assembly until we find the test
        // project root (the .csproj sibling); secrets.env sits
        // directly beside it, no subfolder.
        var asmDir = Path.GetDirectoryName(typeof(SecretsLoader).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Duplimate.Tests.csproj")))
            dir = dir.Parent;
        var root = dir?.FullName ?? asmDir;
        return Path.Combine(root, "secrets.env");
    }

    /// <summary>
    /// Materialise <c>secrets.env</c> with the full template the
    /// first time the loader runs in a fresh checkout. Already-
    /// present files are left alone so a user's edits survive across
    /// runs.
    /// </summary>
    public static void EnsureFileExists(string path)
    {
        if (File.Exists(path)) return;

        var content = """
# Duplimate E2E scenario secrets
# ---------------------------------
# One credential per line. Lines starting with `#` are comments.
# Empty values are treated as "not supplied yet" — the matching
# scenarios will skip with a recorded reason. The suite-level
# SUMMARY.md prints which keys remain empty after each run, so
# you know exactly which to fill in to unlock more coverage.
#
# This file is gitignored. Tokens here never leave the test
# process; the SecretsStore round-trips them through DPAPI when
# they end up persisted via a scenario that runs duplicacy.

# ----- Dropbox (App-scoped or full-access) -----
# Take the long-lived OAuth token out of `~/.duplicacy/preferences`
# under your existing local install, or generate a new one via
# https://duplicacy.com/dropbox_start.
DROPBOX_TOKEN=
# Optional refresh token for long-running scheduled scenarios.
DROPBOX_REFRESH_TOKEN=

# ----- OneDrive (v1.1) -----
ONEDRIVE_TOKEN=

# ----- Google Drive (v1.1) -----
GDRIVE_TOKEN=

# ----- S3-compatible (R2, Wasabi, B2-via-S3, MinIO…) -----
S3_ACCESS_KEY=
S3_SECRET_KEY=
S3_ENDPOINT=
S3_BUCKET=

# ----- Healthchecks.io -----
# Management API key (NOT a project ping URL — the scenarios that
# need this exercise the slug-list / create-check flow).
HEALTHCHECKS_API_KEY=

# ----- Mail provider (one of) -----
RESEND_API_KEY=
MAILGUN_API_KEY=
MAILGUN_DOMAIN=
""";
        File.WriteAllText(path, content);
    }
}
