using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace WatchUnlock.BleClient;

/// <summary>Publishes discoveries immediately; the caller decides when to stop scanning.</summary>
internal sealed class PhoneDiscovery : IDisposable
{
    private readonly BluetoothLEAdvertisementWatcher watcher = new() { ScanningMode = BluetoothLEScanningMode.Active };
    private readonly Channel<Phone> updates = Channel.CreateUnbounded<Phone>();
    private readonly Dictionary<ulong, Phone> phones = new();
    private readonly object gate = new();
    private bool disposed;
    internal ChannelReader<Phone> Updates => updates.Reader;

    private PhoneDiscovery()
    {
        watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(BleClient.ServiceId);
        watcher.Received += Received;
        watcher.Stopped += Stopped;
    }

    internal static async Task<PhoneDiscovery> StartAsync(CancellationToken token)
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(token);
        if (adapter is null || !adapter.IsLowEnergySupported)
            throw new InvalidOperationException("电脑没有可用的 BLE 适配器。");
        token.ThrowIfCancellationRequested();
        var discovery = new PhoneDiscovery();
        try { discovery.watcher.Start(); return discovery; }
        catch { discovery.Dispose(); throw; }
    }

    private void Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        lock (gate)
        {
            if (disposed) return;
            var name = string.Concat((args.Advertisement.LocalName ?? "").Where(character => !char.IsControl(character))).Trim();
            var phone = new Phone(args.BluetoothAddress, args.BluetoothAddressType,
                string.IsNullOrWhiteSpace(name) ? "WatchUnlock 手机" : name);
            if (phones.TryGetValue(phone.Address, out var old))
            {
                if (string.IsNullOrWhiteSpace(name) || phone == old) return;
            }
            else if (phones.Count >= 128) return;
            phones[phone.Address] = phone;
            updates.Writer.TryWrite(phone);
        }
    }

    private void Stopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        lock (gate)
        {
            if (!disposed) updates.Writer.TryComplete(new IOException($"蓝牙扫描已停止（状态 {args.Error}），请检查蓝牙开关和权限。"));
        }
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        try { watcher.Stop(); } catch (Exception) { /* Preserve a discovery or selection error. */ }
        watcher.Received -= Received;
        watcher.Stopped -= Stopped;
        updates.Writer.TryComplete();
    }
}
