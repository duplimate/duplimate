using System.Runtime.Versioning;

namespace Duplimate.Services.Platform.Windows;

/// <summary>
/// Windows-only thin wrapper around <see cref="System.Media.SystemSounds"/>.
/// Lives under Services/Platform/Windows/ so the
/// <c>System.Media.SystemSounds</c> reference (which is forwarded to
/// <c>System.Windows.Extensions</c> — a Windows-only assembly) never
/// reaches the cross-platform TFM.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsSystemSounds
{
    public static void PlayFor(NotificationSeverity s)
    {
        try
        {
            switch (s)
            {
                case NotificationSeverity.Failure:
                case NotificationSeverity.Warning:
                    System.Media.SystemSounds.Exclamation.Play();
                    break;
                case NotificationSeverity.Success:
                    System.Media.SystemSounds.Asterisk.Play();
                    break;
                default:
                    System.Media.SystemSounds.Beep.Play();
                    break;
            }
        }
        catch { }
    }
}
