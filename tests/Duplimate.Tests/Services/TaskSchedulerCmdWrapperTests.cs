using Duplimate.Services.Platform.Windows;
using Xunit;

namespace Duplimate.Tests.Services;

/// <summary>
/// Pins the exact cmd.exe-wrapper argument shape that the Windows task
/// scheduler implementation uses for every backup task. Quoting under
/// <c>cmd /S /C "..."</c> is tricky enough that a casual refactor could
/// break every scheduled task silently; these tests assert the literal
/// shape so a regression is loud at build time.
/// </summary>
public class TaskSchedulerCmdWrapperTests
{
    [Fact]
    public void BuildCmdWrapperArguments_producesIfExistFallbackShape()
    {
        var args = WindowsTaskScheduler.BuildCmdWrapperArguments(
            mainExe: @"C:\Users\me\Downloads\Duplimate.exe",
            stubExe: @"C:\Users\me\AppData\Local\Programs\Duplimate\Duplimate-stub.exe",
            backupName: "documents");

        // Outer cmd /D /S /C "..." with each path/name doubled-up
        // (cmd's "" → " escape inside the /S /C string). Using
        // standard escapes here for clarity — \" is one quote char.
        var expected =
            "/D /S /C \"if exist \"\"C:\\Users\\me\\Downloads\\Duplimate.exe\"\" " +
            "( \"\"C:\\Users\\me\\Downloads\\Duplimate.exe\"\" --run \"\"documents\"\" ) " +
            "else ( \"\"C:\\Users\\me\\AppData\\Local\\Programs\\Duplimate\\Duplimate-stub.exe\"\" )\"";
        Assert.Equal(expected, args);
    }

    [Fact]
    public void BuildCmdWrapperArguments_handlesPathWithSpaces()
    {
        // Spaces are why we quote in the first place. Pin that the path
        // segments come out as a single quoted token, not split into
        // separate cmd arguments.
        var args = WindowsTaskScheduler.BuildCmdWrapperArguments(
            mainExe: @"C:\Program Files\Duplimate\Duplimate.exe",
            stubExe: @"C:\Users\me\AppData\Local\Programs\Duplimate\Duplimate-stub.exe",
            backupName: "my_backup");

        Assert.Contains("\"\"C:\\Program Files\\Duplimate\\Duplimate.exe\"\"", args);
        Assert.Contains("--run \"\"my_backup\"\"", args);
    }
}
