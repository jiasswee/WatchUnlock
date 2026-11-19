using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32.SafeHandles;
using WatchUnlock.BleClient;
using Windows.Devices.Bluetooth;

namespace WatchUnlock.Service;

internal sealed class ServiceWorker : BackgroundService
{
    private readonly BleCoordinator coordinator;
    public ServiceWorker(BleCoordinator coordinator) { this.coordinator = coordinator; }
    private sealed record Caller(string Sid, bool Admin, bool System, uint Session);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var listener = ListenAsync(lifetime.Token);
        var reconnect = coordinator.ReconnectLoopAsync(lifetime.Token);
        await Task.WhenAny(listener, reconnect);
        lifetime.Cancel();
        await Task.WhenAll(listener, reconnect);
    }

    private async Task ListenAsync(CancellationToken token)
    {
        using var slots = new SemaphoreSlim(8, 8);
        var clients = new HashSet<Task>();
        var first = true;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await slots.WaitAsync(token);
                NamedPipeServerStream? pipe = null;
                try
                {
                    var security = new PipeSecurity();
                    security.SetAccessRuleProtection(true, false);
                    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
                    var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                    pipe = NamedPipeServerStreamAcl.Create(ServiceProtocol.PipeName, PipeDirection.InOut, 8,
                        PipeTransmissionMode.Byte, options, 4096, 4096, security);
                    first = false;
                    await pipe.WaitForConnectionAsync(token);
                    var accepted = pipe;
                    pipe = null;
                    clients.RemoveWhere(task => task.IsCompleted);
                    clients.Add(HandleAsync(accepted, slots, token));
                }
                catch { pipe?.Dispose(); slots.Release(); throw; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await Task.WhenAll(clients); }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, SemaphoreSlim slots, CancellationToken shutdown)
    {
        using (pipe)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown))
        {
            lifetime.CancelAfter(TimeSpan.FromSeconds(90));
            var id = "";
            Task? monitor = null;
            try
            {
                var command = await ServiceProtocol.ReadAsync<ServiceCommand>(pipe, lifetime.Token);
                id = command.RequestId;
                if (command.V != 1 || !Guid.TryParseExact(id, "D", out var parsed) || parsed.ToString("D") != id)
                    throw new InvalidDataException("后台服务请求版本或编号无效。");
                var caller = ReadCaller(pipe);
                if (!caller.Admin && !caller.System && caller.Sid != coordinator.OwnerSid)
                    throw new UnauthorizedAccessException("此 Windows 用户没有使用 WatchUnlock 的权限。");
                monitor = MonitorClientAsync(pipe, lifetime);
                switch (command.Operation)
                {
                    case "status":
                        await ServiceProtocol.WriteAsync(pipe, coordinator.Status(id), lifetime.Token);
                        break;
                    case "configure":
                        if (!caller.Admin || caller.Session == 0 || !caller.Sid.StartsWith("S-1-5-21-", StringComparison.Ordinal))
                            throw new UnauthorizedAccessException("请在目标用户的桌面上，以管理员身份设置手机。");
                        if (command.Address == 0 || command.Address >= (1UL << 48) ||
                            !Enum.IsDefined((BluetoothAddressType)command.AddressType) ||
                            string.IsNullOrWhiteSpace(command.Name) || command.Name.Length > 248 || command.Name.Any(char.IsControl))
                            throw new InvalidDataException("手机配置无效，请重新扫描选择。");
                        var key = PairingProtocol.Base64Bytes(command.Key ?? "");
                        try
                        {
                            await ServiceProtocol.WriteAsync(pipe, new ServiceReply(1, id, "progress", "后台服务正在连接手机并校验 Key……"), lifetime.Token);
                            await coordinator.ConfigureAsync(new Phone(command.Address, (BluetoothAddressType)command.AddressType, command.Name), key, caller.Sid, lifetime.Token);
                        }
                        finally { CryptographicOperations.ZeroMemory(key); }
                        await ServiceProtocol.WriteAsync(pipe, coordinator.Status(id) with { Kind = "result" }, lifetime.Token);
                        break;
                    case "enabled":
                    case "autoreconnect":
                        coordinator.ChangeMode(command.Operation, command.Value ?? throw new InvalidDataException("缺少开关状态。"), caller.Sid, caller.Admin || caller.System);
                        await ServiceProtocol.WriteAsync(pipe, coordinator.Status(id) with { Kind = "result" }, lifetime.Token);
                        break;
                    case "request":
                        if (caller.Session == 0 || (!caller.System && caller.Sid != coordinator.OwnerSid))
                            throw new UnauthorizedAccessException("授权请求必须来自已配置用户的交互会话。");
                        await ServiceProtocol.WriteAsync(pipe, new ServiceReply(1, id, "progress", "后台服务正在发起新的 10 秒请求……"), lifetime.Token);
                        var decision = await coordinator.RequestAsync(caller.Sid, caller.System, lifetime.Token);
                        lifetime.Token.ThrowIfCancellationRequested();
                        var text = decision == "approved" ? "回复匹配，后台服务已验证授权（尚未接入 Windows 登录）。" : $"本次未授权，结果：{decision}。";
                        await ServiceProtocol.WriteAsync(pipe, new ServiceReply(1, id, "result", text, Decision: decision), lifetime.Token);
                        break;
                    default: throw new InvalidDataException("未知后台服务操作。");
                }
            }
            catch (Exception error) when (!shutdown.IsCancellationRequested)
            {
                try { await ServiceProtocol.WriteAsync(pipe, new ServiceReply(1, id, "error", ErrorInfo.Describe(error)), lifetime.Token); }
                catch (Exception) { /* Disconnected callers cannot receive or reuse an authorization. */ }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            finally
            {
                lifetime.Cancel();
                if (monitor is not null) await monitor;
                slots.Release();
            }
        }
    }

    private static async Task MonitorClientAsync(Stream pipe, CancellationTokenSource lifetime)
    {
        try
        {
            await pipe.ReadAsync(new byte[1], lifetime.Token);
            lifetime.Cancel(); // EOF or another command invalidates this single-command connection.
        }
        catch (Exception) { lifetime.Cancel(); }
    }

    private static Caller ReadCaller(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var session))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Caller? result = null;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(true) ?? throw new UnauthorizedAccessException("无法确认管道用户。");
            var sid = identity.User?.Value ?? throw new UnauthorizedAccessException("无法确认管道用户。");
            result = new(sid, new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), sid == "S-1-5-18", session);
        });
        return result ?? throw new UnauthorizedAccessException("无法确认管道用户。");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint session);
}
