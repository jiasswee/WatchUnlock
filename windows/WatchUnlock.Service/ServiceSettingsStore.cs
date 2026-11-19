using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace WatchUnlock.Service;

internal sealed record ServiceSettings(string Revision, ulong Address, int AddressType, string Name, string OwnerSid,
    string ProtectedKey, bool Enabled, bool AutoReconnect);

internal sealed class ServiceSettingsStore
{
    private readonly string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WatchUnlock");
    private string FilePath => Path.Combine(folder, "service.json");

    internal ServiceSettings? Load()
    {
        if (!File.Exists(FilePath)) return null;
        var directory = new DirectoryInfo(folder);
        var file = new FileInfo(FilePath);
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("后台服务配置目录不能使用链接。");
        RequirePrivate(directory.GetAccessControl());
        RequirePrivate(file.GetAccessControl());
        if (new FileInfo(FilePath).Length > 8192) throw new InvalidDataException("后台服务配置过大。");
        return JsonSerializer.Deserialize<ServiceSettings>(File.ReadAllBytes(FilePath))
            ?? throw new InvalidDataException("后台服务配置无效。");
    }

    internal void Save(ServiceSettings value)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        foreach (var sidType in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sidType, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(folder);
        if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("后台服务配置目录不能使用链接。");
        directory.Create(security);
        directory.SetAccessControl(security);
        var temporary = Path.Combine(folder, $"{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RequirePrivate(FileSystemSecurity security)
    {
        static bool Trusted(IdentityReference identity) => identity.Value is "S-1-5-18" or "S-1-5-32-544";
        if (!Trusted(security.GetOwner(typeof(SecurityIdentifier)))) throw new UnauthorizedAccessException("配置所有者无效，请以管理员身份重新配置。");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !Trusted(rule.IdentityReference))
                throw new UnauthorizedAccessException("配置权限过宽，请以管理员身份重新配置。");
    }

    internal static string Protect(byte[] key) => Convert.ToBase64String(ProtectedData.Protect(key, null, DataProtectionScope.LocalMachine));
    internal static byte[] Unprotect(ServiceSettings settings)
    {
        var key = ProtectedData.Unprotect(Convert.FromBase64String(settings.ProtectedKey), null, DataProtectionScope.LocalMachine);
        if (key.Length == 32) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new InvalidDataException("后台服务 Key 无效，请重新设置手机。");
    }
}
