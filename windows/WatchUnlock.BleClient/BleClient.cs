using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace WatchUnlock.BleClient;

internal sealed record Phone(ulong Address, BluetoothAddressType AddressType, string Name);

/// <summary>Interactive BLE transport only; this program never submits Windows credentials.</summary>
internal sealed class BleClient : IDisposable
{
    internal static readonly Guid ServiceId = new("6d87f6f0-3274-4f00-8e0a-770450f8a900");
    private static readonly Guid ChallengeId = new("6d87f6f0-3274-4f00-8e0a-770450f8a901");
    private static readonly Guid ApprovalId = new("6d87f6f0-3274-4f00-8e0a-770450f8a902");
    private static readonly Guid StatusId = new("6d87f6f0-3274-4f00-8e0a-770450f8a903");
    private const int ChunkBytes = 20;
    private const int MaximumMessageBytes = 4096;
    private readonly BluetoothLEDevice device;
    private readonly GattDeviceService service;
    private readonly GattCharacteristic challenge;
    private readonly GattCharacteristic approval;
    private readonly object responseLock = new();
    private readonly MemoryStream incoming = new();
    private TaskCompletionSource<string>? response;
    private string? requestId;
    private string? nonce;
    private long started;
    private int lifetimeSeconds;
    private TaskCompletionSource<JsonElement>? pairReply;
    private string? expectedPairType;
    private string? sessionId;
    private byte[]? sessionKey;
    private bool disposed;
    internal bool IsConnected
    {
        get { lock (responseLock) return !disposed && sessionKey is not null && device.ConnectionStatus == BluetoothConnectionStatus.Connected; }
    }

    private BleClient(BluetoothLEDevice device, GattDeviceService service,
        GattCharacteristic challenge, GattCharacteristic approval)
    {
        this.device = device;
        this.service = service;
        this.challenge = challenge;
        this.approval = approval;
        approval.ValueChanged += OnValueChanged;
        device.ConnectionStatusChanged += OnConnectionChanged;
    }

    internal static async Task<BleClient> ConnectAsync(Phone phone, byte[] pairingKey, CancellationToken token, Action<string> progress)
    {
        BluetoothLEDevice? device = null;
        GattDeviceService? selectedService = null;
        BleClient? client = null;
        var stage = "打开蓝牙设备";
        try
        {
            progress(stage);
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(phone.Address, phone.AddressType).AsTask(token)
                ?? throw new IOException("无法访问手机，请检查蓝牙范围和 Windows 设备访问权限。");
            progress(stage = "发现 GATT 服务");
            var services = await device.GetGattServicesForUuidAsync(ServiceId, BluetoothCacheMode.Uncached).AsTask(token);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                throw new IOException($"GATT 服务不可用（状态 {services.Status}），请重新开启手机端 BLE。");
            selectedService = services.Services[0];
            foreach (var extra in services.Services.Skip(1)) extra.Dispose();
            progress(stage = "读取挑战特征");
            var write = await FindCharacteristicAsync(selectedService, ChallengeId, token);
            progress(stage = "读取授权结果特征");
            var read = await FindCharacteristicAsync(selectedService, ApprovalId, token);
            progress(stage = "读取协议特征");
            var status = await FindCharacteristicAsync(selectedService, StatusId, token);
            if (!write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write) ||
                !read.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
                throw new IOException("手机 GATT 特征不兼容。");
            progress(stage = "检查手机协议版本");
            var version = await status.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(token);
            if (version.Status != GattCommunicationStatus.Success)
                throw new IOException($"协议读取失败（状态 {version.Status}）。");
            if (ReadUtf8(version.Value) != "paired-v2")
                throw new IOException("协议版本不兼容，请同时安装 0.4.0 或更新的手机和 Windows 程序。");
            client = new BleClient(device, selectedService, write, read);
            progress(stage = "订阅授权结果");
            var subscription = await read.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Indicate).AsTask(token);
            if (subscription != GattCommunicationStatus.Success)
                throw new IOException($"授权结果订阅失败（状态 {subscription}）。");
            progress(stage = "校验两端配对 Key");
            await client.PairAsync(pairingKey, token);
            progress("Key 配对成功");
            return client;
        }
        catch (Exception error)
        {
            if (client is not null) client.Dispose();
            else
            {
                Quietly(() => selectedService?.Dispose());
                Quietly(() => device?.Dispose());
            }
            if (token.IsCancellationRequested)
                throw new OperationCanceledException($"[{stage}] 连接已取消或超时。", error, token);
            if (error is OperationCanceledException)
                throw new TimeoutException($"[{stage}] Key 配对超过 35 秒，请重新连接。", error);
            throw new IOException($"[{stage}] {ErrorInfo.Describe(error)}", error);
        }
    }

    private static async Task<GattCharacteristic> FindCharacteristicAsync(GattDeviceService service, Guid id, CancellationToken token)
    {
        var result = await service.GetCharacteristicsForUuidAsync(id, BluetoothCacheMode.Uncached).AsTask(token);
        if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count != 1)
            throw new IOException($"缺少 GATT 特征（状态 {result.Status}，数量 {result.Characteristics.Count}）。");
        return result.Characteristics[0];
    }

    private async Task PairAsync(byte[] key, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        var id = Guid.NewGuid().ToString("D");
        var clientNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var proof = await ExchangePairAsync(new Dictionary<string, object>
        {
            ["v"] = 2, ["type"] = "pair_request", ["request_id"] = id, ["client_nonce"] = clientNonce,
            ["mac"] = PairingProtocol.Sign(key, "pair_request", id, clientNonce)
        }, "pair_challenge", deadline.Token);
        PairingProtocol.Fields(proof, "request_id", "client_nonce", "server_nonce", "mac");
        if (PairingProtocol.Text(proof, "request_id") != id || PairingProtocol.Text(proof, "client_nonce") != clientNonce)
            throw new InvalidDataException("手机配对证明与当前连接不匹配。");
        var serverNonce = PairingProtocol.Text(proof, "server_nonce");
        PairingProtocol.Base64Bytes(serverNonce);
        PairingProtocol.Verify(key, PairingProtocol.Text(proof, "mac"), "pair_challenge", id, clientNonce, serverNonce);
        var confirmed = await ExchangePairAsync(new Dictionary<string, object>
        {
            ["v"] = 2, ["type"] = "pair_confirm", ["request_id"] = id,
            ["client_nonce"] = clientNonce, ["server_nonce"] = serverNonce,
            ["mac"] = PairingProtocol.Sign(key, "pair_confirm", id, clientNonce, serverNonce)
        }, "pair_ok", deadline.Token);
        PairingProtocol.Fields(confirmed, "request_id", "client_nonce", "server_nonce", "mac");
        if (PairingProtocol.Text(confirmed, "request_id") != id ||
            PairingProtocol.Text(confirmed, "client_nonce") != clientNonce ||
            PairingProtocol.Text(confirmed, "server_nonce") != serverNonce)
            throw new InvalidDataException("手机配对确认与当前连接不匹配。");
        PairingProtocol.Verify(key, PairingProtocol.Text(confirmed, "mac"), "pair_ok", id, clientNonce, serverNonce);
        lock (responseLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                throw new IOException("配对期间手机连接已断开。");
            sessionId = id;
            sessionKey = PairingProtocol.RawSign(key, "session_key", id, clientNonce, serverNonce);
        }
    }

    private async Task<JsonElement> ExchangePairAsync(Dictionary<string, object> message, string replyType, CancellationToken token)
    {
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (responseLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pairReply = completion;
            expectedPairType = replyType;
        }
        try
        {
            await WriteFrameAsync(PairingProtocol.Frame(message), token);
            return await completion.Task.WaitAsync(token);
        }
        finally
        {
            lock (responseLock)
            {
                pairReply = null;
                expectedPairType = null;
            }
        }
    }

    private async Task WriteFrameAsync(byte[] bytes, CancellationToken token)
    {
        if (bytes.Length > MaximumMessageBytes + 1) throw new InvalidDataException("BLE 消息过大。");
        for (var offset = 0; offset < bytes.Length; offset += ChunkBytes)
        {
            token.ThrowIfCancellationRequested();
            using var writer = new DataWriter();
            writer.WriteBytes(bytes.AsSpan(offset, Math.Min(ChunkBytes, bytes.Length - offset)).ToArray());
            var status = await challenge.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse).AsTask(token);
            if (status != GattCommunicationStatus.Success)
                throw new IOException($"发送 BLE 消息失败（状态 {status}，分片 {offset / ChunkBytes + 1}）。");
        }
    }

    internal async Task<string> RequestAsync(int seconds, CancellationToken token)
    {
        if (seconds is < 5 or > 120) throw new ArgumentOutOfRangeException(nameof(seconds));
        var activeKey = sessionKey ?? throw new InvalidOperationException("Key 尚未配对。");
        var activeSession = sessionId ?? throw new InvalidOperationException("Key 尚未配对。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        var id = Guid.NewGuid().ToString("D");
        var challengeNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (responseLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (response is not null) throw new InvalidOperationException("已有请求在等待回复。");
            incoming.SetLength(0);
            requestId = id;
            nonce = challengeNonce;
            lifetimeSeconds = seconds;
            started = Stopwatch.GetTimestamp();
            response = completion;
        }
        var name = Environment.MachineName;
        var bytes = PairingProtocol.Frame(new Dictionary<string, object>
        {
            ["v"] = 2, ["type"] = "unlock_request", ["request_id"] = id, ["nonce"] = challengeNonce,
            ["expires_in"] = seconds, ["computer_name"] = name, ["session_id"] = activeSession,
            ["mac"] = PairingProtocol.Sign(activeKey, "unlock_request", activeSession, id, challengeNonce,
                seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), Convert.ToBase64String(Encoding.UTF8.GetBytes(name)))
        });
        try
        {
            await WriteFrameAsync(bytes, deadline.Token);
            return await completion.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return "expired";
        }
        finally
        {
            lock (responseLock)
            {
                response = null;
                requestId = null;
                nonce = null;
                if (!disposed) incoming.SetLength(0);
            }
        }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        lock (responseLock)
        {
            if (disposed) return;
            try
            {
                using var reader = DataReader.FromBuffer(args.CharacteristicValue);
                var bytes = new byte[checked((int)reader.UnconsumedBufferLength)];
                reader.ReadBytes(bytes);
                foreach (var value in bytes)
                {
                    if (value != 10)
                    {
                        if (incoming.Length >= MaximumMessageBytes) throw new InvalidDataException("BLE 结果消息过大。");
                        incoming.WriteByte(value);
                        continue;
                    }
                    var body = PairingProtocol.Parse(incoming.ToArray());
                    incoming.SetLength(0);
                    var type = PairingProtocol.Text(body, "type");
                    if (pairReply is not null)
                    {
                        if (type == "pair_error")
                        {
                            PairingProtocol.Fields(body, "reason");
                            var reason = PairingProtocol.Text(body, "reason");
                            if (reason == "key_mismatch")
                                throw new UnauthorizedAccessException("Key 不一致，配对失败。请输入与手机相同的 Key。");
                            if (reason == "expired") throw new TimeoutException("手机 Key 配对已超时，请重新连接。");
                            throw new InvalidDataException("手机返回了未知配对错误。");
                        }
                        if (type != expectedPairType) throw new InvalidDataException("手机返回了不符合当前步骤的配对消息。");
                        pairReply.TrySetResult(body);
                        continue;
                    }
                    if (response is null) continue;
                    if (type != "approval")
                        throw new InvalidDataException("BLE 结果消息格式错误。");
                    PairingProtocol.Fields(body, "session_id", "request_id", "nonce", "decision", "mac");
                    if (PairingProtocol.Text(body, "session_id") != sessionId)
                        throw new InvalidDataException("授权结果不属于当前配对连接。");
                    var resultId = PairingProtocol.Text(body, "request_id");
                    var resultNonce = PairingProtocol.Text(body, "nonce");
                    var decision = PairingProtocol.Text(body, "decision");
                    PairingProtocol.Verify(sessionKey ?? throw new InvalidDataException("配对连接已失效。"),
                        PairingProtocol.Text(body, "mac"), "approval", sessionId!, resultId, resultNonce, decision);
                    if (resultId != requestId || resultNonce != nonce)
                        continue; // Stale results cannot complete this request.
                    if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(lifetimeSeconds))
                    {
                        response.TrySetResult("expired");
                        return;
                    }
                    if (decision is not ("approved" or "rejected" or "expired" or "cancelled" or "replaced" or "busy" or "unavailable"))
                        throw new InvalidDataException("BLE 结果包含未知决定。");
                    response.TrySetResult(decision);
                    return;
                }
            }
            catch (Exception error)
            {
                incoming.SetLength(0);
                pairReply?.TrySetException(error);
                response?.TrySetException(error);
            }
        }
    }

    private void OnConnectionChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            lock (responseLock)
            {
                var error = new IOException("手机连接已断开，当前配对或请求已作废。");
                pairReply?.TrySetException(error);
                response?.TrySetException(error);
            }
        }
    }

    private static string ReadUtf8(Windows.Storage.Streams.IBuffer buffer)
    {
        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[checked((int)reader.UnconsumedBufferLength)];
        reader.ReadBytes(bytes);
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    public void Dispose()
    {
        lock (responseLock)
        {
            if (disposed) return;
            disposed = true;
            pairReply?.TrySetCanceled();
            response?.TrySetCanceled();
            if (sessionKey is not null) CryptographicOperations.ZeroMemory(sessionKey);
            sessionKey = null;
            sessionId = null;
            incoming.Dispose();
        }
        Quietly(() => approval.ValueChanged -= OnValueChanged);
        Quietly(() => device.ConnectionStatusChanged -= OnConnectionChanged);
        Quietly(service.Dispose);
        Quietly(device.Dispose);
    }

    private static void Quietly(Action cleanup)
    {
        try { cleanup(); } catch (Exception) { /* Cleanup must not hide the original connection error. */ }
    }
}
