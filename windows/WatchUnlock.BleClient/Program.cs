using System.Text;
using System.Security.Cryptography;
using WatchUnlock.BleClient;

Console.OutputEncoding = Encoding.UTF8;
if (args is ["--setup"])
{
    try { await ServiceControl.MenuAsync(CancellationToken.None); }
    catch (Exception error) { Console.Error.WriteLine(ErrorInfo.Describe(error)); Environment.ExitCode = 1; }
    return;
}
Console.WriteLine($"WatchUnlock {typeof(BleClient).Assembly.GetName().Version?.ToString(3)} BLE 通信测试：只验证通知回复，不执行 Windows 解锁。");
Console.WriteLine("请安装 0.4.0 或更新版本，在手机保存配对 Key，并开启 BLE、通知和附近设备权限。");
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
Phone? selected = null;
byte[]? pairingKey = null;
try
{
    pairingKey = PairingProtocol.ReadKey();
    while (!shutdown.IsCancellationRequested)
    {
        if (selected is null)
        {
            selected = await PhonePicker.SelectAsync(shutdown.Token);
            if (selected is null) break;
        }
        Console.WriteLine("回车发起新的 10 秒请求，输入 r 重新选手机，k 修改 Key，q 退出。");
        var command = Console.ReadLine()?.Trim();
        if (command is null || command.Equals("q", StringComparison.OrdinalIgnoreCase)) break;
        if (command.Equals("r", StringComparison.OrdinalIgnoreCase)) { selected = null; continue; }
        if (command.Equals("k", StringComparison.OrdinalIgnoreCase))
        {
            CryptographicOperations.ZeroMemory(pairingKey);
            pairingKey = PairingProtocol.ReadKey();
            selected = null;
            continue;
        }
        if (command.Length != 0) continue;
        try
        {
            using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            connectDeadline.CancelAfter(TimeSpan.FromSeconds(60));
            using var client = await BleClient.ConnectAsync(selected, pairingKey, connectDeadline.Token,
                step => Console.WriteLine($"连接步骤：{step}……"));
            Console.WriteLine("Key 配对成功，正在发起请求；请从手机或手表回复已设置的文字。");
            var decision = await client.RequestAsync(10, shutdown.Token);
            Console.WriteLine(decision switch
            {
                "approved" => "回复匹配，已收到确认（仅通信测试，不执行解锁）。",
                "rejected" => "回复错误，本次请求已拒绝。",
                "expired" => "请求已超时，本次请求不予授权。",
                "busy" => "手机已有其他请求，请处理或取消后重试。",
                "unavailable" => "手机通知或服务不可用，请检查手机设置。",
                _ => "请求已取消或作废。"
            });
        }
        catch (OperationCanceledException error) when (!shutdown.IsCancellationRequested)
        { Console.WriteLine($"连接超时：{ErrorInfo.Describe(error)} 下次发起会重新连接；手机地址变化时输入 r 重新扫描。"); }
        catch (Exception error) when (!shutdown.IsCancellationRequested)
        { Console.WriteLine($"通信失败：{ErrorInfo.Describe(error)} 下次发起会重新连接；Key 不一致时输入 k 修改。"); }
    }
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
catch (Exception error) { Console.Error.WriteLine($"无法继续：{ErrorInfo.Describe(error)}"); Environment.ExitCode = 1; }
finally { if (pairingKey is not null) CryptographicOperations.ZeroMemory(pairingKey); }
