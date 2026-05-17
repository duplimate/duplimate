using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Duplimate.Services.Platform.Unix;
using Xunit;

namespace Duplimate.Tests.Services;

/// <summary>
/// Unit tests for the macOS / Linux secrets encryption provider. The
/// AES-GCM logic is platform-agnostic, so even though the provider is
/// only used on Unix at runtime we can exercise round-trip, tamper
/// detection, and corrupt-keyfile handling on the Windows test host.
///
/// <para>
/// Note on <see cref="Assert.ThrowsAny{T}"/>: AesGcm.Decrypt raises
/// <see cref="System.Security.Cryptography.AuthenticationTagMismatchException"/>
/// — a subclass of <see cref="CryptographicException"/>. Tests use
/// <c>ThrowsAny</c> so the contract is "any crypto failure surfaces",
/// which matches what SecretsStore.Load catches at the call site
/// (<c>catch (Exception ex)</c>).
/// </para>
///
/// <para>
/// The chmod-600 keyfile-permission contract isn't covered here —
/// that would need a Unix host to verify. Best-effort code paths
/// (try/catch around <see cref="File.SetUnixFileMode"/>) ensure
/// no-op on Windows.
/// </para>
/// </summary>
public class FileKeySecretsEncryptionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _keyPath;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("test-entropy-v1");

    public FileKeySecretsEncryptionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "duplimate-fks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _keyPath = Path.Combine(_tempDir, "secrets-key.bin");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void RoundTrip_recovers_cleartext_byte_for_byte()
    {
        var crypto = new FileKeySecretsEncryption(_keyPath);
        var plain = Encoding.UTF8.GetBytes("dropbox-token=abc123, mailgun-key=xyz789, s3-secret=hunter2");

        var ct = crypto.Protect(plain, Entropy);
        var pt = crypto.Unprotect(ct, Entropy);

        Assert.Equal(plain, pt);
        Assert.True(File.Exists(_keyPath), "Key should be persisted after first Protect call");
    }

    [Fact]
    public void RoundTrip_works_across_separate_provider_instances()
    {
        // First instance writes the key + ciphertext. Second instance
        // reads the key from disk and decrypts. Mirrors the SecretsStore
        // lifecycle: encrypt-on-Set, persist, then a separate process
        // (or app restart) re-reads.
        var plain = Encoding.UTF8.GetBytes("payload");

        var w = new FileKeySecretsEncryption(_keyPath);
        var ct = w.Protect(plain, Entropy);

        var r = new FileKeySecretsEncryption(_keyPath);
        var pt = r.Unprotect(ct, Entropy);

        Assert.Equal(plain, pt);
    }

    [Fact]
    public void Unprotect_throws_when_ciphertext_was_tampered_with()
    {
        var crypto = new FileKeySecretsEncryption(_keyPath);
        var ct = crypto.Protect(Encoding.UTF8.GetBytes("payload"), Entropy);

        // Flip a bit in the ciphertext body. AES-GCM's tag check fires
        // on any single-bit change anywhere in the wire blob — that's
        // the trip-wire SecretsStore.Load relies on to detect a copied
        // / corrupted vault.
        ct[ct.Length / 2] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => crypto.Unprotect(ct, Entropy));
    }

    [Fact]
    public void Unprotect_throws_when_entropy_differs()
    {
        // Entropy doubles as AES-GCM's "additional authenticated data"
        // — encrypt-with-A, decrypt-with-B fails the tag check.
        // SecretsStore feeds the same fixed entropy on both sides, but
        // a future provider that rotates entropy still gets the right
        // failure semantics.
        var crypto = new FileKeySecretsEncryption(_keyPath);
        var ct = crypto.Protect(Encoding.UTF8.GetBytes("payload"), Entropy);

        var otherEntropy = Encoding.UTF8.GetBytes("different-entropy");
        Assert.ThrowsAny<CryptographicException>(() => crypto.Unprotect(ct, otherEntropy));
    }

    [Fact]
    public void Unprotect_throws_when_keyfile_was_replaced()
    {
        var w = new FileKeySecretsEncryption(_keyPath);
        var ct = w.Protect(Encoding.UTF8.GetBytes("payload"), Entropy);

        // Simulate the "config copied between machines without the
        // keyfile" failure mode: zap the key, let the second provider
        // generate a fresh one, then try to decrypt with the wrong
        // key.
        File.Delete(_keyPath);

        var r = new FileKeySecretsEncryption(_keyPath);
        Assert.ThrowsAny<CryptographicException>(() => r.Unprotect(ct, Entropy));
    }

    [Fact]
    public void Unprotect_throws_on_too_short_blob()
    {
        var crypto = new FileKeySecretsEncryption(_keyPath);
        // 8 bytes < nonce(12) + tag(16) — can't possibly be a valid
        // AES-GCM ciphertext. Defensive check fires before the AesGcm
        // ctor can throw a less-actionable error.
        Assert.ThrowsAny<CryptographicException>(() =>
            crypto.Unprotect(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03 }, Entropy));
    }

    [Fact]
    public void Different_calls_produce_different_ciphertexts_for_same_input()
    {
        // Per-call random nonce so re-encrypting unchanged payload
        // still rotates the wire blob. Otherwise a passive observer
        // could detect "this user didn't change their password" by
        // diffing successive secrets.bin snapshots.
        var crypto = new FileKeySecretsEncryption(_keyPath);
        var plain = Encoding.UTF8.GetBytes("payload");

        var a = crypto.Protect(plain, Entropy);
        var b = crypto.Protect(plain, Entropy);

        Assert.NotEqual(Convert.ToHexString(a), Convert.ToHexString(b));
    }

    [Fact]
    public void Corrupt_keyfile_is_rotated_aside_and_a_fresh_key_is_minted()
    {
        // Drop a wrong-length blob where the keyfile should be. The
        // provider treats this as corrupt, moves it aside with a
        // .corrupt-* suffix, and creates a brand-new 32-byte key.
        File.WriteAllBytes(_keyPath, new byte[] { 1, 2, 3, 4, 5 });

        var crypto = new FileKeySecretsEncryption(_keyPath);
        var ct = crypto.Protect(Encoding.UTF8.GetBytes("payload"), Entropy);

        // Round-trip with the new key.
        Assert.Equal("payload", Encoding.UTF8.GetString(crypto.Unprotect(ct, Entropy)));

        // Original corrupt blob preserved next to the keyfile (best-
        // effort — exact suffix uses a UTC timestamp, so we just
        // assert at least one .corrupt-* sibling exists).
        var siblings = Directory.GetFiles(_tempDir, "secrets-key.bin.corrupt-*");
        Assert.NotEmpty(siblings);
    }

    [Fact]
    public void DisplayName_mentions_provider_kind()
    {
        // SecretsStore embeds the provider DisplayName in the "couldn't
        // decrypt your vault" user-facing message. Pin that the string
        // contains "AES-GCM" so a grep over the codebase still finds
        // the right provider when investigating a user report.
        var crypto = new FileKeySecretsEncryption(_keyPath);
        Assert.Contains("AES-GCM", crypto.DisplayName);
    }
}
