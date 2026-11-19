namespace WatchUnlock.BleClient;

internal static class PhonePicker
{
    internal static async Task<Phone?> SelectAsync(CancellationToken token)
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("请选择交互式终端中的手机设备。");
        while (true)
        {
            using var discovery = await PhoneDiscovery.StartAsync(token);
            var phones = new List<Phone>();
            var input = "";
            Console.WriteLine("正在扫描，搜到的手机会立即显示；输入编号后回车即可选择。");
            Prompt(input);
            var rescan = false;
            while (!rescan)
            {
                token.ThrowIfCancellationRequested();
                while (discovery.Updates.TryRead(out var phone))
                {
                    var index = phones.FindIndex(item => item.Address == phone.Address);
                    if (index < 0) { index = phones.Count; phones.Add(phone); }
                    else phones[index] = phone;
                    ClearPrompt();
                    Console.WriteLine($"{index + 1}. {phone.Name}（{phone.Address:X12}）");
                    Prompt(input);
                }
                if (discovery.Updates.Completion.IsCompleted) await discovery.Updates.Completion;
                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter)
                    {
                        ClearPrompt();
                        if (input.Equals("q", StringComparison.OrdinalIgnoreCase)) return null;
                        if (input.Equals("r", StringComparison.OrdinalIgnoreCase)) { rescan = true; break; }
                        if (int.TryParse(input, out var number) && number > 0 && number <= phones.Count)
                        {
                            Console.WriteLine($"已选择：{phones[number - 1].Name}（{phones[number - 1].Address:X12}）");
                            return phones[number - 1];
                        }
                        Console.WriteLine(phones.Count == 0 ? "暂未发现手机，扫描仍在继续。" : "请输入已经显示的手机编号。");
                        input = "";
                    }
                    else if (key.Key == ConsoleKey.Backspace && input.Length > 0) input = input[..^1];
                    else if ((char.IsAsciiDigit(key.KeyChar) || key.KeyChar is 'r' or 'R' or 'q' or 'Q') && input.Length < 6)
                        input += key.KeyChar;
                    ClearPrompt();
                    Prompt(input);
                }
                if (!rescan) await Task.Delay(50, token);
            }
        }
    }

    private static void Prompt(string input) => Console.Write($"选择编号（r 重扫，q 退出）：{input}");
    private static void ClearPrompt()
    {
        if (Console.IsOutputRedirected) { Console.WriteLine(); return; }
        Console.CursorLeft = 0;
        Console.Write(new string(' ', Math.Max(1, Console.WindowWidth - 1)));
        Console.CursorLeft = 0;
    }
}
