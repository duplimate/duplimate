using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Serilog;

namespace Duplimate.Services.Platform.Unix;

/// <summary>
/// macOS / Linux secrets encryption — AES-GCM with a 256-bit key
/// stored in a chmod-600 keyfile next to the vault. Equivalent
/// security posture to a private SSH key on the same disk: an
/// offline attacker who only sees <c>secrets.bin</c> finds opaque
/// AES-GCM ciphertext; one who can read your home directory at
/// rest already has trivially worse access (they can read your SSH
/// keys, browser cookies, etc).
///
/// <para>
/// Wire format (per-call): <c>nonce(12) || ciphertext(N) || tag(16)</c>.
/// Per-call random nonce so re-encrypting unchanged content still
/// rotates the ciphertext.
/// </para>
///
/// <para>
/// Roadmap: a follow-up can replace this with native Keychain
/// (macOS) / libsecret (Linux) bindings. The interface stays the
/// same; only the key source changes.
/// </para>
/// </summary>
public sealed class FileKeySecretsEncryption : ISecretsEncryption
{
    private static ILogger _log => AppLogger.For<FileKeySecretsEncryption>();
    private const int KeyLen   = 32;
    private const int NonceLen = 12;
    private const int TagLen   = 16;

    private readonly string _keyPath;

    public FileKeySecretsEncryption(string keyPath) => _keyPath = keyPath;

    public string DisplayName =>
        PlatformInfo.IsMacOS ? "AES-GCM file key (macOS)" :
        PlatformInfo.IsLinux ? "AES-GCM file key (Linux)" :
                               "AES-GCM file key";

    public byte[] Protect(byte[] cleartext, byte[] entropy)
    {
        var key = LoadOrCreateKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var ciphertext = new byte[cleartext.Length];
        var tag = new byte[TagLen];

        using var aes = new AesGcm(key, TagLen);
        aes.Encrypt(nonce, cleartext, ciphertext, tag, entropy);

        // Pack: nonce || ciphertext || tag — single contiguous blob.
        var packed = new byte[NonceLen + ciphertext.Length + TagLen];
        Buffer.BlockCopy(nonce,      0, packed, 0,                          NonceLen);
        Buffer.BlockCopy(ciphertext, 0, packed, NonceLen,                   ciphertext.Length);
        Buffer.BlockCopy(tag,        0, packed, NonceLen + ciphertext.Length, TagLen);
        return packed;
    }

    public byte[] Unprotect(byte[] ciphertext, byte[] entropy)
    {
        if (ciphertext.Length < NonceLen + TagLen)
            throw new CryptographicException("Secrets blob is too short to be a valid AES-GCM ciphertext.");

        var key = LoadOrCreateKey();
        var nonce  = new byte[NonceLen];
        var tag    = new byte[TagLen];
        var ctLen  = ciphertext.Length - NonceLen - TagLen;
        var ct     = new byte[ctLen];
        Buffer.BlockCopy(ciphertext, 0,                    nonce, 0, NonceLen);
        Buffer.BlockCopy(ciphertext, NonceLen,             ct,    0, ctLen);
        Buffer.BlockCopy(ciphertext, NonceLen + ctLen,     tag,   0, TagLen);

        var plain = new byte[ctLen];
        using var aes = new AesGcm(key, TagLen);
        // Throws CryptographicException on tag-mismatch — exactly the
        // trip-wire SecretsStore.Load expects.
        aes.Decrypt(nonce, ct, tag, plain, entropy);
        return plain;
    }

    /// <summary>
    /// Returns the per-user AES key, creating one on first call. The
    /// keyfile is written with chmod 600 so other users on the same
    /// box (and stray backup tools that stumble into the home dir)
    /// can't read it.
    /// </summary>
    private byte[] LoadOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            var existing = File.ReadAllBytes(_keyPath);
            if (existing.Length == KeyLen) return existing;
            // Wrong-length keyfile is treated as corrupt — swap it
            // aside and roll a fresh one. Existing ciphertexts will
            // become unreadable; SecretsStore handles that case (it
            // moves the unreadable vault aside and prompts re-entry).
            var corrupt = _keyPath + $".corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            try { File.Move(_keyPath, corrupt); }
            catch (Exception ex) { _log.Debug(ex, "Couldn't preserve corrupt keyfile aside; will overwrite"); }
            _log.Warning("Secrets keyfile had unexpected length ({Len}); rotated to {Corrupt}", existing.Length, corrupt);
        }

        var dir = Path.GetDirectoryName(_keyPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var key = RandomNumberGenerator.GetBytes(KeyLen);

        // Atomic write + chmod 600. On Unix we use UnixCreateMode so
        // the mode bits are set *as the file is created* — no window
        // where the keyfile is mode 0644 and visible to other users.
        // On Windows the same setter throws PlatformNotSupportedException
        // unconditionally; we fall back to a plain write (Windows ACL
        // semantics differ — the keyfile inherits the parent dir's
        // ACLs, and the per-user config dir under %LOCALAPPDATA%
        // already restricts to the owner). The whole class is only
        // selected on Unix at runtime; the Windows branch exists so
        // the unit tests can exercise round-trip / tamper logic on
        // the CI host without bringing up a Linux container.
        var tmp = _keyPath + ".tmp";
        try
        {
            FileStream fs;
            if (PlatformInfo.IsWindows)
            {
                fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            else
            {
                fs = new FileStream(tmp, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
            }
            using (fs)
            {
                fs.Write(key, 0, key.Length);
            }

            if (File.Exists(_keyPath)) File.Replace(tmp, _keyPath, destinationBackupFileName: null);
            else File.Move(tmp, _keyPath);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }

        // Belt + braces: on Unix, re-set the mode after the rename in
        // case the host filesystem (NFS, exotic FUSE) ignored
        // UnixCreateMode. No-op on Windows.
        TryChmod600(_keyPath);
        return key;
    }

    private static void TryChmod600(string path)
    {
        if (PlatformInfo.IsWindows) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception ex) { _log.Debug(ex, "chmod 600 on {Path} failed", path); }
    }
}
