namespace Duplimate.Services.Platform;

/// <summary>
/// Encrypt / decrypt the secrets vault blob. The whole vault is a
/// single JSON-serialised dictionary, wrapped by this transform, then
/// written to <c>secrets.bin</c> atomically.
///
/// <para>
/// Per-platform implementations:
/// <list type="bullet">
///   <item><b>Windows</b> — DPAPI (CurrentUser) via
///         <c>System.Security.Cryptography.ProtectedData</c>. Bound
///         to the Windows user + machine pair. Same security
///         posture as Duplicacy's own keyring.</item>
///   <item><b>macOS / Linux</b> — AES-GCM with a 256-bit key kept in
///         a chmod-600 file at <c>secrets-key.bin</c> in the config
///         dir. Not bound to the OS keychain (a follow-up can wire
///         macOS Keychain / libsecret) but a meaningful obstacle to
///         offline-only attackers — without the key the ciphertext
///         is opaque.</item>
/// </list>
/// </para>
///
/// <para>
/// <see cref="Unprotect"/> throws on tamper / wrong key / corrupt
/// blob. <see cref="SecretsStore"/> catches that and surfaces a
/// user-facing "your saved tokens couldn't be decrypted" error path,
/// preserving the original ciphertext for forensic recovery.
/// </para>
/// </summary>
public interface ISecretsEncryption
{
    byte[] Protect(byte[] cleartext, byte[] entropy);
    byte[] Unprotect(byte[] ciphertext, byte[] entropy);

    /// <summary>
    /// Short label shown in the user-facing "couldn't decrypt" message
    /// so a power user reading it knows whether they're looking at
    /// DPAPI / Keychain / file-key trouble.
    /// </summary>
    string DisplayName { get; }
}
