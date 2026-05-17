using System.IO;
using System.Linq;
using Duplimate.Services;
using Duplimate.Tests.TestHelpers;
using Xunit;

namespace Duplimate.Tests.Services;

/// <summary>
/// SecretsStore is the DPAPI-protected per-user secret vault. The tests
/// here pin two behaviours we don't want to regress:
///
///   1. A secrets.bin that this user/machine can't decrypt (typically:
///      copied from another machine or another Windows account, since
///      DPAPI keys are user+machine bound) must not be silently
///      overwritten on the next save. Earlier the code logged a warning
///      and started fresh — the next Set() call clobbered the
///      preserved ciphertext, and the user lost the chance to recover
///      anything via a manual DPAPI session on the original account.
///   2. The store surfaces a LoadErrorMessage so the UI can warn the
///      user once at startup that previously-saved tokens/passwords
///      need to be re-entered.
/// </summary>
public class SecretsStoreTests
{
    [Fact]
    public void Load_clean_succeeds_with_no_error()
    {
        using var ws = new TempWorkspace();
        var store = new SecretsStore();
        store.Set("ref:dropbox", "abc123");

        // Re-open: a brand-new store reading the same vault.
        var reopened = new SecretsStore();
        Assert.True(reopened.TryGet("ref:dropbox", out var v));
        Assert.Equal("abc123", v);
        Assert.Null(reopened.LoadErrorMessage);
        Assert.Null(reopened.PreservedCorruptPath);
    }

    [Fact]
    public void Load_undecryptable_blob_preserves_file_and_surfaces_error()
    {
        using var ws = new TempWorkspace();
        // Drop garbage at the secrets path so DPAPI Unprotect throws.
        // Random bytes that aren't a valid DPAPI blob produce a
        // CryptographicException on Unprotect — same path a foreign
        // account's encrypted blob would hit.
        File.WriteAllBytes(AppPaths.SecretsFile, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03 });

        var store = new SecretsStore();
        // Trigger Load via a read.
        Assert.False(store.TryGet("anything", out _));

        Assert.NotNull(store.LoadErrorMessage);
        Assert.Contains("DPAPI", store.LoadErrorMessage);

        // Original ciphertext must be preserved (renamed) so a forensic
        // recovery on the originating account is still possible.
        Assert.NotNull(store.PreservedCorruptPath);
        Assert.True(File.Exists(store.PreservedCorruptPath));
        Assert.False(File.Exists(AppPaths.SecretsFile)); // moved aside

        // A subsequent Set() must NOT touch the preserved file — it
        // creates a new vault next to it.
        store.Set("ref:test", "newvalue");
        Assert.True(File.Exists(AppPaths.SecretsFile));
        Assert.True(File.Exists(store.PreservedCorruptPath));

        // The new vault works as expected.
        var reopened = new SecretsStore();
        Assert.True(reopened.TryGet("ref:test", out var v));
        Assert.Equal("newvalue", v);
        Assert.Null(reopened.LoadErrorMessage); // new vault is clean
    }

    [Fact]
    public void TryGet_missing_key_returns_default_when_load_failed()
    {
        // Even after a load failure, callers (e.g., DestinationEditor
        // password rotation guard) should be able to TryGet safely.
        // Returning false + a default value lets the caller treat the
        // absence as "no stored secret" rather than NRE.
        using var ws = new TempWorkspace();
        File.WriteAllBytes(AppPaths.SecretsFile, new byte[] { 0xFF, 0xFF, 0xFF });

        var store = new SecretsStore();
        var ok = store.TryGet("ref:never-set", out var v);

        Assert.False(ok);
        Assert.NotNull(store.LoadErrorMessage);
        // out value is whatever Dictionary.TryGetValue produces on miss
        // — we don't pin its exact value, only that the call didn't
        // throw and `ok` is false.
    }
}
