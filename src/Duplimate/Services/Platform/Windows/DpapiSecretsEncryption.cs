using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Duplimate.Services.Platform.Windows;

/// <summary>
/// DPAPI-backed encryption (CurrentUser scope). Bound to the Windows
/// user + machine pair: copying <c>secrets.bin</c> to another account
/// or another machine produces a <see cref="CryptographicException"/>
/// from <see cref="ProtectedData.Unprotect"/> — exactly the trip-wire
/// the SecretsStore expects so it can preserve the unreadable blob
/// and prompt the user to re-enter their tokens.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretsEncryption : ISecretsEncryption
{
    public string DisplayName => "DPAPI (Windows user + machine)";

    public byte[] Protect(byte[] cleartext, byte[] entropy) =>
        ProtectedData.Protect(cleartext, entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext, byte[] entropy) =>
        ProtectedData.Unprotect(ciphertext, entropy, DataProtectionScope.CurrentUser);
}
