using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Duplimate.Stub;

/// <summary>
/// Fallback for Duplimate scheduled tasks when the user's main
/// Duplimate.exe is no longer at the path the task was registered
/// with — i.e. they moved or deleted the download and haven't reopened
/// the app yet to refresh schedules.
///
/// Two side effects, in this order:
///   1. Write a flag file
///      (<c>%LOCALAPPDATA%\Duplimate\stub-fired.flag</c>) so the
///      main app can show a Dashboard banner the next time it opens.
///      Done first because it's the durable signal — it survives even
///      if the MessageBox can't render.
///   2. Pop a MessageBox via P/Invoke with
///      <c>MB_SERVICE_NOTIFICATION</c> set, which routes the dialog to
///      the active interactive session even when the task is running
///      under S4U logon (no desktop of its own). If no user is logged
///      in, the call returns silently and the flag-file path takes
///      over.
///
/// The stub takes no command-line args. It returns 0 on the
/// flag-write-then-prompt path even when the prompt couldn't display —
/// returning nonzero would just look like a backup failure in Task
/// Scheduler history, which is the wrong category. The flag file is
/// the trustworthy signal here.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        TryWriteFlag();
        TryShowWarning();
        return 0;
    }

    private static void TryWriteFlag()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Duplimate");
            Directory.CreateDirectory(dir);
            var flag = Path.Combine(dir, "stub-fired.flag");
            File.WriteAllText(flag, DateTime.UtcNow.ToString("o"));
        }
        catch
        {
            // Last-ditch fallback path; if even %LOCALAPPDATA% is
            // unreachable there's nothing useful we can do here.
        }
    }

    private static void TryShowWarning()
    {
        const uint MB_OK = 0x00000000;
        const uint MB_ICONWARNING = 0x00000030;
        const uint MB_SETFOREGROUND = 0x00010000;
        // The magic flag: tells Windows to display the dialog on the
        // currently-active interactive session even when this process
        // has no desktop of its own (S4U scheduled tasks don't).
        const uint MB_SERVICE_NOTIFICATION = 0x00200000;

        const string Title = "Duplimate needs attention";
        const string Body =
            "Duplimate tried to run a scheduled backup but the app file " +
            "isn't where it was last seen.\n\n" +
            "If you moved or deleted Duplimate.exe, just open it again from " +
            "its new location — your scheduled backups will fix themselves " +
            "the moment the app launches.";

        try
        {
            _ = MessageBoxW(
                IntPtr.Zero,
                Body,
                Title,
                MB_OK | MB_ICONWARNING | MB_SETFOREGROUND | MB_SERVICE_NOTIFICATION);
        }
        catch
        {
            // Ignore — flag file is the durable signal regardless.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
