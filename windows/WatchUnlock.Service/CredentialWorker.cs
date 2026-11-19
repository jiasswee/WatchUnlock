using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32.SafeHandles;
using WatchUnlock.BleClient;

namespace WatchUnlock.Service;

internal sealed class CredentialWorker : BackgroundService
{
    private readonly BleCoordinator coordinator;
    public CredentialWorker(BleCoordinator coordinator) { this.coordinator = coordinator; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var login = ListenAsync(CredentialProtocol.LoginPipe, false, lifetime.Token);
        var setup = ListenAsync(CredentialProtocol.SetupPipe, true, lifetime.Token);
        await Task.WhenAny(login, setup);
        lifetime.Cancel();
        await Task.WhenAll(login, setup);
    }

    private async Task ListenAsync(string name, bool setup, CancellationToken token)
    {
        using var slots = new SemaphoreSlim(4);
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
                    if (setup) security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                    pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 4096, 4096, security);
                    first = false;
                    await pipe.WaitForConnectionAsync(token);
                    var accepted = pipe;
                    pipe = null;
                    clients.RemoveWhere(client => client.IsCompleted);
                    clients.Add(HandleAsync(accepted, setup, slots, token));
                }
                catch { pipe?.Dispose(); slots.Release(); throw; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await Task.WhenAll(clients); }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, bool setup, SemaphoreSlim slots, CancellationToken shutdown)
    {
        using (pipe)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown))
        {
            lifetime.CancelAfter(TimeSpan.FromSeconds(setup ? 30 : 90));
            var requestId = new byte[16];
            Task? monitor = null;
            try
            {
                if (setup) await SetupAsync(pipe, lifetime.Token);
                else
                {
                    var header = await CredentialProtocol.ReadAsync(pipe, 28, lifetime.Token);
                    header.AsSpan(8, 16).CopyTo(requestId);
                    var scenario = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                    var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)));
                    if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CredentialProtocol.LoginMagic || scenario is not (1 or 2) ||
                        length is <= 0 or > 184 || length % 2 != 0 || new Guid(requestId) == Guid.Empty)
                        throw new InvalidDataException("登录请求无效。");
                    var sid = new UnicodeEncoding(false, false, true).GetString(await CredentialProtocol.ReadAsync(pipe, length, lifetime.Token));
                    if (new SecurityIdentifier(sid).Value != sid) throw new UnauthorizedAccessException();
                    VerifyLogonUi(pipe, sid, scenario);
                    monitor = MonitorAsync(pipe, lifetime);
                    var revision = coordinator.CredentialRevision(sid);
                    var decision = await coordinator.RequestAsync(sid, lifetime.Token);
                    lifetime.Token.ThrowIfCancellationRequested();
                    if (decision != "approved") await ReplyAsync(pipe, requestId, 1, null, null, lifetime.Token);
                    else
                    {
                        var credential = coordinator.TakeCredential(sid, revision);
                        try
                        {
                            lifetime.Token.ThrowIfCancellationRequested();
                            VerifyLogonUi(pipe, sid, scenario);
                            if (coordinator.CredentialRevision(sid) != revision) throw new OperationCanceledException();
                            await ReplyAsync(pipe, requestId, 0, credential.Account, credential.Password, lifetime.Token);
                        }
                        finally { CryptographicOperations.ZeroMemory(credential.Password); }
                    }
                }
            }
            catch (Exception) when (!shutdown.IsCancellationRequested)
            {
                try
                {
                    if (setup) await pipe.WriteAsync(new byte[] { 1, 0, 0, 0 }, lifetime.Token);
                    else await ReplyAsync(pipe, requestId, 2, null, null, lifetime.Token);
                }
                catch (Exception) { }
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

    private async Task SetupAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        var header = await CredentialProtocol.ReadAsync(pipe, 12, token);
        var operation = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)));
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CredentialProtocol.SetupMagic || operation is not (1 or 2) ||
            length < 0 || length > CredentialProtocol.MaximumPasswordBytes || length % 2 != 0 || (operation == 2 && length != 0))
            throw new InvalidDataException();
        var caller = Caller(pipe);
        if (!caller.Admin || caller.System || caller.Session == 0) throw new UnauthorizedAccessException();
        var password = await CredentialProtocol.ReadAsync(pipe, length, token);
        try { coordinator.SetCredential(caller.Sid, operation == 1 ? password : null); }
        finally { CryptographicOperations.ZeroMemory(password); }
        await pipe.WriteAsync(new byte[4], token);
    }

    private static async Task ReplyAsync(Stream pipe, byte[] id, uint result, string? account, byte[]? password, CancellationToken token)
    {
        var domain = result == 0 ? Encoding.Unicode.GetBytes(Environment.MachineName) : Array.Empty<byte>();
        var user = result == 0 ? Encoding.Unicode.GetBytes(account!) : Array.Empty<byte>();
        var size = password?.Length ?? 0;
        var bytes = new byte[36 + domain.Length + user.Length + size];
        try
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, CredentialProtocol.LoginMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), result);
            id.CopyTo(bytes, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), (uint)domain.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), (uint)user.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), (uint)size);
            domain.CopyTo(bytes, 36);
            user.CopyTo(bytes, 36 + domain.Length);
            password?.CopyTo(bytes, 36 + domain.Length + user.Length);
            await pipe.WriteAsync(bytes, token);
            await pipe.FlushAsync(token);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private sealed record Client(string Sid, bool Admin, bool System, uint Session);
    private static Client Caller(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var session)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Client? caller = null;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(true) ?? throw new UnauthorizedAccessException();
            var sid = identity.User?.Value ?? throw new UnauthorizedAccessException();
            caller = new(sid, new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), sid == "S-1-5-18", session);
        });
        return caller ?? throw new UnauthorizedAccessException();
    }

    private static void VerifyLogonUi(NamedPipeServerStream pipe, string sid, uint scenario)
    {
        var caller = Caller(pipe);
        if (!caller.System || caller.Session == 0 || caller.Session != WTSGetActiveConsoleSessionId()) throw new UnauthorizedAccessException();
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var process = OpenProcess(0x1000, false, pid);
        var path = new StringBuilder(32768);
        var length = path.Capacity;
        if (process.IsInvalid || !QueryFullProcessImageName(process, 0, path, ref length) ||
            !path.ToString().Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "LogonUI.exe"), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException();
        var user = SessionText(caller.Session, 5);
        var domain = SessionText(caller.Session, 7);
        if (user.Length == 0) { if (scenario == 2) throw new UnauthorizedAccessException(); }
        else if (((SecurityIdentifier)new NTAccount(domain, user).Translate(typeof(SecurityIdentifier))).Value != sid)
            throw new UnauthorizedAccessException();
    }

    private static string SessionText(uint session, int field)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, field, out var text, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return Marshal.PtrToStringUni(text) ?? ""; }
        finally { WTSFreeMemory(text); }
    }
    private static async Task MonitorAsync(Stream pipe, CancellationTokenSource lifetime)
    {
        try { await pipe.ReadAsync(new byte[1], lifetime.Token); }
        catch (Exception) { }
        finally { lifetime.Cancel(); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint session, int field, out IntPtr buffer, out uint bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr buffer);
}
