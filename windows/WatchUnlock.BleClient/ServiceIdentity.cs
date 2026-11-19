using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WatchUnlock.BleClient;

internal static class ServiceIdentity
{
    // Check SCM's running service PID before sending any Key to a pipe server.
    internal static void VerifyServer(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actual)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var service = IntPtr.Zero;
        try
        {
            service = OpenService(manager, "WatchUnlock", 4);
            if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (status.State != 4 || status.ProcessId == 0 || status.ProcessId != actual)
                throw new UnauthorizedAccessException("管道服务身份与已安装的 WatchUnlock 服务不匹配。");
        }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            CloseServiceHandle(manager);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        internal uint Type, State, Controls, ExitCode, SpecificExitCode, Checkpoint, WaitHint, ProcessId, Flags;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW", SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW", SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatus status, int bytes, out int needed);
    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
