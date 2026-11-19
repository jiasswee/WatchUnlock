using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
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
    private readonly GattSession session;
    private readonly GattCharacteristic challenge;
    private readonly GattCharacteristic approval;
    private readonly object responseLock = new();
    private readonly MemoryStream incoming = new();
    private TaskCompletionSource<string>? response;
    private string? requestId;
    private string? nonce;
    private long started;
    private int lifetimeSeconds;

    private BleClient(BluetoothLEDevice device, GattDeviceService service, GattSession session,
        GattCharacteristic challenge, GattCharacteristic approval)
    {
        this.device = device;
        this.service = service;
        this.session = session;
        this.challenge = challenge;
        this.approval = approval;
        approval.ValueChanged += OnValueChanged;
        device.ConnectionStatusChanged += OnConnectionChanged;
    }

    internal static async Task<IReadOnlyList<Phone>> DiscoverAsync(CancellationToken token)
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(token);
        if (adapter is null || !adapter.IsLowEnergySupported)
            throw new InvalidOperationException("电脑没有可用的 BLE 适配器。");
        var phones = new Dictionary<ulong, Phone>();
        var gate = new object();
        var stopped = new TaskCompletionSource<BluetoothError>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(ServiceId);
        watcher.Received += (_, args) =>
        {
            lock (gate)
            {
                phones[args.BluetoothAddress] = new Phone(args.BluetoothAddress, args.BluetoothAddressType,
                    string.IsNullOrWhiteSpace(args.Advertisement.LocalName) ? "WatchUnlock 手机" : args.Advertisement.LocalName);
            }
        };
        watcher.Stopped += (_, args) => stopped.TrySetResult(args.Error);
        watcher.Start();
        try
        {
            await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(8), token), stopped.Task);
            token.ThrowIfCancellationRequested();
            if (stopped.Task.IsCompleted && await stopped.Task != BluetoothError.Success)
                throw new InvalidOperationException("蓝牙扫描失败，请开启电脑蓝牙并检查隐私权限。");
            lock (gate) return phones.Values.OrderBy(phone => phone.Address).ToArray();
        }
        finally { watcher.Stop(); }
    }

    internal static async Task<BleClient> ConnectAsync(Phone phone, CancellationToken token)
    {
        BluetoothLEDevice? device = null;
        GattDeviceService? selectedService = null;
        GattSession? session = null;
        BleClient? client = null;
        try
        {
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(phone.Address, phone.AddressType).AsTask(token)
                ?? throw new IOException("无法访问手机，请检查蓝牙范围和 Windows 设备访问权限。");
            session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(token)
                ?? throw new IOException("无法建立 GATT 会话。");
            if (session.CanMaintainConnection) session.MaintainConnection = true;
            var services = await device.GetGattServicesForUuidAsync(ServiceId, BluetoothCacheMode.Uncached).AsTask(token);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                throw new IOException("手机未提供 WatchUnlock GATT 服务，请重新开启手机端 BLE。");
            selectedService = services.Services[0];
            foreach (var extra in services.Services.Skip(1)) extra.Dispose();
            var write = await FindCharacteristicAsync(selectedService, ChallengeId, token);
            var read = await FindCharacteristicAsync(selectedService, ApprovalId, token);
            var status = await FindCharacteristicAsync(selectedService, StatusId, token);
            if (!write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write) ||
                !read.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
                throw new IOException("手机 GATT 特征不兼容。");
            var version = await status.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(token);
            if (version.Status != GattCommunicationStatus.Success || ReadUtf8(version.Value) != "transport-v1")
                throw new IOException("手机 BLE 协议版本不兼容。");
            client = new BleClient(device, selectedService, session, write, read);
            var subscription = await read.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Indicate).AsTask(token);
            if (subscription != GattCommunicationStatus.Success)
                throw new IOException("无法订阅手机的授权结果。");
            return client;
        }
        catch
        {
            if (client is not null) client.Dispose();
            else
            {
                session?.Dispose();
                selectedService?.Dispose();
                device?.Dispose();
            }
            throw;
        }
    }

    private static async Task<GattCharacteristic> FindCharacteristicAsync(GattDeviceService service, Guid id, CancellationToken token)
    {
        var result = await service.GetCharacteristicsForUuidAsync(id, BluetoothCacheMode.Uncached).AsTask(token);
        if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count != 1)
            throw new IOException("缺少必要的 GATT 特征。");
        return result.Characteristics[0];
    }

    internal async Task<string> RequestAsync(int seconds, CancellationToken token)
    {
        if (seconds is < 5 or > 120) throw new ArgumentOutOfRangeException(nameof(seconds));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        var id = Guid.NewGuid().ToString("D");
        var challengeNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (responseLock)
        {
            if (response is not null) throw new InvalidOperationException("已有请求在等待回复。");
            incoming.SetLength(0);
            requestId = id;
            nonce = challengeNonce;
            lifetimeSeconds = seconds;
            started = Stopwatch.GetTimestamp();
            response = completion;
        }
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            v = 1, type = "unlock_request", request_id = id, nonce = challengeNonce,
            expires_in = seconds, computer_name = Environment.MachineName
        }) + "\n");
        try
        {
            for (var offset = 0; offset < bytes.Length; offset += ChunkBytes)
            {
                using var writer = new DataWriter();
                writer.WriteBytes(bytes.AsSpan(offset, Math.Min(ChunkBytes, bytes.Length - offset)).ToArray());
                var result = await challenge.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse).AsTask(deadline.Token);
                if (result != GattCommunicationStatus.Success) throw new IOException("挑战发送失败，当前请求已作废。");
            }
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
                incoming.SetLength(0);
            }
        }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        lock (responseLock)
        {
            if (response is null) return;
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
                    var text = new UTF8Encoding(false, true).GetString(incoming.ToArray());
                    incoming.SetLength(0);
                    using var message = JsonDocument.Parse(text);
                    var body = message.RootElement;
                    if (body.GetProperty("v").GetInt32() != 1 || body.GetProperty("type").GetString() != "approval")
                        throw new InvalidDataException("BLE 结果消息格式错误。");
                    if (body.GetProperty("request_id").GetString() != requestId || body.GetProperty("nonce").GetString() != nonce)
                        continue; // Stale results cannot complete this request.
                    if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(lifetimeSeconds))
                    {
                        response.TrySetResult("expired");
                        return;
                    }
                    var decision = body.GetProperty("decision").GetString();
                    if (decision is not ("approved" or "rejected" or "expired" or "cancelled" or "replaced" or "busy" or "unavailable"))
                        throw new InvalidDataException("BLE 结果包含未知决定。");
                    response.TrySetResult(decision);
                    return;
                }
            }
            catch (Exception error) { response.TrySetException(error); }
        }
    }

    private void OnConnectionChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            lock (responseLock) response?.TrySetException(new IOException("手机连接已断开，当前请求已作废。"));
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
        approval.ValueChanged -= OnValueChanged;
        device.ConnectionStatusChanged -= OnConnectionChanged;
        lock (responseLock)
        {
            response?.TrySetCanceled();
            response = null;
            incoming.Dispose();
        }
        if (session.CanMaintainConnection) session.MaintainConnection = false;
        service.Dispose();
        session.Dispose();
        device.Dispose();
    }
}
