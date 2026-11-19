using System.IO.Pipes;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Buffers.Binary;

namespace WatchUnlock.BleClient;

internal static class ServiceControl
{
    internal static async Task MenuAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await SendAsync(ServiceProtocol.Command("status"), token);
                Console.WriteLine("1 设置/切换手机  2 请求确认  3 开启自动回连  4 关闭自动回连  5 停用  6 启用  7 保存本地账户密码  8 删除已保存密码  q 退出");
                var input = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (input is null or "q") return;
                if (input == "1") await ConfigureAsync(token);
                else if (input == "2") await SendAsync(ServiceProtocol.Command("request"), token);
                else if (input is "3" or "4") await SendAsync(ServiceProtocol.Command("autoreconnect") with { Value = input == "3" }, token);
                else if (input is "5" or "6") await SendAsync(ServiceProtocol.Command("enabled") with { Value = input == "6" }, token);
                else if (input is "7" or "8") await ConfigureCredentialAsync(input == "7", token);
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                Console.WriteLine($"后台操作未完成：{ErrorInfo.Describe(error)}");
                Console.WriteLine("部署配置需先运行 install-service.ps1；通信测试直接打开程序即可。回车重试，输入 q 退出。");
                if (Console.ReadLine()?.Trim().Equals("q", StringComparison.OrdinalIgnoreCase) != false) return;
            }
        }
    }

    private static async Task ConfigureCredentialAsync(bool save, CancellationToken token)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("请在目标账户的桌面上以管理员身份打开部署配置。");
        byte[] password = save ? PasswordInput.Read() : Array.Empty<byte>();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var pipe = new NamedPipeClientStream(".", CredentialProtocol.SetupPipe, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            await pipe.ConnectAsync(5000, deadline.Token);
            ServiceIdentity.VerifyServer(pipe);
            await pipe.WriteAsync(CredentialProtocol.SetupHeader(save ? 1u : 2u, password.Length), deadline.Token);
            await pipe.WriteAsync(password, deadline.Token);
            var result = await CredentialProtocol.ReadAsync(pipe, 4, deadline.Token);
            if (BinaryPrimitives.ReadUInt32LittleEndian(result) != 0)
                throw new InvalidOperationException("配置失败：请先设置手机，确认当前账户是本地账户、密码正确，并使用该账户的管理员权限。");
            Console.WriteLine(save ? "密码已在电脑端校验并加密保存。安装登录入口后生效。" : "已删除保存的密码，WatchUnlock 登录入口已停用。");
        }
        finally { CryptographicOperations.ZeroMemory(password); }
    }

    private static async Task ConfigureAsync(CancellationToken token)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("设置或切换后台服务手机时，请以管理员身份运行这个程序。");
        var phone = await PhonePicker.SelectAsync(token);
        if (phone is null) return;
        var key = PairingProtocol.ReadKey();
        try
        {
            await SendAsync(ServiceProtocol.Command("configure") with
            {
                Address = phone.Address, AddressType = (int)phone.AddressType, Name = phone.Name,
                Key = Convert.ToBase64String(key)
            }, token);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static async Task SendAsync(ServiceCommand command, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        using var pipe = new NamedPipeClientStream(".", ServiceProtocol.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await pipe.ConnectAsync(5000, deadline.Token);
        ServiceIdentity.VerifyServer(pipe);
        await ServiceProtocol.WriteAsync(pipe, command, deadline.Token);
        while (true)
        {
            var reply = await ServiceProtocol.ReadAsync<ServiceReply>(pipe, deadline.Token);
            if (reply.V != 1 || reply.RequestId != command.RequestId) throw new InvalidDataException("后台服务回复不属于当前请求。");
            if (reply.Kind == "error") throw new IOException(reply.Message);
            Console.WriteLine(reply.Message);
            if (reply.Kind == "progress") continue;
            if (reply.Kind is not ("status" or "result")) throw new InvalidDataException("后台服务回复类型无效。");
            return;
        }
    }
}
