using System.IO.Pipes;
using System.Security.Principal;
using System.Security.Cryptography;

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
                Console.WriteLine("1 设置/切换手机（需管理员）  2 请求确认  3 开启自动回连  4 关闭自动回连  5 停用  6 启用  q 退出");
                var input = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (input is null or "q") return;
                if (input == "1") await ConfigureAsync(token);
                else if (input == "2") await SendAsync(ServiceProtocol.Command("request"), token);
                else if (input is "3" or "4") await SendAsync(ServiceProtocol.Command("autoreconnect") with { Value = input == "3" }, token);
                else if (input is "5" or "6") await SendAsync(ServiceProtocol.Command("enabled") with { Value = input == "6" }, token);
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                Console.WriteLine($"后台操作未完成：{ErrorInfo.Describe(error)}");
                Console.WriteLine("首次使用需安装后台服务。回车重试，输入 q 退出。");
                if (Console.ReadLine()?.Trim().Equals("q", StringComparison.OrdinalIgnoreCase) != false) return;
            }
        }
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
