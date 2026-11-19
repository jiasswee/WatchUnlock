using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WatchUnlock.Service;

internal static class LocalAccount
{
    internal static string Validate(string sid, byte[] password)
    {
        if (password.Length is 0 or > 512 || password.Length % 2 != 0)
            throw new InvalidDataException("密码长度无效。");
        var account = ((NTAccount)new SecurityIdentifier(sid).Translate(typeof(NTAccount))).Value;
        var parts = account.Split('\\');
        if (parts.Length != 2 || !parts[0].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("仅支持本地账户。");
        var result = NetUserGetInfo(null, parts[1], 24, out var info);
        if (result != 0) throw new Win32Exception((int)result);
        try { if (Marshal.ReadInt32(info) != 0) throw new UnauthorizedAccessException("Microsoft 账户暂不支持。"); }
        finally { NetApiBufferFree(info); }
        var terminated = new byte[password.Length + 2];
        password.CopyTo(terminated, 0);
        var pinned = GCHandle.Alloc(terminated, GCHandleType.Pinned);
        try
        {
            // No immutable managed password string is constructed.
            for (var i = 0; i < password.Length; i += 2)
                if (password[i] == 0 && password[i + 1] == 0) throw new InvalidDataException("密码包含空字符。");
            if (!LogonUser(parts[1], parts[0], pinned.AddrOfPinnedObject(), 2, 0, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                if (identity.User?.Value != sid) throw new UnauthorizedAccessException("密码属于其他账户。");
            return parts[1];
        }
        finally { pinned.Free(); CryptographicOperations.ZeroMemory(terminated); }
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint NetUserGetInfo(string? server, string user, uint level, out IntPtr buffer);
    [DllImport("netapi32.dll")]
    private static extern uint NetApiBufferFree(IntPtr buffer);
    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(string user, string domain, IntPtr password, uint type, uint provider, out SafeAccessTokenHandle token);
}
