using System.Text;
using WatchUnlock.BleClient;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("WatchUnlock BLE 通信测试：只验证通知回复，不执行 Windows 解锁。");
Console.WriteLine("请在手机上开启 BLE 通信测试，并允许通知和附近设备权限。");
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
Phone? selected = null;
try
{
    while (!shutdown.IsCancellationRequested)
    {
        if (selected is null)
        {
            Console.WriteLine("正在扫描手机（8 秒）……");
            var phones = await BleClient.DiscoverAsync(shutdown.Token);
            if (phones.Count == 0)
            {
                Console.WriteLine("未发现手机。按回车重新扫描，输入 q 退出。");
                if (Console.ReadLine()?.Trim().Equals("q", StringComparison.OrdinalIgnoreCase) != false) break;
                continue;
            }
            for (var index = 0; index < phones.Count; index++)
                Console.WriteLine($"{index + 1}. {phones[index].Name}（{phones[index].Address:X12}）");
            Console.Write("选择手机编号（q 退出）：");
            var choice = Console.ReadLine();
            if (choice is null || choice.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) break;
            if (!int.TryParse(choice, out var number) || number < 1 || number > phones.Count) continue;
            selected = phones[number - 1];
        }
        Console.WriteLine("回车发起新的 10 秒请求，输入 r 重新选手机，输入 q 退出。");
        var command = Console.ReadLine()?.Trim();
        if (command is null || command.Equals("q", StringComparison.OrdinalIgnoreCase)) break;
        if (command.Equals("r", StringComparison.OrdinalIgnoreCase)) { selected = null; continue; }
        try
        {
            using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            connectDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var client = await BleClient.ConnectAsync(selected, connectDeadline.Token);
            Console.WriteLine("电脑已连接，请在收到通知后从手机或手表回复已设置的文字。");
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
        catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
        { Console.WriteLine("连接超时。下次发起会重新连接；手机地址变化时输入 r 重新扫描。"); }
        catch (Exception error) when (!shutdown.IsCancellationRequested)
        { Console.WriteLine($"通信失败：{error.Message} 下次发起会重新连接。"); }
    }
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
catch (Exception error) { Console.Error.WriteLine($"无法继续：{error.Message}"); Environment.ExitCode = 1; }
