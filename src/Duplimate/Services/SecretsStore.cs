using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Duplimate.Services.Platform;
using Serilog;

namespace Duplimate.Services;

/// <summary>
/// Tiny secrets store: string-keyed values encrypted via the per-platform
/// <see cref="ISecretsEncryption"/> provider. The whole blob is a JSON
/// dict, serialised, then encrypted, then written to <c>secrets.bin</c>
/// atomically. Keys are treated as opaque refs from Destination
/// / MailSettings / MonitoringSettings.
///
/// <para>
/// Provider per platform:
/// <list type="bullet">
///   <item><b>Windows</b> — DPAPI (CurrentUser scope). Same machine +
///         user can decrypt; copying secrets.bin to another account
///         or another machine fails on Unprotect, exactly the trip-wire
///         this class is designed for.</item>
///   <item><b>macOS / Linux</b> — AES-GCM with a chmod-600 keyfile next
///         to the vault. Same trip-wire applies if the keyfile and
///         ciphertext are separated.</item>
/// </list>
/// </para>
/// </summary>
public sealed class SecretsStore
{
    private static ILogger _log => AppLogger.For<SecretsStore>();
    private readonly object _gate = new();
    private Dictionary<string, string>? _cache;
    private readonly ISecretsEncryption _crypto;

    public SecretsStore() : this(SelectProvider()) { }

    /// <summary>For tests / DI: inject a custom encryption provider
    /// (e.g. an in-memory pass-through).</summary>
    public SecretsStore(ISecretsEncryption crypto) => _crypto = crypto;

    private static ISecretsEncryption SelectProvider()
    {
#if WINDOWS
        if (PlatformInfo.IsWindows) return new Platform.Windows.DpapiSecretsEncryption();
#endif
        // Unix: keyfile lives next to the vault under the config root.
        var keyPath = Path.Combine(AppPaths.ConfigRoot, "secrets-key.bin");
        return new Platform.Unix.FileKeySecretsEncryption(keyPath);
    }

    /// <summary>
    /// Set when an existing <c>secrets.bin</c> couldn't be decrypted on
    /// load — typically because the file (or, on Unix, the keyfile) was
    /// copied without its counterpart. The UI consults this on startup
    /// to warn the user that previously-saved tokens/passwords are
    /// unavailable and that they need to re-enter them. Empty when
    /// load was clean.
    /// </summary>
    public string? LoadErrorMessage { get; private set; }
    /// <summary>Path the corrupt vault was moved to (for forensics /
    /// manual recovery). Null when no failure occurred.</summary>
    public string? PreservedCorruptPath { get; private set; }

    public string Get(string key)
    {
        lock (_gate)
        {
            var map = Load();
            return map.TryGetValue(key, out var v) ? v : "";
        }
    }

    public bool TryGet(string key, out string value)
    {
        lock (_gate)
        {
            var map = Load();
            return map.TryGetValue(key, out value!);
        }
    }

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            // Clone before mutating: Load returns the same reference
            // _cache holds, so a direct map[key] = value would dirty
            // the cached map. If Save() then throws (disk-full, AV
            // quarantine), the cache is invalidated by Save's catch
            // — but ANY OTHER thread that got the cached reference
            // before the invalidation would still see the dirty
            // mutation. Cloning isolates the in-flight write from
            // observers and makes the failure path consistent.
            var loaded = Load();
            var map = new Dictionary<string, string>(loaded);
            var existed = map.ContainsKey(key);
            map[key] = value;
            Save(map);
            // Never log the value — keys are public refs but values are
            // tokens, passwords, OAuth refresh tokens.
            _log.Debug("Secret {Action}: ref={Key} valueLength={Len}",
                existed ? "updated" : "stored", key, value?.Length ?? 0);
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            var loaded = Load();
            if (!loaded.ContainsKey(key)) return;
            // Clone before mutating, same rationale as Set above.
            var map = new Dictionary<string, string>(loaded);
            map.Remove(key);
            Save(map);
            _log.Debug("Secret removed: ref={Key}", key);
        }
    }

    public string PutNew(string prefix, string value)
    {
        var @ref = $"{prefix}:{Guid.NewGuid():N}";
        Set(@ref, value);
        return @ref;
    }

    private Dictionary<string, string> Load()
    {
        if (_cache is not null) return _cache;
        if (!File.Exists(AppPaths.SecretsFile))
            return _cache = new();

        try
        {
            var wrapped = File.ReadAllBytes(AppPaths.SecretsFile);
            var clear = _crypto.Unprotect(wrapped, Entropy);
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(clear) ?? new();
            _log.Debug("Secrets vault opened: {Count} entries", map.Count);
            return _cache = map;
        }
        catch (Exception ex)
        {
            // The encryption key is platform-specific (DPAPI /
            // file-key). Copying secrets.bin to another account/machine
            // — or losing the keyfile on Unix — produces exactly this
            // exception. We CANNOT silently overwrite the file with
            // an empty vault: the next Save() call would clobber the
            // preserved ciphertext, and the user never knew their
            // saved tokens/passwords were dropped. Move the unreadable
            // file aside (preserving it for forensics or manual
            // recovery on the original system) and surface an error
            // message the UI can display. Subsequent Save() calls
            // then start a fresh vault next to the preserved one.
            var preserved = AppPaths.SecretsFile + $".unreadable-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(AppPaths.SecretsFile, preserved);
                PreservedCorruptPath = preserved;
            }
            catch (Exception moveEx)
            {
                // Preservation failed (file locked? permissions?). Log
                // and continue — the worst case is the next Save()
                // overwrites the file. Surfacing the error in the UI
                // still gives the user a heads-up that secrets are
                // gone.
                _log.Warning(moveEx, "Couldn't move unreadable secrets vault aside");
            }
            LoadErrorMessage = BuildLoadErrorMessage(_crypto.DisplayName);
            _log.Error(ex, "Couldn't decrypt secrets vault — starting empty (preserved as {Preserved}; provider={Provider})", preserved, _crypto.DisplayName);
            return _cache = new();
        }
    }

    /// <summary>
    /// Produces the user-facing "saved tokens couldn't be decrypted"
    /// blurb. Wording acknowledges the platform-specific reason
    /// without overwhelming a non-technical reader.
    /// </summary>
    private string BuildLoadErrorMessage(string providerName)
    {
        var why = PlatformInfo.IsWindows
            ? "DPAPI (the Windows secret-protection API) ties encrypted blobs to a specific account+machine pair, " +
              "so a config copied from another user account or another computer won't open here."
            : "The vault is encrypted with a per-user key file kept alongside it. " +
              "If the key file was lost, replaced, or copied without its companion, the saved secrets become unreadable.";
        var preserved = PreservedCorruptPath is null
            ? ""
            : $" The unreadable file was preserved as {Path.GetFileName(PreservedCorruptPath)}.";
        return
            "Saved cloud tokens and storage passwords couldn't be decrypted on this machine. " +
            why +
            " Re-enter the tokens/passwords for each destination via Destinations → Edit." +
            preserved +
            $" (provider: {providerName})";
    }

    private void Save(Dictionary<string, string> map)
    {
        AppPaths.EnsureAll();
        var clear = JsonSerializer.SerializeToUtf8Bytes(map, new JsonSerializerOptions { WriteIndented = false });
        var wrapped = _crypto.Protect(clear, Entropy);
        try
        {
            AtomicWrite(AppPaths.SecretsFile, wrapped);
            // Only swap the in-memory cache if disk write succeeded.
            // Previously _cache was assigned unconditionally — a failed
            // write (disk full, AV quarantine) would leave the cache
            // ahead of disk; the next Load would re-read truth and
            // silently revert the user's just-saved secret.
            _cache = map;
        }
        catch
        {
            // Invalidate the cache so the next read pulls truth from
            // disk rather than serving the in-memory copy that was
            // about to become canonical. Re-throw so callers see the
            // failure (Set / Remove already log + propagate).
            _cache = null;
            throw;
        }
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        var bak = path + ".bak";
        File.WriteAllBytes(tmp, bytes);
        // Pass a backup filename to File.Replace so a power-cut
        // between write and rename leaves a recoverable .bak. Without
        // this the prior secrets file is gone the moment Replace
        // succeeds; with this the user can rename .bak → secrets if
        // their main file is somehow corrupted post-crash.
        if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: bak);
        else File.Move(tmp, path);
    }

    /// <summary>
    /// App-specific entropy makes the ciphertext useless to other apps running
    /// as the same user — not a real secret, just hygiene. Used as the
    /// "additional authenticated data" input on Unix (AES-GCM) and as the
    /// optionalEntropy parameter on Windows (DPAPI).
    /// </summary>
    private static readonly byte[] Entropy =
    {
        0x45, 0x44, 0x42, 0x2D, 0x73, 0x65, 0x63, 0x72, 0x65, 0x74, 0x73, 0x2D, 0x76, 0x31
    };
}
