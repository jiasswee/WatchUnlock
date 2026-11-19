using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using WatchUnlock.BleClient;
using Windows.Devices.Bluetooth;
using BluetoothClient = WatchUnlock.BleClient.BleClient;

namespace WatchUnlock.Service;

/// <summary>Serializes BLE operations; settings changes cancel the current operation and session.</summary>
internal sealed class BleCoordinator : IDisposable
{
    private readonly ServiceSettingsStore store;
    private readonly object state = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly SemaphoreSlim wake = new(0, 1);
    private ServiceSettings? settings;
    private BluetoothClient? connection;
    private CancellationTokenSource? active;
    private string message = "尚未设置手机，请以管理员身份配置";

    public BleCoordinator(ServiceSettingsStore store, ILogger<BleCoordinator> logger)
    {
        this.store = store;
        try { settings = store.Load(); }
        catch (Exception error) { logger.LogWarning("配置读取失败，请重新配置；错误类型 {Type}", error.GetType().Name); }
        if (settings is not null) message = settings.Enabled ? "已配置，等待连接或请求" : "WatchUnlock 已停用";
    }

    internal string? OwnerSid { get { lock (state) return settings?.OwnerSid; } }
    internal ServiceReply Status(string id)
    {
        lock (state)
        {
            var text = settings is null ? message : $"{message}；手机：{settings.Name}；自动回连：{(settings.AutoReconnect ? "开启" : "关闭")}";
            return new(1, id, "status", text, Enabled: settings?.Enabled, AutoReconnect: settings?.AutoReconnect, Phone: settings?.Name);
        }
    }

    internal void ChangeMode(string operation, bool value, string callerSid, bool privileged)
    {
        lock (state)
        {
            var configured = settings ?? throw new InvalidOperationException("请先设置手机。");
            if (!privileged && configured.OwnerSid != callerSid) throw new UnauthorizedAccessException("配置用户已改变，请重新打开管理程序。");
            var updated = operation == "enabled" ? configured with { Enabled = value } : configured with { AutoReconnect = value };
            updated = updated with { Revision = Guid.NewGuid().ToString("D") };
            store.Save(updated);
            settings = updated;
            InterruptLocked();
            message = updated.Enabled ? "设置已更新，等待连接或请求" : "WatchUnlock 已停用，当前授权已取消";
            PulseLocked();
        }
    }

    internal async Task ConfigureAsync(Phone phone, byte[] key, string ownerSid, CancellationToken token)
    {
        lock (state) { InterruptLocked(); PulseLocked(); }
        await operations.WaitAsync(token);
        CancellationTokenSource? scope = null;
        BluetoothClient? paired = null;
        try
        {
            string? revision;
            lock (state)
            {
                revision = settings?.Revision;
                scope = StartOperationLocked(token, 60);
            }
            paired = await BluetoothClient.ConnectAsync(phone, key, scope.Token, SetStatus);
            lock (state)
            {
                scope.Token.ThrowIfCancellationRequested();
                if (settings?.Revision != revision) throw new OperationCanceledException("配置已改变，请重新选择手机。");
                var updated = new ServiceSettings(Guid.NewGuid().ToString("D"), phone.Address, (int)phone.AddressType,
                    phone.Name, ownerSid, ServiceSettingsStore.Protect(key), settings?.Enabled ?? true, settings?.AutoReconnect ?? false);
                store.Save(updated);
                settings = updated;
                connection?.Dispose();
                connection = updated.Enabled ? paired : null;
                if (updated.Enabled) paired = null;
                message = updated.Enabled ? "新手机 Key 配对成功，等待请求" : "新手机已保存，WatchUnlock 仍停用";
                PulseLocked();
            }
        }
        finally { paired?.Dispose(); FinishOperation(scope); operations.Release(); }
    }

    internal async Task<string> RequestAsync(string callerSid, bool system, CancellationToken token)
    {
        await operations.WaitAsync(token);
        CancellationTokenSource? scope = null;
        try
        {
            ServiceSettings snapshot;
            lock (state)
            {
                snapshot = EnabledSettingsLocked();
                if (!system && snapshot.OwnerSid != callerSid) throw new UnauthorizedAccessException("配置用户已改变，当前请求已作废。");
                scope = StartOperationLocked(token, 80);
            }
            var client = await EnsureConnectedAsync(snapshot, scope.Token);
            scope.Token.ThrowIfCancellationRequested();
            SetStatus("已发起请求，等待手机或手表回复");
            var decision = await client.RequestAsync(10, scope.Token);
            scope.Token.ThrowIfCancellationRequested();
            SetStatus("请求已结束，等待下次请求");
            return decision;
        }
        finally { FinishOperation(scope); operations.Release(); }
    }

    internal async Task ReconnectLoopAsync(CancellationToken token)
    {
        var backoff = 2;
        while (!token.IsCancellationRequested)
        {
            var retry = false;
            lock (state) retry = settings is { Enabled: true, AutoReconnect: true } && connection?.IsConnected != true;
            if (!retry) { backoff = 2; await wake.WaitAsync(TimeSpan.FromSeconds(1), token); continue; }
            await operations.WaitAsync(token);
            CancellationTokenSource? scope = null;
            try
            {
                ServiceSettings snapshot;
                lock (state)
                {
                    snapshot = EnabledSettingsLocked();
                    if (!snapshot.AutoReconnect) continue;
                    scope = StartOperationLocked(token, 60);
                }
                await EnsureConnectedAsync(snapshot, scope.Token);
                backoff = 2;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { backoff = 2; }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                SetStatus($"手机暂不可用，{backoff} 秒后重连；{ErrorInfo.Describe(error)}");
            }
            finally { FinishOperation(scope); operations.Release(); }
            await wake.WaitAsync(TimeSpan.FromSeconds(backoff), token);
            backoff = Math.Min(backoff * 2, 30);
        }
    }

    private async Task<BluetoothClient> EnsureConnectedAsync(ServiceSettings snapshot, CancellationToken token)
    {
        lock (state)
        {
            token.ThrowIfCancellationRequested();
            if (connection?.IsConnected == true) return connection;
            connection?.Dispose();
            connection = null;
        }
        var key = ServiceSettingsStore.Unprotect(snapshot);
        BluetoothClient? paired = null;
        try
        {
            var phone = new Phone(snapshot.Address, (BluetoothAddressType)snapshot.AddressType, snapshot.Name);
            try
            {
                using var quick = CancellationTokenSource.CreateLinkedTokenSource(token);
                quick.CancelAfter(TimeSpan.FromSeconds(15));
                paired = await BluetoothClient.ConnectAsync(phone, key, quick.Token, SetStatus);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                SetStatus("重新发现手机，并使用已保存的 Key 校验");
                using var scan = await PhoneDiscovery.StartAsync(token);
                var attempted = new HashSet<ulong>();
                await foreach (var candidate in scan.Updates.ReadAllAsync(token))
                {
                    if (!attempted.Add(candidate.Address)) continue;
                    try
                    {
                        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                        attempt.CancelAfter(TimeSpan.FromSeconds(10));
                        paired = await BluetoothClient.ConnectAsync(candidate, key, attempt.Token, SetStatus);
                        phone = candidate;
                        break;
                    }
                    catch (Exception error) when (!token.IsCancellationRequested) { SetStatus(ErrorInfo.Describe(error)); }
                }
            }
            token.ThrowIfCancellationRequested();
            if (paired is null) throw new IOException("未找到使用当前 Key 的手机。");
            lock (state)
            {
                token.ThrowIfCancellationRequested();
                if (settings?.Revision != snapshot.Revision || !settings.Enabled)
                    throw new OperationCanceledException("配置已改变，旧连接已作废。");
                if (phone.Address != snapshot.Address || (int)phone.AddressType != snapshot.AddressType)
                {
                    var updated = snapshot with { Address = phone.Address, AddressType = (int)phone.AddressType };
                    store.Save(updated);
                    settings = updated;
                }
                connection = paired;
                paired = null;
                message = "Key 配对成功，等待请求";
                return connection;
            }
        }
        finally { paired?.Dispose(); CryptographicOperations.ZeroMemory(key); }
    }

    private ServiceSettings EnabledSettingsLocked()
    {
        var configured = settings ?? throw new InvalidOperationException("请先设置手机。");
        if (!configured.Enabled) throw new InvalidOperationException("WatchUnlock 已停用，请先启用。");
        return configured;
    }
    private CancellationTokenSource StartOperationLocked(CancellationToken token, int seconds)
    {
        var scope = CancellationTokenSource.CreateLinkedTokenSource(token);
        scope.CancelAfter(TimeSpan.FromSeconds(seconds));
        active = scope;
        return scope;
    }
    private void FinishOperation(CancellationTokenSource? scope)
    {
        lock (state) { if (ReferenceEquals(active, scope)) active = null; }
        scope?.Dispose();
    }
    private void SetStatus(string text)
    {
        lock (state) message = settings?.Enabled == false ? "WatchUnlock 已停用" : text;
    }
    private void InterruptLocked()
    {
        active?.Cancel();
        connection?.Dispose();
        connection = null;
    }
    private void PulseLocked() { if (wake.CurrentCount == 0) wake.Release(); }
    public void Dispose()
    {
        lock (state) InterruptLocked();
        operations.Dispose();
        wake.Dispose();
    }
}
