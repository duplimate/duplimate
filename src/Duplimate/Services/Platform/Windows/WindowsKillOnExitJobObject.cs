using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Duplimate.Services.Platform.Windows;

/// <summary>
/// Windows Job Object configured with KILL_ON_JOB_CLOSE. Every spawned
/// duplicacy.exe is assigned to this job; when Duplimate exits (or
/// crashes), Windows tears down the job and every member process with
/// it. Without this, a duplicacy.exe whose parent died ungracefully
/// would keep running until it finished — accumulating across crashes
/// to leave dozens of orphans in Task Manager.
///
/// CancellationToken-based <c>process.Kill(entireProcessTree: true)</c>
/// covers the cooperative path; the job object is the safety net for
/// the catastrophic path. Both are kept because Kill is faster and
/// gets stderr drained cleanly; the job object catches anything Kill
/// missed.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsKillOnExitJobObject
{
    private static readonly Lazy<IntPtr> _job = new(Create, isThreadSafe: true);

    /// <summary>
    /// Assigns <paramref name="process"/> to the per-process job object,
    /// guaranteeing it dies when Duplimate exits. Best-effort: if the
    /// API isn't available (e.g. running under restricted permissions)
    /// the call is a silent no-op and the process simply isn't
    /// job-assigned.
    /// </summary>
    public static void TryAssign(Process process)
    {
        try
        {
            var job = _job.Value;
            if (job == IntPtr.Zero) return;
            if (process.HasExited) return;
            // Process.Handle requires the caller to have already called
            // process.Start() — guaranteed by every site that calls us.
            AssignProcessToJobObject(job, process.Handle);
        }
        catch
        {
            // Restricted token, sandbox, or process gone before assign —
            // not a fatal condition. The cooperative cancel path
            // (process.Kill in the runner) still works.
        }
    }

    private static IntPtr Create()
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                AppLogger.For(nameof(WindowsKillOnExitJobObject)).Warning(
                    "CreateJobObject returned NULL (Win32 error {Err}); duplicacy.exe will not be killed on app crash.",
                    Marshal.GetLastWin32Error());
                return IntPtr.Zero;
            }

            // Configure the job so closing the LAST handle (i.e. when
            // Duplimate process dies) terminates every member.
            var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            };
            var ext = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = info,
            };
            int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(ext, buf, fDeleteOld: false);
                if (!SetInformationJobObject(job,
                        JobObjectInfoType.ExtendedLimitInformation,
                        buf, (uint)len))
                {
                    AppLogger.For(nameof(WindowsKillOnExitJobObject)).Warning(
                        "SetInformationJobObject failed (Win32 error {Err}); duplicacy.exe will not be killed on app crash.",
                        Marshal.GetLastWin32Error());
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
            return job;
        }
        catch (Exception ex)
        {
            // Restricted token, sandboxed environment, missing kernel32 —
            // log so an operator investigating "why are duplicacy.exe
            // orphans accumulating after a crash" has a starting point.
            AppLogger.For(nameof(WindowsKillOnExitJobObject)).Warning(ex,
                "Job Object creation threw; duplicacy.exe orphan-protection unavailable");
            return IntPtr.Zero;
        }
    }

    // ---- Win32 P/Invoke ------------------------------------------------

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}
